using System.Globalization;
using YamlDotNet.Core;
using Version = System.Version;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Migration.Mapping;

/// <summary>
/// Reads a mapping file (spec §6) with the YAML representation model, walking the mappings by hand so that an unknown key or a
/// bad value is reported with its line. Only the shape is checked here; the cross-file rules are checked by the validator.
/// </summary>
public static class MapReader
{
    /// <summary>Parses <paramref name="yaml"/>; <paramref name="name"/> (usually the file name) is kept in the result.</summary>
    public static MapFile Parse(string yaml, string name)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException e)
        {
            throw new MapFormatException($"line {e.Start.Line}: not valid YAML: {e.Message}");
        }
        catch (ArgumentException e)
        {
            throw new MapFormatException($"not valid YAML: {e.Message}");
        }
        if (stream.Documents.Count == 0) throw new MapFormatException("the map is empty.");
        var root = Map(stream.Documents[0].RootNode, "the map");

        int? version = null;
        var requires = new List<MapRequirement>();
        var tables = new List<MapTable>();
        var relationships = new List<MapRelationship>();
        foreach (var (key, value) in Entries(root, ["version", "requires", "tables", "relationships"]))
        {
            switch (key)
            {
                case "version":
                    version = Int(value, "version");
                    if (version != 1) throw Error(value, $"version must be 1, not {version}.");
                    break;
                case "requires":
                    foreach (var n in value is YamlSequenceNode seq ? seq.Children : [value]) requires.Add(Requirement(n));
                    break;
                case "tables":
                    foreach (var n in Seq(value, "tables")) tables.Add(Table(n));
                    break;
                case "relationships":
                    foreach (var n in Seq(value, "relationships")) relationships.Add(Relationship(n));
                    break;
            }
        }
        if (version is null) throw Error(root, "version is missing (it must be 1).");
        return new MapFile(name, 1, requires, tables, relationships);
    }

    private static MapRequirement Requirement(YamlNode node)
    {
        string? solution = null;
        Version? min = null;
        foreach (var (key, value) in Entries(Map(node, "a requires entry"), ["solution", "minVersion"]))
        {
            if (key == "solution") solution = Str(value, "solution");
            else
            {
                var text = Str(value, "minVersion");
                try { min = Version.Parse(text); }
                catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
                {
                    throw Error(value, $"minVersion '{text}' is not a version of two to four numbers.");
                }
            }
        }
        if (solution is null) throw Error(node, "a requires entry needs 'solution'.");
        if (min is null) throw Error(node, $"requires '{solution}' needs 'minVersion'.");
        return new MapRequirement(solution, min);
    }

    private static MapRelationship Relationship(YamlNode node)
    {
        string? source = null, target = null;
        foreach (var (key, value) in Entries(Map(node, "a relationship"), ["source", "target"]))
        {
            if (key == "source") source = Str(value, "source");
            else target = Str(value, "target");
        }
        if (source is null || target is null) throw Error(node, "a relationship needs 'source' and 'target'.");
        return new MapRelationship(source, target);
    }

    private static MapTable Table(YamlNode node)
    {
        string? source = null, target = null;
        var columns = new Dictionary<string, MapColumn>(StringComparer.Ordinal);
        var derived = new Dictionary<string, MapDerived>(StringComparer.Ordinal);
        var state = new List<MapStatePair>();
        var stateFallback = new Dictionary<int, (int State, int Status)>();
        var carry = true;
        var archive = false;
        foreach (var (key, value) in Entries(Map(node, "a table entry"), ["source", "target", "columns", "derived", "state", "stateFallback", "customColumns", "archive"]))
        {
            switch (key)
            {
                case "source": source = Str(value, "source"); break;
                case "target": target = Str(value, "target"); break;
                case "columns":
                    foreach (var (column, spec) in Entries(Map(value, "columns"), null)) columns[column] = Column(spec);
                    break;
                case "derived":
                    foreach (var (column, spec) in Entries(Map(value, "derived"), null)) derived[column] = Derived(spec);
                    break;
                case "state":
                    foreach (var n in Seq(value, "state")) state.Add(StatePair(n));
                    break;
                case "stateFallback":
                    foreach (var (from, pair) in Entries(Map(value, "stateFallback"), null))
                        stateFallback[Int(KeyNode(value, from), "a stateFallback key")] = Pair(pair);
                    break;
                case "archive":
                    archive = Bool(value, "archive");
                    break;
                case "customColumns":
                    carry = Str(value, "customColumns") switch
                    {
                        "carry" => true,
                        "drop" => false,
                        var other => throw Error(value, $"customColumns must be 'carry' or 'drop', not '{other}'."),
                    };
                    break;
            }
        }
        if (source is null || (target is null && !archive)) throw Error(node, "a table entry needs 'source' and 'target'.");
        // An archive entry may leave the target out: it is the source's own archive table.
        target ??= ArchivePrefix + source;
        if (!archive && columns.Values.Any(c => c.Drop))
            throw Error(node, $"'target: null' (drop a column) is only for an archive entry; table '{source}' has no 'archive: true'.");
        return new MapTable(source, target, columns, derived, state, stateFallback, carry, archive);
    }

    /// <summary>The prefix of the tables <c>archive: true</c> creates (spec §8.7).</summary>
    public const string ArchivePrefix = "xrmva_";

    private static MapColumn Column(YamlNode node)
    {
        if (IsNull(node)) return new MapColumn("", null, false, default, Drop: true);
        if (node is YamlScalarNode) return new MapColumn(Str(node, "a column target"), null, false, default);
        string? target = null;
        var drop = false;
        var tr = new Translation();
        foreach (var (key, value) in Entries(Map(node, "a column"), ["target", "values", "byLabel", "fallback"]))
        {
            if (key == "target")
            {
                if (IsNull(value)) drop = true;
                else target = Str(value, "target");
            }
            else tr.Read(key, value);
        }
        if (drop) return new MapColumn("", tr.Values, tr.ByLabel, tr.Fallback, Drop: true);
        if (target is null) throw Error(node, "a column with options needs 'target'.");
        return new MapColumn(target, tr.Values, tr.ByLabel, tr.Fallback);
    }

    private static MapDerived Derived(YamlNode node)
    {
        string? from = null;
        YamlNode? fromNode = null;
        var tr = new Translation();
        foreach (var (key, value) in Entries(Map(node, "a derived entry"), ["from", "values", "byLabel", "fallback"]))
        {
            if (key == "from") { from = Str(value, "from"); fromNode = value; }
            else tr.Read(key, value);
        }
        if (from is null) throw Error(node, "a derived entry needs 'from'.");
        var parts = from.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw Error(fromNode!, $"'from: {from}' must be <lookup>.<column> (one dot).");
        return new MapDerived(parts[0], parts[1], tr.Values, tr.ByLabel, tr.Fallback);
    }

    private static MapStatePair StatePair(YamlNode node)
    {
        (int State, int Status)? from = null, to = null;
        foreach (var (key, value) in Entries(Map(node, "a state entry"), ["from", "to"]))
        {
            if (key == "from") from = Pair(value);
            else to = Pair(value);
        }
        if (from is null || to is null) throw Error(node, "a state entry needs 'from' and 'to'.");
        return new MapStatePair(from.Value, to.Value);
    }

    private static (int State, int Status) Pair(YamlNode node)
    {
        var items = Seq(node, "a state pair");
        if (items.Count != 2) throw Error(node, "a state pair is [statecode, statuscode].");
        return (Int(items[0], "statecode"), Int(items[1], "statuscode"));
    }

    /// <summary>The <c>values</c>, <c>byLabel</c> and <c>fallback</c> keys shared by columns and derived entries.</summary>
    private sealed class Translation
    {
        public IReadOnlyDictionary<int, int>? Values { get; private set; }
        public bool ByLabel { get; private set; }
        public MapFallback Fallback { get; private set; }

        public void Read(string key, YamlNode value)
        {
            switch (key)
            {
                case "values":
                    var values = new Dictionary<int, int>();
                    foreach (var (k, v) in Entries(Map(value, "values"), null)) values[Int(KeyNode(value, k), "a value key")] = Int(v, "a value");
                    Values = values;
                    break;
                case "byLabel":
                    ByLabel = Bool(value, "byLabel");
                    break;
                case "fallback":
                    Fallback = IsNull(value) ? new MapFallback(true, null) : new MapFallback(true, Int(value, "fallback"));
                    break;
            }
        }
    }

    private static bool Bool(YamlNode node, string what)
    {
        var text = Str(node, what);
        return text switch
        {
            "true" => true,
            "false" => false,
            _ => throw Error(node, $"{what} must be true or false, not '{text}'."),
        };
    }

    private static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "null" or "Null" or "NULL" or "~" };

    private static IEnumerable<(string Key, YamlNode Value)> Entries(YamlMappingNode map, string[]? allowed)
    {
        foreach (var (k, v) in map.Children)
        {
            if (k is not YamlScalarNode { Value: { } key }) throw Error(k, "a key must be a plain name.");
            if (allowed is not null && Array.IndexOf(allowed, key) < 0)
                throw Error(k, $"unknown key '{key}' (allowed: {string.Join(", ", allowed)}).");
            yield return (key, v);
        }
    }

    /// <summary>The key node named <paramref name="key"/> of <paramref name="map"/>, for a number key's line.</summary>
    private static YamlNode KeyNode(YamlNode map, string key) =>
        ((YamlMappingNode)map).Children.Keys.First(k => k is YamlScalarNode s && s.Value == key);

    private static YamlMappingNode Map(YamlNode node, string what) =>
        node as YamlMappingNode ?? throw Error(node, $"{what} must be a mapping.");

    private static IList<YamlNode> Seq(YamlNode node, string what) =>
        node is YamlSequenceNode s ? s.Children : throw Error(node, $"{what} must be a list.");

    private static string Str(YamlNode node, string what) =>
        node is YamlScalarNode { Value: { Length: > 0 } v } ? v : throw Error(node, $"{what} must be a non-empty text.");

    private static int Int(YamlNode node, string what)
    {
        var text = Str(node, what);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw Error(node, $"{what} must be a whole number, not '{text}'.");
    }

    private static MapFormatException Error(YamlNode node, string message) => new($"line {node.Start.Line}: {message}");
}
