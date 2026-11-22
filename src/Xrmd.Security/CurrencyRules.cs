using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Security;

/// <summary>
/// The request rules of multi-currency (plan 2.10, rulings X2, X3, X9, D2), called by <see cref="SystemTableRules.EnforceAsync"/>
/// before any exemption, so they hold for every caller (SYSTEM and System Administrator included):
/// <list type="bullet">
/// <item><c>transactioncurrency</c> Create and Update: name and symbol are required and not blank; <c>isocurrencycode</c> is unique
/// (<c>0x80048cf3</c>), a System currency's (<c>currencytype</c> 0) is an ISO 4217 code and a Custom one's (1) three letters A–Z
/// (<c>0x80048cf2</c>); <c>exchangerate</c> is given on Create and stays in 1E-10..1E11 (<c>0x80048cfd</c>); <c>currencyprecision</c>
/// is 0 to 4 (400).</item>
/// <item>The base currency (<c>organization.basecurrencyid</c>): its rate stays 1 (<c>0x80048cf5</c>), it stays active
/// (<c>0x80048cf4</c>, Update and SetState), it is never deleted (<c>0x80048cff</c>), its code and type stay as they are (400) and
/// its symbol has at most <see cref="BaseCurrencySeed.MaxSymbolLength"/> characters, the length of
/// <c>organization.basecurrencysymbol</c> that copies it (400: never cut).</item>
/// <item><c>transactioncurrency</c> Delete: a currency in a user's settings is in use (400); records holding it are refused by their
/// relationship's Delete Restrict in the core operation.</item>
/// <item><c>organization</c> Update: <c>basecurrencyid</c> keeps its stored value (400); <c>pricingdecimalprecision</c> is 0 to 4
/// (400).</item>
/// <item><c>usersettings</c> Create and Update: a currency given that the row does not already hold is active (<c>0x80048cfc</c>).</item>
/// </list>
/// Associate and Disassociate over one of these lookups are the Update of the lookup and follow the same rules. A missing record is
/// left to the core operation's 404.
/// <para>
/// The rules run twice (final review I1). <see cref="EnforceAsync"/>, from the <c>SecurityGuard</c>, is the early answer: it sees
/// the request as the client sent it, before the plug-ins and, for a top-level request, before the transaction begins, so its
/// reads hold no lock past their own statement. The core operation then calls <see cref="EnforceRowAsync"/> on the final row (after
/// the pre-validation and pre-operation plug-ins, from the values it writes) and <see cref="EnforceDeleteAsync"/>, inside the
/// request's transaction: there the rows these rules read are locked (<c>UPDLOCK, HOLDLOCK</c>) to the end of the transaction, so a
/// plug-in cannot change the base currency and a concurrent request cannot slip a deactivation or a user's currency between the
/// check and the write. The unique index on <c>isocurrencycode</c> settles a duplicate code a concurrent Create commits first.
/// </para>
/// </summary>
public static class CurrencyRules
{
    private const string Organization = "organization";
    private const string StateCode = "statecode";
    private const string StatusCode = "statuscode";
    private const string Name = "currencyname";
    private const string IdParameter = "@_id";
    private const int SystemType = 0, CustomType = 1;
    private const int Inactive = 1;

    /// <summary>The rules of <paramref name="request"/> (Create, Update, Delete, Associate, Disassociate); other messages pass.</summary>
    /// <remarks>
    /// The parameters mirror <see cref="SystemTableRules.EnforceAsync"/> (pre-flight ruling C9); <paramref name="caller"/>,
    /// <paramref name="systemUserId"/> and <paramref name="cache"/> are not read: these rules hold for every caller.
    /// </remarks>
    /// <exception cref="XrmdFaultException">400 with the codes listed on the class.</exception>
    public static async Task EnforceAsync(OrganizationRequest request, Caller caller, Guid systemUserId, SecurityCache cache,
        IMetadataStore store, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        _ = systemUserId;
        var parameters = request.Parameters;
        parameters.TryGetValue("Target", out var target);
        switch (request.RequestName?.ToLowerInvariant())
        {
            case "create" when target is Entity created:
                await WriteAsync(created, isCreate: true, store, session, ct);
                break;
            case "update" when target is Entity updated:
                await WriteAsync(updated, isCreate: false, store, session, ct);
                break;
            case "delete" when target is EntityReference { LogicalName: CurrencySchema.Table } deleted:
                await DeleteAsync(deleted.Id, session, ct);
                break;
            case "associate" or "disassociate" when target is EntityReference associated
                && parameters.TryGetValue("Relationship", out var r) && r is Relationship relationship
                && parameters.TryGetValue("RelatedEntities", out var rel) && rel is EntityReferenceCollection related
                && store.GetRelationship(relationship.SchemaName ?? "") is OneToManyRelationshipMetadata { ReferencedEntity: CurrencySchema.Table } lookup:
                foreach (var update in SystemTableRules.LookupUpdates(lookup, associated, related,
                             request.RequestName.Equals("associate", StringComparison.OrdinalIgnoreCase)))
                    await WriteAsync(update, isCreate: false, store, session, ct);
                break;
        }
    }

    /// <summary>
    /// The value rules of a Create or Update on the final <paramref name="row"/> the core operation writes (the values left after
    /// the plug-ins and the read-only columns are dropped), with the record id <paramref name="id"/>; called inside the request's
    /// transaction, so the reads are locked to its end. Other tables pass.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 with the codes listed on the class.</exception>
    public static Task EnforceRowAsync(Entity row, Guid id, bool isCreate, IMetadataStore store, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        if (row.LogicalName is not (CurrencySchema.Table or Organization or CurrencySchema.UserSettings)) return Task.CompletedTask;
        var target = new Entity(row.LogicalName, id);
        foreach (var (name, value) in row.Attributes) target[name] = value;
        return WriteAsync(target, isCreate, store, session, ct);
    }

    /// <summary>
    /// The rules of the Delete of the currency <paramref name="id"/> in the core operation, inside the request's transaction: the
    /// base currency is never deleted, a currency in a user's settings is in use (both read under <c>UPDLOCK, HOLDLOCK</c>).
    /// </summary>
    /// <exception cref="XrmdFaultException">0x80048cff or 400 InvalidArgument.</exception>
    public static Task EnforceDeleteAsync(Guid id, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        return DeleteAsync(id, session, ct);
    }

    private static Task WriteAsync(Entity target, bool isCreate, IMetadataStore store, DataSession session, CancellationToken ct) => target.LogicalName switch
    {
        CurrencySchema.Table => CurrencyAsync(target, isCreate, store, session, ct),
        Organization when !isCreate => OrganizationAsync(target, session, ct),
        CurrencySchema.UserSettings when store.GetEntity(CurrencySchema.UserSettings) is { } settings =>
            UserSettingsAsync(target, SystemAttributes.PrimaryIdName(settings), isCreate, session, ct),
        _ => Task.CompletedTask,
    };

    // ---- transactioncurrency ----------------------------------------------------------------------------------------------

    /// <summary>The stored columns of a currency these rules read.</summary>
    private sealed record StoredCurrency(string? Code, int? Type, bool IsBase);

    private static async Task CurrencyAsync(Entity target, bool isCreate, IMetadataStore store, DataSession session, CancellationToken ct)
    {
        var id = SystemTableRules.RecordId(target);
        StoredCurrency? stored = null;
        if (!isCreate)
        {
            if (id == Guid.Empty) return; // the core operation answers a missing id
            stored = await StoredCurrencyAsync(id, session, ct);
            if (stored is null) return; // the core operation answers the 404
        }

        if (stored is { IsBase: true }) DemandBaseUnchanged(target, id, stored, store);

        foreach (var column in (string[])[Name, CurrencySchema.Symbol])
            if ((isCreate || target.Contains(column)) && (Value(target, column) as string)?.Trim() is not { Length: > 0 })
                throw XrmdFaultException.InvalidArgument($"A currency needs a {column}: it cannot be missing or blank.");

        if (isCreate || target.Contains(CurrencySchema.ExchangeRate))
            DemandRate(Value(target, CurrencySchema.ExchangeRate));

        if (isCreate || target.Contains(CurrencySchema.Precision))
        {
            if (Value(target, CurrencySchema.Precision) is not int precision
                || precision is < CurrencySchema.MinPrecision or > CurrencySchema.MaxPrecision)
                throw XrmdFaultException.InvalidArgument(
                    $"The {CurrencySchema.Precision} of a currency must be a whole number from {CurrencySchema.MinPrecision} to " +
                    $"{CurrencySchema.MaxPrecision} (xrmverse stores money with 4 decimals).");
        }

        if (isCreate || target.Contains(CurrencySchema.IsoCode) || target.Contains(CurrencySchema.CurrencyType))
        {
            var code = target.Contains(CurrencySchema.IsoCode) || isCreate ? Value(target, CurrencySchema.IsoCode) as string : stored!.Code;
            var type = target.Contains(CurrencySchema.CurrencyType) || isCreate
                ? OptionValue(Value(target, CurrencySchema.CurrencyType))
                : stored!.Type;
            DemandValidCode(code, type);
            if (target.Contains(CurrencySchema.IsoCode) || isCreate) await DemandUniqueCodeAsync(code!, isCreate ? null : id, session, ct);
        }
    }

    /// <summary>X2: the base currency's rate, state, code and type do not change, and its symbol fits <c>basecurrencysymbol</c>.</summary>
    /// <remarks>
    /// The state is the one the write ends in: <c>statecode</c>, else the state that owns the given <c>statuscode</c> (the core
    /// derives the one from the other, so a lone <c>statuscode</c> 2 would deactivate the row; review I1).
    /// </remarks>
    private static void DemandBaseUnchanged(Entity target, Guid id, StoredCurrency stored, IMetadataStore store)
    {
        var state = target.Contains(StateCode) ? OptionValue(Value(target, StateCode))
            : target.Contains(StatusCode) ? StateOfStatus(store, OptionValue(Value(target, StatusCode))) : null;
        if (state == Inactive)
            throw new XrmdFaultException(ErrorCodes.BaseCurrencyCannotBeDeactivated,
                $"The base currency {id} cannot be deactivated.", 400);
        if (target.Contains(CurrencySchema.ExchangeRate) && AsNumber(Value(target, CurrencySchema.ExchangeRate)) != 1m)
            throw new XrmdFaultException(ErrorCodes.ExchangeRateOfBaseCurrencyNotUpdatable,
                $"The exchange rate of the base currency {id} is always 1 and cannot be changed.", 400);
        if (target.Contains(CurrencySchema.IsoCode)
            && !string.Equals(Value(target, CurrencySchema.IsoCode) as string, stored.Code, StringComparison.Ordinal))
            throw XrmdFaultException.InvalidArgument(
                $"The {CurrencySchema.IsoCode} of the base currency ({stored.Code}) cannot be changed: the base currency is fixed once set.");
        if (target.Contains(CurrencySchema.CurrencyType) && OptionValue(Value(target, CurrencySchema.CurrencyType)) != stored.Type)
            throw XrmdFaultException.InvalidArgument(
                $"The {CurrencySchema.CurrencyType} of the base currency cannot be changed: the base currency is fixed once set.");
        if (Value(target, CurrencySchema.Symbol) is string symbol && symbol.Length > BaseCurrencySeed.MaxSymbolLength)
            throw XrmdFaultException.InvalidArgument(
                $"The {CurrencySchema.Symbol} of the base currency can have at most {BaseCurrencySeed.MaxSymbolLength} characters: " +
                $"organization.{CurrencySchema.BaseSymbol} copies it. '{symbol}' has {symbol.Length}.");
    }

    /// <summary>
    /// X3, before the metadata range check of the write (which allows the currency column's wider 1E-12): a rate given as a number
    /// stays in <see cref="CurrencySchema.MinRate"/>..<see cref="CurrencySchema.MaxRate"/>, so an out-of-range rate is answered
    /// with InvalidExchangeRate. A missing or null rate is left to <see cref="EnforceAsync"/> (the base currency's own code first).
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidExchangeRate (400).</exception>
    public static void DemandRateInRange(Entity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // A value that is not a number is left to the write's type check (an Edm.Decimal is expected).
        if (target.LogicalName == CurrencySchema.Table && Value(target, CurrencySchema.ExchangeRate) is { } value
            && AsNumber(value) is { } rate)
            DemandRate(rate);
    }

    /// <summary>X3: a rate is given and stays in <see cref="CurrencySchema.MinRate"/>..<see cref="CurrencySchema.MaxRate"/>.</summary>
    private static void DemandRate(object? value)
    {
        if (value is not null && AsNumber(value) is null)
            throw XrmdFaultException.InvalidArgument(
                $"The {CurrencySchema.ExchangeRate} of a currency must be a number (Edm.Decimal); got a {value.GetType().Name}.");
        if (AsNumber(value) is not { } rate || rate is < CurrencySchema.MinRate or > CurrencySchema.MaxRate)
            throw new XrmdFaultException(ErrorCodes.InvalidExchangeRate,
                $"The {CurrencySchema.ExchangeRate} of a currency is required and must be from {CurrencySchema.MinRate} to " +
                $"{CurrencySchema.MaxRate}; got {value?.ToString() ?? "none"}.", 400);
    }

    /// <summary>
    /// A rate given as any number, as a decimal (review m2): a plug-in's request may carry an int or a double; a double too large
    /// or not finite counts as out of range. Null for a value that is not a number.
    /// </summary>
    private static decimal? AsNumber(object? value) => value switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        double d when double.IsFinite(d) && Math.Abs(d) < 1e20 => (decimal)d,
        float f when float.IsFinite(f) && Math.Abs(f) < 1e20f => (decimal)f,
        double or float => decimal.MaxValue,
        _ => null,
    };

    /// <summary>X3: a System currency has an ISO 4217 code, a Custom one three letters A–Z.</summary>
    private static void DemandValidCode(string? code, int? type)
    {
        switch (type)
        {
            case SystemType when code is not null && IsoCurrencyCodes.Contains(code):
            case CustomType when code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z'):
                return;
            case SystemType:
                throw new XrmdFaultException(ErrorCodes.InvalidIsoCurrencyCode,
                    $"'{code}' is not an ISO 4217 currency code, which a System currency (currencytype 0) needs (for example USD or NOK, in capitals).", 400);
            case CustomType:
                throw new XrmdFaultException(ErrorCodes.InvalidIsoCurrencyCode,
                    $"'{code}' is not a valid code for a custom currency (currencytype 1): it needs exactly three capital letters A to Z.", 400);
            default:
                throw XrmdFaultException.InvalidArgument(
                    $"A currency needs a {CurrencySchema.CurrencyType}: 0 (System, an ISO 4217 code) or 1 (Custom).");
        }
    }

    /// <summary>X3: no other currency has <paramref name="code"/> (read under a range lock; the unique index settles a race).</summary>
    private static async Task DemandUniqueCodeAsync(string code, Guid? self, DataSession session, CancellationToken ct)
    {
        await session.KeyLockAsync(CurrencySchema.Table, code, ct);
        await using var cmd = session.Command(session.Dialect.Limit(
            $"SELECT {CurrencySchema.CurrencyId} FROM {session.DataTable(CurrencySchema.Table)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
            // Plan 2.7b, ruling Q1: compared as the unique index compares (TextKey; the column itself on SQL Server).
            $"WHERE {session.Dialect.TextKey(CurrencySchema.IsoCode)} = {session.Dialect.TextKey("@_code")} AND ({IdParameter} IS NULL OR {CurrencySchema.CurrencyId} <> {IdParameter})", "1")
            + session.Dialect.LockClause(RowLock.UpdateRange));
        cmd.AddParameter("@_code", DbType.String, code, 5);
        cmd.AddParameter(IdParameter, DbType.Guid, (object?)self ?? DBNull.Value);
        if (await cmd.ExecuteScalarAsync(ct) is Guid other) throw DuplicateCode(code, other);
    }

    /// <summary>The refusal of a currency code another currency holds (<c>0x80048cf3</c>).</summary>
    public static XrmdFaultException DuplicateCode(string code, Guid? other = null) =>
        new(ErrorCodes.DuplicateIsoCurrencyCode,
            $"A currency with the code '{code}' already exists{(other is { } id ? $" ({id})" : "")}: each currency code is used once.", 400);

    /// <summary>The code, type and base flag of the currency <paramref name="id"/>, update-locked; null when it does not exist.</summary>
    private static async Task<StoredCurrency?> StoredCurrencyAsync(Guid id, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT c.{CurrencySchema.IsoCode}, c.{CurrencySchema.CurrencyType}, " +
            $"CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable(Organization)} o WHERE o.{CurrencySchema.BaseCurrencyId} = c.{CurrencySchema.CurrencyId}) THEN 1 ELSE 0 END " +
            $"FROM {session.DataTable(CurrencySchema.Table)} c{session.Dialect.LockHint(RowLock.UpdateRange)} WHERE c.{CurrencySchema.CurrencyId} = {IdParameter}{session.Dialect.LockClause(RowLock.UpdateRange, "c")}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new StoredCurrency(rd.IsDBNull(0) ? null : rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetInt32(2) == 1);
    }

    /// <summary>X2: the base currency is never deleted; a currency in a user's settings is in use.</summary>
    private static async Task DeleteAsync(Guid id, DataSession session, CancellationToken ct)
    {
        if (await StoredCurrencyAsync(id, session, ct) is not { } stored) return;
        if (stored.IsBase)
            throw new XrmdFaultException(ErrorCodes.BaseCurrencyNotDeletable, $"The base currency {id} cannot be deleted.", 400);
        await session.KeyLockAsync(CurrencySchema.UserSettings, id, ct);
        await using var cmd = session.Command(
            $"SELECT COUNT(*) FROM {session.DataTable(CurrencySchema.UserSettings)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {CurrencySchema.CurrencyId} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) is var users and > 0) // bigint on PostgreSQL
            throw XrmdFaultException.InvalidArgument(
                $"The currency {id} cannot be deleted: it is the currency of {users} user setting(s) ({CurrencySchema.UserSettings}." +
                $"{CurrencySchema.CurrencyId}). Change those settings first, or deactivate the currency instead.");
    }

    // ---- organization -----------------------------------------------------------------------------------------------------

    private static async Task OrganizationAsync(Entity target, DataSession session, CancellationToken ct)
    {
        if (target.Contains(CurrencySchema.PricingPrecision)
            && (Value(target, CurrencySchema.PricingPrecision) is not int pricing
                || pricing is < CurrencySchema.MinPrecision or > CurrencySchema.MaxPrecision))
            throw XrmdFaultException.InvalidArgument(
                $"{CurrencySchema.PricingPrecision} must be a whole number from {CurrencySchema.MinPrecision} to {CurrencySchema.MaxPrecision} " +
                "(xrmverse stores money with 4 decimals).");
        if (!target.Contains(CurrencySchema.BaseCurrencyId)) return;
        var id = SystemTableRules.RecordId(target);
        var given = (Value(target, CurrencySchema.BaseCurrencyId) as EntityReference)?.Id;
        await using var cmd = session.Command(
            $"SELECT {CurrencySchema.BaseCurrencyId} FROM {session.DataTable(Organization)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {SystemAttributes.PrimaryIdName(Organization)} = {IdParameter}{session.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        if (given is null || await cmd.ExecuteScalarAsync(ct) is not Guid stored || stored != given)
            throw XrmdFaultException.InvalidArgument(
                $"organization.{CurrencySchema.BaseCurrencyId} cannot be changed: the base currency is set once, when the organization is created.");
    }

    // ---- usersettings -----------------------------------------------------------------------------------------------------

    private static async Task UserSettingsAsync(Entity target, string key, bool isCreate, DataSession session, CancellationToken ct)
    {
        if (Value(target, CurrencySchema.CurrencyId) is not EntityReference { Id: var currency } || currency == Guid.Empty) return;
        // usersettings is keyed by systemuserid: the target's Id, else that key.
        var id = target.Id != Guid.Empty ? target.Id : Value(target, key) as Guid? ?? Guid.Empty;
        await using var cmd = session.Command(
            $"SELECT c.statecode, CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable(CurrencySchema.UserSettings)} s " +
            $"WHERE s.{key} = @_settings AND s.{CurrencySchema.CurrencyId} = c.{CurrencySchema.CurrencyId}) " +
            $"THEN 1 ELSE 0 END FROM {session.DataTable(CurrencySchema.Table)} c{session.Dialect.LockHint(RowLock.Shared)} WHERE c.{CurrencySchema.CurrencyId} = {IdParameter}{session.Dialect.LockClause(RowLock.Shared, "c")}");
        cmd.AddParameter(IdParameter, DbType.Guid, currency);
        cmd.AddParameter("@_settings", DbType.Guid, isCreate ? Guid.Empty : id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return; // a missing currency is the core operation's 404
        var inactive = !rd.IsDBNull(0) && rd.GetInt32(0) == Inactive;
        if (inactive && rd.GetInt32(1) == 0)
            throw new XrmdFaultException(ErrorCodes.InvalidCurrency,
                $"The currency {currency} is inactive and cannot become a user's currency.", 400);
    }

    /// <summary>The state that owns <paramref name="status"/> in the status options of <c>transactioncurrency</c>; null when unknown.</summary>
    private static int? StateOfStatus(IMetadataStore store, int? status) =>
        status is null ? null
        : (store.GetEntity(CurrencySchema.Table)?.Attributes?.FirstOrDefault(a => a.LogicalName == StatusCode) as StatusAttributeMetadata)
            ?.OptionSet?.Options.OfType<StatusOptionMetadata>().FirstOrDefault(o => o.Value == status)?.State;

    // ---- values -------------------------------------------------------------------------------------------------------------

    private static object? Value(Entity target, string attribute) => target.Attributes.TryGetValue(attribute, out var value) ? value : null;

    /// <summary>The value of a choice given as an <see cref="OptionSetValue"/> or a number; null otherwise.</summary>
    private static int? OptionValue(object? value) => value switch
    {
        OptionSetValue option => option.Value,
        int number => number,
        _ => null,
    };
}
