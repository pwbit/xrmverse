using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Merge;
using Xrmd.Pipeline.Registration;

namespace Xrmd.Pipeline.Tests;

public class ContextTests
{
    private sealed class NullFactory : IOrganizationServiceFactory
    {
        public IOrganizationService CreateOrganizationService(Guid? userId) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(typeof(IPluginExecutionContext))]
    [InlineData(typeof(IPluginExecutionContext2))]
    [InlineData(typeof(IPluginExecutionContext3))]
    [InlineData(typeof(IPluginExecutionContext4))]
    [InlineData(typeof(IPluginExecutionContext5))]
    [InlineData(typeof(IPluginExecutionContext6))]
    [InlineData(typeof(IPluginExecutionContext7))]
    [InlineData(typeof(IExecutionContext))]
    public void ServiceProvider_returns_context_for_every_context_interface(Type t)
    {
        var ctx = new XrmdPluginExecutionContext();
        var sp = new PluginServiceProvider(ctx, new TracingService(), new NullFactory(), new PluginLogger(new TracingService(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        sp.GetService(t).Should().BeSameAs(ctx);
    }

    [Fact]
    public void ServiceProvider_returns_tracing_factory_logger_and_null_for_unknown()
    {
        var tr = new TracingService();
        var f = new NullFactory();
        var l = new PluginLogger(new TracingService(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var sp = new PluginServiceProvider(new XrmdPluginExecutionContext(), tr, f, l);
        sp.GetService(typeof(ITracingService)).Should().BeSameAs(tr);
        sp.GetService(typeof(IOrganizationServiceFactory)).Should().BeSameAs(f);
        sp.GetService(typeof(Microsoft.Xrm.Sdk.PluginTelemetry.ILogger)).Should().BeSameAs(l);
        sp.GetService(typeof(string)).Should().BeNull();
    }

    [Fact]
    public void ServiceProvider_returns_an_environment_variable_service_acting_as_system()
    {
        var factory = new RecordingFactory();
        var sp = new PluginServiceProvider(new XrmdPluginExecutionContext(), new TracingService(), factory,
            new PluginLogger(new TracingService(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        var service = sp.GetService(typeof(IEnvironmentVariableService));
        service.Should().BeOfType<EnvironmentVariableService>();
        sp.GetService(typeof(IEnvironmentVariableService)).Should().BeSameAs(service);
        factory.Requested.Should().Equal([null], "the service reads as the SYSTEM user, once per plug-in run");
    }

    private sealed class RecordingFactory : IOrganizationServiceFactory
    {
        public List<Guid?> Requested { get; } = [];

        public IOrganizationService CreateOrganizationService(Guid? userId)
        {
            Requested.Add(userId);
            return null!;
        }
    }

    [Fact]
    public void Clone_shares_shared_variables_and_parameters()
    {
        var ctx = new XrmdPluginExecutionContext { Stage = 20 };
        var clone = ctx.Clone(40);
        clone.SharedVariables["k"] = 1;
        ctx.SharedVariables["k"].Should().Be(1);
        clone.InputParameters.Should().BeSameAs(ctx.InputParameters);
        clone.OutputParameters.Should().BeSameAs(ctx.OutputParameters);
        clone.Stage.Should().Be(40);
        ctx.Stage.Should().Be(20);
    }

    [Fact]
    public void Clone_has_distinct_images()
    {
        var ctx = new XrmdPluginExecutionContext();
        var clone = ctx.Clone(40);
        clone.PreEntityImages.Should().NotBeSameAs(ctx.PreEntityImages);
        clone.PostEntityImages.Should().NotBeSameAs(ctx.PostEntityImages);
        clone.PreEntityImagesCollection.Should().NotBeSameAs(ctx.PreEntityImagesCollection);
        clone.OwningExtension.Should().NotBeSameAs(ctx.OwningExtension);
    }

    [Fact]
    public void PluginLogger_writes_level_prefixed_line_to_trace()
    {
        var tr = new TracingService();
        var l = new PluginLogger(tr, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        l.LogInformation("hello {0}", "x");
        tr.Text.Should().Be("Information: hello x");
    }

    [Fact]
    public void New_context_has_empty_collections_and_isolation_sandbox()
    {
        var ctx = new XrmdPluginExecutionContext();
        ctx.InputParameters.Should().NotBeNull();
        ctx.OutputParameters.Should().NotBeNull();
        ctx.SharedVariables.Should().NotBeNull();
        ctx.PreEntityImages.Should().NotBeNull();
        ctx.PostEntityImages.Should().NotBeNull();
        ctx.IsolationMode.Should().Be(2);
        ctx.SecondaryEntityName.Should().Be("none");
    }

    // ---- #76, Task 4: the multiple view of a single-record context (ruling B5) and filtering attributes (B7) ----------

    private static XrmdPluginExecutionContext SingleContext(OrganizationRequest request, string message) => new()
    {
        MessageName = message,
        Stage = 20,
        PrimaryEntityName = "account",
        InputParameters = request.Parameters,
    };

    [Fact]
    public void MultipleView_of_a_create_shares_the_target_and_the_shared_variables()
    {
        var target = new Entity("account") { ["name"] = "a" };
        var request = new CreateRequest { Target = target, ["SuppressDuplicateDetection"] = true };
        var single = SingleContext(request, "Create");
        single.SharedVariables["k"] = 1;

        var view = MergedSteps.MultipleView(single, request);

        view.MessageName.Should().Be("CreateMultiple");
        view.InputParameters.Should().NotContainKey("Target");
        view.InputParameters["SuppressDuplicateDetection"].Should().Be(true);
        var targets = view.InputParameters["Targets"].Should().BeOfType<EntityCollection>().Subject;
        targets.EntityName.Should().Be("account");
        targets.Entities.Should().ContainSingle().Which.Should().BeSameAs(target);
        view.SharedVariables.Should().BeSameAs(single.SharedVariables);
        view.Stage.Should().Be(20);
        view.PrimaryEntityName.Should().Be("account");
        view.OutputParameters.Should().BeEmpty();
        // The single-record context itself is left as it was.
        single.MessageName.Should().Be("Create");
        single.InputParameters.Should().ContainKey("Target");
    }

    [Fact]
    public void MultipleView_of_a_created_record_has_its_id_in_Ids()
    {
        var id = Guid.NewGuid();
        var request = new CreateRequest { Target = new Entity("account") };
        var single = SingleContext(request, "Create");
        single.OutputParameters["id"] = id;

        var view = MergedSteps.MultipleView(single, request);

        view.OutputParameters["Ids"].Should().BeEquivalentTo(new[] { id });
        view.OutputParameters.Should().NotContainKey("id");
    }

    [Fact]
    public void MultipleView_of_an_update_is_UpdateMultiple()
    {
        var target = new Entity("account", Guid.NewGuid()) { ["name"] = "a" };
        var request = new UpdateRequest { Target = target };

        var view = MergedSteps.MultipleView(SingleContext(request, "Update"), request);

        view.MessageName.Should().Be("UpdateMultiple");
        ((EntityCollection)view.InputParameters["Targets"]).Entities.Single().Should().BeSameAs(target);
    }

    private static PluginStep Filtered(string message, params string[]? attributes) =>
        new(Guid.NewGuid(), "s", message, "account", 20, 0, 1, attributes, null, null, null, Guid.Empty, "", Guid.Empty, 0, []);

    [Fact]
    public void Applies_matches_a_filter_on_any_target_of_UpdateMultiple()
    {
        var request = new UpdateMultipleRequest
        {
            Targets = new EntityCollection([new Entity("account") { ["emailaddress1"] = "x" }, new Entity("account") { ["name"] = "n" }])
                { EntityName = "account" },
        };
        MergedSteps.Applies(Filtered("UpdateMultiple", "name"), request).Should().BeTrue();
        MergedSteps.Applies(Filtered("UpdateMultiple", "telephone1"), request).Should().BeFalse();
        MergedSteps.Applies(Filtered("UpdateMultiple", null), request).Should().BeTrue();
    }

    [Fact]
    public void Applies_matches_a_filter_on_the_single_target_of_Update()
    {
        var request = new UpdateRequest { Target = new Entity("account") { ["name"] = "n" } };
        MergedSteps.Applies(Filtered("Update", "name"), request).Should().BeTrue();
        MergedSteps.Applies(Filtered("UpdateMultiple", "name"), request).Should().BeTrue();
        MergedSteps.Applies(Filtered("UpdateMultiple", "telephone1"), request).Should().BeFalse();
        // Filters only apply to updates.
        MergedSteps.Applies(Filtered("Create", "telephone1"), new CreateRequest { Target = new Entity("account") }).Should().BeTrue();
    }
}
