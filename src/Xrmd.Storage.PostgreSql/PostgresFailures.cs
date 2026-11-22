using Npgsql;

namespace Xrmd.Storage.PostgreSql;

/// <summary>What kind of failure a PostgreSQL exception is, for callers that may not name Npgsql (ruling P3), such as the CLI.</summary>
public enum PostgresFailureKind
{
    /// <summary>The server refused the login (SQLSTATE 28P01 or 28000).</summary>
    Authentication,

    /// <summary>The server answered with any other error.</summary>
    Refused,

    /// <summary>The server could not be reached, or the connection failed without an answer from it.</summary>
    Unreachable,
}

/// <summary>Classifies Npgsql exceptions and validates connection strings for code outside this project (plan 2.7b, Task 9).</summary>
public static class PostgresFailures
{
    /// <summary>
    /// The kind and message of <paramref name="exception"/> when it is an Npgsql one (the server's own message text for an error
    /// it returned), else null.
    /// </summary>
    public static (PostgresFailureKind Kind, string Message)? Classify(Exception exception) => exception switch
    {
        PostgresException { SqlState: "28P01" or "28000" } pg => (PostgresFailureKind.Authentication, pg.MessageText),
        PostgresException pg => (PostgresFailureKind.Refused, pg.MessageText),
        NpgsqlException npgsql => (PostgresFailureKind.Unreachable, npgsql.Message),
        _ => null,
    };

    /// <summary>The Npgsql form of <paramref name="connectionString"/>.</summary>
    /// <exception cref="ArgumentException">It is not a valid Npgsql connection string.</exception>
    public static string Normalize(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).ConnectionString;
}
