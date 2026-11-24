using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Solution;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The currency columns of a table with money (plan 2.10, task 2, rulings X4 and X14): the pair <c>transactioncurrencyid</c> +
/// <c>exchangerate</c> and the relationship <c>TransactionCurrency_{table}</c> (Delete Restrict) come with the first money column,
/// stay when the last one goes, cannot be deleted; a <c>_base</c> cannot be deleted on its own; an existing database gets them
/// at startup (<see cref="CurrencyColumnsUpgrade"/>), and its rows the base currency at rate 1.
/// </summary>
[Collection("xrmd")]
public sealed class CurrencyColumnsTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private IDbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_curcol_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(_factory).MigrateAsync();
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
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

    private async Task InSessionAsync(Func<DataSession, Task> write)
    {
        await using var conn = await _factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            await write(session);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            session.RolledBack();
            throw;
        }
        session.Committed();
    }

    private async Task<List<Dictionary<string, object?>>> RowsAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ColumnsAsync(string table) =>
        await TestSql.ColumnsAsync(TestProvider.WithDatabase(fx.ConnectionString, _database!), "data", table);

    private static EntityMetadata Order(params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata
        {
            LogicalName = "xv_order", SchemaName = "xv_Order", OwnershipType = OwnershipTypes.UserOwned,
            DisplayName = SystemAttributes.CreateLabel("Order", 1033),
        };
        entity.SetAttributes(attributes);
        return entity;
    }

    private static StringAttributeMetadata Name() => new("xv_Name") { LogicalName = "xv_name", MaxLength = 100 };

    private static MoneyAttributeMetadata Money(string name) => new(name) { LogicalName = name, Precision = 2 };

    private static void ShouldHaveCurrencyRelationship(MetadataStore store, string table, string navigation)
    {
        var relationship = store.GetRelationship(CurrencySchema.RelationshipName(table)).Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        relationship.ReferencedEntity.Should().Be(CurrencySchema.Table);
        relationship.ReferencedAttribute.Should().Be(CurrencySchema.CurrencyId);
        relationship.ReferencingEntity.Should().Be(table);
        relationship.ReferencingAttribute.Should().Be(CurrencySchema.CurrencyId);
        relationship.ReferencingEntityNavigationPropertyName.Should().Be(navigation);
        relationship.CascadeConfiguration.Delete.Should().Be(CascadeType.Restrict);
        relationship.CascadeConfiguration.Assign.Should().Be(CascadeType.NoCascade);
        relationship.IsCustomRelationship.Should().BeFalse();
    }

    private static void ShouldHavePair(EntityMetadata entity)
    {
        CurrencySchema.HasCurrencyColumns(entity).Should().BeTrue();
        entity.Attributes.Count(a => a.LogicalName == CurrencySchema.CurrencyId).Should().Be(1);
        entity.Attributes.Count(a => a.LogicalName == CurrencySchema.ExchangeRate).Should().Be(1);
        entity.Attributes.Single(a => a.LogicalName == CurrencySchema.CurrencyId).IsCustomAttribute.Should().NotBe(true);
    }

    [Fact]
    public async Task CreateEntity_with_money_gets_pair_and_relationship()
    {
        var store = await NewStoreAsync();

        await store.CreateEntityAsync(Order(Money("xv_amount")), Name());

        (await ColumnsAsync("xv_order")).Should().Contain(["transactioncurrencyid", "exchangerate", "xv_amount", "xv_amount_base"]);
        var cs = TestProvider.WithDatabase(fx.ConnectionString, _database!);
        (await TestSql.ColumnTypeAsync(cs, "data", "xv_order", "transactioncurrencyid"))!.Value.Type
            .Should().Be(TestProvider.IsPostgres ? "uuid" : "uniqueidentifier");
        var rate = (await TestSql.ColumnTypeAsync(cs, "data", "xv_order", "exchangerate"))!.Value;
        rate.Type.Should().Be(TestProvider.IsPostgres ? "numeric" : "decimal");
        rate.Scale.Should().Be(10);
        ShouldHavePair(store.GetEntity("xv_order")!);
        ShouldHaveCurrencyRelationship(store, "xv_order", "transactioncurrencyid");

        // Persisted: a fresh load sees the same.
        var reloaded = await NewStoreAsync();
        ShouldHavePair(reloaded.GetEntity("xv_order")!);
        ShouldHaveCurrencyRelationship(reloaded, "xv_order", "transactioncurrencyid");
        reloaded.GetOneToManyRelationships().Should().ContainSingle(r => r.ReferencingEntity == "xv_order" && r.ReferencingAttribute == "transactioncurrencyid")
            .Which.SchemaName.Should().Be("TransactionCurrency_xv_order");
    }

    [Fact]
    public async Task Table_without_money_has_no_pair()
    {
        var store = await NewStoreAsync();

        await store.CreateEntityAsync(Order(), Name());

        CurrencySchema.HasCurrencyColumns(store.GetEntity("xv_order")!).Should().BeFalse();
        (await ColumnsAsync("xv_order")).Should().NotContain(["transactioncurrencyid", "exchangerate"]);
        store.GetRelationship("TransactionCurrency_xv_order").Should().BeNull();
    }

    [Fact]
    public async Task CreateAttribute_first_money_adds_pair_second_does_not()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(), Name());

        await store.AddAttributeAsync("xv_order", Money("xv_amount"));
        ShouldHavePair(store.GetEntity("xv_order")!);
        ShouldHaveCurrencyRelationship(store, "xv_order", "transactioncurrencyid");
        var stamp = store.VersionStamp;

        await store.AddAttributeAsync("xv_order", Money("xv_cost"));
        ShouldHavePair(store.GetEntity("xv_order")!);
        store.VersionStamp.Should().Be(stamp + 1, "the second money column brings only itself and its _base");
        (await ColumnsAsync("xv_order")).Count(c => c is "transactioncurrencyid" or "exchangerate").Should().Be(2);
        (await RowsAsync("SELECT COUNT(*) AS n FROM meta.relationship WHERE schema_name = N'TransactionCurrency_xv_order'"))
            .Single()["n"].Should().Be(1);
    }

    [Fact]
    public async Task Callers_cannot_define_the_pair()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(), Name());

        var lookup = SystemAttributes.CurrencyLookup();
        await store.Invoking(s => s.AddAttributeAsync("xv_order", lookup)).Should().ThrowAsync<XrmdValidationException>()
            .WithMessage("*transactioncurrencyid*generated*");
        await store.Invoking(s => s.CreateEntityAsync(new EntityMetadata { LogicalName = "xv_other", SchemaName = "xv_Other" }.With(SystemAttributes.ExchangeRate()), Name()))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*exchangerate*generated*");
    }

    [Fact]
    public async Task Pair_stays_after_last_money_is_deleted()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(Money("xv_amount")), Name());

        await InSessionAsync(s => store.DeleteAttributeAsync(s, "xv_order", "xv_amount", default));

        var order = store.GetEntity("xv_order")!;
        CurrencySchema.MoneyAttributes(order).Should().BeEmpty();
        ShouldHavePair(order);
        ShouldHaveCurrencyRelationship(store, "xv_order", "transactioncurrencyid");
        (await ColumnsAsync("xv_order")).Should().Contain(["transactioncurrencyid", "exchangerate"]).And.NotContain("xv_amount_base");

        // A money column added again does not add the pair twice.
        await store.AddAttributeAsync("xv_order", Money("xv_cost"));
        ShouldHavePair(store.GetEntity("xv_order")!);
    }

    [Theory]
    [InlineData("transactioncurrencyid")]
    [InlineData("exchangerate")]
    public async Task Pair_cannot_be_deleted(string attribute)
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(Money("xv_amount")), Name());

        var fault = (await store.Invoking(s => InSessionAsync(x => s.DeleteAttributeAsync(x, "xv_order", attribute, default)))
            .Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        fault.HttpStatus.Should().Be(403);
        ShouldHavePair(store.GetEntity("xv_order")!);
    }

    [Fact]
    public async Task Deleting_base_attribute_gives_0x80048cfe()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(Money("xv_amount")), Name());

        var fault = (await store.Invoking(s => InSessionAsync(x => s.DeleteAttributeAsync(x, "xv_order", "xv_amount_base", default)))
            .Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(ErrorCodes.CannotDeleteBaseMoneyCalculationAttribute);
        unchecked((uint)fault.ErrorCode).Should().Be(0x80048cfe);
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_amount_base").And.Contain("xv_amount");
        store.GetEntity("xv_order")!.Attributes.Should().Contain(a => a.LogicalName == "xv_amount_base");
    }

    /// <summary>Review M3: the currency table's own key and rate carry the pair's names, so it cannot get a money column.</summary>
    [Fact]
    public async Task Money_column_on_transactioncurrency_is_refused()
    {
        var store = await NewStoreAsync();
        var stamp = store.VersionStamp;

        await store.Invoking(s => s.AddAttributeAsync(CurrencySchema.Table, Money("xv_amount"))).Should().ThrowAsync<XrmdValidationException>()
            .WithMessage("*'transactioncurrency' cannot have a money column*");
        store.VersionStamp.Should().Be(stamp);
        (await ColumnsAsync(CurrencySchema.Table)).Should().NotContain("xv_amount");
    }

    /// <summary>
    /// Review M1: a table that already has the pair is never backfilled: a row without currency (allowed when all its money is null,
    /// X6) keeps it, even when the upgrade fixes the table's <c>_base</c> metadata.
    /// </summary>
    [Fact]
    public async Task Upgrade_leaves_rows_of_a_table_that_has_the_pair()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(Money("xv_amount")), Name());
        await ExecuteAsync("""
            UPDATE meta.attribute SET definition = JSON_MODIFY(definition, '$.Precision', 2)
                WHERE entity_logical_name = N'xv_order' AND logical_name = N'xv_amount_base';
            INSERT INTO data.xv_order (xv_orderid, xv_name, statecode, statuscode) VALUES (NEWID(), N'No currency', 0, 1);
            """);
        var old = await NewStoreAsync();
        ((MoneyAttributeMetadata)old.GetEntity("xv_order")!.Attributes.Single(a => a.LogicalName == "xv_amount_base")).Precision.Should().Be(2);
        var before = await RowsAsync("SELECT transactioncurrencyid, exchangerate, CAST(versionnumber AS bigint) AS v FROM data.xv_order");

        await CurrencyColumnsUpgrade.EnsureAsync(old, _factory);

        ((MoneyAttributeMetadata)old.GetEntity("xv_order")!.Attributes.Single(a => a.LogicalName == "xv_amount_base")).Precision.Should().Be(4);
        var after = await RowsAsync("SELECT transactioncurrencyid, exchangerate, CAST(versionnumber AS bigint) AS v FROM data.xv_order");
        after.Should().BeEquivalentTo(before);
        after.Single()["transactioncurrencyid"].Should().BeNull();
    }

    /// <summary>
    /// RF3: a money table and its rows made before plan 2.10 (simulated by dropping the pair's columns, attribute rows and relationship,
    /// and giving the <c>_base</c> its old metadata) get the pair, the relationship, the base currency at rate 1 and the
    /// <c>_base</c> metadata at the next start; a second start changes nothing.
    /// </summary>
    [Fact]
    public async Task Upgrade_adds_currency_columns_and_backfills_rows_once()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(Money("xv_amount")), Name());
        await store.CreateEntityAsync(new EntityMetadata { LogicalName = "xv_plain", SchemaName = "xv_Plain" }, new StringAttributeMetadata("xv_Name")
            { LogicalName = "xv_name", MaxLength = 100 });
        await ExecuteAsync($"""
            {TestSql.Dialect.DropColumns("data.xv_order", ["transactioncurrencyid", "exchangerate"])}
            DELETE FROM meta.attribute WHERE entity_logical_name = N'xv_order' AND logical_name IN (N'transactioncurrencyid', N'exchangerate');
            DELETE FROM meta.relationship WHERE schema_name = N'TransactionCurrency_xv_order';
            """);
        // The base column of before task 2: Precision 2, no PrecisionSource (the definition rewritten here, on both engines).
        var cs = TestProvider.WithDatabase(fx.ConnectionString, _database!);
        const string baseColumn = "entity_logical_name = N'xv_order' AND logical_name = N'xv_amount_base'";
        var definition = System.Text.Json.Nodes.JsonNode.Parse((string)(await TestSql.ScalarAsync(cs, $"SELECT definition FROM meta.attribute WHERE {baseColumn}"))!)!;
        definition["Precision"] = 2;
        definition.AsObject().Remove("PrecisionSource");
        await TestSql.ExecAsync(cs, $"UPDATE meta.attribute SET definition = @definition WHERE {baseColumn}", ("@definition", definition.ToJsonString()));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO data.xv_order (xv_orderid, xv_name, xv_amount, xv_amount_base, statecode, statuscode, createdon, modifiedon)
            VALUES ('{first}', N'One', 10.5, 10.5, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                   ('{second}', N'Two', NULL, NULL, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """);
        var old = await NewStoreAsync();
        CurrencySchema.HasCurrencyColumns(old.GetEntity("xv_order")!).Should().BeFalse("the simulated table predates the pair");
        ((MoneyAttributeMetadata)old.GetEntity("xv_order")!.Attributes.Single(a => a.LogicalName == "xv_amount_base")).Precision.Should().Be(2);

        await CurrencyColumnsUpgrade.EnsureAsync(old, _factory);

        var baseId = (Guid)(await RowsAsync("SELECT basecurrencyid FROM data.organization")).Single()["basecurrencyid"]!;
        foreach (var upgraded in new[] { old, await NewStoreAsync() })
        {
            var order = upgraded.GetEntity("xv_order")!;
            ShouldHavePair(order);
            ShouldHaveCurrencyRelationship(upgraded, "xv_order", "transactioncurrencyid");
            var b = (MoneyAttributeMetadata)order.Attributes.Single(a => a.LogicalName == "xv_amount_base");
            (b.Precision, b.PrecisionSource, b.MinValue, b.MaxValue).Should().Be((4, 2, -922337203685477d, 922337203685477d));
            CurrencySchema.HasCurrencyColumns(upgraded.GetEntity("xv_plain")!).Should().BeFalse("a table without money gets no pair");
        }
        var rows = await RowsAsync("SELECT xv_orderid, transactioncurrencyid, exchangerate, xv_amount_base, CAST(versionnumber AS bigint) AS v FROM data.xv_order");
        rows.Should().HaveCount(2).And.OnlyContain(r => (Guid)r["transactioncurrencyid"]! == baseId && (decimal)r["exchangerate"]! == 1m);
        rows.Single(r => (Guid)r["xv_orderid"]! == first)["xv_amount_base"].Should().Be(10.5m);

        // A second start is a no-op: no metadata change, no row touched.
        var restarted = await NewStoreAsync();
        var stamp = restarted.VersionStamp;
        await CurrencyColumnsUpgrade.EnsureAsync(restarted, _factory);
        restarted.VersionStamp.Should().Be(stamp);
        (await RowsAsync("SELECT xv_orderid, CAST(versionnumber AS bigint) AS v FROM data.xv_order"))
            .Select(r => (r["xv_orderid"], r["v"])).Should().BeEquivalentTo(rows.Select(r => (r["xv_orderid"], r["v"])));
        (await RowsAsync("SELECT COUNT(*) AS n FROM meta.relationship WHERE schema_name = N'TransactionCurrency_xv_order'"))
            .Single()["n"].Should().Be(1);
    }

    /// <summary>
    /// #183: a currency relationship saved before the fix names its navigation property <c>TransactionCurrencyId</c>; the next
    /// start renames it <c>transactioncurrencyid</c> (as Dataverse does), keeps everything else, and a second start changes nothing.
    /// </summary>
    [Fact]
    public async Task Upgrade_renames_the_legacy_currency_navigation_property_once()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(Order(Money("xv_amount")), Name());
        await ExecuteAsync("""
            UPDATE meta.relationship
            SET definition = JSON_MODIFY(definition, '$.ReferencingEntityNavigationPropertyName', N'TransactionCurrencyId')
            WHERE schema_name = N'TransactionCurrency_xv_order';
            """);
        var old = await NewStoreAsync();
        ShouldHaveCurrencyRelationship(old, "xv_order", "TransactionCurrencyId");

        await CurrencyColumnsUpgrade.EnsureAsync(old, _factory);

        foreach (var upgraded in new[] { old, await NewStoreAsync() })
            ShouldHaveCurrencyRelationship(upgraded, "xv_order", "transactioncurrencyid");
        var restarted = await NewStoreAsync();
        var stamp = restarted.VersionStamp;
        await CurrencyColumnsUpgrade.EnsureAsync(restarted, _factory);
        restarted.VersionStamp.Should().Be(stamp);
    }

    /// <summary>
    /// Parked review item (task 1, m3): an account table made before plan 2.10 (no pair, no relationship) gets both back from the
    /// next start's seed, with the relationship <c>transactioncurrency_account</c> on Delete Restrict.
    /// </summary>
    [Fact]
    public async Task Account_without_the_pair_gets_it_back_at_the_next_start()
    {
        await ExecuteAsync($"""
            {TestSql.Dialect.DropColumns("data.account", ["transactioncurrencyid", "exchangerate"])}
            DELETE FROM meta.attribute WHERE entity_logical_name = N'account' AND logical_name IN (N'transactioncurrencyid', N'exchangerate');
            DELETE FROM meta.relationship WHERE schema_name = N'transactioncurrency_account';
            """);
        var old = await NewStoreAsync();
        CurrencySchema.HasCurrencyColumns(old.GetEntity("account")!).Should().BeFalse();
        old.GetRelationship("transactioncurrency_account").Should().BeNull();

        await new SystemSeed(old, _factory).EnsureAsync(Options);
        await CurrencyColumnsUpgrade.EnsureAsync(old, _factory);

        foreach (var store in new[] { old, await NewStoreAsync() })
        {
            ShouldHavePair(store.GetEntity("account")!);
            ShouldHaveCurrencyRelationship(store, "account", "transactioncurrencyid");
        }
        (await ColumnsAsync("account")).Should().Contain(["transactioncurrencyid", "exchangerate"]);
    }
}

internal static class EntityMetadataTestExtensions
{
    public static EntityMetadata With(this EntityMetadata entity, params AttributeMetadata[] attributes)
    {
        entity.SetAttributes(attributes);
        return entity;
    }
}
