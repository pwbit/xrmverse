using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Xrmd.Core.Tests.Storage;

/// <summary>
/// Plan 2.7b, Task 1 (ruling Q11): T-SQL stays inside the SQL Server classes. Every other file of <c>src/</c> gets its engine-specific
/// SQL text from <c>ISqlDialect</c>, so it runs on every storage provider. The scan looks inside C# string literals only (regular,
/// verbatim, interpolated and raw), so comments and identifiers never match. Files outside <c>SqlServer/</c> folders and
/// <c>SqlServerOrgProvisioner.cs</c> that still hold T-SQL are on <see cref="Allowed"/>: a reason with a task number means that
/// task converts the file and deletes the entry; a reason without one is permanent.
/// </summary>
public sealed partial class TsqlConfinementTests
{
    /// <summary>Repo-relative path to reason. Each 2.7b task removes the files it converts (the stale-entry test enforces it).</summary>
    internal static readonly Dictionary<string, string> Allowed = new()
    {
        // ---- permanent ----
        // An error message that names OPENJSON to say what the PostgreSQL dialect cannot do; no T-SQL is emitted.
        ["src/Xrmd.Storage.PostgreSql/PostgresDialect.cs"] = "error message names OPENJSON, not T-SQL text",
    };

    private const string Pattern =
        @"\bTOP\s*\(|SELECT TOP\b|WITH \((UPDLOCK|HOLDLOCK|ROWLOCK|READCOMMITTEDLOCK|TABLOCK|REPEATABLEREAD|SERIALIZABLE)|OPENJSON|SYSUTCDATETIME|NEWID\(\)|@@ROWCOUNT|OUTPUT (inserted|deleted)|ISNULL\(|DATEADD\(|COLLATE Latin1|(meta|data)\.\[";

    [GeneratedRegex(Pattern)]
    private static partial Regex Tsql();

    [Fact]
    public void Only_sql_server_classes_contain_tsql()
    {
        var found = Scan();

        var unexpected = found.Where(kv => !Allowed.ContainsKey(kv.Key)).Select(kv => $"{kv.Key} -> {kv.Value}").ToList();

        if (unexpected.Count > 0)
            throw new Xunit.Sdk.XunitException(
                "T-SQL belongs in SqlServer/ classes; take the text from ISqlDialect, or add the file to Allowed with the 2.7b task that converts it (plan 2.7b, ruling Q11):\n"
                + string.Join("\n", unexpected));
    }

    [Fact]
    public void Allow_list_has_no_stale_entries()
    {
        var found = Scan();

        var stale = Allowed.Keys.Where(k => !found.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();

        if (stale.Count > 0)
            throw new Xunit.Sdk.XunitException(
                "a file that no longer matches must leave Allowed, so it cannot slip back in unnoticed:\n" + string.Join("\n", stale));
    }

    /// <summary>Repo-relative path to "line: matching literal text" (the first match) for every scanned file with T-SQL.</summary>
    private static Dictionary<string, string> Scan()
    {
        var root = FindRepoRoot();
        var result = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                     .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
                     .Where(f => !f.Contains("/SqlServer/") && !f.EndsWith("/SqlServerOrgProvisioner.cs", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var hit = FirstMatch(File.ReadAllText(Path.Combine(root, path)));
            if (hit is not null) result[path] = hit;
        }

        return result;
    }

    internal static string? FirstMatch(string source)
    {
        foreach (var (line, text) in StringLiterals(source))
        {
            var m = Tsql().Match(text);
            if (m.Success)
            {
                var offset = text.AsSpan(0, m.Index).Count('\n');
                return $"{line + offset}: {m.Value} in \"{Shorten(text, m.Index)}\"";
            }
        }

        return null;
    }

    private static string Shorten(string text, int index)
    {
        var start = Math.Max(0, index - 30);
        var length = Math.Min(text.Length - start, 90);
        return text.Substring(start, length).Replace("\r", " ").Replace("\n", " ");
    }

    /// <summary>
    /// The string literals of a C# source with the 1-based line each starts on: regular, verbatim, interpolated, interpolated verbatim
    /// and raw (<c>"""</c>, <c>$$"""</c>). Comments and char literals are skipped. The code of an interpolation hole is scanned
    /// recursively (a literal nested in a hole is a literal of its own) and appears in the enclosing literal as <c>{}</c>.
    /// </summary>
    internal static IEnumerable<(int Line, string Text)> StringLiterals(string source)
    {
        var scanner = new LiteralScanner(source);
        scanner.ScanCode(inHole: false);
        return scanner.Results;
    }

    private sealed class LiteralScanner(string s)
    {
        private int i;
        private int line = 1;
        public List<(int Line, string Text)> Results { get; } = [];

        /// <summary>Scans code; in a hole it stops (without consuming it) at the closing brace of the hole.</summary>
        public void ScanCode(bool inHole)
        {
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\n') { line++; i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }

                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                    {
                        if (s[i] == '\n') line++;
                        i++;
                    }

                    i += 2;
                    continue;
                }

                if (c == '\'')
                {
                    i++;
                    while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                    {
                        if (s[i] == '\\') i++;
                        i++;
                    }

                    i++;
                    continue;
                }

                if (inHole && c == '{') { depth++; i++; continue; }
                if (inHole && c == '}')
                {
                    if (depth == 0) return;
                    depth--;
                    i++;
                    continue;
                }

                // A literal starts at an optional run of $ and @ prefixes (at most three) followed by a quote.
                var p = i;
                while (p < s.Length && (s[p] == '$' || s[p] == '@') && p - i < 3) p++;
                if (p < s.Length && s[p] == '"' && (p == i || IsPrefixStart(s, i))) ReadString(p);
                else i++;
            }
        }

        private void ReadString(int quotePos)
        {
            var dollars = 0;
            var verbatim = false;
            for (var k = i; k < quotePos; k++)
            {
                if (s[k] == '$') dollars++; else verbatim = true;
            }

            var startLine = line;
            var slot = Results.Count;
            var quotes = 0;
            while (quotePos + quotes < s.Length && s[quotePos + quotes] == '"') quotes++;

            var sb = new StringBuilder();
            if (!verbatim && quotes >= 3)
            {
                // Raw literal: ends at the same number of quotes; with N dollars a hole is N braces.
                var end = new string('"', quotes);
                i = quotePos + quotes;
                while (i < s.Length && string.CompareOrdinal(s, i, end, 0, quotes) != 0)
                {
                    if (dollars > 0 && s[i] == '{')
                    {
                        var run = 0;
                        while (i + run < s.Length && s[i + run] == '{') run++;
                        if (run >= dollars)
                        {
                            sb.Append('{', run - dollars).Append("{}");
                            i += run;
                            ScanCode(inHole: true);
                            i = Math.Min(s.Length, i + dollars);
                        }
                        else
                        {
                            sb.Append('{', run);
                            i += run;
                        }

                        continue;
                    }

                    if (s[i] == '\n') line++;
                    sb.Append(s[i++]);
                }

                i = Math.Min(s.Length, i + quotes);
            }
            else
            {
                i = quotePos + 1;
                while (i < s.Length)
                {
                    var d = s[i];
                    if (d == '"')
                    {
                        if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        break;
                    }

                    if (dollars > 0 && d == '{')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '{') { sb.Append("{{"); i += 2; continue; }
                        sb.Append("{}");
                        i++;
                        ScanCode(inHole: true);
                        if (i < s.Length) i++;
                        continue;
                    }

                    if (d == '\\' && !verbatim && i + 1 < s.Length) { sb.Append(d).Append(s[i + 1]); i += 2; continue; }
                    if (d == '\n') line++;
                    sb.Append(d);
                    i++;
                }

                i++;
            }

            Results.Insert(slot, (startLine, sb.ToString()));
        }
    }

    /// <summary>The prefix run must not be the tail of an identifier (<c>a$"</c> never occurs, but <c>x@"</c> must not count).</summary>
    private static bool IsPrefixStart(string s, int i) => i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_');

    [Theory]
    [InlineData("var q = \"SELECT TOP (1) a FROM b\";", true)]
    [InlineData("var q = @\"SELECT 1\r\nFROM x WITH (UPDLOCK)\";", true)]
    [InlineData("var q = $\"SELECT ISNULL(a, {x})\";", true)]
    [InlineData("var q = $@\"SELECT {x} FROM data.[t]\";", true)]
    [InlineData("var q = \"\"\"\n    SELECT OPENJSON(x)\n    \"\"\";", true)]
    [InlineData("var q = \"a \\\" SYSUTCDATETIME()\";", true)]
    [InlineData("var q = @\"a \"\" NEWID()\";", true)]
    [InlineData("// SELECT TOP (1) in a comment\nvar a = 1;", false)]
    [InlineData("/* ISNULL( */ var a = 1;", false)]
    [InlineData("/// <summary>SELECT TOP 5, OPENJSON</summary>", false)]
    [InlineData("var topN = \"top\"; var q = Top(1);", false)]
    [InlineData("var c = '\"'; var q = ISNULL(a);", false)]
    [InlineData("var q = \"SELECT TOPIC FROM t\";", false)]
    [InlineData("var q = $\"x {(c ? \" WITH (TABLOCK)\" : \"\")}\";", true)]
    [InlineData("var q = $\"a {f($\"b {(c ? \"NEWID()\" : \"\")}\")} c\";", true)]
    [InlineData("var q = $\"a {(c ? \"x\" : \"y\")} b\"; var r = \"ISNULL(\";", true)]
    [InlineData("var q = $\"a {(c ? \"x\" : \"y\")} b\"; var r = ISNULL(1);", false)]
    [InlineData("var q = $\"{{ISNULL(}}\";", true)]
    [InlineData("var q = $$\"\"\"SELECT {{x}} OPENJSON {y}\"\"\";", true)]
    [InlineData("var q = @\"\"\"abc\"\"\"; var r = ISNULL(1);", false)]
    public void Scanner_finds_tsql_in_literals_only(string source, bool expected) =>
        (FirstMatch(source) is not null).Should().Be(expected);

    [Fact]
    public void Scanner_reports_the_line_of_the_match()
    {
        FirstMatch("var a = 1;\nvar b = \"x\";\nvar q = @\"first\nSELECT TOP (1) x\";")
            .Should().StartWith("4: ");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
