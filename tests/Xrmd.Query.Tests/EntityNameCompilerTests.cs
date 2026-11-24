using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Model.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>Plan 2.11 A7: an EntityName companion reads and filters its lookup's type column, by table name.</summary>
public class EntityNameCompilerTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc));

    private static CompiledQuery Compile(FilterOperator op, params object?[] values)
    {
        var root = new EntityNode { EntityName = "annotation" };
        root.Columns.Add(new ColumnNode("objecttypecode"));
        root.Filter.Conditions.Add(new ConditionNode(null, "objecttypecode", op, values));
        return new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(new QueryModel { Root = root }, Context);
    }

    [Fact]
    public void Table_name_filters_the_type_column_by_object_type_code()
    {
        var c = Compile(FilterOperator.Equal, "account");
        c.Sql.Should().Contain("t0.[objectidtype] AS c1").And.Contain("t0.[objectidtype] = @p0");
        c.Parameters.Single(p => p.ParameterName == "@p0").Value.Should().Be(1);
        Compile(FilterOperator.In, "account", "pb_project").Parameters.Where(p => p.ParameterName.StartsWith("@p", StringComparison.Ordinal))
            .Select(p => p.Value).Should().Equal(1, 10000);
        Compile(FilterOperator.NotNull).Sql.Should().Contain("t0.[objectidtype] IS NOT NULL");
    }

    [Fact]
    public void Unknown_table_name_is_refused() =>
        FluentActions.Invoking(() => Compile(FilterOperator.Equal, "nosuchtable"))
            .Should().Throw<XrmdValidationException>().WithMessage("*nosuchtable*");

    [Theory]
    [InlineData(FilterOperator.Like, "acc%")]
    [InlineData(FilterOperator.GreaterThan, "1")]
    [InlineData(FilterOperator.BeginsWith, "acc")]
    public void Other_operators_are_refused(FilterOperator op, string value) =>
        FluentActions.Invoking(() => Compile(op, value))
            .Should().Throw<XrmdValidationException>().WithMessage("*objecttypecode*");
}
