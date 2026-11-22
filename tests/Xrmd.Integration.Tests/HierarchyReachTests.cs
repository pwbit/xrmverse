using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.5, task 3: the reporting tree, the hierarchy settings and the reach a manager gets from them (rulings H3, H4, H10).
/// Every test restores the organization settings and the exclusions it changes.
/// </summary>
[Collection("xrmd")]
public sealed class HierarchyReachTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Enabled = "ishierarchicalsecuritymodelenabled";
    private const string Depth = "maxdepthforhierarchicalsecuritymodel";
    private const string Manager = "parentsystemuserid";
    private Guid _root;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(Store);
        _root = await RootBusinessUnitAsync(fx);
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"UPDATE data.organization SET {Enabled} = 0, {Depth} = 3; DELETE FROM meta.hierarchy_security_exclusion;");
        Cache(fx).Invalidate();
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>Writes the settings directly (no pipeline) and invalidates the cache.</summary>
    private async Task SetSettingsSqlAsync(bool enabled, int depth)
    {
        await ExecuteAsync($"UPDATE data.organization SET {Enabled} = @e, {Depth} = @d", ("@e", enabled), ("@d", depth));
        Cache(fx).Invalidate();
    }

    /// <summary>Sets the manager directly (no pipeline, no loop check) and invalidates the cache.</summary>
    private async Task SetManagerSqlAsync(Guid user, Guid? manager)
    {
        await ExecuteAsync($"UPDATE data.systemuser SET {Manager} = @m WHERE systemuserid = @u",
            ("@m", manager is { } m ? m : DBNull.Value), ("@u", user));
        Cache(fx).Invalidate();
    }

    private async Task<Guid> OrganizationIdAsync() => (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;

    private Task SetExclusionsAsync(params string[] tables) =>
        TestEntities.InSessionAsync(fx.ConnectionString, s => Cache(fx).SetHierarchyExclusionsAsync(s, tables, default));

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int status)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(status, fault.Message);
        return fault;
    }

    private static EntityReference User(Guid id) => new("systemuser", id);

    [Fact]
    public async Task Reach_rules()
    {
        // root ← C ← G ← GG. The manager is in C.
        var c = await CreateBusinessUnitAsync(fx, _root, "reach C");
        var g = await CreateBusinessUnitAsync(fx, c, "reach G");
        var gg = await CreateBusinessUnitAsync(fx, g, "reach GG");
        var m = await CreateUserAsync(fx, c);
        var same = await CreateUserAsync(fx, c);                 // level 1, same unit: counted
        var child = await CreateUserAsync(fx, g);                // level 1, child unit: counted
        var grandchild = await CreateUserAsync(fx, gg);          // level 1, grandchild unit: not counted
        var parent = await CreateUserAsync(fx, _root);           // level 1, parent unit: not counted
        var disabled = await CreateUserAsync(fx, c, disabled: true); // level 1, disabled: counted
        var belowGrandchild = await CreateUserAsync(fx, c);      // level 2 under the uncounted report: counted, read only
        var belowSame = await CreateUserAsync(fx, c);            // level 2: read only
        foreach (var report in new[] { same, child, grandchild, parent, disabled }) await SetManagerSqlAsync(report, m);
        await SetManagerSqlAsync(belowGrandchild, grandchild);
        await SetManagerSqlAsync(belowSame, same);
        var sameTeam = await CreateTeamAsync(fx, c, 0, same);
        var accessTeam = await CreateTeamAsync(fx, c, 1, same);
        var levelTwoTeam = await CreateTeamAsync(fx, c, 0, belowSame);
        var uncountedTeam = await CreateTeamAsync(fx, gg, 0, grandchild);

        // Disabled model: no reach.
        (await Cache(fx).GetProfileAsync(m, default)).Hierarchy.Should().BeNull();

        await SetSettingsSqlAsync(enabled: true, depth: 3);
        var reach = (await Cache(fx).GetProfileAsync(m, default)).Hierarchy;
        reach.Should().NotBeNull();
        reach!.ReadPrincipals.Should().BeEquivalentTo(
            [same, child, disabled, belowGrandchild, belowSame, sameTeam, accessTeam, levelTwoTeam]);
        reach.WritePrincipals.Should().BeEquivalentTo([same, child, disabled, sameTeam, accessTeam]);
        reach.ReadPrincipals.Should().NotContain(uncountedTeam);

        // A user without reports, and a report, have no reach of their own.
        (await Cache(fx).GetProfileAsync(belowSame, default)).Hierarchy.Should().BeNull();

        // Tables (H3): user- and team-owned tables unless excluded; organization-owned, intersect and fileattachment never.
        var project = Store.GetEntity(TestEntities.Project)!;
        reach.Covers(project).Should().BeTrue();
        reach.Covers(Store.GetEntity("account")!).Should().BeTrue();
        reach.Covers(Store.GetEntity("role")!).Should().BeFalse("business-owned");
        reach.Covers(Store.GetEntity("publisher")!).Should().BeFalse("organization-owned");
        reach.Covers(Store.GetEntity("teammembership")!).Should().BeFalse("intersect");
        reach.Covers(Store.GetEntity("fileattachment")!).Should().BeFalse("follows its parent record");
        reach.Covers(new EntityMetadata { LogicalName = "fileattachment", OwnershipType = OwnershipTypes.UserOwned }).Should().BeFalse();

        await SetExclusionsAsync(TestEntities.Project);
        reach = (await Cache(fx).GetProfileAsync(m, default)).Hierarchy!;
        reach.ExcludedTables.Should().BeEquivalentTo([TestEntities.Project]);
        reach.Covers(project).Should().BeFalse("excluded");
        reach.Covers(Store.GetEntity("account")!).Should().BeTrue();

        // H7: the caller and every report up to the depth, without the business unit rule; with teams on request.
        (await Cache(fx).ReportingHierarchyAsync(m, includeTeams: false, default)).Should().BeEquivalentTo(
            [m, same, child, grandchild, parent, disabled, belowGrandchild, belowSame]);
        (await Cache(fx).ReportingHierarchyAsync(m, includeTeams: true, default)).Should().BeEquivalentTo(
            [m, same, child, grandchild, parent, disabled, belowGrandchild, belowSame, sameTeam, accessTeam, levelTwoTeam, uncountedTeam]);

        var settings = await Cache(fx).GetHierarchySettingsAsync(default);
        settings.Enabled.Should().BeTrue();
        settings.Depth.Should().Be(3);
        settings.ExcludedTables.Should().Contain(TestEntities.Project.ToUpperInvariant(), "the names are case-insensitive");
    }

    [Fact]
    public async Task Reach_follows_manager_and_team_changes()
    {
        var org = await OrganizationIdAsync();
        var m = await CreateUserAsync(fx, _root);
        var r1 = await CreateUserAsync(fx, _root);
        var r2 = await CreateUserAsync(fx, _root);
        var team = await CreateTeamAsync(fx, _root, 0);
        await SetSettingsSqlAsync(enabled: true, depth: 3);
        var cache = Cache(fx);

        // Manager set, changed and cleared through the pipeline.
        (await cache.GetProfileAsync(m, default)).Hierarchy.Should().BeNull();
        await Admin.UpdateAsync(new Entity("systemuser", r1) { [Manager] = User(m) });
        (await cache.GetProfileAsync(m, default)).Hierarchy!.WritePrincipals.Should().BeEquivalentTo([r1]);
        await Admin.UpdateAsync(new Entity("systemuser", r2) { [Manager] = User(r1) });
        (await cache.GetProfileAsync(m, default)).Hierarchy!.ReadPrincipals.Should().BeEquivalentTo([r1, r2]);

        // Team membership of a report.
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("team", team),
            Relationship = new Relationship(SecurityAdministration.TeamMembership),
            RelatedEntities = [User(r1)],
        });
        (await cache.GetProfileAsync(m, default)).Hierarchy!.WritePrincipals.Should().BeEquivalentTo([r1, team]);
        await Admin.ExecuteAsync(new DisassociateRequest
        {
            Target = new EntityReference("team", team),
            Relationship = new Relationship(SecurityAdministration.TeamMembership),
            RelatedEntities = [User(r1)],
        });
        (await cache.GetProfileAsync(m, default)).Hierarchy!.WritePrincipals.Should().BeEquivalentTo([r1]);

        // Depth and the model switch, through the pipeline (an organization write invalidates the cache, H10).
        await Admin.UpdateAsync(new Entity("organization", org) { [Depth] = 1 });
        (await cache.GetProfileAsync(m, default)).Hierarchy!.ReadPrincipals.Should().BeEquivalentTo([r1]);
        await Admin.UpdateAsync(new Entity("organization", org) { [Enabled] = false });
        (await cache.GetProfileAsync(m, default)).Hierarchy.Should().BeNull();
        await Admin.UpdateAsync(new Entity("organization", org) { [Enabled] = true, [Depth] = 2 });
        (await cache.GetProfileAsync(m, default)).Hierarchy!.ReadPrincipals.Should().BeEquivalentTo([r1, r2]);

        // Exclusions.
        await SetExclusionsAsync(TestEntities.Project, "account");
        (await cache.GetProfileAsync(m, default)).Hierarchy!.ExcludedTables.Should().BeEquivalentTo([TestEntities.Project, "account"]);
        await SetExclusionsAsync();
        (await cache.GetProfileAsync(m, default)).Hierarchy!.ExcludedTables.Should().BeEmpty();

        // Clearing the manager.
        await Admin.UpdateAsync(new Entity("systemuser", r1) { [Manager] = null });
        (await cache.GetProfileAsync(m, default)).Hierarchy.Should().BeNull();
    }

    [Fact]
    public async Task Manager_set_by_associate_refuses_loops_and_changes_the_reach()
    {
        var m = await CreateUserAsync(fx, _root);
        var r = await CreateUserAsync(fx, _root);
        await SetSettingsSqlAsync(enabled: true, depth: 3);
        var userParentUser = new Relationship("user_parent_user");

        // Associate from the manager (the referenced side): the report's parentsystemuserid points to the manager.
        await Admin.ExecuteAsync(new AssociateRequest { Target = User(m), Relationship = userParentUser, RelatedEntities = [User(r)] });
        (await ScalarAsync($"SELECT {Manager} FROM data.systemuser WHERE systemuserid = @u", ("@u", r))).Should().Be(m);
        (await Cache(fx).GetProfileAsync(m, default)).Hierarchy!.WritePrincipals.Should().BeEquivalentTo([r]);

        // The other way round would close a loop; so would a user managing themselves.
        (await FaultAsync(() => Admin.ExecuteAsync(new AssociateRequest { Target = User(r), Relationship = userParentUser, RelatedEntities = [User(m)] }), 400))
            .Message.Should().Contain("would close one");
        await FaultAsync(() => Admin.ExecuteAsync(new AssociateRequest { Target = User(m), Relationship = userParentUser, RelatedEntities = [User(m)] }), 400);
        (await ScalarAsync($"SELECT {Manager} FROM data.systemuser WHERE systemuserid = @u", ("@u", m))).Should().BeNull();

        await Admin.ExecuteAsync(new DisassociateRequest { Target = User(m), Relationship = userParentUser, RelatedEntities = [User(r)] });
        (await Cache(fx).GetProfileAsync(m, default)).Hierarchy.Should().BeNull();
    }

    [Fact]
    public async Task A_loop_already_above_the_new_manager_is_refused_with_its_own_message()
    {
        // x <-> y written in SQL (no loop check); making y the manager of u walks y -> x -> y without meeting u.
        var x = await CreateUserAsync(fx, _root);
        var y = await CreateUserAsync(fx, _root);
        var u = await CreateUserAsync(fx, _root);
        await SetManagerSqlAsync(x, y);
        await SetManagerSqlAsync(y, x);
        try
        {
            var closing = await FaultAsync(() => Admin.UpdateAsync(new Entity("systemuser", y) { [Manager] = User(x) }), 400);
            var existing = await FaultAsync(() => Admin.UpdateAsync(new Entity("systemuser", u) { [Manager] = User(y) }), 400);

            existing.ErrorCode.Should().Be(closing.ErrorCode, "same refusal, only the cause differs");
            existing.Message.Should().Contain($"the manager chain above user {y} already loops").And.NotContain("would close one");
            (await ScalarAsync($"SELECT {Manager} FROM data.systemuser WHERE systemuserid = @u", ("@u", u))).Should().BeNull();
        }
        finally
        {
            await SetManagerSqlAsync(x, null);
            await SetManagerSqlAsync(y, null);
        }
    }

    [Fact]
    public async Task Deleting_a_table_removes_its_exclusion()
    {
        var table = "new_hx" + Guid.NewGuid().ToString("N")[..8];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await SetExclusionsAsync(table, "account");
        (await Cache(fx).GetHierarchySettingsAsync(default)).ExcludedTables.Should().BeEquivalentTo([table, "account"]);

        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, table, default));

        (await ScalarAsync("SELECT COUNT(*) FROM meta.hierarchy_security_exclusion WHERE entity_logical_name = @t", ("@t", table))).Should().Be(0);
        (await Cache(fx).GetHierarchySettingsAsync(default)).ExcludedTables.Should().BeEquivalentTo(["account"]);
    }

    [Fact]
    public async Task Reporting_tree_survives_cycles_in_the_data()
    {
        var a = await CreateUserAsync(fx, _root);
        var b = await CreateUserAsync(fx, _root);
        await SetManagerSqlAsync(a, b);
        await SetManagerSqlAsync(b, a); // a loop written directly in SQL
        await SetSettingsSqlAsync(enabled: true, depth: 100);

        (await Cache(fx).GetProfileAsync(a, default)).Hierarchy!.ReadPrincipals.Should().BeEquivalentTo([b]);
        (await Cache(fx).ReportingHierarchyAsync(a, includeTeams: false, default)).Should().BeEquivalentTo([a, b]);
        await SetManagerSqlAsync(a, null);
    }
}
