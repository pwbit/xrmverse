using FluentAssertions;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The documentation of <see cref="ActivityCatalog"/> (plan 2.14, Task 10): the table of docs/workflows.md is generated from the
/// catalog, one row per entry in catalog order.
/// </summary>
public class ActivityCatalogDocTests
{
    internal const string TableStart = "<!-- activity-catalog:start -->";
    internal const string TableEnd = "<!-- activity-catalog:end -->";

    /// <summary>The name of an entry in the docs: the namespace of an element becomes the prefix the parity export uses.</summary>
    internal static string DocName(CatalogEntry e)
    {
        foreach (var (ns, prefix) in new[]
                 {
                     (ActivityCatalog.SdkActivitiesNs, "mxswa:"),
                     (ActivityCatalog.ClientActivitiesNs, "mcwc:"),
                     (ActivityCatalog.ActivitiesNs, ""),
                 })
        {
            var start = "{" + ns + "}";
            if (e.Element.StartsWith(start, StringComparison.Ordinal))
                return prefix + e.Element[start.Length..];
        }
        return e.Element;
    }

    /// <summary>The table row of an entry, as docs/workflows.md holds it.</summary>
    internal static string DocRow(CatalogEntry e)
    {
        // Text with markup characters goes in a code span; a pipe is escaped so that it stays in its cell.
        var kind = e.Kind.IndexOfAny(['<', '>', '"', '*', '_', '|']) >= 0 ? $"`{e.Kind.Replace("|", "\\|")}`" : e.Kind;
        return $"| `{DocName(e)}` | {kind} | {e.Status} | {e.Source} | {e.Reason ?? ""} |";
    }

    internal static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "xrmverse.sln")))
                return Path.Combine(dir.FullName, relative);
        throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Workflows_page_lists_every_catalog_entry_as_generated()
    {
        var page = File.ReadAllText(RepoFile("docs/workflows.md")).ReplaceLineEndings("\n");
        var start = page.IndexOf(TableStart, StringComparison.Ordinal);
        var end = page.IndexOf(TableEnd, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "docs/workflows.md marks the generated table with {0}", TableStart);
        end.Should().BeGreaterThan(start);

        var rows = page[(start + TableStart.Length)..end].Split('\n')
            .Where(l => l.StartsWith("| `", StringComparison.Ordinal)).ToList();

        rows.Should().Equal(ActivityCatalog.Entries.Select(DocRow),
            "the table of docs/workflows.md is generated from ActivityCatalog (ActivityCatalogDocTests.DocRow), one row per entry in catalog order");
    }
}
