using FluentAssertions;
using Xrmd.Schema;
using Xrmd.Workflows.Authoring;
using Xrmd.TestSupport;

namespace Xrmd.Workflows.Tests.Authoring;

/// <summary>
/// The YAML twins of the real exports compile to the XAML the classic designer wrote (plan 2.21, Task 2, A8/A9): equal through
/// <see cref="GoldenXaml.Canonical"/>, which only normalizes layout and the generated step label ids.
/// </summary>
public class GoldenXamlTests
{
    internal static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", relative));

    internal static string Real(string prefix) =>
        File.ReadAllText(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "real"), prefix + "-*.xaml").Single());

    internal static Task<CompileResult> CompileFixture(string yamlFile, FakeAuthoringEnvironment? env = null) =>
        WorkflowCompiler.CompileAsync(WorkflowYaml.Read(Fixture(Path.Combine("yaml", yamlFile))), env ?? new FakeAuthoringEnvironment(), null, default);

    [Fact]
    public async Task OnCreate_yaml_compiles_to_the_export()
    {
        var result = await CompileFixture("oncreate.yaml");
        GoldenXaml.Canonical(result.Xaml).Should().Be(GoldenXaml.Canonical(Real("OnCreate")));
    }

    [Fact]
    public async Task OnUpdate_yaml_compiles_to_the_export()
    {
        var result = await CompileFixture("onupdate.yaml");
        GoldenXaml.Canonical(result.Xaml).Should().Be(GoldenXaml.Canonical(Real("OnUpdate")));
    }

    [Fact]
    public async Task Child_yaml_compiles_to_the_export()
    {
        var result = await CompileFixture("child.yaml");
        GoldenXaml.Canonical(result.Xaml).Should().Be(GoldenXaml.Canonical(Real("Child")));
    }

    /// <summary>
    /// The business rule of the export (plan 2.21, Task 3, A8/A9): its actions named after the condition's first branch
    /// (<c>SetMessageStep_ConditionBranchStep2_2</c> in the else branch), the designer's <c>True</c> variable, <c>ContainsElseBranch</c>
    /// False, the branch descriptions, the error message as a step label, and the <c>x:Class</c> of zeros the export writes for a rule.
    /// </summary>
    [Fact]
    public async Task Business_rule_yaml_compiles_to_the_export()
    {
        var result = await CompileFixture("business-rule.yaml");
        GoldenXaml.Canonical(result.Xaml).Should().Be(GoldenXaml.Canonical(Real("Newbusinessrule")));
        result.Xaml.Should().Contain("DisplayName=\"SetMessageStep_ConditionBranchStep2_2: New Action\"")
            .And.Contain("Name=\"True\"").And.Contain("<x:Boolean x:Key=\"ContainsElseBranch\">False</x:Boolean>")
            .And.Contain("<mcwo:StepLabel Description=\"fdsfds\"");
        result.AssumedShapes.Should().BeEmpty();
    }

    /// <summary>
    /// The action of the export: <c>x:Members</c> with the five <c>mxsw:Argument*Attribute</c>s in export order and the generated
    /// <c>Target</c>, <c>CustomOperationArguments</c>, <c>Add</c>, the output assignment <c>AssignOutputArgumentStep2: Total\</c> with its
    /// <c>DirectCast</c>.
    /// </summary>
    [Fact]
    public async Task Recalculate_yaml_compiles_to_the_export()
    {
        var result = await CompileFixture("recalculate.yaml");
        GoldenXaml.Canonical(result.Xaml).Should().Be(GoldenXaml.Canonical(Real("Recalculate")));
        result.Xaml.Should().Contain("<x:Property Name=\"Target\" Type=\"InArgument(mxs:EntityReference)\">")
            .And.Contain("DisplayName=\"AssignOutputArgumentStep2: Total\\\"")
            .And.Contain("Value=\"[DirectCast(AssignOutputArgumentStep2_1,System.Decimal)]\"");
        result.AssumedShapes.Should().BeEmpty();
    }

    /// <summary>
    /// The text constraint (UTF-8 without BOM, CRLF, two-space indentation, no final line end, the export's attribute escaping):
    /// the files without generated label ids are equal byte for byte.
    /// </summary>
    [Theory]
    [InlineData("oncreate.yaml", "OnCreate")]
    [InlineData("onupdate.yaml", "OnUpdate")]
    public async Task Compiled_text_is_byte_identical_to_the_export(string yaml, string real)
    {
        var result = await CompileFixture(yaml);
        result.Xaml.Should().Be(Real(real));
    }

    [Fact]
    public async Task Compile_is_deterministic()
    {
        foreach (var yaml in new[] { "oncreate.yaml", "onupdate.yaml", "child.yaml", "business-rule.yaml", "recalculate.yaml" })
        {
            var first = await CompileFixture(yaml);
            var second = await CompileFixture(yaml);
            second.Xaml.Should().Be(first.Xaml, yaml);
            second.Row.Should().BeEquivalentTo(first.Row, yaml);
        }
    }

    [Fact]
    public void Canonical_normalizes_only_layout_and_label_ids()
    {
        const string a = "<A xmlns:x=\"u\"><Variable Name=\"stepLabelLabelId\" Default=\"1\" /><x:B>t</x:B></A>";
        const string b = "<A xmlns:x=\"u\">\r\n  <Variable Name=\"stepLabelLabelId\" Default=\"2\" />\r\n  <x:B>t</x:B>\r\n</A>";
        GoldenXaml.Canonical(a).Should().Be(GoldenXaml.Canonical(b));
        GoldenXaml.Canonical(a).Should().NotBe(GoldenXaml.Canonical(a.Replace(">t<", ">u<")));
        GoldenXaml.Canonical(a).Should().NotBe(GoldenXaml.Canonical("<A xmlns:y=\"u\"><Variable Name=\"stepLabelLabelId\" Default=\"1\" /><y:B>t</y:B></A>"));
    }
}
