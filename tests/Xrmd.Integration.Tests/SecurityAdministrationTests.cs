using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Role and team administration: the security relationships (<c>systemuserroles_association</c>, <c>teamroles_association</c>,
/// <c>teammembership_association</c>) through Associate/Disassociate, AddPrivilegesRole/ReplacePrivilegesRole/RemovePrivilegeRole,
/// the escalation guard and the invalidation of the security cache. Business unit R (root) → A; users uA and uB in A.
/// </summary>
[Collection("xrmd")]
public sealed class SecurityAdministrationTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private const string SystemUserRoles = "systemuserroles_association";
    private const string TeamRoles = "teamroles_association";
    private const string TeamMembership = "teammembership_association";

    private readonly string _tag = "SA" + Guid.NewGuid().ToString("N");
    private IMetadataStore _store = null!;
    private OrganizationService _admin = null!;
    private Guid _a;
    private (Guid Id, Guid Oid) _uA, _uB;
    private Guid _pA;

    public async Task InitializeAsync()
    {
        _store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureProjectAsync(_store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _a = await CreateBusinessUnitAsync(fx, await RootBusinessUnitAsync(fx), "A-" + _tag);
        _uA = await UserAsync(_a);
        _uB = await UserAsync(_a);
        // Every user holds a role (a user without any role is refused everywhere); none of these covers pb_project.
        await GrantAsync(_uA.Id, (Privilege(AccessRights.ReadAccess, "team"), DepthMask.Global));
        await GrantAsync(_uB.Id, (Privilege(AccessRights.ReadAccess, "team"), DepthMask.Global));
        _pA = await _admin.CreateAsync(new Entity(Project)
        {
            ["pb_name"] = $"{_tag}-A", ["ownerid"] = new EntityReference("systemuser", _uA.Id),
        });
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit)
    {
        var oid = Guid.NewGuid();
        return (await CreateUserAsync(fx, businessUnit, oid), oid);
    }

    private Task GrantAsync(Guid userId, params (string Privilege, DepthMask Depth)[] privileges) =>
        SecurityTestHelper.GrantAsync(fx, userId, team: false, privileges);

    private Task<Guid> RoleAsync(params (string Privilege, DepthMask Depth)[] privileges) =>
        CreateRoleAsync(fx, "role-" + Guid.NewGuid().ToString("N")[..8], privileges);

    private string Privilege(AccessRights right, string entity) => PrivilegeNames.For(right, _store.GetEntity(entity)!);

    private OrganizationService As((Guid Id, Guid Oid) user) => ServiceFor(fx, user.Oid);

    private static EntityReference User(Guid id) => new("systemuser", id);
    private static EntityReference Team(Guid id) => new("team", id);
    private static EntityReference Role(Guid id) => new("role", id);

    private static AssociateRequest Associate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private static DisassociateRequest Disassociate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private Task<Guid> CreateProjectAsync(OrganizationService service) =>
        service.CreateAsync(new Entity(Project) { ["pb_name"] = $"{_tag}-new" });

    /// <summary>The <c>roleprivileges</c> rows of the role: privilege name → depth mask.</summary>
    private async Task<Dictionary<string, int>> RolePrivilegesAsync(Guid roleId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, """
            SELECT p.name, rp.privilegedepthmask FROM data.roleprivileges AS rp JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
            WHERE rp.roleid = @role
            """);
        cmd.Parameters.AddWithValue("@role", roleId);
        var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add(rd.GetString(0), rd.GetInt32(1));
        return rows;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static Task FaultAsync(Func<Task> act, int code, int status, string? message = null) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == code && e.HttpStatus == status && (message == null || e.Message == message));

    private static Task PrivilegeDeniedAsync(Func<Task> act, string privilege) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.HttpStatus == 403 && e.Message.Contains($" {privilege} "));

    private static Task AccessDeniedAsync(Func<Task> act, AccessRights right, Guid userId, Guid recordId) =>
        FaultAsync(act, ErrorCodes.AccessDenied, 403,
            $"Principal user with id {userId} does not have {right} right(s) for record with id {recordId} of entity {Project}.");

    private static Task EscalationAsync(Func<Task> act, Guid userId, string privilege, string depth) =>
        FaultAsync(act, ErrorCodes.RoleUpdateValidationFailure, 403,
            $"User {userId} cannot grant {privilege} at depth {depth} without holding it at that depth.");

    // ---- security relationships -------------------------------------------------------------------------------------

    [Fact]
    public async Task Associating_a_role_grants_its_privileges_immediately()
    {
        var creator = await RoleAsync(("prvCreatepb_Project", DepthMask.Basic));
        // The denied Create caches uA's profile: only the invalidation after the Associate lets the next Create through.
        await PrivilegeDeniedAsync(() => CreateProjectAsync(As(_uA)), "prvCreatepb_Project");

        await _admin.ExecuteAsync(Associate(User(_uA.Id), SystemUserRoles, Role(creator)));

        (await CountAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u AND roleid = @r",
            ("@u", _uA.Id), ("@r", creator))).Should().Be(1);
        await CreateProjectAsync(As(_uA));

        await _admin.ExecuteAsync(Disassociate(User(_uA.Id), SystemUserRoles, Role(creator)));

        (await CountAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u AND roleid = @r",
            ("@u", _uA.Id), ("@r", creator))).Should().Be(0);
        await PrivilegeDeniedAsync(() => CreateProjectAsync(As(_uA)), "prvCreatepb_Project");

        // From the role's side too.
        await _admin.ExecuteAsync(Associate(Role(creator), SystemUserRoles, User(_uA.Id)));
        await CreateProjectAsync(As(_uA));
        await _admin.ExecuteAsync(Disassociate(Role(creator), SystemUserRoles, User(_uA.Id)));
        await PrivilegeDeniedAsync(() => CreateProjectAsync(As(_uA)), "prvCreatepb_Project");
    }

    [Fact]
    public async Task Team_roles_and_membership()
    {
        var team = await CreateTeamAsync(fx, _a);
        var reader = await RoleAsync(("prvReadpb_Project", DepthMask.Global));
        await PrivilegeDeniedAsync(() => As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")), "prvReadpb_Project");

        await _admin.ExecuteAsync(Associate(Team(team), TeamRoles, Role(reader)));
        await _admin.ExecuteAsync(Associate(Team(team), TeamMembership, User(_uB.Id)));

        (await As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name"))).GetAttributeValue<string>("pb_name").Should().Be($"{_tag}-A");

        await _admin.ExecuteAsync(Disassociate(Team(team), TeamMembership, User(_uB.Id)));

        await PrivilegeDeniedAsync(() => As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")), "prvReadpb_Project");

        // Membership from the user's side, then the team's role taken away.
        await _admin.ExecuteAsync(Associate(User(_uB.Id), TeamMembership, Team(team)));
        await As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name"));
        await _admin.ExecuteAsync(Disassociate(Role(reader), TeamRoles, Team(team)));
        await PrivilegeDeniedAsync(() => As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")), "prvReadpb_Project");
    }

    /// <summary>Review Focus 4: a share with a team is lost with the membership (cached teams included).</summary>
    [Fact]
    public async Task Leaving_team_loses_shared_access()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic), ("prvWritepb_Project", DepthMask.Basic));
        var team = await CreateTeamAsync(fx, _a);
        await AssignRoleAsync(fx, team, await RoleAsync(("prvReadpb_Project", DepthMask.Basic)), team: true); // a share needs prvRead
        await _admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference(Project, _pA),
            PrincipalAccess = new PrincipalAccess { Principal = Team(team), AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess },
        });
        await AccessDeniedAsync(() => As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")), AccessRights.ReadAccess, _uB.Id, _pA);

        await _admin.ExecuteAsync(Associate(Team(team), TeamMembership, User(_uB.Id)));

        (await Cache(fx).GetProfileAsync(_uB.Id, default)).TeamIds.Should().Contain(team);
        await As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name"));
        await As(_uB).UpdateAsync(new Entity(Project, _pA) { ["pb_count"] = 7 });

        await _admin.ExecuteAsync(Disassociate(Team(team), TeamMembership, User(_uB.Id)));

        // The profile cached before the Disassociate was dropped by the commit.
        (await Cache(fx).GetProfileAsync(_uB.Id, default)).TeamIds.Should().NotContain(team);
        await AccessDeniedAsync(() => As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")), AccessRights.ReadAccess, _uB.Id, _pA);
        await AccessDeniedAsync(() => As(_uB).UpdateAsync(new Entity(Project, _pA) { ["pb_count"] = 8 }), AccessRights.WriteAccess, _uB.Id, _pA);
    }

    [Fact]
    public async Task Access_team_cannot_have_roles()
    {
        var team = await CreateTeamAsync(fx, _a, teamType: 1);
        var role = await RoleAsync(("prvReadpb_Project", DepthMask.Global));

        await FaultAsync(() => _admin.ExecuteAsync(Associate(Team(team), TeamRoles, Role(role))),
            ErrorCodes.InvalidArgument, 400, "Roles can be assigned only to owner teams.");
        await FaultAsync(() => _admin.ExecuteAsync(Associate(Role(role), TeamRoles, Team(team))),
            ErrorCodes.InvalidArgument, 400, "Roles can be assigned only to owner teams.");

        (await CountAsync("SELECT COUNT(*) FROM data.teamroles WHERE teamid = @t", ("@t", team))).Should().Be(0);
        // An access team still takes members.
        await _admin.ExecuteAsync(Associate(Team(team), TeamMembership, User(_uB.Id)));
    }

    [Fact]
    public async Task Duplicate_association_is_412()
    {
        var role = await RoleAsync(("prvReadpb_Project", DepthMask.Basic));
        var team = await CreateTeamAsync(fx, _a);
        await _admin.ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(role)));
        await _admin.ExecuteAsync(Associate(Team(team), TeamRoles, Role(role)));
        await _admin.ExecuteAsync(Associate(Team(team), TeamMembership, User(_uB.Id)));

        await FaultAsync(() => _admin.ExecuteAsync(Associate(Role(role), SystemUserRoles, User(_uB.Id))), ErrorCodes.DuplicateRecord, 412);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(Team(team), TeamRoles, Role(role))), ErrorCodes.DuplicateRecord, 412);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(User(_uB.Id), TeamMembership, Team(team))), ErrorCodes.DuplicateRecord, 412);
        unchecked((uint)ErrorCodes.DuplicateRecord).Should().Be(0x80040237);

        (await CountAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u AND roleid = @r",
            ("@u", _uB.Id), ("@r", role))).Should().Be(1);
    }

    [Fact]
    public async Task Security_association_of_wrong_records_is_rejected()
    {
        var role = await RoleAsync();
        await FaultAsync(() => _admin.ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Team(Guid.NewGuid()))),
            ErrorCodes.InvalidArgument, 400);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(new EntityReference(Project, _pA), TeamMembership, User(_uB.Id))),
            ErrorCodes.InvalidArgument, 400);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(Guid.NewGuid()))),
            ErrorCodes.ObjectDoesNotExist, 404);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(Role(role), SystemUserRoles, User(Guid.NewGuid()))),
            ErrorCodes.ObjectDoesNotExist, 404);
    }

    // ---- role privileges --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddPrivilegesRole_sets_depth_Replace_replaces_Remove_removes()
    {
        var role = await RoleAsync(("prvReadpb_Project", DepthMask.Basic));
        await AssignRoleAsync(fx, _uA.Id, role);
        await PrivilegeDeniedAsync(() => CreateProjectAsync(As(_uA)), "prvCreatepb_Project"); // caches uA's profile

        await _admin.ExecuteAsync(new AddPrivilegesRoleRequest
        {
            RoleId = role,
            Privileges =
            [
                new RolePrivilege { PrivilegeId = PrivilegeNames.IdOf("prvWritepb_Project"), Depth = PrivilegeDepth.Deep },
                new RolePrivilege { PrivilegeName = "prvReadpb_Project", Depth = PrivilegeDepth.Global },
                new RolePrivilege { PrivilegeId = PrivilegeNames.IdOf("prvCreatepb_Project"), Depth = PrivilegeDepth.Basic },
            ],
        });

        (await RolePrivilegesAsync(role)).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["prvReadpb_Project"] = 8, ["prvWritepb_Project"] = 4, ["prvCreatepb_Project"] = 1,
        });
        // No primary entity: the role-privilege rows alone invalidate the cache.
        await CreateProjectAsync(As(_uA));

        await _admin.ExecuteAsync(new ReplacePrivilegesRoleRequest
        {
            RoleId = role, Privileges = [new RolePrivilege { PrivilegeName = "prvDeletepb_Project", Depth = PrivilegeDepth.Local }],
        });

        (await RolePrivilegesAsync(role)).Should().BeEquivalentTo(new Dictionary<string, int> { ["prvDeletepb_Project"] = 2 });
        await PrivilegeDeniedAsync(() => CreateProjectAsync(As(_uA)), "prvCreatepb_Project");

        await _admin.ExecuteAsync(new RemovePrivilegeRoleRequest { RoleId = role, PrivilegeId = PrivilegeNames.IdOf("prvDeletepb_Project") });

        (await RolePrivilegesAsync(role)).Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_privilege_is_0x80090903_and_disallowed_depth_is_400()
    {
        var role = await RoleAsync(("prvReadpb_Project", DepthMask.Basic));
        var unknown = Guid.NewGuid();

        await FaultAsync(() => _admin.ExecuteAsync(new AddPrivilegesRoleRequest
            {
                RoleId = role, Privileges = [new RolePrivilege { PrivilegeId = unknown, Depth = PrivilegeDepth.Global }],
            }),
            ErrorCodes.InvalidPrivilege, 400, $"No privilege with id {unknown} exists.");
        unchecked((uint)ErrorCodes.InvalidPrivilege).Should().Be(0x80090903);
        var unknownName = PrivilegeNames.IdOf("prvReadNoSuchEntity");
        await FaultAsync(() => _admin.ExecuteAsync(new ReplacePrivilegesRoleRequest
            {
                RoleId = role, Privileges = [new RolePrivilege { PrivilegeName = "prvReadNoSuchEntity", Depth = PrivilegeDepth.Global }],
            }),
            ErrorCodes.InvalidPrivilege, 400, $"No privilege with id {unknownName} exists.");
        await FaultAsync(() => _admin.ExecuteAsync(new RemovePrivilegeRoleRequest { RoleId = role, PrivilegeId = unknown }),
            ErrorCodes.InvalidPrivilege, 400);

        // pluginassembly is organization-owned: Global only (names are case-insensitive).
        const string pluginAssemblyRead = "prvReadPluginAssembly";
        await FaultAsync(() => _admin.ExecuteAsync(new AddPrivilegesRoleRequest
            {
                RoleId = role, Privileges = [new RolePrivilege { PrivilegeName = pluginAssemblyRead, Depth = PrivilegeDepth.Basic }],
            }),
            ErrorCodes.InvalidArgument, 400);

        // Nothing was written by the failed requests.
        (await RolePrivilegesAsync(role)).Should().BeEquivalentTo(new Dictionary<string, int> { ["prvReadpb_Project"] = 1 });
        await FaultAsync(() => _admin.ExecuteAsync(new AddPrivilegesRoleRequest
            {
                RoleId = Guid.NewGuid(), Privileges = [new RolePrivilege { PrivilegeName = "prvReadpb_Project", Depth = PrivilegeDepth.Global }],
            }),
            ErrorCodes.ObjectDoesNotExist, 404);
    }

    // ---- guard ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Non_admin_cannot_escalate()
    {
        var writeUser = Privilege(AccessRights.WriteAccess, "systemuser");
        var writeRole = Privilege(AccessRights.WriteAccess, "role");
        var manager = await UserAsync(_a);
        await GrantAsync(manager.Id, (writeRole, DepthMask.Global));

        // Assigning a role is a Write on the principal.
        var harmless = await RoleAsync((writeRole, DepthMask.Local));
        await PrivilegeDeniedAsync(() => As(manager).ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(harmless))), writeUser);

        await GrantAsync(manager.Id, (writeUser, DepthMask.Global));
        var deleter = await RoleAsync(("prvDeletepb_Project", DepthMask.Basic), (writeRole, DepthMask.Local));

        await EscalationAsync(() => As(manager).ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(deleter))),
            manager.Id, "prvDeletepb_Project", "Basic");
        // System Administrator: only a System Administrator grants it (reported on a privilege the caller lacks at Global).
        await FluentActions.Awaiting(() => As(manager).ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(StandardRoles.SystemAdministrator))))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.RoleUpdateValidationFailure && e.HttpStatus == 403
                        && e.Message.StartsWith($"User {manager.Id} cannot grant prv", StringComparison.Ordinal)
                        && e.Message.EndsWith(" at depth Global without holding it at that depth.", StringComparison.Ordinal));
        await EscalationAsync(() => As(manager).ExecuteAsync(new AddPrivilegesRoleRequest
            {
                RoleId = harmless, Privileges = [new RolePrivilege { PrivilegeName = "prvDeletepb_Project", Depth = PrivilegeDepth.Basic }],
            }),
            manager.Id, "prvDeletepb_Project", "Basic");
        (await CountAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u", ("@u", _uB.Id))).Should().Be(1);

        // A role whose privileges the caller holds at that depth or wider can be granted and changed.
        await As(manager).ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(harmless)));
        await As(manager).ExecuteAsync(new AddPrivilegesRoleRequest
        {
            RoleId = harmless, Privileges = [new RolePrivilege { PrivilegeName = writeUser, Depth = PrivilegeDepth.Deep }],
        });
        (await RolePrivilegesAsync(harmless)).Should().BeEquivalentTo(new Dictionary<string, int> { [writeRole] = 2, [writeUser] = 4 });

        // Changing privileges is a Write on the role.
        await PrivilegeDeniedAsync(() => As(_uA).ExecuteAsync(new RemovePrivilegeRoleRequest
            {
                RoleId = harmless, PrivilegeId = PrivilegeNames.IdOf(writeUser),
            }),
            writeRole);
    }

    [Fact]
    public async Task Role_granting_a_wider_depth_cannot_be_assigned_to_a_user()
    {
        var writeUser = Privilege(AccessRights.WriteAccess, "systemuser");
        var manager = await UserAsync(_a);
        await GrantAsync(manager.Id, (writeUser, DepthMask.Global), ("prvDeletepb_Project", DepthMask.Local));
        var wider = await RoleAsync(("prvDeletepb_Project", DepthMask.Global));

        await EscalationAsync(() => As(manager).ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(wider))),
            manager.Id, "prvDeletepb_Project", "Global");
        await EscalationAsync(() => As(manager).ExecuteAsync(Associate(Role(wider), SystemUserRoles, User(_uB.Id))),
            manager.Id, "prvDeletepb_Project", "Global");
        (await CountAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u AND roleid = @r",
            ("@u", _uB.Id), ("@r", wider))).Should().Be(0);

        // The same privilege at the caller's depth is granted.
        var same = await RoleAsync(("prvDeletepb_Project", DepthMask.Local));
        await As(manager).ExecuteAsync(Associate(User(_uB.Id), SystemUserRoles, Role(same)));
    }

    [Fact]
    public async Task Role_granting_a_wider_depth_cannot_be_assigned_to_a_team()
    {
        var writeTeam = Privilege(AccessRights.WriteAccess, "team");
        var manager = await UserAsync(_a);
        await GrantAsync(manager.Id, (writeTeam, DepthMask.Global), ("prvDeletepb_Project", DepthMask.Local));
        var team = await CreateTeamAsync(fx, _a, 0, manager.Id);
        var wider = await RoleAsync(("prvDeletepb_Project", DepthMask.Global));

        await EscalationAsync(() => As(manager).ExecuteAsync(Associate(Team(team), TeamRoles, Role(wider))),
            manager.Id, "prvDeletepb_Project", "Global");
        await EscalationAsync(() => As(manager).ExecuteAsync(Associate(Role(wider), TeamRoles, Team(team))),
            manager.Id, "prvDeletepb_Project", "Global");
        (await CountAsync("SELECT COUNT(*) FROM data.teamroles WHERE teamid = @t", ("@t", team))).Should().Be(0);
        (await Cache(fx).GetProfileAsync(manager.Id, default)).DepthOf("prvDeletepb_Project").Should().Be(DepthMask.Local);
    }

    [Fact]
    public async Task Joining_an_owner_team_requires_holding_its_roles()
    {
        var writeTeam = Privilege(AccessRights.WriteAccess, "team");
        var manager = await UserAsync(_a);
        await GrantAsync(manager.Id, (writeTeam, DepthMask.Global), ("prvDeletepb_Project", DepthMask.Local));
        var admins = await CreateTeamAsync(fx, _a, 0);
        await AssignRoleAsync(fx, admins, StandardRoles.SystemAdministrator, team: true);

        // The manager cannot join, nor add another user to, a team holding System Administrator (either side of the pair).
        await FaultAsync(() => As(manager).ExecuteAsync(Associate(Team(admins), TeamMembership, User(manager.Id))),
            ErrorCodes.RoleUpdateValidationFailure, 403);
        await FaultAsync(() => As(manager).ExecuteAsync(Associate(User(_uB.Id), TeamMembership, Team(admins))),
            ErrorCodes.RoleUpdateValidationFailure, 403);
        (await CountAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @t", ("@t", admins))).Should().Be(0);
        (await Cache(fx).GetProfileAsync(manager.Id, default)).IsSystemAdministrator.Should().BeFalse();

        // A team granting a wider depth than the manager holds: refused too.
        var wider = await CreateTeamAsync(fx, _a, 0);
        await AssignRoleAsync(fx, wider, await RoleAsync(("prvDeletepb_Project", DepthMask.Global)), team: true);
        await EscalationAsync(() => As(manager).ExecuteAsync(Associate(Team(wider), TeamMembership, User(manager.Id))),
            manager.Id, "prvDeletepb_Project", "Global");

        // A team whose roles the manager already holds can be joined; an access team carries no role.
        var same = await CreateTeamAsync(fx, _a, 0);
        await AssignRoleAsync(fx, same, await RoleAsync(("prvDeletepb_Project", DepthMask.Local)), team: true);
        await As(manager).ExecuteAsync(Associate(Team(same), TeamMembership, User(manager.Id)));
        var access = await CreateTeamAsync(fx, _a, 1);
        await As(manager).ExecuteAsync(Associate(User(_uB.Id), TeamMembership, Team(access)));
        (await CountAsync("SELECT COUNT(*) FROM data.teammembership WHERE systemuserid = @u AND teamid = @t",
            ("@u", manager.Id), ("@t", same))).Should().Be(1);
    }
    [Fact]
    public async Task Team_type_cannot_be_flipped_to_skip_the_escalation_check()
    {
        var manager = await UserAsync(_a);
        await GrantAsync(manager.Id, (Privilege(AccessRights.WriteAccess, "team"), DepthMask.Global));
        var admins = await CreateTeamAsync(fx, _a, 0);
        await AssignRoleAsync(fx, admins, StandardRoles.SystemAdministrator, team: true);

        // Owner -> access is refused, so the manager cannot join it as an access team and flip it back.
        await FaultAsync(() => As(manager).UpdateAsync(new Entity("team", admins) { ["teamtype"] = new OptionSetValue(1) }),
            ErrorCodes.InvalidArgument, 400, "The team type cannot be changed.");
        (await CountAsync("SELECT COUNT(*) FROM data.team WHERE teamid = @t AND teamtype = 0", ("@t", admins))).Should().Be(1);
        await FaultAsync(() => As(manager).ExecuteAsync(Associate(Team(admins), TeamMembership, User(manager.Id))),
            ErrorCodes.RoleUpdateValidationFailure, 403);

        // Access -> owner is refused too; echoing the stored type is allowed.
        var access = await CreateTeamAsync(fx, _a, 1);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("team", access) { ["teamtype"] = new OptionSetValue(0) }),
            ErrorCodes.InvalidArgument, 400, "The team type cannot be changed.");
        await _admin.UpdateAsync(new Entity("team", access) { ["teamtype"] = new OptionSetValue(1), ["name"] = "still-access" });
        (await Cache(fx).GetProfileAsync(manager.Id, default)).IsSystemAdministrator.Should().BeFalse();
    }

    [Fact]
    public async Task Team_without_a_type_is_an_owner_team()
    {
        var manager = await UserAsync(_a);
        await GrantAsync(manager.Id, (Privilege(AccessRights.WriteAccess, "team"), DepthMask.Global));
        var untyped = await CreateTeamAsync(fx, _a, 0);
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "UPDATE data.team SET teamtype = NULL WHERE teamid = @t");
            cmd.Parameters.AddWithValue("@t", untyped);
            await cmd.ExecuteNonQueryAsync();
        }
        Cache(fx).Invalidate();
        await AssignRoleAsync(fx, untyped, StandardRoles.SystemAdministrator, team: true);

        // A NULL teamtype counts as Owner: joining it needs the escalation check, and its roles reach its members.
        await FaultAsync(() => As(manager).ExecuteAsync(Associate(Team(untyped), TeamMembership, User(manager.Id))),
            ErrorCodes.RoleUpdateValidationFailure, 403);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("team", untyped) { ["teamtype"] = new OptionSetValue(1) }),
            ErrorCodes.InvalidArgument, 400, "The team type cannot be changed.");
        await _admin.ExecuteAsync(Associate(Team(untyped), TeamMembership, User(_uB.Id)));
        (await Cache(fx).GetProfileAsync(_uB.Id, default)).IsSystemAdministrator.Should().BeTrue();
        await _admin.ExecuteAsync(Disassociate(Team(untyped), TeamMembership, User(_uB.Id)));
    }
}
