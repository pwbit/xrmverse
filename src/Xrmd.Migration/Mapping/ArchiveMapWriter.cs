using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Migration.Mapping;

/// <summary>
/// Adds the archive tables an owner chose after a dry run (migration table mapping spec §8.7) to a map file, as text: the file's own
/// text, comments and layout stay as they are, and one entry <c>- { source: &lt;t&gt;, target: xrmva_&lt;t&gt;, archive: true }</c> per
/// chosen table goes at the end of its <c>tables:</c> list (a list created at the end of the file when it has none; a whole map with
/// <c>version: 1</c> when there is no file). The entries are never written back from the parsed map.
/// </summary>
public static partial class ArchiveMapWriter
{
    /// <summary>
    /// <paramref name="existingYaml"/> with an archive entry for each of <paramref name="tables"/> that the map does not name yet; a
    /// table the map names already (mapped or archived) is skipped. Each entry has a comment line with its rows and, when some names
    /// would not fit, the shortened ones (<see cref="ArchiveNames"/>), so that the owner can rename them.
    /// </summary>
    /// <param name="catalog">The package's source catalog: the columns of each table, for the shortened names.</param>
    /// <param name="candidates">
    /// The archive candidates of the package with their rows (the manifest's <c>ArchiveCandidates</c>, or a dry run's report); a table
    /// must be one of them.
    /// </param>
    /// <exception cref="MapFormatException"><paramref name="existingYaml"/> is not a valid map, or its <c>tables</c> list is written inline.</exception>
    /// <exception cref="MapValidationException">A table is not a candidate, or the map with the new entries is not valid.</exception>
    public static string Append(string? existingYaml, IReadOnlyList<string> tables, SourceCatalog catalog,
        IReadOnlyDictionary<string, long> candidates)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(candidates);
        var text = existingYaml ?? "";
        var hasMap = HasDocument(text);
        var named = hasMap ? MapReader.Parse(text, "map").Tables.Select(t => t.Source).ToHashSet(StringComparer.Ordinal) : [];

        var added = tables.Distinct(StringComparer.Ordinal).Where(t => !named.Contains(t)).ToList();
        var problems = added.Where(t => !candidates.ContainsKey(t))
            .Select(t => $"Archive: '{t}' is not an archive candidate of the package (a source table with rows that the target does not have and no map names).")
            .ToList();
        if (problems.Count > 0) throw new MapValidationException(problems);
        if (added.Count == 0) return text;

        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n');
        var (insertAt, indent) = hasMap ? TablesBlock(lines) : (-1, "  ");
        var block = new StringBuilder();
        foreach (var table in added) Entry(block, table, indent, catalog, candidates, nl);

        string result;
        if (insertAt < 0)
        {
            // No list yet: one at the end of the file (with the version when there is no map at all).
            var head = text.Length == 0 || text.EndsWith('\n') ? text : text + nl;
            result = head + (hasMap ? "" : "version: 1" + nl) + "tables:" + nl + block;
        }
        else
        {
            // After the last line of the list: the original text before and after it is kept byte for byte.
            var offset = lines.Take(insertAt + 1).Sum(l => l.Length + 1);
            result = offset > text.Length
                ? (text.EndsWith('\n') ? text : text + nl) + block
                : text[..offset] + block + text[offset..];
        }

        try
        {
            MapSet.Combine([MapReader.Parse(result, "map")]);
        }
        catch (MapFormatException e)
        {
            throw new MapValidationException([$"The map with the archive tables is not valid: {e.Message}"]);
        }
        return result;
    }

    /// <summary>The entry of <paramref name="table"/> and its comment lines.</summary>
    private static void Entry(StringBuilder block, string table, string indent, SourceCatalog catalog, IReadOnlyDictionary<string, long> candidates, string nl)
    {
        var target = ArchiveNames.Table(table);
        var rows = candidates[table].ToString(CultureInfo.InvariantCulture);
        block.Append(indent).Append("# ").Append(rows).Append(" row(s); columns become ").Append(ArchiveNames.Prefix).Append("<column>").Append(nl);
        var shortened = Shortened(table, target, catalog);
        if (shortened.Count > 0)
            block.Append(indent).Append("# shortened names: ").Append(string.Join(", ", shortened.Select(s => $"{s.Source} -> {s.Target}"))).Append(nl);
        block.Append(indent).Append("- { source: ").Append(table).Append(", target: ").Append(target).Append(", archive: true }").Append(nl);
    }

    /// <summary>The names <see cref="ArchiveNames"/> shortens for this table: the table's own, then its columns', as the archive would create them.</summary>
    private static List<(string Source, string Target)> Shortened(string table, string target, SourceCatalog catalog)
    {
        var list = new List<(string Source, string Target)>();
        if (target != ArchiveNames.Prefix + table) list.Add((table, target));
        if (catalog.Tables.FirstOrDefault(t => t.LogicalName == table) is not { } source) return list;
        var entry = new MapTable(table, target, new Dictionary<string, MapColumn>(), new Dictionary<string, MapDerived>(), [],
            new Dictionary<int, (int, int)>(), true, Archive: true);
        foreach (var column in ArchiveColumns.Plan(entry, source, catalog, _ => false))
        {
            var name = column.Source.LogicalName;
            if (column.Target is { } t && column.Kind is ArchiveColumnKind.Copied or ArchiveColumnKind.Lookup or ArchiveColumnKind.LookupAsText
                && t != ArchiveNames.Prefix + name)
                list.Add((name, t));
            if (column.TextTarget is { } text && text != ArchiveNames.Prefix + name + "name") list.Add((name, text));
        }
        return list;
    }

    /// <summary>
    /// Where the entries go: after the last line of the top-level <c>tables:</c> list when another key follows it (blank and comment lines
    /// before that key stay with it), else after the file's last line; and the indentation of its items. -1 when the map has no such list.
    /// </summary>
    private static (int InsertAfter, string Indent) TablesBlock(string[] lines)
    {
        var start = Array.FindIndex(lines, l => TablesKey().IsMatch(l.TrimEnd('\r')));
        if (start < 0) return (-1, "  ");
        var rest = TablesKey().Match(lines[start].TrimEnd('\r')).Groups["rest"].Value.Trim();
        if (rest.Length > 0 && !rest.StartsWith('#'))
            throw new MapFormatException($"line {start + 1}: 'tables' is written inline; write it as a list, one entry per line, to add archive tables.");

        var last = start;
        string? indent = null;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            // The list ends at the next top-level key or document marker; its items may stand at column 0 ("- source: …").
            var topLevel = line.Length == trimmed.Length;
            if (topLevel && (!trimmed.StartsWith('-') || trimmed.StartsWith("---", StringComparison.Ordinal))) return (last, indent ?? "  ");
            if (indent is null && trimmed.StartsWith('-')) indent = line[..(line.Length - trimmed.Length)];
            last = i;
        }
        // The list runs to the end of the file: the entries go after everything, so the file's text is a prefix of the result.
        return (lines.Length - 1, indent ?? "  ");
    }

    /// <summary>Whether <paramref name="yaml"/> holds a YAML document (not only blank lines and comments).</summary>
    private static bool HasDocument(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return false;
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or ArgumentException)
        {
            return true; // MapReader reports it, with its line
        }
        return stream.Documents.Count > 0;
    }

    [GeneratedRegex(@"^tables[ \t]*:(?<rest>.*)$")]
    private static partial Regex TablesKey();
}
