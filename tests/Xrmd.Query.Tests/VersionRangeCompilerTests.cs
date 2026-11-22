using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>
/// Plan 2.13: <see cref="VersionRangeNode"/>, the internal filter of RetrieveEntityChanges, compiles to a sargable range on the
/// raw <c>rowversion</c> column (bigint parameters cast to <c>binary(8)</c>), so the <c>IX_{table}_versionnumber</c> index serves it.
/// </summary>
public class VersionRangeCompilerTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc));

    private static CompiledQuery Compile(QueryModel q) => new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    [Fact]
    public void Version_range_compiles_to_a_sargable_binary_range()
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Columns.Add(new ColumnNode("pb_name"));
        root.Filter.Versions.Add(new VersionRangeNode(100, 250));

        var c = Compile(new QueryModel { Root = root });

        Normalize(c.Sql).Should().Contain(
            "WHERE ((t0.[versionnumber] > CAST(@p0 AS binary(8)) AND t0.[versionnumber] < CAST(@p1 AS binary(8))))");
        c.Parameters.Single(p => p.ParameterName == "@p0").Should().Match<DbParameter>(
            p => p.DbType == DbType.Int64 && (long)p.Value! == 100);
        c.Parameters.Single(p => p.ParameterName == "@p1").Value.Should().Be(250L);
    }

    [Fact]
    public void Version_range_is_anded_with_the_conditions()
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, ["x"]));
        root.Filter.Versions.Add(new VersionRangeNode(1, 2));

        var c = Compile(new QueryModel { Root = root });

        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_name] = @p0 AND (t0.[versionnumber] > CAST(@p1 AS binary(8)) AND t0.[versionnumber] < CAST(@p2 AS binary(8))))");
    }
}
