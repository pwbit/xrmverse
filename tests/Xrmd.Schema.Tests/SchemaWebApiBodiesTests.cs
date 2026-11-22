using System.Text.Json.Nodes;
using FluentAssertions;

namespace Xrmd.Schema.Tests;

public class SchemaWebApiBodiesTests
{
    private static string Label(JsonNode? label) => label!["LocalizedLabels"]![0]!["Label"]!.GetValue<string>();

    [Fact]
    public void Entity_has_names_labels_ownership_and_one_primary_name()
    {
        var json = SchemaWebApiBodies.Entity(new SchemaTable
        {
            Name = "xv_project", Label = "Project", PluralLabel = "Projects", Ownership = SchemaOwnership.Organization,
            PrimaryName = new SchemaPrimaryName { Name = "xv_name", Label = "Name" },
        });

        json["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.EntityMetadata");
        json["LogicalName"]!.GetValue<string>().Should().Be("xv_project");
        json["SchemaName"]!.GetValue<string>().Should().Be("xv_project");
        json["OwnershipType"]!.GetValue<string>().Should().Be("OrganizationOwned");
        Label(json["DisplayName"]).Should().Be("Project");
        Label(json["DisplayCollectionName"]).Should().Be("Projects");
        var primary = json["Attributes"]!.AsArray().Should().ContainSingle().Subject!;
        primary["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.StringAttributeMetadata");
        primary["LogicalName"]!.GetValue<string>().Should().Be("xv_name");
        primary["IsPrimaryName"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Entity_defaults_to_user_ownership()
    {
        var json = SchemaWebApiBodies.Entity(new SchemaTable { Name = "xv_t", Label = "T", PrimaryName = new SchemaPrimaryName { Name = "xv_name" } });
        json["OwnershipType"]!.GetValue<string>().Should().Be("UserOwned");
    }

    [Fact]
    public void Attribute_string_carries_max_length_and_label()
    {
        var json = SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_notes", Type = "string", Label = "Notes", MaxLength = 250 });
        json["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.StringAttributeMetadata");
        json["MaxLength"]!.GetValue<int>().Should().Be(250);
        json["SchemaName"]!.GetValue<string>().Should().Be("xv_notes");
        Label(json["DisplayName"]).Should().Be("Notes");
    }

    [Fact]
    public void Attribute_bool_has_yes_and_no_options()
    {
        var json = SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_flag", Type = "bool" });
        json["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.BooleanAttributeMetadata");
        var options = json["OptionSet"]!.AsObject();
        Label(options["TrueOption"]!["Label"]).Should().Be("Yes");
        options["TrueOption"]!["Value"]!.GetValue<int>().Should().Be(1);
        Label(options["FalseOption"]!["Label"]).Should().Be("No");
        options["FalseOption"]!["Value"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void Attribute_decimal_carries_precision()
    {
        var json = SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_rate", Type = "decimal", Precision = 4 });
        json["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.DecimalAttributeMetadata");
        json["Precision"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public void Attribute_money_and_choice_with_a_global_set()
    {
        SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_price", Type = "money" })["@odata.type"]!.GetValue<string>()
            .Should().Be("Microsoft.Dynamics.CRM.MoneyAttributeMetadata");

        var choice = SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_prio", Type = "choice", Choice = "xv_priority" });
        choice["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.PicklistAttributeMetadata");
        choice["OptionSet"]!["Name"]!.GetValue<string>().Should().Be("xv_priority");
        choice["OptionSet"]!["IsGlobal"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Attribute_refuses_a_lookup_and_an_invalid_column()
    {
        FluentActions.Invoking(() => SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_l", Type = "lookup", Target = "xv_t" }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_i", Type = "int", MaxLength = 3 }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Lookup_is_a_one_to_many_with_its_lookup_attribute()
    {
        var json = SchemaWebApiBodies.Lookup(new SchemaColumn { Name = "xv_parentid", Type = "lookup", Target = "xv_parent", Label = "Parent" },
            "xv_child", "xv_child_xv_parentid_xv_parent", "xv_parentid");

        json["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata");
        json["SchemaName"]!.GetValue<string>().Should().Be("xv_child_xv_parentid_xv_parent");
        json["ReferencingEntity"]!.GetValue<string>().Should().Be("xv_child");
        json["ReferencedEntity"]!.GetValue<string>().Should().Be("xv_parent");
        json["ReferencedAttribute"]!.GetValue<string>().Should().Be("xv_parentid");
        var lookup = json["Lookup"]!.AsObject();
        lookup["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.LookupAttributeMetadata");
        lookup["LogicalName"]!.GetValue<string>().Should().Be("xv_parentid");
        lookup["Targets"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("xv_parent");
    }

    [Fact]
    public void ManyToMany_names_both_tables_and_the_intersect()
    {
        var json = SchemaWebApiBodies.ManyToMany("xv_a_xv_b", "xv_a", "xv_b");
        json["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata");
        json["SchemaName"]!.GetValue<string>().Should().Be("xv_a_xv_b");
        json["Entity1LogicalName"]!.GetValue<string>().Should().Be("xv_a");
        json["Entity2LogicalName"]!.GetValue<string>().Should().Be("xv_b");
        json["IntersectEntityName"]!.GetValue<string>().Should().Be("xv_a_xv_b");
    }

    [Fact]
    public void Choice_is_a_global_option_set_with_its_options()
    {
        var json = SchemaWebApiBodies.Choice("xv_priority", "Priority",
            [new SchemaOption { Label = "Low", Value = 100000000 }, new SchemaOption { Label = "High", Value = 100000001 }]);

        json["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.OptionSetMetadata");
        json["Name"]!.GetValue<string>().Should().Be("xv_priority");
        json["IsGlobal"]!.GetValue<bool>().Should().BeTrue();
        Label(json["DisplayName"]).Should().Be("Priority");
        json["Options"]!.AsArray().Select(o => (o!["Value"]!.GetValue<int>(), Label(o["Label"])))
            .Should().Equal((100000000, "Low"), (100000001, "High"));
    }

    [Fact]
    public void Entity_of_an_activity_states_IsActivity_the_flags_and_the_Subject_column()
    {
        var json = SchemaWebApiBodies.Entity(new SchemaTable
        {
            Name = "xv_sms", Label = "SMS", IsActivity = true, HasNotes = true,
            PrimaryName = new SchemaPrimaryName { Name = "subject", Label = "Subject" },
        });

        json["IsActivity"]!.GetValue<bool>().Should().BeTrue();
        json["HasNotes"]!.GetValue<bool>().Should().BeTrue();
        json.ContainsKey("HasActivities").Should().BeFalse();
        var primary = json["Attributes"]![0]!;
        (primary["LogicalName"]!.GetValue<string>(), primary["SchemaName"]!.GetValue<string>()).Should().Be(("subject", "Subject"));

        var plain = SchemaWebApiBodies.Entity(new SchemaTable { Name = "xv_t", Label = "T", HasActivities = true, PrimaryName = new SchemaPrimaryName { Name = "xv_name" } });
        plain["HasActivities"]!.GetValue<bool>().Should().BeTrue();
        plain.ContainsKey("IsActivity").Should().BeFalse();
    }
}
