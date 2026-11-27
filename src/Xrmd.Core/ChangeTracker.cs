using System.Data;
using System.Globalization;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Query.Model;

namespace Xrmd.Core;

/// <summary>A change tracking read (plan 2.13): the parameters of RetrieveEntityChanges, also built by the Web API delta.</summary>
/// <param name="EntityName">The tracked table.</param>
/// <param name="Columns">The columns of the new or updated rows (<see cref="ColumnSelection.AllColumns"/> allowed).</param>
/// <param name="DataVersion">The token of the previous sync; null or empty: every row, no deletion (ruling C5).</param>
/// <param name="PageSize">1 to <see cref="ChangeTracker.MaxPageSize"/>.</param>
/// <param name="PageNumber">1 or more; above 1, <paramref name="PagingCookie"/> of the page before is required (ruling C7).</param>
public sealed record ChangeQuery(string EntityName, ColumnSelection Columns, string? DataVersion, int PageSize, int PageNumber,
    string? PagingCookie, bool GetGlobalMetadataVersion);

/// <summary>One page of changes: the new or updated rows (by version), then the deleted ones (by log version).</summary>
public sealed record ChangePage(IReadOnlyList<Record> Updated, IReadOnlyList<RecordReference> Deleted, string DataToken, bool MoreRecords,
    string? PagingCookie, string? GlobalMetadataVersion);

/// <summary>
/// RetrieveEntityChanges (plan 2.13). A read covers the versions strictly between the token's version and
/// <c>MIN_ACTIVE_ROWVERSION()</c> (read before the rows, fixed on page 1 and carried by the paging cookie): every version below it
/// is committed, so a transaction still in flight is never skipped, and the new token is that bound minus one. On PostgreSQL the
/// bound is the lower of the sequence's next value and the lowest version a transaction in flight may hold
/// (<see cref="ISqlDialect.MinActiveRowVersionAsync"/>, plan 2.7a Task 7).
/// </summary>
/// <remarks>
/// <para>The new or updated rows go through the query path of RetrieveMultiple (<see cref="CoreOperations.RetrieveRecordsAsync"/>)
/// with an internal <see cref="VersionRangeNode"/> and an order on <c>versionnumber</c>: the columns, their validation, the read
/// restriction and the field security masks are the ones RetrieveMultiple applies. The deletions come from <c>meta.deleted_record</c>.
/// Pages walk the updates, then the deletions (ruling C7), with the cookie <c>{to}!{phase}!{lastVersion}!{issuedUnixSeconds}</c>.</para>
/// <para>A token is expired (400, ruling C4) when its version is below <c>meta.change_tracking_state.purged_version</c> (checked
/// before and again after the deletion log is read, so a purge that commits in between is caught), or when it predates the
/// table's tracking: <c>token + 1 &lt; enabled_version</c>, where <c>enabled_version</c> is the <c>MIN_ACTIVE_ROWVERSION()</c> (<see cref="ISqlDialect.RowVersionStart"/>) at which
/// tracking started (a token is that bound minus one, so the token of a sync right after enabling is valid). It is also expired
/// when its version is above the current bound minus one (a version this database has not reached: the token comes from
/// another database or from before a restore), when its time is before <c>meta.change_tracking_state.restored_at</c> (the
/// last restore from a backup), or when its time is older than <c>organization.expirechangetrackingindays</c> days (age expiry,
/// as in Dataverse). A token without a readable time is expired, as its age is unknown. A new token is never dated before the
/// last restore (its time is raised to the whole second after <c>restored_at</c> when the clock is behind it), so the token of
/// a sync right after a restore is valid.</para>
/// <para>The caller's Global read on the table is the guard's (<c>SecurityGuard.DemandGlobalReadAsync</c>), not checked here.</para>
/// <para>S.2e3c1 (Task 4): on the model request and records. The answer's <c>BusinessEntityChanges</c> has no model twin: it is built
/// by <see cref="CoreSdkBridges.EntityChanges"/> and crosses the mirror as its own twin, as before. <see cref="ReadRequest(Request)"/>
/// has no SDK twin: nothing outside the core calls it (Task 4 ruling 4c; the Web API delta sends the request through the organization
/// service).</para>
/// </remarks>
public sealed class ChangeTracker(CoreOperations core, IMetadataStore store)
{
    /// <summary>The message name.</summary>
    public const string MessageName = "RetrieveEntityChanges";

    /// <summary>The largest page (and the default one).</summary>
    public const int MaxPageSize = 5000;

    private const char UpdatesPhase = 'u';
    private const char DeletionsPhase = 'd';

    /// <summary>The parameters of a RetrieveEntityChanges model request (PageInfo absent or zero: page 1 of 5000).</summary>
    /// <exception cref="XrmdFaultException">400 when <c>EntityName</c> or <c>Columns</c> is missing.</exception>
    public static ChangeQuery ReadRequest(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = CoreOperations.Parameter<string>(request, "EntityName", "String");
        var columns = CoreOperations.Parameter<ColumnSelection>(request, "Columns", "ColumnSet");
        var token = request.Parameters.TryGetValue("DataVersion", out var v) ? v as string : null;
        // A PagingInfo parameter crosses the mirror as its own twin (it is a query part only inside a query).
        var page = request.Parameters.TryGetValue("PageInfo", out var p) ? p as QueryPaging ?? CoreSdkBridges.Paging(p) : null;
        var metadataVersion = request.Parameters.TryGetValue("GetGlobalMetadataVersion", out var g) && g is true;
        return new ChangeQuery(entity, columns, token,
            page is { Count: not 0 } ? page.Count : MaxPageSize,
            page is { PageNumber: not 0 } ? page.PageNumber : 1,
            string.IsNullOrEmpty(page?.PagingCookie) ? null : page.PagingCookie,
            metadataVersion);
    }

    /// <summary>
    /// The RetrieveEntityChanges response of <paramref name="page"/>: its <c>EntityChanges</c>, updates as <c>NewOrUpdatedItem</c>,
    /// then deletions (<see cref="CoreSdkBridges.EntityChanges"/>).
    /// </summary>
    public static Response ToResponse(ChangePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new Response { Name = MessageName, Results = { ["EntityChanges"] = CoreSdkBridges.EntityChanges(page) } };
    }

    /// <summary>Reads one page of the changes of <paramref name="q"/> for <paramref name="caller"/> in <paramref name="s"/>.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for an unknown or untracked table, a page size or number out of
    /// range, a token or paging cookie that does not parse, an expired token, or an invalid column (as RetrieveMultiple).</exception>
    public async Task<ChangePage> RetrieveAsync(DataSession s, Caller caller, ChangeQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(q);
        var entity = core.TableOf(q.EntityName).LogicalName!;
        if (!store.ChangeTrackingTables.Contains(entity))
            throw XrmdFaultException.InvalidArgument(
                $"Table '{entity}' does not have change tracking enabled: turn ChangeTrackingEnabled on before reading its changes.");
        if (q.Columns is null) throw XrmdFaultException.InvalidArgument("RetrieveEntityChanges needs a column set (Columns).");
        if (q.PageSize is < 1 or > MaxPageSize)
            throw XrmdFaultException.InvalidArgument($"The page size of RetrieveEntityChanges must be between 1 and {MaxPageSize}, got {q.PageSize}.");
        if (q.PageNumber < 1)
            throw XrmdFaultException.InvalidArgument($"The page number of RetrieveEntityChanges must be 1 or greater, got {q.PageNumber}.");

        long? from = null;
        DateTime? issued = null;
        if (!string.IsNullOrEmpty(q.DataVersion))
        {
            if (!ChangeToken.TryParse(q.DataVersion, out var version, out issued))
                throw XrmdFaultException.InvalidArgument(
                    $"The DataVersion '{q.DataVersion}' is not a change tracking token; send the DataToken of an earlier response, or none to sync from scratch.");
            from = version;
        }

        Cookie? cookie = null;
        if (q.PageNumber > 1)
        {
            cookie = Cookie.TryParse(q.PagingCookie)
                     ?? throw XrmdFaultException.InvalidArgument(
                         $"Page {q.PageNumber} of RetrieveEntityChanges needs the paging cookie of page {q.PageNumber - 1}; got "
                         + (q.PagingCookie is null ? "none." : $"'{q.PagingCookie}', which is not one."));
            if (cookie.Phase == DeletionsPhase && from is null)
                throw XrmdFaultException.InvalidArgument("The paging cookie belongs to a read with a DataVersion: send the same DataVersion on every page.");
        }

        var state = await ReadStateAsync(s, entity, ct);
        var now = state.Now;
        if (cookie is not null && (cookie.To > state.MinActive || (from is { } f && cookie.To - 1 < f)))
            throw XrmdFaultException.InvalidArgument(
                $"The paging cookie '{q.PagingCookie}' does not belong to this read: send the cookie of the page before with the same DataVersion.");
        if (from is { } token) DemandNotExpired(entity, token, issued, state, now);
        var to = cookie?.To ?? state.MinActive;
        var issuedAt = cookie?.Issued ?? IssueTime(now, state.RestoredAt);
        var after = from ?? 0;

        // Updates: from the token (or the cookie's last version) to the bound, by version, one page.
        var updated = new List<Record>();
        var more = false;
        string? nextCookie = null;
        if (cookie is null || cookie.Phase == UpdatesPhase)
        {
            var start = cookie is null ? after : Math.Max(after, cookie.Last);
            var collection = await core.RetrieveRecordsAsync(s, caller, UpdatesQuery(entity, q.Columns, start, to, q.PageSize), ct);
            foreach (var row in collection.Records) updated.Add(Shaped(row, q.Columns));
            if (collection.MoreRecords)
            {
                more = true;
                nextCookie = Cookie.Format(to, UpdatesPhase, LastVersion(collection.Records[^1]), issuedAt);
            }
        }

        // Deletions (only with a token, ruling C5): after the updates, in the room the page has left.
        var deleted = new List<RecordReference>();
        if (!more && from is not null)
        {
            var start = cookie is { Phase: DeletionsPhase } ? Math.Max(after, cookie.Last) : after;
            var room = q.PageSize - updated.Count;
            var rows = await DeletionsAsync(s, entity, start, to, room + 1, ct);
            foreach (var (_, id) in rows.Take(room)) deleted.Add(new RecordReference { LogicalName = entity, Id = id });
            if (rows.Count > room)
            {
                more = true;
                nextCookie = Cookie.Format(to, DeletionsPhase, room == 0 ? start : rows[room - 1].Version, issuedAt);
            }
            // A purge that committed while the log was read: the rows it removed may be missing from this page.
            DemandNotExpired(entity, from.Value, issued, state with { PurgedVersion = await PurgedVersionAsync(s, ct) }, now);
        }

        var metadataVersion = q.GetGlobalMetadataVersion ? await GlobalMetadataVersionAsync(s, ct) : null;
        return new ChangePage(updated, deleted, ChangeToken.Format(to - 1, issuedAt), more,
            more ? nextCookie : null, metadataVersion);
    }

    /// <summary>The columns asked for, the primary key and <c>versionnumber</c>, in the range, by version, one page (+1 probe row).</summary>
    private static QueryModel UpdatesQuery(string entity, ColumnSelection columns, long afterExclusive, long beforeExclusive, int pageSize)
    {
        var root = new EntityNode { EntityName = entity, AllColumns = columns.AllColumns };
        if (!columns.AllColumns)
        {
            foreach (var column in columns.Columns.Distinct(StringComparer.Ordinal)) root.Columns.Add(new ColumnNode(column));
            if (!columns.Columns.Contains(TypeMapping.VersionNumber)) root.Columns.Add(new ColumnNode(TypeMapping.VersionNumber));
        }
        root.Filter.Versions.Add(new VersionRangeNode(afterExclusive, beforeExclusive));
        root.Orders.Add(new OrderNode(TypeMapping.VersionNumber, Descending: false));
        return new QueryModel { Root = root, PageSize = pageSize, PageNumber = 1 };
    }

    /// <summary>Drops <c>versionnumber</c> when it was only added to carry <see cref="Record.RowVersion"/>.</summary>
    private static Record Shaped(Record row, ColumnSelection columns)
    {
        if (!columns.AllColumns && !columns.Columns.Contains(TypeMapping.VersionNumber))
        {
            row.Attributes.Remove(TypeMapping.VersionNumber);
            row.FormattedValues.Remove(TypeMapping.VersionNumber);
        }
        return row;
    }

    private static long LastVersion(Record row) =>
        long.TryParse(row.RowVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : throw new InvalidOperationException($"Row {row.Id} of '{row.LogicalName}' came back without its version.");

    /// <summary>
    /// The time a new token carries: <paramref name="now"/>, or the whole second after <paramref name="restoredAt"/> when that is
    /// later (the token keeps whole seconds, so a token issued in the second of the restore would otherwise read as before it).
    /// </summary>
    private static DateTime IssueTime(DateTime now, DateTime? restoredAt)
    {
        if (restoredAt is not { } restored) return now;
        var ceiling = new DateTime(restored.Ticks - restored.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        if (ceiling < restored) ceiling = ceiling.AddSeconds(1);
        return ceiling > now ? ceiling : now;
    }

    /// <exception cref="XrmdFaultException">400 when the token is expired (ruling C4, and the bound, restore and age rules).</exception>
    private static void DemandNotExpired(string entity, long token, DateTime? issued, TrackingState state, DateTime now)
    {
        if (token < state.PurgedVersion || state.EnabledVersion is not { } enabled || token + 1 < enabled
            || token > state.MinActive - 1
            || issued is not { } time
            || (state.RestoredAt is { } restored && time < restored)
            || time < now.AddDays(-state.ExpireDays))
            throw XrmdFaultException.InvalidArgument(
                $"The change tracking token has expired for table '{entity}': changes after it are no longer all known. " +
                "Sync again without a DataVersion to get every row and a new token.");
    }

    private sealed record TrackingState(long MinActive, long PurgedVersion, long? EnabledVersion, DateTime? RestoredAt, int ExpireDays, DateTime Now);

    /// <summary>
    /// The upper bound (<see cref="ISqlDialect.MinActiveRowVersionAsync"/>, before any row is read), the purged version, the table's start version, the last restore
    /// time, the retention in days, and the database's UTC time (one clock for the token time, the age rule and <c>restored_at</c>).
    /// </summary>
    private static async Task<TrackingState> ReadStateAsync(DataSession s, string entity, CancellationToken ct)
    {
        var minActive = await s.Dialect.MinActiveRowVersionAsync(s, ct);
        await using var cmd = s.Command($"""
            SELECT (SELECT purged_version FROM meta.change_tracking_state),
                   (SELECT enabled_version FROM meta.change_tracking_table WHERE entity_logical_name = @entity),
                   (SELECT restored_at FROM meta.change_tracking_state),
                   ({s.Dialect.Limit($"SELECT {s.Quote(ChangeTrackingSchema.ExpireDaysColumn)} FROM data.organization", "1")}),
                   {s.Dialect.UtcNow};
            """);
        cmd.AddParameter("@entity", DbType.String, entity, 128);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return new TrackingState(minActive, rd.IsDBNull(0) ? 0 : rd.GetInt64(0), rd.IsDBNull(1) ? null : rd.GetInt64(1),
            rd.IsDBNull(2) ? null : DateTime.SpecifyKind(rd.GetDateTime(2), DateTimeKind.Utc),
            rd.IsDBNull(3) ? ChangeTrackingSchema.DefaultExpireDays : rd.GetInt32(3),
            DateTime.SpecifyKind(rd.GetDateTime(4), DateTimeKind.Utc));
    }

    private static async Task<long> PurgedVersionAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command("SELECT purged_version FROM meta.change_tracking_state");
        return await cmd.ExecuteScalarAsync(ct) is long purged ? purged : 0;
    }

    /// <summary>
    /// Up to <paramref name="top"/> log rows of <paramref name="entity"/> strictly between the versions, by version. The bounds are
    /// bigint parameters written as row version values (<see cref="ISqlDialect.RowVersionValue"/>: cast to <c>binary(8)</c> on SQL
    /// Server), so the range seeks on the clustered index of <c>version</c>.
    /// </summary>
    private static async Task<List<(long Version, Guid Id)>> DeletionsAsync(DataSession s, string entity, long afterExclusive,
        long beforeExclusive, int top, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"""
            SELECT CAST(version AS bigint), object_id FROM meta.deleted_record
            WHERE version > {s.Dialect.RowVersionValue("@after")} AND version < {s.Dialect.RowVersionValue("@before")} AND entity_logical_name = @entity
            ORDER BY version
            """, "(@top)") + ";");
        cmd.AddParameter("@top", DbType.Int32, top);
        cmd.AddParameter("@after", DbType.Int64, afterExclusive);
        cmd.AddParameter("@before", DbType.Int64, beforeExclusive);
        cmd.AddParameter("@entity", DbType.String, entity, 128);
        var rows = new List<(long, Guid)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) rows.Add((rd.GetInt64(0), rd.GetGuid(1)));
        return rows;
    }

    private static async Task<string?> GlobalMetadataVersionAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT {s.Quote("current")} FROM meta.version_stamp");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The paging cookie of ruling C7: <c>{to}!{phase}!{lastVersion}!{issuedUnixSeconds}</c>, phase <c>u</c> (updates) or <c>d</c>
    /// (deletions). The issue time of page 1 rides along so every page answers the same <c>DataToken</c>.
    /// </summary>
    private sealed record Cookie(long To, char Phase, long Last, DateTime? Issued)
    {
        public static string Format(long to, char phase, long last, DateTime issued) =>
            string.Create(CultureInfo.InvariantCulture, $"{to}!{phase}!{last}!{new DateTimeOffset(issued, TimeSpan.Zero).ToUnixTimeSeconds()}");

        public static Cookie? TryParse(string? text)
        {
            var parts = text?.Split(ChangeToken.Separator);
            if (parts is not { Length: 3 or 4 } || parts[1] is not ("u" or "d")
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var to) || to < 1
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var last) || last >= to)
                return null;
            DateTime? issued = null;
            if (parts.Length == 4)
            {
                if (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    || seconds > 253402300799) return null;
                issued = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            return new Cookie(to, parts[1][0], last, issued);
        }
    }
}
