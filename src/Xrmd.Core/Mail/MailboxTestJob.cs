using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Async;

namespace Xrmd.Core.Mail;

/// <summary>
/// Tests the outgoing configuration of one mailbox (<c>asyncoperation.operationtype</c> <see cref="AsyncOperationQueue.MailboxTest"/>,
/// plan 4.1b, task 7), queued when <c>mailbox.testemailconfigurationscheduled</c> is written true (<see cref="EnqueueIfScheduledAsync"/>;
/// only the owner of the mailbox or a System Administrator may, the guard's rule). The job runs as the user who asked for it. Data:
/// <c>{"mailboxId": "&lt;guid&gt;"}</c>.
/// </summary>
/// <remarks>
/// <para>The run checks that the owner's email address is approved, that the mailbox is active, has an address and a profile, and
/// resolves the profile and the credentials (<see cref="MailConnections"/>); then the provider of the profile checks the server and the
/// credentials without sending anything (<see cref="IMailProvider.TestAsync"/>). That a test needs the approval is from memory: checklist
/// 7. The outcome goes on the mailbox: <c>outgoingemailstatus</c> Success or Failure, <c>testmailboxaccesscompletedon</c> now,
/// <c>lastsyncerror</c> cleared or the reason (with <c>lastsyncerrorcount</c> and <c>lastsyncerroroccurredon</c>), and
/// <c>testemailconfigurationscheduled</c> back to false. A failed test is a job that succeeded with the reason as its message: the test
/// ran. Secrets never reach a log, a message, the mailbox or the job's data.</para>
/// <para>Plan 4.1c, task 9: when the mailbox's incoming delivery method is Server-Side Synchronization and it is enabled for incoming
/// email, the incoming side is tested too: the address is approved, the mailbox active with an address and a profile, the delivery user
/// exists (<see cref="Mailboxes.DeliveryUserAsync"/>), and the provider opens the inbox read-only
/// (<see cref="IIncomingMailProvider.OpenAsync"/>). The outcome goes to <c>incomingemailstatus</c>, and a success schedules the
/// mailbox's sync (<see cref="MailboxSyncJob.EnsureScheduledAsync"/>) in the same transaction. Otherwise <c>incomingemailstatus</c> is
/// left as it is (the 4.1b test).</para>
/// <para>A test sends nothing, so an interrupted one starts again (<see cref="RestartsWhenInterrupted"/>).</para>
/// </remarks>
public sealed class MailboxTestJob(IDbConnectionFactory factory, CoreOperations core, MailProviders providers, AsyncOperationQueue queue,
    ILogger<MailboxTestJob>? logger = null) : IAsyncOperationHandler
{
    private const int MaxErrorLength = 2048;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.MailboxTest;

    /// <summary>A test sends nothing: an interrupted one is run again.</summary>
    public bool RestartsWhenInterrupted => true;

    /// <summary>The job's data for the mailbox <paramref name="mailboxId"/>.</summary>
    public static string DataOf(Guid mailboxId) => new JsonObject { ["mailboxId"] = mailboxId }.ToJsonString();

    /// <summary>
    /// After a Create or an Update of <c>mailbox</c> <paramref name="id"/> wrote <paramref name="row"/>, in its transaction: when the row
    /// sets <c>testemailconfigurationscheduled</c> true, one test job owned by <paramref name="caller"/>, unless one of the mailbox is
    /// still waiting or running. The worker is woken once the transaction commits.
    /// </summary>
    internal static async Task EnqueueIfScheduledAsync(DataSession s, Caller caller, AsyncOperationQueue queue, string table, Guid id, Entity row,
        CancellationToken ct)
    {
        if (table != MailSchema.Mailbox || caller == Caller.Internal || row.GetAttributeValue<bool?>(MailSchema.TestScheduled) != true) return;
        await using (var cmd = s.Command(s.Dialect.Limit(
                         "SELECT asyncoperationid FROM data.asyncoperation WHERE regardingobjectid = @id AND operationtype = @type AND statecode <> 3", "1")))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            cmd.AddParameter("@type", DbType.Int32, AsyncOperationQueue.MailboxTest);
            if (await cmd.ExecuteScalarAsync(ct) is Guid) return;
        }
        await queue.EnqueueSystemJobAsync(s, caller, AsyncOperationQueue.MailboxTest, AsyncOperationQueue.MailboxTestMessage,
            AsyncOperationQueue.MailboxTestMessage, MailSchema.Mailbox, id, DataOf(id), ct);
        s.OnCommitted(queue.Signal);
    }

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the data names no mailbox.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var mailboxId = Parse(run.Data);

        var prepared = await MailConnections.InTransactionAsync(factory, s => PrepareAsync(s, mailboxId, ct), ct);
        if (prepared is null)
        {
            run.CompletionMessage = $"The mailbox {mailboxId} no longer exists: nothing was tested.";
            return;
        }

        var error = prepared.Error;
        if (error is null)
        {
            try
            {
                var connection = prepared.Connection!;
                await providers.For(connection.Kind).TestAsync(prepared.Address!, connection.Profile, connection.Credentials, ct);
            }
            catch (MailSendException ex)
            {
                error = ex.Message;
            }
        }

        var incoming = await MailConnections.InTransactionAsync(factory, s => PrepareIncomingAsync(s, mailboxId, ct), ct);
        var incomingError = incoming?.Error;
        if (incoming is not null && incomingError is null)
        {
            try
            {
                var connection = incoming.Connection!;
                await using var session = await providers.IncomingFor(connection.Kind).OpenAsync(incoming.Address!, connection.Profile,
                    connection.Credentials, ct);
            }
            catch (MailSendException ex)
            {
                incomingError = ex.Message;
            }
        }

        await MailConnections.InTransactionAsync(factory, s => RecordAsync(s, mailboxId, error, incoming is not null, incomingError, ct), ct);
        string outgoingText;
        if (error is null)
        {
            _logger.LogInformation("The outgoing email test of mailbox {MailboxId} succeeded.", mailboxId);
            outgoingText = $"The outgoing email test of the mailbox {mailboxId} succeeded.";
        }
        else
        {
            _logger.LogWarning("The outgoing email test of mailbox {MailboxId} failed: {Reason}", mailboxId, error);
            outgoingText = $"The outgoing email test of the mailbox {mailboxId} failed: {error}";
        }
        if (incoming is null)
        {
            run.CompletionMessage = outgoingText;
            return;
        }
        if (incomingError is null)
        {
            _logger.LogInformation("The incoming email test of mailbox {MailboxId} succeeded; its sync is scheduled.", mailboxId);
            run.CompletionMessage = $"{outgoingText} The incoming email test succeeded.";
        }
        else
        {
            _logger.LogWarning("The incoming email test of mailbox {MailboxId} failed: {Reason}", mailboxId, incomingError);
            run.CompletionMessage = $"{outgoingText} The incoming email test failed: {incomingError}";
        }
    }

    /// <summary>What the test needs: the address and the connection, or the reason it cannot run (<see cref="Error"/>).</summary>
    private sealed record Prepared(string? Address, MailConnection? Connection, string? Error);

    /// <summary>The mailbox's address and connection; null when the mailbox no longer exists.</summary>
    private async Task<Prepared?> PrepareAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        Entity row;
        try
        {
            row = await core.RetrieveAsync(s, Caller.Internal, MailSchema.Mailbox, mailboxId, new ColumnSet("regardingobjectid"), ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.ObjectDoesNotExist)
        {
            return null;
        }
        if (row.GetAttributeValue<EntityReference>("regardingobjectid") is not { } owner)
            return new Prepared(null, null, $"The mailbox {mailboxId} belongs to no user or queue.");
        var who = $"{owner.LogicalName} {owner.Id}";
        var mailbox = await Mailboxes.ResolveSenderAsync(s, owner, ct);
        if (mailbox.Id != mailboxId) return new Prepared(null, null, $"The mailbox {mailboxId} is not the mailbox of {who} (defaultmailbox).");
        if (!mailbox.Approved)
            return new Prepared(null, null, $"The email address of the mailbox of {who} is not approved by a System Administrator (emailrouteraccessapproval).");
        if (!mailbox.Active) return new Prepared(null, null, $"The mailbox of {who} is inactive.");
        if (string.IsNullOrWhiteSpace(mailbox.Address)) return new Prepared(null, null, $"The mailbox of {who} has no email address.");
        if (mailbox.ProfileId is not { } profileId) return new Prepared(null, null, $"The mailbox of {who} has no email server profile.");
        try
        {
            return new Prepared(mailbox.Address.Trim(), await MailConnections.ResolveAsync(s, core, mailboxId, profileId, who, ct), null);
        }
        catch (MailSetupException ex)
        {
            return new Prepared(null, null, ex.Message);
        }
    }

    /// <summary>
    /// The incoming side to test (plan 4.1c): null when the mailbox's incoming delivery method is not Server-Side Synchronization or it is
    /// not enabled for incoming email (or it no longer exists); else the address and the connection, or the reason it cannot be tested.
    /// </summary>
    private async Task<Prepared?> PrepareIncomingAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        if (await Mailboxes.ResolveReceiverAsync(s, mailboxId, ct) is not { } m || m.DeliveryMethod != MailSchema.DeliveryServerSide || !m.Enabled) return null;
        var who = $"{m.OwnerTable} {m.OwnerId}";
        if (!m.Approved) return new Prepared(null, null, $"The email address of the mailbox {mailboxId} is not approved by a System Administrator (emailrouteraccessapproval).");
        if (!m.Active) return new Prepared(null, null, $"The mailbox {mailboxId} is inactive.");
        if (string.IsNullOrWhiteSpace(m.Address)) return new Prepared(null, null, $"The mailbox {mailboxId} has no email address.");
        if (m.ProfileId is not { } profileId) return new Prepared(null, null, $"The mailbox {mailboxId} has no email server profile.");
        try
        {
            await Mailboxes.DeliveryUserAsync(s, m, ct);
            return new Prepared(m.Address.Trim(), await MailConnections.ResolveIncomingAsync(s, core, mailboxId, profileId, who, ct), null);
        }
        catch (MailSetupException ex)
        {
            return new Prepared(null, null, ex.Message);
        }
    }

    /// <summary>
    /// The outcome on the mailbox: the outgoing status, the incoming status when <paramref name="incomingTested"/>, the completion time, the
    /// last error (both sides' reasons), and the flag back to false. A successful incoming test schedules the mailbox's sync.
    /// </summary>
    private async Task<bool> RecordAsync(DataSession s, Guid mailboxId, string? outgoingError, bool incomingTested, string? incomingError,
        CancellationToken ct)
    {
        var error = outgoingError is null ? incomingError : incomingError is null ? outgoingError : $"{outgoingError} Incoming: {incomingError}";
        var incomingSet = incomingTested ? $"incomingemailstatus = {(incomingError is null ? MailSchema.StatusSuccess : MailSchema.StatusFailure)}, " : "";
        var outgoingStatus = outgoingError is null ? MailSchema.StatusSuccess : MailSchema.StatusFailure;
        var sql = error is null
            ? $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET outgoingemailstatus = {outgoingStatus}, {incomingSet}lastsyncerror = NULL, lastsyncerrorcount = 0, " +
              $"{MailSchema.TestCompletedOn} = @now, {MailSchema.TestScheduled} = {s.Dialect.BooleanLiteral(false)} WHERE mailboxid = @id"
            : $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET outgoingemailstatus = {outgoingStatus}, {incomingSet}lastsyncerror = @error, " +
              $"lastsyncerrorcount = {s.Dialect.IsNull("lastsyncerrorcount", "0")} + 1, lastsyncerroroccurredon = @now, " +
              $"{MailSchema.TestCompletedOn} = @now, {MailSchema.TestScheduled} = {s.Dialect.BooleanLiteral(false)} WHERE mailboxid = @id";
        await using (var cmd = s.Command(sql))
        {
            if (error is not null) cmd.AddParameter("@error", DbType.String, error.Length > MaxErrorLength ? error[..MaxErrorLength] : error, MaxErrorLength);
            cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
            cmd.AddParameter("@id", DbType.Guid, mailboxId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (incomingTested && incomingError is null) await MailboxSyncJob.EnsureScheduledAsync(s, queue, mailboxId, ct);
        return true;
    }

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
        throw XrmdFaultException.InvalidArgument("The mailbox test job names no mailbox (data).");
    }
}
