using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// The currency messages of the core (plan 2.10, ruling X12) and the work the core does after a currency write: RetrieveExchangeRate
/// (<c>TransactionCurrencyId</c> → <c>ExchangeRate</c>), the copy of the base currency's symbol and precision to the organization
/// (ruling X2), and the invalidation of the <see cref="ICurrencyCatalog"/>.
/// </summary>
public static class CurrencyMessages
{
    /// <summary>The message that reads a currency's rate.</summary>
    public const string MessageName = "RetrieveExchangeRate";

    /// <summary>The request parameter: the currency's id.</summary>
    public const string CurrencyIdParameter = "TransactionCurrencyId";

    /// <summary>The response property: the currency's rate.</summary>
    public const string RateProperty = "ExchangeRate";

    private const string IdParameter = "@_id";

    /// <summary>
    /// The rate of the currency <paramref name="currencyId"/> as stored on <c>transactioncurrency</c>, read in
    /// <paramref name="s"/>. The caller's Read on the currency is demanded by the security guard before the message runs.
    /// </summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the currency does not exist; InvalidExchangeRate (400)
    /// when its row holds no rate.</exception>
    public static async Task<decimal> RetrieveExchangeRateAsync(DataSession s, Guid currencyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(
            $"SELECT {CurrencySchema.ExchangeRate} FROM {s.DataTable(CurrencySchema.Table)} " +
            $"WHERE {CurrencySchema.CurrencyId} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, currencyId);
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            null => throw XrmdFaultException.ObjectDoesNotExist(CurrencySchema.Table, currencyId),
            decimal rate => rate,
            _ => throw new XrmdFaultException(ErrorCodes.InvalidExchangeRate, $"The currency {currencyId} has no exchange rate.", 400),
        };
    }

    /// <summary>The RetrieveExchangeRate response of the request (its <c>TransactionCurrencyId</c>), read in <paramref name="s"/>.</summary>
    /// <exception cref="XrmdFaultException">400 without <c>TransactionCurrencyId</c>; the faults of <see cref="RetrieveExchangeRateAsync"/>.</exception>
    public static async Task<RetrieveExchangeRateResponse> ExecuteAsync(OrganizationRequest request, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Parameters.TryGetValue(CurrencyIdParameter, out var value) || value is not Guid id || id == Guid.Empty)
            throw XrmdFaultException.InvalidArgument($"Required parameter '{CurrencyIdParameter}' of request '{MessageName}' is missing.");
        return new RetrieveExchangeRateResponse
        {
            ResponseName = MessageName,
            Results = { [RateProperty] = await RetrieveExchangeRateAsync(s, id, ct) },
        };
    }

    /// <summary>
    /// After an Update of the currency <paramref name="currencyId"/> that wrote its symbol or precision: when it is the base
    /// currency, <c>organization.basecurrencysymbol</c> and <c>basecurrencyprecision</c> take the new values, in the same
    /// transaction (ruling X2). The request rules have already refused a base symbol longer than the organization's column.
    /// </summary>
    public static async Task CopyBaseCurrencyAsync(DataSession s, Guid currencyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // Plan 2.7a, Task 6: scalar subqueries instead of T-SQL's UPDATE … FROM alias form, the same text on both engines.
        await using var cmd = s.Command($"""
            UPDATE {s.DataTable("organization")}
            SET {CurrencySchema.BaseSymbol} = (SELECT c.{CurrencySchema.Symbol} FROM {s.DataTable(CurrencySchema.Table)} c WHERE c.{CurrencySchema.CurrencyId} = {IdParameter}),
                {CurrencySchema.BasePrecision} = (SELECT c.{CurrencySchema.Precision} FROM {s.DataTable(CurrencySchema.Table)} c WHERE c.{CurrencySchema.CurrencyId} = {IdParameter})
            WHERE {CurrencySchema.BaseCurrencyId} = {IdParameter}
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, currencyId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>True for the tables whose writes change what <see cref="ICurrencyCatalog"/> caches.</summary>
    public static bool InvalidatesCatalog(string? table) => table is CurrencySchema.Table or "organization";

    /// <summary>
    /// Invalidates <paramref name="catalog"/> once <paramref name="s"/>'s transaction ends (commit or rollback: a read in the
    /// transaction may have cached an uncommitted value), or at once when the session has no transaction.
    /// </summary>
    public static void InvalidateAfterTransaction(ICurrencyCatalog catalog, DataSession s)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(s);
        if (s.Transaction is null)
        {
            catalog.Invalidate();
            return;
        }
        s.OnCommitted(catalog.Invalidate);
        s.OnRolledBack(catalog.Invalidate);
    }
}
