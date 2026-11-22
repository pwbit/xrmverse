using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Async;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Duplicates;

/// <summary>
/// Runs the publish jobs of duplicate detection rules (<c>asyncoperation.operationtype</c> 7,
/// <see cref="AsyncOperationQueue.DuplicateRulePublish"/>) for the <see cref="AsyncOperationWorker"/> (plan 2.15, ruling D10):
/// the rule named by the job's <c>regardingobjectid</c>, still Publishing, gets the code of every row of its matching table
/// (batches of <see cref="MatchCodeStore.FillBatchSize"/>, each in a transaction of its own, as the system), then becomes
/// Published (1/2). The core writes of the meantime have kept the table already, and a batch never overwrites their codes.
/// The job starts by invalidating the <see cref="DuplicateRuleCache"/>, so a write that began after the publish committed
/// reads the rule as Publishing and keeps its code; the backfill reads the rows with <c>REPEATABLEREAD</c>, so it waits for
/// a write still open and codes its committed value, and a write of a row it read waits for its batch to commit.
/// </summary>
/// <remarks>
/// A job belongs to one publish: the publish registered the rule's match code table with the job's id
/// (<c>meta.matchcode_table.publish_job</c>), and every write of the job (each batch's inserts, the move to Published, the reset
/// after a failure) happens only while the table is still registered with it. A job whose publish was superseded (the rule
/// unpublished, by its message or a switch-off, republished after that, or deleted) stops at its next step without writing
/// anything, and succeeds (3/30) with <see cref="SupersededMessage"/>: it has nothing left to do, and the rule's state is the
/// later operation's. Any other failure puts a rule still Publishing for this job back to Unpublished (0/0) and drops its table,
/// and the job fails (3/31).
/// </remarks>
public sealed class DuplicateRulePublishJob(IDbConnectionFactory factory, DuplicateRulePublisher publisher) : IAsyncOperationHandler
{
    private const int BatchAttempts = 3;

    /// <summary>The <c>message</c> of a job whose publish was superseded before it ended (it wrote nothing more).</summary>
    public const string SupersededMessage =
        "The duplicate detection rule was unpublished, published again or deleted before this publish job ended: the job stopped without changing it.";

    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.DuplicateRulePublish;

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the job names no rule.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var job = run.AsyncOperationId;
        Guid? regarding;
        await using (var conn = await factory.OpenAsync(ct))
            regarding = await AsyncOperationQueue.RegardingObjectIdAsync(new DataSession(conn, null), job, ct);
        if (regarding is not { } ruleId)
            throw XrmdFaultException.InvalidArgument("The system job names no duplicate detection rule (regardingobjectid).");

        try
        {
            if (!await InTransactionAsync(s => MatchCodeStore.IsCurrentPublishAsync(s, ruleId, job, ct), ct))
            {
                run.CompletionMessage = SupersededMessage;
                return;
            }
            publisher.MatchCodes.Cache.Invalidate();
            PublishedRule? rule;
            await using (var conn = await factory.OpenAsync(ct))
                rule = (await publisher.MatchCodes.Cache.LoadAsync(new DataSession(conn, null), ruleId, ct)).SingleOrDefault();
            // Read after the check: a rule unpublished (and maybe edited and republished) in between is caught by the first batch.
            if (rule is not { StatusCode: RuleStatus.Publishing })
            {
                run.CompletionMessage = SupersededMessage;
                return;
            }

            Guid? after = null;
            while (true)
            {
                var (current, read, last) = await InTransactionAsync(
                    s => publisher.MatchCodes.FillBatchAsync(s, rule, job, after, MatchCodeStore.FillBatchSize, ct), ct);
                if (!current)
                {
                    run.CompletionMessage = SupersededMessage;
                    return;
                }
                if (read < MatchCodeStore.FillBatchSize) break;
                after = last;
            }

            if (!await InTransactionAsync(s => publisher.CompletePublishAsync(s, ruleId, job, ct), ct))
                run.CompletionMessage = SupersededMessage;
        }
        catch
        {
            // Whatever failed (the host stopping included), a rule left Publishing for this job would never become Published;
            // a later publish of the rule is not touched.
            await InTransactionAsync(s => publisher.ResetFailedPublishAsync(s, ruleId, job, CancellationToken.None), CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>The worker failed the job before it ran (its owner disabled or gone): the rule goes back to Unpublished.</remarks>
    public async Task OnFailedBeforeRunAsync(Guid asyncOperationId, CancellationToken ct)
    {
        Guid? regarding;
        await using (var conn = await factory.OpenAsync(ct))
            regarding = await AsyncOperationQueue.RegardingObjectIdAsync(new DataSession(conn, null), asyncOperationId, ct);
        if (regarding is not { } ruleId) return;
        await InTransactionAsync(s => publisher.ResetFailedPublishAsync(s, ruleId, asyncOperationId, ct), ct);
    }

    /// <summary>Runs <paramref name="work"/> in a transaction of its own (again, up to <see cref="BatchAttempts"/> times, after a deadlock).</summary>
    private async Task<T> InTransactionAsync<T>(Func<DataSession, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var conn = await factory.OpenAsync(ct);
            var session = new DataSession(conn, await conn.BeginTransactionAsync(ct));
            try
            {
                var result = await work(session);
                await session.Transaction!.CommitAsync(ct);
                session.Committed();
                return result;
            }
            catch (DbException ex) when (session.Dialect.Classify(ex) == DbErrorKind.Deadlock && attempt < BatchAttempts)
            {
                await RollBackAsync(session);
            }
            catch
            {
                await RollBackAsync(session);
                throw;
            }
        }
    }

    private static async Task RollBackAsync(DataSession session)
    {
        try
        {
            if (session.Transaction?.Connection is not null) await session.Transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // The connection is broken: nothing was committed.
        }
        finally
        {
            session.RolledBack();
        }
    }
}
