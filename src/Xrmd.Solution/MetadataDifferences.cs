using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Solution;

/// <summary>A property of a component whose value in the solution file differs from the one in xrmverse.</summary>
/// <param name="Property">The SDK property name (e.g. <c>Precision</c>, <c>CascadeConfiguration.Delete</c>).</param>
/// <param name="File">The value in the solution file.</param>
/// <param name="Current">The value in xrmverse.</param>
public sealed record MetadataDifference(string Property, string File, string Current)
{
    /// <summary>E.g. <c>Precision (file: 2, xrmverse: 4)</c>.</summary>
    public override string ToString() => $"{Property} (file: {File}, xrmverse: {Current})";
}

/// <summary>
/// The differences between a component of a solution file and the same component in xrmverse that an import cannot apply
/// (the metadata store updates labels, the required level, a larger MaxLength and the options of local option sets, nothing
/// else). Only the properties the file gives are compared: a property the file leaves out is not a difference.
/// </summary>
public static class MetadataDifferences
{
    /// <summary>
    /// The definition properties of <paramref name="file"/> that differ from <paramref name="current"/>. With
    /// <paramref name="system"/>, the properties a system attribute keeps (MaxLength, the options of a picklist) are compared
    /// too; labels, the required level and the options of a custom attribute's local option set never are (the import
    /// applies them).
    /// </summary>
    public static List<MetadataDifference> OfAttribute(AttributeMetadata current, AttributeMetadata file, bool system)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(file);
        var differences = new List<MetadataDifference>();
        if (current.GetType() != file.GetType())
        {
            differences.Add(new("type", TypeName(file), TypeName(current)));
            return differences;
        }

        void Compare(string property, object? fileValue, object? currentValue)
        {
            if (fileValue is null) return;
            var f = Text(fileValue);
            var c = currentValue is null ? "(none)" : Text(currentValue);
            var same = IsNumber(fileValue) && currentValue is not null && IsNumber(currentValue)
                ? Convert.ToDouble(fileValue, CultureInfo.InvariantCulture) == Convert.ToDouble(currentValue, CultureInfo.InvariantCulture)
                : string.Equals(f, c, StringComparison.Ordinal);
            if (!same) differences.Add(new(property, f, c));
        }

        // A column is simple (0) or calculated (1) from its creation (plan 2.12a, ruling K4); its formula is applied, not compared.
        Compare(nameof(file.SourceType), file.SourceType ?? 0, current.SourceType ?? 0);
        switch ((current, file))
        {
            case (StringAttributeMetadata c, StringAttributeMetadata f):
                Compare(nameof(f.Format), f.Format, c.Format);
                if (system) Compare(nameof(f.MaxLength), f.MaxLength, c.MaxLength);
                break;
            case (MemoAttributeMetadata c, MemoAttributeMetadata f):
                Compare(nameof(f.Format), f.Format, c.Format);
                if (system) Compare(nameof(f.MaxLength), f.MaxLength, c.MaxLength);
                break;
            case (IntegerAttributeMetadata c, IntegerAttributeMetadata f):
                Compare(nameof(f.MinValue), f.MinValue, c.MinValue);
                Compare(nameof(f.MaxValue), f.MaxValue, c.MaxValue);
                Compare(nameof(f.Format), f.Format, c.Format);
                break;
            case (DecimalAttributeMetadata c, DecimalAttributeMetadata f):
                Compare(nameof(f.MinValue), f.MinValue, c.MinValue);
                Compare(nameof(f.MaxValue), f.MaxValue, c.MaxValue);
                Compare(nameof(f.Precision), f.Precision, c.Precision);
                break;
            case (DoubleAttributeMetadata c, DoubleAttributeMetadata f):
                Compare(nameof(f.MinValue), f.MinValue, c.MinValue);
                Compare(nameof(f.MaxValue), f.MaxValue, c.MaxValue);
                Compare(nameof(f.Precision), f.Precision, c.Precision);
                break;
            case (MoneyAttributeMetadata c, MoneyAttributeMetadata f):
                Compare(nameof(f.MinValue), f.MinValue, c.MinValue);
                Compare(nameof(f.MaxValue), f.MaxValue, c.MaxValue);
                Compare(nameof(f.Precision), f.Precision, c.Precision);
                Compare(nameof(f.PrecisionSource), f.PrecisionSource, c.PrecisionSource);
                break;
            case (DateTimeAttributeMetadata c, DateTimeAttributeMetadata f):
                Compare(nameof(f.Format), f.Format, c.Format);
                // No behavior is UserLocal (the Dataverse default; xrmverse's generated createdon/modifiedon have none).
                Compare(nameof(f.DateTimeBehavior), f.DateTimeBehavior?.Value, c.DateTimeBehavior?.Value ?? DateTimeBehavior.UserLocal.Value);
                break;
            case (BooleanAttributeMetadata c, BooleanAttributeMetadata f):
                Compare(nameof(f.DefaultValue), f.DefaultValue, c.DefaultValue);
                CompareOption("OptionSet.TrueOption", f.OptionSet?.TrueOption, c.OptionSet?.TrueOption);
                CompareOption("OptionSet.FalseOption", f.OptionSet?.FalseOption, c.OptionSet?.FalseOption);
                break;
            case (EnumAttributeMetadata c, EnumAttributeMetadata f) when c is PicklistAttributeMetadata or MultiSelectPicklistAttributeMetadata:
                if (f.OptionSet is { } fileSet)
                {
                    Compare("OptionSet.IsGlobal", fileSet.IsGlobal ?? false, c.OptionSet?.IsGlobal ?? false);
                    if (fileSet.IsGlobal == true) Compare("OptionSet.Name", fileSet.Name, c.OptionSet?.Name);
                    else if (system)
                        foreach (var option in fileSet.Options)
                            CompareOption($"OptionSet.Options[{option.Value}]", option,
                                c.OptionSet?.Options.FirstOrDefault(o => o.Value == option.Value));
                }
                Compare("DefaultFormValue", DefaultFormValue(f), DefaultFormValue(c));
                break;
            case (LookupAttributeMetadata c, LookupAttributeMetadata f):
                if (f.Targets is { Length: > 0 } targets)
                    Compare(nameof(f.Targets), string.Join(",", targets.Order(StringComparer.Ordinal)),
                        string.Join(",", (c.Targets ?? []).Order(StringComparer.Ordinal)));
                break;
        }
        return differences;

        void CompareOption(string property, OptionMetadata? fileOption, OptionMetadata? currentOption)
        {
            if (fileOption is null) return;
            if (currentOption is null)
            {
                differences.Add(new(property, Describe(fileOption), "(none)"));
                return;
            }
            if (fileOption.Value != currentOption.Value || !LabelsMatch(fileOption.Label, currentOption.Label))
                differences.Add(new(property, Describe(fileOption), Describe(currentOption)));
        }
    }

    /// <summary>
    /// The entity properties of the file that differ from the entity in xrmverse and that cannot be updated: its ownership,
    /// its primary id and primary name attributes, its entity set name, whether it is an activity table and an activity's
    /// <c>ActivityTypeMask</c> (labels are applied, not compared).
    /// </summary>
    public static List<MetadataDifference> OfEntity(EntityMetadata current, EntityMetadata file)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(file);
        var differences = new List<MetadataDifference>();
        void Compare(string property, string? fileValue, string? currentValue, StringComparison comparison)
        {
            if (fileValue is null) return;
            if (!string.Equals(fileValue, currentValue, comparison)) differences.Add(new(property, fileValue, currentValue ?? "(none)"));
        }

        Compare(nameof(file.OwnershipType), file.OwnershipType?.ToString(), current.OwnershipType?.ToString(), StringComparison.Ordinal);
        Compare(nameof(file.PrimaryIdAttribute), file.PrimaryIdAttribute, current.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase);
        Compare(nameof(file.PrimaryNameAttribute), file.PrimaryNameAttribute, current.PrimaryNameAttribute, StringComparison.OrdinalIgnoreCase);
        Compare(nameof(file.EntitySetName), file.EntitySetName, current.EntitySetName, StringComparison.Ordinal);
        // Plan 2.11 (A4): a table is an activity table or not from its creation, and an activity's ActivityTypeMask is fixed.
        Compare(nameof(file.IsActivity), file.IsActivity?.ToString(), (current.IsActivity == true).ToString(), StringComparison.Ordinal);
        if (file.IsActivity == true && current.IsActivity == true)
            Compare(nameof(file.ActivityTypeMask), file.ActivityTypeMask?.ToString(CultureInfo.InvariantCulture),
                current.ActivityTypeMask?.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return differences;
    }

    /// <summary>
    /// The definition properties of a relationship of the file that differ from the one of the same SchemaName in xrmverse:
    /// its kind, its entities and attributes, whether it is hierarchical and its cascades.
    /// </summary>
    public static List<MetadataDifference> OfRelationship(RelationshipMetadataBase current, RelationshipMetadataBase file)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(file);
        var differences = new List<MetadataDifference>();
        void Compare(string property, string? fileValue, string? currentValue)
        {
            if (fileValue is null) return;
            if (!string.Equals(fileValue, currentValue, StringComparison.OrdinalIgnoreCase))
                differences.Add(new(property, fileValue, currentValue ?? "(none)"));
        }

        switch ((current, file))
        {
            case (OneToManyRelationshipMetadata c, OneToManyRelationshipMetadata f):
                Compare(nameof(f.ReferencedEntity), f.ReferencedEntity, c.ReferencedEntity);
                Compare(nameof(f.ReferencingEntity), f.ReferencingEntity, c.ReferencingEntity);
                Compare(nameof(f.ReferencingAttribute), f.ReferencingAttribute, c.ReferencingAttribute);
                if (f.IsHierarchical is { } hierarchical)
                    Compare(nameof(f.IsHierarchical), Text(hierarchical), Text(c.IsHierarchical ?? false));
                if (f.CascadeConfiguration is { } fc)
                {
                    var cc = c.CascadeConfiguration ?? new CascadeConfiguration();
                    void Cascade(string action, CascadeType? fileValue, CascadeType? currentValue) =>
                        Compare($"CascadeConfiguration.{action}", fileValue?.ToString(), (currentValue ?? CascadeType.NoCascade).ToString());
                    Cascade(nameof(fc.Assign), fc.Assign, cc.Assign);
                    Cascade(nameof(fc.Delete), fc.Delete, cc.Delete);
                    Cascade(nameof(fc.Merge), fc.Merge, cc.Merge);
                    Cascade(nameof(fc.Reparent), fc.Reparent, cc.Reparent);
                    Cascade(nameof(fc.Share), fc.Share, cc.Share);
                    Cascade(nameof(fc.Unshare), fc.Unshare, cc.Unshare);
                    Cascade(nameof(fc.RollupView), fc.RollupView, cc.RollupView);
                }
                break;
            case (ManyToManyRelationshipMetadata c, ManyToManyRelationshipMetadata f):
                Compare(nameof(f.Entity1LogicalName), f.Entity1LogicalName, c.Entity1LogicalName);
                Compare(nameof(f.Entity2LogicalName), f.Entity2LogicalName, c.Entity2LogicalName);
                Compare(nameof(f.IntersectEntityName), f.IntersectEntityName, c.IntersectEntityName);
                break;
            default:
                differences.Add(new("RelationshipType", file.GetType().Name, current.GetType().Name));
                break;
        }
        return differences;
    }

    /// <summary>True when every label of <paramref name="file"/> is in <paramref name="current"/> with the same text.</summary>
    public static bool LabelsMatch(Label? file, Label? current) =>
        (file?.LocalizedLabels ?? []).All(f => (current?.LocalizedLabels ?? [])
            .Any(c => c.LanguageCode == f.LanguageCode && string.Equals(c.Label, f.Label, StringComparison.Ordinal)));

    private static int? DefaultFormValue(EnumAttributeMetadata attribute) => attribute switch
    {
        PicklistAttributeMetadata p => p.DefaultFormValue,
        MultiSelectPicklistAttributeMetadata m => m.DefaultFormValue,
        _ => null,
    };

    private static string Describe(OptionMetadata option) =>
        $"{option.Value?.ToString(CultureInfo.InvariantCulture)} '{string.Join("/", (option.Label?.LocalizedLabels ?? []).Select(l => l.Label))}'";

    private static string TypeName(AttributeMetadata attribute) => attribute.GetType().Name.Replace("AttributeMetadata", "", StringComparison.Ordinal);

    private static bool IsNumber(object value) => value is int or long or decimal or double or float;

    private static string Text(object value) => value switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
