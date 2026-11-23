using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The mail model (plan 4.1b): the seeded tables <c>emailserverprofile</c>, <c>mailbox</c> and <c>activitymimeattachment</c>, and the
/// option values the outgoing email rules use. Names, types and option values follow the Learn table references.
/// </summary>
/// <remarks>
/// The object type codes 9605, 9606 and 1001 are from memory, not from Learn (not listed on the pages): checklist 1. That Exchange
/// Online is <see cref="ServerTypeExchange"/> is from memory too: checklist 2.
/// </remarks>
public static class MailSchema
{
    /// <summary>The tables.</summary>
    public const string EmailServerProfile = "emailserverprofile", Mailbox = "mailbox", Attachment = "activitymimeattachment";

    /// <summary>The columns of <see cref="Attachment"/> the server maintains or keeps in the file store (plan 4.1b).</summary>
    public const string AttachmentBody = "body", AttachmentNumber = "attachmentnumber", AttachmentCount = "attachmentcount",
        BlockedAttachments = "blockedattachments";

    /// <summary>
    /// The default of <c>organization.blockedattachments</c>: the list a new Dataverse environment holds, as Learn gives it ("Files and
    /// images overview", Block certain types of files). Written for a new organization, and once for an older one whose value is empty
    /// (<c>SystemSeed.BlockedAttachmentsDefaultStep</c>).
    /// </summary>
    public const string DefaultBlockedAttachments =
        "ade;adp;app;asa;ashx;asmx;asp;bas;bat;cdx;cer;chm;class;cmd;com;config;cpl;crt;csh;dll;exe;fxp;hlp;hta;htr;htw;ida;idc;idq;inf;ins;" +
        "isp;its;jar;js;jse;ksh;lnk;mad;maf;mag;mam;maq;mar;mas;mat;mau;mav;maw;mda;mdb;mde;mdt;mdw;mdz;msc;msh;msh1;msh1xml;msh2;msh2xml;" +
        "mshxml;msi;msp;mst;ops;pcd;pif;prf;prg;printer;pst;reg;rem;scf;scr;sct;shb;shs;shtm;shtml;soap;stm;tmp;url;vb;vbe;vbs;vsmacros;vss;" +
        "vst;vsw;ws;wsc;wsf;wsh;svg";

    /// <summary><c>emailserverprofile.servertype</c>: Exchange Server and IMAP/SMTP, the two outgoing xrmverse accepts.</summary>
    public const int ServerTypeExchange = 0, ServerTypeImapSmtp = 4;

    /// <summary>The <c>*authenticationprotocol</c> values: Auto Detect, Basic and OAuth.</summary>
    public const int ProtocolAutoDetect = 0, ProtocolBasic = 3, ProtocolOAuth = 4;

    /// <summary>The <c>*credentialretrieval</c> values: by a user or queue, in the profile, and OAuth with Microsoft Entra ID.</summary>
    public const int CredentialsByUserOrQueue = 0, CredentialsInProfile = 1, CredentialsEntraOAuth = 7;

    /// <summary>The <c>serverauthority</c> values: Public and Automatic.</summary>
    public const int AuthorityPublic = 0, AuthorityAutomatic = 3;

    /// <summary>The delivery methods: None, Outlook (refused), Server-Side Synchronization and Forward Mailbox (refused).</summary>
    public const int DeliveryNone = 0, DeliveryOutlook = 1, DeliveryServerSide = 2, DeliveryForwardMailbox = 3;

    /// <summary>The columns of the owner (<c>systemuser</c>, <c>queue</c>) that name its mailbox and its approval.</summary>
    public const string DefaultMailbox = "defaultmailbox", ApprovalColumn = "emailrouteraccessapproval", ProfileColumn = "emailserverprofile";

    /// <summary>The organization's default email server profile, which a new mailbox gets (final review I3).</summary>
    public const string DefaultProfileColumn = "defaultemailserverprofileid";

    /// <summary>The columns only a System Administrator writes, by table: the approval, and a mailbox's server profile.</summary>
    public static IReadOnlyList<string> AdministratorColumns(string table) => table switch
    {
        Mailbox => [ApprovalColumn, ProfileColumn],
        "systemuser" or "queue" => [ApprovalColumn],
        _ => [],
    };

    /// <summary>
    /// The columns a non-administrator may change on a mailbox (ruling P2, an allowlist): everything else is a System Administrator's,
    /// the server's, or the primary key.
    /// </summary>
    public static readonly IReadOnlySet<string> MailboxUserColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "username", "password", "incomingemaildeliverymethod", "outgoingemaildeliverymethod", "enabledforincomingemail",
        "enabledforoutgoingemail", "testemailconfigurationscheduled", "name",
    };

    /// <summary>
    /// The mailbox columns the server maintains (ruling P7): the address follows the owner's, the statuses and sync times follow the
    /// tests and the synchronization. A direct write is a 400 (an unchanged value sent back is not a write).
    /// </summary>
    public static readonly IReadOnlyList<string> MailboxServerColumns =
    [
        "emailaddress", ApprovalColumn, "incomingemailstatus", "outgoingemailstatus", "lastsyncerror", "lastsyncerrorcount",
        "lastsyncerroroccurredon", "lastsuccessfulsynccompletedon", "processemailreceivedafter", TestCompletedOn,
    ];

    /// <summary>
    /// The mailbox columns of the outgoing test (plan 4.1b, task 7): writing <see cref="TestScheduled"/> true (its owner or a System
    /// Administrator) queues the test, whose job writes <see cref="TestCompletedOn"/> and sets the flag back to false.
    /// </summary>
    public const string TestScheduled = "testemailconfigurationscheduled", TestCompletedOn = "testmailboxaccesscompletedon";

    /// <summary>The columns holding a delivery method on <c>mailbox</c>, <c>queue</c> and <c>systemuser</c>.</summary>
    public static readonly IReadOnlyList<string> DeliveryColumns = ["incomingemaildeliverymethod", "outgoingemaildeliverymethod"];

    /// <summary>The <c>*emailstatus</c> values.</summary>
    public const int StatusNotRun = 0, StatusSuccess = 1, StatusFailure = 2;

    /// <summary>The <c>emailrouteraccessapproval</c> values.</summary>
    public const int ApprovalEmpty = 0, ApprovalApproved = 1, ApprovalPending = 2, ApprovalRejected = 3;

    /// <summary>The <c>email.correlationmethod</c> values.</summary>
    public const int CorrelationNone = 0, CorrelationSkipped = 1, CorrelationXHeader = 2, CorrelationInReplyTo = 3,
        CorrelationTrackingToken = 4, CorrelationConversationIndex = 5, CorrelationSmartMatching = 6, CorrelationCustom = 7;

    /// <summary>The <c>email.statuscode</c> values: Draft and Failed (state 0), Sent (state 1) and Pending Send (state 1).</summary>
    public const int EmailDraft = 1, EmailFailed = 8, EmailSent = 3, EmailPendingSend = 6;

    /// <summary>
    /// The write-only secret columns (<c>IsValidForRead</c> false), keyed by <c>table.column</c>, with the table and the read-only flag
    /// that tells whether one is set. They have no data column (<see cref="IsSecret"/>): a secret is never returned by any read path.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Table, string FlagColumn)> SecretColumns =
        new Dictionary<string, (string, string)>
        {
            ["emailserverprofile.incomingpassword"] = (EmailServerProfile, "isincomingpasswordset"),
            ["emailserverprofile.outgoingpassword"] = (EmailServerProfile, "isoutgoingpasswordset"),
            ["emailserverprofile.oauthclientsecret"] = (EmailServerProfile, "isoauthclientsecretset"),
            ["mailbox.password"] = (Mailbox, "ispasswordset"),
        };

    /// <summary>True for one of the <see cref="SecretColumns"/>.</summary>
    public static bool IsSecret(AttributeMetadata a) =>
        a.EntityLogicalName is { } table && SecretColumns.ContainsKey(table + "." + a.LogicalName);
}
