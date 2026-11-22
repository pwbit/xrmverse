using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The Web API actions bound to role: <c>AddPrivilegesRole</c> and <c>RemovePrivilegeRole</c> (Learn shapes).</summary>
[Collection("xrmd")]
public sealed class RolePrivilegeActionTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Bound = "Microsoft.Dynamics.CRM.";

    public Task InitializeAsync() => TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> PostAsync(string path, JsonNode body, Guid? objectId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<int?> MaskAsync(Guid role, string privilege)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{Api}roleprivilegescollection?$select=privilegedepthmask&$filter=roleid eq {role} and privilegeid eq {PrivilegeNames.IdOf(privilege)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var rows = JsonNode.Parse(text)!["value"]!.AsArray();
        return rows.Count == 0 ? null : rows[0]!["privilegedepthmask"]!.GetValue<int>();
    }

    private static JsonObject Add(string privilege, JsonNode? depth) => new()
    {
        ["Privileges"] = new JsonArray(new JsonObject { ["Depth"] = depth, ["PrivilegeId"] = PrivilegeNames.IdOf(privilege).ToString() }),
    };

    private async Task<Guid> RoleAsync() => await CreateRoleAsync(fx, "ra-" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData("Basic", 1)]
    [InlineData("Local", 2)]
    [InlineData("Deep", 4)]
    [InlineData("Global", 8)]
    public async Task Add_privilege_sets_the_depth_mask_and_returns_204(string depth, int mask)
    {
        var role = await RoleAsync();
        var res = await PostAsync($"roles({role})/{Bound}AddPrivilegesRole", Add("prvReadpb_Project", depth));
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await MaskAsync(role, "prvReadpb_Project")).Should().Be(mask);
    }

    [Fact]
    public async Task Add_privilege_accepts_the_numeric_enum_and_replaces_the_depth()
    {
        var role = await RoleAsync();
        (await PostAsync($"roles({role})/{Bound}AddPrivilegesRole", Add("prvReadpb_Project", 3))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await MaskAsync(role, "prvReadpb_Project")).Should().Be(8);
        (await PostAsync($"roles({role})/{Bound}AddPrivilegesRole", Add("prvReadpb_Project", "Local"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await MaskAsync(role, "prvReadpb_Project")).Should().Be(2);
    }

    [Fact]
    public async Task Remove_privilege_deletes_the_row_and_returns_204()
    {
        var role = await RoleAsync();
        await PostAsync($"roles({role})/{Bound}AddPrivilegesRole", Add("prvReadpb_Project", "Deep"));
        var body = new JsonObject
        {
            ["Privilege"] = new JsonObject { ["@odata.type"] = Bound + "privilege", ["privilegeid"] = PrivilegeNames.IdOf("prvReadpb_Project").ToString() },
        };
        var res = await PostAsync($"roles({role})/{Bound}RemovePrivilegeRole", body);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await MaskAsync(role, "prvReadpb_Project")).Should().BeNull();
    }

    [Theory]
    [InlineData("Everything")]
    [InlineData("7")]
    [InlineData("2")]
    [InlineData("Local,Deep")]
    [InlineData("Basic, Local")]
    [InlineData(" Local")]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(null)]
    public async Task Bad_or_missing_depth_is_400_and_nothing_is_written(object? depth)
    {
        var role = await RoleAsync();
        JsonNode? node = depth switch { null => null, int n => JsonValue.Create(n), string s => JsonValue.Create(s), _ => null };
        var res = await PostAsync($"roles({role})/{Bound}AddPrivilegesRole", Add("prvReadpb_Project", node));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        JsonNode.Parse(await res.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>().Should().Contain("Depth");
        (await MaskAsync(role, "prvReadpb_Project")).Should().BeNull();
    }

    [Fact]
    public async Task Unknown_parameter_and_wrong_type_marker_are_400()
    {
        var role = await RoleAsync();
        (await PostAsync($"roles({role})/{Bound}AddPrivilegesRole", new JsonObject { ["Nope"] = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var wrong = new JsonObject { ["Privilege"] = new JsonObject { ["@odata.type"] = Bound + "role", ["privilegeid"] = Guid.NewGuid().ToString() } };
        (await PostAsync($"roles({role})/{Bound}RemovePrivilegeRole", wrong)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Action_on_another_entity_set_is_404()
    {
        var res = await PostAsync($"teams({Guid.NewGuid()})/{Bound}AddPrivilegesRole", Add("prvReadpb_Project", "Basic"));
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Caller_without_write_on_roles_is_refused_with_403()
    {
        var role = await RoleAsync();
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadteam", DepthMask.Global));
        var res = await PostAsync($"roles({role})/{Bound}AddPrivilegesRole", Add("prvReadpb_Project", "Basic"), oid);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
        (await MaskAsync(role, "prvReadpb_Project")).Should().BeNull();
    }
}
