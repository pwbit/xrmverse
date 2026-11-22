using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd idp user|client</c> (plan 2.8, Task 5) against a fake server.</summary>
public sealed class IdpCommandsTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static HttpResponseMessage Server(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path.EndsWith("/systemusers"))
            return FakeHandler.Json(HttpStatusCode.OK, request.RequestUri.Query.Contains("nobody")
                ? """{"value":[]}"""
                : $$"""{"value":[{"systemuserid":"{{UserId}}"}]}""");
        if (path == "/admin/idp/clients" && request.Method == HttpMethod.Post)
            return FakeHandler.Json(HttpStatusCode.OK, """{"clientId":"c-1","clientSecret":"s3cret-value"}""");
        if (path == "/admin/idp/clients" && request.Method == HttpMethod.Get)
            return FakeHandler.Json(HttpStatusCode.OK, """
                [{"clientId":"xrmd-cli","displayName":"xrmd command line","type":"public","redirectUris":["http://localhost"]},
                 {"clientId":"c-1","displayName":"svc","type":"confidential","systemuserid":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","redirectUris":[]}]
                """);
        if (path.EndsWith("/totp") && request.Method == HttpMethod.Post)
            return FakeHandler.Json(HttpStatusCode.OK, """{"otpauthUri":"otpauth://totp/x?secret=ABC"}""");
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static async Task<TestCli> LoggedInAsync()
    {
        var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(Server);
        return cli;
    }

    private static (HttpRequestMessage Request, string? Body) Last(TestCli cli, HttpMethod method) =>
        cli.Handler.Requests.Select((r, i) => (r, cli.Handler.Bodies[i])).Last(x => x.r.Method == method);

    [Fact]
    public async Task Set_password_reads_one_line_from_stdin_and_puts_it()
    {
        using var cli = await LoggedInAsync();
        cli.Input = "a long enough password\nignored\n";

        var code = await cli.RunAsync("idp", "user", "set-password", "jdoe", "--password-stdin");

        code.Should().Be(0, cli.Err.ToString());
        var (put, body) = Last(cli, HttpMethod.Put);
        put.RequestUri!.AbsolutePath.Should().Be($"/admin/idp/users/{UserId}/password");
        JsonDocument.Parse(body!).RootElement.GetProperty("password").GetString().Should().Be("a long enough password");
        cli.Out.ToString().Should().NotContain("a long enough password");
    }

    [Fact]
    public async Task Set_password_without_the_stdin_flag_is_a_usage_error_and_sends_nothing()
    {
        using var cli = await LoggedInAsync();
        cli.Input = "a long enough password\n";

        (await cli.RunAsync("idp", "user", "set-password", "jdoe")).Should().Be(2);

        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_guid_user_is_matched_as_systemuserid_or_object_id_and_a_missing_user_is_exit_4()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("idp", "user", "remove", UserId.ToString())).Should().Be(0, cli.Err.ToString());
        var query = Uri.UnescapeDataString(cli.Handler.Requests[0].RequestUri!.Query);
        query.Should().Contain($"systemuserid eq {UserId}").And.Contain("azureactivedirectoryobjectid");
        Last(cli, HttpMethod.Delete).Request.RequestUri!.AbsolutePath.Should().Be($"/admin/idp/users/{UserId}");

        (await cli.RunAsync("idp", "user", "remove", "nobody")).Should().Be(4);
    }

    [Fact]
    public async Task Totp_enable_prints_the_uri_and_disable_deletes()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("idp", "user", "totp", "enable", "jdoe")).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("otpauth://totp/x?secret=ABC");
        Last(cli, HttpMethod.Post).Request.RequestUri!.AbsolutePath.Should().Be($"/admin/idp/users/{UserId}/totp");

        (await cli.RunAsync("idp", "user", "totp", "disable", "jdoe")).Should().Be(0, cli.Err.ToString());
        Last(cli, HttpMethod.Delete).Request.RequestUri!.AbsolutePath.Should().Be($"/admin/idp/users/{UserId}/totp");
    }

    [Fact]
    public async Task Client_add_for_an_app_user_prints_the_secret_once_with_the_warning()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("idp", "client", "add", "--name", "svc", "--app-user", "svc-user")).Should().Be(0, cli.Err.ToString());

        var (post, body) = Last(cli, HttpMethod.Post);
        post.RequestUri!.AbsolutePath.Should().Be("/admin/idp/clients");
        var json = JsonDocument.Parse(body!).RootElement;
        json.GetProperty("type").GetString().Should().Be("confidential");
        json.GetProperty("systemuserid").GetGuid().Should().Be(UserId);
        json.GetProperty("displayName").GetString().Should().Be("svc");
        cli.Out.ToString().Should().Contain("s3cret-value").And.Contain("Store this secret now: it is not shown again.");
    }

    [Fact]
    public async Task Client_add_public_sends_the_redirect_uris_and_needs_one()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("idp", "client", "add", "--name", "spa", "--public")).Should().Be(2);
        (await cli.RunAsync("idp", "client", "add", "--name", "spa", "--public", "--app-user", "x", "--redirect-uri", "https://a/cb")).Should().Be(2);
        (await cli.RunAsync("idp", "client", "add", "--name", "spa")).Should().Be(2);
        cli.Handler.Requests.Should().BeEmpty();

        (await cli.RunAsync("idp", "client", "add", "--name", "spa", "--public", "--redirect-uri", "https://a/cb", "--redirect-uri", "https://b/cb"))
            .Should().Be(0, cli.Err.ToString());
        var json = JsonDocument.Parse(Last(cli, HttpMethod.Post).Body!).RootElement;
        json.GetProperty("type").GetString().Should().Be("public");
        json.GetProperty("redirectUris").EnumerateArray().Select(e => e.GetString()).Should().Equal("https://a/cb", "https://b/cb");
    }

    [Fact]
    public async Task Client_list_and_remove()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("idp", "client", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("clientId").GetString()).Should().Equal("xrmd-cli", "c-1");
        cli.Out.ToString().Should().NotContain("secret");

        (await cli.RunAsync("idp", "client", "remove", "c-1")).Should().Be(0, cli.Err.ToString());
        Last(cli, HttpMethod.Delete).Request.RequestUri!.AbsolutePath.Should().Be("/admin/idp/clients/c-1");
    }
}
