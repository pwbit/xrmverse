using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Schema;
using Xrmd.Workflows.Authoring;

namespace Xrmd.Workflows.Tests.Authoring;

/// <summary>
/// The documentation of workflow authoring as code (plan 2.21, Task 9): the reference table of docs/workflow-authoring.md is generated
/// from <see cref="WorkflowYaml.Keys"/>, its examples compile, and they reach every assumed shape the compiler writes.
/// </summary>
public class WorkflowAuthoringDocTests
{
    internal const string TableStart = "<!-- workflow-yaml:start -->";
    internal const string TableEnd = "<!-- workflow-yaml:end -->";

    private const string Page = "docs/workflow-authoring.md";

    /// <summary>The table row of a key, as docs/workflow-authoring.md holds it (a pipe stays in its cell, angle brackets are not markup).</summary>
    internal static string DocRow((string Path, string Meaning) key) =>
        $"| `{key.Path}` | {key.Meaning.Replace("|", "\\|").Replace("<", "&lt;").Replace(">", "&gt;")} |";

    [Fact]
    public void Reference_table_lists_every_yaml_key_as_generated()
    {
        var page = File.ReadAllText(ActivityCatalogDocTests.RepoFile(Page)).ReplaceLineEndings("\n");
        var start = page.IndexOf(TableStart, StringComparison.Ordinal);
        var end = page.IndexOf(TableEnd, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "{0} marks the generated table with {1}", Page, TableStart);
        end.Should().BeGreaterThan(start);

        var rows = page[(start + TableStart.Length)..end].Split('\n').Where(l => l.StartsWith("| `", StringComparison.Ordinal)).ToList();

        rows.Should().Equal(WorkflowYaml.Keys.Select(DocRow),
            "the table of {0} is generated from WorkflowYaml.Keys (WorkflowAuthoringDocTests.DocRow), one row per key in order", Page);
    }

    [Fact]
    public async Task Examples_in_the_page_compile()
    {
        var page = File.ReadAllText(ActivityCatalogDocTests.RepoFile(Page)).ReplaceLineEndings("\n");
        var blocks = Regex.Matches(page, "^```yaml\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
        blocks.Should().HaveCountGreaterThanOrEqualTo(3, "the page holds the examples (workflow, business rule and action)");

        foreach (var yaml in blocks)
        {
            // Each fenced yaml block is a complete document: it reads, and it compiles against the organization of the parity export.
            var env = new FakeAuthoringEnvironment { WorkflowSdkAvailable = true };
            env.WorkflowActivities.Add(("Example.Activities.Normalize", "Example.Activities"));
            var result = await WorkflowCompiler.CompileAsync(WorkflowYaml.Read(yaml), env, null, default);
            result.Definition.Unsupported.Should().BeEmpty("the example\n{0}", yaml);
            // The examples are canonical: reading and writing them again gives the same text.
            WorkflowYaml.Write(WorkflowYaml.Read(yaml)).Should().Be(yaml.ReplaceLineEndings("\n"), "the example is written as the exporter writes it:\n{0}", yaml);
        }
    }

    /// <summary>The shapes the writer names itself (<c>DesignerXaml.Assume</c>): the corpus below must produce each of them.</summary>
    private static readonly string[] WriterShapes =
    [
        "createdKey:id", "sendEmail:assignToId", "sendEmail:noPersist", "stop:succeeded", "stop:message", "ruleCondition:noDescription",
        "ruleCondition:elseIfDescription", "ruleCondition:nested", "customActivity:Example.Activities.Normalize", "clearValue", "showError:noField",
        "action:global", "literal:Integer", "literal:Double", "literal:Money", "literal:DateTime",
        "argument:boolean", "argument:dateTime", "argument:double", "argument:integer", "argument:money", "argument:picklist", "argument:string",
    ];

    private const string Corpus = """
        kind: workflow
        name: Shapes
        entity: account
        mode: background
        trigger:
          create: true
        steps:
          - assign: { record: primary, owner: { lookup: systemuser, id: 5e1f0000-0000-4000-8000-000000000001 } }
          - setState: { record: primary, state: 1, status: 2 }
          - update: { record: { related: parentaccountid }, set: { name: Parent } }
          - create: { id: made, entity: task, set: { subject: x, regardingobjectid: { record: primary } } }
          - update: { record: { created: made }, set: { subject: y } }
          - sendEmail: { id: mail, set: { subject: x, to: { field: primarycontactid }, regardingobjectid: { record: primary } } }
          - if:
              when:
                all:
                  - { field: name, op: eq, value: A }
                  - { field: name, op: ne, value: B }
                  - { field: description, op: null }
                  - { field: description, op: notNull }
                  - { field: name, op: contains, value: C }
                  - { field: name, op: notContains, value: D }
                  - { field: name, op: beginsWith, value: E }
                  - { field: name, op: endsWith, value: F }
                  - { field: numberofemployees, op: ge, value: 1 }
                  - { field: numberofemployees, op: le, value: 9 }
                  - any:
                      - { field: name, op: eq, value: G }
                      - not: { field: name, op: eq, value: H }
              then: [ { stop: { status: succeeded } } ]
              elseIf:
                - when: { field: name, op: eq, value: I }
                  then: [ { stop: { status: canceled, message: Bye } } ]
              else: [ { stop: { status: canceled } } ]
          - update:
              record: primary
              set:
                numberofemployees: { add: [ { field: numberofemployees }, 1 ] }
                revenue: { subtract: [ { field: revenue }, 1 ] }
                address1_latitude: { multiply: [ { field: address1_latitude }, 2 ] }
                lastusedincampaign: { now: true }
                description: { concat: [ "Hello ", { field: name } ] }
                name: { firstOf: [ { field: name }, Unknown ] }
          - update: { record: primary, set: { numberofemployees: { increment: [ { field: numberofemployees }, 1 ] } } }
          - update: { record: primary, set: { numberofemployees: { decrement: [ { field: numberofemployees }, 1 ] } } }
          - update: { record: primary, set: { numberofemployees: { set: [ 7 ] } } }
          - update: { record: primary, set: { revenue: 10, address1_latitude: 1.5, lastusedincampaign: 2026-01-01T00:00:00Z } }
          - performAction: { id: act, message: xrmv_Score, target: primary, inputs: { Factor: 2.5, Label: Hi, Count: 3 } }
          - update: { record: primary, set: { description: { output: Score, of: act } } }
          - customActivity: { type: Example.Activities.Normalize, assembly: Example.Activities, inputs: { Input: { field: name } } }
          - wait:
              until: { field: statuscode, op: eq, value: 2 }
              timeout: { field: lastusedincampaign }
              then:
                - setState: { record: primary, state: 0, status: 1 }
              onTimeout:
                - setState: { record: primary, state: 1, status: 2 }
        """;

    private static readonly string[] Documents =
    [
        Corpus,
        // A real-time email without a Persist.
        "kind: workflow\nname: Mail\nentity: account\nmode: realtime\ntrigger:\n  create: true\nsteps:\n  - sendEmail: { id: mail, set: { subject: x } }\n",
        // Business rule actions and rule conditions the exported rule does not show.
        """
        kind: businessRule
        name: Rule
        entity: xrmv_order
        steps:
          - if:
              when: { field: xrmv_amount, op: notNull }
              then:
                - setDefault: { field: xrmv_status, value: 128830000 }
                - clearValue: { field: xrmv_name }
                - setVisibility: { field: xrmv_name, visible: false }
                - setLock: { field: xrmv_amount, locked: true }
                - setRequirement: { field: xrmv_status, level: required }
                - showError: { message: Anywhere }
        """,
        """
        kind: businessRule
        name: Rule with else if
        entity: xrmv_order
        steps:
          - if:
              description: Amount
              when: { field: xrmv_amount, op: null }
              then: [ { clearValue: { field: xrmv_name } } ]
              elseIf:
                - when: { field: xrmv_amount, op: gt, value: 5 }
                  then: [ { clearValue: { field: xrmv_name } } ]
        """,
        """
        kind: businessRule
        name: Nested rule
        entity: xrmv_order
        steps:
          - if:
              description: Outer
              when: { field: xrmv_amount, op: null }
              then:
                - if:
                    description: Inner
                    when: { field: xrmv_status, op: null }
                    then: [ { clearValue: { field: xrmv_name } } ]
        """,
        // A global action with every argument type.
        """
        kind: action
        name: Everything
        entity: none
        message: xrmv_Everything
        arguments:
          - { name: B, direction: input, type: boolean }
          - { name: Dt, direction: input, type: dateTime }
          - { name: De, direction: input, type: decimal }
          - { name: Dbl, direction: input, type: double }
          - { name: Er, direction: input, type: entityReference, entity: account }
          - { name: I, direction: input, type: integer }
          - { name: M, direction: input, type: money }
          - { name: P, direction: input, type: picklist }
          - { name: S, direction: output, type: string }
          - { name: N, direction: output, type: integer }
        steps:
          - setOutput: { argument: S, value: Hi }
          - setOutput: { argument: N, value: 3 }
          - stop: { status: canceled }
        """,
    ];

    /// <summary>The assumed shapes the compiler reports on <see cref="Documents"/> (the corpus of the documentation examples).</summary>
    internal static async Task<SortedSet<string>> ReportedShapesAsync()
    {
        var env = new FakeAuthoringEnvironment { WorkflowSdkAvailable = true };
        env.WorkflowActivities.Add(("Example.Activities.Normalize", "Example.Activities"));
        var reported = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var yaml in Documents)
            foreach (var shape in (await WorkflowCompiler.CompileAsync(WorkflowYaml.Read(yaml), env, null, default)).AssumedShapes)
                reported.Add(shape);
        return reported;
    }

    [Fact]
    public async Task The_corpus_reaches_every_assumed_shape_the_compiler_writes()
    {
        var reported = await ReportedShapesAsync();

        // The corpus reaches every shape the writer names (a new Assume call without a document here fails this line) and the catalog
        // entries of the assumed and learn sources the compiler writes.
        reported.Should().Contain(WriterShapes);
        reported.Should().Contain(["mxswa:AssignEntity", "mxswa:SetState", "mxswa:Postpone", "EvaluateCondition:NotEqual", "EvaluateLogicalCondition:Not"]);
    }
}
