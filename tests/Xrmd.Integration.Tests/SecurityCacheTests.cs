using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class SecurityCacheTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string ReadProject = "prvReadpb_Project";
    private const string WriteProject = "prvWritepb_Project";
    private Guid _root;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _root = await RootBusinessUnitAsync(fx);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Profile_unions_user_and_owner_team_roles_with_max_depth()
    {
        var user = await CreateUserAsync(fx, _root);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "direct basic", (ReadProject, DepthMask.Basic)));
        var ownerTeam = await CreateTeamAsync(fx, _root, 0, user);
        await AssignRoleAsync(fx, ownerTeam, await CreateRoleAsync(fx, "team deep", (ReadProject, DepthMask.Deep)), team: true);
        var accessTeam = await CreateTeamAsync(fx, _root, 1, user);
        await AssignRoleAsync(fx, accessTeam, await CreateRoleAsync(fx, "access global", (ReadProject, DepthMask.Global), (WriteProject, DepthMask.Global)),
            team: true);

        var profile = await Cache(fx).GetProfileAsync(user, default);

        profile.DepthOf(ReadProject).Should().Be(DepthMask.Deep);
        profile.DepthOf(WriteProject).Should().Be(DepthMask.None);
        profile.HasRoles.Should().BeTrue();
        profile.IsSystemAdministrator.Should().BeFalse();
        profile.IsDisabled.Should().BeFalse();
        profile.BusinessUnitId.Should().Be(_root);
        profile.OwnerTeamIds.Should().BeEquivalentTo([ownerTeam]);
        profile.TeamIds.Should().BeEquivalentTo([ownerTeam, accessTeam]);
    }

    [Fact]
    public async Task Direct_privileges_exclude_team_roles()
    {
        var user = await CreateUserAsync(fx, _root);
        var team = await CreateTeamAsync(fx, _root, 0, user);
        await AssignRoleAsync(fx, team, StandardRoles.Delegate, team: true);

        var profile = await Cache(fx).GetProfileAsync(user, default);

        profile.DepthOf(PrivilegeNames.ActOnBehalfOfAnotherUser).Should().Be(DepthMask.Global);
        profile.DirectPrivileges.Should().NotContain(PrivilegeNames.ActOnBehalfOfAnotherUser);

        await AssignRoleAsync(fx, user, StandardRoles.Delegate);
        (await Cache(fx).GetProfileAsync(user, default)).DirectPrivileges.Should().Contain("PRVACTONBEHALFOFANOTHERUSER");
    }

    [Fact]
    public async Task Direct_system_administrator_acts_on_behalf_and_team_administrator_does_not()
    {
        var admin = await AdminUserIdAsync();
        var adminProfile = await Cache(fx).GetProfileAsync(admin, default);
        adminProfile.IsSystemAdministrator.Should().BeTrue();
        adminProfile.IsDisabled.Should().BeFalse(); // isdisabled is NULL for the seeded administrator
        adminProfile.DepthOf("prvDeleteAnything").Should().Be(DepthMask.Global);
        adminProfile.DirectPrivileges.Should().Contain(PrivilegeNames.ActOnBehalfOfAnotherUser);

        var user = await CreateUserAsync(fx, _root);
        await AssignRoleAsync(fx, await CreateTeamAsync(fx, _root, 0, user), StandardRoles.SystemAdministrator, team: true);
        var viaTeam = await Cache(fx).GetProfileAsync(user, default);
        viaTeam.IsSystemAdministrator.Should().BeTrue();
        viaTeam.DirectPrivileges.Should().NotContain(PrivilegeNames.ActOnBehalfOfAnotherUser);
    }

    [Fact]
    public async Task User_without_roles_disabled_user_and_missing_user()
    {
        var disabled = await CreateUserAsync(fx, _root, disabled: true);
        var profile = await Cache(fx).GetProfileAsync(disabled, default);
        profile.IsDisabled.Should().BeTrue();
        profile.HasRoles.Should().BeFalse();
        profile.Privileges.Should().BeEmpty();

        var missing = Guid.NewGuid();
        var act = () => Cache(fx).GetProfileAsync(missing, default);
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public async Task Business_unit_tree_system_user_and_unrestricted_callers()
    {
        var a = await CreateBusinessUnitAsync(fx, _root, "cache A");
        var a1 = await CreateBusinessUnitAsync(fx, a, "cache A1");

        var tree = await Cache(fx).GetBusinessUnitTreeAsync(default);
        tree.SubtreeOf(a).Should().BeEquivalentTo([a, a1]);
        tree.AncestorsOf(a1).Should().Equal(a, _root);

        var system = await Cache(fx).SystemUserIdAsync(default);
        system.Should().Be(Xrmd.Metadata.SeedOptions.DeriveSystemUserId(XrmdFixture.OrgName));
        Cache(fx).IsUnrestricted(Caller.Internal, system).Should().BeTrue();
        Cache(fx).IsUnrestricted(new Caller(system, _root), system).Should().BeTrue();
        Cache(fx).IsUnrestricted(new Caller(await AdminUserIdAsync(), _root), system).Should().BeFalse();
        // Only Caller.Internal itself (or SYSTEM) is unrestricted, not any caller with an empty user id.
        Cache(fx).IsUnrestricted(new Caller(Guid.Empty, _root), system).Should().BeFalse();
        Cache(fx).IsUnrestricted(new Caller(Guid.Empty, Guid.Empty, ImpersonatorId: Guid.NewGuid()), system).Should().BeFalse();
    }

    [Fact]
    public async Task Write_to_role_via_pipeline_invalidates()
    {
        var user = await CreateUserAsync(fx, _root);
        var role = await CreateRoleAsync(fx, "invalidated", (ReadProject, DepthMask.Basic));
        await AssignRoleAsync(fx, user, role);
        (await Cache(fx).GetProfileAsync(user, default)).DepthOf(WriteProject).Should().Be(DepthMask.None);

        await AddPrivilegeAsync(fx, role, WriteProject, DepthMask.Local); // no Invalidate: the cached profile stays
        (await Cache(fx).GetProfileAsync(user, default)).DepthOf(WriteProject).Should().Be(DepthMask.None);

        await ServiceFor(fx, fx.AdminObjectId).UpdateAsync(new Entity("role", role) { ["description"] = "touched" });

        (await Cache(fx).GetProfileAsync(user, default)).DepthOf(WriteProject).Should().Be(DepthMask.Local);
    }

    private sealed class InvalidatingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public Xrmd.Abstractions.Storage.ISqlDialect Dialect => inner.Dialect;

        public Type ConnectionType => inner.ConnectionType;

        public SecurityCache? Cache;
        public int Opens;

        public Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Opens) == 1) Cache!.Invalidate(); // Invalidate while the first load is running
            return inner.OpenAsync(ct);
        }
    }

    [Fact]
    public async Task Invalidate_during_load_is_not_lost()
    {
        var factory = new InvalidatingFactory(fx.App.Services.GetRequiredService<IDbConnectionFactory>());
        var cache = factory.Cache = new SecurityCache(factory);
        var user = await CreateUserAsync(fx, _root);
        var role = await CreateRoleAsync(fx, "slow load", (ReadProject, DepthMask.Basic));
        await AssignRoleAsync(fx, user, role);

        (await cache.GetProfileAsync(user, default)).DepthOf(WriteProject).Should().Be(DepthMask.None);
        var opensAfterFirst = factory.Opens;
        await AddPrivilegeAsync(fx, role, WriteProject, DepthMask.Basic);

        // The profile loaded before the Invalidate is stale: the next read must reload and see the new privilege.
        (await cache.GetProfileAsync(user, default)).DepthOf(WriteProject).Should().Be(DepthMask.Basic);
        factory.Opens.Should().BeGreaterThan(opensAfterFirst);
    }

    [Fact]
    public async Task Writes_outside_the_pipeline_invalidate_the_security_cache()
    {
        // SecurityBootstrap.EnsureSystemAdministratorAsync: the user the cache has seen without roles becomes an administrator.
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, _root, oid);
        (await Cache(fx).GetProfileAsync(user, default)).IsSystemAdministrator.Should().BeFalse();

        await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureSystemAdministratorAsync(oid, "bootstrapped", default);

        (await Cache(fx).GetProfileAsync(user, default)).IsSystemAdministrator.Should().BeTrue("the next request sees the role");

        // SecurityAdministration (the writes of the Admin API, on a session of its own): role, membership, role privilege.
        var administration = fx.App.Services.GetRequiredService<SecurityAdministration>();
        var other = await CreateUserAsync(fx, _root);
        var team = await CreateTeamAsync(fx, _root, 0);
        var role = await CreateRoleAsync(fx, "admin writes", (ReadProject, DepthMask.Basic));
        (await Cache(fx).GetProfileAsync(other, default)).HasRoles.Should().BeFalse();

        await InSessionAsync(s => administration.AddRoleAsync(s, new EntityReference("systemuser", other), role, default));
        var profile = await Cache(fx).GetProfileAsync(other, default);
        profile.DepthOf(ReadProject).Should().Be(DepthMask.Basic);
        profile.DepthOf(WriteProject).Should().Be(DepthMask.None);

        await InSessionAsync(s => administration.SetRolePrivilegesAsync(s, role,
            [(PrivilegeNames.IdOf(WriteProject), DepthMask.Local)], replaceAll: false, default));
        (await Cache(fx).GetProfileAsync(other, default)).DepthOf(WriteProject).Should().Be(DepthMask.Local);

        await InSessionAsync(s => administration.AddMemberAsync(s, team, other, default));
        (await Cache(fx).GetProfileAsync(other, default)).TeamIds.Should().Contain(team);

        await InSessionAsync(s => administration.RemoveRoleAsync(s, new EntityReference("systemuser", other), role, default));
        (await Cache(fx).GetProfileAsync(other, default)).HasRoles.Should().BeFalse();
    }

    /// <summary>Runs <paramref name="write"/> in a session of its own, committed (and reported to the session) when it succeeds.</summary>
    private async Task InSessionAsync(Func<DataSession, Task> write)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            await write(session);
        }
        catch
        {
            await tx.RollbackAsync();
            session.RolledBack();
            throw;
        }
        await tx.CommitAsync();
        session.Committed();
    }

    private async Task<Guid> AdminUserIdAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }
}
