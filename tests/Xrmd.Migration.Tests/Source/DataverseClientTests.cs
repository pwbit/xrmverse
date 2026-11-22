using System.Net;
using FluentAssertions;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public class DataverseClientTests
{
    private readonly List<TimeSpan> _delays = [];

    private DataverseClient Client(FakeDataverse fake) =>
        new(fake, FakeDataverse.Source, _ => Task.FromResult("tok-123"), (d, _) => { _delays.Add(d); return Task.CompletedTask; });

    [Fact]
    public async Task Sends_bearer_and_odata_headers()
    {
        var fake = new FakeDataverse().GetJson("^/api/data/v9.2/WhoAmI$", """{"UserId":"11111111-1111-1111-1111-111111111111"}""");
        using var client = Client(fake);
        using var doc = await client.GetJsonAsync("WhoAmI", CancellationToken.None);
        doc.RootElement.GetProperty("UserId").GetString().Should().StartWith("1111");
        var r = fake.Requests.Single().Request;
        r.Headers.Authorization!.ToString().Should().Be("Bearer tok-123");
        r.Headers.GetValues("OData-MaxVersion").Should().Equal("4.0");
        r.Headers.GetValues("OData-Version").Should().Equal("4.0");
        r.Headers.GetValues("Prefer").Single().Should().Contain("odata.include-annotations=\"*\"");
    }

    [Fact]
    public async Task Retries_429_using_Retry_After()
    {
        var calls = 0;
        var fake = new FakeDataverse().Get("WhoAmI", (_, _) =>
        {
            if (calls++ > 0) return FakeDataverse.Json("{}");
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return r;
        });
        using var client = Client(fake);
        (await client.GetJsonAsync("WhoAmI", CancellationToken.None)).Dispose();
        calls.Should().Be(2);
        _delays.Should().Equal(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Retries_429_without_header_after_5_s()
    {
        var calls = 0;
        var fake = new FakeDataverse().Get("WhoAmI", (_, _) => calls++ < 3 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : FakeDataverse.Json("{}"));
        using var client = Client(fake);
        (await client.GetJsonAsync("WhoAmI", CancellationToken.None)).Dispose();
        _delays.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Gives_up_after_5_server_errors_naming_the_url()
    {
        var fake = new FakeDataverse().Get("accounts", (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = Client(fake);
        var act = () => client.GetJsonAsync("accounts?$top=1", CancellationToken.None);
        (await act.Should().ThrowAsync<DataverseException>()).Which.Message.Should().Contain("accounts").And.NotContain("tok-123");
        fake.Requests.Should().HaveCount(6);
        _delays.Should().Equal(new[] { 1, 2, 4, 8, 16 }.Select(s => TimeSpan.FromSeconds(s)));
    }

    [Fact]
    public async Task Client_errors_are_not_retried_and_keep_the_status()
    {
        var fake = new FakeDataverse().Get("accounts", (_, _) => FakeDataverse.Json("""{"error":{"code":"0x80040220","message":"no privilege"}}""", HttpStatusCode.Forbidden));
        using var client = Client(fake);
        var act = () => client.GetJsonAsync("accounts", CancellationToken.None);
        (await act.Should().ThrowAsync<DataverseException>()).Which.Status.Should().Be(HttpStatusCode.Forbidden);
        fake.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Refuses_actions_other_than_ExportSolution()
    {
        using var client = Client(new FakeDataverse());
        var act = () => client.PostActionAsync("DeleteRecordChangeHistory", new { }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Fetch_sends_fetchxml_and_returns_rows()
    {
        var fake = new FakeDataverse().GetJson(@"^/api/data/v9.2/accounts\?fetchXml=", """{"value":[{"accountid":"a"},{"accountid":"b"}]}""");
        using var client = Client(fake);
        var rows = await client.FetchAsync("accounts", "<fetch count=\"5000\"><entity name=\"account\"/></fetch>", CancellationToken.None);
        rows.Select(r => r.GetProperty("accountid").GetString()).Should().Equal("a", "b");
        fake.Requests.Single().Url.Should().Contain("<entity name=\"account\"/>");
    }

    [Fact]
    public async Task Get_all_follows_next_links()
    {
        var fake = new FakeDataverse()
            .GetJson(@"^/api/data/v9\.2/roles\?\$select=name$", """{"value":[{"name":"a"}],"@odata.nextLink":"https://contoso.example.com/api/data/v9.2/roles?$select=name&$skiptoken=x"}""")
            .GetJson(@"skiptoken=x", """{"value":[{"name":"b"}]}""");
        using var client = Client(fake);
        (await client.GetAllAsync("roles?$select=name", CancellationToken.None)).Select(r => r.GetProperty("name").GetString()).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Get_all_refuses_a_next_link_to_another_host()
    {
        var fake = new FakeDataverse().GetJson("roles", """{"value":[],"@odata.nextLink":"https://evil.example.net/api/data/v9.2/roles?$skiptoken=x"}""");
        using var client = Client(fake);
        await FluentActions.Awaiting(() => client.GetAllAsync("roles", CancellationToken.None)).Should().ThrowAsync<DataverseException>();
        fake.Requests.Should().ContainSingle();
    }
}
