using System.Diagnostics;
using System.Xml.Linq;
using FluentAssertions;

namespace Xrmd.Repository.Tests;

/// <summary>
/// Plan 10, Task 2: repository rules from the legal guardrails spec (§3 packages and binaries,
/// §5 fixtures origin). The scanners are static methods over in-memory data, so each rule has a
/// negative case; the rule tests run them on the tracked files of the repository.
/// </summary>
public sealed class ArchitectureTests
{
    internal static readonly string RepoRoot = FindRepoRoot();

    private static readonly string[] DataverseClientAllowed =
    {
        "src/Xrmd.Abstractions/Xrmd.Abstractions.csproj",
        "src/Xrmd.Schema/Xrmd.Schema.csproj",
    };

    private const string DataverseClient = "Microsoft.PowerPlatform.Dataverse.Client";
    private const string CrmSdkPrefix = "Microsoft.CrmSdk.";

    /// <summary>The Microsoft workflow SDK (plan 2.14, W18): bring your own SDK, referenced only by the test activities.</summary>
    private const string CrmSdkWorkflow = "Microsoft.CrmSdk.Workflow";
    private const string CrmSdkWorkflowAllowed = "tests/Xrmd.TestWorkflowActivities/Xrmd.TestWorkflowActivities.csproj";

    /// <summary>CoreWF (plan 2.14, MIT): the runtime package only, and only in Xrmd.Workflows; the full package never.</summary>
    private const string WorkflowRuntime = "UiPath.Workflow.Runtime";
    private const string WorkflowRuntimeVersion = "6.0.3";
    private const string WorkflowFull = "UiPath.Workflow";
    private const string WorkflowRuntimeAllowed = "src/Xrmd.Workflows/Xrmd.Workflows.csproj";

    // ---- rules on the repository ----

    [Fact]
    public void Dataverse_client_is_referenced_only_by_abstractions_and_schema()
    {
        var violations = DataverseClientViolations(LoadBuildFiles());

        violations.Should().BeEmpty("Microsoft.PowerPlatform.Dataverse.Client may be referenced only by Xrmd.Abstractions and Xrmd.Schema (spec §3)");
    }

    [Fact]
    public void Model_project_has_no_references()
    {
        var model = "src/Xrmd.Model/Xrmd.Model.csproj";
        var files = LoadBuildFiles();

        // The inherited SNI runtime reference of Directory.Build.props is not in the csproj, so it does not count here.
        files.Should().Contain(f => f.Path == model, "the SDK-free model project exists");
        files.Single(f => f.Path == model).Packages.Should().BeEmpty("Xrmd.Model has no package reference (S.2b1)");
        XDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, model))).Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference").Should().BeEmpty("Xrmd.Model has no project reference (S.2b1)");
    }

    [Fact]
    public void Crm_sdk_packages_are_referenced_only_under_tests()
    {
        var violations = CrmSdkViolations(LoadBuildFiles());

        violations.Should().BeEmpty("Microsoft.CrmSdk.* packages are for tests/ only (spec §3)");
    }

    [Fact]
    public void No_src_project_references_Microsoft_CrmSdk_Workflow()
    {
        var files = LoadBuildFiles();

        CrmSdkWorkflowViolations(files).Should().BeEmpty(
            "Microsoft.CrmSdk.Workflow is installed by the customer (issue #45); only the test activities reference it (spec §3)");
        files.Should().Contain(f => f.Path == CrmSdkWorkflowAllowed && f.Packages.Contains(CrmSdkWorkflow),
            "the test activities are built against the real workflow SDK");
    }

    [Fact]
    public void No_src_build_file_has_a_file_reference_to_a_microsoft_sdk()
    {
        var violations = GitLsFiles("src")
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => SdkFileReferenceViolations(p, File.ReadAllText(Path.Combine(RepoRoot, p))))
            .ToList();

        violations.Should().BeEmpty("src/ never references the Microsoft SDK or workflow SDK at compile time (spec §3, plan 2.14 W18)");
    }

    [Fact]
    public void No_src_source_file_uses_the_workflow_sdk_namespace()
    {
        var sources = GitLsFiles("src").Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();

        sources.Should().NotBeEmpty();
        var violations = sources
            .SelectMany(p => WorkflowSdkCodeViolations(p, File.ReadAllText(Path.Combine(RepoRoot, p))))
            .ToList();

        violations.Should().BeEmpty(
            "src/ names Microsoft.Xrm.Sdk.Workflow only in string literals and comments, never in code (plan 2.14 W18)");
    }

    [Fact]
    public void UiPath_workflow_runtime_only_in_Xrmd_Workflows()
    {
        var files = LoadBuildFiles();

        WorkflowRuntimeViolations(files).Should().BeEmpty();
        var workflows = XDocument.Load(Path.Combine(RepoRoot, WorkflowRuntimeAllowed)).Descendants()
            .Where(e => e.Name.LocalName == "PackageReference" && (string?)e.Attribute("Include") == WorkflowRuntime).ToList();
        workflows.Should().ContainSingle("Xrmd.Workflows runs the custom workflow activities with CoreWF")
            .Which.Attribute("Version")!.Value.Should().Be(WorkflowRuntimeVersion);
    }

    [Fact]
    public void No_microsoft_binaries_are_committed()
    {
        var violations = BinaryViolations(GitLsFiles());

        violations.Should().BeEmpty("no .dll, .exe or .nupkg is committed (spec §3)");
    }

    [Fact]
    public void Every_fixture_file_is_listed_in_fixtures_readme()
    {
        var readmePath = Path.Combine(RepoRoot, "fixtures", "README.md");
        File.Exists(readmePath).Should().BeTrue("fixtures/README.md records the origin of every fixture (spec §5)");

        var fixtures = GitLsFiles("fixtures/seed", "fixtures/ui", "fixtures/migration")
            .Select(p => p["fixtures/".Length..]);

        var problems = FixtureReadmeProblems(fixtures, File.ReadAllText(readmePath));

        problems.Should().BeEmpty();
    }

    // ---- negative cases of the scanners ----

    [Fact]
    public void Scanner_flags_dataverse_client_in_another_project()
    {
        var files = new[]
        {
            Build("src/Xrmd.Abstractions/Xrmd.Abstractions.csproj", DataverseClient),
            Build("src/Xrmd.Schema/Xrmd.Schema.csproj", DataverseClient),
            Build("src/Xrmd.Core/Xrmd.Core.csproj", "microsoft.powerplatform.dataverse.client"),
            Build("tests/Xrmd.Core.Tests/Xrmd.Core.Tests.csproj", "xunit"),
            Build("tests/Directory.Build.props", DataverseClient),
            Build("Directory.Build.props", DataverseClient),
        };

        var violations = DataverseClientViolations(files);

        violations.Should().HaveCount(2);
        violations[0].Should().Contain("src/Xrmd.Core/Xrmd.Core.csproj");
        violations[1].Should().Contain("Directory.Build.props").And.NotContain("tests/");
    }

    [Fact]
    public void Scanner_parses_package_reference_and_package_version_from_xml()
    {
        const string xml = """
            <Project>
              <ItemGroup>
                <PackageReference Include="A.One" Version="1.0.0" />
                <PackageVersion Include="B.Two" Version="2.0.0" />
                <PackageReference Update="C.Three" Version="3.0.0" />
                <Reference Include="D.Four" />
              </ItemGroup>
            </Project>
            """;

        ParsePackageIds(xml).Should().BeEquivalentTo("A.One", "B.Two");
    }

    [Fact]
    public void Scanner_flags_crm_sdk_outside_tests()
    {
        var files = new[]
        {
            Build("tests/Xrmd.TestPlugins/Xrmd.TestPlugins.csproj", "Microsoft.CrmSdk.CoreAssemblies"),
            Build("tests/TestPlugins.props", "Microsoft.CrmSdk.Workflow"),
            Build("src/Xrmd.Pipeline/Xrmd.Pipeline.csproj", "Microsoft.CrmSdk.CoreAssemblies"),
            Build("src/Xrmd.Workflows/Xrmd.Workflows.csproj", "Microsoft.CrmSdk.Workflow"),
            Build("Directory.Build.props", "Microsoft.CrmSdk.XrmTooling.CoreAssembly"),
            Build("tools/Xrmd.Licenses/Xrmd.Licenses.csproj", "Newtonsoft.Json"),
        };

        var violations = CrmSdkViolations(files);

        violations.Should().HaveCount(3);
        violations.Should().Contain(v => v.Contains("src/Xrmd.Pipeline/Xrmd.Pipeline.csproj"));
        violations.Should().Contain(v => v.Contains("src/Xrmd.Workflows/Xrmd.Workflows.csproj") && v.Contains("Microsoft.CrmSdk.Workflow"));
        violations.Should().Contain(v => v.Contains("Directory.Build.props"));
    }

    [Fact]
    public void Scanner_flags_crm_sdk_workflow_outside_the_test_activities()
    {
        var files = new[]
        {
            Build(CrmSdkWorkflowAllowed, CrmSdkWorkflow),
            Build("src/Xrmd.Workflows/Xrmd.Workflows.csproj", "microsoft.crmsdk.workflow"),
            Build("tests/Xrmd.Integration.Tests/Xrmd.Integration.Tests.csproj", CrmSdkWorkflow),
            Build("tests/Xrmd.TestPlugins/Xrmd.TestPlugins.csproj", "Microsoft.CrmSdk.CoreAssemblies"),
        };

        var violations = CrmSdkWorkflowViolations(files);

        violations.Should().HaveCount(2);
        violations.Should().Contain(v => v.StartsWith("src/Xrmd.Workflows/Xrmd.Workflows.csproj", StringComparison.Ordinal));
        violations.Should().Contain(v => v.StartsWith("tests/Xrmd.Integration.Tests/", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_flags_workflow_runtime_outside_xrmd_workflows_and_the_full_package()
    {
        var files = new[]
        {
            Build(WorkflowRuntimeAllowed, WorkflowRuntime),
            Build("src/Xrmd.Host/Xrmd.Host.csproj", "uipath.workflow.runtime"),
            Build("tests/Xrmd.Workflows.Tests/Xrmd.Workflows.Tests.csproj", WorkflowRuntime),
            Build(WorkflowRuntimeAllowed, WorkflowFull),
        };

        var violations = WorkflowRuntimeViolations(files);

        violations.Should().HaveCount(3);
        violations.Should().Contain(v => v.StartsWith("src/Xrmd.Host/", StringComparison.Ordinal));
        violations.Should().Contain(v => v.StartsWith("tests/Xrmd.Workflows.Tests/", StringComparison.Ordinal));
        violations.Should().Contain(v => v.Contains(WorkflowFull + ";"));
    }

    [Theory]
    [InlineData("""<Project><ItemGroup><Reference Include="Microsoft.Xrm.Sdk.Workflow" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><Reference Include="microsoft.xrm.sdk.workflow, Version=9.0.0.0, PublicKeyToken=31bf3856ad364e35" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><Reference Include="Wf"><HintPath>$(NuGetPackageRoot)microsoft.crmsdk.workflow/9.0.2.60/lib/net462/Wf.dll</HintPath></Reference></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><Reference Include="Wf" HintPath="..\lib\Microsoft.Xrm.Sdk.Workflow.dll" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><Reference><Include>Microsoft.CrmSdk.CoreAssemblies</Include></Reference></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><Reference Include="Microsoft.Xrm.Sdk" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><Reference Include="Microsoft.Crm.Sdk.Proxy" /></ItemGroup></Project>""")]
    public void Scanner_flags_a_file_reference_to_a_microsoft_sdk_under_src(string xml)
    {
        SdkFileReferenceViolations("src/Xrmd.Host/Xrmd.Host.csproj", xml).Should().ContainSingle()
            .Which.Should().StartWith("src/Xrmd.Host/Xrmd.Host.csproj");
    }

    [Fact]
    public void Scanner_allows_other_file_references_and_sdk_references_under_tests()
    {
        const string other = """<Project><ItemGroup><Reference Include="System.Activities" /><Reference Include="Lib" HintPath="lib/Lib.dll" /></ItemGroup></Project>""";
        const string sdk = """<Project><ItemGroup><Reference Include="Microsoft.Xrm.Sdk.Workflow" /></ItemGroup></Project>""";

        SdkFileReferenceViolations("src/Xrmd.Host/Xrmd.Host.csproj", other).Should().BeEmpty();
        SdkFileReferenceViolations("tests/Xrmd.TestWorkflowActivities/Xrmd.TestWorkflowActivities.csproj", sdk).Should().BeEmpty();
    }

    [Theory]
    [InlineData("using Microsoft.Xrm.Sdk.Workflow;\nclass A { }")]
    [InlineData("using Microsoft.Xrm.Sdk.Workflow.Activities;\nclass A { }")]
    [InlineData("global using Microsoft . Xrm . Sdk . Workflow;")]
    [InlineData("using Wf = Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType;")]
    [InlineData("class A { object B() => typeof(global::Microsoft.Xrm.Sdk.Workflow.InputAttribute); }")]
    [InlineData("class A : Microsoft.Xrm.Sdk.Workflow.WorkflowActivity { }")]
    [InlineData("class A { string s = \"x\"; Microsoft.Xrm.Sdk.Workflow.IWorkflowContext c; }")]
    [InlineData("class A { char q = '\"'; Microsoft.Xrm.Sdk.Workflow.IWorkflowContext c; }")]
    [InlineData("class A { string s = @\"a\"\"b\"; Microsoft.Xrm.Sdk.Workflow.IWorkflowContext c; }")]
    [InlineData("class A { /* done */ Microsoft.Xrm.Sdk.Workflow.IWorkflowContext c; }")]
    public void Scanner_flags_workflow_sdk_namespace_in_code(string source)
    {
        WorkflowSdkCodeViolations("src/Xrmd.Workflows/A.cs", source).Should().ContainSingle()
            .Which.Should().StartWith("src/Xrmd.Workflows/A.cs:");
    }

    [Theory]
    [InlineData("class A { const string N = \"Microsoft.Xrm.Sdk.Workflow\"; }")]
    [InlineData("class A { const string N = \"clr-namespace:Microsoft.Xrm.Sdk.Workflow;assembly=Microsoft.Xrm.Sdk.Workflow\"; }")]
    [InlineData("class A { string N = @\"Microsoft.Xrm.Sdk.Workflow.\"\"x\"\"\"; }")]
    [InlineData("class A { string N = $\"{1}Microsoft.Xrm.Sdk.Workflow.X\"; }")]
    [InlineData("class A { string N = \"\"\"\n    Microsoft.Xrm.Sdk.Workflow.X \"\" still inside\n    \"\"\"; }")]
    [InlineData("class A { string N = \"a\\\"Microsoft.Xrm.Sdk.Workflow\\\"b\"; }")]
    [InlineData("// Microsoft.Xrm.Sdk.Workflow.CodeActivity\nclass A { }")]
    [InlineData("/// <summary><c>Microsoft.Xrm.Sdk.Workflow</c></summary>\nclass A { }")]
    [InlineData("/* using Microsoft.Xrm.Sdk.Workflow;\n */ class A { }")]
    [InlineData("using Microsoft.Xrm.Sdk.WorkflowLike;\nclass A { }")]
    [InlineData("using Microsoft.Xrm.Sdk;\nclass A { }")]
    public void Scanner_allows_workflow_sdk_names_in_literals_and_comments(string source)
    {
        WorkflowSdkCodeViolations("src/Xrmd.Workflows/A.cs", source).Should().BeEmpty();
    }

    [Fact]
    public void Scanner_flags_binaries()
    {
        var paths = new[] { "src/a.cs", "lib/Microsoft.Xrm.Sdk.dll", "tools/Setup.EXE", "packages/x.1.0.nupkg", "docs/dll.md" };

        BinaryViolations(paths).Should().BeEquivalentTo(
            "lib/Microsoft.Xrm.Sdk.dll", "tools/Setup.EXE", "packages/x.1.0.nupkg");
    }

    [Fact]
    public void Scanner_flags_unlisted_fixture_stale_row_and_missing_origin()
    {
        const string readme = """
            # Fixtures

            | file | content | origin |
            |---|---|---|
            | `seed/a.json` | table a | https://learn.microsoft.com/example |
            | `seed/gone.json` | table gone | origin to be confirmed |
            | `seed/c.json` | table c |  |
            """;

        var problems = FixtureReadmeProblems(new[] { "seed/a.json", "seed/b.json", "seed/c.json" }, readme);

        problems.Should().HaveCount(3);
        problems.Should().Contain(p => p.Contains("seed/b.json") && p.Contains("not listed"));
        problems.Should().Contain(p => p.Contains("seed/gone.json") && p.Contains("does not exist"));
        problems.Should().Contain(p => p.Contains("seed/c.json") && p.Contains("origin"));
    }

    // ---- scanners ----

    private sealed record BuildFile(string Path, IReadOnlyList<string> Packages);

    private static BuildFile Build(string path, params string[] packages) => new(path, packages);

    private static List<string> DataverseClientViolations(IEnumerable<BuildFile> files) =>
        files
            .Where(f => f.Packages.Contains(DataverseClient, StringComparer.OrdinalIgnoreCase))
            .Where(f => !DataverseClientAllowed.Contains(f.Path, StringComparer.Ordinal))
            .Where(f => !f.Path.StartsWith("tests/", StringComparison.Ordinal)) // a test dependency, never shipped (Plan 12, B3)
            .Select(f => $"{f.Path} references {DataverseClient}; only Xrmd.Abstractions and Xrmd.Schema may")
            .ToList();

    private static List<string> CrmSdkViolations(IEnumerable<BuildFile> files) =>
        files
            .Where(f => !f.Path.StartsWith("tests/", StringComparison.Ordinal))
            .SelectMany(f => f.Packages
                .Where(p => p.StartsWith(CrmSdkPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{f.Path} references {p}; Microsoft.CrmSdk.* packages are for tests/ only"))
            .ToList();

    private static List<string> CrmSdkWorkflowViolations(IEnumerable<BuildFile> files) =>
        files
            .Where(f => f.Packages.Contains(CrmSdkWorkflow, StringComparer.OrdinalIgnoreCase))
            .Where(f => !string.Equals(f.Path, CrmSdkWorkflowAllowed, StringComparison.Ordinal))
            .Select(f => $"{f.Path} references {CrmSdkWorkflow}; only {CrmSdkWorkflowAllowed} may")
            .ToList();

    private static List<string> WorkflowRuntimeViolations(IEnumerable<BuildFile> files) =>
        files.SelectMany(f => f.Packages
                .Where(p => string.Equals(p, WorkflowFull, StringComparison.OrdinalIgnoreCase)
                            || (string.Equals(p, WorkflowRuntime, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(f.Path, WorkflowRuntimeAllowed, StringComparison.Ordinal)))
                .Select(p => string.Equals(p, WorkflowFull, StringComparison.OrdinalIgnoreCase)
                    ? $"{f.Path} references {WorkflowFull}; only {WorkflowRuntime} is allowed"
                    : $"{f.Path} references {p}; only {WorkflowRuntimeAllowed} may"))
            .ToList();

    /// <summary>Names a <c>Reference</c> item must not carry under src/: the SDK is the customer's (bring your own, spec §3).</summary>
    private static readonly string[] SdkReferenceNames = ["Microsoft.Xrm.Sdk", "Microsoft.Crm.Sdk", "Microsoft.CrmSdk"];

    private static List<string> SdkFileReferenceViolations(string path, string xml)
    {
        if (!path.StartsWith("src/", StringComparison.Ordinal))
        {
            return [];
        }

        return XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName == "Reference")
            .Select(e => new[]
            {
                (string?)e.Attribute("Include"), ChildText(e, "Include"),
                (string?)e.Attribute("HintPath"), ChildText(e, "HintPath"),
            })
            .SelectMany(values => values.Where(v => !string.IsNullOrWhiteSpace(v)
                && SdkReferenceNames.Any(n => v!.Contains(n, StringComparison.OrdinalIgnoreCase))).Take(1))
            .Select(v => $"{path} has a file reference to {v}; src/ never references the Microsoft SDK at compile time")
            .ToList();

        static string? ChildText(XElement e, string name) =>
            (string?)e.Elements().FirstOrDefault(c => c.Name.LocalName == name);
    }

    private static readonly System.Text.RegularExpressions.Regex WorkflowSdkNamespace = new(
        @"\bMicrosoft\s*\.\s*Xrm\s*\.\s*Sdk\s*\.\s*Workflow\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Flags <c>Microsoft.Xrm.Sdk.Workflow</c> in C# code (a using directive, alias, base type or type reference): the
    /// workflow SDK is used by full name only in string literals (plan 2.14, W18). Comments and literals are blanked first.
    /// </summary>
    private static List<string> WorkflowSdkCodeViolations(string path, string source)
    {
        var code = CSharpCode(source);
        return WorkflowSdkNamespace.Matches(code)
            .Select(m => $"{path}:{code.AsSpan(0, m.Index).Count('\n') + 1} names {m.Value} in code; use the full name in a string literal")
            .ToList();
    }

    /// <summary>
    /// The C# source with comments, string literals (regular, verbatim, raw, interpolated: holes included) and char
    /// literals replaced by spaces; line breaks are kept so match positions map to lines.
    /// </summary>
    private static string CSharpCode(string source)
    {
        var s = source;
        var output = new System.Text.StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            var c = s[i];
            if (c == '/' && At(i + 1) == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && At(i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                {
                    i += s[i] == '\\' ? 2 : 1;
                }

                i = Math.Min(i + 1, s.Length);
            }
            else if (c is '"' or '$' or '@' && StringStart(i) is var (prefix, verbatim, quotes) && quotes > 0)
            {
                i += prefix;
                if (quotes >= 3)
                {
                    var close = new string('"', quotes);
                    var end = s.IndexOf(close, i + quotes, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + quotes;
                    while (i < s.Length && s[i] == '"')
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                    while (i < s.Length)
                    {
                        if (verbatim && s[i] == '"' && At(i + 1) == '"')
                        {
                            i += 2;
                        }
                        else if (s[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else if (!verbatim && s[i] == '\\')
                        {
                            i += 2;
                        }
                        else if (!verbatim && s[i] == '\n')
                        {
                            break;
                        }
                        else
                        {
                            i++;
                        }
                    }
                }
            }
            else
            {
                output.Append(c);
                i++;
                continue;
            }

            i = Math.Min(i, s.Length);
            foreach (var ch in s.AsSpan(start, i - start))
            {
                output.Append(ch == '\n' ? '\n' : ' ');
            }
        }

        return output.ToString();

        char At(int index) => index < s.Length ? s[index] : '\0';

        // A string literal at index: the prefix length ($ and @ markers), whether it is verbatim, and the opening quote
        // count (1 for a regular or verbatim literal, 3 or more for a raw one, 0 when no literal starts here).
        (int Prefix, bool Verbatim, int Quotes) StringStart(int index)
        {
            var j = index;
            var verbatim = false;
            while (j < s.Length && (s[j] == '$' || s[j] == '@'))
            {
                verbatim |= s[j] == '@';
                j++;
            }

            if (j >= s.Length || s[j] != '"' || (j > index && char.IsLetterOrDigit(At(index - 1))))
            {
                return (0, false, 0);
            }

            var quotes = 0;
            while (j + quotes < s.Length && s[j + quotes] == '"')
            {
                quotes++;
            }

            // "" is an empty regular literal, not the start of a raw one.
            return (j - index, verbatim, quotes >= 3 && !verbatim ? quotes : 1);
        }
    }

    private static List<string> BinaryViolations(IEnumerable<string> trackedPaths) =>
        trackedPaths
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static List<string> FixtureReadmeProblems(IEnumerable<string> fixtureFiles, string readme)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in readme.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('|')))
        {
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3)
            {
                continue;
            }

            var file = cells[0].Trim('`');
            if (file.Length == 0 || file == "file" || file.All(c => c == '-' || c == ':'))
            {
                continue;
            }

            rows[file] = cells[2];
        }

        var actual = fixtureFiles.ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        problems.AddRange(actual.Where(f => !rows.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{f} is not listed in fixtures/README.md"));
        problems.AddRange(rows.Keys.Where(f => !actual.Contains(f)).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{f} is listed in fixtures/README.md but does not exist"));
        problems.AddRange(rows.Where(r => actual.Contains(r.Key) && r.Value.Length == 0)
            .Select(r => $"{r.Key} has no origin in fixtures/README.md"));
        return problems;
    }

    private static List<string> ParsePackageIds(string xml) =>
        XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion")
            // Include as an attribute or as a child element; Update adds no package and is ignored.
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Elements().FirstOrDefault(c => c.Name.LocalName == "Include"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

    // ---- repository access ----

    private static List<BuildFile> LoadBuildFiles() =>
        GitLsFiles(".")
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Select(p => new BuildFile(p, ParsePackageIds(File.ReadAllText(Path.Combine(RepoRoot, p)))))
            .ToList();

    internal static List<string> GitLsFiles(params string[] pathspecs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("ls-files");
        psi.ArgumentList.Add("--");
        foreach (var spec in pathspecs)
        {
            psi.ArgumentList.Add(spec);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "git ls-files must work: " + error);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "xrmverse.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }
}
