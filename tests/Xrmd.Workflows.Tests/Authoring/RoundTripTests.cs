using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Schema;
using Xrmd.Workflows.Authoring;

namespace Xrmd.Workflows.Tests.Authoring;

/// <summary>
/// Export to YAML and the round trip (plan 2.21, Task 4, A12): the real exports decompile to their YAML twins, every valid document
/// survives compile, parse, decompile and write unchanged, and what has no YAML form is reported as not representable with its reasons.
/// </summary>
public class RoundTripTests
{
    // ---- the real rows (customizations.xml of xrmdWfTest_1_0_0_2.zip) ----------------------------------------------------------

    private static readonly string ExportZip = Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmdWfTest_1_0_0_2.zip");

    /// <summary>The <c>workflow</c> row of the process named <paramref name="name"/> in the export: its columns and its XAML file.</summary>
    internal static WorkflowRow RealRow(string name)
    {
        using var zip = ZipFile.OpenRead(ExportZip);
        XElement customizations;
        using (var stream = zip.GetEntry("customizations.xml")!.Open())
            customizations = XDocument.Load(stream).Root!;
        var w = customizations.Descendants("Workflow").Single(e => (string?)e.Attribute("Name") == name);
        string? Text(string element) => (string?)w.Element(element);
        int? Int(string element) => Text(element) is { } text ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : null;
        bool Bool(string element) => Int(element) == 1;
        var xamlEntry = Text("XamlFileName")!.TrimStart('/');
        string xaml;
        using (var reader = new StreamReader(zip.GetEntry(xamlEntry)!.Open(), Encoding.UTF8))
            xaml = reader.ReadToEnd();
        return new WorkflowRow
        {
            Id = Guid.Parse(((string)w.Attribute("WorkflowId")!).Trim('{', '}')),
            Name = name,
            Description = (string?)w.Attribute("Description"),
            Type = Int("Type")!.Value,
            Category = Int("Category")!.Value,
            Mode = Int("Mode")!.Value,
            Scope = Int("Scope")!.Value,
            Subprocess = Bool("Subprocess"),
            OnDemand = Bool("OnDemand"),
            TriggerOnCreate = Bool("TriggerOnCreate"),
            TriggerOnDelete = Bool("TriggerOnDelete"),
            TriggerOnUpdateAttributes = Text("TriggerOnUpdateAttributeList"),
            AsyncAutoDelete = Bool("AsyncAutodelete"),
            SyncWorkflowLogOnFailure = Bool("SyncWorkflowLogOnFailure"),
            StateCode = Int("StateCode")!.Value,
            CreateStage = Int("CreateStage"),
            UpdateStage = Int("UpdateStage"),
            DeleteStage = Int("DeleteStage"),
            RunAs = Int("RunAs")!.Value,
            UniqueName = Text("UniqueName"),
            IsTransacted = Bool("IsTransacted"),
            IntroducedVersion = Text("IntroducedVersion"),
            IsCustomizable = Bool("IsCustomizable"),
            ProcessTriggerScope = Int("ProcessTriggerScope"),
            PrimaryEntity = Text("PrimaryEntity"),
            Xaml = xaml,
        };
    }

    private static string YamlFixture(string file) => GoldenXamlTests.Fixture(Path.Combine("yaml", file));

    private static async Task<WorkflowDocument> Representable(WorkflowRow row, IAuthoringEnvironment env)
    {
        var result = await WorkflowDecompiler.DecompileAsync(row, env, default);
        if (result is DecompileResult.NotRepresentable not)
            throw new Xunit.Sdk.XunitException("Not representable:\n" + string.Join("\n", not.Reasons));
        return result.Should().BeOfType<DecompileResult.Representable>().Subject.Document;
    }

    private static async Task<IReadOnlyList<string>> Reasons(WorkflowRow row, IAuthoringEnvironment? env = null)
    {
        var result = await WorkflowDecompiler.DecompileAsync(row, env ?? new FakeAuthoringEnvironment(), default);
        return result.Should().BeOfType<DecompileResult.NotRepresentable>().Subject.Reasons;
    }

    /// <summary><c>Write(Decompile(Parse(Compile(d))))</c>: the YAML exported from the compiled row of <paramref name="document"/>.</summary>
    private static async Task<string> RoundTrip(WorkflowDocument document, IAuthoringEnvironment env)
    {
        var compiled = await WorkflowCompiler.CompileAsync(document, env, null, default);
        return WorkflowYaml.Write(await Representable(compiled.Row, env));
    }

    public static TheoryData<string, string> RealExports => new()
    {
        { "OnCreate", "oncreate.yaml" },
        { "OnUpdate", "onupdate.yaml" },
        { "Child", "child.yaml" },
        { "New business rule", "business-rule.yaml" },
        { "Recalculate", "recalculate.yaml" },
    };

    [Theory]
    [MemberData(nameof(RealExports))]
    public async Task Each_real_export_decompiles_to_its_yaml_fixture(string process, string yaml)
    {
        var document = await Representable(RealRow(process), new FakeAuthoringEnvironment());
        WorkflowYaml.Write(document).Should().Be(YamlFixture(yaml));
    }

    [Theory]
    [MemberData(nameof(RealExports))]
    public async Task Each_fixture_round_trips(string process, string yaml)
    {
        _ = process;
        var document = WorkflowYaml.Read(YamlFixture(yaml));
        (await RoundTrip(document, new FakeAuthoringEnvironment())).Should().Be(WorkflowYaml.Write(document));
    }

    /// <summary>
    /// The round-trip property of A12 over 500 seeded documents of every kind: the exported YAML is the document's, and it reads back to
    /// the same text. A failing seed is printed with its YAML and what differs.
    /// </summary>
    [Fact]
    public async Task Random_documents_round_trip()
    {
        var env = DocumentGenerator.Environment();
        var failures = new List<string>();
        for (var seed = 0; seed < 500 && failures.Count < 3; seed++)
        {
            var document = DocumentGenerator.Generate(seed, env);
            var expected = WorkflowYaml.Write(document);
            try
            {
                var actual = await RoundTrip(document, env);
                if (actual != expected)
                    failures.Add($"seed {seed}: the exported YAML differs.\n--- expected\n{expected}\n--- actual\n{actual}");
                else if (WorkflowYaml.Write(WorkflowYaml.Read(actual)) != expected)
                    failures.Add($"seed {seed}: the exported YAML does not read back to the same text.\n{actual}");
            }
            catch (Exception ex)
            {
                failures.Add($"seed {seed}: {ex.GetType().Name}: {ex.Message}\n--- document\n{expected}");
            }
        }
        failures.Should().BeEmpty(string.Join("\n\n", failures));
    }

    /// <summary>RF3: names, descriptions, literals, lookup names and messages with text special in XML, VB and YAML.</summary>
    [Fact]
    public async Task Special_characters_round_trip()
    {
        const string text = "He said \"hi\" & <left> ] [x] èé \U0001F600\nsecond line";
        var env = DocumentGenerator.Environment();
        var workflow = new WorkflowDocument(WorkflowKind.Workflow, Guid.Parse("5ca1ab1e-0000-4000-8000-000000000001"), "Quote \" & < ] è \U0001F600",
            text, "account",
        [
            new CreateNode("task", [
                ("subject", new LiteralNode(text)),
                ("description", new OperatorNode("concat", [new LiteralNode("<b>\"x\"</b> & "), new FieldNode("name", RecordNode.PrimaryRecord)])),
                ("regardingobjectid", new LookupNode("account", FakeAuthoringEnvironment.Contoso, text)),
            ], "rec1", text),
            new IfNode([new BranchNode(new CompareNode("eq", new FieldNode("name", RecordNode.PrimaryRecord), new LiteralNode("a ] b \"c\"")),
                [new StopNode(Canceled: true, new LiteralNode(text), "stop: \"now\" <here>")])], Else: [], Description: "check ] & \"x\""),
        ]) { Options = new WorkflowOptions { Mode = "realtime" } };
        (await RoundTrip(workflow, env)).Should().Be(WorkflowYaml.Write(workflow));

        var rule = new WorkflowDocument(WorkflowKind.BusinessRule, Guid.Parse("5ca1ab1e-0000-4000-8000-000000000002"), "Rule \U0001F600", null, "account",
        [
            new IfNode([new BranchNode(new CompareNode("null", new FieldNode("name", RecordNode.PrimaryRecord)),
                [
                    new ShowErrorNode("name", text, "error <&>"),
                    new SetValueNode("description", new LiteralNode(text)),
                ])], Description: text),
        ]);
        (await RoundTrip(rule, env)).Should().Be(WorkflowYaml.Write(rule));
    }

    /// <summary>
    /// The provisional 2.14 fixture writes its steps as <c>StepComposite</c>s with <c>UpdatedEntities</c>, <c>Persist</c> and no target
    /// types: the YAML recompiles to other XAML (designer sequences) whose definition is equivalent. Its regarding is left out, as in
    /// the 2.14 integration tests: the file sets it to the account's primary key, a Guid, without the conversion to a reference the
    /// export writes, which no YAML value means.
    /// </summary>
    [Fact]
    public async Task Provisional_fixture_with_StepComposite_is_representable()
    {
        var xaml = System.Text.RegularExpressions.Regex.Replace(GoldenXamlTests.Fixture("realtime-create-update.xaml"),
            "<mxswa:SetEntityProperty Attribute=\"regardingobjectid\"[^>]*/>", "");
        var row = new WorkflowRow
        {
            Id = Guid.Parse("3c1d7a5e-0b2f-4c6a-a1f0-d9e8c7b6a501"), Name = "Create follow-up", Category = 0, Mode = 1, Scope = 1, RunAs = 1,
            PrimaryEntity = "account", TriggerOnCreate = true, CreateStage = 40, SyncWorkflowLogOnFailure = true, Xaml = xaml,
        };

        var document = await Representable(row, new FakeAuthoringEnvironment());

        var compiled = await WorkflowCompiler.CompileAsync(document, new FakeAuthoringEnvironment(), null, default);
        compiled.Xaml.Should().NotBe(xaml).And.NotContain("StepComposite");
        DefinitionEquivalence.Differences(Xaml.XamlWorkflowParser.Parse(xaml, 0, workflowSdkAvailable: false), compiled.Definition).Should().BeEmpty();
        WorkflowYaml.Write(document).Should().Be("""
            kind: workflow
            id: 3c1d7a5e-0b2f-4c6a-a1f0-d9e8c7b6a501
            name: Create follow-up
            entity: account
            mode: realtime
            trigger:
              create: true
            steps:
              - create:
                  description: create a follow-up task
                  entity: task
                  set:
                    subject: Call the new customer
                    description: { field: name }
              - update:
                  description: mark the account as followed up
                  record: primary
                  set:
                    creditonhold: true
                    numberofemployees: 25

            """.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Unsupported_step_is_not_representable()
    {
        var row = new WorkflowRow
        {
            Id = Guid.NewGuid(), Name = "Unsupported", Category = 0, Mode = 0, Scope = 1, PrimaryEntity = "account", TriggerOnCreate = true,
            Xaml = GoldenXamlTests.Fixture("unsupported-expression.xaml"),
        };

        var reasons = await Reasons(row);

        reasons.Should().Contain(r => r.Contains("UpdateStep1: set a date three days out"))
            .And.Contain(r => r.Contains("FutureStep2: an activity xrmverse does not know"))
            .And.Contain(r => r.Contains("ScopeStep3"));
    }

    [Fact]
    public async Task Category_5_is_not_representable()
    {
        var row = RealRow("OnCreate") with { Category = 5 };

        var reasons = await Reasons(row);

        reasons.Should().ContainSingle().Which.Should().Contain("category 5");
    }

    /// <summary>Final review: the run order (<c>rank</c>) of a real-time workflow has no YAML key, so a rank other than 0 is not representable.</summary>
    [Fact]
    public async Task Rank_other_than_0_is_not_representable()
    {
        var row = RealRow("OnCreate") with { Rank = 2 };
        row.Mode.Should().Be(1, "OnCreate is a real-time workflow");

        var reasons = await Reasons(row);

        reasons.Should().ContainSingle().Which.Should().Contain("rank 2 (run order) has no YAML form");
        await Representable(RealRow("OnCreate") with { Rank = 0 }, new FakeAuthoringEnvironment());
    }

    /// <summary>A <c>DirectCast</c> has a YAML form only around the value of an output argument (and a stop message), A12.</summary>
    [Fact]
    public async Task Cast_outside_output_is_not_representable()
    {
        var row = (await GoldenXamlTests.CompileFixture("oncreate.yaml")).Row;
        var xaml = row.Xaml!.Replace("Value=\"[CreateStep1_1]\"", "Value=\"[DirectCast(CreateStep1_1, System.String)]\"", StringComparison.Ordinal);
        xaml.Should().NotBe(row.Xaml);

        var reasons = await Reasons(row with { Xaml = xaml });

        reasons.Should().ContainSingle().Which.Should().Contain("CreateStep1").And.Contain("DirectCast").And.Contain("subject");
    }

    /// <summary>
    /// A text concatenation as the left operand (<c>Operand</c>) of a condition has no YAML form (A12): the compiler refuses it, and the
    /// exporter reports it.
    /// </summary>
    [Fact]
    public async Task Concat_condition_operand_is_not_representable()
    {
        var yaml = """
            kind: workflow
            name: Concat
            entity: account
            mode: background
            steps:
              - if:
                  when: { field: name, op: eq, value: x }
                  then:
                    - stop: { status: succeeded }
            """;
        var row = (await WorkflowCompiler.CompileAsync(yaml, new FakeAuthoringEnvironment(), null, default)).Row;
        var xaml = row.Xaml!.Replace("x:Key=\"Operand\">[ConditionBranchStep2_1]<", "x:Key=\"Operand\">[String.Concat(ConditionBranchStep2_1, ConditionBranchStep2_1)]<",
            StringComparison.Ordinal);
        xaml.Should().NotBe(row.Xaml);

        var reasons = await Reasons(row with { Xaml = xaml });
        reasons.Should().ContainSingle().Which.Should().Contain("ConditionStep1").And.Contain("concatenation");

        var compile = () => WorkflowCompiler.CompileAsync(yaml.Replace("{ field: name, op: eq", "{ concat: [{ field: name }, x], op: eq"),
            new FakeAuthoringEnvironment(), null, default);
        (await compile.Should().ThrowAsync<WorkflowCompileException>()).Which.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("concat");
    }

    [Fact]
    public async Task Child_by_name_or_by_id_when_ambiguous()
    {
        static WorkflowDocument Parent(string child) => new(WorkflowKind.Workflow, Guid.Parse("5ca1ab1e-0000-4000-8000-000000000003"), "Parent", null,
            "xrmv_order", [new StartChildNode(child, RecordNode.PrimaryRecord)]) { Options = new WorkflowOptions { Mode = "background" } };
        var env = new FakeAuthoringEnvironment();

        (await RoundTrip(Parent(FakeAuthoringEnvironment.ChildId.ToString("D")), env)).Should().Contain("startChild: { workflow: Child }");
        (await RoundTrip(Parent("Twin"), env.With(e => e.Definitions.RemoveAll(d => d.Id == FakeAuthoringEnvironment.TwinB))))
            .Should().Contain("startChild: { workflow: Twin }");
        (await RoundTrip(Parent(FakeAuthoringEnvironment.TwinA.ToString("D")), new FakeAuthoringEnvironment()))
            .Should().Contain($"startChild: {{ workflow: {FakeAuthoringEnvironment.TwinA:D} }}");
    }

    [Fact]
    public async Task Defaults_are_omitted()
    {
        var row = new WorkflowRow
        {
            Id = Guid.Parse("5ca1ab1e-0000-4000-8000-000000000004"), Name = "Defaults", Category = 0, Mode = 1, Scope = 1, RunAs = 1,
            PrimaryEntity = "xrmv_order", CreateStage = 40, SyncWorkflowLogOnFailure = true, OnDemand = true,
        };
        var source = new WorkflowDocument(WorkflowKind.Workflow, row.Id, "Defaults", null, "xrmv_order",
            [new StartChildNode("Child", RecordNode.PrimaryRecord), new StopNode(Canceled: false)])
        {
            Options = new WorkflowOptions { Mode = "realtime", OnDemand = true },
        };
        row = row with { Xaml = (await WorkflowCompiler.CompileAsync(source, new FakeAuthoringEnvironment(), null, default)).Xaml };

        var document = await Representable(row, new FakeAuthoringEnvironment());

        WorkflowYaml.Write(document).Should().Be("""
            kind: workflow
            id: 5ca1ab1e-0000-4000-8000-000000000004
            name: Defaults
            entity: xrmv_order
            mode: realtime
            onDemand: true
            steps:
              - startChild: { workflow: Child }
              - stop: { status: succeeded }

            """.Replace("\r\n", "\n"));
    }

    /// <summary>The settings of the form-only business rule actions come back (the parser keeps their column and setting).</summary>
    [Fact]
    public async Task Client_only_settings_round_trip()
    {
        var rule = new WorkflowDocument(WorkflowKind.BusinessRule, Guid.Parse("5ca1ab1e-0000-4000-8000-000000000005"), "Form", null, "xrmv_order",
        [
            new IfNode([new BranchNode(new CompareNode("notNull", new FieldNode("xrmv_amount", RecordNode.PrimaryRecord)),
                [
                    new ClientOnlyNode("setVisibility", "xrmv_name", "false"),
                    new ClientOnlyNode("setVisibility", "xrmv_name", "true"),
                    new ClientOnlyNode("setLock", "xrmv_amount", "true"),
                    new ClientOnlyNode("setLock", "xrmv_amount", "false"),
                    new ClientOnlyNode("setRequirement", "xrmv_status", "none"),
                    new ClientOnlyNode("setRequirement", "xrmv_status", "recommended"),
                    new ClientOnlyNode("setRequirement", "xrmv_status", "required", "must say"),
                ])], Description: "New Condition"),
        ]);

        (await RoundTrip(rule, new FakeAuthoringEnvironment())).Should().Be(WorkflowYaml.Write(rule));
    }
}

internal static class FakeAuthoringEnvironmentExtensions
{
    public static FakeAuthoringEnvironment With(this FakeAuthoringEnvironment env, Action<FakeAuthoringEnvironment> change)
    {
        change(env);
        return env;
    }
}
