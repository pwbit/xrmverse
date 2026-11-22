using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Impersonation through the HTTP headers <c>MSCRMCallerID</c> and <c>CallerObjectId</c> (the SOAP <c>CallerId</c> header
/// is covered by <see cref="OrganizationSoapEndpointTests"/>). D holds the Delegate role directly plus Read and Create
/// Global on <c>pb_project</c>; L, in a child business unit, holds Create Global on <c>pb_project</c>.
/// </summary>
[Collection("xrmd")]
public sealed class ImpersonationTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Project = TestEntities.Project;
    private const string CallerIdHeader = "MSCRMCallerID";
    private const string CallerObjectIdHeader = "CallerObjectId";

    private readonly string _tag = "IM" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _root, _child;
    private (Guid Id, Guid Oid) _d, _l;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _root = await RootBusinessUnitAsync(fx);
        _child = await CreateBusinessUnitAsync(fx, _root, "IM-" + _tag);
        _d = await UserAsync(_root);
        await AssignRoleAsync(fx, _d.Id, StandardRoles.Delegate);
        await GrantAsync(_d.Id, ("prvReadpb_Project", DepthMask.Global), ("prvCreatepb_Project", DepthMask.Global));
        _l = await UserAsync(_child);
        await GrantAsync(_l.Id, ("prvCreatepb_Project", DepthMask.Global));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit, bool disabled = false)
    {
        var oid = Guid.NewGuid();
        return (await CreateUserAsync(fx, businessUnit, oid, disabled), oid);
    }

    private async Task GrantAsync(Guid userId, params (string Privilege, DepthMask Depth)[] privileges) =>
        await AssignRoleAsync(fx, userId, await CreateRoleAsync(fx, "im-" + Guid.NewGuid().ToString("N")[..8], privileges));

    private Task<HttpResponseMessage> SendAsync((Guid Id, Guid Oid) user, HttpMethod method, string path,
        IEnumerable<(string Name, string Value)> headers, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(user.Oid, fx.BaseUrl));
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return fx.Client.SendAsync(req);
    }

    private static (string, string)[] CallerId(Guid id) => [(CallerIdHeader, id.ToString())];

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage res, HttpStatusCode expected) =>
        (await JsonAsync(res, expected))["error"]!["code"]!.GetValue<string>();

    private async Task<Guid> WhoAmIAsync((Guid Id, Guid Oid) user, params (string, string)[] headers) =>
        (await JsonAsync(await SendAsync(user, HttpMethod.Get, "WhoAmI", headers), HttpStatusCode.OK))["UserId"]!.GetValue<Guid>();

    private async Task<Guid> ScalarGuidAsync(string sql, Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        cmd.Parameters.AddWithValue("@id", id);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<Guid> ProjectAsync(string label, Guid owner) =>
        _admin.CreateAsync(new Entity(Project) { ["pb_name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference("systemuser", owner) });

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MSCRMCallerID_runs_as_impersonated_user()
    {
        var who = await JsonAsync(await SendAsync(_d, HttpMethod.Get, "WhoAmI", CallerId(_l.Id)), HttpStatusCode.OK);
        who["UserId"]!.GetValue<Guid>().Should().Be(_l.Id);
        who["BusinessUnitId"]!.GetValue<Guid>().Should().Be(_child);

        var res = await SendAsync(_d, HttpMethod.Post, TestEntities.ProjectSet, CallerId(_l.Id), $$"""{ "pb_name": "{{_tag}}-created" }""");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var entityId = res.Headers.GetValues("OData-EntityId").Single();
        var id = Guid.Parse(entityId[(entityId.LastIndexOf('(') + 1)..^1]);
        (await ScalarGuidAsync("SELECT createdby FROM data.pb_project WHERE pb_projectid = @id", id)).Should().Be(_l.Id);
        (await ScalarGuidAsync("SELECT ownerid FROM data.pb_project WHERE pb_projectid = @id", id)).Should().Be(_l.Id);
        (await ScalarGuidAsync("SELECT owningbusinessunit FROM data.pb_project WHERE pb_projectid = @id", id)).Should().Be(_child);

        // Guid.Empty is no impersonation; impersonating oneself neither.
        (await WhoAmIAsync(_d, CallerId(Guid.Empty))).Should().Be(_d.Id);
        (await WhoAmIAsync(_d, CallerId(_d.Id))).Should().Be(_d.Id);
        // The organization service honours the same header.
        var response = await TestServices.NewOrganizationService(fx.App.Services, _d.Oid, _l.Id).ExecuteAsync(new WhoAmIRequest());
        response.Results["UserId"].Should().Be(_l.Id);
    }

    [Fact]
    public async Task CallerObjectId_resolves_by_object_id_and_wins_over_MSCRMCallerID()
    {
        (await WhoAmIAsync(_d, (CallerObjectIdHeader, _l.Oid.ToString()))).Should().Be(_l.Id);
        (await WhoAmIAsync(_d, (CallerObjectIdHeader, _l.Oid.ToString()), (CallerIdHeader, _d.Id.ToString()))).Should().Be(_l.Id);
        (await WhoAmIAsync(_d, (CallerObjectIdHeader, Guid.Empty.ToString()), (CallerIdHeader, _l.Id.ToString()))).Should().Be(_l.Id);

        var unknown = await SendAsync(_d, HttpMethod.Get, "WhoAmI", [(CallerObjectIdHeader, Guid.NewGuid().ToString())]);
        (await ErrorCodeAsync(unknown, HttpStatusCode.Forbidden)).Should().Be("0x80072560"); // NotMemberOfOrganization
    }

    [Fact]
    public async Task Without_act_on_behalf_is_403_0x8004a110()
    {
        var plain = await UserAsync(_root);
        await GrantAsync(plain.Id, ("prvReadpb_Project", DepthMask.Global), ("prvCreatepb_Project", DepthMask.Global));

        var res = await SendAsync(plain, HttpMethod.Get, "WhoAmI", CallerId(_l.Id));
        var json = await JsonAsync(res, HttpStatusCode.Forbidden);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x8004a110");
        json["error"]!["message"]!.GetValue<string>().Should().Be("The caller lacks prvActOnBehalfOfAnotherUser, which impersonating another user requires.");
        (await ErrorCodeAsync(await SendAsync(plain, HttpMethod.Get, "WhoAmI", [(CallerObjectIdHeader, _l.Oid.ToString())]),
            HttpStatusCode.Forbidden)).Should().Be("0x8004a110");
    }

    [Fact]
    public async Task Delegate_through_team_only_is_not_enough()
    {
        var member = await UserAsync(_root);
        await GrantAsync(member.Id, ("prvReadpb_Project", DepthMask.Global));
        var team = await CreateTeamAsync(fx, _root, teamType: 0, member.Id);
        await AssignRoleAsync(fx, team, StandardRoles.Delegate, team: true);

        (await ErrorCodeAsync(await SendAsync(member, HttpMethod.Get, "WhoAmI", CallerId(_l.Id)), HttpStatusCode.Forbidden))
            .Should().Be("0x8004a110");
    }

    [Fact]
    public async Task Privileges_are_the_intersection()
    {
        // D2: Delegate, Read Basic, no Delete; L2: Read and Delete Global.
        var d2 = await UserAsync(_root);
        await AssignRoleAsync(fx, d2.Id, StandardRoles.Delegate);
        await GrantAsync(d2.Id, ("prvReadpb_Project", DepthMask.Basic));
        var l2 = await UserAsync(_child);
        await GrantAsync(l2.Id, ("prvReadpb_Project", DepthMask.Global), ("prvDeletepb_Project", DepthMask.Global));
        var ofD2 = await ProjectAsync("d2", d2.Id);
        var ofL2 = await ProjectAsync("l2", l2.Id);
        var ofD = await ProjectAsync("d", _d.Id);

        // Delete: L2 may, D2 may not → the privilege of D2 is missing.
        var deleted = await JsonAsync(await SendAsync(d2, HttpMethod.Delete, $"{TestEntities.ProjectSet}({ofL2})", CallerId(l2.Id)),
            HttpStatusCode.Forbidden);
        deleted["error"]!["code"]!.GetValue<string>().Should().Be("0x80040220");
        deleted["error"]!["message"]!.GetValue<string>().Should().Contain($"Id={d2.Id}").And.Contain("prvDeletepb_Project");

        // Read: D2 Basic AND L2 Global → only D2's records (D2's owner condition is about D2, not the impersonated L2).
        var query = $"{TestEntities.ProjectSet}?$select=pb_name&$filter=startswith(pb_name,'{_tag}')";
        Ids(await JsonAsync(await SendAsync(l2, HttpMethod.Get, query, []), HttpStatusCode.OK))
            .Should().BeEquivalentTo([ofD2, ofL2, ofD]);
        Ids(await JsonAsync(await SendAsync(d2, HttpMethod.Get, query, CallerId(l2.Id)), HttpStatusCode.OK))
            .Should().BeEquivalentTo([ofD2]);

        // A record of one of D2's teams is D2's too.
        var team = await CreateTeamAsync(fx, _root, teamType: 0, d2.Id);
        var ofTeam = await _admin.CreateAsync(new Entity(Project)
        {
            ["pb_name"] = $"{_tag}-team", ["ownerid"] = new EntityReference("team", team),
        });
        Ids(await JsonAsync(await SendAsync(d2, HttpMethod.Get, query, CallerId(l2.Id)), HttpStatusCode.OK))
            .Should().BeEquivalentTo([ofD2, ofTeam]);
    }

    [Fact]
    public async Task Impersonating_disabled_unknown_or_SYSTEM()
    {
        var disabled = await UserAsync(_root, disabled: true);
        await GrantAsync(disabled.Id, ("prvReadpb_Project", DepthMask.Global));
        var roleless = await UserAsync(_root);
        var system = await Cache(fx).SystemUserIdAsync(default);

        (await ErrorCodeAsync(await SendAsync(_d, HttpMethod.Get, "WhoAmI", CallerId(disabled.Id)), HttpStatusCode.Forbidden))
            .Should().Be("0x80040225");
        (await ErrorCodeAsync(await SendAsync(_d, HttpMethod.Get, "WhoAmI", CallerId(roleless.Id)), HttpStatusCode.Forbidden))
            .Should().Be("0x80042f09");
        var unknown = Guid.NewGuid();
        var notFound = await JsonAsync(await SendAsync(_d, HttpMethod.Get, "WhoAmI", CallerId(unknown)), HttpStatusCode.NotFound);
        notFound["error"]!["code"]!.GetValue<string>().Should().Be("0x80040217");
        notFound["error"]!["message"]!.GetValue<string>().Should().Be($"systemuser With Id = {unknown} Does Not Exist");
        (await ErrorCodeAsync(await SendAsync(_d, HttpMethod.Get, "WhoAmI", CallerId(system)), HttpStatusCode.Forbidden))
            .Should().Be("0x8004a110");
    }

    [Fact]
    public async Task Plugins_see_the_authenticated_user_at_every_depth()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            await GrantAsync(_d.Id, ("prvCreatepb_Pipe", DepthMask.Global), ("prvCreatepb_Pipe2Log", DepthMask.Global));
            await GrantAsync(_l.Id, ("prvCreatepb_Pipe", DepthMask.Global), ("prvCreatepb_Pipe2Log", DepthMask.Global));
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
            var creator = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, "Xrmd.TestPlugins.CreateRecordPlugin");
            var recorder = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
            // Depth 1: the recorder on pb_pipe; depth 2: a pb_pipelog created as SYSTEM, recorded too.
            await PluginTestHelper.RegisterStepAsync(_admin, recorder, "Create", TestEntities.Pipe, 40, config: TestEntities.PipeLog2);
            await PluginTestHelper.RegisterStepAsync(_admin, creator, "Create", TestEntities.Pipe, 40, rank: 2,
                config: TestEntities.PipeLog + "|system");
            await PluginTestHelper.RegisterStepAsync(_admin, recorder, "Create", TestEntities.PipeLog, 40, config: TestEntities.PipeLog2);

            async Task<Dictionary<string, string>> RecordAsync(params (string, string)[] headers)
            {
                await ExecAsync($"DELETE FROM data.{TestEntities.PipeLog2}");
                var res = await SendAsync(_d, HttpMethod.Post, TestEntities.PipeSet, headers, """{ "pb_name": "auth" }""");
                res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
                var rows = new Dictionary<string, string>();
                await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
                await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog2}");
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) rows[rd.GetString(0)] = rd.GetString(1);
                return rows;
            }

            var plain = await RecordAsync();
            plain.Keys.Should().BeEquivalentTo("Create|40|0|1", "Create|40|0|2");
            plain.Values.Should().OnlyContain(data => data.Contains($"authenticated={_d.Id}"));

            // Impersonating L: the plug-ins run as L (then SYSTEM), the authenticated user stays D.
            var impersonated = await RecordAsync(CallerId(_l.Id));
            impersonated["Create|40|0|1"].Should().Contain($"user={_l.Id}").And.Contain($"authenticated={_d.Id}");
            impersonated["Create|40|0|2"].Should().Contain($"authenticated={_d.Id}");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
            await ExecAsync($"DELETE FROM data.{TestEntities.Pipe}; DELETE FROM data.{TestEntities.PipeLog}; DELETE FROM data.{TestEntities.PipeLog2};");
        }
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Invalid_header_guid_is_400()
    {
        (await ErrorCodeAsync(await SendAsync(_d, HttpMethod.Get, "WhoAmI", [(CallerIdHeader, "not-a-guid")]), HttpStatusCode.BadRequest))
            .Should().Be("0x80040203");
        (await ErrorCodeAsync(await SendAsync(_d, HttpMethod.Get, "WhoAmI", [(CallerObjectIdHeader, "42")]), HttpStatusCode.BadRequest))
            .Should().Be("0x80040203");
    }

    private static IEnumerable<Guid> Ids(JsonObject json) =>
        json["value"]!.AsArray().Select(v => Guid.Parse(v!["pb_projectid"]!.GetValue<string>()));
}
