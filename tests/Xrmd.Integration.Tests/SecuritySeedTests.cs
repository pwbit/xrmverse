using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class SecuritySeedTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<Guid> RootBusinessUnitAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL");
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData("team", 9)]
    [InlineData("role", 1036)]
    [InlineData("privilege", 1023)]
    [InlineData("privilegeobjecttypecodes", 31)]
    [InlineData("roleprivileges", 12)]
    [InlineData("systemuserroles", 15)]
    [InlineData("teamroles", 40)]
    [InlineData("teammembership", 23)]
    [InlineData("principalobjectaccess", 11)]
    public void Seed_creates_security_tables(string logicalName, int otc)
    {
        var entity = Store.GetEntity(logicalName);
        entity.Should().NotBeNull();
        entity!.ObjectTypeCode.Should().Be(otc);
        Store.GetEntityByObjectTypeCode(otc)!.LogicalName.Should().Be(logicalName);
    }

    [Fact]
    public void Intersect_tables_have_no_primary_name_and_systemuser_has_isdisabled()
    {
        foreach (var name in new[] { "privilegeobjecttypecodes", "roleprivileges", "systemuserroles", "teamroles", "teammembership" })
        {
            Store.GetEntity(name)!.IsIntersect.Should().BeTrue(name);
            Store.GetEntity(name)!.PrimaryNameAttribute.Should().BeNull(name);
        }
        Store.GetEntity("principalobjectaccess")!.PrimaryNameAttribute.Should().BeNull();
        Store.GetEntity("team")!.PrimaryNameAttribute.Should().Be("name");
        Store.GetEntity("systemuser")!.Attributes!.Should().Contain(a => a.LogicalName == "isdisabled");
    }

    [Fact]
    public async Task Standard_roles_are_seeded_in_root_business_unit()
    {
        var root = await RootBusinessUnitAsync();
        foreach (var id in new[] { StandardRoles.SystemAdministrator, StandardRoles.SystemCustomizer, StandardRoles.BasicUser, StandardRoles.Delegate })
            (await ScalarAsync($"SELECT COUNT(*) FROM data.role WHERE roleid = '{id}' AND roletemplateid = '{id}' AND businessunitid = '{root}'")).Should().Be(1);
        (await ScalarAsync(
            $"SELECT COUNT(*) FROM data.role WHERE roleid IN ('{StandardRoles.SystemAdministrator}', '{StandardRoles.SystemCustomizer}', '{StandardRoles.BasicUser}', '{StandardRoles.Delegate}')"))
            .Should().Be(4);

        var actOnBehalf = PrivilegeNames.IdOf(PrivilegeNames.ActOnBehalfOfAnotherUser);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.Delegate}' AND privilegeid = '{actOnBehalf}' AND privilegedepthmask = 8"))
            .Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.Delegate}'")).Should().Be(1);
        var readOrganization = PrivilegeNames.IdOf("prvReadOrganization");
        (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{readOrganization}' AND privilegedepthmask = 8"))
            .Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.SystemAdministrator}'")).Should().Be(0);
        // System Customizer: every privilege of the solution tables it customizes, Read of those the system writes.
        foreach (var privilege in new[]
                 {
                     "prvCreateSolution", "prvWriteSolution", "prvDeleteSolution", "prvReadPublisher", "prvCreatePublisher",
                     "prvCreateSavedQuery", "prvWriteSystemForm", "prvCreateWebResource", "prvDeleteSiteMap", "prvAppendToAppModule",
                     "prvReadSolutionComponent", "prvReadImportJob",
                 })
            (await ScalarAsync(
                    $"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.SystemCustomizer}' AND privilegeid = '{PrivilegeNames.IdOf(privilege)}' AND privilegedepthmask = 8"))
                .Should().Be(1, privilege);
        foreach (var privilege in new[] { "prvCreateSolutionComponent", "prvWriteSolutionComponent", "prvCreateImportJob", "prvDeleteImportJob" })
            (await ScalarAsync(
                    $"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.SystemCustomizer}' AND privilegeid = '{PrivilegeNames.IdOf(privilege)}'"))
                .Should().Be(0, privilege);
        // Every granted privilege exists in the catalog.
        (await ScalarAsync("SELECT COUNT(*) FROM data.roleprivileges rp WHERE NOT EXISTS (SELECT 1 FROM data.privilege p WHERE p.privilegeid = rp.privilegeid)"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Admin_bootstrap_assigns_system_administrator()
    {
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        var admin = $"SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = '{fx.AdminObjectId}'";
        (await ScalarAsync($"SELECT COUNT(*) FROM data.systemuserroles WHERE roleid = '{StandardRoles.SystemAdministrator}' AND systemuserid = ({admin})"))
            .Should().Be(1);

        var rolesBefore = await ScalarAsync("SELECT COUNT(*) FROM data.systemuserroles");
        var usersBefore = await ScalarAsync("SELECT COUNT(*) FROM data.systemuser");
        await bootstrap.EnsureAsync(default);
        var id = await bootstrap.EnsureSystemAdministratorAsync(fx.AdminObjectId, XrmdFixture.AdminName, default);
        (await ScalarAsync("SELECT COUNT(*) FROM data.systemuserroles")).Should().Be(rolesBefore);
        (await ScalarAsync("SELECT COUNT(*) FROM data.systemuser")).Should().Be(usersBefore);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.systemuser WHERE systemuserid = '{id}' AND azureactivedirectoryobjectid = '{fx.AdminObjectId}'")).Should().Be(1);

        var oid = Guid.NewGuid();
        var root = await RootBusinessUnitAsync();
        var created = await bootstrap.EnsureSystemAdministratorAsync(oid, "Ops", default);
        (await ScalarAsync(
            $"SELECT COUNT(*) FROM data.systemuser WHERE systemuserid = '{created}' AND azureactivedirectoryobjectid = '{oid}' AND fullname = 'Ops' AND businessunitid = '{root}'"))
            .Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = '{created}' AND roleid = '{StandardRoles.SystemAdministrator}'")).Should().Be(1);
    }

    [Fact]
    public async Task Principalobjectaccess_has_unique_object_principal_index()
    {
        var objectId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        async Task InsertAsync()
        {
            await using var cmd = TestSql.Command(conn, "INSERT INTO data.principalobjectaccess (principalobjectaccessid, objectid, objecttypecode, principalid) VALUES (NEWID(), @o, 1, @p)");
            cmd.Parameters.AddWithValue("@o", objectId);
            cmd.Parameters.AddWithValue("@p", principalId);
            await cmd.ExecuteNonQueryAsync();
        }

        await InsertAsync();
        var act = InsertAsync;
        TestSql.Dialect.Classify((await act.Should().ThrowAsync<System.Data.Common.DbException>()).Which).Should().Be(DbErrorKind.UniqueViolation);
    }

    [SqlServerFact("an organization from before the field share index is a SQL Server organization (PostgreSQL ones start with it)")]
    public async Task Bootstrap_merges_duplicate_field_shares_before_building_their_unique_index()
    {
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        const string index = FieldSecurityTables.ShareUniqueIndex;
        var (objectId, attributeId, principalId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var rows = $"FROM data.{FieldSecurityTables.Share} WHERE objectid = '{objectId}' AND attributeid = '{attributeId}' AND principalid = '{principalId}'";
        // An organization from before the index: the same (record, column, principal) shared three times.
        await ExecAsync($"DROP INDEX {index} ON data.{FieldSecurityTables.Share}");
        try
        {
            foreach (var (read, update) in new[] { (1, 0), (0, 1), (1, 0) })
                await ExecAsync($"""
                    INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                        attributeid, principalid, principalidtype, readaccess, updateaccess)
                    VALUES (NEWID(), '{objectId}', 1, 'account', '{attributeId}', '{principalId}', 8, {read}, {update})
                    """);
            (await ScalarAsync("SELECT COUNT(*) " + rows)).Should().Be(3);

            await bootstrap.EnsureAsync(default);

            (await ScalarAsync("SELECT COUNT(*) " + rows)).Should().Be(1, "the duplicates are merged into one row");
            (await ScalarAsync("SELECT COUNT(*) " + rows + " AND readaccess = 1 AND updateaccess = 1")).Should().Be(1, "their accesses are ORed");
            (await TestSql.IndexAsync(fx.ConnectionString, "data", FieldSecurityTables.Share, index))!.Unique.Should().BeTrue();
        }
        finally
        {
            await ExecAsync("DELETE " + rows);
            await bootstrap.EnsureAsync(default); // the index, whatever happened above
            SecurityTestHelper.Cache(fx).Invalidate();
        }
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Bootstrap_recognizes_standard_roles_by_template_not_by_id()
    {
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        var root = await RootBusinessUnitAsync();
        var id = StandardRoles.Delegate;
        // A role that took the reserved id without being the standard role (e.g. written before the rules existed).
        await ExecAsync($"DELETE FROM data.role WHERE roleid = '{id}'");
        await ExecAsync($"""
            INSERT INTO data.role (roleid, name, businessunitid, isinherited, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES ('{id}', 'Impostor', '{root}', 1, '{root}', SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """);
        try
        {
            var act = () => bootstrap.EnsureAsync(default);
            (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("reserved");
            (await ScalarAsync($"SELECT COUNT(*) FROM data.role WHERE roleid = '{id}' AND roletemplateid IS NULL")).Should().Be(1);
        }
        finally
        {
            await ExecAsync($"DELETE FROM data.role WHERE roleid = '{id}'");
            await bootstrap.EnsureAsync(default);
            SecurityTestHelper.Cache(fx).Invalidate();
        }
        (await ScalarAsync($"SELECT COUNT(*) FROM data.role WHERE roleid = '{id}' AND roletemplateid = '{id}' AND name = 'Delegate'")).Should().Be(1);
    }

    [Fact]
    public async Task Bootstrap_tops_up_the_custom_api_privileges_of_an_existing_organization_once()
    {
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        var rights = new[]
        {
            AccessRights.CreateAccess, AccessRights.ReadAccess, AccessRights.WriteAccess, AccessRights.DeleteAccess,
            AccessRights.AppendAccess, AccessRights.AppendToAccess, AccessRights.AssignAccess, AccessRights.ShareAccess,
        };
        var customizer = CustomApiTables.All.SelectMany(t => rights.Select(r => PrivilegeNames.For(r, Store.GetEntity(t)!))).ToList();
        var basicRead = CustomApiTables.All.Select(t => PrivilegeNames.For(AccessRights.ReadAccess, Store.GetEntity(t)!)).ToList();
        string Ids(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{PrivilegeNames.IdOf(n)}'"));
        var grants = $"""
            FROM data.roleprivileges WHERE (roleid = '{StandardRoles.SystemCustomizer}' AND privilegeid IN ({Ids(customizer)}))
                OR (roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ({Ids(basicRead)}))
            """;
        var basicReadApi = $"FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{PrivilegeNames.IdOf(basicRead[0])}'";
        (await ScalarAsync("SELECT COUNT(*) " + grants)).Should().Be(customizer.Count + basicRead.Count, "a new organization has them");

        // An organization whose standard roles were created before Plan 15: none of the grants and the top-up not run.
        await ExecAsync("DELETE " + grants);
        await ExecAsync("DELETE FROM meta.upgrade_step WHERE name = N'customapi-role-privileges'");
        try
        {
            await bootstrap.EnsureAsync(default);
            foreach (var name in customizer)
                (await ScalarAsync(
                        $"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.SystemCustomizer}' AND privilegeid = '{PrivilegeNames.IdOf(name)}' AND privilegedepthmask = 8"))
                    .Should().Be(1, name);
            foreach (var name in basicRead)
                (await ScalarAsync(
                        $"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{PrivilegeNames.IdOf(name)}' AND privilegedepthmask = 8"))
                    .Should().Be(1, name);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ({Ids(customizer.Except(basicRead))})"))
                .Should().Be(0, "Basic User only reads the definitions");

            // A second run changes nothing.
            var total = await ScalarAsync("SELECT COUNT(*) FROM data.roleprivileges");
            await bootstrap.EnsureAsync(default);
            (await ScalarAsync("SELECT COUNT(*) FROM data.roleprivileges")).Should().Be(total);

            // The top-up runs once: a grant removed after it is not restored, like any other standard privilege.
            await ExecAsync("DELETE " + basicReadApi);
            await bootstrap.EnsureAsync(default);
            (await ScalarAsync("SELECT COUNT(*) " + basicReadApi)).Should().Be(0);
        }
        finally
        {
            // Whatever failed, the shared organization keeps its grants.
            foreach (var (role, name) in customizer.Select(n => (StandardRoles.SystemCustomizer, n)).Concat(basicRead.Select(n => (StandardRoles.BasicUser, n))))
                await ExecAsync($"""
                    IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE roleid = '{role}' AND privilegeid = '{PrivilegeNames.IdOf(name)}')
                        INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                        VALUES (NEWID(), '{role}', '{PrivilegeNames.IdOf(name)}', 8, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                    """);
            SecurityTestHelper.Cache(fx).Invalidate();
        }
    }

    [Fact]
    public async Task Bootstrap_does_not_restore_a_removed_standard_privilege()
    {
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        var privilege = PrivilegeNames.IdOf("prvDeleteAsyncOperation");
        var row = $"FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{privilege}'";
        (await ScalarAsync("SELECT COUNT(*) " + row)).Should().Be(1);
        await ExecAsync("DELETE " + row);
        try
        {
            await bootstrap.EnsureAsync(default);
            (await ScalarAsync("SELECT COUNT(*) " + row)).Should().Be(0);
        }
        finally
        {
            await ExecAsync($"""
                IF NOT EXISTS (SELECT 1 {row})
                    INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                    VALUES (NEWID(), '{StandardRoles.BasicUser}', '{privilege}', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """);
            SecurityTestHelper.Cache(fx).Invalidate();
        }
    }
}
