namespace Xrmd.Core.Mail;

/// <summary>The provider of each <see cref="MailProviderKind"/>.</summary>
public sealed class MailProviders
{
    private readonly IMailProvider _smtp;
    private readonly IMailProvider _graph;

    /// <summary>The SMTP (MailKit) and Graph (REST) providers.</summary>
    /// <param name="graphHttp">Gives the HttpClient of the Graph calls.</param>
    /// <param name="graphBaseUrl"><c>Mail:GraphBaseUrl</c>; null is the public Graph endpoint.</param>
    /// <param name="allowInsecureSmtp"><c>Mail:AllowInsecureSmtp</c> (default false): allows plaintext SMTP when the server has no STARTTLS.</param>
    public MailProviders(Func<HttpClient> graphHttp, string? graphBaseUrl = null, bool allowInsecureSmtp = false)
        : this(new SmtpMailProvider(allowInsecureSmtp), new GraphMailProvider(graphHttp, graphBaseUrl))
    {
    }

    /// <summary>Given providers: the seam of the send and test jobs (a test replaces them with a recording fake).</summary>
    public MailProviders(IMailProvider smtp, IMailProvider graph)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(graph);
        _smtp = smtp;
        _graph = graph;
    }

    /// <summary>The named HttpClient of the Graph calls.</summary>
    public const string GraphClientName = "xrmd-mail-graph";

    /// <summary>The provider for <paramref name="kind"/>.</summary>
    public IMailProvider For(MailProviderKind kind) => kind switch
    {
        MailProviderKind.Smtp => _smtp,
        MailProviderKind.Graph => _graph,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
