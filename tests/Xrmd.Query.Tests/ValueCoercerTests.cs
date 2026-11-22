using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

public class ValueCoercerTests
{
    private static readonly Guid Id = Guid.Parse("8f9a7c3e-1b2d-4e5f-a6b7-c8d9e0f1a2b3");

    [Theory]
    [InlineData(5.0, 5)]
    [InlineData(-3.0, -3)]
    public void Whole_decimal_and_double_coerce_to_int(double d, int expected)
    {
        Coerce("pb_project", "pb_count", (decimal)d).Should().Be(expected);
        Coerce("pb_project", "pb_count", d).Should().Be(expected);
    }

    [Fact]
    public void Whole_decimal_and_double_coerce_to_long_and_double_columns_take_decimals()
    {
        Coerce("pb_project", "versionnumber", 7m).Should().Be(7L);
        Coerce("pb_project", "versionnumber", 7d).Should().Be(7L);
        Coerce("pb_project", "pb_ratio", 1.5m).Should().Be(1.5d);
        Coerce("pb_project", "pb_budget", 1.5d).Should().Be(1.5m);
    }

    [Theory]
    [InlineData("pb_count", 1.5)]
    [InlineData("pb_count", 3e10)]
    [InlineData("versionnumber", 1.5)]
    [InlineData("versionnumber", 1e28)]
    public void Fractional_or_out_of_range_numbers_stay_a_mismatch(string attribute, double d)
    {
        var a = () => Coerce("pb_project", attribute, d);
        a.Should().Throw<XrmdValidationException>();
        var b = () => Coerce("pb_project", attribute, (decimal)d);
        b.Should().Throw<XrmdValidationException>();
    }

    private static object Coerce(string entity, string attribute, object? value) =>
        ValueCoercer.Coerce(TestMetadata.Attribute(entity, attribute), value);

    public static TheoryData<string, string, object?, object> Valid() => new()
    {
        // String / Memo
        { "pb_project", "pb_name", "abc", "abc" },
        { "pb_project", "pb_name", 12, "12" },
        // Integer
        { "pb_project", "pb_count", 42, 42 },
        { "pb_project", "pb_count", "42", 42 },
        { "pb_project", "pb_count", 42L, 42 },
        // BigInt
        { "pb_project", "versionnumber", 7L, 7L },
        { "pb_project", "versionnumber", "9000000000", 9000000000L },
        { "pb_project", "versionnumber", 7, 7L },
        // Money / Decimal
        { "pb_project", "pb_budget", new Money(12.5m), 12.5m },
        { "pb_project", "pb_budget", "12.5", 12.5m },
        { "pb_project", "pb_budget", 12.5m, 12.5m },
        { "pb_project", "pb_budget", 3, 3m },
        // Double
        { "pb_project", "pb_ratio", 0.25, 0.25 },
        { "pb_project", "pb_ratio", "0.25", 0.25 },
        { "pb_project", "pb_ratio", 2, 2.0 },
        // Boolean (none on the test entities: see Coerce_boolean)
        // Picklist / State / Status
        { "pb_project", "pb_priority", new OptionSetValue(2), 2 },
        { "pb_project", "pb_priority", "2", 2 },
        { "pb_project", "pb_priority", 2, 2 },
        { "pb_project", "statecode", new OptionSetValue(1), 1 },
        { "pb_project", "statuscode", "1", 1 },
        // DateTime → UTC
        { "pb_project", "pb_startdate", "2026-01-02T03:04:05Z", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
        { "pb_project", "pb_startdate", "2026-01-02T03:04:05+02:00", new DateTime(2026, 1, 2, 1, 4, 5, DateTimeKind.Utc) },
        { "pb_project", "pb_startdate", "2026-01-02", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
        { "pb_project", "pb_startdate", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
        { "pb_project", "pb_startdate", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
        // Lookup / Owner / UniqueIdentifier → Guid
        { "pb_project", "pb_customerid", new EntityReference("systemuser", Id), Id },
        { "pb_project", "pb_customerid", Id.ToString(), Id },
        { "pb_project", "ownerid", "{" + Id + "}", Id },
        { "pb_project", "pb_projectid", Id, Id },
        { "pb_project", "pb_projectid", Id.ToString("B").ToUpperInvariant(), Id },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void Coerce_to_column_clr_type(string entity, string attribute, object? value, object expected)
    {
        var result = Coerce(entity, attribute, value);
        result.Should().Be(expected);
        result.GetType().Should().Be(expected.GetType());
        if (result is DateTime d) d.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Coerce_memo()
    {
        ValueCoercer.Coerce(new MemoAttributeMetadata { LogicalName = "pb_notes" }, "long text").Should().Be("long text");
    }

    [Fact]
    public void Coerce_decimal()
    {
        var a = new DecimalAttributeMetadata { LogicalName = "pb_dec" };
        ValueCoercer.Coerce(a, "1.25").Should().Be(1.25m);
        ValueCoercer.Coerce(a, 1.25m).Should().Be(1.25m);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Coerce_boolean(object value, bool expected)
    {
        ValueCoercer.Coerce(new BooleanAttributeMetadata { LogicalName = "pb_flag" }, value).Should().Be(expected);
    }

    [Theory]
    [InlineData("pb_count", "abc")]
    [InlineData("pb_count", "9999999999")]
    [InlineData("pb_customerid", "not-a-guid")]
    [InlineData("pb_startdate", "yesterday")]
    [InlineData("pb_ratio", "x")]
    [InlineData("pb_budget", "1,2,3")]
    [InlineData("pb_priority", "high")]
    [InlineData("pb_count", null)]
    public void Coerce_rejects_invalid_values_with_attribute_name_and_value(string attribute, string? value)
    {
        FluentActions.Invoking(() => Coerce("pb_project", attribute, value))
            .Should().Throw<XrmdValidationException>()
            .Which.Message.Should().Contain(attribute).And.Contain(value ?? "null");
    }

    [Fact]
    public void Coerce_rejects_invalid_boolean()
    {
        FluentActions.Invoking(() => ValueCoercer.Coerce(new BooleanAttributeMetadata { LogicalName = "pb_flag" }, "yes"))
            .Should().Throw<XrmdValidationException>().Which.Message.Should().Contain("pb_flag").And.Contain("yes");
    }
}
