using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Audit;

/// <summary>
/// Reads and deletes <c>audit</c> rows for the audit messages (plan 13, rulings A7 and A8).
/// <list type="bullet">
/// <item>A row becomes the <see cref="AuditDetail"/> of its change data: <see cref="AttributeAuditDetail"/>,
/// <see cref="RelationshipAuditDetail"/>, <see cref="ShareAuditDetail"/> or <see cref="RolePrivilegeAuditDetail"/>, whose
/// <see cref="AuditDetail.AuditRecord"/> is the row as an <c>audit</c> entity (formatted <c>createdon</c>, <c>action</c> and
/// <c>operation</c>; <c>userid</c> and <c>callinguserid</c> with the user's name).</item>
/// <item>A history is newest first (<c>createdon</c> descending, then <c>auditid</c>). <see cref="PagingInfo"/>: <c>PageNumber</c>
/// (from 1), <c>Count</c> (1 to <see cref="MaxPageSize"/>; <see cref="MaxPageSize"/> when the paging info is null or its count
/// is 0), <c>ReturnTotalRecordCount</c> (<c>TotalRecordCount</c> is -1 otherwise). The paging cookie returned is
/// <c>&lt;cookie page="N" /&gt;</c>; it is accepted back as it is, but paging follows <c>PageNumber</c>. Any other cookie is
/// refused (400), never ignored.</item>
/// <item>Rows are read without the record or its table: the history of a deleted record, column or table stays readable.</item>
/// </list>
/// </summary>
public sealed partial class AuditReader(IMetadataStore store)
{
    /// <summary>The largest page, and the page size when no paging info is given.</summary>
    public const int MaxPageSize = 5000;

    /// <summary>
    /// Rows per DELETE statement: below the about 5,000 locks on one table at which SQL Server escalates a statement to a table
    /// lock, so a purge batch does not block the audited writes of the organization.
    /// </summary>
    private const int DeleteBatch = 4_000;

    private static string Columns(ISqlDialect d) => $"""
        a.{d.Quote(AuditTable.AuditId)}, a.{d.Quote(AuditTable.CreatedOn)}, a.{d.Quote(AuditTable.Action)}, a.{d.Quote(AuditTable.Operation)}, a.{d.Quote(AuditTable.ObjectId)},
        a.{d.Quote(AuditTable.ObjectTypeCodeColumn)}, a.{d.Quote(AuditTable.UserId)}, u.{d.Quote("fullname")}, a.{d.Quote(AuditTable.CallingUserId)}, c.{d.Quote("fullname")},
        a.{d.Quote(AuditTable.ChangeData)}, a.{d.Quote(AuditTable.TransactionId)}
        """;

    private static string From(ISqlDialect d) => $"""
        {d.DataTable(AuditTable.Name)} a
        LEFT JOIN {d.DataTable("systemuser")} u ON u.{d.Quote("systemuserid")} = a.{d.Quote(AuditTable.UserId)}
        LEFT JOIN {d.DataTable("systemuser")} c ON c.{d.Quote("systemuserid")} = a.{d.Quote(AuditTable.CallingUserId)}
        """;

    private static string Newest(ISqlDialect d) => $"a.{d.Quote(AuditTable.CreatedOn)} DESC, a.{d.Quote(AuditTable.AuditId)} DESC";

    private sealed record Row(Guid AuditId, DateTime CreatedOn, int Action, int Operation, Guid ObjectId, string ObjectTypeCode,
        Guid UserId, string? UserName, Guid? CallingUserId, string? CallingUserName, string ChangeData, Guid? TransactionId);

    /// <summary>The audit detail of the row <paramref name="auditId"/>.</summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist for an unknown id.</exception>
    public async Task<AuditDetail> DetailAsync(DataSession s, Guid auditId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT {Columns(s.Dialect)} FROM {From(s.Dialect)} WHERE a.{s.Quote(AuditTable.AuditId)} = @id");
        cmd.AddParameter("@id", DbType.Guid, auditId);
        var rows = await ReadAsync(cmd, ct);
        return rows.Count == 0 ? throw XrmdFaultException.ObjectDoesNotExist(AuditTable.Name, auditId) : ToDetail(rows[0], null);
    }

    /// <summary>
    /// The history of <paramref name="target"/> (its rows by id and table, whether the record still exists), newest first; with
    /// <paramref name="attribute"/>, only the attribute changes that hold that column, reduced to it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an invalid count or a paging cookie xrmverse did not write.</exception>
    public async Task<AuditDetailCollection> HistoryAsync(DataSession s, EntityReference target, string? attribute, PagingInfo? paging,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(target);
        var (page, count, withTotal) = Paging(paging);
        var collection = new AuditDetailCollection { TotalRecordCount = -1 };

        if (attribute is null)
        {
            // One row more than the page tells whether more records follow.
            await using var cmd = s.Command($"""
                SELECT {Columns(s.Dialect)} FROM {From(s.Dialect)}
                WHERE a.{s.Quote(AuditTable.ObjectId)} = @id AND a.{s.Quote(AuditTable.ObjectTypeCodeColumn)} = @type
                ORDER BY {Newest(s.Dialect)} {s.Dialect.Page("@skip", "@take")}
                """);
            AddTarget(cmd, target);
            cmd.AddParameter("@skip", DbType.Int64, (long)(page - 1) * count);
            cmd.AddParameter("@take", DbType.Int32, count + 1);
            var rows = await ReadAsync(cmd, ct);
            collection.MoreRecords = rows.Count > count;
            foreach (var row in rows.Take(count)) collection.AuditDetails.Add(ToDetail(row, null));
            if (withTotal)
            {
                await using var total = s.Command($"""
                    SELECT COUNT(*) FROM {s.DataTable(AuditTable.Name)}
                    WHERE {s.Quote(AuditTable.ObjectId)} = @id AND {s.Quote(AuditTable.ObjectTypeCodeColumn)} = @type
                    """);
                AddTarget(total, target);
                collection.TotalRecordCount = Convert.ToInt32(await total.ExecuteScalarAsync(ct));
            }
        }
        else
        {
            // The change data is our own JSON: the rows holding the column are found by decoding them (a record's history is small).
            await using var cmd = s.Command($"""
                SELECT {Columns(s.Dialect)} FROM {From(s.Dialect)}
                WHERE a.{s.Quote(AuditTable.ObjectId)} = @id AND a.{s.Quote(AuditTable.ObjectTypeCodeColumn)} = @type
                ORDER BY {Newest(s.Dialect)}
                """);
            AddTarget(cmd, target);
            var matching = (await ReadAsync(cmd, ct))
                .Select(row => (Row: row, Change: Decode(row)))
                .Where(r => r.Change is AttributeChange a && (a.Old.ContainsKey(attribute) || a.New.ContainsKey(attribute)))
                .ToList();
            // In long: a large PageNumber times Count does not fit an int. A page past the end is empty.
            var skip = (long)(page - 1) * count;
            collection.MoreRecords = matching.Count > skip + count;
            if (skip < matching.Count)
                foreach (var (row, change) in matching.Skip((int)skip).Take(count))
                    collection.AuditDetails.Add(ToDetail(row, change, attribute));
            if (withTotal) collection.TotalRecordCount = matching.Count;
        }

        collection.PagingCookie = string.Create(CultureInfo.InvariantCulture, $"<cookie page=\"{page}\" />");
        return collection;
    }

    /// <summary>Deletes every row of <paramref name="target"/> (by id and table); returns how many.</summary>
    public async Task<int> DeleteRecordAsync(DataSession s, EntityReference target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(target);
        await using var cmd = s.Command($"""
            DELETE FROM {s.DataTable(AuditTable.Name)}
            WHERE {s.Quote(AuditTable.ObjectId)} = @id AND {s.Quote(AuditTable.ObjectTypeCodeColumn)} = @type
            """);
        AddTarget(cmd, target);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// DeleteAuditData's emulation of Dataverse's quarterly partitions: deletes the rows of every calendar quarter (UTC) whose last
    /// day is on or before the date of <paramref name="endDateUtc"/>, never those of the quarter of <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// The rows go in batches (<see cref="DeleteBatch"/>), but in <paramref name="s"/>'s transaction: the DeleteAuditData message
    /// runs them in one transaction, which holds the locks of every deleted row until it commits.
    /// </remarks>
    /// <returns>The number of quarters that had rows.</returns>
    public async Task<int> DeleteUntilAsync(DataSession s, DateTime endDateUtc, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var current = QuarterStart(nowUtc);
        var cutoff = endDateUtc.Date >= DateTime.MaxValue.Date ? current : QuarterStart(endDateUtc.Date.AddDays(1));
        if (cutoff > current) cutoff = current;

        await using (var count = s.Command($"""
            SELECT COUNT(DISTINCT {s.Dialect.DatePart("year", s.Quote(AuditTable.CreatedOn))} * 10 + {s.Dialect.DatePart("quarter", s.Quote(AuditTable.CreatedOn))})
            FROM {s.DataTable(AuditTable.Name)} WHERE {s.Quote(AuditTable.CreatedOn)} < @cutoff
            """))
        {
            count.AddParameter("@cutoff", DbType.DateTime2, cutoff);
            var partitions = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            if (partitions == 0) return 0;
            await DeleteInBatchesAsync(s, $"{s.Quote(AuditTable.CreatedOn)} < @cutoff",
                c => c.AddParameter("@cutoff", DbType.DateTime2, cutoff), ct);
            return partitions;
        }
    }

    /// <summary>The first instant (UTC) of the calendar quarter of <paramref name="utc"/>.</summary>
    public static DateTime QuarterStart(DateTime utc) => new(utc.Year, (utc.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Deletes the rows matching <paramref name="where"/> in batches of <see cref="DeleteBatch"/>, each under the lock-escalation
    /// threshold. Outside a transaction each batch commits on its own; in one, the locks are held until it ends.
    /// </summary>
    internal static async Task<int> DeleteInBatchesAsync(DataSession s, string where, Action<DbCommand> parameters, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            // Plan 2.7a, Task 6: a batch is the rows of a limited key read (PostgreSQL has no DELETE TOP).
            var table = s.DataTable(AuditTable.Name);
            var id = s.Quote(AuditTable.AuditId);
            await using var cmd = s.Command(
                $"DELETE FROM {table} WHERE {id} IN ({s.Dialect.Limit($"SELECT {id} FROM {table} WHERE {where}", DeleteBatch.ToString(CultureInfo.InvariantCulture))})");
            parameters(cmd);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
            if (deleted < DeleteBatch) return total;
        }
    }

    // ---- details ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The attribute detail of <paramref name="change"/> on a record of <paramref name="entity"/>, the table's current metadata
    /// (null when the table has been deleted). Values are typed by the metadata (a big integer read back as an <see cref="int"/>
    /// becomes a <see cref="long"/>) and carry their formatted values; a cleared (null) value is left out. A stored column the
    /// table no longer has goes to <see cref="AttributeAuditDetail.DeletedAttributes"/> (its logical name, keyed by position:
    /// xrmverse keeps no column number), as do all of them when the table is gone, whose entities are named after
    /// <paramref name="objectTypeCode"/> and carry no values. With <paramref name="attribute"/>, only that column is kept;
    /// <paramref name="tables"/> resolves the table names of EntityName values.
    /// </summary>
    public static AttributeAuditDetail AttributeDetail(EntityMetadata? entity, string objectTypeCode, AttributeChange change,
        string? attribute = null, Func<string, EntityMetadata?>? tables = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        var detail = new AttributeAuditDetail
        {
            OldValue = new Entity(entity?.LogicalName ?? objectTypeCode),
            NewValue = new Entity(entity?.LogicalName ?? objectTypeCode),
        };
        var columns = (entity?.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        var deleted = new SortedSet<string>(StringComparer.Ordinal);
        Fill(detail.OldValue, change.Old);
        Fill(detail.NewValue, change.New);
        var position = 0;
        foreach (var name in deleted) detail.DeletedAttributes[position++] = name;
        return detail;

        void Fill(Entity image, IReadOnlyDictionary<string, object?> values)
        {
            foreach (var (name, stored) in values)
            {
                if (attribute is not null && name != attribute) continue;
                if (!columns.TryGetValue(name, out var column))
                {
                    deleted.Add(name);
                    continue;
                }
                if (stored is null) continue; // a cleared value is not listed (Learn's samples)
                var value = Typed(column, stored);
                image[name] = value;
                if (FormattedValues.Format(column, value, tables: tables) is { } formatted) image.FormattedValues[name] = formatted;
            }
        }
    }

    /// <summary>A decoded value in the SDK type of <paramref name="column"/>: the codec reads every whole number that fits as an <see cref="int"/>.</summary>
    private static object Typed(AttributeMetadata column, object value) => (column, value) switch
    {
        (BigIntAttributeMetadata, int number) => (long)number,
        _ => value,
    };

    private AuditDetail ToDetail(Row row, AuditChange? change, string? attribute = null)
    {
        change ??= Decode(row);
        AuditDetail detail = change switch
        {
            AttributeChange a => AttributeDetail(store.GetEntity(row.ObjectTypeCode), row.ObjectTypeCode, a, attribute, store.GetEntity),
            RelationshipChange r => Relationship(r),
            ShareChange share => new ShareAuditDetail { Principal = share.Principal, OldPrivileges = share.Old, NewPrivileges = share.New },
            RolePrivilegesChange p => new RolePrivilegeAuditDetail { OldRolePrivileges = [.. p.Old], NewRolePrivileges = [.. p.New] },
            _ => throw new InvalidOperationException($"The audit row {row.AuditId} holds a change of an unknown kind."),
        };
        detail.AuditRecord = Record(row);
        return detail;
    }

    private static RelationshipAuditDetail Relationship(RelationshipChange change)
    {
        var detail = new RelationshipAuditDetail { RelationshipName = change.Relationship };
        foreach (var target in change.Targets) detail.TargetRecords.Add(target);
        return detail;
    }

    /// <exception cref="InvalidOperationException">The change data cannot be read (a server fault, not the caller's).</exception>
    private static AuditChange Decode(Row row)
    {
        try
        {
            return ChangeData.Deserialize(row.ChangeData);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"The change data of audit row {row.AuditId} cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>The row as an <c>audit</c> entity, as the SDK returns it in <see cref="AuditDetail.AuditRecord"/>.</summary>
    private Entity Record(Row row)
    {
        var record = new Entity(AuditTable.Name, row.AuditId)
        {
            [AuditTable.AuditId] = row.AuditId,
            [AuditTable.CreatedOn] = row.CreatedOn,
            [AuditTable.Action] = new OptionSetValue(row.Action),
            [AuditTable.Operation] = new OptionSetValue(row.Operation),
            [AuditTable.ObjectId] = new EntityReference(row.ObjectTypeCode, row.ObjectId),
            [AuditTable.ObjectTypeCodeColumn] = row.ObjectTypeCode,
            [AuditTable.UserId] = new EntityReference("systemuser", row.UserId) { Name = row.UserName },
            [AuditTable.ChangeData] = row.ChangeData,
        };
        if (row.CallingUserId is { } calling)
            record[AuditTable.CallingUserId] = new EntityReference("systemuser", calling) { Name = row.CallingUserName };
        if (row.TransactionId is { } transaction) record[AuditTable.TransactionId] = transaction;

        var columns = (store.GetEntity(AuditTable.Name)?.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        foreach (var name in new[] { AuditTable.CreatedOn, AuditTable.Action, AuditTable.Operation, AuditTable.UserId, AuditTable.CallingUserId })
            if (record.Attributes.TryGetValue(name, out var value) && value is not null && columns.TryGetValue(name, out var column)
                && FormattedValues.Format(column, value, tables: store.GetEntity) is { } formatted)
                record.FormattedValues[name] = formatted;
        return record;
    }

    // ---- paging and SQL ---------------------------------------------------------------------------------------------

    /// <exception cref="XrmdFaultException">400 for a count above <see cref="MaxPageSize"/> or a negative one, a negative page,
    /// or a paging cookie other than the one this reader writes.</exception>
    private static (int Page, int Count, bool WithTotal) Paging(PagingInfo? paging)
    {
        if (paging is null) return (1, MaxPageSize, false);
        if (paging.Count is < 0 or > MaxPageSize)
            throw XrmdFaultException.InvalidArgument(
                $"PagingInfo.Count {paging.Count} is not supported: a page holds 1 to {MaxPageSize} audit records.");
        if (paging.PageNumber < 0)
            throw XrmdFaultException.InvalidArgument($"PagingInfo.PageNumber {paging.PageNumber} is not valid: pages are numbered from 1.");
        if (!string.IsNullOrEmpty(paging.PagingCookie) && !CookiePattern().IsMatch(paging.PagingCookie))
            throw XrmdFaultException.InvalidArgument(
                "The PagingInfo.PagingCookie was not written by xrmverse: send back the AuditDetailCollection.PagingCookie of the previous page, or none.");
        return (Math.Max(paging.PageNumber, 1), paging.Count == 0 ? MaxPageSize : paging.Count, paging.ReturnTotalRecordCount);
    }

    private static void AddTarget(DbCommand cmd, EntityReference target)
    {
        cmd.AddParameter("@id", DbType.Guid, target.Id);
        cmd.AddParameter("@type", DbType.String, target.LogicalName ?? "", 64);
    }

    private static async Task<List<Row>> ReadAsync(DbCommand cmd, CancellationToken ct)
    {
        var rows = new List<Row>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            rows.Add(new Row(rd.GetGuid(0), DateTime.SpecifyKind(rd.GetDateTime(1), DateTimeKind.Utc), rd.GetInt32(2), rd.GetInt32(3),
                rd.GetGuid(4), rd.GetString(5), rd.GetGuid(6), rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetGuid(8),
                rd.IsDBNull(9) ? null : rd.GetString(9), rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetGuid(11)));
        return rows;
    }

    [GeneratedRegex("""\A\s*<cookie\s+page="[0-9]{1,9}"\s*/>\s*\z""", RegexOptions.CultureInvariant)]
    private static partial Regex CookiePattern();
}
