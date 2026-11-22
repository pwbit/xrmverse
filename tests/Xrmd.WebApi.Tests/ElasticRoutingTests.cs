using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// Plan 3.4, task 7: the Web API forms of an elastic row's key (<c>(id)</c>, <c>(pk=id,partitionid='p')</c>), the
/// <c>@odata.bind</c> by elastic key, the body of the four bulk actions and the error details annotation.
/// </summary>
public class ElasticRoutingTests
{
    private static readonly Guid Id = Guid.Parse("0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70");

    private static EntityMetadata Entity(string logicalName, string setName, string? tableType, params AttributeMetadata[] attributes)
    {
        var e = new EntityMetadata { LogicalName = logicalName, EntitySetName = setName, OwnershipType = OwnershipTypes.UserOwned, TableType = tableType };
        e.SetAttributes(SystemAttributes.Complete(e, attributes));
        return e;
    }

    private static readonly EntityMetadata Sensor = Entity("new_sensor", "new_sensors", ElasticTables.TableTypeElastic,
        new StringAttributeMetadata { LogicalName = "new_name", MaxLength = 100 },
        new IntegerAttributeMetadata { LogicalName = "new_value" });

    private static readonly EntityMetadata Project = Entity("pb_project", "pb_projects", null,
        new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 100 },
        new LookupAttributeMetadata { LogicalName = "pb_sensorid", Targets = ["new_sensor"] },
        new StringAttributeMetadata { LogicalName = "pb_sensoridpid", MaxLength = 100 });

    private static readonly FakeStore Store = new(Sensor, Project);

    private static XrmdFaultException Refused(Action act) =>
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).Which;

    // ---- keys -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Guid_key_has_no_partition()
    {
        EntitySetController.Resolve(Store, $"new_sensors({Id})").Should().Be((Sensor, (Guid?)Id, (string?)null));
        EntitySetController.Resolve(Store, $"pb_projects({Id})").Should().Be((Project, (Guid?)Id, (string?)null));
        EntitySetController.Resolve(Store, "new_sensors").Should().Be((Sensor, (Guid?)null, (string?)null));
    }

    [Theory]
    [InlineData("new_sensorid={0},partitionid='p1'", "p1")]
    [InlineData("partitionid='p1',new_sensorid={0}", "p1")]
    [InlineData("new_sensorid={0},partitionId='p1'", "p1")]
    [InlineData("new_sensorid={0},partitionid='it''s'", "it's")]
    [InlineData("new_sensorid={0},partitionid='a,b=c'", "a,b=c")]
    [InlineData("new_sensorid={0},partitionid='P1'", "P1")]
    [InlineData("new_sensorid={0}", null)]
    public void Elastic_key_gives_id_and_partition(string key, string? partition)
    {
        var (m, id, p) = EntitySetController.Resolve(Store, $"new_sensors({string.Format(key, Id)})");

        m.Should().BeSameAs(Sensor);
        id.Should().Be(Id);
        p.Should().Be(partition);
    }

    [Theory]
    [InlineData("new_sensorid={0},new_name='x'", "new_name")]
    [InlineData("new_sensorid={0},partitionid='p1',partitionid='p2'", "partitionid")]
    [InlineData("partitionid='p1'", "new_sensorid")]
    [InlineData("new_sensorid={0},partitionid=p1", "partitionid")]
    [InlineData("new_sensorid='{0}',partitionid='p1'", "new_sensorid")]
    [InlineData("new_sensorid={0},partitionid='p1", "partitionid")]
    [InlineData("new_sensorid={0},partitionid='a/b'", "partitionid")]
    public void Invalid_elastic_key_is_400(string key, string named) =>
        Refused(() => EntitySetController.Resolve(Store, $"new_sensors({string.Format(key, Id)})")).Message.Should().Contain(named);

    [Fact]
    public void Compound_key_on_a_standard_table_keeps_the_primary_key_error() =>
        Refused(() => EntitySetController.Resolve(Store, $"pb_projects(pb_projectid={Id},partitionid='p1')"))
            .Message.Should().Contain("only the primary key (a GUID) is supported");

    // ---- @odata.bind ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Bind_by_elastic_key_sets_the_key_attributes()
    {
        var e = ODataJson.FromJson(
            JsonNode.Parse($$"""{ "pb_sensorid@odata.bind": "/new_sensors(new_sensorid={{Id}},partitionid='p1')" }""")!.AsObject(), Project, Store);

        var reference = e.GetAttributeValue<EntityReference>("pb_sensorid");
        reference.LogicalName.Should().Be("new_sensor");
        reference.Id.Should().Be(Id);
        reference.KeyAttributes.Should().BeEquivalentTo(new Dictionary<string, object> { ["new_sensorid"] = Id, ["partitionid"] = "p1" });
    }

    [Fact]
    public void Bind_by_guid_keeps_a_plain_reference()
    {
        var e = ODataJson.FromJson(JsonNode.Parse($$"""{ "pb_sensorid@odata.bind": "new_sensors({{Id}})" }""")!.AsObject(), Project, Store);

        var reference = e.GetAttributeValue<EntityReference>("pb_sensorid");
        reference.Id.Should().Be(Id);
        reference.KeyAttributes.Should().BeEmpty();
    }

    [Fact]
    public void Bind_with_a_compound_key_to_a_standard_table_is_400() =>
        Refused(() => ODataJson.FromJson(
            JsonNode.Parse($$"""{ "pb_sensorid@odata.bind": "/pb_projects(pb_projectid={{Id}},partitionid='p1')" }""")!.AsObject(), Project, Store));

    // ---- bulk actions -----------------------------------------------------------------------------------------------------

    private static OrganizationRequest Bulk(string message, string json) =>
        ElasticEndpoints.ToRequest(message, Sensor, JsonNode.Parse(json)!.AsObject(), Store);

    [Fact]
    public void CreateMultiple_reads_typed_targets()
    {
        var request = Bulk("CreateMultiple", """
            { "Targets": [
              { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor", "new_name": "a", "partitionid": "p1", "ttlinseconds": 60 },
              { "@odata.type": "#Microsoft.Dynamics.CRM.new_sensor", "new_name": "b" } ] }
            """);

        var create = request.Should().BeOfType<CreateMultipleRequest>().Which;
        create.Targets.EntityName.Should().Be("new_sensor");
        create.Targets.Entities.Should().HaveCount(2);
        create.Targets.Entities[0]["partitionid"].Should().Be("p1");
        create.Targets.Entities[0]["ttlinseconds"].Should().Be(60);
        create.Targets.Entities[1]["new_name"].Should().Be("b");
    }

    [Fact]
    public void UpdateMultiple_names_each_row_by_id_and_partition()
    {
        var request = Bulk("UpdateMultiple", $$"""
            { "Targets": [ { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor", "new_sensorid": "{{Id}}", "partitionid": "p1", "new_value": 5 } ] }
            """);

        var target = request.Should().BeOfType<UpdateMultipleRequest>().Which.Targets.Entities.Single();
        target.Id.Should().Be(Id);
        target.KeyAttributes.Should().BeEquivalentTo(new Dictionary<string, object> { ["new_sensorid"] = Id, ["partitionid"] = "p1" });
        target["new_value"].Should().Be(5);
    }

    [Fact]
    public void UpsertMultiple_keeps_the_partition_value()
    {
        var request = Bulk("UpsertMultiple", $$"""
            { "Targets": [ { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor", "new_sensorid": "{{Id}}", "partitionid": "p1" } ] }
            """);

        var target = request.Should().BeOfType<UpsertMultipleRequest>().Which.Targets.Entities.Single();
        target.Id.Should().Be(Id);
        target["partitionid"].Should().Be("p1");
    }

    [Fact]
    public void DeleteMultiple_reads_references_by_key()
    {
        var request = Bulk("DeleteMultiple", $$"""
            { "Targets": [
              { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor", "new_sensorid": "{{Id}}", "partitionid": "p1" },
              { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor", "new_sensorid": "{{Id}}" } ] }
            """);

        request.RequestName.Should().Be("DeleteMultiple");
        var targets = (EntityReferenceCollection)request["Targets"];
        targets.Should().HaveCount(2);
        targets[0].KeyAttributes.Should().BeEquivalentTo(new Dictionary<string, object> { ["new_sensorid"] = Id, ["partitionid"] = "p1" });
        targets[1].Id.Should().Be(Id);
        targets[1].KeyAttributes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CreateMultiple", """{ "Targets": [ { "new_name": "a" } ] }""", "@odata.type")]
    [InlineData("CreateMultiple", """{ "Targets": [ { "@odata.type": "Microsoft.Dynamics.CRM.pb_project", "pb_name": "a" } ] }""", "new_sensor")]
    [InlineData("CreateMultiple", """{ "Targets": {} }""", "Targets")]
    [InlineData("CreateMultiple", """{ }""", "Targets")]
    [InlineData("CreateMultiple", """{ "Targets": [], "Bogus": 1 }""", "Bogus")]
    [InlineData("DeleteMultiple", """{ "Targets": [ { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor", "new_name": "a" } ] }""", "new_name")]
    [InlineData("DeleteMultiple", """{ "Targets": [ { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor" } ] }""", "new_sensorid")]
    [InlineData("CreateMultiple", """{ "Targets": [ { "@odata.type": "Microsoft.Dynamics.CRM.new_sensor", "new_sensor_tasks": [ { "subject": "x" } ] } ] }""", "deep insert")]
    public void Invalid_bulk_body_is_400(string message, string json, string named) =>
        Refused(() => Bulk(message, json)).Message.Should().Contain(named);

    // ---- error details annotation -----------------------------------------------------------------------------------------

    private static async Task<JsonNode> ErrorAsync(string? prefer)
    {
        var fault = XrmdFaultException.InvalidArgument("bulk failed");
        fault.ErrorDetails["Plugin.BulkApiErrorDetails"] = """[{"RequestIndex":1,"Id":"","StatusCode":400}]""";
        var context = new DefaultHttpContext();
        if (prefer is not null) context.Request.Headers["Prefer"] = prefer;
        context.Response.Body = new MemoryStream();
        await new ODataErrorMiddleware(_ => throw fault, NullLogger<ODataErrorMiddleware>.Instance).InvokeAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonNode.ParseAsync(context.Response.Body))!["error"]!;
    }

    private const string Annotation = "@Microsoft.PowerApps.CDS.ErrorDetails.Plugin.BulkApiErrorDetails";

    [Theory]
    [InlineData("odata.include-annotations=\"*\"")]
    [InlineData("odata.include-annotations=*")]
    [InlineData("odata.include-annotations=\"Microsoft.PowerApps.CDS.ErrorDetails.*\"")]
    [InlineData("return=representation, odata.include-annotations=\"OData.Community.Display.V1.FormattedValue,Microsoft.PowerApps.CDS.ErrorDetails.*\"")]
    public async Task Error_details_are_annotated_when_asked(string prefer)
    {
        var error = await ErrorAsync(prefer);

        error["code"]!.GetValue<string>().Should().Be("0x80040203");
        error["message"]!.GetValue<string>().Should().Be("bulk failed");
        error[Annotation]!.GetValue<string>().Should().Be("""[{"RequestIndex":1,"Id":"","StatusCode":400}]""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"")]
    [InlineData("return=representation")]
    public async Task Error_details_are_not_written_otherwise(string? prefer)
    {
        var error = await ErrorAsync(prefer);

        error.AsObject().Select(p => p.Key).Should().Equal("code", "message");
    }
}
