using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Admin.Migration.Import;

/// <summary>An entry of the report: a failed row, a nulled lookup, a dropped column, a user created by action (plan M4a).</summary>
public sealed record MigrationIssue(int Phase, string Severity, string Code, string? Table, Guid? RecordId, string? Column, string Message)
{
    public const string Error = "error", Warning = "warning", Info = "info";
}

/// <summary>A lookup left out of a record until the deferred-lookups phase (its target was not loaded yet).</summary>
public sealed record DeferredLookup(string Table, Guid RecordId, string Column, string RefTable, Guid RefId, DateTime? ModifiedOn);

/// <summary>When a phase of the run started and ended.</summary>
public sealed record PhaseTiming(int Phase, string Name, DateTime StartedOn, DateTime? EndedOn);

/// <summary>A plug-in step, webhook step or process an imported solution brought in active, left off by the schema phase (spec §7.9).</summary>
public sealed record DeactivatedItem(string Table, Guid Id, string Name, int SourceState, int SourceStatus, DateTime? ActivatedOn = null);

/// <summary>
/// The import ledger of one run (migration spec §7.3–§7.8, plan M4a task 1): the id map of singletons and users mapped onto existing
/// rows, the deferred lookups, the issues of the report and the phase timings.
/// </summary>
public sealed class MigrationLedger(IDbConnectionFactory factory, Guid runId)
{
    public Guid RunId => runId;

    /// <summary>Maps <paramref name="source"/> onto <paramref name="target"/>; the same pair again is fine, another target throws.</summary>
    public async Task MapAsync(Guid source, Guid target, DataSession? s, CancellationToken ct)
    {
        // Plan 2.7b, Task 10: the IF NOT EXISTS batch as portable statements, unlocked as before. The run's one job writes its map,
        // so a race is theoretical; if one came, a writer of the same pair while another's insert is uncommitted would fail on the
        // primary key on both engines (its NOT EXISTS does not wait for the uncommitted row), not map twice.
        const string sql = """
            INSERT INTO meta.migration_idmap (runid, sourceid, targetid) SELECT @run, @source, @target
            WHERE NOT EXISTS (SELECT 1 FROM meta.migration_idmap WHERE runid = @run AND sourceid = @source);
            SELECT targetid FROM meta.migration_idmap WHERE runid = @run AND sourceid = @source;
            """;
        var mapped = await WithCommandAsync(s, sql, ct, cmd => cmd.ExecuteScalarAsync(ct), ("@run", runId), ("@source", source), ("@target", target));
        if (mapped is Guid existing && existing != target)
            throw new InvalidOperationException($"The source id {source} is already mapped onto {existing}, not {target}.");
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> LoadMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<Guid, Guid>();
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, "SELECT sourceid, targetid FROM meta.migration_idmap WHERE runid = @run", ("@run", runId));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) map[rd.GetGuid(0)] = rd.GetGuid(1);
        return map;
    }

    /// <summary>Records a deferred lookup in <paramref name="s"/>'s transaction (with its record); the same one again is ignored.</summary>
    public async Task DeferAsync(DataSession s, string table, Guid id, string column, string refTable, Guid refId, DateTime? modifiedOn,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = Command(s.Connection, s.Transaction, """
            INSERT INTO meta.migration_deferred (runid, tablename, recordid, columnname, reftable, refid, modifiedon)
            SELECT @run, @table, @id, @column, @reftable, @refid, @modifiedon
            WHERE NOT EXISTS (SELECT 1 FROM meta.migration_deferred WHERE runid = @run AND tablename = @table AND recordid = @id AND columnname = @column);
            """, ("@run", runId), ("@table", table), ("@id", id), ("@column", column), ("@reftable", refTable), ("@refid", refId),
            ("@modifiedon", modifiedOn is { } m ? m : DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The deferred lookups of the run, in the order they were recorded.</summary>
    public async IAsyncEnumerable<DeferredLookup> DeferredAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, """
            SELECT tablename, recordid, columnname, reftable, refid, modifiedon FROM meta.migration_deferred WHERE runid = @run ORDER BY seq
            """, ("@run", runId));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            yield return new DeferredLookup(rd.GetString(0), rd.GetGuid(1), rd.GetString(2), rd.GetString(3), rd.GetGuid(4),
                rd.IsDBNull(5) ? null : DateTime.SpecifyKind(rd.GetDateTime(5), DateTimeKind.Utc));
    }

    /// <summary>Records an issue on a connection of its own, so that it stays when the chunk it is about rolls back.</summary>
    public Task IssueAsync(MigrationIssue issue, CancellationToken ct) => IssueAsync(issue, null, ct);

    /// <summary>
    /// Records an issue in <paramref name="s"/>'s transaction (it goes with the rows it is about), or on its own connection. The same
    /// issue (phase, code, table, record, column and message) is recorded once per run, so a resumed phase does not list it twice.
    /// </summary>
    public async Task IssueAsync(MigrationIssue issue, DataSession? s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issue);
        await WithCommandAsync(s, """
            INSERT INTO meta.migration_issue (runid, phase, severity, code, tablename, recordid, columnname, message)
            SELECT @run, @phase, @severity, @code, @table, @record, @column, @message
            WHERE NOT EXISTS (SELECT 1 FROM meta.migration_issue
                WHERE runid = @run AND code = @code AND phase = @phase AND message = @message
                    AND (recordid = @record OR (recordid IS NULL AND @record IS NULL))
                    AND (tablename = @table OR (tablename IS NULL AND @table IS NULL))
                    AND (columnname = @column OR (columnname IS NULL AND @column IS NULL)))
            """, ct, cmd => cmd.ExecuteNonQueryAsync(ct), ("@run", runId), ("@phase", issue.Phase), ("@severity", issue.Severity), ("@code", issue.Code),
            ("@table", (object?)issue.Table ?? DBNull.Value), ("@record", issue.RecordId is { } r ? r : DBNull.Value),
            ("@column", (object?)issue.Column ?? DBNull.Value), ("@message", Truncate(issue.Message)));
    }

    public async Task<bool> HasErrorsAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null,
            factory.Dialect.Limit("SELECT 1 FROM meta.migration_issue WHERE runid = @run AND severity = 'error'", "1"), ("@run", runId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>The first <paramref name="top"/> issues, in the order they were recorded.</summary>
    public async Task<List<MigrationIssue>> IssuesAsync(int top, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, factory.Dialect.Limit("""
            SELECT phase, severity, code, tablename, recordid, columnname, message
            FROM meta.migration_issue WHERE runid = @run ORDER BY issueid
            """, $"({Math.Max(1, top)})"), ("@run", runId));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var issues = new List<MigrationIssue>();
        while (await rd.ReadAsync(ct))
            issues.Add(new MigrationIssue(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetGuid(4), rd.IsDBNull(5) ? null : rd.GetString(5), rd.GetString(6)));
        return issues;
    }

    /// <summary>Records the package's tables and their exported counts (once; a resume records nothing new).</summary>
    public Task TablesAsync(IEnumerable<(string Table, long Exported)> tables, CancellationToken ct) => ExecuteAsync($"""
        INSERT INTO meta.migration_table (runid, tablename, exported)
        SELECT @run, j.t, j.n FROM {factory.Dialect.JsonRecords("@tables", [new JsonColumn("t", SqlColumnKind.String, 128, "$.t"), new JsonColumn("n", SqlColumnKind.Int64, Path: "$.n")])} j
        WHERE NOT EXISTS (SELECT 1 FROM meta.migration_table x WHERE x.runid = @run AND x.tablename = j.t)
        """, ct, ("@run", runId),
        ("@tables", System.Text.Json.JsonSerializer.Serialize(tables.Select(t => new { t = t.Table, n = t.Exported }))));

    /// <remarks>
    /// Plan 2.7b, Task 10: the <c>MERGE</c> (without <c>HOLDLOCK</c>) became an update and an insert of the missing row, portable
    /// and as unlocked as before. The phases of a run follow each other in its one job, so a race is theoretical: if a second
    /// writer of a phase came while the first one's insert is uncommitted, on PostgreSQL it would fail with a unique violation
    /// (its update and its NOT EXISTS do not wait for the uncommitted row), while on SQL Server its update waits and then updates
    /// the row.
    /// </remarks>
    public Task PhaseStartedAsync(int phase, string name, CancellationToken ct) => ExecuteAsync($"""
        UPDATE meta.migration_phase SET startedon = {factory.Dialect.UtcNow}, endedon = NULL, name = @name WHERE runid = @run AND phase = @phase;
        INSERT INTO meta.migration_phase (runid, phase, name, startedon) SELECT @run, @phase, @name, {factory.Dialect.UtcNow}
        WHERE NOT EXISTS (SELECT 1 FROM meta.migration_phase WHERE runid = @run AND phase = @phase);
        """, ct, ("@run", runId), ("@phase", phase), ("@name", name));

    public Task PhaseEndedAsync(int phase, CancellationToken ct) =>
        ExecuteAsync($"UPDATE meta.migration_phase SET endedon = {factory.Dialect.UtcNow} WHERE runid = @run AND phase = @phase", ct,
            ("@run", runId), ("@phase", phase));

    public async Task<List<PhaseTiming>> PhasesAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, "SELECT phase, name, startedon, endedon FROM meta.migration_phase WHERE runid = @run ORDER BY phase",
            ("@run", runId));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var phases = new List<PhaseTiming>();
        while (await rd.ReadAsync(ct))
            phases.Add(new PhaseTiming(rd.GetInt32(0), rd.GetString(1), DateTime.SpecifyKind(rd.GetDateTime(2), DateTimeKind.Utc),
                rd.IsDBNull(3) ? null : DateTime.SpecifyKind(rd.GetDateTime(3), DateTimeKind.Utc)));
        return phases;
    }

    /// <summary>Records, in <paramref name="s"/>'s transaction, an item the schema phase switched off, with its state as imported (once).</summary>
    public Task RecordDeactivatedAsync(DataSession s, DeactivatedItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        return WithCommandAsync(s, """
            INSERT INTO meta.migration_deactivated (runid, tablename, id, name, sourcestate, sourcestatus)
            SELECT @run, @table, @id, @name, @state, @status
            WHERE NOT EXISTS (SELECT 1 FROM meta.migration_deactivated WHERE runid = @run AND tablename = @table AND id = @id)
            """, ct, cmd => cmd.ExecuteNonQueryAsync(ct), ("@run", runId), ("@table", item.Table), ("@id", item.Id),
            ("@name", item.Name.Length <= 400 ? item.Name : item.Name[..400]), ("@state", item.SourceState), ("@status", item.SourceStatus));
    }

    /// <summary>The items the schema phase switched off (what <c>xrmd migrate activate</c> turns back on), by table and name.</summary>
    public async Task<List<DeactivatedItem>> DeactivatedAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null,
            "SELECT tablename, id, name, sourcestate, sourcestatus, activatedon FROM meta.migration_deactivated WHERE runid = @run ORDER BY tablename, name, id", ("@run", runId));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var items = new List<DeactivatedItem>();
        while (await rd.ReadAsync(ct))
            items.Add(new DeactivatedItem(rd.GetString(0), rd.GetGuid(1), rd.GetString(2), rd.GetInt32(3), rd.GetInt32(4), rd.IsDBNull(5) ? null : rd.GetDateTime(5)));
        return items;
    }

    /// <summary>Records, in <paramref name="s"/>'s transaction, that <c>xrmd migrate activate</c> switched an item back on.</summary>
    public Task MarkActivatedAsync(DataSession s, string table, Guid id, CancellationToken ct) => WithCommandAsync(s,
        $"UPDATE meta.migration_deactivated SET activatedon = {s.Dialect.UtcNow} WHERE runid = @run AND tablename = @table AND id = @id",
        ct, cmd => cmd.ExecuteNonQueryAsync(ct), ("@run", runId), ("@table", table), ("@id", id));

    private static string Truncate(string message) => message.Length <= 2000 ? message : message[..1997] + "...";

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<T> WithCommandAsync<T>(DataSession? s, string sql, CancellationToken ct, Func<DbCommand, Task<T>> run,
        params (string Name, object Value)[] parameters)
    {
        if (s is not null)
        {
            await using var cmd = Command(s.Connection, s.Transaction, sql, parameters);
            return await run(cmd);
        }
        await using var conn = await factory.OpenAsync(ct);
        await using var own = Command(conn, null, sql, parameters);
        return await run(own);
    }

    private static DbCommand Command(DbConnection conn, DbTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = conn.Command(sql, tx);
        foreach (var (name, value) in parameters)
        {
            var (type, size) = value switch
            {
                Guid => (DbType.Guid, 0),
                string => (DbType.String, -1),
                int => (DbType.Int32, 0),
                DateTime => (DbType.DateTime2, 0),
                // Typed nulls: the column of each parameter that can be null.
                DBNull when name is "@modifiedon" => (DbType.DateTime2, 0),
                DBNull when name is "@record" => (DbType.Guid, 0),
                DBNull => (DbType.String, -1),
                _ => throw new ArgumentException($"Unsupported parameter type {value?.GetType()}.", nameof(parameters)),
            };
            cmd.AddParameter(name, type, value, size);
        }
        return cmd;
    }
}
