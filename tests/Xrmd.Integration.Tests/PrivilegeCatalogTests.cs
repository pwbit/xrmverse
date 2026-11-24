using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class PrivilegeCatalogTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task User_owned_entity_gets_eight_privileges()
    {
        await TestEntities.EnsureProjectAsync(Store);
        await fx.App.Services.GetRequiredService<PrivilegeCatalog>().EnsureAsync(default);

        foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" })
        {
            var name = $"prv{verb}pb_Project";
            (await ScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE name = '{name}' AND privilegeid = '{PrivilegeNames.IdOf(name.ToLowerInvariant())}' " +
                               "AND canbebasic = 1 AND canbelocal = 1 AND canbedeep = 1 AND canbeglobal = 1")).Should().Be(1, name);
        }
        (await ScalarAsync("SELECT accessright FROM data.privilege WHERE name = 'prvReadpb_Project'")).Should().Be(1);
        (await ScalarAsync("SELECT accessright FROM data.privilege WHERE name = 'prvSharepb_Project'")).Should().Be(262144);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = 'pb_project'")).Should().Be(8);
        PrivilegeNames.For(AccessRights.ReadAccess, Store.GetEntity("pb_project")!).Should().Be("prvReadpb_Project");
        PrivilegeNames.IdOf("prvReadpb_Project").Should().Be(PrivilegeNames.IdOf("prvreadpb_project"));
        var invalid = () => PrivilegeNames.For(AccessRights.ReadAccess | AccessRights.WriteAccess, Store.GetEntity("pb_project")!);
        invalid.Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public async Task Organization_owned_entity_gets_six_global_privileges()
    {
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE 'prv%pluginassembly' AND canbebasic = 0 AND canbelocal = 0 AND canbedeep = 0 AND canbeglobal = 1"))
            .Should().Be(6);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name IN ('prvAssignpluginassembly', 'prvSharepluginassembly')")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = 'pluginassembly'")).Should().Be(6);
        // Business-owned: six verbs without Basic.
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE 'prv%Team' AND canbebasic = 0 AND canbelocal = 1 AND canbedeep = 1 AND canbeglobal = 1"))
            .Should().Be(6);
        // Intersect tables have no privileges; the non-entity ones are Global-only with accessright 0.
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE 'prv%SystemUserRoles'")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name IN ('prvActOnBehalfOfAnotherUser','prvCreateEntity','prvCreateAttribute') AND accessright = 0 AND canbeglobal = 1 AND canbebasic = 0"))
            .Should().Be(3);
    }

    [Fact]
    public async Task New_entity_gets_privileges()
    {
        var entity = new EntityMetadata { LogicalName = "pb_privnew", SchemaName = "pb_PrivNew", OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });

        (await ScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE privilegeid = '{PrivilegeNames.IdOf("prvAssignpb_PrivNew")}' AND name = 'prvAssignpb_PrivNew'")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = 'pb_privnew'")).Should().Be(8);
    }

    [Fact]
    public async Task Ensure_is_idempotent()
    {
        var catalog = fx.App.Services.GetRequiredService<PrivilegeCatalog>();
        await catalog.EnsureAsync(default);
        var privileges = await ScalarAsync("SELECT COUNT(*) FROM data.privilege");
        var links = await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes");
        await catalog.EnsureAsync(default);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege")).Should().Be(privileges);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes")).Should().Be(links);
    }

    /// <summary>
    /// Plan 2.11 A12: every activity table and the pointer share the Activity privileges, notes have the Note ones; only the
    /// pointer and annotation own their rows. Basic User holds them at Basic depth (the activity-role-privileges top-up).
    /// </summary>
    [Fact]
    public async Task Activity_and_note_privileges_are_shared()
    {
        await fx.App.Services.GetRequiredService<PrivilegeCatalog>().EnsureAsync(default);
        foreach (var stem in new[] { "Activity", "Note" })
            foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" })
            {
                var name = $"prv{verb}{stem}";
                (await ScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE name = '{name}' AND privilegeid = '{PrivilegeNames.IdOf(name)}' " +
                                   "AND canbebasic = 1 AND canbelocal = 1 AND canbedeep = 1 AND canbeglobal = 1")).Should().Be(1, name);
            }
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name IN ('prvReadEmail', 'prvReadActivityPointer', 'prvReadTask', " +
                           "'prvReadPhoneCall', 'prvReadAppointment', 'prvReadAnnotation', 'prvReadActivityParty', 'prvReadactivityparty')")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE 'prv%ActivityParty'")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = 'activitypointer'")).Should().Be(8);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = 'annotation'")).Should().Be(8);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode IN ('email', 'task', 'phonecall', 'appointment', 'activityparty')"))
            .Should().Be(0);
        PrivilegeNames.For(AccessRights.ReadAccess, Store.GetEntity("email")!).Should().Be("prvReadActivity");
        PrivilegeNames.For(AccessRights.CreateAccess, Store.GetEntity("annotation")!).Should().Be("prvCreateNote");

        foreach (var stem in new[] { "Activity", "Note" })
            foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo" })
            {
                var name = $"prv{verb}{stem}";
                (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' " +
                                   $"AND privilegeid = '{PrivilegeNames.IdOf(name)}' AND privilegedepthmask = 1")).Should().Be(1, name);
            }
        (await ScalarAsync("SELECT COUNT(*) FROM meta.upgrade_step WHERE name = 'activity-role-privileges'")).Should().Be(1);
    }
}
