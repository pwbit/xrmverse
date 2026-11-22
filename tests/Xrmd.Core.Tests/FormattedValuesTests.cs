using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Core.Tests;

public sealed class FormattedValuesTests
{
    private static Label English(string text) => new(new LocalizedLabel(text, 1033), new[] { new LocalizedLabel(text, 1033) });

    private static PicklistAttributeMetadata Priority() => new()
    {
        LogicalName = "pb_priority",
        OptionSet = new OptionSetMetadata(new OptionMetadataCollection
        {
            new OptionMetadata(English("Low"), 1), new OptionMetadata(English("Medium"), 2), new OptionMetadata(English("High"), 3),
        }),
    };

    public static TheoryData<AttributeMetadata, object, string?> Cases() => new()
    {
        { Priority(), new OptionSetValue(2), "Medium" },
        { Priority(), new OptionSetValue(9), null },
        { new BooleanAttributeMetadata { LogicalName = "b" }, true, "Yes" },
        { new BooleanAttributeMetadata { LogicalName = "b" }, false, "No" },
        {
            new BooleanAttributeMetadata(new BooleanOptionSetMetadata(new OptionMetadata(English("On"), 1), new OptionMetadata(English("Off"), 0)))
                { LogicalName = "b" },
            true, "On"
        },
        { new MoneyAttributeMetadata { LogicalName = "pb_budget" }, new Money(1234.5m), "1,234.50" },
        { new DecimalAttributeMetadata { LogicalName = "d", Precision = 3 }, 1234.5m, "1,234.500" },
        { new DoubleAttributeMetadata { LogicalName = "f" }, 1234.567d, "1,234.57" },
        { new DoubleAttributeMetadata { LogicalName = "f", Precision = 1 }, 2.26d, "2.3" },
        { new IntegerAttributeMetadata { LogicalName = "i" }, 12345, "12,345" },
        { new BigIntAttributeMetadata { LogicalName = "l" }, 1234567890123L, "1,234,567,890,123" },
        { new BigIntAttributeMetadata { LogicalName = "versionnumber" }, 1234567L, null }, // Dataverse does not format it
        { new DateTimeAttributeMetadata { LogicalName = "pb_startdate" }, new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc), "3/10/2026 2:05 PM" },
        {
            new DateTimeAttributeMetadata { LogicalName = "pb_startdate", Format = DateTimeFormat.DateOnly },
            new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), "3/10/2026"
        },
        { new LookupAttributeMetadata { LogicalName = "pb_customerid" }, new EntityReference("systemuser", Guid.Empty) { Name = "Admin" }, "Admin" },
        { new LookupAttributeMetadata { LogicalName = "pb_customerid" }, new EntityReference("systemuser", Guid.Empty), null },
        { new StringAttributeMetadata { LogicalName = "pb_name" }, "text", null },
        { new UniqueIdentifierAttributeMetadata { LogicalName = "id" }, Guid.Empty, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Format(AttributeMetadata a, object value, string? expected) =>
        FormattedValues.Format(a, value).Should().Be(expected);

    [Fact]
    public void Multi_select_joins_labels()
    {
        var a = new MultiSelectPicklistAttributeMetadata { LogicalName = "m", OptionSet = Priority().OptionSet };

        FormattedValues.Format(a, new OptionSetValueCollection { new(1), new(3) }).Should().Be("Low; High");
    }

    [Fact]
    public void Label_prefers_user_localized_then_1033_then_first()
    {
        var italianOnly = new Label(null, new[] { new LocalizedLabel("Bassa", 1040) });
        var mixed = new Label(null, new[] { new LocalizedLabel("Bassa", 1040), new LocalizedLabel("Low", 1033) });
        var user = new Label(new LocalizedLabel("Mine", 1040), new[] { new LocalizedLabel("Low", 1033) });
        var a = new PicklistAttributeMetadata
        {
            LogicalName = "p",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection
            {
                new OptionMetadata(italianOnly, 1), new OptionMetadata(mixed, 2), new OptionMetadata(user, 3), new OptionMetadata { Value = 4 },
            }),
        };

        FormattedValues.Format(a, new OptionSetValue(1)).Should().Be("Bassa");
        FormattedValues.Format(a, new OptionSetValue(2)).Should().Be("Low");
        FormattedValues.Format(a, new OptionSetValue(3)).Should().Be("Mine");
        FormattedValues.Format(a, new OptionSetValue(4)).Should().BeNull();
    }

    [Fact]
    public void State_and_status_use_option_labels()
    {
        var state = new StateAttributeMetadata
        {
            LogicalName = "statecode",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection { new StateOptionMetadata { Value = 0, Label = English("Active") } }),
        };

        FormattedValues.Format(state, new OptionSetValue(0)).Should().Be("Active");
    }
    // ---- money with a currency (plan 2.10, ruling X11) ---------------------------------------------------------------

    private static MoneyAttributeMetadata Amount() => new() { LogicalName = "xv_amount" };

    [Fact]
    public void Money_with_symbol_and_currency_precision() =>
        FormattedValues.Format(Amount(), new Money(1234.5m), new MoneyFormat("$", 2, 0)).Should().Be("$1,234.50");

    [Theory]
    [InlineData(0, "$1,234.50")]
    [InlineData(1, "1,234.50$")]
    [InlineData(2, "$ 1,234.50")]
    [InlineData(3, "1,234.50 $")]
    public void Format_code_places_the_symbol(int formatCode, string expected) =>
        FormattedValues.Format(Amount(), new Money(1234.5m), new MoneyFormat("$", 2, formatCode)).Should().Be(expected);

    [Theory]
    [InlineData(0, "-$1,234.50")]
    [InlineData(1, "-1,234.50$")]
    [InlineData(2, "-$ 1,234.50")]
    [InlineData(3, "-1,234.50 $")]
    public void Negative_puts_minus_first(int formatCode, string expected) =>
        FormattedValues.Format(Amount(), new Money(-1234.5m), new MoneyFormat("$", 2, formatCode)).Should().Be(expected);

    [Fact]
    public void Precision_0() =>
        FormattedValues.Format(Amount(), new Money(1234.5m), new MoneyFormat("¥", 0, 0)).Should().Be("¥1,235");

    [Fact]
    public void Precision_4_and_iso_code_as_symbol() =>
        FormattedValues.Format(Amount(), new Money(0.12345m), new MoneyFormat("USD", 4, 3)).Should().Be("0.1235 USD");

    [Fact]
    public void Without_money_format_keeps_N2()
    {
        FormattedValues.Format(Amount(), new Money(1234.5m)).Should().Be("1,234.50");
        FormattedValues.Format(Amount(), new Money(1234.5m), null).Should().Be("1,234.50");
    }

    [Fact]
    public void Money_format_is_ignored_for_other_types() =>
        FormattedValues.Format(new DecimalAttributeMetadata { LogicalName = "d", Precision = 1 }, 2.25m, new MoneyFormat("$", 2, 0))
            .Should().Be("2.3");

    [Fact]
    public void Activity_status_uses_the_option_set_of_the_activity_table()
    {
        // Plan 2.11 A11 (checklist 3f): an activitypointer row's statuscode is the activity's own; its label comes from that table.
        var task = new EntityMetadata { LogicalName = "task" };
        task.SetAttributes([new StatusAttributeMetadata
        {
            LogicalName = "statuscode",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection
            {
                new StatusOptionMetadata(2, 0) { Label = English("Not Started") }, new StatusOptionMetadata(3, 0) { Label = English("In Progress") },
            }),
        }]);
        EntityMetadata? Tables(string name) => name == "task" ? task : null;

        FormattedValues.ActivityStatus(3, "task", Tables).Should().Be("In Progress");
        FormattedValues.ActivityStatus(9, "task", Tables).Should().BeNull();
        FormattedValues.ActivityStatus(3, "email", Tables).Should().BeNull();
        FormattedValues.ActivityStatus(3, null, Tables).Should().BeNull();
    }
}
