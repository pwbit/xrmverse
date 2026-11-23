namespace Xrmd.Core.Mail;

/// <summary>The send and incoming providers of each <see cref="MailProviderKind"/>.</summary>
public sealed class MailProviders
{
    private readonly IMailProvider _smtp;
    private readonly IMailProvider _graph;
    private readonly IIncomingMailProvider? _imap;
    private readonly IIncomingMailProvider? _graphIncoming;

    /// <summary>The SMTP and IMAP (MailKit) and Graph (REST) providers; both Graph providers share one token source.</summary>
    /// <param name="graphHttp">Gives the HttpClient of the Graph calls.</param>
    /// <param name="graphBaseUrl"><c>Mail:GraphBaseUrl</c>; null is the public Graph endpoint.</param>
    /// <param name="allowInsecureSmtp"><c>Mail:AllowInsecureSmtp</c> (default false): allows plaintext SMTP when the server has no STARTTLS.</param>
    /// <param name="allowInsecureImap"><c>Mail:AllowInsecureImap</c> (default false): allows plaintext IMAP when the server has no STARTTLS.</param>
    public MailProviders(Func<HttpClient> graphHttp, string? graphBaseUrl = null, bool allowInsecureSmtp = false, bool allowInsecureImap = false)
        : this(GraphTokens.Over(graphHttp), graphHttp, graphBaseUrl, allowInsecureSmtp, allowInsecureImap)
    {
    }

    private MailProviders(GraphTokens tokens, Func<HttpClient> graphHttp, string? graphBaseUrl, bool allowInsecureSmtp, bool allowInsecureImap)
        : this(new SmtpMailProvider(allowInsecureSmtp), new GraphMailProvider(graphHttp, graphBaseUrl, tokens),
            new ImapMailProvider(allowInsecureImap), new GraphIncomingProvider(graphHttp, graphBaseUrl, tokens))
    {
    }

    /// <summary>
    /// Given providers: the seam of the send, test and sync jobs (a test replaces them with recording fakes). Without incoming providers,
    /// <see cref="IncomingFor"/> refuses.
    /// </summary>
    public MailProviders(IMailProvider smtp, IMailProvider graph, IIncomingMailProvider? imap = null, IIncomingMailProvider? graphIncoming = null)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(graph);
        _smtp = smtp;
        _graph = graph;
        _imap = imap;
        _graphIncoming = graphIncoming;
    }

    /// <summary>The named HttpClient of the Graph calls.</summary>
    public const string GraphClientName = "xrmd-mail-graph";

    /// <summary>The send provider for <paramref name="kind"/>.</summary>
    public IMailProvider For(MailProviderKind kind) => kind switch
    {
        MailProviderKind.Smtp => _smtp,
        MailProviderKind.Graph => _graph,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The incoming provider for <paramref name="kind"/>: IMAP for an SMTP/IMAP profile, Graph for Exchange Online.</summary>
    /// <exception cref="InvalidOperationException">These providers were built without incoming providers.</exception>
    public IIncomingMailProvider IncomingFor(MailProviderKind kind) => kind switch
    {
        MailProviderKind.Smtp => _imap,
        MailProviderKind.Graph => _graphIncoming,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    } ?? throw new InvalidOperationException($"No incoming mail provider is configured for {kind}.");
}
