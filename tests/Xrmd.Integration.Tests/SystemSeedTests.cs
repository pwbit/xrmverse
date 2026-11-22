using Xrmd.Abstractions;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class SystemSeedTests(XrmdFixture fx) : IAsyncLifetime
{
    private IDbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_seed_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);

        _factory = TestProvider.Factory(
            TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    private string? _database;

    public async Task DisposeAsync()
    {
        if (_database is not null) await fx.DropDatabaseAsync(_database); // the test's own database (CI runners have little free disk)
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    private static readonly (string Logical, string Old, string New)[] RenamedSets =
    [
        ("pluginassembly", "pluginassemblys", "pluginassemblies"),
        ("principalobjectaccess", "principalobjectaccesss", "principalobjectaccessset"),
        ("privilegeobjecttypecodes", "privilegeobjecttypecodess", "privilegeobjecttypecodesset"),
        ("roleprivileges", "roleprivilegess", "roleprivilegescollection"),
        ("systemuserroles", "systemuserroless", "systemuserrolescollection"),
        ("teamroles", "teamroless", "teamrolescollection"),
    ];

    [Fact]
    public async Task EnsureAsync_upgrades_an_org_seeded_with_the_old_entity_set_names()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        foreach (var (logical, old, current) in RenamedSets)
        {
            (await NewStoreAsync()).GetEntity(logical)!.EntitySetName.Should().Be(current);
            await using var conn = await _factory.OpenAsync();
            await using var cmd = TestSql.Command(conn, "UPDATE meta.entity SET definition = REPLACE(definition, @new, @old) WHERE logical_name = @n");
            cmd.AddParameter("@new", current);
            cmd.AddParameter("@old", old);
            cmd.AddParameter("@n", logical);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var stale = await NewStoreAsync();
        foreach (var (logical, old, _) in RenamedSets) stale.GetEntity(logical)!.EntitySetName.Should().Be(old);

        await new SystemSeed(stale, _factory).EnsureAsync(Options);
        foreach (var (logical, old, current) in RenamedSets)
        {
            stale.GetEntity(logical)!.EntitySetName.Should().Be(current);
            stale.GetEntityBySetName(current).Should().NotBeNull();
            stale.GetEntityBySetName(old).Should().BeNull();
        }

        var reloaded = await NewStoreAsync();
        foreach (var (logical, _, current) in RenamedSets) reloaded.GetEntity(logical)!.EntitySetName.Should().Be(current);
    }

    [Fact]
    public async Task Existing_database_gets_fileattachment_on_upgrade()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        // An organization seeded before fileattachment existed: no table, no metadata.
        await ExecuteAsync("DELETE FROM meta.attribute WHERE entity_logical_name = 'fileattachment'; " +
                           "DELETE FROM meta.entity WHERE logical_name = 'fileattachment'; DROP TABLE data.fileattachment;");
        var stale = await NewStoreAsync();
        stale.GetEntity("fileattachment").Should().BeNull();

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        foreach (var store in new[] { stale, await NewStoreAsync() })
        {
            var entity = store.GetEntity("fileattachment")!;
            entity.EntitySetName.Should().Be("fileattachments");
            entity.OwnershipType.Should().Be(Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.None);
            ((LookupAttributeMetadata)entity.Attributes.Single(a => a.LogicalName == "objectid")).Targets.Should().BeEmpty();
        }
        (await ScalarAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'data' AND table_name = 'fileattachment' AND column_name IN ('objectid', 'objectidtype', 'objecttypecode', 'filename', 'filesizeinbytes')"))
            .Should().Be(5);
    }

    [Fact]
    public async Task Seed_upgrade_keeps_the_targets_and_relationships_of_file_columns()
    {
        var store = await NewStoreAsync();
        await new SystemSeed(store, _factory).EnsureAsync(Options);
        var table = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = "new_withfile", SchemaName = "new_withfile" };
        table.SetAttributes([]);
        await store.CreateEntityAsync(table, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await store.AddAttributeAsync("new_withfile", new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc" });

        string[] Targets(MetadataStore s) => ((LookupAttributeMetadata)s.GetEntity("fileattachment")!.Attributes.Single(a => a.LogicalName == "objectid")).Targets;
        Targets(store).Should().Equal("new_withfile");

        // Run on the live store and on a store loaded from the database: neither resets the persisted targets.
        await new SystemSeed(store, _factory).EnsureAsync(Options);
        var reloaded = await NewStoreAsync();
        await new SystemSeed(reloaded, _factory).EnsureAsync(Options);

        foreach (var s in new[] { store, reloaded, await NewStoreAsync() })
        {
            Targets(s).Should().Equal("new_withfile");
            s.GetRelationship("new_withfile_FileAttachments").Should().BeOfType<Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata>()
                .Which.Should().Match<Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata>(r =>
                    r.ReferencedEntity == "new_withfile" && r.ReferencingEntity == "fileattachment" && r.ReferencingAttribute == "objectid");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task EnsureAsync_twice_creates_single_org_bu_and_admin()
    {
        var oid = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var options = new SeedOptions("Acme", rootId, oid, "Ada Admin");
        var store = await NewStoreAsync();

        await new SystemSeed(store, _factory).EnsureAsync(options);
        await new SystemSeed(store, _factory).EnsureAsync(options);

        (await ScalarAsync("SELECT COUNT(*) FROM data.organization")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.businessunit")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.systemuser")).Should().Be(2); // admin + SYSTEM
        (await ScalarAsync($"SELECT COUNT(*) FROM data.systemuser WHERE azureactivedirectoryobjectid = '{oid}' AND businessunitid = '{rootId}' AND fullname = 'Ada Admin'")).Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.businessunit WHERE businessunitid = '{rootId}' AND name = 'Acme' AND parentbusinessunitid IS NULL")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.organization WHERE name = 'Acme'")).Should().Be(1);
    }

    [Fact]
    public async Task EnsureAsync_creates_system_entities_with_standard_codes_and_no_ownerid()
    {
        var store = await NewStoreAsync();

        await new SystemSeed(store, _factory).EnsureAsync(new SeedOptions("Acme", Guid.NewGuid(), Guid.NewGuid(), "Ada"));

        var reloaded = await NewStoreAsync();
        reloaded.GetEntity("organization")!.ObjectTypeCode.Should().Be(1019);
        reloaded.GetEntity("businessunit")!.ObjectTypeCode.Should().Be(10);
        reloaded.GetEntity("systemuser")!.ObjectTypeCode.Should().Be(8);
        reloaded.GetEntityBySetName("systemusers")!.PrimaryNameAttribute.Should().Be("fullname");
        reloaded.GetEntity("organization")!.PrimaryNameAttribute.Should().Be("name");
        reloaded.GetEntity("businessunit")!.PrimaryNameAttribute.Should().Be("name");
        foreach (var name in new[] { "organization", "businessunit", "systemuser" })
            reloaded.GetEntity(name)!.Attributes.Should().NotContain(a => a.LogicalName == "ownerid");
    }

    [Fact]
    public async Task EnsureAsync_after_restart_reuses_existing_root_and_admin()
    {
        var oid = Guid.NewGuid();
        var firstRoot = Guid.NewGuid();
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(new SeedOptions("Acme", firstRoot, oid, "Ada"));

        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(new SeedOptions("Acme", Guid.NewGuid(), oid, "Ada"));

        (await ScalarAsync("SELECT COUNT(*) FROM data.businessunit")).Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.businessunit WHERE businessunitid = '{firstRoot}'")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.systemuser")).Should().Be(2); // admin + SYSTEM
    }

    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");

    [Fact]
    public async Task Seed_creates_pipeline_tables()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        var store = await NewStoreAsync();
        var expected = new Dictionary<string, int>
        {
            ["pluginassembly"] = 4605, ["plugintype"] = 4602, ["sdkmessage"] = 4606, ["sdkmessagefilter"] = 4607,
            ["sdkmessageprocessingstep"] = 4608, ["sdkmessageprocessingstepimage"] = 4615, ["asyncoperation"] = 4700,
            ["plugintracelog"] = 4619,
        };
        foreach (var (name, code) in expected) store.GetEntity(name)!.ObjectTypeCode.Should().Be(code, name);
        store.GetEntity("asyncoperation")!.OwnershipType.Should().Be(Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned);
        store.GetEntity("sdkmessage")!.OwnershipType.Should().Be(Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.OrganizationOwned);

        static int[] Options_(Microsoft.Xrm.Sdk.Metadata.EntityMetadata e, string attr) =>
            ((Microsoft.Xrm.Sdk.Metadata.EnumAttributeMetadata)e.Attributes.Single(a => a.LogicalName == attr))
                .OptionSet.Options.Select(o => o.Value!.Value).ToArray();
        Options_(store.GetEntity("sdkmessageprocessingstep")!, "stage").Should().BeEquivalentTo(new[] { 10, 20, 30, 40, 50 });
        Options_(store.GetEntity("sdkmessageprocessingstep")!, "statuscode").Should().BeEquivalentTo(new[] { 1, 2 });
        Options_(store.GetEntity("asyncoperation")!, "statuscode").Should().BeEquivalentTo(new[] { 0, 10, 20, 21, 22, 30, 31, 32 });
        Options_(store.GetEntity("asyncoperation")!, "statecode").Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
    }

    [Fact]
    public async Task Seed_catalog_rows_all_have_owning_business_unit()
    {
        var catalog = new CatalogHolder();
        var store = new MetadataStore(_factory, [catalog]);
        catalog.Inner = new SdkMessageCatalog(_factory, store);
        await store.LoadAsync();

        await new SystemSeed(store, _factory).EnsureAsync(Options);
        await catalog.Inner.EnsureAsync(default);

        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage")).Should().BeGreaterThan(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter")).Should().BeGreaterThan(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE owningbusinessunit IS NULL")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE owningbusinessunit IS NULL")).Should().Be(0);
    }

    private sealed class CatalogHolder : Xrmd.Abstractions.IEntityCreatedHandler
    {
        public SdkMessageCatalog Inner { get; set; } = null!;
        public Task OnEntityCreatedAsync(Microsoft.Xrm.Sdk.Metadata.EntityMetadata e, Xrmd.Abstractions.DataSession session, CancellationToken ct) =>
            Inner.OnEntityCreatedAsync(e, session, ct);
    }

    [Fact]
    public async Task Seed_creates_SYSTEM_user()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        var id = SeedOptions.DeriveSystemUserId(Options.OrgName);
        (await ScalarAsync("SELECT COUNT(*) FROM data.systemuser WHERE fullname = 'SYSTEM'")).Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.systemuser WHERE systemuserid = '{id}' AND azureactivedirectoryobjectid IS NULL AND businessunitid IS NOT NULL")).Should().Be(1);
    }

    [Fact]
    public async Task Seed_sets_plugintracelogsetting_to_exception()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        (await ScalarAsync("SELECT plugintracelogsetting FROM data.organization")).Should().Be(1);
    }

    [Fact]
    public async Task Seed_adds_missing_attributes_to_existing_entities()
    {
        const string planTwoOrganization = """
            {
              "LogicalName": "organization", "SchemaName": "Organization", "EntitySetName": "organizations",
              "ObjectTypeCode": 1019, "OwnershipType": "BusinessOwned", "PrimaryNameAttribute": "name",
              "Attributes": [
                { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "name", "SchemaName": "Name", "MaxLength": 160 }
              ]
            }
            """;
        var store = await NewStoreAsync();
        var org = Xrmd.Metadata.MetadataSerializer.Deserialize(planTwoOrganization);
        var primary = org.Attributes.Single(a => a.LogicalName == "name");
        org.SetAttributes([]);
        await store.CreateEntityAsync(org, primary);

        await new SystemSeed(store, _factory).EnsureAsync(Options);

        store.GetEntity("organization")!.Attributes.Should().Contain(a => a.LogicalName == "plugintracelogsetting");
        (await ScalarAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'data' AND table_name = 'organization' AND column_name = 'plugintracelogsetting'")).Should().Be(1);
        (await ScalarAsync("SELECT plugintracelogsetting FROM data.organization")).Should().Be(1);
    }
}
