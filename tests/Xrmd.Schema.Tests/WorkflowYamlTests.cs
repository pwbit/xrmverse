using FluentAssertions;

namespace Xrmd.Schema.Tests;

public class WorkflowYamlTests
{
    private static readonly string[] Fixtures = ["oncreate.yaml", "onupdate.yaml", "child.yaml", "business-rule.yaml", "recalculate.yaml"];

    private static string Fixture(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "yaml", file);
        File.Exists(path).Should().BeTrue($"the committed fixture fixtures/workflows/yaml/{file} must exist");
        return File.ReadAllText(path);
    }

    private static WorkflowDocument Read(string file) => WorkflowYaml.Read(Fixture(file));

    private static SchemaFormatException Refused(string yaml) =>
        FluentActions.Invoking(() => WorkflowYaml.Read(yaml)).Should().Throw<SchemaFormatException>().Which;

    /// <summary>A real-time workflow around <paramref name="steps"/> (indented two spaces under <c>steps:</c>).</summary>
    private static string Realtime(string steps, string header = "") =>
        $"kind: workflow\nname: T\nentity: xrmv_order\nmode: realtime\n{header}steps:\n{steps}\n";

    [Fact]
    public void Reads_each_fixture()
    {
        var onCreate = Read("oncreate.yaml");
        onCreate.Kind.Should().Be(WorkflowKind.Workflow);
        onCreate.Entity.Should().Be("xrmv_order");
        onCreate.Id.Should().Be(Guid.Parse("3e1f0307-d8de-f111-84e8-002248872b66"));
        onCreate.Options.Mode.Should().Be("realtime");
        onCreate.Options.Scope.Should().Be("organization");
        onCreate.Trigger!.Create.Should().NotBeNull();
        onCreate.Steps.Select(s => s.GetType()).Should().Equal(typeof(CreateNode), typeof(StartChildNode));
        var create = (CreateNode)onCreate.Steps[0];
        create.Entity.Should().Be("task");
        create.Set.Select(e => e.Column).Should().Equal("subject", "regardingobjectid", "prioritycode");
        create.Set[0].Value.Should().BeOfType<LiteralNode>().Which.Text.Should().Be("TEST");
        create.Set[1].Value.Should().BeOfType<RecordValueNode>().Which.Record.Should().BeOfType<RecordNode.Primary>();
        ((StartChildNode)onCreate.Steps[1]).Workflow.Should().Be("Child");

        var onUpdate = Read("onupdate.yaml");
        onUpdate.Options.Mode.Should().Be("background");
        onUpdate.Options.AutoDelete.Should().BeTrue();
        onUpdate.Trigger!.Update!.Columns.Should().Equal("xrmv_status");
        onUpdate.Steps.Should().ContainSingle().Which.Should().BeOfType<WaitNode>();
        var wait = (WaitNode)onUpdate.Steps[0];
        wait.Until.Should().BeOfType<CompareNode>().Which.Op.Should().Be("gt");
        wait.Timeout.Should().BeNull();
        var email = wait.Then.Should().ContainSingle().Which.Should().BeOfType<SendEmailNode>().Which;
        email.Set.Select(e => e.Column).Should().Equal(
            "to", "regardingobjectid", "correlatedsubjectchanged", "deliveryprioritycode", "directioncode",
            "followemailuserpreference", "isduplicatesenderunresolved", "notifications", "prioritycode", "transactioncurrencyid");
        email.Set[0].Value.Should().BeOfType<FieldNode>().Which.Record.Should().Be(new RecordNode.Related("xrmv_accountid"));
        email.Set[9].Value.Should().BeOfType<LookupNode>().Which.Name.Should().Be("Euro");

        var child = Read("child.yaml");
        child.Options.ChildProcess.Should().BeTrue();
        child.Steps.Should().ContainSingle().Which.Should().BeOfType<StopNode>().Which.Canceled.Should().BeTrue();

        var rule = Read("business-rule.yaml");
        rule.Kind.Should().Be(WorkflowKind.BusinessRule);
        rule.Description.Should().Be("Click to add description");
        var branch = rule.Steps.Should().ContainSingle().Which.Should().BeOfType<IfNode>().Which;
        branch.Description.Should().Be("New Condition");
        branch.Branches.Should().ContainSingle().Which.When.Should().BeOfType<CompareNode>().Which.Right.Should().BeNull();
        branch.Branches[0].Then.Should().ContainSingle().Which.Should().BeOfType<SetValueNode>().Which.Value
            .Should().BeOfType<LiteralNode>().Which.Text.Should().Be("128830001");
        branch.Else.Should().ContainSingle().Which.Should().BeOfType<ShowErrorNode>().Which.Message.Should().Be("fdsfds");

        var action = Read("recalculate.yaml");
        action.Kind.Should().Be(WorkflowKind.Action);
        action.Message.Should().Be("xrmv_Recalculate");
        action.Arguments.Select(a => (a.Name, a.IsInput, a.Type, a.Required)).Should().Equal(("Increment", true, "decimal", true), ("Total", false, "decimal", true));
        action.Steps.Select(s => s.GetType()).Should().Equal(typeof(UpdateNode), typeof(SetOutputNode), typeof(IfNode));
        ((SetOutputNode)action.Steps[1]).Description.Should().Be("Total\\");
        ((UpdateNode)action.Steps[0]).Set[0].Value.Should().BeOfType<OperatorNode>().Which.Operands
            .Should().HaveCount(2);
    }

    [Theory]
    [InlineData("oncreate.yaml")]
    [InlineData("onupdate.yaml")]
    [InlineData("child.yaml")]
    [InlineData("business-rule.yaml")]
    [InlineData("recalculate.yaml")]
    public void Write_of_read_fixture_is_the_same_text(string file)
    {
        var text = Fixture(file);
        WorkflowYaml.Write(WorkflowYaml.Read(text)).Should().Be(text);
    }

    [Fact]
    public void Scalars_stay_text()
    {
        var doc = WorkflowYaml.Read(Realtime(
            "  - create:\n      entity: task\n      set:\n        a: true\n        b: 1\n        c: 2026-01-01\n        d: ~\n        e: 'null'\n        f: ''\n"));
        var set = ((CreateNode)doc.Steps[0]).Set.Select(e => ((LiteralNode)e.Value).Text).ToArray();
        set.Should().Equal("true", "1", "2026-01-01", null, "null", "");
        var again = WorkflowYaml.Read(WorkflowYaml.Write(doc));
        ((CreateNode)again.Steps[0]).Set.Select(e => ((LiteralNode)e.Value).Text).Should().Equal(set);
    }

    [Fact]
    public void Unknown_key_is_refused_with_line_and_column()
    {
        var ex = Refused("kind: workflow\nname: T\nentity: xrmv_order\nmode: realtime\n  \nbogus: 1\nsteps: []\n");
        ex.Line.Should().Be(6);
        ex.Column.Should().Be(1);
        ex.Message.Should().Contain("bogus");

        var step = Refused(Realtime("  - create:\n      entity: task\n      set: {}\n      bogus: 1"));
        step.Line.Should().Be(9);
        step.Column.Should().Be(7);
    }

    [Fact]
    public void Duplicate_key_is_refused()
    {
        var ex = Refused("kind: workflow\nname: A\nname: B\nentity: x\nmode: realtime\nsteps: []\n");
        ex.Message.Should().Contain("more than once");
        ex.Line.Should().Be(3);
    }

    [Fact]
    public void Step_with_two_kinds_is_refused() =>
        Refused(Realtime("  - stop: { status: canceled }\n    startChild: { workflow: C }")).Message.Should().Contain("exactly one");

    [Fact]
    public void Value_mapping_with_two_kinds_is_refused() =>
        Refused(Realtime("  - create:\n      entity: task\n      set:\n        a: { field: x, arg: y }")).Message.Should().Contain("exactly one kind");

    [Fact]
    public void Wait_in_realtime_workflow_is_refused()
    {
        Refused(Realtime("  - wait:\n      timeout: 1\n      then: []")).Message.Should().Contain("background");
        Refused("kind: action\nname: A\nentity: none\nmessage: a_A\nsteps:\n  - wait: { timeout: 1, then: [] }\n").Message.Should().Contain("background");
    }

    [Fact]
    public void Create_in_business_rule_is_refused() =>
        Refused("kind: businessRule\nname: R\nentity: xrmv_order\nsteps:\n  - create: { entity: task, set: {} }\n").Message.Should().Contain("business rule");

    [Fact]
    public void SetOutput_outside_action_is_refused() =>
        Refused(Realtime("  - setOutput: { argument: Total, value: 1 }")).Message.Should().Contain("action");

    [Fact]
    public void Stage_on_background_is_refused()
    {
        var ex = Refused("kind: workflow\nname: T\nentity: x\nmode: background\ntrigger:\n  create: { stage: pre }\nsteps: []\n");
        ex.Message.Should().Contain("real-time");
        ex.Line.Should().Be(6);
    }

    [Fact]
    public void Form_scope_rule_is_refused() =>
        Refused("kind: businessRule\nname: R\nentity: x\nscope: form\nsteps: []\n").Message.Should().Contain("form-scope rules run only in forms");

    [Fact]
    public void Created_reference_before_its_step_is_refused()
    {
        var ex = Refused(Realtime(
            "  - update:\n      record: { created: made }\n      set: {}\n  - create:\n      id: made\n      entity: task\n      set: {}"));
        ex.Line.Should().Be(7);
        ex.Message.Should().Contain("made");

        // The same reference after the step is fine, and so is an output reference to an action step.
        var ok = WorkflowYaml.Read(Realtime(
            "  - create:\n      id: made\n      entity: task\n      set: {}\n  - update:\n      record: { created: made }\n      set:\n        a: { field: b, created: made }"));
        ((UpdateNode)ok.Steps[1]).Record.Should().Be(new RecordNode.Created("made"));
    }

    [Fact]
    public void Duplicate_step_id_is_refused() =>
        Refused(Realtime("  - create: { id: a, entity: task, set: {} }\n  - sendEmail: { id: a, set: {} }")).Message.Should().Contain("more than once");

    [Fact]
    public void Between_is_not_an_operator()
    {
        var ex = Refused(Realtime("  - if:\n      when: { field: a, op: between, value: 1 }\n      then: []"));
        ex.Message.Should().Contain("all: [");
        ex.Message.Should().Contain("op: ge").And.Contain("op: le");
    }

    [Fact]
    public void Nesting_deeper_than_64_is_refused()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 70)) + string.Concat(Enumerable.Repeat("]", 70));
        var ex = Refused(Realtime($"  - create:\n      entity: task\n      set:\n        a: {deep}"));
        ex.Message.Should().Contain("64");

        var many = new string('[', 200_000);
        FluentActions.Invoking(() => WorkflowYaml.Read(many)).Should().Throw<SchemaFormatException>();

        var fine = string.Concat(Enumerable.Repeat("[", 55)) + string.Concat(Enumerable.Repeat("]", 55));
        FluentActions.Invoking(() => WorkflowYaml.Read(Realtime($"  - create:\n      entity: task\n      set:\n        a: {fine}")))
            .Should().Throw<SchemaFormatException>().Which.Message.Should().NotContain("64");
    }

    [Fact]
    public void Body_over_1_MiB_is_refused()
    {
        WorkflowYaml.MaxBytes.Should().Be(1_048_576);
        var ex = Refused("kind: workflow\n# " + new string('x', WorkflowYaml.MaxBytes) + "\n");
        ex.Message.Should().Contain("1 MiB");
    }

    [Fact]
    public void Anchors_are_refused() =>
        Refused("kind: &a workflow\nname: T\n").Message.Should().Contain("Anchors");

    [Fact]
    public void Special_characters_round_trip_through_write()
    {
        string[] texts =
        [
            "plain", "it's", "say \"hi\"", @"back\slash", @"trailing\", "a: b", "a #b", "- dash", "* star", "& amp", "! bang", "% pct", "@ at", "` tick",
            "[brackets]", "{braces}", "a, b", "line1\nline2", "tab\there", "cr\r\nlf", " leading", "trailing ", "", "~", "null", "true", "1", "2026-01-01",
            "Ünïcödé €", "日本語", "emoji 😀", "<xml attr=\"1\">&amp;</xml>", "New Object() { \"x\" }", "'", "\"", ":", "?", "|", ">", "---", "...",
        ];
        var document = new WorkflowDocument(WorkflowKind.Workflow, null, texts[0], "d: e", "xrmv_order", [
            new CreateNode("task", texts.Select((t, i) => ($"c{i}", (ValueNode)new LiteralNode(t))).ToList(), Description: "x: y \\"),
            new StopNode(false, new LiteralNode("a\nb")),
        ])
        { Options = new WorkflowOptions { Mode = "realtime" } };
        var yaml = WorkflowYaml.Write(document);
        var read = WorkflowYaml.Read(yaml);
        read.Description.Should().Be("d: e");
        var create = (CreateNode)read.Steps[0];
        create.Description.Should().Be("x: y \\");
        create.Set.Select(e => ((LiteralNode)e.Value).Text).Should().Equal(texts);
        ((StopNode)read.Steps[1]).Message.Should().BeOfType<LiteralNode>().Which.Text.Should().Be("a\nb");
        WorkflowYaml.Write(read).Should().Be(yaml);
    }

    [Fact]
    public void Write_covers_every_step_value_and_condition_form()
    {
        const string yaml = """
            kind: workflow
            name: Everything
            entity: xrmv_order
            mode: background
            onDemand: true
            trigger:
              create: true
              update: { columns: [ownerid, statecode] }
              delete: true
            steps:
              - create:
                  id: made
                  description: Make it
                  entity: task
                  set:
                    a: { now: true }
                    b: { concat: [x, { field: y }] }
                    c: { firstOf: [{ field: y }, 1] }
              - assign: { record: { created: made }, owner: { field: ownerid, related: parentid } }
              - setState: { record: primary, state: 1, status: 2 }
              - performAction:
                  id: act
                  message: xrmv_Do
                  target: primary
                  inputs:
                    n: 5
              - customActivity:
                  id: cust
                  type: My.Type
                  assembly: My.Assembly
                  inputs:
                    i: { output: o, of: act }
                  outputs:
                    Result: res
              - if:
                  when: { all: [{ field: a, op: eq, value: 1 }, { not: { any: [{ field: b, op: notNull }, { arg: z, op: contains, value: s }] } }] }
                  then:
                    - stop: { status: succeeded, message: done }
                  elseIf:
                    - when: { field: a, op: ne, value: 2 }
                      then:
                        - startChild: { workflow: 0fe1b782-e5de-f111-84e8-002248872b66, record: { related: accountid } }
                  else: []
              - wait:
                  timeout: { add: [{ now: true }, 3] }
                  then: []
                  onTimeout:
                    - stop: { status: canceled }
            """ + "\n";
        var document = WorkflowYaml.Read(yaml);
        WorkflowYaml.Write(document).Should().Be(yaml);

        const string rule = """
            kind: businessRule
            name: Rule
            entity: xrmv_order
            steps:
              - setDefault: { field: a, value: 1 }
              - clearValue: { field: b }
              - setVisibility: { field: c, visible: false }
              - setLock: { field: d, locked: true }
              - setRequirement: { field: e, level: required }
              - showError: { message: boom }
            """ + "\n";
        WorkflowYaml.Write(WorkflowYaml.Read(rule)).Should().Be(rule);
    }

    [Theory]
    [InlineData("  update: true")]
    [InlineData("  update: { stage: pre }")]
    [InlineData("  update: { columns: [] }")]
    [InlineData("  update: { columns: a }")]
    public void Update_trigger_needs_columns(string update)
    {
        var ex = Refused($"kind: workflow\nname: T\nentity: x\nmode: realtime\ntrigger:\n{update}\nsteps: []\n");
        ex.Line.Should().BeGreaterThan(5);
        ex.Message.Should().Contain("columns");
    }

    [Fact]
    public void Default_stages_are_canonicalised()
    {
        const string text = "kind: workflow\nname: T\nentity: x\nmode: realtime\ntrigger:\n  create: { stage: post }\n  update: { columns: [a], stage: post }\n  delete: { stage: pre }\nsteps: []\n";
        var doc = WorkflowYaml.Read(text);
        doc.Trigger!.Create!.Stage.Should().BeNull();
        doc.Trigger.Update!.Stage.Should().BeNull();
        doc.Trigger.Delete!.Stage.Should().BeNull();
        WorkflowYaml.Write(doc).Should().Be("kind: workflow\nname: T\nentity: x\nmode: realtime\ntrigger:\n  create: true\n  update: { columns: [a] }\n  delete: true\nsteps: []\n");

        var pre = WorkflowYaml.Read(text.Replace("create: { stage: post }", "create: { stage: pre }").Replace("stage: post", "stage: pre"));
        pre.Trigger!.Create!.Stage.Should().Be("pre");
        pre.Trigger.Update!.Stage.Should().Be("pre");
    }

    [Fact]
    public void Quoted_true_is_not_true()
    {
        Refused("kind: workflow\nname: T\nentity: x\nmode: realtime\ntrigger:\n  create: 'true'\nsteps: []\n").Message.Should().Contain("true");
        Refused(Realtime("  - create:\n      entity: task\n      set:\n        a: { now: 'true' }")).Message.Should().Contain("now");
    }

    [Fact]
    public void Write_refuses_a_model_it_cannot_write()
    {
        var literal = new LiteralNode("x");
        var field = new FieldNode("a", RecordNode.PrimaryRecord);
        WorkflowDocument Doc(WorkflowKind kind, params StepNode[] steps) =>
            new(kind, null, "T", null, "x", steps) { Options = new WorkflowOptions { Mode = kind == WorkflowKind.Workflow ? "realtime" : null } };
        WorkflowDocument WithIf(CondNode when) => Doc(WorkflowKind.Workflow, new IfNode([new BranchNode(when, [])]));

        var cases = new (WorkflowDocument Document, string Contains)[]
        {
            (WithIf(new CompareNode("eq", literal, literal)), "literal"),
            (WithIf(new CompareNode("between", field, literal)), "between"),
            (WithIf(new CompareNode("null", field, literal)), "takes no value"),
            (WithIf(new CompareNode("gt", field)), "needs a value"),
            (WithIf(new AllNode([]) ), "all"),
            (Doc(WorkflowKind.Workflow, new IfNode([])), "no branch"),
            (Doc(WorkflowKind.Workflow) with { Options = new WorkflowOptions() }, "mode"),
            (Doc(WorkflowKind.Workflow) with { Options = new WorkflowOptions { Mode = "sync" } }, "sync"),
            (Doc(WorkflowKind.BusinessRule, new ClientOnlyNode("setLock", "a", "maybe")), "maybe"),
            (Doc(WorkflowKind.BusinessRule, new ClientOnlyNode("setRequirement", "a", "true")), "true"),
            (Doc(WorkflowKind.BusinessRule, new ClientOnlyNode("setColor", "a", "red")), "setColor"),
            (Doc(WorkflowKind.Workflow, new UpdateNode(RecordNode.PrimaryRecord, [("a", new OperatorNode("pow", [literal]))])), "pow"),
            (Doc(WorkflowKind.Workflow) with { Trigger = new TriggerNode(null, new UpdateTrigger([]), null) }, "no columns"),
            (Doc(WorkflowKind.Workflow) with { Trigger = new TriggerNode(new CreateTrigger("later"), null, null) }, "later"),
            (Doc(WorkflowKind.Action) with { Arguments = [new ArgumentNode("A", true, "text", null, false, "")] }, "text"),
        };
        foreach (var (document, contains) in cases)
            FluentActions.Invoking(() => WorkflowYaml.Write(document)).Should().Throw<ArgumentException>().Which.Message.Should().Contain(contains);
    }

    [Fact]
    public void Argument_type_name_and_direction_are_checked()
    {
        string Action(string argument) => $"kind: action\nname: A\nentity: none\nmessage: a_A\narguments:\n  - {argument}\nsteps: []\n";
        foreach (var type in WorkflowYaml.ArgumentTypes)
            WorkflowYaml.Read(Action($"{{ name: X, direction: input, type: {type} }}")).Arguments.Should().ContainSingle().Which.Type.Should().Be(type);
        Refused(Action("{ name: X, direction: input, type: text }")).Message.Should().Contain("type");
        Refused(Action("{ name: Target, direction: input, type: string }")).Message.Should().Contain("Target");
        Refused(Action("{ name: X, direction: both, type: string }")).Message.Should().Contain("direction");
    }

    [Fact]
    public void Surrogates_are_escaped_only_when_alone()
    {
        var document = new WorkflowDocument(WorkflowKind.Workflow, null, "T", null, "x", [
            new CreateNode("task", [("a", new LiteralNode("\ud83d\ude00\tx")), ("b", new LiteralNode("x\ud83d\ude00 \ud83d\ude00\n"))]),
        ])
        { Options = new WorkflowOptions { Mode = "realtime" } };
        var yaml = WorkflowYaml.Write(document);
        yaml.Should().Contain("\ud83d\ude00");
        ((CreateNode)WorkflowYaml.Read(yaml).Steps[0]).Set.Select(e => ((LiteralNode)e.Value).Text)
            .Should().Equal("\ud83d\ude00\tx", "x\ud83d\ude00 \ud83d\ude00\n");
    }

    [Fact]
    public void Keys_table_lists_the_top_level_keys()
    {
        WorkflowYaml.Keys.Select(k => k.Path).Should().Contain(["kind", "mode", "steps[].wait", "condition"]);
        WorkflowYaml.MaxDepth.Should().Be(64);
    }
}
