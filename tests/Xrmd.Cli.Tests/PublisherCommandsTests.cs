using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd publisher create|list</c> over the Web API <c>publishers</c> entity set (a fake server).</summary>
public class PublisherCommandsTests
{
    private const string Existing = """
        {"value":[{"publisherid":"11111111-1111-1111-1111-111111111111","uniquename":"DefaultPublisherOrg","friendlyname":"Default",
        "customizationprefix":"new","customizationoptionvalueprefix":10000}]}
        """;

    private static readonly Guid CreatedId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static HttpResponseMessage Server(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get) return FakeHandler.Json(HttpStatusCode.OK, Existing);
        var created = new HttpResponseMessage(HttpStatusCode.NoContent);
        created.Headers.Add("OData-EntityId", $"http://xrmd.test:5000/api/data/v9.2/publishers({CreatedId})");
        return created;
    }

    [Fact]
    public async Task Publisher_create_and_list()
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(Server);

        var code = await cli.RunAsync("publisher", "create", "--unique-name", "AcmeSoft", "--friendly-name", "Acme Soft", "--prefix", "acme",
            "--option-value-prefix", "12345", "-o", "json");

        code.Should().Be(0, cli.Err.ToString());
        var post = cli.Handler.Requests.Single(r => r.Method == HttpMethod.Post);
        post.RequestUri!.AbsolutePath.Should().EndWith("/publishers");
        var body = JsonDocument.Parse(cli.Handler.Bodies[cli.Handler.Requests.IndexOf(post)]!).RootElement;
        body.GetProperty("uniquename").GetString().Should().Be("AcmeSoft");
        body.GetProperty("friendlyname").GetString().Should().Be("Acme Soft");
        body.GetProperty("customizationprefix").GetString().Should().Be("acme");
        body.GetProperty("customizationoptionvalueprefix").GetInt32().Should().Be(12345);
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("publisherid").GetString().Should().Be(CreatedId.ToString());

        (await cli.RunAsync("publisher", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var row = JsonDocument.Parse(cli.Out.ToString()).RootElement.EnumerateArray().Single();
        row.GetProperty("uniquename").GetString().Should().Be("DefaultPublisherOrg");
        row.GetProperty("customizationprefix").GetString().Should().Be("new");
        row.GetProperty("customizationoptionvalueprefix").GetInt32().Should().Be(10000);
    }

    [Theory]
    [InlineData("a", "10000")]            // too short
    [InlineData("abcdefghi", "10000")]    // too long
    [InlineData("Acme", "10000")]         // uppercase
    [InlineData("1abc", "10000")]         // starts with a digit
    [InlineData("ab_c", "10000")]         // not a letter or digit
    [InlineData("mscrmx", "10000")]       // reserved
    [InlineData("acme", "9999")]          // option value prefix out of range
    [InlineData("acme", "100000")]
    public async Task Publisher_create_rejects_bad_prefix(string prefix, string optionValuePrefix)
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(Server);

        var code = await cli.RunAsync("publisher", "create", "--unique-name", "AcmeSoft", "--prefix", prefix, "--option-value-prefix", optionValuePrefix);

        code.Should().Be(2);
        cli.Handler.Requests.Should().BeEmpty("nothing is sent for an invalid value");
    }

    [Theory]
    [InlineData("DefaultPublisherOrg", "acme")] // unique name taken
    [InlineData("AcmeSoft", "new")]             // prefix taken
    public async Task Publisher_create_refuses_a_duplicate_without_creating(string uniqueName, string prefix)
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(Server);

        var code = await cli.RunAsync("publisher", "create", "--unique-name", uniqueName, "--prefix", prefix, "--option-value-prefix", "12345");

        code.Should().Be(1);
        cli.Err.ToString().Should().Contain("already");
        cli.Handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }
}
