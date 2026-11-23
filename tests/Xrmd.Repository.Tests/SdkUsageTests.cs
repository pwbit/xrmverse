using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Xrmd.Repository.Tests;

/// <summary>
/// S.2b1: a shrink-only counter of the src/ files that still use the Microsoft SDK namespaces. The count per project may
/// only go down; lowering it means updating tests/fixtures/sdk-usage-baseline.json.
/// </summary>
/// <remarks>
/// A file counts when, outside comments, it has a using directive for an SDK namespace (plain, <c>global</c>, alias or
/// <c>static</c>), or names a member of one fully qualified (<c>Microsoft.Xrm.Sdk.Entity</c>, also behind <c>global::</c>).
/// String literals do not count: they hold wire-format names that stay when the SDK is gone (the NuGet package id,
/// workflow XAML <c>clr-namespace</c>s, the plugin interface name looked up in a user assembly), so counting them would
/// keep the counter from ever reaching zero. Code inside an interpolated string hole is blanked with its string.
/// </remarks>
public sealed class SdkUsageTests
{
    private const string SdkNamespace = @"(?:Microsoft\.(?:Xrm|Crm)\.Sdk|Microsoft\.PowerPlatform\.Dataverse)";

    // using X; global using X; using A = X...; using static X...; each optionally with global::.
    private static readonly Regex SdkUsing = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:@?\w+\s*=\s*)?(?:global::)?" + SdkNamespace + @"\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // A fully qualified member of an SDK namespace anywhere in the code: Microsoft.Xrm.Sdk.Entity, Microsoft.Crm.Sdk.Messages.
    private static readonly Regex SdkQualifiedName = new(@"\b" + SdkNamespace + @"\.[A-Z]", RegexOptions.CultureInvariant);

    private const string BaselinePath = "tests/fixtures/sdk-usage-baseline.json";

    [Fact]
    public void Sdk_usage_matches_the_baseline()
    {
        var sources = ArchitectureTests.GitLsFiles("src")
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path: p, Text: File.ReadAllText(Path.Combine(ArchitectureTests.RepoRoot, p))))
            .ToList();
        var baseline = ReadBaseline(File.ReadAllText(Path.Combine(ArchitectureTests.RepoRoot, BaselinePath)));

        var problems = SdkUsageProblems(sources, baseline);

        // FluentAssertions lists only the first item: name them all.
        problems.Should().BeEmpty("every project must match; all problems:\n{0}", string.Join("\n", problems));
    }

    [Fact]
    public void An_increase_over_the_baseline_fails()
    {
        var sources = new[]
        {
            ("src/Xrmd.Core/A.cs", "using Microsoft.Xrm.Sdk;\nclass A {}"),
            ("src/Xrmd.Core/B.cs", "using Microsoft.Crm.Sdk.Messages;\nclass B {}"),
        };

        SdkUsageProblems(sources, new Dictionary<string, int> { ["Xrmd.Core"] = 1 }).Should().Equal(
            "Xrmd.Core: 2 files use the Microsoft SDK, baseline 1 (the count may only go down)");
    }

    [Fact]
    public void A_decrease_below_the_baseline_fails()
    {
        var sources = new[] { ("src/Xrmd.Core/A.cs", "using Microsoft.PowerPlatform.Dataverse.Client;\nclass A {}") };

        SdkUsageProblems(sources, new Dictionary<string, int> { ["Xrmd.Core"] = 3 }).Should().Equal(
            "Xrmd.Core: 1 files use the Microsoft SDK, baseline 3: lower the baseline in tests/fixtures/sdk-usage-baseline.json");
    }

    [Fact]
    public void Matching_counts_pass_and_unrelated_files_are_ignored()
    {
        var sources = new[]
        {
            ("src/Xrmd.Core/A.cs", "using Microsoft.Xrm.Sdk;\nusing Microsoft.Xrm.Sdk.Query;"),
            ("src/Xrmd.Core/obj/B.cs", "using Microsoft.Xrm.Sdk;"),
            ("src/Xrmd.Core/bin/C.cs", "using Microsoft.Xrm.Sdk;"),
            ("src/Xrmd.Model/D.cs", "// using Microsoft.Xrm.Sdk;\nclass D {}"),
            ("src/Xrmd.Core/E.txt", "using Microsoft.Xrm.Sdk;"),
        };

        SdkUsageProblems(sources, new Dictionary<string, int> { ["Xrmd.Core"] = 1 }).Should().BeEmpty();
    }

    [Fact]
    public void A_project_missing_from_the_baseline_has_a_baseline_of_zero()
    {
        var sources = new[] { ("src/Xrmd.New/A.cs", "using Microsoft.Xrm.Sdk;") };

        SdkUsageProblems(sources, new Dictionary<string, int>()).Should().Equal(
            "Xrmd.New: 1 files use the Microsoft SDK, baseline 0 (the count may only go down)");
    }

    /// <summary>Each form of SDK use counts its file once: every using form and fully qualified names in code.</summary>
    [Theory]
    [InlineData("global using Microsoft.Xrm.Sdk;\nclass A {}")]
    [InlineData("global using global::Microsoft.Xrm.Sdk.Metadata;\nclass A {}")]
    [InlineData("using Sdk = Microsoft.Xrm.Sdk;\nclass A {}")]
    [InlineData("global using EntityMetadata = Microsoft.Xrm.Sdk.Metadata.EntityMetadata;\nclass A {}")]
    [InlineData("using static Microsoft.Crm.Sdk.Messages.SomeType;\nclass A {}")]
    [InlineData("class A { void M(Microsoft.Xrm.Sdk.Metadata.EntityMetadata e) {} }")]
    [InlineData("class A { static readonly object T = typeof(global::Microsoft.Xrm.Sdk.Entity); }")]
    [InlineData("class A { Microsoft.PowerPlatform.Dataverse.Client.ServiceClient? C; }")]
    [InlineData("class A { const string Url = \"https://x\"; object T = typeof(Microsoft.Crm.Sdk.Messages.WhoAmIRequest); }")]
    [InlineData("class A { /* note */ Microsoft.Xrm.Sdk.Entity? E; }")]
    public void Every_form_of_sdk_use_counts(string text)
    {
        var sources = new[] { ("src/Xrmd.Core/A.cs", text) };

        SdkUsageProblems(sources, new Dictionary<string, int> { ["Xrmd.Core"] = 0 }).Should().Equal(
            "Xrmd.Core: 1 files use the Microsoft SDK, baseline 0 (the count may only go down)");
    }

    /// <summary>Comments and string literals never count (see the remarks); nor does a look-alike identifier.</summary>
    [Theory]
    [InlineData("// uses Microsoft.Xrm.Sdk.Entity\nclass A {}")]
    [InlineData("/// <summary>Like <see cref=\"Microsoft.Xrm.Sdk.Metadata.EntityMetadata\"/>.</summary>\nclass A {}")]
    [InlineData("/* Microsoft.Xrm.Sdk.Entity\n   Microsoft.Crm.Sdk.Messages.X */\nclass A {}")]
    [InlineData("class A {} // global using Microsoft.Xrm.Sdk;")]
    [InlineData("class Microsoft_Xrm_Sdk_Entity {}")]
    [InlineData("class A { const string Name = \"Microsoft.Xrm.Sdk.Entity\"; }")]
    [InlineData("class A { const string Ns = @\"clr-namespace:Microsoft.Xrm.Sdk.Query;\"\" // \"; }")]
    [InlineData("class A { const string Xaml = \"\"\"\n  Microsoft.Xrm.Sdk.Workflow.Activities\n  \"\"\"; }")]
    [InlineData("class A { const char C = '\"'; const string S = \"Microsoft.Crm.Sdk.Proxy\"; }")]
    public void Comments_and_string_literals_do_not_count(string text)
    {
        var sources = new[] { ("src/Xrmd.Core/A.cs", text) };

        SdkUsageProblems(sources, new Dictionary<string, int>()).Should().BeEmpty();
    }

    private static Dictionary<string, int> ReadBaseline(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("files").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
    }

    /// <summary>Counts, per src/ project, the tracked .cs files that use the SDK (see the remarks), and compares with the baseline.</summary>
    internal static List<string> SdkUsageProblems(IEnumerable<(string Path, string Text)> sources, IReadOnlyDictionary<string, int> baseline)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, text) in sources)
        {
            var parts = path.Replace('\\', '/').Split('/');
            if (parts.Length < 3 || parts[0] != "src" || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || parts.Skip(2).Any(p => p is "obj" or "bin"))
            {
                continue;
            }

            if (UsesSdk(text))
            {
                counts[parts[1]] = counts.GetValueOrDefault(parts[1]) + 1;
            }
        }

        var problems = new List<string>();
        foreach (var project in counts.Keys.Concat(baseline.Keys).Distinct().OrderBy(p => p, StringComparer.Ordinal))
        {
            var count = counts.GetValueOrDefault(project);
            var allowed = baseline.GetValueOrDefault(project);
            if (count > allowed)
            {
                problems.Add($"{project}: {count} files use the Microsoft SDK, baseline {allowed} (the count may only go down)");
            }
            else if (count < allowed)
            {
                problems.Add($"{project}: {count} files use the Microsoft SDK, baseline {allowed}: lower the baseline in tests/fixtures/sdk-usage-baseline.json");
            }
        }

        return problems;
    }

    /// <summary>Whether <paramref name="text"/> uses an SDK namespace outside comments and string literals.</summary>
    internal static bool UsesSdk(string text)
    {
        var code = WithoutComments(text);
        return SdkUsing.IsMatch(code) || SdkQualifiedName.IsMatch(code);
    }

    /// <summary>
    /// <paramref name="text"/> with every <c>//</c> (also <c>///</c>) and <c>/* */</c> comment and every string and character
    /// literal blanked out (line breaks kept). A <c>//</c> inside a literal is not a comment.
    /// </summary>
    internal static string WithoutComments(string text)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Blank(result, text, i, end);
                i = end;
            }
            else if (c == '"')
            {
                var start = i;
                var quotes = 0;
                while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                if (quotes >= 3)
                {
                    // Raw string literal: ends at the same number of quotes.
                    var end = text.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + quotes;
                }
                else
                {
                    var verbatim = false;
                    for (var k = i - 1; k >= 0 && text[k] is '$' or '@'; k--) verbatim |= text[k] == '@';
                    i++;
                    while (i < text.Length)
                    {
                        if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                        if (!verbatim && text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '"' || !verbatim && text[i] == '\n') { i++; break; }
                        i++;
                    }
                    i = Math.Min(i, text.Length);
                }
                Blank(result, text, start, i);
            }
            else if (c == '\'')
            {
                var start = i++;
                while (i < text.Length && text[i] != '\'' && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, text.Length);
                Blank(result, text, start, i);
            }
            else
            {
                result.Append(c);
                i++;
            }
        }
        return result.ToString();
    }

    /// <summary>Appends a space for <c>text[start..end]</c>, keeping its line breaks.</summary>
    private static void Blank(StringBuilder result, string text, int start, int end)
    {
        result.Append(' ');
        foreach (var ch in text.AsSpan(start, end - start)) if (ch == '\n') result.Append('\n');
    }
}
