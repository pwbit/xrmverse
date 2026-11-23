using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Async;

namespace Xrmd.Core.Mail;

/// <summary>The settings of the incoming email synchronization (plan 4.1c), bound from the <c>Mail</c> section.</summary>
public sealed class MailboxSyncOptions
{
    /// <summary>The shortest interval accepted for <see cref="IncomingSyncInterval"/>.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);

    /// <summary>The largest batch accepted for <see cref="IncomingBatchSize"/>.</summary>
    public const int MaximumBatchSize = 500;

    /// <summary><c>Mail:IncomingSyncInterval</c>: the time between two passes of a mailbox (default 1 minute, at least 10 seconds).</summary>
    public TimeSpan IncomingSyncInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary><c>Mail:IncomingBatchSize</c>: the most messages one pass lists (default 50, 1 to 500).</summary>
    public int IncomingBatchSize { get; set; } = 50;
}

/// <summary>
/// Synchronizes the incoming email of one mailbox (<c>asyncoperation.operationtype</c> <see cref="AsyncOperationQueue.MailboxSync"/>, plan
/// 4.1c, task 9): one pass per run, then the job suspends itself until the next interval, so the worker runs one pass at a time per
/// mailbox, keeps the job across a restart and runs it again when it was interrupted. Data: <c>{"mailboxId": "&lt;guid&gt;"}</c>; the
/// job is regarding the mailbox and owned by its delivery user (<see cref="Mailboxes.DeliveryUserAsync"/>).
/// </summary>
/// <remarks>
/// <para>One pass: the mailbox must still be able to receive (<see cref="MailboxReceiver.CanReceive"/>; otherwise the job ends and its
/// message says why), the delivery user is resolved, <c>processemailreceivedafter</c> is set to now when empty, and the inbox is opened
/// read-only. Up to <see cref="MailboxSyncOptions.IncomingBatchSize"/> messages are listed from the stored cursor
/// (<see cref="MailboxSyncState"/>); each is fetched (a message gone meanwhile is skipped) and imported in a transaction of its own
/// (<see cref="IncomingEmailImporter"/>), and the cursor after it is stored. The message keys make a pass run again safe: a message
/// imported before the cursor was stored is a duplicate. At the end the cursor of the whole listing is stored,
/// <c>lastsuccessfulsynccompletedon</c> is set, and the job is suspended until now plus the interval (now, when the server has more).</para>
/// <para>Failures (ruling PF11): an import refused by its own pipeline (<see cref="IncomingImportException"/>) stops the pass with the
/// cursor unchanged and counts against the message (<c>failkey</c>, <c>failcount</c>); at the third failure in a row the message is
/// skipped, the cursor moves past it (or, on a listing without resume points, its key is remembered until the cursor moves: ruling
/// T9-a) and <c>lastsyncerror</c> names it. The same count holds for any other failure that belongs to the message (final review I2):
/// the server refuses to give it (an IMAP <c>NO</c> or <c>BAD</c> on its fetch, a Graph 4xx on its MIME), it is not readable MIME, or the
/// reader or the importer throws for it anything but a database, network or timeout failure. A successful pass sets <c>lastsyncerrorcount</c> back to 0 and keeps the text of <c>lastsyncerror</c>. A refusal for a missing privilege is the setup's, not the
/// message's (ruling T8-c): it is recorded and the pass ends without counting it as a strike; refused for the same message three passes
/// in a row, it sets <c>incomingemailstatus</c> Failure with the reason (final review M4), so the admin sees it. A permanent failure of the mailbox (its setup, its
/// delivery user, authentication) sets <c>incomingemailstatus</c> Failure with <c>lastsyncerror</c>, <c>lastsyncerrorcount</c> and
/// <c>lastsyncerroroccurredon</c>, and ends the job: a successful incoming test schedules it again (<see cref="MailboxTestJob"/>). Any other
/// failure (the network, the database, a provider timeout) is transient: it is recorded, the status is kept, nothing counts against a
/// message, and the job waits for the next interval. The server mailbox is never changed.</para>
/// </remarks>
public sealed class MailboxSyncJob(IDbConnectionFactory factory, CoreOperations core, MailProviders providers, IncomingEmailImporter importer,
    MailboxSyncOptions options, ILogger<MailboxSyncJob>? logger = null) : IAsyncOperationHandler
{
    /// <summary>The <c>name</c> and <c>messagename</c> of a mailbox sync job.</summary>
    public const string MessageName = "SyncMailbox";

    /// <summary>How many passes in a row a message may fail its import before it is skipped.</summary>
    public const int MaxAttempts = 3;

    private const int MaxErrorLength = 2048;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.MailboxSync;

    /// <summary>The message keys make a pass run again safe: an interrupted one runs again.</summary>
    public bool RestartsWhenInterrupted => true;

    /// <summary>The job's data for the mailbox <paramref name="mailboxId"/>.</summary>
    public static string DataOf(Guid mailboxId) => new JsonObject { ["mailboxId"] = mailboxId }.ToJsonString();

    /// <summary>
    /// Queues the sync job of <paramref name="mailboxId"/> in <paramref name="s"/>'s transaction, owned by its delivery user, unless the
    /// mailbox already has one that has not ended (checked under the mailbox's row lock, so two callers add one job). Nothing when the
    /// mailbox does not exist. The worker is woken once the transaction commits.
    /// </summary>
    /// <returns>True when a job was queued.</returns>
    /// <exception cref="MailSetupException">The mailbox has no delivery user (<see cref="Mailboxes.DeliveryUserAsync"/>).</exception>
    public static async Task<bool> EnsureScheduledAsync(DataSession s, AsyncOperationQueue queue, Guid mailboxId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(queue);
        await using (var lockCmd = s.Command(
                         $"SELECT mailboxid FROM {s.DataTable(MailSchema.Mailbox)}{s.Dialect.LockHint(RowLock.UpdateRow)} WHERE mailboxid = @id{s.Dialect.LockClause(RowLock.UpdateRow)}"))
        {
            lockCmd.AddParameter("@id", DbType.Guid, mailboxId);
            if (await lockCmd.ExecuteScalarAsync(ct) is not Guid) return false;
        }
        if (await HasJobAsync(s, mailboxId, ct)) return false;
        var receiver = await Mailboxes.ResolveReceiverAsync(s, mailboxId, ct);
        if (receiver is null) return false;
        var user = await Mailboxes.DeliveryUserAsync(s, receiver, ct);
        await queue.EnqueueSystemJobAsync(s, user, AsyncOperationQueue.MailboxSync, MessageName, MessageName, MailSchema.Mailbox, mailboxId,
            DataOf(mailboxId), ct);
        s.OnCommitted(queue.Signal);
        return true;
    }

    /// <summary>
    /// At host start and then periodically (<c>MailboxSyncStartup</c>): schedules every mailbox that can receive (<see cref="MailboxReceiver.CanReceive"/>) and has no sync job that has not
    /// ended, each in a transaction of its own. A mailbox that cannot be scheduled (no delivery user) is logged and left.
    /// </summary>
    /// <returns>How many jobs were queued.</returns>
    public static async Task<int> ScheduleReceivableAsync(IDbConnectionFactory factory, AsyncOperationQueue queue, ILogger? logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(queue);
        var candidates = await MailConnections.InTransactionAsync(factory, async s =>
        {
            var ids = new List<Guid>();
            await using var cmd = s.Command($"""
                SELECT m.mailboxid FROM {s.DataTable(MailSchema.Mailbox)} m
                WHERE m.incomingemaildeliverymethod = {MailSchema.DeliveryServerSide} AND m.enabledforincomingemail = {s.Dialect.BooleanLiteral(true)}
                    AND m.incomingemailstatus = {MailSchema.StatusSuccess} AND (m.statecode IS NULL OR m.statecode = 0)
                    AND NOT EXISTS (SELECT 1 FROM data.asyncoperation a WHERE a.regardingobjectid = m.mailboxid
                        AND a.operationtype = {AsyncOperationQueue.MailboxSync} AND a.statecode <> 3)
                """);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
            return ids;
        }, ct);
        var queued = 0;
        foreach (var id in candidates)
        {
            try
            {
                queued += await MailConnections.InTransactionAsync(factory, async s =>
                {
                    if (await Mailboxes.ResolveReceiverAsync(s, id, ct) is not { CanReceive: true }) return 0;
                    return await EnsureScheduledAsync(s, queue, id, ct) ? 1 : 0;
                }, ct);
            }
            catch (MailSetupException ex)
            {
                logger?.LogWarning("The incoming email of mailbox {MailboxId} is not synchronized: {Reason}", id, ex.Message);
            }
        }
        return queued;
    }

    private static async Task<bool> HasJobAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit(
            "SELECT asyncoperationid FROM data.asyncoperation WHERE regardingobjectid = @id AND operationtype = @type AND statecode <> 3", "1"));
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        cmd.AddParameter("@type", DbType.Int32, AsyncOperationQueue.MailboxSync);
        return await cmd.ExecuteScalarAsync(ct) is Guid;
    }

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the data names no mailbox.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var mailboxId = Parse(run.Data);
        var data = DataOf(mailboxId);
        var next = run.Now + options.IncomingSyncInterval;

        Prepared prepared;
        try
        {
            prepared = await MailConnections.InTransactionAsync(factory, s => PrepareAsync(s, mailboxId, ct), ct);
        }
        catch (MailSetupException ex)
        {
            await FailMailboxAsync(run, mailboxId, ex.Message, ct);
            return;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            await TransientAsync(run, mailboxId, $"The incoming email of the mailbox {mailboxId} could not be prepared: {ex.Message}", next, data, ct);
            return;
        }
        if (prepared.EndMessage is { } end)
        {
            _logger.LogInformation("The sync job of mailbox {MailboxId} ends: {Reason}", mailboxId, end);
            run.CompletionMessage = end;
            return;
        }

        var (receiver, user, connection, state) = (prepared.Receiver!, prepared.User!, prepared.Connection!, prepared.State!);
        IIncomingSession session;
        try
        {
            session = await providers.IncomingFor(connection.Kind).OpenAsync(receiver.Address!, connection.Profile, connection.Credentials, ct);
        }
        catch (MailSendException ex) when (!ex.Transient)
        {
            await FailMailboxAsync(run, mailboxId, ex.Message, ct);
            return;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            await TransientAsync(run, mailboxId, ex.Message, next, data, ct);
            return;
        }

        await using (session)
        {
            IncomingBatch batch;
            try
            {
                batch = await session.ListAsync(state.Cursor, receiver.ReceivedAfter!.Value, options.IncomingBatchSize, ct);
            }
            catch (MailSendException ex) when (!ex.Transient && ex.Scope == MailFailureScope.Mailbox)
            {
                await FailMailboxAsync(run, mailboxId, ex.Message, ct);
                return;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                await TransientAsync(run, mailboxId, ex.Message, next, data, ct);
                return;
            }

            var imported = 0;
            foreach (var item in batch.Items)
            {
                // Ruling T9-a: a message skipped since the cursor last moved is listed again (no resume point): left alone.
                var key = KeyOf(item);
                if (state.IsSkipped(key)) continue;
                MimeMessage? message = null;
                try
                {
                    // A message received before processemailreceivedafter is not delivered (the listing filters by date already).
                    if (ReceivedInWindow(item.ReceivedOn, receiver.ReceivedAfter.Value))
                    {
                        message = await session.FetchAsync(item, ct);
                        if (message is not null)
                        {
                            await importer.ImportAsync(receiver, user, item, message, ct);
                            imported++;
                        }
                    }
                }
                catch (IncomingImportException ex) when (IsPrivilegeFault(ex))
                {
                    // Ruling T8-c: the delivery user's setup, not the message: retried at the next interval, never a poison strike. Final
                    // review M4: refused for the same message MaxAttempts passes in a row, the mailbox fails, so the admin sees it.
                    var same = state.FailKey is { } k && k.AsSpan().SequenceEqual(key);
                    var denied = same ? state.DeniedCount + 1 : 1;
                    if (denied < MaxAttempts)
                    {
                        state = state with { FailKey = key, FailCount = same ? state.FailCount : 0, DeniedCount = denied };
                        if (!await SaveAsync(mailboxId, state, ex.Message, success: false, ct)) { Reset(run, mailboxId); return; }
                        _logger.LogWarning("Incoming message {ServerId} of mailbox {MailboxId} was refused for a missing privilege ({Count} of {Max}): {Reason}",
                            item.ServerId, mailboxId, denied, MaxAttempts, ex.Message);
                        run.Suspend(next, data);
                        return;
                    }
                    state = state with { FailKey = null, FailCount = 0, DeniedCount = 0 };
                    if (!await SaveAsync(mailboxId, state, null, success: false, ct)) { Reset(run, mailboxId); return; }
                    var name = MessageIdOf(item, message) ?? item.ServerId;
                    await FailMailboxAsync(run, mailboxId,
                        $"The incoming message {name} was refused for a missing privilege of the delivery user {MaxAttempts} passes in a row: {ex.InnerException?.Message ?? ex.Message}",
                        ct);
                    return;
                }
                catch (Exception ex) when (IsMessageFailure(ex, ct))
                {
                    // Ruling PF11 and final review I2: the message's own failure (its pipeline refused it, the server will not give it,
                    // it cannot be read): counted against it, and skipped at the third failure in a row.
                    var reason = ex is IncomingImportException ? ex.InnerException?.Message ?? ex.Message : ex.Message;
                    var count = state.FailKey is { } k && k.AsSpan().SequenceEqual(key) ? state.FailCount + 1 : 1;
                    if (count < MaxAttempts)
                    {
                        state = state with { FailKey = key, FailCount = count, DeniedCount = 0 };
                        var text = ex is IncomingImportException ? ex.Message : $"The incoming message {item.ServerId} of the mailbox {mailboxId} failed: {ex.Message}";
                        if (!await SaveAsync(mailboxId, state, text, success: false, ct)) { Reset(run, mailboxId); return; }
                        _logger.LogWarning("Incoming message {ServerId} of mailbox {MailboxId} failed its import ({Count} of {Max}): {Reason}", item.ServerId,
                            mailboxId, count, MaxAttempts, reason);
                        run.Suspend(next, data);
                        return;
                    }
                    // The third failure in a row: the message is skipped and the pass goes on after it.
                    var name = MessageIdOf(item, message) ?? item.ServerId;
                    var error = $"The incoming message {name} was skipped after {MaxAttempts} failed imports: {reason}";
                    state = Skip(state, item, key) with { FailKey = null, FailCount = 0, DeniedCount = 0 };
                    if (!await SaveAsync(mailboxId, state, error, success: false, ct)) { Reset(run, mailboxId); return; }
                    _logger.LogWarning("Mailbox {MailboxId}: {Error}", mailboxId, error);
                    continue;
                }
                catch (Exception ex) when (ex is MailSetupException or MailSendException { Transient: false })
                {
                    await FailMailboxAsync(run, mailboxId, ex.Message, ct);
                    return;
                }
                catch (Exception ex) when (IsTransient(ex, ct))
                {
                    // Not the message's (final review I2): the database, the provider's connection, a timeout; nothing counts against it.
                    await TransientAsync(run, mailboxId, ex.Message, next, data, ct);
                    return;
                }

                // Stored after each message, so an interrupted pass resumes after it.
                var after = state.WithCursor(item.CursorAfter) with { FailKey = null, FailCount = 0, DeniedCount = 0 };
                if (after != state)
                {
                    state = after;
                    if (!await SaveAsync(mailboxId, state, null, success: false, ct)) { Reset(run, mailboxId); return; }
                }
            }

            state = state.WithCursor(batch.CursorAtEnd);
            if (!await SaveAsync(mailboxId, state, null, success: true, ct))
            {
                Reset(run, mailboxId);
                return;
            }
            _logger.LogDebug("Mailbox {MailboxId}: {Count} incoming messages listed, {Imported} imported or already known.", mailboxId,
                batch.Items.Count, imported);
            run.Suspend(batch.More ? run.Now : next, data);
        }
    }

    /// <summary>
    /// <paramref name="state"/> past the skipped <paramref name="item"/>: its cursor when it is a resume point, else its key remembered
    /// until the cursor moves (ruling T9-a).
    /// </summary>
    private MailboxSyncState Skip(MailboxSyncState state, IncomingRef item, byte[] key) =>
        item.CursorAfter is not null ? state.WithCursor(item.CursorAfter) : state.WithSkipped(key, options.IncomingBatchSize);

    /// <summary>
    /// Stores <paramref name="state"/> in a transaction of its own, with <paramref name="error"/> as the last error when given, and, for
    /// <paramref name="success"/>, <c>lastsuccessfulsynccompletedon</c> now and <c>lastsyncerrorcount</c> 0 (the text is kept). Under the
    /// mailbox's row lock, nothing is written when the mailbox is gone or its incoming settings changed meanwhile (status Not Run, or a
    /// state generation other than the one the pass read, when a test succeeded since the reset): the pass must not bring back a cursor the
    /// change forgot.
    /// </summary>
    /// <returns>False when nothing was written.</returns>
    private Task<bool> SaveAsync(Guid mailboxId, MailboxSyncState state, string? error, bool success, CancellationToken ct) =>
        MailConnections.InTransactionAsync(factory, async s =>
        {
            await using (var cmd = s.Command(
                             $"SELECT incomingemailstatus FROM {s.DataTable(MailSchema.Mailbox)}{s.Dialect.LockHint(RowLock.UpdateRow)} WHERE mailboxid = @id{s.Dialect.LockClause(RowLock.UpdateRow)}"))
            {
                cmd.AddParameter("@id", DbType.Guid, mailboxId);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct) || rd.IsDBNull(0) || Convert.ToInt32(rd.GetValue(0)) == MailSchema.StatusNotRun) return false;
            }
            // Final review M2: a reset since the pass read its state (and a test that succeeded since) is seen by the generation.
            if (await MailboxSyncState.GenerationAsync(s, mailboxId, ct) != state.Generation) return false;
            await MailboxSyncState.WriteAsync(s, mailboxId, state, ct);
            if (error is not null) await RecordErrorAsync(s, mailboxId, error, permanent: false, ct);
            if (success)
            {
                await using var cmd = s.Command(
                    $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET lastsuccessfulsynccompletedon = @now, lastsyncerrorcount = 0 WHERE mailboxid = @id");
                cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
                cmd.AddParameter("@id", DbType.Guid, mailboxId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return true;
        }, ct);

    /// <summary>The mailbox is gone or its incoming settings changed during the pass: the job ends (a successful test schedules it again).</summary>
    private void Reset(AsyncOperationRun run, Guid mailboxId)
    {
        _logger.LogInformation("The incoming settings of mailbox {MailboxId} changed during a sync pass; the job ends.", mailboxId);
        run.CompletionMessage = $"The mailbox {mailboxId} was deleted or its incoming settings changed during the pass: its incoming email is no longer synchronized until a successful test.";
    }

    /// <summary>What a pass needs, or the reason the job ends (<see cref="EndMessage"/>).</summary>
    private sealed record Prepared(MailboxReceiver? Receiver, Caller? User, MailConnection? Connection, MailboxSyncState? State, string? EndMessage);

    /// <summary>The receiver, its delivery user, its connection and its state; <c>processemailreceivedafter</c> set to now when empty.</summary>
    /// <exception cref="MailSetupException">The mailbox's setup refuses the sync (no delivery user, a refused profile, no address).</exception>
    private async Task<Prepared> PrepareAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        var receiver = await Mailboxes.ResolveReceiverAsync(s, mailboxId, ct);
        if (receiver is null) return new Prepared(null, null, null, null, $"The mailbox {mailboxId} no longer exists: its incoming email is not synchronized.");
        if (!receiver.CanReceive) return new Prepared(null, null, null, null, $"The mailbox {mailboxId} cannot receive email: {WhyNot(receiver)}.");
        var user = await Mailboxes.DeliveryUserAsync(s, receiver, ct);
        if (string.IsNullOrWhiteSpace(receiver.Address)) throw new MailSetupException($"The mailbox {mailboxId} has no email address.");
        var connection = await MailConnections.ResolveIncomingAsync(s, core, mailboxId, receiver.ProfileId!.Value, $"{receiver.OwnerTable} {receiver.OwnerId}", ct);
        if (receiver.ReceivedAfter is null)
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
            await using var cmd = s.Command($"UPDATE {s.DataTable(MailSchema.Mailbox)} SET processemailreceivedafter = @now WHERE mailboxid = @id");
            cmd.AddParameter("@now", DbType.DateTime2, now);
            cmd.AddParameter("@id", DbType.Guid, mailboxId);
            await cmd.ExecuteNonQueryAsync(ct);
            receiver = receiver with { ReceivedAfter = now };
        }
        return new Prepared(receiver with { Address = receiver.Address.Trim() }, user, connection, await MailboxSyncState.ReadAsync(s, mailboxId, ct), null);
    }

    /// <summary>Why <paramref name="m"/> cannot receive, in the terms of its columns.</summary>
    private static string WhyNot(MailboxReceiver m)
    {
        var reasons = new List<string>();
        if (!m.Active) reasons.Add("it is inactive");
        if (!m.Approved) reasons.Add("its email address is not approved (emailrouteraccessapproval)");
        if (m.ProfileId is null) reasons.Add("it has no email server profile");
        if (m.DeliveryMethod != MailSchema.DeliveryServerSide) reasons.Add("its incoming delivery method is not Server-Side Synchronization (incomingemaildeliverymethod)");
        if (!m.Enabled) reasons.Add("it is not enabled for incoming email (enabledforincomingemail)");
        if (m.IncomingStatus != MailSchema.StatusSuccess) reasons.Add("its incoming email test has not succeeded (incomingemailstatus)");
        return string.Join("; ", reasons);
    }

    /// <summary>A permanent failure of the mailbox: <c>incomingemailstatus</c> Failure with the reason, and the job ends.</summary>
    private async Task FailMailboxAsync(AsyncOperationRun run, Guid mailboxId, string error, CancellationToken ct)
    {
        await MailConnections.InTransactionAsync(factory, s => RecordErrorAsync(s, mailboxId, error, permanent: true, ct), ct);
        _logger.LogWarning("The incoming email of mailbox {MailboxId} failed; the sync stops until a successful test: {Reason}", mailboxId, error);
        run.CompletionMessage = $"The incoming email of the mailbox {mailboxId} failed and is no longer synchronized until a successful test: {error}";
    }

    /// <summary>A transient failure: recorded, the status kept, and the job waits for the next interval.</summary>
    private async Task TransientAsync(AsyncOperationRun run, Guid mailboxId, string error, DateTimeOffset next, string data, CancellationToken ct)
    {
        try
        {
            await MailConnections.InTransactionAsync(factory, s => RecordErrorAsync(s, mailboxId, error, permanent: false, ct), ct);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            // The database itself may be the failure: the next pass records it.
            _logger.LogDebug(ex, "The sync error of mailbox {MailboxId} could not be recorded.", mailboxId);
        }
        _logger.LogWarning("The incoming email of mailbox {MailboxId} failed for now; next pass at {Next}: {Reason}", mailboxId, next, error);
        run.Suspend(next, data);
    }

    /// <summary>The last error on the mailbox; <paramref name="permanent"/> also sets <c>incomingemailstatus</c> Failure.</summary>
    private static async Task<bool> RecordErrorAsync(DataSession s, Guid mailboxId, string error, bool permanent, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET {(permanent ? $"incomingemailstatus = {MailSchema.StatusFailure}, " : "")}lastsyncerror = @error, " +
            $"lastsyncerrorcount = {s.Dialect.IsNull("lastsyncerrorcount", "0")} + 1, lastsyncerroroccurredon = @now WHERE mailboxid = @id");
        cmd.AddParameter("@error", DbType.String, error.Length > MaxErrorLength ? error[..MaxErrorLength] : error, MaxErrorLength);
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>Ruling T8-c: the pipeline refused the import for a missing privilege of the delivery user (403).</summary>
    private static bool IsPrivilegeFault(IncomingImportException ex) => ex.InnerException is XrmdFaultException { HttpStatus: 403 };

    /// <summary>
    /// Final review I2: a failure that belongs to the one message being fetched and imported: its import's pipeline refused it
    /// (<see cref="IncomingImportException"/>, but a missing privilege: ruling T8-c), the server will not give it (a
    /// <see cref="MailSendException"/> about the message), or any other exception of the fetch, the reader or the importer that is not
    /// the mailbox's setup (<see cref="MailSetupException"/>), the provider's connection or authentication (a <see cref="MailSendException"/>
    /// about the mailbox), the database or a timeout (<see cref="IsInfrastructure"/>), or the host stopping the job.
    /// </summary>
    private static bool IsMessageFailure(Exception ex, CancellationToken ct) => ex switch
    {
        _ when !IsTransient(ex, ct) => false,
        IncomingImportException => true,
        MailSendException m => m.Scope == MailFailureScope.Message,
        MailSetupException => false,
        _ => !IsInfrastructure(ex),
    };

    /// <summary>
    /// The database, the network or a timeout, anywhere in the chain of <paramref name="ex"/>: a <see cref="DbException"/>, a
    /// <see cref="TimeoutException"/>, an <see cref="IOException"/>, a <see cref="System.Net.Sockets.SocketException"/>, a cancellation, or
    /// an exception thrown by a database driver (such as a connection pool that ran out).
    /// </summary>
    internal static bool IsInfrastructure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException or TimeoutException or IOException or System.Net.Sockets.SocketException or OperationCanceledException) return true;
            var origin = e.TargetSite?.DeclaringType;
            if (origin is not null && (origin.Namespace?.StartsWith("System.Data", StringComparison.Ordinal) == true || IsDriverAssembly(origin.Assembly)))
                return true;
        }
        return false;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.Assembly, bool> DriverAssemblies = new();

    /// <summary>
    /// An assembly that defines a database connection type: a database driver, whichever engine it serves (the drivers are
    /// confined to the storage projects, so this names none of them).
    /// </summary>
    private static bool IsDriverAssembly(System.Reflection.Assembly assembly) => DriverAssemblies.GetOrAdd(assembly, a =>
    {
        try { return a.GetExportedTypes().Any(t => typeof(DbConnection).IsAssignableFrom(t)); }
        catch (Exception) { return false; }
    });

    /// <summary>
    /// Whether a message received at <paramref name="receivedOn"/> is delivered for a mailbox that processes email received at
    /// or after <paramref name="after"/>. Both are compared to the whole second: IMAP internal dates and Graph filters carry
    /// whole seconds, while <c>processemailreceivedafter</c> is set to the time the first pass ran, so a message that arrived
    /// in that same second is delivered.
    /// </summary>
    internal static bool ReceivedInWindow(DateTime receivedOn, DateTime after) =>
        AsUtc(receivedOn) >= new DateTime(AsUtc(after).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>A stored or listed time (UTC, possibly with an unspecified kind) as UTC.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Any failure but the host stopping the job (which runs again from its data).</summary>
    private static bool IsTransient(Exception ex, CancellationToken ct) => !(ex is OperationCanceledException && ct.IsCancellationRequested);

    /// <summary>
    /// The key of a listed item that failures count against and a skip remembers (ruling T9-a): the SHA-256 of its normalized
    /// <c>Message-ID</c> as the server lists it, else of its server id (stable across passes until the server renumbers its folder).
    /// </summary>
    private static byte[] KeyOf(IncomingRef item) =>
        MailMessageKeys.KeyOf(MailMessageKeys.Normalize(item.MessageId) ?? "server-id:" + item.ServerId);

    private static string? MessageIdOf(IncomingRef item, MimeMessage? message) =>
        MailMessageKeys.Normalize(message?.MessageId) is { } id ? $"<{id}>" : MailMessageKeys.Normalize(item.MessageId) is { } listed ? $"<{listed}>" : null;

    /// <exception cref="XrmdFaultException">400 when the data names no mailbox.</exception>
    private static Guid Parse(string? data)
    {
        try
        {
            if (data is not null && JsonNode.Parse(data) is JsonObject o && o["mailboxId"]?.GetValue<Guid>() is { } id && id != Guid.Empty)
                return id;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // Not the job's data: refused below.
        }
        throw XrmdFaultException.InvalidArgument("The mailbox sync job names no mailbox (data).");
    }
}
