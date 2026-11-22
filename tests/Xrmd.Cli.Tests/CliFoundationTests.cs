using System.CommandLine;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Xrmd.Cli;
using Xrmd.Cli.Auth;
using Xrmd.Cli.Config;
using Xrmd.Cli.Http;
using Xrmd.Cli.Output;

namespace Xrmd.Cli.Tests;

public class CliFoundationTests
{
    private const string WhoAmI =
        """{"BusinessUnitId":"11111111-1111-1111-1111-111111111111","UserId":"22222222-2222-2222-2222-222222222222","OrganizationId":"33333333-3333-3333-3333-333333333333"}""";

    [Theory]
    [InlineData(401, 3)]
    [InlineData(403, 3)]
    [InlineData(404, 4)]
    [InlineData(400, 5)]
    [InlineData(409, 5)]
    [InlineData(412, 5)]
    [InlineData(500, 1)]
    public async Task Exit_codes_follow_the_table(int status, int expected)
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(_ => FakeHandler.Json((HttpStatusCode)status,
            $$$"""{"error":{"code":"0x80040217","message":"Server says {{{status}}}."}}"""));

        var code = await cli.RunAsync("auth", "status");

        code.Should().Be(expected);
        cli.Err.ToString().Trim().Should().Be($"Server says {status}.");
        cli.Out.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Exit_codes_follow_the_table_connection_refused_is_6()
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0);
        cli.Handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused (xrmd.test:5000)",
            new SocketException((int)SocketError.ConnectionRefused)));

        var code = await cli.RunAsync("auth", "status");

        code.Should().Be(6);
        cli.Err.ToString().Should().Contain("http://xrmd.test:5000/").And.Contain("Connection refused");
    }

    [Fact]
    public async Task Status_without_an_error_body_still_maps_and_says_the_status()
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0);
        cli.Handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        (await cli.RunAsync("auth", "status")).Should().Be(3);
        cli.Err.ToString().Should().Contain("401");
    }

    [Fact]
    public async Task Json_output_writes_only_json_to_stdout()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, WhoAmI));
        (await cli.RunAsync("auth", "login", "--url", "http://xrmd.test:5000", "--mode", "dev", "--output", "json")).Should().Be(0);
        using (var login = JsonDocument.Parse(cli.Out.ToString()))
            login.RootElement.GetProperty("profile").GetString().Should().Be("default");

        var code = await cli.RunAsync("auth", "status", "--output", "json");

        code.Should().Be(0, cli.Err.ToString());
        var stdout = cli.Out.ToString();
        stdout.TrimStart().Should().StartWith("{");
        using var doc = JsonDocument.Parse(stdout); // throws if anything but one JSON value is on stdout
        doc.RootElement.GetProperty("userId").GetString().Should().Be("22222222-2222-2222-2222-222222222222");
        doc.RootElement.GetProperty("url").GetString().Should().Be("http://xrmd.test:5000/");
        doc.RootElement.GetProperty("mode").GetString().Should().Be("dev");
    }

    [Fact]
    public async Task Table_output_is_the_default()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, WhoAmI));
        (await cli.LoginDevAsync()).Should().Be(0);

        (await cli.RunAsync("auth", "status")).Should().Be(0);

        var stdout = cli.Out.ToString();
        stdout.Should().MatchRegex(@"(?m)^userId\s+22222222-2222-2222-2222-222222222222$");
        stdout.TrimStart().Should().NotStartWith("{");
    }

    [Fact]
    public async Task Dev_token_has_the_claims_of_dev_token_sh()
    {
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, WhoAmI)) { Clock = new FixedClock(now) };
        (await cli.RunAsync("auth", "login", "--url", "http://xrmd.test:5000", "--mode", "dev",
            "--object-id", "44444444-4444-4444-4444-444444444444")).Should().Be(0, cli.Err.ToString());

        (await cli.RunAsync("auth", "status")).Should().Be(0, cli.Err.ToString());

        var auth = cli.Handler.Requests.Single().Headers.Authorization!;
        auth.Scheme.Should().Be("Bearer");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.Parameter);
        jwt.Header.Alg.Should().Be("HS256");
        jwt.Payload.Iss.Should().Be("xrmverse-dev");
        jwt.Payload.Aud.Should().Equal("http://xrmd.test:5000/");
        jwt.Payload["oid"].Should().Be("44444444-4444-4444-4444-444444444444");
        jwt.Payload["name"].Should().Be("Administrator");
        jwt.Payload.NotBefore.Should().Be(now.ToUnixTimeSeconds());
        jwt.Payload.Expiration.Should().Be(now.ToUnixTimeSeconds() + 3600);

        var parameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = "xrmverse-dev",
            ValidAudience = "http://xrmd.test:5000/",
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(TestCli.SigningKey)),
            ValidateLifetime = false,
        };
        new JwtSecurityTokenHandler().ValidateToken(auth.Parameter, parameters, out _); // signed with the key
    }

    [Fact]
    public async Task Missing_signing_key_env_is_exit_3_with_the_variable_name()
    {
        using var cli = new TestCli();
        cli.Vars.Clear();

        (await cli.LoginDevAsync()).Should().Be(3);
        cli.Err.ToString().Should().Contain("XRMD_DEV_SIGNING_KEY");

        (await cli.RunAsync("auth", "login", "--url", "http://xrmd.test:5000", "--mode", "dev",
            "--signing-key-env", "MY_XRMD_KEY")).Should().Be(3);
        cli.Err.ToString().Should().Contain("MY_XRMD_KEY");

        // A profile saved while the key was set fails the same way once the variable is gone.
        cli.Vars["MY_XRMD_KEY"] = TestCli.SigningKey;
        (await cli.RunAsync("auth", "login", "--url", "http://xrmd.test:5000", "--mode", "dev",
            "--signing-key-env", "MY_XRMD_KEY")).Should().Be(0);
        cli.Vars.Clear();
        (await cli.RunAsync("auth", "status")).Should().Be(3);
        cli.Err.ToString().Should().Contain("MY_XRMD_KEY");
        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Profile_file_has_no_secrets()
    {
        using var cli = new TestCli(MsalTokenEndpoint);
        cli.Vars["XRMD_CLIENT_SECRET"] = "client-secret-value-do-not-store";

        (await cli.RunAsync("auth", "login", "--url", "http://xrmd.test:5000", "--profile", "local", "--mode", "dev")).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("auth", "login", "--url", "https://xrmd.example.com", "--profile", "ci", "--mode", "client-secret",
            "--client-id", "55555555-5555-5555-5555-555555555555", "--tenant-id", "66666666-6666-6666-6666-666666666666")).Should().Be(0, cli.Err.ToString());

        var text = File.ReadAllText(Path.Combine(cli.ConfigDir, "config.json"));
        text.Should().NotContain(TestCli.SigningKey).And.NotContain("client-secret-value-do-not-store").And.NotContain("access-token-from-entra");
        text.Should().NotContainEquivalentOf("secret\":"); // no property holding a secret value
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("currentProfile").GetString().Should().Be("ci");
        var ci = doc.RootElement.GetProperty("profiles").GetProperty("ci");
        ci.GetProperty("url").GetString().Should().Be("https://xrmd.example.com/");
        ci.GetProperty("mode").GetString().Should().Be("client-secret");
        ci.GetProperty("clientId").GetString().Should().Be("55555555-5555-5555-5555-555555555555");
        doc.RootElement.GetProperty("profiles").GetProperty("local").GetProperty("signingKeyEnv").GetString().Should().Be("XRMD_DEV_SIGNING_KEY");
    }

    [Fact]
    public async Task Client_secret_login_requires_the_secret_env_and_is_exit_3()
    {
        using var cli = new TestCli(MsalTokenEndpoint);
        (await cli.RunAsync("auth", "login", "--url", "https://xrmd.example.com", "--mode", "client-secret",
            "--client-id", "55555555-5555-5555-5555-555555555555", "--tenant-id", "66666666-6666-6666-6666-666666666666")).Should().Be(3);
        cli.Err.ToString().Should().Contain("XRMD_CLIENT_SECRET");
        File.Exists(Path.Combine(cli.ConfigDir, "config.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Client_secret_token_is_requested_once_per_command()
    {
        using var cli = new TestCli();
        cli.Vars["XRMD_CLIENT_SECRET"] = "s";
        var profile = new Profile { Url = "https://xrmd.example.com/", Mode = AuthModes.ClientSecret, ClientId = "55555555-5555-5555-5555-555555555555", TenantId = "66666666-6666-6666-6666-666666666666" };
        cli.Handler = new FakeHandler(MsalTokenEndpoint);
        var provider = new MsalTokenProvider(profile, cli.Env());

        (await provider.GetTokenAsync(default)).Should().Be("access-token-from-entra");
        (await provider.GetTokenAsync(default)).Should().Be("access-token-from-entra");

        cli.Handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)).Should().Be(1);
        File.Exists(Path.Combine(cli.ConfigDir, "msal-cache.bin")).Should().BeFalse();
    }

    [Fact]
    public void Msal_clients_use_the_profile_client_id_and_the_url_default_scope()
    {
        using var cli = new TestCli();
        var profile = new Profile { Url = "https://xrmd.example.com/", Mode = AuthModes.DeviceCode, ClientId = "55555555-5555-5555-5555-555555555555", TenantId = "66666666-6666-6666-6666-666666666666" };
        var provider = new MsalTokenProvider(profile, cli.Env());

        provider.Scopes.Should().Equal("https://xrmd.example.com/.default");
        var app = provider.BuildPublicClient();
        app.AppConfig.ClientId.Should().Be("55555555-5555-5555-5555-555555555555");
        provider.Authority.Should().Be("https://login.microsoftonline.com/66666666-6666-6666-6666-666666666666/");
        provider.CachePath.Should().Be(Path.Combine(cli.ConfigDir, "msal-cache.bin"));

        new MsalTokenProvider(profile with { TenantId = null }, cli.Env()).Authority
            .Should().Be("https://login.microsoftonline.com/organizations/");
    }

    [Fact]
    public void Cache_file_is_0600_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new TestCli();
        var path = Path.Combine(cli.ConfigDir, "msal-cache.bin");

        new MsalCacheFile(path).Write([1, 2, 3]);
        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(cli.ConfigDir).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // An existing, wider file is replaced by a 0600 one.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        new MsalCacheFile(path).Write([4, 5]);
        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        new MsalCacheFile(path).Read().Should().Equal(4, 5);
    }

    [Fact]
    public async Task Destructive_without_tty_and_yes_is_exit_2()
    {
        using var cli = new TestCli { IsInputTty = false };
        var ran = false;
        Command Group(CliContext ctx)
        {
            var cmd = new Command("wipe");
            var yes = new Option<bool>("--yes");
            cmd.Add(yes);
            cmd.SetAction(async (parse, ct) =>
            {
                await ctx.ConfirmDestructiveAsync("Wipe everything?", parse.GetValue(yes));
                ran = true;
                return 0;
            });
            return cmd;
        }

        (await cli.RunWithAsync(Group, "wipe")).Should().Be(2);
        cli.Err.ToString().Trim().Should().Be("Refusing to run a destructive command without --yes when stdin is not a terminal.");
        ran.Should().BeFalse();

        (await cli.RunWithAsync(Group, "wipe", "--yes")).Should().Be(0);
        ran.Should().BeTrue();
    }

    [Fact]
    public async Task Destructive_with_tty_asks_and_runs_only_on_yes()
    {
        using var cli = new TestCli { IsInputTty = true, Input = "n\n" };
        var ran = false;
        Command Group(CliContext ctx)
        {
            var cmd = new Command("wipe");
            cmd.SetAction(async (_, _) => { await ctx.ConfirmDestructiveAsync("Wipe everything?", yes: false); ran = true; return 0; });
            return cmd;
        }

        (await cli.RunWithAsync(Group, "wipe")).Should().Be(1);
        cli.Err.ToString().Should().Contain("Wipe everything?");
        ran.Should().BeFalse();

        cli.Input = "y\n";
        (await cli.RunWithAsync(Group, "wipe")).Should().Be(0);
        ran.Should().BeTrue();
    }

    [Fact]
    public async Task No_profile_is_exit_3_even_with_url()
    {
        using var cli = new TestCli();

        (await cli.RunAsync("auth", "status", "--url", "http://xrmd.test:5000")).Should().Be(3);
        cli.Err.ToString().Trim().Should().Be("No profile: run `xrmd auth login`.");
    }

    [Fact]
    public async Task Usage_errors_are_exit_2()
    {
        using var cli = new TestCli();

        (await cli.RunAsync("auth", "status", "--bogus")).Should().Be(2);
        (await cli.RunAsync("auth", "login", "--mode", "dev")).Should().Be(2); // no --url
        cli.Err.ToString().Should().Contain("--url");
        (await cli.RunAsync("auth", "login", "--url", "http://x", "--mode", "nope")).Should().Be(2);
        (await cli.RunAsync("auth", "login", "--url", "http://x", "--mode", "dev", "--client-id", "abc")).Should().Be(2);
        cli.Err.ToString().Should().Contain("--client-id");
        (await cli.RunAsync("auth", "login", "--url", "http://x", "--mode", "device-code")).Should().Be(2);
        cli.Err.ToString().Should().Contain("--client-id");
        (await cli.RunAsync("auth", "status", "--output", "xml")).Should().Be(2);
    }

    [Fact]
    public async Task Logout_removes_the_profile()
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0);

        (await cli.RunAsync("auth", "logout")).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("auth", "status")).Should().Be(3);
        (await cli.RunAsync("auth", "logout", "--profile", "missing")).Should().Be(3); // "profile not found" is 3 everywhere
    }

    [Fact]
    public async Task Url_option_overrides_the_profile_url()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, WhoAmI));
        (await cli.LoginDevAsync()).Should().Be(0);

        (await cli.RunAsync("auth", "status", "--url", "http://other.test:5001")).Should().Be(0, cli.Err.ToString());

        cli.Handler.Requests.Single().RequestUri.Should().Be(new Uri("http://other.test:5001/api/data/v9.2/WhoAmI"));
    }

    [Fact]
    public async Task Client_sends_odata_headers_and_follows_next_link()
    {
        var handler = new FakeHandler(req => req.RequestUri!.Query.Contains("page=2")
            ? FakeHandler.Json(HttpStatusCode.OK, """{"value":[{"n":3}]}""")
            : FakeHandler.Json(HttpStatusCode.OK, """{"value":[{"n":1},{"n":2}],"@odata.nextLink":"http://xrmd.test:5000/api/data/v9.2/accounts?page=2"}"""));
        var client = new XrmdClient(new HttpClient(handler), new Uri("http://xrmd.test:5000/"), _ => Task.FromResult("tok"));

        var rows = await client.GetAllPagesAsync("accounts?$select=name");

        rows.Select(r => r.GetProperty("n").GetInt32()).Should().Equal(1, 2, 3);
        handler.Requests.Should().HaveCount(2);
        var first = handler.Requests[0];
        first.RequestUri.Should().Be(new Uri("http://xrmd.test:5000/api/data/v9.2/accounts?$select=name"));
        first.Headers.Authorization!.ToString().Should().Be("Bearer tok");
        first.Headers.Accept.ToString().Should().Be("application/json");
        first.Headers.GetValues("OData-MaxVersion").Should().Equal("4.0");
        first.Headers.GetValues("OData-Version").Should().Equal("4.0");
        first.Headers.GetValues("Prefer").Should().Equal("odata.maxpagesize=5000");
    }

    [Fact]
    public async Task Client_admin_and_send_json()
    {
        var handler = new FakeHandler(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/data/v9.2/accounts"
            ? new HttpResponseMessage(HttpStatusCode.NoContent) { Headers = { { "OData-EntityId", "http://xrmd.test:5000/api/data/v9.2/accounts(77777777-7777-7777-7777-777777777777)" } } }
            : FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}"""));
        var client = new XrmdClient(new HttpClient(handler), new Uri("http://xrmd.test:5000/"), null);

        var created = await client.SendJsonAsync(HttpMethod.Post, "accounts", new Dictionary<string, object?> { ["name"] = "Acme" });
        created.EntityId.Should().Be(Guid.Parse("77777777-7777-7777-7777-777777777777"));
        handler.Bodies[0].Should().Be("""{"name":"Acme"}""");
        handler.Requests[0].Headers.Contains("Prefer").Should().BeFalse();

        var health = await client.AdminAsync(HttpMethod.Get, "health");
        health.Json!.Value.GetProperty("status").GetString().Should().Be("Healthy");
        handler.Requests[1].RequestUri.Should().Be(new Uri("http://xrmd.test:5000/admin/health"));
        handler.Requests[1].Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public void Renderer_aligns_table_columns_and_indents_json()
    {
        var output = new StringWriter();
        var rows = new[] { new { name = "a", size = 1 }, new { name = "longer", size = 1234 } };

        new Renderer(output).Write(rows, OutputFormat.Table);
        output.ToString().Replace("\r\n", "\n").Should().Be("name    size\na       1\nlonger  1234\n");

        output.GetStringBuilder().Clear();
        new Renderer(output).Write(rows, OutputFormat.Json);
        output.ToString().Should().Contain("\n  {").And.Contain("\"name\": \"a\"");
        JsonDocument.Parse(output.ToString()).RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Real_http_client_has_no_timeout_and_a_slow_response_still_succeeds()
    {
        using (var real = new TestCli())
            new CliContext(real.Env() with { Handler = null }).Http.Timeout.Should().Be(Timeout.InfiniteTimeSpan);

        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, WhoAmI));
        (await cli.LoginDevAsync()).Should().Be(0);
        cli.Handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, WhoAmI)) { Delay = TimeSpan.FromMilliseconds(300) };
        new CliContext(cli.Env()).Http.Timeout.Should().Be(Timeout.InfiniteTimeSpan);

        (await cli.RunAsync("auth", "status")).Should().Be(0, cli.Err.ToString());
    }

    [Theory]
    [InlineData("https://evil.test/api/data/v9.2/accounts?page=2")]
    [InlineData("http://xrmd.test:5001/api/data/v9.2/accounts?page=2")]
    [InlineData("https://xrmd.test:5000/api/data/v9.2/accounts?page=2")]
    public async Task Next_link_to_another_origin_is_refused_without_sending_the_token(string link)
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, $$$"""{"value":[{"n":1}],"@odata.nextLink":"{{{link}}}"}"""));
        var client = new XrmdClient(new HttpClient(handler), new Uri("http://xrmd.test:5000/"), _ => Task.FromResult("tok"));

        var act = () => client.GetAllPagesAsync("accounts");

        var error = (await act.Should().ThrowAsync<CliException>()).Which;
        error.ExitCode.Should().Be(1);
        error.Message.Should().Contain(link);
        handler.Requests.Should().ContainSingle(); // the token never left for the other origin
    }

    [Fact]
    public async Task Response_exposes_binary_bodies()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x00, 0xFF, 0xFE };
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/zip") } },
        });
        var client = new XrmdClient(new HttpClient(handler), new Uri("http://xrmd.test:5000/"), null);

        var response = await client.AdminAsync(HttpMethod.Post, "orgs/xrmd/backup");

        response.Bytes.Should().Equal(bytes);
        response.Json.Should().BeNull();
    }

    [Fact]
    public async Task Transport_error_while_reading_the_body_is_exit_6()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
        var client = new XrmdClient(new HttpClient(handler), new Uri("http://xrmd.test:5000/"), null);

        var act = () => client.AdminAsync(HttpMethod.Get, "health");

        (await act.Should().ThrowAsync<CliException>()).Which.ExitCode.Should().Be(6);
    }

    [Fact]
    public async Task Shared_yes_option_serves_every_destructive_command()
    {
        using var cli = new TestCli { IsInputTty = false };
        var ran = new List<string>();
        Command Group(CliContext ctx)
        {
            Command Destructive(string name)
            {
                var cmd = new Command(name) { ctx.YesOption };
                cmd.SetAction(async (_, _) => { await ctx.ConfirmDestructiveAsync($"{name}?"); ran.Add(name); return 0; });
                return cmd;
            }
            return new Command("danger") { Destructive("one"), Destructive("two") };
        }

        (await cli.RunWithAsync(Group, "danger", "one")).Should().Be(2);
        cli.Err.ToString().Trim().Should().Be(CliContext.RefuseDestructiveMessage);
        (await cli.RunWithAsync(Group, "danger", "one", "--yes")).Should().Be(0);
        (await cli.RunWithAsync(Group, "danger", "two", "-y")).Should().Be(0);
        ran.Should().Equal("one", "two");
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Connection reset by peer.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage MsalTokenEndpoint(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal)
            ? FakeHandler.Json(HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":3600,"ext_expires_in":3600,"access_token":"access-token-from-entra"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound);
}
