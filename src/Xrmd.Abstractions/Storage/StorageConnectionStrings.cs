using System.Data.Common;

namespace Xrmd.Abstractions.Storage;

/// <summary>Reads a storage connection string without the provider's own builder (plan 2.7a, ruling P3).</summary>
public static class StorageConnectionStrings
{
    /// <summary>
    /// The database <paramref name="connectionString"/> names (<c>Initial Catalog</c> on SQL Server, <c>Database</c> on both
    /// providers, keys compared ignoring case); empty when it names none.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is not a valid connection string.</exception>
    public static string DatabaseName(string? connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString ?? "" };
        foreach (var key in (string[])["Initial Catalog", "Database"])
            if (builder.TryGetValue(key, out var value) && value is string { Length: > 0 } name) return name;
        return "";
    }

    /// <summary>
    /// The engine <paramref name="connectionString"/> is for (plan 2.7a, Task 8), for a tool that is given a connection string and
    /// no <c>Storage:Provider</c> (the CLI's <c>--direct</c> commands): PostgreSQL when it names a key only Npgsql accepts
    /// (<see cref="NpgsqlOnlyKeys"/>: <c>Host</c>, <c>Port</c>, <c>Username</c>, <c>SSL Mode</c>; Task 8 review, Minor 5), SQL Server
    /// otherwise. Keys both accept (<c>Server</c>, <c>User ID</c>, <c>UID</c>, <c>Password</c>, <c>Database</c>) decide nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is not a valid connection string.</exception>
    public static StorageProvider ProviderOf(string? connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString ?? "" };
        return NpgsqlOnlyKeys.Any(builder.ContainsKey) ? StorageProvider.PostgreSql : StorageProvider.SqlServer;
    }

    /// <summary>Connection string keys Npgsql accepts and SqlClient refuses (compared ignoring case).</summary>
    private static readonly string[] NpgsqlOnlyKeys = ["Host", "Port", "Username", "SSL Mode", "SslMode"];
}
