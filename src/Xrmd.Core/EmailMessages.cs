using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Records;
using Xrmd.Pipeline.Async;

namespace Xrmd.Core;

/// <summary>
/// Stage 30 of SendEmail (plan 2.11, ruling A17) and the list of the activity and note messages xrmverse refuses (A18).
/// SendEmail changes the email's state; with <c>IssueSend</c> true it also queues the send job (<see cref="EmailSendJob"/>, plan 4.1b),
/// which sends the email through its sender's mailbox and records the outcome (<see cref="CompleteSendAsync"/>).
/// </summary>
/// <remarks>
/// The write goes through the Core's Update path in the request's session (so the closed-activity rule, the party rows, the
/// <c>activitypointer</c> row and the audit run, and a later failure rolls it all back, the queued job included), but it is not an
/// Update message: no Update step fires, only the SendEmail steps around this stage (checklist 3h). The security rules (Write on the
/// email; a queue sender readable) are the <c>SecurityGuard</c>'s, before stage 10.
/// </remarks>
public sealed class EmailMessages
{
    /// <summary>The message name.</summary>
    public const string SendEmail = ActivitySchema.SendEmailMessage;

    /// <summary>The message that hands out the next tracking token (plan 4.1b, task 7).</summary>
    public const string GetTrackingTokenEmail = ActivitySchema.GetTrackingTokenEmailMessage;

    /// <summary>The <c>statecode</c> and <c>statuscode</c> values SendEmail uses (Learn, email reference).</summary>
    private const int Open = 0, Completed = 1;

    private const string Email = ActivitySchema.Email;

    private static readonly string[] Recipients = ["to", "cc", "bcc"];

    private readonly CoreOperations _core;
    private readonly IMetadataStore _store;
    private readonly ILogger _logger;

    internal EmailMessages(CoreOperations core, IMetadataStore store, ILogger logger)
    {
        _core = core;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// The activity and note messages a caller could reach that xrmverse refuses with 400 "not supported" (A18). The pipeline
    /// refuses them before looking up custom messages, and no custom API can take their names.
    /// </summary>
    public static IReadOnlyList<string> Refused => ActivitySchema.RefusedMessages;

    /// <summary>
    /// SendEmail with the final parameters (after the pre-operation stages): <c>EmailId</c> (required), <c>IssueSend</c>
    /// (required) and <c>TrackingToken</c> (optional). The email, locked until commit, must be open (state 0, Draft or Failed)
    /// and have at least one To, CC or BCC party. Its sender must be the caller's own user or a queue (ruling Z1; plan 4.1b: the queue
    /// readable, the guard's check); with no sender, the caller (the impersonated user under impersonation) becomes its <c>from</c> party.
    /// Then, in one Update: state 1 with status 3 Sent and <c>senton</c> now when <c>IssueSend</c> is false, or status 6 Pending Send
    /// with <c>senton</c> cleared and <c>deliveryattempts</c> 0 when it is true; <c>directioncode</c> true; the tracking token when one
    /// is given. With <c>IssueSend</c> true (plan 4.1b) also: every recipient resolved to a record unless the organization's
    /// <c>allowunresolvedpartiesonemailsend</c> is on (ruling P1, as Dataverse); under the sender policy <c>queueOnly</c>
    /// (<see cref="EmailSendPolicy"/>) a queue as the sender; with tracking tokens on (<see cref="TrackingTokens"/>), a new token when
    /// none is given, appended to the subject (a token already in the subject is kept only when it is the email's own stored
    /// <c>trackingtoken</c>); and one <see cref="EmailSendJob"/> queued in the same transaction, owned by the caller, whose data names
    /// the queue items the Update deactivates (a Failed send reactivates them, #333).
    /// The response's <c>Subject</c> is the email's subject.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter, an email that is not open, one without recipients, one whose
    /// sender is neither the caller nor a queue, an unresolved recipient, a user sender under <c>queueOnly</c>; 404 for an unknown
    /// email; the faults of the Update.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(s);
        var id = CoreOperations.GuidParameter(request, "EmailId");
        var issueSend = request.Parameters.TryGetValue("IssueSend", out var value) && value is bool flag
            ? flag
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'IssueSend' of request '{SendEmail}' is missing or not a Boolean.");
        var token = request.Parameters.TryGetValue("TrackingToken", out var t) ? t as string : null;
        var email = _store.GetEntity(Email) ?? throw new InvalidOperationException("The email table is not seeded.");

        var (state, status, subject, storedToken) = await LockAsync(s, id, ct);
        if (state != Open)
            throw XrmdFaultException.InvalidArgument(
                $"The email {id} cannot be sent: it is not open (statecode {state}, statuscode {status}). Only a draft or failed email is sent.");
        var parties = (await _core.Parties.ReadAsync(s, email, [id], ["from", .. Recipients], ct))[id];
        if (!Recipients.Any(name => parties.TryGetValue(name, out var list) && list.Entities.Count > 0))
            throw XrmdFaultException.InvalidArgument($"The email {id} cannot be sent: it has no recipient in to, cc or bcc.");

        var update = new Entity(Email, id)
        {
            ["statecode"] = new OptionSetValue(Completed),
            ["statuscode"] = new OptionSetValue(issueSend ? MailSchema.EmailPendingSend : MailSchema.EmailSent),
            ["directioncode"] = true,
        };
        // Pending Send clears a senton left by an earlier attempt of a Failed email (from memory: checklist 3h), and its attempts.
        var serverAttributes = new Dictionary<string, object?>(StringComparer.Ordinal) { ["senton"] = issueSend ? null : DateTime.UtcNow };
        var serverParties = new Dictionary<string, RecordCollection>(StringComparer.Ordinal);
        // The sender defaults to the caller (from memory: checklist 3h); it is the caller's own record, so no read check (Y2).
        EntityReference? sender = null;
        if (parties.TryGetValue("from", out var from) && from.Entities.Count > 0) sender = DemandSender(id, from, caller);
        else
            serverParties["from"] = new RecordCollection
            {
                Records = [new Record(ActivitySchema.Party) { [ActivitySchema.PartyId] = new RecordReference { LogicalName = "systemuser", Id = caller.UserId } }],
            };

        IReadOnlyList<Guid> queueItems = [];
        if (issueSend)
        {
            await DemandResolvedRecipientsAsync(s, id, parties, ct);
            if (await EmailSendPolicy.QueueOnlyAsync(s, ct) && sender?.LogicalName != QueueSchema.Queue)
                throw XrmdFaultException.InvalidArgument(EmailSendPolicy.QueueOnlyRefusal(id, sender));
            serverAttributes["deliveryattempts"] = 0;
            // Tracking tokens on: a token when none is given, and the token at the end of the subject (from memory: checklist 7). An
            // email sent again after a failure whose subject still carries its own stored token keeps it: no second token is taken or
            // appended (final review M2). Any other token in the subject (another email's, quoted by a reply) is never reused: the
            // email gets a new one (#333; whether Dataverse reuses a token is from memory: checklist 7).
            string? tracked;
            var appended = true;
            if (string.IsNullOrEmpty(token))
            {
                tracked = null;
                if (await TrackingTokens.FormatAsync(s, ct) is { } format)
                {
                    if (!string.IsNullOrEmpty(storedToken)
                        && TrackingTokens.FindAllInSubject(subject, format.Prefix, format.Digits).Contains(storedToken, StringComparer.Ordinal))
                    {
                        tracked = storedToken;
                        appended = false;
                    }
                    else tracked = await TrackingTokens.NextAsync(s, ct);
                }
            }
            else
            {
                tracked = await TrackingTokens.EnabledAsync(s, ct) ? token : null;
                appended = subject is null || !subject.Contains(token, StringComparison.Ordinal);
            }
            if (tracked is not null)
            {
                token = tracked;
                if (appended) serverAttributes["subject"] = subject = WithToken(email, id, subject, tracked);
            }
            // #333: the items the Update below deactivates go in the send job's data, so that a Failed send puts them back.
            queueItems = await QueueMessages.ActiveItemIdsAsync(s, id, ct);
        }
        if (!string.IsNullOrEmpty(token)) update["trackingtoken"] = token;

        await _core.UpdateForMessageAsync(s, caller, update, AuditActions.Update,
            new CoreOperations.ServerValues(serverAttributes, serverParties), ct);
        if (issueSend)
        {
            var queue = _core.JobQueue;
            await queue.EnqueueSystemJobAsync(s, caller, AsyncOperationQueue.EmailSend, AsyncOperationQueue.EmailSendMessage,
                AsyncOperationQueue.EmailSendMessage, Email, id, EmailSendJob.DataOf(id, queueItems: queueItems), ct);
            s.OnCommitted(queue.Signal);
            _logger.LogDebug("Email {EmailId} is Pending Send: its send job is queued.", id);
        }
        return new SendEmailResponse { ResponseName = SendEmail, Results = { ["Subject"] = subject } };
    }

    /// <summary>
    /// GetTrackingTokenEmail (plan 4.1b, task 7): the next tracking token of the organization (<see cref="TrackingTokens.NextAsync"/>, the
    /// same generator, counter and lock as SendEmail's), taken in the request's transaction; the response's <c>TrackingToken</c>. The
    /// optional <c>Subject</c> (Learn: "the context of the email") does not change the token.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when tracking tokens are off (no <c>trackingprefix</c>), a <c>Subject</c> that is not a
    /// string, or no token left.</exception>
    public static async Task<OrganizationResponse> GetTrackingTokenAsync(OrganizationRequest request, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(s);
        if (request.Parameters.TryGetValue("Subject", out var subject) && subject is not (null or string))
            throw XrmdFaultException.InvalidArgument($"The parameter 'Subject' of request '{GetTrackingTokenEmail}' must be a string.");
        var token = await TrackingTokens.NextAsync(s, ct)
                    ?? throw XrmdFaultException.InvalidArgument(
                        "Tracking tokens are off in this organization: it has no trackingprefix. Set organization.trackingprefix to turn them on.");
        return new GetTrackingTokenEmailResponse { ResponseName = GetTrackingTokenEmail, Results = { ["TrackingToken"] = token } };
    }

    /// <summary>
    /// The outcome of the send job (plan 4.1b), in one Update through the same path as SendEmail (no Update step fires): state 1
    /// status 3 Sent with <c>senton</c> now, or state 0 status 8 Failed. The caller locked the email and checked it is still Pending Send.
    /// </summary>
    internal Task CompleteSendAsync(DataSession s, Caller caller, Guid id, bool sent, CancellationToken ct)
    {
        var update = new Entity(Email, id)
        {
            ["statecode"] = new OptionSetValue(sent ? Completed : Open),
            ["statuscode"] = new OptionSetValue(sent ? MailSchema.EmailSent : MailSchema.EmailFailed),
        };
        var server = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (sent) server["senton"] = DateTime.UtcNow;
        return _core.UpdateForMessageAsync(s, caller, update, AuditActions.Update,
            new CoreOperations.ServerValues(server, new Dictionary<string, RecordCollection>()), ct);
    }

    /// <summary>
    /// Review I1, ruling Z1, extended by plan 4.1b: the sender of a sent email is the caller's own <c>systemuser</c> or a queue (whose
    /// read the guard checks). Dataverse lets a user send as another user only with a send-as privilege and that user's consent, which is
    /// not modelled, so any other sender is refused rather than sent unchecked (checklist 3h: <c>prvSendAsUser</c>, the opt-in, the error).
    /// </summary>
    /// <returns>The sender.</returns>
    /// <exception cref="XrmdFaultException">400 for another user, a contact, an account or an unresolved address.</exception>
    private static EntityReference DemandSender(Guid id, EntityCollection from, Caller caller)
    {
        var party = from.Entities[0]; // one at most (the validator)
        var reference = party.GetAttributeValue<EntityReference>(ActivitySchema.PartyId);
        if (reference is { LogicalName: "systemuser" } && reference.Id == caller.UserId) return reference;
        if (reference is { LogicalName: QueueSchema.Queue }) return reference;
        var sender = reference is null
            ? $"the address '{party.GetAttributeValue<string>(ActivitySchema.AddressUsed)}'"
            : $"{reference.LogicalName} {reference.Id}";
        throw XrmdFaultException.InvalidArgument(
            $"The email {id} cannot be sent: its sender (from) is {sender}, neither the calling user nor a queue. Sending on behalf of " +
            "another user, or from a sender that is not a user or a queue, is not supported by xrmverse. Clear from, or set it to the " +
            "calling user or a queue.");
    }

    /// <summary>
    /// Ruling P1 (as Dataverse): with the organization's <c>allowunresolvedpartiesonemailsend</c> off (the default), every to, cc and
    /// bcc party of an email that is sent names a record; an address alone is refused.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the first unresolved address and the setting.</exception>
    private static async Task DemandResolvedRecipientsAsync(DataSession s, Guid id, IReadOnlyDictionary<string, EntityCollection> parties,
        CancellationToken ct)
    {
        var unresolved = Recipients
            .SelectMany(name => parties.TryGetValue(name, out var list) ? list.Entities.Select(p => (Name: name, Party: p)) : [])
            .FirstOrDefault(p => p.Party.GetAttributeValue<EntityReference>(ActivitySchema.PartyId) is null);
        if (unresolved.Party is null) return;
        await using var cmd = s.Command($"SELECT allowunresolvedpartiesonemailsend FROM {s.DataTable("organization")}");
        if (await cmd.ExecuteScalarAsync(ct) is { } allowed and not DBNull && Convert.ToBoolean(allowed)) return;
        throw XrmdFaultException.InvalidArgument(
            $"The email {id} cannot be sent: the recipient '{unresolved.Party.GetAttributeValue<string>(ActivitySchema.AddressUsed)}' in " +
            $"{unresolved.Name} is not resolved to a record, and the organization does not allow unresolved recipients " +
            "(allowunresolvedpartiesonemailsend). Resolve it to a contact, an account, a user or a queue, or turn the setting on.");
    }

    /// <summary>The subject with the tracking token appended, refused when it would not fit.</summary>
    /// <exception cref="XrmdFaultException">400 when the subject and the token exceed the column's MaxLength.</exception>
    private static string WithToken(EntityMetadata email, Guid id, string? subject, string token)
    {
        var result = string.IsNullOrEmpty(subject) ? token : subject + " " + token;
        var max = (email.Attributes ?? []).OfType<StringAttributeMetadata>().FirstOrDefault(a => a.LogicalName == "subject")?.MaxLength;
        if (max is { } limit && result.Length > limit)
            throw XrmdFaultException.InvalidArgument(
                $"The email {id} cannot be sent: its subject with the tracking token '{token}' is {result.Length} characters long, more than " +
                $"the subject's MaxLength {limit}. Shorten the subject.");
        return result;
    }

    /// <summary>The email's state, status, subject and stored tracking token, read under an update lock held until commit.</summary>
    /// <exception cref="XrmdFaultException">404 when the email does not exist.</exception>
    private static async Task<(int? State, int? Status, string? Subject, string? TrackingToken)> LockAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT statecode, statuscode, subject, trackingtoken FROM {s.DataTable(Email)}{s.Dialect.LockHint(RowLock.UpdateRow)} WHERE activityid = @id{s.Dialect.LockClause(RowLock.UpdateRow)}");
        cmd.AddParameter("@id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(Email, id);
        return (rd.IsDBNull(0) ? null : rd.GetInt32(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3));
    }
}
