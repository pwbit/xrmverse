using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xrmd.Integration.Tests;

/// <summary>Static hosting of the web shell and the anonymous /ui/config.json. Needs no database: a bare host serves them.</summary>
public sealed class UiHostingTests : IDisposable
{
    private const string ShellMarker = "<title>xrmverse shell</title>";
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-ui-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteShell()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "index.html"), $"<!doctype html><html><head>{ShellMarker}</head><body></body></html>");
        File.WriteAllText(Path.Combine(_root, "assets", "app-abc123.js"), "console.log('app');");
    }

    private WebApplicationFactory<Program> Host(string? root = null, params (string Key, string Value)[] settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Ui:RootPath", root ?? _root);
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    [Theory]
    [InlineData("/")]
    [InlineData("/manage/tables")]
    [InlineData("/main.aspx?appid=x")]
    [InlineData("/apps")]
    [InlineData("/manage/api")]
    [InlineData("/administration")] // a server prefix only as a whole segment
    public async Task Root_and_manage_routes_serve_index_html(string path)
    {
        WriteShell();
        await using var app = Host();
        var res = await app.CreateClient().GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await res.Content.ReadAsStringAsync()).Should().Contain(ShellMarker);
    }

    [Fact]
    public async Task Index_is_not_cached_and_assets_are_immutable()
    {
        WriteShell();
        await using var app = Host();
        var client = app.CreateClient();

        foreach (var path in new[] { "/", "/index.html", "/manage/tables" })
            (await client.GetAsync(path)).Headers.CacheControl.Should().NotBeNull(path).And.Subject.ToString().Should().Be("no-cache", path);

        var asset = await client.GetAsync("/assets/app-abc123.js");
        asset.StatusCode.Should().Be(HttpStatusCode.OK);
        asset.Headers.CacheControl!.ToString().Should().Be("public, max-age=31536000, immutable");
    }

    [Theory]
    [InlineData("/api/data/v9.2/nonexistents")]
    [InlineData("/admin/nope")]
    [InlineData("/mcp")]
    [InlineData("/XRMServices/x")]
    [InlineData("/metrics")]
    [InlineData("/WebResources/x")]
    [InlineData("/Image/download.aspx")]
    [InlineData("/image/x")]
    public async Task Fallback_never_swallows_server_paths(string path)
    {
        WriteShell();
        await using var app = Host(null, ("Observability:Metrics:Enabled", "false")); // /metrics is a server path only when disabled; enabled it serves Prometheus text
        var res = await app.CreateClient().GetAsync(path);

        res.StatusCode.Should().NotBe(HttpStatusCode.OK, path);
        res.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html", path);
        (await res.Content.ReadAsStringAsync()).Should().NotContain(ShellMarker);
    }

    [Fact]
    public async Task Config_is_anonymous_and_has_no_secrets_in_development()
    {
        WriteShell();
        await using var app = Host(null,
            ("Auth:Mode", "Development"), ("Auth:Audience", "http://localhost/"), ("Auth:DevSigningKey", DevTokenFactory.SigningKey));
        var res = await app.CreateClient().GetAsync("/ui/config.json");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain(DevTokenFactory.SigningKey);
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("authMode").GetString().Should().Be("Development");
        json.GetProperty("audience").GetString().Should().Be("http://localhost/");
        json.GetProperty("apiVersion").GetString().Should().Be("v9.2");
        json.GetProperty("authority").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Config_in_entra_mode_exposes_authority_client_and_scope()
    {
        WriteShell();
        await using var app = Host(null,
            ("Auth:Mode", "Entra"), ("Auth:Audience", "api://xrmd"), ("Auth:TenantId", "tenant-1"),
            ("Ui:Entra:ClientId", "spa-client"), ("Ui:Entra:Scope", "api://xrmd/access"));
        var json = JsonDocument.Parse(await app.CreateClient().GetStringAsync("/ui/config.json")).RootElement;

        json.GetProperty("authMode").GetString().Should().Be("Entra");
        json.GetProperty("audience").GetString().Should().Be("api://xrmd");
        json.GetProperty("authority").GetString().Should().Be("https://login.microsoftonline.com/tenant-1/v2.0");
        json.GetProperty("clientId").GetString().Should().Be("spa-client");
        json.GetProperty("scope").GetString().Should().Be("api://xrmd/access");
    }

    [Fact]
    public async Task Missing_ui_build_serves_placeholder()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        await using var app = Host(empty);
        var res = await app.CreateClient().GetAsync("/");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("not been built").And.Contain("pnpm -C ui build");
    }

    [Theory]
    [InlineData("/API/x")]
    [InlineData("/Admin/x")]
    [InlineData("/MCP")]
    [InlineData("/healthz/x")]
    public async Task Server_paths_are_matched_case_insensitively(string path)
    {
        WriteShell();
        await using var app = Host();
        var res = await app.CreateClient().GetAsync(path);

        res.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html", path);
        (await res.Content.ReadAsStringAsync()).Should().NotContain(ShellMarker);
    }

    [Theory]
    [InlineData("/assets/..%2findex.html")]
    [InlineData("/%2e%2e/appsettings.json")]
    [InlineData("/..%5c..%5cx")]
    public async Task Traversal_is_refused(string path)
    {
        WriteShell();
        File.WriteAllText(Path.Combine(Directory.GetParent(_root)!.FullName, "xrmd-secret.txt"), "secret");
        await using var app = Host();
        var res = await app.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative)));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain(ShellMarker).And.NotContain("secret");
    }

    [Fact]
    public async Task Head_on_the_shell_has_no_body_and_is_not_cached()
    {
        WriteShell();
        await using var app = Host();
        var res = await app.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.CacheControl!.ToString().Should().Be("no-cache");
        (await res.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/manage/plugins/Acme.Plugins")]
    [InlineData("/manage/solutions/v1.2")]
    public async Task Dotted_deep_links_serve_the_shell(string path)
    {
        WriteShell();
        await using var app = Host();
        var res = await app.CreateClient().GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain(ShellMarker);
    }

    [Theory]
    [InlineData("/assets/missing.js")]
    [InlineData("/assets/Acme.Plugins")]
    [InlineData("/missing.css")]
    public async Task Missing_static_files_are_404_not_the_shell(string path)
    {
        WriteShell();
        await using var app = Host();
        var res = await app.CreateClient().GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        (await res.Content.ReadAsStringAsync()).Should().NotContain(ShellMarker);
    }

    [Theory]
    [InlineData("POST", "/")]
    [InlineData("PUT", "/manage/tables")]
    [InlineData("DELETE", "/assets/app-abc123.js")]
    public async Task Other_methods_on_shell_routes_are_405(string method, string path)
    {
        WriteShell();
        await using var app = Host();
        var res = await app.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        res.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        res.Content.Headers.Allow.Should().BeEquivalentTo("GET", "HEAD");
    }

    [Fact]
    public async Task A_ui_built_after_startup_is_served()
    {
        var later = Path.Combine(_root, "later");
        await using var app = Host(later);
        var client = app.CreateClient();
        (await client.GetStringAsync("/")).Should().Contain("not been built");

        Directory.CreateDirectory(Path.Combine(later, "assets"));
        File.WriteAllText(Path.Combine(later, "index.html"), $"<html>{ShellMarker}</html>");
        File.WriteAllText(Path.Combine(later, "assets", "late.js"), "1");

        (await client.GetStringAsync("/")).Should().Contain(ShellMarker);
        (await client.GetAsync("/assets/late.js")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
