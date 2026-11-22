using System.Collections.Concurrent;
using System.Data.Common;
using Xrmd.Abstractions.Storage.SqlServer;

namespace Xrmd.Abstractions.Storage;

/// <summary>
/// The dialect of each provider's connections (plan 2.7a, ruling P3): code that holds a connection or a <see cref="DataSession"/>
/// but no injected <see cref="ISqlDialect"/> (static helpers, stores built per call) finds the dialect of the engine it talks to
/// here, as <see cref="DataSession.Dialect"/> does. SQL Server's is registered from the start; another provider registers its own
/// with its connection type when its services are added.
/// </summary>
public static class SqlDialects
{
    private static readonly ConcurrentDictionary<Type, ISqlDialect> ByConnectionType = new()
    {
        [SqlServerDialect.ConnectionType] = SqlServerDialect.Instance,
    };

    /// <summary>Registers <paramref name="dialect"/> for the connections of type <paramref name="connectionType"/> (or derived).</summary>
    public static void Register(Type connectionType, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(connectionType);
        ArgumentNullException.ThrowIfNull(dialect);
        if (!typeof(DbConnection).IsAssignableFrom(connectionType))
            throw new ArgumentException($"{connectionType} is not a DbConnection.", nameof(connectionType));
        ByConnectionType[connectionType] = dialect;
    }

    /// <summary>The dialect of the engine <paramref name="connection"/> talks to.</summary>
    /// <exception cref="NotSupportedException">No dialect is registered for the connection's type.</exception>
    public static ISqlDialect For(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return For(connection.GetType());
    }

    /// <summary>The dialect of the engine the connections of type <paramref name="connectionType"/> talk to (plan 2.7a, Task 3).</summary>
    /// <exception cref="NotSupportedException">No dialect is registered for <paramref name="connectionType"/>.</exception>
    public static ISqlDialect For(Type connectionType) =>
        TryFor(connectionType)
        ?? throw new NotSupportedException($"No SQL dialect is registered for connections of type {connectionType.FullName}.");

    /// <summary><see cref="For(DbConnection)"/>, or null when no dialect is registered for the connection's type (plan 2.7a, Task 3).</summary>
    public static ISqlDialect? TryFor(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return TryFor(connection.GetType());
    }

    /// <summary>The checked parts of <see cref="ISqlDialect.Concat"/>: at least two, none empty (plan 2.7b, Task 10).</summary>
    /// <exception cref="ArgumentException">Fewer than two parts, or an empty one.</exception>
    public static string[] ConcatParts(string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length < 2) throw new ArgumentException("Concat takes at least two parts.", nameof(parts));
        if (parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A part of Concat is empty.", nameof(parts));
        return parts;
    }

    private static ISqlDialect? TryFor(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (ByConnectionType.TryGetValue(type, out var dialect)) return dialect;
        foreach (var (registered, candidate) in ByConnectionType)
            if (registered.IsAssignableFrom(type)) return ByConnectionType.GetOrAdd(type, candidate);
        return null;
    }

    /// <summary>
    /// The class of <paramref name="ex"/> according to the dialect of its engine (ruling P9): the first registered dialect that
    /// recognizes it; <see cref="DbErrorKind.Other"/> when none does. For code that catches a failure without its connection.
    /// </summary>
    public static DbErrorKind Classify(DbException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        foreach (var dialect in ByConnectionType.Values.Distinct())
        {
            var kind = dialect.Classify(ex);
            if (kind != DbErrorKind.Other) return kind;
        }
        return DbErrorKind.Other;
    }

    /// <summary>True when <paramref name="ex"/> is the primary key violation of a registered dialect (<see cref="ISqlDialect.IsPrimaryKeyViolation"/>).</summary>
    public static bool IsPrimaryKeyViolation(DbException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ByConnectionType.Values.Distinct().Any(d => d.IsPrimaryKeyViolation(ex));
    }

    /// <summary>
    /// The dialect of <paramref name="provider"/>, for the composition of a server's services (<c>Storage:Provider</c>).
    /// </summary>
    /// <exception cref="NotSupportedException">No dialect of <paramref name="provider"/> is registered.</exception>
    public static ISqlDialect For(StorageProvider provider) =>
        ByConnectionType.Values.FirstOrDefault(d => d.Provider == provider)
        ?? throw new NotSupportedException($"The storage provider {provider} is not available in this build.");

    /// <summary>
    /// The dialect of the connections <paramref name="factory"/> opens, checked against the configured provider (plan 2.7a, fix
    /// round 1): the server registers this one as its <see cref="ISqlDialect"/>, so that the compiler and the stores (which read
    /// <see cref="DataSession.Dialect"/> from the connection) cannot write for two engines.
    /// </summary>
    /// <exception cref="InvalidOperationException">The factory opens connections of another engine than <paramref name="configured"/>,
    /// or its dialect is not the one registered for the type of its connections (<see cref="IDbConnectionFactory.ConnectionType"/>).</exception>
    public static ISqlDialect Of(IDbConnectionFactory factory, StorageProvider configured)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var dialect = factory.Dialect;
        if (dialect.Provider != configured)
            throw new InvalidOperationException(
                $"{StorageSettings.ProviderKey} is {configured}, but the organization connections are {dialect.Provider} connections.");
        // Carry-over N2 (plan 2.7a, Task 3): the dialect the stores will use is the one registered for the factory's connection
        // type (DataSession.Dialect looks it up by the connection), so the check is keyed on that type.
        if (!ReferenceEquals(TryFor(factory.ConnectionType), dialect))
            throw new InvalidOperationException(
                $"The {dialect.Provider} dialect of the connection factory is not the one registered for its connections ({factory.ConnectionType.FullName}).");
        return dialect;
    }
}
