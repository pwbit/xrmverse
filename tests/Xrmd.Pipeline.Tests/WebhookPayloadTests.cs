using System.Runtime.Serialization.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Tests;

public class WebhookPayloadTests
{
    private static readonly string[] TopLevelKeys =
    [
        "BusinessUnitId", "CorrelationId", "Depth", "InitiatingUserId", "InputParameters", "IsExecutingOffline", "IsInTransaction",
        "IsOfflinePlayback", "IsolationMode", "MessageName", "Mode", "OperationCreatedOn", "OperationId", "OrganizationId",
        "OrganizationName", "OutputParameters", "OwningExtension", "ParentContext", "PostEntityImages", "PreEntityImages",
        "PrimaryEntityId", "PrimaryEntityName", "RequestId", "SecondaryEntityName", "SharedVariables", "Stage", "UserId",
    ];

    internal static RemoteExecutionContext UpdateContext(out Entity target)
    {
        target = new Entity("contact", Guid.NewGuid())
        {
            ["fullname"] = "Ada Quill",
            ["birthdate"] = new DateTime(1990, 4, 2, 0, 0, 0, DateTimeKind.Utc),
            ["parentcustomerid"] = new EntityReference("account", Guid.NewGuid()),
        };
        var context = new RemoteExecutionContext
        {
            MessageName = "Update", PrimaryEntityName = "contact", PrimaryEntityId = target.Id, Stage = 40, Mode = 1, Depth = 1,
            UserId = Guid.NewGuid(), InitiatingUserId = Guid.NewGuid(), BusinessUnitId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(), OrganizationName = "contoso", RequestId = Guid.NewGuid(), OperationId = Guid.NewGuid(),
            OperationCreatedOn = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc),
            OwningExtension = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid()),
            ParentContext = new RemoteExecutionContext { MessageName = "Update", Depth = 1 },
        };
        context.InputParameters["Target"] = target;
        context.PreEntityImages["pre"] = new Entity("contact", target.Id) { ["fullname"] = "Ada" };
        context.PostEntityImages["post"] = new Entity("contact", target.Id) { ["fullname"] = "Ada Quill" };
        return context;
    }

    [Fact]
    public void Payload_has_dataverse_shape()
    {
        var context = UpdateContext(out var target);

        var body = WebhookPayload.Serialize(context);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var keys = root.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().Contain(TopLevelKeys);

        var parameter = root.GetProperty("InputParameters")[0];
        parameter.GetProperty("key").GetString().Should().Be("Target");
        var entity = parameter.GetProperty("value");
        entity.GetProperty("__type").GetString().Should().Be("Entity:http://schemas.microsoft.com/xrm/2011/Contracts");

        var attributes = entity.GetProperty("Attributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("key").GetString()!, a => a.GetProperty("value"));
        attributes["parentcustomerid"].GetProperty("__type").GetString()
            .Should().Be("EntityReference:http://schemas.microsoft.com/xrm/2011/Contracts");
        attributes["birthdate"].GetString().Should().MatchRegex(@"^/Date\(-?\d+([+-]\d{4})?\)/$");

        var serializer = new DataContractJsonSerializer(typeof(RemoteExecutionContext),
            new DataContractJsonSerializerSettings { KnownTypes = WebhookPayload.KnownTypes });
        var copy = (RemoteExecutionContext)serializer.ReadObject(new MemoryStream(body))!;
        var t = (Entity)copy.InputParameters["Target"];
        (t.LogicalName, t.Id).Should().Be((target.LogicalName, target.Id));
        t["fullname"].Should().Be("Ada Quill");
        // A DateTime in an object slot carries no __type in DataContractJsonSerializer JSON, so it reads back as its /Date()/ text.
        t["birthdate"].Should().Be(attributes["birthdate"].GetString());
        var milliseconds = long.Parse(((string)t["birthdate"])[6..^2], System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.Should().Be(new DateTime(1990, 4, 2, 0, 0, 0, DateTimeKind.Utc));
        var reference = t.GetAttributeValue<EntityReference>("parentcustomerid");
        (reference.LogicalName, reference.Id).Should().Be(("account", ((EntityReference)target["parentcustomerid"]).Id));
    }

    [Fact]
    public void Small_payload_is_not_trimmed()
    {
        var context = UpdateContext(out _);

        var (body, trimmed) = WebhookPayload.Build(context);

        trimmed.Should().BeFalse();
        body.Should().Equal(WebhookPayload.Serialize(context));
    }

    [Fact]
    public void Oversized_payload_is_trimmed()
    {
        var context = UpdateContext(out var target);
        target["description"] = new string('x', 300 * 1024);
        context.InitiatingUserAgent = "xrm-test-agent";
        context.OutputParameters["id"] = target.Id;
        context.SharedVariables["counter"] = 42;

        var (body, trimmed) = WebhookPayload.Build(context);

        trimmed.Should().BeTrue();
        body.Length.Should().BeLessThan(262144);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        AbsentOrEmpty(root, "InputParameters");
        AbsentOrEmpty(root, "PreEntityImages");
        AbsentOrEmpty(root, "PostEntityImages");
        AbsentOrEmpty(root, "ParentContext");
        root.GetProperty("MessageName").GetString().Should().Be("Update");
        root.GetProperty("PrimaryEntityId").GetString().Should().Be(target.Id.ToString());
        root.GetProperty("OwningExtension").GetProperty("Id").GetString().Should().Be(context.OwningExtension.Id.ToString());
        root.GetProperty("CorrelationId").GetString().Should().Be(context.CorrelationId.ToString());
        root.GetProperty("InitiatingUserAgent").GetString().Should().Be("xrm-test-agent");
        root.GetProperty("OutputParameters")[0].GetProperty("key").GetString().Should().Be("id");
        root.GetProperty("SharedVariables")[0].GetProperty("key").GetString().Should().Be("counter");
        root.GetProperty("SharedVariables")[0].GetProperty("value").GetInt32().Should().Be(42);
        // The caller's context is left as it was.
        context.InputParameters.Should().ContainKey("Target");
        context.ParentContext.Should().NotBeNull();
        context.PostEntityImages.Should().ContainKey("post");
        context.PreEntityImages.Should().ContainKey("pre");
    }

    private static void AbsentOrEmpty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return;
        var empty = value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;
        empty.Should().BeTrue($"{name} is absent or empty in a trimmed body, not {value}");
    }
}
