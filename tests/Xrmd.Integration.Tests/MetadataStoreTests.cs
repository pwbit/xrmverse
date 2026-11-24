using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Each test gets its own freshly migrated database on the shared container, so metadata created here
/// (and the version stamp it bumps) never leaks into other tests of the "xrmd" collection.
/// </summary>
[Collection("xrmd")]
public sealed class MetadataStoreTests(XrmdFixture fx) : IAsyncLifetime
{
    private IDbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_meta_" + Guid.NewGuid().ToString("N");
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

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    /// <summary>
    /// A bare store's stand-in for the seeded <c>transactioncurrency</c> table (system ObjectTypeCode 9105), which a money column needs
    /// for its currency pair (plan 2.10, X4). Takes version stamp 1.
    /// </summary>
    private static Task CurrencyTableAsync(MetadataStore store)
    {
        var currency = new EntityMetadata { LogicalName = CurrencySchema.Table, SchemaName = "TransactionCurrency", OwnershipType = OwnershipTypes.OrganizationOwned };
        SetServerOwned(currency, nameof(EntityMetadata.ObjectTypeCode), 9105);
        return store.CreateEntityAsync(currency, new StringAttributeMetadata { LogicalName = "currencyname", MaxLength = 100 });
    }

    private static EntityMetadata Project(string logicalName = "pb_project") => new()
    {
        LogicalName = logicalName,
        SchemaName = logicalName,
        OwnershipType = OwnershipTypes.UserOwned,
        DisplayName = SystemAttributes.CreateLabel("Project", 1033),
    };

    private static StringAttributeMetadata PrimaryName(string logicalName = "pb_name") =>
        new() { LogicalName = logicalName, MaxLength = 200 };

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<List<string>> ColumnsAsync(string table)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = @t");
        cmd.AddParameter("@t", table);
        var columns = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) columns.Add(rd.GetString(0));
        return columns;
    }

    [Fact]
    public async Task CreateEntityAsync_persists_and_creates_table()
    {
        var store = await NewStoreAsync();
        await CurrencyTableAsync(store);
        var before = store.VersionStamp;
        var entity = Project();
        entity.SetAttributes([new MoneyAttributeMetadata { LogicalName = "pb_budget" }]);

        await store.CreateEntityAsync(entity, PrimaryName());

        var created = store.GetEntity("pb_project");
        created.Should().NotBeNull();
        store.VersionStamp.Should().Be(before + 1);
        (await ScalarAsync("SELECT [current] FROM meta.version_stamp")).Should().Be(before + 1);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity WHERE logical_name = 'pb_project'")).Should().Be(1);
        (await TestSql.HoldsAsync(TestProvider.WithDatabase(fx.ConnectionString, _database!), TestSql.Dialect.TableExists("data", "pb_project"))).Should().BeTrue();
        (await ColumnsAsync("pb_project")).Should().Contain(
            ["pb_projectid", "pb_name", "pb_budget", "pb_budget_base", "ownerid", "owneridtype", "createdon", "versionnumber"]);

        created!.MetadataId.Should().NotBeNull().And.NotBe(Guid.Empty);
        created.EntitySetName.Should().Be("pb_projects");
        created.ObjectTypeCode.Should().Be(10000);
        created.PrimaryIdAttribute.Should().Be("pb_projectid");
        created.PrimaryNameAttribute.Should().Be("pb_name");
        created.Attributes.Should().Contain(a => a.LogicalName == "pb_projectid" && a.IsPrimaryId == true);
        created.Attributes.Should().Contain(a => a.LogicalName == "pb_name" && a.IsPrimaryName == true);
        created.Attributes.Should().OnlyContain(a => a.EntityLogicalName == "pb_project" && a.MetadataId != null);
        store.GetEntityBySetName("pb_projects").Should().BeSameAs(created);
        store.GetAllEntities().Select(e => e.LogicalName).Should().BeEquivalentTo("pb_project", CurrencySchema.Table);
    }

    /// <summary>Review M1: a money column needs the system table <c>transactioncurrency</c> for its currency pair (400 otherwise).</summary>
    [Fact]
    public async Task Money_without_transactioncurrency_is_refused_on_create_and_add()
    {
        var store = await NewStoreAsync();
        var entity = Project();
        entity.SetAttributes([new MoneyAttributeMetadata { LogicalName = "pb_budget" }]);

        await FluentActions.Awaiting(() => store.CreateEntityAsync(entity, PrimaryName()))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*Money columns need the system table 'transactioncurrency'*");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(0);

        await store.CreateEntityAsync(Project(), PrimaryName());
        await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_project", new MoneyAttributeMetadata { LogicalName = "pb_cost" }))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*Money columns need the system table 'transactioncurrency'*");
        store.VersionStamp.Should().Be(1);
        (await ColumnsAsync("pb_project")).Should().NotContain("pb_cost");
    }

    [Fact]
    public async Task CreateEntityAsync_with_invalid_name_throws_and_writes_nothing()
    {
        var store = await NewStoreAsync();

        var act = () => store.CreateEntityAsync(Project("PB_Project"), PrimaryName());

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*PB_Project*");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(0);
        (await ScalarAsync("SELECT [current] FROM meta.version_stamp")).Should().Be(0);
        store.GetEntity("PB_Project").Should().BeNull();
    }

    [Fact]
    public async Task CreateEntityAsync_with_invalid_attribute_name_throws_and_writes_nothing()
    {
        var store = await NewStoreAsync();

        var act = () => store.CreateEntityAsync(Project(), PrimaryName("pb Name"));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*pb Name*");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.attribute")).Should().Be(0);
    }

    [Fact]
    public async Task CreateEntityAsync_twice_for_same_name_throws()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Project(), PrimaryName());

        var act = () => store.CreateEntityAsync(Project(), PrimaryName());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pb_project*");
        store.VersionStamp.Should().Be(1);
    }

    [Fact]
    public async Task LoadAsync_restores_entities_with_typed_attributes()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Project(), PrimaryName());
        await store.CreateEntityAsync(Project("pb_task"), PrimaryName());

        var reloaded = await NewStoreAsync();

        reloaded.VersionStamp.Should().Be(2);
        reloaded.GetAllEntities().Select(e => e.LogicalName).Should().BeEquivalentTo("pb_project", "pb_task");
        reloaded.GetEntity("pb_task")!.ObjectTypeCode.Should().Be(10001);
        var project = reloaded.GetEntity("pb_project")!;
        project.Should().BeEquivalentTo(store.GetEntity("pb_project")!, o => o.RespectingRuntimeTypes()
            .Excluding(e => e.Attributes)); // attribute order is not persisted
        project.Attributes.Should().BeEquivalentTo(store.GetEntity("pb_project")!.Attributes, o => o.RespectingRuntimeTypes());
        project.Attributes.Single(a => a.LogicalName == "ownerid").AttributeType.Should().Be(AttributeTypeCode.Owner);
        project.Attributes.Single(a => a.LogicalName == "statuscode").Should().BeOfType<StatusAttributeMetadata>();
        reloaded.GetEntityBySetName("pb_tasks").Should().NotBeNull();
    }

    [Fact]
    public async Task AddAttributeAsync_adds_columns_metadata_and_bumps_version()
    {
        var store = await NewStoreAsync();
        await CurrencyTableAsync(store);
        await store.CreateEntityAsync(Project(), PrimaryName());

        await store.AddAttributeAsync("pb_project", new MoneyAttributeMetadata { LogicalName = "pb_cost" });

        store.VersionStamp.Should().Be(3);
        (await ColumnsAsync("pb_project")).Should().Contain(["pb_cost", "pb_cost_base"]);
        store.GetEntity("pb_project")!.Attributes.Select(a => a.LogicalName).Should().Contain(["pb_cost", "pb_cost_base"]);
        store.GetEntityBySetName("pb_projects")!.Attributes.Should().Contain(a => a.LogicalName == "pb_cost");
        (await NewStoreAsync()).GetEntity("pb_project")!.Attributes
            .Should().ContainSingle(a => a.LogicalName == "pb_cost").Which.Should().BeOfType<MoneyAttributeMetadata>();
    }

    [Fact]
    public async Task AddAttributeAsync_rejects_invalid_duplicate_or_unknown_entity_without_writing()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Project(), PrimaryName());

        await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_project", PrimaryName("Bad-Name")))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Bad-Name*");
        await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_project", PrimaryName("pb_name")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*pb_name*");
        await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_missing", PrimaryName("pb_code")))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*pb_missing*");

        store.VersionStamp.Should().Be(1);
        (await ScalarAsync("SELECT [current] FROM meta.version_stamp")).Should().Be(1);
    }

    [Fact]
    public async Task AddAttributeAsync_rolls_back_metadata_when_ddl_fails()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Project(), PrimaryName());
        await using (var conn = await _factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, "ALTER TABLE data.pb_project ADD pb_code int NULL"))
            await cmd.ExecuteNonQueryAsync(); // column exists but metadata does not: the DDL will fail

        await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_project", PrimaryName("pb_code")))
            .Should().ThrowAsync<System.Data.Common.DbException>();

        (await ScalarAsync("SELECT COUNT(*) FROM meta.attribute WHERE logical_name = 'pb_code'")).Should().Be(0);
        (await ScalarAsync("SELECT [current] FROM meta.version_stamp")).Should().Be(1);
        store.VersionStamp.Should().Be(1);
        store.GetEntity("pb_project")!.Attributes.Should().NotContain(a => a.LogicalName == "pb_code");
    }
    private static void SetServerOwned(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static StringAttributeMetadata StoredBy(string attributeOf)
    {
        var a = new StringAttributeMetadata { LogicalName = "pb_alias", MaxLength = 10 };
        SetServerOwned(a, nameof(AttributeMetadata.AttributeOf), attributeOf);
        return a;
    }

    [Fact]
    public async Task CreateEntityAsync_ignores_caller_PrimaryIdAttribute()
    {
        var store = await NewStoreAsync();
        var entity = Project();
        SetServerOwned(entity, nameof(EntityMetadata.PrimaryIdAttribute), "createdon");

        await store.CreateEntityAsync(entity, PrimaryName());

        var created = store.GetEntity("pb_project")!;
        created.PrimaryIdAttribute.Should().Be("pb_projectid");
        created.Attributes.Single(a => a.LogicalName == "pb_projectid").IsPrimaryId.Should().BeTrue();
        created.Attributes.Single(a => a.LogicalName == "createdon").Should().BeOfType<DateTimeAttributeMetadata>()
            .Which.IsPrimaryId.Should().NotBe(true);
    }

    public static TheoryData<string> SystemAttributeNames() =>
    [
        "createdon", "createdby", "modifiedon", "modifiedby", "owninguser", "owningteam", "owningbusinessunit",
        "statecode", "statuscode", "versionnumber", "ownerid", "owneridtype", "pb_projectid",
    ];

    [Theory]
    [MemberData(nameof(SystemAttributeNames))]
    public async Task CreateEntityAsync_rejects_attributes_named_like_system_attributes(string name)
    {
        var store = await NewStoreAsync();
        var entity = Project();
        entity.SetAttributes([new LookupAttributeMetadata { LogicalName = name, Targets = ["pb_other"] }]);

        await FluentActions.Awaiting(() => store.CreateEntityAsync(entity, PrimaryName()))
            .Should().ThrowAsync<ArgumentException>().WithMessage($"*{name}*");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(0);
    }

    [Fact]
    public async Task CreateEntityAsync_rejects_base_companions_and_attributes_stored_by_others()
    {
        var store = await NewStoreAsync();
        var baseCurrency = new MoneyAttributeMetadata { LogicalName = "pb_fee" };
        SetServerOwned(baseCurrency, nameof(MoneyAttributeMetadata.IsBaseCurrency), true);
        AttributeMetadata[][] cases =
        [
            [new MoneyAttributeMetadata { LogicalName = "pb_budget" }, new MoneyAttributeMetadata { LogicalName = "pb_budget_base" }],
            [baseCurrency],
            [StoredBy("pb_name")],
        ];

        foreach (var attributes in cases)
        {
            var entity = Project();
            entity.SetAttributes(attributes);
            await FluentActions.Awaiting(() => store.CreateEntityAsync(entity, PrimaryName()))
                .Should().ThrowAsync<ArgumentException>();
        }
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(SystemAttributeNames))]
    public async Task AddAttributeAsync_rejects_attributes_named_like_system_attributes(string name)
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Project(), PrimaryName());

        await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_project", new LookupAttributeMetadata { LogicalName = name, Targets = ["systemuser"] }))
            .Should().ThrowAsync<ArgumentException>().WithMessage($"*{name}*");
        store.VersionStamp.Should().Be(1);
    }

    [Fact]
    public async Task AddAttributeAsync_rejects_base_companions_and_attributes_stored_by_others()
    {
        var store = await NewStoreAsync();
        await CurrencyTableAsync(store);
        var entity = Project();
        entity.SetAttributes([new MoneyAttributeMetadata { LogicalName = "pb_budget" }]);
        await store.CreateEntityAsync(entity, PrimaryName());
        var baseCurrency = new MoneyAttributeMetadata { LogicalName = "pb_fee" };
        SetServerOwned(baseCurrency, nameof(MoneyAttributeMetadata.IsBaseCurrency), true);

        foreach (var a in new AttributeMetadata[]
                 {
                     new MoneyAttributeMetadata { LogicalName = "pb_budget_base" },
                     baseCurrency,
                     StoredBy("pb_name"),
                 })
        {
            await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_project", a)).Should().ThrowAsync<ArgumentException>();
        }
        store.VersionStamp.Should().Be(2);
    }

    [Fact]
    public async Task CreateEntityAsync_rejects_invalid_EntitySetName()
    {
        var store = await NewStoreAsync();
        var entity = Project();
        entity.EntitySetName = "Bad Set]";

        await FluentActions.Awaiting(() => store.CreateEntityAsync(entity, PrimaryName()))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Bad Set]*");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(0);
    }

    [Fact]
    public async Task String_attributes_without_MaxLength_get_100()
    {
        var store = await NewStoreAsync();
        var entity = Project();
        entity.SetAttributes([new StringAttributeMetadata { LogicalName = "pb_code" }]);

        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "pb_name" });
        await store.AddAttributeAsync("pb_project", new StringAttributeMetadata { LogicalName = "pb_tag" });

        var attributes = store.GetEntity("pb_project")!.Attributes.OfType<StringAttributeMetadata>().ToList();
        attributes.Select(a => a.LogicalName).Should().BeEquivalentTo("pb_name", "pb_code", "pb_tag");
        attributes.Should().OnlyContain(a => a.MaxLength == 100);
        (await NewStoreAsync()).GetEntity("pb_project")!.Attributes.OfType<StringAttributeMetadata>()
            .Should().OnlyContain(a => a.MaxLength == 100);
    }

    public static TheoryData<AttributeMetadata> InvalidAttributeDefinitions() =>
    [
        new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 0 },
        new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = -1 },
        new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 4001 },
        new DecimalAttributeMetadata { LogicalName = "pb_rate", Precision = -1 },
        new DecimalAttributeMetadata { LogicalName = "pb_rate", Precision = 11 },
        new LookupAttributeMetadata { LogicalName = "pb_ref", Targets = ["systemuser", "systemuser"] },
        new LookupAttributeMetadata { LogicalName = "pb_ref", Targets = ["systemuser", "Not-A-Name"] },
        new LookupAttributeMetadata { LogicalName = "pb_ref", Targets = [] },
        new LookupAttributeMetadata { LogicalName = "pb_ref" },
    ];

    [Theory]
    [MemberData(nameof(InvalidAttributeDefinitions))]
    public async Task Invalid_attribute_definitions_are_rejected_on_create_and_add(AttributeMetadata attribute)
    {
        var store = await NewStoreAsync();
        var entity = Project("pb_task");
        entity.SetAttributes([attribute]);
        await FluentActions.Awaiting(() => store.CreateEntityAsync(entity, PrimaryName()))
            .Should().ThrowAsync<ArgumentException>().WithMessage($"*{attribute.LogicalName}*");

        await store.CreateEntityAsync(Project(), PrimaryName());
        await FluentActions.Awaiting(() => store.AddAttributeAsync("pb_project", attribute))
            .Should().ThrowAsync<ArgumentException>().WithMessage($"*{attribute.LogicalName}*");
        store.VersionStamp.Should().Be(1);
    }

    [Fact]
    public async Task Valid_limits_are_accepted()
    {
        var store = await NewStoreAsync();
        var entity = Project();
        entity.SetAttributes(
        [
            new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 4000 },
            new DecimalAttributeMetadata { LogicalName = "pb_rate", Precision = 10 },
            new DecimalAttributeMetadata { LogicalName = "pb_whole", Precision = 0 },
        ]);

        await store.CreateEntityAsync(entity, PrimaryName());

        store.GetEntity("pb_project").Should().NotBeNull();
    }

    [Fact]
    public async Task GetEntityByObjectTypeCode_finds_loaded_and_created_entities()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Project(), PrimaryName());

        store.GetEntityByObjectTypeCode(10000)!.LogicalName.Should().Be("pb_project");
        store.GetEntityByObjectTypeCode(10001).Should().BeNull();
        (await NewStoreAsync()).GetEntityByObjectTypeCode(10000)!.LogicalName.Should().Be("pb_project");
        store.GetAllEntities().Select(e => e.LogicalName).Should().Equal("pb_project");
        store.GetAllEntities().Should().BeSameAs(store.GetAllEntities(), "the sorted list is built once per change, not per call");
    }
}
