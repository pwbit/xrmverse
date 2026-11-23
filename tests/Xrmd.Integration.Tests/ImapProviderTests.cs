using FluentAssertions;
using MailKit;
using Xrmd.Core.Mail;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 4: the IMAP incoming provider against <see cref="GreenMail"/> (IMAP 3143, no TLS, so <c>allowInsecureImap: true</c>).
/// One container for the class; each test fills the inbox of its own user with IMAP APPEND, which sets the internal date.
/// </summary>
public sealed class ImapProviderTests(GreenMailFixture mail) : IClassFixture<GreenMailFixture>
{
    private static readonly DateTime Since = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private GreenMail GreenMail => mail.Server;

    private MailProfile Profile() => new(GreenMail.Hostname, GreenMail.ImapPort, UseSsl: false, Protocol: 3, null, null, 0);

    private static MailCredentials Creds(MailUser user, string? password = null) => new(user.Login, password ?? user.Password, null);

    private Task<IIncomingSession> OpenAsync(MailUser user) =>
        new ImapMailProvider(allowInsecure: true).OpenAsync(user.Address, Profile(), Creds(user), default);

    private Task AppendAsync(MailUser user, string id, DateTime receivedOn) =>
        GreenMail.AppendAsync(user, GreenMail.Message(id, user.Address), new DateTimeOffset(receivedOn));

    [Fact]
    public async Task List_FromNoCursor_ReturnsMessagesSince()
    {
        var user = await GreenMail.NewUserAsync();
        await AppendAsync(user, "old", Since.AddDays(-2));
        await AppendAsync(user, "new1", Since.AddHours(1));
        await AppendAsync(user, "new2", Since.AddHours(2));
        await using var session = await OpenAsync(user);

        var batch = await session.ListAsync(null, Since, 50, default);

        batch.Items.Select(i => MailMessageKeys.Normalize(i.MessageId)).Should().Equal("new1@example.com", "new2@example.com");
        batch.Items.Select(i => i.ReceivedOn).Should().Equal(Since.AddHours(1), Since.AddHours(2));
        batch.Items.Should().OnlyContain(i => i.ReceivedOn.Kind == DateTimeKind.Utc);
        var validity = batch.CursorAtEnd!.Split(':')[1];
        batch.Items.Should().OnlyContain(i => i.ServerId.StartsWith(validity + ":"), "the server id carries the UIDVALIDITY");
        batch.More.Should().BeFalse();
        batch.CursorAtEnd.Should().StartWith("imap:").And.Be(batch.Items[^1].CursorAfter);
    }

    [Fact]
    public async Task List_FromCursor_ReturnsOnlyNewer_InUidOrder()
    {
        var user = await GreenMail.NewUserAsync();
        await AppendAsync(user, "a", Since.AddHours(1));
        await AppendAsync(user, "b", Since.AddHours(2));
        string? cursor;
        await using (var first = await OpenAsync(user))
        {
            cursor = (await first.ListAsync(null, Since, 50, default)).CursorAtEnd;
        }

        // UID order, not date order: c arrives first with the later internal date.
        await AppendAsync(user, "c", Since.AddHours(5));
        await AppendAsync(user, "d", Since.AddHours(3));
        await using var session = await OpenAsync(user);

        var batch = await session.ListAsync(cursor, Since, 50, default);

        batch.Items.Select(i => MailMessageKeys.Normalize(i.MessageId)).Should().Equal("c@example.com", "d@example.com");
        batch.More.Should().BeFalse();

        var again = await session.ListAsync(batch.CursorAtEnd, Since, 50, default);
        again.Items.Should().BeEmpty("the UID range past the last UID echoes the last message, which is dropped");
        again.CursorAtEnd.Should().Be(batch.CursorAtEnd);
    }

    [Fact]
    public async Task List_UidValidityChanged_RestartsFromSince()
    {
        var user = await GreenMail.NewUserAsync();
        await AppendAsync(user, "old", Since.AddDays(-2));
        await AppendAsync(user, "a", Since.AddHours(1));
        await AppendAsync(user, "b", Since.AddHours(2));
        await using var session = await OpenAsync(user);
        var current = (await session.ListAsync(null, Since, 50, default)).CursorAtEnd!.Split(':');
        var stale = $"imap:{long.Parse(current[1]) + 1}:{current[2]}";

        var batch = await session.ListAsync(stale, Since, 50, default);

        batch.Items.Select(i => MailMessageKeys.Normalize(i.MessageId)).Should().Equal("a@example.com", "b@example.com");
        batch.CursorAtEnd.Should().StartWith($"imap:{current[1]}:");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("imap:x:1")]
    [InlineData("https://graph.microsoft.com/v1.0/x")]
    public async Task List_UnreadableCursor_RestartsFromSince(string cursor)
    {
        var user = await GreenMail.NewUserAsync();
        await AppendAsync(user, "old", Since.AddDays(-2));
        await AppendAsync(user, "a", Since.AddHours(1));
        await using var session = await OpenAsync(user);

        var batch = await session.ListAsync(cursor, Since, 50, default);

        batch.Items.Select(i => MailMessageKeys.Normalize(i.MessageId)).Should().Equal("a@example.com");
    }

    [Fact]
    public async Task Fetch_DoesNotSetSeenFlag()
    {
        var user = await GreenMail.NewUserAsync();
        await GreenMail.AppendAsync(user, GreenMail.Message("a", user.Address, "Quarterly numbers"), new DateTimeOffset(Since.AddHours(1)));
        await using var session = await OpenAsync(user);
        var item = (await session.ListAsync(null, Since, 50, default)).Items.Single();

        var message = await session.FetchAsync(item, default);

        message.Should().NotBeNull();
        message!.Subject.Should().Be("Quarterly numbers");
        message.MessageId.Should().Be("a@example.com");
        var flags = await GreenMail.InboxFlagsAsync(user);
        flags.Should().ContainSingle().Which.HasFlag(MessageFlags.Seen).Should().BeFalse("the inbox is opened read-only and fetched with BODY.PEEK");
    }

    [Fact]
    public async Task List_RespectsMax_AndCursorAfterEachItem()
    {
        var user = await GreenMail.NewUserAsync();
        await AppendAsync(user, "a", Since.AddHours(1));
        await AppendAsync(user, "b", Since.AddHours(2));
        await AppendAsync(user, "c", Since.AddHours(3));
        await using var session = await OpenAsync(user);

        var batch = await session.ListAsync(null, Since, 2, default);

        batch.Items.Select(i => MailMessageKeys.Normalize(i.MessageId)).Should().Equal("a@example.com", "b@example.com");
        batch.Items.Should().OnlyContain(i => i.CursorAfter != null && i.CursorAfter.StartsWith("imap:"));
        batch.More.Should().BeTrue();
        batch.CursorAtEnd.Should().Be(batch.Items[1].CursorAfter);

        (await session.ListAsync(batch.Items[0].CursorAfter, Since, 50, default)).Items.Select(i => MailMessageKeys.Normalize(i.MessageId))
            .Should().Equal("b@example.com", "c@example.com");
        var rest = await session.ListAsync(batch.CursorAtEnd, Since, 2, default);
        rest.Items.Select(i => MailMessageKeys.Normalize(i.MessageId)).Should().Equal("c@example.com");
        rest.More.Should().BeFalse();
    }

    [Fact]
    public async Task Open_WrongPassword_IsPermanentMailboxScope()
    {
        var user = await GreenMail.NewUserAsync();

        var act = () => new ImapMailProvider(allowInsecure: true).OpenAsync(user.Address, Profile(), Creds(user, "wrong-pw"), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Mailbox);
        e.Message.Should().NotContain("wrong-pw");
    }

    [Fact]
    public async Task Open_NoTls_WithoutAllowInsecure_IsPermanent()
    {
        var user = await GreenMail.NewUserAsync();

        var act = () => new ImapMailProvider().OpenAsync(user.Address, Profile(), Creds(user), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Mailbox);
        e.Message.Should().Contain("STARTTLS").And.Contain("Mail:AllowInsecureImap");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Open_NoIncomingServer_IsPermanentMailboxScope(string? host)
    {
        var act = () => new ImapMailProvider(allowInsecure: true).OpenAsync(
            "a@example.com", new MailProfile(host, 143, false, 3, null, null, 0), new MailCredentials("a", "pw", null), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Mailbox);
        e.Message.Should().Contain("no incoming server");
    }

    [Fact]
    public async Task Open_ConnectionRefused_IsTransient()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var act = () => new ImapMailProvider(allowInsecure: true).OpenAsync(
            "a@example.com", new MailProfile("127.0.0.1", freePort, false, 3, null, null, 0), new MailCredentials("a", "pw", null), default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeTrue();
    }

    [Fact]
    public async Task Fetch_OtherUidValidity_ReturnsNull()
    {
        var user = await GreenMail.NewUserAsync();
        await AppendAsync(user, "a", Since.AddHours(1));
        await using var session = await OpenAsync(user);
        var item = (await session.ListAsync(null, Since, 50, default)).Items.Single();
        var parts = item.ServerId.Split(':');
        (await session.FetchAsync(item, default)).Should().NotBeNull();

        var renumbered = item with { ServerId = $"{long.Parse(parts[0]) + 1}:{parts[1]}" };

        (await session.FetchAsync(renumbered, default)).Should().BeNull("under another UIDVALIDITY the UID may name a different message");
    }

    [Theory]
    [InlineData("7")]
    [InlineData("x:7")]
    [InlineData("1:0")]
    [InlineData("")]
    public async Task Fetch_MalformedServerId_IsPermanentMessageScope(string serverId)
    {
        var user = await GreenMail.NewUserAsync();
        await using var session = await OpenAsync(user);

        var act = () => session.FetchAsync(new IncomingRef(serverId, null, null, Since, null), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Message);
    }

    /// <summary>Final review I2: a fetch the server refuses, or a message that cannot be parsed, is about that message only.</summary>
    [Fact]
    public void Fetch_RefusedOrUnreadable_IsPermanentMessageScope()
    {
        Exception[] failures =
        [
            new MailKit.Net.Imap.ImapCommandException(MailKit.Net.Imap.ImapCommandResponse.No, "The message is damaged."),
            new MailKit.Net.Imap.ImapCommandException(MailKit.Net.Imap.ImapCommandResponse.Bad, "Invalid message."),
            new FormatException("Not MIME."),
            new MimeKit.ParseException("Bad header.", 0, 0),
        ];

        foreach (var failure in failures)
        {
            ImapMailProvider.IsAboutTheMessage(failure).Should().BeTrue();
            var mapped = ImapMailProvider.MapFetch(failure);
            mapped.Transient.Should().BeFalse();
            mapped.Scope.Should().Be(MailFailureScope.Message);
        }

        ImapMailProvider.IsAboutTheMessage(new IOException("reset")).Should().BeFalse("the network is the mailbox's");
        ImapMailProvider.IsAboutTheMessage(new MailKit.Net.Imap.ImapProtocolException("BYE")).Should().BeFalse();
    }

    [Fact]
    public async Task Fetch_Expunged_ReturnsNull()
    {
        var user = await GreenMail.NewUserAsync();
        await AppendAsync(user, "a", Since.AddHours(1));
        await using var session = await OpenAsync(user);
        var item = (await session.ListAsync(null, Since, 50, default)).Items.Single();

        await GreenMail.ExpungeAllAsync(user);

        (await session.FetchAsync(item, default)).Should().BeNull();
    }
}
