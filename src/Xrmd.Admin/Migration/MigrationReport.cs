using System.Data;
using System.Data.Common;
using Xrmd.Abstractions.Storage;
using Xrmd.Migration.Users;
using System.Text.Json.Nodes;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration.Import;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The report of an import run (migration spec §7.8, plan M4a task 6), read from the run's ledger: per table the records exported
/// (the manifest), imported and failed (the committed sub-chunks); the issues (at most <see cref="MaxIssues"/>); the lookups left
/// empty per column; the users created or mapped by action; when each phase ran.
/// </summary>
public static class MigrationReport
{
    public const int MaxIssues = 10_000;

    /// <summary>The users' actions the report counts (from the security phase's <c>user-&lt;action&gt;</c> issues).</summary>
    private static readonly string[] UserActionsCounted = [UserActions.Entra, UserActions.EntraRemap, UserActions.Local, UserActions.Stub, UserActions.Existing];

    public static async Task<JsonObject> BuildAsync(IDbConnectionFactory factory, MigrationRunRow run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(run);
        var ledger = new MigrationLedger(factory, run.RunId);
        await using var conn = await factory.OpenAsync(ct);
        var d = factory.Dialect;

        // Imported and failed rows per table: the committed sub-chunks of the security and records phases, by the table in their key
        // (security/<table>#n, data/<table>/<chunk>#n).
        var tables = new JsonArray();
        await using (var cmd = Command(conn, $"""
            SELECT t.tablename, t.exported,
                {d.IsNull("SUM(c.rows - c.failedrows)", "0")} AS imported, {d.IsNull("SUM(c.failedrows)", "0")} AS failed
            FROM meta.migration_table t
            LEFT JOIN meta.migration_chunk c ON c.runid = t.runid AND c.phase IN (3, 4)
                AND (c.entry LIKE {d.Concat("'security/'", "t.tablename", "'#%'")} OR c.entry LIKE {d.Concat("'data/'", "t.tablename", "'/%'")})
            WHERE t.runid = @run
            GROUP BY t.tablename, t.exported
            ORDER BY t.tablename
            """, run.RunId))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                tables.Add(new JsonObject
                {
                    ["table"] = rd.GetString(0), ["exported"] = rd.GetInt64(1), ["imported"] = Convert.ToInt64(rd.GetValue(2)),
                    ["failed"] = Convert.ToInt64(rd.GetValue(3)),
                });

        var lookups = new JsonArray();
        await using (var cmd = Command(conn, """
            SELECT tablename, columnname, COUNT(*) FROM meta.migration_issue
            WHERE runid = @run AND code = 'lookup-nulled' GROUP BY tablename, columnname ORDER BY tablename, columnname
            """, run.RunId))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                lookups.Add(new JsonObject { ["table"] = rd.IsDBNull(0) ? null : rd.GetString(0), ["column"] = rd.IsDBNull(1) ? null : rd.GetString(1), ["count"] = Convert.ToInt32(rd.GetValue(2)) });

        var users = new JsonObject();
        foreach (var action in UserActionsCounted) users[action] = 0;
        await using (var cmd = Command(conn, """
            SELECT code, COUNT(*) FROM meta.migration_issue WHERE runid = @run AND code LIKE 'user-%' GROUP BY code
            """, run.RunId))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                if (rd.GetString(0)["user-".Length..] is var action && users.ContainsKey(action)) users[action] = Convert.ToInt32(rd.GetValue(1));

        var issues = await ledger.IssuesAsync(MaxIssues + 1, ct);
        var phases = await ledger.PhasesAsync(ct);
        var deactivated = await ledger.DeactivatedAsync(ct);
        return new JsonObject
        {
            ["runId"] = run.RunId.ToString(),
            ["state"] = run.State,
            ["tables"] = tables,
            ["issues"] = new JsonArray([.. issues.Take(MaxIssues).Select(i => (JsonNode)new JsonObject
            {
                ["phase"] = i.Phase, ["severity"] = i.Severity, ["code"] = i.Code, ["table"] = i.Table, ["recordId"] = i.RecordId?.ToString(),
                ["column"] = i.Column, ["message"] = i.Message,
            })]),
            ["issuesTruncated"] = issues.Count > MaxIssues,
            ["lookupsNulled"] = lookups,
            ["users"] = users,
            // Spec §7.9: what the import left off, with its state in the source, for 'xrmd migrate activate'.
            ["deactivated"] = new JsonArray([.. deactivated.Select(d => (JsonNode)new JsonObject
            {
                ["table"] = d.Table, ["id"] = d.Id.ToString(), ["name"] = d.Name, ["sourceState"] = d.SourceState, ["sourceStatus"] = d.SourceStatus,
                ["activatedOn"] = d.ActivatedOn,
            })]),
            ["phases"] = new JsonArray([.. phases.Select(p => (JsonNode)new JsonObject
            {
                ["phase"] = p.Phase, ["name"] = p.Name, ["startedOn"] = p.StartedOn, ["endedOn"] = p.EndedOn,
                ["seconds"] = p.EndedOn is { } end ? Math.Round((end - p.StartedOn).TotalSeconds, 3) : null,
            })]),
        };
    }

    private static DbCommand Command(DbConnection conn, string sql, Guid runId)
    {
        var cmd = conn.Command(sql);
        cmd.AddParameter("@run", DbType.Guid, runId);
        return cmd;
    }
}
