using System.Data.Common;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.5, task 1: the hierarchical relationships (<c>user_parent_user</c>, <c>account_parent_account</c>, the one-per-table
/// rule), the manager column <c>systemuser.parentsystemuserid</c> and its loop check, and the three hierarchy settings of
/// <c>organization</c> (defaults, value rules, System Administrator only).
/// </summary>
[Collection("xrmd")]
public sealed class HierarchyMetadataTests(XrmdFixture fx)
{
    private const string Enabled = "ishierarchicalsecuritymodelenabled";
    private const string Depth = "maxdepthforhierarchicalsecuritymodel";
    private const string Position = "usepositionhierarchy";
    private const string Manager = "parentsystemuserid";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int status)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(status, fault.Message);
        return fault;
    }

    /// <summary>A 400 refusal: an XrmdFaultException from the Core or an XrmdValidationException from the metadata store.</summary>
    private static async Task<string> BadRequestAsync(Func<Task> act)
    {
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        if (ex is XrmdFaultException fault) (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400), fault.Message);
        else ex.Should().BeOfType<XrmdValidationException>();
        return ex.Message;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private async Task<Guid> OrganizationIdAsync() => (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;

    [Fact]
    public void Seed_has_user_hierarchy_and_hierarchical_account()
    {
        var manager = Store.GetEntity("systemuser")!.Attributes.Single(a => a.LogicalName == Manager);
        manager.Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("systemuser");
        manager.SchemaName.Should().Be("ParentSystemUserId");

        var user = Store.GetHierarchicalRelationship("systemuser");
        user.Should().NotBeNull();
        user!.SchemaName.Should().Be("user_parent_user");
        (user.ReferencedEntity, user.ReferencingEntity, user.ReferencingAttribute).Should().Be(("systemuser", "systemuser", Manager));
        user.IsHierarchical.Should().BeTrue();
        user.CascadeConfiguration.Delete.Should().Be(CascadeType.RemoveLink);
        foreach (var cascade in new[]
                 {
                     user.CascadeConfiguration.Assign, user.CascadeConfiguration.Merge, user.CascadeConfiguration.Reparent,
                     user.CascadeConfiguration.Share, user.CascadeConfiguration.Unshare, user.CascadeConfiguration.Archive,
                     user.CascadeConfiguration.RollupView,
                 })
            cascade.Should().Be(CascadeType.NoCascade);
        ((OneToManyRelationshipMetadata)Store.GetRelationship("user_parent_user")!).IsHierarchical.Should().BeTrue();

        var account = Store.GetHierarchicalRelationship("account");
        account.Should().NotBeNull();
        account!.SchemaName.Should().Be("account_parent_account");
        account.ReferencingAttribute.Should().Be("parentaccountid");

        Store.GetHierarchicalRelationship("contact").Should().BeNull();
        Store.GetHierarchicalRelationship("no_such_table").Should().BeNull();

        var organization = Store.GetEntity("organization")!;
        organization.Attributes.Single(a => a.LogicalName == Enabled).Should().BeOfType<BooleanAttributeMetadata>();
        organization.Attributes.Single(a => a.LogicalName == Depth).Should().BeOfType<IntegerAttributeMetadata>();
        organization.Attributes.Single(a => a.LogicalName == Position).Should().BeOfType<BooleanAttributeMetadata>();
    }

    [Fact]
    public async Task Older_organization_is_upgraded()
    {
        var database = "xrmd_hier_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(factory).MigrateAsync();
        var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        async Task<MetadataStore> NewStoreAsync()
        {
            var store = new MetadataStore(factory);
            await store.LoadAsync();
            return store;
        }
        async Task RunAsync(string sql)
        {
            await using var conn = await factory.OpenAsync();
            await using var cmd = TestSql.Command(conn, sql);
            await cmd.ExecuteNonQueryAsync();
        }

        await new SystemSeed(await NewStoreAsync(), factory).EnsureAsync(options);
        var id = (await NewStoreAsync()).GetRelationship("account_parent_account")!.MetadataId;

        // An organization seeded before this plan: account_parent_account not hierarchical, the settings empty.
        await RunAsync("""
            UPDATE meta.relationship SET definition = JSON_MODIFY(definition, '$.IsHierarchical', CAST(0 AS bit))
                WHERE schema_name = 'account_parent_account';
            UPDATE data.organization SET ishierarchicalsecuritymodelenabled = NULL, maxdepthforhierarchicalsecuritymodel = NULL,
                usepositionhierarchy = NULL;
            """);
        var stale = await NewStoreAsync();
        ((OneToManyRelationshipMetadata)stale.GetRelationship("account_parent_account")!).IsHierarchical.Should().NotBe(true);
        stale.GetHierarchicalRelationship("account").Should().BeNull();

        await new SystemSeed(stale, factory).EnsureAsync(options);

        foreach (var store in new[] { stale, await NewStoreAsync() })
        {
            var upgraded = store.GetHierarchicalRelationship("account");
            upgraded.Should().NotBeNull();
            upgraded!.SchemaName.Should().Be("account_parent_account");
            upgraded.MetadataId.Should().Be(id!.Value, "the row is kept");
            store.GetHierarchicalRelationship("systemuser")!.SchemaName.Should().Be("user_parent_user");
        }

        await using (var conn = await factory.OpenAsync())
        {
            await using var cmd = TestSql.Command(conn, $"SELECT {Enabled}, {Depth}, {Position} FROM data.organization");
            await using var rd = await cmd.ExecuteReaderAsync();
            (await rd.ReadAsync()).Should().BeTrue();
            (rd.GetBoolean(0), rd.GetInt32(1), rd.GetBoolean(2)).Should().Be((false, 3, false));
        }

        // A value set by an administrator is kept.
        await RunAsync($"UPDATE data.organization SET {Depth} = 7");
        await new SystemSeed(await NewStoreAsync(), factory).EnsureAsync(options);
        await using (var conn = await factory.OpenAsync())
        {
            await using var cmd = TestSql.Command(conn, $"SELECT {Depth} FROM data.organization");
            (await cmd.ExecuteScalarAsync()).Should().Be(7);
        }
    }

    [Fact]
    public async Task Hierarchical_relationship_rules()
    {
        var table = "new_hier" + Guid.NewGuid().ToString("N")[..8];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        try
        {
            Store.GetHierarchicalRelationship(table).Should().BeNull();

            // Between two different tables.
            (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
            {
                OneToManyRelationship = new OneToManyRelationshipMetadata
                {
                    SchemaName = $"new_{table}_acc", ReferencedEntity = "account", ReferencingEntity = table, IsHierarchical = true,
                },
                Lookup = new LookupAttributeMetadata { LogicalName = "new_accountid", SchemaName = "new_AccountId" },
            }))).Should().Contain("hierarchical");
            Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_accountid");

            // The first self-referencing one.
            await Admin.ExecuteAsync(new CreateOneToManyRequest
            {
                OneToManyRelationship = new OneToManyRelationshipMetadata
                {
                    SchemaName = $"new_{table}_parent", ReferencedEntity = table, ReferencingEntity = table, IsHierarchical = true,
                },
                Lookup = new LookupAttributeMetadata { LogicalName = "new_parentid", SchemaName = "new_ParentId" },
            });
            var hierarchical = Store.GetHierarchicalRelationship(table);
            hierarchical.Should().NotBeNull();
            (hierarchical!.SchemaName, hierarchical.ReferencingAttribute).Should().Be(($"new_{table}_parent", "new_parentid"));

            // A second one on the same table.
            (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
            {
                OneToManyRelationship = new OneToManyRelationshipMetadata
                {
                    SchemaName = $"new_{table}_other", ReferencedEntity = table, ReferencingEntity = table, IsHierarchical = true,
                },
                Lookup = new LookupAttributeMetadata { LogicalName = "new_otherid", SchemaName = "new_OtherId" },
            }))).Should().Contain($"new_{table}_parent");
            Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_otherid");

            // A second self-referencing relationship that is not hierarchical is fine.
            await Admin.ExecuteAsync(new CreateOneToManyRequest
            {
                OneToManyRelationship = new OneToManyRelationshipMetadata
                {
                    SchemaName = $"new_{table}_plain", ReferencedEntity = table, ReferencingEntity = table,
                },
                Lookup = new LookupAttributeMetadata { LogicalName = "new_plainid", SchemaName = "new_PlainId" },
            });
            Store.GetHierarchicalRelationship(table)!.SchemaName.Should().Be($"new_{table}_parent");
        }
        finally
        {
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, table, default));
        }
    }

    [Fact]
    public async Task Manager_loops_are_refused()
    {
        var root = await RootBusinessUnitAsync(fx);
        var a = await CreateUserAsync(fx, root);
        var b = await CreateUserAsync(fx, root);
        var c = await CreateUserAsync(fx, root);
        EntityReference User(Guid id) => new("systemuser", id);

        (await FaultAsync(() => Admin.UpdateAsync(new Entity("systemuser", a) { [Manager] = User(a) }), 400))
            .Message.Should().Contain("manager");

        await Admin.UpdateAsync(new Entity("systemuser", a) { [Manager] = User(b) });
        await Admin.UpdateAsync(new Entity("systemuser", b) { [Manager] = User(c) });
        await FaultAsync(() => Admin.UpdateAsync(new Entity("systemuser", b) { [Manager] = User(a) }), 400);
        await FaultAsync(() => Admin.UpdateAsync(new Entity("systemuser", c) { [Manager] = User(a) }), 400);
        (await ScalarAsync($"SELECT {Manager} FROM data.systemuser WHERE systemuserid = '{c}'")).Should().BeNull();

        // Setting the same manager again, clearing it, and closing the old chain once it is open.
        await Admin.UpdateAsync(new Entity("systemuser", a) { [Manager] = User(b) });
        await Admin.UpdateAsync(new Entity("systemuser", a) { [Manager] = null });
        (await ScalarAsync($"SELECT {Manager} FROM data.systemuser WHERE systemuserid = '{a}'")).Should().BeNull();
        await Admin.UpdateAsync(new Entity("systemuser", c) { [Manager] = User(a) });

        // A new user cannot be their own manager. Through a request the security guard answers 404 first (the lookup's record
        // does not exist yet); the core operation's own 400 holds for the callers the guard does not check.
        var d = Guid.NewGuid();
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await FaultAsync(() => TestEntities.InSessionAsync(fx.ConnectionString, s => core.CreateAsync(s, Caller.Internal, new Entity("systemuser", d)
        {
            ["fullname"] = "Dana Example", ["domainname"] = "dana@example.com", ["businessunitid"] = new EntityReference("businessunit", root),
            [Manager] = User(d),
        }, default)), 400);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.systemuser WHERE systemuserid = '{d}'")).Should().Be(0);
    }

    [Fact]
    public async Task Organization_settings_rules()
    {
        var org = await OrganizationIdAsync();
        var root = await RootBusinessUnitAsync(fx);
        var customizerOid = Guid.NewGuid();
        var customizerId = await CreateUserAsync(fx, root, customizerOid);
        await AssignRoleAsync(fx, customizerId, StandardRoles.SystemCustomizer);
        // Write on organization, so that only the hierarchy rule can refuse (a repeated value then passes).
        await GrantAsync(fx, customizerId, team: false, ("prvWriteOrganization", DepthMask.Global));
        var customizer = ServiceFor(fx, customizerOid);
        await ExecuteAsync($"UPDATE data.organization SET {Enabled} = {TestSql.False}, {Depth} = 3, {Position} = {TestSql.False}");
        Cache(fx).Invalidate();
        try
        {
            foreach (var (column, value) in new (string, object)[] { (Enabled, true), (Depth, 5), (Position, true) })
                (await FaultAsync(() => customizer.UpdateAsync(new Entity("organization", org) { [column] = value }), 403))
                    .Message.Should().Contain("System Administrator");
            // One changed value among repeated ones is still a change.
            (await FaultAsync(() => customizer.UpdateAsync(new Entity("organization", org) { [Enabled] = false, [Depth] = 4 }), 403))
                .Message.Should().Contain("System Administrator");
            // Repeating the stored values is not a change (Retrieve-all-columns-then-Update by another user).
            await customizer.UpdateAsync(new Entity("organization", org) { [Enabled] = false, [Depth] = 3, [Position] = false });

            await Admin.UpdateAsync(new Entity("organization", org) { [Enabled] = true });
            (await ScalarAsync($"SELECT {Enabled} FROM data.organization")).Should().Be(true);

            foreach (var depth in new object?[] { 0, 101, -1, null })
                (await FaultAsync(() => Admin.UpdateAsync(new Entity("organization", org) { [Depth] = depth }), 400))
                    .Message.Should().Contain(Depth);
            await Admin.UpdateAsync(new Entity("organization", org) { [Depth] = 1 });
            await Admin.UpdateAsync(new Entity("organization", org) { [Depth] = 100 });
            (await ScalarAsync($"SELECT {Depth} FROM data.organization")).Should().Be(100);

            (await FaultAsync(() => Admin.UpdateAsync(new Entity("organization", org) { [Position] = true }), 400))
                .Message.Should().Contain("position hierarchy");
            await Admin.UpdateAsync(new Entity("organization", org) { [Position] = false });

            // Other columns of organization stay writable by whoever could write them before.
            await Admin.UpdateAsync(new Entity("organization", org) { ["name"] = XrmdFixture.OrgName });
        }
        finally
        {
            await ExecuteAsync($"UPDATE data.organization SET {Enabled} = {TestSql.False}, {Depth} = 3, {Position} = {TestSql.False}");
            Cache(fx).Invalidate();
        }
    }

    [Fact]
    public async Task Import_rejects_two_hierarchical_relationships()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        try
        {
            var stamp = Store.VersionStamp;
            var zip = SolutionFixtureZip.Build(c =>
            {
                var lookup = c.Root!.Descendants("attribute")
                    .Single(a => (string?)a.Attribute("PhysicalName") == "xv_ProjectId" && a.Element("Type")!.Value == "lookup");
                var relationship = c.Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
                    .Single(r => (string?)r.Attribute("Name") == "xv_project_xv_task_ProjectId");
                foreach (var n in new[] { "One", "Two" })
                {
                    var column = new XElement(lookup);
                    column.SetAttributeValue("PhysicalName", $"xv_Parent{n}Id");
                    column.Element("Name")!.Value = column.Element("LogicalName")!.Value = $"xv_parent{n.ToLowerInvariant()}id";
                    lookup.AddAfterSelf(column);

                    var r = new XElement(relationship);
                    r.SetAttributeValue("Name", $"xv_task_xv_task_Parent{n}");
                    r.Element("ReferencedEntityName")!.Value = "xv_task";
                    r.Element("IsHierarchical")!.Value = "1";
                    r.Element("ReferencingAttributeName")!.Value = $"xv_Parent{n}Id";
                    r.Element("RelationshipDescription")?.Remove();
                    var roles = r.Element("EntityRelationshipRoles")!.Elements("EntityRelationshipRole").ToList();
                    roles[0].Element("NavigationPropertyName")!.Value = $"xv_Parent{n}Id";
                    roles[1].Element("NavigationPropertyName")!.Value = $"xv_task_xv_task_Parent{n}";
                    relationship.AddAfterSelf(r);
                }
            });

            var fault = await FaultAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() }), 400);
            fault.Message.Should().Contain("xv_task_xv_task_ParentOne").And.Contain("xv_task_xv_task_ParentTwo");
            Store.GetEntity("xv_task").Should().BeNull();
            Store.VersionStamp.Should().Be(stamp, "the import fails before any write");
        }
        finally
        {
            await SolutionImportTests.ClearImportAsync(fx);
        }
    }
}
