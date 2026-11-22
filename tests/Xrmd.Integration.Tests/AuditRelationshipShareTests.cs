using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Data;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The audit rows of Associate / Disassociate, GrantAccess / ModifyAccess / RevokeAccess and the role privilege messages (plan 13,
/// task 4; ruling A1), written in the operation's transaction; and the limits that stay unaudited (A2: team membership).
/// </summary>
/// <remarks>Auditing is switched on for the organization at the start of each test and off at its end.</remarks>
[Collection("xrmd")]
public sealed class AuditRelationshipShareTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account";
    private const string SystemUserRoles = "systemuserroles_association";
    private const string TeamRoles = "teamroles_association";
    private const string TeamMembership = "teammembership_association";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private sealed record Row(int Action, int Operation, string ObjectTypeCode, string ChangeData)
    {
        public AuditChange Change => Audit.ChangeData.Deserialize(ChangeData);
    }

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectTagAsync(Store, fx.ConnectionString);
        await SetEntityAuditAsync(TestEntities.Project, true);
        await SetEntityAuditAsync(TestEntities.Tag, true);
        await SetOrganizationAuditAsync(true);
    }

    public async Task DisposeAsync()
    {
        await SetOrganizationAuditAsync(false);
        await SetEntityAuditAsync(TestEntities.Project, false);
        await SetEntityAuditAsync(TestEntities.Tag, false);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task SetOrganizationAuditAsync(bool enabled)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT TOP 1 organizationid FROM data.organization");
        var id = (Guid)(await cmd.ExecuteScalarAsync())!;
        await Admin.UpdateAsync(new Entity("organization", id) { ["isauditenabled"] = enabled });
    }

    private Task SetEntityAuditAsync(string entity, bool enabled) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = entity, IsAuditEnabled = new BooleanManagedProperty(enabled) } });

    private async Task<List<Row>> RowsAsync(Guid objectId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT action, operation, objecttypecode, changedata FROM data.audit WHERE objectid = @id ORDER BY createdon, auditid");
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = objectId;
        var rows = new List<Row>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add(new Row(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3)));
        return rows;
    }

    private static List<Row> OfAction(IEnumerable<Row> rows, int action) => rows.Where(r => r.Action == action).ToList();

    private static EntityReference User(Guid id) => new("systemuser", id);
    private static EntityReference Team(Guid id) => new("team", id);
    private static EntityReference RoleRef(Guid id) => new("role", id);

    private static AssociateRequest Associate(string relationship, EntityReference target, params EntityReference[] related) =>
        new() { Target = target, Relationship = new Relationship(relationship), RelatedEntities = [.. related] };

    private static DisassociateRequest Disassociate(string relationship, EntityReference target, params EntityReference[] related) =>
        new() { Target = target, Relationship = new Relationship(relationship), RelatedEntities = [.. related] };

    /// <summary>A share needs a principal that can read the record's table.</summary>
    private async Task<Guid> ShareUserAsync()
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await GrantAsync(fx, user, team: false, ("prvReadAccount", DepthMask.Basic));
        return user;
    }

    private async Task<Guid> ShareTeamAsync()
    {
        var team = await CreateTeamAsync(fx, await RootBusinessUnitAsync(fx));
        await GrantAsync(fx, team, team: true, ("prvReadAccount", DepthMask.Basic));
        return team;
    }

    private async Task<(Guid Id, Guid Owner)> NewAccountAsync()
    {
        var owner = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        var id = await Admin.CreateAsync(new Entity(Account) { ["name"] = "Share " + _run, ["ownerid"] = User(owner) });
        return (id, owner);
    }

    private Task ShareAsync(Guid account, EntityReference principal, AccessRights rights, bool modify = false) =>
        modify
            ? Admin.ExecuteAsync(new ModifyAccessRequest { Target = new EntityReference(Account, account), PrincipalAccess = new PrincipalAccess { Principal = principal, AccessMask = rights } })
            : Admin.ExecuteAsync(new GrantAccessRequest { Target = new EntityReference(Account, account), PrincipalAccess = new PrincipalAccess { Principal = principal, AccessMask = rights } });

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Many_to_many_associate_and_disassociate_are_audited()
    {
        var project = await Admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "m2m " + _run });
        var tagA = await Admin.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = "a " + _run });
        var tagB = await Admin.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = "b " + _run });
        var related = new[] { new EntityReference(TestEntities.Tag, tagA), new EntityReference(TestEntities.Tag, tagB) };

        await Admin.ExecuteAsync(Associate(TestEntities.ProjectTag, new EntityReference(TestEntities.Project, project), related));
        await Admin.ExecuteAsync(Disassociate(TestEntities.ProjectTag, new EntityReference(TestEntities.Project, project), related));

        var rows = (await RowsAsync(project)).Where(r => r.Action is AuditActions.AssociateEntities or AuditActions.DisassociateEntities).ToList();
        rows.Should().HaveCount(2);
        foreach (var (row, action) in rows.Zip([AuditActions.AssociateEntities, AuditActions.DisassociateEntities]))
        {
            row.Action.Should().Be(action);
            row.Operation.Should().Be(AuditOperations.Update);
            row.ObjectTypeCode.Should().Be(TestEntities.Project);
            var change = row.Change.Should().BeOfType<RelationshipChange>().Subject;
            change.Relationship.Should().Be(TestEntities.ProjectTag);
            change.Targets.Select(t => (t.LogicalName, t.Id)).Should().BeEquivalentTo(related.Select(t => (t.LogicalName, t.Id)));
        }
        // The row is on the Target only.
        (await RowsAsync(tagA)).Where(r => r.Action is AuditActions.AssociateEntities or AuditActions.DisassociateEntities).Should().BeEmpty();
    }

    [Fact]
    public async Task Role_assignment_to_user_and_team_is_audited()
    {
        var root = await RootBusinessUnitAsync(fx);
        var user = await CreateUserAsync(fx, root);
        var team = await CreateTeamAsync(fx, root);
        var role = await CreateRoleAsync(fx, "audit-role-" + _run);

        await Admin.ExecuteAsync(Associate(SystemUserRoles, User(user), RoleRef(role)));
        await Admin.ExecuteAsync(Associate(TeamRoles, Team(team), RoleRef(role)));
        await Admin.ExecuteAsync(Disassociate(SystemUserRoles, User(user), RoleRef(role)));
        await Admin.ExecuteAsync(Disassociate(TeamRoles, Team(team), RoleRef(role)));

        var userRows = await RowsAsync(user);
        userRows.Select(r => r.Action).Should().Equal(AuditActions.AssignRoleToUser, AuditActions.RemoveRoleFromUser);
        userRows.Should().OnlyContain(r => r.Operation == AuditOperations.Update && r.ObjectTypeCode == "systemuser");
        var change = userRows[0].Change.Should().BeOfType<RelationshipChange>().Subject;
        change.Relationship.Should().Be(SystemUserRoles);
        change.Targets.Should().ContainSingle().Which.Id.Should().Be(role);

        var teamRows = await RowsAsync(team);
        teamRows.Select(r => r.Action).Should().Equal(AuditActions.AssignRoleToTeam, AuditActions.RemoveRoleFromTeam);
        teamRows.Should().OnlyContain(r => r.ObjectTypeCode == "team");
        ((RelationshipChange)teamRows[0].Change).Relationship.Should().Be(TeamRoles);

        // The row is on whichever side the request names: here the role.
        await Admin.ExecuteAsync(Associate(SystemUserRoles, RoleRef(role), User(user)));
        var roleRows = OfAction(await RowsAsync(role), AuditActions.AssignRoleToUser);
        roleRows.Should().ContainSingle().Which.ObjectTypeCode.Should().Be("role");

        await Admin.ExecuteAsync(Associate(TeamRoles, RoleRef(role), Team(team)));
        await Admin.ExecuteAsync(Disassociate(TeamRoles, RoleRef(role), Team(team)));
        var all = await RowsAsync(role);
        all.Select(r => r.Action).Should().Contain([AuditActions.AssignRoleToTeam, AuditActions.RemoveRoleFromTeam]);
        ((RelationshipChange)OfAction(all, AuditActions.AssignRoleToTeam).Single().Change).Targets.Should().ContainSingle().Which.Id.Should().Be(team);
    }

    [Fact]
    public async Task Sharing_is_audited_with_old_and_new_rights()
    {
        var (account, _) = await NewAccountAsync();
        var user = await ShareUserAsync();
        var team = await ShareTeamAsync();

        await ShareAsync(account, User(user), AccessRights.ReadAccess);
        await ShareAsync(account, User(user), AccessRights.ReadAccess | AccessRights.WriteAccess, modify: true);
        await Admin.ExecuteAsync(new RevokeAccessRequest { Target = new EntityReference(Account, account), Revokee = User(user) });
        await ShareAsync(account, Team(team), AccessRights.ReadAccess);
        await Admin.ExecuteAsync(new RevokeAccessRequest { Target = new EntityReference(Account, account), Revokee = Team(team) });

        // The action follows the message: ModifyAccess without a share is 48 (Old None), GrantAccess on a share is 14.
        var user2 = await ShareUserAsync();
        await ShareAsync(account, User(user2), AccessRights.ReadAccess, modify: true);
        await ShareAsync(account, User(user2), AccessRights.WriteAccess);
        var late = (await RowsAsync(account)).Where(r => r.Change is ShareChange c && c.Principal.Id == user2).ToList();
        late.Select(r => r.Action).Should().Equal(AuditActions.ModifyShare, AuditActions.Share);
        var lateChanges = late.Select(r => (ShareChange)r.Change).ToList();
        (lateChanges[0].Old, lateChanges[0].New).Should().Be((AccessRights.None, AccessRights.ReadAccess));
        (lateChanges[1].Old, lateChanges[1].New).Should().Be((AccessRights.ReadAccess, AccessRights.ReadAccess | AccessRights.WriteAccess));

        var rows = (await RowsAsync(account)).Where(r => r.Change is ShareChange c && c.Principal.Id != user2).ToList();
        rows.Select(r => r.Action).Should().Equal(AuditActions.Share, AuditActions.ModifyShare, AuditActions.Unshare, AuditActions.Share, AuditActions.Unshare);
        rows.Should().OnlyContain(r => r.Operation == AuditOperations.Update && r.ObjectTypeCode == Account);
        var changes = rows.Select(r => (ShareChange)r.Change).ToList();
        changes[0].Principal.Id.Should().Be(user);
        (changes[0].Old, changes[0].New).Should().Be((AccessRights.None, AccessRights.ReadAccess));
        (changes[1].Old, changes[1].New).Should().Be((AccessRights.ReadAccess, AccessRights.ReadAccess | AccessRights.WriteAccess));
        (changes[2].Old, changes[2].New).Should().Be((AccessRights.ReadAccess | AccessRights.WriteAccess, AccessRights.None));
        changes[2].Principal.LogicalName.Should().Be("systemuser");
        changes[3].Principal.Should().Match<EntityReference>(p => p.LogicalName == "team" && p.Id == team);
        (changes[4].Old, changes[4].New).Should().Be((AccessRights.ReadAccess, AccessRights.None));
    }

    [Fact]
    public async Task Role_privilege_changes_are_audited()
    {
        var read = "prvReadpb_Project";
        var write = "prvWritepb_Project";
        var create = "prvCreatepb_Project";
        var role = await CreateRoleAsync(fx, "audit-priv-" + _run, (read, DepthMask.Basic));

        await Admin.ExecuteAsync(new AddPrivilegesRoleRequest
        {
            RoleId = role,
            Privileges = [new RolePrivilege { PrivilegeName = write, Depth = PrivilegeDepth.Deep }],
        });
        await Admin.ExecuteAsync(new RemovePrivilegeRoleRequest { RoleId = role, PrivilegeId = PrivilegeNames.IdOf(read) });
        await Admin.ExecuteAsync(new ReplacePrivilegesRoleRequest
        {
            RoleId = role,
            Privileges = [new RolePrivilege { PrivilegeName = create, Depth = PrivilegeDepth.Global }],
        });

        var rows = await RowsAsync(role);
        rows.Select(r => r.Action).Should().Equal(AuditActions.AddPrivilegesToRole, AuditActions.RemovePrivilegesFromRole, AuditActions.ReplacePrivilegesInRole);
        rows.Should().OnlyContain(r => r.Operation == AuditOperations.Update && r.ObjectTypeCode == "role");
        var changes = rows.Select(r => (RolePrivilegesChange)r.Change).ToList();
        static (Guid, int)[] Of(IEnumerable<RolePrivilege> l) => l.Select(p => (p.PrivilegeId, (int)p.Depth)).Order().ToArray();
        Of(changes[0].Old).Should().Equal((PrivilegeNames.IdOf(read), 0));
        Of(changes[0].New).Should().BeEquivalentTo(new[] { (PrivilegeNames.IdOf(read), 0), (PrivilegeNames.IdOf(write), 2) });
        Of(changes[1].Old).Should().HaveCount(2);
        Of(changes[1].New).Should().Equal((PrivilegeNames.IdOf(write), 2));
        Of(changes[2].Old).Should().Equal((PrivilegeNames.IdOf(write), 2));
        Of(changes[2].New).Should().Equal((PrivilegeNames.IdOf(create), 3));
    }

    [Fact]
    public async Task Team_membership_is_not_audited()
    {
        var root = await RootBusinessUnitAsync(fx);
        var user = await CreateUserAsync(fx, root);
        var team = await CreateTeamAsync(fx, root);

        await Admin.ExecuteAsync(Associate(TeamMembership, Team(team), User(user)));
        await Admin.ExecuteAsync(Disassociate(TeamMembership, Team(team), User(user)));

        // Documented limit (A2): Dataverse's action for it is not documented, so xrmverse writes nothing.
        (await RowsAsync(team)).Should().BeEmpty();
        (await RowsAsync(user)).Should().BeEmpty();
    }

    [Fact]
    public async Task Events_follow_the_switches()
    {
        var (account, _) = await NewAccountAsync();
        var user = await ShareUserAsync();
        var role = await CreateRoleAsync(fx, "audit-sw-" + _run);
        var project = await Admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "sw " + _run });
        var tag = await Admin.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = "sw " + _run });

        async Task EventsAsync()
        {
            await ShareAsync(account, User(user), AccessRights.ReadAccess);
            await Admin.ExecuteAsync(new RevokeAccessRequest { Target = new EntityReference(Account, account), Revokee = User(user) });
            await Admin.ExecuteAsync(Associate(SystemUserRoles, User(user), RoleRef(role)));
            await Admin.ExecuteAsync(Disassociate(SystemUserRoles, User(user), RoleRef(role)));
            await Admin.ExecuteAsync(Associate(TestEntities.ProjectTag, new EntityReference(TestEntities.Project, project), new EntityReference(TestEntities.Tag, tag)));
            await Admin.ExecuteAsync(Disassociate(TestEntities.ProjectTag, new EntityReference(TestEntities.Project, project), new EntityReference(TestEntities.Tag, tag)));
            await Admin.ExecuteAsync(new AddPrivilegesRoleRequest
            {
                RoleId = role, Privileges = [new RolePrivilege { PrivilegeName = "prvReadpb_Project", Depth = PrivilegeDepth.Basic }],
            });
        }

        async Task<int> CountAsync() => (await RowsAsync(account)).Count(r => r.Change is ShareChange)
            + (await RowsAsync(user)).Count(r => r.Change is RelationshipChange)
            + (await RowsAsync(project)).Count(r => r.Change is RelationshipChange)
            + (await RowsAsync(role)).Count;

        // Tables off (account, systemuser, role, project): nothing, even though the organization is on.
        foreach (var t in new[] { Account, "systemuser", "role", TestEntities.Project }) await SetEntityAuditAsync(t, false);
        try
        {
            await EventsAsync();
            (await CountAsync()).Should().Be(0);
        }
        finally
        {
            foreach (var t in new[] { Account, "systemuser", "role", TestEntities.Project }) await SetEntityAuditAsync(t, true);
        }

        // Organization off: nothing.
        await SetOrganizationAuditAsync(false);
        await EventsAsync();
        (await CountAsync()).Should().Be(0);

        // On again: rows appear.
        await SetOrganizationAuditAsync(true);
        await EventsAsync();
        (await RowsAsync(account)).Where(r => r.Change is ShareChange).Select(r => r.Action).Should().Equal(AuditActions.Share, AuditActions.Unshare);
        (await RowsAsync(user)).Where(r => r.Change is RelationshipChange).Select(r => r.Action).Should().Equal(AuditActions.AssignRoleToUser, AuditActions.RemoveRoleFromUser);
        (await RowsAsync(project)).Where(r => r.Change is RelationshipChange).Select(r => r.Action).Should().Equal(AuditActions.AssociateEntities, AuditActions.DisassociateEntities);
        // The privilege of EventsAsync was added in the earlier phases (no change, no row): a new one now is recorded.
        await Admin.ExecuteAsync(new AddPrivilegesRoleRequest
        {
            RoleId = role, Privileges = [new RolePrivilege { PrivilegeName = "prvWritepb_Project", Depth = PrivilegeDepth.Basic }],
        });
        (await RowsAsync(role)).Select(r => r.Action).Should().Equal(AuditActions.AddPrivilegesToRole);
    }

    [Fact]
    public async Task Share_audit_row_rolls_back_with_the_operation()
    {
        var (account, _) = await NewAccountAsync();
        var user = await ShareUserAsync();
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "GrantAccess", Account, 40, config: "invalid:refused after the share");

            (await TestHelpers.FaultAsync(() => ShareAsync(account, User(user), AccessRights.ReadAccess))).Message.Should().Contain("refused");
            (await RowsAsync(account)).Where(r => r.Change is ShareChange).Should().BeEmpty("the audit row went with the rolled-back share");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }
}
