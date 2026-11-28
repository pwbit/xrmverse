using System.Data;
using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Admin.Migration;

/// <summary>The states of an uploaded package (<c>meta.migration_package.state</c>).</summary>
public static class MigrationPackageStates
{
    public const string Uploading = "uploading";
    public const string Complete = "complete";
}

/// <summary>
/// The states of a migration run (<c>meta.migration_run.state</c>, migration spec §7.7). The organization's migration lock is on in
/// every state but <see cref="Finalized"/>, except for a dry run, which never takes it (table mapping spec §8.6).
/// </summary>
public static class MigrationRunStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Finalized = "finalized";

    /// <summary>True for the states in which the run's job has ended (it can be resumed or finalized, or it finalizes itself).</summary>
    public static bool IsTerminal(string state) => state is Completed or CompletedWithErrors or Failed or Cancelled;

    /// <summary>True for the states that hold the organization's migration lock: every state but <see cref="Finalized"/>.</summary>
    public static bool HoldsLock(string state) => state != Finalized;
}

/// <summary>A row of <c>meta.migration_package</c>.</summary>
public sealed record MigrationPackageRow(Guid PackageId, string State, long? Bytes, string? Sha256, Guid CreatedBy, DateTime CreatedOn);

/// <summary>A row of <c>meta.migration_run</c>. <see cref="Phase"/> is the number of phases completed.</summary>
public sealed record MigrationRunRow(Guid RunId, Guid? PackageId, string? ServerPath, string State, int Phase, string Options,
    Guid? AsyncOperationId, string? Message, Guid CreatedBy, DateTime CreatedOn, DateTime? StartedOn, DateTime? EndedOn, DateTime? FinalizedOn)
{
    /// <summary>The run option that makes a run a dry run (migration table mapping spec §8.6).</summary>
    public const string DryRunOption = "dryRun";

    /// <summary>True for a dry run: it reads the package and the organization and writes only its ledger (spec §8.6).</summary>
    public bool IsDryRun => MigrationImportJob.Options(Options).TryGetValue(DryRunOption, out var value) && value == "true";

    /// <summary>
    /// True for a dry run that has ended: it holds no lock and blocks no other run (its row is closed when the next run is created).
    /// </summary>
    public bool IsEndedDryRun => IsDryRun && MigrationRunStates.IsTerminal(State);
}

/// <summary>A run cannot be created: another run of the organization is not finalized (one open run, migration plan M3 R8).</summary>
public sealed class MigrationRunConflictException(string message) : Exception(message);

/// <summary>
/// The ledger of the import side of a migration from Dataverse (migration spec §7): uploaded packages, runs and the package entries a
/// run has committed. Plain SQL on the organization database (<c>meta.migration_*</c>, migration 0015).
/// </summary>
public sealed class MigrationRunStore(IDbConnectionFactory factory)
{

    private const string RunColumns =
        "runid, packageid, serverpath, state, phase, options, asyncoperationid, message, createdby, createdon, startedon, endedon, finalizedon";

    public async Task<Guid> CreatePackageAsync(Guid createdBy, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO meta.migration_package (packageid, state, createdby, createdon) VALUES (@id, @state, @by, {factory.Dialect.UtcNow})",
            ct, ("@id", id), ("@state", MigrationPackageStates.Uploading), ("@by", createdBy));
        return id;
    }

    public async Task<MigrationPackageRow?> GetPackageAsync(Guid packageId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null,
            "SELECT packageid, state, bytes, sha256, createdby, createdon FROM meta.migration_package WHERE packageid = @id", ("@id", packageId));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new MigrationPackageRow(rd.GetGuid(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetInt64(2),
            rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetGuid(4), DateTime.SpecifyKind(rd.GetDateTime(5), DateTimeKind.Utc));
    }

    public Task CompletePackageAsync(Guid packageId, long bytes, string sha256, CancellationToken ct) =>
        ExecuteAsync("UPDATE meta.migration_package SET state = @state, bytes = @bytes, sha256 = @sha WHERE packageid = @id",
            ct, ("@id", packageId), ("@state", MigrationPackageStates.Complete), ("@bytes", bytes), ("@sha", sha256));

    public Task DeletePackageAsync(Guid packageId, CancellationToken ct) =>
        ExecuteAsync("DELETE FROM meta.migration_package WHERE packageid = @id", ct, ("@id", packageId));

    /// <summary>
    /// Creates a run. A dry run that has ended is closed first (set to <see cref="MigrationRunStates.Finalized"/>, its ended state
    /// kept in its message: <c>Dry run ended &lt;state&gt;; &lt;message&gt;</c>): the database keeps at most one run that is not
    /// finalized, and an ended dry run blocks nothing.
    /// </summary>
    /// <exception cref="MigrationRunConflictException">Another run is not finalized.</exception>
    public async Task CreateRunAsync(MigrationRunRow run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        foreach (var ended in (await QueryRunsAsync("WHERE state <> @finalized", ct, ("@finalized", MigrationRunStates.Finalized))).Where(r => r.IsEndedDryRun))
            await SetStateAsync(ended.RunId, MigrationRunStates.Finalized, EndedDryRunMessage(ended), ct, ended.State);
        try
        {
            await ExecuteAsync($"INSERT INTO meta.migration_run ({RunColumns}) VALUES " +
                               "(@id, @package, @path, @state, @phase, @options, @job, @message, @by, @on, @started, @ended, @finalized)", ct,
                ("@id", run.RunId), ("@package", Of(DbType.Guid, run.PackageId)), ("@path", Of(DbType.String, run.ServerPath)),
                ("@state", run.State), ("@phase", run.Phase), ("@options", run.Options), ("@job", Of(DbType.Guid, run.AsyncOperationId)),
                ("@message", Of(DbType.String, run.Message)), ("@by", run.CreatedBy), ("@on", run.CreatedOn),
                ("@started", Of(DbType.DateTime2, run.StartedOn)), ("@ended", Of(DbType.DateTime2, run.EndedOn)),
                ("@finalized", Of(DbType.DateTime2, run.FinalizedOn)));
        }
        catch (DbException ex) when (SqlDialects.Classify(ex) == DbErrorKind.UniqueViolation)
        {
            var open = await GetOpenRunAsync(ct);
            throw new MigrationRunConflictException($"Another migration run is open: {open?.RunId}.");
        }
    }

    /// <summary>The message of a closed dry run: the state it ended in, then its own message.</summary>
    public static string EndedDryRunMessage(MigrationRunRow run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var text = $"Dry run ended {run.State}" + (run.Message is { Length: > 0 } m ? $"; {m}" : ".");
        return text.Length <= 2000 ? text : text[..1997] + "...";
    }

    public async Task<MigrationRunRow?> GetRunAsync(Guid runId, CancellationToken ct) =>
        (await QueryRunsAsync("WHERE runid = @id", ct, ("@id", runId))).SingleOrDefault();

    /// <summary>The latest <paramref name="top"/> runs, newest first.</summary>
    public Task<List<MigrationRunRow>> ListRunsAsync(int top, CancellationToken ct) =>
        QueryRunsAsync(d => "ORDER BY createdon DESC " + d.Page(0, Math.Clamp(top, 1, 1000)), ct);

    /// <summary>
    /// The run that is open, if any: not finalized, and not a dry run that has ended (spec §8.6: it needs no finalize). A real run
    /// that is open holds the migration lock; a dry run never does.
    /// </summary>
    public async Task<MigrationRunRow?> GetOpenRunAsync(CancellationToken ct) =>
        (await QueryRunsAsync("WHERE state <> @finalized", ct, ("@finalized", MigrationRunStates.Finalized))).SingleOrDefault(r => !r.IsEndedDryRun);

    /// <summary>
    /// Sets the run's state and message; <see cref="MigrationRunStates.Running"/> stamps <c>startedon</c> (once), a terminal state
    /// <c>endedon</c>, <see cref="MigrationRunStates.Finalized"/> <c>finalizedon</c>; <see cref="MigrationRunStates.Queued"/> (a resume)
    /// clears <c>endedon</c>. With <paramref name="from"/>, only a run in one of those states changes (compare and set): a late write
    /// of a job never overwrites what an administrator did meanwhile.
    /// </summary>
    /// <returns>True when the run changed.</returns>
    public async Task<bool> SetStateAsync(Guid runId, string state, string? message, CancellationToken ct, params string[] from)
    {
        var (sql, parameters) = StateUpdate(runId, state, message, from);
        return await ExecuteAsync(sql, ct, parameters) > 0;
    }

    /// <summary>
    /// Finalizes the run if it is in one of the states <paramref name="from"/> (compare and set), in one transaction with the deletion
    /// of the row of the uploaded package <paramref name="deletePackageId"/> (when not null): a reader sees the run finalized only
    /// with its package gone. <paramref name="beforeCommit"/> runs once the run has changed, before the commit (it deletes the
    /// package's files and lifts the in-process lock, so that both are done by the time anyone reads the run finalized); if it
    /// throws, the transaction rolls back and the run and the package's row stay, so the finalize can be retried.
    /// </summary>
    /// <returns>True when the run was finalized; false, with nothing done, when it was in another state.</returns>
    public async Task<bool> FinalizeRunAsync(Guid runId, Guid? deletePackageId, Func<Task> beforeCommit, CancellationToken ct, params string[] from)
    {
        ArgumentNullException.ThrowIfNull(beforeCommit);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (sql, parameters) = StateUpdate(runId, MigrationRunStates.Finalized, null, from);
        await using (var cmd = Command(conn, tx, sql, parameters))
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;
        if (deletePackageId is { } packageId)
            await using (var cmd = Command(conn, tx, "DELETE FROM meta.migration_package WHERE packageid = @id", ("@id", packageId)))
                await cmd.ExecuteNonQueryAsync(ct);
        await beforeCommit();
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>The statement of <see cref="SetStateAsync"/> and its parameters.</summary>
    private (string Sql, (string Name, object? Value)[] Parameters) StateUpdate(Guid runId, string state, string? message, string[] from)
    {
        ArgumentNullException.ThrowIfNull(from);
        var parameters = new List<(string Name, object? Value)>
        {
            ("@id", runId), ("@state", state), ("@message", Of(DbType.String, message)), ("@terminal", MigrationRunStates.IsTerminal(state)),
        };
        for (var i = 0; i < from.Length; i++) parameters.Add(($"@from{i}", from[i]));
        var guard = from.Length == 0 ? "" : $" AND state IN ({string.Join(", ", from.Select((_, i) => $"@from{i}"))})";
        var d = factory.Dialect;
        return ($"""
            UPDATE meta.migration_run SET state = @state, message = @message,
                startedon = CASE WHEN @state = 'running' AND startedon IS NULL THEN {d.UtcNow} ELSE startedon END,
                endedon = CASE WHEN @terminal = {d.BooleanLiteral(true)} THEN {d.UtcNow} WHEN @state = 'queued' THEN NULL ELSE endedon END,
                finalizedon = CASE WHEN @state = 'finalized' THEN {d.UtcNow} ELSE finalizedon END
            WHERE runid = @id{guard}
            """, [.. parameters]);
    }

    /// <summary>Deletes a run that never started (its start was refused after the run was created).</summary>
    public Task DeleteRunAsync(Guid runId, CancellationToken ct) =>
        ExecuteAsync("DELETE FROM meta.migration_chunk WHERE runid = @id; DELETE FROM meta.migration_run WHERE runid = @id", ct, ("@id", runId));

    /// <summary>The names of the system jobs other than migration ones that are in progress (the worker took them).</summary>
    public async Task<List<string>> JobsInProgressAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null,
            factory.Dialect.Limit("SELECT name FROM data.asyncoperation WHERE statecode = 2 AND (operationtype IS NULL OR operationtype <> @migration) ORDER BY startedon", "10"),
            ("@migration", 99010));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await rd.ReadAsync(ct)) names.Add(rd.IsDBNull(0) ? "(unnamed)" : rd.GetString(0));
        return names;
    }

    public Task SetPhaseAsync(Guid runId, int completedPhases, CancellationToken ct) =>
        ExecuteAsync("UPDATE meta.migration_run SET phase = @phase WHERE runid = @id", ct, ("@id", runId), ("@phase", completedPhases));

    public Task SetJobAsync(Guid runId, Guid asyncOperationId, CancellationToken ct) =>
        ExecuteAsync("UPDATE meta.migration_run SET asyncoperationid = @job WHERE runid = @id", ct, ("@id", runId), ("@job", asyncOperationId));

    /// <summary>Cancels the system job <paramref name="asyncOperationId"/> while it waits in the queue; false when the worker took it.</summary>
    public async Task<bool> CancelQueuedJobAsync(Guid asyncOperationId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var d = factory.Dialect;
        await using var cmd = Command(conn, null, $"""
            UPDATE data.asyncoperation SET statecode = 3, statuscode = 32, completedon = {d.UtcNow}, modifiedon = {d.UtcNow},
                message = {d.TextLiteral("Cancelled by an administrator.")}
            WHERE asyncoperationid = @job AND statecode = 0
            """, ("@job", asyncOperationId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Records <paramref name="entry"/> as committed by the run, in <paramref name="session"/>'s transaction (with its rows).</summary>
    public async Task MarkChunkAsync(Guid runId, int phase, string entry, int rows, int failedRows, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var cmd = Command(session.Connection, session.Transaction,
            $"INSERT INTO meta.migration_chunk (runid, entry, phase, rows, failedrows, committedon) VALUES (@id, @entry, @phase, @rows, @failed, {session.Dialect.UtcNow})",
            ("@id", runId), ("@entry", entry), ("@phase", phase), ("@rows", rows), ("@failed", failedRows));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsChunkDoneAsync(Guid runId, string entry, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, "SELECT 1 FROM meta.migration_chunk WHERE runid = @id AND entry = @entry", ("@id", runId), ("@entry", entry));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private Task<List<MigrationRunRow>> QueryRunsAsync(string tail, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        QueryRunsAsync(_ => tail, ct, parameters);

    /// <summary>The runs the statement ending with <paramref name="tail"/> (written in the connection's dialect) reads.</summary>
    private async Task<List<MigrationRunRow>> QueryRunsAsync(Func<ISqlDialect, string> tail, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, $"SELECT {RunColumns} FROM meta.migration_run {tail(SqlDialects.For(conn))}", parameters);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<MigrationRunRow>();
        while (await rd.ReadAsync(ct))
            rows.Add(new MigrationRunRow(rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.GetString(3), rd.GetInt32(4), rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetGuid(6), rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.GetGuid(8), Utc(rd.GetDateTime(9)), UtcOrNull(rd, 10), UtcOrNull(rd, 11), UtcOrNull(rd, 12)));
        return rows;
    }

    /// <summary>A parameter value with its SQL type, so that a null keeps the column's type.</summary>
    private sealed record Typed(DbType Type, object? Value);

    private static Typed Of(DbType type, object? value) => new(type, value);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? UtcOrNull(DbDataReader rd, int i) => rd.IsDBNull(i) ? null : Utc(rd.GetDateTime(i));

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = Command(conn, null, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand Command(DbConnection conn, DbTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.Command(sql, tx);
        foreach (var (name, value) in parameters)
        {
            var (type, size, typedValue) = value switch
            {
                Guid g => (DbType.Guid, 0, (object?)g),
                string s => (DbType.String, -1, s),
                int i => (DbType.Int32, 0, i),
                long l => (DbType.Int64, 0, l),
                bool b => (DbType.Boolean, 0, b),
                DateTime d => (DbType.DateTime2, 0, d),
                Typed t => (t.Type, t.Type == DbType.String ? -1 : 0, t.Value),
                _ => throw new ArgumentException($"Unsupported parameter type {value?.GetType()}.", nameof(parameters)),
            };
            cmd.AddParameter(name, type, typedValue, size);
        }
        return cmd;
    }
}
