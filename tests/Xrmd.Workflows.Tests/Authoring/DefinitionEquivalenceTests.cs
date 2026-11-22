using FluentAssertions;
using Xrmd.Workflows.Authoring;
using Xrmd.Workflows.Xaml;
using static Xrmd.Workflows.Xaml.ValueExpr;

namespace Xrmd.Workflows.Tests.Authoring;

/// <summary>
/// The comparison of the recompile check (plan 2.21, A12): it ignores the paths, the generated variable names and the generated part
/// of the display names, and reports anything else, naming the step.
/// </summary>
public class DefinitionEquivalenceTests
{
    private static WorkflowDefinition Definition(params WorkflowStep[] steps) => new(0, "xrmv_order", [], new SequenceStep("0", null, steps));

    private static OrderedDictionary<string, ValueExpr> Values(params (string Column, ValueExpr Value)[] values)
    {
        var dictionary = new OrderedDictionary<string, ValueExpr>(StringComparer.Ordinal);
        foreach (var (column, value) in values)
            dictionary.Add(column, value);
        return dictionary;
    }

    [Fact]
    public void Ignores_paths_and_generated_names()
    {
        var expected = Definition(
            new CreateStep("0/0", "CreateStep1: a task", "task", Values(("subject", new Literal("String", "Call"))), "CreateStep1_localParameter"),
            new UpdateStep("0/1", "UpdateStep2", new RecordRef.Created("CreateStep1_localParameter"),
                Values(("description", new AttributeOf(new RecordRef.Created("CreateStep1_localParameter"), "subject") { TargetType = "System.String" }))),
            new CustomActivityStep("0/2", "CustomActivityStep3", "Example.Activities.Normalize", "Example.Activities",
                new Dictionary<string, ValueExpr>(), new Dictionary<string, string> { ["Normalized"] = "CustomActivityStep3_clean" }),
            new UpdateStep("0/3", "UpdateStep4", new RecordRef.Primary(), Values(("xrmv_name", new VariableRef("CustomActivityStep3_clean")))));
        var actual = Definition(
            new CreateStep("0/5/1", "CreateStep7: a task", "task", Values(("subject", new Literal("String", "Call"))), "created1"),
            new UpdateStep("0/6", "UpdateStep8", new RecordRef.Created("created1"),
                Values(("description", new AttributeOf(new RecordRef.Created("created1"), "subject") { TargetType = "System.String" }))),
            new CustomActivityStep("0/7", "CustomActivityStep9", "Example.Activities.Normalize", "Example.Activities",
                new Dictionary<string, ValueExpr>(), new Dictionary<string, string> { ["Normalized"] = "CustomActivityStep9_clean" }),
            new UpdateStep("0/8", "UpdateStep10", new RecordRef.Primary(), Values(("xrmv_name", new VariableRef("CustomActivityStep9_clean")))));

        DefinitionEquivalence.Differences(expected, actual).Should().BeEmpty();
    }

    [Fact]
    public void Reports_a_changed_literal()
    {
        var expected = Definition(new CreateStep("0/0", "CreateStep1", "task", Values(("subject", new Literal("String", "Call"))), "CreateStep1_localParameter"));
        var actual = Definition(new CreateStep("0/0", "CreateStep1", "task", Values(("subject", new Literal("String", "Mail"))), "CreateStep1_localParameter"));

        var differences = DefinitionEquivalence.Differences(expected, actual);

        differences.Should().ContainSingle().Which.Should().Contain("CreateStep1").And.Contain("subject").And.Contain("Call").And.Contain("Mail");
    }

    [Fact]
    public void Reports_a_missing_branch()
    {
        var when = new ConditionExpr.Compare("Null", new AttributeOf(new RecordRef.Primary(), "xrmv_amount"), null);
        var stop = new StopStep("0/0/1/then/0", "StopWorkflowStep3", Canceled: true, Message: null);
        var expected = Definition(new ConditionStep("0/0", "ConditionStep1", [
            new ConditionBranch(when, new SequenceStep("0/0/1/then", "ConditionBranchStep2", [stop])),
            new ConditionBranch(new ConditionExpr.Not(when), new SequenceStep("0/0/2/then", "ConditionBranchStep4", [])),
        ], null));
        var actual = Definition(new ConditionStep("0/0", "ConditionStep1", [
            new ConditionBranch(when, new SequenceStep("0/0/1/then", "ConditionBranchStep2", [stop])),
        ], null));

        var differences = DefinitionEquivalence.Differences(expected, actual);

        differences.Should().ContainSingle().Which.Should().Contain("ConditionStep1").And.Contain("2 branch").And.Contain("1");
    }

    [Fact]
    public void Compares_description_part_of_display_names()
    {
        static WorkflowDefinition Stop(string? displayName) => Definition(new StopStep("0/0", displayName, Canceled: false, Message: null));

        DefinitionEquivalence.Differences(Stop("StopWorkflowStep1"), Stop("StopWorkflowStep4")).Should().BeEmpty();
        DefinitionEquivalence.Differences(Stop("StopWorkflowStep1: done"), Stop("StopWorkflowStep9: done")).Should().BeEmpty();
        DefinitionEquivalence.Differences(Stop("StopWorkflowStep1: done"), Stop("StopWorkflowStep1: finished"))
            .Should().ContainSingle().Which.Should().Contain("done").And.Contain("finished");
        DefinitionEquivalence.Differences(Stop("StopWorkflowStep1: done"), Stop("StopWorkflowStep1")).Should().ContainSingle();
        // A display name the designer did not generate is all description.
        DefinitionEquivalence.Differences(Stop("Stop here"), Stop("StopWorkflowStep1: Stop here")).Should().BeEmpty();

        // The description of a business rule's condition is its branches' Description (Task 3).
        var when = new ConditionExpr.Compare("Null", new AttributeOf(new RecordRef.Primary(), "xrmv_amount"), null);
        WorkflowDefinition Rule(string? description) => new(2, "xrmv_order", [], new SequenceStep("0", null, [
            new ConditionStep("0/0", "ConditionStep1", [
                new ConditionBranch(when, new SequenceStep("0/0/1/then", "ConditionBranchStep2", [])) { Description = description },
            ], null),
        ]));
        DefinitionEquivalence.Differences(Rule("New Condition"), Rule("New Condition")).Should().BeEmpty();
        DefinitionEquivalence.Differences(Rule("New Condition"), Rule(null)).Should().ContainSingle().Which.Should().Contain("New Condition");
    }
}
