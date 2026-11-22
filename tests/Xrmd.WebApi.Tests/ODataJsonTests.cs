using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.WebApi;

namespace Xrmd.WebApi.Tests;

public class ODataJsonTests
{
    private const string BaseUrl = "https://org.example";
    private static readonly Guid AdminId = Guid.Parse("6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e");
    private static readonly Guid ProjectId = Guid.Parse("0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70");

    private static EntityMetadata Entity(string logicalName, string setName, OwnershipTypes ownership, params AttributeMetadata[] attributes)
    {
        var e = new EntityMetadata { LogicalName = logicalName, EntitySetName = setName, OwnershipType = ownership };
        e.SetAttributes(SystemAttributes.Complete(e, attributes));
        return e;
    }

    private static readonly EntityMetadata Project = Entity("pb_project", "pb_projects", OwnershipTypes.UserOwned,
        new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 200 },
        new LookupAttributeMetadata { LogicalName = "pb_customerid", Targets = ["systemuser"] },
        new MoneyAttributeMetadata { LogicalName = "pb_budget" },
        new IntegerAttributeMetadata { LogicalName = "pb_count" },
        new BigIntAttributeMetadata { LogicalName = "pb_big" },
        new DecimalAttributeMetadata { LogicalName = "pb_rate" },
        new DoubleAttributeMetadata { LogicalName = "pb_ratio" },
        new DateTimeAttributeMetadata { LogicalName = "pb_startdate" },
        new BooleanAttributeMetadata { LogicalName = "pb_active" },
        new PicklistAttributeMetadata { LogicalName = "pb_priority" });

    private static readonly EntityMetadata SystemUser =
        Entity("systemuser", "systemusers", OwnershipTypes.BusinessOwned, new StringAttributeMetadata { LogicalName = "fullname", MaxLength = 200 });

    private static readonly FakeStore Store = new(Project, SystemUser);

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    private static Entity FromJson(string json) => ODataJson.FromJson(Body(json), Project, Store);

    private const string ProjectBody = """
        {
          "pb_name": "Apollo",
          "pb_customerid@odata.bind": "/systemusers(6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e)",
          "pb_budget": 1234.5,
          "pb_count": 7,
          "pb_big": 9007199254740993,
          "pb_rate": 0.125,
          "pb_ratio": 1.5,
          "pb_startdate": "2026-11-07T10:30:15Z",
          "pb_active": true,
          "pb_priority": 2
        }
        """;

    [Fact]
    public void FromJson_converts_values_to_metadata_types()
    {
        var e = FromJson(ProjectBody);

        e.LogicalName.Should().Be("pb_project");
        e["pb_name"].Should().Be("Apollo");
        e.GetAttributeValue<EntityReference>("pb_customerid").Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == AdminId);
        e.GetAttributeValue<Money>("pb_budget").Value.Should().Be(1234.5m);
        e["pb_count"].Should().Be(7);
        e["pb_big"].Should().Be(9007199254740993L);
        e["pb_rate"].Should().Be(0.125m);
        e["pb_ratio"].Should().Be(1.5d);
        var start = e.GetAttributeValue<DateTime>("pb_startdate");
        start.Should().Be(new DateTime(2026, 11, 7, 10, 30, 15, DateTimeKind.Utc));
        start.Kind.Should().Be(DateTimeKind.Utc);
        e["pb_active"].Should().Be(true);
        e.GetAttributeValue<OptionSetValue>("pb_priority").Value.Should().Be(2);
    }

    [Fact]
    public void Round_trip_FromJson_then_ToJson_keeps_values_and_writes_lookup_annotations()
    {
        var e = FromJson(ProjectBody);
        e.Id = ProjectId;
        e["pb_projectid"] = ProjectId;
        e["versionnumber"] = 4242L;

        var json = ODataJson.ToJson(e, Project, BaseUrl);

        json["@odata.context"]!.GetValue<string>().Should().Be("https://org.example/api/data/v9.2/$metadata#pb_projects/$entity");
        json["@odata.etag"]!.GetValue<string>().Should().Be("W/\"4242\"");
        json["pb_projectid"]!.GetValue<string>().Should().Be(ProjectId.ToString());
        json["pb_name"]!.GetValue<string>().Should().Be("Apollo");
        json["pb_budget"]!.GetValue<decimal>().Should().Be(1234.5m);
        json["pb_count"]!.GetValue<int>().Should().Be(7);
        json["pb_big"]!.GetValue<long>().Should().Be(9007199254740993L);
        json["pb_rate"]!.GetValue<decimal>().Should().Be(0.125m);
        json["pb_ratio"]!.GetValue<double>().Should().Be(1.5d);
        json["pb_startdate"]!.GetValue<string>().Should().Be("2026-11-07T10:30:15Z");
        json["pb_active"]!.GetValue<bool>().Should().BeTrue();
        json["pb_priority"]!.GetValue<int>().Should().Be(2);
        json["versionnumber"]!.GetValue<long>().Should().Be(4242L);

        json.ContainsKey("pb_customerid").Should().BeFalse();
        json["_pb_customerid_value"]!.GetValue<string>().Should().Be(AdminId.ToString());
        json["_pb_customerid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("systemuser");
        json["_pb_customerid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be("pb_customerid");
    }

    [Fact]
    public void ToJson_writes_context_first_then_etag()
    {
        var e = new Entity("pb_project", ProjectId) { ["pb_projectid"] = ProjectId, ["versionnumber"] = 7L };

        var names = ODataJson.ToJson(e, Project, BaseUrl + "/").Select(p => p.Key).ToList();

        names.Take(2).Should().Equal("@odata.context", "@odata.etag");
        ODataJson.ToJson(e, Project, BaseUrl)["@odata.context"]!.GetValue<string>()
            .Should().Be("https://org.example/api/data/v9.2/$metadata#pb_projects/$entity");
    }

    [Fact]
    public void ToJson_writes_formatted_value_annotations_only_when_requested()
    {
        var e = new Entity("pb_project", ProjectId)
        {
            ["pb_projectid"] = ProjectId,
            ["pb_name"] = "Apollo",
            ["pb_priority"] = new OptionSetValue(2),
            ["pb_customerid"] = new EntityReference("systemuser", AdminId) { Name = "Admin" },
        };
        e.FormattedValues["pb_priority"] = "Medium";
        e.FormattedValues["pb_customerid"] = "Admin";

        var plain = ODataJson.ToJson(e, Project, BaseUrl);
        plain.Select(p => p.Key).Should().NotContain(k => k.Contains("FormattedValue"));

        var json = ODataJson.ToJson(e, Project, BaseUrl, includeFormattedValues: true);
        json["pb_priority@OData.Community.Display.V1.FormattedValue"]!.GetValue<string>().Should().Be("Medium");
        json["_pb_customerid_value@OData.Community.Display.V1.FormattedValue"]!.GetValue<string>().Should().Be("Admin");
        json["_pb_customerid_value"]!.GetValue<string>().Should().Be(AdminId.ToString());
        var keys = json.Select(p => p.Key).ToList();
        keys.IndexOf("pb_priority@OData.Community.Display.V1.FormattedValue").Should().Be(keys.IndexOf("pb_priority") - 1);
        json.ContainsKey("pb_name@OData.Community.Display.V1.FormattedValue").Should().BeFalse();
    }

    [Fact]
    public void EntityCollectionJson_writes_context_count_records_and_next_link()
    {
        var e = new Entity("pb_project", ProjectId) { ["pb_projectid"] = ProjectId, ["pb_name"] = "Apollo", ["versionnumber"] = 7L };
        var collection = new EntityCollection([e]) { TotalRecordCount = 12 };

        var json = EntityCollectionJson.ToJson(collection, Project, BaseUrl + "/", ["pb_name"], false, "https://org.example/next");

        json.Select(p => p.Key).Should().Equal("@odata.context", "@odata.count", "value", "@odata.nextLink");
        json["@odata.context"]!.GetValue<string>().Should().Be("https://org.example/api/data/v9.2/$metadata#pb_projects(pb_name)");
        json["@odata.count"]!.GetValue<int>().Should().Be(12);
        json["@odata.nextLink"]!.GetValue<string>().Should().Be("https://org.example/next");
        var record = json["value"]!.AsArray().Single()!.AsObject();
        record.ContainsKey("@odata.context").Should().BeFalse();
        record["@odata.etag"]!.GetValue<string>().Should().Be("W/\"7\"");
        record["pb_name"]!.GetValue<string>().Should().Be("Apollo");
    }

    [Fact]
    public void EntityCollectionJson_omits_count_and_next_link_when_absent()
    {
        var json = EntityCollectionJson.ToJson(new EntityCollection { TotalRecordCount = -1 }, Project, BaseUrl, [], false, null);

        json.Select(p => p.Key).Should().Equal("@odata.context", "value");
        json["@odata.context"]!.GetValue<string>().Should().Be("https://org.example/api/data/v9.2/$metadata#pb_projects");
        json["value"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void ToJson_writes_money_without_trailing_zeros()
    {
        var e = new Entity("pb_project", ProjectId) { ["pb_budget"] = new Money(1234.5000m), ["pb_budget_base"] = new Money(1234.5000m) };

        var json = ODataJson.ToJson(e, Project, BaseUrl);

        json["pb_budget"]!.ToJsonString().Should().Be("1234.5");
        json["pb_budget_base"]!.ToJsonString().Should().Be("1234.5");
    }

    [Theory]
    [InlineData("systemusers(6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e)")]
    [InlineData("/systemusers(6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e)")]
    [InlineData("https://org.example/api/data/v9.2/systemusers(6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e)")]
    public void FromJson_accepts_bind_forms(string bind)
    {
        var e = FromJson($$"""{ "pb_customerid@odata.bind": "{{bind}}" }""");

        e.GetAttributeValue<EntityReference>("pb_customerid").Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == AdminId);
    }

    [Fact]
    public void FromJson_maps_null_to_null_and_skips_annotations()
    {
        var e = FromJson("""{ "pb_name": null, "@odata.type": "#Microsoft.Dynamics.CRM.pb_project", "pb_count@odata.type": "Edm.Int32" }""");

        e.Attributes.Should().ContainKey("pb_name").WhoseValue.Should().BeNull();
        e.Attributes.Should().HaveCount(1);
    }

    [Fact]
    public void Unknown_property_throws_InvalidArgument()
    {
        var act = () => FromJson("""{ "pb_nosuch": 1 }""");

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400 && e.Message.Contains("pb_nosuch"));
    }

    [Theory]
    [InlineData("""{ "pb_count": "seven" }""", "pb_count")]
    [InlineData("""{ "pb_count": 1.5 }""", "pb_count")]
    [InlineData("""{ "pb_name": 3 }""", "pb_name")]
    [InlineData("""{ "pb_startdate": "not a date" }""", "pb_startdate")]
    [InlineData("""{ "pb_startdate": "11/07/2026" }""", "pb_startdate")]
    [InlineData("""{ "pb_startdate": "7 Nov 2026" }""", "pb_startdate")]
    [InlineData("""{ "pb_customerid@odata.bind": "/systemusers({6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e})" }""", "pb_customerid")]
    [InlineData("""{ "pb_customerid@odata.bind": "/systemusers(6f1b5c552a8e4b8e9d553c1a1b2c3d4e)" }""", "pb_customerid")]
    [InlineData("""{ "pb_ratio": 1e400 }""", "pb_ratio")]
    [InlineData("""{ "pb_active": "yes" }""", "pb_active")]
    [InlineData("""{ "_pb_customerid_value": "6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e" }""", "_pb_customerid_value")]
    [InlineData("""{ "pb_customerid": "6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e" }""", "pb_customerid")]
    [InlineData("""{ "pb_name@odata.bind": "/systemusers(6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e)" }""", "pb_name")]
    [InlineData("""{ "pb_customerid@odata.bind": "/nosuchset(6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e)" }""", "nosuchset")]
    [InlineData("""{ "pb_customerid@odata.bind": "/systemusers(notaguid)" }""", "pb_customerid")]
    public void Invalid_values_throw_InvalidArgument_naming_the_property(string json, string named)
    {
        var act = () => FromJson(json);

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400 && e.Message.Contains(named));
    }

    [Theory]
    [InlineData("2026-11-07", "2026-11-07T00:00:00Z")]
    [InlineData("2026-11-07T10:30Z", "2026-11-07T10:30:00Z")]
    [InlineData("2026-11-07T10:30:15Z", "2026-11-07T10:30:15Z")]
    [InlineData("2026-11-07T10:30:15.1234567Z", "2026-11-07T10:30:15.1234567Z")]
    [InlineData("2026-11-07T12:30:15+02:00", "2026-11-07T10:30:15Z")]
    [InlineData("2026-11-07T10:30:15", "2026-11-07T10:30:15Z")]
    public void FromJson_accepts_ISO_8601_dates_as_UTC(string text, string expected)
    {
        var e = FromJson($$"""{ "pb_startdate": "{{text}}" }""");

        var value = e.GetAttributeValue<DateTime>("pb_startdate");
        value.Kind.Should().Be(DateTimeKind.Utc);
        value.Should().Be(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal));
    }
}
