using System.Data;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Query.Sdk;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests.Sdk;

/// <summary>
/// SDK condition values (Money, OptionSetValue, EntityReference) are unwrapped at the Sdk/ edge and coerce exactly as before
/// (S.2c Review Focus 2): same value, same parameter type, alone and as the elements of an <c>object[]</c> (an In condition).
/// </summary>
public class SdkConditionValuesTests
{
    private static readonly Guid Id = Guid.Parse("2b7c1e4a-9d3f-4a6b-8c5d-1e2f3a4b5c6d");

    public static TheoryData<object, object, string, DbType> Cases() => new()
    {
        { new Money(12.5m), 12.5m, "pb_budget", DbType.Decimal },
        { new OptionSetValue(3), 3, "pb_priority", DbType.Int32 },
        { new EntityReference("account", Id), Id, "pb_customerid", DbType.Guid },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Unwrap_gives_the_raw_value(object sdkValue, object expected, string attribute, DbType dbType)
    {
        _ = attribute;
        _ = dbType;
        SdkConditionValues.Unwrap(sdkValue).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Coerce_on_the_matching_column_gives_the_same_value_and_type(object sdkValue, object expected, string attribute, DbType dbType)
    {
        var a = TestMetadata.Attribute("pb_project", attribute);

        ValueCoercer.Coerce(a, sdkValue).Should().Be(expected);
        ValueCoercer.DbTypeOf(a).Should().Be(dbType);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_element_of_an_array_coerces_the_same(object sdkValue, object expected, string attribute, DbType dbType)
    {
        var a = TestMetadata.Attribute("pb_project", attribute);
        object?[] values = [sdkValue, sdkValue];

        values.Select(v => SdkConditionValues.Unwrap(v)).Should().Equal(expected, expected);
        values.Select(v => ValueCoercer.Coerce(a, v)).Should().Equal(expected, expected);
        ValueCoercer.DbTypeOf(a).Should().Be(dbType);
    }

    [Fact]
    public void Other_values_are_returned_unchanged()
    {
        var array = new object[] { new Money(1m) };
        SdkConditionValues.Unwrap(null).Should().BeNull();
        SdkConditionValues.Unwrap("12.5").Should().Be("12.5");
        SdkConditionValues.Unwrap(7).Should().Be(7);
        SdkConditionValues.Unwrap(array).Should().BeSameAs(array);
    }

    /// <summary>Only the matching column type unwraps an SDK value; on another column it is refused, as on main.</summary>
    [Theory]
    [MemberData(nameof(Mismatches))]
    public void An_sdk_value_on_another_column_type_is_still_refused(object sdkValue, string entity, string attribute)
    {
        var act = () => ValueCoercer.Coerce(TestMetadata.Attribute(entity, attribute), sdkValue);

        act.Should().Throw<XrmdValidationException>().WithMessage($"Invalid value * for attribute '{attribute}' of type *");
    }

    public static TheoryData<object, string, string> Mismatches() => new()
    {
        { new OptionSetValue(3), "xrmv_calcorder", "xrmv_ratio" },        // decimal column
        { new OptionSetValue(3), "pb_project", "pb_budget" },             // money column (same arm)
        { new Money(3m), "pb_project", "pb_priority" },                   // picklist column
        { new EntityReference("account", Id), "xrmv_calcorder", "xrmv_ratio" }, // decimal column
        { new EntityReference("account", Id), "pb_project", "pb_budget" },     // money column (same arm)
    };
}
