using System.Data;
using FluentAssertions;
using Xrmd.Core;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public class SqlConnectionTests(XrmdFixture fx)
{
    [SqlServerFact("SqlConnectionFactory is the SQL Server connection factory")]
    public async Task OpenAsync_returns_open_connection()
    {
        var factory = new SqlConnectionFactory(fx.ConnectionString);
        await using var conn = await factory.OpenAsync();
        conn.State.Should().Be(ConnectionState.Open);
    }
}
