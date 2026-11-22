using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Tokens;
using Xrmd.Host.Idp;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static Xrmd.Integration.Tests.Idp.IdpBrowser;
using static Xrmd.Integration.Tests.Idp.LocalIdpTestHost;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>
/// Plan 2.8, Task 4: the interactive flows of the local identity provider. An <see cref="HttpClient"/> with cookies plays the
/// browser: authorization code with PKCE, the login and TOTP pages, device code (with MSAL.NET), refresh, and the redirect rules
/// of Review Focus 1.
/// </summary>
[Collection("xrmd")]
public sealed class InteractiveFlowTests(XrmdFixture fx)
{
    private const string Password = "correct horse battery";
    private const string Loopback = "http://localhost:54321/";
    private const string ApiScope = Audience + ".default";

    [Fact]
    public async Task Auth_code_with_pkce_signs_in_and_returns_a_token()
    {
        await using var app = Create(fx);
        var (userId, oid) = await LocalUserAsync(app, "kim@example.com", Password);
        var browser = Browser(app);
        var (verifier, challenge) = Pkce();

        var authorize = await browser.GetAsync(AuthorizeUrl(IdpClients.Cli, Loopback, challenge, $"openid {ApiScope}"));
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        authorize.Headers.Location!.AbsolutePath.Should().Be("/adfs/login");
        var login = await browser.GetAsync(authorize.Headers.Location);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var signedIn = await SubmitAsync(browser, await login.Content.ReadAsStringAsync(),
            new() { ["username"] = "KIM@example.com", ["password"] = Password });
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var back = await browser.GetAsync(signedIn.Headers.Location);
        var code = CodeFrom(back, Loopback);

        var token = await RedeemAsync(app, code, verifier, Loopback);

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token.GetProperty("access_token").GetString());
        jwt.Claims.Should().Contain(c => c.Type == "oid" && c.Value == oid.ToString());
        jwt.Subject.Should().Be(userId.ToString());
        jwt.Audiences.Should().Equal(Audience);
        var whoAmI = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/WhoAmI");
        whoAmI.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        var res = await Api(app).SendAsync(whoAmI);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("UserId").GetGuid().Should().Be(userId);
    }

    [Fact]
    public async Task Missing_pkce_is_refused()
    {
        await using var app = Create(fx);
        await LocalUserAsync(app, "lee@example.com", Password);
        var browser = Browser(app);
        await SignInAsync(browser, "lee@example.com", Password);

        var res = await browser.GetAsync(
            $"/adfs/oauth2/authorize?client_id={IdpClients.Cli}&response_type=code&redirect_uri={Uri.EscapeDataString(Loopback)}&scope=openid&state=s1");

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        res.Headers.Location.Should().BeNull();
        body.Should().Contain(Errors.InvalidRequest).And.Contain("code_challenge");
    }

    [Theory]
    [InlineData("https://evil/")]
    [InlineData("http://localhost.evil.com/")]
    [InlineData("http://localhost@evil.com/")]
    [InlineData("https://localhost:54321/")]
    [InlineData("http://127.0.0.1:54321/")]
    [InlineData("http://localhost:54321/other")]
    [InlineData("http://localhost:54321/?next=https://evil/")]
    public async Task Unregistered_redirect_uri_is_refused(string redirectUri)
    {
        await using var app = Create(fx);
        await LocalUserAsync(app, "mia@example.com", Password);
        var browser = Browser(app);
        await SignInAsync(browser, "mia@example.com", Password);
        var (_, challenge) = Pkce();

        var res = await browser.GetAsync(AuthorizeUrl(IdpClients.Cli, redirectUri, challenge, "openid"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        res.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Totp_user_is_asked_for_a_code()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "ned@example.com", Password);
        await WithCredentialsAsync(app, c => c.EnableTotpAsync(userId, default));
        var key = (byte[])(await ScalarAsync(app, "SELECT [TotpKey] FROM auth.credential WHERE [SystemUserId] = @id", ("@id", userId)))!;
        var browser = Browser(app);
        var (verifier, challenge) = Pkce();

        var authorize = await browser.GetAsync(AuthorizeUrl(IdpClients.Cli, Loopback, challenge, "openid"));
        var login = await browser.GetAsync(authorize.Headers.Location);
        var asked = await SubmitAsync(browser, await login.Content.ReadAsStringAsync(),
            new() { ["username"] = "ned@example.com", ["password"] = Password });
        asked.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await asked.Content.ReadAsStringAsync();
        page.Should().Contain("name=\"totp\"").And.NotContain(Password);

        var right = Totp.Compute(key, Step());
        var wrong = await SubmitAsync(browser, page, new() { ["totp"] = right == "000000" ? "111111" : "000000" });
        wrong.StatusCode.Should().Be(HttpStatusCode.OK, "a wrong code shows the sign-in page again");
        (await wrong.Content.ReadAsStringAsync()).Should().Contain(
            "The user name, password or code is incorrect, or the account is temporarily locked.");
        var signedIn = await SubmitAsync(browser, page, new() { ["totp"] = right });
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = CodeFrom(await browser.GetAsync(signedIn.Headers.Location), Loopback);
        (await RedeemAsync(app, code, verifier, Loopback)).TryGetProperty("access_token", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Device_code_flow_completes_after_verification()
    {
        await using var app = Create(fx);
        var (userId, oid) = await LocalUserAsync(app, "ola@example.com", Password);
        var msal = PublicClientApplicationBuilder.Create(IdpClients.Cli)
            // No authority validation here: MSAL caches it process-wide, and ClientCredentialsTests asserts that it calls WebFinger.
            .WithAuthority($"https://{LocalIdpTestHost.Host}/adfs/", validateAuthority: false)
            .WithHttpClientFactory(new TestHttpClientFactory(new HttpClient(app.Server.CreateHandler())))
            .Build();

        var result = await msal.AcquireTokenWithDeviceCode([ApiScope],
            device => ApproveDeviceAsync(() => Browser(app), device, "ola@example.com", Password)).ExecuteAsync();

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "oid" && c.Value == oid.ToString());
        jwt.Subject.Should().Be(userId.ToString());
    }

    [Fact]
    public async Task Device_code_is_refused_when_the_user_is_disabled_before_redemption()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "pia@example.com", Password);
        var api = Api(app);
        var start = await api.PostAsync("/adfs/oauth2/devicecode", Form(new() { ["client_id"] = IdpClients.Cli, ["scope"] = ApiScope }));
        start.StatusCode.Should().Be(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var device = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement;

        var browser = Browser(app);
        await SignInAsync(browser, "pia@example.com", Password);
        var confirm = await browser.GetAsync("/adfs/oauth2/deviceauth?user_code=" + Uri.EscapeDataString(device.GetProperty("user_code").GetString()!));
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SubmitAsync(browser, await confirm.Content.ReadAsStringAsync(), new() { ["action"] = "approve" }))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);
        await ExecAsync(app, "UPDATE data.systemuser SET isdisabled = 1 WHERE systemuserid = @id", ("@id", userId));

        var res = await api.PostAsync("/adfs/oauth2/token", Form(new()
        {
            ["grant_type"] = GrantTypes.DeviceCode, ["client_id"] = IdpClients.Cli, ["device_code"] = device.GetProperty("device_code").GetString()!,
        }));

        await ShouldFailAsync(res, Errors.InvalidGrant);
    }

    [Fact]
    public async Task A_denied_device_gets_access_denied()
    {
        await using var app = Create(fx);
        await LocalUserAsync(app, "pat@example.com", Password);
        var api = Api(app);
        var start = await api.PostAsync("/adfs/oauth2/devicecode", Form(new() { ["client_id"] = IdpClients.Cli, ["scope"] = ApiScope }));
        var device = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement;
        var browser = Browser(app);
        await SignInAsync(browser, "pat@example.com", Password);
        var confirm = await browser.GetAsync("/adfs/oauth2/deviceauth?user_code=" + Uri.EscapeDataString(device.GetProperty("user_code").GetString()!));

        (await SubmitAsync(browser, await confirm.Content.ReadAsStringAsync(), new() { ["action"] = "deny" }))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        var res = await api.PostAsync("/adfs/oauth2/token", Form(new()
        {
            ["grant_type"] = GrantTypes.DeviceCode, ["client_id"] = IdpClients.Cli, ["device_code"] = device.GetProperty("device_code").GetString()!,
        }));
        await ShouldFailAsync(res, Errors.AccessDenied);
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_returns_to_the_client()
    {
        await using var app = Create(fx);
        await LocalUserAsync(app, "ray@example.com", Password);
        var browser = Browser(app);
        await SignInAsync(browser, "ray@example.com", Password);
        (await (await browser.GetAsync("/adfs/login")).Content.ReadAsStringAsync()).Should().Contain("You are signed in");

        var page = await browser.GetAsync(
            $"/adfs/oauth2/logout?client_id={IdpClients.Web}&post_logout_redirect_uri={Uri.EscapeDataString(Audience)}&state=s2");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var res = await SubmitAsync(browser, await page.Content.ReadAsStringAsync(), []);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be(Audience);
        Query(res.Headers.Location)["state"].Should().Be("s2");
        (await (await browser.GetAsync("/adfs/login")).Content.ReadAsStringAsync()).Should().Contain("name=\"password\"");
        var (_, challenge) = Pkce();
        (await browser.GetAsync(AuthorizeUrl(IdpClients.Cli, Loopback, challenge, "openid"))).Headers.Location!.AbsolutePath
            .Should().Be("/adfs/login", "the session is gone");
    }

    [Fact]
    public async Task Refresh_is_refused_after_the_user_is_disabled()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "quinn@example.com", Password);
        var refresh = await RefreshTokenAsync(app, "quinn@example.com");
        var renewed = await RefreshAsync(app, refresh, null);
        renewed.StatusCode.Should().Be(HttpStatusCode.OK, "refresh works while the user is enabled");
        refresh = JsonDocument.Parse(await renewed.Content.ReadAsStringAsync()).RootElement.GetProperty("refresh_token").GetString()!;

        await ExecAsync(app, "UPDATE data.systemuser SET isdisabled = 1 WHERE systemuserid = @id", ("@id", userId));

        await ShouldFailAsync(await RefreshAsync(app, refresh, null), Errors.InvalidGrant);
    }

    [Fact]
    public async Task Refresh_is_refused_after_the_credential_is_removed()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "rae@example.com", Password);
        var refresh = await RefreshTokenAsync(app, "rae@example.com");

        await WithCredentialsAsync(app, c => c.RemoveAsync(userId, default));

        await ShouldFailAsync(await RefreshAsync(app, refresh, null), Errors.InvalidGrant);
    }

    [Fact]
    public async Task A_password_reset_ends_sessions_and_refresh_tokens()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "una@example.com", Password);
        var refresh = await RefreshTokenAsync(app, "una@example.com");
        var browser = Browser(app);
        await SignInAsync(browser, "una@example.com", Password);

        await WithCredentialsAsync(app, c => c.SetPasswordAsync(userId, "another long password", default));

        await ShouldFailAsync(await RefreshAsync(app, refresh, null), Errors.InvalidGrant);
        var (_, challenge) = Pkce();
        (await browser.GetAsync(AuthorizeUrl(IdpClients.Cli, Loopback, challenge, "openid"))).Headers.Location!.AbsolutePath
            .Should().Be("/adfs/login", "the session from before the reset is over");
    }

    [Fact]
    public async Task Enabling_totp_ends_refresh_tokens()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "vic@example.com", Password);
        var refresh = await RefreshTokenAsync(app, "vic@example.com");

        await WithCredentialsAsync(app, c => c.EnableTotpAsync(userId, default));

        await ShouldFailAsync(await RefreshAsync(app, refresh, null), Errors.InvalidGrant);
    }

    [Fact]
    public async Task Refresh_and_code_redemption_do_not_widen_the_granted_scopes()
    {
        await using var app = Create(fx);
        await LocalUserAsync(app, "sam@example.com", Password);
        var refresh = await RefreshTokenAsync(app, "sam@example.com", "openid offline_access");

        await ShouldFailAsync(await RefreshAsync(app, refresh, $"openid offline_access {ApiScope}"), Errors.InvalidGrant);
        (await RefreshAsync(app, refresh, "openid")).StatusCode.Should().Be(HttpStatusCode.OK, "a narrower scope is fine");

        // MSAL sends scope with the code; it may not exceed what was authorized either.
        var browser = Browser(app);
        await SignInAsync(browser, "sam@example.com", Password);
        var (verifier, challenge) = Pkce();
        var code = CodeFrom(await browser.GetAsync(AuthorizeUrl(IdpClients.Cli, Loopback, challenge, "openid")), Loopback);
        var res = await Api(app).PostAsync("/adfs/oauth2/token", Form(new()
        {
            ["grant_type"] = GrantTypes.AuthorizationCode, ["client_id"] = IdpClients.Cli, ["code"] = code,
            ["redirect_uri"] = Loopback, ["code_verifier"] = verifier, ["scope"] = $"openid {ApiScope}",
        }));
        await ShouldFailAsync(res, Errors.InvalidGrant);
    }

    [Fact]
    public async Task Pages_send_security_headers()
    {
        await using var app = Create(fx);
        await LocalUserAsync(app, "tia@example.com", Password);
        var browser = Browser(app);

        var login = await browser.GetAsync("/adfs/login");
        ShouldHaveSecurityHeaders(login);
        var signedIn = await SubmitAsync(browser, await login.Content.ReadAsStringAsync(),
            new() { ["username"] = "tia@example.com", ["password"] = Password });
        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("xrmd.idp=", StringComparison.Ordinal));
        cookie.ToLowerInvariant().Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax").And.Contain("path=/adfs");
        var device = await browser.GetAsync("/adfs/oauth2/deviceauth");
        device.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveSecurityHeaders(device);
        (await device.Content.ReadAsStringAsync()).Should().NotContain("<script");
    }

    [Fact]
    public async Task Admin_created_public_client_with_a_loopback_redirect_accepts_any_port_but_not_other_hosts()
    {
        await using var app = Create(fx);
        var (adminId, _) = await LocalUserAsync(app, "ada@example.com", Password);
        var bearer = Api(app);
        var secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>().CreateAsync(
                new OpenIddict.Abstractions.OpenIddictApplicationDescriptor
                {
                    ClientId = "admin-bearer",
                    ClientSecret = secret,
                    ClientType = ClientTypes.Confidential,
                    Permissions = { Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials },
                    Properties = { [IdpClients.SystemUserIdProperty] = JsonSerializer.SerializeToElement(adminId.ToString()) },
                });
        var tokenRes = await bearer.PostAsync("/adfs/oauth2/token", Form(new()
        {
            ["grant_type"] = GrantTypes.ClientCredentials, ["client_id"] = "admin-bearer", ["client_secret"] = secret, ["scope"] = ApiScope,
        }));
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString());
        var create = await bearer.PostAsync("/admin/idp/clients", new StringContent(JsonSerializer.Serialize(
            new { displayName = "tool", type = "public", redirectUris = new[] { "http://localhost" } }), Encoding.UTF8, "application/json"));
        create.StatusCode.Should().Be(HttpStatusCode.OK, await create.Content.ReadAsStringAsync());
        var clientId = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("clientId").GetString()!;

        var browser = Browser(app);
        await SignInAsync(browser, "ada@example.com", Password);
        var (_, challenge) = Pkce();
        var ok = await browser.GetAsync(AuthorizeUrl(clientId, Loopback, challenge, $"openid {ApiScope}"));
        CodeFrom(ok, Loopback).Should().NotBeNullOrEmpty();

        var other = await browser.GetAsync(AuthorizeUrl(clientId, "https://example.com:8443/", challenge, $"openid {ApiScope}"));
        other.Headers.Location?.GetLeftPart(UriPartial.Path).Should().NotBe("https://example.com:8443/");
        other.StatusCode.Should().NotBe(HttpStatusCode.Redirect, "a non-loopback redirect on another port is refused, not redirected to");
    }

    [Fact]
    public void Request_log_redacts_the_idp_credentials_of_the_query()
    {
        Xrmd.Host.RequestLoggingMiddleware.RedactQuery(new Microsoft.AspNetCore.Http.QueryString("?id_token_hint=eyJ&user_code=123&state=s"))
            .Should().Be("?id_token_hint=***&user_code=***&state=s");
        Xrmd.Host.RequestLoggingMiddleware.RedactQuery(new Microsoft.AspNetCore.Http.QueryString("?code=abc&Code=def&x=1"))
            .Should().Be("?code=***&Code=***&x=1");
        var callback = Xrmd.Host.RequestLoggingMiddleware.RedactQuery(new Microsoft.AspNetCore.Http.QueryString(
            "?returnUrl=" + Uri.EscapeDataString("/cb?code=SECRET&state=s")));
        Uri.UnescapeDataString(callback).Should().Be("?returnUrl=/cb?code=***&state=s");
        var login = Xrmd.Host.RequestLoggingMiddleware.RedactQuery(new Microsoft.AspNetCore.Http.QueryString(
            "?returnUrl=" + Uri.EscapeDataString("/adfs/oauth2/deviceauth?user_code=ABCD-1234&x=1")));
        Uri.UnescapeDataString(login).Should().Be("?returnUrl=/adfs/oauth2/deviceauth?user_code=***&x=1");
    }

    private static void ShouldHaveSecurityHeaders(HttpResponseMessage res)
    {
        res.Headers.CacheControl!.NoStore.Should().BeTrue();
        res.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        res.Headers.GetValues("Content-Security-Policy").Should().Equal("default-src 'self'");
    }

    // ---- helpers ----

    private static HttpClient Browser(WebApplicationFactory<Program> app) => app.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(Audience), AllowAutoRedirect = false, HandleCookies = true,
    });

    private static HttpClient Api(WebApplicationFactory<Program> app) => app.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(Audience), AllowAutoRedirect = false, HandleCookies = false,
    });

    private static long Step() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string AuthorizeUrl(string clientId, string redirectUri, string challenge, string scope) =>
        $"/adfs/oauth2/authorize?client_id={clientId}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(scope)}&code_challenge={challenge}&code_challenge_method=S256&state=s1";

    /// <summary>Signs the browser in at the login page, without an authorization request.</summary>
    private static async Task SignInAsync(HttpClient browser, string userName, string password)
    {
        var login = await browser.GetAsync("/adfs/login");
        var res = await SubmitAsync(browser, await login.Content.ReadAsStringAsync(), new() { ["username"] = userName, ["password"] = password });
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    private static string CodeFrom(HttpResponseMessage res, string redirectUri)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = res.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(redirectUri);
        var query = Query(location);
        query.Should().NotContainKey("error");
        query["state"].Should().Be("s1");
        return query["code"];
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));

    private static async Task<JsonElement> RedeemAsync(WebApplicationFactory<Program> app, string code, string verifier, string redirectUri)
    {
        var res = await Api(app).PostAsync("/adfs/oauth2/token", Form(new()
        {
            ["grant_type"] = GrantTypes.AuthorizationCode, ["client_id"] = IdpClients.Cli, ["code"] = code,
            ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier,
        }));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<string> RefreshTokenAsync(WebApplicationFactory<Program> app, string userName,
        string scope = "openid offline_access " + ApiScope)
    {
        var browser = Browser(app);
        await SignInAsync(browser, userName, Password);
        var (verifier, challenge) = Pkce();
        var code = CodeFrom(await browser.GetAsync(AuthorizeUrl(IdpClients.Cli, Loopback, challenge, scope)), Loopback);
        return (await RedeemAsync(app, code, verifier, Loopback)).GetProperty("refresh_token").GetString()!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(WebApplicationFactory<Program> app, string refreshToken, string? scope)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.RefreshToken, ["client_id"] = IdpClients.Cli, ["refresh_token"] = refreshToken,
        };
        if (scope is not null) form["scope"] = scope;
        return Api(app).PostAsync("/adfs/oauth2/token", Form(form));
    }

    private static async Task ShouldFailAsync(HttpResponseMessage res, string error)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString().Should().Be(error);
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => client;
    }
}
