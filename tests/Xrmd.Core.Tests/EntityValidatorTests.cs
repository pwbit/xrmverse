using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Core.Validation;

namespace Xrmd.Core.Tests;

public sealed class EntityValidatorTests
{
    private static readonly EntityMetadata Project = new() { LogicalName = "pb_project" };

    private static readonly IReadOnlyDictionary<string, AttributeMetadata> Attrs = new AttributeMetadata[]
    {
        new UniqueIdentifierAttributeMetadata { LogicalName = "pb_projectid", IsValidForCreate = true, IsValidForUpdate = false },
        new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 10 },
        new IntegerAttributeMetadata { LogicalName = "pb_count" },
        new IntegerAttributeMetadata { LogicalName = "pb_percent", MinValue = 0, MaxValue = 100 },
        BigInt("pb_big", -10, 10),
        new MoneyAttributeMetadata { LogicalName = "pb_budget" },
        new MoneyAttributeMetadata { LogicalName = "pb_fee", MinValue = 0, MaxValue = 1000 },
        new DecimalAttributeMetadata { LogicalName = "pb_rate", Precision = 2 },
        new DecimalAttributeMetadata { LogicalName = "pb_share", Precision = 2, MinValue = 0m, MaxValue = 1m },
        new DoubleAttributeMetadata { LogicalName = "pb_ratio" },
        new DoubleAttributeMetadata { LogicalName = "pb_angle", MinValue = -180, MaxValue = 180 },
        new DateTimeAttributeMetadata { LogicalName = "createdon", IsValidForCreate = false, IsValidForUpdate = false },
        new LookupAttributeMetadata { LogicalName = "pb_customerid", Targets = ["systemuser"] },
        new PicklistAttributeMetadata
        {
            LogicalName = "pb_priority",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection { new OptionMetadata { Value = 1 }, new OptionMetadata { Value = 2 } }),
        },
    }.ToDictionary(a => a.LogicalName);

    /// <summary>BigInt MinValue/MaxValue have non-public setters in the SDK.</summary>
    private static BigIntAttributeMetadata BigInt(string logicalName, long min, long max)
    {
        var a = new BigIntAttributeMetadata { LogicalName = logicalName };
        typeof(BigIntAttributeMetadata).GetProperty(nameof(BigIntAttributeMetadata.MinValue))!.SetValue(a, min);
        typeof(BigIntAttributeMetadata).GetProperty(nameof(BigIntAttributeMetadata.MaxValue))!.SetValue(a, max);
        return a;
    }

    private static Action Validating(string attribute, object? value, bool isCreate = true) =>
        () => EntityValidator.Validate(new Entity("pb_project") { [attribute] = value }, Project, Attrs, isCreate);

    [Fact]
    public void Unknown_attribute_throws_InvalidArgument_with_name()
    {
        Validating("pb_nope", "x").Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage("Attribute 'pb_nope' not found on entity 'pb_project'*");
    }

    [Fact]
    public void String_over_maxlength_throws()
    {
        Validating("pb_name", new string('x', 11)).Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage("*pb_name*");
    }

    [Fact]
    public void String_at_maxlength_is_valid()
    {
        Validating("pb_name", new string('x', 10)).Should().NotThrow();
    }

    [Fact]
    public void Wrong_clr_type_for_int_throws()
    {
        Validating("pb_count", "abc").Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage("*pb_count*");
    }

    [Fact]
    public void Decimal_for_money_throws()
    {
        Validating("pb_budget", 10m).Should().Throw<XrmdFaultException>().WithMessage("*pb_budget*");
    }

    [Fact]
    public void Lookup_to_entity_outside_targets_throws()
    {
        Validating("pb_customerid", new EntityReference("account", Guid.NewGuid())).Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument).WithMessage("*account*");
    }

    [Fact]
    public void Undefined_option_value_throws()
    {
        Validating("pb_priority", new OptionSetValue(99)).Should().Throw<XrmdFaultException>().WithMessage("*99*");
    }

    [Fact]
    public void Null_and_valid_values_pass()
    {
        Validating("pb_name", null).Should().NotThrow();
        Validating("pb_count", 5).Should().NotThrow();
        Validating("pb_budget", new Money(1.5m)).Should().NotThrow();
        Validating("pb_customerid", new EntityReference("systemuser", Guid.NewGuid())).Should().NotThrow();
        Validating("pb_priority", new OptionSetValue(2)).Should().NotThrow();
    }

    [Fact]
    public void Attributes_not_valid_for_the_operation_are_ignored()
    {
        // Dataverse ignores read-only attributes (e.g. a retrieved entity sent back to Update).
        Validating("createdon", "not a date").Should().NotThrow();
        Validating("pb_projectid", "not a guid", isCreate: false).Should().NotThrow();
        EntityValidator.IsWritable(Attrs["createdon"], isCreate: true).Should().BeFalse();
        EntityValidator.IsWritable(Attrs["pb_projectid"], isCreate: true).Should().BeTrue();
        EntityValidator.IsWritable(Attrs["pb_projectid"], isCreate: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("pb_ratio", double.PositiveInfinity)]
    [InlineData("pb_ratio", double.NegativeInfinity)]
    [InlineData("pb_ratio", double.NaN)]
    [InlineData("pb_angle", 180.5)]
    [InlineData("pb_angle", -181d)]
    public void Double_not_finite_or_out_of_range_throws(string attribute, double value)
    {
        Validating(attribute, value).Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage($"*{attribute}*");
    }

    [Fact]
    public void Double_within_range_is_valid()
    {
        Validating("pb_angle", 180d).Should().NotThrow();
        Validating("pb_ratio", 1e300).Should().NotThrow();
    }

    [Theory]
    [InlineData("pb_budget", "922337203685477.5808")]
    [InlineData("pb_budget", "-1e20")]
    [InlineData("pb_fee", "1000.01")]
    [InlineData("pb_fee", "-0.01")]
    public void Money_out_of_range_throws(string attribute, string amount)
    {
        Validating(attribute, new Money(decimal.Parse(amount, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)))
            .Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).WithMessage($"*{attribute}*");
    }

    [Fact]
    public void Money_at_limits_is_valid()
    {
        Validating("pb_budget", new Money(922337203685477.5807m)).Should().NotThrow();
        Validating("pb_budget", new Money(-922337203685477.5807m)).Should().NotThrow();
        Validating("pb_fee", new Money(1000m)).Should().NotThrow();
    }

    [Theory]
    [InlineData("pb_rate", "100000000000000000000000")] // 10^23: beyond decimal(23,2)
    [InlineData("pb_rate", "999999999999999999999.995")] // rounds to 10^21: beyond decimal(23,2)
    [InlineData("pb_share", "1.5")]
    public void Decimal_out_of_range_throws(string attribute, string value)
    {
        Validating(attribute, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).WithMessage($"*{attribute}*");
    }

    [Fact]
    public void Decimal_within_column_and_range_is_valid_and_excess_scale_is_accepted()
    {
        Validating("pb_rate", 999999999999999999999.99m).Should().NotThrow();
        Validating("pb_rate", 0.12345m).Should().NotThrow();
        Validating("pb_share", 1m).Should().NotThrow();
    }

    [Theory]
    [InlineData("pb_percent", 101L)]
    [InlineData("pb_percent", -1L)]
    [InlineData("pb_big", 11L)]
    [InlineData("pb_big", -11L)]
    public void Integer_and_bigint_out_of_range_throw(string attribute, long value)
    {
        object boxed = attribute == "pb_percent" ? (int)value : value;
        Validating(attribute, boxed).Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).WithMessage($"*{attribute}*");
    }
}
