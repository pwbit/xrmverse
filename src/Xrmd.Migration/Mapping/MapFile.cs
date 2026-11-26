namespace Xrmd.Migration.Mapping;

/// <summary>A mapping file that cannot be used; the message starts with <c>line &lt;n&gt;: </c> when a YAML node is at fault.</summary>
public sealed class MapFormatException : Exception
{
    public MapFormatException(string message) : base(message) { }

    public MapFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A solution the target must have, at least at <see cref="MinVersion"/>.</summary>
public sealed record MapRequirement(string Solution, Version MinVersion);

/// <summary>
/// The value of a <c>fallback</c> key: <c>IsSet = false</c> when the key is absent, <c>IsSet = true, Value = null</c> for
/// <c>fallback: null</c> (the value is left empty).
/// </summary>
public readonly record struct MapFallback(bool IsSet, int? Value);

/// <summary>
/// A listed column of a table: its target column and how its whole-number values are translated. <paramref name="Drop"/> is
/// <c>target: null</c> (only in an archive entry): the column is left out of the archive table, and <paramref name="Target"/> is empty.
/// </summary>
public sealed record MapColumn(string Target, IReadOnlyDictionary<int, int>? Values, bool ByLabel, MapFallback Fallback, bool Drop = false);

/// <summary>A value taken from one column of the row a lookup of the source row refers to (<c>from: lookup.column</c>).</summary>
public sealed record MapDerived(string LookupColumn, string Column, IReadOnlyDictionary<int, int>? Values, bool ByLabel, MapFallback Fallback);

/// <summary>A source (statecode, statuscode) pair and the target pair it becomes.</summary>
public sealed record MapStatePair((int State, int Status) From, (int State, int Status) To);

/// <summary>An N:N relationship (intersect table) of the source and its name in the target.</summary>
public sealed record MapRelationship(string Source, string Target);

/// <summary>
/// One table entry: the source table, the target table, and what is renamed, derived and translated. <paramref name="Archive"/> (spec §8.7)
/// asks for the target, an <c>xrmva_</c> table, to be created as a plain copy of the source.
/// </summary>
public sealed record MapTable(
    string Source,
    string Target,
    IReadOnlyDictionary<string, MapColumn> Columns,
    IReadOnlyDictionary<string, MapDerived> Derived,
    IReadOnlyList<MapStatePair> State,
    IReadOnlyDictionary<int, (int State, int Status)> StateFallback,
    bool CarryCustomColumns,
    bool Archive = false)
{
    /// <summary>True when the target table differs from the source table (false: a column map on the same table).</summary>
    public bool IsRenamed => Source != Target;
}

/// <summary>A parsed mapping file (format version 1, migration table mapping spec §6).</summary>
public sealed record MapFile(
    string Name,
    int Version,
    IReadOnlyList<MapRequirement> Requires,
    IReadOnlyList<MapTable> Tables,
    IReadOnlyList<MapRelationship> Relationships);
