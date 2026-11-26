using System.Security.Cryptography;
using System.Text;

namespace Xrmd.Migration.Mapping;

/// <summary>
/// The names of an archive table (migration table mapping spec §8.7) and of its columns, the same wherever they are derived (the
/// record translator, the schema phase that creates the table, the map writer of the dry run): table <c>xrmva_&lt;source&gt;</c>,
/// column <c>xrmva_&lt;column&gt;</c>, a lookup kept as text <c>xrmva_&lt;column&gt;name</c>. A name that would not fit is shortened
/// deterministically: cut, then <c>_</c> and the first 8 hex digits of the SHA-256 of the whole name, so two long names that share
/// their start stay apart and a run on another machine finds the same name.
/// </summary>
public static class ArchiveNames
{
    /// <summary>The prefix of every archive table and column (the publisher <c>xrmverse archive</c>'s).</summary>
    public const string Prefix = MapReader.ArchivePrefix;

    /// <summary>
    /// The longest name kept whole, server-made suffixes included: xrmverse's logical names have at most 64 characters
    /// (<c>LogicalNameValidator</c>), and a PostgreSQL identifier at most 63 bytes; the lower of the two.
    /// </summary>
    public const int MaxLength = 63;

    /// <summary>Room for the primary key the server names after the table (<c>&lt;table&gt;id</c>).</summary>
    private const int TableSuffix = 2;

    /// <summary>Room for the companion columns the server names after a column (a money column's <c>_base</c>).</summary>
    private const int ColumnSuffix = 5;

    private const int HashLength = 8;

    /// <summary>The archive table of <paramref name="sourceTable"/>.</summary>
    public static string Table(string sourceTable) => Fit(Prefix + Source(sourceTable), TableSuffix);

    /// <summary>The archive column of <paramref name="sourceColumn"/>.</summary>
    public static string Column(string sourceColumn) => Fit(Prefix + Source(sourceColumn), ColumnSuffix);

    /// <summary>The text column that holds the referenced record's name of a lookup the archive cannot keep as a lookup.</summary>
    public static string LookupText(string sourceColumn) => Fit(Prefix + Source(sourceColumn) + "name", ColumnSuffix);

    /// <summary>The one-to-many relationship of the lookup <paramref name="sourceColumn"/> of the archive of <paramref name="sourceTable"/>.</summary>
    public static string Relationship(string sourceTable, string sourceColumn) =>
        Fit(Prefix + Source(sourceTable) + "_" + Source(sourceColumn), 0);

    /// <summary><paramref name="name"/> when it fits with <paramref name="suffix"/> characters to spare; otherwise its shortened form.</summary>
    internal static string Fit(string name, int suffix)
    {
        var max = MaxLength - suffix;
        if (name.Length <= max) return name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..HashLength];
        return name[..(max - HashLength - 1)].TrimEnd('_') + "_" + hash;
    }

    private static string Source(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return name;
    }
}
