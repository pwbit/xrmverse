using System.Data;
using System.Data.Common;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Mail;

/// <summary>A reason a mailbox cannot send or be tested (its setup, its profile, its credentials): no provider is called.</summary>
/// <remarks>The message never carries a secret; it becomes the email's failure text, the job's message or the mailbox's last error.</remarks>
internal sealed class MailSetupException(string message) : Exception(message);

/// <summary>How a mailbox reaches its server: the provider, the profile's plain settings and the credentials.</summary>
internal sealed record MailConnection(MailProviderKind Kind, MailProfile Profile, MailCredentials Credentials);

/// <summary>
/// The connection of a mailbox (plan 4.1b), shared by the send job (<see cref="EmailSendJob"/>) and the test job
/// (<see cref="MailboxTestJob"/>): its email server profile, which must exist, be active and be one xrmverse accepts
/// (<see cref="ProfileRules"/>), and the credentials (<see cref="MailSecrets"/>): the profile's client secret for Graph, the profile's
/// user name and password when the credentials are in the profile, else the mailbox's.
/// </summary>
internal static class MailConnections
{
    /// <summary>The connection of the mailbox <paramref name="mailboxId"/> on the profile <paramref name="profileId"/>, for sending.</summary>
    /// <param name="owner">The mailbox's owner as the texts name it (<c>systemuser {id}</c>).</param>
    /// <remarks>
    /// With <c>usesamesettingsforoutgoingconnections</c> true on an SMTP profile whose incoming server is set, the outgoing credentials are
    /// the incoming ones (plan 4.1c): the credential retrieval, user name and <c>incomingpassword</c>. A profile without an incoming server
    /// keeps its own outgoing settings (the flag defaults to true, so it cannot tell on its own).
    /// </remarks>
    /// <exception cref="MailSetupException">The profile is missing, inactive or not accepted.</exception>
    public static async Task<MailConnection> ResolveAsync(DataSession s, CoreOperations core, Guid mailboxId, Guid profileId, string owner,
        CancellationToken ct)
    {
        var (row, kind) = await LoadAsync(s, core, mailboxId, profileId, owner, ct);
        var profile = new MailProfile(row.GetAttributeValue<string>("outgoingserverlocation"), row.GetAttributeValue<int?>("outgoingportnumber"),
            row.GetAttributeValue<bool?>("outgoingusessl") ?? false, row.GetAttributeValue<OptionSetValue>("outgoingauthenticationprotocol")?.Value,
            row.GetAttributeValue<string>("exchangeonlinetenantid"), row.GetAttributeValue<string>("oauthclientid"),
            row.GetAttributeValue<OptionSetValue>("serverauthority")?.Value ?? MailSchema.AuthorityPublic);
        MailCredentials credentials;
        var sameSettings = row.GetAttributeValue<bool?>("usesamesettingsforoutgoingconnections") == true
                           && !string.IsNullOrWhiteSpace(row.GetAttributeValue<string>("incomingserverlocation"));
        if (kind == MailProviderKind.Graph)
            credentials = new MailCredentials(null, null, await MailSecrets.ReadAsync(s, row.Id, "oauthclientsecret", ct));
        else if (sameSettings)
            credentials = await ImapCredentialsAsync(s, row, mailboxId, ct);
        else if (row.GetAttributeValue<OptionSetValue>("outgoingcredentialretrieval")?.Value == MailSchema.CredentialsInProfile)
            credentials = new MailCredentials(row.GetAttributeValue<string>("outgoingusername"), await MailSecrets.ReadAsync(s, row.Id, "outgoingpassword", ct), null);
        else
            credentials = new MailCredentials(await MailboxUserNameAsync(s, mailboxId, ct), await MailSecrets.ReadAsync(s, mailboxId, "password", ct), null);
        return new MailConnection(kind, profile, credentials);
    }

    /// <summary>
    /// The connection of the mailbox <paramref name="mailboxId"/> on the profile <paramref name="profileId"/>, for receiving (plan 4.1c): the
    /// profile's <c>incomingserverlocation</c>, <c>incomingportnumber</c> and <c>incomingusessl</c> (true when unset), and the credentials by the
    /// incoming retrieval (the profile's <c>incomingusername</c> and <c>incomingpassword</c>, or the mailbox's); a Graph profile uses the client secret.
    /// </summary>
    /// <exception cref="MailSetupException">The profile is missing, inactive or not accepted.</exception>
    public static async Task<MailConnection> ResolveIncomingAsync(DataSession s, CoreOperations core, Guid mailboxId, Guid profileId, string owner,
        CancellationToken ct)
    {
        var (row, kind) = await LoadAsync(s, core, mailboxId, profileId, owner, ct);
        var profile = new MailProfile(row.GetAttributeValue<string>("incomingserverlocation"), row.GetAttributeValue<int?>("incomingportnumber"),
            row.GetAttributeValue<bool?>("incomingusessl") ?? true, row.GetAttributeValue<OptionSetValue>("incomingauthenticationprotocol")?.Value,
            row.GetAttributeValue<string>("exchangeonlinetenantid"), row.GetAttributeValue<string>("oauthclientid"),
            row.GetAttributeValue<OptionSetValue>("serverauthority")?.Value ?? MailSchema.AuthorityPublic);
        var credentials = kind == MailProviderKind.Graph
            ? new MailCredentials(null, null, await MailSecrets.ReadAsync(s, row.Id, "oauthclientsecret", ct))
            : await ImapCredentialsAsync(s, row, mailboxId, ct);
        return new MailConnection(kind, profile, credentials);
    }

    /// <summary>The credentials of the incoming side of an SMTP/IMAP profile: in the profile, or the mailbox's.</summary>
    private static async Task<MailCredentials> ImapCredentialsAsync(DataSession s, Entity row, Guid mailboxId, CancellationToken ct) =>
        row.GetAttributeValue<OptionSetValue>("incomingcredentialretrieval")?.Value == MailSchema.CredentialsInProfile
            ? new MailCredentials(row.GetAttributeValue<string>("incomingusername"), await MailSecrets.ReadAsync(s, row.Id, "incomingpassword", ct), null)
            : new MailCredentials(await MailboxUserNameAsync(s, mailboxId, ct), await MailSecrets.ReadAsync(s, mailboxId, "password", ct), null);

    private static async Task<(Entity Row, MailProviderKind Kind)> LoadAsync(DataSession s, CoreOperations core, Guid mailboxId, Guid profileId,
        string owner, CancellationToken ct)
    {
        Entity row;
        try
        {
            row = await core.RetrieveAsync(s, Caller.Internal, MailSchema.EmailServerProfile, profileId, new ColumnSet(
                "name", "servertype", "outgoingserverlocation", "outgoingportnumber", "outgoingusessl", "outgoingusername",
                "outgoingauthenticationprotocol", "outgoingcredentialretrieval", "serverauthority", "exchangeonlinetenantid", "oauthclientid",
                "incomingserverlocation", "incomingportnumber", "incomingusessl", "incomingusername", "incomingauthenticationprotocol",
                "incomingcredentialretrieval", "usesamesettingsforoutgoingconnections", "statecode"), ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.ObjectDoesNotExist)
        {
            throw new MailSetupException($"The email server profile {profileId} of the mailbox of {owner} does not exist.");
        }
        if (row.GetAttributeValue<OptionSetValue>("statecode")?.Value is { } profileState and not 0)
            throw new MailSetupException($"The email server profile '{row.GetAttributeValue<string>("name")}' is inactive (statecode {profileState}).");
        try
        {
            return (row, ProfileRules.Validate(row));
        }
        catch (XrmdFaultException ex)
        {
            throw new MailSetupException(ex.Message);
        }
    }

    private static async Task<string?> MailboxUserNameAsync(DataSession s, Guid mailbox, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT username FROM {s.DataTable(MailSchema.Mailbox)} WHERE mailboxid = @id");
        cmd.AddParameter("@id", DbType.Guid, mailbox);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Runs <paramref name="work"/> in a transaction of its own on <paramref name="factory"/>, committed when it returns.</summary>
    public static async Task<T> InTransactionAsync<T>(IDbConnectionFactory factory, Func<DataSession, Task<T>> work, CancellationToken ct)
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
        catch
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
            throw;
        }
    }
}
