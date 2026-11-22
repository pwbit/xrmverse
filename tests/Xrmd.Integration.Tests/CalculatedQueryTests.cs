using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Integration.Tests.Tds;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The table <c>xrmv_calcorder</c> of the calculated column fixtures (tests/fixtures/calculated, plan 2.12a), with each fixture as a
/// calculated column, defined through the server's metadata store and its XAML translator.
/// </summary>
internal static class CalculatedOrders
{
    public const string Table = "xrmv_calcorder";

    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static string Xaml(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", name + ".xaml"));

    private static T Calculated<T>(T attribute, string xaml) where T : AttributeMetadata
    {
        attribute.SourceType = 1;
        attribute.DisplayName = SystemAttributes.CreateLabel(attribute.LogicalName, 1033);
        ComputedColumnRules.SetFormula(attribute, xaml);
        return attribute;
    }

    private static OptionSetMetadata Options(params int[] values) =>
        new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(SystemAttributes.CreateLabel("Option " + v, 1033), v)).ToList()))
        {
            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
        };

    /// <summary>Creates the table unless <paramref name="store"/> has it.</summary>
    public static async Task EnsureAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Table) is not null) return;
            var entity = new EntityMetadata { LogicalName = Table, SchemaName = "xrmv_CalcOrder", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes(
            [
                new MoneyAttributeMetadata { LogicalName = "xrmv_amount", SchemaName = "xrmv_Amount", PrecisionSource = 2 },
                new IntegerAttributeMetadata { LogicalName = "xrmv_quantity", SchemaName = "xrmv_Quantity" },
                new DateTimeAttributeMetadata { LogicalName = "xrmv_orderdate", SchemaName = "xrmv_OrderDate", DateTimeBehavior = DateTimeBehavior.UserLocal },
                new BooleanAttributeMetadata
                {
                    LogicalName = "xrmv_rush", SchemaName = "xrmv_Rush",
                    OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(SystemAttributes.CreateLabel("Yes", 1033), 1),
                        new OptionMetadata(SystemAttributes.CreateLabel("No", 1033), 0)),
                },
                new PicklistAttributeMetadata { LogicalName = "xrmv_status", SchemaName = "xrmv_Status", OptionSet = Options(100000000, 100000001) },
                new LookupAttributeMetadata { LogicalName = "xrmv_accountid", SchemaName = "xrmv_AccountId", Targets = ["account"] },
            ]);
            await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_Name", MaxLength = 100 });

            AttributeMetadata[] calculated =
            [
                Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total", SchemaName = "xrmv_Total", PrecisionSource = 0, Precision = 2 },
                    Xaml("arithmetic-money")),
                Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", SchemaName = "xrmv_Label", MaxLength = 200 }, Xaml("concat-related")),
                Calculated(new DateTimeAttributeMetadata
                {
                    LogicalName = "xrmv_followup", SchemaName = "xrmv_Followup", DateTimeBehavior = DateTimeBehavior.UserLocal,
                }, Xaml("conditional-dates")),
                Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_age", SchemaName = "xrmv_Age" }, Xaml("diff-now")),
                Calculated(new BooleanAttributeMetadata
                {
                    LogicalName = "xrmv_isbig", SchemaName = "xrmv_IsBig",
                    OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(SystemAttributes.CreateLabel("Yes", 1033), 1),
                        new OptionMetadata(SystemAttributes.CreateLabel("No", 1033), 0)),
                }, Xaml("boolean-and-or")),
                Calculated(new PicklistAttributeMetadata
                {
                    LogicalName = "xrmv_tier", SchemaName = "xrmv_Tier", OptionSet = Options(100000000, 100000001, 100000002),
                }, Xaml("picklist-branches")),
                Calculated(new StringAttributeMetadata { LogicalName = "xrmv_code", SchemaName = "xrmv_Code", MaxLength = 100 }, Xaml("trim")),
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_double", SchemaName = "xrmv_Double", Precision = 2 }, Xaml("chained")),
                // A chain of two: xrmv_quarter = xrmv_double / 2 = (xrmv_amount * xrmv_quantity) / 2 / 2.
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_quarter", SchemaName = "xrmv_Quarter", Precision = 2 },
                    Xaml("chained").Replace("xrmv_double", "xrmv_quarter").Replace("\"xrmv_total\"", "\"xrmv_double\"")),
                // xrmv_ratio = xrmv_quantity / 3, in a Decimal of Precision 10 (all 10 decimals kept, K8).
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_ratio", SchemaName = "xrmv_Ratio", Precision = 10 },
                    Xaml("chained").Replace("xrmv_double", "xrmv_ratio").Replace("\"xrmv_total\"", "\"xrmv_quantity\"")
                        .Replace("WorkflowPropertyType.Decimal, \"2\"", "WorkflowPropertyType.Decimal, \"3\"")),
                // xrmv_big = xrmv_amount * xrmv_quantity (a decimal(38, 2)), xrmv_square = xrmv_big * xrmv_big: an operand too large
                // for the product gives null (K8) instead of failing the query.
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_big", SchemaName = "xrmv_Big", Precision = 2 },
                    Xaml("arithmetic-money").Replace("xrmv_total", "xrmv_big").Replace("Value=\"mxs:Money\"", "Value=\"x:Decimal\"")),
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_square", SchemaName = "xrmv_Square", Precision = 2 },
                    Xaml("arithmetic-money").Replace("xrmv_total", "xrmv_square").Replace("Attribute=\"xrmv_amount\"", "Attribute=\"xrmv_big\"")
                        .Replace("Attribute=\"xrmv_quantity\"", "Attribute=\"xrmv_big\"").Replace("Value=\"mxs:Money\"", "Value=\"x:Decimal\"")),
                // One empty branch beside values (final review I2): the branch reads as null, the others as usual.
                Calculated(new PicklistAttributeMetadata
                {
                    LogicalName = "xrmv_tiermaybe", SchemaName = "xrmv_TierMaybe", OptionSet = Options(100000000, 100000001, 100000002),
                }, Xaml("picklist-branches").Replace("xrmv_tier", "xrmv_tiermaybe").Replace("Value=\"[SetAttributeValueStep1_1]\"", "Value=\"[Nothing]\"")),
                Calculated(new DateTimeAttributeMetadata
                {
                    LogicalName = "xrmv_followupmaybe", SchemaName = "xrmv_FollowupMaybe", DateTimeBehavior = DateTimeBehavior.UserLocal,
                }, Xaml("conditional-dates").Replace("xrmv_followup", "xrmv_followupmaybe").Replace("Value=\"[SetAttributeValueStep1_1]\"", "Value=\"[Nothing]\"")),
            ];
            foreach (var a in calculated) await store.AddAttributeAsync(Table, a);
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>A record of the table: the values the fixtures read.</summary>
    public static Entity Order(string name, decimal amount, int quantity, DateTime orderDate, bool rush, int status, Guid? account = null)
    {
        var e = new Entity(Table)
        {
            ["xrmv_name"] = name,
            ["xrmv_amount"] = new Money(amount),
            ["xrmv_quantity"] = quantity,
            ["xrmv_orderdate"] = orderDate,
            ["xrmv_rush"] = rush,
            ["xrmv_status"] = new OptionSetValue(status),
        };
        if (account is { } id) e["xrmv_accountid"] = new EntityReference("account", id);
        return e;
    }

    // ---- the values K7 gives (computed here in C#, independently of the server) -------------------------------------------

    public static string Label(string name, string? accountName) => $"{name} / {accountName}";

    public static DateTime Followup(decimal amount, DateTime orderDate) =>
        amount > 1000 ? orderDate.AddDays(2) : amount > 100 ? orderDate.AddDays(7) : orderDate.AddMonths(1);

    public static bool IsBig(decimal amount, int quantity, bool rush) => (amount > 1000 && quantity >= 10) || rush;

    public static int Tier(int status) => status == 100000001 ? 100000002 : 100000000;

    /// <summary>TRIMLEFT(TRIMRIGHT(name, 2), 3).</summary>
    public static string Code(string name)
    {
        var right = name.Length <= 2 ? "" : name[..^2];
        return right.Length <= 3 ? "" : right[3..];
    }

    /// <summary>DIFFINDAYS(orderdate, now): the number of day boundaries between them.</summary>
    public static int Age(DateTime orderDate, DateTime now) => (now.Date - orderDate.Date).Days;
}

/// <summary>
/// Calculated columns read through the query compiler on SQL Server (plan 2.12a, Task 3): the fixtures' values per K7, a parent lookup
/// that is null or points to a deleted row, chains, and a calculated column used at once in the projection, a filter, an order and a
/// grouping, through FetchXML and the Web API.
/// </summary>
[Collection("xrmd")]
public sealed class CalculatedQueryTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Set = "xrmv_calcorders";

    private readonly string _tag = "q" + Guid.NewGuid().ToString("N")[..8];
    private OrganizationService _admin = null!;

    public async Task InitializeAsync()
    {
        await CalculatedOrders.EnsureAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _admin = ServiceFor(fx, fx.AdminObjectId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Guid> AccountAsync(string name) => _admin.CreateAsync(new Entity("account") { ["name"] = name });

    private Task<Entity> RetrieveAsync(Guid id) => _admin.RetrieveAsync(CalculatedOrders.Table, id, new ColumnSet(true));

    private async Task<JsonObject> GetAsync(string pathAndQuery)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters) =>
        TestSql.ExecAsync(fx.ConnectionString, sql, [.. parameters.Select(p => (p.Name, (object?)p.Value))]);

    [Fact]
    public async Task Retrieve_returns_computed_values()
    {
        var accountName = _tag + " Contoso";
        var account = await AccountAsync(accountName);
        var date = new DateTime(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);
        var big = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " widget order", 1500m, 12, date, rush: false, status: 100000001, account));
        // A name of two characters (no tag): TRIMRIGHT(name, 2) leaves it empty.
        var small = await _admin.CreateAsync(CalculatedOrders.Order("ab", 50m, 1, new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            rush: true, status: 100000000));
        var middle = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " mid", 500m, 2, date, rush: false, status: 100000000));

        var before = DateTime.UtcNow;
        var e = await RetrieveAsync(big);
        var after = DateTime.UtcNow;

        // arithmetic-money: xrmv_amount * xrmv_quantity, rounded to the column's 2 decimals; its base in the base currency (rate 1).
        e.GetAttributeValue<Money>("xrmv_total").Value.Should().Be(18000m);
        e.GetAttributeValue<Money>("xrmv_total_base").Value.Should().Be(18000m);
        e.GetAttributeValue<string>("xrmv_label").Should().Be(CalculatedOrders.Label(_tag + " widget order", accountName));
        e.GetAttributeValue<DateTime>("xrmv_followup").Should().Be(CalculatedOrders.Followup(1500m, date));
        e.GetAttributeValue<int>("xrmv_age").Should().BeOneOf(CalculatedOrders.Age(date, before), CalculatedOrders.Age(date, after));
        e.GetAttributeValue<bool>("xrmv_isbig").Should().BeTrue();
        e.GetAttributeValue<OptionSetValue>("xrmv_tier").Value.Should().Be(100000002);
        e.GetAttributeValue<string>("xrmv_code").Should().Be(CalculatedOrders.Code(_tag + " widget order"));
        e.GetAttributeValue<decimal>("xrmv_double").Should().Be(9000m);
        e.FormattedValues.Should().ContainKey("xrmv_isbig");

        var s = await RetrieveAsync(small);
        s.GetAttributeValue<Money>("xrmv_total").Value.Should().Be(50m);
        s.GetAttributeValue<DateTime>("xrmv_followup").Should().Be(new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc), "DATEADD month keeps the last day");
        s.GetAttributeValue<bool>("xrmv_isbig").Should().BeTrue("rush");
        s.GetAttributeValue<OptionSetValue>("xrmv_tier").Value.Should().Be(100000000);
        // TRIMRIGHT of 2 characters or more than the text has leaves it empty (K7).
        (s.GetAttributeValue<string>("xrmv_code") ?? "").Should().BeEmpty();
        s.GetAttributeValue<string>("xrmv_label").Should().Be("ab / ", "CONCAT reads the null parent name as empty");

        var m = await RetrieveAsync(middle);
        m.GetAttributeValue<DateTime>("xrmv_followup").Should().Be(CalculatedOrders.Followup(500m, date));
        m.GetAttributeValue<bool>("xrmv_isbig").Should().BeFalse();
        m.GetAttributeValue<Money>("xrmv_total").Value.Should().Be(1000m);
    }

    [Fact]
    public async Task Parent_lookup_null_gives_null_source()
    {
        var date = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var none = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " none", 10m, 1, date, false, 100000000));
        var account = await AccountAsync(_tag + " gone");
        var dangling = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " dangling", 10m, 1, date, false, 100000000, account));
        // The parent row disappears without the lookup being cleared (a row deleted outside the server).
        await ExecuteAsync($"UPDATE data.{CalculatedOrders.Table} SET xrmv_accountid = @id WHERE xrmv_calcorderid = @row",
            ("@id", Guid.NewGuid()), ("@row", dangling));

        // CONCAT reads the missing parent name as empty: the row is returned, with its value.
        (await RetrieveAsync(none)).GetAttributeValue<string>("xrmv_label").Should().Be(CalculatedOrders.Label(_tag + " none", null));
        (await RetrieveAsync(dangling)).GetAttributeValue<string>("xrmv_label").Should().Be(CalculatedOrders.Label(_tag + " dangling", null));

        var fetch = $"""
            <fetch><entity name="{CalculatedOrders.Table}"><attribute name="xrmv_label" />
              <filter><condition attribute="xrmv_name" operator="like" value="{_tag}%" /></filter>
              <order attribute="xrmv_name" /></entity></fetch>
            """;
        var rows = (await _admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities;
        rows.Select(r => r.GetAttributeValue<string>("xrmv_label")).Should().Equal(
            CalculatedOrders.Label(_tag + " dangling", null), CalculatedOrders.Label(_tag + " none", null));
    }

    [Fact]
    public async Task Calculated_in_select_filter_order_and_groupby()
    {
        var a = await AccountAsync(_tag + " Alpha");
        var b = await AccountAsync(_tag + " Beta");
        var date = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var name = _tag + " o";
        foreach (var account in new[] { a, b, b, a, b })
            await _admin.CreateAsync(CalculatedOrders.Order(name, 10m, 1, date, false, 100000000, account));
        await _admin.CreateAsync(CalculatedOrders.Order(name, 10m, 1, date, false, 100000000));
        var alpha = CalculatedOrders.Label(name, _tag + " Alpha");
        var beta = CalculatedOrders.Label(name, _tag + " Beta");

        // FetchXML: select, filter and order on the label (whose " / " is a parameter of the expression).
        var fetch = $"""
            <fetch><entity name="{CalculatedOrders.Table}"><attribute name="xrmv_label" />
              <filter><condition attribute="xrmv_name" operator="eq" value="{name}" />
                <condition attribute="xrmv_label" operator="like" value="%/ {_tag}%" /></filter>
              <order attribute="xrmv_label" descending="true" /></entity></fetch>
            """;
        var rows = (await _admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities;
        rows.Select(r => r.GetAttributeValue<string>("xrmv_label")).Should().Equal(beta, beta, beta, alpha, alpha);

        // FetchXML aggregate: grouped by the label (no subquery in the GROUP BY), ordered by it, filtered on it.
        var grouped = $"""
            <fetch aggregate="true"><entity name="{CalculatedOrders.Table}">
              <attribute name="xrmv_label" alias="label" groupby="true" />
              <attribute name="xrmv_total" alias="sum" aggregate="sum" />
              <attribute name="xrmv_calcorderid" alias="n" aggregate="count" />
              <filter><condition attribute="xrmv_name" operator="eq" value="{name}" />
                <condition attribute="xrmv_label" operator="ne" value="{CalculatedOrders.Label(name, null)}" /></filter>
              <order alias="label" /></entity></fetch>
            """;
        var groups = (await _admin.RetrieveMultipleAsync(new FetchExpression(grouped))).Entities;
        groups.Select(g => ((string)g.GetAttributeValue<AliasedValue>("label").Value, (int)g.GetAttributeValue<AliasedValue>("n").Value,
                ((Money)g.GetAttributeValue<AliasedValue>("sum").Value).Value))
            .Should().Equal((alpha, 2, 20m), (beta, 3, 30m));

        // Web API: $select, $filter and $orderby, then $apply groupby.
        var filter = Uri.EscapeDataString($"xrmv_name eq '{name}' and contains(xrmv_label,'{_tag} Beta')");
        var json = await GetAsync($"{Set}?$select=xrmv_label,xrmv_total&$filter={filter}&$orderby=xrmv_label asc");
        json["value"]!.AsArray().Select(v => v!["xrmv_label"]!.GetValue<string>()).Should().Equal(beta, beta, beta);
        json["value"]![0]!["xrmv_total"]!.GetValue<decimal>().Should().Be(10m);

        var apply = Uri.EscapeDataString($"filter(xrmv_name eq '{name}' and xrmv_label ne '{CalculatedOrders.Label(name, null)}')" +
                                         "/groupby((xrmv_label),aggregate($count as c))");
        var applied = await GetAsync($"{Set}?$apply={apply}&$orderby=xrmv_label desc");
        applied["value"]!.AsArray().Select(v => (v!["xrmv_label"]!.GetValue<string>(), v["c"]!.GetValue<int>()))
            .Should().Equal((beta, 3), (alpha, 2));
    }

    [Fact]
    public async Task Chained_calculated_values()
    {
        var date = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " chain", 33.33m, 3, date, false, 100000000));

        var e = await _admin.RetrieveAsync(CalculatedOrders.Table, id, new ColumnSet("xrmv_total", "xrmv_double", "xrmv_quarter"));

        // 33.33 * 3 = 99.99; / 2 = 49.995 → 50.00 (rounded to 2 decimals, K8); / 2 again = 25.00.
        e.GetAttributeValue<Money>("xrmv_total").Value.Should().Be(99.99m);
        e.GetAttributeValue<decimal>("xrmv_double").Should().Be(50.00m);
        e.GetAttributeValue<decimal>("xrmv_quarter").Should().Be(25.00m);

        // A filter on the end of the chain.
        var fetch = $"""
            <fetch><entity name="{CalculatedOrders.Table}"><attribute name="xrmv_name" />
              <filter><condition attribute="xrmv_name" operator="like" value="{_tag}%" />
                <condition attribute="xrmv_quarter" operator="eq" value="25" /></filter></entity></fetch>
            """;
        (await _admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Select(r => r.Id).Should().Equal(id);
    }

    [Fact]
    public async Task Decimal_division_keeps_the_column_precision()
    {
        var date = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        var id = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " third", 1m, 1, date, false, 100000000));

        var e = await _admin.RetrieveAsync(CalculatedOrders.Table, id, new ColumnSet("xrmv_ratio"));

        // 1 / 3 with the column's 10 decimals (decimal(38, 10) operands would keep 6: 0.3333330000).
        e.GetAttributeValue<decimal>("xrmv_ratio").Should().Be(0.3333333333m);
    }

    [Fact]
    public async Task One_empty_branch_reads_as_null_and_an_empty_divisor_is_refused()
    {
        // Final review I2: a conditional with one empty branch reads; a shape SQL Server cannot run is refused at definition.
        var date = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);
        var big = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " big", 1500m, 1, date, false, 100000001));
        var small = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " small", 50m, 1, date, false, 100000000));
        var columns = new ColumnSet("xrmv_tiermaybe", "xrmv_followupmaybe");

        var b = await _admin.RetrieveAsync(CalculatedOrders.Table, big, columns);
        b.Contains("xrmv_tiermaybe").Should().BeFalse("the empty branch is taken");
        b.Contains("xrmv_followupmaybe").Should().BeFalse("the empty branch is taken");
        var s = await _admin.RetrieveAsync(CalculatedOrders.Table, small, columns);
        s.GetAttributeValue<OptionSetValue>("xrmv_tiermaybe").Value.Should().Be(100000000);
        s.GetAttributeValue<DateTime>("xrmv_followupmaybe").Should().Be(date.AddMonths(1));

        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var divisor = new DecimalAttributeMetadata
        {
            LogicalName = "xrmv_nulldiv", SchemaName = "xrmv_NullDiv", Precision = 2, SourceType = 1,
            DisplayName = SystemAttributes.CreateLabel("xrmv_nulldiv", 1033),
        };
        ComputedColumnRules.SetFormula(divisor, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", "chained.xaml"))
            .Replace("xrmv_double", "xrmv_nulldiv").Replace("{ SetAttributeValueStep1_2, SetAttributeValueStep1_3 }", "{ SetAttributeValueStep1_2, Nothing }"));
        await FluentActions.Awaiting(() => store.AddAttributeAsync(CalculatedOrders.Table, divisor))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_nulldiv'*Divide with an empty value*");
        store.GetEntity(CalculatedOrders.Table)!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_nulldiv");
    }

    [Fact]
    public async Task Operand_overflow_gives_null_not_an_error()
    {
        var date = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        var small = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " small", 3m, 2, date, false, 100000000));
        var huge = await _admin.CreateAsync(CalculatedOrders.Order(_tag + " huge", 1m, 1, date, false, 100000000));
        // The largest money and int values (written in SQL, past the columns' validation): their product has 25 digits.
        await ExecuteAsync($"UPDATE data.{CalculatedOrders.Table} SET xrmv_amount = 900000000000000, xrmv_quantity = 2000000000 WHERE xrmv_calcorderid = @row",
            ("@row", huge));

        var columns = new ColumnSet("xrmv_big", "xrmv_square", "xrmv_total");
        var s = await _admin.RetrieveAsync(CalculatedOrders.Table, small, columns);
        s.GetAttributeValue<decimal>("xrmv_big").Should().Be(6m);
        s.GetAttributeValue<decimal>("xrmv_square").Should().Be(36m);

        var h = await _admin.RetrieveAsync(CalculatedOrders.Table, huge, columns);
        h.GetAttributeValue<decimal>("xrmv_big").Should().Be(1_800_000_000_000_000_000_000_000m);
        h.Contains("xrmv_square").Should().BeFalse("the product of two 25-digit operands does not fit: null (K8)");
        h.Contains("xrmv_total").Should().BeFalse("a money holds 15 integer digits: null (K8)");

        // A query over both rows, filtered on the overflowing column, is not failed by the huge one.
        var fetch = $"""
            <fetch><entity name="{CalculatedOrders.Table}"><attribute name="xrmv_square" />
              <filter><condition attribute="xrmv_name" operator="like" value="{_tag}%" />
                <condition attribute="xrmv_square" operator="gt" value="0" /></filter></entity></fetch>
            """;
        (await _admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Select(r => r.Id).Should().Equal(small);
    }
}

/// <summary>Calculated columns over the SQL endpoint (plan 2.12a, Task 3): selected, filtered and sorted like any column.</summary>
[Collection("tds-query")]
public sealed class CalculatedTdsQueryTests(TdsQueryFixture fx)
{
    private readonly string _tag = "t" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Tds_select_where_order_by_calculated()
    {
        await CalculatedOrders.EnsureAsync(fx.Host.App.Services.GetRequiredService<IMetadataStore>());
        var service = TestServices.NewOrganizationService(fx.Host.App.Services, fx.AdminObjectId);
        var account = await service.CreateAsync(new Entity("account") { ["name"] = _tag + " Fabrikam" });
        var date = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (amount, quantity) in new[] { (10m, 1), (300m, 2), (20m, 3), (1m, 1) })
            await service.CreateAsync(CalculatedOrders.Order($"{_tag} {amount.ToString(CultureInfo.InvariantCulture)}", amount, quantity, date, false,
                100000000, account));

        await using var connection = new SqlConnection(fx.Host.SqlLoginConnectionString().ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT xrmv_name, xrmv_total, xrmv_label FROM xrmv_calcorder WHERE xrmv_name LIKE @tag AND xrmv_total > 5 ORDER BY xrmv_total DESC",
            connection);
        command.Parameters.AddWithValue("@tag", _tag + "%");
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, decimal, string)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetDecimal(1), reader.GetString(2)));

        rows.Should().Equal(
            ($"{_tag} 300", 600m, CalculatedOrders.Label($"{_tag} 300", _tag + " Fabrikam")),
            ($"{_tag} 20", 60m, CalculatedOrders.Label($"{_tag} 20", _tag + " Fabrikam")),
            ($"{_tag} 10", 10m, CalculatedOrders.Label($"{_tag} 10", _tag + " Fabrikam")));
    }
}
