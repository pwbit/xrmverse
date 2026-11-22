using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The Admin API base: the administrator-only group, health, logs, the metrics endpoint and the pipeline's activities.</summary>
[Collection("xrmd")]
public sealed class AdminApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string AdminRequiredBody =
        """{"error":{"code":"0x80040220","message":"The Admin API requires the System Administrator role."}}""";

    private Guid _nonAdminOid;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        // A member with a role that is not System Administrator.
        _nonAdminOid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), _nonAdminOid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "admin-api-" + Guid.NewGuid().ToString("N")[..8],
            ("prvReadpb_Project", DepthMask.Global)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> GetAsync(string path, Guid? oid)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (oid is { } o) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(o, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private Task<HttpResponseMessage> GetAsAdminAsync(string path) => GetAsync(path, fx.AdminObjectId);

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    [Fact]
    public async Task Admin_without_token_is_401()
    {
        (await GetAsync("/admin/health", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync("/admin/logs", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_for_a_user_without_System_Administrator_is_403()
    {
        var res = await GetAsync("/admin/health", _nonAdminOid);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Be(AdminRequiredBody);

        // A valid token whose user is not a member of the organization is no administrator either.
        var stranger = await GetAsync("/admin/health", Guid.NewGuid());
        stranger.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stranger.Content.ReadAsStringAsync()).Should().Be(AdminRequiredBody);
    }

    [Fact]
    public async Task Admin_403_comes_before_input_validation()
    {
        // take=0 is a 400 for an administrator; for anybody else the role check answers first.
        var res = await GetAsync("/admin/logs?take=0", _nonAdminOid);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Be(AdminRequiredBody);

        (await GetAsync("/admin/logs?take=not-a-number&since=junk&level=nope", _nonAdminOid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsAdminAsync("/admin/logs?take=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Health_reports_schema_version_and_queue()
    {
        var health = await JsonAsync(await GetAsAdminAsync("/admin/health"), HttpStatusCode.OK);

        health["status"]!.GetValue<string>().Should().Be("Healthy");
        health["version"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        health["organization"]!.GetValue<string>().Should().Be(XrmdFixture.OrgName);
        health["database"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        health["schemaVersion"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(3);
        health["pendingMigrations"]!.GetValue<int>().Should().Be(0);
        health["asyncQueue"]!["waiting"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
        health["asyncQueue"]!["inProgress"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
        health["authMode"]!.GetValue<string>().Should().Be("Development");
        health["sdkVersion"]!.GetValue<string>().Should().Be(Xrmd.Abstractions.Sdk.SdkPackage.XrmSdkFileVersion);
        // The connection string (credentials) never leaves the server.
        health.ToJsonString().Should().NotContain("Password").And.NotContain(fx.ConnectionString);
    }

    [Fact]
    public async Task Logs_returns_recent_entries_filtered_by_level()
    {
        var tag = "admin-log-" + Guid.NewGuid().ToString("N");
        var logger = fx.App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminApiTests");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        logger.LogInformation("info {Tag}", tag);
        logger.LogWarning("warn {Tag}", tag);
        logger.LogError(new InvalidOperationException("boom"), "error {Tag}", tag);

        var all = (await JsonAsync(await GetAsAdminAsync("/admin/logs?take=1000"), HttpStatusCode.OK))["value"]!.AsArray()
            .Where(e => e!["message"]!.GetValue<string>().Contains(tag)).ToList();
        all.Select(e => e!["level"]!.GetValue<string>()).Should().Equal("Information", "Warning", "Error");
        all[2]!["category"]!.GetValue<string>().Should().Be("AdminApiTests");
        all[2]!["exception"]!.GetValue<string>().Should().Contain("boom");

        // level is a minimum.
        var warnings = (await JsonAsync(await GetAsAdminAsync("/admin/logs?level=Warning&take=1000"), HttpStatusCode.OK))["value"]!.AsArray()
            .Where(e => e!["message"]!.GetValue<string>().Contains(tag)).Select(e => e!["level"]!.GetValue<string>());
        warnings.Should().Equal("Warning", "Error");

        // since and take.
        var since = Uri.EscapeDataString(before.ToString("O"));
        (await JsonAsync(await GetAsAdminAsync($"/admin/logs?since={since}&take=1000"), HttpStatusCode.OK))["value"]!.AsArray()
            .Should().Contain(e => e!["message"]!.GetValue<string>().Contains(tag));
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        (await JsonAsync(await GetAsAdminAsync($"/admin/logs?since={future}"), HttpStatusCode.OK))["value"]!.AsArray().Should().BeEmpty();
        (await JsonAsync(await GetAsAdminAsync("/admin/logs?take=1"), HttpStatusCode.OK))["value"]!.AsArray().Should().HaveCount(1);

        // Out-of-range or unknown input is a 400 that names it.
        foreach (var bad in new[] { "take=0", "take=1001", "take=abc", "level=Loud", "since=yesterday" })
        {
            var res = await GetAsAdminAsync("/admin/logs?" + bad);
            var error = await JsonAsync(res, HttpStatusCode.BadRequest);
            error["error"]!["message"]!.GetValue<string>().Should().Contain(bad.Split('=')[0]);
        }
    }

    [Fact]
    public async Task Metrics_is_anonymous_and_exposes_xrmd_request_counter()
    {
        // Some traffic first, so the counter has a value.
        (await GetAsAdminAsync("/admin/health")).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await fx.Client.GetAsync("/metrics");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await res.Content.ReadAsStringAsync();
        text.Should().Contain("xrmd_requests_total");
        text.Should().Contain("route=\"/admin/health\"");
    }

    [Fact]
    public async Task Metrics_disabled_is_404()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("Auth:Audience", "http://localhost/");
            b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
            b.UseSetting("Observability:Metrics:Enabled", "false");
        });
        (await app.CreateClient().GetAsync("/metrics")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pipeline_stages_emit_activities()
    {
        var stages = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Xrmd",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (stages) stages.Add(activity.OperationName); },
        };
        ActivitySource.AddActivityListener(listener);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + TestEntities.ProjectSet)
        {
            Content = new StringContent($$"""{ "pb_name": "otel-{{Guid.NewGuid():N}}" }""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());

        lock (stages) stages.Should().Contain(["pipeline.preoperation", "pipeline.mainoperation", "pipeline.postoperation"]);
    }
}
