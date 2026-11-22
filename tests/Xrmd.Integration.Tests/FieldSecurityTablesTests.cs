using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
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
/// The seeded field security tables (plan 14, task 1): <c>fieldsecurityprofile</c>, <c>fieldpermission</c>,
/// <c>systemuserprofiles</c>, <c>teamprofiles</c> and <c>principalobjectattributeaccess</c>, the read-only System Administrator
/// profile, the permission values, the profile associations and their privilege, and the delete cleanup.
/// </summary>
[Collection("xrmd")]
public sealed class FieldSecurityTablesTests(XrmdFixture fx)
{
    private const string UserProfiles = "systemuserprofiles_association";
    private const string TeamProfiles = "teamprofiles_association";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private static readonly Guid AdminProfile = new("572329c1-a042-4e22-be47-367c6374ea45");

    private static IEnumerable<int> OptionValues(AttributeMetadata a) =>
        ((EnumAttributeMetadata)a).OptionSet!.Options.Select(o => o.Value!.Value);

    private static Task FaultAsync(Func<Task> act, int code, int status, string? contains = null) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == code && e.HttpStatus == status && (contains == null || e.Message.Contains(contains)));

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

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

    private static AssociateRequest Associate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private static DisassociateRequest Disassociate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private const string Table = "new_fstables", Secret = "new_fstsecret", Open = "new_fstopen";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>A table with a secured column (<see cref="Secret"/>) and one that is not (<see cref="Open"/>), made once.</summary>
    private async Task EnsureTableAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity(Table) is not null) return;
            var entity = new EntityMetadata { LogicalName = Table, SchemaName = "new_FsTables", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes(
            [
                new StringAttributeMetadata { LogicalName = Secret, SchemaName = "new_FsTSecret", MaxLength = 100, IsSecured = true },
                new StringAttributeMetadata { LogicalName = Open, SchemaName = "new_FsTOpen", MaxLength = 100 },
            ]);
            await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        }
        finally
        {
            Lock.Release();
        }
    }

    private Task<Guid> NewProfileAsync() =>
        Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "fsp-" + Guid.NewGuid().ToString("N")[..8] });

    [Fact]
    public void Field_security_tables_are_seeded_with_learn_columns()
    {
        FieldSecurityTables.Profile.Should().Be("fieldsecurityprofile");
        FieldSecurityTables.Permission.Should().Be("fieldpermission");
        FieldSecurityTables.UserProfiles.Should().Be("systemuserprofiles");
        FieldSecurityTables.TeamProfiles.Should().Be("teamprofiles");
        FieldSecurityTables.Share.Should().Be("principalobjectattributeaccess");
        FieldSecurityTables.SystemAdministratorProfileId.Should().Be(AdminProfile);
        (FieldSecurityTables.Allowed, FieldSecurityTables.NotAllowed).Should().Be((4, 0));

        foreach (var (name, set, otc) in new[]
                 {
                     ("fieldsecurityprofile", "fieldsecurityprofiles", 1200), ("fieldpermission", "fieldpermissions", 1201),
                     ("systemuserprofiles", "systemuserprofilescollection", 1202), ("teamprofiles", "teamprofilescollection", 1203),
                     ("principalobjectattributeaccess", "principalobjectattributeaccessset", 44),
                 })
        {
            var e = Store.GetEntity(name);
            e.Should().NotBeNull(name);
            e!.EntitySetName.Should().Be(set);
            e.ObjectTypeCode.Should().Be(otc);
            e.OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);
        }
        Store.GetEntity("systemuserprofiles")!.IsIntersect.Should().BeTrue();
        Store.GetEntity("teamprofiles")!.IsIntersect.Should().BeTrue();

        AttributeMetadata Column(string entity, string name) => Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == name);

        var profile = Store.GetEntity("fieldsecurityprofile")!;
        profile.PrimaryIdAttribute.Should().Be("fieldsecurityprofileid");
        profile.PrimaryNameAttribute.Should().Be("name");
        Column("fieldsecurityprofile", "name").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(100);
        Column("fieldsecurityprofile", "name").RequiredLevel!.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
        Column("fieldsecurityprofile", "description").Should().BeOfType<MemoAttributeMetadata>().Which.MaxLength.Should().Be(2000);

        Column("fieldpermission", "fieldsecurityprofileid").Should().BeOfType<LookupAttributeMetadata>()
            .Which.Targets.Should().Equal("fieldsecurityprofile");
        Column("fieldpermission", "entityname").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(128);
        Column("fieldpermission", "attributelogicalname").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(128);
        foreach (var name in new[] { "cancreate", "canread", "canupdate" })
        {
            var a = (PicklistAttributeMetadata)Column("fieldpermission", name);
            a.OptionSet!.Name.Should().Be("field_security_permission_type");
            OptionValues(a).Should().BeEquivalentTo([0, 4]);
            a.DefaultFormValue.Should().Be(0);
        }
        var unmasked = (PicklistAttributeMetadata)Column("fieldpermission", "canreadunmasked");
        unmasked.OptionSet!.Name.Should().Be("field_security_permission_readunmasked");
        OptionValues(unmasked).Should().BeEquivalentTo([0, 1, 3]);
        Store.GetOptionSet("field_security_permission_type").Should().NotBeNull();
        Store.GetOptionSet("field_security_permission_readunmasked").Should().NotBeNull();

        Column("systemuserprofiles", "systemuserid").Should().BeOfType<UniqueIdentifierAttributeMetadata>();
        Column("systemuserprofiles", "fieldsecurityprofileid").Should().BeOfType<UniqueIdentifierAttributeMetadata>();
        Column("teamprofiles", "teamid").Should().BeOfType<UniqueIdentifierAttributeMetadata>();
        Column("teamprofiles", "fieldsecurityprofileid").Should().BeOfType<UniqueIdentifierAttributeMetadata>();

        const string share = "principalobjectattributeaccess";
        Column(share, "attributeid").Should().BeOfType<UniqueIdentifierAttributeMetadata>();
        Column(share, "objectid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().BeEmpty();
        TypeMapping.IsPolymorphicLookup(Column(share, "objectid")).Should().BeTrue();
        Column(share, "objecttypecode").Should().BeOfType<StringAttributeMetadata>();
        Column(share, "principalid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().BeEquivalentTo(["systemuser", "team"]);
        TypeMapping.IsPolymorphicLookup(Column(share, "principalid")).Should().BeTrue();
        Column(share, "readaccess").Should().BeOfType<BooleanAttributeMetadata>();
        Column(share, "updateaccess").Should().BeOfType<BooleanAttributeMetadata>();

        var cascade = Store.GetOneToManyRelationships().Single(r => r.SchemaName == "lk_fieldpermission_fieldsecurityprofileid");
        cascade.ReferencedEntity.Should().Be("fieldsecurityprofile");
        cascade.ReferencingEntity.Should().Be("fieldpermission");
        cascade.ReferencingAttribute.Should().Be("fieldsecurityprofileid");
        cascade.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);

        SecurityAdministration.FindRelationship(UserProfiles).Should()
            .Be(new SecurityRelationship(UserProfiles, "systemuser", "fieldsecurityprofile", "systemuserprofiles"));
        SecurityAdministration.FindRelationship(TeamProfiles).Should()
            .Be(new SecurityRelationship(TeamProfiles, "team", "fieldsecurityprofile", "teamprofiles"));

        foreach (var name in new[] { "fieldsecurityprofile", "fieldpermission", "systemuserprofiles", "teamprofiles", share })
            SecurityCache.SecurityEntities.Should().Contain(name);
    }

    [Fact]
    public async Task Field_security_privileges_are_granted_to_standard_roles()
    {
        var customizer = await RolePrivilegesAsync(StandardRoles.SystemCustomizer);
        var basic = await RolePrivilegesAsync(StandardRoles.BasicUser);
        foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo" })
        {
            foreach (var table in new[] { "FieldSecurityProfile", "FieldPermission", "PrincipalObjectAttributeAccess" })
                (await CountAsync("SELECT COUNT(*) FROM data.privilege WHERE name = @n", ("@n", $"prv{verb}{table}"))).Should().Be(1, $"prv{verb}{table}");
            customizer.Should().NotContainKey($"prv{verb}FieldSecurityProfile");
            customizer.Should().NotContainKey($"prv{verb}FieldPermission");
            basic.Should().NotContainKey($"prv{verb}FieldSecurityProfile");
            basic.Should().NotContainKey($"prv{verb}FieldPermission");
        }
        foreach (var verb in new[] { "Create", "Read", "Write", "Delete" })
            basic.Should().ContainKey($"prv{verb}PrincipalObjectAttributeAccess").WhoseValue.Should().Be((int)DepthMask.Global);

        // The System Administrator holds every one of them at Global (through its role template, not roleprivileges rows).
        var adminUser = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;
        var administrator = await Cache(fx).GetProfileAsync(adminUser, default);
        administrator.IsSystemAdministrator.Should().BeTrue();
        foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo" })
            foreach (var table in new[] { "FieldSecurityProfile", "FieldPermission", "PrincipalObjectAttributeAccess" })
                administrator.DepthOf($"prv{verb}{table}").Should().Be(DepthMask.Global, $"prv{verb}{table}");
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    [Fact]
    public async Task Permission_rows_of_the_system_administrator_profile_cannot_be_changed_or_deleted()
    {
        await EnsureTableAsync();
        // No request can add one; a row left by SQL (an older version, a manual edit) is still read-only.
        var id = Guid.NewGuid();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, """
                INSERT INTO data.fieldpermission (fieldpermissionid, fieldsecurityprofileid, entityname, attributelogicalname, canread, cancreate, canupdate,
                    canreadunmasked, createdon, modifiedon, statecode, statuscode)
                VALUES (@id, @profile, @entity, @attribute, 4, 4, 4, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@profile", AdminProfile);
            cmd.Parameters.AddWithValue("@entity", Table);
            cmd.Parameters.AddWithValue("@attribute", Secret);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            const int adminProfileReadOnly = unchecked((int)0x8004F50C);
            await FaultAsync(() => Admin.UpdateAsync(new Entity("fieldpermission", id) { ["canread"] = new OptionSetValue(0) }), adminProfileReadOnly, 403);
            await FaultAsync(() => Admin.DeleteAsync("fieldpermission", id), adminProfileReadOnly, 403);
            (await CountAsync("SELECT COUNT(*) FROM data.fieldpermission WHERE fieldpermissionid = @id AND canread = 4", ("@id", id))).Should().Be(1);
        }
        finally
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, "DELETE FROM data.fieldpermission WHERE fieldpermissionid = @id");
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
            Cache(fx).Invalidate();
        }
    }

    [Fact]
    public async Task System_administrator_profile_is_seeded_and_read_only()
    {
        await EnsureTableAsync();
        const string sql = "SELECT COUNT(*) FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = @id AND name = N'System Administrator'";
        (await CountAsync(sql, ("@id", AdminProfile))).Should().Be(1);
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await new SystemSeed(Store, factory).EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName));
        (await CountAsync(sql, ("@id", AdminProfile))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.fieldpermission WHERE fieldsecurityprofileid = @id", ("@id", AdminProfile))).Should().Be(0);

        const int adminProfileReadOnly = unchecked((int)0x8004F50C);
        await FaultAsync(() => Admin.UpdateAsync(new Entity("fieldsecurityprofile", AdminProfile) { ["description"] = "x" }),
            adminProfileReadOnly, 403);
        await FaultAsync(() => Admin.DeleteAsync("fieldsecurityprofile", AdminProfile), adminProfileReadOnly, 403);
        await FaultAsync(() => Admin.CreateAsync(new Entity("fieldpermission")
        {
            ["fieldsecurityprofileid"] = new EntityReference("fieldsecurityprofile", AdminProfile),
            ["entityname"] = Table, ["attributelogicalname"] = Secret, ["canread"] = new OptionSetValue(4),
        }), adminProfileReadOnly, 403);

        // A permission of another profile cannot be moved to it either (Update, or Associate over the lookup).
        var other = await NewProfileAsync();
        var permission = await Admin.CreateAsync(new Entity("fieldpermission")
        {
            ["fieldsecurityprofileid"] = new EntityReference("fieldsecurityprofile", other),
            ["entityname"] = Table, ["attributelogicalname"] = Secret, ["canread"] = new OptionSetValue(4),
        });
        await FaultAsync(() => Admin.UpdateAsync(new Entity("fieldpermission", permission)
        {
            ["fieldsecurityprofileid"] = new EntityReference("fieldsecurityprofile", AdminProfile),
        }), adminProfileReadOnly, 403);
        await FaultAsync(() => Admin.ExecuteAsync(Associate(new EntityReference("fieldsecurityprofile", AdminProfile),
            "lk_fieldpermission_fieldsecurityprofileid", new EntityReference("fieldpermission", permission))), adminProfileReadOnly, 403);

        // A user can still be given the profile (it changes the associations, not the profile).
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await Admin.ExecuteAsync(Associate(new EntityReference("systemuser", user), UserProfiles,
            new EntityReference("fieldsecurityprofile", AdminProfile)));
        await Admin.ExecuteAsync(Disassociate(new EntityReference("systemuser", user), UserProfiles,
            new EntityReference("fieldsecurityprofile", AdminProfile)));

        // Another profile is an ordinary row; deleting it takes its permissions with it.
        await Admin.UpdateAsync(new Entity("fieldsecurityprofile", other) { ["description"] = "changed" });
        await Admin.DeleteAsync("fieldsecurityprofile", other);
        (await CountAsync("SELECT COUNT(*) FROM data.fieldpermission WHERE fieldpermissionid = @id", ("@id", permission))).Should().Be(0);
    }

    [Fact]
    public async Task Field_permission_values_are_validated()
    {
        await EnsureTableAsync();
        var profile = new EntityReference("fieldsecurityprofile", await NewProfileAsync());
        Entity Permission(string column, int value) => new("fieldpermission")
        {
            ["fieldsecurityprofileid"] = profile, ["entityname"] = Table, ["attributelogicalname"] = Secret,
            [column] = new OptionSetValue(value),
        };

        foreach (var column in new[] { "canread", "cancreate", "canupdate" })
            await FaultAsync(() => Admin.CreateAsync(Permission(column, 2)), ErrorCodes.InvalidArgument, 400, column);
        await FaultAsync(() => Admin.CreateAsync(Permission("canreadunmasked", 3)), ErrorCodes.InvalidArgument, 400, "masking");
        await FaultAsync(() => Admin.CreateAsync(Permission("canreadunmasked", 1)), ErrorCodes.InvalidArgument, 400, "masking");

        var id = await Admin.CreateAsync(Permission("canread", 4));
        await Admin.UpdateAsync(new Entity("fieldpermission", id) { ["canupdate"] = new OptionSetValue(4), ["canreadunmasked"] = new OptionSetValue(0) });
        await FaultAsync(() => Admin.UpdateAsync(new Entity("fieldpermission", id) { ["canread"] = new OptionSetValue(2) }),
            ErrorCodes.InvalidArgument, 400, "canread");
        await FaultAsync(() => Admin.UpdateAsync(new Entity("fieldpermission", id) { ["canreadunmasked"] = new OptionSetValue(3) }),
            ErrorCodes.InvalidArgument, 400, "masking");
    }

    [Fact]
    public async Task Field_permission_needs_an_existing_secured_column()
    {
        await EnsureTableAsync();
        var profile = new EntityReference("fieldsecurityprofile", await NewProfileAsync());
        Entity Permission(string? entity, string? attribute)
        {
            var e = new Entity("fieldpermission") { ["fieldsecurityprofileid"] = profile, ["canread"] = new OptionSetValue(4) };
            if (entity is not null) e["entityname"] = entity;
            if (attribute is not null) e["attributelogicalname"] = attribute;
            return e;
        }

        await FaultAsync(() => Admin.CreateAsync(Permission(Table, Open)), ErrorCodes.PermissionOnUnsecuredColumn, 400, Open);
        await FaultAsync(() => Admin.CreateAsync(Permission(Table, "new_nosuchcolumn")), ErrorCodes.ObjectDoesNotExist, 404, "new_nosuchcolumn");
        await FaultAsync(() => Admin.CreateAsync(Permission("new_nosuchtable", "new_nosuchcolumn")), ErrorCodes.ObjectDoesNotExist, 404);
        await FaultAsync(() => Admin.CreateAsync(Permission(Table, null)), ErrorCodes.InvalidArgument, 400);

        // Update: moving a permission is checked against the resulting column; other changes are not affected.
        var id = await Admin.CreateAsync(Permission(Table, Secret.ToUpperInvariant()));
        await FaultAsync(() => Admin.UpdateAsync(new Entity("fieldpermission", id) { ["attributelogicalname"] = Open }),
            ErrorCodes.PermissionOnUnsecuredColumn, 400);
        await FaultAsync(() => Admin.UpdateAsync(new Entity("fieldpermission", id) { ["entityname"] = "account" }), ErrorCodes.ObjectDoesNotExist, 404);
        await Admin.UpdateAsync(new Entity("fieldpermission", id) { ["canupdate"] = new OptionSetValue(4) });
        await Admin.UpdateAsync(new Entity("fieldpermission", id) { ["entityname"] = Table, ["attributelogicalname"] = Secret });
    }

    [Fact]
    public async Task Deleting_a_secured_column_or_table_removes_its_permissions_and_shares()
    {
        var table = "new_fsdel" + Guid.NewGuid().ToString("N")[..6];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes(
        [
            new StringAttributeMetadata { LogicalName = table + "_a", SchemaName = table + "_a", MaxLength = 100, IsSecured = true },
            new StringAttributeMetadata { LogicalName = table + "_b", SchemaName = table + "_b", MaxLength = 100, IsSecured = true },
        ]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = table + "_name", SchemaName = table + "_name", MaxLength = 100 });
        var profile = new EntityReference("fieldsecurityprofile", await NewProfileAsync());
        var record = await Admin.CreateAsync(new Entity(table) { [table + "_name"] = "r" });
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        foreach (var column in new[] { table + "_a", table + "_b" })
        {
            await Admin.CreateAsync(new Entity("fieldpermission")
            {
                ["fieldsecurityprofileid"] = profile, ["entityname"] = table, ["attributelogicalname"] = column, ["canread"] = new OptionSetValue(4),
            });
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Share)
            {
                [FieldSecurityTables.ShareObjectId] = new EntityReference(table, record), [FieldSecurityTables.ShareObjectTypeCodeColumn] = table,
                [FieldSecurityTables.ShareAttributeId] = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == column).MetadataId!.Value,
                [FieldSecurityTables.SharePrincipalId] = new EntityReference("systemuser", user),
                [FieldSecurityTables.ShareReadAccess] = true,
            });
        }
        Task<int> PermissionsAsync() => CountAsync("SELECT COUNT(*) FROM data.fieldpermission WHERE entityname = @e", ("@e", table));
        Task<int> SharesAsync() => CountAsync("SELECT COUNT(*) FROM data.principalobjectattributeaccess WHERE objecttypecode = @e", ("@e", table));
        (await PermissionsAsync(), await SharesAsync()).Should().Be((2, 2));

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = table + "_a" });
        (await PermissionsAsync(), await SharesAsync()).Should().Be((1, 1), "the deleted column's permission and share go with it");

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
        (await PermissionsAsync(), await SharesAsync()).Should().Be((0, 0), "the deleted table's permissions and shares go with it");
    }

    [Fact]
    public async Task Profile_association_needs_write_profile_privilege()
    {
        var root = await RootBusinessUnitAsync(fx);
        var profile = new EntityReference("fieldsecurityprofile", await NewProfileAsync());
        var target = await CreateUserAsync(fx, root);
        var team = await CreateTeamAsync(fx, root);

        // A System Customizer cannot give a profile.
        var customizerOid = Guid.NewGuid();
        var customizer = await CreateUserAsync(fx, root, customizerOid);
        await AssignRoleAsync(fx, customizer, StandardRoles.SystemCustomizer);
        await FaultAsync(() => ServiceFor(fx, customizerOid).ExecuteAsync(
            Associate(new EntityReference("systemuser", target), UserProfiles, profile)), ErrorCodes.PrivilegeDenied, 403);

        // Write on users and teams is not enough: the profile privilege is what counts.
        var writerOid = Guid.NewGuid();
        var writer = await CreateUserAsync(fx, root, writerOid);
        await GrantAsync(fx, writer, team: false, ("prvWriteSystemUser", DepthMask.Global), ("prvWriteTeam", DepthMask.Global),
            ("prvReadFieldSecurityProfile", DepthMask.Global));
        await FaultAsync(() => ServiceFor(fx, writerOid).ExecuteAsync(
            Associate(new EntityReference("systemuser", target), UserProfiles, profile)), ErrorCodes.PrivilegeDenied, 403,
            "prvWriteFieldSecurityProfile");
        await FaultAsync(() => ServiceFor(fx, writerOid).ExecuteAsync(
            Associate(profile, TeamProfiles, new EntityReference("team", team))), ErrorCodes.PrivilegeDenied, 403,
            "prvWriteFieldSecurityProfile");

        // With it, a user that is not an administrator can associate and disassociate.
        var granterOid = Guid.NewGuid();
        var granter = await CreateUserAsync(fx, root, granterOid);
        await GrantAsync(fx, granter, team: false, ("prvWriteFieldSecurityProfile", DepthMask.Global));
        await ServiceFor(fx, granterOid).ExecuteAsync(Associate(new EntityReference("systemuser", target), UserProfiles, profile));
        await ServiceFor(fx, granterOid).ExecuteAsync(Disassociate(new EntityReference("systemuser", target), UserProfiles, profile));

        // A System Administrator can, from either side; a duplicate is 412.
        await Admin.ExecuteAsync(Associate(profile, UserProfiles, new EntityReference("systemuser", target)));
        await Admin.ExecuteAsync(Associate(new EntityReference("team", team), TeamProfiles, profile));
        (await CountAsync("SELECT COUNT(*) FROM data.systemuserprofiles WHERE systemuserid = @u AND fieldsecurityprofileid = @p",
            ("@u", target), ("@p", profile.Id))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.teamprofiles WHERE teamid = @t AND fieldsecurityprofileid = @p",
            ("@t", team), ("@p", profile.Id))).Should().Be(1);
        await FaultAsync(() => Admin.ExecuteAsync(Associate(profile, UserProfiles, new EntityReference("systemuser", target))),
            ErrorCodes.DuplicateRecord, 412);
        await Admin.ExecuteAsync(Disassociate(new EntityReference("team", team), TeamProfiles, profile));
        (await CountAsync("SELECT COUNT(*) FROM data.teamprofiles WHERE teamid = @t", ("@t", team))).Should().Be(0);

        // The intersects are written only through the relationships.
        await FaultAsync(() => Admin.CreateAsync(new Entity("systemuserprofiles")
        {
            ["systemuserid"] = target, ["fieldsecurityprofileid"] = profile.Id,
        }), ErrorCodes.PrivilegeDenied, 403);
        await FaultAsync(() => Admin.CreateAsync(new Entity("teamprofiles")
        {
            ["teamid"] = team, ["fieldsecurityprofileid"] = profile.Id,
        }), ErrorCodes.PrivilegeDenied, 403);

        // The written intersect is reported to the pipeline (cache invalidation, plug-ins).
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        core.WrittenEntities(Associate(profile, UserProfiles, new EntityReference("systemuser", target))).Should().Equal("systemuserprofiles");
        core.WrittenEntities(Associate(profile, TeamProfiles, new EntityReference("team", team))).Should().Equal("teamprofiles");
    }

    [Fact]
    public async Task Deleting_principal_or_record_removes_its_profile_links_and_shares()
    {
        var root = await RootBusinessUnitAsync(fx);
        var profile = new EntityReference("fieldsecurityprofile", await NewProfileAsync());
        var user = await CreateUserAsync(fx, root);
        var team = await CreateTeamAsync(fx, root);
        await Admin.ExecuteAsync(Associate(new EntityReference("team", team), TeamProfiles, profile));
        await Admin.ExecuteAsync(Associate(new EntityReference("systemuser", user), UserProfiles, profile));

        var record = await Admin.CreateAsync(new Entity("account") { ["name"] = "fs-" + Guid.NewGuid().ToString("N")[..8] });
        var other = await Admin.CreateAsync(new Entity("account") { ["name"] = "fs-" + Guid.NewGuid().ToString("N")[..8] });
        var nameId = Store.GetEntity("account")!.Attributes.Single(a => a.LogicalName == "name").MetadataId!.Value;
        // account.name is not secured, so F6 refuses these shares through a request (FieldSharingTests): they are written with SQL.
        async Task ShareAsync(Guid objectId, Guid principal, int principalType)
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, """
                INSERT INTO data.principalobjectattributeaccess (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                    attributeid, principalid, principalidtype, readaccess, updateaccess)
                VALUES (NEWID(), @record, 1, 'account', @attribute, @principal, @type, 1, 0)
                """);
            cmd.Parameters.AddWithValue("@record", objectId);
            cmd.Parameters.AddWithValue("@attribute", nameId);
            cmd.Parameters.AddWithValue("@principal", principal);
            cmd.Parameters.AddWithValue("@type", principalType);
            await cmd.ExecuteNonQueryAsync();
        }
        await ShareAsync(record, user, 8);
        await ShareAsync(other, team, 9);
        await ShareAsync(other, user, 8);

        // Deleting a record removes its field shares.
        await Admin.DeleteAsync("account", record);
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectattributeaccess WHERE objectid = @id", ("@id", record))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectattributeaccess WHERE objectid = @id", ("@id", other))).Should().Be(2);

        // Deleting a team removes its profile links and the field shares it holds.
        await Admin.DeleteAsync("team", team);
        (await CountAsync("SELECT COUNT(*) FROM data.teamprofiles WHERE teamid = @id", ("@id", team))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectattributeaccess WHERE principalid = @id", ("@id", team))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectattributeaccess WHERE objectid = @id", ("@id", other))).Should().Be(1);

        // Deleting a profile removes its user and team links.
        await Admin.DeleteAsync("fieldsecurityprofile", profile.Id);
        (await CountAsync("SELECT COUNT(*) FROM data.systemuserprofiles WHERE fieldsecurityprofileid = @id", ("@id", profile.Id))).Should().Be(0);

        await Admin.DeleteAsync("account", other);
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectattributeaccess WHERE principalid = @id", ("@id", user))).Should().Be(0);
    }
}
