using System.Text;
using FluentAssertions;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Tests;

/// <summary>Hostile or unexpected XAML: the parser fails explicitly (exception or unsupported step), never crashes.</summary>
public class ParserRobustnessTests
{
    private const string Crm = ", Microsoft.Crm.Workflow";

    private static string Wrap(string body, string members = "") => $"""
        <Activity x:Class="XrmWorkflowTest"
                  xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:mxs="clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0"
                  xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0"
                  xmlns:sco="clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, Version=4.0.0.0"
                  xmlns:ex="clr-namespace:Example.Workflows.Activities;assembly=Example.Workflows"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          {members}
          <mxswa:Workflow>
        {body}
          </mxswa:Workflow>
        </Activity>
        """;

    private static string Step(string activities, string name = "Step1") => $"""
        <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.StepComposite{Crm}" DisplayName="{name}">
          <mxswa:ActivityReference.Properties>
            <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
        {activities}
            </sco:Collection>
          </mxswa:ActivityReference.Properties>
        </mxswa:ActivityReference>
        """;

    private const string NameIsNull = """
        <mxswa:GetEntityProperty Attribute="name" Entity="[InputEntities(&quot;primaryEntity&quot;)]" EntityName="account" Value="[V1]" />
        <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.EvaluateCondition, Microsoft.Crm.Workflow">
          <mxswa:ActivityReference.Arguments>
            <InArgument x:TypeArguments="x:String" x:Key="ConditionOperator">Null</InArgument>
            <InArgument x:TypeArguments="x:Object" x:Key="Operand">[V1]</InArgument>
            <OutArgument x:TypeArguments="x:Boolean" x:Key="Result">[C1]</OutArgument>
          </mxswa:ActivityReference.Arguments>
        </mxswa:ActivityReference>
        """;

    private static string Branch(string condition, string then, string @else = "<x:Null x:Key=\"Else\" />") => $"""
        <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.ConditionBranch{Crm}">
          <mxswa:ActivityReference.Arguments>
            {condition}
          </mxswa:ActivityReference.Arguments>
          <mxswa:ActivityReference.Properties>
            <mxswa:ActivityReference x:Key="Then" AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.Composite{Crm}">
              <mxswa:ActivityReference.Properties>
                <sco:Collection x:TypeArguments="Activity" x:Key="Activities">{then}</sco:Collection>
              </mxswa:ActivityReference.Properties>
            </mxswa:ActivityReference>
            {@else}
          </mxswa:ActivityReference.Properties>
        </mxswa:ActivityReference>
        """;

    private static string ConditionSequence(bool wait, string activities, string key = "") => $"""
        <mxswa:ActivityReference {key} AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.ConditionSequence{Crm}" DisplayName="Cond">
          <mxswa:ActivityReference.Arguments>
            <InArgument x:TypeArguments="x:Boolean" x:Key="Wait">{(wait ? "True" : "False")}</InArgument>
          </mxswa:ActivityReference.Arguments>
          <mxswa:ActivityReference.Properties>
            <sco:Collection x:TypeArguments="Activity" x:Key="Activities">{activities}</sco:Collection>
          </mxswa:ActivityReference.Properties>
        </mxswa:ActivityReference>
        """;

    private const string Condition = """<InArgument x:TypeArguments="x:Boolean" x:Key="Condition">[C1]</InArgument>""";

    /// <summary>Parsed as with the workflow SDK installed, so that a custom activity's own shape is what is checked.</summary>
    private static UnsupportedStep SingleUnsupported(string xaml) =>
        XamlWorkflowParser.Parse(xaml, 0, workflowSdkAvailable: true).Unsupported.Should().ContainSingle().Subject;

    [Fact]
    public void Doctype_is_refused()
    {
        var xaml = """
            <!DOCTYPE Activity [ <!ENTITY a "aaaaaaaaaa"> <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;"> ]>
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities">&b;</Activity>
            """;

        var act = () => XamlWorkflowParser.Parse(xaml, 0);

        act.Should().Throw<XamlWorkflowException>().Which.Message.Should().Contain("DTD").And.NotContain("line 0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Empty_xaml_is_refused_with_a_clear_message(string xaml)
    {
        var act = () => XamlWorkflowParser.Parse(xaml, 0);

        act.Should().Throw<XamlWorkflowException>().Which.Message.Should().Contain("empty").And.NotContain("line 0");
    }

    [Fact]
    public void Nested_else_that_is_a_wait_is_unsupported()
    {
        var nestedWait = ConditionSequence(wait: true, Branch(Condition, ""), key: "x:Key=\"Else\"");
        var xaml = Wrap(ConditionSequence(wait: false, NameIsNull + Branch(Condition, "", nestedWait)));

        var step = SingleUnsupported(xaml);

        step.Path.Should().Be("0/0");
        step.Reason.Should().Contain("wait");
    }

    [Fact]
    public void Custom_activity_property_with_two_arguments_is_unsupported()
    {
        var xaml = Wrap(Step("""
            <ex:Echo>
              <ex:Echo.Text>
                <InArgument x:TypeArguments="x:String">a</InArgument>
                <InArgument x:TypeArguments="x:String">b</InArgument>
              </ex:Echo.Text>
            </ex:Echo>
            """));

        SingleUnsupported(xaml).Reason.Should().Contain("Text");
    }

    [Fact]
    public void Deeply_nested_expression_is_unsupported()
    {
        var expression = "[" + string.Concat(Enumerable.Repeat("f(", 5000)) + "x" + new string(')', 5000) + "]";
        var xaml = Wrap(Step($"""<mxswa:Postpone PostponeUntil="{expression}" />"""));

        var step = SingleUnsupported(xaml);

        step.Reason.Length.Should().BeLessThan(400);
    }

    [Fact]
    public void Expression_parser_limits_nesting()
    {
        var act = () => ExpressionParser.Parse(string.Concat(Enumerable.Repeat("f(", 100)) + "x" + new string(')', 100));

        act.Should().Throw<FormatException>().WithMessage("*nested*");
        ExpressionParser.Parse(string.Concat(Enumerable.Repeat("f(", 20)) + "x" + new string(')', 20)).Should().NotBeNull();
    }

    [Fact]
    public void Deeply_nested_xml_is_refused()
    {
        var body = new StringBuilder();
        for (var i = 0; i < 5000; i++)
            body.Append("<Sequence>");
        for (var i = 0; i < 5000; i++)
            body.Append("</Sequence>");

        var act = () => XamlWorkflowParser.Parse(Wrap(body.ToString()), 0);

        act.Should().Throw<XamlWorkflowException>().WithMessage("*nest*");
    }

    [Fact]
    public void Depth_first_walk_handles_deep_trees()
    {
        WorkflowStep step = new UnsupportedStep("leaf", null, "X", "r");
        for (var i = 0; i < 100_000; i++)
            step = new SequenceStep("s", null, [step]);

        new WorkflowDefinition(0, null, [], step).Unsupported.Should().ContainSingle().Which.Path.Should().Be("leaf");
    }

    [Theory]
    [InlineData("InOutArgument(x:String)")]
    [InlineData("x:String")]
    public void Member_that_is_not_an_in_or_out_argument_is_unsupported(string type)
    {
        var xaml = Wrap("", $"""<x:Members><x:Property Name="Odd" Type="{type}" /></x:Members>""");

        var d = XamlWorkflowParser.Parse(xaml, 3);

        var step = d.Unsupported.Should().ContainSingle().Subject;
        step.Path.Should().Be("x:Members/Odd");
        step.Element.Should().Be("x:Property");
        step.Reason.Should().Contain(type);
        d.Arguments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CreatedEntities(&quot;A&quot;)", "CreatedEntities(&quot;B#Temp&quot;)")]
    // InputEntities("primaryEntity") = CreatedEntities("primaryEntity#Temp") is the write-back of the parity export (supported).
    [InlineData("UpdatedEntities(&quot;primaryEntity&quot;)", "CreatedEntities(&quot;primaryEntity#Temp&quot;)")]
    [InlineData("UpdatedEntities(&quot;primaryEntity&quot;)", "InputEntities(&quot;primaryEntity&quot;)")]
    public void Entity_copies_outside_the_known_shapes_are_unsupported(string to, string value)
    {
        var xaml = Wrap(Step($"""<Assign x:TypeArguments="mxs:Entity" To="[{to}]" Value="[{value}]" />"""));

        SingleUnsupported(xaml).Reason.Should().Contain("assignment");
    }

    [Theory]
    [InlineData("CreatedEntities(&quot;A&quot;)", "CreatedEntities(&quot;A#Temp&quot;)")]
    [InlineData("InputEntities(&quot;primaryEntity&quot;)", "UpdatedEntities(&quot;primaryEntity#Temp&quot;)")]
    public void Known_entity_copies_are_bookkeeping(string to, string value)
    {
        var xaml = Wrap(Step($"""<Assign x:TypeArguments="mxs:Entity" To="[{to}]" Value="[{value}]" />"""));

        XamlWorkflowParser.Parse(xaml, 0).Unsupported.Should().BeEmpty();
    }

    [Fact]
    public void Timeout_branch_with_a_condition_is_unsupported()
    {
        var postpone = """<mxswa:Postpone PostponeUntil="[V1]" />""";
        var xaml = Wrap(ConditionSequence(wait: true, NameIsNull + Branch(Condition, "") + Branch(Condition, postpone)));

        SingleUnsupported(xaml).Reason.Should().Contain("timeout branch");
    }

    [Fact]
    public void Timeout_branch_without_a_condition_is_a_wait()
    {
        var postpone = """<mxswa:Postpone PostponeUntil="[V1]" />""";
        var xaml = Wrap(ConditionSequence(wait: true, NameIsNull + Branch(Condition, "") + Branch("", postpone)));

        var d = XamlWorkflowParser.Parse(xaml, 0);

        d.Unsupported.Should().BeEmpty();
        d.Root.DepthFirst().OfType<WaitStep>().Should().ContainSingle().Which.Timeout.Should().NotBeNull();
    }

    [Fact]
    public void Long_expression_text_is_truncated_in_the_reason()
    {
        var expression = "[" + string.Join(" + ", Enumerable.Repeat("SomeLongVariableName", 100)) + "]";
        var xaml = Wrap(Step($"""<mxswa:Postpone PostponeUntil="{expression}" />"""));

        var reason = SingleUnsupported(xaml).Reason;

        reason.Length.Should().BeLessThan(400);
        reason.Should().Contain("…");
    }
}
