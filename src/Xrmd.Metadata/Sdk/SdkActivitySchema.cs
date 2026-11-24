using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="ActivitySchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class ActivitySchema
{
    /// <summary>The SDK twin of <see cref="IsActivityTable(TableDefinition)"/>.</summary>
    public static bool IsActivityTable(EntityMetadata e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.IsActivity == true && e.LogicalName != Pointer;
    }

    /// <summary>The SDK twin of <see cref="PrivilegeStem(TableDefinition)"/>.</summary>
    public static string? PrivilegeStem(EntityMetadata e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return PrivilegeStem(SdkTwin.Model(e));
    }

    /// <summary>The SDK twin of <see cref="IsManagedLookup(ColumnDefinition)"/>.</summary>
    public static bool IsManagedLookup(AttributeMetadata a) => a is LookupAttributeMetadata && IsManagedLookup(a.EntityLogicalName, a.LogicalName);

    /// <summary>The SDK twin of <see cref="IsPartyList(ColumnDefinition?)"/>.</summary>
    public static bool IsPartyList(AttributeMetadata? a) => a is LookupAttributeMetadata { AttributeType: AttributeTypeCode.PartyList };

    /// <summary>The SDK twin of <see cref="PartyLists(TableDefinition)"/>.</summary>
    public static IReadOnlyList<(string Attribute, ParticipationType Type, bool Single)> PartyLists(EntityMetadata activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return PartyLists(SdkTwin.Model(activity));
    }

    /// <summary>The SDK twin of <see cref="CommonColumns"/> (new objects on each call).</summary>
    public static IReadOnlyList<AttributeMetadata> CommonAttributes() => SdkTwin.Sdk<AttributeMetadata>(CommonColumns());

    /// <summary>The SDK twin of <see cref="CustomActivityColumns"/> (new objects on each call).</summary>
    public static IReadOnlyList<AttributeMetadata> CustomActivityAttributes() => SdkTwin.Sdk<AttributeMetadata>(CustomActivityColumns());

    /// <summary>The SDK twin of <see cref="CollectionSchemaName(TableDefinition)"/>.</summary>
    public static string CollectionSchemaName(EntityMetadata activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return CollectionSchemaName(SdkTwin.Model(activity));
    }

    /// <summary>The SDK twin of <see cref="RegardingRelationship(TableDefinition, TableDefinition)"/>.</summary>
    public static OneToManyRelationshipMetadata RegardingRelationship(EntityMetadata target, EntityMetadata activity)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(activity);
        return SdkTwin.Sdk(RegardingRelationship(SdkTwin.Model(target), SdkTwin.Model(activity)));
    }

    /// <summary>The SDK twin of <see cref="NotesRelationship(TableDefinition)"/>.</summary>
    public static OneToManyRelationshipMetadata NotesRelationship(EntityMetadata target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return SdkTwin.Sdk(NotesRelationship(SdkTwin.Model(target)));
    }

    /// <summary>The SDK twin of <see cref="PartiesRelationship(TableDefinition)"/>.</summary>
    public static OneToManyRelationshipMetadata PartiesRelationship(EntityMetadata activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return SdkTwin.Sdk(PartiesRelationship(SdkTwin.Model(activity)));
    }

    /// <summary>The SDK twin of <see cref="PartyTargetRelationshipDefinition"/>.</summary>
    public static OneToManyRelationshipMetadata PartyTargetRelationship(string target) => SdkTwin.Sdk(PartyTargetRelationshipDefinition(target));

    /// <summary>The SDK twin of <see cref="RelationshipDefinition"/>.</summary>
    internal static OneToManyRelationshipMetadata Relationship(string schemaName, string referenced, string referencing, string attribute,
        string navigation, CascadeConfiguration cascade) =>
        SdkTwin.Sdk(RelationshipDefinition(schemaName, referenced, referencing, attribute, navigation, SdkTwin.ModelJson<CascadeSettings>(cascade)));

    /// <summary>The SDK twin of <see cref="CascadeSettings"/>.</summary>
    internal static CascadeConfiguration Cascade(CascadeType assign = CascadeType.NoCascade, CascadeType delete = CascadeType.NoCascade,
        CascadeType merge = CascadeType.NoCascade, CascadeType reparent = CascadeType.NoCascade, CascadeType share = CascadeType.NoCascade,
        CascadeType unshare = CascadeType.NoCascade) =>
        SdkTwin.Sdk(CascadeSettings((CascadeAction)assign, (CascadeAction)delete, (CascadeAction)merge, (CascadeAction)reparent, (CascadeAction)share,
            (CascadeAction)unshare));
}
