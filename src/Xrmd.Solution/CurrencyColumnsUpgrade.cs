using System.Data;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization whose tables got money columns before plan 2.10 (ruling X14): each table with a money
/// column and no currency pair gets <c>transactioncurrencyid</c> + <c>exchangerate</c> (nullable columns) and the relationship
/// <c>TransactionCurrency_{table}</c> (Delete Restrict), and its rows the base currency at rate 1 (their <c>_base</c> already
/// equals the value, which is right at rate 1); every <c>_base</c> attribute gets the Dataverse metadata (Precision 4,
/// PrecisionSource 2, ±922337203685477). Runs after <see cref="SystemSeed"/> (which gives the organization its base currency and
/// backfills <c>usersettings</c>, pre-flight C7).
/// </summary>
/// <remarks>
/// One metadata session per table: the columns, the relationship and the backfill of its rows are committed together, so a
/// table is either upgraded with its rows or not at all. The backfill is raw SQL (not audited); it bumps <c>versionnumber</c>, so
/// change tracking clients see those rows once. Idempotent: an upgraded table has the pair and is skipped, so a second run
/// changes nothing.
/// </remarks>
public static class CurrencyColumnsUpgrade
{
    /// <summary>Runs the upgrade.</summary>
    /// <param name="store">The organization's metadata store (loaded, seeded).</param>
    /// <param name="factory">The organization's database: reads the base currency before any metadata write.</param>
    /// <param name="logger">Receives one line per upgraded table.</param>
    /// <exception cref="NotSupportedException"><paramref name="store"/> is not the server's <see cref="MetadataStore"/>.</exception>
    /// <exception cref="InvalidOperationException">A table needs the backfill and the organization has no base currency.</exception>
    public static async Task EnsureAsync(IMetadataStore store, IDbConnectionFactory factory, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        if (store is not MetadataStore concrete)
            throw new NotSupportedException("The currency columns upgrade needs the metadata store of the server.");

        var tables = store.GetAllEntities()
            .Where(e => e.IsIntersect != true && CurrencySchema.MoneyAttributes(e).Count > 0 && NeedsUpgrade(store, e))
            .Select(e => e.LogicalName)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (tables.Count > 0) await UpgradeTablesAsync(concrete, factory, tables, logger, ct);

        // #183: the currency relationships of custom tables made before the fix named their navigation property
        // TransactionCurrencyId; Dataverse names it transactioncurrencyid.
        IReadOnlyList<string> renamed = [];
        if (store.GetOneToManyRelationships().Any(r => r.ReferencedEntity == CurrencySchema.Table
                                                       && r.ReferencingEntityNavigationPropertyName == MetadataStore.LegacyCurrencyNavigation))
            await concrete.InUpgradeSessionAsync(async session => renamed = await concrete.RenameLegacyCurrencyNavigationAsync(session, ct), ct);
        foreach (var name in renamed)
            logger?.LogInformation("Currency relationship '{Relationship}': navigation property renamed to 'transactioncurrencyid' as in Dataverse.", name);
    }

    private static async Task UpgradeTablesAsync(MetadataStore concrete, IDbConnectionFactory factory, List<string> tables, ILogger? logger,
        CancellationToken ct)
    {
        var baseCurrency = await BaseCurrencyAsync(factory, ct);
        foreach (var table in tables)
        {
            var rows = 0;
            await concrete.InUpgradeSessionAsync(async session =>
            {
                if (!await concrete.EnsureCurrencyColumnsAsync(session, table, ct)) return;
                await using var cmd = session.Command(
                    $"UPDATE {session.DataTable(table)} SET {session.Quote(CurrencySchema.CurrencyId)} = @currency, {session.Quote(CurrencySchema.ExchangeRate)} = 1 " +
                    $"WHERE {session.Quote(CurrencySchema.CurrencyId)} IS NULL");
                cmd.AddParameter("@currency", DbType.Guid, baseCurrency ?? throw new InvalidOperationException(
                    $"Table '{table}' needs its currency columns, but the organization has no base currency: the seed must run first."));
                // No command timeout: a large table can outlast the default 30 s.
                cmd.CommandTimeout = 0;
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            logger?.LogInformation("Currency columns of '{Table}' upgraded: {Rows} rows set to the base currency at rate 1.", table, rows);
        }
    }

    /// <summary>The table lacks the pair or its relationship, or a <c>_base</c> lacks the Dataverse metadata.</summary>
    private static bool NeedsUpgrade(IMetadataStore store, Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity) =>
        !CurrencySchema.HasCurrencyColumns(entity)
        || store.GetRelationship(CurrencySchema.RelationshipName(entity.LogicalName)) is null
        || (entity.Attributes ?? []).OfType<Microsoft.Xrm.Sdk.Metadata.MoneyAttributeMetadata>()
            .Any(m => m.IsBaseCurrency == true && SystemAttributes.SetBaseMoneyMetadata(MetadataSerializer.Clone(m)));

    private static async Task<Guid?> BaseCurrencyAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var d = SqlDialects.For(conn);
        await using var cmd = conn.Command(
            d.Limit($"SELECT {d.Quote(CurrencySchema.BaseCurrencyId)} FROM {d.DataTable("organization")}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }
}
