using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Core.Elastic;

/// <summary>
/// The time to live of elastic rows (plan 3.4, ruling E7): a row with a <c>ttlinseconds</c> expires at
/// <c>modifiedon + ttlinseconds</c> seconds. The sweep deletes the expired rows through the core delete as the SYSTEM user
/// (<see cref="Caller.Internal"/>), so audit and change tracking see a Delete; the pipeline does not run, so no plug-in step fires.
/// </summary>
public static class ElasticTtl
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> expired rows of each elastic table (at most one batch of deletions per table per call; rows that fail, or that were renewed since they were read, are passed over and do not count), each in a
    /// transaction of its own so that a row that cannot be deleted does not block the others (it is skipped and logged to
    /// <paramref name="logger"/>; the caller's own transaction, when <paramref name="s"/> has one, is used instead).
    /// </summary>
    /// <param name="utcNow">The instant the expiry is judged at: a row is expired when its expiry is at or before it.</param>
    /// <returns>The number of rows deleted.</returns>
    public static async Task<int> PurgeAsync(CoreOperations core, DataSession s, DateTime utcNow, int batchSize, CancellationToken ct,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var deleted = 0;
        foreach (var m in core.ElasticEntities().ToList())
        {
            // Keyset paging on (modifiedon, id): a row that cannot be deleted is passed over, so persistent failures never fill
            // the batch and starve the expired rows behind them. The table stops at batchSize deletions or when no expired row is left.
            var tableDeleted = 0;
            (DateTime On, Guid Id)? after = null;
            while (tableDeleted < batchSize)
            {
                var page = await ExpiredAsync(s, m.LogicalName, SystemAttributes.PrimaryIdName(m), utcNow, batchSize, after, ct);
                if (page.Count == 0) break;
                foreach (var (on, id) in page)
                {
                    ct.ThrowIfCancellationRequested();
                    after = (on, id);
                    var own = s.Transaction is null ? await s.Connection.BeginTransactionAsync(ct) : null;
                    try
                    {
                        var session = own is null ? s : new DataSession(s.Connection, own);
                        var gone = await DeleteIfExpiredAsync(core, session, m, id, utcNow, ct);
                        if (own is not null) await own.CommitAsync(ct);
                        if (gone) tableDeleted++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (own is not null) await own.RollbackAsync(CancellationToken.None);
                        if (s.Transaction is not null) throw; // the caller's transaction is in an unknown state
                        logger?.LogWarning(ex, "The time to live sweep could not delete the {Table} row {Id}; it is tried again at the next sweep.",
                            m.LogicalName, id);
                    }
                    finally
                    {
                        if (own is not null) await own.DisposeAsync();
                    }
                    if (tableDeleted >= batchSize) break;
                }
                if (page.Count < batchSize) break;
            }
            deleted += tableDeleted;
        }
        return deleted;
    }

    /// <summary>
    /// Deletes the row <paramref name="id"/> of <paramref name="m"/> through the core delete when it is still expired at
    /// <paramref name="utcNow"/>: the expiry is checked again under an update lock held until the end of the transaction of
    /// <paramref name="s"/>, so an Update that renewed the row after the page of expired rows was read keeps it (the write that
    /// lands first wins, Review Focus 4).
    /// </summary>
    /// <returns>True when the row was deleted, false when it is gone or no longer expired.</returns>
    /// <exception cref="ArgumentException"><paramref name="s"/> has no transaction (the lock would not last until the delete).</exception>
    internal static async Task<bool> DeleteIfExpiredAsync(CoreOperations core, DataSession s, EntityMetadata m, Guid id, DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        if (s.Transaction is null) throw new ArgumentException("The time to live delete needs a transaction.", nameof(s));
        var d = s.Dialect;
        // Plan 2.7b, Task 5: the key lock before the row lock, the order of every writer of the row (the core delete's row lock).
        await s.KeyLockAsync(m.LogicalName, id, ct);
        await using (var cmd = s.Command($"""
            SELECT 1 FROM {s.DataTable(m.LogicalName)}{d.LockHint(RowLock.UpdateRow)}
            WHERE {d.Quote(SystemAttributes.PrimaryIdName(m))} = @id AND {ExpiredPredicate(d)}{d.LockClause(RowLock.UpdateRow)}
            """))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            cmd.AddParameter("@now", DbType.DateTime2, utcNow);
            if (await cmd.ExecuteScalarAsync(ct) is null) return false;
        }
        await core.DeleteAsync(s, Caller.Internal, m.LogicalName, id, ct);
        return true;
    }

    /// <summary>A row expired at <c>@now</c> (the <c>modifiedon</c> bound is redundant but sargable).</summary>
    private static string ExpiredPredicate(ISqlDialect d)
    {
        var ttl = d.Quote(ElasticTables.TtlInSeconds);
        var modifiedOn = d.Quote("modifiedon");
        return $"{ttl} IS NOT NULL AND {modifiedOn} <= @now AND {d.AddInterval("second", ttl, modifiedOn)} <= @now";
    }

    /// <summary>
    /// The next (at most <paramref name="batchSize"/>) rows expired at <paramref name="utcNow"/>, oldest first, after the
    /// <paramref name="after"/> position. Each row is checked again before its delete (<see cref="DeleteIfExpiredAsync"/>).
    /// </summary>
    private static async Task<List<(DateTime On, Guid Id)>> ExpiredAsync(DataSession s, string table, string idColumn, DateTime utcNow,
        int batchSize, (DateTime On, Guid Id)? after, CancellationToken ct)
    {
        var d = s.Dialect;
        var (modifiedOn, id) = (d.Quote("modifiedon"), d.Quote(idColumn));
        await using var cmd = s.Command(d.Limit($"""
            SELECT {modifiedOn}, {id} FROM {s.DataTable(table)}
            WHERE {ExpiredPredicate(d)}
              {(after is null ? "" : $"AND ({modifiedOn} > @afterOn OR ({modifiedOn} = @afterOn AND {id} > @afterId))")}
            ORDER BY {modifiedOn}, {id}
            """, $"({batchSize})"));
        cmd.AddParameter("@now", DbType.DateTime2, utcNow);
        if (after is { } a)
        {
            cmd.AddParameter("@afterOn", DbType.DateTime2, a.On);
            cmd.AddParameter("@afterId", DbType.Guid, a.Id);
        }
        var rows = new List<(DateTime, Guid)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) rows.Add((rd.GetDateTime(0), rd.GetGuid(1)));
        return rows;
    }
}
