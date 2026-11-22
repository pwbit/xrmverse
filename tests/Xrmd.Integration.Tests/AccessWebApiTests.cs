using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1b, task 2: RetrievePrincipalAccess, RetrieveSharedPrincipalsAndAccess, GrantAccess, ModifyAccess and RevokeAccess
/// over the Web API. Business units R (root) → A and R → B; users uA and uB, each owning one <c>pb_project</c>.
/// </summary>
[Collection("xrmd")]
public sealed class AccessWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private const string Api = "/api/data/v9.2/";
    private const string Ns = "Microsoft.Dynamics.CRM";

    private const string AllRights =
        "ReadAccess, WriteAccess, AppendAccess, AppendToAccess, CreateAccess, DeleteAccess, ShareAccess, AssignAccess";

    private readonly string _tag = "AW" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _a, _b;
    private (Guid Id, Guid Oid) _uA, _uB;
    private Guid _pA, _pB;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureProjectAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        _admin = ServiceFor(fx, fx.AdminObjectId);
        var root = await RootBusinessUnitAsync(fx);
        _a = await CreateBusinessUnitAsync(fx, root, "A-" + _tag);
        _b = await CreateBusinessUnitAsync(fx, root, "B-" + _tag);
        _uA = await UserAsync(_a);
        _uB = await UserAsync(_b);
        _pA = await ProjectAsync("A", "systemuser", _uA.Id);
        _pB = await ProjectAsync("B", "systemuser", _uB.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit)
    {
        var oid = Guid.NewGuid();
        return (await CreateUserAsync(fx, businessUnit, oid), oid);
    }

    private Task<Guid> ProjectAsync(string label, string ownerTable, Guid owner) =>
        _admin.CreateAsync(new Entity(Project) { ["pb_name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference(ownerTable, owner) });

    private static (string Privilege, DepthMask Depth)[] Privileges(DepthMask depth, params string[] verbs) =>
        [.. verbs.Select(v => ($"prv{v}pb_Project", depth))];

    private Task GrantAsync(Guid principalId, bool team, params (string Privilege, DepthMask Depth)[] privileges) =>
        SecurityTestHelper.GrantAsync(fx, principalId, team, privileges);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid oid, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static string Reference(Guid project, string set = "pb_projects") => Uri.EscapeDataString($$"""{"@odata.id":"{{set}}({{project}})"}""");

    private static string EntityJson(string table, Guid id) => $$"""{ "@odata.type": "{{Ns}}.{{table}}", "{{table}}id": "{{id}}" }""";

    private async Task<string> PrincipalAccessAsync(string set, Guid principal, Guid project, Guid caller, string targetSet = "pb_projects")
    {
        var res = await SendAsync(HttpMethod.Get, $"{set}({principal})/{Ns}.RetrievePrincipalAccess(Target=@t)?@t={Reference(project, targetSet)}", caller);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        json["@odata.context"]!.GetValue<string>().Should().EndWith($"$metadata#{Ns}.RetrievePrincipalAccessResponse");
        return json["AccessRights"]!.GetValue<string>();
    }

    private async Task<JsonArray> SharesAsync(Guid project, Guid caller)
    {
        var res = await SendAsync(HttpMethod.Get, $"RetrieveSharedPrincipalsAndAccess(Target=@t)?@t={Reference(project)}", caller);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        json["@odata.context"]!.GetValue<string>().Should().EndWith($"$metadata#{Ns}.RetrieveSharedPrincipalsAndAccessResponse");
        return json["PrincipalAccesses"]!.AsArray();
    }

    private static string Access(string mask, string table, Guid principal) =>
        $$"""{ "AccessMask": "{{mask}}", "Principal": {{EntityJson(table, principal)}} }""";

    private Task<HttpResponseMessage> ActionAsync(string action, string body, Guid caller) =>
        SendAsync(HttpMethod.Post, action, caller, body);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage res) =>
        JsonNode.Parse(await res.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>();

    // ---- RetrievePrincipalAccess ------------------------------------------------------------------------------------

    [Fact]
    public async Task Principal_access_of_owner_is_full()
    {
        await GrantAsync(_uA.Id, team: false,
            Privileges(DepthMask.Basic, "Read", "Write", "Append", "AppendTo", "Create", "Delete", "Share", "Assign"));

        (await PrincipalAccessAsync("systemusers", _uA.Id, _pA, _uA.Oid)).Should().Be(AllRights);
        // The same user on a record of another business unit it cannot reach: nothing (a read check applies to the caller, so the
        // administrator asks).
        (await PrincipalAccessAsync("systemusers", _uA.Id, _pB, fx.AdminObjectId)).Should().Be("None");
    }

    [Fact]
    public async Task Principal_access_from_share()
    {
        // The privileges at Basic depth do not reach the record of uA; the Read share does, for Read only.
        await GrantAsync(_uB.Id, team: false, Privileges(DepthMask.Basic, "Read", "Write", "Delete", "Share"));
        (await PrincipalAccessAsync("systemusers", _uB.Id, _pA, fx.AdminObjectId)).Should().Be("None");

        var grant = await ActionAsync("GrantAccess",
            $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access("ReadAccess", "systemuser", _uB.Id)}} }""", fx.AdminObjectId);
        grant.StatusCode.Should().Be(HttpStatusCode.NoContent, await grant.Content.ReadAsStringAsync());

        (await PrincipalAccessAsync("systemusers", _uB.Id, _pA, fx.AdminObjectId)).Should().Be("ReadAccess");
    }

    [Fact]
    public async Task Principal_access_for_team()
    {
        var team = await CreateTeamAsync(fx, _a, teamType: 0, _uB.Id);
        await GrantAsync(team, team: true, Privileges(DepthMask.Basic, "Read", "Write"));

        // A record the team owns: the team's own rights, from its roles.
        var owned = await ProjectAsync("T", "team", team);
        (await PrincipalAccessAsync("teams", team, owned, fx.AdminObjectId)).Should().Be("ReadAccess, WriteAccess");

        // A record it does not own: only what a share gives, and only the rights its roles hold.
        (await PrincipalAccessAsync("teams", team, _pA, fx.AdminObjectId)).Should().Be("None");
        var grant = await ActionAsync("GrantAccess",
            $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access("ReadAccess, DeleteAccess", "team", team)}} }""", fx.AdminObjectId);
        grant.StatusCode.Should().Be(HttpStatusCode.NoContent, await grant.Content.ReadAsStringAsync());
        (await PrincipalAccessAsync("teams", team, _pA, fx.AdminObjectId)).Should().Be("ReadAccess");
    }

    [Fact]
    public async Task Principal_access_for_team_follows_the_depth_of_its_roles()
    {
        // Global Read on a user-owned record the team does not own: the evaluator gives Read; Write is not granted.
        var global = await CreateTeamAsync(fx, _a, teamType: 0, _uB.Id);
        await GrantAsync(global, team: true, Privileges(DepthMask.Global, "Read"));
        (await PrincipalAccessAsync("teams", global, _pA, fx.AdminObjectId)).Should().Be("ReadAccess");
        (await PrincipalAccessAsync("teams", global, _pB, fx.AdminObjectId)).Should().Be("ReadAccess");

        // Local depth reaches the records of the team's business unit only (A, not B).
        var local = await CreateTeamAsync(fx, _a, teamType: 0);
        await GrantAsync(local, team: true, Privileges(DepthMask.Local, "Read", "Write"));
        (await PrincipalAccessAsync("teams", local, _pA, fx.AdminObjectId)).Should().Be("ReadAccess, WriteAccess");
        (await PrincipalAccessAsync("teams", local, _pB, fx.AdminObjectId)).Should().Be("None");

        // A table owned by the organization: a privilege at any depth covers every record.
        var currency = (Guid)(await ScalarAsync("SELECT TOP 1 transactioncurrencyid FROM data.transactioncurrency"))!;
        var orgTeam = await CreateTeamAsync(fx, _a, teamType: 0);
        await SecurityTestHelper.GrantAsync(fx, orgTeam, team: true, ("prvReadTransactionCurrency", DepthMask.Basic));
        (await PrincipalAccessAsync("teams", orgTeam, currency, fx.AdminObjectId, "transactioncurrencies")).Should().Be("ReadAccess");
    }

    [Fact]
    public async Task Principal_access_of_the_system_user_is_full()
    {
        var system = await fx.App.Services.GetRequiredService<SecurityCache>().SystemUserIdAsync(default);
        (await PrincipalAccessAsync("systemusers", system, _pA, fx.AdminObjectId)).Should().Be(AllRights);
    }

    [Fact]
    public async Task Principal_access_needs_a_bound_user_or_team_and_a_record_the_caller_reads()
    {
        // Organization is a Learn binding xrmverse does not offer: refused, not answered with another principal's rights.
        var org = await SendAsync(HttpMethod.Get, $"organizations({Guid.NewGuid()})/{Ns}.RetrievePrincipalAccess(Target=@t)?@t={Reference(_pA)}", fx.AdminObjectId);
        org.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var missing = await SendAsync(HttpMethod.Get,
            $"systemusers({_uA.Id})/{Ns}.RetrievePrincipalAccess(Target=@t)?@t={Reference(Guid.NewGuid())}", fx.AdminObjectId);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unknownUser = await SendAsync(HttpMethod.Get,
            $"systemusers({Guid.NewGuid()})/{Ns}.RetrievePrincipalAccess(Target=@t)?@t={Reference(_pA)}", fx.AdminObjectId);
        unknownUser.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // uB holds no read on the record of uA: the Retrieve's 403.
        await GrantAsync(_uB.Id, team: false, Privileges(DepthMask.Basic, "Read"));
        var denied = await SendAsync(HttpMethod.Get,
            $"systemusers({_uB.Id})/{Ns}.RetrievePrincipalAccess(Target=@t)?@t={Reference(_pA)}", _uB.Oid);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- the actions and RetrieveSharedPrincipalsAndAccess ----------------------------------------------------------

    [Fact]
    public async Task Grant_modify_revoke_over_web_api()
    {
        await GrantAsync(_uB.Id, team: false, Privileges(DepthMask.Basic, "Read"));
        (await SharesAsync(_pA, fx.AdminObjectId)).Should().BeEmpty();

        var grant = await ActionAsync("GrantAccess",
            $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access("ReadAccess, WriteAccess", "systemuser", _uB.Id)}} }""", fx.AdminObjectId);
        grant.StatusCode.Should().Be(HttpStatusCode.NoContent, await grant.Content.ReadAsStringAsync());
        var shares = await SharesAsync(_pA, fx.AdminObjectId);
        var share = shares.Should().ContainSingle().Subject!;
        share["AccessMask"]!.GetValue<string>().Should().Be("ReadAccess, WriteAccess");
        share["Principal"]!["@odata.type"]!.GetValue<string>().Should().Be($"#{Ns}.systemuser");
        share["Principal"]!["systemuserid"]!.GetValue<string>().Should().Be(_uB.Id.ToString());

        // Grant adds; Modify replaces.
        (await ActionAsync("GrantAccess",
            $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access("DeleteAccess", "systemuser", _uB.Id)}} }""", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SharesAsync(_pA, fx.AdminObjectId)).Single()!["AccessMask"]!.GetValue<string>().Should().Be("ReadAccess, WriteAccess, DeleteAccess");

        var modify = await ActionAsync("ModifyAccess",
            $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access("ShareAccess", "systemuser", _uB.Id)}} }""", fx.AdminObjectId);
        modify.StatusCode.Should().Be(HttpStatusCode.NoContent, await modify.Content.ReadAsStringAsync());
        (await SharesAsync(_pA, fx.AdminObjectId)).Single()!["AccessMask"]!.GetValue<string>().Should().Be("ShareAccess");

        var revoke = await ActionAsync("RevokeAccess",
            $$"""{ "Target": {{EntityJson(Project, _pA)}}, "Revokee": {{EntityJson("systemuser", _uB.Id)}} }""", fx.AdminObjectId);
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent, await revoke.Content.ReadAsStringAsync());
        (await SharesAsync(_pA, fx.AdminObjectId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Shared_principals_list_users_and_teams()
    {
        var team = await CreateTeamAsync(fx, _a, teamType: 0, _uB.Id);
        await GrantAsync(_uB.Id, team: false, Privileges(DepthMask.Basic, "Read"));
        await GrantAsync(team, team: true, Privileges(DepthMask.Basic, "Read"));
        foreach (var (table, id, mask) in new[] { ("systemuser", _uB.Id, "ReadAccess"), ("team", team, "ReadAccess, AppendAccess") })
            (await ActionAsync("GrantAccess",
                $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access(mask, table, id)}} }""", fx.AdminObjectId))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var shares = await SharesAsync(_pA, fx.AdminObjectId);

        shares.Should().HaveCount(2);
        shares.Select(s => (s!["Principal"]!["@odata.type"]!.GetValue<string>(), s["AccessMask"]!.GetValue<string>())).Should().BeEquivalentTo(
        [
            ($"#{Ns}.systemuser", "ReadAccess"), ($"#{Ns}.team", "ReadAccess, AppendAccess"),
        ]);
        shares.Single(s => s!["Principal"]!["@odata.type"]!.GetValue<string>().EndsWith("team"))!["Principal"]!["teamid"]!
            .GetValue<string>().Should().Be(team.ToString());
    }

    [Fact]
    public async Task Grant_needs_share_access()
    {
        await GrantAsync(_uA.Id, team: false, Privileges(DepthMask.Basic, "Read", "Write"));
        await GrantAsync(_uB.Id, team: false, Privileges(DepthMask.Basic, "Read"));
        var body = $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access("ReadAccess", "systemuser", _uB.Id)}} }""";

        var denied = await ActionAsync("GrantAccess", body, _uA.Oid);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SharesAsync(_pA, fx.AdminObjectId)).Should().BeEmpty();
        (await ActionAsync("RevokeAccess", $$"""{ "Target": {{EntityJson(Project, _pA)}}, "Revokee": {{EntityJson("systemuser", _uB.Id)}} }""", _uA.Oid))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // With the Share privilege, the owner shares.
        await GrantAsync(_uA.Id, team: false, Privileges(DepthMask.Basic, "Share"));
        (await ActionAsync("GrantAccess", body, _uA.Oid)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SharesAsync(_pA, fx.AdminObjectId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_target_is_404()
    {
        var missing = Guid.NewGuid();
        (await ActionAsync("GrantAccess",
            $$"""{ "Target": {{EntityJson(Project, missing)}}, "PrincipalAccess": {{Access("ReadAccess", "systemuser", _uB.Id)}} }""", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ActionAsync("ModifyAccess",
            $$"""{ "Target": {{EntityJson(Project, missing)}}, "PrincipalAccess": {{Access("ReadAccess", "systemuser", _uB.Id)}} }""", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ActionAsync("RevokeAccess",
            $$"""{ "Target": {{EntityJson(Project, missing)}}, "Revokee": {{EntityJson("systemuser", _uB.Id)}} }""", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"RetrieveSharedPrincipalsAndAccess(Target=@t)?@t={Reference(missing)}", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        // An unknown principal.
        (await ActionAsync("GrantAccess",
            $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access("ReadAccess", "systemuser", Guid.NewGuid())}} }""", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Bad_access_mask_is_400()
    {
        foreach (var mask in new[] { "Bogus", "ReadAccess, Bogus", "3", "None" })
        {
            var res = await ActionAsync("GrantAccess",
                $$"""{ "Target": {{EntityJson(Project, _pA)}}, "PrincipalAccess": {{Access(mask, "systemuser", _uB.Id)}} }""", fx.AdminObjectId);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, mask);
            (await ErrorAsync(res)).Should().NotBeEmpty();
        }
        (await SharesAsync(_pA, fx.AdminObjectId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Metadata_lists_the_access_operations()
    {
        var res = await SendAsync(HttpMethod.Get, "$metadata", fx.AdminObjectId);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var xml = await res.Content.ReadAsStringAsync();

        xml.Should().Contain("""<ComplexType Name="PrincipalAccess">""")
            .And.Contain("""<Function Name="RetrievePrincipalAccess" IsBound="true">""")
            .And.Contain("""<Function Name="RetrieveSharedPrincipalsAndAccess">""")
            .And.Contain("""<Action Name="GrantAccess">""")
            .And.Contain("""<Action Name="ModifyAccess">""")
            .And.Contain("""<Action Name="RevokeAccess">""");
    }
}
