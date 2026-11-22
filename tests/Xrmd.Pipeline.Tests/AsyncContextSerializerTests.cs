using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;

namespace Xrmd.Pipeline.Tests;

public class AsyncContextSerializerTests
{
    [Fact]
    public void Round_trip_keeps_parameters_images_and_shared_variables()
    {
        var owner = new EntityReference("systemuser", Guid.NewGuid()) { Name = "Owner" };
        var when = new DateTime(2026, 11, 7, 10, 30, 15, DateTimeKind.Utc);
        var target = new Entity("account", Guid.NewGuid())
        {
            ["name"] = "Acme",
            ["ownerid"] = owner,
            ["industrycode"] = new OptionSetValue(7),
            ["revenue"] = new Money(1234.56m),
            ["lastonholdtime"] = when,
        };
        var context = new XrmdPluginExecutionContext
        {
            MessageName = "Create", PrimaryEntityName = "account", PrimaryEntityId = target.Id, Stage = 40, Mode = 1, Depth = 2,
            UserId = Guid.NewGuid(), InitiatingUserId = Guid.NewGuid(), BusinessUnitId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
            OwningExtension = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid()),
            ParentContext = new XrmdPluginExecutionContext { MessageName = "Update", Depth = 1 },
        };
        context.InputParameters["Target"] = target;
        context.OutputParameters["id"] = target.Id;
        context.SharedVariables["counter"] = 42;
        context.SharedVariables["label"] = "x";
        context.PreEntityImages["pre"] = new Entity("account", target.Id) { ["name"] = "Old", ["industrycode"] = new OptionSetValue(1) };
        context.PostEntityImages["post"] = new Entity("account", target.Id) { ["revenue"] = new Money(9m), ["ownerid"] = owner };

        var copy = AsyncContextSerializer.Deserialize(AsyncContextSerializer.Serialize(AsyncContextSerializer.ToRemote(context)));

        copy.MessageName.Should().Be("Create");
        (copy.Stage, copy.Mode, copy.Depth).Should().Be((40, 1, 2));
        (copy.UserId, copy.InitiatingUserId, copy.BusinessUnitId, copy.CorrelationId)
            .Should().Be((context.UserId, context.InitiatingUserId, context.BusinessUnitId, context.CorrelationId));
        copy.OwningExtension.Id.Should().Be(context.OwningExtension.Id);
        copy.ParentContext.MessageName.Should().Be("Update");

        var t = (Entity)copy.InputParameters["Target"];
        (t.LogicalName, t.Id).Should().Be(("account", target.Id));
        t["name"].Should().Be("Acme");
        var o = t.GetAttributeValue<EntityReference>("ownerid");
        (o.LogicalName, o.Id, o.Name).Should().Be(("systemuser", owner.Id, "Owner"));
        t.GetAttributeValue<OptionSetValue>("industrycode").Value.Should().Be(7);
        t.GetAttributeValue<Money>("revenue").Value.Should().Be(1234.56m);
        var d = t.GetAttributeValue<DateTime>("lastonholdtime");
        d.Should().Be(when);
        d.Kind.Should().Be(DateTimeKind.Utc);

        copy.OutputParameters["id"].Should().Be(target.Id);
        copy.SharedVariables["counter"].Should().Be(42);
        copy.SharedVariables["label"].Should().Be("x");
        copy.PreEntityImages["pre"]["name"].Should().Be("Old");
        copy.PreEntityImages["pre"].GetAttributeValue<OptionSetValue>("industrycode").Value.Should().Be(1);
        copy.PostEntityImages["post"].GetAttributeValue<Money>("revenue").Value.Should().Be(9m);
        copy.PostEntityImages["post"].GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(owner.Id);
    }

    [Fact]
    public void Serialized_context_is_a_snapshot()
    {
        var context = new XrmdPluginExecutionContext { MessageName = "Create" };
        context.SharedVariables["v"] = 1;
        var xml = AsyncContextSerializer.Serialize(AsyncContextSerializer.ToRemote(context));
        context.SharedVariables["v"] = 2;

        AsyncContextSerializer.Deserialize(xml).SharedVariables["v"].Should().Be(1);
    }
}
