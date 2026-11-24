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
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The read restriction of Retrieve and RetrieveMultiple across QueryExpression, FetchXML and OData. Business units
/// R (root) → A → A1 and R → B; users uR, uA, uA1, uB, each owning one <c>pb_project</c>; one role per test.
/// </summary>
[Collection("xrmd")]
public sealed class ReadSecurityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Project = TestEntities.Project;
    private const string ReadProject = "prvReadpb_Project";

    private readonly string _tag = "RS" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _root, _a, _a1, _b;
    private (Guid Id, Guid Oid) _uR, _uA, _uA1, _uB;
    private Guid _pR, _pA, _pA1, _pB;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeParentAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _root = await RootBusinessUnitAsync(fx);
        _a = await CreateBusinessUnitAsync(fx, _root, "A-" + _tag);
        _a1 = await CreateBusinessUnitAsync(fx, _a, "A1-" + _tag);
        _b = await CreateBusinessUnitAsync(fx, _root, "B-" + _tag);
        _uR = await UserAsync(_root);
        _uA = await UserAsync(_a);
        _uA1 = await UserAsync(_a1);
        _uB = await UserAsync(_b);
        _pR = await ProjectAsync("R", _uR.Id);
        _pA = await ProjectAsync("A", _uA.Id);
        _pA1 = await ProjectAsync("A1", _uA1.Id);
        _pB = await ProjectAsync("B", _uB.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit)
    {
        var oid = Guid.NewGuid();
        return (await CreateUserAsync(fx, businessUnit, oid), oid);
    }

    private Task<Guid> ProjectAsync(string label, Guid owner, string ownerEntity = "systemuser") =>
        _admin.CreateAsync(new Entity(Project) { ["pb_name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference(ownerEntity, owner) });

    /// <summary>Gives <paramref name="userId"/> a new role with the privileges (none: a role without privileges).</summary>
    private async Task GrantAsync(Guid userId, params (string Privilege, DepthMask Depth)[] privileges) =>
        await AssignRoleAsync(fx, userId, await CreateRoleAsync(fx, "rs-" + Guid.NewGuid().ToString("N")[..8], privileges));

    private OrganizationService As((Guid Id, Guid Oid) user) => ServiceFor(fx, user.Oid);

    private QueryExpression Tagged(string? prefix = null)
    {
        var q = new QueryExpression(Project) { ColumnSet = new ColumnSet("pb_name") };
        q.Criteria.AddCondition("pb_name", ConditionOperator.BeginsWith, prefix ?? _tag);
        q.AddOrder("pb_name", OrderType.Ascending);
        return q;
    }

    private async Task<IReadOnlyList<Guid>> VisibleAsync((Guid Id, Guid Oid) user) =>
        (await As(user).RetrieveMultipleAsync(Tagged())).Entities.Select(e => e.Id).ToList();

    private Task<HttpResponseMessage> GetAsync((Guid Id, Guid Oid) user, string pathAndQuery)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(user.Oid, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static IEnumerable<Guid> Ids(JsonObject json, string idName) =>
        json["value"]!.AsArray().Select(v => Guid.Parse(v![idName]!.GetValue<string>()));

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task ShareAsync(Guid recordId, Guid principalId, AccessRights rights) => ExecAsync("""
        INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask, inheritedaccessrightsmask)
        VALUES (NEWID(), @principal, 8, @object, @otc, @mask, 0)
        """, ("@principal", principalId), ("@object", recordId),
        ("@otc", fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(Project)!.ObjectTypeCode!.Value), ("@mask", (int)rights));

    private static Task ShouldBePrivilegeDeniedAsync(Func<Task> act) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.HttpStatus == 403 && e.Message.Contains(ReadProject));

    // ---- depths -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Basic_reads_own_and_team_records()
    {
        var team = await CreateTeamAsync(fx, _a, teamType: 0, _uA.Id);
        var pTeam = await ProjectAsync("T", team, "team");
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Basic));

        (await VisibleAsync(_uA)).Should().BeEquivalentTo([_pA, pTeam]);
    }

    [Fact]
    public async Task Local_reads_business_unit()
    {
        var uA2 = await UserAsync(_a);
        var pA2 = await ProjectAsync("A2", uA2.Id);
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Local));

        (await VisibleAsync(_uA)).Should().BeEquivalentTo([_pA, pA2]);
    }

    [Fact]
    public async Task Deep_reads_subtree()
    {
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Deep));
        (await VisibleAsync(_uA)).Should().BeEquivalentTo([_pA, _pA1]);
    }

    [Fact]
    public async Task Global_reads_all()
    {
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Global));
        (await VisibleAsync(_uA)).Should().BeEquivalentTo([_pR, _pA, _pA1, _pB]);
    }

    // ---- errors -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_read_privilege_is_403_PrivilegeDenied()
    {
        await GrantAsync(_uA.Id); // a role, but no prvReadpb_Project

        await ShouldBePrivilegeDeniedAsync(() => As(_uA).RetrieveMultipleAsync(Tagged()));
        await ShouldBePrivilegeDeniedAsync(() => As(_uA).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")));

        var json = await JsonAsync(await GetAsync(_uA, TestEntities.ProjectSet), HttpStatusCode.Forbidden);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040220");
        json["error"]!["message"]!.GetValue<string>().Should().Contain(ReadProject);
    }

    [Fact]
    public async Task Invisible_record_is_403_AccessDenied_missing_is_404()
    {
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Local));

        (await As(_uA).RetrieveAsync(Project, _pA, new ColumnSet("pb_name"))).Id.Should().Be(_pA);
        await FluentActions.Awaiting(() => As(_uA).RetrieveAsync(Project, _pB, new ColumnSet("pb_name")))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.AccessDenied && e.HttpStatus == 403
                        && e.Message == $"Principal user with id {_uA.Id} does not have ReadAccess right(s) for record with id {_pB} of entity {Project}.");
        await FluentActions.Awaiting(() => As(_uA).RetrieveAsync(Project, Guid.NewGuid(), new ColumnSet("pb_name")))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);

        var json = await JsonAsync(await GetAsync(_uA, $"{TestEntities.ProjectSet}({_pB})"), HttpStatusCode.Forbidden);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80048306");
    }

    // ---- front ends, paging, links ----------------------------------------------------------------------------------

    [Fact]
    public async Task All_front_ends_apply_the_same_predicate()
    {
        var uA2 = await UserAsync(_a);
        var pA2 = await ProjectAsync("A2", uA2.Id);
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Local));
        Guid[] expected = [_pA, pA2];

        (await VisibleAsync(_uA)).Should().BeEquivalentTo(expected);

        var fetch = $"""
            <fetch><entity name="{Project}"><attribute name="pb_name"/>
              <filter><condition attribute="pb_name" operator="begins-with" value="{_tag}"/></filter>
            </entity></fetch>
            """;
        (await As(_uA).RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Select(e => e.Id).Should().BeEquivalentTo(expected);

        var viaFetchXml = await JsonAsync(await GetAsync(_uA, $"{TestEntities.ProjectSet}?fetchXml={Uri.EscapeDataString(fetch)}"), HttpStatusCode.OK);
        Ids(viaFetchXml, "pb_projectid").Should().BeEquivalentTo(expected);

        var viaOData = await JsonAsync(await GetAsync(_uA, $"{TestEntities.ProjectSet}?$select=pb_name&$filter=startswith(pb_name,'{_tag}')"), HttpStatusCode.OK);
        Ids(viaOData, "pb_projectid").Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Paging_count_and_aggregates_see_only_visible_records()
    {
        var page = _tag + "-pg";
        var visible = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            // Interleaved names: invisible records sit between visible ones in the order.
            var id = await ProjectAsync($"pg{i:00}", i % 2 == 0 || i == 11 ? _uA.Id : _uB.Id);
            if (i % 2 == 0 || i == 11) visible.Add(id);
        }
        visible.Should().HaveCount(7);
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Local));

        var q = Tagged(page);
        q.PageInfo = new PagingInfo { Count = 3, PageNumber = 1, ReturnTotalRecordCount = true };
        var seen = new List<Guid>();
        var sizes = new List<int>();
        while (true)
        {
            var result = await As(_uA).RetrieveMultipleAsync(q);
            result.TotalRecordCount.Should().Be(7);
            sizes.Add(result.Entities.Count);
            seen.AddRange(result.Entities.Select(e => e.Id));
            if (!result.MoreRecords) break;
            q.PageInfo.PageNumber++;
            q.PageInfo.PagingCookie = result.PagingCookie;
        }
        sizes.Should().Equal(3, 3, 1);
        seen.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(visible);

        var count = await As(_uA).RetrieveMultipleAsync(new FetchExpression($"""
            <fetch aggregate="true"><entity name="{Project}"><attribute name="pb_projectid" alias="n" aggregate="count"/>
              <filter><condition attribute="pb_name" operator="begins-with" value="{page}"/></filter>
            </entity></fetch>
            """));
        count.Entities.Single().GetAttributeValue<AliasedValue>("n").Value.Should().Be(7);

        var json = await JsonAsync(await GetAsync(_uA, $"{TestEntities.ProjectSet}?$select=pb_name&$filter=startswith(pb_name,'{page}')&$count=true"), HttpStatusCode.OK);
        json["@odata.count"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task Links_to_unreadable_records()
    {
        var parent = await _admin.CreateAsync(new Entity(TestEntities.PipeParent)
        {
            ["pb_name"] = _tag + "-parent", ["ownerid"] = new EntityReference("systemuser", _uB.Id),
        });
        var pipe = await _admin.CreateAsync(new Entity(TestEntities.Pipe)
        {
            ["pb_name"] = _tag + "-pipe", ["ownerid"] = new EntityReference("systemuser", _uA.Id),
            ["pb_parentid"] = new EntityReference(TestEntities.PipeParent, parent),
        });
        await GrantAsync(_uA.Id, ("prvReadpb_Pipe", DepthMask.Local), ("prvReadpb_PipeParent", DepthMask.Local));

        QueryExpression Query(JoinOperator join)
        {
            var q = new QueryExpression(TestEntities.Pipe) { ColumnSet = new ColumnSet("pb_name") };
            q.Criteria.AddCondition("pb_name", ConditionOperator.Equal, _tag + "-pipe");
            var link = q.AddLink(TestEntities.PipeParent, "pb_parentid", "pb_pipeparentid", join);
            link.EntityAlias = "p";
            link.Columns = new ColumnSet("pb_name");
            return q;
        }

        (await As(_uA).RetrieveMultipleAsync(Query(JoinOperator.Inner))).Entities.Should().BeEmpty();
        var outer = (await As(_uA).RetrieveMultipleAsync(Query(JoinOperator.LeftOuter))).Entities.Should().ContainSingle().Subject;
        outer.Id.Should().Be(pipe);
        outer.Contains("p.pb_name").Should().BeFalse();

        // The same rows for the administrator see the parent.
        (await _admin.RetrieveMultipleAsync(Query(JoinOperator.Inner))).Entities.Single()
            .GetAttributeValue<AliasedValue>("p.pb_name").Value.Should().Be(_tag + "-parent");
    }

    [Fact]
    public async Task Link_without_read_privilege_is_403_PrivilegeDenied()
    {
        await GrantAsync(_uA.Id, ("prvReadpb_Pipe", DepthMask.Global)); // nothing on pb_pipeparent
        var q = new QueryExpression(TestEntities.Pipe) { ColumnSet = new ColumnSet("pb_name") };
        q.AddLink(TestEntities.PipeParent, "pb_parentid", "pb_pipeparentid", JoinOperator.LeftOuter).Columns = new ColumnSet("pb_name");

        await FluentActions.Awaiting(() => As(_uA).RetrieveMultipleAsync(q))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.HttpStatus == 403 && e.Message.Contains("prvReadpb_PipeParent"));
    }

    // ---- sharing and teams ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Shared_record_is_readable()
    {
        await GrantAsync(_uB.Id, (ReadProject, DepthMask.Basic));
        (await VisibleAsync(_uB)).Should().BeEquivalentTo([_pB]);

        await ShareAsync(_pA, _uB.Id, AccessRights.ReadAccess);
        (await VisibleAsync(_uB)).Should().BeEquivalentTo([_pB, _pA]);
        (await As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name"))).Id.Should().Be(_pA);

        // A share for Write only does not give Read.
        await ShareAsync(_pA1, _uB.Id, AccessRights.WriteAccess);
        (await VisibleAsync(_uB)).Should().BeEquivalentTo([_pB, _pA]);

        // A share does not replace the privilege.
        var uB2 = await UserAsync(_b);
        await GrantAsync(uB2.Id);
        await ShareAsync(_pA, uB2.Id, AccessRights.ReadAccess);
        await ShouldBePrivilegeDeniedAsync(() => As(uB2).RetrieveMultipleAsync(Tagged()));
        await ShouldBePrivilegeDeniedAsync(() => As(uB2).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")));
    }

    [Fact]
    public async Task Team_owned_record_is_visible_to_members()
    {
        // A team of another business unit: its records are visible to its members at Basic depth only.
        var team = await CreateTeamAsync(fx, _b, teamType: 0, _uA.Id);
        var pTeam = await ProjectAsync("TB", team, "team");
        await GrantAsync(_uA.Id, (ReadProject, DepthMask.Basic));
        await GrantAsync(_uA1.Id, (ReadProject, DepthMask.Basic));

        (await VisibleAsync(_uA)).Should().BeEquivalentTo([_pA, pTeam]);
        (await VisibleAsync(_uA1)).Should().BeEquivalentTo([_pA1]);
    }

    // ---- hierarchy operators ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Under_operator_on_owningbusinessunit()
    {
        async Task<IEnumerable<Guid>> FetchAsync(string op) =>
            (await _admin.RetrieveMultipleAsync(new FetchExpression($"""
                <fetch><entity name="{Project}"><attribute name="pb_name"/>
                  <filter>
                    <condition attribute="pb_name" operator="begins-with" value="{_tag}"/>
                    <condition attribute="owningbusinessunit" operator="{op}" value="{_a}"/>
                  </filter>
                </entity></fetch>
                """))).Entities.Select(e => e.Id);

        (await FetchAsync("under")).Should().BeEquivalentTo([_pA1]);
        (await FetchAsync("eq-or-under")).Should().BeEquivalentTo([_pA, _pA1]);
        (await FetchAsync("not-under")).Should().BeEquivalentTo([_pR, _pB]);
        (await FetchAsync("above")).Should().BeEquivalentTo([_pR]);
        (await FetchAsync("eq-or-above")).Should().BeEquivalentTo([_pR, _pA]);

        // A table without a hierarchical relationship (plan 2.5, H6): 400.
        await FluentActions.Awaiting(() => _admin.RetrieveMultipleAsync(new FetchExpression($"""
                <fetch><entity name="{Project}"><filter><condition attribute="pb_projectid" operator="under" value="{_pA}"/></filter></entity></fetch>
                """)))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400
                        && e.Message.Contains("hierarchical relationship", StringComparison.Ordinal));
    }
}
