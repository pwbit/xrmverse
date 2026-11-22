using System.Data;
using System.Data.Common;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Core;

/// <summary>
/// Stage 30 of Create and Update on a table with the currency pair (plan 2.10, rulings X5, X6, X8–X10): resolves the record's
/// currency, stores its rate in <c>exchangerate</c>, rounds every money value of the row to its effective precision and puts each
/// <c>{money}_base</c> = value / rate in the row. The currency (rate, precision, state) and the organization's pricing precision
/// are read in the request's session, never from a cache, so a rate committed before the request is the one applied.
/// </summary>
public static class CurrencyStamp
{
    private const string IdParameter = "@_id";

    /// <summary>
    /// Completes <paramref name="row"/> (the values this Create or Update writes, after the pre-operation plug-ins) of the record
    /// <paramref name="id"/>. On Create it always runs: the currency is the row's, else the one in the effective caller's
    /// <c>usersettings</c> when it is active, else the base currency. On Update it runs only when the row sets the currency or a
    /// money column: the rate is refreshed from the currency and every <c>_base</c> recomputed, from the stored value of each money
    /// column the row does not carry. A table without the pair is left alone.
    /// </summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) for a currency that does not exist; InvalidCurrency (400) for
    /// a new currency that is inactive; CurrencyCannotBeNullDueToNonNullMoneyFields (400) when the currency is cleared while a
    /// money value remains; BaseCurrencyOverflow/BaseCurrencyUnderflow (400) for a base value beyond ±922337203685477.</exception>
    public static async Task ApplyAsync(DataSession s, Caller caller, EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs,
        Entity row, Guid id, bool isCreate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(attrs);
        ArgumentNullException.ThrowIfNull(row);
        if (!CurrencySchema.HasCurrencyColumns(m)) return;
        // A calculated money column (plan 2.12a) stores nothing: the query computes it and its _base from the stamped rate. A rollup
        // money column (plan 2.12b, ruling R11) is the calculation's: its value and _base are written only by it (R7: _base is the
        // aggregate of the related bases, the value _base × rate), so a write of the row never restamps them.
        var monies = CurrencySchema.MoneyAttributes(m).Where(a => !ComputedColumnRules.IsComputed(a) && !RollupColumns.IsRollup(a)).ToList();
        var currencyGiven = row.Contains(CurrencySchema.CurrencyId);
        if (!isCreate && !currencyGiven && !monies.Any(a => row.Contains(a.LogicalName))) return;

        // The values of the money columns: the row's, else (Update) the stored ones.
        var values = monies.ToDictionary(a => a.LogicalName, a => row.Contains(a.LogicalName) ? row.GetAttributeValue<Money>(a.LogicalName)?.Value : null);
        Guid? storedCurrency = null;
        if (!isCreate)
        {
            var stored = await StoredAsync(s, m, monies.Where(a => !row.Contains(a.LogicalName)).ToList(), id, ct);
            if (stored is null) return; // no such record: the Update reports it
            storedCurrency = stored.Value.Currency;
            foreach (var (name, value) in stored.Value.Money) values[name] = value;
        }

        // A null currency on Create is no currency given (the default applies); on Update it clears the currency (X6).
        var given = currencyGiven ? row.GetAttributeValue<EntityReference>(CurrencySchema.CurrencyId) : null;
        if (!isCreate && currencyGiven && given is null)
        {
            if (values.FirstOrDefault(v => v.Value is not null) is { Key: { } withValue })
                throw new XrmdFaultException(ErrorCodes.CurrencyCannotBeNullDueToNonNullMoneyFields,
                    $"The currency of this '{m.LogicalName}' record cannot be cleared: its money attribute '{withValue}' has a value.", 400);
            row[CurrencySchema.ExchangeRate] = null;
            foreach (var a in monies) SetBase(row, attrs, a, null);
            return;
        }

        CurrencyRow currency;
        if (given is not null)
        {
            currency = await CurrencyAsync(s, given.Id, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(CurrencySchema.Table, given.Id);
            // A record may keep an inactive currency; a new one must be active (X5, X6).
            if (!currency.Active && given.Id != storedCurrency)
                throw new XrmdFaultException(ErrorCodes.InvalidCurrency,
                    $"The currency {given.Id} is inactive and cannot be given to a '{m.LogicalName}' record.", 400);
        }
        else if (storedCurrency is { } kept && await CurrencyAsync(s, kept, ct) is { } keptCurrency)
        {
            currency = keptCurrency;
        }
        else
        {
            currency = await DefaultCurrencyAsync(s, caller, ct);
            row[CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, currency.Id);
        }

        // A rate that is missing or rounds to 0 on the record cannot convert anything: refused, never a division by zero.
        if (currency.Rate is not { } currencyRate || CurrencyMath.RecordRate(currencyRate) <= 0)
            throw new XrmdFaultException(ErrorCodes.InvalidExchangeRate,
                $"The currency {currency.Id} has no exchange rate a '{m.LogicalName}' record can store (at least {CurrencySchema.MinRate}).", 400);
        var rate = CurrencyMath.RecordRate(currencyRate);
        row[CurrencySchema.ExchangeRate] = rate;
        var pricing = monies.Any(a => a.PrecisionSource == 1) ? await PricingPrecisionAsync(s, ct) : CurrencySchema.DefaultPricingPrecision;
        foreach (var a in monies)
        {
            var value = values[a.LogicalName];
            if (value is { } amount && row.Contains(a.LogicalName))
            {
                value = CurrencyMath.Round(amount, CurrencyMath.EffectivePrecision(a, currency.Precision, pricing));
                row[a.LogicalName] = new Money(value.Value);
            }
            SetBase(row, attrs, a, value is { } v ? BaseOf(m, a, v, rate) : null);
        }
    }

    private static void SetBase(Entity row, IReadOnlyDictionary<string, AttributeMetadata> attrs, MoneyAttributeMetadata a, decimal? value)
    {
        var name = a.LogicalName + TypeMapping.BaseColumnSuffix;
        if (attrs.ContainsKey(name)) row[name] = value is { } v ? new Money(v) : null;
    }

    /// <summary>The base value of <paramref name="value"/>; refused beyond the base money range (X10), naming the attribute.</summary>
    private static decimal BaseOf(EntityMetadata m, MoneyAttributeMetadata a, decimal value, decimal rate)
    {
        var result = CurrencyMath.ToBase(value, rate);
        var name = a.LogicalName + TypeMapping.BaseColumnSuffix;
        if (result > CurrencyMath.BaseLimit)
            throw new XrmdFaultException(ErrorCodes.BaseCurrencyOverflow,
                $"The value of '{m.LogicalName}.{name}' ({value} / {rate}) is above the largest base currency value {CurrencyMath.BaseLimit}.", 400);
        if (result < -CurrencyMath.BaseLimit)
            throw new XrmdFaultException(ErrorCodes.BaseCurrencyUnderflow,
                $"The value of '{m.LogicalName}.{name}' ({value} / {rate}) is below the smallest base currency value {-CurrencyMath.BaseLimit}.", 400);
        return result;
    }

    private readonly record struct CurrencyRow(Guid Id, decimal? Rate, int Precision, bool Active);

    /// <summary>The currency <paramref name="currencyId"/>, read under a shared lock held to the end of the transaction; null when missing.</summary>
    private static async Task<CurrencyRow?> CurrencyAsync(DataSession s, Guid currencyId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {CurrencySchema.ExchangeRate}, {CurrencySchema.Precision}, statecode FROM {s.DataTable(CurrencySchema.Table)}{s.Dialect.LockHint(RowLock.Shared)} " +
            $"WHERE {CurrencySchema.CurrencyId} = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
        cmd.AddParameter(IdParameter, DbType.Guid, currencyId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd, currencyId, 0) : null;
    }

    /// <summary>The currency at <paramref name="first"/>: rate, precision, statecode.</summary>
    private static CurrencyRow Read(DbDataReader rd, Guid currencyId, int first) =>
        new(currencyId, rd.IsDBNull(first) ? null : rd.GetDecimal(first),
            rd.IsDBNull(first + 1) ? CurrencyMath.DefaultPrecision : rd.GetInt32(first + 1),
            rd.IsDBNull(first + 2) || rd.GetInt32(first + 2) == 0);

    /// <summary>
    /// The currency of a record that names none: the effective caller's (<see cref="Caller.UserId"/>, the impersonated user)
    /// <c>usersettings.transactioncurrencyid</c> when set and active, else the organization's base currency.
    /// </summary>
    private static async Task<CurrencyRow> DefaultCurrencyAsync(DataSession s, Caller caller, CancellationToken ct)
    {
        // One read: the organization, the caller's settings (none for an internal caller) and the chosen currency, which is held
        // under a shared lock to the end of the transaction as any currency a record is given.
        var personal = caller.UserId == Guid.Empty ? "NULL"
            : $"(SELECT us.{CurrencySchema.CurrencyId} FROM {s.DataTable(CurrencySchema.UserSettings)} us " +
              $"JOIN {s.DataTable(CurrencySchema.Table)} pc ON pc.{CurrencySchema.CurrencyId} = us.{CurrencySchema.CurrencyId} " +
              $"WHERE us.systemuserid = {IdParameter} AND pc.statecode = 0)";
        await using var cmd = s.Command(s.Dialect.Limit(
            $"SELECT c.{CurrencySchema.CurrencyId}, c.{CurrencySchema.ExchangeRate}, c.{CurrencySchema.Precision}, c.statecode " +
            $"FROM {s.DataTable("organization")} o " +
            $"JOIN {s.DataTable(CurrencySchema.Table)} c{s.Dialect.LockHint(RowLock.Shared)} " +
            $"ON c.{CurrencySchema.CurrencyId} = COALESCE({personal}, o.{CurrencySchema.BaseCurrencyId})", "1") + s.Dialect.LockClause(RowLock.Shared, "c"));
        if (caller.UserId != Guid.Empty) cmd.AddParameter(IdParameter, DbType.Guid, caller.UserId);
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await rd.ReadAsync(ct)) throw new InvalidOperationException("The organization has no base currency: the seed did not run.");
            var chosen = Read(rd, rd.GetGuid(0), 1);
            // The sub-select saw the personal currency active, but a deactivation may have committed before the lock was granted
            // (final review m3): the row read under the lock decides, and an inactive personal currency gives way to the base one.
            if (chosen.Active) return chosen;
        }
        return await BaseCurrencyAsync(s, ct);
    }

    /// <summary>The organization's base currency, read under a shared lock held to the end of the transaction.</summary>
    private static async Task<CurrencyRow> BaseCurrencyAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit(
            $"SELECT c.{CurrencySchema.CurrencyId}, c.{CurrencySchema.ExchangeRate}, c.{CurrencySchema.Precision}, c.statecode " +
            $"FROM {s.DataTable("organization")} o " +
            $"JOIN {s.DataTable(CurrencySchema.Table)} c{s.Dialect.LockHint(RowLock.Shared)} ON c.{CurrencySchema.CurrencyId} = o.{CurrencySchema.BaseCurrencyId}", "1") +
            s.Dialect.LockClause(RowLock.Shared, "c"));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (await rd.ReadAsync(ct)) return Read(rd, rd.GetGuid(0), 1);
        throw new InvalidOperationException("The organization has no base currency: the seed did not run.");
    }

    private static async Task<int> PricingPrecisionAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT {CurrencySchema.PricingPrecision} FROM {s.DataTable("organization")}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is int precision ? precision : CurrencySchema.DefaultPricingPrecision;
    }

    /// <summary>
    /// The stored currency of the record and the stored values of <paramref name="monies"/>, read under an update lock (the
    /// Update writes the row next); null when the record does not exist.
    /// </summary>
    private static async Task<(Guid? Currency, Dictionary<string, decimal?> Money)?> StoredAsync(DataSession s, EntityMetadata m,
        IReadOnlyList<MoneyAttributeMetadata> monies, Guid id, CancellationToken ct)
    {
        var columns = string.Concat(monies.Select(a => $", {s.Quote(a.LogicalName)}"));
        await using var cmd = s.Command(
            $"SELECT {s.Quote(CurrencySchema.CurrencyId)}{columns} FROM {s.DataTable(m.LogicalName)}{s.Dialect.LockHint(RowLock.Update)} " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}{s.Dialect.LockClause(RowLock.Update)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var money = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        for (var i = 0; i < monies.Count; i++) money[monies[i].LogicalName] = rd.IsDBNull(i + 1) ? null : rd.GetDecimal(i + 1);
        return (rd.IsDBNull(0) ? null : rd.GetGuid(0), money);
    }
}
