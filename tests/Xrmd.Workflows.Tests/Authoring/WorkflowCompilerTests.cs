using FluentAssertions;
using Xrmd.Schema;
using Xrmd.Workflows.Authoring;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Tests.Authoring;

/// <summary>
/// The compiler of <c>kind: workflow</c> documents (plan 2.21, Task 2): the row defaults of the export, the assumed shapes parsed
/// back by the 2.14 parser, typing by metadata (A11) and the refusals of A10.
/// </summary>
public class WorkflowCompilerTests
{
    private static Task<CompileResult> Compile(string yaml, FakeAuthoringEnvironment? env = null) =>
        WorkflowCompiler.CompileAsync(WorkflowYaml.Read(yaml), env ?? new FakeAuthoringEnvironment(), null, default);

    private static async Task<IReadOnlyList<CompileError>> Errors(string yaml, FakeAuthoringEnvironment? env = null)
    {
        var act = () => Compile(yaml, env);
        return (await act.Should().ThrowAsync<WorkflowCompileException>()).Which.Errors;
    }

    /// <summary>A background workflow on <c>account</c> created on create, with the given steps (indented as list items).</summary>
    private static string Account(string steps, string mode = "background") =>
        $"kind: workflow\nname: Test\nentity: account\nmode: {mode}\ntrigger:\n  create: true\nsteps:\n{steps}";

    private static IReadOnlyList<WorkflowStep> Steps(CompileResult result) => ((SequenceStep)result.Definition.Root).Steps;

    // ---- the row ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Row_columns_follow_the_defaults_table()
    {
        // The real exports (customizations.xml of xrmdWfTest_1_0_0_2.zip).
        var onCreate = (await GoldenXamlTests.CompileFixture("oncreate.yaml")).Row;
        onCreate.Should().BeEquivalentTo(new
        {
            Id = FakeAuthoringEnvironment.OnCreateId, Name = "OnCreate", Description = (string?)null, Category = 0, Type = 1, Mode = 1, Scope = 4,
            PrimaryEntity = "xrmv_order", TriggerOnCreate = true, TriggerOnDelete = false, TriggerOnUpdateAttributes = (string?)null,
            CreateStage = (int?)40, UpdateStage = (int?)null, DeleteStage = (int?)null, RunAs = 1, OnDemand = false, Subprocess = false,
            AsyncAutoDelete = false, SyncWorkflowLogOnFailure = true, IsTransacted = true, IsCustomizable = true, IntroducedVersion = "1.0.0.0",
            StateCode = 0, ProcessTriggerScope = (int?)null,
        });
        var child = (await GoldenXamlTests.CompileFixture("child.yaml")).Row;
        child.Should().BeEquivalentTo(new { Mode = 1, Scope = 4, Subprocess = true, TriggerOnCreate = false, CreateStage = (int?)40, RunAs = 1, SyncWorkflowLogOnFailure = true });
        var onUpdate = (await GoldenXamlTests.CompileFixture("onupdate.yaml")).Row;
        onUpdate.Should().BeEquivalentTo(new
        {
            Mode = 0, Scope = 4, TriggerOnUpdateAttributes = "xrmv_status", TriggerOnCreate = false, CreateStage = (int?)null, UpdateStage = (int?)null,
            RunAs = 1, AsyncAutoDelete = true, SyncWorkflowLogOnFailure = false, IsTransacted = true, IsCustomizable = true, Type = 1,
            IntroducedVersion = "1.0.0.0",
        });
        foreach (var row in new[] { onCreate, child, onUpdate })
            row.Xaml.Should().NotBeNullOrEmpty();

        // The defaults a file leaves out.
        var realtime = (await Compile(Account("  - stop: { status: succeeded }", mode: "realtime"))).Row;
        realtime.Should().BeEquivalentTo(new { Mode = 1, Scope = 1, RunAs = 1, SyncWorkflowLogOnFailure = true, AsyncAutoDelete = false, CreateStage = (int?)40 });
        var background = (await Compile(Account("  - stop: { status: succeeded }"))).Row;
        background.Should().BeEquivalentTo(new { Mode = 0, Scope = 1, RunAs = 1, SyncWorkflowLogOnFailure = false, AsyncAutoDelete = false, CreateStage = (int?)null });

        // The settings a file states.
        var stated = (await Compile("""
            kind: workflow
            name: Stated
            description: Says it all
            entity: account
            mode: realtime
            scope: businessUnit
            runAs: owner
            logFailures: false
            onDemand: true
            trigger:
              create: { stage: pre }
              update: { columns: [name, ownerid], stage: pre }
              delete: true
            steps:
              - stop: { status: succeeded }
            """)).Row;
        stated.Should().BeEquivalentTo(new
        {
            Description = "Says it all", Scope = 2, RunAs = 0, SyncWorkflowLogOnFailure = false, OnDemand = true, CreateStage = (int?)20,
            UpdateStage = (int?)20, DeleteStage = (int?)20, TriggerOnUpdateAttributes = "name,ownerid", TriggerOnDelete = true,
        });
        var parentChild = (await Compile(Account("  - stop: { status: succeeded }").Replace("mode: background", "mode: background\nscope: parentChildBusinessUnits\nautoDelete: true"))).Row;
        parentChild.Should().BeEquivalentTo(new { Scope = 3, AsyncAutoDelete = true });
    }

    [Fact]
    public async Task Compiled_xaml_parses_without_unsupported_steps()
    {
        foreach (var yaml in new[] { "oncreate.yaml", "onupdate.yaml", "child.yaml" })
        {
            var result = await GoldenXamlTests.CompileFixture(yaml);
            var parsed = XamlWorkflowParser.Parse(result.Xaml, 0, workflowSdkAvailable: false);
            parsed.Unsupported.Should().BeEmpty(yaml);
            parsed.Should().BeEquivalentTo(result.Definition, o => o.RespectingRuntimeTypes(), yaml);
            result.Row.Xaml.Should().Be(result.Xaml);
        }
    }

    [Fact]
    public async Task Id_is_uuid_v5_of_kind_entity_name()
    {
        // UUID v5 (RFC 9562) of "workflow/account/test" in the namespace 6c1f2f8e-5b52-4c3e-9a63-2d7f0b8e2a11, pinned (Python uuid.uuid5).
        var id = WorkflowIdentity.IdOf(WorkflowKind.Workflow, "account", "Test");
        id.Should().Be(new Guid("f422b1d1-b171-5078-9e5f-22468462cafe"));
        id.ToString()[14].Should().Be('5');
        WorkflowIdentity.IdOf(WorkflowKind.Workflow, "Account", "TEST").Should().Be(id);
        WorkflowIdentity.IdOf(WorkflowKind.BusinessRule, "account", "Test").Should().NotBe(id);
        (await Compile(Account("  - stop: { status: succeeded }"))).Row.Id.Should().Be(id);
        (await Compile(Account("  - stop: { status: succeeded }"))).Xaml.Should().Contain($"x:Class=\"XrmWorkflow{id:N}\"");
        WorkflowIdentity.LabelId(id, "StopWorkflowStep1").Should().Be(WorkflowIdentity.LabelId(id, "StopWorkflowStep1"))
            .And.NotBe(WorkflowIdentity.LabelId(id, "StopWorkflowStep2"));
    }

    // ---- assumed shapes, parsed back -----------------------------------------------------------------------------------

    [Fact]
    public async Task Assign_to_user_parses_as_AssignStep()
    {
        var result = await Compile(Account($"  - assign: {{ record: primary, owner: {{ lookup: systemuser, id: {FakeAuthoringEnvironment.Ada} }}, description: give it to Ada }}"));
        var step = Steps(result).Should().ContainSingle().Which.Should().BeOfType<AssignStep>().Subject;
        step.DisplayName.Should().Be("AssignStep1: give it to Ada");
        step.Record.Should().Be(new RecordRef.Primary());
        step.Owner.Should().Be(new ValueExpr.Literal("EntityReference", new EntityRefValue("systemuser", FakeAuthoringEnvironment.Ada)));
    }

    [Fact]
    public async Task SetState_parses_as_SetStateStep()
    {
        var result = await Compile(Account("  - setState: { record: primary, state: 1, status: 2 }"));
        Steps(result).Should().ContainSingle().Which.Should().BeEquivalentTo(new SetStateStep("0/0", "SetStateStep1", new RecordRef.Primary(), 1, 2));
    }

    [Fact]
    public async Task Update_related_record_parses_with_Related_ref()
    {
        var result = await Compile(Account("  - update: { record: { related: parentaccountid }, set: { name: Parent } }"));
        var step = Steps(result).Should().ContainSingle().Which.Should().BeOfType<UpdateStep>().Subject;
        step.Record.Should().Be(new RecordRef.Related("parentaccountid"));
        step.Values.Should().ContainKey("name").WhoseValue.Should().Be(new ValueExpr.Literal("String", "Parent"));
    }

    [Fact]
    public async Task Field_of_created_record_parses_with_Created_ref()
    {
        var result = await Compile(Account("""
              - create: { id: followUp, entity: task, set: { subject: Call, regardingobjectid: { record: primary } } }
              - update: { record: primary, set: { description: { field: subject, created: followUp } } }
              - update: { record: { created: followUp }, set: { description: Done } }
            """));
        var steps = Steps(result);
        steps[0].Should().BeOfType<CreateStep>().Which.Variable.Should().Be("followUp");
        var read = steps[1].Should().BeOfType<UpdateStep>().Which.Values["description"].Should().BeOfType<ValueExpr.AttributeOf>().Subject;
        read.Record.Should().Be(new RecordRef.Created("followUp"));
        read.Attribute.Should().Be("subject");
        steps[2].Should().BeOfType<UpdateStep>().Which.Record.Should().Be(new RecordRef.Created("followUp"));
    }

    [Fact]
    public async Task Wait_with_timeout_parses_with_OnTimeout()
    {
        var result = await Compile(Account("""
              - wait:
                  until: { field: statuscode, op: eq, value: 2 }
                  timeout: { field: lastusedincampaign }
                  then:
                    - setState: { record: primary, state: 0, status: 1 }
                  onTimeout:
                    - setState: { record: primary, state: 1, status: 2 }
            """));
        var wait = result.Definition.Root.DepthFirst().OfType<WaitStep>().Should().ContainSingle().Subject;
        wait.Until.Should().Be(new ConditionExpr.Compare("Equal", new ValueExpr.AttributeOf(new RecordRef.Primary(), "statuscode"), new ValueExpr.Literal("OptionSetValue", 2)));
        wait.Timeout.Should().BeOfType<ValueExpr.AttributeOf>().Which.Attribute.Should().Be("lastusedincampaign");
        wait.OnTimeout!.DepthFirst().OfType<SetStateStep>().Should().ContainSingle().Which.State.Should().Be(1);
        wait.OnTimeout.DepthFirst().OfType<SetStateStep>().Single().DisplayName.Should().Be("SetStateStep5");
        Steps(result).Single().Should().BeOfType<SequenceStep>().Which.Steps[1].DepthFirst().OfType<SetStateStep>().Single().DisplayName.Should().Be("SetStateStep3");
        result.Xaml.Should().Contain("DisplayName=\"WaitStep1\"").And.Contain("DisplayName=\"WaitBranchStep2\"").And.Contain("DisplayName=\"TimeoutBranchStep4\"");

        // A wait with only a timeout: the steps of then follow it.
        var plain = await Compile(Account("""
              - wait:
                  timeout: { field: lastusedincampaign }
                  then:
                    - setState: { record: primary, state: 1, status: 2 }
            """));
        var only = plain.Definition.Root.DepthFirst().OfType<WaitStep>().Should().ContainSingle().Subject;
        only.Until.Should().BeNull();
        only.Timeout.Should().NotBeNull();
        Steps(plain).Select(s => s.GetType()).Should().Equal(typeof(WaitStep), typeof(SetStateStep));
    }

    [Fact]
    public async Task Stop_succeeded_with_message_parses()
    {
        var result = await Compile(Account("""
              - stop: { status: succeeded, message: All done }
              - stop: { status: canceled, message: { field: name } }
              - stop: { status: canceled }
            """));
        var stops = Steps(result).Cast<StopStep>().ToList();
        stops[0].Should().BeEquivalentTo(new StopStep("0/0", "StopWorkflowStep1", false, new ValueExpr.Literal("String", "All done")));
        stops[1].Canceled.Should().BeTrue();
        stops[1].Message.Should().BeOfType<ValueExpr.Cast>().Which.Operand.Should().BeOfType<ValueExpr.AttributeOf>().Which.Attribute.Should().Be("name");
        stops[2].Message.Should().BeNull();
    }

    [Fact]
    public async Task ElseIf_parses_as_second_branch()
    {
        var result = await Compile(Account("""
              - if:
                  when: { field: numberofemployees, op: gt, value: 100 }
                  then:
                    - setState: { record: primary, state: 1, status: 2 }
                  elseIf:
                    - when: { field: name, op: eq, value: Contoso }
                      then:
                        - update: { record: primary, set: { description: Contoso } }
                  else:
                    - stop: { status: canceled }
            """));
        var condition = Steps(result).Should().ContainSingle().Which.Should().BeOfType<ConditionStep>().Subject;
        condition.DisplayName.Should().Be("ConditionStep1");
        condition.Branches.Should().HaveCount(2);
        condition.Branches[0].When.Should().Be(new ConditionExpr.Compare("GreaterThan", new ValueExpr.AttributeOf(new RecordRef.Primary(), "numberofemployees"), new ValueExpr.Literal("Integer", 100)));
        condition.Branches[1].When.Should().Be(new ConditionExpr.Compare("Equal", new ValueExpr.AttributeOf(new RecordRef.Primary(), "name"), new ValueExpr.Literal("String", "Contoso")));
        condition.Branches[1].Then.DepthFirst().OfType<UpdateStep>().Should().ContainSingle().Which.DisplayName.Should().Be("UpdateStep5");
        condition.Otherwise!.DepthFirst().OfType<StopStep>().Should().ContainSingle().Which.DisplayName.Should().Be("StopWorkflowStep7");
        result.Xaml.Should().Contain("DisplayName=\"ConditionBranchStep6\"");
    }

    [Fact]
    public async Task All_any_not_parse_as_logical_conditions()
    {
        var result = await Compile(Account("""
              - if:
                  when:
                    all:
                      - { field: numberofemployees, op: ge, value: 10 }
                      - any:
                          - { field: name, op: beginsWith, value: A }
                          - not: { field: description, op: null }
                      - { field: creditonhold, op: eq, value: false }
                  then:
                    - stop: { status: canceled }
            """));
        var condition = Steps(result).Should().ContainSingle().Which.Should().BeOfType<ConditionStep>().Subject;
        var primary = new RecordRef.Primary();
        condition.Branches[0].When.Should().Be(new ConditionExpr.And(
            new ConditionExpr.And(
                new ConditionExpr.Compare("GreaterEqual", new ValueExpr.AttributeOf(primary, "numberofemployees"), new ValueExpr.Literal("Integer", 10)),
                new ConditionExpr.Or(
                    new ConditionExpr.Compare("BeginsWith", new ValueExpr.AttributeOf(primary, "name"), new ValueExpr.Literal("String", "A")),
                    new ConditionExpr.Not(new ConditionExpr.Compare("Null", new ValueExpr.AttributeOf(primary, "description"), null)))),
            new ConditionExpr.Compare("Equal", new ValueExpr.AttributeOf(primary, "creditonhold"), new ValueExpr.Literal("Boolean", false))));
    }

    [Theory]
    [InlineData("eq", "Equal")]
    [InlineData("ne", "NotEqual")]
    [InlineData("null", "Null")]
    [InlineData("notNull", "NotNull")]
    [InlineData("contains", "Contains")]
    [InlineData("notContains", "DoesNotContain")]
    [InlineData("beginsWith", "BeginsWith")]
    [InlineData("endsWith", "EndsWith")]
    [InlineData("gt", "GreaterThan")]
    [InlineData("ge", "GreaterEqual")]
    [InlineData("lt", "LessThan")]
    [InlineData("le", "LessEqual")]
    public async Task Each_operator_parses_to_its_ConditionOperator(string op, string expected)
    {
        var compare = op is "null" or "notNull" ? $"{{ field: name, op: {op} }}" : $"{{ field: name, op: {op}, value: x }}";
        var result = await Compile(Account($"  - if: {{ when: {compare}, then: [ {{ stop: {{ status: canceled }} }} ] }}"));
        var when = Steps(result).OfType<ConditionStep>().Single().Branches[0].When.Should().BeOfType<ConditionExpr.Compare>().Subject;
        when.Operator.Should().Be(expected);
        when.Right.Should().Be(op is "null" or "notNull" ? null : new ValueExpr.Literal("String", "x"));
    }

    [Fact]
    public async Task Literal_of_each_column_type_parses_typed()
    {
        var result = await Compile(Account("""
              - update:
                  record: primary
                  set:
                    numberofemployees: 42
                    address1_latitude: 45.5
                    revenue: 1234.56
                    lastusedincampaign: 2026-01-02T03:04:05Z
                    description: "Line one\nline two"
              - if:
                  when: { all: [ { field: statecode, op: eq, value: 1 }, { field: statuscode, op: ne, value: 2 } ] }
                  then: [ { stop: { status: canceled } } ]
            """));
        var values = Steps(result).OfType<UpdateStep>().Single().Values;
        values["numberofemployees"].Should().Be(new ValueExpr.Literal("Integer", 42));
        values["address1_latitude"].Should().Be(new ValueExpr.Literal("Double", 45.5d));
        values["revenue"].Should().Be(new ValueExpr.Literal("Money", 1234.56m));
        values["lastusedincampaign"].Should().Be(new ValueExpr.Literal("DateTime", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
        values["description"].Should().Be(new ValueExpr.Literal("String", "Line one\nline two"));
        var when = (ConditionExpr.And)Steps(result).OfType<ConditionStep>().Single().Branches[0].When;
        ((ConditionExpr.Compare)when.Left).Right.Should().Be(new ValueExpr.Literal("OptionSetValue", 1));
        ((ConditionExpr.Compare)when.Right).Right.Should().Be(new ValueExpr.Literal("OptionSetValue", 2));
        result.Xaml.Should().Contain("WorkflowPropertyType.Integer, \"42\" }").And.Contain("WorkflowPropertyType.Money, \"1234.56\" }")
            .And.Contain("WorkflowPropertyType.DateTime, \"2026-01-02T03:04:05Z\" }").And.Contain("WorkflowPropertyType.Double, \"45.5\" }");
    }

    [Fact]
    public async Task Arithmetic_concat_now_firstOf_parse()
    {
        var result = await Compile(Account("""
              - update:
                  record: primary
                  set:
                    numberofemployees: { add: [ { field: numberofemployees }, 1 ] }
                    revenue: { multiply: [ { field: revenue }, 2 ] }
                    description: { concat: [ "Hello ", { field: name } ] }
                    lastusedincampaign: { now: true }
                    name: { firstOf: [ { field: name }, Unknown ] }
            """));
        var values = Steps(result).OfType<UpdateStep>().Single().Values;
        var primary = new RecordRef.Primary();
        var add = values["numberofemployees"].Should().BeOfType<ValueExpr.Arithmetic>().Subject;
        add.Operator.Should().Be("Add");
        add.Operands[0].Should().BeOfType<ValueExpr.AttributeOf>().Which.Should().Be(new ValueExpr.AttributeOf(primary, "numberofemployees") { TargetType = "System.Int32" });
        add.Operands[1].Should().Be(new ValueExpr.Literal("Integer", 1));
        values["revenue"].Should().BeOfType<ValueExpr.Arithmetic>().Which.Operator.Should().Be("Multiply");
        var concat = values["description"].Should().BeOfType<ValueExpr.Concat>().Subject;
        concat.Parts[0].Should().Be(new ValueExpr.Literal("String", "Hello "));
        concat.Parts[1].Should().BeOfType<ValueExpr.AttributeOf>().Which.Attribute.Should().Be("name");
        values["lastusedincampaign"].Should().BeOfType<ValueExpr.ExecutionTime>();
        var first = values["name"].Should().BeOfType<ValueExpr.FirstNonNull>().Subject;
        first.Operands.Should().HaveCount(2);
        first.Operands[1].Should().Be(new ValueExpr.Literal("String", "Unknown"));
    }

    [Fact]
    public async Task Realtime_send_email_has_no_persist()
    {
        const string email = "  - sendEmail: { set: { subject: Hi, to: { field: primarycontactid }, regardingobjectid: { record: primary } } }";
        var realtime = await Compile(Account(email, mode: "realtime"));
        realtime.Xaml.Should().NotContain("<Persist");
        Steps(realtime).Should().ContainSingle().Which.Should().BeOfType<SendEmailStep>().Which.Values.Keys.Should().BeEquivalentTo("subject", "to", "regardingobjectid");
        var background = await Compile(Account(email));
        background.Xaml.Should().Contain("<Persist />");
    }

    [Fact]
    public async Task Assumed_shapes_are_reported()
    {
        (await GoldenXamlTests.CompileFixture("onupdate.yaml")).AssumedShapes.Should().BeEmpty();
        (await GoldenXamlTests.CompileFixture("oncreate.yaml")).AssumedShapes.Should().BeEmpty();
        var assign = await Compile(Account($"  - assign: {{ record: primary, owner: {{ lookup: systemuser, id: {FakeAuthoringEnvironment.Ada} }} }}"));
        assign.AssumedShapes.Should().Contain("mxswa:AssignEntity");
        // Equal is confirmed by the calculated-column export (2.12a), so NotEqual stands for an assumed condition operator.
        var condition = await Compile(Account("  - if: { when: { field: name, op: ne, value: A }, then: [ { stop: { status: succeeded } } ] }"));
        condition.AssumedShapes.Should().Contain("EvaluateCondition:NotEqual");
        condition.AssumedShapes.Should().NotContain("EvaluateCondition:Equal");
    }

    private static FakeAuthoringEnvironment WithNormalize()
    {
        var env = new FakeAuthoringEnvironment { WorkflowSdkAvailable = true };
        env.WorkflowActivities.Add(("Example.Activities.Normalize", "Example.Activities"));
        return env;
    }

    private const string Normalize = "type: Example.Activities.Normalize, assembly: Example.Activities";

    [Fact]
    public async Task Custom_activity_outputs_are_named_by_their_step()
    {
        // Review I1: two steps with the same output name each keep their own variable, {step name}_{output name}.
        var result = await Compile(Account(
            $"  - customActivity: {{ id: first, {Normalize}, inputs: {{ Input: {{ field: name }} }}, outputs: {{ Normalized: result }} }}\n" +
            $"  - customActivity: {{ id: second, {Normalize}, inputs: {{ Input: {{ field: description }} }}, outputs: {{ Normalized: result }} }}\n" +
            "  - update: { record: primary, set: { name: { output: result, of: second }, description: { output: result, of: first } } }\n"), WithNormalize());

        var steps = Steps(result);
        steps[0].Should().BeOfType<CustomActivityStep>().Which.Outputs["Normalized"].Should().Be("CustomActivityStep1_result");
        steps[1].Should().BeOfType<CustomActivityStep>().Which.Outputs["Normalized"].Should().Be("CustomActivityStep2_result");
        var update = steps[2].Should().BeOfType<UpdateStep>().Subject;
        update.Values["name"].Should().Be(new ValueExpr.VariableRef("CustomActivityStep2_result"));
        update.Values["description"].Should().Be(new ValueExpr.VariableRef("CustomActivityStep1_result"));
    }

    [Fact]
    public async Task Introduced_version_is_the_solution_version()
    {
        var env = new FakeAuthoringEnvironment();
        env.Solutions["Core"] = "2.3.0.0";
        var yaml = WorkflowYaml.Read(Account("  - stop: { status: canceled }"));
        (await WorkflowCompiler.CompileAsync(yaml, env, "Core", default)).Row.IntroducedVersion.Should().Be("2.3.0.0");
        (await WorkflowCompiler.CompileAsync(yaml, env, null, default)).Row.IntroducedVersion.Should().Be("1.0.0.0");
        var act = () => WorkflowCompiler.CompileAsync(yaml, env, "Nope", default);
        (await act.Should().ThrowAsync<WorkflowCompileException>()).Which.Errors.Should().ContainSingle().Which.Message.Should().Contain("'Nope'");
    }

    [Fact]
    public async Task Activity_table_records_are_referenced_by_activityid()
    {
        // Review I4: an activity table's key is activityid. A custom lookup to task (xrmv_order.xrmv_followuptaskid) takes them: Dataverse
        // lets no activity's regarding target another activity.
        var result = await Compile("""
            kind: workflow
            name: OnTask
            entity: task
            mode: background
            trigger:
              create: true
            steps:
              - create: { id: copy, entity: task, set: { subject: Copy } }
              - create: { entity: xrmv_order, set: { xrmv_followuptaskid: { record: primary } } }
              - create: { entity: xrmv_order, set: { xrmv_followuptaskid: { record: { created: copy } } } }
            """);
        result.Definition.Unsupported.Should().BeEmpty();
        var steps = Steps(result);
        steps[1].Should().BeOfType<CreateStep>().Which.Values["xrmv_followuptaskid"].Should().Be(new ValueExpr.Conversion(
            "System.Guid", "Microsoft.Xrm.Sdk.EntityReference", new ValueExpr.AttributeOf(new RecordRef.Primary(), "activityid"), "task"));
        steps[2].Should().BeOfType<CreateStep>().Which.Values["xrmv_followuptaskid"].Should().Be(new ValueExpr.Conversion(
            "System.Guid", "Microsoft.Xrm.Sdk.EntityReference", new ValueExpr.AttributeOf(new RecordRef.Created("copy"), "activityid"), "task"));
        (await Errors(Account("  - sendEmail: { id: mail, set: { subject: x } }\n  - create: { entity: task, set: { regardingobjectid: { record: { created: mail } } } }\n")))
            .Should().ContainSingle().Which.Message.Should().Contain("'email'");
    }

    [Fact]
    public async Task Every_shape_written_without_an_export_is_reported()
    {
        // Review M1, M7: the shapes the compiler writes that no real export showed, with stable short names.
        (await Compile(Account($"  - customActivity: {{ {Normalize}, inputs: {{ Input: {{ field: name }} }} }}"), WithNormalize()))
            .AssumedShapes.Should().Contain("customActivity:Example.Activities.Normalize");
        (await Compile(Account("  - create: { id: made, entity: task, set: { subject: x } }"))).AssumedShapes.Should().Contain("createdKey:id");
        var email = await Compile(Account("  - sendEmail: { id: mail, set: { subject: x } }", mode: "realtime"));
        email.AssumedShapes.Should().Contain(new[] { "createdKey:id", "sendEmail:assignToId", "sendEmail:noPersist" });
        (await Compile(Account("  - sendEmail: { set: { subject: x } }"))).AssumedShapes.Should().NotContain("sendEmail:noPersist");
        (await Compile(Account("  - stop: { status: succeeded }"))).AssumedShapes.Should().Contain("stop:succeeded").And.NotContain("stop:message");
        (await Compile(Account("  - stop: { status: canceled, message: Bye }"))).AssumedShapes.Should().Contain("stop:message").And.NotContain("stop:succeeded");
        (await Compile(Account("  - stop: { status: canceled }"))).AssumedShapes.Should().BeEmpty();
        var literals = await Compile(Account(
            "  - update: { record: primary, set: { numberofemployees: 3, address1_latitude: 1.5, revenue: 10, lastusedincampaign: 2026-01-01T00:00:00Z, name: x } }"));
        literals.AssumedShapes.Should().Contain(new[] { "literal:Integer", "literal:Double", "literal:Money", "literal:DateTime" }).And.NotContain("literal:String");
    }

    [Fact]
    public async Task Output_names_must_be_identifiers()
    {
        // Review M2: at the YAML position of the name.
        var line = $"  - customActivity: {{ {Normalize}, outputs: {{ Normalized: 1st, Other: '' }} }}";
        var errors = await Errors(Account(line), WithNormalize());
        errors.Should().HaveCount(2);
        errors[0].At.Should().Be(new SourceSpan(8, line.IndexOf("1st", StringComparison.Ordinal) + 1));
        errors[0].Message.Should().Contain("'1st'");
        errors[1].At.Should().Be(new SourceSpan(8, line.IndexOf("''", StringComparison.Ordinal) + 1));
        errors[1].Message.Should().Contain("empty");
    }

    [Theory]
    [InlineData("my-act")]
    [InlineData("True")]
    [InlineData("nothing")]
    [InlineData("ActionStep1_result")]
    [InlineData("stepLabelDescription")]
    public async Task Step_ids_must_be_free_vb_identifiers(string id)
    {
        // Review M3: at the YAML position of the id.
        var line = $"  - performAction: {{ id: '{id}', message: xrmv_Score }}";
        var error = (await Errors(Account(line))).Should().ContainSingle().Subject;
        error.At.Should().Be(new SourceSpan(8, line.IndexOf('\'', StringComparison.Ordinal) + 1));
        error.Message.Should().Contain($"'{id}'");
        (await Errors(Account($"  - create: {{ id: '{id}', entity: task, set: {{ subject: x }} }}"))).Should().ContainSingle();
    }

    [Fact]
    public async Task A_concat_is_refused_as_the_left_operand_of_a_condition()
    {
        // Task 4 (A7 vs A12): the export has no form for a Concat operand, so a document with one could not round-trip.
        var line = "  - if: { when: { concat: [{ field: name }, x], op: eq, value: y }, then: [] }";
        var error = (await Errors(Account(line))).Should().ContainSingle().Subject;
        error.At.Should().Be(new SourceSpan(8, line.IndexOf("{ concat", StringComparison.Ordinal) + 1));
        error.Message.Should().Contain("concat").And.Contain("left operand");
    }

    [Fact]
    public async Task Trigger_column_and_description_errors_point_at_their_node()
    {
        // Review M5.
        var errors = await Errors("""
            kind: workflow
            name: T
            description: "bad \x01"
            entity: account
            mode: background
            trigger:
              update: { columns: [name, nmae] }
            steps:
              - stop: { status: canceled, description: "worse \x02" }
            """);
        errors.Select(e => e.At).Should().BeEquivalentTo(new SourceSpan?[] { new SourceSpan(3, 14), new SourceSpan(7, 29), new SourceSpan(9, 44) });
    }

    [Fact]
    public async Task Xml_invalid_text_in_names_is_a_compile_error()
    {
        // Review M6: never an exception from the writer.
        (await Errors(Account("  - performAction: { message: \"xrmv_\\x01Score\" }"))).Should().ContainSingle().Which.Message.Should().Contain("message");
        (await Errors(Account("  - customActivity: { type: \"Example.\\x01Normalize\", assembly: Example.Activities }"), WithNormalize()))
            .Should().Contain(e => e.Message.Contains("type"));
        (await Errors(Account("  - customActivity: { type: Example.Activities.Normalize, assembly: \"Ex\\x01\" }"), WithNormalize()))
            .Should().Contain(e => e.Message.Contains("assembly"));
        (await Errors(Account("  - create: { id: \"a\\x01\", entity: task, set: { subject: x } }"))).Should().ContainSingle();
    }

    [Fact]
    public async Task Created_record_of_a_perform_action_names_output_instead()
    {
        // Ruled M4: kept refused, with the reason.
        var errors = await Errors(Account("  - performAction: { id: act, message: xrmv_Score }\n  - update: { record: { created: act }, set: { name: x } }\n"));
        errors.Should().ContainSingle().Which.Message.Should().Contain("{ output: <name>, of: act }");
    }

    // ---- perform action and custom activity inputs typed by the callee (ruling R6) ---------------------------------------

    [Fact]
    public async Task Perform_action_and_output_parse_as_ActionStep_and_ActionOutput()
    {
        var result = await Compile(Account("""
              - performAction: { id: act, message: xrmv_Score, target: primary, inputs: { Factor: 2.5, Label: Hi, Count: 3 } }
              - update: { record: primary, set: { description: { output: Score, of: act } } }
            """));
        var steps = Steps(result);
        var action = steps[0].Should().BeOfType<ActionStep>().Subject;
        (action.Message, action.Target, action.Variable).Should().Be(("xrmv_Score", new RecordRef.Primary(), "act"));
        action.Inputs.Should().BeEquivalentTo(new Dictionary<string, ValueExpr>
        {
            ["Factor"] = new ValueExpr.Literal("Decimal", 2.5m),
            ["Label"] = new ValueExpr.Literal("String", "Hi"),
            ["Count"] = new ValueExpr.Literal("Integer", 3),
        }, o => o.RespectingRuntimeTypes());
        steps[1].Should().BeOfType<UpdateStep>().Which.Values["description"].Should().Be(new ValueExpr.ActionOutput("act", "Score"));
        result.Xaml.Should().Contain("<InArgument x:TypeArguments=\"x:Int32\" x:Key=\"Count\">[ActionStep1_3]</InArgument>");
    }

    [Fact]
    public async Task Perform_action_input_unknown_or_of_another_type_is_refused()
    {
        const string line = "  - performAction: { message: xrmv_Score, inputs: { Count: many, Nope: 1 } }";
        var errors = await Errors(Account(line));
        errors.Should().HaveCount(2);
        errors[0].Message.Should().Contain("many").And.Contain("Integer");
        errors[1].At.Should().Be(new SourceSpan(8, line.IndexOf("1 }", StringComparison.Ordinal) + 1));
        errors[1].Message.Should().Contain("'Nope'").And.Contain("xrmv_Score").And.Contain("Factor, Label, Count");
        // Review fix 3: at the message's own position.
        const string unknown = "  - performAction: { message: xrmv_Nothing }";
        var missing = (await Errors(Account(unknown))).Should().ContainSingle().Subject;
        missing.Message.Should().Contain("'xrmv_Nothing'");
        missing.At.Should().Be(new SourceSpan(8, unknown.IndexOf("xrmv_Nothing", StringComparison.Ordinal) + 1));
    }

    [Fact]
    public async Task Perform_action_output_not_of_the_message_is_refused()
    {
        // Review fix 4: the outputs of the called message, at the output's position.
        const string read = "  - update: { record: primary, set: { description: { output: Nope, of: act } } }";
        var error = (await Errors(Account("  - performAction: { id: act, message: xrmv_Score }\n" + read + "\n"))).Should().ContainSingle().Subject;
        error.Message.Should().Contain("'Nope'").And.Contain("Score");
        error.At.Should().Be(new SourceSpan(9, read.IndexOf("{ output", StringComparison.Ordinal) + 1));
        // An unknown message is reported once, not again for each output read.
        (await Errors(Account("  - performAction: { id: act, message: xrmv_Nothing }\n" + read + "\n"))).Should().ContainSingle()
            .Which.Message.Should().Contain("'xrmv_Nothing'");
    }

    [Fact]
    public async Task Custom_activity_step_parses_with_inputs_and_outputs()
    {
        var result = await Compile(Account(
            $"  - customActivity: {{ id: norm, {Normalize}, inputs: {{ Input: Hello, Times: 3 }}, outputs: {{ Normalized: result }} }}\n" +
            "  - update: { record: primary, set: { name: { output: result, of: norm } } }\n"), WithNormalize());
        var steps = Steps(result);
        var custom = steps[0].Should().BeOfType<CustomActivityStep>().Subject;
        (custom.TypeName, custom.AssemblyName).Should().Be(("Example.Activities.Normalize", "Example.Activities"));
        custom.Inputs.Should().BeEquivalentTo(new Dictionary<string, ValueExpr>
        {
            ["Input"] = new ValueExpr.Literal("String", "Hello"),
            ["Times"] = new ValueExpr.Literal("Integer", 3),
        }, o => o.RespectingRuntimeTypes());
        custom.Outputs.Should().Equal(new Dictionary<string, string> { ["Normalized"] = "CustomActivityStep1_result" });
        steps[1].Should().BeOfType<UpdateStep>().Which.Values["name"].Should().Be(new ValueExpr.VariableRef("CustomActivityStep1_result"));
    }

    [Fact]
    public async Task Custom_activity_unknown_input_and_output_are_refused()
    {
        var line = $"  - customActivity: {{ {Normalize}, inputs: {{ Nope: {{ field: name }} }}, outputs: {{ Bogus: x }} }}";
        var errors = await Errors(Account(line), WithNormalize());
        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.Message.Contains("'Nope'") && e.At == new SourceSpan(8, line.IndexOf("{ field", StringComparison.Ordinal) + 1));
        errors.Should().Contain(e => e.Message.Contains("'Bogus'") && e.Message.Contains("Normalized")
                                     && e.At == new SourceSpan(8, line.IndexOf("x }", StringComparison.Ordinal) + 1));
        // An input property is not an output, nor the reverse.
        (await Errors(Account($"  - customActivity: {{ {Normalize}, inputs: {{ Normalized: x }}, outputs: {{ Input: y }} }}"), WithNormalize()))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task Custom_activity_type_not_registered_is_refused()
    {
        var env = new FakeAuthoringEnvironment { WorkflowSdkAvailable = true };
        var errors = await Errors(Account($"  - customActivity: {{ {Normalize}, inputs: {{ Input: {{ field: name }} }} }}"), env);
        errors.Should().ContainSingle().Which.Message.Should().Contain("Example.Activities.Normalize").And.Contain("'Example.Activities'");
    }

    // ---- business rules (A5, A8, A9) -----------------------------------------------------------------------------------

    /// <summary>A business rule on <c>xrmv_order</c> with the given steps (indented as list items).</summary>
    private static string Rule(string steps) => $"kind: businessRule\nname: Rule\nentity: xrmv_order\nsteps:\n{steps}";

    [Fact]
    public async Task Business_rule_row_has_processtriggerscope_2_and_mode_1()
    {
        // The real export (customizations.xml of xrmdWfTest_1_0_0_2.zip).
        var row = (await GoldenXamlTests.CompileFixture("business-rule.yaml")).Row;
        row.Should().BeEquivalentTo(new
        {
            Id = new Guid("216fa344-e5de-f111-84e8-002248872b66"), Name = "New business rule", Description = "Click to add description",
            Category = 2, Type = 1, Mode = 1, Scope = 4, PrimaryEntity = "xrmv_order", TriggerOnCreate = false, TriggerOnDelete = false,
            TriggerOnUpdateAttributes = (string?)null, CreateStage = (int?)null, UpdateStage = (int?)null, DeleteStage = (int?)null, RunAs = 1,
            OnDemand = false, Subprocess = false, AsyncAutoDelete = false, SyncWorkflowLogOnFailure = false, IsTransacted = true,
            IsCustomizable = true, IntroducedVersion = "1.0.0.0", StateCode = 0, ProcessTriggerScope = (int?)2, UniqueName = (string?)null,
        });
        row.Xaml.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Set_default_clear_and_client_only_parse_as_their_steps()
    {
        var result = await Compile(Rule("""
              - if:
                  when: { field: xrmv_amount, op: notNull }
                  then:
                    - setDefault: { field: xrmv_status, value: 128830000 }
                    - clearValue: { field: xrmv_name }
                    - setVisibility: { field: xrmv_name, visible: false }
                    - setLock: { field: xrmv_amount, locked: true }
                    - setRequirement: { field: xrmv_status, level: required, description: must say }
            """));
        result.Definition.Unsupported.Should().BeEmpty();
        var then = ((SequenceStep)Steps(result).OfType<ConditionStep>().Single().Branches.Single().Then).Steps;
        then.Should().HaveCount(5);
        then[0].Should().BeEquivalentTo(new SetValueStep("0/0/2/then/0", "SetDefaultValueStep_ConditionBranchStep2_1", "xrmv_status",
            new ValueExpr.Literal("OptionSetValue", 128830000), DefaultOnly: true), o => o.RespectingRuntimeTypes());
        then[1].Should().BeEquivalentTo(new SetValueStep("0/0/2/then/1", "SetAttributeValueStep_ConditionBranchStep2_2", "xrmv_name",
            new ValueExpr.Literal("Null", null), DefaultOnly: false), o => o.RespectingRuntimeTypes());
        then[2].Should().Be(new ClientOnlyStep("0/0/2/then/2", "SetVisibilityStep_ConditionBranchStep2_3", "SetVisibility") { Attribute = "xrmv_name", Setting = "False" });
        then[3].Should().Be(new ClientOnlyStep("0/0/2/then/3", "SetDisplayModeStep_ConditionBranchStep2_4", "SetDisplayMode") { Attribute = "xrmv_amount", Setting = "True" });
        then[4].Should().Be(new ClientOnlyStep("0/0/2/then/4", "SetFieldRequiredLevelStep_ConditionBranchStep2_5: must say", "SetFieldRequiredLevel")
        {
            Attribute = "xrmv_status", Setting = "2",
        });
        result.Xaml.Should().Contain("<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"IsVisible\">False</InArgument>")
            .And.Contain("<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"IsReadOnly\">True</InArgument>")
            .And.Contain("<InArgument x:TypeArguments=\"x:Int32\" x:Key=\"RequiredLevel\">2</InArgument>");
        result.AssumedShapes.Should().Contain(new[]
        {
            "Microsoft.Crm.Workflow.Activities.SetDefaultValue", "Microsoft.Crm.Workflow.Activities.SetVisibility",
            "Microsoft.Crm.Workflow.Activities.SetDisplayMode", "Microsoft.Crm.Workflow.Activities.SetFieldRequiredLevel", "clearValue",
        });
    }

    [Fact]
    public async Task Rule_actions_are_named_after_the_first_branch_of_their_condition()
    {
        // A8: the actions count over the whole rule; each is named after the first branch of the condition that holds it.
        var result = await Compile(Rule("""
              - if:
                  when: { field: xrmv_amount, op: null }
                  then: [ { setValue: { field: xrmv_name, value: a } } ]
                  else: [ { setValue: { field: xrmv_name, value: b } } ]
              - if:
                  when: { field: xrmv_amount, op: gt, value: 10 }
                  then: [ { setValue: { field: xrmv_name, value: c } } ]
            """));
        var names = result.Definition.Root.DepthFirst().OfType<SetValueStep>().Select(s => s.DisplayName);
        names.Should().Equal("SetAttributeValueStep_ConditionBranchStep2_1", "SetAttributeValueStep_ConditionBranchStep2_2",
            "SetAttributeValueStep_ConditionBranchStep5_3");
    }

    [Fact]
    public async Task Show_error_message_is_the_step_label()
    {
        var result = await Compile(Rule("""
              - if:
                  when: { field: xrmv_amount, op: lt, value: 0 }
                  then:
                    - showError: { field: xrmv_amount, message: 'Amount "too" low & <odd>' }
                    - showError: { message: Anywhere }
            """));
        var then = ((SequenceStep)Steps(result).OfType<ConditionStep>().Single().Branches.Single().Then).Steps;
        var error = then[0].Should().BeOfType<ShowErrorStep>().Subject;
        error.Message.Should().Be(new ValueExpr.Literal("String", "Amount \"too\" low & <odd>"));
        error.Attribute.Should().Be("xrmv_amount");
        result.Xaml.Should().Contain("<mcwo:StepLabel Description=\"Amount &quot;too&quot; low &amp; &lt;odd&gt;\"");
        then[1].Should().BeOfType<ShowErrorStep>().Which.Attribute.Should().BeNull();
        result.AssumedShapes.Should().Contain("showError:noField");
    }

    [Fact]
    public async Task Rule_label_ids_are_uuid_v5()
    {
        var id = new Guid("216fa344-e5de-f111-84e8-002248872b66");
        var result = await GoldenXamlTests.CompileFixture("business-rule.yaml");
        result.Xaml.Should().Contain($"LabelId=\"{WorkflowIdentity.LabelId(id, "SetMessageStep_ConditionBranchStep2_2")}\"");
        // The x:Class of a rule is zeros (export), but the labels are the process's own.
        result.Xaml.Should().Contain("x:Class=\"XrmWorkflow00000000000000000000000000000000\"");
        var other = await Compile(Rule("  - if: { when: { field: xrmv_amount, op: null }, then: [ { showError: { field: xrmv_status, message: fdsfds } } ] }"));
        other.Xaml.Should().Contain($"LabelId=\"{WorkflowIdentity.LabelId(other.Row.Id, "SetMessageStep_ConditionBranchStep2_1")}\"");
    }

    [Fact]
    public async Task Rule_action_outside_a_condition_or_with_an_empty_value_is_refused()
    {
        (await Errors(Rule("  - setValue: { field: xrmv_name, value: a }"))).Should().ContainSingle().Which.Message.Should().Contain("condition");
        (await Errors(Rule("  - if: { when: { field: xrmv_amount, op: null }, then: [ { setValue: { field: xrmv_name, value: ~ } } ] }")))
            .Should().ContainSingle().Which.Message.Should().Contain("clearValue");
        (await Errors(Rule("  - if: { when: { field: xrmv_amount, op: null }, then: [ { clearValue: { field: nope } } ] }")))
            .Should().ContainSingle().Which.Message.Should().Contain("'nope'");
        (await Errors(Rule("  - if: { when: { field: xrmv_amount, op: null }, then: [ { setLock: { field: nope, locked: true } } ] }")))
            .Should().ContainSingle().Which.Message.Should().Contain("'nope'");
        (await Errors(Rule("  - if: { when: { field: xrmv_amount, op: null }, then: [ { setValue: { field: createdon, value: 2026-01-01T00:00:00Z } } ] }")))
            .Should().ContainSingle().Which.Message.Should().Contain("createdon");
    }

    [Fact]
    public async Task Every_rule_and_action_shape_written_without_an_export_is_reported()
    {
        // Review fix 1: argument types other than decimal and entityReference (A15), a global action, and the rule conditions the
        // one exported rule does not show.
        const string types = """
              - { name: B, direction: input, type: boolean }
              - { name: Dt, direction: input, type: dateTime }
              - { name: De, direction: input, type: decimal }
              - { name: Dbl, direction: input, type: double }
              - { name: Er, direction: input, type: entityReference, entity: account }
              - { name: I, direction: input, type: integer }
              - { name: M, direction: input, type: money }
              - { name: P, direction: input, type: picklist }
              - { name: S, direction: output, type: string }

            """;
        var action = await Compile(Action("  - stop: { status: canceled }\n", types));
        action.AssumedShapes.Should().Contain(new[]
        {
            "argument:boolean", "argument:dateTime", "argument:double", "argument:integer", "argument:money", "argument:picklist", "argument:string",
        }).And.NotContain(new[] { "argument:decimal", "argument:entityReference", "action:global" });
        (await Compile(Action("  - stop: { status: canceled }\n", entity: "none"))).AssumedShapes.Should().Contain("action:global");
        (await GoldenXamlTests.CompileFixture("recalculate.yaml")).AssumedShapes.Should().BeEmpty();

        var plain = await Compile(Rule("  - if: { when: { field: xrmv_amount, op: null }, then: [ { clearValue: { field: xrmv_name } } ] }"));
        plain.AssumedShapes.Should().Contain("ruleCondition:noDescription").And.NotContain(new[] { "ruleCondition:elseIfDescription", "ruleCondition:nested" });
        var elseIf = await Compile(Rule("""
              - if:
                  description: Amount
                  when: { field: xrmv_amount, op: null }
                  then: [ { clearValue: { field: xrmv_name } } ]
                  elseIf:
                    - when: { field: xrmv_amount, op: gt, value: 5 }
                      then: [ { clearValue: { field: xrmv_name } } ]
            """));
        elseIf.AssumedShapes.Should().Contain("ruleCondition:elseIfDescription").And.NotContain("ruleCondition:noDescription");
        elseIf.Xaml.Split("<x:String x:Key=\"Description\">Amount</x:String>").Should().HaveCount(3);
        var nested = await Compile(Rule("""
              - if:
                  description: Outer
                  when: { field: xrmv_amount, op: null }
                  then:
                    - if:
                        description: Inner
                        when: { field: xrmv_status, op: null }
                        then: [ { clearValue: { field: xrmv_name } } ]
            """));
        nested.AssumedShapes.Should().Contain("ruleCondition:nested");
        nested.Definition.Root.DepthFirst().OfType<SetValueStep>().Single().DisplayName.Should().Be("SetAttributeValueStep_ConditionBranchStep4_1");
        (await GoldenXamlTests.CompileFixture("business-rule.yaml")).AssumedShapes.Should().BeEmpty();
    }

    // ---- actions (A15) -------------------------------------------------------------------------------------------------

    /// <summary>An action <c>xrmv_Act</c> on <paramref name="entity"/> with the given arguments (YAML list items) and steps.</summary>
    private static string Action(string steps, string arguments = "", string entity = "xrmv_order") =>
        $"kind: action\nname: Act\nentity: {entity}\nmessage: xrmv_Act\n{(arguments.Length == 0 ? "" : "arguments:\n" + arguments)}steps:\n{steps}";

    [Fact]
    public async Task Action_message_is_split_into_prefix_and_uniquename()
    {
        WorkflowCompiler.SplitMessage("xrmv_Recalculate", "xrmv").Should().Be("Recalculate");
        WorkflowCompiler.SplitMessage("xrmv_Re_calc", "xrmv").Should().Be("Re_calc");
        // The real export (customizations.xml): UniqueName without the prefix, the defaults of an action.
        var row = (await GoldenXamlTests.CompileFixture("recalculate.yaml")).Row;
        row.Should().BeEquivalentTo(new
        {
            Id = new Guid("9df696a1-e6de-f111-84e8-002248872b66"), Name = "Recalculate", Category = 3, Type = 1, Mode = 0, Scope = 4,
            PrimaryEntity = "xrmv_order", UniqueName = "Recalculate", CreateStage = (int?)40, RunAs = 1, SyncWorkflowLogOnFailure = true,
            AsyncAutoDelete = false, IsTransacted = true, IsCustomizable = true, TriggerOnCreate = false, ProcessTriggerScope = (int?)null,
            OnDemand = false, Subprocess = false,
        });
    }

    [Fact]
    public async Task Action_prefix_mismatch_is_refused()
    {
        var act = () => WorkflowCompiler.SplitMessage("abc_Do", "xrmv");
        var fault = act.Should().Throw<Xrmd.Core.Errors.XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("'abc'").And.Contain("'xrmv'");
        var errors = await Errors(Action("  - stop: { status: succeeded }").Replace("xrmv_Act", "abc_Act"));
        errors.Should().ContainSingle().Which.At.Should().Be(new SourceSpan(4, 10));
        errors[0].Message.Should().Contain("'abc'").And.Contain("'xrmv'");
        (await Errors(Action("  - stop: { status: succeeded }").Replace("xrmv_Act", "Act"))).Should().ContainSingle().Which.Message.Should().Contain("xrmv_");
        (await Errors(Action("  - stop: { status: succeeded }").Replace("xrmv_Act", "xrmv_A-b"))).Should().ContainSingle();
        // Review fix 5: one case rule, case-sensitive.
        WorkflowCompiler.SplitMessage("xrmv_XRMV_Do", "xrmv").Should().Be("XRMV_Do");
        var repeated = () => WorkflowCompiler.SplitMessage("xrmv_xrmv_Do", "xrmv");
        repeated.Should().Throw<Xrmd.Core.Errors.XrmdFaultException>().Which.Message.Should().Contain("repeats");
        var upper = () => WorkflowCompiler.SplitMessage("XRMV_Do", "xrmv");
        upper.Should().Throw<Xrmd.Core.Errors.XrmdFaultException>().Which.Message.Should().Contain("'XRMV'");
    }

    [Fact]
    public async Task Global_action_has_no_target()
    {
        var result = await Compile(Action("  - setOutput: { argument: Echo, value: { arg: Text } }\n",
            "  - { name: Text, direction: input, type: string }\n  - { name: Echo, direction: output, type: string }\n", entity: "none"));
        result.Row.PrimaryEntity.Should().Be("none");
        result.Row.UniqueName.Should().Be("Act");
        result.Xaml.Should().NotContain("Name=\"Target\"").And.NotContain(".Target>").And.NotContain("ArgumentTargetAttribute Value=\"True\"");
        result.Definition.Arguments.Should().NotContain(a => a.IsTarget);
        result.Definition.Arguments.Select(a => a.Name).Should().Equal("Text", "Echo");
        Steps(result).Should().ContainSingle().Which.Should().BeOfType<AssignOutputStep>().Which.Argument.Should().Be("Echo");
        (await Errors(Action("  - update: { record: primary, set: { xrmv_name: x } }\n", entity: "none")))
            .Should().ContainSingle().Which.Message.Should().Contain("global action");
    }

    [Fact]
    public async Task Each_argument_type_parses_with_its_clr_type()
    {
        var result = await Compile(Action("  - stop: { status: succeeded }\n", """
              - { name: B, direction: input, type: boolean }
              - { name: Dt, direction: input, type: dateTime }
              - { name: De, direction: input, type: decimal, required: true }
              - { name: Dbl, direction: input, type: double }
              - { name: Er, direction: input, type: entityReference, entity: account }
              - { name: I, direction: input, type: integer }
              - { name: M, direction: input, type: money }
              - { name: P, direction: input, type: picklist }
              - { name: S, direction: output, type: string, description: the text }

            """));
        result.Definition.Unsupported.Should().BeEmpty();
        result.Definition.Arguments.Select(a => (a.Name, a.ClrType, a.IsInput, a.EntityTarget)).Should().Equal(
            ("B", "System.Boolean", true, null), ("Dt", "System.DateTime", true, null), ("De", "System.Decimal", true, null),
            ("Dbl", "System.Double", true, null), ("Er", "Microsoft.Xrm.Sdk.EntityReference", true, "account"),
            ("I", "System.Int32", true, null), ("M", "Microsoft.Xrm.Sdk.Money", true, null),
            ("P", "Microsoft.Xrm.Sdk.OptionSetValue", true, null), ("S", "System.String", false, null),
            ("Target", "Microsoft.Xrm.Sdk.EntityReference", true, "xrmv_order"));
        result.Definition.Arguments.Single(a => a.Name == "De").Required.Should().BeTrue();
        result.Definition.Arguments.Single(a => a.Name == "S").Description.Should().Be("the text");
        result.Definition.Arguments.Single(a => a.Name == "Target").IsTarget.Should().BeTrue();
    }

    [Fact]
    public async Task Arguments_are_read_and_set_typed()
    {
        var result = await Compile(Action("""
              - update: { record: primary, set: { xrmv_amount: { arg: Amount }, xrmv_name: { arg: Name } } }
              - if:
                  when: { arg: Amount, op: gt, value: 5 }
                  then: [ { setOutput: { argument: Count, value: 7 } } ]
            """, "  - { name: Amount, direction: input, type: decimal }\n  - { name: Name, direction: input, type: string }\n" +
                 "  - { name: Count, direction: output, type: integer }\n"));
        var steps = Steps(result);
        steps[0].Should().BeOfType<UpdateStep>().Which.Values["xrmv_amount"].Should().Be(new ValueExpr.VariableRef("Amount"));
        var condition = steps[1].Should().BeOfType<ConditionStep>().Subject;
        condition.Branches[0].When.Should().Be(new ConditionExpr.Compare("GreaterThan", new ValueExpr.VariableRef("Amount"), new ValueExpr.Literal("Decimal", 5m)));
        ((SequenceStep)condition.Branches[0].Then).Steps.Single().Should().BeOfType<AssignOutputStep>()
            .Which.Value.Should().Be(new ValueExpr.Cast("System.Int32", new ValueExpr.Literal("Integer", 7)));
        result.Xaml.Should().Contain("Default=\"System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\"")
            .And.Contain("<Assign x:TypeArguments=\"x:Int32\" To=\"[Count]\"");
        (await Errors(Action("  - update: { record: primary, set: { xrmv_amount: { arg: Name } } }\n", "  - { name: Name, direction: input, type: string }\n")))
            .Should().ContainSingle().Which.Message.Should().Contain("Name");
        (await Errors(Action("  - update: { record: primary, set: { xrmv_name: { arg: Nope } } }\n"))).Should().ContainSingle().Which.Message.Should().Contain("'Nope'");
        // Names the designer writes itself (the output assignment's TypeName variable) and VB keywords are no argument names.
        (await Errors(Action("  - stop: { status: succeeded }\n", "  - { name: TypeName, direction: output, type: string }\n")))
            .Should().ContainSingle().Which.Message.Should().Contain("generates");
        (await Errors(Action("  - stop: { status: succeeded }\n", "  - { name: Do, direction: input, type: string }\n")))
            .Should().ContainSingle().Which.Message.Should().Contain("keyword");
    }

    [Fact]
    public async Task Set_output_of_unknown_argument_is_refused()
    {
        var errors = await Errors(Action("  - setOutput: { argument: Nope, value: 1 }\n", "  - { name: Total, direction: output, type: decimal }\n"));
        errors.Should().ContainSingle().Which.Message.Should().Contain("'Nope'").And.Contain("Total");
        (await Errors(Action("  - setOutput: { argument: Inp, value: 1 }\n", "  - { name: Inp, direction: input, type: decimal }\n")))
            .Should().ContainSingle().Which.Message.Should().Contain("input");
        (await Errors(Action("  - setOutput: { argument: Total, value: lots }\n", "  - { name: Total, direction: output, type: decimal }\n")))
            .Should().ContainSingle().Which.Message.Should().Contain("lots");
    }

    // ---- refusals (A10) ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Metadata_errors_are_all_reported_with_line_and_column()
    {
        var errors = await Errors("""
            kind: workflow
            name: Broken
            entity: account
            mode: background
            steps:
              - create:
                  entity: tsak
                  set: { subject: x }
              - update:
                  record: primary
                  set:
                    nmae: x
                    industrycode: 7
                    numberofemployees: lots
            """);
        errors.Select(e => (e.At, e.Message)).Should().SatisfyRespectively(
            e => { e.At.Should().Be(new SourceSpan(6, 5)); e.Message.Should().Contain("'tsak'"); },
            e => { e.At.Should().Be(new SourceSpan(12, 15)); e.Message.Should().Contain("'nmae'").And.Contain("account"); },
            e => { e.At.Should().Be(new SourceSpan(13, 23)); e.Message.Should().Contain("7").And.Contain("industrycode"); },
            e => { e.At.Should().Be(new SourceSpan(14, 28)); e.Message.Should().Contain("lots").And.Contain("Integer"); });
    }

    [Fact]
    public async Task Column_not_valid_for_create_is_refused()
    {
        var errors = await Errors(Account("  - create: { entity: task, set: { subject: x, createdon: 2026-01-01T00:00:00Z } }"));
        errors.Should().ContainSingle().Which.Message.Should().Contain("createdon").And.Contain("create");
        (await Errors(Account("  - update: { record: primary, set: { createdon: 2026-01-01T00:00:00Z } }")))
            .Should().ContainSingle().Which.Message.Should().Contain("createdon").And.Contain("update");
    }

    [Fact]
    public async Task Trigger_column_not_on_the_table_is_refused()
    {
        var errors = await Errors("kind: workflow\nname: T\nentity: account\nmode: background\ntrigger:\n  update: { columns: [name, nmae] }\nsteps:\n  - stop: { status: succeeded }\n");
        errors.Should().ContainSingle().Which.Message.Should().Contain("'nmae'");
    }

    [Fact]
    public async Task Status_not_of_the_state_is_refused()
    {
        var errors = await Errors(Account("  - setState: { record: primary, state: 1, status: 1 }"));
        errors.Should().ContainSingle().Which.Message.Should().Contain("1");
    }

    [Fact]
    public async Task Output_of_unknown_type_compares_with_dynamic_values_only()
    {
        const string Head = "  - performAction: { id: act, message: xrmv_Score, target: primary }\n  - if:\n      when: ";
        var errors = await Errors(Account(Head + "{ output: Score, of: act, op: eq, value: 3 }\n      then:\n        - stop: { status: succeeded }\n"));
        errors.Should().ContainSingle().Which.Message.Should().Contain("not known");
        var result = await Compile(Account(Head + "{ output: Score, of: act, op: eq, value: { field: numberofemployees } }\n      then:\n        - stop: { status: succeeded }\n"));
        result.Xaml.Should().Contain("[act(\"Score\")]").And.Contain("Value=\"x:Int32\"").And.NotContain("Value=\"x:Object\"");
    }

    [Fact]
    public async Task Option_value_not_in_choice_is_refused()
    {
        var errors = await Errors(Account("  - create: { entity: task, set: { prioritycode: 9 } }"));
        errors.Should().ContainSingle().Which.Message.Should().Contain("9").And.Contain("prioritycode").And.Contain("0, 1, 2");
    }

    [Fact]
    public async Task Text_into_decimal_is_refused()
    {
        var errors = await Errors("""
            kind: workflow
            name: T
            entity: xrmv_order
            mode: realtime
            steps:
              - update: { record: primary, set: { xrmv_amount: twelve } }
            """);
        errors.Should().ContainSingle().Which.Message.Should().Contain("twelve").And.Contain("Decimal");
    }

    [Fact]
    public async Task Reference_into_text_column_is_refused()
    {
        var errors = await Errors(Account("  - update: { record: primary, set: { name: { record: primary } } }"));
        errors.Should().ContainSingle().Which.Message.Should().Contain("name").And.Contain("reference");
        (await Errors(Account("  - create: { entity: task, set: { regardingobjectid: Contoso } }")))
            .Should().ContainSingle().Which.Message.Should().Contain("lookup");
    }

    [Fact]
    public async Task Lookup_literal_to_missing_record_is_refused()
    {
        var missing = Guid.Parse("00000000-0000-4000-8000-0000000000ff");
        var errors = await Errors(Account($"  - create: {{ entity: task, set: {{ regardingobjectid: {{ lookup: account, id: {missing} }} }} }}"));
        errors.Should().ContainSingle().Which.Message.Should().Contain(missing.ToString()).And.Contain("account");
        (await Errors(Account($"  - create: {{ entity: task, set: {{ regardingobjectid: {{ lookup: systemuser, id: {FakeAuthoringEnvironment.Ada} }} }} }}")))
            .Should().ContainSingle().Which.Message.Should().Contain("systemuser").And.Contain("regardingobjectid");
    }

    [Fact]
    public async Task Lookup_literal_name_defaults_to_primary_name()
    {
        var result = await Compile(Account($"  - create: {{ entity: task, set: {{ regardingobjectid: {{ lookup: account, id: {FakeAuthoringEnvironment.Contoso} }} }} }}"));
        result.Xaml.Should().Contain($"WorkflowPropertyType.EntityReference, \"account\", \"Contoso\", CreateStep1_2, \"Lookup\" }}");
        result.Xaml.Should().Contain($"WorkflowPropertyType.Guid, \"{FakeAuthoringEnvironment.Contoso}\", \"Key\" }}");
        Steps(result).OfType<CreateStep>().Single().Values["regardingobjectid"]
            .Should().Be(new ValueExpr.Literal("EntityReference", new EntityRefValue("account", FakeAuthoringEnvironment.Contoso)));
        var named = await Compile(Account($"  - create: {{ entity: task, set: {{ regardingobjectid: {{ lookup: account, id: {FakeAuthoringEnvironment.Contoso}, name: Other }} }} }}"));
        named.Xaml.Should().Contain("\"account\", \"Other\",");
    }

    [Fact]
    public async Task Child_workflow_by_name_resolves_id()
    {
        const string yaml = "kind: workflow\nname: P\nentity: xrmv_order\nmode: realtime\nsteps:\n  - startChild: { workflow: Child }\n";
        var step = Steps(await Compile(yaml)).Should().ContainSingle().Which.Should().BeOfType<ChildWorkflowStep>().Subject;
        step.WorkflowId.Should().Be(FakeAuthoringEnvironment.ChildId);
        step.Record.Should().Be(new RecordRef.Primary());
        var byId = Steps(await Compile(yaml.Replace("Child }", FakeAuthoringEnvironment.ChildId + " }")));
        byId.OfType<ChildWorkflowStep>().Single().WorkflowId.Should().Be(FakeAuthoringEnvironment.ChildId);
        (await Errors(yaml.Replace("Child }", "Nobody }"))).Should().ContainSingle().Which.Message.Should().Contain("Nobody");
        (await Errors(Account("  - startChild: { workflow: Child }"))).Should().ContainSingle().Which.Message.Should().Contain("xrmv_order");
    }

    [Fact]
    public async Task Child_that_is_not_a_child_process_is_refused()
    {
        var errors = await Errors("kind: workflow\nname: P\nentity: xrmv_order\nmode: realtime\nsteps:\n  - startChild: { workflow: OnCreate }\n");
        errors.Should().ContainSingle().Which.Message.Should().Contain("OnCreate").And.Contain("child process");
    }

    [Fact]
    public async Task Ambiguous_child_name_is_refused_with_ids()
    {
        var errors = await Errors("kind: workflow\nname: P\nentity: xrmv_order\nmode: realtime\nsteps:\n  - startChild: { workflow: Twin }\n");
        errors.Should().ContainSingle().Which.Message.Should().Contain(FakeAuthoringEnvironment.TwinA.ToString()).And.Contain(FakeAuthoringEnvironment.TwinB.ToString());
    }

    [Fact]
    public async Task Custom_activity_without_workflow_sdk_is_refused()
    {
        var env = new FakeAuthoringEnvironment { WorkflowSdkAvailable = false };
        env.WorkflowActivities.Add(("Example.Activities.Normalize", "Example.Activities"));
        var errors = await Errors(Account("  - customActivity: { type: Example.Activities.Normalize, assembly: Example.Activities, inputs: { Input: { field: name } } }"), env);
        errors.Should().ContainSingle().Which.Message.Should().Contain(XamlWorkflowParser.WorkflowSdkMissingReason);
    }

    [Fact]
    public async Task Template_email_and_recommendations_name_the_alternative()
    {
        foreach (var kind in new[] { "sendEmailFromTemplate", "recommendation" })
        {
            var act = () => WorkflowCompiler.CompileAsync(Account($"  - {kind}: {{ template: x }}"), new FakeAuthoringEnvironment(), null, default);
            var error = (await act.Should().ThrowAsync<WorkflowCompileException>()).Which.Errors.Should().ContainSingle().Subject;
            error.At.Should().Be(new SourceSpan(8, 5));
            error.Message.Should().Contain("webhook").And.Contain("custom workflow activity");
        }
    }

    [Fact]
    public async Task Special_characters_survive_compile_and_run_in_parser()
    {
        const string text = "He said \"hi\" & <left> ] [x] èé \U0001F600\nsecond line";
        var yaml = Account("""
              - create:
                  entity: task
                  description: "Quote \" amp & lt < bracket ] è \U0001F600"
                  set:
                    subject: "He said \"hi\" & <left> ] [x] èé \U0001F600\nsecond line"
            """);
        var result = await Compile(yaml);
        var parsed = XamlWorkflowParser.Parse(result.Xaml, 0, workflowSdkAvailable: false);
        parsed.Unsupported.Should().BeEmpty();
        var create = Steps(result).OfType<CreateStep>().Single();
        create.Values["subject"].Should().Be(new ValueExpr.Literal("String", text));
        create.DisplayName.Should().Be("CreateStep1: Quote \" amp & lt < bracket ] è \U0001F600");
        result.Xaml.Should().Contain("\"He said \"\"hi\"\" &amp; &lt;left&gt; ] [x]");
    }
}
