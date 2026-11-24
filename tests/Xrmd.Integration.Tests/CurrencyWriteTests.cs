using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The write path of currencies (plan 2.10, task 3; rulings X5–X10, review focus RF1, RF2, RF4): on a table with money the Core
/// resolves the record's currency, stores its rate in <c>exchangerate</c>, rounds every money value to its effective precision and
/// computes each <c>{money}_base</c> = value / rate, on Create and on an Update that touches the currency or a money column.
/// </summary>
/// <remarks>
/// The currencies of these tests are set up by SQL (the request rules on <c>transactioncurrency</c> are another task's), upserted by
/// ISO code so that a test sets the rate it needs. The table <c>xv_order</c> has <c>xv_amount</c> (PrecisionSource 2, the currency's
/// precision) and <c>xv_fee</c> (PrecisionSource 0, Precision 1).
/// </remarks>
[Collection("xrmd")]
public sealed class CurrencyWriteTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = "xv_order";
    private const string Amount = "xv_amount", AmountBase = "xv_amount_base", Fee = "xv_fee", FeeBase = "xv_fee_base";
    private const string Price = "xv_price", PriceBase = "xv_price_base";
    private const string Plugins = "Xrmd.TestPlugins.";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService _admin = null!;
    private Guid _base;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await EnsureOrderAsync(Store);
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _base = (Guid)(await ScalarAsync("SELECT basecurrencyid FROM data.organization"))!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Creates <c>xv_order</c> (audited, name, <c>xv_amount</c> and <c>xv_fee</c>) unless it exists.</summary>
    private static async Task EnsureOrderAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Order) is not null) return;
            var entity = new EntityMetadata
            {
                LogicalName = Order, SchemaName = "xv_Order", OwnershipType = OwnershipTypes.UserOwned,
                IsAuditEnabled = new BooleanManagedProperty(true),
            };
            entity.SetAttributes(
            [
                new MoneyAttributeMetadata("xv_Amount") { LogicalName = Amount, PrecisionSource = 2, Precision = 2 },
                new MoneyAttributeMetadata("xv_Fee") { LogicalName = Fee, PrecisionSource = 0, Precision = 1 },
                new MoneyAttributeMetadata("xv_Price") { LogicalName = Price, PrecisionSource = 1, Precision = 2 },
            ]);
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

    private Task ExecAsync(string sql, params (string Name, object? Value)[] parameters) => ScalarAsync(sql, parameters);

    /// <summary>The stored columns of the order <paramref name="id"/>.</summary>
    private async Task<Dictionary<string, object?>> RowAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT transactioncurrencyid, exchangerate, {Amount}, {AmountBase}, {Fee}, {FeeBase}, {Price}, {PriceBase}, CAST(versionnumber AS bigint) AS versionnumber " +
            $"FROM data.{Order} WHERE xv_orderid = @id");
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        return Enumerable.Range(0, rd.FieldCount).ToDictionary(rd.GetName, i => rd.IsDBNull(i) ? null : rd.GetValue(i));
    }

    /// <summary>The currency with ISO code <paramref name="code"/>, inserted or updated to the given rate, precision and state.</summary>
    private async Task<Guid> CurrencyAsync(string code, decimal rate, int precision = 2, bool active = true, int type = 0)
    {
        await ExecAsync("""
            IF EXISTS (SELECT 1 FROM data.transactioncurrency WHERE isocurrencycode = @code)
                UPDATE data.transactioncurrency SET exchangerate = @rate, currencyprecision = @precision, statecode = @state,
                    statuscode = @status WHERE isocurrencycode = @code
            ELSE
                INSERT INTO data.transactioncurrency (transactioncurrencyid, currencyname, isocurrencycode, currencysymbol,
                    currencyprecision, exchangerate, currencytype, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                SELECT NEWID(), @code, @code, @code, @precision, @rate, @type, businessunitid, SYSUTCDATETIME(), SYSUTCDATETIME(), @state, @status
                FROM data.businessunit WHERE parentbusinessunitid IS NULL
            """, ("@code", code), ("@rate", rate), ("@precision", precision), ("@state", active ? 0 : 1), ("@status", active ? 1 : 2),
            ("@type", type));
        return (Guid)(await ScalarAsync("SELECT transactioncurrencyid FROM data.transactioncurrency WHERE isocurrencycode = @code", ("@code", code)))!;
    }

    private static EntityReference Currency(Guid id) => new(CurrencySchema.Table, id);

    private Task<Guid> CreateOrderAsync(OrganizationService? service = null, Guid? currency = null, decimal? amount = null, decimal? fee = null)
    {
        var order = new Entity(Order) { ["xv_name"] = "order " + _tag };
        if (currency is { } c) order[CurrencySchema.CurrencyId] = Currency(c);
        if (amount is { } a) order[Amount] = new Money(a);
        if (fee is { } f) order[Fee] = new Money(f);
        return (service ?? _admin).CreateAsync(order);
    }

    /// <summary>A user with Entra object id <paramref name="objectId"/>, Create/Read/Write on <c>xv_order</c> and a <c>usersettings</c> row.</summary>
    private async Task<Guid> UserAsync(Guid objectId, Guid? settingsCurrency)
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "cur-" + Guid.NewGuid().ToString("N")[..8],
            ("prvCreatexv_Order", DepthMask.Global), ("prvReadxv_Order", DepthMask.Global), ("prvWritexv_Order", DepthMask.Global)));
        await ExecAsync("""
            INSERT INTO data.usersettings (systemuserid, ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon,
                statecode, statuscode, transactioncurrencyid)
            SELECT systemuserid, systemuserid, 8, systemuserid, businessunitid, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1, @currency
            FROM data.systemuser WHERE systemuserid = @id
            """, ("@id", user), ("@currency", settingsCurrency));
        return user;
    }

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        fault.HttpStatus.Should().Be(400);
        return fault;
    }

    // ---- Create -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_with_usd_at_1_10_stores_rate_and_base()
    {
        var usd = await CurrencyAsync("USD", 1.10m);

        var id = await CreateOrderAsync(currency: usd, amount: 100m);

        var row = await RowAsync(id);
        row["transactioncurrencyid"].Should().Be(usd);
        row["exchangerate"].Should().Be(1.1m);
        row[Amount].Should().Be(100m);
        row[AmountBase].Should().Be(90.9091m);
        row[Fee].Should().BeNull();
        row[FeeBase].Should().BeNull();
    }

    [Fact]
    public async Task Create_without_money_sets_currency()
    {
        var id = await CreateOrderAsync();

        var row = await RowAsync(id);
        row["transactioncurrencyid"].Should().Be(_base, "the admin has no currency in its settings: the base one applies");
        row["exchangerate"].Should().Be(1m);
    }

    [Fact]
    public async Task Create_without_currency_uses_user_settings_then_base()
    {
        var gbp = await CurrencyAsync("GBP", 0.8m);
        var oid = Guid.NewGuid();
        var user = await UserAsync(oid, gbp);
        var asUser = ServiceFor(fx, oid);

        var withSettings = await RowAsync(await CreateOrderAsync(asUser, amount: 10m));
        withSettings["transactioncurrencyid"].Should().Be(gbp);
        withSettings["exchangerate"].Should().Be(0.8m);
        withSettings[AmountBase].Should().Be(12.5m);

        // An inactive personal currency is skipped: the base one applies.
        await CurrencyAsync("CHF", 0.9m, active: false);
        await ExecAsync("UPDATE data.usersettings SET transactioncurrencyid = (SELECT transactioncurrencyid FROM data.transactioncurrency WHERE isocurrencycode = 'CHF') WHERE systemuserid = @id",
            ("@id", user));
        (await RowAsync(await CreateOrderAsync(asUser, amount: 10m)))["transactioncurrencyid"].Should().Be(_base);

        await ExecAsync("UPDATE data.usersettings SET transactioncurrencyid = NULL WHERE systemuserid = @id", ("@id", user));
        var withoutSettings = await RowAsync(await CreateOrderAsync(asUser, amount: 10m));
        withoutSettings["transactioncurrencyid"].Should().Be(_base);
        withoutSettings["exchangerate"].Should().Be(1m);
        withoutSettings[AmountBase].Should().Be(10m);
    }

    [Fact]
    public async Task Personal_currency_deactivated_while_locking_falls_back_to_base()
    {
        // Final review m3: the personal currency is chosen by a sub-select (no lock, the committed version) and then locked. A
        // deactivation that commits in between must not give the record an inactive currency: the base one applies.
        var racing = await CurrencyAsync("QRC", 2m, type: 1);
        var oid = Guid.NewGuid();
        await UserAsync(oid, racing);
        var asUser = ServiceFor(fx, oid);

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        int spid;
        await using (var cmd = TestSql.Command(conn, TestSql.SessionIdQuery)) spid = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Task<Guid> create;
        await using (var tx = conn.BeginTransaction())
        {
            await using (var cmd = TestSql.Command(conn, tx, "UPDATE data.transactioncurrency SET statecode = 1, statuscode = 2 WHERE transactioncurrencyid = @id"))
            {
                cmd.Parameters.AddWithValue("@id", racing);
                await cmd.ExecuteNonQueryAsync();
            }
            create = CreateOrderAsync(asUser, amount: 10m);
            // Wait until the Create waits on the deactivated row, then commit the deactivation.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Convert.ToInt32(await ScalarAsync(TestSql.BlockedByQuery, ("@spid", spid))) == 0)
            {
                create.IsCompleted.Should().BeFalse("the Create must wait on the currency row the deactivation holds");
                DateTime.UtcNow.Should().BeBefore(deadline);
                await Task.Delay(20);
            }
            await tx.CommitAsync();
        }

        var row = await RowAsync(await create);
        row["transactioncurrencyid"].Should().Be(_base);
        row["exchangerate"].Should().Be(1m);
    }

    [Fact]
    public async Task Default_currency_comes_from_the_impersonated_user()
    {
        var gbp = await CurrencyAsync("GBP", 0.8m);
        var user = await UserAsync(Guid.NewGuid(), gbp);
        var impersonating = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, callerId: user);

        var row = await RowAsync(await CreateOrderAsync(impersonating, amount: 10m));

        row["transactioncurrencyid"].Should().Be(gbp, "the effective caller is the impersonated user, not the admin (base currency)");
        row[AmountBase].Should().Be(12.5m);
    }

    [Fact]
    public async Task Create_with_inactive_currency_gives_0x80048cfc()
    {
        var chf = await CurrencyAsync("CHF", 0.9m, active: false);

        await FaultAsync(() => CreateOrderAsync(currency: chf, amount: 1m), ErrorCodes.InvalidCurrency);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{Order} WHERE transactioncurrencyid = @c", ("@c", chf))).Should().Be(0);
    }

    [Fact]
    public async Task Create_with_missing_currency_gives_404()
    {
        var act = () => CreateOrderAsync(currency: Guid.NewGuid(), amount: 1m);

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        fault.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Money_is_rounded_to_effective_precision()
    {
        var usd = await CurrencyAsync("USD", 1.10m);

        var row = await RowAsync(await CreateOrderAsync(currency: usd, amount: 10.555m, fee: 1.25m));

        row[Amount].Should().Be(10.56m, "PrecisionSource 2: the currency's precision 2, away from zero");
        row[Fee].Should().Be(1.3m, "PrecisionSource 0: the column's Precision 1");
        row[AmountBase].Should().Be(9.6000m, "10.56 / 1.1");
        row[FeeBase].Should().Be(1.1818m, "1.3 / 1.1");
    }

    [Fact]
    public async Task Sent_exchangerate_and_base_are_ignored()
    {
        var usd = await CurrencyAsync("USD", 1.10m);

        var id = await _admin.CreateAsync(new Entity(Order)
        {
            ["xv_name"] = "sent " + _tag, [CurrencySchema.CurrencyId] = Currency(usd), [Amount] = new Money(100m),
            [CurrencySchema.ExchangeRate] = 5m, [AmountBase] = new Money(1m),
        });
        await _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.ExchangeRate] = 7m, [AmountBase] = new Money(2m), [FeeBase] = new Money(3m) });

        var row = await RowAsync(id);
        row["exchangerate"].Should().Be(1.1m);
        row[AmountBase].Should().Be(90.9091m);
        row[FeeBase].Should().BeNull();
    }

    [Fact]
    public async Task Base_overflow_is_refused()
    {
        var tiny = await CurrencyAsync("XVT", 0.0000000001m, type: 1);

        var overflow = await FaultAsync(() => CreateOrderAsync(currency: tiny, amount: 100000m), ErrorCodes.BaseCurrencyOverflow);
        overflow.Message.Should().Contain(AmountBase);
        var underflow = await FaultAsync(() => CreateOrderAsync(currency: tiny, amount: -100000m), ErrorCodes.BaseCurrencyUnderflow);
        underflow.Message.Should().Contain(AmountBase);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{Order} WHERE transactioncurrencyid = @c", ("@c", tiny))).Should().Be(0);
    }

    // ---- Update -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rate_change_does_not_touch_existing_records()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, amount: 100m, fee: 10m);
        var before = await RowAsync(id);

        await CurrencyAsync("USD", 1.25m);

        var after = await RowAsync(id);
        after.Should().Equal(before, "a rate change recomputes nothing (X7), not even the row version");
    }

    [Fact]
    public async Task Update_of_money_applies_the_new_rate()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, amount: 100m, fee: 10m);
        await CurrencyAsync("USD", 1.25m);

        await _admin.UpdateAsync(new Entity(Order, id) { [Amount] = new Money(50m) });

        var row = await RowAsync(id);
        row["exchangerate"].Should().Be(1.25m);
        row[AmountBase].Should().Be(40m);
        row[FeeBase].Should().Be(8m);
    }

    [Fact]
    public async Task Update_of_currency_only_recomputes_every_base()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var gbp = await CurrencyAsync("GBP", 0.8m);
        var id = await CreateOrderAsync(currency: usd, amount: 100m, fee: 10m);

        await _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.CurrencyId] = Currency(gbp) });

        var row = await RowAsync(id);
        row["transactioncurrencyid"].Should().Be(gbp);
        row["exchangerate"].Should().Be(0.8m);
        row[Amount].Should().Be(100m);
        row[AmountBase].Should().Be(125m);
        row[FeeBase].Should().Be(12.5m);
    }

    [Fact]
    public async Task Update_of_one_money_recomputes_the_other_base()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, amount: 100m, fee: 10m);
        await CurrencyAsync("USD", 2m);

        await _admin.UpdateAsync(new Entity(Order, id) { [Fee] = new Money(4m) });

        var row = await RowAsync(id);
        row["exchangerate"].Should().Be(2m);
        row[AmountBase].Should().Be(50m, "the stored amount is converted at the new rate");
        row[FeeBase].Should().Be(2m);
    }

    [Fact]
    public async Task Update_without_money_or_currency_keeps_rate()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, amount: 100m);
        await CurrencyAsync("USD", 2m);

        await _admin.UpdateAsync(new Entity(Order, id) { ["xv_name"] = "renamed " + _tag });

        var row = await RowAsync(id);
        row["exchangerate"].Should().Be(1.1m);
        row[AmountBase].Should().Be(90.9091m);
    }

    [Fact]
    public async Task Update_to_inactive_currency_gives_0x80048cfc()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var chf = await CurrencyAsync("CHF", 0.9m, active: false);
        var id = await CreateOrderAsync(currency: usd, amount: 100m);

        await FaultAsync(() => _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.CurrencyId] = Currency(chf) }), ErrorCodes.InvalidCurrency);
        (await RowAsync(id))["transactioncurrencyid"].Should().Be(usd);
    }

    [Fact]
    public async Task Update_of_a_record_keeping_an_inactive_currency_succeeds()
    {
        var sek = await CurrencyAsync("SEK", 10m);
        var id = await CreateOrderAsync(currency: sek, amount: 100m);
        await CurrencyAsync("SEK", 10m, active: false);

        await _admin.UpdateAsync(new Entity(Order, id) { [Amount] = new Money(50m), [CurrencySchema.CurrencyId] = Currency(sek) });

        var row = await RowAsync(id);
        row["transactioncurrencyid"].Should().Be(sek);
        row[AmountBase].Should().Be(5m);
    }

    [Fact]
    public async Task Null_money_gives_null_base()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, amount: 100m, fee: 10m);

        await _admin.UpdateAsync(new Entity(Order, id) { [Amount] = null });

        var row = await RowAsync(id);
        row[Amount].Should().BeNull();
        row[AmountBase].Should().BeNull();
        row[FeeBase].Should().Be(9.0909m);
    }

    [Fact]
    public async Task Clearing_currency_with_money_is_refused()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, fee: 10m);

        await FaultAsync(() => _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.CurrencyId] = null }),
            ErrorCodes.CurrencyCannotBeNullDueToNonNullMoneyFields);
        await FaultAsync(() => _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.CurrencyId] = null, [Amount] = null }),
            ErrorCodes.CurrencyCannotBeNullDueToNonNullMoneyFields);
        await FaultAsync(() => _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.CurrencyId] = null, [Fee] = null, [Amount] = new Money(1m) }),
            ErrorCodes.CurrencyCannotBeNullDueToNonNullMoneyFields);

        var row = await RowAsync(id);
        row["transactioncurrencyid"].Should().Be(usd);
        row[Fee].Should().Be(10m);
    }

    [Fact]
    public async Task Clearing_currency_with_all_money_null_clears_rate()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, fee: 10m);

        await _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.CurrencyId] = null, [Fee] = null });

        var row = await RowAsync(id);
        row["transactioncurrencyid"].Should().BeNull();
        row["exchangerate"].Should().BeNull();
        row[FeeBase].Should().BeNull();

        // Money on a record without a currency brings the default one back.
        await _admin.UpdateAsync(new Entity(Order, id) { [Amount] = new Money(3m) });
        row = await RowAsync(id);
        row["transactioncurrencyid"].Should().Be(_base);
        row["exchangerate"].Should().Be(1m);
        row[AmountBase].Should().Be(3m);
    }

    // ---- fix round 1 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_with_explicit_null_currency_uses_the_default()
    {
        var id = await _admin.CreateAsync(new Entity(Order) { ["xv_name"] = "null " + _tag, [CurrencySchema.CurrencyId] = null, [Amount] = new Money(5m) });

        var row = await RowAsync(id);
        row["transactioncurrencyid"].Should().Be(_base, "a null currency on Create is no currency given (ruling Y3)");
        row["exchangerate"].Should().Be(1m);
        row[AmountBase].Should().Be(5m);
    }

    [Fact]
    public async Task Pricing_precision_rounds_precision_source_1_on_create_and_update()
    {
        var usd = await CurrencyAsync("USD", 2m);
        await ExecAsync("UPDATE data.organization SET pricingdecimalprecision = 3");
        try
        {
            var id = await CreateOrderAsync(currency: usd);
            await _admin.UpdateAsync(new Entity(Order, id) { [Price] = new Money(1.23456m) });
            var row = await RowAsync(id);
            row[Price].Should().Be(1.235m);
            row[PriceBase].Should().Be(0.6175m);

            await ExecAsync("UPDATE data.organization SET pricingdecimalprecision = 1");
            var created = await RowAsync(await _admin.CreateAsync(new Entity(Order) { ["xv_name"] = "price " + _tag, [CurrencySchema.CurrencyId] = Currency(usd), [Price] = new Money(2.25m) }));
            created[Price].Should().Be(2.3m);
            created[PriceBase].Should().Be(1.15m);
        }
        finally
        {
            await ExecAsync($"UPDATE data.organization SET pricingdecimalprecision = {CurrencySchema.DefaultPricingPrecision}");
        }
    }

    [Fact]
    public async Task Money_is_rounded_on_update()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var id = await CreateOrderAsync(currency: usd, amount: 1m, fee: 1m);

        await _admin.UpdateAsync(new Entity(Order, id) { [Amount] = new Money(10.555m), [Fee] = new Money(-1.25m) });

        var row = await RowAsync(id);
        row[Amount].Should().Be(10.56m);
        row[Fee].Should().Be(-1.3m);
        row[AmountBase].Should().Be(9.6m);
        row[FeeBase].Should().Be(-1.1818m);
    }

    [Fact]
    public async Task Base_underflow_on_update_is_refused()
    {
        var tiny = await CurrencyAsync("XVU", 0.0000000001m, type: 1);
        var id = await CreateOrderAsync(currency: tiny, amount: 1m);

        var fault = await FaultAsync(() => _admin.UpdateAsync(new Entity(Order, id) { [Fee] = new Money(-100000m) }), ErrorCodes.BaseCurrencyUnderflow);
        fault.Message.Should().Contain(FeeBase);
        (await RowAsync(id))[Fee].Should().BeNull();
    }

    [Fact]
    public async Task Currency_rate_that_rounds_to_zero_or_is_null_gives_0x80048cfd()
    {
        var zero = await CurrencyAsync("XVZ", 0.00000000004m, type: 1);
        await FaultAsync(() => CreateOrderAsync(currency: zero, amount: 1m), ErrorCodes.InvalidExchangeRate);

        var none = await CurrencyAsync("XVN", 1m, type: 1);
        await ExecAsync("UPDATE data.transactioncurrency SET exchangerate = NULL WHERE transactioncurrencyid = @id", ("@id", none));
        await FaultAsync(() => CreateOrderAsync(currency: none, amount: 1m), ErrorCodes.InvalidExchangeRate);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{Order} WHERE transactioncurrencyid IN (@a, @b)", ("@a", zero), ("@b", none))).Should().Be(0);
    }

    [Fact]
    public async Task Base_pushed_by_a_pre_operation_plugin_is_overwritten()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, Plugins + "SetAttributePlugin");
            await PluginTestHelper.RegisterStepAsync(_admin, type, "Create", Order, 20, config: AmountBase + "=1");

            var row = await RowAsync(await CreateOrderAsync(currency: await CurrencyAsync("USD", 1.10m), amount: 100m));

            row[AmountBase].Should().Be(90.9091m);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    [Fact]
    public async Task New_systemuser_keeps_an_existing_usersettings_row()
    {
        var root = await RootBusinessUnitAsync(fx);
        var id = Guid.NewGuid();
        var gbp = await CurrencyAsync("GBP", 0.8m);
        await ExecAsync("""
            INSERT INTO data.usersettings (systemuserid, ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon,
                statecode, statuscode, transactioncurrencyid)
            VALUES (@id, @id, 8, @id, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1, @currency)
            """, ("@id", id), ("@bu", root), ("@currency", gbp));

        await _admin.CreateAsync(new Entity("systemuser", id)
        {
            ["fullname"] = "Oli Example " + _tag, ["domainname"] = $"oli.{_tag}@example.com",
            ["azureactivedirectoryobjectid"] = Guid.NewGuid(), ["businessunitid"] = new EntityReference("businessunit", root),
        });

        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings WHERE systemuserid = @id AND transactioncurrencyid = @currency",
            ("@id", id), ("@currency", gbp))).Should().Be(1);
    }

    // ---- plug-ins, audit, new users -----------------------------------------------------------------------------------

    [Fact]
    public async Task Post_image_has_base_pre_operation_target_does_not()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, Plugins + "ContextRecorderPlugin");
            await PluginTestHelper.RegisterStepAsync(_admin, type, "Create", Order, 20, config: TestEntities.PipeLog);
            var post = await PluginTestHelper.RegisterStepAsync(_admin, type, "Create", Order, 40, config: TestEntities.PipeLog);
            await PluginTestHelper.RegisterImageAsync(_admin, post, 1, "post");

            await CreateOrderAsync(currency: await CurrencyAsync("USD", 1.10m), amount: 100m);

            var logs = new Dictionary<string, string>();
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}");
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) logs[rd.GetString(0)] = rd.GetString(1);
            }
            var target = logs["Create|20|0|1"].Split('\n').Single(l => l.StartsWith("target=", StringComparison.Ordinal))["target=".Length..].Split(',');
            target.Should().Contain([Amount, CurrencySchema.CurrencyId]).And.NotContain([CurrencySchema.ExchangeRate, AmountBase]);
            var image = logs["Create|40|0|1"].Split('\n').Single(l => l.StartsWith("post=", StringComparison.Ordinal))["post=post:".Length..].Split(',');
            image.Should().Contain([Amount, AmountBase, CurrencySchema.ExchangeRate, CurrencySchema.CurrencyId]);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    [Fact]
    public async Task Audit_records_the_rate_like_other_computed_columns_and_not_the_base()
    {
        var usd = await CurrencyAsync("USD", 1.10m);
        var gbp = await CurrencyAsync("GBP", 0.8m);
        var id = await CreateOrderAsync(currency: usd, amount: 100m);
        var organization = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        await _admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        try
        {
            await _admin.UpdateAsync(new Entity(Order, id) { [CurrencySchema.CurrencyId] = Currency(gbp) });
        }
        finally
        {
            await _admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = false });
        }

        var changedata = (string)(await ScalarAsync("SELECT changedata FROM data.audit WHERE objectid = @id AND action = @action",
            ("@id", id), ("@action", AuditActions.Update)))!;
        var change = (AttributeChange)ChangeData.Deserialize(changedata);
        change.New.Keys.Should().BeEquivalentTo([CurrencySchema.CurrencyId, CurrencySchema.ExchangeRate],
            "the computed rate is audited as any server-set column (fullname); a _base column is never audited");
        Convert.ToDecimal(change.Old[CurrencySchema.ExchangeRate], System.Globalization.CultureInfo.InvariantCulture).Should().Be(1.1m);
        Convert.ToDecimal(change.New[CurrencySchema.ExchangeRate], System.Globalization.CultureInfo.InvariantCulture).Should().Be(0.8m);
    }

    [Fact]
    public async Task New_systemuser_gets_usersettings_row()
    {
        var root = await RootBusinessUnitAsync(fx);

        var id = await _admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Nia Example " + _tag, ["domainname"] = $"nia.{_tag}@example.com",
            ["azureactivedirectoryobjectid"] = Guid.NewGuid(), ["businessunitid"] = new EntityReference("businessunit", root),
        });

        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings WHERE systemuserid = @id AND ownerid = @id AND owninguser = @id AND transactioncurrencyid IS NULL",
            ("@id", id))).Should().Be(1);
    }
}
