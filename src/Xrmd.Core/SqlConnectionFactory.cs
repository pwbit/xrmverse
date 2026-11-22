using System.Data.Common;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Abstractions.Storage.SqlServer;

namespace Xrmd.Core;

/// <summary>The SQL Server <see cref="IDbConnectionFactory"/>: opens a <see cref="SqlConnection"/> on the connection string.</summary>
public sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public ISqlDialect Dialect => SqlServerDialect.Instance;

    public Type ConnectionType => typeof(SqlConnection);

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
