using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Rollups;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Sql;
using Xrmd.Workflows.Calculated;
using Xrmd.Workflows.Rollup;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Rollup values computed by SQL Server (plan 2.12b, Task 3; rulings R6, R7; Review Focus 2 and 3). The tables <c>account</c>
/// (hierarchical through <c>account_parent_account</c>) and <c>xrmv_calcorder</c> are made in a database of the test's own, with
/// the four rollups of the parity export (tests/fixtures/rollup/real) and the provisional ones (tests/fixtures/rollup), read by the
/// server's XAML translator. Rows are written directly in SQL: the calculation reads the stored values only. The expected values are
/// those of R7 (assumed until the recalculated export of Q1).
/// </summary>
[Collection("xrmd")]
public sealed class RollupCalculationTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account", Order = "xrmv_calcorder";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);

    private IDbConnectionFactory _factory = null!;
    private string _database = "";
    private string _connectionString = "";
    private MetadataStore _store = null!;
    private readonly RollupColumnsService _rollups = new();
    private readonly CalculatedColumns _computed = new(TestSql.Dialect);
    private readonly Guid _euro = Guid.NewGuid(), _dollar = Guid.NewGuid();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public async Task InitializeAsync()
    {
        _database = "xrmd_rollcalc_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, _database);
        _connectionString = TestProvider.WithDatabase(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(_connectionString);
        await new SchemaMigrator(_factory).MigrateAsync();
        _store = new MetadataStore(_factory, computed: _computed, rollups: _rollups);
        await _store.LoadAsync();
        await TablesAsync();
    }

    public Task DisposeAsync() => fx.DropDatabaseAsync(_database);

    private RollupCalculator Calculator(RollupLimits? limits = null) =>
        new(_factory, TestSql.Dialect, _store, _rollups, _computed, new FixedTime(Now), limits);

    // ---- tables and rollups -------------------------------------------------------------------------------------------

    private static string Real(string column) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", "real", $"account-{column}.xaml"));

    private static string Designer(string column) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", "real", "designer", $"account-{column}.xaml"));

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", name + ".xaml"));

    private static T Named<T>(T a, string name) where T : AttributeMetadata
    {
        a.LogicalName = name;
        a.SchemaName = name;
        a.DisplayName = SystemAttributes.CreateLabel(name, 1033);
        return a;
    }

    private static T Rollup<T>(T a, string name, string xaml) where T : AttributeMetadata
    {
        Named(a, name);
        a.SourceType = RollupColumns.Rollup;
        ComputedColumnRules.SetFormula(a, xaml);
        return a;
    }

    private static OptionSetMetadata Options(params int[] values) =>
        new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(SystemAttributes.CreateLabel("Option " + v, 1033), v)).ToList()))
        {
            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
        };

    private static EntityMetadata Table(string name) => new()
    {
        LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel(name, 1033),
    };

    private async Task TablesAsync()
    {
        // A stand-in for the seeded transactioncurrency table (name and precision are what the calculation reads).
        var currency = new EntityMetadata { LogicalName = CurrencySchema.Table, SchemaName = "TransactionCurrency", OwnershipType = OwnershipTypes.OrganizationOwned };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.GetSetMethod(nonPublic: true)!.Invoke(currency, [9105]);
        await _store.CreateEntityAsync(currency, new StringAttributeMetadata { LogicalName = "currencyname", MaxLength = 100 });
        await _store.AddAttributeAsync(CurrencySchema.Table, Named(new IntegerAttributeMetadata(), CurrencySchema.Precision));

        await _store.CreateEntityAsync(Table(Account), new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 });
        await _store.AddAttributeAsync(Account, Named(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_budget"));
        await _store.CreateEntityAsync(Table(Order), new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 });
        await _store.AddAttributeAsync(Order, Named(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_amount"));
        await _store.AddAttributeAsync(Order, Named(new IntegerAttributeMetadata(), "xrmv_quantity"));
        await _store.AddAttributeAsync(Order, Named(new DecimalAttributeMetadata { Precision = 3 }, "xrmv_exact"));
        await _store.AddAttributeAsync(Order, Named(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_price"));
        await _store.AddAttributeAsync(Order, Named(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal }, "xrmv_startedon"));
        await _store.AddAttributeAsync(Order, Named(new PicklistAttributeMetadata { OptionSet = Options(1, 2, 3) }, "xrmv_status"));
        await TestEntities.InSessionAsync(_connectionString, async s =>
        {
            await _store.CreateOneToManyAsync(s, new OneToManyRelationshipMetadata
            {
                SchemaName = "account_parent_account", ReferencedEntity = Account, ReferencingEntity = Account, ReferencedAttribute = "accountid",
                IsHierarchical = true,
            }, Named(new LookupAttributeMetadata(), "parentaccountid"), CancellationToken.None);
            await _store.CreateOneToManyAsync(s, new OneToManyRelationshipMetadata
            {
                SchemaName = "xrmv_calcorder_AccountId_account", ReferencedEntity = Account, ReferencingEntity = Order, ReferencedAttribute = "accountid",
            }, Named(new LookupAttributeMetadata(), "xrmv_accountid"), CancellationToken.None);
        });

        AttributeMetadata[] rollups =
        [
            // The parity export.
            Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_ordertotal", Real("xrmv_ordertotal")),
            Rollup(new IntegerAttributeMetadata(), "xrmv_ordercount", Real("xrmv_ordercount")),
            Rollup(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal }, "xrmv_lastorder", Real("xrmv_lastorder")),
            Rollup(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_treebudget", Real("xrmv_treebudget")),
            // The provisional shapes.
            Rollup(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal }, "xrmv_firstorder", Fixture("minimum")),
            Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_bigopen", Fixture("two-conditions")),
            Rollup(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_wonprice", Fixture("currency-sum")),
            Rollup(new IntegerAttributeMetadata(), "xrmv_treeorders", Fixture("source-filter")),
            Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_avgamount", Fixture("average")),
            // average.xaml over the whole number xrmv_quantity, into a whole number (R7: rounded half away from zero).
            Rollup(new IntegerAttributeMetadata(), "xrmv_avgquantity", Fixture("average").Replace("Attribute=\"xrmv_amount\"", "Attribute=\"xrmv_quantity\"")),
            // The export's xrmv_ordertotal with a maximum of 100 (R7: a value outside MinValue/MaxValue is an overflow).
            Rollup(new DecimalAttributeMetadata { Precision = 2, MinValue = 0, MaxValue = 100 }, "xrmv_capped", Real("xrmv_ordertotal")),
            // The export's xrmv_ordertotal over xrmv_exact (3 decimals) into 2 decimals: each value is cut before the sum (R7).
            Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_exacttotal", Real("xrmv_ordertotal").Replace("Attribute=\"xrmv_amount\"", "Attribute=\"xrmv_exact\"")),
            // The export's xrmv_ordercount over the nullable xrmv_amount: COUNT counts the rows with a value.
            Rollup(new IntegerAttributeMetadata(), "xrmv_amountcount", Real("xrmv_ordercount").Replace("Attribute=\"xrmv_calcorderid\"", "Attribute=\"xrmv_amount\"")),
            // The export's xrmv_ordertotal over the whole number xrmv_quantity, into a whole number with no MinValue/MaxValue.
            Rollup(new IntegerAttributeMetadata(), "xrmv_qtysum", Real("xrmv_ordertotal").Replace("Attribute=\"xrmv_amount\"", "Attribute=\"xrmv_quantity\"")),
            // The parity run on a licensed environment (plan 2.12b, Task 7): the designer's filters and the real AVG.
            Rollup(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal }, "xrmv_p_firstorder", Designer("xrmv_firstorder")),
            Rollup(new IntegerAttributeMetadata(), "xrmv_p_treeorders", Designer("xrmv_treeorders")),
            Rollup(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_p_wonprice", Real("xrmv_ordertotal").Replace("Attribute=\"xrmv_amount\"", "Attribute=\"xrmv_price\"")),
            Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_p_avgamount", Real("xrmv_avgamount")),
        ];
        foreach (var a in rollups) await _store.AddAttributeAsync(Account, a);
    }

    // ---- rows ---------------------------------------------------------------------------------------------------------

    private async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value); // a null goes untyped (TestParameterExtensions)
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CurrenciesAsync()
    {
        await ExecAsync("INSERT INTO [data].[transactioncurrency] (transactioncurrencyid, currencyname, currencyprecision) VALUES (@e, N'Euro', 2), (@d, N'Dollar', 2)",
            ("@e", _euro), ("@d", _dollar));
    }

    private async Task<Guid> AccountAsync(string name, Guid? parent = null, decimal? budget = null, Guid? currency = null, decimal? rate = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO [data].[account] (accountid, name, parentaccountid, xrmv_budget, xrmv_budget_base, transactioncurrencyid, exchangerate, modifiedon, modifiedby) " +
            "VALUES (@id, @name, @parent, @budget, @base, @currency, @rate, @modified, NULL)",
            ("@id", id), ("@name", name), ("@parent", parent), ("@budget", budget), ("@base", budget is { } b ? b / (rate ?? 1m) : null),
            ("@currency", currency), ("@rate", rate), ("@modified", new DateTime(2026, 1, 1)));
        return id;
    }

    private async Task<Guid> OrderAsync(Guid? account, decimal? amount = null, int? status = null, DateTime? startedOn = null, int? quantity = null,
        decimal? priceBase = null, Guid? currency = null, DateTime? modifiedOn = null, decimal? exact = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO [data].[xrmv_calcorder] (xrmv_calcorderid, xrmv_name, xrmv_accountid, xrmv_amount, xrmv_status, xrmv_startedon, xrmv_quantity, " +
            "xrmv_price, xrmv_price_base, transactioncurrencyid, exchangerate, modifiedon, xrmv_exact) " +
            "VALUES (@id, N'order', @account, @amount, @status, @started, @quantity, @price, @base, @currency, @rate, @modified, @exact)",
            ("@id", id), ("@account", account), ("@amount", amount), ("@status", status), ("@started", startedOn), ("@quantity", quantity), ("@exact", exact),
            ("@price", priceBase is { } p ? p * 3m : null), ("@base", priceBase), ("@currency", currency), ("@rate", priceBase is null ? null : 3m),
            ("@modified", modifiedOn ?? new DateTime(2026, 1, 1)));
        return id;
    }

    private async Task<Dictionary<string, object?>> RowAsync(Guid account, params string[] columns)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, $"SELECT {string.Join(", ", columns.Select(c => $"[{c}]"))} FROM [data].[account] WHERE accountid = @id");
        cmd.AddParameter("@id", account);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        return columns.Select((c, i) => (c, rd.IsDBNull(i) ? null : rd.GetValue(i))).ToDictionary(x => x.c, x => x.Item2);
    }

    /// <summary>Value and state of <paramref name="rollup"/> on <paramref name="account"/>.</summary>
    private async Task<(object? Value, object? State)> RollupAsync(Guid account, string rollup)
    {
        var row = await RowAsync(account, rollup, rollup + "_state");
        return (row[rollup], row[rollup + "_state"]);
    }

    private Task<int> AllAsync(string rollup) => Calculator().RecalculateAsync(Account, rollup, new RollupScope.All(), CancellationToken.None);

    // ---- tests --------------------------------------------------------------------------------------------------------

    /// <summary>An order with its own price, currency and rate (the parity run's rows).</summary>
    private async Task OrderInAsync(Guid? account, decimal? amount, int status, DateTime startedOn, decimal? price, Guid currency, decimal rate)
    {
        await ExecAsync(
            "INSERT INTO [data].[xrmv_calcorder] (xrmv_calcorderid, xrmv_name, xrmv_accountid, xrmv_amount, xrmv_status, xrmv_startedon, " +
            "xrmv_price, xrmv_price_base, transactioncurrencyid, exchangerate, modifiedon) " +
            "VALUES (@id, N'order', @account, @amount, @status, @started, @price, @base, @currency, @rate, @modified)",
            ("@id", Guid.NewGuid()), ("@account", account), ("@amount", amount), ("@status", status), ("@started", startedOn),
            ("@price", price), ("@base", price is { } p ? Math.Round(p / rate, 2) : null), ("@currency", currency), ("@rate", rate),
            ("@modified", new DateTime(2026, 1, 1)));
    }

    [Fact]
    public async Task Parity_values_match_the_licensed_environment_run()
    {
        // Plan 2.12b, Task 7: the accounts and orders of the parity run on a licensed Dataverse environment (2026-11-18, invented
        // data), the eight rollups recalculated by the Mass jobs there, and the values Dataverse stored (all with state 1).
        await CurrenciesAsync();
        DateTime At(int day, int hour) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);
        var parent = await AccountAsync("Contoso test parent", null, 10000m, _euro, 1m);
        var child = await AccountAsync("Contoso test Child", parent, 2500m, _euro, 1m);
        var fabrikam = await AccountAsync("Fabrikam test", null, null, _dollar, 12m);
        var northwind = await AccountAsync("Northwind test", parent, 500m, _euro, 1m);
        var empty = await AccountAsync("Empty test", null, 100m, _euro, 1m);
        await OrderInAsync(child, 1500m, 2, At(9, 23), 200m, _dollar, 12m);      // ORD-001
        await OrderInAsync(parent, 400m, 1, At(16, 23), 50m, _euro, 1m);         // ORD-002
        await OrderInAsync(null, null, 2, At(16, 23), 120m, _euro, 1m);          // ORD-003, no account
        await OrderInAsync(fabrikam, null, 1, At(15, 23), null, _euro, 1m);      // AB
        await OrderInAsync(child, 300m, 2, At(18, 8), 80m, _dollar, 12m);        // ORD-005
        await OrderInAsync(child, null, 2, At(17, 13), 40m, _euro, 1m);          // ORD-006
        await OrderInAsync(parent, 150m, 1, new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc), 60m, _euro, 1m); // ORD-007

        string[] columns = ["xrmv_ordertotal", "xrmv_ordercount", "xrmv_lastorder", "xrmv_treebudget", "xrmv_p_avgamount", "xrmv_p_firstorder",
            "xrmv_p_wonprice", "xrmv_p_treeorders"];
        foreach (var rollup in columns) await AllAsync(rollup);

        var expected = new Dictionary<Guid, object?[]>
        {
            [parent] = [0m, 2, At(16, 23), 13000m, 275m, new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc), 0m, 5],
            // Decimal SUM does not convert currencies (1500 + 300); Currency SUM converts each row from its full value
            // (200/12 + 80/12 + 40 = 63.333332 in Dataverse, cut to the money scale of 4 here); AVG counts the empty amount as 0.
            [child] = [1800m, 3, At(18, 8), 2500m, 600m, null, 63.3333m, 3],
            // A hierarchy SUM of an empty budget is 0; the source filter (budget has a value) also drops the row itself.
            [fabrikam] = [0m, 1, At(15, 23), 0m, 0m, null, 0m, 0],
            [northwind] = [0m, 0, null, 500m, 0m, null, 0m, 0],
            [empty] = [0m, 0, null, 100m, 0m, null, 0m, 0],
        };
        foreach (var (account, values) in expected)
            for (var i = 0; i < columns.Length; i++)
            {
                var (value, state) = await RollupAsync(account, columns[i]);
                value.Should().Be(values[i], $"{columns[i]} of {account}");
                state.Should().Be(1, $"{columns[i]} of {account}");
            }
        (await RowAsync(child, "xrmv_p_wonprice_base"))["xrmv_p_wonprice_base"].Should().Be(63.3333m);
        (await RowAsync(parent, "xrmv_treebudget_base"))["xrmv_treebudget_base"].Should().Be(13000m);
    }

    [Fact]
    public async Task Sum_count_max_values_and_state_1()
    {
        var a = await AccountAsync("A");
        var b = await AccountAsync("B");
        await OrderAsync(a, 100.25m, 2, new DateTime(2026, 3, 1, 8, 0, 0));
        await OrderAsync(a, 50.50m, 2, new DateTime(2026, 5, 2, 9, 0, 0));
        await OrderAsync(a, 20m, 1, new DateTime(2026, 4, 1));
        await OrderAsync(a, null, 2);
        await OrderAsync(b, 7m, 2, new DateTime(2025, 1, 1));
        await OrderAsync(null, 1000m, 2);

        (await AllAsync("xrmv_ordertotal")).Should().Be(2, "every account row is recalculated the first time");
        await AllAsync("xrmv_ordercount");
        await AllAsync("xrmv_lastorder");

        var row = await RowAsync(a, "xrmv_ordertotal", "xrmv_ordertotal_state", "xrmv_ordertotal_date", "xrmv_ordercount", "xrmv_ordercount_state",
            "xrmv_lastorder", "xrmv_lastorder_state", "modifiedon", "modifiedby");
        row["xrmv_ordertotal"].Should().Be(150.75m, "the sum of the orders with status 2 (a null amount adds nothing)");
        row["xrmv_ordertotal_state"].Should().Be(1);
        row["xrmv_ordertotal_date"].Should().Be(Now.UtcDateTime);
        row["xrmv_ordercount"].Should().Be(4, "COUNT of the primary key counts every related row");
        row["xrmv_ordercount_state"].Should().Be(1);
        row["xrmv_lastorder"].Should().Be(new DateTime(2026, 5, 2, 9, 0, 0));
        row["xrmv_lastorder_state"].Should().Be(1);
        row["modifiedon"].Should().Be(new DateTime(2026, 1, 1), "the calculation does not touch modifiedon (R8)");
        row["modifiedby"].Should().BeNull();
        (await RollupAsync(b, "xrmv_ordertotal")).Should().Be((7m, 1));

        // Nothing changed: no row is written again (Update_only_where_value_or_state_differs).
        (await AllAsync("xrmv_ordertotal")).Should().Be(0);
        await OrderAsync(b, 3m, 2);
        (await AllAsync("xrmv_ordertotal")).Should().Be(1);
        (await RollupAsync(b, "xrmv_ordertotal")).Should().Be((10m, 1));
    }

    [Fact]
    public async Task Empty_set_gives_zero_or_null_per_aggregate()
    {
        var a = await AccountAsync("Empty");
        foreach (var rollup in new[] { "xrmv_ordertotal", "xrmv_ordercount", "xrmv_lastorder", "xrmv_firstorder", "xrmv_avgamount" })
            await AllAsync(rollup);

        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((0m, 1));
        (await RollupAsync(a, "xrmv_ordercount")).Should().Be((0, 1));
        (await RollupAsync(a, "xrmv_lastorder")).Should().Be((null, 1));
        (await RollupAsync(a, "xrmv_firstorder")).Should().Be((null, 1));
        (await RollupAsync(a, "xrmv_avgamount")).Should().Be((0m, 1), "an empty AVG is 0 (parity run)");
    }

    [Fact]
    public async Task Average_of_whole_numbers_rounds()
    {
        var half = await AccountAsync("Half");
        await OrderAsync(half, quantity: 1);
        await OrderAsync(half, quantity: 2);
        await OrderAsync(half, quantity: null);
        var negative = await AccountAsync("Negative");
        await OrderAsync(negative, quantity: -1);
        await OrderAsync(negative, quantity: -2);
        var third = await AccountAsync("Third");
        await OrderAsync(third, 1m, quantity: 1);
        await OrderAsync(third, 1m, quantity: 1);
        await OrderAsync(third, 2m, quantity: 2);

        await AllAsync("xrmv_avgquantity");
        await AllAsync("xrmv_avgamount");

        (await RollupAsync(half, "xrmv_avgquantity")).Should().Be((1, 1), "an empty value counts as 0 (parity run): (1 + 2 + 0) / 3 = 1");
        (await RollupAsync(negative, "xrmv_avgquantity")).Should().Be((-2, 1));
        (await RollupAsync(third, "xrmv_avgquantity")).Should().Be((1, 1), "1.33 rounds to 1");
        (await RollupAsync(third, "xrmv_avgamount")).Should().Be((1.33m, 1), "a decimal average is rounded to the column's precision");
    }

    [Fact]
    public async Task Filter_excludes_rows()
    {
        var a = await AccountAsync("A");
        await OrderAsync(a, 150m, 1);
        await OrderAsync(a, 50m, 1);
        await OrderAsync(a, 200m, 2);
        await OrderAsync(a, 120m, null);

        await AllAsync("xrmv_bigopen");

        (await RollupAsync(a, "xrmv_bigopen")).Should().Be((150m, 1), "only status 1 and an amount above 100");
    }

    [Fact]
    public async Task Hierarchy_sum_includes_self_and_descendants()
    {
        await CurrenciesAsync();
        var parent = await AccountAsync("P", budget: 100m, currency: _euro, rate: 1m);
        var child1 = await AccountAsync("C1", parent, 20m, _euro, 1m);
        var child2 = await AccountAsync("C2", parent, null, _euro, 1m);
        var grandchild = await AccountAsync("G", child1, 5m, _euro, 1m);
        await OrderAsync(parent);
        await OrderAsync(child1);
        await OrderAsync(child2);
        await OrderAsync(child2);
        await OrderAsync(grandchild);

        await AllAsync("xrmv_treebudget");
        await AllAsync("xrmv_treeorders");

        (await RollupAsync(parent, "xrmv_treebudget")).Should().Be((125m, 1));
        (await RollupAsync(child1, "xrmv_treebudget")).Should().Be((25m, 1));
        (await RollupAsync(child2, "xrmv_treebudget")).Should().Be((0m, 1));
        (await RollupAsync(grandchild, "xrmv_treebudget")).Should().Be((5m, 1));
        (await RowAsync(parent, "xrmv_treebudget_base"))["xrmv_treebudget_base"].Should().Be(125m);
        // source-filter.xaml counts the orders of the accounts of the tree whose budget has a value: C2's two orders do not count.
        (await RollupAsync(parent, "xrmv_treeorders")).Should().Be((3, 1));
        (await RollupAsync(child2, "xrmv_treeorders")).Should().Be((0, 1));
    }

    [Fact]
    public async Task Hierarchy_loop_and_depth_states_leave_other_rows_computed()
    {
        await CurrenciesAsync();
        // A loop (possible after an import): L1 is the parent of L2 and L2 the parent of L1; L3 hangs below L2.
        var l1 = await AccountAsync("L1", budget: 1m, currency: _euro, rate: 1m);
        var l2 = await AccountAsync("L2", l1, 2m, _euro, 1m);
        await ExecAsync("UPDATE [data].[account] SET parentaccountid = @l2 WHERE accountid = @l1", ("@l1", l1), ("@l2", l2));
        var l3 = await AccountAsync("L3", l2, 4m, _euro, 1m);
        // A chain of 102 rows: the top one has 101 levels below it, one more than a job walks.
        var chain = new List<Guid> { await AccountAsync("D0", budget: 1m, currency: _euro, rate: 1m) };
        for (var i = 1; i <= 101; i++) chain.Add(await AccountAsync("D" + i, chain[^1], 1m, _euro, 1m));
        var plain = await AccountAsync("Plain", budget: 9m, currency: _euro, rate: 1m);

        await AllAsync("xrmv_treebudget");

        (await RollupAsync(l1, "xrmv_treebudget")).Should().Be((null, 6));
        (await RollupAsync(l2, "xrmv_treebudget")).Should().Be((null, 6));
        (await RollupAsync(l3, "xrmv_treebudget")).Should().Be((4m, 1));
        (await RollupAsync(chain[0], "xrmv_treebudget")).Should().Be((null, 5));
        (await RollupAsync(chain[1], "xrmv_treebudget")).Should().Be((101m, 1), "100 levels below it are walked");
        (await RollupAsync(chain[^1], "xrmv_treebudget")).Should().Be((1m, 1));
        (await RollupAsync(plain, "xrmv_treebudget")).Should().Be((9m, 1));
    }

    [Fact]
    public async Task Currency_rollup_sums_base_and_converts_at_target_rate()
    {
        await CurrenciesAsync();
        var a = await AccountAsync("A", currency: _euro, rate: 2m);
        var none = await AccountAsync("None");
        // Orders in two currencies: their base values are summed.
        await OrderAsync(a, status: 2, priceBase: 10.004m, currency: _euro);
        await OrderAsync(a, status: 2, priceBase: 5.50m, currency: _dollar);
        await OrderAsync(a, status: 1, priceBase: 99m, currency: _dollar);
        await OrderAsync(none, status: 2, priceBase: 4m, currency: _euro);

        await AllAsync("xrmv_wonprice");

        var row = await RowAsync(a, "xrmv_wonprice", "xrmv_wonprice_base", "xrmv_wonprice_state");
        row["xrmv_wonprice_base"].Should().Be(15.504m, "each row converts its full value at its own rate, with no cut (parity run)");
        row["xrmv_wonprice"].Should().Be(31.008m, "the base sum at the target row's rate, not rounded to the precision (parity run)");
        row["xrmv_wonprice_state"].Should().Be(1);
        var missing = await RowAsync(none, "xrmv_wonprice", "xrmv_wonprice_base", "xrmv_wonprice_state");
        missing["xrmv_wonprice_state"].Should().Be(7);
        missing["xrmv_wonprice"].Should().BeNull();
        missing["xrmv_wonprice_base"].Should().BeNull();
    }

    [Fact]
    public async Task Overflow_gives_state_2_and_null()
    {
        var big = await AccountAsync("Big");
        await OrderAsync(big, 80m, 2);
        await OrderAsync(big, 30m, 2);
        var small = await AccountAsync("Small");
        await OrderAsync(small, 80m, 2);

        await AllAsync("xrmv_capped");

        (await RollupAsync(big, "xrmv_capped")).Should().Be((null, 2));
        (await RollupAsync(small, "xrmv_capped")).Should().Be((80m, 1));
    }

    [Fact]
    public async Task CalculateOne_refuses_over_50000_related_rows_and_depth_10()
    {
        await CurrenciesAsync();
        var limits = new RollupLimits(MaxRelatedRows: 3, MaxOnlineDepth: 2);
        var many = await AccountAsync("Many");
        for (var i = 0; i < 4; i++) await OrderAsync(many, 1m, 2);
        var few = await AccountAsync("Few");
        for (var i = 0; i < 3; i++) await OrderAsync(few, 1m, 2);
        var top = await AccountAsync("Top", budget: 1m, currency: _euro, rate: 1m);
        var level1 = await AccountAsync("L1", top, 1m, _euro, 1m);
        var level2 = await AccountAsync("L2", level1, 1m, _euro, 1m);
        await AccountAsync("L3", level2, 1m, _euro, 1m);

        var calculator = Calculator(limits);
        var rows = (await FluentActions.Awaiting(() => calculator.CalculateOneAsync(new EntityReference(Account, many), "xrmv_ordercount", CancellationToken.None))
            .Should().ThrowAsync<XrmdFaultException>()).Which;
        rows.ErrorCode.Should().Be(unchecked((int)0x8004E025));
        rows.Message.Should().Contain("account.xrmv_ordercount");
        var depth = (await FluentActions.Awaiting(() => calculator.CalculateOneAsync(new EntityReference(Account, top), "xrmv_treebudget", CancellationToken.None))
            .Should().ThrowAsync<XrmdFaultException>()).Which;
        depth.ErrorCode.Should().Be(unchecked((int)0x80060547));
        (await RollupAsync(top, "xrmv_treebudget")).Should().Be((null, null), "a refused calculation writes nothing");

        // Within the limits: the row is calculated and returned, other rows and other rollups are left alone.
        var entity = await calculator.CalculateOneAsync(new EntityReference(Account, few), "xrmv_ordercount", CancellationToken.None);
        entity.LogicalName.Should().Be(Account);
        entity.Id.Should().Be(few);
        entity["xrmv_ordercount"].Should().Be(3);
        entity["xrmv_ordercount_state"].Should().Be(1);
        entity["xrmv_ordercount_date"].Should().Be(Now.UtcDateTime);
        (await RollupAsync(few, "xrmv_ordertotal")).Should().Be((null, null));
        (await RollupAsync(many, "xrmv_ordercount")).Should().Be((null, null));
        var level = await calculator.CalculateOneAsync(new EntityReference(Account, level1), "xrmv_treebudget", CancellationToken.None);
        level["xrmv_treebudget"].Should().Be(new Money(3m));
        level["xrmv_treebudget_base"].Should().Be(new Money(3m));

        // An unknown row, and a column that is not a rollup.
        (await FluentActions.Awaiting(() => calculator.CalculateOneAsync(new EntityReference(Account, Guid.NewGuid()), "xrmv_ordercount", CancellationToken.None))
            .Should().ThrowAsync<XrmdFaultException>()).Which.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await FluentActions.Awaiting(() => calculator.CalculateOneAsync(new EntityReference(Account, few), "name", CancellationToken.None))
            .Should().ThrowAsync<XrmdFaultException>()).Which.ErrorCode.Should().Be(unchecked((int)0x80060554));
    }

    [Fact]
    public async Task ChangedSince_writes_differing_and_touched_rows()
    {
        await CurrenciesAsync();
        var a = await AccountAsync("A");
        var b = await AccountAsync("B");
        var c = await AccountAsync("C");
        var moved = await OrderAsync(a, 10m, 2);
        await OrderAsync(b, 5m, 2);
        await AllAsync("xrmv_ordertotal");
        var since = new DateTime(2026, 6, 1);

        // The order moves from A to B (only B is touched: A differs), and C is edited without a change of its value.
        await ExecAsync("UPDATE [data].[xrmv_calcorder] SET xrmv_accountid = @b, modifiedon = @m WHERE xrmv_calcorderid = @o",
            ("@b", b), ("@m", since.AddHours(1)), ("@o", moved));
        await ExecAsync("UPDATE [data].[account] SET modifiedon = @m WHERE accountid = @c", ("@m", since.AddHours(1)), ("@c", c));

        (await Calculator().RecalculateAsync(Account, "xrmv_ordertotal", new RollupScope.ChangedSince(since), CancellationToken.None))
            .Should().Be(3);
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((0m, 1));
        (await RollupAsync(b, "xrmv_ordertotal")).Should().Be((15m, 1));
        (await RollupAsync(c, "xrmv_ordertotal")).Should().Be((0m, 1));

        // Over the hierarchy: a change of a grandchild touches its ancestors.
        var top = await AccountAsync("Top", budget: 1m, currency: _euro, rate: 1m);
        var middle = await AccountAsync("Middle", top, 1m, _euro, 1m);
        var leaf = await AccountAsync("Leaf", middle, 1m, _euro, 1m);
        await AllAsync("xrmv_treebudget");
        await ExecAsync("UPDATE [data].[account] SET modifiedon = @m WHERE accountid = @l", ("@m", since.AddHours(1)), ("@l", leaf));
        (await Calculator().RecalculateAsync(Account, "xrmv_treebudget", new RollupScope.ChangedSince(since), CancellationToken.None))
            .Should().Be(4, "the leaf, its two ancestors and C, touched since then");
        (await RollupAsync(top, "xrmv_treebudget")).Should().Be((3m, 1));
    }

    [Fact]
    public async Task Values_are_cut_not_rounded_before_the_sum()
    {
        var a = await AccountAsync("A");
        await OrderAsync(a, status: 2, exact: 10.009m);
        await OrderAsync(a, status: 2, exact: 0.004m);

        await AllAsync("xrmv_exacttotal");

        (await RollupAsync(a, "xrmv_exacttotal")).Should().Be((10.00m, 1), "10.009 is cut to 10.00 and 0.004 to 0.00 (rounding would give 10.01)");
    }

    [Fact]
    public async Task Count_of_a_nullable_column_skips_null_rows()
    {
        var a = await AccountAsync("A");
        await OrderAsync(a, 1m);
        await OrderAsync(a, null);
        await OrderAsync(a, 2m);

        await AllAsync("xrmv_amountcount");
        await AllAsync("xrmv_ordercount");

        (await RollupAsync(a, "xrmv_amountcount")).Should().Be((2, 1));
        (await RollupAsync(a, "xrmv_ordercount")).Should().Be((3, 1));
    }

    [Fact]
    public async Task Overflow_of_the_column_type_and_of_the_base_range_give_state_2()
    {
        await CurrenciesAsync();
        _store.GetEntity(Account)!.Attributes.OfType<IntegerAttributeMetadata>().Single(x => x.LogicalName == "xrmv_qtysum")
            .MaxValue.Should().BeNull("the type check, not the range, must catch this one");
        var whole = await AccountAsync("Whole");
        await OrderAsync(whole, status: 2, quantity: 2_000_000_000);
        await OrderAsync(whole, status: 2, quantity: 2_000_000_000);
        var fits = await AccountAsync("Fits");
        await OrderAsync(fits, status: 2, quantity: 2_000_000_000);
        // Three base values of 330,000,000,000,000: their sum is outside the base range (±922,337,203,685,477).
        var rich = await AccountAsync("Rich", currency: _euro, rate: 1m);
        for (var i = 0; i < 3; i++) await OrderAsync(rich, status: 2, priceBase: 330_000_000_000_000m, currency: _euro);

        await AllAsync("xrmv_qtysum");
        await AllAsync("xrmv_wonprice");

        (await RollupAsync(whole, "xrmv_qtysum")).Should().Be((null, 2));
        (await RollupAsync(fits, "xrmv_qtysum")).Should().Be((2_000_000_000, 1));
        var row = await RowAsync(rich, "xrmv_wonprice", "xrmv_wonprice_base", "xrmv_wonprice_state");
        row["xrmv_wonprice_state"].Should().Be(2);
        row["xrmv_wonprice"].Should().BeNull();
        row["xrmv_wonprice_base"].Should().BeNull();
    }

    [Fact]
    public void Jobs_have_no_command_timeout_and_the_online_calculation_a_bounded_one()
    {
        var defaults = Calculator();
        defaults.CommandTimeoutSeconds(online: false).Should().Be(0, "a mass or incremental run may take as long as it needs");
        defaults.CommandTimeoutSeconds(online: true).Should().Be(120);
        new RollupLimits().Should().Be(new RollupLimits(50_000, 10, 0, 120));

        var custom = Calculator(new RollupLimits(JobCommandTimeoutSeconds: 600, OnlineCommandTimeoutSeconds: 5));
        custom.CommandTimeoutSeconds(online: false).Should().Be(600);
        custom.CommandTimeoutSeconds(online: true).Should().Be(5);
    }
}
