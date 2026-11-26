using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Xrmd.Abstractions.Storage;
using Xrmd.Migration.Users;
using System.Text.Json.Nodes;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration.Import;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The report of an import run (migration spec §7.8, plan M4a task 6), read from the run's ledger: per table the records exported
/// (the manifest), imported and failed (the committed sub-chunks); the issues (at most <see cref="MaxIssues"/>); the lookups left
/// empty per column; the users created or mapped by action; the mapped tables (table mapping spec §8.5); the archive candidates of a dry
/// run (§8.7); when each phase ran.
/// </summary>
public static partial class MigrationReport
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

        var mappedTables = await MappedTablesAsync(conn, run.RunId, ct);

        // Imported and failed rows per table: the committed sub-chunks of the security and records phases, by the table in their key
        // (security/<table>#n, data/<table>/<chunk>#n). The keys of a mapped table name its source (they are the package's chunk paths),
        // the ledger's tables the target: the mapped tables section pairs them. That pairing depends on the phase-1 'mapped-table'
        // census messages ("<source> → <target>: <n> rows"); a run without them counts a mapped table under its target name only.
        var sources = System.Text.Json.JsonSerializer.Serialize(mappedTables.Select(m => new { t = m!["target"]!.GetValue<string>(), s = m["source"]!.GetValue<string>() }));
        var tables = new JsonArray();
        await using (var cmd = Command(conn, $"""
            SELECT t.tablename, t.exported,
                {d.IsNull("SUM(c.rows - c.failedrows)", "0")} AS imported, {d.IsNull("SUM(c.failedrows)", "0")} AS failed
            FROM meta.migration_table t
            LEFT JOIN {d.JsonRecords("@sources", [new JsonColumn("t", SqlColumnKind.String, 128, "$.t"), new JsonColumn("s", SqlColumnKind.String, 128, "$.s")])} m
                ON m.t = t.tablename
            LEFT JOIN meta.migration_chunk c ON c.runid = t.runid AND c.phase IN (3, 4)
                AND (c.entry LIKE {d.Concat("'security/'", "t.tablename", "'#%'")} OR c.entry LIKE {d.Concat("'data/'", d.IsNull("m.s", "t.tablename"), "'/%'")})
            WHERE t.runid = @run
            GROUP BY t.tablename, t.exported
            ORDER BY t.tablename
            """, run.RunId))
        {
            cmd.AddParameter("@sources", DbType.String, sources, -1);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                tables.Add(new JsonObject
                {
                    ["table"] = rd.GetString(0), ["exported"] = rd.GetInt64(1), ["imported"] = Convert.ToInt64(rd.GetValue(2)),
                    ["failed"] = Convert.ToInt64(rd.GetValue(3)),
                });
        }

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

        // Spec §8.7: every archive candidate of a dry run, whatever the cap on the listed issues.
        var candidates = new JsonArray();
        await using (var cmd = Command(conn, $"""
            SELECT tablename, message FROM meta.migration_issue
            WHERE runid = @run AND code = '{Mapping.DryRunPreview.CandidateCode}' AND tablename IS NOT NULL ORDER BY tablename
            """, run.RunId))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                candidates.Add(new JsonObject
                {
                    ["table"] = rd.GetString(0),
                    ["rows"] = !rd.IsDBNull(1) && RowCountMessage().Match(rd.GetString(1)) is { Success: true } m
                        ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                });

        var issues = await ledger.IssuesAsync(MaxIssues + 1, ct);
        var phases = await ledger.PhasesAsync(ct);
        var deactivated = await ledger.DeactivatedAsync(ct);
        return new JsonObject
        {
            ["runId"] = run.RunId.ToString(),
            ["state"] = run.State,
            // Table mapping spec §8.6: a dry run's report lists what the real run would do; it imported nothing.
            ["dryRun"] = run.IsDryRun,
            ["message"] = run.Message,
            // Whether any issue is an error, whatever the cap on the listed issues.
            ["hasErrors"] = await ledger.HasErrorsAsync(ct),
            ["tables"] = tables,
            ["issues"] = new JsonArray([.. issues.Take(MaxIssues).Select(i => (JsonNode)new JsonObject
            {
                ["phase"] = i.Phase, ["severity"] = i.Severity, ["code"] = i.Code, ["table"] = i.Table, ["recordId"] = i.RecordId?.ToString(),
                ["column"] = i.Column, ["message"] = i.Message,
            })]),
            ["issuesTruncated"] = issues.Count > MaxIssues,
            ["lookupsNulled"] = lookups,
            ["users"] = users,
            ["mappedTables"] = mappedTables,
            // Table mapping spec §8.7: the source tables a dry run found that can be kept as archive tables, with their rows.
            ["archiveCandidates"] = candidates,
            // Spec §8.7: the maps the run used (the request's, else the package's), so that a dry run's choices are saved with them.
            ["maps"] = new JsonArray([.. Mapping.MigrationMaps.UsedTexts(MigrationImportJob.Options(run.Options)).Select(t => (JsonNode)t)]),
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

    /// <summary>
    /// The section per mapped table (table mapping spec §8.5), from the census of phase 1: <c>{source, target, rows, notes: [{column,
    /// code, count}]}</c>, one per <c>mapped-table</c> issue, with the counted issues of its target table.
    /// </summary>
    /// <remarks>
    /// Parsed from the issue messages that <see cref="Mapping.MapCensus"/> writes, whose formats are fixed by the table mapping's issue
    /// codes: <c>&lt;source&gt; → &lt;target&gt;: &lt;n&gt; rows</c> and <c>&lt;n&gt; row(s)</c>. A change to either breaks this section.
    /// </remarks>
    private static async Task<JsonArray> MappedTablesAsync(DbConnection conn, Guid runId, CancellationToken ct)
    {
        var census = new List<(string? Table, string? Column, string Code, string Message)>();
        await using (var cmd = Command(conn, $"""
            SELECT tablename, columnname, code, message FROM meta.migration_issue
            WHERE runid = @run AND phase = {VerifyPackagePhase.PhaseOrder} AND code <> 'map-check' ORDER BY issueid
            """, runId))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                census.Add((rd.IsDBNull(0) ? null : rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2), rd.GetString(3)));

        var mapped = new JsonArray();
        foreach (var (target, _, _, message) in census.Where(c => c.Code == "mapped-table"))
        {
            if (MappedTableMessage().Match(message) is not { Success: true } m) continue;
            mapped.Add(new JsonObject
            {
                ["source"] = m.Groups[1].Value,
                ["target"] = target ?? m.Groups[2].Value,
                ["rows"] = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                ["notes"] = new JsonArray([.. census
                    .Where(c => c.Code != "mapped-table" && c.Table == target)
                    .Select(c => (Note: c, Count: RowCountMessage().Match(c.Message)))
                    .Where(x => x.Count.Success)
                    .Select(x => (JsonNode)new JsonObject
                    {
                        ["column"] = x.Note.Column, ["code"] = x.Note.Code, ["count"] = long.Parse(x.Count.Groups[1].Value, CultureInfo.InvariantCulture),
                    })]),
            });
        }
        return mapped;
    }

    /// <summary>The message of a <c>mapped-table</c> issue: <c>&lt;source&gt; → &lt;target&gt;: &lt;n&gt; rows</c>.</summary>
    [GeneratedRegex(@"^(\S+) → (\S+): (\d+) rows$")]
    private static partial Regex MappedTableMessage();

    /// <summary>The message of a counted census issue: <c>&lt;n&gt; row(s)</c>.</summary>
    [GeneratedRegex(@"^(\d+) row\(s\)$")]
    private static partial Regex RowCountMessage();

    private static DbCommand Command(DbConnection conn, string sql, Guid runId)
    {
        var cmd = conn.Command(sql);
        cmd.AddParameter("@run", DbType.Guid, runId);
        return cmd;
    }
}
