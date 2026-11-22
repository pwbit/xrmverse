using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>A unique index of the <c>data</c> schema on a text column compared as stored (plan 2.7b, ruling Q3).</summary>
/// <param name="Table">The data table.</param>
/// <param name="Index">The index, the alternate key's name.</param>
/// <param name="Columns">The key columns in order, each with true when it is text.</param>
/// <param name="Filter">The predicate of a filtered index, or null.</param>
public sealed record PlainTextKey(string Table, string Index, IReadOnlyList<(string Column, bool Text)> Columns, string? Filter);

/// <summary>
/// The unique (alternate) keys on text columns of a 2.7a PostgreSQL organization (plan 2.7b, ruling Q3, #299): keys whose index
/// compares the stored text (<c>citext</c>) instead of its text key (<see cref="ISqlDialect.TextKey"/>). The system seed step
/// <see cref="SystemSeed.TextKeysStep"/> rebuilds them; one it cannot rebuild, because its rows already collide under the new rule,
/// stays as it was and is reported by the health report and the doctor until its rows are fixed.
/// </summary>
public static class TextKeys
{
    /// <summary>The report of a key the seed step could not rebuild (plan 2.7b, ruling Q3; our wording).</summary>
    /// <remarks>
    /// The ruling names <c>xrmd org upgrade</c>, which the CLI does not have: <c>xrmd server upgrade</c> restarts the server, whose
    /// startup runs the seed step again.
    /// </remarks>
    public static string ConflictMessage(string table, string key) =>
        $"Alternate key '{key}' on '{table}' has values that differ only by trailing spaces, width, kana or ß; fix the rows, then run 'xrmd server upgrade'.";

    /// <summary>
    /// The plain text keys of the organization of <paramref name="conn"/> (<see cref="ISqlDialect.PlainTextKeyIndexes"/>); none on
    /// an engine whose keys compare like this already (SQL Server).
    /// </summary>
    public static async Task<IReadOnlyList<PlainTextKey>> ReadPlainAsync(DbConnection conn, DbTransaction? tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (SqlDialects.For(conn).PlainTextKeyIndexes is not { } sql) return [];
        var rows = new List<(string Table, string Index, string Column, bool Text, string? Filter)>();
        await using (var cmd = conn.Command(sql, tx))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                rows.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetBoolean(3), rd.IsDBNull(4) ? null : rd.GetString(4)));
        // Only tables and columns named like xrmverse names them (logical names); any other index is left alone.
        return rows.GroupBy(r => (r.Table, r.Index))
            .Where(g => LogicalNameValidator.IsValid(g.Key.Table) && g.All(r => LogicalNameValidator.IsValid(r.Column)))
            .Select(g => new PlainTextKey(g.Key.Table, g.Key.Index, g.Select(r => (r.Column, r.Text)).ToList(), g.First().Filter))
            .ToList();
    }
}
