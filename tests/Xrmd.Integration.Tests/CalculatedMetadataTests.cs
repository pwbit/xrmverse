using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Solution;
using Xrmd.Tests.Shared;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Calculated column definitions in the metadata store (plan 2.12a, Task 1): no SQL column, the dependency check on delete, the
/// stored definition and mask, and the refusal of a store without a translator. A fake <see cref="IComputedColumns"/> stands in
/// for the XAML translator (Task 2). Each test has its own database.
/// </summary>
[Collection("xrmd")]
public sealed class CalculatedMetadataTests(XrmdFixture fx) : IAsyncLifetime
{
    private IDbConnectionFactory _factory = null!;
    private string _database = "";
    private string _connectionString = "";
    private readonly FakeComputedColumns _fake = new();

    public async Task InitializeAsync()
    {
        _database = "xrmd_calc_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database());
        _connectionString = TestProvider.WithDatabase(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(_connectionString);
        await new SchemaMigrator(_factory).MigrateAsync();

        string database() => _database;
    }

    public Task DisposeAsync() => fx.DropDatabaseAsync(_database);

    private async Task<MetadataStore> StoreAsync(IComputedColumns? computed)
    {
        var store = new MetadataStore(_factory, computed: computed);
        await store.LoadAsync();
        return store;
    }

    private static EntityMetadata Table(string name) => new()
    {
        LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel(name, 1033),
    };

    private Task InSessionAsync(Func<DataSession, Task> write) => TestEntities.InSessionAsync(_connectionString, write);

    private async Task<List<string>> ColumnsAsync(string table)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT column_name FROM information_schema.columns WHERE table_schema = 'data' AND table_name = CAST(@t AS varchar(300))");
        cmd.AddParameter("@t", table);
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    private async Task<string> ScalarTextAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static IntegerAttributeMetadata Calculated(string name)
    {
        var a = new IntegerAttributeMetadata { LogicalName = name, SchemaName = name, DisplayName = SystemAttributes.CreateLabel(name, 1033), SourceType = 1 };
        ComputedColumnRules.SetFormula(a, "<xaml/>");
        return a;
    }

    private static IntegerAttributeMetadata Plain(string name) =>
        new() { LogicalName = name, SchemaName = name, DisplayName = SystemAttributes.CreateLabel(name, 1033) };

    private async Task<MetadataStore> TablesAsync()
    {
        var store = await StoreAsync(_fake);
        await store.CreateEntityAsync(Table("xrmv_p"), new StringAttributeMetadata { LogicalName = "xrmv_pname", MaxLength = 100 });
        await store.CreateEntityAsync(Table("xrmv_t"), new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 });
        await store.AddAttributeAsync("xrmv_p", Plain("xrmv_pn"));
        await store.AddAttributeAsync("xrmv_t", Plain("xrmv_total"));
        await store.AddAttributeAsync("xrmv_t", new LookupAttributeMetadata
        {
            LogicalName = "xrmv_parentid", SchemaName = "xrmv_parentid", Targets = ["xrmv_p"], DisplayName = SystemAttributes.CreateLabel("Parent", 1033),
        });
        return store;
    }

    [Fact]
    public async Task CreateAttribute_calculated_adds_no_sql_column()
    {
        var store = await TablesAsync();
        _fake.With("xrmv_t", "xrmv_calc", new ComputedExpr.Column(null, "xrmv_total"));
        await store.AddAttributeAsync("xrmv_t", Calculated("xrmv_calc"));

        (await ColumnsAsync("xrmv_t")).Should().Contain("xrmv_total").And.NotContain("xrmv_calc");
        var stored = store.GetEntity("xrmv_t")!.Attributes.Single(a => a.LogicalName == "xrmv_calc");
        stored.SourceType.Should().Be(1);
        stored.IsValidForCreate.Should().BeFalse();
        stored.IsValidForUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task Create_entity_with_a_calculated_attribute_validates_it()
    {
        var store = await StoreAsync(_fake);
        var entity = Table("xrmv_c");
        entity.SetAttributes([Plain("xrmv_a"), Calculated("xrmv_b")]);
        _fake.With("xrmv_c", "xrmv_b", new ComputedExpr.Column(null, "xrmv_a"));
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "xrmv_cname", MaxLength = 100 });

        (await ColumnsAsync("xrmv_c")).Should().Contain("xrmv_a").And.NotContain("xrmv_b");
        ComputedColumnRules.MaskOf(store.GetEntity("xrmv_c")!.Attributes.Single(a => a.LogicalName == "xrmv_b")).Should().Be(1);
    }

    [Fact]
    public async Task Delete_of_referenced_column_is_refused()
    {
        var store = await TablesAsync();
        _fake.With("xrmv_t", "xrmv_calc", new ComputedExpr.Column(null, "xrmv_total"), new ComputedExpr.Column("xrmv_parentid", "xrmv_pn"));
        await store.AddAttributeAsync("xrmv_t", Calculated("xrmv_calc"));

        foreach (var (table, column) in new[] { ("xrmv_t", "xrmv_total"), ("xrmv_p", "xrmv_pn"), ("xrmv_t", "xrmv_parentid") })
        {
            var fault = await FluentActions.Awaiting(() => InSessionAsync(s => store.DeleteAttributeAsync(s, table, column, default)))
                .Should().ThrowAsync<XrmdFaultException>();
            fault.Which.Message.Should().Contain("xrmv_t.xrmv_calc");
            fault.Which.HttpStatus.Should().Be(400);
            fault.Which.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            store.GetEntity(table)!.Attributes.Should().Contain(a => a.LogicalName == column, "a refused delete drops nothing");
            (await ColumnsAsync(table)).Should().Contain(column);
        }

        // The parent table cannot go while a calculated column of another table reads it.
        var tableFault = await FluentActions.Awaiting(() => InSessionAsync(s => store.DeleteEntityAsync(s, "xrmv_p", default)))
            .Should().ThrowAsync<XrmdFaultException>();
        tableFault.Which.Message.Should().Contain("xrmv_t");

        // Deleting the calculated column frees its sources.
        await InSessionAsync(s => store.DeleteAttributeAsync(s, "xrmv_t", "xrmv_calc", default));
        await InSessionAsync(s => store.DeleteAttributeAsync(s, "xrmv_t", "xrmv_total", default));
        store.GetEntity("xrmv_t")!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_total");
    }

    [Fact]
    public async Task RetrieveAttribute_returns_FormulaDefinition_and_mask()
    {
        var store = await TablesAsync();
        _fake.With("xrmv_t", "xrmv_calc", new ComputedExpr.Column(null, "xrmv_total"), new ComputedExpr.Column("xrmv_parentid", "xrmv_pn"));
        var given = Calculated("xrmv_calc");
        ComputedColumnRules.SetFormula(given, "<xaml>text</xaml>");
        // A client-supplied mask is ignored (ServerOwnedProperties).
        ServerOwnedProperties.Clear(given);
        await store.AddAttributeAsync("xrmv_t", given);

        var reloaded = await StoreAsync(_fake);
        foreach (var source in new[] { store.GetEntity("xrmv_t")!, reloaded.GetEntity("xrmv_t")! })
        {
            var attribute = source.Attributes.Single(a => a.LogicalName == "xrmv_calc");
            ComputedColumnRules.FormulaOf(attribute).Should().Be("<xaml>text</xaml>");
            ComputedColumnRules.MaskOf(attribute).Should().Be(3);
        }

        // K12: simple columns read 0, a column type without the property reads null; the mask is never stored.
        foreach (var source in new[] { store.GetEntity("xrmv_t")!, reloaded.GetEntity("xrmv_t")! })
        {
            ComputedColumnRules.MaskOf(source.Attributes.Single(x => x.LogicalName == "xrmv_total")).Should().Be(0);
            ComputedColumnRules.MaskOf(source.Attributes.Single(x => x.LogicalName == "xrmv_parentid")).Should().BeNull();
        }
        (await ScalarTextAsync("SELECT definition FROM meta.attribute WHERE entity_logical_name = N'xrmv_t' AND logical_name = N'xrmv_calc'"))
            .Should().NotContain("\"SourceTypeMask\":3").And.NotContain("\"SourceTypeMask\":0");
        (await ScalarTextAsync("SELECT definition FROM meta.attribute WHERE entity_logical_name = N'xrmv_t' AND logical_name = N'xrmv_total'"))
            .Should().NotContain("\"SourceTypeMask\":0");

        var readSimple = ExportedSettings.Attribute(reloaded, "xrmv_t", reloaded.GetEntity("xrmv_t")!.Attributes.Single(x => x.LogicalName == "xrmv_total"));
        ComputedColumnRules.MaskOf(readSimple).Should().Be(0);
        var read = ExportedSettings.Attribute(reloaded, "xrmv_t", reloaded.GetEntity("xrmv_t")!.Attributes.Single(a => a.LogicalName == "xrmv_calc"));
        read.SourceType.Should().Be(1);
        ComputedColumnRules.FormulaOf(read).Should().Be("<xaml>text</xaml>");
        ComputedColumnRules.MaskOf(read).Should().Be(3);
    }

    [Fact]
    public async Task Update_cannot_change_the_source_type_but_can_change_the_formula()
    {
        var store = await TablesAsync();
        _fake.With("xrmv_t", "xrmv_calc", new ComputedExpr.Column(null, "xrmv_total"));
        await store.AddAttributeAsync("xrmv_t", Calculated("xrmv_calc"));

        var toSimple = new IntegerAttributeMetadata { LogicalName = "xrmv_calc", SourceType = 0 };
        await FluentActions.Awaiting(() => InSessionAsync(s => store.UpdateAttributeAsync(s, "xrmv_t", toSimple, default)))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*SourceType*cannot change*xrmv_t.xrmv_calc*");

        var plain = new IntegerAttributeMetadata { LogicalName = "xrmv_total", SourceType = 1 };
        await FluentActions.Awaiting(() => InSessionAsync(s => store.UpdateAttributeAsync(s, "xrmv_t", plain, default)))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*SourceType*cannot change*xrmv_t.xrmv_total*");

        var changed = new IntegerAttributeMetadata { LogicalName = "xrmv_calc" };
        ComputedColumnRules.SetFormula(changed, "<xaml>v2</xaml>");
        await InSessionAsync(s => store.UpdateAttributeAsync(s, "xrmv_t", changed, default));
        ComputedColumnRules.FormulaOf(store.GetEntity("xrmv_t")!.Attributes.Single(a => a.LogicalName == "xrmv_calc")).Should().Be("<xaml>v2</xaml>");
    }

    [Fact]
    public async Task Label_only_update_does_not_translate_dependents()
    {
        var store = await TablesAsync();
        _fake.With("xrmv_t", "xrmv_calc", new ComputedExpr.Column(null, "xrmv_total"));
        await store.AddAttributeAsync("xrmv_t", Calculated("xrmv_calc"));
        // From now on the dependent's definition cannot be read: an update that could affect it is refused, a label change is not.
        _fake.Throwing("xrmv_t", "xrmv_calc", "broken definition");

        var label = new IntegerAttributeMetadata { LogicalName = "xrmv_total", DisplayName = SystemAttributes.CreateLabel("Total renamed", 1033) };
        await InSessionAsync(s => store.UpdateAttributeAsync(s, "xrmv_t", label, default));
        store.GetEntity("xrmv_t")!.Attributes.Single(a => a.LogicalName == "xrmv_total").DisplayName.LocalizedLabels.Single().Label
            .Should().Be("Total renamed");

        var required = new IntegerAttributeMetadata
        {
            LogicalName = "xrmv_total", RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
        };
        await InSessionAsync(s => store.UpdateAttributeAsync(s, "xrmv_t", required, default));
    }

    [Fact]
    public async Task A_calculated_column_whose_definition_cannot_be_read_can_be_repaired_or_deleted()
    {
        // Final review I1: a stored definition that no longer translates (here the fake refuses its formula from now on) does not
        // lock the column itself; another column's delete still names it.
        var store = await TablesAsync();
        await store.AddAttributeAsync("xrmv_t", Plain("xrmv_other"));
        _fake.With("xrmv_t", "xrmv_calc", new ComputedExpr.Column(null, "xrmv_total"));
        await store.AddAttributeAsync("xrmv_t", Calculated("xrmv_calc"));
        _fake.With("xrmv_t", "xrmv_reader", new ComputedExpr.Column(null, "xrmv_calc"));
        await store.AddAttributeAsync("xrmv_t", Calculated("xrmv_reader"));
        _fake.Throwing("xrmv_t", "xrmv_calc", "broken definition", formula: "<xaml/>");

        // (c) Deleting a column it does not read: its definition cannot be read, so the check refuses, naming it.
        var fault = await FluentActions.Awaiting(() => InSessionAsync(s => store.DeleteAttributeAsync(s, "xrmv_t", "xrmv_other", default)))
            .Should().ThrowAsync<XrmdFaultException>();
        fault.Which.HttpStatus.Should().Be(400);
        fault.Which.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Which.Message.Should().Contain("'xrmv_t.xrmv_calc'").And.Contain("broken definition");

        // (b) A new definition repairs it (the calculated column that reads it is checked against the new one).
        var repaired = new IntegerAttributeMetadata { LogicalName = "xrmv_calc" };
        ComputedColumnRules.SetFormula(repaired, "<xaml>v2</xaml>");
        await InSessionAsync(s => store.UpdateAttributeAsync(s, "xrmv_t", repaired, default));
        ComputedColumnRules.FormulaOf(store.GetEntity("xrmv_t")!.Attributes.Single(a => a.LogicalName == "xrmv_calc")).Should().Be("<xaml>v2</xaml>");
        await InSessionAsync(s => store.DeleteAttributeAsync(s, "xrmv_t", "xrmv_other", default));

        // (a) Broken again: once its reader is gone, it can be deleted.
        await InSessionAsync(s => store.DeleteAttributeAsync(s, "xrmv_t", "xrmv_reader", default));
        _fake.Throwing("xrmv_t", "xrmv_calc", "broken definition", formula: "<xaml>v2</xaml>");
        await InSessionAsync(s => store.DeleteAttributeAsync(s, "xrmv_t", "xrmv_calc", default));
        store.GetEntity("xrmv_t")!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_calc");
    }

    [Fact]
    public async Task A_table_whose_own_calculated_column_cannot_be_read_can_be_deleted()
    {
        var store = await TablesAsync();
        _fake.With("xrmv_t", "xrmv_calc", new ComputedExpr.Column(null, "xrmv_total"));
        await store.AddAttributeAsync("xrmv_t", Calculated("xrmv_calc"));
        _fake.Throwing("xrmv_t", "xrmv_calc", "broken definition");

        await InSessionAsync(s => store.DeleteEntityAsync(s, "xrmv_t", default));
        store.GetEntity("xrmv_t").Should().BeNull();
    }

    [Fact]
    public async Task Calculated_primary_name_is_refused_on_create_and_on_add()
    {
        // Final review I3: a lookup to the table would show no name.
        var store = await StoreAsync(_fake);
        var primary = new StringAttributeMetadata { LogicalName = "xrmv_cname", SchemaName = "xrmv_cname", MaxLength = 100, SourceType = 1 };
        ComputedColumnRules.SetFormula(primary, "<xaml/>");
        _fake.With("xrmv_c", "xrmv_cname");
        var fault = await FluentActions.Awaiting(() => store.CreateEntityAsync(Table("xrmv_c"), primary))
            .Should().ThrowAsync<XrmdValidationException>();
        fault.Which.Message.Should().Be("The primary name column of a table cannot be a calculated column: 'xrmv_c.xrmv_cname'.");
        store.GetEntity("xrmv_c").Should().BeNull();

        // An existing primary name cannot become calculated either (SourceType cannot change).
        var tables = await TablesAsync();
        var toCalculated = new StringAttributeMetadata { LogicalName = "xrmv_name", SourceType = 1 };
        ComputedColumnRules.SetFormula(toCalculated, "<xaml/>");
        _fake.With("xrmv_t", "xrmv_name");
        await FluentActions.Awaiting(() => InSessionAsync(s => tables.UpdateAttributeAsync(s, "xrmv_t", toCalculated, default)))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*'xrmv_t.xrmv_name'*");
        tables.GetEntity("xrmv_t")!.Attributes.Single(a => a.LogicalName == "xrmv_name").SourceType.Should().NotBe(1);
    }

    [Fact]
    public async Task Store_without_IComputedColumns_refuses_calculated()
    {
        var store = await StoreAsync(null);
        await store.CreateEntityAsync(Table("xrmv_t"), new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 });
        await FluentActions.Awaiting(() => store.AddAttributeAsync("xrmv_t", Calculated("xrmv_calc")))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("Calculated columns are not available on this server.");
        store.GetEntity("xrmv_t")!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_calc");
    }
}
