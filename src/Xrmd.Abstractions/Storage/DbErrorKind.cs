namespace Xrmd.Abstractions.Storage;

/// <summary>
/// The engine-neutral class of a database failure (plan 2.7a, ruling P9): each dialect maps its own error numbers
/// (SQL Server) or SQLSTATE codes (PostgreSQL) to one of these, so callers decide on the class and never on a number.
/// </summary>
public enum DbErrorKind
{
    /// <summary>A unique index or primary key rejected the row (SQL Server 2601/2627, PostgreSQL 23505).</summary>
    UniqueViolation,
    /// <summary>A foreign key rejected the change (SQL Server 547, PostgreSQL 23503).</summary>
    ForeignKeyViolation,
    /// <summary>The engine chose the request as a deadlock victim or failed it on serialization (SQL Server 1205, PostgreSQL 40P01/40001).</summary>
    Deadlock,
    /// <summary>The command ran out of time or was cancelled by the engine (SQL Server -2, PostgreSQL 57014).</summary>
    Timeout,
    /// <summary>A lock wait ran out of time (SQL Server 1222, PostgreSQL 55P03; plan 2.7a, Task 2 addition).</summary>
    LockTimeout,
    /// <summary>The connection to the engine broke or was refused (SQL Server 233, 64, 10053, 10054, 10060; PostgreSQL 08xxx).</summary>
    ConnectionLost,
    /// <summary>The engine refused the credentials (SQL Server 18456, PostgreSQL 28P01).</summary>
    LoginFailed,
    /// <summary>A table or other object the command names does not exist (SQL Server 208, PostgreSQL 42P01).</summary>
    UndefinedObject,
    /// <summary>
    /// The database cannot be opened or the service is busy for now (SQL Server 4060, 40197, 40501, 40613, 49918–49920;
    /// PostgreSQL 57P03, 53300; plan 2.7a, Task 2 addition).
    /// </summary>
    Unavailable,
    /// <summary>A value did not fit its numeric type (SQL Server 8115, PostgreSQL 22003; plan 2.7a, Task 2 addition).</summary>
    NumericOverflow,
    /// <summary>Any other failure.</summary>
    Other,
}
