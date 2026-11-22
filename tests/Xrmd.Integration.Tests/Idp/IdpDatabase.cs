using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>A new, migrated organization database on the fixture's server, of the run's provider (dropped by <see cref="DisposeAsync"/>).</summary>
internal sealed class IdpDatabase : IAsyncDisposable
{
    private IdpDatabase(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
        Factory = TestProvider.Factory(connectionString);
    }

    public string Name { get; }
    public string ConnectionString { get; }
    public IDbConnectionFactory Factory { get; }

    public static async Task<IdpDatabase> CreateAsync(string fixtureConnectionString)
    {
        var name = "xrmd_idp_" + Guid.NewGuid().ToString("N")[..12];
        await TestProvider.CreateDatabaseAsync(fixtureConnectionString, name);
        var db = new IdpDatabase(name, TestProvider.WithDatabase(fixtureConnectionString, name));
        await new SchemaMigrator(db.Factory).MigrateAsync();
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        if (TestProvider.ProviderOf(ConnectionString) == Xrmd.Abstractions.Storage.StorageProvider.SqlServer)
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await TestProvider.DropDatabaseAsync(ConnectionString, Name);
    }
}
