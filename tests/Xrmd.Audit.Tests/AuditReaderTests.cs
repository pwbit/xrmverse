using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Audit.Tests;

/// <summary>
/// The <c>AttributeAuditDetail</c> built from stored change data (plan 13, ruling A7): values typed by the current metadata (a
/// big integer as <see cref="long"/> even when it fits an <see cref="int"/>), formatted values, a cleared value left out, a
/// deleted column in <c>DeletedAttributes</c>, a deleted table without values.
/// </summary>
public sealed class AuditReaderTests
{
    private static Label English(string text) => new(new LocalizedLabel(text, 1033), new[] { new LocalizedLabel(text, 1033) });

    private static EntityMetadata Table()
    {
        var entity = new EntityMetadata { LogicalName = "new_thing", SchemaName = "new_Thing" };
        entity.SetAttributes(
        [
            new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name" },
            new BigIntAttributeMetadata { LogicalName = "new_big", SchemaName = "new_Big" },
            new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count" },
            new PicklistAttributeMetadata
            {
                LogicalName = "new_size", SchemaName = "new_Size",
                OptionSet = new OptionSetMetadata([new OptionMetadata(English("Small"), 1), new OptionMetadata(English("Large"), 2)]),
            },
            new LookupAttributeMetadata { LogicalName = "new_userid", SchemaName = "new_UserId", Targets = ["systemuser"] },
        ]);
        return entity;
    }

    private static AttributeChange Change(Dictionary<string, object?> old, Dictionary<string, object?> @new) =>
        // Through the codec, as the reader gets it.
        (AttributeChange)ChangeData.Deserialize(ChangeData.Serialize(new AttributeChange(old, @new)));

    [Fact]
    public void Big_integer_columns_read_back_as_long()
    {
        var change = Change(new() { ["new_big"] = 5L, ["new_count"] = 4 }, new() { ["new_big"] = 6_000_000_000L, ["new_count"] = 5 });
        change.Old["new_big"].Should().BeOfType<int>("the format does not tell a small big integer from an integer");

        var detail = AuditReader.AttributeDetail(Table(), "new_thing", change);

        detail.OldValue["new_big"].Should().BeOfType<long>().Which.Should().Be(5L);
        detail.NewValue["new_big"].Should().BeOfType<long>().Which.Should().Be(6_000_000_000L);
        detail.OldValue["new_count"].Should().BeOfType<int>().Which.Should().Be(4);
        detail.OldValue.FormattedValues["new_big"].Should().Be("5");
    }

    [Fact]
    public void Values_carry_formatted_values_and_a_cleared_value_is_left_out()
    {
        var user = new EntityReference("systemuser", Guid.NewGuid()) { Name = "Ada" };
        var detail = AuditReader.AttributeDetail(Table(), "new_thing",
            Change(new() { ["new_size"] = new OptionSetValue(1), ["new_userid"] = null }, new() { ["new_size"] = new OptionSetValue(2), ["new_userid"] = user }));

        detail.OldValue.LogicalName.Should().Be("new_thing");
        detail.OldValue.Attributes.Keys.Should().BeEquivalentTo(["new_size"], "a null value is not listed");
        detail.OldValue.FormattedValues["new_size"].Should().Be("Small");
        detail.NewValue.FormattedValues["new_size"].Should().Be("Large");
        detail.NewValue["new_userid"].Should().BeOfType<EntityReference>().Which.Name.Should().Be("Ada");
        detail.NewValue.FormattedValues["new_userid"].Should().Be("Ada");
        detail.DeletedAttributes.Should().BeEmpty();
        detail.InvalidNewValueAttributes.Should().BeEmpty();
    }

    [Fact]
    public void A_deleted_column_goes_to_deleted_attributes()
    {
        var detail = AuditReader.AttributeDetail(Table(), "new_thing",
            Change(new() { ["new_gone"] = "a", ["new_name"] = "x" }, new() { ["new_gone"] = "b", ["new_name"] = "y" }));

        // Keyed by position: xrmverse keeps no column number for a deleted column.
        detail.DeletedAttributes.Should().Equal(new Dictionary<int, string> { [0] = "new_gone" });
        detail.OldValue.Attributes.Keys.Should().BeEquivalentTo(["new_name"]);
        detail.NewValue.Attributes.Keys.Should().BeEquivalentTo(["new_name"]);
    }

    [Fact]
    public void A_deleted_table_gives_entities_without_values()
    {
        var detail = AuditReader.AttributeDetail(null, "new_gonetable", Change(new() { ["new_name"] = "x" }, new() { ["new_name"] = "y" }));

        detail.OldValue.LogicalName.Should().Be("new_gonetable");
        detail.OldValue.Attributes.Should().BeEmpty();
        detail.NewValue.Attributes.Should().BeEmpty();
        detail.DeletedAttributes.Values.Should().BeEquivalentTo(["new_name"]);
    }

    [Fact]
    public void Attribute_history_keeps_only_that_column()
    {
        var detail = AuditReader.AttributeDetail(Table(), "new_thing",
            Change(new() { ["new_name"] = "x", ["new_count"] = 1 }, new() { ["new_name"] = "y", ["new_count"] = 2 }), "new_count");

        detail.OldValue.Attributes.Keys.Should().BeEquivalentTo(["new_count"]);
        detail.NewValue.Attributes.Keys.Should().BeEquivalentTo(["new_count"]);
    }
}
