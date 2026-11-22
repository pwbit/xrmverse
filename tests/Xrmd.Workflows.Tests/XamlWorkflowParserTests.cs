using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Workflows.Xaml;
using static Xrmd.Workflows.Xaml.ValueExpr;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The parser on the provisional fixtures of tests/fixtures/workflows (W25), for the shapes the parity export
/// (<see cref="RealExportXamlTests"/>, tests/fixtures/workflows/real) does not contain. Each test names the provisional shapes it
/// still covers; the shapes the export contains (create, update, child workflow on the primary record, send email, a condition with
/// an otherwise, a wait without timeout, stop, set value and show error of a business rule, action arguments and output assignment)
/// are tested on the real files only.
/// </summary>
public class XamlWorkflowParserTests
{
    private static WorkflowDefinition Fixture(string name, int category = 0) =>
        XamlWorkflowParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", name + ".xaml")), category);

    private static IReadOnlyList<WorkflowStep> Steps(WorkflowDefinition d) => ((SequenceStep)d.Root).Steps;

    [Fact]
    public void Parse_provisional_step_composite_and_updated_entities_shapes()
    {
        // Provisional shapes (not in the parity export): the StepComposite wrapper of a designer step, the UpdatedEntities
        // dictionary of an update and Integer literals. Create and update themselves: RealExportXamlTests.
        var d = Fixture("realtime-create-update");

        d.Unsupported.Should().BeEmpty();
        d.PrimaryEntity.Should().Be("account");
        Steps(d).Should().HaveCount(2);

        var create = Steps(d)[0].Should().BeOfType<CreateStep>().Subject;
        create.Path.Should().Be("0/0");
        create.DisplayName.Should().Be("CreateStep1: create a follow-up task");
        create.Entity.Should().Be("task");
        create.Variable.Should().Be("CreateStep1_localParameter");
        create.Values.Should().HaveCount(3);
        create.Values["subject"].Should().Be(new Literal("String", "Call the new customer"));
        create.Values["description"].Should().Be(new AttributeOf(new RecordRef.Primary(), "name"));
        create.Values["regardingobjectid"].Should().Be(new AttributeOf(new RecordRef.Primary(), "accountid"));

        var update = Steps(d)[1].Should().BeOfType<UpdateStep>().Subject;
        update.Path.Should().Be("0/1");
        update.Record.Should().Be(new RecordRef.Primary());
        update.Values["creditonhold"].Should().Be(new Literal("Boolean", true));
        update.Values["numberofemployees"].Should().Be(new Literal("Integer", 25));
    }

    [Fact]
    public void Parse_condition_branches_and_otherwise()
    {
        // Provisional shapes (not in the parity export): an Else holding a nested condition (else-if), an Else block as the
        // otherwise, And / NotNull / Equal, set state and assign.
        var d = Fixture("background-wait");
        d.Unsupported.Should().BeEmpty();

        var condition = Steps(d)[0].Should().BeOfType<ConditionStep>().Subject;
        condition.DisplayName.Should().Be("ConditionStep1: check the credit limit");
        condition.Branches.Should().HaveCount(2);

        var primary = new RecordRef.Primary();
        condition.Branches[0].When.Should().Be(new ConditionExpr.And(
            new ConditionExpr.Compare("GreaterThan", new AttributeOf(primary, "creditlimit"), new Literal("Money", 1000m)),
            new ConditionExpr.Compare("NotNull", new AttributeOf(primary, "name"), null)));
        var setState = ((SequenceStep)condition.Branches[0].Then).Steps.Should().ContainSingle().Subject.Should().BeOfType<SetStateStep>().Subject;
        setState.Should().BeEquivalentTo(new { Record = primary, State = 1, Status = 2 });

        // The Else of the first branch is a nested condition: flattened into an else-if branch.
        condition.Branches[1].When.Should().Be(
            new ConditionExpr.Compare("Equal", new AttributeOf(primary, "name"), new Literal("String", "Example Ltd")));
        var assign = ((SequenceStep)condition.Branches[1].Then).Steps.Should().ContainSingle().Subject.Should().BeOfType<AssignStep>().Subject;
        assign.Record.Should().Be(primary);
        assign.Owner.Should().Be(new Literal("EntityReference", new EntityRefValue("systemuser", Guid.Parse("6a1f5c2e-0000-4000-8000-000000000001"))));
        assign.DisplayName.Should().Be("AssignStep6: give it to the sales lead");

        var otherwise = condition.Otherwise.Should().BeOfType<SequenceStep>().Subject;
        var update = otherwise.Steps.Should().ContainSingle().Subject.Should().BeOfType<UpdateStep>().Subject;
        update.Values["description"].Should().Be(new Literal("String", "Review the credit limit"));
    }

    [Fact]
    public void Parse_wait_with_timeout_branch()
    {
        // Provisional shapes (not in the parity export): the timeout branch of a wait and a plain Postpone. A wait without
        // timeout: RealExportXamlTests (OnUpdate).
        var d = Fixture("background-wait");
        var primary = new RecordRef.Primary();

        var block = Steps(d)[1].Should().BeOfType<SequenceStep>().Subject;
        block.DisplayName.Should().Be("WaitStep9: wait for approval");
        var wait = block.Steps[0].Should().BeOfType<WaitStep>().Subject;
        wait.Until.Should().Be(new ConditionExpr.Compare("Equal", new AttributeOf(primary, "statuscode"), new Literal("OptionSetValue", 2)));
        wait.Timeout.Should().Be(new AttributeOf(primary, "lastusedincampaign"));
        var onTimeout = wait.OnTimeout.Should().BeOfType<SequenceStep>().Subject;
        onTimeout.Steps.Should().ContainSingle().Which.Should().BeOfType<SetStateStep>().Which.State.Should().Be(1);

        // The condition branch's Then runs after the wait.
        var then = block.Steps[1].Should().BeOfType<SequenceStep>().Subject;
        then.Steps.Should().ContainSingle().Which.Should().BeOfType<SetStateStep>().Which.Should().BeEquivalentTo(new { State = 0, Status = 1 });

        // A plain Postpone is a wait with only a timeout.
        var plain = Steps(d)[2].Should().BeOfType<WaitStep>().Subject;
        plain.Path.Should().Be("0/2");
        plain.Until.Should().BeNull();
        plain.Timeout.Should().Be(new AttributeOf(primary, "lastusedincampaign"));
        plain.OnTimeout.Should().BeNull();
    }

    [Fact]
    public void Parse_child_workflow_on_a_related_record_and_stop_canceled_with_message()
    {
        // Provisional shapes (not in the parity export): a WorkflowId written as New Guid(...), a child workflow on a related
        // record after RetrieveEntity, and a stop message passed in the exception. A child workflow on the primary record and a stop
        // without message: RealExportXamlTests (OnCreate, Child).
        var d = Fixture("child-and-stop");
        d.Unsupported.Should().BeEmpty();

        Steps(d)[0].Should().BeOfType<ChildWorkflowStep>().Which.WorkflowId.Should().Be(Guid.Parse("0b8e2d4c-1a3f-4e5d-9c7b-000000000101"));

        var related = Steps(d)[1].Should().BeOfType<ChildWorkflowStep>().Subject;
        related.WorkflowId.Should().Be(Guid.Parse("0b8e2d4c-1a3f-4e5d-9c7b-000000000102"));
        related.Record.Should().Be(new RecordRef.Related("parentaccountid"));

        var stop = Steps(d)[3].Should().BeOfType<StopStep>().Subject;
        stop.Canceled.Should().BeTrue();
        stop.Message.Should().Be(new Literal("String", "The credit check failed"));
        stop.Label.Should().Be("0/3 (StopStep4: stop as canceled)");
    }

    [Fact]
    public void Parse_business_rule_maps_set_value_default_and_error_and_client_only_steps()
    {
        // Provisional shapes (not in the parity export, which writes mcwc:SetAttributeValue and mcwc:SetMessage, see
        // RealExportXamlTests): SetEntityProperty straight on the primary record, the ActivityReference business rule activities
        // (SetDefaultValue, SetMessage, client-only ones) and an Else block as the otherwise.
        var d = Fixture("business-rule-entity", category: 2);
        d.Unsupported.Should().BeEmpty();
        d.Category.Should().Be(2);
        d.PrimaryEntity.Should().Be("contact");

        var condition = Steps(d).Should().ContainSingle().Subject.Should().BeOfType<ConditionStep>().Subject;
        condition.Branches.Should().ContainSingle().Which.When.Should().Be(
            new ConditionExpr.Compare("Null", new AttributeOf(new RecordRef.Primary(), "jobtitle"), null));

        var then = ((SequenceStep)condition.Branches[0].Then).Steps;
        then.Should().HaveCount(5);
        then[0].Should().Be(new SetValueStep(then[0].Path, "SetValueStep3", "jobtitle", new Literal("String", "Unknown"), DefaultOnly: false));
        then[1].Should().BeOfType<SetValueStep>().Which.Should().BeEquivalentTo(new
        {
            Attribute = "preferredcontactmethodcode",
            DefaultOnly = true,
        });
        ((SetValueStep)then[1]).Value.Should().Be(new Literal("OptionSetValue", 1));
        then[2].Should().BeOfType<SetValueStep>().Which.Value.Should().Be(new Literal("Null", null));
        then[3].Should().BeOfType<ClientOnlyStep>().Which.Kind.Should().Be("SetFieldRequiredLevel");
        then[4].Should().BeOfType<ClientOnlyStep>().Which.Kind.Should().Be("SetVisibility");

        var otherwise = ((SequenceStep)condition.Otherwise!).Steps.Should().ContainSingle().Subject;
        otherwise.Should().BeOfType<ShowErrorStep>().Which.Message.Should().Be(new Literal("String", "Check the job title"));
    }

    /// <summary>
    /// Plan 2.21, Task 4: a form-only business rule action keeps its column (<c>ControlId</c>) and its setting (<c>IsVisible</c>,
    /// <c>IsReadOnly</c>, <c>RequiredLevel</c>) as written, so the exporter can write it back; the server still does nothing with them.
    /// </summary>
    [Fact]
    public void Client_only_steps_keep_their_column_and_setting()
    {
        var d = Fixture("business-rule-entity", category: 2);
        var condition = Steps(d).OfType<ConditionStep>().Single();
        var then = ((SequenceStep)condition.Branches[0].Then).Steps;

        then[3].Should().BeOfType<ClientOnlyStep>().Which.Should().BeEquivalentTo(new { Kind = "SetFieldRequiredLevel", Attribute = "jobtitle", Setting = "2" });
        then[4].Should().BeOfType<ClientOnlyStep>().Which.Should().BeEquivalentTo(new { Kind = "SetVisibility", Attribute = "fax", Setting = "False" });
    }

    [Fact]
    public void Business_rule_activities_outside_a_business_rule_are_unsupported()
    {
        // Provisional shapes; the export's own business rule activities: RealExportXamlTests.
        var d = Fixture("business-rule-entity", category: 0);

        d.Unsupported.Should().NotBeEmpty();
        d.Unsupported.Select(u => u.Element).Should().Contain(["SetEntityProperty", "Microsoft.Crm.Workflow.Activities.SetDefaultValue"]);
    }

    [Fact]
    public void Parse_perform_action_step()
    {
        // Provisional shapes (not in the parity export): the ExecuteAction "perform action" step and an output assigned straight
        // from an argument. Arguments and output assignment as the designer writes them: RealExportXamlTests (Recalculate).
        var d = Fixture("action-echo", category: 3);
        d.Unsupported.Should().BeEmpty();

        Steps(d)[0].Should().Be(new AssignOutputStep("0/0", "AssignStep1: echo the text", "OutText", new VariableRef("InText")));

        var call = Steps(d)[1].Should().BeOfType<ActionStep>().Subject;
        call.Message.Should().Be("xrmv_Notify");
        call.Target.Should().BeNull();
        call.Variable.Should().Be("ActionStep2_result");
        call.Inputs.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, ValueExpr>("Text", new VariableRef("InText")));

        var create = Steps(d)[2].Should().BeOfType<CreateStep>().Subject;
        create.Entity.Should().Be("task");
        create.Values.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, ValueExpr>("subject", new VariableRef("InText")));
    }

    [Fact]
    public void Parse_perform_action_output_read_by_a_later_step()
    {
        // Provisional (W13, catalog Vb:ActionOutput): [ActionStep1_result("Total")] reads the Total output of the action step.
        var d = Fixture("action-call");
        d.Unsupported.Should().BeEmpty();

        var call = Steps(d)[0].Should().BeOfType<ActionStep>().Subject;
        call.Message.Should().Be("xrmv_Recalculate");
        call.Target.Should().Be(new RecordRef.Primary());
        call.Variable.Should().Be("ActionStep1_result");
        call.Inputs.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, ValueExpr>("Increment", new Literal("Decimal", 5m)));

        var create = Steps(d)[1].Should().BeOfType<CreateStep>().Subject;
        create.Entity.Should().Be("xrmv_order");
        create.Values["xrmv_amount"].Should().Be(new ActionOutput("ActionStep1_result", "Total"));
    }

    [Fact]
    public void Action_output_of_a_name_that_is_no_action_step_is_unsupported()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "action-call.xaml"))
            .Replace("[ActionStep1_result(&quot;Total&quot;)]", "[CreateStep2_1(&quot;Total&quot;)]", StringComparison.Ordinal);
        XamlWorkflowParser.Parse(xaml, 0).Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("CreateStep2_1");
    }

    [Fact]
    public void Argument_written_as_an_element_is_unsupported_not_absent()
    {
        // The Wait flag of a wait written as a property element: read as absent, the wait would silently become a condition (final
        // review M1). It is a step xrmverse does not run, named with its reason.
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "background-wait.xaml"))
            .Replace("x:Key=\"Wait\">True</InArgument>", "x:Key=\"Wait\"><x:Boolean>True</x:Boolean></InArgument>", StringComparison.Ordinal);

        var step = XamlWorkflowParser.Parse(xaml, 0).Unsupported.Should().ContainSingle().Subject;
        step.DisplayName.Should().StartWith("WaitStep9");
        step.Reason.Should().Contain("'Wait'").And.Contain("written as an element");
    }

    [Fact]
    public void Parse_custom_activity_step_names_type_assembly_inputs_outputs()
    {
        var d = XamlWorkflowParser.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "custom-activity.xaml")), 0, workflowSdkAvailable: true);
        d.Unsupported.Should().BeEmpty();

        var custom = Steps(d)[0].Should().BeOfType<CustomActivityStep>().Subject;
        custom.Path.Should().Be("0/0");
        custom.DisplayName.Should().Be("CustomStep1: normalize the name");
        custom.TypeName.Should().Be("Example.Workflows.Activities.NormalizeName");
        custom.AssemblyName.Should().Be("Example.Workflows, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        custom.Inputs.Should().HaveCount(2);
        custom.Inputs["Input"].Should().Be(new AttributeOf(new RecordRef.Primary(), "name"));
        custom.Inputs["MaxLength"].Should().Be(new Literal("Integer", 50));
        custom.Outputs.Should().Equal(new Dictionary<string, string> { ["Normalized"] = "CustomStep1_Normalized" });

        // A later step reads the output as a run-time variable.
        Steps(d)[1].Should().BeOfType<UpdateStep>().Which.Values["name"].Should().Be(new VariableRef("CustomStep1_Normalized"));
    }

    [Fact]
    public void Custom_activity_without_the_workflow_sdk_is_an_unsupported_step()
    {
        // Plan 2.14, W17: without Microsoft.Xrm.Sdk.Workflow.dll in the SDK folder the step cannot run; the rest of the
        // definition is parsed as usual.
        var d = XamlWorkflowParser.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "custom-activity.xaml")), 0, workflowSdkAvailable: false);

        var step = Steps(d)[0].Should().BeOfType<UnsupportedStep>().Subject;
        step.Path.Should().Be("0/0");
        step.DisplayName.Should().Be("CustomStep1: normalize the name");
        step.Reason.Should().Be("custom workflow activities need the Microsoft workflow SDK: run xrmd sdk install");
        step.Reason.Should().Be(XamlWorkflowParser.WorkflowSdkMissingReason);
        d.Unsupported.Should().StartWith(step, "the later step that reads the activity's output cannot run either");
    }

    [Fact]
    public void Parse_reads_the_workflow_sdk_availability_of_the_host()
    {
        // The two-argument overload uses what the host found at startup (WorkflowSdk); no SDK is loaded in these tests.
        Custom.WorkflowSdk.IsAvailable.Should().BeFalse();
        Fixture("custom-activity").Unsupported.Should().Contain(u => u.Path == "0/0"
            && u.Reason == "custom workflow activities need the Microsoft workflow SDK: run xrmd sdk install");
    }

    [Fact]
    public void Unknown_activity_becomes_unsupported_step_with_path()
    {
        var d = Fixture("unsupported-expression");

        var future = Steps(d)[1].Should().BeOfType<UnsupportedStep>().Subject;
        future.Path.Should().Be("0/1");
        future.DisplayName.Should().Be("FutureStep2: an activity xrmverse does not know");
        future.Element.Should().Be("Microsoft.Crm.Workflow.Activities.FutureStep");

        var scope = Steps(d)[2].Should().BeOfType<UnsupportedStep>().Subject;
        scope.Path.Should().Be("0/2");
        scope.Element.Should().Be("CancellationScope");

        // The rest of the process is still parsed.
        Steps(d)[3].Should().BeOfType<UpdateStep>().Which.Values["description"].Should().Be(new Literal("String", "still here"));
        d.Unsupported.Select(u => u.Path).Should().Equal("0/0", "0/1", "0/2");
    }

    [Fact]
    public void Unknown_vb_expression_becomes_unsupported()
    {
        var d = Fixture("unsupported-expression");

        // The whole designer step is one unsupported step, named by the step's path and display name.
        var step = Steps(d)[0].Should().BeOfType<UnsupportedStep>().Subject;
        step.Path.Should().Be("0/0");
        step.DisplayName.Should().Be("UpdateStep1: set a date three days out");
        step.Element.Should().Be("SetEntityProperty");
        step.Reason.Should().Contain("DateTime.Now.AddDays(3)");
    }

    [Theory]
    [InlineData("[If(a, b, c)]")]
    [InlineData("[a + b]")]
    [InlineData("[CreateStep1_1.ToString()]")]
    [InlineData("[UnknownVariable]")]
    public void Expressions_outside_the_catalog_are_unsupported(string expression)
    {
        var xaml = Wrap($"""
            <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.StepComposite, Microsoft.Crm.Workflow" DisplayName="Step1">
              <mxswa:ActivityReference.Properties>
                <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
                  <mxswa:GetEntityProperty Attribute="name" Entity="[InputEntities(&quot;primaryEntity&quot;)]" EntityName="account" Value="[CreateStep1_1]" />
                  <Assign x:TypeArguments="mxs:Entity" To="[UpdatedEntities(&quot;primaryEntity#Temp&quot;)]" Value="[New Entity(&quot;account&quot;)]" />
                  <mxswa:SetEntityProperty Attribute="name" Entity="[UpdatedEntities(&quot;primaryEntity#Temp&quot;)]" EntityName="account" Value="{System.Security.SecurityElement.Escape(expression)}" />
                  <mxswa:UpdateEntity Entity="[UpdatedEntities(&quot;primaryEntity#Temp&quot;)]" EntityName="account" />
                </sco:Collection>
              </mxswa:ActivityReference.Properties>
            </mxswa:ActivityReference>
            """);

        var d = XamlWorkflowParser.Parse(xaml, 0);

        d.Unsupported.Should().ContainSingle().Which.Path.Should().Be("0/0");
    }

    [Fact]
    public void X_code_block_is_unsupported()
    {
        var xaml = """
            <Activity x:Class="XrmWorkflowCode"
                      xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <x:Code><![CDATA[ Sub Run() : End Sub ]]></x:Code>
              <mxswa:Workflow />
            </Activity>
            """;

        var d = XamlWorkflowParser.Parse(xaml, 0);

        var code = d.Unsupported.Should().ContainSingle().Subject;
        code.Element.Should().Be("x:Code");
        code.Path.Should().Be("0/0");
    }

    [Fact]
    public void Unsupported_learn_activity_names_its_reason()
    {
        var d = XamlWorkflowParser.Parse(Wrap("""<mxswa:SendEmailFromTemplate DisplayName="SendStep1" />"""), 0);

        var step = d.Unsupported.Should().ContainSingle().Subject;
        step.Element.Should().Be("SendEmailFromTemplate");
        step.Reason.Should().Contain("template");
    }

    [Fact]
    public void Malformed_xml_throws_with_position()
    {
        var act = () => XamlWorkflowParser.Parse("<Activity xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\">\n  <Sequence>\n</Activity>", 0);

        act.Should().Throw<XamlWorkflowException>().WithMessage("*line 3, position 3*");
    }

    [Fact]
    public void Root_that_is_not_an_activity_throws()
    {
        var act = () => XamlWorkflowParser.Parse("<Workflow xmlns=\"urn:other\" />", 0);

        act.Should().Throw<XamlWorkflowException>().WithMessage("*Activity*line 1, position 2*");
    }

    [Fact]
    public void Lookup_matches_namespaces_by_uri_whatever_the_version()
    {
        var name = XName.Get("CreateEntity",
            "clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");

        ActivityCatalog.Lookup(name, null).Should().Be((CatalogStatus.Supported, "Create"));
        ActivityCatalog.Lookup(XName.Get("CreateEntity", "urn:elsewhere"), null).Should().Be((CatalogStatus.Unsupported, "Unknown"));
        ActivityCatalog.Lookup(
                XName.Get("ActivityReference", ActivityCatalog.SdkActivitiesNs),
                "Microsoft.Crm.Workflow.Activities.SetVisibility, Microsoft.Crm.Workflow, Version=9.0.0.0")
            .Should().Be((CatalogStatus.ClientOnly, "SetVisibility"));
        ActivityCatalog.Lookup(XName.Get("NormalizeName", "clr-namespace:Example.Workflows.Activities;assembly=Example.Workflows"), null)
            .Should().Be((CatalogStatus.Supported, "CustomActivity"));
    }

    [Fact]
    public void Catalog_sources_and_unsupported_reasons()
    {
        var assumed = ActivityCatalog.Entries.Where(e => e.Source == "assumed").ToList();
        assumed.Should().NotBeEmpty();
        assumed.Select(e => e.Element).Should().OnlyHaveUniqueItems();
        ActivityCatalog.Entries.Select(e => e.Source).Distinct().Should().BeSubsetOf(["learn", "assumed", "export"]);
        ActivityCatalog.Entries.Where(e => e.Status == CatalogStatus.Unsupported).Should().OnlyContain(e => e.Reason != null);
    }

    private static string Wrap(string body) => $"""
        <Activity x:Class="XrmWorkflowTest"
                  xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:mxs="clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0"
                  xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0"
                  xmlns:sco="clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, Version=4.0.0.0"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <mxswa:Workflow>
        {body}
          </mxswa:Workflow>
        </Activity>
        """;
}
