using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary><c>GET|PUT /admin/hierarchy-security</c> (plan 2.5, task 6): the settings and the excluded tables.</summary>
[Collection("xrmd")]
public sealed class HierarchySecurityAdminTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string AdminRequiredBody =
        """{"error":{"code":"0x80040220","message":"The Admin API requires the System Administrator role."}}""";

    private Guid _nonAdminOid;

    public async Task InitializeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _nonAdminOid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), _nonAdminOid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "hier-admin-" + Guid.NewGuid().ToString("N")[..8],
            ("prvReadpb_Project", DepthMask.Global)));
    }

    /// <summary>Leaves the organization as the other tests expect it: model off, depth 3, nothing excluded.</summary>
    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await PutAsync(fx.AdminObjectId, new JsonObject { ["enabled"] = false, ["depth"] = 3, ["excludedTables"] = new JsonArray() });
    }

    /// <summary>Registers <c>ThrowPlugin</c> with <paramref name="config"/> on organization Update at <paramref name="stage"/>.</summary>
    private async Task RegisterThrowOnOrganizationUpdateAsync(int stage, string config)
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(admin, type, "Update", "organization", stage, config: config);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, Guid? oid, JsonObject? body = null)
    {
        var req = new HttpRequestMessage(method, "/admin/hierarchy-security");
        if (oid is { } o) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(o, fx.BaseUrl));
        if (body is not null) req.Content = JsonContent.Create(body);
        return fx.Client.SendAsync(req);
    }

    private Task<HttpResponseMessage> GetAsync(Guid? oid) => SendAsync(HttpMethod.Get, oid);

    private Task<HttpResponseMessage> PutAsync(Guid? oid, JsonObject body) => SendAsync(HttpMethod.Put, oid, body);

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    [Fact]
    public async Task Put_then_get_round_trips_and_the_security_cache_follows()
    {
        var saved = await JsonAsync(await PutAsync(fx.AdminObjectId,
            new JsonObject { ["enabled"] = true, ["depth"] = 5, ["excludedTables"] = new JsonArray("pb_project", "PB_Project") }), HttpStatusCode.OK);
        saved["enabled"]!.GetValue<bool>().Should().BeTrue();
        saved["depth"]!.GetValue<int>().Should().Be(5);
        saved["excludedTables"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("pb_project");

        var read = await JsonAsync(await GetAsync(fx.AdminObjectId), HttpStatusCode.OK);
        read.ToJsonString().Should().Be(saved.ToJsonString());

        var cached = await fx.App.Services.GetRequiredService<SecurityCache>().GetHierarchySettingsAsync(default);
        cached.Enabled.Should().BeTrue();
        cached.Depth.Should().Be(5);
        cached.ExcludedTables.Should().BeEquivalentTo(["pb_project"]);
    }

    [Fact]
    public async Task Put_with_only_some_fields_leaves_the_others_unchanged()
    {
        await JsonAsync(await PutAsync(fx.AdminObjectId,
            new JsonObject { ["enabled"] = true, ["depth"] = 7, ["excludedTables"] = new JsonArray("pb_project") }), HttpStatusCode.OK);
        var saved = await JsonAsync(await PutAsync(fx.AdminObjectId, new JsonObject { ["depth"] = 2 }), HttpStatusCode.OK);
        saved["enabled"]!.GetValue<bool>().Should().BeTrue();
        saved["depth"]!.GetValue<int>().Should().Be(2);
        saved["excludedTables"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Non_administrator_is_403_on_get_and_put_and_nothing_changes()
    {
        (await GetAsync(_nonAdminOid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var res = await PutAsync(_nonAdminOid, new JsonObject { ["enabled"] = true });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Be(AdminRequiredBody);
        (await GetAsync(null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await JsonAsync(await GetAsync(fx.AdminObjectId), HttpStatusCode.OK))["enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_excluded_table_is_400_and_nothing_changes()
    {
        var res = await PutAsync(fx.AdminObjectId,
            new JsonObject { ["enabled"] = true, ["depth"] = 9, ["excludedTables"] = new JsonArray("pb_project", "no_such_table") });
        (await res.Content.ReadAsStringAsync()).Should().Contain("no_such_table");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var read = await JsonAsync(await GetAsync(fx.AdminObjectId), HttpStatusCode.OK);
        read["enabled"]!.GetValue<bool>().Should().BeFalse();
        read["excludedTables"]!.AsArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-4)]
    public async Task Depth_out_of_range_is_400_with_the_range_in_the_message_and_the_exclusions_are_not_written(int depth)
    {
        var res = await PutAsync(fx.AdminObjectId,
            new JsonObject { ["depth"] = depth, ["excludedTables"] = new JsonArray("pb_project") });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("1 to 100");
        (await JsonAsync(await GetAsync(fx.AdminObjectId), HttpStatusCode.OK))["excludedTables"]!.AsArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"enabled":"yes"}""")]
    [InlineData("""{"depth":"3"}""")]
    [InlineData("""{"depth":2.5}""")]
    [InlineData("""{"excludedTables":"pb_project"}""")]
    [InlineData("""{"excludedTables":[1]}""")]
    [InlineData("""{"excludedTables":[""]}""")]
    public async Task Malformed_body_is_400(string body)
    {
        var res = await PutAsync(fx.AdminObjectId, JsonNode.Parse(body)!.AsObject());
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(20)] // PreOperation: before the core operation
    [InlineData(40)] // PostOperation: after it, in the same transaction
    public async Task A_plugin_step_on_organization_Update_fires_for_the_PUT_and_its_failure_rolls_everything_back(int stage)
    {
        await RegisterThrowOnOrganizationUpdateAsync(stage, "invalid:hierarchy step ran");

        var res = await PutAsync(fx.AdminObjectId,
            new JsonObject { ["enabled"] = true, ["depth"] = 8, ["excludedTables"] = new JsonArray("pb_project") });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("hierarchy step ran");
        await PluginTestHelper.ClearAsync(fx);
        var read = await JsonAsync(await GetAsync(fx.AdminObjectId), HttpStatusCode.OK);
        read["enabled"]!.GetValue<bool>().Should().BeFalse();
        read["depth"]!.GetValue<int>().Should().Be(3);
        read["excludedTables"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_PUT_with_only_exclusions_runs_no_organization_step()
    {
        await RegisterThrowOnOrganizationUpdateAsync(20, "invalid:must not run");

        var saved = await JsonAsync(await PutAsync(fx.AdminObjectId, new JsonObject { ["excludedTables"] = new JsonArray("pb_project") }),
            HttpStatusCode.OK);

        saved["excludedTables"]!.AsArray().Should().HaveCount(1);
    }
}
