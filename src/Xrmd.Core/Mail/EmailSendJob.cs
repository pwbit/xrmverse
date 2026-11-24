using System.Data;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Async;
using Xrmd.Security;

namespace Xrmd.Core.Mail;

/// <summary>
/// Sends one email (<c>asyncoperation.operationtype</c> <see cref="AsyncOperationQueue.EmailSend"/>, plan 4.1b), queued by SendEmail with
/// <c>IssueSend</c> true; the job runs as its owner, the user who called SendEmail. Data: <c>{"emailId": "&lt;guid&gt;"}</c>, plus the
/// queue items SendEmail deactivated (<c>"queueItems"</c>, #333) and the attempts made so far (<c>"attempts"</c>) once it waits for a
/// retry; every retry keeps the items.
/// </summary>
/// <remarks>
/// <para>A run first locks the email and ends without sending when it is no longer Pending Send (reopened, deleted, already sent) or
/// when a newer send job of the email exists (it was reopened and sent again). It then checks the sender again (the policy
/// <see cref="EmailSendPolicy"/>; a queue still readable by the owner; a user sender is the owner, ruling Z1), resolves the sender's
/// mailbox (<see cref="Mailboxes.ResolveSenderAsync"/>: approved, on a profile, Server-Side Synchronization, enabled, active and tested),
/// its profile (<see cref="ProfileRules"/>) and the credentials (<see cref="MailSecrets"/>), and the recipients' addresses. Any of these
/// that fails ends the email Failed (state 0, status 8) with the reason as the job's message, and nothing is sent.</para>
/// <para>Then it stores the <c>Message-ID</c> on the email (<c>messageid</c>, kept from an earlier attempt) and registers its key (outgoing,
/// <see cref="MailMessageKeys"/>; plan 4.1c), counts the attempt
/// (<c>deliveryattempts</c>) and commits, before the provider is called: an attempt that started is never made again by a run that
/// does not know its outcome. A run that finds more attempts on the email than its data counts (a run interrupted after it started
/// sending, which the worker puts back to Ready after a crash) fails without sending, and the email stays Pending Send (Known limits);
/// a job interrupted by the host stopping is not restarted (<see cref="RestartsWhenInterrupted"/> false) for the same reason.</para>
/// <para>The message is built (<see cref="MimeBuilder"/>, the attachments from the file store) and handed to the provider of the
/// profile (<see cref="MailProviders"/>). Success: the email becomes Sent (state 1, status 3) with <c>senton</c>, and the mailbox's
/// outgoing status Success. A transient failure (<see cref="MailSendException.Transient"/>) is recorded on the mailbox
/// (<c>lastsyncerror</c>, <c>lastsyncerrorcount</c>, <c>lastsyncerroroccurredon</c>) and retried after <see cref="RetryDelays"/> (the
/// job is suspended, the email stays Pending Send). A permanent failure, or a transient one after the last retry, ends the email Failed
/// with the reason as the job's message; when the mailbox is at fault (<see cref="MailFailureScope.Mailbox"/>, or the retries ran out)
/// the error is recorded on the mailbox with its outgoing status Failure, while a refusal of this message only
/// (<see cref="MailFailureScope.Message"/>: a recipient, its size) leaves the mailbox as it is. Secrets never reach a log, a message or
/// the job's data.</para>
/// <para>An email that ends Failed, for any of these reasons, gets back the queue items SendEmail deactivated, in the transaction that
/// fails it (<see cref="QueueMessages.ReactivateItemsAsync"/>: core writes as the job's owner, no queue item step fires; #333). A sent email leaves them
/// inactive.</para>
/// </remarks>
public sealed class EmailSendJob(IDbConnectionFactory factory, CoreOperations core, MailProviders providers, FileColumnService files,
    SecurityGuard guard, ILogger<EmailSendJob>? logger = null) : IAsyncOperationHandler
{
    /// <summary>The waits before the retries of a transient failure: three retries, after 1, 5 and 15 minutes.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private const int MaxErrorLength = 2048;
    private const int MessageIdLength = 320;
    private const int MaxReferences = 20;
    private const string Email = ActivitySchema.Email;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.EmailSend;

    /// <summary>An interrupted send may have reached the server: it is not started again (the email stays Pending Send).</summary>
    public bool RestartsWhenInterrupted => false;

    /// <summary>
    /// The job's data for the email <paramref name="emailId"/> after <paramref name="attempts"/> attempts, with the queue items
    /// SendEmail deactivated (<paramref name="queueItems"/>; none when null or empty).
    /// </summary>
    public static string DataOf(Guid emailId, int attempts = 0, IReadOnlyList<Guid>? queueItems = null)
    {
        var data = new JsonObject { ["emailId"] = emailId };
        if (attempts > 0) data["attempts"] = attempts;
        if (queueItems is { Count: > 0 }) data["queueItems"] = new JsonArray([.. queueItems.Select(i => (JsonNode)JsonValue.Create(i))]);
        return data.ToJsonString();
    }

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the data names no email.</exception>
    /// <exception cref="InvalidOperationException">An earlier run was interrupted after it started sending: the email is not sent again.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (emailId, attempts, queueItems) = Parse(run.Data);

        var prepared = await InTransactionAsync(s => PrepareAsync(s, run, emailId, attempts, queueItems, ct), ct);
        if (prepared is not Ready ready)
        {
            run.CompletionMessage = ((Ended)prepared).Message;
            return;
        }

        IReadOnlyList<AttachmentContent> attachments = [];
        try
        {
            MimeKit.MimeMessage message;
            try
            {
                await using (var conn = await factory.OpenAsync(ct))
                    attachments = await files.Attachments.ReadAllAsync(new DataSession(conn, null), emailId, ct);
                message = MimeBuilder.Build(ready.Mail with { Attachments = attachments });
            }
            catch (Exception ex) when (ex is MailSendException or XrmdFaultException)
            {
                // The message cannot be built (a line break in an address or a name, a missing attachment): the mailbox is not at fault.
                run.CompletionMessage = await FailAsync(run, emailId, queueItems, null, ex.Message, ct);
                return;
            }

            try
            {
                await providers.For(ready.Kind).SendAsync(message, ready.Profile, ready.Credentials, ct);
            }
            catch (MailSendException ex)
            {
                var attempt = attempts + 1;
                if (ex.Transient && attempt <= RetryDelays.Count)
                {
                    await InTransactionAsync(s => RecordErrorAsync(s, ready.MailboxId, ex.Message, failed: false, ct), ct);
                    _logger.LogWarning("Email {EmailId}: attempt {Attempt} failed ({Reason}); it is tried again in {Delay}.",
                        emailId, attempt, ex.Message, RetryDelays[attempt - 1]);
                    run.Suspend(run.Now + RetryDelays[attempt - 1], DataOf(emailId, attempt, queueItems));
                    return;
                }
                var reason = ex.Transient ? $"{ex.Message} (after {attempt} attempts)" : ex.Message;
                // A failure of the mailbox, or retries run out, sets its outgoing status Failure; a refusal of this message only (a
                // recipient, its size) fails the email and leaves the mailbox as it is.
                var mailboxAtFault = ex.Transient || ex.Scope == MailFailureScope.Mailbox;
                run.CompletionMessage = await FailAsync(run, emailId, queueItems, mailboxAtFault ? ready.MailboxId : null, reason, ct,
                    mailboxError: ex.Message);
                return;
            }
        }
        finally
        {
            foreach (var attachment in attachments) await attachment.Body.DisposeAsync();
        }

        var changed = await InTransactionAsync(async s =>
        {
            var still = await IsPendingSendAsync(s, emailId, ct);
            if (still) await core.EmailMessages.CompleteSendAsync(s, run.Owner, emailId, sent: true, ct);
            await MailboxSuccessAsync(s, ready.MailboxId, ct);
            return !still;
        }, ct);
        if (changed)
        {
            _logger.LogWarning("Email {EmailId} was sent, but it was changed while it was being sent: it is left as it is.", emailId);
            run.CompletionMessage = $"The email {emailId} was sent, but it was no longer Pending Send when the send ended: it is left as it is.";
        }
    }

    private abstract record Outcome;

    /// <summary>What the run sends, once the attempt is counted and the message id stored.</summary>
    private sealed record Ready(OutgoingMail Mail, MailProviderKind Kind, MailProfile Profile, MailCredentials Credentials, Guid MailboxId) : Outcome;

    /// <summary>The run ends without sending; <see cref="Message"/> is the job's message.</summary>
    private sealed record Ended(string Message) : Outcome;

    /// <summary>
    /// The checks before the send, under the email's update lock: ends the run (no longer Pending Send, a newer job), fails the email
    /// (sender, mailbox, profile, credentials or recipients), or counts the attempt and stores the message id.
    /// </summary>
    /// <exception cref="InvalidOperationException">The email has more attempts than the run counts: an interrupted send.</exception>
    private async Task<Outcome> PrepareAsync(DataSession s, AsyncOperationRun run, Guid emailId, int attempts, IReadOnlyList<Guid> queueItems,
        CancellationToken ct)
    {
        string? subject, body, messageId;
        Guid? parentId;
        int delivered;
        int? state, status;
        await using (var cmd = s.Command(
                         $"SELECT statecode, statuscode, subject, description, messageid, deliveryattempts, parentactivityid FROM {s.DataTable(Email)}" +
                         $"{s.Dialect.LockHint(RowLock.UpdateRow)} WHERE activityid = @id{s.Dialect.LockClause(RowLock.UpdateRow)}"))
        {
            cmd.AddParameter("@id", DbType.Guid, emailId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return new Ended($"The email {emailId} no longer exists: nothing was sent.");
            state = rd.IsDBNull(0) ? null : rd.GetInt32(0);
            status = rd.IsDBNull(1) ? null : rd.GetInt32(1);
            subject = rd.IsDBNull(2) ? null : rd.GetString(2);
            body = rd.IsDBNull(3) ? null : rd.GetString(3);
            messageId = rd.IsDBNull(4) ? null : rd.GetString(4);
            delivered = rd.IsDBNull(5) ? 0 : rd.GetInt32(5);
            parentId = rd.IsDBNull(6) ? null : rd.GetGuid(6);
        }
        if (state != 1 || status != MailSchema.EmailPendingSend)
            return new Ended($"The email {emailId} is no longer Pending Send (statecode {state}, statuscode {status}): nothing was sent.");
        if (await NewestJobAsync(s, emailId, ct) is { } newest && newest != run.AsyncOperationId)
            return new Ended($"The email {emailId} was sent again: its newer send job {newest} sends it, not this one.");
        if (delivered != attempts)
            throw new InvalidOperationException(
                $"The email {emailId} may already have been sent (message id {messageId}): an earlier run of this job was interrupted after " +
                "it started sending, so it is not sent again and stays Pending Send. Check whether it arrived, then close or resend it.");

        try
        {
            var ready = await ResolveAsync(s, run, emailId, subject, body, messageId, parentId, ct);
            await using (var cmd = s.Command($"UPDATE {s.DataTable(Email)} SET messageid = @messageid, inreplyto = COALESCE(@inreplyto, inreplyto), deliveryattempts = @attempts WHERE activityid = @id"))
            {
                cmd.AddParameter("@messageid", DbType.String, ready.Mail.MessageId, MessageIdLength);
                cmd.AddParameter("@inreplyto", DbType.String, ready.Mail.InReplyTo, MessageIdLength);
                cmd.AddParameter("@attempts", DbType.Int32, attempts + 1);
                cmd.AddParameter("@id", DbType.Guid, emailId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // Plan 4.1c: the message id is the email's key, outgoing, so that a copy coming back is not imported (kept from an earlier attempt).
            if (MailMessageKeys.Normalize(ready.Mail.MessageId) is { } normalized
                && await MailMessageKeys.TryRegisterAsync(s, MailMessageKeys.KeyOf(normalized), emailId, incoming: false, ct) is { } owner && owner != emailId)
                _logger.LogDebug("Email {EmailId}: its message id is already the key of email {OwnerId}; the key is kept there.", emailId, owner);
            return ready;
        }
        catch (MailSetupException ex)
        {
            await FailEmailAsync(s, run, emailId, queueItems, ct);
            _logger.LogWarning("Email {EmailId} was not sent: {Reason}", emailId, ex.Message);
            return new Ended(Failed(emailId, ex.Message));
        }
    }

    /// <summary>The sender, its mailbox, profile and credentials, and the message to send.</summary>
    /// <exception cref="MailSetupException">Any of them does not allow the send.</exception>
    private async Task<Ready> ResolveAsync(DataSession s, AsyncOperationRun run, Guid emailId, string? subject, string? body,
        string? storedMessageId, Guid? parentId, CancellationToken ct)
    {
        var email = core.Store.GetEntity(Email) ?? throw new InvalidOperationException("The email table is not seeded.");
        var parties = (await core.Parties.ReadAsync(s, email, [emailId], ["from", "to", "cc", "bcc"], ct))[emailId];
        var sender = await DemandSenderAsync(s, run, emailId, parties, ct);

        var mailbox = await Mailboxes.ResolveSenderAsync(s, sender, ct);
        if (!mailbox.CanSend) throw new MailSetupException(WhyNot(mailbox, sender));
        if (string.IsNullOrWhiteSpace(mailbox.Address))
            throw new MailSetupException($"The mailbox of {sender.LogicalName} {sender.Id} has no email address.");

        var (kind, profile, credentials) = await MailConnections.ResolveAsync(s, core, mailbox.Id, mailbox.ProfileId!.Value,
            $"{sender.LogicalName} {sender.Id}", ct);

        var messageId = string.IsNullOrWhiteSpace(storedMessageId) ? MimeBuilder.NewMessageId(mailbox.Address) : storedMessageId;
        if (messageId.Length > MessageIdLength) throw new MailSetupException($"The message id of the email is longer than {MessageIdLength} characters.");
        var references = await ReferencesAsync(s, parentId, ct);
        var mail = new OutgoingMail(messageId, Address(mailbox.Address, $"The mailbox of {sender.LogicalName} {sender.Id}"),
            Addresses(parties, "to"), Addresses(parties, "cc"), Addresses(parties, "bcc"), subject ?? "", body, [],
            references.Count > 0 ? references[^1] : null, references);
        return new Ready(mail, kind, profile, credentials, mailbox.Id);
    }

    /// <summary>
    /// The Message-IDs of the conversation the email answers, oldest first, ending with the one of its parent (<paramref name="parentId"/>):
    /// the parent's, then those of its ancestors (<c>parentactivityid</c> upward), at most <see cref="MaxReferences"/>. The walk stops at a
    /// record that is not an email or has no message id, and at a loop. A parent without a message id gives none (no headers).
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReferencesAsync(DataSession s, Guid? parentId, CancellationToken ct)
    {
        var chain = new List<string>();
        var seen = new HashSet<Guid>();
        var current = parentId;
        while (current is { } id && chain.Count < MaxReferences && seen.Add(id))
        {
            string? messageId;
            Guid? next;
            await using (var cmd = s.Command($"SELECT messageid, parentactivityid FROM {s.DataTable(Email)} WHERE activityid = @id"))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) break;
                messageId = rd.IsDBNull(0) ? null : rd.GetString(0);
                next = rd.IsDBNull(1) ? null : rd.GetGuid(1);
            }
            if (string.IsNullOrWhiteSpace(messageId)) break;
            chain.Add(messageId.Trim());
            current = next;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// The sender (<c>from</c>) checked again as SendEmail checked it: the policy, and either the owner of the job (ruling Z1) or a queue
    /// the owner can still read.
    /// </summary>
    /// <exception cref="MailSetupException">The sender is not allowed (any more).</exception>
    private async Task<EntityReference> DemandSenderAsync(DataSession s, AsyncOperationRun run, Guid emailId,
        IReadOnlyDictionary<string, EntityCollection> parties, CancellationToken ct)
    {
        var sender = parties.TryGetValue("from", out var from) && from.Entities.Count > 0
            ? from.Entities[0].GetAttributeValue<EntityReference>(ActivitySchema.PartyId)
            : null;
        bool queueOnly;
        try
        {
            queueOnly = await EmailSendPolicy.QueueOnlyAsync(s, ct);
        }
        catch (XrmdFaultException ex)
        {
            throw new MailSetupException(ex.Message);
        }
        if (queueOnly && sender?.LogicalName != QueueSchema.Queue) throw new MailSetupException(EmailSendPolicy.QueueOnlyRefusal(emailId, sender));
        switch (sender)
        {
            case { LogicalName: QueueSchema.Queue }:
                try
                {
                    await guard.DemandQueueReadableAsync(run.Owner, sender.Id, s, ct);
                }
                catch (XrmdFaultException)
                {
                    throw new MailSetupException(
                        $"Its sender, the queue {sender.Id}, is no longer readable by the user who sent it ({run.Owner.UserId}).");
                }
                return sender;
            case { LogicalName: "systemuser" } when sender.Id == run.Owner.UserId:
                return sender;
            default:
                throw new MailSetupException(
                    $"Its sender (from) is {(sender is null ? "empty" : $"{sender.LogicalName} {sender.Id}")}, neither the user who sent it nor a queue.");
        }
    }

    /// <summary>Why the mailbox <paramref name="m"/> of <paramref name="sender"/> cannot send.</summary>
    private static string WhyNot(MailboxState m, EntityReference sender)
    {
        var who = $"{sender.LogicalName} {sender.Id}";
        if (m.Id == Guid.Empty) return $"The sender {who} has no mailbox.";
        if (!m.Approved) return $"The email address of the mailbox of {who} is not approved by a System Administrator (emailrouteraccessapproval).";
        if (m.ProfileId is null) return $"The mailbox of {who} has no email server profile.";
        if (!m.Active) return $"The mailbox of {who} is inactive.";
        if (m.DeliveryMethod != MailSchema.DeliveryServerSide || !m.Enabled)
            return $"The mailbox of {who} is not set up to send: its outgoing delivery method must be Server-Side Synchronization and outgoing email enabled.";
        return $"The outgoing email test of the mailbox of {who} has not succeeded (outgoingemailstatus {m.OutgoingStatus}): test the mailbox first.";
    }

    private static List<MailAddress> Addresses(IReadOnlyDictionary<string, EntityCollection> parties, string list) =>
        parties.TryGetValue(list, out var collection)
            ? [.. collection.Entities.Select(p => Address(p.GetAttributeValue<string>(ActivitySchema.AddressUsed),
                p.GetAttributeValue<EntityReference>(ActivitySchema.PartyId) is { } r ? $"The recipient {r.LogicalName} {r.Id} in {list}" : $"A recipient in {list}"))]
            : [];

    /// <exception cref="MailSetupException">No address, or one that is not valid.</exception>
    private static MailAddress Address(string? address, string what)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new MailSetupException($"{what} has no email address.");
        try
        {
            return new MailAddress(address.Trim());
        }
        catch (FormatException)
        {
            throw new MailSetupException($"{what} has an email address that is not valid: '{address}'.");
        }
    }

    /// <summary>The newest send job of the email (null when there is none).</summary>
    private static async Task<Guid?> NewestJobAsync(DataSession s, Guid emailId, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit(
            "SELECT asyncoperationid FROM data.asyncoperation WHERE regardingobjectid = @id AND operationtype = @type ORDER BY createdon DESC, asyncoperationid", "1"));
        cmd.AddParameter("@id", DbType.Guid, emailId);
        cmd.AddParameter("@type", DbType.Int32, AsyncOperationQueue.EmailSend);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>True when the email is still Pending Send, read under an update lock held until commit.</summary>
    private static async Task<bool> IsPendingSendAsync(DataSession s, Guid emailId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT statecode, statuscode FROM {s.DataTable(Email)}{s.Dialect.LockHint(RowLock.UpdateRow)} WHERE activityid = @id{s.Dialect.LockClause(RowLock.UpdateRow)}");
        cmd.AddParameter("@id", DbType.Guid, emailId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) && !rd.IsDBNull(0) && rd.GetInt32(0) == 1 && !rd.IsDBNull(1) && rd.GetInt32(1) == MailSchema.EmailPendingSend;
    }

    /// <summary>
    /// Ends the email Failed (when it is still Pending Send) and, for a failure of the provider (<paramref name="mailboxId"/>), records
    /// it on the mailbox with the outgoing status Failure.
    /// </summary>
    /// <returns>The job's message.</returns>
    private async Task<string> FailAsync(AsyncOperationRun run, Guid emailId, IReadOnlyList<Guid> queueItems, Guid? mailboxId, string reason,
        CancellationToken ct, string? mailboxError = null)
    {
        await InTransactionAsync(async s =>
        {
            if (await IsPendingSendAsync(s, emailId, ct)) await FailEmailAsync(s, run, emailId, queueItems, ct);
            if (mailboxId is { } mailbox) await RecordErrorAsync(s, mailbox, mailboxError ?? reason, failed: true, ct);
            return true;
        }, ct);
        _logger.LogWarning("Email {EmailId} was not sent: {Reason}", emailId, reason);
        return Failed(emailId, reason);
    }

    /// <summary>
    /// The email, locked and still Pending Send, ends Failed, and the queue items SendEmail deactivated are made active again in the
    /// same transaction (#333).
    /// </summary>
    private async Task FailEmailAsync(DataSession s, AsyncOperationRun run, Guid emailId, IReadOnlyList<Guid> queueItems, CancellationToken ct)
    {
        await core.EmailMessages.CompleteSendAsync(s, run.Owner, emailId, sent: false, ct);
        if (queueItems.Count == 0) return;
        var reactivated = await core.QueueMessages.ReactivateItemsAsync(s, run.Owner, queueItems, ct);
        if (reactivated < queueItems.Count)
            _logger.LogDebug("Email {EmailId} failed: {Reactivated} of its {Count} queue items were made active again; the others were left inactive.",
                emailId, reactivated, queueItems.Count);
    }

    private static string Failed(Guid emailId, string reason) => $"The email {emailId} was not sent: {reason}";

    /// <summary>The mailbox's last error; with <paramref name="failed"/>, also its outgoing status Failure.</summary>
    private static async Task<bool> RecordErrorAsync(DataSession s, Guid mailbox, string error, bool failed, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET lastsyncerror = @error, lastsyncerrorcount = {s.Dialect.IsNull("lastsyncerrorcount", "0")} + 1, " +
            $"lastsyncerroroccurredon = @now{(failed ? $", outgoingemailstatus = {MailSchema.StatusFailure}" : "")} WHERE mailboxid = @id");
        cmd.AddParameter("@error", DbType.String, error.Length > MaxErrorLength ? error[..MaxErrorLength] : error, MaxErrorLength);
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@id", DbType.Guid, mailbox);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>A sent email: the mailbox's outgoing status Success, and no error counted since.</summary>
    private static async Task MailboxSuccessAsync(DataSession s, Guid mailbox, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET outgoingemailstatus = {MailSchema.StatusSuccess}, lastsyncerrorcount = 0 WHERE mailboxid = @id");
        cmd.AddParameter("@id", DbType.Guid, mailbox);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <exception cref="XrmdFaultException">400 when the data names no email.</exception>
    private static (Guid EmailId, int Attempts, IReadOnlyList<Guid> QueueItems) Parse(string? data)
    {
        try
        {
            if (data is not null && JsonNode.Parse(data) is JsonObject o && o["emailId"]?.GetValue<Guid>() is { } id && id != Guid.Empty)
                return (id, o["attempts"]?.GetValue<int>() ?? 0,
                    o["queueItems"] is JsonArray items ? [.. items.Select(i => i?.GetValue<Guid>()).OfType<Guid>()] : []);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // Not the job's data: refused below.
        }
        throw XrmdFaultException.InvalidArgument("The email send job names no email (data).");
    }

    /// <summary>Runs <paramref name="work"/> in a transaction of its own, committed when it returns.</summary>
    private Task<T> InTransactionAsync<T>(Func<DataSession, Task<T>> work, CancellationToken ct) => MailConnections.InTransactionAsync(factory, work, ct);
}
