using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Xrmd.Integration.Tests;

/// <summary>Database options the host sets at startup (the fixture's database exists before the host starts).</summary>
[Collection("xrmd")]
public sealed class DatabaseSettingsTests(XrmdFixture fx)
{
    [SqlServerFact("read committed snapshot is a SQL Server database option; PostgreSQL's readers never wait for writers")]
    public async Task Organization_database_reads_committed_snapshots()
    {
        // Readers (e.g. the step registry's own connection) do not wait for the row locks of a writing transaction.
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()");

        (await cmd.ExecuteScalarAsync()).Should().Be(true);
    }
}
