using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;

namespace Xrmd.Integration.Tests;

/// <summary>With a valid token the server paths answer for themselves, so the fallback's refusal is what is observed.</summary>
[Collection("xrmd")]
public sealed class UiHostingServerPathTests(XrmdFixture fx)
{
    [Fact]
    public async Task Unknown_entity_set_is_the_web_apis_404_json_not_the_shell()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/nonexistents");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Unknown_admin_path_with_a_token_is_not_the_shell()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/admin/nope");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        res.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    // The fallback must not be a candidate on server paths: routing's own 405 (wrong method) and the authorization
    // fallback policy's 401 (no token on an unknown path) come through instead of the fallback's 404. These are the
    // answers the host gave before the web shell existed (27e79b0). PUT on an entity set (e.g. /accounts) is not here:
    // it was already a 404 then, because the complex-segment route PUT EntityDefinitions({key}) is a PUT candidate at
    // the same routing node, so routing never builds a 405 for it.
    [Theory]
    [InlineData("PUT", "/api/data/v9.2/WhoAmI")]
    [InlineData("PUT", "/API/data/v9.2/WhoAmI")]
    [InlineData("PUT", "/api/data/v9.2/EntityDefinitions")]
    [InlineData("GET", "/mcp")]
    [InlineData("DELETE", "/mcp")]
    [InlineData("GET", "/MCP")]
    [InlineData("POST", "/admin/health")]
    [InlineData("PUT", "/Admin/Health")]
    [InlineData("GET", "/XRMServices/2011/Organization.svc/web")]
    [InlineData("POST", "/healthz")]
    [InlineData("POST", "/ui/config.json")]
    public async Task A_wrong_method_on_a_server_path_is_routings_405(string method, string path)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, $"{method} {path}");
        res.Content.Headers.Allow.Should().NotBeEmpty($"{method} {path}");
    }

    [Theory]
    [InlineData("/api/data/v9.2/x/y/z")]
    [InlineData("/api/nope")]
    [InlineData("/Api/data/v9.2/x/y/z")]
    [InlineData("/admin/nope")]
    [InlineData("/WebResources/x.js")]
    public async Task An_unknown_server_path_without_a_token_is_401(string path)
    {
        var res = await fx.Client.GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
    }
}
