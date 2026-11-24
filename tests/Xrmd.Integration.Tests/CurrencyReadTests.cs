using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The read path of currencies (plan 2.10, task 5; ruling X11, review focus RF5): the formatted value of a money column carries the
/// symbol and precision of the record's currency, a <c>{money}_base</c> those of the base currency, through the SDK and the Web API.
/// The currency each scope is formatted with is a hidden column of the query, never returned.
/// </summary>
/// <remarks>
/// The currencies are upserted by SQL by ISO code (USD <c>$</c> 2 decimals; JPY <c>¥</c> 0 decimals; the custom <c>ZRA</c>), then the
/// cached catalog is invalidated, as a write through the Core would. The base currency is the fixture's EUR (<c>€</c>, 2 decimals).
/// </remarks>
[Collection("xrmd")]
public sealed class CurrencyReadTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Sale = "xv_sale";
    private const string Amount = "xv_amount", AmountBase = "xv_amount_base";
    private const string Api = "/api/data/v9.2/";
    private const string FormattedValue = "@OData.Community.Display.V1.FormattedValue";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService _admin = null!;
    private Guid _usd, _jpy;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private ICurrencyCatalog Catalog => fx.App.Services.GetRequiredService<ICurrencyCatalog>();

    public async Task InitializeAsync()
    {
        await EnsureSaleAsync(Store);
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _usd = await CurrencyAsync("USD", "$", 1.10m, 2, type: 0);
        _jpy = await CurrencyAsync("JPY", "¥", 160m, 0, type: 0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Creates <c>xv_sale</c> (name, <c>xv_amount</c> with the currency's precision) unless it exists.</summary>
    private static async Task EnsureSaleAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Sale) is not null) return;
            var entity = new EntityMetadata { LogicalName = Sale, SchemaName = "xv_Sale", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes([new MoneyAttributeMetadata("xv_Amount") { LogicalName = Amount, PrecisionSource = 2, Precision = 2 }]);
            await store.CreateEntityAsync(entity, new StringAttributeMetadata("xv_Name") { LogicalName = "xv_name", MaxLength = 200 });
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>
    /// The currency <paramref name="code"/>, inserted or updated (active) to the given symbol, rate and precision; the catalog is then
    /// invalidated, since a SQL write does not go through the Core.
    /// </summary>
    private async Task<Guid> CurrencyAsync(string code, string symbol, decimal rate, int precision, int type)
    {
        await ScalarAsync("""
            IF EXISTS (SELECT 1 FROM data.transactioncurrency WHERE isocurrencycode = @code)
                UPDATE data.transactioncurrency SET currencysymbol = @symbol, exchangerate = @rate, currencyprecision = @precision,
                    statecode = 0, statuscode = 1 WHERE isocurrencycode = @code
            ELSE
                INSERT INTO data.transactioncurrency (transactioncurrencyid, currencyname, isocurrencycode, currencysymbol,
                    currencyprecision, exchangerate, currencytype, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                SELECT NEWID(), @code, @code, @symbol, @precision, @rate, @type, businessunitid, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
                FROM data.businessunit WHERE parentbusinessunitid IS NULL
            """, ("@code", code), ("@symbol", symbol), ("@rate", rate), ("@precision", precision), ("@type", type));
        Catalog.Invalidate();
        return (Guid)(await ScalarAsync("SELECT transactioncurrencyid FROM data.transactioncurrency WHERE isocurrencycode = @code", ("@code", code)))!;
    }

    private Task<Guid> CreateSaleAsync(Guid currency, decimal amount, string? name = null) =>
        _admin.CreateAsync(new Entity(Sale)
        {
            ["xv_name"] = name ?? "sale " + _tag,
            [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, currency),
            [Amount] = new Money(amount),
        });

    private Task<Entity> RetrieveAsync(Guid id, params string[] columns) => _admin.RetrieveAsync(Sale, id, new ColumnSet(columns));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return fx.Client.SendAsync(req);
    }

    private string SaleSet => Store.GetEntity(Sale)!.EntitySetName;

    // ---- SDK --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retrieve_formats_money_and_base()
    {
        var id = await CreateSaleAsync(_usd, 100m);

        var sale = await RetrieveAsync(id, Amount, AmountBase);

        sale.GetAttributeValue<Money>(AmountBase).Value.Should().Be(90.9091m);
        sale.FormattedValues[Amount].Should().Be("$100.00");
        sale.FormattedValues[AmountBase].Should().Be("€90.91");
    }

    [Fact]
    public async Task Currency_precision_and_retrieve_multiple()
    {
        var id = await CreateSaleAsync(_jpy, 1234.4m);
        var query = new QueryExpression(Sale) { ColumnSet = new ColumnSet(Amount) };
        query.Criteria.AddCondition("xv_saleid", ConditionOperator.Equal, id);

        var sale = (await _admin.RetrieveMultipleAsync(query)).Entities.Single();

        sale.GetAttributeValue<Money>(Amount).Value.Should().Be(1234m);
        sale.FormattedValues[Amount].Should().Be("¥1,234");
    }

    [Fact]
    public async Task Display_option_code_uses_iso_code()
    {
        var id = await CreateSaleAsync(_usd, 100m);
        var organization = (Guid)(await ScalarAsync("SELECT organizationid FROM data.organization"))!;
        await RetrieveAsync(id, Amount); // the catalog is loaded with display option 0
        try
        {
            await _admin.UpdateAsync(new Entity("organization", organization) { [CurrencySchema.DisplayOption] = new OptionSetValue(1) });

            var sale = await RetrieveAsync(id, Amount, AmountBase);

            sale.FormattedValues[Amount].Should().Be("USD100.00");
            sale.FormattedValues[AmountBase].Should().Be("EUR90.91");
        }
        finally
        {
            await _admin.UpdateAsync(new Entity("organization", organization) { [CurrencySchema.DisplayOption] = new OptionSetValue(0) });
        }
        (await RetrieveAsync(id, Amount)).FormattedValues[Amount].Should().Be("$100.00");
    }

    [Fact]
    public async Task Format_code_places_the_symbol()
    {
        var id = await CreateSaleAsync(_usd, -100m);
        var organization = (Guid)(await ScalarAsync("SELECT organizationid FROM data.organization"))!;
        try
        {
            await _admin.UpdateAsync(new Entity("organization", organization) { [CurrencySchema.FormatCode] = new OptionSetValue(3) });

            (await RetrieveAsync(id, Amount)).FormattedValues[Amount].Should().Be("-100.00 $");
        }
        finally
        {
            await _admin.UpdateAsync(new Entity("organization", organization) { [CurrencySchema.FormatCode] = new OptionSetValue(0) });
        }
        (await RetrieveAsync(id, Amount)).FormattedValues[Amount].Should().Be("-$100.00");
    }

    [Fact]
    public async Task Rate_update_leaves_formatting_unchanged()
    {
        var currency = await CurrencyAsync("ZRB", "R$", 2m, 2, type: 1);
        var id = await CreateSaleAsync(currency, 100m);
        (await RetrieveAsync(id, Amount, AmountBase)).FormattedValues[AmountBase].Should().Be("€50.00");

        await _admin.UpdateAsync(new Entity(CurrencySchema.Table, currency) { [CurrencySchema.ExchangeRate] = 4m });

        // Ruling X7: the record keeps its rate and base value; the formatting does not depend on the rate.
        var sale = await RetrieveAsync(id, Amount, AmountBase);
        sale.FormattedValues[Amount].Should().Be("R$100.00");
        sale.FormattedValues[AmountBase].Should().Be("€50.00");
    }

    [Fact]
    public async Task Symbol_update_reformats()
    {
        var currency = await CurrencyAsync("ZRA", "Z$", 2m, 2, type: 1);
        var id = await CreateSaleAsync(currency, 100m);
        (await RetrieveAsync(id, Amount)).FormattedValues[Amount].Should().Be("Z$100.00"); // cached

        await _admin.UpdateAsync(new Entity(CurrencySchema.Table, currency) { [CurrencySchema.Symbol] = "Zr", [CurrencySchema.Precision] = 3 });

        // The write through the Core invalidated the catalog: the next read has the new symbol and precision.
        (await RetrieveAsync(id, Amount)).FormattedValues[Amount].Should().Be("Zr100.000");
    }

    [Fact]
    public async Task Linked_money_is_formatted_with_the_linked_rows_currency()
    {
        var name = "linked " + _tag;
        var dollars = await CreateSaleAsync(_usd, 100m, name);
        await CreateSaleAsync(_jpy, 1235m, name);
        var query = new QueryExpression(Sale) { ColumnSet = new ColumnSet(Amount) };
        query.Criteria.AddCondition("xv_saleid", ConditionOperator.Equal, dollars);
        var link = query.AddLink(Sale, "xv_name", "xv_name");
        link.EntityAlias = "yen";
        link.Columns = new ColumnSet(Amount, AmountBase);
        link.LinkCriteria.AddCondition(CurrencySchema.CurrencyId, ConditionOperator.Equal, _jpy);

        var sale = (await _admin.RetrieveMultipleAsync(query)).Entities.Single();

        sale.FormattedValues[Amount].Should().Be("$100.00");
        sale.FormattedValues["yen." + Amount].Should().Be("¥1,235");
        sale.FormattedValues["yen." + AmountBase].Should().Be("€7.72");
    }

    [Fact]
    public async Task Hidden_currency_column_is_not_returned()
    {
        var name = "hidden " + _tag;
        var id = await CreateSaleAsync(_usd, 100m, name);
        var query = new QueryExpression(Sale) { ColumnSet = new ColumnSet(Amount) };
        query.Criteria.AddCondition("xv_saleid", ConditionOperator.Equal, id);
        var link = query.AddLink(Sale, "xv_name", "xv_name");
        link.EntityAlias = "same";
        link.Columns = new ColumnSet(Amount);

        var sale = (await _admin.RetrieveMultipleAsync(query)).Entities.Single();

        sale.Attributes.Keys.Should().BeEquivalentTo("xv_saleid", Amount, "same." + Amount);
        sale.FormattedValues.Keys.Should().BeEquivalentTo(Amount, "same." + Amount);
        (await RetrieveAsync(id, Amount)).Attributes.Keys.Should().BeEquivalentTo("xv_saleid", Amount);

        // Selected, the currency comes back as any lookup, with its name.
        var withCurrency = await RetrieveAsync(id, Amount, CurrencySchema.CurrencyId);
        withCurrency.GetAttributeValue<EntityReference>(CurrencySchema.CurrencyId).Id.Should().Be(_usd);
        withCurrency.FormattedValues.Should().ContainKey(CurrencySchema.CurrencyId);
    }

    [Fact]
    public async Task Aggregate_sum_money_has_base_currency_formatted_value()
    {
        var name = "sum " + _tag;
        await CreateSaleAsync(_usd, 100m, name);
        await CreateSaleAsync(_usd, 50m, name);
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{Sale}">
                <attribute name="{Amount}" alias="total" aggregate="sum" />
                <filter><condition attribute="xv_name" operator="eq" value="{name}" /></filter>
              </entity>
            </fetch>
            """;

        var row = (await _admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Single();

        ((Money)row.GetAttributeValue<AliasedValue>("total").Value).Value.Should().Be(150m);
        // An aggregate has no row currency: the base currency (EUR) formats it (plan 3.5 UI-1f, task 4b).
        row.FormattedValues["total"].Should().Be("€150.00");
    }

    // ---- Web API ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Formatted_value_annotation_has_symbol()
    {
        var id = await CreateSaleAsync(_usd, 1234.5m);

        var res = await SendAsync(HttpMethod.Get, $"{SaleSet}({id})?$select={Amount},{AmountBase},_transactioncurrencyid_value", null,
            ("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\""));
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!.AsObject();

        json[Amount]!.GetValue<decimal>().Should().Be(1234.5m);
        json[Amount + FormattedValue]!.GetValue<string>().Should().Be("$1,234.50");
        json[AmountBase + FormattedValue]!.GetValue<string>().Should().Be("€1,122.27");
        json["_transactioncurrencyid_value"]!.GetValue<Guid>().Should().Be(_usd);
        json.ContainsKey("_transactioncurrencyid_value" + FormattedValue).Should().BeTrue();
        json.ContainsKey("transactioncurrencyid").Should().BeFalse();
    }

    [Fact]
    public async Task Select_money_with_expand_does_not_leak_the_currency_column()
    {
        var id = await CreateSaleAsync(_usd, 10m);

        foreach (var path in (string[])[$"{SaleSet}({id})?$select={Amount}&$expand=owninguser($select=fullname)",
                     $"{SaleSet}?$select={Amount}&$filter=xv_saleid eq {id}&$expand=owninguser($select=fullname)"])
        {
            var res = await SendAsync(HttpMethod.Get, path, null, ("Prefer", "odata.include-annotations=\"*\""));
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var root = JsonNode.Parse(body)!.AsObject();
            var json = root.ContainsKey("value") ? root["value"]!.AsArray().Single()!.AsObject() : root;

            json[Amount]!.GetValue<decimal>().Should().Be(10m);
            json.ContainsKey("owninguser").Should().BeTrue(body);
            json.Select(p => p.Key).Should().NotContain(k => k.StartsWith("transactioncurrencyid", StringComparison.Ordinal), body);
            json.Select(p => p.Key).Should().NotContain(k => k.StartsWith("_transactioncurrencyid_value", StringComparison.Ordinal), body);
            json["owninguser"]!.AsObject().Select(p => p.Key).Should().NotContain(k => k.Contains("transactioncurrencyid"), body);
        }

        // Selected, only the lookup's value property comes back.
        var selected = await SendAsync(HttpMethod.Get,
            $"{SaleSet}({id})?$select={Amount},_transactioncurrencyid_value&$expand=owninguser($select=fullname)");
        var selectedBody = await selected.Content.ReadAsStringAsync();
        selected.StatusCode.Should().Be(HttpStatusCode.OK, selectedBody);
        var selectedJson = JsonNode.Parse(selectedBody)!.AsObject();
        selectedJson["_transactioncurrencyid_value"]!.GetValue<Guid>().Should().Be(_usd);
        selectedJson.ContainsKey("transactioncurrencyid").Should().BeFalse(selectedBody);
    }

    [Fact]
    public async Task Transaction_currency_bind_on_create()
    {
        var res = await SendAsync(HttpMethod.Post, SaleSet,
            $$"""{ "xv_name": "bound {{_tag}}", "xv_amount": 100, "transactioncurrencyid@odata.bind": "/transactioncurrencies({{_jpy}})" }""");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, body);
        var location = res.Headers.GetValues("OData-EntityId").Single();
        var id = Guid.Parse(location[(location.LastIndexOf('(') + 1)..^1]);

        var sale = await RetrieveAsync(id, Amount, AmountBase, CurrencySchema.CurrencyId, CurrencySchema.ExchangeRate);

        sale.GetAttributeValue<EntityReference>(CurrencySchema.CurrencyId).Id.Should().Be(_jpy);
        sale.GetAttributeValue<decimal>(CurrencySchema.ExchangeRate).Should().Be(160m);
        sale.GetAttributeValue<Money>(AmountBase).Value.Should().Be(0.625m);
        sale.FormattedValues[Amount].Should().Be("¥100");
    }
}
