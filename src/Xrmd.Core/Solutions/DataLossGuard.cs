using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Solution;

namespace Xrmd.Core.Solutions;

/// <summary>
/// Data a solution operation would drop (plan 2.6, ruling M10): a table with <paramref name="Rows"/> rows when
/// <paramref name="Column"/> is null, else a column of <paramref name="Table"/> with <paramref name="Rows"/> non-null values.
/// </summary>
/// <param name="Table">The table dropped, or the table of the column dropped.</param>
/// <param name="Column">The column dropped; null for the whole table.</param>
/// <param name="Rows">The rows of the table, or the rows where the column (or one of its companions) has a value.</param>
public sealed record DataLoss(string Table, string? Column, long Rows);

/// <summary>A table (<paramref name="Column"/> null) or column a caller expects a solution operation to drop (final review M1).</summary>
/// <param name="Table">The table's logical name.</param>
/// <param name="Column">The column's logical name; null for the whole table.</param>
public sealed record DataLossKey(string Table, string? Column);

/// <summary>
/// The data-loss guard in front of the component deletes of the solution operations (plan 2.6, ruling M10, a deliberate
/// divergence from Dataverse): lists every table to drop that has at least one row and every column to drop that has at least
/// one non-null value (a column's companions, such as a money column's <c>_base</c>, count with it). The SDK paths refuse
/// when the list is not empty; only the Admin API drops, with an explicit confirmation. Holds no state.
/// </summary>
public sealed class DataLossGuard(IMetadataStore store)
{
    /// <summary>
    /// The data the deletes of <paramref name="ordered"/> (as <see cref="ComponentRemover.ExecuteAsync"/> runs them) would drop,
    /// read on <paramref name="s"/> with the published metadata (before any write of the operation): the rows of each table
    /// deleted, the rows of the intersect table of each many-to-many relationship deleted, the values of each column deleted and
    /// of the lookup of each one-to-many relationship deleted (except where its table is dropped as a whole).
    /// </summary>
    public async Task<IReadOnlyList<DataLoss>> FindAsync(DataSession s, IReadOnlyList<(int Type, Guid Id)> ordered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ordered);
        var meta = new ComponentIndex(store);
        var tables = new List<string>();
        var columns = new List<(EntityMetadata Entity, AttributeMetadata Attribute)>();
        foreach (var (rawType, id) in ordered)
        {
            switch (ComponentType.Normalize(rawType))
            {
                case ComponentType.Entity when meta.Entities.TryGetValue(id, out var e):
                    tables.Add(e.LogicalName);
                    break;
                case ComponentType.Attribute when meta.Attributes.TryGetValue(id, out var a):
                    columns.Add(a);
                    break;
                case ComponentType.EntityRelationship when meta.ManyToMany.TryGetValue(id, out var m):
                    tables.Add(m.IntersectEntityName);
                    break;
                case ComponentType.EntityRelationship when meta.OneToMany.TryGetValue(id, out var r)
                    && meta.EntitiesByName.GetValueOrDefault(r.ReferencingEntity) is { } referencing
                    && (referencing.Attributes ?? []).FirstOrDefault(x => x.LogicalName == r.ReferencingAttribute) is { } lookup:
                    columns.Add((referencing, lookup));
                    break;
            }
        }

        var dropped = tables.ToHashSet(StringComparer.Ordinal);
        var losses = new List<DataLoss>();
        // In the operation's transaction, each count keeps a shared table lock until the commit (final review I3): no row
        // can be inserted, and no value written, between the count and the DROP that follows it.
        var locked = s.Transaction is not null;
        foreach (var table in tables.Distinct(StringComparer.Ordinal))
        {
            if (store.GetEntity(table) is null) continue;
            var rows = await CountAsync(s, table, CountSql(s.Dialect, table, null, locked), locked, ct);
            if (rows > 0) losses.Add(new DataLoss(table, null, rows));
        }
        foreach (var (e, a) in columns.DistinctBy(c => (c.Entity.LogicalName, c.Attribute.LogicalName)))
        {
            if (dropped.Contains(e.LogicalName)) continue;
            var physical = PhysicalColumns(e, a);
            if (physical.Count == 0) continue;
            var rows = await CountAsync(s, e.LogicalName, CountSql(s.Dialect, e.LogicalName, physical, locked), locked, ct);
            if (rows > 0) losses.Add(new DataLoss(e.LogicalName, a.LogicalName, rows));
        }
        return losses;
    }

    /// <summary>
    /// The confirmation of a data loss (final review M1): with <paramref name="expected"/> (what the caller's dry run showed),
    /// every loss of <paramref name="losses"/> (the plan made under the lock) must be in it, compared by table and column
    /// (ordinal, ignoring case); the row counts may differ. Null expects nothing in particular.
    /// </summary>
    /// <param name="what">The operation for the message, e.g. <c>The uninstall of 'xm_base'</c>.</param>
    /// <param name="verb">The operation to retry, e.g. <c>uninstall</c>.</param>
    /// <exception cref="XrmdFaultException">409 naming the losses that were not expected; nothing is changed.</exception>
    public static void DemandExpected(IReadOnlyList<DataLoss> losses, IReadOnlyCollection<DataLossKey>? expected, string what, string verb)
    {
        ArgumentNullException.ThrowIfNull(losses);
        if (expected is null) return;
        var known = expected.Select(e => (e.Table.ToLowerInvariant(), e.Column?.ToLowerInvariant())).ToHashSet();
        var unexpected = losses.Where(l => !known.Contains((l.Table.ToLowerInvariant(), l.Column?.ToLowerInvariant()))).ToList();
        if (unexpected.Count > 0)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"{what} would now drop data its plan did not show: {Describe(unexpected)}. Nothing was changed; review the plan again (dry run) and confirm the {verb}.", 409);
    }

    /// <summary>The losses as a refusal names them: each table with its rows and each column with its values.</summary>
    public static string Describe(IReadOnlyList<DataLoss> losses)
    {
        ArgumentNullException.ThrowIfNull(losses);
        return string.Join(", ", losses.Select(l => l.Column is null
            ? $"table {l.Table} ({Rows(l.Rows)})"
            : $"column {l.Table}.{l.Column} ({l.Rows} {(l.Rows == 1 ? "value" : "values")})"));
    }

    private static string Rows(long rows) => $"{rows} {(rows == 1 ? "row" : "rows")}";

    /// <summary>The physical columns of <paramref name="a"/> and of its companions that have a column of their own.</summary>
    private static List<string> PhysicalColumns(EntityMetadata e, AttributeMetadata a)
    {
        var names = TypeMapping.ToColumns(a).Select(c => c.Name).ToList();
        // The companions found as the remover finds them (money _base, AttributeOf), plus the companions with a column of their own.
        foreach (var companion in (e.Attributes ?? []).Where(c => c.LogicalName != a.LogicalName
                     && (c.AttributeOf == a.LogicalName || ComponentRemover.StoredBy(c) == a.LogicalName)))
            names.AddRange(TypeMapping.ToColumns(companion).Select(c => c.Name));
        return names.Where(n => n != "versionnumber").Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The count of the rows of <paramref name="table"/>, or, with <paramref name="columns"/>, of its rows where one of them has
    /// a value. With <paramref name="locked"/> (in a transaction) the count holds a shared table lock to the end of the
    /// transaction, so the data counted is the data the drop finds: <c>WITH (TABLOCK, HOLDLOCK)</c> on SQL Server; on PostgreSQL
    /// the <c>LOCK TABLE … IN SHARE MODE</c> that <see cref="CountAsync"/> runs before it (plan 2.7b, ruling Q4).
    /// </summary>
    /// <param name="d">The dialect of the session.</param>
    /// <param name="table">A table name from the published metadata (validated), quoted here.</param>
    /// <param name="columns">Physical column names from the published metadata, quoted here; null for the table.</param>
    public static string CountSql(ISqlDialect d, string table, IReadOnlyCollection<string>? columns, bool locked)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(table);
        var sql = $"SELECT {d.CountBig("*")} FROM {DdlGenerator.Table(table, d)}{(locked ? d.LockHint(RowLock.TableShared) : "")}";
        return columns is null ? sql : $"{sql} WHERE {string.Join(" OR ", columns.Select(c => $"{d.Quote(c)} IS NOT NULL"))}";
    }

    private static async Task<long> CountAsync(DataSession s, string table, string sql, bool locked, CancellationToken ct)
    {
        if (locked) await s.Dialect.LockTableSharedAsync(s, DdlGenerator.DataSchema, table, ct);
        await using var cmd = s.Command(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
