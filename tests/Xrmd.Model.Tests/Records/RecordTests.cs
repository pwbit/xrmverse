using FluentAssertions;
using Xrmd.Model.Records;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Model.Tests.Records;

public class RecordTests
{
    [Fact]
    public void Indexer_throws_KeyNotFound_when_absent()
    {
        var record = new Record { LogicalName = "account" };
        var act = () => record["x"];
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Indexer_sets_and_gets()
    {
        var record = new Record { LogicalName = "account" };
        record["name"] = "Contoso";
        record["name"].Should().Be("Contoso");
        record.Contains("name").Should().BeTrue();
        record.Contains("other").Should().BeFalse();
    }

    [Fact]
    public void GetValue_returns_default_when_absent()
    {
        var record = new Record { LogicalName = "account" };
        record.GetValue<int?>("x").Should().BeNull();
        record.GetValue<string>("x").Should().BeNull();
        record.GetValue<int>("x").Should().Be(0);
    }

    [Fact]
    public void GetValue_returns_stored_instance()
    {
        var choice = new ChoiceValue(3);
        var record = new Record { LogicalName = "account" };
        record["status"] = choice;
        record.GetValue<ChoiceValue>("status").Should().BeSameAs(choice);
    }

    [Fact]
    public void Attributes_preserve_insertion_order()
    {
        var record = new Record { LogicalName = "account" };
        record["zeta"] = 1;
        record["alpha"] = 2;
        record["mid"] = 3;
        record.Attributes.Keys.Should().Equal("zeta", "alpha", "mid");
    }

    [Fact]
    public void Attribute_keys_compare_ordinally()
    {
        var record = new Record { LogicalName = "account" };
        record["Name"] = "a";
        record["name"] = "b";
        record.Attributes.Should().HaveCount(2);
        record.Contains("NAME").Should().BeFalse();
    }

    [Fact]
    public void ToReference_copies_identity_and_row_version_but_not_keys_or_name()
    {
        var id = Guid.NewGuid();
        var record = new Record { LogicalName = "account", Id = id, RowVersion = "123" };
        record.KeyAttributes["accountnumber"] = "A1";
        record["name"] = "Contoso";

        var reference = record.ToReference();

        reference.LogicalName.Should().Be("account");
        reference.Id.Should().Be(id);
        reference.RowVersion.Should().Be("123");
        reference.KeyAttributes.Should().BeEmpty();
        reference.Name.Should().BeNull();
    }

    [Fact]
    public void GetValue_throws_InvalidCast_on_wrong_type()
    {
        var record = new Record { LogicalName = "account" };
        record["s"] = "text";
        record["i"] = 5;
        var asInt = () => record.GetValue<int>("s");
        var asString = () => record.GetValue<string>("i");
        var asLong = () => record.GetValue<long>("i");
        var asChoice = () => record.GetValue<ChoiceValue>("s");
        asInt.Should().Throw<InvalidCastException>();
        asString.Should().Throw<InvalidCastException>();
        asLong.Should().Throw<InvalidCastException>();
        asChoice.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void GetValue_returns_default_for_explicit_null()
    {
        var record = new Record { LogicalName = "account" };
        record["n"] = null;
        record.GetValue<int?>("n").Should().BeNull();
        record.GetValue<string>("n").Should().BeNull();
        record.GetValue<int>("n").Should().Be(0);
    }

    [Fact]
    public void RelationshipName_has_value_equality_as_dictionary_key()
    {
        var related = new Dictionary<RelationshipName, RecordCollection>
        {
            [new RelationshipName("contact_customer_accounts", RecordRole.Referenced)] = new(),
        };
        related.ContainsKey(new RelationshipName("contact_customer_accounts", RecordRole.Referenced)).Should().BeTrue();
        related.ContainsKey(new RelationshipName("contact_customer_accounts", null)).Should().BeFalse();
    }

    [Fact]
    public void Enum_values_match_the_documented_sdk_values()
    {
        ((int)RecordRole.Referencing).Should().Be(0);
        ((int)RecordRole.Referenced).Should().Be(1);
        ((int)RecordState.Unchanged).Should().Be(0);
        ((int)RecordState.Created).Should().Be(1);
        ((int)RecordState.Changed).Should().Be(2);
    }
}
