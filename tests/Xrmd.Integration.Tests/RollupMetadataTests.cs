using System.Data.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Tests.Shared;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Rollup column definitions in the metadata store (plan 2.12b, Task 1): the value, <c>_date</c> and <c>_state</c> columns, the
/// companions' rules, the dependency check on delete (Review Focus 5) and the job notifications. A fake <see cref="IRollupColumns"/>
/// stands in for the XAML translator (Task 2) and a fake <see cref="IRollupJobs"/> for the scheduler (Task 4). Each test has its
/// own database.
/// </summary>
[Collection("xrmd")]
public sealed class RollupMetadataTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account", Order = "xrmv_calcorder", Relationship = "xrmv_calcorder_xrmv_accountid_account";

    private IDbConnectionFactory _factory = null!;
    private string _database = "";
    private string _connectionString = "";
    private readonly FakeRollupColumns _rollups = new();
    private readonly FakeRollupJobs _jobs = new();
    private readonly FakeComputedColumns _computed = new();
    private readonly ListLogger<MetadataStore> _logger = new();

    public async Task InitializeAsync()
    {
        _database = "xrmd_rollup_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, _database);
        _connectionString = TestProvider.WithDatabase(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(_connectionString);
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public Task DisposeAsync() => fx.DropDatabaseAsync(_database);

    private async Task<MetadataStore> StoreAsync(IRollupColumns? rollups = null, bool noRollups = false)
    {
        var store = new MetadataStore(_factory, computed: _computed, rollups: noRollups ? null : rollups ?? _rollups,
            rollupJobs: _jobs, logger: _logger);
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

    private static T Labelled<T>(T a, string name) where T : AttributeMetadata
    {
        a.LogicalName = name;
        a.SchemaName = name;
        a.DisplayName = SystemAttributes.CreateLabel(name, 1033);
        return a;
    }

    private static T Rollup<T>(T a, string name) where T : AttributeMetadata
    {
        Labelled(a, name);
        a.SourceType = RollupColumns.Rollup;
        ComputedColumnRules.SetFormula(a, "<rollup/>");
        return a;
    }

    /// <summary>A stand-in for the seeded <c>transactioncurrency</c> table (as <c>MetadataStoreTests</c>), which money columns need.</summary>
    private static Task CurrencyTableAsync(MetadataStore store)
    {
        var currency = new EntityMetadata { LogicalName = CurrencySchema.Table, SchemaName = "TransactionCurrency", OwnershipType = OwnershipTypes.OrganizationOwned };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.GetSetMethod(nonPublic: true)!.Invoke(currency, [9105]);
        return store.CreateEntityAsync(currency, new StringAttributeMetadata { LogicalName = "currencyname", MaxLength = 100 });
    }

    /// <summary><c>account</c> and <c>xrmv_calcorder</c> (amount, price, start, status, the lookup to account).</summary>
    private async Task<MetadataStore> TablesAsync()
    {
        var store = await StoreAsync();
        await CurrencyTableAsync(store);
        await store.CreateEntityAsync(Table(Account), new StringAttributeMetadata { LogicalName = "name", MaxLength = 100 });
        await store.CreateEntityAsync(Table(Order), new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 });
        await store.AddAttributeAsync(Order, Labelled(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_amount"));
        await store.AddAttributeAsync(Order, Labelled(new MoneyAttributeMetadata(), "xrmv_price"));
        await store.AddAttributeAsync(Order, Labelled(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime), "xrmv_startedon"));
        await store.AddAttributeAsync(Order, Labelled(new IntegerAttributeMetadata(), "xrmv_status"));
        await store.AddAttributeAsync(Order, Labelled(new LookupAttributeMetadata { Targets = [Account] }, "xrmv_accountid"));
        return store;
    }

    private static ComputedCondition StatusIs2 =>
        new ComputedCondition.Compare("Equal", new ComputedExpr.Column(null, "xrmv_status"), new ComputedExpr.Literal(2));

    private async Task<MetadataStore> OrderTotalAsync()
    {
        var store = await TablesAsync();
        _rollups.Related(Account, "xrmv_ordertotal", RollupAggregate.Sum, Order, "xrmv_accountid", Relationship, "xrmv_amount", StatusIs2);
        await store.AddAttributeAsync(Account, Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_ordertotal"));
        return store;
    }

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code)
    {
        var fault = (await FluentActions.Awaiting(act).Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        fault.HttpStatus.Should().Be(400);
        return fault;
    }

    [Fact]
    public async Task CreateAttribute_rollup_adds_value_date_and_state_columns()
    {
        var store = await OrderTotalAsync();
        _rollups.Related(Account, "xrmv_wonprice", RollupAggregate.Sum, Order, "xrmv_accountid", Relationship, "xrmv_price");
        await store.AddAttributeAsync(Account, Rollup(new MoneyAttributeMetadata(), "xrmv_wonprice"));

        (await ColumnsAsync(Account)).Should().Contain(
        [
            "xrmv_ordertotal", "xrmv_ordertotal_date", "xrmv_ordertotal_state",
            "xrmv_wonprice", "xrmv_wonprice_base", "xrmv_wonprice_date", "xrmv_wonprice_state",
        ]);

        var reloaded = await StoreAsync();
        foreach (var source in new[] { store.GetEntity(Account)!, reloaded.GetEntity(Account)! })
        {
            var rollup = source.Attributes.Single(a => a.LogicalName == "xrmv_ordertotal");
            rollup.SourceType.Should().Be(2);
            rollup.IsValidForCreate.Should().BeFalse();
            rollup.IsValidForUpdate.Should().BeFalse();
            ComputedColumnRules.FormulaOf(rollup).Should().Be("<rollup/>");
            ComputedColumnRules.MaskOf(rollup).Should().Be(0);
            var date = source.Attributes.Single(a => a.LogicalName == "xrmv_ordertotal_date").Should().BeOfType<DateTimeAttributeMetadata>().Which;
            var state = source.Attributes.Single(a => a.LogicalName == "xrmv_ordertotal_state").Should().BeOfType<IntegerAttributeMetadata>().Which;
            foreach (var companion in new AttributeMetadata[] { date, state })
            {
                companion.AttributeOf.Should().Be("xrmv_ordertotal");
                companion.LinkedAttributeId.Should().Be(rollup.MetadataId);
                companion.IsValidForCreate.Should().BeFalse();
                companion.IsValidForUpdate.Should().BeFalse();
            }
            date.SchemaName.Should().Be("xrmv_ordertotal_Date");
            source.Attributes.Should().Contain(a => a.LogicalName == "xrmv_wonprice_base");
        }
    }

    [Fact]
    public async Task Create_entity_with_a_rollup_adds_its_companions()
    {
        var store = await TablesAsync();
        _rollups.With("xrmv_tree", "xrmv_total", new RollupDefinition(RollupAggregate.Sum, "xrmv_amount", null, "xrmv_tree_xrmv_parentid_xrmv_tree",
            null, [new RollupSource("xrmv_tree", "xrmv_amount")]));
        var tree = Table("xrmv_tree");
        tree.SetAttributes([Labelled(new DecimalAttributeMetadata(), "xrmv_amount"), Rollup(new DecimalAttributeMetadata(), "xrmv_total")]);
        await store.CreateEntityAsync(tree, new StringAttributeMetadata { LogicalName = "xrmv_treename", MaxLength = 100 });

        (await ColumnsAsync("xrmv_tree")).Should().Contain(["xrmv_total", "xrmv_total_date", "xrmv_total_state"]);
        (await _jobs.WaitForAsync(1)).Should().Contain(("xrmv_tree", "xrmv_total", RollupChange.Added));
    }

    [Fact]
    public async Task Companion_name_clash_is_refused()
    {
        var store = await TablesAsync();
        await store.AddAttributeAsync(Account, Labelled(new IntegerAttributeMetadata(), "xrmv_ordertotal_state"));
        _rollups.Related(Account, "xrmv_ordertotal", RollupAggregate.Sum, Order, "xrmv_accountid", Relationship, "xrmv_amount");

        var fault = await FaultAsync(() => store.AddAttributeAsync(Account, Rollup(new DecimalAttributeMetadata(), "xrmv_ordertotal")),
            unchecked((int)0x80060556));
        fault.Message.Should().Contain("xrmv_ordertotal_state").And.Contain("account.xrmv_ordertotal");
        store.GetEntity(Account)!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_ordertotal");
        (await ColumnsAsync(Account)).Should().NotContain("xrmv_ordertotal");

        // The same on a new table.
        _rollups.With("xrmv_new", "xrmv_r", new RollupDefinition(RollupAggregate.Count, "xrmv_amount", null, "h", null, []));
        var table = Table("xrmv_new");
        table.SetAttributes([Rollup(new IntegerAttributeMetadata(), "xrmv_r"), Labelled(new DateTimeAttributeMetadata(), "xrmv_r_date")]);
        await FaultAsync(() => store.CreateEntityAsync(table, new StringAttributeMetadata { LogicalName = "xrmv_newname", MaxLength = 100 }),
            unchecked((int)0x80060556));
        store.GetEntity("xrmv_new").Should().BeNull();
    }

    [Fact]
    public async Task Delete_of_rollup_source_is_refused_naming_the_rollup()
    {
        var store = await OrderTotalAsync();

        foreach (var column in new[] { "xrmv_amount", "xrmv_accountid", "xrmv_status" })
        {
            var fault = await FaultAsync(() => InSessionAsync(s => store.DeleteAttributeAsync(s, Order, column, default)), unchecked((int)0x80060541));
            fault.Message.Should().Contain("account.xrmv_ordertotal");
            store.GetEntity(Order)!.Attributes.Should().Contain(a => a.LogicalName == column, "a refused delete drops nothing");
            (await ColumnsAsync(Order)).Should().Contain(column);
        }
        var relationship = await FaultAsync(() => InSessionAsync(s => store.DeleteRelationshipAsync(s, Relationship, default)), unchecked((int)0x80060541));
        relationship.Message.Should().Contain("account.xrmv_ordertotal");
        var table = await FaultAsync(() => InSessionAsync(s => store.DeleteEntityAsync(s, Order, default)), unchecked((int)0x80060541));
        table.Message.Should().Contain("account.xrmv_ordertotal");
        store.GetEntity(Order).Should().NotBeNull();

        // A column the rollup does not read goes.
        await InSessionAsync(s => store.DeleteAttributeAsync(s, Order, "xrmv_startedon", default));
        store.GetEntity(Order)!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_startedon");
    }

    [Fact]
    public async Task Delete_of_rollup_drops_companions()
    {
        var store = await OrderTotalAsync();
        _rollups.Related(Account, "xrmv_wonprice", RollupAggregate.Sum, Order, "xrmv_accountid", Relationship, "xrmv_price");
        await store.AddAttributeAsync(Account, Rollup(new MoneyAttributeMetadata(), "xrmv_wonprice"));

        await InSessionAsync(s => store.DeleteAttributeAsync(s, Account, "xrmv_ordertotal", default));
        await InSessionAsync(s => store.DeleteAttributeAsync(s, Account, "xrmv_wonprice", default));

        var columns = await ColumnsAsync(Account);
        columns.Should().NotContain(c => c.StartsWith("xrmv_ordertotal", StringComparison.Ordinal) || c.StartsWith("xrmv_wonprice", StringComparison.Ordinal));
        foreach (var source in new[] { store.GetEntity(Account)!, (await StoreAsync()).GetEntity(Account)! })
            source.Attributes.Should().NotContain(a => a.LogicalName.StartsWith("xrmv_ordertotal", StringComparison.Ordinal)
                                                       || a.LogicalName.StartsWith("xrmv_wonprice", StringComparison.Ordinal));

        // Its sources are free again.
        await InSessionAsync(s => store.DeleteAttributeAsync(s, Order, "xrmv_amount", default));
        await InSessionAsync(s => store.DeleteRelationshipAsync(s, Relationship, default));
        store.GetEntity(Order)!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_amount" || a.LogicalName == "xrmv_accountid");
    }

    [Fact]
    public async Task Update_or_delete_of_companion_is_refused()
    {
        var store = await OrderTotalAsync();
        foreach (var companion in new[] { "xrmv_ordertotal_date", "xrmv_ordertotal_state" })
        {
            var delete = await FaultAsync(() => InSessionAsync(s => store.DeleteAttributeAsync(s, Account, companion, default)), unchecked((int)0x80060555));
            delete.Message.Should().Contain($"account.{companion}").And.Contain("account.xrmv_ordertotal");
            AttributeMetadata change = companion.EndsWith("_date", StringComparison.Ordinal)
                ? new DateTimeAttributeMetadata { LogicalName = companion }
                : new IntegerAttributeMetadata { LogicalName = companion };
            change.DisplayName = SystemAttributes.CreateLabel("Renamed", 1033);
            await FaultAsync(() => InSessionAsync(s => store.UpdateAttributeAsync(s, Account, change, default)), unchecked((int)0x80060555));
        }
        (await ColumnsAsync(Account)).Should().Contain(["xrmv_ordertotal_date", "xrmv_ordertotal_state"]);
    }

    [Fact]
    public async Task Behavior_change_of_rollup_date_source_is_refused()
    {
        var store = await TablesAsync();
        _rollups.Related(Account, "xrmv_lastorder", RollupAggregate.Max, Order, "xrmv_accountid", Relationship, "xrmv_startedon");
        await store.AddAttributeAsync(Account, Rollup(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime), "xrmv_lastorder"));

        var change = new DateTimeAttributeMetadata { LogicalName = "xrmv_startedon", DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent };
        var fault = await FaultAsync(() => InSessionAsync(s => store.UpdateAttributeAsync(s, Order, change, default)), unchecked((int)0x80097214));
        fault.Message.Should().Contain("account.xrmv_lastorder").And.Contain("xrmv_calcorder.xrmv_startedon");

        // The same behavior is no change.
        var same = new DateTimeAttributeMetadata { LogicalName = "xrmv_startedon", DateTimeBehavior = DateTimeBehavior.UserLocal };
        await InSessionAsync(s => store.UpdateAttributeAsync(s, Order, same, default));
    }

    [Fact]
    public async Task Store_without_IRollupColumns_refuses_rollup()
    {
        var store = await StoreAsync(noRollups: true);
        await store.CreateEntityAsync(Table(Account), new StringAttributeMetadata { LogicalName = "name", MaxLength = 100 });
        var fault = await FluentActions.Awaiting(() => store.AddAttributeAsync(Account, Rollup(new DecimalAttributeMetadata(), "xrmv_ordertotal")))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("Rollup columns are not available on this server.");
        store.GetEntity(Account)!.Attributes.Should().NotContain(a => a.LogicalName.StartsWith("xrmv_ordertotal", StringComparison.Ordinal));
        (await ColumnsAsync(Account)).Should().NotContain("xrmv_ordertotal");
    }

    [Fact]
    public async Task Definition_change_notifies_jobs()
    {
        var store = await OrderTotalAsync();
        (await _jobs.WaitForAsync(1)).Should().Equal((Account, "xrmv_ordertotal", RollupChange.Added));

        // A rolled-back change notifies nothing.
        var relabel = new DecimalAttributeMetadata { LogicalName = "xrmv_ordertotal", DisplayName = SystemAttributes.CreateLabel("Total", 1033) };
        await FluentActions.Awaiting(() => InSessionAsync(async s =>
        {
            await store.UpdateAttributeAsync(s, Account, relabel, default);
            throw new InvalidOperationException("roll back");
        })).Should().ThrowAsync<InvalidOperationException>();

        // An update notifies even without a change of definition (it resets the mass calculation, ruling R8); a failing scheduler
        // is logged, not thrown.
        _jobs.FailOn = RollupChange.Updated;
        await InSessionAsync(s => store.UpdateAttributeAsync(s, Account, relabel, default));
        await InSessionAsync(s => store.DeleteAttributeAsync(s, Account, "xrmv_ordertotal", default));

        (await _jobs.WaitForAsync(3)).Should().Equal(
            (Account, "xrmv_ordertotal", RollupChange.Added),
            (Account, "xrmv_ordertotal", RollupChange.Updated),
            (Account, "xrmv_ordertotal", RollupChange.Deleted));
        await store.RollupJobNotifications;
        // The failed notification (the update) is logged once, as an error naming the column and the change.
        lock (_logger.Entries)
            _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which.Message
                .Should().Contain("account.xrmv_ordertotal").And.Contain("Updated");
        // A simple column notifies nothing.
        await InSessionAsync(s => store.DeleteAttributeAsync(s, Order, "xrmv_startedon", default));
        await Task.Delay(100);
        _jobs.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_of_rollup_is_refused_while_a_calculated_column_reads_a_companion()
    {
        var store = await OrderTotalAsync();
        var calculated = Labelled(new IntegerAttributeMetadata(), "xrmv_calc");
        calculated.SourceType = 1;
        ComputedColumnRules.SetFormula(calculated, "<xaml/>");
        _computed.With(Account, "xrmv_calc", new ComputedExpr.Column(null, "xrmv_ordertotal_state"));
        await store.AddAttributeAsync(Account, calculated);
        ComputedColumnRules.MaskOf(store.GetEntity(Account)!.Attributes.Single(a => a.LogicalName == "xrmv_calc")).Should().Be(16);

        var fault = await FaultAsync(() => InSessionAsync(s => store.DeleteAttributeAsync(s, Account, "xrmv_ordertotal", default)),
            ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("account.xrmv_calc");
        store.GetEntity(Account)!.Attributes.Should().Contain(a => a.LogicalName == "xrmv_ordertotal_state", "a refused delete drops nothing");
        (await ColumnsAsync(Account)).Should().Contain(["xrmv_ordertotal", "xrmv_ordertotal_date", "xrmv_ordertotal_state"]);

        // Without the calculated column, the rollup goes with its companions.
        await InSessionAsync(s => store.DeleteAttributeAsync(s, Account, "xrmv_calc", default));
        await InSessionAsync(s => store.DeleteAttributeAsync(s, Account, "xrmv_ordertotal", default));
        (await ColumnsAsync(Account)).Should().NotContain(c => c.StartsWith("xrmv_ordertotal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Formula_change_that_makes_an_aggregated_calculated_column_non_basic_is_refused()
    {
        var store = await TablesAsync();
        var basic = Labelled(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_basic");
        basic.SourceType = 1;
        ComputedColumnRules.SetFormula(basic, "<own/>");
        _computed.With(Order, "xrmv_basic", new ComputedExpr.Column(null, "xrmv_amount"));
        await store.AddAttributeAsync(Order, basic);
        _rollups.Related(Account, "xrmv_basictotal", RollupAggregate.Sum, Order, "xrmv_accountid", Relationship, "xrmv_basic");
        await store.AddAttributeAsync(Account, Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_basictotal"));

        // The new formula reads a column of the parent row: the calculated column is no longer basic (mask 2).
        _computed.With(Order, "xrmv_basic", new ComputedExpr.Column("xrmv_accountid", "name"));
        var change = new DecimalAttributeMetadata { LogicalName = "xrmv_basic" };
        ComputedColumnRules.SetFormula(change, "<parent/>");
        var fault = await FaultAsync(() => InSessionAsync(s => store.UpdateAttributeAsync(s, Order, change, default)), unchecked((int)0x8006053c));
        fault.Message.Should().Contain("account.xrmv_basictotal").And.Contain("xrmv_calcorder.xrmv_basic");
        ComputedColumnRules.FormulaOf(store.GetEntity(Order)!.Attributes.Single(a => a.LogicalName == "xrmv_basic")).Should().Be("<own/>");
    }
}
