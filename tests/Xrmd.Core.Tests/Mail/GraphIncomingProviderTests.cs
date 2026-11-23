using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Identity.Client;
using Xrmd.Core.Mail;

namespace Xrmd.Core.Tests.Mail;

/// <summary>Plan 4.1c, task 4: the Graph incoming provider against a fake HTTP handler (token endpoint and Graph).</summary>
public sealed class GraphIncomingProviderTests
{
    private const string Base = "https://graph.test/v1.0";
    private const string Address = "sales@example.com";
    private static readonly MailProfile Profile = new(null, null, false, 4, "tenant-1", "client-1", 0);
    private static readonly MailCredentials Creds = new(null, null, "s3cret");
    private static readonly DateTime Since = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seen(HttpMethod Method, string Url, string? Auth, string? Prefer);

    /// <summary>Answers the token request and the inbox read of the open; every other Graph call goes to <c>graph</c>.</summary>
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage>? graph = null, HttpStatusCode inbox = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        public IEnumerable<Seen> Graph => Requests.Where(r => r.Url.StartsWith("https://graph.test", StringComparison.Ordinal)
            || r.Url.StartsWith("https://evil.example.com", StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Seen(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Prefer", out var p) ? string.Join(",", p) : null));
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"tok-1","token_type":"Bearer","expires_in":3600}"""));
            }

            if (url.Contains("/mailFolders/inbox?", StringComparison.Ordinal))
            {
                return Task.FromResult(inbox == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, """{"id":"inbox-1"}""")
                    : Json(inbox, """{"error":{"code":"InvalidAuthenticationToken"}}"""));
            }

            return Task.FromResult(graph?.Invoke(request) ?? Json(HttpStatusCode.OK, """{"value":[]}"""));
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => new(h, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Task<IIncomingSession> OpenAsync(Fake fake) =>
        new GraphIncomingProvider(() => new HttpClient(fake, disposeHandler: false), Base, new Factory(fake)).OpenAsync(Address, Profile, Creds, default);

    private static string Item(string id, string received) =>
        $$"""{"id":"{{id}}","internetMessageId":"<{{id}}@example.com>","conversationIndex":"AQHZ{{id}}","receivedDateTime":"{{received}}"}""";

    private const string FirstUrl = Base + "/users/sales@example.com/mailFolders/inbox/messages/delta"
        + "?$select=internetMessageId,conversationIndex,receivedDateTime&$filter=receivedDateTime ge 2026-11-01T00:00:00Z";

    [Fact]
    public async Task Open_ReadsTheInbox_WithTheToken()
    {
        var fake = new Fake();

        await using var session = await OpenAsync(fake);

        var open = fake.Graph.Should().ContainSingle().Which;
        open.Method.Should().Be(HttpMethod.Get);
        Uri.UnescapeDataString(open.Url).Should().Be(Base + "/users/sales@example.com/mailFolders/inbox?$select=id");
        open.Auth.Should().Be("Bearer tok-1");
    }

    [Fact]
    public async Task List_FirstCall_UsesDeltaWithReceivedFilter_AndPageSize()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, $$"""{"value":[{{Item("m1", "2026-11-02T08:30:00Z")}}],"@odata.deltaLink":"{{Base}}/delta?$deltatoken=d1"}"""));
        await using var session = await OpenAsync(fake);

        var batch = await session.ListAsync(null, Since, 25, default);

        var list = fake.Graph.Last();
        list.Method.Should().Be(HttpMethod.Get);
        Uri.UnescapeDataString(list.Url).Should().Be(FirstUrl);
        list.Prefer.Should().Be("odata.maxpagesize=25");
        list.Auth.Should().Be("Bearer tok-1");
        var item = batch.Items.Should().ContainSingle().Which;
        item.Should().Be(new IncomingRef("m1", "<m1@example.com>", "AQHZm1", new DateTime(2026, 11, 2, 8, 30, 0, DateTimeKind.Utc), Base + "/delta?$deltatoken=d1"));
        item.ReceivedOn.Kind.Should().Be(DateTimeKind.Utc);
        batch.More.Should().BeFalse();
        batch.CursorAtEnd.Should().Be(Base + "/delta?$deltatoken=d1");
    }

    [Fact]
    public async Task List_FollowsNextLink_CursorAfterOnlyOnLastItemOfPage()
    {
        const string next = Base + "/users/sales%40example.com/mailFolders/inbox/messages/delta?$skiptoken=s1";
        const string delta = Base + "/users/sales%40example.com/mailFolders/inbox/messages/delta?$deltatoken=d1";
        var fake = new Fake(r => r.RequestUri!.AbsoluteUri.Contains("skiptoken", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, $$"""{"value":[{{Item("m3", "2026-11-03T00:00:00Z")}}],"@odata.deltaLink":"{{delta}}"}""")
            : Json(HttpStatusCode.OK, $$"""{"value":[{{Item("m1", "2026-11-02T00:00:00Z")}},{{Item("m2", "2026-11-02T01:00:00Z")}}],"@odata.nextLink":"{{next}}"}"""));
        await using var session = await OpenAsync(fake);

        var first = await session.ListAsync(null, Since, 2, default);

        first.Items.Select(i => i.ServerId).Should().Equal("m1", "m2");
        first.Items[0].CursorAfter.Should().BeNull("stopping inside a page is not a safe resume point");
        first.Items[1].CursorAfter.Should().Be(next);
        first.More.Should().BeTrue();
        first.CursorAtEnd.Should().Be(next);

        var second = await session.ListAsync(first.CursorAtEnd, Since, 2, default);

        fake.Graph.Last().Url.Should().Be(next);
        fake.Graph.Last().Prefer.Should().Be("odata.maxpagesize=2");
        second.Items.Should().ContainSingle().Which.CursorAfter.Should().Be(delta);
        second.More.Should().BeFalse();
        second.CursorAtEnd.Should().Be(delta);
    }

    [Fact]
    public async Task List_RemovedEntries_Ignored()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, $$$"""
            {"value":[{{{Item("m1", "2026-11-02T00:00:00Z")}}},{"id":"gone","@removed":{"reason":"deleted"}}],"@odata.deltaLink":"{{{Base}}}/d"}
            """));
        await using var session = await OpenAsync(fake);

        var batch = await session.ListAsync(null, Since, 10, default);

        var item = batch.Items.Should().ContainSingle().Which;
        item.ServerId.Should().Be("m1");
        item.CursorAfter.Should().Be(Base + "/d", "the entries after it in the page are ignored, so it ends the page");
    }

    [Fact]
    public async Task Fetch_GetsMimeFromValue()
    {
        const string mime = "From: a@example.com\r\nTo: sales@example.com\r\nSubject: Quarterly numbers\r\nMessage-ID: <m1@example.com>\r\n\r\nHello\r\n";
        var fake = new Fake(r => r.RequestUri!.AbsolutePath.EndsWith("/$value", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(mime, Encoding.ASCII, "message/rfc822") }
            : Json(HttpStatusCode.NotFound, "{}"));
        await using var session = await OpenAsync(fake);

        var message = await session.FetchAsync(new IncomingRef("AAMk=1/2", "<m1@example.com>", null, Since, null), default);

        message.Should().NotBeNull();
        message!.Subject.Should().Be("Quarterly numbers");
        message.MessageId.Should().Be("m1@example.com");
        var fetch = fake.Graph.Last();
        fetch.Method.Should().Be(HttpMethod.Get);
        fetch.Url.Should().Be(Base + "/users/sales%40example.com/messages/AAMk%3D1%2F2/$value");
        fetch.Auth.Should().Be("Bearer tok-1");
    }

    [Fact]
    public async Task Fetch_404_ReturnsNull()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.NotFound, """{"error":{"code":"ErrorItemNotFound"}}"""));
        await using var session = await OpenAsync(fake);

        (await session.FetchAsync(new IncomingRef("m1", null, null, Since, null), default)).Should().BeNull();
    }

    [Fact]
    public async Task Open_401_IsPermanent()
    {
        var fake = new Fake(inbox: HttpStatusCode.Unauthorized);

        var act = () => OpenAsync(fake);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Mailbox);
        e.Message.Should().Contain("401").And.NotContain("s3cret");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task List_429_IsTransient(HttpStatusCode code)
    {
        var fake = new Fake(_ => new HttpResponseMessage(code));
        await using var session = await OpenAsync(fake);

        var act = () => session.ListAsync(null, Since, 10, default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeTrue();
        e.Scope.Should().Be(MailFailureScope.Mailbox);
    }

    [Fact]
    public async Task Fetch_500_IsTransient()
    {
        var fake = new Fake(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await using var session = await OpenAsync(fake);

        var act = () => session.FetchAsync(new IncomingRef("m1", null, null, Since, null), default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example.com/v1.0/users/sales%40example.com/mailFolders/inbox/messages/delta?$deltatoken=x")]
    [InlineData("https://graph.test/v1.0.evil.example.com/delta?$deltatoken=x")]
    [InlineData("not a url")]
    public async Task Cursor_ForeignHost_IsIgnored(string cursor)
    {
        var fake = new Fake();
        await using var session = await OpenAsync(fake);

        await session.ListAsync(cursor, Since, 10, default);

        fake.Graph.Should().NotContain(r => r.Url.StartsWith("https://evil.example.com", StringComparison.Ordinal) || r.Url.Contains("deltatoken=x"));
        Uri.UnescapeDataString(fake.Graph.Last().Url).Should().Be(FirstUrl);
    }

    /// <summary>Final review I1: an expired or reset delta cursor restarts the listing from the received date in the same call.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Gone, "resyncRequired")]
    [InlineData(HttpStatusCode.Gone, null)]
    [InlineData(HttpStatusCode.BadRequest, "syncStateNotFound")]
    [InlineData(HttpStatusCode.NotFound, "syncStateNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "syncStateInvalid")]
    public async Task List_ExpiredCursor_RestartsFullRead(HttpStatusCode code, string? error)
    {
        const string stale = Base + "/users/sales%40example.com/mailFolders/inbox/messages/delta?$deltatoken=old";
        var fake = new Fake(r => r.RequestUri!.AbsoluteUri.Contains("deltatoken=old", StringComparison.Ordinal)
            ? Json(code, error is null ? "{}" : $$$"""{"error":{"code":"{{{error}}}"}}""")
            : Json(HttpStatusCode.OK, $$"""{"value":[{{Item("m1", "2026-11-02T00:00:00Z")}}],"@odata.deltaLink":"{{Base}}/delta?$deltatoken=new"}"""));
        await using var session = await OpenAsync(fake);

        var batch = await session.ListAsync(stale, Since, 10, default);

        fake.Graph.Select(r => r.Url).Should().Contain(stale);
        Uri.UnescapeDataString(fake.Graph.Last().Url).Should().Be(FirstUrl);
        batch.Items.Should().ContainSingle().Which.ServerId.Should().Be("m1");
        batch.CursorAtEnd.Should().Be(Base + "/delta?$deltatoken=new");
    }

    /// <summary>Final review I1: the same answers to the first listing (no stored cursor) are not retried: they fail.</summary>
    [Fact]
    public async Task List_SyncStateNotFound_WithoutCursor_Fails()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.BadRequest, """{"error":{"code":"syncStateNotFound"}}"""));
        await using var session = await OpenAsync(fake);

        var act = () => session.ListAsync(null, Since, 10, default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeFalse();
        fake.Graph.Count().Should().Be(2, "the open and one listing");
    }

    /// <summary>Final review I1: another 400 on a stored cursor is not an expired cursor.</summary>
    [Fact]
    public async Task List_OtherBadRequest_OnCursor_Fails()
    {
        const string stored = Base + "/delta?$deltatoken=d1";
        var fake = new Fake(_ => Json(HttpStatusCode.BadRequest, """{"error":{"code":"ErrorInvalidRequest"}}"""));
        await using var session = await OpenAsync(fake);

        var act = () => session.ListAsync(stored, Since, 10, default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Message.Should().Contain("ErrorInvalidRequest");
    }

    /// <summary>Final review I2: a 4xx on one message's MIME is about that message, not the mailbox.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task Fetch_4xx_IsAboutTheMessage(HttpStatusCode code)
    {
        var fake = new Fake(_ => Json(code, """{"error":{"code":"ErrorCorruptData"}}"""));
        await using var session = await OpenAsync(fake);

        var act = () => session.FetchAsync(new IncomingRef("m1", null, null, Since, null), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Message);
    }

    /// <summary>Final review I2: a 401 or 403 on a fetch is still the mailbox's (the token, the application's access to the mailbox).</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Fetch_401_IsAboutTheMailbox(HttpStatusCode code)
    {
        var fake = new Fake(_ => Json(code, """{"error":{"code":"InvalidAuthenticationToken"}}"""));
        await using var session = await OpenAsync(fake);

        var act = () => session.FetchAsync(new IncomingRef("m1", null, null, Since, null), default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Scope.Should().Be(MailFailureScope.Mailbox);
    }

    [Fact]
    public async Task MailProviders_IncomingFor_GivesTheProviderOfTheKind()
    {
        var fake = new Fake();
        var providers = new MailProviders(() => new HttpClient(fake, disposeHandler: false), Base);

        providers.IncomingFor(MailProviderKind.Graph).Should().BeOfType<GraphIncomingProvider>();
        providers.IncomingFor(MailProviderKind.Smtp).Should().BeOfType<ImapMailProvider>();
    }
}
