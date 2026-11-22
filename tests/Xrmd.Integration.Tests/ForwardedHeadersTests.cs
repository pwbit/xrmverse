using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Host;
using Xrmd.Metadata;
using Xrmd.TestSupport;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Ruling H4: the host honors <c>X-Forwarded-Proto</c>, <c>-Host</c> and <c>-For</c> only from the addresses of
/// <c>Proxy:TrustedProxies</c>, and only for a host of <c>Proxy:AllowedHosts</c>; every absolute URL it returns then carries the
/// public scheme and host.
/// </summary>
[Collection("xrmd")]
public sealed class ForwardedHeadersTests(XrmdFixture fx, ITestOutputHelper output) : IDisposable
{
    private const string TrustedProxy = "10.0.0.5";
    private const string TrustedNetwork = "192.168.50.0/24";
    private const string PublicHost = "acme.xrm.test";
    private const string PublicRoot = "https://" + PublicHost + "/api/data/v9.2/";
    private const string InternalRoot = "http://localhost/api/data/v9.2/";

    /// <summary>The test-only header the <see cref="RemoteAddressFilter"/> turns into the connection's remote address.</summary>
    private const string RemoteAddressHeader = "X-Test-Remote-Address";

    private readonly List<RunningProcess> _processes = [];

    public void Dispose()
    {
        foreach (var process in _processes) process.Dispose();
    }

    [Fact]
    public async Task No_trusted_proxy_ignores_forwarded_headers()
    {
        // The fixture's host has no Proxy settings (today's behavior).
        var json = await GetJsonAsync(fx.Client, "WhoAmI", remoteAddress: null, forwardedHost: PublicHost);

        json["@odata.context"]!.GetValue<string>().Should().StartWith(InternalRoot);
    }

    [Fact]
    public async Task Trusted_proxy_rewrites_scheme_and_host()
    {
        await using var app = ProxiedHost();
        var client = app.CreateClient();

        var json = await GetJsonAsync(client, "roles?$select=name", TrustedProxy, PublicHost, ("Prefer", "odata.maxpagesize=1"));

        json["@odata.context"]!.GetValue<string>().Should().StartWith(PublicRoot);
        json["@odata.nextLink"]!.GetValue<string>().Should().StartWith(PublicRoot);
    }

    [Fact]
    public async Task Untrusted_address_cannot_spoof_the_host()
    {
        await using var app = ProxiedHost();
        var client = app.CreateClient();

        var json = await GetJsonAsync(client, "roles?$select=name", "10.0.0.6", PublicHost, ("Prefer", "odata.maxpagesize=1"));

        json["@odata.context"]!.GetValue<string>().Should().StartWith(InternalRoot);
        json["@odata.nextLink"]!.GetValue<string>().Should().StartWith(InternalRoot);
    }

    [Fact]
    public async Task Forwarded_host_outside_allowed_hosts_is_ignored()
    {
        var log = new RequestLogCollector();
        await using var app = ProxiedHost(log);
        var client = app.CreateClient();

        // From a trusted proxy, but for a host that is not the organization's: the request keeps its own host, and the forwarded
        // https is not applied either (ASP.NET Core's middleware alone would still apply the scheme of that entry, see
        // ProxyOptions), so no URL mixes the public scheme with the internal host.
        for (var i = 0; i < 2; i++)
        {
            var json = await GetJsonAsync(client, "WhoAmI", TrustedProxy, "evil.example.com");
            json["@odata.context"]!.GetValue<string>().Should().StartWith(InternalRoot);
        }
        (await GetJsonAsync(client, "WhoAmI", TrustedProxy, "other.example.com"))["@odata.context"]!.GetValue<string>()
            .Should().StartWith(InternalRoot);
        // A trailing empty entry is skipped by the middleware (the host it reads is still the rejected one): no way around the guard.
        foreach (var trailing in new[] { "trail.example.com, ", "trail.example.com," })
            (await GetJsonAsync(client, "WhoAmI", TrustedProxy, trailing))["@odata.context"]!.GetValue<string>()
                .Should().StartWith(InternalRoot, "X-Forwarded-Host: '{0}'", trailing);

        var warnings = log.Drain().Where(l => l.StartsWith("[xrmd Warning]", StringComparison.Ordinal)).ToList();
        warnings.Count(l => l.Contains("evil.example.com", StringComparison.Ordinal)).Should().Be(1, "logged once per host: {0}",
            string.Join('\n', warnings));
        warnings.Count(l => l.Contains("other.example.com", StringComparison.Ordinal)).Should().Be(1, string.Join('\n', warnings));
        warnings.Count(l => l.Contains("trail.example.com", StringComparison.Ordinal)).Should().Be(1, string.Join('\n', warnings));
    }

    [Fact]
    public async Task Malformed_forwarded_host_is_ignored()
    {
        var log = new RequestLogCollector();
        await using var app = ProxiedHost(log);
        var client = app.CreateClient();

        // The host part is allowed but the value is not a host: the middleware would reject it yet apply the forwarded https.
        // Twice: the warning is logged once.
        foreach (var malformed in new[] { PublicHost + ":abc", PublicHost + ":abc" })
            (await GetJsonAsync(client, "WhoAmI", TrustedProxy, malformed))["@odata.context"]!.GetValue<string>()
                .Should().StartWith(InternalRoot, "X-Forwarded-Host: '{0}'", malformed);

        var warnings = log.Drain().Where(l => l.StartsWith("[xrmd Warning]", StringComparison.Ordinal)).ToList();
        warnings.Count(l => l.Contains(PublicHost + ":abc", StringComparison.Ordinal)).Should().Be(1, string.Join('\n', warnings));
    }

    [Theory]
    [InlineData("acme.xrm.test", true)]
    [InlineData("acme.xrm.test:8443", true)]
    [InlineData("10.0.0.5:80", true)]
    [InlineData("[fd00::1]:443", true)]
    [InlineData("acme.xrm.test:abc", false)]
    [InlineData("acme.xrm.test:", false)]
    [InlineData("acme.xrm.test:70000", false)]
    [InlineData("fd00::1", false)]
    [InlineData("[fd00::1", false)]
    [InlineData("acme xrm", false)]
    [InlineData("acme.xrm.test/path", false)]
    public void Forwarded_host_shape_is_checked(string host, bool wellFormed) =>
        ProxyOptions.IsWellFormedHost(host).Should().Be(wellFormed, host);

    [Theory]
    // Several hosts: only the last one (the trusted proxy's, ForwardLimit = 1) counts.
    [InlineData("https", "evil.example.com, " + PublicHost, true)]
    [InlineData("https", PublicHost + ", evil.example.com", false)]
    // Proto and Host lists of different lengths: the entries pair from the right.
    [InlineData("http, https", PublicHost, true)]
    [InlineData("http, https", "evil.example.com, " + PublicHost, true)]
    [InlineData("https", PublicHost + ", evil.example.com, ", false)]
    [InlineData("http, http, https", PublicHost + ", evil.example.com", false)]
    public async Task Forwarded_lists_use_the_last_host_only(string proto, string hosts, bool applied)
    {
        await using var app = ProxiedHost();
        var client = app.CreateClient();

        var json = await GetJsonAsync(client, "WhoAmI", TrustedProxy, hosts, proto);

        json["@odata.context"]!.GetValue<string>().Should().StartWith(applied ? PublicRoot : InternalRoot,
            "X-Forwarded-Proto: '{0}', X-Forwarded-Host: '{1}'", proto, hosts);
    }

    [Fact]
    public async Task Cidr_entry_is_trusted()
    {
        await using var app = ProxiedHost();
        var client = app.CreateClient();

        var json = await GetJsonAsync(client, "WhoAmI", "192.168.50.23", PublicHost);
        json["@odata.context"]!.GetValue<string>().Should().StartWith(PublicRoot);

        var outside = await GetJsonAsync(client, "WhoAmI", "192.168.51.23", PublicHost);
        outside["@odata.context"]!.GetValue<string>().Should().StartWith(InternalRoot);
    }

    [Fact]
    public async Task Invalid_trusted_proxy_entry_fails_startup()
    {
        // The proxy settings are checked before anything else (the SDK folder is empty: the refusal is not the SDK's).
        var psi = await PublishedApps.HostStartInfoAsync(PublishedApps.NewFolder("no-sdk"));
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{FreePort()}";
        psi.Environment["Proxy__TrustedProxies"] = "10.0.0.5, 10.0.0.300";
        psi.Environment["Proxy__AllowedHosts"] = PublicHost;

        var host = new RunningProcess(psi, line =>
        {
            try { output.WriteLine(line); } catch (InvalidOperationException) { }
        });
        _processes.Add(host);
        (await host.WaitForExitAsync(TimeSpan.FromMinutes(2))).Should().BeTrue("the host refuses to start\n" + host.Output);

        host.Process.ExitCode.Should().Be(3, host.Output);
        host.Output.Should().Contain("Proxy:TrustedProxies").And.Contain("'10.0.0.300'");
        host.Output.Should().NotContain("Unhandled exception").And.NotContain("   at ", "no stack trace is printed");
    }

    [Theory]
    [InlineData("TrustedProxies", "10.5", "'10.5'")]
    [InlineData("TrustedProxies", "10.0.0.0/33", "'10.0.0.0/33'")]
    [InlineData("TrustedProxies", "10.1.0.5/16", "'10.1.0.5/16'")]
    [InlineData("TrustedProxies", "fd00::1/64", "'fd00::1/64'")]
    [InlineData("TrustedProxies", "proxy.local", "'proxy.local'")]
    [InlineData("AllowedHosts", "https://acme.xrm.test", "'https://acme.xrm.test'")]
    [InlineData("AllowedHosts", "acme.xrm.test/path", "'acme.xrm.test/path'")]
    public void Invalid_proxy_entries_are_named(string key, string value, string named)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:TrustedProxies"] = "10.0.0.5, fd00::/64", ["Proxy:AllowedHosts"] = "acme.xrm.test, *.xrm.test:443",
        }).Build();
        ProxyOptions.Validate(configuration).Should().BeNull("the base settings are valid");

        configuration["Proxy:" + key] = value;
        ProxyOptions.Validate(configuration).Should().Contain("Proxy:" + key).And.Contain(named);
    }

    [Fact]
    public async Task Soap_and_auth_challenge_use_the_public_host()
    {
        await using var app = ProxiedHost();
        var client = app.CreateClient();

        // The SOAP endpoint's address, as clients discover it (RetrieveCurrentOrganization's Endpoints).
        var json = await GetJsonAsync(client, "RetrieveCurrentOrganization(AccessType=Microsoft.Dynamics.CRM.EndpointAccessType'Default')",
            TrustedProxy, PublicHost);
        json["@odata.context"]!.GetValue<string>().Should().StartWith(PublicRoot);
        var endpoints = json["Detail"]!["Endpoints"]!;
        var keys = endpoints["Keys"]!.AsArray().Select(k => k!.GetValue<string>()).ToList();
        var values = endpoints["Values"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        values[keys.IndexOf("OrganizationService")].Should().Be("https://" + PublicHost + "/XRMServices/2011/Organization.svc");
        values.Should().OnlyContain(v => v.StartsWith("https://" + PublicHost + "/", StringComparison.Ordinal));

        // The bearer challenge names no URL at all (no resource_metadata, no authorization_uri): nothing internal can leak.
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/WhoAmI");
        AddForwarded(anonymous, TrustedProxy, PublicHost);
        using var challenge = await client.SendAsync(anonymous);
        challenge.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.Select(h => h.ToString()).Should().Equal("Bearer");
    }

    [Fact]
    public async Task File_download_location_uses_the_public_host()
    {
        await using var app = ProxiedHost();
        var client = app.CreateClient();
        var table = "new_fh" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        var store = app.Services.GetRequiredService<IMetadataStore>();
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        var admin = TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc", MaxSizeInKB = 1024 },
        });
        var id = await admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var path = $"/api/data/v9.2/{store.GetEntity(table)!.EntitySetName}({id})/new_doc";

        using var start = Request(HttpMethod.Patch, path + "?x-ms-file-name=a.bin", TrustedProxy, PublicHost);
        start.Headers.Add("x-ms-transfer-mode", "chunked");
        using var response = await client.SendAsync(start);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString().Should().StartWith(PublicRoot + store.GetEntity(table)!.EntitySetName + "(");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A host of its own (own database) behind the proxy 10.0.0.5 and the network 192.168.50.0/24, for acme.xrm.test.</summary>
    private WebApplicationFactory<Program> ProxiedHost(RequestLogCollector? log = null) => fx.WithOwnDatabase(b =>
    {
        b.UseSetting("Proxy:TrustedProxies", $"{TrustedProxy}, {TrustedNetwork}");
        b.UseSetting("Proxy:AllowedHosts", PublicHost);
        b.ConfigureServices(services => services.AddSingleton<IStartupFilter, RemoteAddressFilter>());
        if (log is not null) b.ConfigureLogging(l => l.AddProvider(log));
    });

    private HttpRequestMessage Request(HttpMethod method, string path, string? remoteAddress, string forwardedHost, string forwardedProto = "https")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        AddForwarded(request, remoteAddress, forwardedHost, forwardedProto);
        return request;
    }

    private static void AddForwarded(HttpRequestMessage request, string? remoteAddress, string forwardedHost, string forwardedProto = "https")
    {
        if (remoteAddress is not null) request.Headers.Add(RemoteAddressHeader, remoteAddress);
        // Without validation: the tests send malformed values (trailing commas, lists) as a proxy could.
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto).Should().BeTrue();
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost).Should().BeTrue();
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
    }

    private Task<JsonNode> GetJsonAsync(HttpClient client, string path, string? remoteAddress, string forwardedHost,
        params (string Name, string Value)[] headers) => GetJsonAsync(client, path, remoteAddress, forwardedHost, "https", headers);

    private async Task<JsonNode> GetJsonAsync(HttpClient client, string path, string? remoteAddress, string forwardedHost,
        string forwardedProto, params (string Name, string Value)[] headers)
    {
        using var request = Request(HttpMethod.Get, "/api/data/v9.2/" + path, remoteAddress, forwardedHost, forwardedProto);
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>TestServer has no remote address: this sets the one the test names, before any middleware of the host runs.</summary>
    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var value))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(value.ToString());
                    context.Request.Headers.Remove(RemoteAddressHeader);
                }
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
