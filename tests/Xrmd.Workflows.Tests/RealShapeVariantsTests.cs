using FluentAssertions;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The shapes the parity export added to the catalog (plan 2.14, Task 3b) are read strictly: a real file with one detail changed
/// to something the export did not show becomes an <see cref="UnsupportedStep"/> naming it (W16: never widened silently).
/// </summary>
public class RealShapeVariantsTests
{
    private const string Child = "Child-0FE1B782-E5DE-F111-84E8-002248872B66";
    private const string OnCreate = "OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66";
    private const string Rule = "Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66";
    private const string Recalculate = "Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66";
    private const string OnUpdate = "OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66";

    private static WorkflowDefinition Variant(string file, int category, string original, string replacement)
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "real", file + ".xaml"));
        xaml.Should().Contain(original, "the variant changes text of the real file");
        return XamlWorkflowParser.Parse(xaml.Replace(original, replacement), category, workflowSdkAvailable: false);
    }

    [Fact]
    public void Show_error_with_labels_in_two_languages_is_unsupported()
    {
        const string label = """<mcwo:StepLabel Description="fdsfds" LabelId="99f9f58a-b176-491f-93f5-e7efc3dd7e55" LanguageCode="1033" />""";
        var d = Variant(Rule, 2, label, label + """<mcwo:StepLabel Description="x" LabelId="99f9f58a-b176-491f-93f5-e7efc3dd7e55" LanguageCode="1040" />""");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("exactly one StepLabel");
    }

    [Fact]
    public void Show_error_with_a_level_other_than_nothing_is_unsupported()
    {
        var d = Variant(Rule, 2, "Level=\"[Nothing]\"", "Level=\"[Warning]\"");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("level");
    }

    [Fact]
    public void Set_attribute_value_with_an_unknown_attribute_is_unsupported()
    {
        var d = Variant(Rule, 2, "<mcwc:SetAttributeValue DisplayName=", "<mcwc:SetAttributeValue Mode=\"Default\" DisplayName=");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("'Mode'");
    }

    [Fact]
    public void Else_branch_that_is_not_last_is_unsupported()
    {
        // Swap the two branch conditions: the True branch comes first.
        var d = Variant(Rule, 2, "<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"Condition\">[ConditionBranchStep2_condition]</InArgument>",
            "<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"Condition\">[True]</InArgument>");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("must be the last one");
    }

    [Fact]
    public void Key_read_of_an_activity_table_is_a_reference()
    {
        // An activity table's primary key is activityid, not {table}id (plan 2.21, Task 2 review I4).
        var d = Variant(OnCreate, 0, "Attribute=\"xrmv_orderid\" Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityName=\"xrmv_order\"",
            "Attribute=\"activityid\" Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityName=\"task\"");

        d.Unsupported.Should().BeEmpty();
        var create = ((SequenceStep)d.Root).Steps[0].Should().BeOfType<CreateStep>().Subject;
        create.Values["regardingobjectid"].Should().Be(new ValueExpr.Conversion("System.Guid", "Microsoft.Xrm.Sdk.EntityReference",
            new ValueExpr.AttributeOf(new RecordRef.Primary(), "activityid"), "task"));
    }

    [Fact]
    public void Child_workflow_inputs_that_are_not_an_empty_dictionary_are_unsupported()
    {
        var d = Variant(OnCreate, 0, "Default=\"[New Dictionary(Of System.String, System.Object)]\"", "Default=\"[Inputs]\"");

        var step = d.Unsupported.Should().ContainSingle().Subject;
        step.Path.Should().Be("0/1");
        step.Reason.Should().Contain("empty dictionary");
    }

    [Fact]
    public void Action_argument_read_of_an_output_is_unsupported()
    {
        var d = Variant(Recalculate, 3, "[New Object() { Increment }]</InArgument>\r\n          <InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"x:Decimal\" />",
            "[New Object() { Total }]</InArgument>\r\n          <InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"x:Decimal\" />");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("CustomOperationArguments");
    }

    [Fact]
    public void Argument_whose_direction_contradicts_its_type_is_unsupported()
    {
        var d = Variant(Recalculate, 3, "<mxsw:ArgumentDescriptionAttribute Value=\"New Argument\" />\r\n        <mxsw:ArgumentDirectionAttribute Value=\"Output\" />",
            "<mxsw:ArgumentDescriptionAttribute Value=\"New Argument\" />\r\n        <mxsw:ArgumentDirectionAttribute Value=\"Input\" />");

        d.UnsupportedMembers.Should().ContainSingle().Which.Path.Should().Be("x:Members/Total");
        d.Arguments.Select(a => a.Name).Should().Equal("Increment", "Target");
    }

    [Fact]
    public void Argument_with_an_unknown_attribute_is_unsupported()
    {
        var d = Variant(Recalculate, 3, "<mxsw:ArgumentEntityAttribute Value=\"xrmv_order\" />",
            "<mxsw:ArgumentEntityAttribute Value=\"xrmv_order\" />\r\n        <mxsw:ArgumentFutureAttribute Value=\"x\" />");

        var member = d.UnsupportedMembers.Should().ContainSingle().Subject;
        member.Path.Should().Be("x:Members/Target");
        member.Reason.Should().Contain("ArgumentFutureAttribute");
    }

    [Fact]
    public void Direct_cast_to_another_type_is_unsupported()
    {
        var d = Variant(Child, 0, "DirectCast(StopWorkflowStep1_1, System.String)", "DirectCast(StopWorkflowStep1_1, System.Uri)");

        d.Unsupported.Should().ContainSingle().Which.Path.Should().Be("0/0");
    }

    [Fact]
    public void Stop_exception_with_more_arguments_is_unsupported()
    {
        var d = Variant(Child, 0, "OperationStatus.Canceled)", "OperationStatus.Canceled, StopWorkflowStep1_1, StopWorkflowStep1_1)");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("stop expression");
    }

    [Fact]
    public void Stop_message_in_the_exception_is_still_read()
    {
        // The assumed shape: the message in the exception, and no Reason.
        var d = Variant(Child, 0, "OperationStatus.Canceled)]\" Reason=\"[DirectCast(StopWorkflowStep1_1, System.String)]\"",
            "OperationStatus.Canceled, &quot;Stopped&quot;)]\"");

        d.Unsupported.Should().BeEmpty();
        d.Root.DepthFirst().OfType<StopStep>().Should().ContainSingle().Which.Message.Should().Be(new ValueExpr.Literal("String", "Stopped"));
    }

    [Fact]
    public void Stop_with_a_message_in_the_exception_and_a_reason_is_unsupported()
    {
        // Neither shape has been seen together with the other: which message Dataverse shows is unknown.
        var d = Variant(Child, 0, "OperationStatus.Canceled)", "OperationStatus.Canceled, &quot;Stopped&quot;)");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("Reason");
    }

    // ---- TargetType conversions (I1) ----------------------------------------------------------------------------------

    [Fact]
    public void Primary_key_read_as_a_type_other_than_a_reference_is_unsupported_naming_the_pair()
    {
        var d = Variant(OnCreate, 0, "Value=\"[CreateStep1_3]\">\r\n        <mxswa:GetEntityProperty.TargetType>\r\n          <InArgument x:TypeArguments=\"s:Type\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"mxs:EntityReference\" />",
            "Value=\"[CreateStep1_3]\">\r\n        <mxswa:GetEntityProperty.TargetType>\r\n          <InArgument x:TypeArguments=\"s:Type\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"mxs:EntityCollection\" />");

        var step = d.Unsupported.Should().ContainSingle().Subject;
        step.Path.Should().Be("0/0");
        step.Reason.Should().Contain("System.Guid").And.Contain("Microsoft.Xrm.Sdk.EntityCollection");
    }

    [Fact]
    public void Set_value_whose_type_differs_from_its_target_type_is_unsupported_naming_the_pair()
    {
        // The subject is a String literal; a Boolean target would need a conversion the export never showed.
        var d = Variant(OnCreate, 0, "EntityName=\"task\" Value=\"[CreateStep1_1]\">\r\n        <mxswa:SetEntityProperty.TargetType>\r\n          <InArgument x:TypeArguments=\"s:Type\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"x:String\" />",
            "EntityName=\"task\" Value=\"[CreateStep1_1]\">\r\n        <mxswa:SetEntityProperty.TargetType>\r\n          <InArgument x:TypeArguments=\"s:Type\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"x:Boolean\" />");

        var step = d.Unsupported.Should().ContainSingle().Subject;
        step.Path.Should().Be("0/0");
        step.Reason.Should().Contain("System.String").And.Contain("System.Boolean");
    }

    [Fact]
    public void Expression_result_whose_type_differs_from_its_target_type_is_unsupported_naming_the_pair()
    {
        var d = Variant(OnCreate, 0, "\"TEST\", \"String\" }]</InArgument>\r\n          <InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"x:String\" />",
            "\"TEST\", \"String\" }]</InArgument>\r\n          <InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\">\r\n            <mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"x:Int32\" />");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("System.String").And.Contain("System.Int32");
    }

    [Fact]
    public void Target_type_that_is_not_a_type_literal_is_unsupported()
    {
        var d = Variant(OnCreate, 0, "<mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"mxs:OptionSetValue\" />\r\n          </InArgument>\r\n        </mxswa:SetEntityProperty.TargetType>",
            "[GetType(Microsoft.Xrm.Sdk.OptionSetValue)]\r\n          </InArgument>\r\n        </mxswa:SetEntityProperty.TargetType>");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("TargetType");
    }

    // ---- waits (I3) ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Wait_branch_with_an_else_block_is_unsupported()
    {
        var d = Variant(OnUpdate, 0, "<x:Null x:Key=\"Else\" />",
            "<mxswa:ActivityReference x:Key=\"Else\" AssemblyQualifiedName=\"Microsoft.Crm.Workflow.Activities.Composite, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\" DisplayName=\"ElseStep\">" +
            "<mxswa:ActivityReference.Properties><sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\" />" +
            "<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\"><Persist /></sco:Collection></mxswa:ActivityReference.Properties></mxswa:ActivityReference>");

        var step = d.Unsupported.Should().ContainSingle().Subject;
        step.Path.Should().Be("0/0");
        step.Reason.Should().Contain("Else");
    }

    [Fact]
    public void Wait_that_declares_an_else_branch_is_unsupported()
    {
        var d = Variant(OnUpdate, 0, "<x:Null x:Key=\"ContainsElseBranch\" />", "<x:Boolean x:Key=\"ContainsElseBranch\">True</x:Boolean>");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("ContainsElseBranch");
    }

    // ---- action arguments (M6) ------------------------------------------------------------------------------------------

    private const string TotalTarget = "<mxsw:ArgumentTargetAttribute Value=\"False\" />\r\n        <mxsw:ArgumentDescriptionAttribute Value=\"New Argument\" />\r\n        <mxsw:ArgumentDirectionAttribute Value=\"Output\" />";
    private const string IncrementTarget = "<mxsw:ArgumentTargetAttribute Value=\"False\" />\r\n        <mxsw:ArgumentDescriptionAttribute Value=\"New Argument\" />\r\n        <mxsw:ArgumentDirectionAttribute Value=\"Input\" />";
    private const string TargetRequired = "<mxsw:ArgumentRequiredAttribute Value=\"True\" />\r\n        <mxsw:ArgumentTargetAttribute Value=\"True\" />";

    [Fact]
    public void Target_attribute_on_an_output_is_unsupported()
    {
        var d = Variant(Recalculate, 3, TotalTarget, TotalTarget.Replace("Value=\"False\"", "Value=\"True\""));

        d.UnsupportedMembers.Should().ContainSingle().Which.Path.Should().Be("x:Members/Total");
        d.Arguments.Select(a => a.Name).Should().Equal("Increment", "Target");
    }

    [Fact]
    public void Target_attribute_on_an_input_that_is_not_a_reference_is_unsupported()
    {
        var d = Variant(Recalculate, 3, IncrementTarget, IncrementTarget.Replace("Value=\"False\"", "Value=\"True\""));

        d.UnsupportedMembers.Should().ContainSingle().Which.Reason.Should().Contain("Increment").And.Contain("EntityReference");
        d.Arguments.Single(a => a.Name == "Target").IsTarget.Should().BeTrue();
    }

    [Fact]
    public void Target_attribute_on_several_members_is_unsupported_on_each()
    {
        // Increment becomes a second EntityReference input marked as the target.
        const string increment = "<x:Property Name=\"Increment\" Type=\"InArgument(x:Decimal)\">";
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "real", Recalculate + ".xaml"));
        xaml.Should().Contain(increment).And.Contain(IncrementTarget);
        var d = XamlWorkflowParser.Parse(xaml.Replace(increment, increment.Replace("x:Decimal", "mxs:EntityReference"))
            .Replace(IncrementTarget, IncrementTarget.Replace("Value=\"False\"", "Value=\"True\"")), 3, workflowSdkAvailable: false);

        d.UnsupportedMembers.Select(m => m.Path).Should().BeEquivalentTo("x:Members/Increment", "x:Members/Target");
        d.Arguments.Should().NotContain(a => a.IsTarget);
    }

    [Theory]
    [InlineData("<mxsw:ArgumentRequiredAttribute Value=\"True\" />\r\n        <mxsw:ArgumentRequiredAttribute Value=\"True\" />\r\n        <mxsw:ArgumentTargetAttribute Value=\"True\" />", "twice")]
    [InlineData("<mxsw:ArgumentRequiredAttribute Value=\"Yes\" />\r\n        <mxsw:ArgumentTargetAttribute Value=\"True\" />", "'Yes'")]
    [InlineData("<mxsw:ArgumentRequiredAttribute Value=\"True\" />\r\n        <mxsw:ArgumentTargetAttribute Value=\"1\" />", "'1'")]
    [InlineData("<mxsw:ArgumentRequiredAttribute Value=\"True\" />\r\n        <mxsw:ArgumentTargetAttribute Value=\"True\" />\r\n        <mxsw:ArgumentTargetAttribute Value=\"True\" />", "twice")]
    public void Argument_with_a_duplicate_or_odd_flag_attribute_is_unsupported(string replacement, string reason)
    {
        var d = Variant(Recalculate, 3, TargetRequired, replacement);

        var member = d.UnsupportedMembers.Should().ContainSingle().Subject;
        member.Path.Should().Be("x:Members/Target");
        member.Reason.Should().Contain(reason);
    }

    [Fact]
    public void Target_argument_of_another_entity_than_the_primary_one_is_unsupported()
    {
        var d = Variant(Recalculate, 3, "<mxsw:ArgumentEntityAttribute Value=\"xrmv_order\" />", "<mxsw:ArgumentEntityAttribute Value=\"account\" />");

        d.UnsupportedMembers.Should().ContainSingle().Which.Reason.Should().Contain("account").And.Contain("xrmv_order");
    }

    // ---- DirectCast (I2) ------------------------------------------------------------------------------------------------

    [Fact]
    public void Direct_cast_to_another_workflow_property_type_still_parses_as_an_assumed_shape()
    {
        var d = Variant(Child, 0, "DirectCast(StopWorkflowStep1_1, System.String)", "DirectCast(StopWorkflowStep1_1, System.Int32)");

        d.Unsupported.Should().BeEmpty();
        ActivityCatalog.FindShape("Vb:DirectCast")!.Source.Should().Be("export");
        ActivityCatalog.FindShape("Vb:DirectCast:OtherPropertyTypes")!.Source.Should().Be("assumed");
    }

    [Fact]
    public void Lookup_literal_whose_id_is_not_a_guid_literal_is_unsupported()
    {
        var d = Variant("OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66", 0, "WorkflowPropertyType.Guid, \"692363f0-a3da-f111-84e8-3833c5c48caf\", \"Key\"",
            "WorkflowPropertyType.String, \"692363f0-a3da-f111-84e8-3833c5c48caf\", \"Key\"");

        // A String literal declared with the lookup's TargetType: refused for the pair before the lookup reads it.
        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("System.String").And.Contain("Microsoft.Xrm.Sdk.EntityReference");
    }

    [Fact]
    public void Lookup_literal_whose_id_variable_holds_no_guid_is_unsupported()
    {
        // SendEmailStep3_5 holds a Boolean literal.
        var d = Variant(OnUpdate, 0, "\"Euro\", SendEmailStep3_13, \"Lookup\"", "\"Euro\", SendEmailStep3_5, \"Lookup\"");

        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("Guid literal");
    }

    [Fact]
    public void The_shapes_confirmed_by_the_export_are_marked_export()
    {
        const string Sdk = "{clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow}";
        const string Wf = "{http://schemas.microsoft.com/netfx/2009/xaml/activities}";
        ActivityCatalog.Entries.Where(e => e.Source == "export").Select(e => e.Element).Should().BeEquivalentTo(
            // Task 9: the Learn type names the export uses.
            Sdk + "Workflow", Sdk + "CreateEntity", Sdk + "UpdateEntity", Sdk + "SendEmail", Sdk + "StartChildWorkflow",
            Sdk + "GetEntityProperty", Sdk + "SetEntityProperty", Sdk + "ActivityReference", Sdk + "ReferenceLiteral",
            Wf + "Sequence", Wf + "Assign", Wf + "Persist", Wf + "Variable", Wf + "TerminateWorkflow",
            "Microsoft.Crm.Workflow.Activities.Composite", "Microsoft.Crm.Workflow.Activities.ConditionSequence",
            "Microsoft.Crm.Workflow.Activities.ConditionBranch", "Microsoft.Crm.Workflow.Activities.EvaluateCondition",
            "Microsoft.Crm.Workflow.Activities.EvaluateExpression",
            "{clr-namespace:Microsoft.Crm.Workflow.ClientActivities;assembly=Microsoft.Crm.Workflow}SetAttributeValue",
            "{clr-namespace:Microsoft.Crm.Workflow.ClientActivities;assembly=Microsoft.Crm.Workflow}SetMessage",
            "Shape:EntityBookkeeping", "Shape:RecordKeys", "Shape:StopException", "Shape:DesignerSequence", "Shape:WaitBranch",
            "Shape:ElseBranch", "Shape:ChildInputs", "Shape:OutputAssign", "Shape:ArgumentAttributes", "Shape:LookupLiteral",
            "Shape:StepLabel", "EvaluateExpression:CreateCrmType", "EvaluateExpression:SelectFirstNonNull",
            "EvaluateExpression:CustomOperationArguments", "EvaluateExpression:Add", "Vb:DirectCast", "Shape:TargetType",
            "EvaluateCondition:Null", "EvaluateCondition:GreaterThan", "EvaluateCondition:LessThan",
            // Plan 2.12a, Task 6: the calculated column definitions of the 2.12a parity export.
            "Microsoft.Crm.Workflow.Activities.EvaluateLogicalCondition", "EvaluateLogicalCondition:And", "EvaluateLogicalCondition:Or",
            "EvaluateCondition:Equal", "EvaluateCondition:GreaterEqual", "EvaluateExpression:Subtract", "EvaluateExpression:Multiply",
            "EvaluateExpression:Divide", "EvaluateExpression:Now", "Shape:CalculatedTargetType",
            "EvaluateExpression:AddHours", "EvaluateExpression:AddDays", "EvaluateExpression:AddWeeks", "EvaluateExpression:AddMonths",
            "EvaluateExpression:AddYears", "EvaluateExpression:SubtractHours", "EvaluateExpression:SubtractDays",
            "EvaluateExpression:SubtractWeeks", "EvaluateExpression:SubtractMonths", "EvaluateExpression:SubtractYears",
            "EvaluateExpression:DiffInDays", "EvaluateExpression:DiffInHours", "EvaluateExpression:DiffInMinutes",
            "EvaluateExpression:DiffInMonths", "EvaluateExpression:DiffInWeeks", "EvaluateExpression:DiffInYears",
            "EvaluateExpression:Concat", "EvaluateExpression:TrimLeft", "EvaluateExpression:TrimRight",
            // Plan 2.12b, Task 2: the rollup column definitions of the same export.
            "Shape:RollupRule", "Shape:RollupHierarchy", "Shape:RollupRelatedLink", "EvaluateExpression:Sum", "EvaluateExpression:Count",
            "EvaluateExpression:Maximum",
            // Plan 2.12b, Task 7: the export 1.0.0.5.
            "EvaluateExpression:Minimum", "EvaluateExpression:Average");
    }
}
