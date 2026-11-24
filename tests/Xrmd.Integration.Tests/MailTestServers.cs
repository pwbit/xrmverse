using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace Xrmd.Integration.Tests;

/// <summary>
/// GreenMail (Apache 2.0, a test container only, exact tag): SMTP on 3025 with authentication, IMAP on 3143, and its REST API on 8080
/// (to add users). Users <c>sender</c> / <c>secret</c> (sender@example.com) and <c>rcpt</c> / <c>pw</c> (rcpt@example.com); a test
/// that reads or fills a mailbox adds its own user with <see cref="NewUserAsync"/>, so a container shared by a test class
/// (<see cref="GreenMailFixture"/>) gives each test an empty inbox. GreenMail has no TLS: the host needs <c>Mail:AllowInsecureSmtp</c>
/// and <c>Mail:AllowInsecureImap</c> true (the providers take <c>allowInsecure: true</c>). IMAP refuses a wrong password; the SMTP
/// failure cases (a refused password, 4xx and 5xx replies) use <see cref="FakeSmtpServer"/>.
/// </summary>
internal sealed class GreenMail : IAsyncDisposable
{
    public const string Image = "greenmail/standalone:2.1.8";
    public const string SenderUser = "sender";
    public const string SenderPassword = "secret";
    public const string RecipientAddress = "rcpt@example.com";

    private readonly IContainer _container;

    private GreenMail(IContainer container) => _container = container;

    public string Hostname => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(3025);

    public int ImapPort => _container.GetMappedPublicPort(3143);

    private int ApiPort => _container.GetMappedPublicPort(8080);

    public static async Task<GreenMail> StartAsync()
    {
        var container = new ContainerBuilder(Image)
            .WithPortBinding(3025, true)
            .WithPortBinding(3143, true)
            .WithPortBinding(8080, true)
            .WithEnvironment("GREENMAIL_OPTS",
                // No greenmail.auth.disabled: its mere presence (even "=false") turns authentication off, and IMAP must refuse a wrong password.
                "-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 "
                + "-Dgreenmail.users=sender:secret@example.com,rcpt:pw@example.com")
            // The API logs its start before it listens: wait until it answers (users are added through it).
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Starting GreenMail API server")
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/api/service/readiness")))
            .Build();
        await container.StartAsync();
        return new GreenMail(container);
    }

    /// <summary>A new user with an empty inbox (login, password and address <c>{login}@example.com</c>), added through the REST API.</summary>
    public async Task<MailUser> NewUserAsync()
    {
        var login = "u" + Guid.NewGuid().ToString("N")[..12];
        var user = new MailUser(login, "pw-" + Guid.NewGuid().ToString("N")[..8], login + "@example.com");
        // The container is local: never through an HTTP proxy of the environment.
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var response = await http.PostAsync($"http://{Hostname}:{ApiPort}/api/user", new StringContent(
            $$"""{"email":"{{user.Address}}","login":"{{user.Login}}","password":"{{user.Password}}"}""", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return user;
    }

    /// <summary>An IMAP session of <paramref name="user"/> (plaintext; GreenMail has no TLS).</summary>
    public async Task<ImapClient> ImapAsync(MailUser user)
    {
        var imap = new ImapClient();
        await imap.ConnectAsync(Hostname, ImapPort, SecureSocketOptions.None);
        await imap.AuthenticateAsync(user.Login, user.Password);
        return imap;
    }

    /// <summary>Puts <paramref name="message"/> in the inbox of <paramref name="user"/> with the internal date <paramref name="receivedOn"/> (IMAP APPEND).</summary>
    public async Task AppendAsync(MailUser user, MimeMessage message, DateTimeOffset receivedOn)
    {
        // GreenMail 2.1.8 parses the APPEND date with "dd-MMM-yyyy hh:mm:ss Z" (12-hour clock, lenient): hour 12 is read as 00, so an
        // internal date between 12:00 and 12:59 lands 12 hours early. The same instant written with another offset avoids hour 12.
        if (receivedOn.Hour == 12) receivedOn = receivedOn.ToOffset(receivedOn.Offset + TimeSpan.FromHours(1));
        using var imap = await ImapAsync(user);
        await imap.Inbox.AppendAsync(new AppendRequest(message, MessageFlags.None, receivedOn));
        await imap.DisconnectAsync(true);
    }

    /// <summary>A small message from <c>sender@example.com</c> to <paramref name="to"/> with the Message-ID <c>{id}@example.com</c>.</summary>
    public static MimeMessage Message(string id, string to, string? subject = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject ?? id;
        m.MessageId = id + "@example.com";
        m.Body = new TextPart("plain") { Text = "Body of " + id };
        return m;
    }

    /// <summary>The flags of every message in the inbox of <paramref name="user"/>, read by a separate session.</summary>
    public async Task<IReadOnlyList<MessageFlags>> InboxFlagsAsync(MailUser user)
    {
        using var imap = await ImapAsync(user);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var flags = imap.Inbox.Count == 0 ? [] : (await imap.Inbox.FetchAsync(0, -1, MessageSummaryItems.Flags)).Select(m => m.Flags ?? MessageFlags.None).ToList();
        await imap.DisconnectAsync(true);
        return flags;
    }

    /// <summary>Deletes and expunges every message of the inbox of <paramref name="user"/>, from a separate session.</summary>
    public async Task ExpungeAllAsync(MailUser user)
    {
        using var imap = await ImapAsync(user);
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
        if (imap.Inbox.Count > 0)
        {
            await imap.Inbox.AddFlagsAsync(Enumerable.Range(0, imap.Inbox.Count).ToList(), MessageFlags.Deleted, silent: true);
            await imap.Inbox.ExpungeAsync();
        }
        await imap.DisconnectAsync(true);
    }

    /// <summary>The messages in the inbox of <paramref name="user"/>, after waiting up to <paramref name="timeout"/> for <paramref name="atLeast"/> of them.</summary>
    public async Task<IReadOnlyList<MimeMessage>> InboxAsync(MailUser user, int atLeast = 1, TimeSpan? timeout = null)
    {
        using var imap = await ImapAsync(user);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (inbox.Count < atLeast && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            await inbox.CheckAsync();
        }
        var messages = new List<MimeMessage>();
        for (var i = 0; i < inbox.Count; i++) messages.Add(await inbox.GetMessageAsync(i));
        await imap.DisconnectAsync(true);
        return messages;
    }

    /// <summary>The messages in the inbox of rcpt@example.com (the user the acceptance tests send to).</summary>
    public Task<IReadOnlyList<MimeMessage>> RecipientInboxAsync(int atLeast = 1, TimeSpan? timeout = null) =>
        InboxAsync(new MailUser("rcpt", "pw", RecipientAddress), atLeast, timeout);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>A GreenMail user: IMAP login, password and address.</summary>
internal sealed record MailUser(string Login, string Password, string Address);

/// <summary>
/// One <see cref="GreenMail"/> for a whole test class (<c>IClassFixture&lt;GreenMailFixture&gt;</c>): the container starts once, and each
/// test adds its own user (<see cref="GreenMail.NewUserAsync"/>) instead of starting its own container.
/// </summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    private GreenMail? _server;

    internal GreenMail Server => _server ?? throw new InvalidOperationException("GreenMail has not started.");

    public async Task InitializeAsync() => _server = await GreenMail.StartAsync();

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}

/// <summary>
/// A minimal SMTP server for the SMTP failure cases (a refused password, chosen MAIL FROM and RCPT TO replies), which GreenMail cannot
/// script. It listens on
/// 127.0.0.1, offers AUTH PLAIN and answers 535 unless <c>authOk</c>, answers MAIL FROM with <c>mailReply</c> and RCPT TO with
/// <c>rcptReply</c>. It offers no STARTTLS (the provider needs <c>allowInsecure</c>).
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public System.Collections.Concurrent.ConcurrentQueue<string> Commands { get; } = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static FakeSmtpServer Start(string mailReply, bool authOk = false, string rcptReply = "250 ok")
    {
        var server = new FakeSmtpServer();
        server._listener.Start();
        server._loop = Task.Run(async () =>
        {
            while (!server._stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await server._listener.AcceptTcpClientAsync(server._stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = Task.Run(() => server.Serve(client, mailReply, authOk, rcptReply));
            }
        });
        return server;
    }

    private async Task Serve(TcpClient client, string mailReply, bool authOk, string rcptReply)
    {
        using var c = client;
        using var stream = c.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, new ASCIIEncoding()) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            Commands.Enqueue(line.Split(' ')[0].ToUpperInvariant());
            var cmd = line.ToUpperInvariant();
            if (cmd.StartsWith("EHLO", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("250-fake");
                await writer.WriteLineAsync("250 AUTH PLAIN");
            }
            else if (cmd.StartsWith("AUTH", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync(authOk ? "235 2.7.0 ok" : "535 5.7.8 Authentication credentials invalid");
            }
            else if (cmd.StartsWith("MAIL", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync(mailReply);
            }
            else if (cmd.StartsWith("RCPT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync(rcptReply);
            }
            else if (cmd.StartsWith("QUIT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("221 bye");
                return;
            }
            else
            {
                await writer.WriteLineAsync("250 ok");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
    }
}
