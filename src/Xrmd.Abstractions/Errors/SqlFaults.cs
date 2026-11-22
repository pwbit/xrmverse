using System.Data.Common;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Core.Errors;

/// <summary>
/// Maps the two SQL failures a caller can fix by trying again, a deadlock victim and a command timeout, to the Dataverse
/// SQL error codes. The server never retries by itself: the request was rolled back and the caller decides.
/// </summary>
public static class SqlFaults
{
    public const string DeadlockMessage = "The database chose this request as a deadlock victim and rolled it back. Retry the request.";
    public const string TimeoutMessage = "The database did not answer in time and the request was rolled back. Retry the request.";

    /// <summary>
    /// 500 <see cref="ErrorCodes.SqlError"/> for a deadlock, 500 <see cref="ErrorCodes.SqlTimeout"/> for a timeout; null for
    /// anything else. Looks at <paramref name="ex"/> and at its <see cref="Exception.InnerException"/> (the pipeline may wrap).
    /// </summary>
    public static XrmdFaultException? Translate(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return Of(ex) ?? (ex.InnerException is { } inner ? Of(inner) : null);
    }

    private static XrmdFaultException? Of(Exception ex)
    {
        if (ex is not DbException db) return null;
        // Ruling P9: the class of the failure according to the dialect of its engine.
        return SqlDialects.Classify(db) switch
        {
            DbErrorKind.Deadlock => new XrmdFaultException(ErrorCodes.SqlError, DeadlockMessage, 500),
            DbErrorKind.Timeout => new XrmdFaultException(ErrorCodes.SqlTimeout, TimeoutMessage, 500),
            _ => null,
        };
    }
}
