using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Audit;

/// <summary>
/// What is audited (plan 13, rulings A1 and A5): a change is recorded when the organization (<c>isauditenabled</c>), the table
/// and, for a column value, the column all have auditing on. A table or column without the setting is not audited.
/// </summary>
public static class AuditPolicy
{
    /// <summary>True when the table's <c>IsAuditEnabled</c> is on.</summary>
    public static bool IsAudited(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.IsAuditEnabled?.Value == true;
    }

    /// <summary>True when both the organization and the table have auditing on.</summary>
    public static bool IsAudited(OrganizationAuditSettings organization, EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return organization.IsAuditEnabled && IsAudited(entity);
    }

    /// <summary>
    /// The columns whose values are recorded: none when the table is not audited; otherwise those with <c>IsAuditEnabled</c> on
    /// that hold a value of their own and can be read back (never a companion such as a lookup's name, a money base value, or a
    /// file or image column, whose content is not a column value), and never a rollup column (plan 2.12b, ruling R11 candidate: its
    /// value is the server's calculation, which no caller writes; its companions and <c>_base</c> are companions already).
    /// </summary>
    public static IReadOnlyList<AttributeMetadata> AuditedAttributes(EntityMetadata entity)
    {
        if (!IsAudited(entity)) return [];
        return (entity.Attributes ?? [])
            .Where(a => a.IsAuditEnabled?.Value == true && a.AttributeOf is null && !FileColumns.IsFileOrImage(a)
                        && a is not MoneyAttributeMetadata { IsBaseCurrency: true } && !RollupColumns.IsRollup(a)
                        && TypeMapping.ToColumns(a).Count > 0)
            .ToList();
    }

    /// <summary>
    /// True when two column values are the same for auditing: references by table and id (not by name), choices and money by
    /// value, multi-select choices as sets, anything else by <see cref="object.Equals(object, object)"/>.
    /// </summary>
    public static bool SameValue(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (EntityReference x, EntityReference y) => x.Id == y.Id && string.Equals(x.LogicalName, y.LogicalName, StringComparison.Ordinal),
        (OptionSetValueCollection x, OptionSetValueCollection y) =>
            x.Select(o => o.Value).Order().SequenceEqual(y.Select(o => o.Value).Order()),
        (OptionSetValue x, OptionSetValue y) => x.Value == y.Value,
        (Money x, Money y) => x.Value == y.Value,
        _ => Equals(a, b),
    };
}
