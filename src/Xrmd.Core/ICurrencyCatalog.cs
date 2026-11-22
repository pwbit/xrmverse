using Xrmd.Abstractions;

namespace Xrmd.Core;

/// <summary>
/// The cached currencies of the organization (plan 2.10): what the read path formats money with. <see cref="CoreOperations"/> calls
/// <see cref="Invalidate"/> after the commit (and after the rollback) of any write to <c>transactioncurrency</c> or
/// <c>organization</c>, so the next read sees the committed symbols, precisions and settings.
/// </summary>
public interface ICurrencyCatalog
{
    /// <summary>The currencies and the organization's settings: the cached snapshot, loaded on first use and after <see cref="Invalidate"/>.</summary>
    Task<CurrencySnapshot> GetAsync(CancellationToken ct);

    /// <summary>
    /// As <see cref="GetAsync(CancellationToken)"/>, loading (when nothing is cached) on <paramref name="session"/>: inside its
    /// transaction, so that a read never waits on the locks of the transaction it runs in. A snapshot loaded inside a transaction
    /// may hold uncommitted values: it serves this read only and is not cached.
    /// </summary>
    Task<CurrencySnapshot> GetAsync(DataSession session, CancellationToken ct);

    /// <summary>Forgets the cached currencies; the next read loads them again.</summary>
    void Invalidate();
}
