using System.Activities;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Workflows.Custom;
using Xrmd.Workflows.Engine;
using Xrmd.Workflows.Xaml;

// Stand-ins of the workflow SDK's types, of the same full names: the runner reads them by name only (the SDK is not referenced here).
namespace Microsoft.Xrm.Sdk.Workflow
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class InputAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class OutputAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ReferenceTargetAttribute(string entityName) : Attribute
    {
        public string EntityName { get; } = entityName;
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class DefaultAttribute(string value) : Attribute
    {
        public string Value { get; } = value;
    }

    public enum XrmWorkflowCompletionStatus
    {
        Succeeded = 0,
        Canceled = 1,
        Failed = 2,
        Retry = 3,
        Suspended = 4,
    }

    public class StopWorkflowException(XrmWorkflowCompletionStatus status, string message) : Exception(message)
    {
        public XrmWorkflowCompletionStatus Status { get; } = status;
    }

    public sealed class DerivedStopWorkflowException(XrmWorkflowCompletionStatus status, string message) : StopWorkflowException(status, message);
}

namespace Xrmd.Workflows.Tests
{
    using Microsoft.Xrm.Sdk.Workflow;

    /// <summary>An activity with one argument of each shape the runner checks.</summary>
    public sealed class ArgumentsActivity : CodeActivity
    {
        [Input("Count")] public InArgument<int> Count { get; set; } = null!;

        [Input("Amount")] public InArgument<decimal?> Amount { get; set; } = null!;

        [Input("Text")] public InArgument<string> Text { get; set; } = null!;

        [Input("Account"), ReferenceTarget("account")] public InArgument<EntityReference> Account { get; set; } = null!;

        [Input("Defaulted"), Default("7")] public InArgument<int> Defaulted { get; set; } = null!;

        [RequiredArgument, Input("Needed")] public InArgument<string> Needed { get; set; } = null!;

        public InArgument<string> NoAttribute { get; set; } = null!;

        [Output("Result")] public OutArgument<string> Result { get; set; } = null!;

        protected override void Execute(CodeActivityContext context) { }
    }

    /// <summary>A required argument without <c>[Input]</c>, which no step can set.</summary>
    public sealed class HiddenRequiredActivity : CodeActivity
    {
        [RequiredArgument] public InArgument<string> Hidden { get; set; } = null!;

        protected override void Execute(CodeActivityContext context) { }
    }

    /// <summary>Arguments a step can set.</summary>
    public sealed class SettableActivity : CodeActivity
    {
        [Input("Count")] public InArgument<int> Count { get; set; } = null!;

        [Input("Account"), ReferenceTarget("account")] public InArgument<EntityReference> Account { get; set; } = null!;

        [RequiredArgument, Input("Needed")] public InArgument<string> Needed { get; set; } = null!;

        protected override void Execute(CodeActivityContext context) { }
    }

    /// <summary>
    /// Plan 2.14, Task 8b (W17, fix I3): the checks of a custom workflow activity's inputs, which convert no value and fail with a 400
    /// InvalidArgument naming the argument; and the outcome of a <c>StopWorkflowException</c> (fix I1).
    /// </summary>
    public sealed class CustomActivityArgumentTests
    {
        private static readonly WorkflowRun Run = new()
        {
            Workflow = new WorkflowRow { Id = Guid.NewGuid(), Name = "Flow", Category = 0 },
            Primary = new EntityReference("account", Guid.NewGuid()),
            Caller = new Caller(Guid.NewGuid(), Guid.NewGuid()),
            Invocation = new InvocationContext(new Caller(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), 1, null, null, Guid.NewGuid()),
        };

        private static readonly CustomActivityStep Step = new("0", "Custom", typeof(ArgumentsActivity).FullName!, "Test",
            new Dictionary<string, ValueExpr>(), new Dictionary<string, string>());

        private static Dictionary<string, object> Inputs<T>(Dictionary<string, object?> values) where T : Activity =>
            CustomActivityRunner.Inputs(Step, values, CustomActivityRunner.Arguments(typeof(T)), Run, typeof(T));

        private static void ShouldRefuse<T>(Dictionary<string, object?> values, params string[] fragments) where T : Activity
        {
            var act = () => Inputs<T>(values);
            var fault = act.Should().Throw<XrmdFaultException>().Which;
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().StartWith("Workflow 'Flow': the step '0 (Custom)': ");
            foreach (var fragment in fragments) fault.Message.Should().Contain(fragment);
        }

        [Fact]
        public void Arguments_reads_the_attributes_by_full_name()
        {
            var arguments = CustomActivityRunner.Arguments(typeof(ArgumentsActivity));

            arguments["Count"].Should().Match<CustomActivityRunner.ActivityArgument>(a => a.IsInput && !a.Required && a.ValueType == typeof(int));
            arguments["Account"].ReferenceTarget.Should().Be("account");
            arguments["Defaulted"].HasDefault.Should().BeTrue();
            arguments["Needed"].Should().Match<CustomActivityRunner.ActivityArgument>(a => a.IsInput && a.Required);
            CustomActivityRunner.Arguments(typeof(HiddenRequiredActivity))["Hidden"]
                .Should().Match<CustomActivityRunner.ActivityArgument>(a => !a.IsInput && a.Required);
            arguments["NoAttribute"].IsInput.Should().BeFalse();
            arguments["Result"].Should().Match<CustomActivityRunner.ActivityArgument>(a => a.IsOutput && !a.IsInput && !a.Required);
        }

        [Fact]
        public void Values_of_the_argument_types_pass_unchanged()
        {
            var account = new EntityReference("account", Guid.NewGuid());

            var inputs = Inputs<SettableActivity>(new() { ["Count"] = 3, ["Account"] = account, ["Needed"] = "x" });

            inputs.Should().BeEquivalentTo(new Dictionary<string, object> { ["Count"] = 3, ["Account"] = account, ["Needed"] = "x" });
        }

        [Fact]
        public void A_value_of_another_type_is_refused_not_converted() =>
            ShouldRefuse<SettableActivity>(new() { ["Count"] = "3", ["Needed"] = "x" },
                "'Count'", "is of type System.Int32", "its value is of type System.String", "converts no value");

        [Fact]
        public void A_reference_to_another_table_is_refused() =>
            ShouldRefuse<SettableActivity>(new() { ["Account"] = new EntityReference("contact", Guid.NewGuid()), ["Needed"] = "x" },
                "'Account'", "takes a reference to 'account', not to 'contact'");

        [Fact]
        public void An_argument_the_activity_does_not_have_is_refused() =>
            ShouldRefuse<SettableActivity>(new() { ["Missing"] = 1, ["Needed"] = "x" }, "has no input argument 'Missing'");

        [Fact]
        public void An_InArgument_without_Input_cannot_be_set() =>
            ShouldRefuse<ArgumentsActivity>(new() { ["NoAttribute"] = "x" }, "has no input argument 'NoAttribute'");

        [Fact]
        public void An_empty_value_for_a_value_type_is_refused() =>
            ShouldRefuse<SettableActivity>(new() { ["Count"] = null, ["Needed"] = "x" },
                "'Count'", "is empty", "System.Int32 cannot be empty");

        [Fact]
        public void An_empty_value_for_a_type_that_can_be_empty_passes()
        {
            var inputs = Inputs<SettableActivity>(new() { ["Account"] = null, ["Needed"] = "x" });

            inputs.Should().ContainKey("Account").WhoseValue.Should().BeNull();
        }

        [Fact]
        public void An_unset_required_input_is_refused() =>
            ShouldRefuse<SettableActivity>(new(), "does not set the required input argument 'Needed'");

        [Fact]
        public void An_unset_required_argument_without_Input_is_refused_with_its_cause() =>
            ShouldRefuse<HiddenRequiredActivity>(new(), "required input argument 'Hidden'", "has no [Input] attribute");

        [Fact]
        public void An_unset_input_with_a_Default_is_refused() =>
            ShouldRefuse<ArgumentsActivity>(new() { ["Needed"] = "x" }, "'Defaulted'", "does not apply the [Default] value");

        [Theory]
        [InlineData(XrmWorkflowCompletionStatus.Succeeded, typeof(WorkflowOutcome.Completed))]
        [InlineData(XrmWorkflowCompletionStatus.Canceled, typeof(WorkflowOutcome.Canceled))]
        public void A_StopWorkflowException_stops_the_run(XrmWorkflowCompletionStatus status, Type outcome)
        {
            var stopped = CustomActivityRunner.Stopped(new StopWorkflowException(status, "Why"));

            stopped.Should().BeOfType(outcome);
            if (stopped is WorkflowOutcome.Canceled canceled) canceled.Message.Should().Be("Why");
        }

        [Fact]
        public void A_derived_StopWorkflowException_is_recognised() =>
            CustomActivityRunner.Stopped(new DerivedStopWorkflowException(XrmWorkflowCompletionStatus.Canceled, "Why"))
                .Should().Be(new WorkflowOutcome.Canceled("Why"));

        [Fact]
        public void A_failed_StopWorkflowException_is_the_abort()
        {
            var act = () => CustomActivityRunner.Stopped(new StopWorkflowException(XrmWorkflowCompletionStatus.Failed, "Why"));

            var failure = act.Should().Throw<PluginExecutionException>().Which;
            failure.Unexpected.Should().BeFalse();
            failure.Fault.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
            failure.Fault.Message.Should().Be("Why");
        }

        [Theory]
        [InlineData(XrmWorkflowCompletionStatus.Retry)]
        [InlineData(XrmWorkflowCompletionStatus.Suspended)]
        public void A_StopWorkflowException_of_another_status_fails_explicitly(XrmWorkflowCompletionStatus status)
        {
            var act = () => CustomActivityRunner.Stopped(new StopWorkflowException(status, "Why"));

            var fault = act.Should().Throw<PluginExecutionException>().Which.Fault;
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            fault.Message.Should().Contain($"'{status}'").And.Contain("does not support");
        }

        [Fact]
        public void Any_other_exception_is_not_a_stop() =>
            CustomActivityRunner.Stopped(new InvalidOperationException("x")).Should().BeNull();
    }
}
