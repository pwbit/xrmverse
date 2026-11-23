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
/// GreenMail (Apache 2.0, a test container only, exact tag): SMTP on 3025 with authentication, IMAP on 3143 to read what arrived.
/// Users <c>sender</c> / <c>secret</c> (sender@example.com) and <c>rcpt</c> / <c>pw</c> (rcpt@example.com). GreenMail has no TLS:
/// the host needs <c>Mail:AllowInsecureSmtp</c> true (the provider takes <c>allowInsecure: true</c>), and it accepts any SMTP
/// password (a refused password needs <see cref="FakeSmtpServer"/>).
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

    public static async Task<GreenMail> StartAsync()
    {
        var container = new ContainerBuilder(Image)
            .WithPortBinding(3025, true)
            .WithPortBinding(3143, true)
            .WithEnvironment("GREENMAIL_OPTS",
                "-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.auth.disabled=false "
                + "-Dgreenmail.users=sender:secret@example.com,rcpt:pw@example.com")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Starting GreenMail API server"))
            .Build();
        await container.StartAsync();
        return new GreenMail(container);
    }

    /// <summary>The messages in the inbox of rcpt@example.com, after waiting up to <paramref name="timeout"/> for <paramref name="atLeast"/> of them.</summary>
    public async Task<IReadOnlyList<MimeMessage>> RecipientInboxAsync(int atLeast = 1, TimeSpan? timeout = null)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(_container.Hostname, _container.GetMappedPublicPort(3143), SecureSocketOptions.None);
        await imap.AuthenticateAsync("rcpt", "pw");
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

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// A minimal SMTP server for what GreenMail cannot do: GreenMail accepts every SMTP password, so it cannot refuse one. It listens on
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
