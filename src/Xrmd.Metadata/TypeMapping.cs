using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// A physical column of a <c>data.*</c> table: its name and its engine-neutral type (plan 2.7a, Task 2), which
/// <see cref="SqlType"/> writes in the engine's SQL.
/// </summary>
/// <param name="IsRowVersion">The <c>versionnumber</c> column, which the engine sets on every write (<see cref="ISqlDialect.RowVersionType"/>).</param>
public readonly record struct SqlColumn(string Name, SqlColumnKind Kind, int Length = 0, int Precision = 0, int Scale = 0, bool IsRowVersion = false)
{
    /// <summary>The column's type in the SQL of <paramref name="dialect"/> (<see cref="ISqlDialect.ColumnType"/>).</summary>
    public string SqlType(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        return IsRowVersion ? dialect.RowVersionType : dialect.ColumnType(Kind, Length, Precision, Scale);
    }
}

/// <summary>Maps SDK attribute metadata to column types (spec §4 "Mapping tipi → T-SQL"; plan 2.7a: written by the dialect).</summary>
public static class TypeMapping
{
    public const string BaseColumnSuffix = "_base";
    public const string TypeColumnSuffix = "type";
    public const string VersionNumber = "versionnumber";

    public const int MaxNvarcharLength = 4000;

    /// <summary>MaxLength of a String attribute created without one (also its column length).</summary>
    public const int DefaultStringLength = 100;

    /// <summary>Range of <c>DecimalAttributeMetadata.Precision</c> (decimal places) accepted by the SDK.</summary>
    public const int MinDecimalPrecision = 0, MaxDecimalPrecision = 10;
    /// <summary>Precision (total digits) of decimal columns: <c>decimal(23,p)</c>, or more for a seed precision above 11 (<see cref="DecimalDigits"/>).</summary>
    public const int DecimalTotalDigits = 23;

    /// <summary>
    /// The highest <c>DecimalAttributeMetadata.Precision</c> a seed definition may have (pre-flight C14): the seed path of
    /// <see cref="MetadataStore"/> passes it where the public API keeps <see cref="MaxDecimalPrecision"/>. Only
    /// <c>transactioncurrency.exchangerate</c> uses it (Learn: Precision 12, up to 1E11).
    /// </summary>
    public const int SeedMaxDecimalPrecision = 12;

    /// <summary>
    /// The total digits of the column of a decimal with <paramref name="precision"/> decimals: 23, or <c>precision + 12</c> when that
    /// is more, so that at least twelve integer digits remain (Precision 12 gives <c>decimal(24,12)</c>).
    /// </summary>
    public static int DecimalDigits(int precision) => Math.Max(DecimalTotalDigits, precision + 12);

    /// <summary>Scale of money columns: <c>decimal(19,4)</c>.</summary>
    public const int MoneyScale = 4;

    public const int DefaultDecimalPrecision = 2;

    /// <summary>
    /// The type of the attribute's own column in the SQL of <paramref name="dialect"/> (e.g. String{MaxLength=100} →
    /// <c>nvarchar(100)</c> on SQL Server).
    /// </summary>
    /// <exception cref="NotSupportedException">The attribute type has no column mapping (...).</exception>
    public static string ToSql(AttributeMetadata a, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        return Column(a).SqlType(dialect);
    }

    /// <summary>The attribute's own column, named after it, with its engine-neutral type.</summary>
    /// <exception cref="NotSupportedException">The attribute type has no column mapping (...).</exception>
    private static SqlColumn Column(AttributeMetadata a) => a switch
    {
        // Above MaxNvarcharLength the dialect writes the unbounded type (nvarchar(max)).
        StringAttributeMetadata s => new(a.LogicalName, SqlColumnKind.String, s.MaxLength ?? DefaultStringLength),
        MemoAttributeMetadata => new(a.LogicalName, SqlColumnKind.Text),
        IntegerAttributeMetadata => new(a.LogicalName, SqlColumnKind.Int32),
        BigIntAttributeMetadata => new(a.LogicalName, SqlColumnKind.Int64),
        DecimalAttributeMetadata d => new(a.LogicalName, SqlColumnKind.Decimal,
            Precision: DecimalDigits(d.Precision ?? DefaultDecimalPrecision), Scale: d.Precision ?? DefaultDecimalPrecision),
        DoubleAttributeMetadata => new(a.LogicalName, SqlColumnKind.Double),
        MoneyAttributeMetadata => new(a.LogicalName, SqlColumnKind.Decimal, Precision: 19, Scale: MoneyScale),
        BooleanAttributeMetadata => new(a.LogicalName, SqlColumnKind.Boolean),
        MultiSelectPicklistAttributeMetadata => new(a.LogicalName, SqlColumnKind.Text), // CSV of option values
        PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata => new(a.LogicalName, SqlColumnKind.Int32),
        // An EntityName column without AttributeOf (activitytypecode) holds the table's object type code (plan 2.11 A7).
        EntityNameAttributeMetadata => new(a.LogicalName, SqlColumnKind.Int32),
        DateTimeAttributeMetadata => new(a.LogicalName, SqlColumnKind.DateTime), // always UTC
        LookupAttributeMetadata or UniqueIdentifierAttributeMetadata => new(a.LogicalName, SqlColumnKind.Guid),
        FileAttributeMetadata => new(a.LogicalName, SqlColumnKind.Guid), // the fileattachmentid of the file (ruling F1)
        ImageAttributeMetadata => new(a.LogicalName, SqlColumnKind.Binary), // the thumbnail, stored in the row (ruling F2)
        _ => throw new NotSupportedException(
            $"Attribute '{a.LogicalName}' of type {a.GetType().Name} ({a.AttributeType}) has no SQL mapping."),
    };

    /// <summary>
    /// Owner/Customer lookups (ruling R1) and lookups with more than one target (plan 5 ruling on polymorphic lookups) also
    /// store the target's object type code in <c>&lt;name&gt;type</c>.
    /// </summary>
    public static bool IsPolymorphicLookup(AttributeMetadata a) =>
        a is LookupAttributeMetadata lookup
        // A party list has no column at all (plan 2.11 A8).
        && lookup.AttributeType != AttributeTypeCode.PartyList
        && (lookup.AttributeType is AttributeTypeCode.Owner or AttributeTypeCode.Customer || lookup.Targets is { Length: > 1 }
            // fileattachment.objectid starts without targets and each table with a file or image column adds itself (FileColumns):
            // it keeps its type column whatever the number of targets.
            || FileColumns.IsAttachmentObjectId(lookup)
            // audit.objectid and audit.regardingobjectid name a record of any table, so they have no targets either.
            || AuditTable.IsTargetlessLookup(lookup)
            // principalobjectattributeaccess.objectid, the record of a field share, likewise.
            || FieldSecurityTables.IsTargetlessLookup(lookup)
            // team.regardingobjectid starts without targets; each table with AutoCreateAccessTeams adds itself (AccessTeamSchema).
            || AccessTeamSchema.IsRegardingLookup(lookup)
            // The lookups of the activity model whose targets the metadata store maintains (plan 2.11, pre-flight C1).
            || ActivitySchema.IsManagedLookup(lookup)
            // duplicaterecord.baserecordid and duplicaterecordid likewise, with the tables that have IsDuplicateDetectionEnabled.
            || DuplicateDetectionSchema.IsRecordLookup(lookup));

    /// <summary>
    /// True for attributes whose value lives in a column owned by another attribute
    /// (money <c>_base</c> attributes, attributes with <c>AttributeOf</c>); they produce no column of their own. The companions of
    /// file and image columns that hold a value (<see cref="FileColumns.IsStoredCompanion"/>) and the <c>_date</c> and <c>_state</c>
    /// companions of rollup columns (<see cref="Computed.RollupColumns.IsCompanion"/>, plan 2.12b) have their own column.
    /// </summary>
    public static bool IsStoredByOtherAttribute(AttributeMetadata a) =>
        a is MoneyAttributeMetadata { IsBaseCurrency: true }
        || (a.AttributeOf is not null && !FileColumns.IsStoredCompanion(a) && !Computed.RollupColumns.IsCompanion(a));

    /// <summary>
    /// Attributes that can be read back: those with their own column, plus money <c>_base</c> attributes, the computed url of
    /// an image column (<see cref="FileColumns.IsUrlCompanion"/>), which a query selects but cannot filter or sort on, and the
    /// calculated and formula columns (plans 2.12a and 2.12c), which the query computes.
    /// </summary>
    public static bool IsReadable(AttributeMetadata a) =>
        a is MoneyAttributeMetadata { IsBaseCurrency: true } || FileColumns.IsUrlCompanion(a) || IsEntityNameCompanion(a)
        || Computed.ComputedColumnRules.IsComputed(a) || ToColumns(a).Count > 0;

    /// <summary>
    /// An EntityName column with <c>AttributeOf</c> (<c>regardingobjecttypecode</c>, <c>objecttypecode</c>,
    /// <c>partyobjecttypecode</c>; plan 2.11 A7): it has no column, it reads and filters the type column of its lookup.
    /// </summary>
    public static bool IsEntityNameCompanion(AttributeMetadata a) => a is EntityNameAttributeMetadata { AttributeOf: { Length: > 0 } };

    /// <summary>
    /// The column an EntityName attribute reads (an object type code): <c>{AttributeOf}type</c> for a companion of a polymorphic
    /// lookup, its own column otherwise.
    /// </summary>
    public static string EntityNameColumn(EntityNameAttributeMetadata a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return IsEntityNameCompanion(a) ? a.AttributeOf + TypeColumnSuffix : a.LogicalName;
    }

    /// <summary>
    /// All physical columns for an attribute: Money adds <c>&lt;name&gt;_base</c>, polymorphic lookups add
    /// <c>&lt;name&gt;type int</c>, <c>versionnumber</c> is the row version (<c>rowversion</c> on SQL Server; on PostgreSQL a <c>bigint</c>
    /// that the table's trigger draws from the organization's sequence, plan 2.7a, ruling P4). The write-only <c>serviceendpoint.authvalue</c>
    /// has none (<see cref="ServiceEndpointSchema.IsAuthValue"/>).
    /// </summary>
    public static IReadOnlyList<SqlColumn> ToColumns(AttributeMetadata a)
    {
        if (IsStoredByOtherAttribute(a) || ServiceEndpointSchema.IsAuthValue(a)) return [];
        // Plans 2.12a and 2.12c: a calculated or formula column is evaluated by the query, it has no column.
        if (Computed.ComputedColumnRules.IsComputed(a)) return [];
        // Plan 2.11: a party list's rows live in activityparty (A8) and a note body in the file store (A16).
        if (ActivitySchema.IsPartyList(a) || ActivitySchema.IsDocumentBody(a)) return [];
        if (a is BigIntAttributeMetadata && a.LogicalName == VersionNumber) return [new(a.LogicalName, SqlColumnKind.Int64, IsRowVersion: true)];

        var column = Column(a);
        if (a is MoneyAttributeMetadata) return [column, column with { Name = a.LogicalName + BaseColumnSuffix }];
        if (IsPolymorphicLookup(a)) return [column, new(a.LogicalName + TypeColumnSuffix, SqlColumnKind.Int32)];
        return [column];
    }
}
