using System.Globalization;
using System.Text;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Security;

namespace Xrmd.Core.Mail;

/// <summary>What the import of one incoming message did.</summary>
public enum ImportOutcome
{
    /// <summary>The message became a received email (and, for a queue mailbox, a queue item).</summary>
    Created,

    /// <summary>Its key was registered already (imported by any mailbox, or sent from here): nothing was written (ruling PF1).</summary>
    Duplicate,

    /// <summary>The mailbox's incoming email filtering did not admit it: nothing was written.</summary>
    Filtered,
}

/// <summary>
/// The import of an incoming message failed for a reason of its own that importing it again does not change (plan 4.1c, ruling PF11): a
/// plug-in or a rule of its pipeline refused it, or an earlier email it correlates with holds data that cannot be resolved (ruling T8-a).
/// The sync job counts it against the message. <see cref="Exception.InnerException"/> is the cause.
/// </summary>
public sealed class IncomingImportException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Imports one incoming message (plan 4.1c, task 8) in one transaction of its own, through the pipeline, as the delivery user
/// (<see cref="Mailboxes.DeliveryUserAsync"/>, the contract for the contact center): read, correlate (<see cref="EmailCorrelator"/>),
/// filter (<see cref="IncomingFilter"/>), then register the message's key (<see cref="MailMessageKeys"/>). A key registered already is a
/// <see cref="ImportOutcome.Duplicate"/> and nothing else happens (ruling PF1: a message imported by another mailbox, or sent from
/// here, reaches no queue). A new key becomes the received email, its attachments (a blocked or oversized one is left out and a note on
/// the email names it: an xrmverse extension) and, for a queue mailbox, AddToQueue, whose nested <c>queueitem</c> Create runs through the
/// pipeline (ruling PF2: never a <c>queueitem</c> Update).
/// </summary>
/// <remarks>
/// <para>Every request runs at depth 1 on the import's session as the delivery user (<c>InitiatingUserId</c> too), with the server shared
/// variable <see cref="MailSchema.MailboxDeliveryVariable"/> = the mailbox id (<c>D</c> format); the email's owner is the mailbox's owner.
/// The email's <c>senton</c> (the Date header, else the time the server received it), <c>correlationmethod</c> and <c>emailsender</c> are
/// server values (<see cref="InvocationContext.ServerSetValues"/>). A regarding record or parent email the delivery user may not append
/// to is left out, so the delivery is not refused for it.</para>
/// <para>Nothing is committed when the import fails. A failure raised by the import's own pipeline (a plug-in, a validation, a privilege)
/// is thrown as <see cref="IncomingImportException"/>; any other exception (the database, a cancellation, a setup error such as an
/// unknown filtering method) propagates unchanged.</para>
/// </remarks>
public sealed class IncomingEmailImporter(IDbConnectionFactory factory, PipelineExecutor pipeline, IMetadataStore metadata, SecurityGuard guard,
    ILogger<IncomingEmailImporter>? logger = null)
{
    private const string Email = ActivitySchema.Email;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Imports <paramref name="message"/>, listed as <paramref name="item"/>, into <paramref name="mailbox"/> as <paramref name="deliveryUser"/>.</summary>
    /// <exception cref="IncomingImportException">The import's pipeline refused the message, or its correlation data cannot be resolved.</exception>
    public async Task<ImportOutcome> ImportAsync(MailboxReceiver mailbox, Caller deliveryUser, IncomingRef item, MimeMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(deliveryUser);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(message);
        var mail = IncomingMessageReader.Read(message);
        await using var conn = await factory.OpenAsync(ct);
        var s = new DataSession(conn, null);
        await pipeline.BeginTransactionAsync(s, ct);
        ImportOutcome outcome;
        try
        {
            outcome = await ImportAsync(s, mailbox, deliveryUser, item, message, mail, ct);
        }
        catch
        {
            await RollbackAsync(s);
            throw;
        }

        if (outcome != ImportOutcome.Created)
        {
            await RollbackAsync(s);
            _logger.LogDebug("Incoming message {ServerId} of mailbox {MailboxId}: {Outcome}.", item.ServerId, mailbox.Id, outcome);
            return outcome;
        }
        try
        {
            // A nested call that failed and was swallowed dooms the transaction: the commit refuses it (and rolls it back).
            await pipeline.CommitTransactionAsync(s);
        }
        catch (XrmdFaultException ex)
        {
            throw Failed(mailbox, item, ex);
        }
        _logger.LogInformation("Incoming message {ServerId} of mailbox {MailboxId} imported.", item.ServerId, mailbox.Id);
        return outcome;
    }

    private async Task RollbackAsync(DataSession s)
    {
        try
        {
            await pipeline.RollbackTransactionAsync(s);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection is closed with the transaction; the failure that led here is the one to report.
            _logger.LogWarning(ex, "The rollback of an incoming message's import failed.");
        }
    }

    private static IncomingImportException Failed(MailboxReceiver mailbox, IncomingRef item, Exception cause) =>
        new($"The incoming message {item.ServerId}{(item.MessageId is { } id ? $" ({id})" : "")} of the mailbox {mailbox.Id} cannot be imported: {cause.Message}",
            cause);

    private async Task<ImportOutcome> ImportAsync(DataSession s, MailboxReceiver mailbox, Caller user, IncomingRef item, MimeMessage message,
        IncomingMail mail, CancellationToken ct)
    {
        Correlation correlation;
        try
        {
            correlation = await EmailCorrelator.CorrelateAsync(s, metadata, message, item.ConversationIndex, ct);
        }
        catch (UnresolvedRegardingException ex)
        {
            throw Failed(mailbox, item, ex);
        }

        var addresses = new[] { mail.FromAddress }.Concat(mail.To).Concat(mail.Cc).Concat(mail.Bcc);
        var matches = await IncomingParties.ResolveAsync(s, metadata, addresses, ct);
        AddressMatch Match(string address) => matches.TryGetValue(address.Trim(), out var m) ? m : new AddressMatch(address, []);
        var from = Match(mail.FromAddress);
        if (!IncomingFilter.Admits(mailbox.Filter, from, correlation.Method != MailSchema.CorrelationNone)) return ImportOutcome.Filtered;

        // Ruling PF5: the MIME Message-ID first, then the one the server reports; with neither, the message's content (ruling PF3).
        var normalized = MailMessageKeys.Normalize(message.MessageId) ?? MailMessageKeys.Normalize(item.MessageId);
        var key = normalized is not null
            ? MailMessageKeys.KeyOf(normalized)
            : MailMessageKeys.KeyOf(mail.SentOn, mail.FromAddress, mail.To, mail.Subject, Encoding.UTF8.GetByteCount(mail.HtmlBody ?? ""));
        var emailId = Guid.NewGuid();
        if (await MailMessageKeys.TryRegisterAsync(s, key, emailId, incoming: true, ct) is not null) return ImportOutcome.Duplicate;

        var shared = new Dictionary<string, object> { [MailSchema.MailboxDeliveryVariable] = mailbox.Id.ToString("D") };
        var correlationId = Guid.NewGuid();
        Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, IReadOnlyDictionary<string, object?>? setValues = null) =>
            RunAsync(mailbox, item, request, new InvocationContext(user, user.UserId, 1, null, s, correlationId)
            {
                ServerSharedVariables = shared, ServerSetValues = setValues,
            }, ct);

        var table = metadata.GetEntity(s, Email) ?? throw new InvalidOperationException("The email table is missing.");
        var (fromList, to, cc, bcc, sender, ambiguous) = IncomingParties.Build(from,
            [.. mail.To.Select(Match)], [.. mail.Cc.Select(Match)], [.. mail.Bcc.Select(Match)]);
        // A party the delivery user may not read keeps its address only, so the delivery is not refused for it (ruling Y2 of the Create).
        var readable = new Dictionary<(string, Guid), bool>();
        foreach (var party in new[] { fromList, to, cc, bcc }.SelectMany(l => l.Entities))
            if (party.GetAttributeValue<EntityReference>(ActivitySchema.PartyId) is { } record
                && !await CanAccessAsync(s, user, record, AccessRights.ReadAccess, readable, ct))
                party[ActivitySchema.PartyId] = null;
        if (sender is not null && !readable.GetValueOrDefault((sender.LogicalName, sender.Id))) sender = null;
        var email = new Entity(Email, emailId)
        {
            ["directioncode"] = false,
            ["statecode"] = new OptionSetValue(1),
            ["statuscode"] = new OptionSetValue(QueueSchema.ReceivedEmailStatus),
            ["subject"] = Fit(table, "subject", mail.Subject),
            ["description"] = Fit(table, "description", mail.HtmlBody),
            ["to"] = to,
            ["cc"] = cc,
            ["bcc"] = bcc,
            ["messageid"] = Stored(table, "messageid", normalized),
            ["inreplyto"] = Fit(table, "inreplyto", Bracketed(message.InReplyTo)),
            ["conversationindex"] = Fit(table, "conversationindex", item.ConversationIndex ?? message.Headers["Thread-Index"]),
            ["sender"] = Fit(table, "sender", mail.FromAddress),
            ["torecipients"] = Fit(table, "torecipients", string.Join(';', mail.To)),
            ["isduplicatesenderunresolved"] = ambiguous,
            ["ownerid"] = new EntityReference(mailbox.OwnerTable, mailbox.OwnerId),
        };
        if (mail.FromAddress.Length > 0) email["from"] = fromList;
        var appendable = new Dictionary<(string, Guid), bool>();
        if (correlation.Regarding is { } regarding && await CanAccessAsync(s, user, regarding, AccessRights.AppendToAccess, appendable, ct))
            email["regardingobjectid"] = regarding;
        if (correlation.ParentEmailId is { } parentId
            && await CanAccessAsync(s, user, new EntityReference(Email, parentId), AccessRights.AppendToAccess, appendable, ct))
            email["parentactivityid"] = new EntityReference(Email, parentId);
        // Final review M5: with neither the parent nor the regarding record kept, the email is linked to nothing: method None.
        var method = email.Contains("parentactivityid") || email.Contains("regardingobjectid") ? correlation.Method : MailSchema.CorrelationNone;

        var serverValues = new Dictionary<string, object?>
        {
            ["senton"] = mail.SentOn ?? DateTime.SpecifyKind(item.ReceivedOn, DateTimeKind.Utc),
            ["correlationmethod"] = new OptionSetValue(method),
        };
        if (sender is not null) serverValues["emailsender"] = sender;
        await ExecuteAsync(new CreateRequest { Target = email }, serverValues);

        var target = new EntityReference(Email, emailId);
        await AttachAsync(s, target, mail.Attachments, request => ExecuteAsync(request), ct);

        if (mailbox.QueueId is { } queue)
            await ExecuteAsync(new AddToQueueRequest { Target = target, DestinationQueueId = queue });
        return ImportOutcome.Created;
    }

    /// <summary>Runs one request of the import; a failure the pipeline raises (<see cref="XrmdFaultException"/>) becomes an <see cref="IncomingImportException"/>.</summary>
    private async Task<OrganizationResponse> RunAsync(MailboxReceiver mailbox, IncomingRef item, OrganizationRequest request, InvocationContext invocation,
        CancellationToken ct)
    {
        try
        {
            return await pipeline.ExecuteAsync(request, invocation, ct);
        }
        catch (XrmdFaultException ex)
        {
            throw Failed(mailbox, item, ex);
        }
    }

    /// <summary>
    /// One <c>activitymimeattachment</c> Create per admitted attachment; one that is blocked (<c>organization.blockedattachments</c>) or
    /// larger than <c>organization.maxuploadfilesize</c> is not stored, and one note on the email names each such file and why.
    /// </summary>
    private async Task AttachAsync(DataSession s, EntityReference email, IReadOnlyList<IncomingAttachment> attachments,
        Func<OrganizationRequest, Task<OrganizationResponse>> execute, CancellationToken ct)
    {
        if (attachments.Count == 0) return;
        var attachmentTable = metadata.GetEntity(s, MailSchema.Attachment) ?? throw new InvalidOperationException("The email attachment table is missing.");
        var maxSize = await CoreOperations.MaxUploadFileSizeAsync(s, ct);
        var skipped = new List<string>();
        using (EmailAttachments.BeginImport(s, email.Id))
        {
            foreach (var attachment in attachments)
            {
                var fileName = Fit(attachmentTable, ActivitySchema.FileName, attachment.FileName)!;
                if (await EmailAttachments.BlockedExtensionAsync(s, fileName, ct) is { } extension)
                {
                    skipped.Add($"'{fileName}': files of the type '{extension}' are blocked by the organization (blockedattachments).");
                    continue;
                }
                if (attachment.Body.LongLength > maxSize)
                {
                    skipped.Add(string.Create(CultureInfo.InvariantCulture,
                        $"'{fileName}': it is {attachment.Body.LongLength} bytes long, more than the organization's maximum upload file size of {maxSize} bytes (maxuploadfilesize)."));
                    continue;
                }
                await execute(new CreateRequest
                {
                    Target = new Entity(MailSchema.Attachment)
                    {
                        [ActivitySchema.ObjectId] = email,
                        [ActivitySchema.FileName] = fileName,
                        [ActivitySchema.MimeType] = Fit(attachmentTable, ActivitySchema.MimeType, attachment.MimeType),
                        [MailSchema.AttachmentBody] = Convert.ToBase64String(attachment.Body),
                    },
                });
            }
        }
        if (skipped.Count == 0) return;
        await execute(new CreateRequest
        {
            Target = new Entity(ActivitySchema.Annotation)
            {
                [ActivitySchema.ObjectId] = email,
                ["subject"] = "Attachments not imported",
                [ActivitySchema.NoteText] = "These attachments of the incoming message were not imported:\n" + string.Join('\n', skipped),
            },
        });
    }

    /// <summary>
    /// Whether <paramref name="user"/> holds <paramref name="right"/> on <paramref name="record"/> (the guard's rule for a party or a lookup
    /// the email's Create sets), remembered in <paramref name="known"/>; a record that no longer exists counts as no.
    /// </summary>
    private async Task<bool> CanAccessAsync(DataSession s, Caller user, EntityReference record, AccessRights right,
        Dictionary<(string, Guid), bool> known, CancellationToken ct)
    {
        if (known.TryGetValue((record.LogicalName, record.Id), out var held)) return held;
        try
        {
            await guard.DemandAccessAsync(user, record, right, s, ct);
            held = true;
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus is 403 or 404)
        {
            _logger.LogDebug("The delivery user {UserId} lacks {Right} on {Table} {Id}: the incoming email leaves it out.", user.UserId, right,
                record.LogicalName, record.Id);
            held = false;
        }
        known[(record.LogicalName, record.Id)] = held;
        return held;
    }

    /// <summary>A <c>Message-ID</c> as MimeKit reads it (no angle brackets) in its header form; null when there is none.</summary>
    private static string? Bracketed(string? id) => string.IsNullOrWhiteSpace(id) ? null : $"<{id.Trim()}>";

    /// <summary>
    /// Final review M1: the normalized <c>Message-ID</c> that made the key (the MIME one, else the server's), in its header form with angle
    /// brackets, when it fits the string column <paramref name="column"/>; null otherwise (a longer id is never stored cut, and a message
    /// keyed by its content has none).
    /// </summary>
    private static string? Stored(EntityMetadata table, string column, string? normalized) =>
        normalized is null || Bracketed(normalized) is not { } value || Fit(table, column, value) != value ? null : value;

    /// <summary><paramref name="value"/> cut to the length of the string column <paramref name="column"/> of <paramref name="table"/>.</summary>
    private static string? Fit(EntityMetadata table, string column, string? value)
    {
        if (value is null) return null;
        var max = table.Attributes?.FirstOrDefault(a => a.LogicalName == column) switch
        {
            StringAttributeMetadata { MaxLength: { } length } => length,
            MemoAttributeMetadata { MaxLength: { } length } => length,
            _ => int.MaxValue,
        };
        return value.Length <= max ? value : value[..max];
    }
}
