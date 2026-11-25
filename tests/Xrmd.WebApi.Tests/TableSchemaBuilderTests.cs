using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Security;
using Xrmd.WebApi.Connector;

namespace Xrmd.WebApi.Tests;

public class TableSchemaBuilderTests
{
    private static LabelSet Label(string text) => new() { LocalizedLabels = [new LocalizedText { Label = text, LanguageCode = 1033 }] };

    private static T Col<T>(T column, string name, string label, bool read = true, bool create = true, bool update = true,
        RequirementLevel? level = null, string? description = null) where T : ColumnDefinition
    {
        column.LogicalName = name;
        column.SchemaName = name;
        column.DisplayName = Label(label);
        if (description is not null) column.Description = Label(description);
        column.IsValidODataAttribute = true;
        column.IsValidForRead = read;
        column.IsValidForCreate = create;
        column.IsValidForUpdate = update;
        if (level is { } l) column.RequiredLevel = new RequiredLevelSetting { Value = l };
        return column;
    }

    private static LookupColumn Lookup(string name, string label, ColumnTypeCode type, RequirementLevel? level, params string[] targets)
    {
        var l = Col(new LookupColumn { Targets = [.. targets] }, name, label, level: level);
        l.AttributeType = type;
        return l;
    }

    private static ChoiceOption Option(int value, string label) => new() { Value = value, Label = Label(label) };

    private static readonly TableDefinition Account = new()
    {
        LogicalName = "account", EntitySetName = "accounts", PrimaryIdAttribute = "accountid", DisplayName = Label("Account"),
        Attributes =
        [
            Col(new UniqueIdentifierColumn { IsPrimaryId = true }, "accountid", "Account"),
            Col(new TextColumn(), "name", "Account Name", level: RequirementLevel.ApplicationRequired, description: "The name"),
            Col(new TextColumn(), "xrmv_secret", "Secret"),
            Col(new ChoiceColumn { OptionSet = new ChoiceSet { Options = [Option(1, "Preferred"), Option(2, "Other")] } }, "customertypecode", "Type"),
            Col(new MultiChoiceColumn { OptionSet = new ChoiceSet { Options = [Option(1, "A"), Option(2, "B")] } }, "xrmv_multi", "Multi"),
            Col(new StateColumn { OptionSet = new ChoiceSet { Options = [Option(0, "Active")] } }, "statecode", "Status", level: RequirementLevel.SystemRequired),
            Col(new StatusColumn { OptionSet = new ChoiceSet { Options = [Option(1, "Active")] } }, "statuscode", "Status Reason"),
            Col(new FileColumn(), "xrmv_file", "File"),
            Col(new ImageColumn(), "xrmv_image", "Image"),
            Col(new DateTimeColumn { DateTimeBehavior = new DateTimeBehaviorName { Value = "DateOnly" } }, "xrmv_day", "Day"),
            Col(new DateTimeColumn { DateTimeBehavior = new DateTimeBehaviorName { Value = "UserLocal" } }, "xrmv_at", "At"),
            Col(new IntegerColumn(), "xrmv_count", "Count"),
            Col(new BigIntColumn(), "xrmv_big", "Big"),
            Col(new MoneyColumn(), "xrmv_money", "Money"),
            Col(new BooleanColumn(), "xrmv_flag", "Flag"),
            Lookup("primarycontactid", "Primary Contact", ColumnTypeCode.Lookup, RequirementLevel.ApplicationRequired, "contact"),
            Lookup("ownerid", "Owner", ColumnTypeCode.Owner, RequirementLevel.SystemRequired, "systemuser", "team"),
        ],
    };

    private static readonly TableDefinition Contact = new()
    {
        LogicalName = "contact", EntitySetName = "contacts", PrimaryIdAttribute = "contactid", DisplayName = Label("Contact"),
        Attributes =
        [
            Col(new UniqueIdentifierColumn { IsPrimaryId = true }, "contactid", "Contact"),
            Lookup("parentcustomerid", "Company", ColumnTypeCode.Customer, null, "account", "contact"),
        ],
    };

    private static readonly FakeStore Store = new(
        new EntityMetadata { LogicalName = "account", EntitySetName = "accounts" },
        new EntityMetadata { LogicalName = "contact", EntitySetName = "contacts", DisplayName = new Microsoft.Xrm.Sdk.Label("Contact", 1033) },
        new EntityMetadata { LogicalName = "systemuser", EntitySetName = "systemusers" });

    private static readonly FieldPermissionSet NoSecret = new(
        new HashSet<string> { "xrmv_secret" }, new HashSet<string> { "xrmv_secret" }, new HashSet<string>(), new HashSet<string>());

    private static JsonObject Build(TableDefinition table, SchemaUse use, FieldPermissionSet? fields = null) =>
        TableSchemaBuilder.Build(table, use, [], Store, fields ?? FieldPermissionSet.Unrestricted, 1033);

    private static JsonObject Props(JsonObject schema) => schema["properties"]!.AsObject();

    [Fact]
    public void Read_includes_lookup_value_property_as_uuid()
    {
        var p = Props(Build(Account, SchemaUse.Read));
        p["_primarycontactid_value"]!["format"]!.ToString().Should().Be("uuid");
        p["_primarycontactid_value"]!["type"]!.ToString().Should().Be("string");
        p.ContainsKey("@odata.etag").Should().BeTrue();
        p["accountid"]!["format"]!.ToString().Should().Be("uuid");
        p["name"]!["x-ms-summary"]!.ToString().Should().Be("Account Name");
        p["name"]!["description"]!.ToString().Should().Be("The name");
    }

    [Fact]
    public void Create_maps_lookup_to_bind_property()
    {
        var p = Props(Build(Account, SchemaUse.Create));
        p.ContainsKey("primarycontactid@odata.bind").Should().BeTrue();
        p.ContainsKey("_primarycontactid_value").Should().BeFalse();
        p["primarycontactid@odata.bind"]!["x-ms-summary"]!.ToString().Should().Be("Primary Contact (Contact)");
        p["primarycontactid@odata.bind"]!["description"]!.ToString().Should().Be("/contacts(<id>)");
    }

    [Fact]
    public void Create_customer_lookup_has_one_bind_per_target()
    {
        var p = Props(Build(Contact, SchemaUse.Create));
        p.ContainsKey("parentcustomerid_account@odata.bind").Should().BeTrue();
        p.ContainsKey("parentcustomerid_contact@odata.bind").Should().BeTrue();
        p.ContainsKey("parentcustomerid@odata.bind").Should().BeFalse();
    }

    [Fact]
    public void Choice_has_enum_and_labels()
    {
        var p = Props(Build(Account, SchemaUse.Read))["customertypecode"]!;
        p["enum"]!.AsArray().Select(v => (int)v!).Should().Equal(1, 2);
        p["x-ms-enum-values"]![0]!["displayName"]!.ToString().Should().Be("Preferred");
        p["x-ms-enum-values"]![0]!["value"]!.GetValue<int>().Should().Be(1);
        Props(Build(Account, SchemaUse.Create))["xrmv_multi"]!["description"]!.ToString().Should().Contain("1=A");
    }

    [Fact]
    public void Create_omits_secured_column_caller_cannot_create()
    {
        Props(Build(Account, SchemaUse.Create, NoSecret)).ContainsKey("xrmv_secret").Should().BeFalse();
        Props(Build(Account, SchemaUse.Update, NoSecret)).ContainsKey("xrmv_secret").Should().BeFalse();
        Props(Build(Account, SchemaUse.Read, NoSecret)).ContainsKey("xrmv_secret").Should().BeTrue();
    }

    [Fact]
    public void Create_lists_application_required_columns_as_required()
    {
        var required = Build(Account, SchemaUse.Create)["required"]!.AsArray().Select(v => v!.ToString()).ToList();
        required.Should().BeEquivalentTo("name", "primarycontactid@odata.bind");
    }

    [Fact]
    public void Update_has_no_required_array()
    {
        Build(Account, SchemaUse.Update).ContainsKey("required").Should().BeFalse();
        Build(Account, SchemaUse.Read).ContainsKey("required").Should().BeFalse();
    }

    [Fact]
    public void Primary_id_absent_from_create_and_update()
    {
        Props(Build(Account, SchemaUse.Create)).ContainsKey("accountid").Should().BeFalse();
        Props(Build(Account, SchemaUse.Update)).ContainsKey("accountid").Should().BeFalse();
    }

    [Fact]
    public void Status_present_only_for_update()
    {
        foreach (var name in new[] { "statecode", "statuscode" })
        {
            Props(Build(Account, SchemaUse.Update)).ContainsKey(name).Should().BeTrue();
            Props(Build(Account, SchemaUse.Read)).ContainsKey(name).Should().BeTrue();
            Props(Build(Account, SchemaUse.Create)).ContainsKey(name).Should().BeFalse();
        }
    }

    [Fact]
    public void File_and_image_columns_are_omitted_everywhere()
    {
        foreach (var use in Enum.GetValues<SchemaUse>())
        {
            var p = Props(Build(Account, use));
            p.ContainsKey("xrmv_file").Should().BeFalse();
            p.ContainsKey("xrmv_image").Should().BeFalse();
        }
    }

    [Fact]
    public void Date_only_column_uses_date_format()
    {
        var p = Props(Build(Account, SchemaUse.Read));
        p["xrmv_day"]!["format"]!.ToString().Should().Be("date");
        p["xrmv_at"]!["format"]!.ToString().Should().Be("date-time");
        p["xrmv_count"]!["format"]!.ToString().Should().Be("int32");
        p["xrmv_big"]!["format"]!.ToString().Should().Be("int64");
        p["xrmv_money"]!["type"]!.ToString().Should().Be("number");
        p["xrmv_flag"]!["type"]!.ToString().Should().Be("boolean");
    }

    private static TableDefinition Custom(params ColumnDefinition[] columns) => new()
    {
        LogicalName = "xrmv_thing", EntitySetName = "xrmv_things", PrimaryIdAttribute = "xrmv_thingid", DisplayName = Label("Thing"),
        Attributes = [Col(new UniqueIdentifierColumn { IsPrimaryId = true }, "xrmv_thingid", "Thing"), .. columns],
    };

    [Fact]
    public void Required_multi_target_lookup_marks_no_bind_property_required()
    {
        var table = Custom(Lookup("xrmv_party", "Party", ColumnTypeCode.Customer, RequirementLevel.ApplicationRequired, "account", "contact"));
        var schema = Build(table, SchemaUse.Create);
        Props(schema).ContainsKey("xrmv_party_account@odata.bind").Should().BeTrue();
        Props(schema).ContainsKey("xrmv_party_contact@odata.bind").Should().BeTrue();
        schema["required"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Columns_hidden_from_the_web_api_are_skipped_for_every_use()
    {
        var table = new TableDefinition
        {
            LogicalName = "audit", EntitySetName = "audits", PrimaryIdAttribute = "auditid", DisplayName = Label("Audit"),
            Attributes =
            [
                Col(new UniqueIdentifierColumn { IsPrimaryId = true }, "auditid", "Audit"),
                Col(new TextColumn(), "changedata", "Change Data"),
                Col(new TextColumn(), "attributemask", "Mask"),
            ],
        };
        foreach (var use in Enum.GetValues<SchemaUse>())
        {
            var p = Props(Build(table, use));
            p.ContainsKey("changedata").Should().BeFalse(use.ToString());
            p.ContainsKey("attributemask").Should().BeTrue(use.ToString());
        }
    }

    [Fact]
    public void Choice_without_options_has_no_enum()
    {
        var table = Custom(Col(new ChoiceColumn { OptionSet = new ChoiceSet { Options = [] } }, "xrmv_empty", "Empty"));
        var p = Props(Build(table, SchemaUse.Read))["xrmv_empty"]!.AsObject();
        p["type"]!.ToString().Should().Be("integer");
        p.ContainsKey("enum").Should().BeFalse();
        p.ContainsKey("x-ms-enum-values").Should().BeFalse();
    }
}
