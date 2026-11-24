using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of the static rules of <see cref="MetadataStore"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public sealed partial class MetadataStore
{
    /// <summary>The SDK twin of <see cref="IsSystemEntity(TableDefinition)"/>.</summary>
    public static bool IsSystemEntity(EntityMetadata e) => e.ObjectTypeCode is < FirstCustomObjectTypeCode;

    /// <summary>The SDK twin of <see cref="IsSystemAttribute(TableDefinition, ColumnDefinition)"/>.</summary>
    public static bool IsSystemAttribute(EntityMetadata e, AttributeMetadata a) => IsSystemAttribute(SdkTwin.Model(e), SdkTwin.Model(a));

    /// <summary>The SDK twin of <see cref="IsCustomAttribute(TableDefinition, ColumnDefinition)"/>.</summary>
    public static bool IsCustomAttribute(EntityMetadata e, AttributeMetadata a)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(a);
        return IsCustomAttribute(SdkTwin.Model(e), SdkTwin.Model(a));
    }

    /// <summary>The SDK twin of <see cref="ActivitySettingRefusal(TableDefinition, bool, bool)"/> (Xrmd.Solution's upgrades).</summary>
    internal string? ActivitySettingRefusal(EntityMetadata entity, bool activities, bool notes)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ActivitySettingRefusal(SdkTwin.Model(entity), activities, notes);
    }

    /// <summary>The SDK twin of <see cref="QueueSettingRefusal(TableDefinition)"/> (Xrmd.Solution's upgrades).</summary>
    internal string? QueueSettingRefusal(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return QueueSettingRefusal(SdkTwin.Model(entity));
    }

    /// <summary>
    /// The SDK twin of <see cref="SetAuditSettingsAsync(DataSession, IReadOnlyList{ValueTuple{string, string, ManagedFlag}}, CancellationToken)"/>
    /// (Xrmd.Solution's upgrades).
    /// </summary>
    internal Task SetAuditSettingsAsync(DataSession session, IReadOnlyList<(string Entity, string? Attribute, Microsoft.Xrm.Sdk.BooleanManagedProperty Setting)> settings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SetAuditSettingsAsync(session, settings.Select(s => (s.Entity, s.Attribute, SdkTwin.Model(s.Setting)!)).ToList(), ct);
    }
}
