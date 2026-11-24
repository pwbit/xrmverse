using Xrmd.Model.Metadata;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// The SQL of the autonumber sequences (autonumber plan, ruling N3): one <c>SEQUENCE</c> per column with an
/// <c>AutoNumberFormat</c>, named by the column's metadata id in the schema <c>autonumber</c> (a name that never collides
/// and stays within the identifier length whatever the table and column names). <c>NEXT VALUE FOR</c> is not undone by a
/// rollback (nor is <c>nextval</c> on PostgreSQL): a cancelled Create leaves a gap, as in Dataverse. The statements are the
/// dialect's (plan 2.7a, Task 4).
/// </summary>
public static partial class AutoNumberSequences
{
    /// <summary>The schema of the sequences.</summary>
    public const string Schema = "autonumber";

    /// <summary>The quoted name of the sequence of <paramref name="attribute"/>.</summary>
    public static string Name(ColumnDefinition attribute, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var id = attribute.MetadataId ?? throw new InvalidOperationException($"Attribute '{attribute.LogicalName}' has no MetadataId.");
        ArgumentNullException.ThrowIfNull(d);
        return $"{d.Quote(Schema)}.{d.Quote(id.ToString("D"))}";
    }

    /// <summary>Creates the schema and the sequence when missing, starting at <see cref="AutoNumberFormat.DefaultSeed"/>.</summary>
    public static string EnsureSql(ColumnDefinition attribute, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.CreateSequenceIfMissing(Schema, SequenceName(attribute), AutoNumberFormat.DefaultSeed);
    }

    /// <summary>Drops the sequence when it exists.</summary>
    public static string DropSql(ColumnDefinition attribute, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.DropSequenceIfExists(Schema, SequenceName(attribute));
    }

    /// <summary>Recreates the sequence so that its next value is <paramref name="seed"/> (SetAutoNumberSeed).</summary>
    public static string ResetSql(ColumnDefinition attribute, long seed, ISqlDialect d) =>
        $"{DropSql(attribute, d)}\n{d.CreateSequence(Schema, SequenceName(attribute), seed)}";

    /// <summary>One value of the sequence.</summary>
    public static string NextValueSql(ColumnDefinition attribute, ISqlDialect dialect) => $"SELECT {dialect.NextSequenceValue(Name(attribute, dialect))};";

    /// <summary>
    /// The seed (the start value, the last one SetAutoNumberSeed gave) and the value the next Create takes; no row when the
    /// sequence does not exist.
    /// </summary>
    public static string ReadSql(ColumnDefinition attribute, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.ReadSequence(Schema, SequenceName(attribute));
    }

    /// <summary>The unquoted name of the sequence in <see cref="Schema"/>: the column's metadata id.</summary>
    private static string SequenceName(ColumnDefinition attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var id = attribute.MetadataId ?? throw new InvalidOperationException($"Attribute '{attribute.LogicalName}' has no MetadataId.");
        return id.ToString("D");
    }
}
