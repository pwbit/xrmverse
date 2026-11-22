using System.Data;
using System.Data.Common;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Query.Model;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Duplicates;

/// <summary>
/// The duplicate lookups of plan 2.15 on the match code tables of the Published rules: the Create/Update check (rulings D1, D2,
/// D12) and <c>RetrieveDuplicates</c> (D14). A lookup (D13) codes the base-side values of each Published rule and reads the
/// matching rows with that code, joined to the matching table so a row deleted outside the core never matches. A rule in
/// Publishing never takes part: its codes may be incomplete.
/// </summary>
public sealed class DuplicateFinder(CoreOperations core, IMetadataStore store, MatchCodeStore codes)
{
    /// <summary>The optional parameter of Create and Update that asks for the check when false (absent: true, Learn).</summary>
    public const string SuppressParameter = "SuppressDuplicateDetection";

    /// <summary>The message name of <see cref="RetrieveAsync"/>.</summary>
    public const string RetrieveMessage = "RetrieveDuplicates";

    /// <summary>The ids of the matches a <see cref="RetrieveAsync"/> reads as the caller per query (within SQL Server's 2100 parameters).</summary>
    private const int ReadChunk = 1000;

    /// <summary>
    /// The rows of <paramref name="matchingEntity"/> that duplicate <paramref name="values"/> (a row of <paramref name="baseEntity"/>)
    /// under a Published rule from the one table to the other (ruling D13): at most <paramref name="top"/> ids, distinct, in
    /// <c>uniqueidentifier</c> order (stable for paging), without <paramref name="exclude"/>. Every row counts, whatever the
    /// reader may see; a rule with <c>excludeinactiverecords</c> leaves out the rows whose <c>statecode</c> is not 0.
    /// </summary>
    /// <param name="values">The value of a base-side attribute (an SDK value), null when the row has none.</param>
    /// <exception cref="XrmdFaultException">400 for an unknown table, or a value that cannot be coded.</exception>
    public async Task<IReadOnlyList<Guid>> FindAsync(DataSession s, string baseEntity, string matchingEntity, Func<string, object?> values,
        Guid? exclude, int top, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(values);
        await codes.Cache.EnsureLoadedAsync(s, ct);
        var rules = Published(baseEntity).Where(r => r.MatchingEntity == matchingEntity).ToList();
        return await FindAsync(s, EntityOf(baseEntity), EntityOf(matchingEntity), rules, values, exclude, top, ct);
    }

    /// <summary>
    /// True when a row of the matching table of <paramref name="rule"/> duplicates <paramref name="values"/> (the row
    /// <paramref name="id"/> of its base table) under that rule alone (ruling D13; the row itself is not its own duplicate): the
    /// lookup of <c>BulkDetectDuplicates</c> (D15), which records each rule with a match.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown table, or a value that cannot be coded.</exception>
    public async Task<bool> HasDuplicateAsync(DataSession s, PublishedRule rule, Func<string, object?> values, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(values);
        var m = EntityOf(rule.BaseEntity);
        var matching = EntityOf(rule.MatchingEntity);
        return (await FindAsync(s, m, matching, [rule], values, Self(m, matching, id), 1, ct)).Count > 0;
    }

    /// <summary>
    /// The Create/Update check (rulings D1, D2): refuses <paramref name="merged"/> when a row duplicates it under a Published rule
    /// whose base table is <paramref name="m"/>, provided the organization switches (<c>isduplicatedetectionenabled</c> and
    /// <c>…foronlinecreateupdate</c>) and the table's <c>IsDuplicateDetectionEnabled</c> are on. The row itself is not a duplicate.
    /// </summary>
    /// <param name="merged">The values the write stores (stage 20 plug-ins included). On Update it carries the changed columns
    /// only: the rest are read from the stored row (a row that does not exist is not checked; the Update answers 404).</param>
    /// <param name="changedColumns">Null on Create. On Update the columns written: only the rules with a base-side attribute among
    /// them whose code differs from the stored row's code are checked (RF1), so editing a row that already has a duplicate, or
    /// sending its matched values back unchanged, does not fail.</param>
    /// <exception cref="XrmdFaultException">412 <see cref="ErrorCodes.DuplicateRecordsFound"/>.</exception>
    public async Task DemandNoDuplicateAsync(DataSession s, EntityMetadata m, Entity merged, Guid id, IReadOnlyCollection<string>? changedColumns,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(merged);
        if (m.IsDuplicateDetectionEnabled?.Value != true) return;
        await codes.Cache.EnsureLoadedAsync(s, ct);
        var rules = Published(m.LogicalName).ToList();
        if (changedColumns is not null)
        {
            var changed = changedColumns.ToHashSet(StringComparer.Ordinal);
            rules = rules.Where(r => r.Conditions.Any(c => changed.Contains(c.BaseAttribute))).ToList();
        }
        if (rules.Count == 0 || !await SwitchesOnAsync(s, ct)) return;

        Func<string, object?> values = name => merged.Attributes.TryGetValue(name, out var v) ? v : null;
        if (changedColumns is not null)
        {
            // RF1 by code: a rule whose code the Update leaves as stored (a column re-sent unchanged, or changed only in what the
            // rule ignores, such as case) is not checked, so a row that already has a duplicate can still be edited.
            if (await codes.ReadValuesAsync(s, m, id, BaseAttributes(rules), ct) is not { } stored) return;
            Func<string, object?> before = name => stored.GetValueOrDefault(name);
            values = name => merged.Attributes.TryGetValue(name, out var v) ? v : stored.GetValueOrDefault(name);
            var after = values;
            rules = rules.Where(r => !SameCode(BaseCode(r, m, after), BaseCode(r, m, before))).ToList();
        }
        foreach (var group in rules.GroupBy(r => r.MatchingEntity, StringComparer.Ordinal))
        {
            var matching = EntityOf(group.Key);
            if ((await FindAsync(s, m, matching, group.ToList(), values, Self(m, matching, id), 1, ct)).Count > 0)
                throw XrmdFaultException.DuplicateRecordsFound();
        }
    }

    /// <summary>
    /// <c>RetrieveDuplicates</c> (ruling D14): the rows of <paramref name="matchingEntity"/> that duplicate <paramref name="business"/>
    /// under the Published rules from its table to that one (none: an empty collection). When <paramref name="business"/> has the
    /// id of a stored row, the stored values are read and its attributes overlay them, and the row itself is left out. The matches
    /// are read as <paramref name="caller"/> through the query path (all columns): record and field security apply, and a match the
    /// caller cannot read is left out before paging. A rule condition on a secured column (base or matching side) the caller
    /// cannot read is refused as a query condition on it is (ruling F3).
    /// </summary>
    /// <param name="paging">Absent, or a zero <c>Count</c>/<c>PageNumber</c>: page 1 of <see cref="MaxPageSize"/>.</param>
    /// <exception cref="XrmdFaultException">400 for an unknown table, a page size outside 1 to 5000 or a page number below 1; 403
    /// FieldReadDenied (0x8004F504) when an applicable rule compares a secured column the caller cannot read.</exception>
    public async Task<EntityCollection> RetrieveAsync(DataSession s, Caller caller, Entity business, string matchingEntity, PagingInfo? paging,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(business);
        var size = paging is { Count: not 0 } ? paging.Count : MaxPageSize;
        var page = paging is { PageNumber: not 0 } ? paging.PageNumber : 1;
        if (size is < 1 or > MaxPageSize)
            throw XrmdFaultException.InvalidArgument($"The page size of {RetrieveMessage} must be between 1 and {MaxPageSize}, got {size}.");
        if (page < 1) throw XrmdFaultException.InvalidArgument($"The page number of {RetrieveMessage} must be 1 or greater, got {page}.");
        var m = EntityOf(business.LogicalName);
        var matching = EntityOf(matchingEntity);
        var result = new EntityCollection { EntityName = matching.LogicalName };

        await codes.Cache.EnsureLoadedAsync(s, ct);
        var rules = Published(m.LogicalName).Where(r => r.MatchingEntity == matching.LogicalName).ToList();
        if (rules.Count == 0) return result;
        // Ruling F3: a condition is a filter on the matched rows (and, with a stored id, on that row's values), and the matches
        // come back: the caller must be able to read every column the rules compare, as for a condition of a query.
        await core.DemandFilterableAsync(caller, rules.SelectMany(r => r.Conditions)
            .SelectMany(c => new[] { (m.LogicalName, c.BaseAttribute), (matching.LogicalName, c.MatchingAttribute) })
            .Distinct(), ct);

        Func<string, object?> values = name => business.Attributes.TryGetValue(name, out var v) ? v : null;
        Guid? self = null;
        if (RecordId(m, business) is { } id
            && await codes.ReadValuesAsync(s, m, id, BaseAttributes(rules).Where(a => !business.Contains(a)), ct) is { } stored)
        {
            values = name => business.Attributes.TryGetValue(name, out var v) ? v : stored.GetValueOrDefault(name);
            self = Self(m, matching, id);
        }
        var candidates = await FindAsync(s, m, matching, rules, values, self, int.MaxValue, ct);

        // The readable matches, in the candidates' order, until one past the page (for MoreRecords).
        var wanted = (long)page * size + 1;
        var readable = new List<Entity>();
        foreach (var chunk in candidates.Chunk(ReadChunk))
        {
            readable.AddRange(await ReadAsCallerAsync(s, caller, matching, chunk, ct));
            if (readable.Count >= wanted) break;
        }
        var skip = (long)(page - 1) * size;
        result.Entities.AddRange(readable.Skip((int)Math.Min(skip, int.MaxValue)).Take(size));
        result.MoreRecords = readable.Count > skip + size;
        return result;
    }

    // ---- lookups ------------------------------------------------------------------------------------------------------

    private IEnumerable<PublishedRule> Published(string baseEntity) =>
        codes.Cache.ForBase(baseEntity).Where(r => r.StatusCode == RuleStatus.Published);

    private static byte[]? BaseCode(PublishedRule rule, EntityMetadata m, Func<string, object?> values) =>
        MatchCode.Compute(rule, MatchSide.Base, values, name => Attribute(m, name));

    private static bool SameCode(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static IEnumerable<string> BaseAttributes(IEnumerable<PublishedRule> rules) =>
        rules.SelectMany(r => r.Conditions).Select(c => c.BaseAttribute).Distinct(StringComparer.Ordinal);

    /// <summary>The row excluded from its own matches: only when the base and the matching table are one.</summary>
    private static Guid? Self(EntityMetadata m, EntityMetadata matching, Guid id) => m.LogicalName == matching.LogicalName ? id : null;

    private static Guid? RecordId(EntityMetadata m, Entity entity) =>
        entity.Id != Guid.Empty ? entity.Id
        : entity.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(m), out var pk) && pk is Guid id && id != Guid.Empty ? id
        : null;

    private EntityMetadata EntityOf(string? logicalName) =>
        store.GetEntity(logicalName ?? "")
        ?? throw XrmdFaultException.InvalidArgument($"The entity with a name = '{logicalName}' was not found in the MetadataCache.");

    private static AttributeMetadata Attribute(EntityMetadata m, string name) =>
        m.Attributes?.FirstOrDefault(a => a.LogicalName == name)
        ?? throw new InvalidOperationException($"The column '{m.LogicalName}.{name}' of a duplicate detection rule does not exist.");

    /// <summary>The lookup of D13 over <paramref name="rules"/> (all from <paramref name="m"/> to <paramref name="matching"/>).</summary>
    /// <remarks>
    /// Plan 2.7b, Task 4: one portable query, a <c>UNION</c> of one branch per rule, over the rules whose match code table is
    /// still registered (<see cref="MatchCodeStore.RegisteredAsync"/>, whose lock is held, so an unpublish of one of them waits
    /// for the end of the transaction instead of dropping the table under the query).
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> FindAsync(DataSession s, EntityMetadata m, EntityMetadata matching,
        IReadOnlyList<PublishedRule> rules, Func<string, object?> values, Guid? exclude, int top, CancellationToken ct)
    {
        if (top < 1) return [];
        var coded = new List<(int Index, Guid Rule, byte[] Code)>();
        for (var k = 0; k < rules.Count; k++)
            if (BaseCode(rules[k], m, values) is { } code) coded.Add((k, rules[k].Id, code));
        if (coded.Count == 0) return [];
        // A rule unpublished by another session since the cache was loaded has no table any more: it is skipped.
        var registered = await MatchCodeStore.RegisteredAsync(s, coded.Select(c => c.Rule), ct);
        coded.RemoveAll(c => !registered.Contains(c.Rule));
        if (coded.Count == 0) return [];

        var d = s.Dialect;
        var pk = SystemAttributes.PrimaryIdName(matching);
        var hasState = matching.Attributes?.Any(a => a.LogicalName == "statecode") == true;
        var parameters = new List<DbParameter>();
        var branches = new List<string>();
        foreach (var (k, rule, code) in coded)
        {
            parameters.Add(new DetachedParameter($"@_c{k}", DbType.Binary, code, MatchCode.Length));
            var active = rules[k].ExcludeInactive && hasState ? $" AND d.{d.Quote("statecode")} = 0" : "";
            var self = exclude is null ? "" : " AND c.objectid <> @_exclude";
            branches.Add($"""
                SELECT c.objectid AS id FROM meta.{d.Quote(MatchCodeTableName(rule))} AS c
                JOIN {s.DataTable(matching.LogicalName)} AS d ON d.{d.Quote(pk)} = c.objectid
                WHERE c.matchcode = @_c{k}{self}{active}
                """);
        }
        if (exclude is { } excluded) parameters.Add(new DetachedParameter("@_exclude", DbType.Guid, excluded));
        parameters.Add(new DetachedParameter("@_top", DbType.Int32, top));
        await using var cmd = s.Command(d.Limit($"SELECT u.id FROM (\n{string.Join("\nUNION\n", branches)}\n) AS u ORDER BY u.id", "(@_top)"));
        cmd.AddParameters(parameters);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// The rows <paramref name="ids"/> of <paramref name="matching"/> the caller can read, all columns, through the query path (so
    /// field masks apply), ordered by primary key like the candidates.
    /// </summary>
    private async Task<IReadOnlyCollection<Entity>> ReadAsCallerAsync(DataSession s, Caller caller, EntityMetadata matching,
        IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var pk = SystemAttributes.PrimaryIdName(matching);
        var root = new EntityNode { EntityName = matching.LogicalName, AllColumns = true };
        root.Filter.Conditions.Add(new ConditionNode(null, pk, ConditionOperator.In, ids.Cast<object?>().ToList()));
        root.Orders.Add(new OrderNode(pk, Descending: false));
        return (await core.RetrieveMultipleAsync(s, caller, new QueryModel { Root = root }, ct)).Entities;
    }

    /// <summary>The organization switches of the Create/Update check: <c>isduplicatedetectionenabled</c> and <c>…foronlinecreateupdate</c>.</summary>
    private static async Task<bool> SwitchesOnAsync(DataSession s, CancellationToken ct)
    {
        var d = s.Dialect;
        string On(string column) => $"{d.IsNull(d.Quote(column), d.BooleanLiteral(false))} = {d.BooleanLiteral(true)}";
        await using var cmd = s.Command(d.Limit(
            $"SELECT CAST(CASE WHEN {On(OrgEnabled)} AND {On(OrgEnabledForOnlineCreateUpdate)} THEN 1 ELSE 0 END AS {d.ColumnType(SqlColumnKind.Boolean)}) " +
            $"FROM {s.DataTable("organization")}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
