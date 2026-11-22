using System.Data.Common;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>A currency as the read path formats money with it (plan 2.10).</summary>
/// <param name="Id">The <c>transactioncurrencyid</c>.</param>
/// <param name="IsoCode">The <c>isocurrencycode</c>, shown instead of the symbol with <c>currencydisplayoption</c> 1.</param>
/// <param name="Symbol">The <c>currencysymbol</c>.</param>
/// <param name="Precision">The <c>currencyprecision</c>.</param>
/// <param name="Rate">The <c>exchangerate</c> (0 when the row has none).</param>
/// <param name="IsActive">True when <c>statecode</c> is 0 (an inactive currency still formats the records that keep it).</param>
public sealed record CurrencyInfo(Guid Id, string IsoCode, string Symbol, int Precision, decimal Rate, bool IsActive);

/// <summary>
/// The currencies of the organization and its money settings at one point in time (plan 2.10): <c>organization.basecurrencyid</c>,
/// <c>pricingdecimalprecision</c>, <c>currencydisplayoption</c> (0 symbol, 1 ISO code) and <c>currencyformatcode</c>.
/// </summary>
public sealed record CurrencySnapshot(Guid BaseCurrencyId, int PricingPrecision, int DisplayOption, int FormatCode,
    IReadOnlyDictionary<Guid, CurrencyInfo> Currencies)
{
    /// <summary>No currency at all (an organization not seeded yet): every money value keeps the <c>N2</c> format.</summary>
    public static CurrencySnapshot Empty { get; } =
        new(Guid.Empty, CurrencySchema.DefaultPricingPrecision, 0, 0, new Dictionary<Guid, CurrencyInfo>());

    /// <summary>
    /// The format of a value of <paramref name="a"/> on a record in the currency <paramref name="currencyId"/> (ruling X11): a
    /// <c>{money}_base</c> takes the base currency whatever the record's; any other money its record's currency. The symbol is the
    /// currency's (its ISO code with display option 1), the precision the attribute's effective one
    /// (<see cref="CurrencyMath.EffectivePrecision"/>). Null, which keeps the <c>N2</c> format, without a known currency.
    /// </summary>
    public MoneyFormat? FormatFor(MoneyAttributeMetadata a, Guid? currencyId)
    {
        ArgumentNullException.ThrowIfNull(a);
        var id = a.IsBaseCurrency == true ? BaseCurrencyId : currencyId;
        if (id is not { } known || !Currencies.TryGetValue(known, out var currency)) return null;
        return new MoneyFormat(DisplayOption == 1 ? currency.IsoCode : currency.Symbol,
            CurrencyMath.EffectivePrecision(a, currency.Precision, PricingPrecision), FormatCode);
    }
}

/// <summary>
/// The <see cref="ICurrencyCatalog"/> of the host: one snapshot of <c>transactioncurrency</c> and the organization's money settings,
/// loaded lazily and kept until <see cref="Invalidate"/>. A load that an invalidation overtakes is used once and not cached, so a
/// snapshot read before a currency write commits never outlives it. Singleton, thread-safe.
/// </summary>
public sealed class CurrencyCatalog(IDbConnectionFactory factory) : ICurrencyCatalog
{
    private readonly Lock _gate = new();
    private CurrencySnapshot? _snapshot;
    private long _generation;

    /// <inheritdoc />
    public async Task<CurrencySnapshot> GetAsync(CancellationToken ct)
    {
        if (Cached(out var snapshot, out var generation)) return snapshot;
        await using var conn = await factory.OpenAsync(ct);
        return Keep(await LoadAsync(new DataSession(conn, null), ct), generation);
    }

    /// <inheritdoc />
    public async Task<CurrencySnapshot> GetAsync(DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Cached(out var snapshot, out var generation)) return snapshot;
        var loaded = await LoadAsync(session, ct);
        return session.Transaction is null ? Keep(loaded, generation) : loaded;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
            _generation++;
        }
    }

    private bool Cached(out CurrencySnapshot snapshot, out long generation)
    {
        lock (_gate)
        {
            snapshot = _snapshot!;
            generation = _generation;
            return _snapshot is not null;
        }
    }

    /// <summary>Caches <paramref name="loaded"/> unless an invalidation happened since the load began.</summary>
    private CurrencySnapshot Keep(CurrencySnapshot loaded, long generation)
    {
        lock (_gate)
        {
            if (_generation == generation) _snapshot = loaded;
        }
        return loaded;
    }

    private static async Task<CurrencySnapshot> LoadAsync(DataSession s, CancellationToken ct)
    {
        var data = DdlGenerator.DataSchema;
        await using var cmd = s.Command(s.Dialect.Limit(
            $"SELECT {CurrencySchema.BaseCurrencyId}, {CurrencySchema.PricingPrecision}, {CurrencySchema.DisplayOption}, " +
            $"{CurrencySchema.FormatCode} FROM {s.Quote(data)}.{s.Quote("organization")}", "1") + ";\n" +
            $"SELECT {CurrencySchema.CurrencyId}, {CurrencySchema.IsoCode}, {CurrencySchema.Symbol}, {CurrencySchema.Precision}, " +
            $"{CurrencySchema.ExchangeRate}, statecode FROM {s.Quote(data)}.{s.Quote(CurrencySchema.Table)}");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var (baseId, pricing, display, format) = (Guid.Empty, CurrencySchema.DefaultPricingPrecision, 0, 0);
        if (await rd.ReadAsync(ct))
        {
            if (!rd.IsDBNull(0)) baseId = rd.GetGuid(0);
            if (!rd.IsDBNull(1)) pricing = rd.GetInt32(1);
            display = Int(rd, 2);
            format = Int(rd, 3);
        }
        await rd.NextResultAsync(ct);
        var currencies = new Dictionary<Guid, CurrencyInfo>();
        while (await rd.ReadAsync(ct))
        {
            var id = rd.GetGuid(0);
            var code = rd.IsDBNull(1) ? "" : rd.GetString(1);
            currencies[id] = new CurrencyInfo(id, code, rd.IsDBNull(2) ? code : rd.GetString(2),
                rd.IsDBNull(3) ? CurrencyMath.DefaultPrecision : rd.GetInt32(3), rd.IsDBNull(4) ? 0m : rd.GetDecimal(4), Int(rd, 5) == 0);
        }
        return new CurrencySnapshot(baseId, pricing, display, format, currencies);
    }

    private static int Int(DbDataReader rd, int ordinal) => rd.IsDBNull(ordinal) ? 0 : rd.GetInt32(ordinal);
}
