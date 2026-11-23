using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Mail;

/// <summary>How an accepted email server profile sends: over SMTP, or through Microsoft Graph.</summary>
public enum MailProviderKind
{
    /// <summary>IMAP/SMTP profile (server type 4) with Basic authentication.</summary>
    Smtp,

    /// <summary>Exchange Online profile (server type 0) with OAuth through Microsoft Entra ID.</summary>
    Graph,
}

/// <summary>
/// The profiles xrmverse accepts (plan 4.1b): SMTP is server type 4 with outgoing protocol Basic (Auto Detect is stored as Basic)
/// and credentials by a user or queue, or in the profile; Graph is server type 0 with outgoing protocol OAuth, credentials through
/// Microsoft Entra ID and a Public or Automatic authority. Anything else is a 400 that names the value.
/// </summary>
/// <remarks>That Exchange Online is server type 0 is from memory, not from Learn (the Learn pages list the values only): checklist 2.</remarks>
public static class ProfileRules
{
    /// <summary>The columns the rules read.</summary>
    public static readonly IReadOnlyList<string> Columns =
        ["servertype", "outgoingauthenticationprotocol", "outgoingcredentialretrieval", "serverauthority"];

    private static readonly Dictionary<int, string> ServerTypes = new()
    {
        [0] = "Exchange Server", [1] = "Other (POP3/SMTP)", [2] = "Exchange Server (Hybrid)", [3] = "Exchange Online (Hybrid)", [4] = "IMAP/SMTP",
    };

    private static readonly Dictionary<int, string> Protocols = new()
    {
        [0] = "Auto Detect", [1] = "Negotiate", [2] = "NTLM", [3] = "Basic", [4] = "OAuth",
    };

    private static readonly Dictionary<int, string> Retrievals = new()
    {
        [0] = "by a user or queue", [1] = "in the profile", [2] = "server to server", [3] = "Windows", [4] = "anonymous",
        [5] = "Gmail OAuth", [6] = "HMA", [7] = "OAuth with Microsoft Entra ID", [8] = "PPMI",
    };

    private static readonly Dictionary<int, string> Authorities = new() { [0] = "Public", [1] = "US Government", [2] = "China", [3] = "Automatic" };

    /// <summary>
    /// Checks the profile <paramref name="merged"/> (the stored row with the change applied) and tells how it sends. An absent
    /// protocol, credential retrieval or authority counts as its column default (0). An SMTP profile with Auto Detect is stored as
    /// Basic: <paramref name="merged"/>'s protocol is set to it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> for a profile xrmverse does not support.</exception>
    public static MailProviderKind Validate(Entity merged)
    {
        ArgumentNullException.ThrowIfNull(merged);
        var name = merged.GetAttributeValue<string>("name") ?? merged.Id.ToString();
        var serverType = Value(merged, "servertype") ?? throw Refused(name, "server type is required");
        var protocol = Value(merged, "outgoingauthenticationprotocol") ?? MailSchema.ProtocolAutoDetect;
        var retrieval = Value(merged, "outgoingcredentialretrieval") ?? MailSchema.CredentialsByUserOrQueue;
        var authority = Value(merged, "serverauthority") ?? MailSchema.AuthorityPublic;

        switch (serverType)
        {
            case MailSchema.ServerTypeImapSmtp:
                if (protocol is not (MailSchema.ProtocolBasic or MailSchema.ProtocolAutoDetect))
                    throw Refused(name, $"outgoing authentication protocol {Describe(Protocols, protocol)} is not supported for SMTP; use Basic (3)");
                if (retrieval is not (MailSchema.CredentialsByUserOrQueue or MailSchema.CredentialsInProfile))
                    throw Refused(name, $"outgoing credential retrieval {Describe(Retrievals, retrieval)} is not supported for SMTP; use 0 (by a user or queue) or 1 (in the profile)");
                merged["outgoingauthenticationprotocol"] = new OptionSetValue(MailSchema.ProtocolBasic);
                return MailProviderKind.Smtp;
            case MailSchema.ServerTypeExchange:
                if (protocol != MailSchema.ProtocolOAuth)
                    throw Refused(name, $"outgoing authentication protocol {Describe(Protocols, protocol)} is not supported for Microsoft Graph; use OAuth (4)");
                if (retrieval != MailSchema.CredentialsEntraOAuth)
                    throw Refused(name, $"outgoing credential retrieval {Describe(Retrievals, retrieval)} is not supported for Microsoft Graph; use 7 (OAuth with Microsoft Entra ID)");
                if (authority is not (MailSchema.AuthorityPublic or MailSchema.AuthorityAutomatic))
                    throw Refused(name, $"server authority {Describe(Authorities, authority)} is not supported; use 0 (Public) or 3 (Automatic)");
                return MailProviderKind.Graph;
            default:
                throw Refused(name, $"server type {Describe(ServerTypes, serverType)} is not supported; use 4 (IMAP/SMTP) for SMTP or 0 (Exchange Server) for Microsoft Graph");
        }
    }

    private static int? Value(Entity e, string column) => e.GetAttributeValue<OptionSetValue>(column)?.Value;

    private static string Describe(Dictionary<int, string> labels, int value) =>
        labels.TryGetValue(value, out var label) ? $"{value} ({label})" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static XrmdFaultException Refused(string name, string reason) =>
        XrmdFaultException.InvalidArgument($"Email server profile '{name}': {reason}.");
}
