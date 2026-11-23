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
/// <c>testemailconfigurationscheduled</c> back to false. The incoming side is not tested (plan 4.1c). A failed test is a job that
/// succeeded with the reason as its message: the test ran. Secrets never reach a log, a message, the mailbox or the job's data.</para>
/// <para>A test sends nothing, so an interrupted one starts again (<see cref="RestartsWhenInterrupted"/>).</para>
/// </remarks>
public sealed class MailboxTestJob(IDbConnectionFactory factory, CoreOperations core, MailProviders providers,
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

        await MailConnections.InTransactionAsync(factory, s => RecordAsync(s, mailboxId, error, ct), ct);
        if (error is null)
        {
            _logger.LogInformation("The outgoing email test of mailbox {MailboxId} succeeded.", mailboxId);
            run.CompletionMessage = $"The outgoing email test of the mailbox {mailboxId} succeeded.";
        }
        else
        {
            _logger.LogWarning("The outgoing email test of mailbox {MailboxId} failed: {Reason}", mailboxId, error);
            run.CompletionMessage = $"The outgoing email test of the mailbox {mailboxId} failed: {error}";
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

    /// <summary>The outcome on the mailbox: the outgoing status, the completion time, the last error, and the flag back to false.</summary>
    private static async Task<bool> RecordAsync(DataSession s, Guid mailboxId, string? error, CancellationToken ct)
    {
        var sql = error is null
            ? $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET outgoingemailstatus = {MailSchema.StatusSuccess}, lastsyncerror = NULL, lastsyncerrorcount = 0, " +
              $"{MailSchema.TestCompletedOn} = @now, {MailSchema.TestScheduled} = {s.Dialect.BooleanLiteral(false)} WHERE mailboxid = @id"
            : $"UPDATE {s.DataTable(MailSchema.Mailbox)} SET outgoingemailstatus = {MailSchema.StatusFailure}, lastsyncerror = @error, " +
              $"lastsyncerrorcount = {s.Dialect.IsNull("lastsyncerrorcount", "0")} + 1, lastsyncerroroccurredon = @now, " +
              $"{MailSchema.TestCompletedOn} = @now, {MailSchema.TestScheduled} = {s.Dialect.BooleanLiteral(false)} WHERE mailboxid = @id";
        await using var cmd = s.Command(sql);
        if (error is not null) cmd.AddParameter("@error", DbType.String, error.Length > MaxErrorLength ? error[..MaxErrorLength] : error, MaxErrorLength);
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        await cmd.ExecuteNonQueryAsync(ct);
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
