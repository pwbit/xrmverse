using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Query;
using Xrmd.Query.Model;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.5, task 4: the read access a manager gets through the manager hierarchy (ruling H4) on every read path, and the
/// operators <c>eq-useroruserhierarchy</c> / <c>eq-useroruserhierarchyandteams</c> (ruling H7). Every test builds its own
/// users and managers, and the organization settings and exclusions are reset after each test.
/// </summary>
[Collection("xrmd")]
public sealed class HierarchyReadTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Enabled = "ishierarchicalsecuritymodelenabled";
    private const string Depth = "maxdepthforhierarchicalsecuritymodel";
    private const string ReadAccount = "prvReadAccount";

    private readonly string _tag = "HR" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _root;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _root = await RootBusinessUnitAsync(fx);
    }

    public async Task DisposeAsync()
    {
        await ExecAsync($"UPDATE data.organization SET {Enabled} = 0, {Depth} = 3; DELETE FROM meta.hierarchy_security_exclusion;");
        Cache(fx).Invalidate();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SettingsAsync(bool enabled, int depth = 3)
    {
        await ExecAsync($"UPDATE data.organization SET {Enabled} = @e, {Depth} = @d", ("@e", enabled), ("@d", depth));
        Cache(fx).Invalidate();
    }

    private async Task ManagerAsync((Guid Id, Guid Oid) report, (Guid Id, Guid Oid) manager)
    {
        await ExecAsync("UPDATE data.systemuser SET parentsystemuserid = @m WHERE systemuserid = @u", ("@m", manager.Id), ("@u", report.Id));
        Cache(fx).Invalidate();
    }

    /// <summary>A user in <paramref name="businessUnit"/> holding <paramref name="privileges"/> (by a new role).</summary>
    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit, params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, businessUnit, oid);
        if (privileges.Length > 0) await GrantAsync(fx, id, team: false, privileges);
        return (id, oid);
    }

    private Task<Guid> AccountAsync(string label, Guid owner, string ownerEntity = "systemuser", Guid? parent = null)
    {
        var account = new Entity("account") { ["name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference(ownerEntity, owner) };
        if (parent is { } p) account["parentaccountid"] = new EntityReference("account", p);
        return _admin.CreateAsync(account);
    }

    private Task<Guid> ProjectAsync(string label, Guid owner) => _admin.CreateAsync(new Entity(TestEntities.Project)
    {
        ["pb_name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference("systemuser", owner),
    });

    private Task ShareAsync(string table, Guid recordId, Guid principalId, AccessRights rights) => ExecAsync("""
        INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask, inheritedaccessrightsmask)
        VALUES (NEWID(), @principal, 8, @object, @otc, @mask, 0)
        """, ("@principal", principalId), ("@object", recordId),
        ("@otc", fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(table)!.ObjectTypeCode!.Value), ("@mask", (int)rights));

    private OrganizationService As((Guid Id, Guid Oid) user, Guid? impersonating = null) =>
        TestServices.NewOrganizationService(fx.App.Services, user.Oid, impersonating);

    private QueryExpression Tagged(string table = "account", string name = "name")
    {
        var q = new QueryExpression(table) { ColumnSet = new ColumnSet(name) };
        q.Criteria.AddCondition(name, ConditionOperator.BeginsWith, _tag);
        return q;
    }

    private static async Task<IReadOnlyList<Guid>> IdsAsync(OrganizationService service, QueryBase query) =>
        (await service.RetrieveMultipleAsync(query)).Entities.Select(e => e.Id).ToList();

    private Task<IReadOnlyList<Guid>> VisibleAsync((Guid Id, Guid Oid) user, Guid? impersonating = null) =>
        IdsAsync(As(user, impersonating), Tagged());

    private FetchExpression Fetch(string condition) => new($"""
        <fetch><entity name="account"><attribute name="name"/>
          <filter><condition attribute="name" operator="begins-with" value="{_tag}"/>{condition}</filter>
        </entity></fetch>
        """);

    private Task<HttpResponseMessage> GetAsync((Guid Id, Guid Oid) user, string pathAndQuery)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(user.Oid, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static IEnumerable<Guid> Ids(JsonObject json) =>
        json["value"]!.AsArray().Select(v => Guid.Parse(v!["accountid"]!.GetValue<string>()));

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Manager_reads_reports_records()
    {
        var m = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        var r = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        var peer = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        await ManagerAsync(r, m);
        var ofM = await AccountAsync("m", m.Id);
        var ofR = await AccountAsync("r", r.Id);
        var child = await AccountAsync("child", m.Id, parent: ofR);
        await ShareAsync("account", child, peer.Id, AccessRights.ReadAccess);

        // Disabled model (the default): the manager's own records only.
        (await VisibleAsync(m)).Should().BeEquivalentTo([ofM, child]);

        await SettingsAsync(enabled: true);

        // QueryExpression, FetchXML and Retrieve.
        (await VisibleAsync(m)).Should().BeEquivalentTo([ofM, ofR, child]);
        (await VisibleAsync(peer)).Should().BeEquivalentTo([child]);
        (await IdsAsync(As(m), Fetch(""))).Should().BeEquivalentTo([ofM, ofR, child]);
        (await IdsAsync(As(peer), Fetch(""))).Should().BeEquivalentTo([child]);
        (await As(m).RetrieveAsync("account", ofR, new ColumnSet("name"))).Id.Should().Be(ofR);
        await FluentActions.Awaiting(() => As(peer).RetrieveAsync("account", ofR, new ColumnSet("name")))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.AccessDenied && e.HttpStatus == 403);

        // Web API: GET of the collection and of the record, and $expand (a link-entity, restricted on its own).
        var query = $"accounts?$select=name&$filter=startswith(name,'{_tag}')";
        Ids(await JsonAsync(await GetAsync(m, query))).Should().BeEquivalentTo([ofM, ofR, child]);
        Ids(await JsonAsync(await GetAsync(peer, query))).Should().BeEquivalentTo([child]);
        (await JsonAsync(await GetAsync(m, $"accounts({ofR})?$select=name")))["accountid"]!.GetValue<Guid>().Should().Be(ofR);
        await JsonAsync(await GetAsync(peer, $"accounts({ofR})?$select=name"), HttpStatusCode.Forbidden);
        var expand = $"accounts({child})?$select=name&$expand=parentaccountid($select=name)";
        (await JsonAsync(await GetAsync(m, expand)))["parentaccountid"]!["accountid"]!.GetValue<Guid>().Should().Be(ofR);
        (await JsonAsync(await GetAsync(peer, expand)))["parentaccountid"].Should().BeNull();

        // The report does not read its manager's records.
        (await VisibleAsync(r)).Should().BeEquivalentTo([ofR]);
    }

    [Fact]
    public async Task Manager_sees_team_and_shared_records_of_reports()
    {
        // The manager is in C; the report and a colleague of the report are in G, a child of C (the business unit rule holds).
        var c = await CreateBusinessUnitAsync(fx, _root, "hr C " + _tag);
        var g = await CreateBusinessUnitAsync(fx, c, "hr G " + _tag);
        var m = await UserAsync(c, (ReadAccount, DepthMask.Basic));
        var r = await UserAsync(g, (ReadAccount, DepthMask.Local)); // Learn's "User 3": reads its unit through its depth
        var colleague = await UserAsync(g, (ReadAccount, DepthMask.Basic));
        var outsider = await UserAsync(_root);
        await ManagerAsync(r, m);
        var ownerTeam = await CreateTeamAsync(fx, g, 0, r.Id);
        var accessTeam = await CreateTeamAsync(fx, g, 1, r.Id);

        var ofTeam = await AccountAsync("team", ownerTeam, "team");
        var sharedWithReport = await AccountAsync("shared-r", outsider.Id);
        var sharedWithTeam = await AccountAsync("shared-team", outsider.Id);
        var sharedWithAccessTeam = await AccountAsync("shared-access", outsider.Id);
        var sharedForWrite = await AccountAsync("shared-write", outsider.Id);
        var ofColleague = await AccountAsync("colleague", colleague.Id);
        var notShared = await AccountAsync("outsider", outsider.Id);
        await ShareAsync("account", sharedWithReport, r.Id, AccessRights.ReadAccess);
        await ShareAsync("account", sharedWithTeam, ownerTeam, AccessRights.ReadAccess | AccessRights.WriteAccess);
        await ShareAsync("account", sharedWithAccessTeam, accessTeam, AccessRights.ReadAccess);
        await ShareAsync("account", sharedForWrite, r.Id, AccessRights.WriteAccess); // no read right: not a read share
        await SettingsAsync(enabled: true);

        (await VisibleAsync(r)).Should().BeEquivalentTo([ofTeam, sharedWithReport, sharedWithTeam, sharedWithAccessTeam, ofColleague]);
        (await VisibleAsync(m)).Should().BeEquivalentTo([ofTeam, sharedWithReport, sharedWithTeam, sharedWithAccessTeam],
            "the colleague's record is the report's only through its business unit depth");
        (await VisibleAsync(m)).Should().NotContain([ofColleague, notShared, sharedForWrite]);
    }

    [Fact]
    public async Task Depth_limits_read_reach()
    {
        // gm ← l1 ← l2 ← l3, depth 2.
        var gm = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        var l1 = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        var l2 = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        var l3 = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        await ManagerAsync(l1, gm);
        await ManagerAsync(l2, l1);
        await ManagerAsync(l3, l2);
        var of1 = await AccountAsync("l1", l1.Id);
        var of2 = await AccountAsync("l2", l2.Id);
        var of3 = await AccountAsync("l3", l3.Id);
        await SettingsAsync(enabled: true, depth: 2);

        (await VisibleAsync(gm)).Should().BeEquivalentTo([of1, of2]);
        (await VisibleAsync(l1)).Should().BeEquivalentTo([of1, of2, of3]);
        await FluentActions.Awaiting(() => As(gm).RetrieveAsync("account", of3, new ColumnSet("name")))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.HttpStatus == 403);

        await SettingsAsync(enabled: true, depth: 3);
        (await VisibleAsync(gm)).Should().BeEquivalentTo([of1, of2, of3]);
    }

    [Fact]
    public async Task Excluded_and_disabled_model_give_no_reach()
    {
        var m = await UserAsync(_root, (ReadAccount, DepthMask.Basic), ("prvReadpb_Project", DepthMask.Basic));
        var r = await UserAsync(_root);
        await ManagerAsync(r, m);
        var account = await AccountAsync("r", r.Id);
        var project = await ProjectAsync("r", r.Id);
        var projects = Tagged(TestEntities.Project, "pb_name");

        (await VisibleAsync(m)).Should().BeEmpty();
        (await IdsAsync(As(m), projects)).Should().BeEmpty();

        await SettingsAsync(enabled: true);
        (await VisibleAsync(m)).Should().BeEquivalentTo([account]);
        (await IdsAsync(As(m), projects)).Should().BeEquivalentTo([project]);

        await TestEntities.InSessionAsync(fx.ConnectionString, s => Cache(fx).SetHierarchyExclusionsAsync(s, ["account"], default));
        (await VisibleAsync(m)).Should().BeEmpty();
        (await IdsAsync(As(m), projects)).Should().BeEquivalentTo([project], "only the excluded table loses the reach");
    }

    [Fact]
    public async Task Impersonation_ands_hierarchy_reach()
    {
        var m = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        var r = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        await ManagerAsync(r, m);
        var ofM = await AccountAsync("m", m.Id);
        var ofR = await AccountAsync("r", r.Id);
        // A non-manager application user that may impersonate, reading its own records only.
        var app = await UserAsync(_root, (ReadAccount, DepthMask.Basic));
        await AssignRoleAsync(fx, app.Id, StandardRoles.Delegate);
        // One reading everything.
        var globalApp = await UserAsync(_root, (ReadAccount, DepthMask.Global));
        await AssignRoleAsync(fx, globalApp.Id, StandardRoles.Delegate);
        await SettingsAsync(enabled: true);

        (await VisibleAsync(m)).Should().BeEquivalentTo([ofM, ofR]);
        (await VisibleAsync(app, impersonating: m.Id)).Should().BeEmpty("the AND of the manager's reach and the application user's own records");
        (await VisibleAsync(globalApp, impersonating: m.Id)).Should().BeEquivalentTo([ofM, ofR]);
        // The manager impersonating its report gets the report's view ANDed with its own: the report's records.
        await AssignRoleAsync(fx, m.Id, StandardRoles.Delegate);
        (await VisibleAsync(m, impersonating: r.Id)).Should().BeEquivalentTo([ofR]);

        // A plug-in running as the manager acts as a Caller without impersonator, so it gets the reach.
        await using var scope = fx.App.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<QueryExecutor>();
        var root = new EntityNode { EntityName = "account" };
        root.Columns.Add(new ColumnNode("name"));
        root.Filter.Conditions.Add(new ConditionNode(null, "name", ConditionOperator.BeginsWith, [_tag]));
        (await executor.ExecuteAsync(new QueryModel { Root = root }, new Caller(m.Id, _root))).Entities.Select(e => e.Id)
            .Should().BeEquivalentTo([ofM, ofR]);
    }

    [Fact]
    public async Task User_hierarchy_operators()
    {
        // m ← r1 ← r2 ← r3; r1 in an owner team; r1 in another business unit (no business unit rule for the operators).
        var other = await CreateBusinessUnitAsync(fx, _root, "hr other " + _tag);
        var m = await UserAsync(_root, (ReadAccount, DepthMask.Global));
        var r1 = await UserAsync(other);
        var r2 = await UserAsync(_root);
        var r3 = await UserAsync(_root);
        await ManagerAsync(r1, m);
        await ManagerAsync(r2, r1);
        await ManagerAsync(r3, r2);
        var team = await CreateTeamAsync(fx, other, 0, r1.Id);
        var ofM = await AccountAsync("m", m.Id);
        var of1 = await AccountAsync("r1", r1.Id);
        var of2 = await AccountAsync("r2", r2.Id);
        var of3 = await AccountAsync("r3", r3.Id);
        var ofTeam = await AccountAsync("team", team, "team");
        await AccountAsync("other", (await UserAsync(_root)).Id);

        // Disabled model: 400.
        var fault = (await FluentActions.Awaiting(() => As(m).RetrieveMultipleAsync(Fetch("""<condition attribute="ownerid" operator="eq-useroruserhierarchy"/>""")))
            .Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("hierarchy security model");

        await SettingsAsync(enabled: true, depth: 2);
        (await IdsAsync(As(m), Fetch("""<condition attribute="ownerid" operator="eq-useroruserhierarchy"/>""")))
            .Should().BeEquivalentTo([ofM, of1, of2]);
        (await IdsAsync(As(m), Fetch("""<condition attribute="ownerid" operator="eq-useroruserhierarchyandteams"/>""")))
            .Should().BeEquivalentTo([ofM, of1, of2, ofTeam]);

        // QueryExpression, and inside a nested filter.
        var q = Tagged();
        var nested = new FilterExpression(LogicalOperator.Or);
        nested.AddCondition("ownerid", ConditionOperator.EqualUserOrUserHierarchyAndTeams);
        q.Criteria.AddFilter(nested);
        (await IdsAsync(As(m), q)).Should().BeEquivalentTo([ofM, of1, of2, ofTeam]);

        // A report's hierarchy starts at the report.
        await GrantAsync(fx, r1.Id, team: false, (ReadAccount, DepthMask.Global));
        (await IdsAsync(As(r1), Fetch("""<condition attribute="ownerid" operator="eq-useroruserhierarchy"/>""")))
            .Should().BeEquivalentTo([of1, of2, of3]);

        // A column that is not an id.
        await FluentActions.Awaiting(() => As(m).RetrieveMultipleAsync(Fetch("""<condition attribute="name" operator="eq-useroruserhierarchy"/>""")))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.HttpStatus == 400);
    }

    [Theory]
    [InlineData("Under")]
    [InlineData("UnderOrEqual")]
    [InlineData("Above")]
    [InlineData("AboveOrEqual")]
    [InlineData("NotUnder")]
    public async Task Web_api_hierarchy_query_functions_fail_with_400(string function)
    {
        // H9: no Dataverse query function is supported in $filter; the hierarchical ones fail explicitly (use ?fetchXml=).
        var query = $"accounts?$select=name&$filter=Microsoft.Dynamics.CRM.{function}(PropertyName='parentaccountid',PropertyValue='{Guid.NewGuid()}')";
        var json = await JsonAsync(await GetAsync((Guid.Empty, fx.AdminObjectId), query), HttpStatusCode.BadRequest);
        json["error"]!["message"]!.GetValue<string>().Should()
            .Contain($"Microsoft.Dynamics.CRM.{function}").And.Contain("query functions are not supported").And.Contain("fetchXml");
    }

    [Fact]
    public async Task Web_api_user_hierarchy_query_functions_fail_with_400()
    {
        var query = "accounts?$select=name&$filter=Microsoft.Dynamics.CRM.EqualUserOrUserHierarchy(PropertyName='ownerid')";
        var json = await JsonAsync(await GetAsync((Guid.Empty, fx.AdminObjectId), query), HttpStatusCode.BadRequest);
        json["error"]!["message"]!.GetValue<string>().Should().Contain("query functions are not supported");
    }
}
