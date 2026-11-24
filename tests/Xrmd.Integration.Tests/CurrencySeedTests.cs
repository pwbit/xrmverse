using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The currency seed (plan 2.10, task 1): the tables <c>transactioncurrency</c> and <c>usersettings</c>, the currency columns of
/// <c>organization</c>, the base currency from <c>Seed:BaseCurrency</c>, the <c>usersettings</c> backfill, the currency pair of
/// account and contact, the unique ISO code and the Basic User top-up.
/// </summary>
[Collection("xrmd")]
public sealed class CurrencySeedTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private IDbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_cur_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    private string? _database;

    public async Task DisposeAsync()
    {
        if (_database is not null) await fx.DropDatabaseAsync(_database); // the test's own database (CI runners have little free disk)
    }

    private IMetadataStore HostStore => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<MetadataStore> NewStoreAsync(IDbConnectionFactory? factory = null)
    {
        var store = new MetadataStore(factory ?? _factory);
        await store.LoadAsync();
        return store;
    }

    private async Task SeedAsync(SeedOptions options, ILogger<SystemSeed>? logger = null) =>
        await new SystemSeed(await NewStoreAsync(), _factory, logger).EnsureAsync(options);

    private static async Task<List<Dictionary<string, object?>>> RowsAsync(IDbConnectionFactory factory, string sql, params (string, object)[] parameters)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (n, v) in parameters) cmd.AddParameter(n, v);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        return rows;
    }

    private Task<List<Dictionary<string, object?>>> RowsAsync(string sql, params (string, object)[] parameters) => RowsAsync(_factory, sql, parameters);

    private async Task<long> ScalarAsync(string sql, params (string, object)[] parameters) =>
        Convert.ToInt64((await RowsAsync(sql, parameters)).Single().Values.Single());

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The single base currency of the database and the currency columns of its organization.</summary>
    private static async Task AssertBaseCurrencyAsync(IDbConnectionFactory factory, string iso, string name, string symbol, int precision)
    {
        var currencies = await RowsAsync(factory, "SELECT * FROM data.transactioncurrency");
        var currency = currencies.Should().ContainSingle().Subject;
        currency["isocurrencycode"].Should().Be(iso);
        currency["currencyname"].Should().Be(name);
        currency["currencysymbol"].Should().Be(symbol);
        currency["currencyprecision"].Should().Be(precision);
        currency["exchangerate"].Should().Be(1m);
        currency["currencytype"].Should().Be(0);
        currency["statecode"].Should().Be(0);
        currency["statuscode"].Should().Be(1);

        var organization = (await RowsAsync(factory, "SELECT * FROM data.organization")).Should().ContainSingle().Subject;
        organization["basecurrencyid"].Should().Be(currency["transactioncurrencyid"]);
        organization["basecurrencysymbol"].Should().Be(symbol);
        organization["basecurrencyprecision"].Should().Be(precision);
        organization["pricingdecimalprecision"].Should().Be(CurrencySchema.DefaultPricingPrecision);
        organization["currencydisplayoption"].Should().Be(0);
        organization["currencyformatcode"].Should().Be(0);
    }

    [Fact]
    public async Task New_organization_has_EUR_base_currency_at_rate_1()
    {
        await SeedAsync(Options);
        await AssertBaseCurrencyAsync(_factory, "EUR", "Euro", "€", 2);

        // The host's organization too (Seed:BaseCurrency not configured).
        var host = TestProvider.Factory(fx.ConnectionString);
        var hostCurrency = (await RowsAsync(host,
            "SELECT c.isocurrencycode, c.exchangerate FROM data.transactioncurrency c JOIN data.organization o ON o.basecurrencyid = c.transactioncurrencyid"))
            .Should().ContainSingle().Subject;
        hostCurrency["isocurrencycode"].Should().Be("EUR");
        hostCurrency["exchangerate"].Should().Be(1m);

        // Seeding again changes nothing.
        await SeedAsync(Options);
        await AssertBaseCurrencyAsync(_factory, "EUR", "Euro", "€", 2);
    }

    [Fact]
    public async Task Organization_made_before_currencies_gets_the_base_currency_at_the_next_start()
    {
        await SeedAsync(Options);
        await ExecuteAsync("""
            UPDATE data.organization SET basecurrencyid = NULL, basecurrencysymbol = NULL, basecurrencyprecision = NULL,
                pricingdecimalprecision = NULL, currencydisplayoption = NULL, currencyformatcode = NULL;
            DELETE FROM data.transactioncurrency;
            """);

        await SeedAsync(Options with { BaseCurrency = BaseCurrencySeed.From("GBP", "Pound Sterling", "£", 2) });

        await AssertBaseCurrencyAsync(_factory, "GBP", "Pound Sterling", "£", 2);
    }

    [Fact]
    public async Task Seed_BaseCurrency_USD_with_overrides_is_used()
    {
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Seed:BaseCurrency", "USD");
            b.UseSetting("Seed:BaseCurrencyName", "US Dollar");
            b.UseSetting("Seed:BaseCurrencySymbol", "$");
        });
        using var client = app.CreateClient(); // starts the host, which seeds its database
        var factory = TestProvider.Factory(app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!);

        await AssertBaseCurrencyAsync(factory, "USD", "US Dollar", "$", 2);
    }

    [Fact]
    public async Task Unknown_base_currency_code_stops_startup()
    {
        var direct = async () => await SeedAsync(Options with { BaseCurrency = new BaseCurrencySeed("XYZ", "X", "X", 2) });
        (await direct.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Seed:BaseCurrency").And.Contain("XYZ");
        // Nothing was seeded: the check runs before any table is made.
        (await ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'data'")).Should().Be(0);

        await using var app = fx.WithOwnDatabase(b => b.UseSetting("Seed:BaseCurrency", "XYZ"));
        var start = () => app.CreateClient();
        start.Should().Throw<Exception>().Where(e => e.ToString().Contains("Seed:BaseCurrency"));
    }

    [Fact]
    public async Task Restart_with_another_base_currency_keeps_the_first_and_warns()
    {
        await SeedAsync(Options);
        var log = new RequestLogCollector();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(log));

        await SeedAsync(Options with { BaseCurrency = BaseCurrencySeed.From("USD", "US Dollar", "$", null) }, loggers.CreateLogger<SystemSeed>());

        await AssertBaseCurrencyAsync(_factory, "EUR", "Euro", "€", 2);
        log.Drain().Should().ContainSingle(l => l.Contains("Warning") && l.Contains("Seed:BaseCurrency") && l.Contains("USD") && l.Contains("EUR"));
    }

    [Fact]
    public async Task Restart_without_the_setting_keeps_the_base_currency_and_does_not_warn()
    {
        await SeedAsync(Options with { BaseCurrency = BaseCurrencySeed.From("USD", "US Dollar", "$", null) });
        var log = new RequestLogCollector();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(log));

        await SeedAsync(Options, loggers.CreateLogger<SystemSeed>()); // Seed:BaseCurrency not given

        await AssertBaseCurrencyAsync(_factory, "USD", "US Dollar", "$", 2);
        log.Drain().Should().NotContain(l => l.Contains("Seed:BaseCurrency"));
    }

    [Fact]
    public async Task Reused_currency_row_is_normalised_into_the_base_currency()
    {
        await SeedAsync(Options);
        // An organization without base currency whose EUR row is inactive at another rate.
        await ExecuteAsync("""
            UPDATE data.organization SET basecurrencyid = NULL, basecurrencysymbol = NULL, basecurrencyprecision = NULL;
            UPDATE data.transactioncurrency SET exchangerate = 2.5, statecode = 1, statuscode = 2;
            """);

        await SeedAsync(Options);

        await AssertBaseCurrencyAsync(_factory, "EUR", "Euro", "€", 2);
    }

    [Fact]
    public async Task Reused_custom_currency_row_stops_startup()
    {
        await SeedAsync(Options);
        await ExecuteAsync("""
            UPDATE data.organization SET basecurrencyid = NULL, basecurrencysymbol = NULL, basecurrencyprecision = NULL;
            UPDATE data.transactioncurrency SET currencytype = 1;
            """);

        var act = async () => await SeedAsync(Options);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("EUR").And.Contain("custom");
        (await ScalarAsync("SELECT COUNT(*) FROM data.organization WHERE basecurrencyid IS NULL")).Should().Be(1, "nothing is written");
    }

    [Fact]
    public async Task Every_user_has_usersettings()
    {
        await SeedAsync(Options);
        var users = await RowsAsync("SELECT systemuserid, businessunitid, fullname FROM data.systemuser");
        users.Select(u => u["fullname"]).Should().BeEquivalentTo(["Ada", "SYSTEM"]);
        foreach (var user in users)
        {
            var settings = (await RowsAsync("SELECT * FROM data.usersettings WHERE systemuserid = @id", ("@id", user["systemuserid"]!)))
                .Should().ContainSingle(user["fullname"]!.ToString()).Subject;
            settings["ownerid"].Should().Be(user["systemuserid"]);
            settings["owneridtype"].Should().Be(8);
            settings["owninguser"].Should().Be(user["systemuserid"]);
            settings["owningbusinessunit"].Should().Be(user["businessunitid"]);
            settings["transactioncurrencyid"].Should().BeNull("no personal currency: the base one applies");
        }

        // A row deleted by SQL, and a user inserted by SQL without one, have theirs at the next seed run.
        var ada = users.Single(u => (string)u["fullname"]! == "Ada")["systemuserid"]!;
        await ExecuteAsync($"DELETE FROM data.usersettings WHERE systemuserid = '{ada}'");
        var extra = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO data.systemuser (systemuserid, fullname, domainname, businessunitid, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT '{extra}', N'Extra', N'Extra', businessunitid, businessunitid, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
            FROM data.businessunit WHERE parentbusinessunitid IS NULL
            """);
        await SeedAsync(Options);
        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings WHERE systemuserid = @id", ("@id", ada))).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings WHERE systemuserid = @id", ("@id", extra))).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings")).Should().Be(3);

        // The host's organization: the administrator and SYSTEM.
        var host = TestProvider.Factory(fx.ConnectionString);
        (await RowsAsync(host, """
            SELECT COUNT(*) AS n FROM data.systemuser u
            WHERE (u.systemuserid = @system OR u.azureactivedirectoryobjectid = @admin)
              AND EXISTS (SELECT 1 FROM data.usersettings s WHERE s.systemuserid = u.systemuserid)
            """, ("@system", SeedOptions.DeriveSystemUserId(XrmdFixture.OrgName)), ("@admin", fx.AdminObjectId)))
            .Single()["n"].Should().Be(2);
    }

    [Fact]
    public async Task Seed_tables_have_dataverse_names()
    {
        foreach (var store in new[] { HostStore, await SeededStoreAsync() })
        {
            var currency = store.GetEntity(CurrencySchema.Table)!;
            currency.EntitySetName.Should().Be("transactioncurrencies");
            currency.ObjectTypeCode.Should().Be(9105);
            currency.OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);
            currency.PrimaryIdAttribute.Should().Be(CurrencySchema.CurrencyId);
            currency.PrimaryNameAttribute.Should().Be("currencyname");
            store.GetEntityByObjectTypeCode(9105).Should().BeSameAs(currency);
            Attribute<StringAttributeMetadata>(currency, "currencyname").MaxLength.Should().Be(100);
            Attribute<StringAttributeMetadata>(currency, CurrencySchema.IsoCode).MaxLength.Should().Be(5);
            Attribute<StringAttributeMetadata>(currency, CurrencySchema.Symbol).MaxLength.Should().Be(10);
            Attribute<IntegerAttributeMetadata>(currency, CurrencySchema.Precision).Should().NotBeNull();
            var rate = Attribute<DecimalAttributeMetadata>(currency, CurrencySchema.ExchangeRate);
            (rate.Precision, rate.MinValue, rate.MaxValue).Should().Be((12, 0.000000000001m, 100000000000m));
            Attribute<PicklistAttributeMetadata>(currency, CurrencySchema.CurrencyType).OptionSet.Name.Should().Be("transactioncurrency_currencytype");
            foreach (var name in new[] { "currencyname", CurrencySchema.IsoCode, CurrencySchema.Symbol, CurrencySchema.Precision, CurrencySchema.ExchangeRate, CurrencySchema.CurrencyType })
                currency.Attributes.Single(a => a.LogicalName == name).RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired, name);

            var settings = store.GetEntity(CurrencySchema.UserSettings)!;
            settings.EntitySetName.Should().Be("usersettingscollection");
            settings.ObjectTypeCode.Should().Be(150);
            settings.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
            settings.PrimaryIdAttribute.Should().Be("systemuserid");
            settings.Attributes.Should().NotContain(a => a.LogicalName == "usersettingsid");
            Attribute<LookupAttributeMetadata>(settings, CurrencySchema.CurrencyId).Targets.Should().Equal(CurrencySchema.Table);
            Relationship(store, "transactioncurrency_usersettings", CurrencySchema.UserSettings, CurrencySchema.CurrencyId, CascadeType.NoCascade);

            var organization = store.GetEntity("organization")!;
            Attribute<LookupAttributeMetadata>(organization, CurrencySchema.BaseCurrencyId).Targets.Should().Equal(CurrencySchema.Table);
            Relationship(store, "basecurrency_organization", "organization", CurrencySchema.BaseCurrencyId, CascadeType.NoCascade);
            Attribute<PicklistAttributeMetadata>(organization, CurrencySchema.DisplayOption).OptionSet.Name.Should().Be("organization_currencydisplayoption");
            Attribute<PicklistAttributeMetadata>(organization, CurrencySchema.FormatCode).OptionSet.Options.Select(o => o.Value).Should().Equal(0, 1, 2, 3);
            Attribute<StringAttributeMetadata>(organization, CurrencySchema.BaseSymbol).MaxLength.Should().Be(5);
            foreach (var readOnly in new[] { CurrencySchema.BaseSymbol, CurrencySchema.BasePrecision })
                organization.Attributes.Single(a => a.LogicalName == readOnly).IsValidForUpdate.Should().BeFalse(readOnly);
            var pricing = Attribute<IntegerAttributeMetadata>(organization, CurrencySchema.PricingPrecision);
            (pricing.MinValue, pricing.MaxValue).Should().Be((0, 10));

            foreach (var table in new[] { "account", "contact" })
            {
                var entity = store.GetEntity(table)!;
                Attribute<LookupAttributeMetadata>(entity, CurrencySchema.CurrencyId).Targets.Should().Equal(CurrencySchema.Table);
                Attribute<DecimalAttributeMetadata>(entity, CurrencySchema.ExchangeRate).Precision.Should().Be(CurrencySchema.RecordRateDecimals);
                entity.Attributes.Should().NotContain(a => a is MoneyAttributeMetadata, "D1: no standard money column is seeded");
                var r = Relationship(store, CurrencySchema.RelationshipName(table), table, CurrencySchema.CurrencyId, CascadeType.Restrict);
                r.ReferencingEntityNavigationPropertyName.Should().Be(CurrencySchema.CurrencyId);
            }
        }

        // Pre-flight C14: the seed path gives Precision 12 its decimal(24,12) column; the record rate is decimal(23,10). (The host's
        // other tests make money tables, which have the record rate too since task 2: only the seed tables are compared.)
        foreach (var factory in new[] { TestProvider.Factory(fx.ConnectionString), _factory })
        {
            var columns = await RowsAsync(factory, """
                SELECT table_name AS t, numeric_precision AS p, numeric_scale AS s FROM information_schema.columns
                WHERE column_name = 'exchangerate' AND table_schema = 'data'
                    AND table_name IN ('account', 'contact', 'transactioncurrency') ORDER BY table_name
                """);
            columns.Select(c => ((string)c["t"]!, Convert.ToInt32(c["p"]), Convert.ToInt32(c["s"]))).Should().Equal(
                ("account", 23, 10), ("contact", 23, 10), ("transactioncurrency", 24, 12));
        }
    }

    private async Task<MetadataStore> SeededStoreAsync()
    {
        await SeedAsync(Options);
        return await NewStoreAsync();
    }

    private static T Attribute<T>(EntityMetadata entity, string name) where T : AttributeMetadata =>
        entity.Attributes.Single(a => a.LogicalName == name).Should().BeOfType<T>($"{entity.LogicalName}.{name}").Subject;

    private static OneToManyRelationshipMetadata Relationship(IMetadataStore store, string name, string referencing, string lookup, CascadeType delete)
    {
        var r = store.GetRelationship(name).Should().BeOfType<OneToManyRelationshipMetadata>(name).Subject;
        r.ReferencedEntity.Should().Be(CurrencySchema.Table, name);
        r.ReferencingEntity.Should().Be(referencing, name);
        r.ReferencingAttribute.Should().Be(lookup, name);
        r.CascadeConfiguration.Delete.Should().Be(delete, name);
        (r.CascadeConfiguration.Assign, r.CascadeConfiguration.Share, r.CascadeConfiguration.Unshare, r.CascadeConfiguration.Reparent,
            r.CascadeConfiguration.Merge).Should().Be((CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade,
            CascadeType.NoCascade), name);
        return r;
    }

    [Fact]
    public async Task Duplicate_iso_code_is_blocked_by_the_unique_index()
    {
        await SeedAsync(Options);
        var insert = async () => await ExecuteAsync("""
            INSERT INTO data.transactioncurrency (transactioncurrencyid, currencyname, isocurrencycode, currencysymbol, currencyprecision,
                exchangerate, currencytype, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT NEWID(), N'Second Euro', N'EUR', N'E', 2, 1.5, 0, businessunitid, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
            FROM data.businessunit WHERE parentbusinessunitid IS NULL
            """);
        var violation = (await insert.Should().ThrowAsync<System.Data.Common.DbException>()).Which;
        TestSql.Dialect.Classify(violation).Should().Be(DbErrorKind.UniqueViolation);
        violation.Message.Should().Contain(TestSql.Dialect.ObjectName("UX_transactioncurrency_isocurrencycode"));
    }

    [Fact]
    public async Task Basic_user_reads_currencies_and_writes_own_settings()
    {
        var host = TestProvider.Factory(fx.ConnectionString);
        var baseId = (Guid)(await RowsAsync(host, "SELECT basecurrencyid FROM data.organization")).Single()["basecurrencyid"]!;
        var objectId = Guid.NewGuid();
        var root = await RootBusinessUnitAsync(fx);
        var userId = await CreateUserAsync(fx, root, objectId);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        // A user inserted by SQL after the start has no settings row yet (the Core adds it on systemuser Create, a later task).
        await using (var conn = await host.OpenAsync())
        await using (var cmd = TestSql.Command(conn, """
            INSERT INTO data.usersettings (systemuserid, ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (@id, @id, 8, @id, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """))
        {
            cmd.AddParameter("@id", userId);
            cmd.AddParameter("@bu", root);
            await cmd.ExecuteNonQueryAsync();
        }

        var service = ServiceFor(fx, objectId);
        (await service.RetrieveAsync(CurrencySchema.Table, baseId, new ColumnSet(CurrencySchema.IsoCode)))[CurrencySchema.IsoCode].Should().Be("EUR");
        var all = await service.RetrieveMultipleAsync(new QueryExpression(CurrencySchema.Table) { ColumnSet = new ColumnSet(CurrencySchema.IsoCode) });
        all.Entities.Should().NotBeEmpty();

        await service.UpdateAsync(new Entity(CurrencySchema.UserSettings, userId)
        {
            [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, baseId),
        });
        (await service.RetrieveAsync(CurrencySchema.UserSettings, userId, new ColumnSet(CurrencySchema.CurrencyId)))
            .GetAttributeValue<EntityReference>(CurrencySchema.CurrencyId).Id.Should().Be(baseId);

        // Another user's settings stay out of reach (Basic depth), and currencies are not written by a Basic User.
        var adminId = (Guid)(await RowsAsync(host, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId))).Single()["systemuserid"]!;
        var other = async () => await service.UpdateAsync(new Entity(CurrencySchema.UserSettings, adminId)
        {
            [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, baseId),
        });
        (await other.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(403);
        var write = async () => await service.UpdateAsync(new Entity(CurrencySchema.Table, baseId) { [CurrencySchema.Symbol] = "E" });
        (await write.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task Currency_top_up_reaches_an_existing_organization_once()
    {
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        var store = HostStore;
        string Name(AccessRights right, string table) => PrivilegeNames.For(right, store.GetEntity(table)!);
        var expected = new[]
        {
            (Name(AccessRights.ReadAccess, CurrencySchema.Table), 8), (Name(AccessRights.AppendToAccess, CurrencySchema.Table), 8),
            (Name(AccessRights.ReadAccess, CurrencySchema.UserSettings), 1), (Name(AccessRights.WriteAccess, CurrencySchema.UserSettings), 1), (Name(AccessRights.AppendAccess, CurrencySchema.UserSettings), 1),
        };
        var host = TestProvider.Factory(fx.ConnectionString);
        async Task<long> CountAsync(string privilege, int depth) => Convert.ToInt64((await RowsAsync(host,
            "SELECT COUNT(*) AS n FROM data.roleprivileges WHERE roleid = @role AND privilegeid = @p AND privilegedepthmask = @d",
            ("@role", StandardRoles.BasicUser), ("@p", PrivilegeNames.IdOf(privilege)), ("@d", depth))).Single()["n"]);
        async Task ExecAsync(string sql)
        {
            await using var conn = await host.OpenAsync();
            await using var cmd = TestSql.Command(conn, sql);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var (privilege, depth) in expected) (await CountAsync(privilege, depth)).Should().Be(1, privilege);

        var ids = string.Join(", ", expected.Select(e => $"'{PrivilegeNames.IdOf(e.Item1)}'"));
        await ExecAsync($"DELETE FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ({ids})");
        await ExecAsync("DELETE FROM meta.upgrade_step WHERE name = N'currency-role-privileges'");
        try
        {
            await bootstrap.EnsureAsync(default);
            foreach (var (privilege, depth) in expected) (await CountAsync(privilege, depth)).Should().Be(1, privilege);
        }
        finally
        {
            foreach (var (privilege, depth) in expected)
                await ExecAsync($"""
                    IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{PrivilegeNames.IdOf(privilege)}')
                        INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                        VALUES (NEWID(), '{StandardRoles.BasicUser}', '{PrivilegeNames.IdOf(privilege)}', {depth}, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                    """);
            Cache(fx).Invalidate();
        }
    }
}
