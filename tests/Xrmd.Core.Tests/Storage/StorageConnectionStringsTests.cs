using FluentAssertions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Core.Tests.Storage;

/// <summary>
/// Plan 2.7a, Task 8 review (Minor 5): <see cref="StorageConnectionStrings.ProviderOf"/> names the engine of a connection string
/// by the keys only Npgsql accepts (<c>Host</c>, <c>Port</c>, <c>Username</c>, <c>SSL Mode</c>), so an Npgsql string written with
/// its <c>Server</c> synonym is not taken for SQL Server; keys SqlClient accepts too (<c>Server</c>, <c>User ID</c>, <c>UID</c>,
/// <c>Password</c>, <c>Database</c>) decide nothing.
/// </summary>
public class StorageConnectionStringsTests
{
    [Theory]
    [InlineData("Host=localhost;Username=postgres;Password=x")]
    [InlineData("host=localhost;username=postgres;password=x")]
    [InlineData("Server=pg.example.com;Port=5432;User ID=postgres;Password=x")]
    [InlineData("Server=pg.example.com;Username=postgres;Password=x")]
    [InlineData("Server=pg.example.com;User ID=postgres;Password=x;SSL Mode=Require")]
    [InlineData("Server=pg.example.com;UID=postgres;Password=x;SslMode=Require")]
    public void Npgsql_strings_are_postgresql(string connectionString) =>
        StorageConnectionStrings.ProviderOf(connectionString).Should().Be(StorageProvider.PostgreSql);

    [Theory]
    [InlineData("Server=sql.example.com;User ID=sa;Password=x;TrustServerCertificate=True")]
    [InlineData("Data Source=sql,1433;UID=sa;PWD=x;Initial Catalog=acme")]
    [InlineData("Server=tcp:sql.example.com,1433;Database=acme;Integrated Security=true")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_strings_are_sql_server(string? connectionString) =>
        StorageConnectionStrings.ProviderOf(connectionString).Should().Be(StorageProvider.SqlServer);
}
