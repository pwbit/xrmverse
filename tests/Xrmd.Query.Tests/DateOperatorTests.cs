using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

public class DateOperatorTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc));

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static CompiledQuery Compile(string attribute, ConditionOperator op, params object?[] values)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Filter.Conditions.Add(new ConditionNode(null, attribute, op, values));
        return new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(new QueryModel { Root = root }, Context);
    }

    private static string Norm(string sql) => Regex.Replace(sql, @"\s+", " ");

    private static object?[] Values(CompiledQuery c) =>
        c.Parameters.Where(p => p.ParameterName.StartsWith("@p", StringComparison.Ordinal)).Select(p => p.Value).ToArray();

    public static TheoryData<ConditionOperator, object[], DateTime[]> Ranges => new()
    {
        { ConditionOperator.On, ["2026-03-10"], [Utc(2026, 3, 10), Utc(2026, 3, 11)] },
        { ConditionOperator.OnOrAfter, ["2026-03-10T15:00:00Z"], [Utc(2026, 3, 10)] },
        { ConditionOperator.OnOrBefore, ["2026-03-10T15:00:00Z"], [Utc(2026, 3, 11)] },
        { ConditionOperator.LastXDays, [7], [Utc(2026, 3, 8), Utc(2026, 3, 15, 10, 30)] },
        { ConditionOperator.LastXDays, ["7"], [Utc(2026, 3, 8), Utc(2026, 3, 15, 10, 30)] },
        { ConditionOperator.NextXDays, [3], [Utc(2026, 3, 15, 10, 30), Utc(2026, 3, 19)] },
        { ConditionOperator.LastXMonths, [2], [Utc(2026, 1, 15, 10, 30), Utc(2026, 3, 15, 10, 30)] },
        { ConditionOperator.ThisMonth, [], [Utc(2026, 3, 1), Utc(2026, 4, 1)] },
        { ConditionOperator.ThisMonth, [""], [Utc(2026, 3, 1), Utc(2026, 4, 1)] },
        { ConditionOperator.ThisYear, [], [Utc(2026, 1, 1), Utc(2027, 1, 1)] },
    };

    [Theory]
    [MemberData(nameof(Ranges))]
    public void Date_operators_pass_expected_bounds(ConditionOperator op, object[] values, DateTime[] expected)
    {
        var c = Compile("pb_startdate", op, values);
        Values(c).Should().Equal(expected.Cast<object?>());
    }

    [Fact]
    public void Range_predicates_are_half_open_where_specified()
    {
        Norm(Compile("pb_startdate", ConditionOperator.ThisMonth).Sql).Should().Contain("t0.[pb_startdate] >= @p0 AND t0.[pb_startdate] < @p1");
        Norm(Compile("pb_startdate", ConditionOperator.LastXDays, 7).Sql).Should().Contain("t0.[pb_startdate] >= @p0 AND t0.[pb_startdate] <= @p1");
        Norm(Compile("pb_startdate", ConditionOperator.OnOrBefore, "2026-03-10").Sql).Should().Contain("t0.[pb_startdate] < @p0");
    }

    [Fact]
    public void EqualUserId_uses_user_id()
    {
        var c = Compile("pb_customerid", ConditionOperator.EqualUserId);
        Values(c).Should().Equal(Context.UserId);
        Norm(c.Sql).Should().Contain("t0.[pb_customerid] = @p0");
    }

    [Fact]
    public void NotEqualUserId_includes_nulls()
    {
        var c = Compile("pb_customerid", ConditionOperator.NotEqualUserId);
        Values(c).Should().Equal(Context.UserId);
        Norm(c.Sql).Should().Contain("(t0.[pb_customerid] <> @p0 OR t0.[pb_customerid] IS NULL)");
    }

    [Fact]
    public void EqualBusinessId_uses_business_unit()
    {
        Values(Compile("owningbusinessunit", ConditionOperator.EqualBusinessId)).Should().Equal(Context.BusinessUnitId);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public void Invalid_x_is_rejected(string x)
    {
        var act = () => Compile("pb_startdate", ConditionOperator.LastXDays, x);
        act.Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void X_requires_exactly_one_value()
    {
        var act = () => Compile("pb_startdate", ConditionOperator.NextXDays);
        act.Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Date_operator_on_non_datetime_attribute_is_rejected()
    {
        var act = () => Compile("pb_count", ConditionOperator.ThisYear);
        act.Should().Throw<XrmdValidationException>().WithMessage("*DateTime*");
    }

    [Fact]
    public void User_operator_on_non_lookup_attribute_is_rejected()
    {
        var act = () => Compile("pb_name", ConditionOperator.EqualUserId);
        act.Should().Throw<XrmdValidationException>();
    }

    [Theory]
    [InlineData(ConditionOperator.LastXDays)]
    [InlineData(ConditionOperator.NextXDays)]
    [InlineData(ConditionOperator.LastXMonths)]
    public void Huge_x_is_a_validation_error(ConditionOperator op)
    {
        var act = () => Compile("pb_startdate", op, int.MaxValue);
        act.Should().Throw<XrmdValidationException>().WithMessage("*pb_startdate*");
    }

    [Fact]
    public void On_with_offset_is_normalized_to_utc_before_truncation()
    {
        Values(Compile("pb_startdate", ConditionOperator.On, "2026-03-10T23:00:00+02:00"))
            .Should().Equal(Utc(2026, 3, 10), Utc(2026, 3, 11));
        Values(Compile("pb_startdate", ConditionOperator.On, "2026-03-10T00:30:00+02:00"))
            .Should().Equal(Utc(2026, 3, 9), Utc(2026, 3, 10));
    }

    [Fact]
    public void On_with_unparseable_value_is_rejected()
    {
        var act = () => Compile("pb_startdate", ConditionOperator.On, "not-a-date");
        act.Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void On_on_non_datetime_attribute_is_rejected()
    {
        var act = () => Compile("pb_name", ConditionOperator.On, "2026-03-10");
        act.Should().Throw<XrmdValidationException>();
    }
}
