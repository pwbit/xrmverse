using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The Web API side of the queue messages (plan 4.1a, task 5), without a database: the bodies translated into the SDK requests,
/// the bindings of the Learn Web API reference (AddToQueue and AddPrincipalToQueue bound to <c>queue</c>, RemoveFromQueue bound to
/// <c>queueitem</c>, RouteTo unbound; task 6: PickFromQueue and ReleaseToQueue bound to <c>queueitem</c>, the function
/// RetrieveUserQueues bound to <c>systemuser</c>), and the operations declared in <c>$metadata</c>.
/// </summary>
public class QueueActionTests
{
    private static readonly Guid QueueId = Guid.Parse("7c1a2b3c-0000-4000-8000-000000000001");
    private static readonly Guid RecordId = Guid.Parse("7c1a2b3c-0000-4000-8000-000000000002");
    private static readonly Guid ItemId = Guid.Parse("7c1a2b3c-0000-4000-8000-000000000003");

    private static EntityMetadata Table(string name, string set, string key, params AttributeMetadata[] more)
    {
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, EntitySetName = set };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(entity, key); // read-only in the SDK
        entity.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = key, SchemaName = key }, .. more]);
        return entity;
    }

    private static FakeStore Store() => new(
        Table("queue", "queues", "queueid"),
        Table("queueitem", "queueitems", "queueitemid", new IntegerAttributeMetadata { LogicalName = "priority", SchemaName = "Priority" }),
        Table("email", "emails", "activityid"),
        Table("systemuser", "systemusers", "systemuserid"),
        Table("team", "teams", "teamid"));

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    private static XrmdFaultException Fault(Action act, int status)
    {
        var fault = FluentActions.Invoking(act).Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(status);
        return fault;
    }

    [Fact]
    public void AddToQueue_reads_the_bound_queue_Target_SourceQueue_and_properties()
    {
        var source = Guid.NewGuid();
        var request = QueueActions.ParseAddToQueue($"queues({QueueId})", Body($$"""
            { "Target": { "@odata.type": "Microsoft.Dynamics.CRM.email", "activityid": "{{RecordId}}" },
              "SourceQueue": { "queueid": "{{source}}" }, "QueueItemProperties": { "priority": 4 } }
            """), Store());

        request.DestinationQueueId.Should().Be(QueueId);
        (request.Target.LogicalName, request.Target.Id).Should().Be(("email", RecordId));
        request.SourceQueueId.Should().Be(source);
        request.QueueItemProperties.LogicalName.Should().Be("queueitem");
        request.QueueItemProperties.GetAttributeValue<int>("priority").Should().Be(4);
    }

    [Fact]
    public void AddToQueue_without_a_source_leaves_it_out()
    {
        var request = QueueActions.ParseAddToQueue($"queues({QueueId})", Body($$"""
            { "Target": { "@odata.type": "#Microsoft.Dynamics.CRM.email", "activityid": "{{RecordId}}" } }
            """), Store());

        request.Parameters.ContainsKey("SourceQueueId").Should().BeFalse();
        request.Parameters.ContainsKey("QueueItemProperties").Should().BeFalse();
    }

    [Theory]
    [InlineData("{}", "Target")]
    [InlineData("""{ "Target": { "activityid": "7c1a2b3c-0000-4000-8000-000000000002" } }""", "Target")]
    [InlineData("""{ "Target": { "@odata.type": "Microsoft.Dynamics.CRM.email" } }""", "activityid")]
    [InlineData("""{ "Target": { "@odata.type": "Microsoft.Dynamics.CRM.email", "activityid": "7c1a2b3c-0000-4000-8000-000000000002" }, "Other": 1 }""", "Other")]
    [InlineData("""{ "Target": { "@odata.type": "Microsoft.Dynamics.CRM.email", "activityid": "7c1a2b3c-0000-4000-8000-000000000002" }, "SourceQueue": { "queueid": "x" } }""", "SourceQueue")]
    public void AddToQueue_wrong_parameters_are_400_naming_them(string json, string named) =>
        Fault(() => QueueActions.ParseAddToQueue($"queues({QueueId})", Body(json), Store()), 400).Message.Should().Contain(named);

    [Fact]
    public void AddToQueue_on_another_entity_set_is_404() =>
        Fault(() => QueueActions.ParseAddToQueue($"emails({RecordId})", Body("{}"), Store()), 404);

    [Fact]
    public void RemoveFromQueue_reads_the_bound_item()
    {
        QueueActions.ParseRemoveFromQueue($"queueitems({ItemId})", Body("{}"), Store()).QueueItemId.Should().Be(ItemId);
        Fault(() => QueueActions.ParseRemoveFromQueue($"queues({QueueId})", Body("{}"), Store()), 404);
        Fault(() => QueueActions.ParseRemoveFromQueue($"queueitems({ItemId})", Body("""{ "Other": 1 }"""), Store()), 400)
            .Message.Should().Contain("Other");
    }

    [Fact]
    public void RouteTo_reads_Target_and_QueueItem()
    {
        var request = QueueActions.ParseRouteTo(Body($$"""
            { "Target": { "@odata.type": "Microsoft.Dynamics.CRM.systemuser", "systemuserid": "{{RecordId}}" },
              "QueueItem": { "queueitemid": "{{ItemId}}" } }
            """), Store());

        (request.Target.LogicalName, request.Target.Id).Should().Be(("systemuser", RecordId));
        request.QueueItemId.Should().Be(ItemId);
        Fault(() => QueueActions.ParseRouteTo(Body($$"""{ "Target": { "@odata.type": "Microsoft.Dynamics.CRM.queue", "queueid": "{{QueueId}}" } }"""), Store()), 400)
            .Message.Should().Contain("QueueItem");
    }

    [Fact]
    public void AddPrincipalToQueue_reads_the_bound_queue_and_Principal()
    {
        var request = QueueActions.ParseAddPrincipalToQueue($"queues({QueueId})", Body($$"""
            { "Principal": { "@odata.type": "Microsoft.Dynamics.CRM.team", "teamid": "{{RecordId}}" } }
            """), Store());

        request.QueueId.Should().Be(QueueId);
        (request.Principal.LogicalName, request.Principal.Id).Should().Be(("team", RecordId));
        Fault(() => QueueActions.ParseAddPrincipalToQueue($"queues({QueueId})", Body("{}"), Store()), 400).Message.Should().Contain("Principal");
    }

    [Fact]
    public void PickFromQueue_reads_the_bound_item_SystemUser_and_RemoveQueueItem()
    {
        var request = QueueActions.ParsePickFromQueue($"queueitems({ItemId})", Body($$"""
            { "SystemUser": { "@odata.type": "Microsoft.Dynamics.CRM.systemuser", "systemuserid": "{{RecordId}}" }, "RemoveQueueItem": true }
            """), Store());

        request.QueueItemId.Should().Be(ItemId);
        request.WorkerId.Should().Be(RecordId);
        request.RemoveQueueItem.Should().BeTrue();
        // The type annotation may be left out: the parameter is a systemuser.
        QueueActions.ParsePickFromQueue($"queueitems({ItemId})", Body($$"""
            { "SystemUser": { "systemuserid": "{{RecordId}}" }, "RemoveQueueItem": false }
            """), Store()).RemoveQueueItem.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{ "RemoveQueueItem": false }""", "SystemUser")]
    [InlineData("""{ "SystemUser": { "systemuserid": "7c1a2b3c-0000-4000-8000-000000000002" } }""", "RemoveQueueItem")]
    [InlineData("""{ "SystemUser": { "systemuserid": "7c1a2b3c-0000-4000-8000-000000000002" }, "RemoveQueueItem": "yes" }""", "RemoveQueueItem")]
    [InlineData("""{ "SystemUser": { "@odata.type": "Microsoft.Dynamics.CRM.team", "teamid": "7c1a2b3c-0000-4000-8000-000000000002" }, "RemoveQueueItem": false }""", "SystemUser")]
    [InlineData("""{ "SystemUser": { "systemuserid": "7c1a2b3c-0000-4000-8000-000000000002" }, "RemoveQueueItem": false, "Other": 1 }""", "Other")]
    public void PickFromQueue_wrong_parameters_are_400_naming_them(string json, string named) =>
        Fault(() => QueueActions.ParsePickFromQueue($"queueitems({ItemId})", Body(json), Store()), 400).Message.Should().Contain(named);

    [Fact]
    public void ReleaseToQueue_reads_the_bound_item()
    {
        QueueActions.ParseReleaseToQueue($"queueitems({ItemId})", Body("{}"), Store()).QueueItemId.Should().Be(ItemId);
        Fault(() => QueueActions.ParseReleaseToQueue($"queues({QueueId})", Body("{}"), Store()), 404);
        Fault(() => QueueActions.ParseReleaseToQueue($"queueitems({ItemId})", Body("""{ "Other": 1 }"""), Store()), 400)
            .Message.Should().Contain("Other");
    }

    [Theory]
    [InlineData("IncludePublic=true", true)]
    [InlineData("IncludePublic=false", false)]
    [InlineData("IncludePublic=@p", true)]
    public void RetrieveUserQueues_reads_the_bound_user_and_IncludePublic(string parameters, bool expected)
    {
        var request = QueueActions.ParseRetrieveUserQueues($"systemusers({RecordId})", parameters, alias => alias == "@p" ? "true" : null, Store());

        request.UserId.Should().Be(RecordId);
        request.IncludePublic.Should().Be(expected);
    }

    [Theory]
    [InlineData("", "IncludePublic")]
    [InlineData("IncludePublic=1", "IncludePublic")]
    [InlineData("IncludePublic=@q", "@q")]
    [InlineData("IncludePublic=true,Other=1", "Other")]
    [InlineData("IncludePublic=true,IncludePublic=false", "IncludePublic")]
    public void RetrieveUserQueues_wrong_parameters_are_400(string parameters, string named) =>
        Fault(() => QueueActions.ParseRetrieveUserQueues($"systemusers({RecordId})", parameters, _ => null, Store()), 400)
            .Message.Should().Contain(named);

    [Fact]
    public void RetrieveUserQueues_on_another_entity_set_is_404() =>
        Fault(() => QueueActions.ParseRetrieveUserQueues($"teams({RecordId})", "IncludePublic=true", _ => null, Store()), 404);

    [Fact]
    public void Queue_action_names_are_reserved_entity_set_names()
    {
        foreach (var name in QueueSchema.Messages)
            LogicalNameValidator.ReservedEntitySetNames.Contains(name.ToLowerInvariant()).Should().BeTrue(name);
        ActivitySchema.RefusedMessages.Should().NotContain(
            ["AddToQueue", "RemoveFromQueue", "RouteTo", "PickFromQueue", "ReleaseToQueue", "RetrieveUserQueues"]);
    }

    [Fact]
    public void Metadata_declares_the_queue_actions()
    {
        var model = new ODataEdmModelBuilder(Store(), TestCustomApis.None()).Build();

        IEdmOperation Single(string name) => model.FindDeclaredOperations("Microsoft.Dynamics.CRM." + name).Should().ContainSingle().Which;
        var add = Single("AddToQueue");
        add.IsBound.Should().BeTrue();
        add.Parameters.Select(p => (p.Name, p.Type.FullName())).Should().Equal(
            ("entity", "Microsoft.Dynamics.CRM.queue"), ("Target", "Microsoft.Dynamics.CRM.crmbaseentity"),
            ("SourceQueue", "Microsoft.Dynamics.CRM.queue"), ("QueueItemProperties", "Microsoft.Dynamics.CRM.queueitem"));
        add.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.AddToQueueResponse");

        var remove = Single("RemoveFromQueue");
        remove.IsBound.Should().BeTrue();
        remove.Parameters.Select(p => p.Type.FullName()).Should().Equal("Microsoft.Dynamics.CRM.queueitem");

        var route = Single("RouteTo");
        route.IsBound.Should().BeFalse();
        route.Parameters.Select(p => (p.Name, p.Type.FullName())).Should().Equal(
            ("Target", "Microsoft.Dynamics.CRM.crmbaseentity"), ("QueueItem", "Microsoft.Dynamics.CRM.queueitem"));
        model.EntityContainer.FindOperationImports("RouteTo").Should().ContainSingle();

        var principal = Single("AddPrincipalToQueue");
        principal.Parameters.Select(p => (p.Name, p.Type.FullName())).Should().Equal(
            ("entity", "Microsoft.Dynamics.CRM.queue"), ("Principal", "Microsoft.Dynamics.CRM.crmbaseentity"));

        var pick = Single("PickFromQueue");
        pick.IsBound.Should().BeTrue();
        pick.Parameters.Select(p => (p.Name, p.Type.FullName())).Should().Equal(
            ("entity", "Microsoft.Dynamics.CRM.queueitem"), ("SystemUser", "Microsoft.Dynamics.CRM.systemuser"), ("RemoveQueueItem", "Edm.Boolean"));
        pick.GetReturn().Should().BeNull();

        var release = Single("ReleaseToQueue");
        release.IsBound.Should().BeTrue();
        release.Parameters.Select(p => p.Type.FullName()).Should().Equal("Microsoft.Dynamics.CRM.queueitem");

        var queues = Single("RetrieveUserQueues");
        queues.Should().BeAssignableTo<IEdmFunction>();
        queues.IsBound.Should().BeTrue();
        queues.Parameters.Select(p => (p.Name, p.Type.FullName())).Should().Equal(
            ("entity", "Microsoft.Dynamics.CRM.systemuser"), ("IncludePublic", "Edm.Boolean"));
        queues.GetReturn().Type.FullName().Should().Be("Collection(Microsoft.Dynamics.CRM.queue)");
    }
}
