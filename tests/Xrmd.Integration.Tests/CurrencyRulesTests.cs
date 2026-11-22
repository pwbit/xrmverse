using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The request rules on currencies (plan 2.10, task 4; rulings X2, X3, X9, D2) and the RetrieveExchangeRate message (X12): the base
/// currency keeps rate 1, stays active and is never deleted; ISO codes are unique and valid; rates and precisions stay in range;
/// <c>organization.basecurrencyid</c> is fixed; <c>usersettings</c> takes active currencies only.
/// </summary>
/// <remarks>
/// Each test uses currency codes of its own (custom codes start with Q), so the shared database never sees the same code twice. A test
/// that changes the base currency or the organization restores it in <c>finally</c>.
/// </remarks>
[Collection("xrmd")]
public sealed class CurrencyRulesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Item = "xv_curitem", Cost = "xv_cost", CostBase = "xv_cost_base";
    private const string Api = "/api/data/v9.2/";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private OrganizationService _admin = null!;
    private Guid _base;
    private Guid _organization;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await EnsureItemAsync(Store);
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _base = (Guid)(await ScalarAsync("SELECT basecurrencyid FROM data.organization"))!;
        _organization = (Guid)(await ScalarAsync("SELECT organizationid FROM data.organization"))!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Creates <c>xv_curitem</c> (name and the money column <c>xv_cost</c>) unless it exists.</summary>
    private static async Task EnsureItemAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Item) is not null) return;
            var entity = new EntityMetadata { LogicalName = Item, SchemaName = "xv_CurItem", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes([new MoneyAttributeMetadata("xv_Cost") { LogicalName = Cost, PrecisionSource = 2, Precision = 2 }]);
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

    private static Entity NewCurrency(string code, decimal? rate = 1.5m, int type = 1, int precision = 2, string? symbol = null)
    {
        var currency = new Entity(CurrencySchema.Table)
        {
            ["currencyname"] = "Currency " + code,
            [CurrencySchema.IsoCode] = code,
            [CurrencySchema.Symbol] = symbol ?? code[..1] + "$",
            [CurrencySchema.Precision] = precision,
            [CurrencySchema.CurrencyType] = new OptionSetValue(type),
        };
        if (rate is { } r) currency[CurrencySchema.ExchangeRate] = r;
        return currency;
    }

    private Task<Guid> CreateCurrencyAsync(string code, decimal rate = 1.5m, int type = 1, int precision = 2) =>
        _admin.CreateAsync(NewCurrency(code, rate, type, precision));

    private static Entity Change(string table, Guid id, string attribute, object? value) => new(table, id) { [attribute] = value };

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code, int status = 400)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        fault.HttpStatus.Should().Be(status, fault.Message);
        return fault;
    }

    private Task SetStateAsync(Guid currency, int state) => _admin.ExecuteAsync(new SetStateRequest
    {
        EntityMoniker = new EntityReference(CurrencySchema.Table, currency),
        State = new OptionSetValue(state),
        Status = new OptionSetValue(state == 0 ? 1 : 2),
    });

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    /// <summary>A user with Entra object id <paramref name="objectId"/>, the given privileges and a <c>usersettings</c> row without currency.</summary>
    private async Task<Guid> UserAsync(Guid objectId, params (string Privilege, DepthMask Depth)[] privileges)
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        if (privileges.Length > 0)
            await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "cr-" + Guid.NewGuid().ToString("N")[..8], privileges));
        await ScalarAsync("""
            INSERT INTO data.usersettings (systemuserid, ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon,
                statecode, statuscode)
            SELECT systemuserid, systemuserid, 8, systemuserid, businessunitid, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
            FROM data.systemuser WHERE systemuserid = @id
            """, ("@id", user));
        return user;
    }

    // ---- the base currency (X2) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Base_rate_update_gives_0x80048cf5()
    {
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.ExchangeRate, 2m)),
            ErrorCodes.ExchangeRateOfBaseCurrencyNotUpdatable);
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.ExchangeRate, null)),
            ErrorCodes.ExchangeRateOfBaseCurrencyNotUpdatable);

        // Sending the stored rate back (a Retrieve-then-Update) is no change.
        await _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.ExchangeRate, 1m));
        (await ScalarAsync("SELECT exchangerate FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base)))
            .Should().Be(1m);
    }

    [Fact]
    public async Task Base_deactivation_gives_0x80048cf4()
    {
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, "statecode", new OptionSetValue(1))),
            ErrorCodes.BaseCurrencyCannotBeDeactivated);
        await FaultAsync(() => SetStateAsync(_base, 1), ErrorCodes.BaseCurrencyCannotBeDeactivated);
        (await ScalarAsync("SELECT statecode FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base))).Should().Be(0);
    }

    [Fact]
    public async Task Base_deactivation_by_statuscode_alone_gives_0x80048cf4()
    {
        // statuscode 2 (Inactive) belongs to statecode 1: the core would derive the state from it (review I1).
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, "statuscode", new OptionSetValue(2))),
            ErrorCodes.BaseCurrencyCannotBeDeactivated);
        (await ScalarAsync("SELECT statecode FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base))).Should().Be(0);
        (await ScalarAsync("SELECT statuscode FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base))).Should().Be(1);

        // The Web API PATCH carries the same body.
        using var req = new HttpRequestMessage(HttpMethod.Patch, Api + $"transactioncurrencies({_base})")
        {
            Content = new StringContent("{\"statuscode\": 2}", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        using var patch = await fx.Client.SendAsync(req);
        var body = await patch.Content.ReadAsStringAsync();
        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("0x80048cf4");
        (await ScalarAsync("SELECT statecode FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base))).Should().Be(0);

        // The active status sent back is no change.
        await _admin.UpdateAsync(Change(CurrencySchema.Table, _base, "statuscode", new OptionSetValue(1)));
    }

    [Fact]
    public async Task Base_delete_gives_0x80048cff()
    {
        await FaultAsync(() => _admin.DeleteAsync(CurrencySchema.Table, _base), ErrorCodes.BaseCurrencyNotDeletable);
        (await ScalarAsync("SELECT COUNT(*) FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base))).Should().Be(1);
    }

    [Fact]
    public async Task Base_symbol_update_is_copied_to_organization()
    {
        var (symbol, precision) = ((string)(await ScalarAsync("SELECT currencysymbol FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base)))!,
            Convert.ToInt32(await ScalarAsync("SELECT currencyprecision FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base))));
        try
        {
            await _admin.UpdateAsync(new Entity(CurrencySchema.Table, _base) { [CurrencySchema.Symbol] = "EU$", [CurrencySchema.Precision] = 3 });

            (await ScalarAsync("SELECT basecurrencysymbol FROM data.organization")).Should().Be("EU$");
            (await ScalarAsync("SELECT basecurrencyprecision FROM data.organization")).Should().Be(3);

            // organization.basecurrencysymbol holds 5 characters: a longer base symbol is refused, never cut.
            var fault = await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.Symbol, "EURO$X")),
                ErrorCodes.InvalidArgument);
            fault.Message.Should().Contain("5");
            (await ScalarAsync("SELECT basecurrencysymbol FROM data.organization")).Should().Be("EU$");

            // Another currency may have a symbol of up to 10 characters.
            var other = await CreateCurrencyAsync("QBA");
            await _admin.UpdateAsync(Change(CurrencySchema.Table, other, CurrencySchema.Symbol, "LONGSYMBOL"));
            (await ScalarAsync("SELECT basecurrencysymbol FROM data.organization")).Should().Be("EU$");
        }
        finally
        {
            await _admin.UpdateAsync(new Entity(CurrencySchema.Table, _base) { [CurrencySchema.Symbol] = symbol, [CurrencySchema.Precision] = precision });
        }
        (await ScalarAsync("SELECT basecurrencysymbol FROM data.organization")).Should().Be(symbol);
        (await ScalarAsync("SELECT basecurrencyprecision FROM data.organization")).Should().Be(precision);
    }

    [Fact]
    public async Task Base_currency_code_and_type_are_fixed()
    {
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.IsoCode, "NOK")), ErrorCodes.InvalidArgument);
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.CurrencyType, new OptionSetValue(1))),
            ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Basecurrencyid_update_is_refused()
    {
        var other = await CreateCurrencyAsync("QBB");

        await FaultAsync(() => _admin.UpdateAsync(Change("organization", _organization, CurrencySchema.BaseCurrencyId,
            new EntityReference(CurrencySchema.Table, other))), ErrorCodes.InvalidArgument);
        await FaultAsync(() => _admin.UpdateAsync(Change("organization", _organization, CurrencySchema.BaseCurrencyId, null)),
            ErrorCodes.InvalidArgument);

        // The stored value sent back is no change.
        await _admin.UpdateAsync(Change("organization", _organization, CurrencySchema.BaseCurrencyId, new EntityReference(CurrencySchema.Table, _base)));
        (await ScalarAsync("SELECT basecurrencyid FROM data.organization")).Should().Be(_base);
    }

    // ---- codes (X3) ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_iso_code_gives_0x80048cf3()
    {
        await CreateCurrencyAsync("QCA");
        await FaultAsync(() => CreateCurrencyAsync("QCA"), ErrorCodes.DuplicateIsoCurrencyCode);

        var second = await CreateCurrencyAsync("QCB");
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, second, CurrencySchema.IsoCode, "QCA")),
            ErrorCodes.DuplicateIsoCurrencyCode);

        // The base currency's code is taken too.
        var baseCode = (string)(await ScalarAsync("SELECT isocurrencycode FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base)))!;
        await FaultAsync(() => _admin.CreateAsync(NewCurrency(baseCode, type: 0)), ErrorCodes.DuplicateIsoCurrencyCode);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_code_give_0x80048cf3_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                return (Id: (Guid?)await CreateCurrencyAsync("QOA"), Fault: (XrmdFaultException?)null);
            }
            catch (XrmdFaultException ex)
            {
                return (Id: null, Fault: ex);
            }
        }));

        results.Count(r => r.Id is not null).Should().Be(1);
        var fault = results.Single(r => r.Fault is not null).Fault!;
        fault.ErrorCode.Should().Be(ErrorCodes.DuplicateIsoCurrencyCode, fault.Message);
        fault.HttpStatus.Should().Be(400);
        (await ScalarAsync("SELECT COUNT(*) FROM data.transactioncurrency WHERE isocurrencycode = 'QOA'")).Should().Be(1);
    }

    [Fact]
    public async Task System_currency_with_unknown_code_gives_0x80048cf2()
    {
        await FaultAsync(() => CreateCurrencyAsync("QQQ", type: 0), ErrorCodes.InvalidIsoCurrencyCode);
        await FaultAsync(() => CreateCurrencyAsync("nok", type: 0), ErrorCodes.InvalidIsoCurrencyCode);

        var nok = await CreateCurrencyAsync("NOK", 11.5m, type: 0);
        (await ScalarAsync("SELECT currencytype FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", nok))).Should().Be(0);

        // Turning a custom currency with a non-ISO code into a System one is refused too.
        var custom = await CreateCurrencyAsync("QDA");
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, custom, CurrencySchema.CurrencyType, new OptionSetValue(0))),
            ErrorCodes.InvalidIsoCurrencyCode);
    }

    [Fact]
    public async Task Custom_currency_code_must_be_three_letters()
    {
        foreach (var code in (string[])["QE", "QEAB", "qea", "QE1", "Q-E"])
            await FaultAsync(() => CreateCurrencyAsync(code), ErrorCodes.InvalidIsoCurrencyCode);
        await FaultAsync(() => _admin.CreateAsync(NewCurrency("QEA").Also(c => c.Attributes.Remove(CurrencySchema.IsoCode))),
            ErrorCodes.InvalidIsoCurrencyCode);

        var id = await CreateCurrencyAsync("QEA");
        (await ScalarAsync("SELECT isocurrencycode FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", id))).Should().Be("QEA");
    }

    [Fact]
    public async Task Name_and_symbol_are_required()
    {
        await FaultAsync(() => _admin.CreateAsync(NewCurrency("QFA").Also(c => c.Attributes.Remove("currencyname"))), ErrorCodes.InvalidArgument);
        await FaultAsync(() => _admin.CreateAsync(NewCurrency("QFA").Also(c => c[CurrencySchema.Symbol] = " ")), ErrorCodes.InvalidArgument);
        var id = await CreateCurrencyAsync("QFA");
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, id, "currencyname", null)), ErrorCodes.InvalidArgument);
    }

    // ---- rate and precision (X3, X9) -----------------------------------------------------------------------------------

    [Fact]
    public async Task Rate_out_of_range_or_missing_gives_0x80048cfd()
    {
        foreach (var rate in (decimal?[])[0m, 0.00000000001m, 1000000000000m, -1m, null])
            await FaultAsync(() => _admin.CreateAsync(NewCurrency("QGA", rate)), ErrorCodes.InvalidExchangeRate);

        // The bounds themselves are accepted.
        var low = await CreateCurrencyAsync("QGA", 0.0000000001m);
        var high = await CreateCurrencyAsync("QGB", 100000000000m);
        (await ScalarAsync("SELECT exchangerate FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", low))).Should().Be(0.0000000001m);

        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, high, CurrencySchema.ExchangeRate, null)), ErrorCodes.InvalidExchangeRate);
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, high, CurrencySchema.ExchangeRate, 0m)), ErrorCodes.InvalidExchangeRate);
        await _admin.UpdateAsync(Change(CurrencySchema.Table, high, CurrencySchema.ExchangeRate, 3.25m));
    }

    [Fact]
    public async Task Rate_of_another_type_is_a_type_error_and_json_integers_are_rates()
    {
        // The SDK path: a rate that is not a decimal is refused by its type, not as an out-of-range rate (review m2).
        var fault = await FaultAsync(() => _admin.CreateAsync(NewCurrency("QPA").Also(c => c[CurrencySchema.ExchangeRate] = 2)),
            ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("Decimal");

        // A rule reached without the front-end check (a plug-in's request) reads any number as a rate.
        CurrencyRules.DemandRateInRange(NewCurrency("QPA").Also(c => c[CurrencySchema.ExchangeRate] = 2));
        var outOfRange = FluentActions.Invoking(() => CurrencyRules.DemandRateInRange(
            NewCurrency("QPA").Also(c => c[CurrencySchema.ExchangeRate] = 0.0)));
        outOfRange.Should().Throw<XrmdFaultException>().Which.ErrorCode.Should().Be(ErrorCodes.InvalidExchangeRate);

        // The Web API reads a JSON integer as an Edm.Decimal.
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + "transactioncurrencies")
        {
            Content = new StringContent(
                "{ \"currencyname\": \"Currency QPA\", \"isocurrencycode\": \"QPA\", \"currencysymbol\": \"Q$\", " +
                "\"currencyprecision\": 2, \"currencytype\": 1, \"exchangerate\": 2 }", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        using var post = await fx.Client.SendAsync(req);
        post.IsSuccessStatusCode.Should().BeTrue(await post.Content.ReadAsStringAsync());
        (await ScalarAsync("SELECT exchangerate FROM data.transactioncurrency WHERE isocurrencycode = 'QPA'")).Should().Be(2m);
    }

    [Fact]
    public async Task Currency_precision_above_4_is_refused()
    {
        await FaultAsync(() => CreateCurrencyAsync("QHA", precision: 5), ErrorCodes.InvalidArgument);
        await FaultAsync(() => CreateCurrencyAsync("QHA", precision: -1), ErrorCodes.InvalidArgument);
        var id = await CreateCurrencyAsync("QHA", precision: 4);
        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, id, CurrencySchema.Precision, 10)), ErrorCodes.InvalidArgument);
        await _admin.UpdateAsync(Change(CurrencySchema.Table, id, CurrencySchema.Precision, 0));
        (await ScalarAsync("SELECT currencyprecision FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Pricing_precision_above_4_is_refused()
    {
        foreach (var precision in (object?[])[5, 10, -1, null])
            await FaultAsync(() => _admin.UpdateAsync(Change("organization", _organization, CurrencySchema.PricingPrecision, precision)),
                ErrorCodes.InvalidArgument);
        (await ScalarAsync("SELECT pricingdecimalprecision FROM data.organization")).Should().Be(CurrencySchema.DefaultPricingPrecision);
    }

    [Fact]
    public async Task Pricing_precision_0_to_4_is_accepted()
    {
        try
        {
            for (var precision = 0; precision <= 4; precision++)
            {
                await _admin.UpdateAsync(Change("organization", _organization, CurrencySchema.PricingPrecision, precision));
                (await ScalarAsync("SELECT pricingdecimalprecision FROM data.organization")).Should().Be(precision);
            }
        }
        finally
        {
            await _admin.UpdateAsync(Change("organization", _organization, CurrencySchema.PricingPrecision, CurrencySchema.DefaultPricingPrecision));
        }
    }

    // ---- the rules on the final row, after the plug-ins (final review I1) ------------------------------------------------

    /// <summary>Registers <c>SetTypedAttributePlugin</c> with <paramref name="config"/> on PreOperation <paramref name="message"/>.</summary>
    private async Task PreOperationSetAsync(string message, string entity, string config)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, "Xrmd.TestPlugins.SetTypedAttributePlugin");
        await PluginTestHelper.RegisterStepAsync(_admin, type, message, entity, 20, config: config);
    }

    [Fact]
    public async Task Plugin_setting_the_base_rate_gives_0x80048cf5()
    {
        var symbol = (string)(await ScalarAsync("SELECT currencysymbol FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base)))!;
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            await PreOperationSetAsync("Update", CurrencySchema.Table, $"{CurrencySchema.ExchangeRate}=decimal:2");
            await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.Symbol, symbol)),
                ErrorCodes.ExchangeRateOfBaseCurrencyNotUpdatable);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
        (await ScalarAsync("SELECT exchangerate FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base)))
            .Should().Be(1m);
    }

    [Fact]
    public async Task Plugin_deactivating_the_base_currency_gives_0x80048cf4()
    {
        var symbol = (string)(await ScalarAsync("SELECT currencysymbol FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base)))!;
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            await PreOperationSetAsync("Update", CurrencySchema.Table, "statecode=option:1");
            await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.Table, _base, CurrencySchema.Symbol, symbol)),
                ErrorCodes.BaseCurrencyCannotBeDeactivated);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
        (await ScalarAsync("SELECT statecode FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", _base))).Should().Be(0);
    }

    [Fact]
    public async Task Plugin_setting_currency_precision_7_is_refused()
    {
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            await PreOperationSetAsync("Create", CurrencySchema.Table, $"{CurrencySchema.Precision}=int:7");
            // The final row is checked: the column's own range (0..4) or the currency rule, both 400.
            await FaultAsync(() => CreateCurrencyAsync("QPL"), ErrorCodes.InvalidArgument);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
        (await ScalarAsync("SELECT COUNT(*) FROM data.transactioncurrency WHERE isocurrencycode = 'QPL'")).Should().Be(0);
    }

    // ---- delete and deactivate -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Currency_in_use_cannot_be_deleted()
    {
        var used = await CreateCurrencyAsync("QIA");
        var item = await _admin.CreateAsync(new Entity(Item) { ["xv_name"] = "in use", [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, used) });

        var fault = await FaultAsync(() => _admin.DeleteAsync(CurrencySchema.Table, used), ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(CurrencySchema.RelationshipName(Item));

        // A currency in a user's settings is in use too.
        var personal = await CreateCurrencyAsync("QIB");
        var user = await UserAsync(Guid.NewGuid());
        await _admin.UpdateAsync(Change(CurrencySchema.UserSettings, user, CurrencySchema.CurrencyId, new EntityReference(CurrencySchema.Table, personal)));
        await FaultAsync(() => _admin.DeleteAsync(CurrencySchema.Table, personal), ErrorCodes.InvalidArgument);

        // Freed, both go.
        await _admin.DeleteAsync(Item, item);
        await _admin.UpdateAsync(Change(CurrencySchema.UserSettings, user, CurrencySchema.CurrencyId, null));
        await _admin.DeleteAsync(CurrencySchema.Table, used);
        await _admin.DeleteAsync(CurrencySchema.Table, personal);
    }

    [Fact]
    public async Task Unused_currency_can_be_deleted()
    {
        var id = await CreateCurrencyAsync("QJA");
        await _admin.DeleteAsync(CurrencySchema.Table, id);
        (await ScalarAsync("SELECT COUNT(*) FROM data.transactioncurrency WHERE transactioncurrencyid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Deactivated_currency_stays_on_existing_records()
    {
        var currency = await CreateCurrencyAsync("QKA", 2m);
        var item = await _admin.CreateAsync(new Entity(Item)
        {
            ["xv_name"] = "kept", [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, currency), [Cost] = new Money(10m),
        });

        await SetStateAsync(currency, 1);
        // An inactive currency's rate can still change (ruling X7: nothing is recomputed).
        await _admin.UpdateAsync(Change(CurrencySchema.Table, currency, CurrencySchema.ExchangeRate, 4m));

        var stored = await _admin.RetrieveAsync(Item, item, new ColumnSet(CurrencySchema.CurrencyId, CurrencySchema.ExchangeRate, CostBase));
        stored.GetAttributeValue<EntityReference>(CurrencySchema.CurrencyId).Id.Should().Be(currency);
        stored.GetAttributeValue<decimal>(CurrencySchema.ExchangeRate).Should().Be(2m);
        stored.GetAttributeValue<Money>(CostBase).Value.Should().Be(5m);

        // A money Update of the record keeps the inactive currency and applies its current rate (ruling X6, Task 3 semantics).
        await _admin.UpdateAsync(Change(Item, item, Cost, new Money(20m)));
        var updated = await _admin.RetrieveAsync(Item, item, new ColumnSet(CurrencySchema.CurrencyId, CurrencySchema.ExchangeRate, CostBase));
        updated.GetAttributeValue<EntityReference>(CurrencySchema.CurrencyId).Id.Should().Be(currency);
        updated.GetAttributeValue<decimal>(CurrencySchema.ExchangeRate).Should().Be(4m);
        updated.GetAttributeValue<Money>(CostBase).Value.Should().Be(5m);

        // A new record cannot take it.
        await FaultAsync(() => _admin.CreateAsync(new Entity(Item) { [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, currency) }),
            ErrorCodes.InvalidCurrency);

        // Active again, it is given out again.
        await SetStateAsync(currency, 0);
        await _admin.CreateAsync(new Entity(Item) { [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, currency) });
    }

    [Fact]
    public async Task Usersettings_with_inactive_currency_is_refused()
    {
        var inactive = await CreateCurrencyAsync("QLA");
        await SetStateAsync(inactive, 1);
        var active = await CreateCurrencyAsync("QLB");
        var user = await UserAsync(Guid.NewGuid());

        await FaultAsync(() => _admin.UpdateAsync(Change(CurrencySchema.UserSettings, user, CurrencySchema.CurrencyId,
            new EntityReference(CurrencySchema.Table, inactive))), ErrorCodes.InvalidCurrency);

        await _admin.UpdateAsync(Change(CurrencySchema.UserSettings, user, CurrencySchema.CurrencyId, new EntityReference(CurrencySchema.Table, active)));
        (await ScalarAsync("SELECT transactioncurrencyid FROM data.usersettings WHERE systemuserid = @id", ("@id", user))).Should().Be(active);

        // A setting that keeps a currency deactivated since is no change; clearing it is allowed.
        await SetStateAsync(active, 1);
        await _admin.UpdateAsync(Change(CurrencySchema.UserSettings, user, CurrencySchema.CurrencyId, new EntityReference(CurrencySchema.Table, active)));
        await _admin.UpdateAsync(Change(CurrencySchema.UserSettings, user, CurrencySchema.CurrencyId, null));
    }

    // ---- privileges (X15) ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Non_admin_cannot_create_currency()
    {
        var oid = Guid.NewGuid();
        await UserAsync(oid, ("prvReadTransactionCurrency", DepthMask.Global), ("prvAppendToTransactionCurrency", DepthMask.Global));
        var user = ServiceFor(fx, oid);

        await FaultAsync(() => user.CreateAsync(NewCurrency("QMA")), ErrorCodes.PrivilegeDenied, 403);
        await FaultAsync(() => user.UpdateAsync(Change(CurrencySchema.Table, _base, "currencyname", "Mine")), ErrorCodes.PrivilegeDenied, 403);

        // Reading a rate needs only Read.
        var response = await user.ExecuteAsync(new RetrieveExchangeRateRequest { TransactionCurrencyId = _base });
        response.Results["ExchangeRate"].Should().Be(1m);
    }

    // ---- RetrieveExchangeRate (X12) ------------------------------------------------------------------------------------

    [Fact]
    public async Task RetrieveExchangeRate_returns_rate()
    {
        var id = await CreateCurrencyAsync("QNA", 1.25m);

        var response = await _admin.ExecuteAsync(new RetrieveExchangeRateRequest { TransactionCurrencyId = id });
        response.ResponseName.Should().Be(CurrencyMessages.MessageName);
        response.Results["ExchangeRate"].Should().Be(1.25m);

        foreach (var path in (string[])[$"RetrieveExchangeRate(TransactionCurrencyId={id})", $"RetrieveExchangeRate(TransactionCurrencyId=@p1)?@p1={id}"])
        {
            using var web = await GetAsync(path);
            var body = await web.Content.ReadAsStringAsync();
            web.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var json = JsonNode.Parse(body)!.AsObject();
            json["ExchangeRate"]!.GetValue<decimal>().Should().Be(1.25m);
            json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RetrieveExchangeRateResponse");
        }

        using var metadata = await GetAsync("$metadata");
        (await metadata.Content.ReadAsStringAsync()).Should().Contain("<Function Name=\"RetrieveExchangeRate\"");
    }

    [Fact]
    public async Task RetrieveExchangeRate_unknown_id_404()
    {
        var unknown = Guid.NewGuid();
        await FaultAsync(() => _admin.ExecuteAsync(new RetrieveExchangeRateRequest { TransactionCurrencyId = unknown }),
            ErrorCodes.ObjectDoesNotExist, 404);

        using var web = await GetAsync($"RetrieveExchangeRate(TransactionCurrencyId={unknown})");
        web.StatusCode.Should().Be(HttpStatusCode.NotFound, await web.Content.ReadAsStringAsync());

        using var bad = await GetAsync("RetrieveExchangeRate(TransactionCurrencyId='x')");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, await bad.Content.ReadAsStringAsync());
    }
}

/// <summary>A fluent helper of the currency tests.</summary>
internal static class CurrencyTestExtensions
{
    /// <summary>Runs <paramref name="change"/> on <paramref name="entity"/> and returns it.</summary>
    public static Entity Also(this Entity entity, Action<Entity> change)
    {
        change(entity);
        return entity;
    }
}
