using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Workflows.Background;

/// <summary>
/// The waits of suspended background workflow jobs (plan 2.14, ruling W10): one <c>meta.workflow_wait</c> row per job whose wait
/// condition reads a record. A core write of that record wakes the job (<see cref="WakeAsync"/>, in the write's transaction); the
/// worker's claim skips a job whose <c>wake_at</c> is in the future. Process-wide (a singleton).
/// </summary>
/// <remarks>
/// <see cref="HasAny"/> keeps every write free of SQL against the table while no job waits: it is true from the first registration,
/// becomes unknown (true) when a row is removed, and false only once a count read from the database finds none and no registration
/// happened meanwhile. It starts unknown, so a host started on a database holding waits finds them.
/// </remarks>
public sealed class WorkflowWaits
{
    /// <summary>The <c>wake_at</c> of a wait without a timeout: only a write of its record wakes it.</summary>
    public static readonly DateTime Never = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private const string Table = "meta.workflow_wait";

    // null: unknown (a count is read on next use); true: some may exist; false: none.
    private bool? _any;
    private long _version;
    private readonly object _gate = new();

    /// <summary>True when a job may be waiting on a record (unknown counts as true).</summary>
    public bool HasAny
    {
        get
        {
            lock (_gate) return _any ?? true;
        }
    }

    /// <summary>
    /// Records that job <paramref name="job"/> waits on <paramref name="record"/> until <paramref name="timeout"/> (null: until the
    /// record is written), replacing any earlier wait of the job.
    /// </summary>
    /// <remarks>
    /// <see cref="HasAny"/> is marked before the INSERT (a write from then on looks for waits) and again once the row is committed
    /// (on an autocommit session after the statements, or the commit of the method's own transaction on PostgreSQL; at the commit of
    /// <paramref name="s"/>'s transaction otherwise): a count that ran meanwhile under row versioning, without seeing the row, is
    /// then never kept.
    /// </remarks>
    public Task RegisterAsync(DataSession s, Guid job, EntityReference record, DateTimeOffset? timeout, CancellationToken ct) =>
        RegisterAsync(s, job, record, timeout, null, ct);

    /// <inheritdoc cref="RegisterAsync(DataSession, Guid, EntityReference, DateTimeOffset?, CancellationToken)"/>
    /// <param name="seenVersion">The <c>versionnumber</c> of <paramref name="record"/> the run is known to have seen (null: unknown).</param>
    internal async Task RegisterAsync(DataSession s, Guid job, EntityReference record, DateTimeOffset? timeout, long? seenVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(record);
        Mark();
        // Plan 2.7b, Task 7 fix round 1: on PostgreSQL the replacement holds the job's key (the key lock WakeRunningAsync takes)
        // from its DELETE to the end of its INSERT, in a transaction of its own on an autocommit session; without it either one
        // misses the other's uncommitted row and fails on the key. SQL Server is unchanged (the same batch, no transaction): its
        // DELETE and UPDATE wait for an uncommitted row of the key, as UpsertInterleavingTests shows there. Final review I2: the
        // dialect says whether the key lock needs a transaction to hold (ISqlDialect.KeyLocksNeedTransaction).
        var own = s.Transaction is null && s.Dialect.KeyLocksNeedTransaction
            ? await s.Connection.BeginTransactionAsync(ct)
            : null;
        try
        {
            var session = own is null ? s : new DataSession(s.Connection, own);
            await session.KeyLockAsync(Table, job, ct);
            await using (var cmd = session.Command($"""
                DELETE FROM {Table} WHERE asyncoperationid = @job;
                INSERT INTO {Table} (asyncoperationid, entity, objectid, wake_at, seen_version) VALUES (@job, @entity, @id, @wake, @seen);
                """))
            {
                cmd.AddParameter("@seen", DbType.Int64, (object?)seenVersion ?? DBNull.Value);
                cmd.AddParameter("@job", DbType.Guid, job);
                cmd.AddParameter("@entity", DbType.String, record.LogicalName, 128);
                cmd.AddParameter("@id", DbType.Guid, record.Id);
                cmd.AddParameter("@wake", DbType.DateTime2, timeout?.UtcDateTime ?? Never);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (own is not null) await own.CommitAsync(ct);
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
        if (s.Transaction is null) Mark();
        else s.OnCommitted(Mark);
    }

    /// <summary>A wait may exist; a count begun before this is not kept.</summary>
    private void Mark()
    {
        lock (_gate)
        {
            _any = true;
            _version++;
        }
    }

    /// <summary>
    /// Closes the window between a run's read of its wait condition and <see cref="RegisterAsync"/> (W10): a write of the record in
    /// that window ran its <see cref="WakeAsync"/> before the wait row existed. Run on an autocommit session right after the
    /// registration. The record is read with <c>READCOMMITTEDLOCK</c> (a writer still holding it is waited for), and the job is
    /// woken (<c>wake_at</c> = now) when its <c>versionnumber</c> is after what the run is known to have seen: the row's
    /// <c>seen_version</c> (the version read before the run read its condition), or, when unknown (a first wait), at least
    /// <paramref name="token"/>, the <c>MIN_ACTIVE_ROWVERSION()</c> read before the run read anything (a write the run did not see,
    /// in flight then or later, has a version at least that; this may wake once spuriously, e.g. while a long transaction is open or
    /// when the run's own steps wrote the record). A write whose wake runs after the registration finds the row itself.
    /// </summary>
    /// <returns>False when the record no longer exists (the caller fails the job).</returns>
    /// <remarks>
    /// Every xrmverse table has the system column <c>versionnumber</c>; a table whose metadata lacked it would only be checked for
    /// existence.
    /// </remarks>
    internal async Task<bool> RecheckAsync(DataSession s, Guid job, EntityReference record, EntityMetadata? m, long token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(record);
        var d = s.Dialect;
        var pk = m is null ? SystemAttributes.PrimaryIdName(record.LogicalName) : SystemAttributes.PrimaryIdName(m);
        var versioned = m?.Attributes?.Any(a => a.LogicalName == TypeMapping.VersionNumber) ?? true;
        var version = versioned ? $"CAST({d.Quote(TypeMapping.VersionNumber)} AS bigint)" : "CAST(NULL AS bigint)";
        // Plan 2.7b, Task 7: the T-SQL batch (DECLARE, IF) became commands of their own, on the same autocommit session (each
        // statement of the batch committed on its own too); the decision between them is made here.
        long? seen;
        await using (var cmd = s.Command($"SELECT seen_version FROM {Table} WHERE asyncoperationid = @job"))
        {
            cmd.AddParameter("@job", DbType.Guid, job);
            seen = await cmd.ExecuteScalarAsync(ct) is long known ? known : null;
        }
        long? current;
        await using (var cmd = s.Command(
                         $"SELECT {version} FROM {s.DataTable(record.LogicalName)}{d.LockHint(RowLock.ReadCommittedLock)} " +
                         $"WHERE {d.Quote(pk)} = @id{d.LockClause(RowLock.ReadCommittedLock)}"))
        {
            cmd.AddParameter("@id", DbType.Guid, record.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return false;
            current = rd.IsDBNull(0) ? null : rd.GetInt64(0);
        }
        if (current is not { } written || !(seen is { } baseline ? written > baseline : written >= token)) return true;
        await using (var wake = s.Command($"UPDATE {Table} SET wake_at = @now WHERE asyncoperationid = @job AND wake_at > @now"))
        {
            wake.AddParameter("@job", DbType.Guid, job);
            wake.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
            await wake.ExecuteNonQueryAsync(ct);
        }
        return true;
    }

    /// <summary>
    /// A write of the record <paramref name="entity"/>/<paramref name="id"/> (null: of some rows of the table, unknown): the jobs waiting
    /// on it may run now (<c>wake_at</c> = now). Nothing is read or written while <see cref="HasAny"/> is false.
    /// </summary>
    /// <returns>The number of jobs woken.</returns>
    public async Task<int> WakeAsync(DataSession s, string entity, Guid? id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(entity);
        if (!await AnyAsync(s, ct)) return 0;
        await using var cmd = s.Command(
            $"UPDATE {Table} SET wake_at = @now WHERE entity = @entity{(id is null ? "" : " AND objectid = @id")} AND wake_at > @now");
        cmd.AddParameter("@entity", DbType.String, entity, 128);
        if (id is { } record) cmd.AddParameter("@id", DbType.Guid, record);
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The jobs among <paramref name="jobs"/> that are running (2/20) while the record or workflow their run reads goes: each gets a
    /// wait that is ready now (its row kept with <c>wake_at</c> = now, or one on <paramref name="entity"/>/<paramref name="id"/>
    /// added), so a suspension the worker records after this runs again at once and meets the delete (W4, W10). A registration of
    /// the run that comes after replaces the row, and its own checks then see the delete.
    /// </summary>
    internal async Task WakeRunningAsync(DataSession s, IEnumerable<Guid> jobs, string entity, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(jobs);
        foreach (var job in jobs)
        {
            Mark();
            // Plan 2.7b, ruling Q5: the update-else-insert batch became the key lock of the job's wait, its update, then its insert
            // when the update found no row (the key lock is PostgreSQL's; SQL Server runs the UPDATE and INSERT of the batch as they
            // were, its UPDATE waiting for an uncommitted row of the job: UpsertInterleavingTests).
            var now = DateTime.UtcNow;
            await s.KeyLockAsync(Table, job, ct);
            var updated = false;
            await using (var update = s.Command($"UPDATE {Table} SET wake_at = @now WHERE asyncoperationid = @job"))
            {
                update.AddParameter("@job", DbType.Guid, job);
                update.AddParameter("@now", DbType.DateTime2, now);
                updated = await update.ExecuteNonQueryAsync(ct) > 0;
            }
            if (!updated)
            {
                await using var insert = s.Command($"""
                    INSERT INTO {Table} (asyncoperationid, entity, objectid, wake_at)
                    SELECT @job, @entity, @id, @now
                    WHERE EXISTS (SELECT 1 FROM data.asyncoperation WHERE asyncoperationid = @job AND statecode = 2)
                    """);
                insert.AddParameter("@job", DbType.Guid, job);
                insert.AddParameter("@entity", DbType.String, entity, 128);
                insert.AddParameter("@id", DbType.Guid, id);
                insert.AddParameter("@now", DbType.DateTime2, now);
                await insert.ExecuteNonQueryAsync(ct);
            }
            if (s.Transaction is null) Mark();
            else s.OnCommitted(Mark);
        }
    }

    /// <summary>Removes the wait of job <paramref name="job"/> and returns its record; null when it had none.</summary>
    internal async Task<EntityReference?> TakeAsync(DataSession s, Guid job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!HasAny) return null;
        string[] returned = ["entity", "objectid"];
        await using var cmd = s.Command(
            $"DELETE FROM {Table}{s.Dialect.ReturningInline(returned, RowImage.Deleted)} WHERE asyncoperationid = @job{s.Dialect.ReturningTrailing(returned, RowImage.Deleted)}");
        cmd.AddParameter("@job", DbType.Guid, job);
        EntityReference? taken = null;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct))
                taken = new EntityReference(rd.GetString(0), rd.GetGuid(1));
        if (taken is not null) Forget();
        return taken;
    }

    /// <summary>
    /// The <c>versionnumber</c> of <paramref name="record"/>, read with <c>READCOMMITTEDLOCK</c> (a writer holding it is waited for, so
    /// a read of the record after this sees at least this version); null when the record or its version column does not exist.
    /// </summary>
    internal static async Task<long?> VersionOfAsync(DataSession s, EntityReference record, EntityMetadata? m, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(record);
        if (m is null || m.Attributes?.Any(a => a.LogicalName == TypeMapping.VersionNumber) != true) return null;
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT CAST({d.Quote(TypeMapping.VersionNumber)} AS bigint) FROM {s.DataTable(m.LogicalName)}{d.LockHint(RowLock.ReadCommittedLock)} " +
            $"WHERE {d.Quote(SystemAttributes.PrimaryIdName(m))} = @id{d.LockClause(RowLock.ReadCommittedLock)}");
        cmd.AddParameter("@id", DbType.Guid, record.Id);
        return await cmd.ExecuteScalarAsync(ct) is long version ? version : null;
    }

    /// <summary>Removes the wait of job <paramref name="job"/>, if any.</summary>
    public async Task RemoveAsync(DataSession s, Guid job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!HasAny) return;
        await using var cmd = s.Command($"DELETE FROM {Table} WHERE asyncoperationid = @job");
        cmd.AddParameter("@job", DbType.Guid, job);
        if (await cmd.ExecuteNonQueryAsync(ct) > 0) Forget();
    }

    /// <summary>
    /// Removes the waits on the record <paramref name="entity"/>/<paramref name="id"/> (it is being deleted) and returns their jobs.
    /// </summary>
    /// <remarks>
    /// Plan 2.7b, final review I3: where key locks hold (<see cref="ISqlDialect.KeyLocksNeedTransaction"/>, PostgreSQL), the job's key
    /// is taken before its row, as <see cref="RegisterAsync(DataSession, Guid, EntityReference, DateTimeOffset?, long?, CancellationToken)"/>
    /// and <see cref="WakeRunningAsync"/> (which the delete runs next) take it: the waits are read without a lock, each job's key is
    /// locked in order, then its row deleted. A deleted row and then the key would be the opposite order of a registration of the
    /// same job, which holds the key and waits for the row: a deadlock. SQL Server runs the one DELETE it ran (its key locks are
    /// the range locks of the statements themselves, no extra read).
    /// </remarks>
    internal async Task<IReadOnlyList<Guid>> RemoveForRecordAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        if (!await AnyAsync(s, ct)) return [];
        if (s.Dialect.KeyLocksNeedTransaction) return await RemoveForRecordKeyedAsync(s, entity, id, ct);
        string[] returned = ["asyncoperationid"];
        await using var cmd = s.Command(
            $"DELETE FROM {Table}{s.Dialect.ReturningInline(returned, RowImage.Deleted)} WHERE entity = @entity AND objectid = @id{s.Dialect.ReturningTrailing(returned, RowImage.Deleted)}");
        cmd.AddParameter("@entity", DbType.String, entity, 128);
        cmd.AddParameter("@id", DbType.Guid, id);
        var jobs = new List<Guid>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) jobs.Add(rd.GetGuid(0));
        if (jobs.Count > 0) Forget();
        return jobs;
    }

    /// <summary>
    /// <see cref="RemoveForRecordAsync"/> where key locks hold: the jobs waiting on the record are read, each one's key is locked
    /// (in order) and its row on the record deleted; read again until no new job waits on it (a registration committed between the
    /// read and the deletes). Every row deleted is one whose key the transaction holds.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> RemoveForRecordKeyedAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        var tried = new HashSet<Guid>();
        var jobs = new List<Guid>();
        while (true)
        {
            var found = new List<Guid>();
            await using (var read = s.Command($"SELECT asyncoperationid FROM {Table} WHERE entity = @entity AND objectid = @id"))
            {
                read.AddParameter("@entity", DbType.String, entity, 128);
                read.AddParameter("@id", DbType.Guid, id);
                await using var rd = await read.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) found.Add(rd.GetGuid(0));
            }
            var fresh = found.Where(tried.Add).Order().ToList();
            if (fresh.Count == 0) break;
            string[] returned = ["asyncoperationid"];
            foreach (var job in fresh)
            {
                await s.KeyLockAsync(Table, job, ct);
                await using var delete = s.Command(
                    $"DELETE FROM {Table}{s.Dialect.ReturningInline(returned, RowImage.Deleted)} WHERE asyncoperationid = @job AND entity = @entity AND objectid = @id{s.Dialect.ReturningTrailing(returned, RowImage.Deleted)}");
                delete.AddParameter("@job", DbType.Guid, job);
                delete.AddParameter("@entity", DbType.String, entity, 128);
                delete.AddParameter("@id", DbType.Guid, id);
                await using var rd = await delete.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) jobs.Add(rd.GetGuid(0));
            }
        }
        if (jobs.Count > 0) Forget();
        return jobs;
    }

    /// <summary>The next use counts the rows again (a row was removed: there may be none left).</summary>
    private void Forget()
    {
        lock (_gate)
            if (_any == true) _any = null;
    }

    /// <summary>
    /// The state the next <see cref="HasAny"/> reads is counted again from the database (a host start, or after a test removed rows).
    /// </summary>
    public async Task RefreshAsync(DataSession s, CancellationToken ct)
    {
        lock (_gate) _any = null;
        await AnyAsync(s, ct);
    }

    /// <summary>The state of a new process: unknown, counted from the database on next use (a test of a restart).</summary>
    internal void Reset()
    {
        lock (_gate) _any = null;
    }

    /// <summary><see cref="HasAny"/>, counting the rows when it is unknown.</summary>
    private async Task<bool> AnyAsync(DataSession s, CancellationToken ct)
    {
        long version;
        lock (_gate)
        {
            if (_any is { } known) return known;
            version = _version;
        }
        // The row of a job that has ended waits for nothing (a delete gave it to the job as it ended; the worker removes it).
        await using var cmd = s.Command($"SELECT CASE WHEN EXISTS (SELECT 1 FROM {Table} w WHERE NOT EXISTS (SELECT 1 FROM data.asyncoperation j " +
                                        "WHERE j.asyncoperationid = w.asyncoperationid AND j.statecode = 3)) THEN 1 ELSE 0 END");
        var any = (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
        lock (_gate)
        {
            // A registration since the count began: there is at least that one.
            if (_version != version) return true;
            _any = any;
        }
        return any;
    }
}
