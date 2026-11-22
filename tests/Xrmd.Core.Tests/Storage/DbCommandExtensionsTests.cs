using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Core.Tests.Storage;

/// <summary>Plan 2.7a, Task 1: the provider-neutral parameter helpers.</summary>
public sealed class DbCommandExtensionsTests
{
    // Any provider's command: the helpers only use the DbCommand surface.
    private static DbCommand NewCommand() => new SqlCommand();

    [Fact]
    public void Null_value_is_sent_as_DBNull()
    {
        using var cmd = NewCommand();

        var p = cmd.AddParameter("@a", DbType.String, null);

        p.Value.Should().Be(DBNull.Value);
        p.ParameterName.Should().Be("@a");
        p.DbType.Should().Be(DbType.String);
        cmd.Parameters.Cast<DbParameter>().Should().ContainSingle().Which.Should().BeSameAs(p);
    }

    [Fact]
    public void Size_precision_and_scale_are_set()
    {
        using var cmd = NewCommand();

        var text = cmd.AddParameter("@t", DbType.String, "x", 256);
        var max = cmd.AddParameter("@m", DbType.Binary, new byte[] { 1 }, -1);
        var money = cmd.AddParameter("@d", DbType.Decimal, 1.5m, precision: 38, scale: 10);

        text.Size.Should().Be(256);
        max.Size.Should().Be(-1);
        money.Precision.Should().Be(38);
        money.Scale.Should().Be(10);
        money.Value.Should().Be(1.5m);
    }

    [Fact]
    public void Detached_parameters_are_copied_onto_the_command()
    {
        using var cmd = NewCommand();
        var detached = new DetachedParameter("@d", DbType.Decimal, 2m, precision: 38, scale: 10);

        cmd.AddParameters([detached, new DetachedParameter("@s", DbType.String, null, -1)]);

        cmd.Parameters.Count.Should().Be(2);
        var d = cmd.Parameters["@d"];
        d.Should().NotBeSameAs(detached);
        d.DbType.Should().Be(DbType.Decimal);
        (d.Precision, d.Scale, d.Value).Should().Be(((byte)38, (byte)10, (object)2m));
        cmd.Parameters["@s"].Value.Should().Be(DBNull.Value);
        cmd.Parameters["@s"].Size.Should().Be(-1);
    }

    [Fact]
    public void Command_is_enlisted_in_the_transaction_given()
    {
        using var conn = new SqlConnection();

        using var cmd = conn.Command("SELECT 1");

        cmd.CommandText.Should().Be("SELECT 1");
        cmd.Connection.Should().BeSameAs(conn);
        cmd.Transaction.Should().BeNull();
    }

    [Theory]
    [InlineData("Server=.;Initial Catalog=org1;Integrated Security=true", "org1")]
    [InlineData("Server=.;Database=org2", "org2")]
    [InlineData("Host=pg;database=org3;Username=x", "org3")]
    [InlineData("Server=.", "")]
    [InlineData(null, "")]
    public void Database_name_is_read_from_either_provider_connection_string(string? connectionString, string expected) =>
        StorageConnectionStrings.DatabaseName(connectionString).Should().Be(expected);
}
