using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The <c>IsAuditEnabled</c> setting of tables and columns (a <see cref="BooleanManagedProperty"/> whose managed property is
/// <see cref="ManagedPropertyName"/>): the value a component starts with, and whether the value can change.
/// </summary>
/// <remarks>
/// A table created in xrmverse starts with auditing off and can change it; the six seeded tables that carry the business and
/// security data start with it on; every other seeded table is off and fixed. A column created in xrmverse starts on and can
/// change, except the columns that identify, date or version a row (the primary key, <c>createdon</c>, <c>createdby</c>,
/// <c>modifiedon</c>, <c>modifiedby</c>, <c>owninguser</c>, <c>owningteam</c>, <c>owningbusinessunit</c>, <c>versionnumber</c>),
/// the money <c>_base</c> companions, and the file and image columns with their companions: those are off and fixed.
/// </remarks>
public static class AuditDefaults
{
    /// <summary>The managed property that tells whether the setting can change.</summary>
    public const string ManagedPropertyName = "canmodifyauditsettings";

    /// <summary>The seeded tables that start with auditing on.</summary>
    private static readonly HashSet<string> AuditedSeedTables =
        new(["account", "contact", "systemuser", "team", "role", "businessunit"], StringComparer.Ordinal);

    /// <summary>The generated columns that are off and fixed.</summary>
    private static readonly HashSet<string> FixedColumns = new(StringComparer.Ordinal)
    {
        "createdon", "createdby", "modifiedon", "modifiedby", "owninguser", "owningteam", "owningbusinessunit", TypeMapping.VersionNumber,
    };

    /// <summary>The setting of a table: <paramref name="seed"/> for a seeded one, else one created in xrmverse.</summary>
    public static BooleanManagedProperty ForEntity(string entityLogicalName, bool seed)
    {
        ArgumentNullException.ThrowIfNull(entityLogicalName);
        return Managed(seed && AuditedSeedTables.Contains(entityLogicalName), canBeChanged: !seed || AuditedSeedTables.Contains(entityLogicalName));
    }

    /// <summary>The setting of the column <paramref name="attribute"/> of a table created in xrmverse or seeded.</summary>
    /// <param name="primaryId">The logical name of the table's primary key, when known (<c>IsPrimaryId</c> also tells).</param>
    public static BooleanManagedProperty ForAttribute(AttributeMetadata attribute, string? primaryId = null)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var fixedOff = attribute.IsPrimaryId == true || attribute.LogicalName == primaryId || FixedColumns.Contains(attribute.LogicalName)
                       || attribute is MoneyAttributeMetadata { IsBaseCurrency: true } || FileColumns.IsFileOrImage(attribute)
                       || attribute.AttributeOf is not null;
        return Managed(!fixedOff, canBeChanged: !fixedOff);
    }

    /// <summary><paramref name="value"/> with the managed property of the setting.</summary>
    public static BooleanManagedProperty Managed(bool value, bool canBeChanged = true)
    {
        var property = new BooleanManagedProperty(value) { CanBeChanged = canBeChanged };
        SdkProperty.Set(property, nameof(ManagedProperty<bool>.ManagedPropertyLogicalName), ManagedPropertyName);
        return property;
    }
}
