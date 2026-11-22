namespace Xrmd.Schema;

/// <summary>
/// The metadata-as-code model of a schema YAML file (<see cref="SchemaYaml"/>): the global choices, the tables with their
/// primary name, columns and many-to-many relationships, all named with the publisher's prefix.
/// </summary>
public sealed class SchemaDocument
{
    /// <summary>The publisher prefix (<c>publisher:</c>): every name starts with <c>{prefix}_</c>.</summary>
    public string Publisher { get; set; } = "";

    /// <summary>The <c>SolutionUniqueName</c> given to the requests (<c>solution:</c>); null for none.</summary>
    public string? Solution { get; set; }

    /// <summary>The global choices (option sets).</summary>
    public List<SchemaChoice> Choices { get; set; } = [];

    public List<SchemaTable> Tables { get; set; } = [];
}

/// <summary>A global choice (<c>choices:</c>).</summary>
public sealed class SchemaChoice
{
    public string Name { get; set; } = "";

    public string? Label { get; set; }

    public string? Description { get; set; }

    public List<SchemaOption> Options { get; set; } = [];
}

/// <summary>An option of a choice: <c>{ value: 100000000, label: Low }</c>.</summary>
public sealed class SchemaOption
{
    public int Value { get; set; }

    public string Label { get; set; } = "";
}

/// <summary>The ownership of a table (<c>ownership: user | organization</c>).</summary>
public enum SchemaOwnership
{
    User,
    Organization,
}

/// <summary>A table (<c>tables:</c>).</summary>
public sealed class SchemaTable
{
    /// <summary>The logical name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The SchemaName (<c>schemaName:</c>); null: the logical name.</summary>
    public string? SchemaName { get; set; }

    public string Label { get; set; } = "";

    public string? PluralLabel { get; set; }

    public string? Description { get; set; }

    public SchemaOwnership Ownership { get; set; } = SchemaOwnership.User;

    /// <summary>
    /// A custom activity (<c>isActivity: true</c>, plan 2.11): its primary column is <c>subject</c> (SchemaName <c>Subject</c>,
    /// no publisher prefix), it always has notes and never activities, and it is user owned. Set at creation only.
    /// </summary>
    public bool IsActivity { get; set; }

    /// <summary>The logical name of the primary column of an activity.</summary>
    public const string ActivityPrimaryName = "subject";

    /// <summary>The SchemaName of the primary column of an activity (Dataverse requires it for a custom activity).</summary>
    public const string ActivityPrimarySchemaName = "Subject";

    /// <summary>The SchemaName of the primary column: the stated one, else <c>Subject</c> for an activity, else the logical name.</summary>
    public string PrimarySchemaName =>
        PrimaryName.SchemaName ?? (IsActivity && PrimaryName.Name == ActivityPrimaryName ? ActivityPrimarySchemaName : PrimaryName.Name);

    /// <summary>Notes can be attached to the table's records (<c>hasNotes:</c>); null: not stated (unchanged). One-way: true cannot become false.</summary>
    public bool? HasNotes { get; set; }

    /// <summary>Activities can be regarding the table's records (<c>hasActivities:</c>); null: not stated (unchanged). One-way: true cannot become false.</summary>
    public bool? HasActivities { get; set; }

    /// <summary>
    /// The table type (<c>tableType:</c>), <c>Standard</c> or <c>Elastic</c> (<see cref="Xrmd.Metadata.ElasticTables"/>). Written only
    /// when elastic; it cannot change on an existing table.
    /// </summary>
    public string TableType { get; set; } = Xrmd.Metadata.ElasticTables.TableTypeStandard;

    public SchemaPrimaryName PrimaryName { get; set; } = new();

    public List<SchemaColumn> Columns { get; set; } = [];

    public List<SchemaManyToMany> ManyToMany { get; set; } = [];
}

/// <summary>The primary name column of a table (a string column): <c>{ name: xv_name, label: Name, maxLength: 200 }</c>.</summary>
public sealed class SchemaPrimaryName
{
    public string Name { get; set; } = "";

    public string? SchemaName { get; set; }

    public string? Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Null: the server's default (100).</summary>
    public int? MaxLength { get; set; }

    /// <summary>The column's AutoNumberFormat (e.g. <c>PRJ-{SEQNUM:5}</c>); null: none, or unchanged on apply.</summary>
    public string? AutoNumberFormat { get; set; }
}

/// <summary>
/// A column: its <see cref="Type"/> is one of <see cref="SchemaColumnTypes.Names"/>, with the parameters that type takes
/// (<c>maxLength</c> for string and memo, <c>autoNumberFormat</c> for string, <c>precision</c> for decimal, <c>choice</c> or <c>options</c> for choice and
/// choices, <c>target</c> for lookup, <c>maxSizeInKB</c> for file and image, <c>canStoreFullImage</c> and <c>primary</c> for image).
/// </summary>
public sealed class SchemaColumn
{
    public string Name { get; set; } = "";

    public string? SchemaName { get; set; }

    public string Type { get; set; } = "";

    public string? Label { get; set; }

    public string? Description { get; set; }

    public int? MaxLength { get; set; }

    /// <summary>The column's AutoNumberFormat (string); null: none, or unchanged on apply.</summary>
    public string? AutoNumberFormat { get; set; }

    public int? Precision { get; set; }

    /// <summary>The global choice the column uses (choice, choices).</summary>
    public string? Choice { get; set; }

    /// <summary>The column's own options (choice, choices); null when it uses a global choice.</summary>
    public List<SchemaOption>? Options { get; set; }

    /// <summary>The table a lookup refers to.</summary>
    public string? Target { get; set; }

    /// <summary>The largest file or image the column takes, in KB (file, image); null: the server's default.</summary>
    public int? MaxSizeInKB { get; set; }

    /// <summary>Whether the column keeps the full-size image besides the thumbnail (image); null: no (the server's default).</summary>
    public bool? CanStoreFullImage { get; set; }

    /// <summary>
    /// True for the primary image of its table (image). Only true is written: the first image column of a table is primary
    /// until another one takes the flag, which never drops (Dataverse refuses IsPrimaryImage false).
    /// </summary>
    public bool? Primary { get; set; }

    /// <summary>
    /// The XAML of a calculated column's <c>FormulaDefinition</c> (<c>calculated:</c>, plan 2.12a): the column is created with
    /// <c>SourceType</c> 1 and this definition, which the server checks; null for a simple column. String, int, decimal, money, bool,
    /// datetime and choice columns only; a column cannot switch between simple and calculated.
    /// </summary>
    public string? Calculated { get; set; }

    /// <summary>
    /// The XAML of a rollup column's <c>FormulaDefinition</c> (<c>rollup:</c>, plan 2.12b, ruling R15): the column is created with
    /// <c>SourceType</c> 2 and this definition, which the server checks; null for a column that is not a rollup. Int, decimal, money and
    /// datetime columns only; a column cannot switch between simple, calculated and rollup.
    /// </summary>
    public string? Rollup { get; set; }

    /// <summary>
    /// The Power Fx text of a formula column's <c>FormulaDefinition</c> (<c>formula:</c>, plan 2.12c): the column is created with
    /// <c>SourceType</c> 3 and this formula, which the server checks and stores without a leading <c>=</c>; null for a column that is
    /// not a formula column. String, int, decimal, bool and datetime columns only; a column cannot switch between simple, calculated,
    /// rollup and formula.
    /// </summary>
    public string? Formula { get; set; }
}

/// <summary>A many-to-many relationship of a table with another: <c>{ name: xv_project_xv_tag, with: xv_tag }</c>.</summary>
public sealed class SchemaManyToMany
{
    /// <summary>The relationship's SchemaName, also the name of its intersect table.</summary>
    public string Name { get; set; } = "";

    public string With { get; set; } = "";
}
