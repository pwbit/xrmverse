using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// The qualified names of the data tables in the dialect of a session or of a dialect (plan 2.7a, fix round 1): the stores write
/// <c>{s.DataTable(name)}</c> where they wrote <c>[data].[name]</c>.
/// </summary>
public static class DataSessionSql
{
    /// <summary>The table <paramref name="table"/> of the <c>data</c> schema, quoted (<see cref="DdlGenerator.Table"/>).</summary>
    public static string DataTable(this DataSession s, string table)
    {
        ArgumentNullException.ThrowIfNull(s);
        return DdlGenerator.Table(table, s.Dialect);
    }

    /// <summary>The table <paramref name="table"/> of the <c>data</c> schema, quoted (<see cref="DdlGenerator.Table"/>).</summary>
    public static string DataTable(this ISqlDialect d, string table) => DdlGenerator.Table(table, d);

    /// <summary>
    /// Takes the key lock (<see cref="ISqlDialect.LockKeyAsync"/>) of the key value <paramref name="key"/> in the table
    /// <paramref name="table"/>, before a read that must serialize on rows it may not find (a count, an existence check, a check
    /// then insert): no statement on SQL Server, whose range lock does it; a command of its own on PostgreSQL (plan 2.7a, Task 3
    /// fix round 1).
    /// </summary>
    public static Task KeyLockAsync(this DataSession s, string table, object? key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(table);
        if (!table.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.'))
            throw new ArgumentException($"'{table}' is not a table name.", nameof(table));
        return s.Dialect.LockKeyAsync(s, table, key, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/>, whose failure the caller recovers from in the same transaction (a lost race on a unique
    /// index), so that the transaction outlives the failure (plan 2.7a, Task 6): under a savepoint when a failed statement aborts
    /// the transaction (<see cref="ISqlDialect.FailedStatementAbortsTransaction"/>), rolled back to it on a failure; as it is
    /// otherwise, and outside a transaction.
    /// </summary>
    /// <exception cref="AggregateException">The work failed and the rollback to the savepoint failed too: the work's failure, then
    /// the rollback's.</exception>
    public static async Task<T> RecoverableAsync<T>(this DataSession s, Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(work);
        if (!s.Dialect.FailedStatementAbortsTransaction || s.Transaction is not { } tx) return await work();
        var savepoint = "xrmd_" + Guid.NewGuid().ToString("N");
        await tx.SaveAsync(savepoint, ct);
        T result;
        try
        {
            result = await work();
        }
        catch (Exception failure)
        {
            try
            {
                // CancellationToken.None: the transaction must be usable again whatever happened to the request.
                await tx.RollbackAsync(savepoint, CancellationToken.None);
            }
            catch (Exception rollback)
            {
                // Final review m3: the work's failure is kept (first), with the rollback's. Not the work's failure alone: the
                // transaction did not outlive it, so a caller must not recover from it as from a lost race.
                throw new AggregateException(failure, rollback);
            }
            throw;
        }
        await tx.ReleaseAsync(savepoint, ct);
        return result;
    }

    /// <summary>
    /// The root business unit (the first by <c>createdon</c>) and the engine's current UTC time, read in one statement, or null
    /// while the seed has not created the unit yet (plan 2.7a, Task 5 fix round 1: the <c>DECLARE @bu</c> and <c>DECLARE @now</c>
    /// of the seed batches, which became commands of their own).
    /// </summary>
    public static async Task<(Guid BusinessUnitId, DateTime Now)?> RootBusinessUnitAsync(this DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(s.Dialect.Limit(
            $"SELECT businessunitid, {s.Dialect.UtcNow} FROM data.businessunit WHERE parentbusinessunitid IS NULL ORDER BY createdon", "1"));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.GetGuid(0), DateTime.SpecifyKind(rd.GetDateTime(1), DateTimeKind.Utc));
    }
}
