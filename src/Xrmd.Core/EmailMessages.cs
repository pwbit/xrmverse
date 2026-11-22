using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// Stage 30 of SendEmail (plan 2.11, ruling A17) and the list of the activity and note messages xrmverse refuses (A18).
/// SendEmail changes the email's state only: xrmverse has no email provider before 4.1, so nothing leaves the server.
/// </summary>
/// <remarks>
/// The write goes through the Core's Update path in the request's session (so the closed-activity rule, the party rows, the
/// <c>activitypointer</c> row and the audit run, and a later failure rolls it all back), but it is not an Update message: no
/// Update step fires, only the SendEmail steps around this stage (checklist 3h). The security rule (Write on the email) is the
/// <c>SecurityGuard</c>'s, before stage 10.
/// </remarks>
public sealed class EmailMessages
{
    /// <summary>The message name.</summary>
    public const string SendEmail = ActivitySchema.SendEmailMessage;

    /// <summary>The <c>statecode</c> and <c>statuscode</c> values SendEmail uses (Learn, email reference).</summary>
    private const int Open = 0, Completed = 1, Sent = 3, PendingSend = 6;

    private const string Email = ActivitySchema.Email;

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
    /// and have at least one To, CC or BCC party; a sender other than the caller is refused (ruling Z1). Then, in one Update: state 1 with status 3 Sent and <c>senton</c> now when
    /// <c>IssueSend</c> is false, or status 6 Pending Send with <c>senton</c> cleared when it is true (no provider: one
    /// information log line);
    /// <c>directioncode</c> true; the tracking token when one is given; and, when the email has no sender, the caller (the
    /// impersonated user under impersonation) as its <c>from</c> party. The response's <c>Subject</c> is the email's subject.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter, an email that is not open, one without recipients or
    /// one whose sender is not the caller;
    /// 404 for an unknown email; the faults of the Update.</exception>
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

        var (state, status, subject) = await LockAsync(s, id, ct);
        if (state != Open)
            throw XrmdFaultException.InvalidArgument(
                $"The email {id} cannot be sent: it is not open (statecode {state}, statuscode {status}). Only a draft or failed email is sent.");
        var parties = (await _core.Parties.ReadAsync(s, email, [id], ["from", "to", "cc", "bcc"], ct))[id];
        if (!new[] { "to", "cc", "bcc" }.Any(name => parties.TryGetValue(name, out var list) && list.Entities.Count > 0))
            throw XrmdFaultException.InvalidArgument($"The email {id} cannot be sent: it has no recipient in to, cc or bcc.");

        var update = new Entity(Email, id)
        {
            ["statecode"] = new OptionSetValue(Completed),
            ["statuscode"] = new OptionSetValue(issueSend ? PendingSend : Sent),
            ["directioncode"] = true,
        };
        if (!string.IsNullOrEmpty(token)) update["trackingtoken"] = token;
        // Pending Send clears a senton left by an earlier attempt of a Failed email (from memory: checklist 3h).
        var serverAttributes = new Dictionary<string, object?>(StringComparer.Ordinal) { ["senton"] = issueSend ? null : DateTime.UtcNow };
        var serverParties = new Dictionary<string, EntityCollection>(StringComparer.Ordinal);
        // The sender defaults to the caller (from memory: checklist 3h); it is the caller's own record, so no read check (Y2).
        if (parties.TryGetValue("from", out var from) && from.Entities.Count > 0) DemandCallerIsSender(id, from, caller);
        else
            serverParties["from"] = new EntityCollection(
                [new Entity(ActivitySchema.Party) { [ActivitySchema.PartyId] = new EntityReference("systemuser", caller.UserId) }]);

        await _core.UpdateForMessageAsync(s, caller, update, AuditActions.Update,
            new CoreOperations.ServerValues(serverAttributes, serverParties), ct);
        if (issueSend)
            _logger.LogInformation(
                "Email {EmailId} is Pending Send: no email provider is configured, so xrmverse keeps it without sending it.", id);
        return new SendEmailResponse { ResponseName = SendEmail, Results = { ["Subject"] = subject } };
    }

    /// <summary>
    /// Review I1, ruling Z1: the sender of a sent email must be the caller's own <c>systemuser</c>. Dataverse lets a user send as
    /// another user only with a send-as privilege and that user's consent, and sends from queues; neither is modelled before 4.1,
    /// so any other sender is refused rather than sent unchecked (checklist 3h: <c>prvSendAsUser</c>, the opt-in, the error).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for another user, a contact, an account or an unresolved address.</exception>
    private static void DemandCallerIsSender(Guid id, EntityCollection from, Caller caller)
    {
        foreach (var party in from.Entities)
        {
            var reference = party.GetAttributeValue<EntityReference>(ActivitySchema.PartyId);
            if (reference is { LogicalName: "systemuser" } && reference.Id == caller.UserId) continue;
            var sender = reference is null
                ? $"the address '{party.GetAttributeValue<string>(ActivitySchema.AddressUsed)}'"
                : $"{reference.LogicalName} {reference.Id}";
            throw XrmdFaultException.InvalidArgument(
                $"The email {id} cannot be sent: its sender (from) is {sender}, not the calling user. Sending on behalf of another " +
                "user, or from a sender that is not a user, is not supported by xrmverse yet (queues come with 4.1). Clear from, or " +
                "set it to the calling user.");
        }
    }

    /// <summary>The email's state, status and subject, read under an update lock held until commit.</summary>
    /// <exception cref="XrmdFaultException">404 when the email does not exist.</exception>
    private static async Task<(int? State, int? Status, string? Subject)> LockAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT statecode, statuscode, subject FROM {s.DataTable(Email)}{s.Dialect.LockHint(RowLock.UpdateRow)} WHERE activityid = @id{s.Dialect.LockClause(RowLock.UpdateRow)}");
        cmd.AddParameter("@id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(Email, id);
        return (rd.IsDBNull(0) ? null : rd.GetInt32(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetString(2));
    }
}
