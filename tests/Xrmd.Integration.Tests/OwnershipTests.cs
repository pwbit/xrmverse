using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class OwnershipTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;

    private OrganizationService _svc = null!;
    private Guid _adminId;
    private Guid _rootBuId;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _adminId = (Guid)(await ScalarAsync($"SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = '{fx.AdminObjectId}'"))!;
        _rootBuId = (Guid)(await ScalarAsync("SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL"))!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private Guid NewBusinessUnit(Guid? parent = null) => _svc.Create(new Entity("businessunit")
    {
        ["name"] = "BU-" + Guid.NewGuid().ToString("N"),
        ["parentbusinessunitid"] = new EntityReference("businessunit", parent ?? _rootBuId),
    });

    private Guid NewTeam(Guid businessUnit, int teamType = 0) => _svc.Create(new Entity("team")
    {
        ["name"] = "Team-" + Guid.NewGuid().ToString("N"),
        ["businessunitid"] = new EntityReference("businessunit", businessUnit),
        ["teamtype"] = new OptionSetValue(teamType),
    });

    private Guid NewProject(EntityReference? owner = null)
    {
        var e = new Entity(Project) { ["pb_name"] = "P-" + Guid.NewGuid().ToString("N") };
        if (owner is not null) e["ownerid"] = owner;
        return _svc.Create(e);
    }

    private static void ShouldFail(Action act, string message) =>
        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400).WithMessage(message);

    [Fact]
    public async Task Create_with_team_owner_sets_owningteam_and_team_business_unit()
    {
        var bu = NewBusinessUnit();
        var team = NewTeam(bu);

        var id = NewProject(new EntityReference("team", team));

        var e = _svc.Retrieve(Project, id, new ColumnSet("ownerid", "owninguser", "owningteam", "owningbusinessunit"));
        e.GetAttributeValue<EntityReference>("ownerid").Should().Match<EntityReference>(r => r.LogicalName == "team" && r.Id == team);
        e.GetAttributeValue<EntityReference>("owningteam").Id.Should().Be(team);
        e.Contains("owninguser").Should().BeFalse();
        e.GetAttributeValue<EntityReference>("owningbusinessunit").Id.Should().Be(bu);
        (await ScalarAsync($"SELECT owneridtype FROM data.pb_project WHERE pb_projectid = '{id}'")).Should().Be(9);
    }

    [Fact]
    public async Task Assign_from_team_to_user_switches_owning_columns()
    {
        var bu = NewBusinessUnit();
        var team = NewTeam(bu);
        // A new owner must hold prvRead on the entity (rule of Assign).
        await SecurityTestHelper.GrantAsync(fx, team, team: true, ("prvReadpb_Project", DepthMask.Basic));
        var id = NewProject(new EntityReference("team", team));

        _svc.Execute(new AssignRequest { Target = new EntityReference(Project, id), Assignee = new EntityReference("systemuser", _adminId) });

        var e = _svc.Retrieve(Project, id, new ColumnSet("ownerid", "owninguser", "owningteam", "owningbusinessunit"));
        e.GetAttributeValue<EntityReference>("ownerid").Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == _adminId);
        e.GetAttributeValue<EntityReference>("owninguser").Id.Should().Be(_adminId);
        e.Contains("owningteam").Should().BeFalse();
        e.GetAttributeValue<EntityReference>("owningbusinessunit").Id.Should().Be(_rootBuId);
        (await ScalarAsync($"SELECT owneridtype FROM data.pb_project WHERE pb_projectid = '{id}'")).Should().Be(8);

        _svc.Execute(new AssignRequest { Target = new EntityReference(Project, id), Assignee = new EntityReference("team", team) });
        _svc.Retrieve(Project, id, new ColumnSet("owningbusinessunit")).GetAttributeValue<EntityReference>("owningbusinessunit").Id.Should().Be(bu);
    }

    [Fact]
    public void Access_team_cannot_own_records()
    {
        var team = NewTeam(_rootBuId, teamType: 1);

        // SystemTableRules refuses it before the core operation's own check ("Team '{name}' is not an owner team ...").
        ShouldFail(() => NewProject(new EntityReference("team", team)), $"An access team cannot own records. The team {team} is an access team*");
    }

    [Fact]
    public void Owner_of_another_type_is_rejected()
    {
        ShouldFail(() => NewProject(new EntityReference("businessunit", _rootBuId)), "*cannot reference entity*");
    }

    [Fact]
    public async Task Systemuser_owningbusinessunit_follows_businessunitid()
    {
        var bu1 = NewBusinessUnit();
        var bu2 = NewBusinessUnit();
        var user = _svc.Create(new Entity("systemuser")
        {
            ["fullname"] = "User " + Guid.NewGuid().ToString("N"),
            ["businessunitid"] = new EntityReference("businessunit", bu1),
        });
        (await ScalarAsync($"SELECT owningbusinessunit FROM data.systemuser WHERE systemuserid = '{user}'")).Should().Be(bu1);

        _svc.Update(new Entity("systemuser", user) { ["businessunitid"] = new EntityReference("businessunit", bu2) });

        (await ScalarAsync($"SELECT owningbusinessunit FROM data.systemuser WHERE systemuserid = '{user}'")).Should().Be(bu2);
        (await ScalarAsync($"SELECT businessunitid FROM data.systemuser WHERE systemuserid = '{user}'")).Should().Be(bu2);

        var team = NewTeam(bu1);
        _svc.Update(new Entity("team", team) { ["businessunitid"] = new EntityReference("businessunit", bu2) });
        (await ScalarAsync($"SELECT owningbusinessunit FROM data.team WHERE teamid = '{team}'")).Should().Be(bu2);
    }

    [Fact]
    public async Task Systemuser_businessunitid_defaults_to_caller_business_unit()
    {
        var user = _svc.Create(new Entity("systemuser") { ["fullname"] = "Default " + Guid.NewGuid().ToString("N") });

        (await ScalarAsync($"SELECT businessunitid FROM data.systemuser WHERE systemuserid = '{user}'")).Should().Be(_rootBuId);
        (await ScalarAsync($"SELECT owningbusinessunit FROM data.systemuser WHERE systemuserid = '{user}'")).Should().Be(_rootBuId);
    }

    [Fact]
    public void Business_unit_requires_parent_and_rejects_loops()
    {
        ShouldFail(() => _svc.Create(new Entity("businessunit") { ["name"] = "Orphan" }), "A business unit must have a parent business unit.");

        var a = NewBusinessUnit();
        var b = NewBusinessUnit(a);
        var c = NewBusinessUnit(b);

        ShouldFail(() => _svc.Update(new Entity("businessunit", a) { ["parentbusinessunitid"] = new EntityReference("businessunit", c) }),
            "Business unit hierarchy cannot contain a loop.");
        ShouldFail(() => _svc.Update(new Entity("businessunit", a) { ["parentbusinessunitid"] = new EntityReference("businessunit", a) }),
            "Business unit hierarchy cannot contain a loop.");
        ShouldFail(() => _svc.Update(new Entity("businessunit", _rootBuId) { ["parentbusinessunitid"] = new EntityReference("businessunit", a) }),
            "The root business unit cannot have a parent business unit.");
        ShouldFail(() => _svc.Update(new Entity("businessunit", c) { ["parentbusinessunitid"] = null }),
            "A business unit must have a parent business unit.");

        _svc.Update(new Entity("businessunit", c) { ["parentbusinessunitid"] = new EntityReference("businessunit", _rootBuId) });
        _svc.Retrieve("businessunit", c, new ColumnSet("parentbusinessunitid")).GetAttributeValue<EntityReference>("parentbusinessunitid").Id
            .Should().Be(_rootBuId);
    }

    [Fact]
    public async Task Delete_rules()
    {
        // Users are never deleted.
        ShouldFail(() => _svc.Delete("systemuser", _adminId), "Users cannot be deleted; set isdisabled instead.");

        // A business unit with a child, a team or a user is kept.
        var parent = NewBusinessUnit();
        var child = NewBusinessUnit(parent);
        ShouldFail(() => _svc.Delete("businessunit", parent),
            $"The business unit {parent} has users, teams or child business units and cannot be deleted.");
        _svc.Delete("businessunit", child);
        var team = NewTeam(parent);
        ShouldFail(() => _svc.Delete("businessunit", parent), "*has users, teams or child business units*");

        // A team that owns records is kept; once the record moves, the team and its rows go away.
        var project = NewProject(new EntityReference("team", team));
        ShouldFail(() => _svc.Delete("team", team), $"The team {team} owns records and cannot be deleted.");
        _svc.Execute(new AssignRequest { Target = new EntityReference(Project, project), Assignee = new EntityReference("systemuser", _adminId) });
        var roleId = Guid.NewGuid();
        _svc.Create(new Entity("role", roleId) { ["name"] = "R-" + roleId.ToString("N"), ["businessunitid"] = new EntityReference("businessunit", _rootBuId) });
        await ExecuteAsync($"INSERT INTO data.teammembership (teammembershipid, teamid, systemuserid) VALUES (NEWID(), '{team}', '{_adminId}')");
        await ExecuteAsync($"INSERT INTO data.teamroles (teamrolesid, teamid, roleid) VALUES (NEWID(), '{team}', '{roleId}')");
        await ExecuteAsync(
            $"INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask) " +
            $"VALUES (NEWID(), '{team}', 9, '{project}', 10000, 1)");
        _svc.Delete("team", team);
        foreach (var table in new[] { "teammembership", "teamroles" })
            (await ScalarAsync($"SELECT COUNT(*) FROM data.{table} WHERE teamid = '{team}'")).Should().Be(0, table);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.principalobjectaccess WHERE principalid = '{team}'")).Should().Be(0);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.teamroles WHERE roleid = '{roleId}'")).Should().Be(0);
        _svc.Delete("businessunit", parent);

        // A role takes its assignments and privileges with it.
        await ExecuteAsync($"INSERT INTO data.systemuserroles (systemuserrolesid, systemuserid, roleid) VALUES (NEWID(), '{_adminId}', '{roleId}')");
        await ExecuteAsync(
            $"INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask) " +
            $"VALUES (NEWID(), '{roleId}', '{PrivilegeNames.IdOf("prvReadOrganization")}', 8)");
        _svc.Delete("role", roleId);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.systemuserroles WHERE roleid = '{roleId}'")).Should().Be(0);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{roleId}'")).Should().Be(0);
    }

    [Fact]
    public async Task System_columns_are_maintained()
    {
        var team = NewTeam(_rootBuId);
        var id = NewProject(new EntityReference("team", team));
        var columns = new ColumnSet("createdby", "modifiedby", "createdon", "modifiedon");
        var created = _svc.Retrieve(Project, id, columns);
        var version1 = Convert.ToInt64(await ScalarAsync($"SELECT CAST(versionnumber AS bigint) FROM data.pb_project WHERE pb_projectid = '{id}'"));
        created.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(_adminId);
        created.GetAttributeValue<EntityReference>("modifiedby").Id.Should().Be(_adminId);

        await Task.Delay(20);
        _svc.Update(new Entity(Project, id) { ["pb_name"] = "Renamed" });

        var updated = _svc.Retrieve(Project, id, columns);
        var version2 = Convert.ToInt64(await ScalarAsync($"SELECT CAST(versionnumber AS bigint) FROM data.pb_project WHERE pb_projectid = '{id}'"));
        updated.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(_adminId);
        updated.GetAttributeValue<EntityReference>("modifiedby").Id.Should().Be(_adminId);
        updated.GetAttributeValue<DateTime>("createdon").Should().Be(created.GetAttributeValue<DateTime>("createdon"));
        updated.GetAttributeValue<DateTime>("createdon").Should().BeOnOrBefore(updated.GetAttributeValue<DateTime>("modifiedon"));
        updated.GetAttributeValue<DateTime>("modifiedon").Should().BeAfter(created.GetAttributeValue<DateTime>("modifiedon"));
        version2.Should().BeGreaterThan(version1);
    }
}
