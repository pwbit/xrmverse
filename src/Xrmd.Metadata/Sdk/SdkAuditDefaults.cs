using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="AuditDefaults"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class AuditDefaults
{
    /// <summary>The SDK twin of <see cref="ForEntityFlag"/>.</summary>
    public static BooleanManagedProperty ForEntity(string entityLogicalName, bool seed) => SdkTwin.Sdk(ForEntityFlag(entityLogicalName, seed));

    /// <summary>The SDK twin of <see cref="ForAttribute(ColumnDefinition, string?)"/>.</summary>
    public static BooleanManagedProperty ForAttribute(AttributeMetadata attribute, string? primaryId = null)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return SdkTwin.Sdk(ForAttribute(SdkTwin.Model(attribute), primaryId));
    }

    /// <summary>The SDK twin of <see cref="ManagedFlag"/>.</summary>
    public static BooleanManagedProperty Managed(bool value, bool canBeChanged = true) => SdkTwin.Sdk(ManagedFlag(value, canBeChanged));
}
