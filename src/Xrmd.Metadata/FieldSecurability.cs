using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Which columns field security can secure (plan 14, Global Constraints, "Securable columns"). The rule is the only source
/// of <c>CanBeSecuredForCreate</c>, <c>CanBeSecuredForRead</c> and <c>CanBeSecuredForUpdate</c>: reads compute them with
/// <see cref="Apply"/>, and a value kept in a stored definition is never used.
/// </summary>
/// <remarks>
/// A column is securable when it is of one of the types String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean,
/// Picklist, MultiSelectPicklist, DateTime, File or Image, custom or system, and it is none of: the primary id or the primary
/// name; <c>statecode</c>, <c>statuscode</c>, <c>createdon</c>, <c>modifiedon</c>, <c>createdby</c>, <c>modifiedby</c>,
/// <c>ownerid</c> or <c>versionnumber</c>; a generated companion (<c>*name</c>, <c>*yominame</c>, <c>*_base</c>, <c>*_url</c>,
/// <c>*_timestamp</c>), recognised by its metadata (<c>AttributeOf</c>, stored by another column), never by its name alone;
/// a column of the field security tables, of <c>audit</c> or of
/// the security tables (<see cref="SecurityEntities"/>); a common activity column of an activity table or any column of
/// <c>activitypointer</c> (plan 2.11, ruling Y3); the note body <c>annotation.documentbody</c> (plan 2.11, A16); a formula column
/// (<c>SourceType</c> 3, plan 2.12c: Learn lists formula columns among the columns that cannot be secured). Lookup, Customer and
/// Owner columns are not of the listed types.
/// </remarks>
public static class FieldSecurability
{
    /// <summary>
    /// The tables security profiles and the business unit tree are read from, the five field security tables among them. A
    /// write to one invalidates the security cache (<c>SecurityCache.SecurityEntities</c> is this set); none of their columns
    /// can be secured.
    /// </summary>
    public static IReadOnlySet<string> SecurityEntities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "systemuser", "team", "businessunit", "role", "privilege",
        "systemuserroles", "teamroles", "teammembership", "roleprivileges", "privilegeobjecttypecodes",
        FieldSecurityTables.Profile, FieldSecurityTables.Permission, FieldSecurityTables.UserProfiles, FieldSecurityTables.TeamProfiles,
        FieldSecurityTables.Share,
    };

    /// <summary>The system columns that are never securable, whatever their type.</summary>
    private static readonly HashSet<string> ExcludedColumns = new(StringComparer.Ordinal)
    {
        "statecode", "statuscode", "createdon", "modifiedon", "createdby", "modifiedby", "ownerid", "versionnumber",
    };

    /// <summary>True when <paramref name="attribute"/> of <paramref name="entity"/> can be secured.</summary>
    public static bool IsSecurable(EntityMetadata entity, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        if (entity.LogicalName is null || SecurityEntities.Contains(entity.LogicalName) || entity.LogicalName == AuditTable.Name) return false;
        var name = attribute.LogicalName;
        if (name is null || name == entity.PrimaryIdAttribute || name == entity.PrimaryNameAttribute
            || attribute.IsPrimaryId == true || attribute.IsPrimaryName == true || ExcludedColumns.Contains(name))
            return false;
        if (!IsSecurableType(attribute)) return false;
        // Plan 2.12c (final review I2): Learn, field-level-security, "Which columns can be secured?": formula columns cannot be.
        if (attribute.SourceType == Computed.ComputedColumnRules.Formula) return false;
        if (IsCommonActivityColumn(entity, name)) return false;
        // Plan 2.11 A16: the note body is read from the file store after the query, outside the column checks of field security,
        // so securing it would not hide it: it is refused rather than half applied (known limit, checklist).
        if (entity.LogicalName == ActivitySchema.Annotation && name == ActivitySchema.DocumentBody) return false;
        return !IsCompanion(attribute);
    }

    private static readonly HashSet<string> CommonActivityColumns =
        new(ActivitySchema.CommonAttributes().Select(a => a.LogicalName), StringComparer.Ordinal);

    /// <summary>
    /// Plan 2.11, controller ruling Y3: the common activity columns (A2) of an activity table, and every column of
    /// <c>activitypointer</c>, cannot be secured. <c>activitypointer</c> holds a copy of them (A1) that the read path does not
    /// secure per activity table, so a secured value could be read there. Known limit, from memory: checklist (whether Dataverse
    /// lets field security apply to these columns and how the pointer honours it).
    /// </summary>
    private static bool IsCommonActivityColumn(EntityMetadata entity, string name) =>
        entity.LogicalName == ActivitySchema.Pointer || (entity.IsActivity == true && CommonActivityColumns.Contains(name));

    /// <summary>
    /// Sets <c>CanBeSecuredForCreate</c>, <c>CanBeSecuredForRead</c> and <c>CanBeSecuredForUpdate</c> of
    /// <paramref name="attribute"/> (a copy a read hands out) from <see cref="IsSecurable"/>.
    /// </summary>
    public static void Apply(EntityMetadata entity, AttributeMetadata attribute)
    {
        var securable = IsSecurable(entity, attribute);
        SdkProperty.Set(attribute, nameof(AttributeMetadata.CanBeSecuredForCreate), securable);
        SdkProperty.Set(attribute, nameof(AttributeMetadata.CanBeSecuredForRead), securable);
        SdkProperty.Set(attribute, nameof(AttributeMetadata.CanBeSecuredForUpdate), securable);
    }

    private static bool IsSecurableType(AttributeMetadata attribute) => attribute switch
    {
        MoneyAttributeMetadata money => money.IsBaseCurrency != true,
        StringAttributeMetadata or MemoAttributeMetadata or IntegerAttributeMetadata or BigIntAttributeMetadata
            or DecimalAttributeMetadata or DoubleAttributeMetadata or BooleanAttributeMetadata or PicklistAttributeMetadata
            or MultiSelectPicklistAttributeMetadata or DateTimeAttributeMetadata or FileAttributeMetadata or ImageAttributeMetadata => true,
        _ => false,
    };

    /// <summary>
    /// The main column a companion of <paramref name="entity"/> belongs to, or null when <paramref name="attribute"/> is not a
    /// companion (<see cref="IsCompanion"/>) or its main column is not in the table. A column named in <c>AttributeOf</c> (the
    /// file name, the image id, timestamp and url, a name column) or, for a money <c>_base</c>, the money column whose name it
    /// extends. Field security secures a companion with the rights of its main column (plan 14, final review C1).
    /// </summary>
    public static string? MainColumnOf(EntityMetadata entity, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        if (!IsCompanion(attribute) || attribute.LogicalName is not { } name) return null;
        string? main = attribute.AttributeOf;
        if (main is null && attribute is MoneyAttributeMetadata { IsBaseCurrency: true }
            && name.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal))
            main = name[..^TypeMapping.BaseColumnSuffix.Length];
        if (main is null || string.Equals(main, name, StringComparison.OrdinalIgnoreCase)) return null;
        return entity.Attributes?.FirstOrDefault(a => string.Equals(a.LogicalName, main, StringComparison.OrdinalIgnoreCase))?.LogicalName;
    }

    /// <summary>
    /// A column generated for another one, known from its metadata only: it names its main column in <c>AttributeOf</c> (the
    /// companions of a file or image column, a name column a solution file gives) or is stored by another column (a money
    /// <c>_base</c>). The name alone never decides: <c>new_codename</c> next to <c>new_code</c> is an ordinary column.
    /// </summary>
    private static bool IsCompanion(AttributeMetadata attribute) =>
        attribute.AttributeOf is not null || TypeMapping.IsStoredByOtherAttribute(attribute)
        || FileColumns.IsStoredCompanion(attribute) || FileColumns.IsUrlCompanion(attribute);
}
