using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1c, task 2 (ruling C1): <c>POST /api/session</c> puts the access token in the <c>xrmd.session</c> cookie, which then
/// authenticates same-origin script requests (Web API, web resources, <c>ClientGlobalContext.js.aspx</c>) and nothing else;
/// cookie-authenticated writes are refused from another origin or with a content type an HTML form can send.
/// </summary>
[Collection("xrmd")]
public sealed class SessionCookieTests(XrmdFixture fx)
{
    private const string Cookie = "xrmd.session";
    private const string WhoAmI = "/api/data/v9.2/WhoAmI";
    private const string Accounts = "/api/data/v9.2/accounts";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>A client of its own that keeps no cookies: the fixture's shared client must never pick up a session.</summary>
    private HttpClient NewClient(string baseAddress = "http://localhost") =>
        fx.App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(baseAddress) });

    private string Token(Guid? objectId = null, TimeSpan? lifetime = null) =>
        DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl, lifetime: lifetime);

    private static HttpRequestMessage WithCookie(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("Cookie", $"{Cookie}={token}");
        return req;
    }

    private static HttpRequestMessage AccountPost(string token, string mediaType, string? origin = null)
    {
        var req = WithCookie(HttpMethod.Post, Accounts, token);
        req.Content = new StringContent("{\"name\":\"cookie " + Guid.NewGuid().ToString("N")[..8] + "\"}", Encoding.UTF8);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        return req;
    }

    private static SetCookieHeaderValue SessionSetCookie(HttpResponseMessage response) =>
        SetCookieHeaderValue.ParseList(response.Headers.GetValues(HeaderNames.SetCookie).ToList()).Single(c => c.Name == Cookie);

    private async Task<string> CreateWebResourceAsync()
    {
        var name = "xrmd_/cookie/" + Guid.NewGuid().ToString("N")[..8] + ".js";
        await Admin.CreateAsync(new Entity("webresource")
        {
            ["name"] = name, ["webresourcetype"] = new OptionSetValue(3),
            ["content"] = Convert.ToBase64String("function f() {}"u8.ToArray()),
        });
        return name;
    }

    [Theory]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost", true)]
    public async Task Post_session_sets_httponly_strict_cookie(string baseAddress, bool secure)
    {
        using var client = NewClient(baseAddress);
        var token = Token();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/session");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cookie = SessionSetCookie(response);
        cookie.Value.ToString().Should().Be(token);
        cookie.HttpOnly.Should().BeTrue();
        cookie.SameSite.Should().Be(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        cookie.Path.ToString().Should().Be("/");
        cookie.Domain.HasValue.Should().BeFalse();
        cookie.Secure.Should().Be(secure);
        cookie.Expires.Should().Be(new DateTimeOffset(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo, TimeSpan.Zero));
    }

    [Fact]
    public async Task Post_session_without_bearer_is_401()
    {
        using var client = NewClient();
        (await client.PostAsync("/api/session", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The cookie itself is never a way to renew the session.
        var withCookie = await client.SendAsync(WithCookie(HttpMethod.Post, "/api/session", Token()));
        withCookie.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withCookie.Headers.Contains(HeaderNames.SetCookie).Should().BeFalse();
    }

    [Fact]
    public async Task Cookie_authenticates_web_api_get()
    {
        using var client = NewClient();
        var response = await client.SendAsync(WithCookie(HttpMethod.Get, WhoAmI, Token()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("UserId");
    }

    [Fact]
    public async Task Cookie_authenticates_web_resource()
    {
        var name = await CreateWebResourceAsync();
        using var client = NewClient();

        (await client.SendAsync(WithCookie(HttpMethod.Get, "/WebResources/" + name, Token()))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(WithCookie(HttpMethod.Get, "/%7b0001%7d/WebResources/" + name, Token()))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(WithCookie(HttpMethod.Get, "/{0001}/WebResources/" + name, Token()))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/WebResources/" + name)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", "/admin/health")]
    [InlineData("GET", "/admin/orgs")]
    [InlineData("POST", "/mcp")]
    [InlineData("POST", "/XRMServices/2011/Organization.svc/web")]
    [InlineData("GET", "/Image/download.aspx")]
    public async Task Cookie_ignored_on_admin_mcp_soap(string method, string path)
    {
        using var client = NewClient();
        var req = WithCookie(new HttpMethod(method), path, Token());
        if (method == "POST") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bearer_header_wins_over_cookie()
    {
        var other = Guid.NewGuid();
        var otherUserId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), other);
        await AssignRoleAsync(fx, otherUserId, StandardRoles.BasicUser);
        using var client = NewClient();
        var req = WithCookie(HttpMethod.Get, WhoAmI, Token());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(other));

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["UserId"]!.GetValue<Guid>().Should().Be(otherUserId);

        // A header that is not a valid token is not rescued by the cookie.
        var bad = WithCookie(HttpMethod.Get, WhoAmI, Token());
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        (await client.SendAsync(bad)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://localhost")]
    public async Task Cross_origin_write_refused(string origin)
    {
        using var client = NewClient();
        (await client.SendAsync(AccountPost(Token(), "application/json", origin))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var patch = WithCookie(HttpMethod.Patch, Accounts + "(" + Guid.NewGuid() + ")", Token());
        patch.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        patch.Headers.TryAddWithoutValidation("Origin", origin);
        (await client.SendAsync(patch)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var delete = WithCookie(HttpMethod.Delete, Accounts + "(" + Guid.NewGuid() + ")", Token());
        delete.Headers.TryAddWithoutValidation("Origin", origin);
        (await client.SendAsync(delete)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Same_origin_write_accepted()
    {
        using var client = NewClient();
        (await client.SendAsync(AccountPost(Token(), "application/json", "http://localhost"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(AccountPost(Token(), "application/json"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The bearer header is not subject to the cookie's rules.
        var bearer = new HttpRequestMessage(HttpMethod.Post, Accounts)
        {
            Content = new StringContent("{\"name\":\"bearer\"}", Encoding.UTF8, "application/json"),
        };
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        bearer.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        (await client.SendAsync(bearer)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=xyz")]
    [InlineData("text/plain")]
    [InlineData("Text/Plain; charset=utf-8")]
    public async Task Form_content_types_refused(string mediaType)
    {
        using var client = NewClient();
        (await client.SendAsync(AccountPost(Token(), mediaType))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(AccountPost(Token(), "application/json"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Expired_token_in_cookie_is_401()
    {
        using var client = NewClient();
        (await client.SendAsync(WithCookie(HttpMethod.Get, WhoAmI, Token(lifetime: TimeSpan.FromMinutes(-10)))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_session_clears_cookie()
    {
        using var client = NewClient();
        // The bearer header is required; the cookie alone (even a valid one) is not read on /api/session.
        var cookieOnly = await client.SendAsync(WithCookie(HttpMethod.Delete, "/api/session", Token()));
        cookieOnly.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        cookieOnly.Headers.Contains(HeaderNames.SetCookie).Should().BeFalse();
        (await client.DeleteAsync("/api/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var req = WithCookie(HttpMethod.Delete, "/api/session", Token());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cookie = SessionSetCookie(response);
        cookie.Value.ToString().Should().BeEmpty();
        cookie.Path.ToString().Should().Be("/");
        cookie.Expires.Should().BeBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Client_global_context_script_served()
    {
        using var client = NewClient();
        var response = await client.SendAsync(WithCookie(HttpMethod.Get, "/ClientGlobalContext.js.aspx", Token()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/javascript");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        var script = await response.Content.ReadAsStringAsync();
        script.Should().Contain("GetGlobalContext").And.Contain("getGlobalContext");

        (await client.GetAsync("/ClientGlobalContext.js.aspx")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
