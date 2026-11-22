using System.Data.Common;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;

namespace Xrmd.Core.Rollups;

/// <summary>
/// The failures after which a rollup calculation job tries again later instead of ending (final review I2): the database errors that
/// trying again can cure (a deadlock victim, a command or lock timeout, a database or connection that is not available for now). A
/// definition the calculator refuses, any other SQL error and a cancellation are not: they end the job as before.
/// </summary>
public static class RollupJobFailures
{
    /// <summary>True when <paramref name="ex"/> (or the exception it wraps) is a transient database failure and <paramref name="ct"/> is not cancelled.</summary>
    public static bool IsTransient(Exception ex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ct.IsCancellationRequested) return false;
        return Of(ex) || ex.InnerException is { } inner && Of(inner);
    }

    /// <summary>
    /// A deadlock victim, a command or lock timeout, a connection lost or refused, a database or service unavailable for now
    /// (ruling P9; SQL Server 1205, -2, 1222, 233/64/10053/10054/10060, 4060/40197/40501/40613/49918–49920).
    /// </summary>
    private static bool Of(Exception ex) =>
        ex is DbException sql
        && SqlDialects.Classify(sql) is DbErrorKind.Deadlock or DbErrorKind.Timeout or DbErrorKind.LockTimeout
            or DbErrorKind.ConnectionLost or DbErrorKind.Unavailable;
}
