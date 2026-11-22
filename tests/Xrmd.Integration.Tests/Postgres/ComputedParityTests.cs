using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using static Xrmd.Integration.Tests.CalculatedParityExport;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7a, Task 7: the parity data of plans 2.12a/b/c give the values Dataverse recorded on either engine. The export's tables
/// are made through the metadata API (<see cref="CalculatedParityExport.CreateThroughMetadataAsync"/>) in a database of the test's
/// own, the rows through the organization service, and the values read back:
/// <list type="bullet">
/// <item>the calculated and formula columns of the recorded orders (tests/fixtures/calculated/real/xrmv_calcorders.csv): Power Fx
/// blank is 0 (<c>xrmv_fxlabel</c> of an order without quantity ends in "-0"), <c>DateAdd</c> keeps UTC (<c>xrmv_fxplus3</c>);</item>
/// <item>the rollups of the licensed run of plan 2.12b (calculated/buttami/findings.md, rollup-values.json): the export's four and
/// the designer's average, first order and hierarchy count, all with state 1 — the empty set too (0 or null) — and an average
/// that counts a row without amount as 0.</item>
/// </list>
/// The same assertions run on SQL Server and on PostgreSQL (<c>XRMD_TEST_PROVIDER</c>).
/// </summary>
[Collection("xrmd")]
public sealed class ComputedParityTests(XrmdFixture fx) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _app = null!;

    private MetadataStore Store => (MetadataStore)_app.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => TestServices.NewOrganizationService(_app.Services, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        _app = fx.WithOwnDatabase(_ => { });
        var connectionString = _app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
        await _app.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CreateThroughMetadataAsync(Store, connectionString);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Recorded_calculated_and_formula_values()
    {
        var accounts = Recorded("accounts.csv");
        var orders = Recorded("xrmv_calcorders.csv");
        var accountIds = new Dictionary<string, Guid>();
        foreach (var account in accounts.OrderBy(a => a["parentaccount"].Length))
        {
            var entity = new Entity("account") { ["name"] = account["name"] };
            if (account["xrmv_budget"].Length > 0) entity["xrmv_budget"] = new Money(Dec(account["xrmv_budget"]));
            if (account["parentaccount"].Length > 0) entity["parentaccountid"] = new EntityReference("account", accountIds[account["parentaccount"]]);
            accountIds[account["name"]] = await Admin.CreateAsync(entity);
        }

        var table = Store.GetEntity(Order)!;
        var calculated = table.Attributes.Where(a => a.SourceType == ComputedColumnRules.Calculated).Select(a => a.LogicalName).ToList();
        calculated.Should().HaveCount(27, "the export's 25 calculated columns and the _base of its two Currency ones");
        var formulas = table.Attributes.Where(a => a.SourceType == ComputedColumnRules.Formula).Select(a => a.LogicalName).ToList();
        formulas.Should().BeEquivalentTo(Formulas.Keys);
        var columns = calculated.Except(NowColumns).Concat(formulas).Order(StringComparer.Ordinal).ToList();

        var rows = new Dictionary<string, Entity>();
        foreach (var recorded in orders)
        {
            var code = recorded["xrmv_code"];
            var id = await Admin.CreateAsync(NewOrder(recorded, accountIds, "recorded " + code, TimeSpan.Zero));
            var row = rows[code] = await Admin.RetrieveAsync(Order, id, new ColumnSet(true));
            foreach (var column in columns)
                Text(row, column).Should().Be(recorded[column], "Dataverse recorded {0}.{1} = '{2}'", code, column, recorded[column]);
        }

        // The two Power Fx rules of the recording, named on the computed values: a blank quantity is 0 in Text(), DateAdd adds
        // days to the UTC time.
        rows["ORD-002"].GetAttributeValue<string>("xrmv_fxlabel").Should().Be("ORD-002-0");
        rows["AB"].GetAttributeValue<DateTime>("xrmv_fxplus3").Should().Be(new DateTime(2026, 11, 18, 23, 0, 0, DateTimeKind.Utc));
        rows["AB"].GetAttributeValue<DateTime>("xrmv_fxplus3").Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Rollups_of_the_licensed_run()
    {
        // The designer's rollups of the run (plan 2.12b, Task 7), beside the export's four.
        foreach (var (column, attribute, file) in new (string, AttributeMetadata, string)[]
                 {
                     ("xrmv_avgamount", new DecimalAttributeMetadata { Precision = 2 }, "account-xrmv_avgamount.xaml"),
                     ("xrmv_firstorder", new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal },
                         Path.Combine("designer", "account-xrmv_firstorder.xaml")),
                     ("xrmv_treeorders", new IntegerAttributeMetadata(), Path.Combine("designer", "account-xrmv_treeorders.xaml")),
                 })
        {
            attribute.LogicalName = attribute.SchemaName = column;
            attribute.DisplayName = SystemAttributes.CreateLabel(column, 1033);
            attribute.SourceType = RollupColumns.Rollup;
            ComputedColumnRules.SetFormula(attribute, File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "rollup", "real", file)));
            await Store.AddAttributeAsync("account", attribute);
        }
        await Store.RollupJobNotifications;

        // The accounts and orders of the run (invented data; one currency here: the run's dollar rows only move the Currency SUM).
        DateTime At(int day, int hour) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);
        var parent = await AccountAsync("Contoso test parent", null, 10000m);
        var child = await AccountAsync("Contoso test Child", parent, 2500m);
        var fabrikam = await AccountAsync("Fabrikam test", null, null);
        var northwind = await AccountAsync("Northwind test", parent, 500m);
        var empty = await AccountAsync("Empty test", null, 100m);
        await OrderAsync(child, 1500m, 2, At(9, 23));      // ORD-001
        await OrderAsync(parent, 400m, 1, At(16, 23));     // ORD-002
        await OrderAsync(null, null, 2, At(16, 23));       // ORD-003, no account
        await OrderAsync(fabrikam, null, 1, At(15, 23));   // AB
        await OrderAsync(child, 300m, 2, At(18, 8));       // ORD-005
        await OrderAsync(child, null, 2, At(17, 13));      // ORD-006
        await OrderAsync(parent, 150m, 1, At(15, 9));      // ORD-007

        string[] columns = ["xrmv_ordertotal", "xrmv_ordercount", "xrmv_lastorder", "xrmv_treebudget", "xrmv_avgamount", "xrmv_firstorder", "xrmv_treeorders"];
        var expected = new Dictionary<Guid, object?[]>
        {
            // treebudget: 10000 + 2500 + 500 (findings 3); avgamount: (400 + 150) / 2; firstorder: the Open order above 100 (7).
            [parent] = [0m, 2, At(16, 23), 13000m, 275m, At(15, 9), 5],
            // AVG counts the order without amount as 0: (1500 + 300 + 0) / 3 = 600 (findings 5); Decimal SUM 1500 + 300 (9).
            [child] = [1800m, 3, At(18, 8), 2500m, 600m, null, 3],
            // A hierarchy SUM over an empty budget is 0 (4); the source filter drops the row itself (8).
            [fabrikam] = [0m, 1, At(15, 23), 0m, 0m, null, 0],
            [northwind] = [0m, 0, null, 500m, 0m, null, 0],
            // The empty set: state 1, 0 for SUM, COUNT and AVG, null for MIN and MAX (findings 1).
            [empty] = [0m, 0, null, 100m, 0m, null, 0],
        };
        foreach (var (account, values) in expected)
            for (var i = 0; i < columns.Length; i++)
            {
                var row = (Entity)(await Admin.ExecuteAsync(new CalculateRollupFieldRequest
                {
                    Target = new EntityReference("account", account), FieldName = columns[i],
                })).Results["Entity"];
                var value = row.Contains(columns[i]) ? row[columns[i]] switch { Money m => m.Value, var v => v } : null;
                value.Should().Be(values[i], "{0} of {1}", columns[i], account);
                row[columns[i] + RollupColumns.StateSuffix].Should().Be(1, "{0} of {1}", columns[i], account);
            }
    }

    private Task<Guid> AccountAsync(string name, Guid? parent, decimal? budget)
    {
        var account = new Entity("account") { ["name"] = name };
        if (parent is { } p) account["parentaccountid"] = new EntityReference("account", p);
        if (budget is { } b) account["xrmv_budget"] = new Money(b);
        return Admin.CreateAsync(account);
    }

    private Task<Guid> OrderAsync(Guid? account, decimal? amount, int status, DateTime startedOn)
    {
        var order = new Entity(Order)
        {
            ["xrmv_name"] = "order", ["xrmv_status"] = new OptionSetValue(status), ["xrmv_startedon"] = startedOn,
        };
        if (account is { } a) order["xrmv_accountid"] = new EntityReference("account", a);
        if (amount is { } m) order["xrmv_amount"] = m;
        return Admin.CreateAsync(order);
    }
}
