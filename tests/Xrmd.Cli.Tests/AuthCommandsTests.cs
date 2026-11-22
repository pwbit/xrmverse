using System.Text.Json;
using FluentAssertions;
using Microsoft.Identity.Client;
using Xrmd.Cli.Auth;
using Xrmd.Cli.Config;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd auth login --idp local</c> (plan 2.8, Task 5).</summary>
public sealed class AuthCommandsTests
{
    [Fact]
    public async Task Tenant_id_with_idp_local_is_a_usage_error()
    {
        using var cli = new TestCli();

        var code = await cli.RunAsync("auth", "login", "--mode", "device-code", "--idp", "local", "--url", "https://h/", "--tenant-id", Guid.NewGuid().ToString());

        code.Should().Be(2);
        cli.Err.ToString().Should().Contain("--tenant-id");
        File.Exists(new ProfileStore(cli.ConfigDir).FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task Idp_local_does_not_apply_to_dev()
    {
        using var cli = new TestCli();

        (await cli.RunAsync("auth", "login", "--mode", "dev", "--idp", "local", "--url", "https://h/")).Should().Be(2);
    }

    [Fact]
    public async Task Local_login_uses_the_adfs_authority_of_the_server_and_saves_a_profile_without_client_id()
    {
        // The fake server answers MSAL's ADFS authority validation (WebFinger), discovery and the device code request; the
        // user never completes the code, so the login ends with the device code message (exit 3) after the requests were seen.
        using var cli = new TestCli(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/webfinger")
                return FakeHandler.Json(System.Net.HttpStatusCode.OK,
                    """{"subject":"https://h","links":[{"rel":"http://schemas.microsoft.com/rel/trusted-realm","href":"https://h"}]}""");
            return FakeHandler.Json(System.Net.HttpStatusCode.BadRequest, """{"error":"invalid_request"}""");
        });

        var code = await cli.RunAsync("auth", "login", "--mode", "device-code", "--idp", "local", "--url", "https://h/");

        code.Should().Be(3, cli.Err.ToString());
        var seen = ((FakeHandler)cli.Handler).Requests.Select(r => r.RequestUri!).ToList();
        seen.Should().NotBeEmpty().And.OnlyContain(u => u.Host == "h", "MSAL never contacts Microsoft");
        seen.Should().Contain(u => u.AbsolutePath == "/.well-known/webfinger");
        seen.Should().Contain(u => u.AbsolutePath.StartsWith("/adfs/"), "the authority is <url>adfs/");
    }

    [Fact]
    public async Task Local_profile_is_saved_with_idp_local_and_no_client_id_and_msal_gets_the_adfs_authority()
    {
        var profile = new Profile { Url = "https://h/", Mode = AuthModes.DeviceCode, Idp = IdentityProviders.Local };
        using var cli = new TestCli();
        var provider = new MsalTokenProvider(profile, cli.Env());

        provider.Authority.Should().Be("https://h/adfs/");
        provider.Scopes.Should().Equal("https://h/.default");
        var app = provider.BuildPublicClient();
        app.Authority.Should().StartWith("https://h/adfs");
        app.AppConfig.ClientId.Should().Be("xrmd-cli");

        new MsalTokenProvider(profile with { Idp = null, TenantId = "t" }, cli.Env()).Authority
            .Should().Be("https://login.microsoftonline.com/t/");

        // the saved form
        new ProfileStore(cli.ConfigDir).Save(new CliConfig { Profiles = { ["p"] = profile } });
        var json = JsonDocument.Parse(File.ReadAllText(new ProfileStore(cli.ConfigDir).FilePath)).RootElement.GetProperty("profiles").GetProperty("p");
        json.GetProperty("idp").GetString().Should().Be("local");
        json.TryGetProperty("clientId", out _).Should().BeFalse();
    }

    private static HttpResponseMessage FakeLocalIdp(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/webfinger")
            return FakeHandler.Json(System.Net.HttpStatusCode.OK,
                """{"subject":"https://h","links":[{"rel":"http://schemas.microsoft.com/rel/trusted-realm","href":"https://h"}]}""");
        if (path == "/adfs/.well-known/openid-configuration")
            return FakeHandler.Json(System.Net.HttpStatusCode.OK, """
                {"issuer":"https://h/adfs","authorization_endpoint":"https://h/adfs/oauth2/authorize","token_endpoint":"https://h/adfs/oauth2/token",
                 "device_authorization_endpoint":"https://h/adfs/oauth2/devicecode","jwks_uri":"https://h/adfs/discovery/keys"}
                """);
        if (path == "/adfs/oauth2/token")
            return FakeHandler.Json(System.Net.HttpStatusCode.OK, """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_login_command_saves_idp_local_with_the_ca_file_and_a_relogin_keeps_it()
    {
        using var cli = new TestCli(FakeLocalIdp);
        cli.Vars["XRMD_CLIENT_SECRET"] = "secret";
        var ca = Path.Combine(cli.ConfigDir, "root.crt");
        Directory.CreateDirectory(cli.ConfigDir);
        File.WriteAllText(ca, "pem");

        // client-secret: MSAL completes against the fake server without a user (the fake handler also stands in for the CA file)
        var code = await cli.RunAsync("auth", "login", "--mode", "client-secret", "--idp", "local", "--client-id", "svc", "--url", "https://h/",
            "--ca-file", ca, "--profile", "p");

        code.Should().Be(0, cli.Err.ToString());
        var profile = new ProfileStore(cli.ConfigDir).Load().Profiles["p"];
        profile.Idp.Should().Be("local");
        profile.TenantId.Should().BeNull();
        profile.CaFile.Should().Be(ca);

        (await cli.RunAsync("auth", "login", "--mode", "client-secret", "--idp", "local", "--client-id", "svc", "--url", "https://h/", "--profile", "p"))
            .Should().Be(0, cli.Err.ToString());
        new ProfileStore(cli.ConfigDir).Load().Profiles["p"].CaFile.Should().Be(ca);
    }

    [Fact]
    public async Task Ca_file_needs_idp_local_and_an_existing_file()
    {
        using var cli = new TestCli();

        (await cli.RunAsync("auth", "login", "--mode", "device-code", "--client-id", "c", "--url", "https://h/", "--ca-file", "x.crt")).Should().Be(2);
        (await cli.RunAsync("auth", "login", "--mode", "device-code", "--idp", "local", "--url", "https://h/", "--ca-file", "/nonexistent.crt")).Should().Be(2);
    }
}
