using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Storage.PostgreSql;

/// <summary>The PostgreSQL storage provider's registrations (plan 2.7a, Task 3; ruling P3).</summary>
public static class PostgresStorage
{
    /// <summary>
    /// Registers <see cref="PostgresDialect"/> for <see cref="NpgsqlConnection"/>s in <see cref="SqlDialects"/>, so that
    /// <see cref="DataSession.Dialect"/>, <see cref="SqlDialects.Classify"/> and the parameter helpers know the engine. Idempotent;
    /// <see cref="NpgsqlConnectionFactory"/> and <see cref="AddPostgresStorage(IServiceCollection, string)"/> call it.
    /// </summary>
    public static void Register() => SqlDialects.Register(PostgresDialect.ConnectionType, PostgresDialect.Instance);

    /// <summary>
    /// Registers the organization's <see cref="IDbConnectionFactory"/> (an <see cref="NpgsqlConnectionFactory"/> on
    /// <paramref name="connectionString"/>) and its <see cref="ISqlDialect"/>.
    /// </summary>
    public static IServiceCollection AddPostgresStorage(this IServiceCollection s, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return s.AddPostgresStorage(_ => connectionString);
    }

    /// <summary>
    /// <see cref="AddPostgresStorage(IServiceCollection, string)"/> with the connection string read when the factory is first
    /// resolved (the host starts without <c>ConnectionStrings:Org</c>, e.g. for <c>/healthz</c>).
    /// </summary>
    public static IServiceCollection AddPostgresStorage(this IServiceCollection s, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(connectionString);
        Register();
        s.AddSingleton<IDbConnectionFactory>(sp => new NpgsqlConnectionFactory(connectionString(sp)));
        // One source for the dialect (plan 2.7a, fix round 1, m1): the one of the factory's connections.
        s.AddSingleton(sp => SqlDialects.Of(sp.GetRequiredService<IDbConnectionFactory>(), StorageProvider.PostgreSql));
        return s;
    }
}
