using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Identity.Client;
using MimeKit;
using Xrmd.Core.Mail;

namespace Xrmd.Core.Tests.Mail;

public sealed class GraphMailProviderTests
{
    private static readonly MailProfile Profile = new(null, null, false, 4, "tenant-1", "client-1", 0);
    private static readonly MailCredentials Creds = new(null, null, "s3cret");

    private sealed record Seen(HttpMethod Method, string Url, string? Auth, string? ContentType, string Body);

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage>? graph = null) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType, body));
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"access_token":"tok-1","token_type":"Bearer","expires_in":3600}""");
            }

            if (graph is not null)
            {
                return graph(request);
            }

            return url.EndsWith("/send", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Accepted)
                : Json(HttpStatusCode.Created, """{"id":"AAMk=draft/1"}""");
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => new(h, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GraphMailProvider Provider(Fake fake) =>
        new(() => new HttpClient(fake, disposeHandler: false), "https://graph.test/v1.0", new Factory(fake));

    private static MimeMessage Message()
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Sales", "sales@example.com"));
        m.To.Add(new MailboxAddress("", "to@example.com"));
        m.Subject = "Hi";
        m.MessageId = "id1@example.com";
        m.Body = new TextPart("html") { Text = "<p>x</p>" };
        return m;
    }

    [Fact]
    public async Task Send_CreatesDraftFromMime_ThenSends()
    {
        var fake = new Fake();

        await Provider(fake).SendAsync(Message(), Profile, Creds, default);

        var graph = fake.Requests.Where(r => r.Url.StartsWith("https://graph.test", StringComparison.Ordinal)).ToList();
        graph.Should().HaveCount(2);
        graph[0].Method.Should().Be(HttpMethod.Post);
        graph[0].Url.Should().Be("https://graph.test/v1.0/users/sales%40example.com/messages");
        graph[0].ContentType.Should().Be("text/plain");
        graph[0].Auth.Should().Be("Bearer tok-1");
        Encoding.UTF8.GetString(Convert.FromBase64String(graph[0].Body)).Should().ContainEquivalentOf("Message-ID: <id1@example.com>");
        graph[1].Url.Should().Be("https://graph.test/v1.0/users/sales%40example.com/messages/AAMk%3Ddraft%2F1/send");
        graph[1].Auth.Should().Be("Bearer tok-1");
        fake.Requests.Should().Contain(r => r.Url.StartsWith("https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_401_IsPermanent()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken"}}"""));

        var act = () => Provider(fake).SendAsync(Message(), Profile, Creds, default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Mailbox, "the mailbox's credentials are refused");
        e.Message.Should().Contain("401").And.Contain("InvalidAuthenticationToken").And.NotContain("s3cret");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task Send_400Or413_IsPermanent_AndAboutTheMessageOnly(HttpStatusCode code)
    {
        var fake = new Fake(_ => Json(code, """{"error":{"code":"ErrorInvalidRecipients"}}"""));

        var act = () => Provider(fake).SendAsync(Message(), Profile, Creds, default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Send_429Or503_IsTransient(HttpStatusCode code)
    {
        var fake = new Fake(_ => new HttpResponseMessage(code));

        var act = () => Provider(fake).SendAsync(Message(), Profile, Creds, default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Authority_FollowsServerAuthority(int authority)
    {
        GraphMailProvider.AuthorityFor(Profile with { ServerAuthority = authority }).AbsoluteUri
            .Should().Be("https://login.microsoftonline.com/tenant-1");
    }

    [Fact]
    public void Authority_OtherCloud_IsPermanentRefusal()
    {
        var act = () => GraphMailProvider.AuthorityFor(Profile with { ServerAuthority = 2 });
        act.Should().Throw<MailSendException>().Which.Transient.Should().BeFalse();
    }

    [Fact]
    public async Task Test_ReadsDraftsFolder_WithoutSending()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"id":"x"}"""));

        await Provider(fake).TestAsync("sales@example.com", Profile, Creds, default);

        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post && r.Url.StartsWith("https://graph.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenFailure_InvalidClient_IsPermanent()
    {
        var fake = new Fake();
        var p = new GraphMailProvider(() => new HttpClient(fake, false), "https://graph.test/v1.0", new Factory(new Reject()));

        var act = () => p.SendAsync(Message(), Profile, Creds, default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeFalse();
    }

    private sealed class Reject : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Json(HttpStatusCode.BadRequest, """{"error":"invalid_client","error_description":"bad secret"}"""));
    }

    [Fact]
    public async Task Send_Failing_DeletesTheDraft_AndKeepsTheClassification()
    {
        var fake = new Fake(r => r.RequestUri!.AbsoluteUri.EndsWith("/send", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : r.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.Created, """{"id":"D1"}"""));

        var act = () => Provider(fake).SendAsync(Message(), Profile, Creds, default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeTrue();
        fake.Requests.Should().Contain(r => r.Method == HttpMethod.Delete && r.Url == "https://graph.test/v1.0/users/sales%40example.com/messages/D1");
    }

    [Fact]
    public async Task Send_Failing_WithFailingDelete_StillThrowsTheOriginal()
    {
        var fake = new Fake(r => r.Method == HttpMethod.Delete ? throw new HttpRequestException("boom")
            : r.RequestUri!.AbsoluteUri.EndsWith("/send", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json(HttpStatusCode.Created, """{"id":"D1"}"""));

        var act = () => Provider(fake).SendAsync(Message(), Profile, Creds, default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeFalse();
    }

    [Fact]
    public void TokenFailureClassifier()
    {
        GraphMailProvider.IsTransientTokenFailure(new MsalServiceException(MsalError.RequestTimeout, "t")).Should().BeTrue();
        GraphMailProvider.IsTransientTokenFailure(new MsalServiceException(MsalError.ServiceNotAvailable, "t")).Should().BeTrue();
        GraphMailProvider.IsTransientTokenFailure(new MsalServiceException("x", "t", 429, "", null)).Should().BeTrue();
        GraphMailProvider.IsTransientTokenFailure(new MsalServiceException("x", "t", 503, "", null)).Should().BeTrue();
        GraphMailProvider.IsTransientTokenFailure(new MsalServiceException("x", "t", new TaskCanceledException())).Should().BeTrue();
        GraphMailProvider.IsTransientTokenFailure(new MsalServiceException("x", "t", new IOException())).Should().BeTrue();
        GraphMailProvider.IsTransientTokenFailure(new MsalServiceException("invalid_client", "t", 401, "", null)).Should().BeFalse();
    }

    [Fact]
    public async Task TokenTimeout_FromTheHttpClient_IsTransient()
    {
        var p = new GraphMailProvider(() => new HttpClient(new Fake(), false), "https://graph.test/v1.0", new Factory(new Throwing()));

        var act = () => p.SendAsync(Message(), Profile, Creds, default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeTrue();
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new TaskCanceledException("timeout", new TimeoutException());
    }
}
