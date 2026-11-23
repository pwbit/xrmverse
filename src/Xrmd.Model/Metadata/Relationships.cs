namespace Xrmd.Model.Metadata;

/// <summary>Base of the relationship definitions (Web API <c>RelationshipMetadataBase</c>); abstract like the SDK type.</summary>
[WireType("RelationshipMetadataBase")]
public abstract class RelationshipDefinition
{
    public bool? IsCustomRelationship { get; set; }
    public ManagedFlag? IsCustomizable { get; set; }
    public bool? IsValidForAdvancedFind { get; set; }
    public string? SchemaName { get; set; }
    public SecurityBehaviors? SecurityTypes { get; set; }
    public bool? IsManaged { get; set; }
    public RelationshipKind RelationshipType { get; set; }
    public string? IntroducedVersion { get; set; }
    public Guid? MetadataId { get; set; }
    public bool? HasChanged { get; set; }
}

/// <summary>A one-to-many relationship (Web API <c>OneToManyRelationshipMetadata</c>).</summary>
[WireType("OneToManyRelationshipMetadata")]
public class OneToManyRelationship : RelationshipDefinition
{
    public OneToManyRelationship() => RelationshipType = RelationshipKind.OneToManyRelationship;

    public MenuSettings? AssociatedMenuConfiguration { get; set; }
    public CascadeSettings? CascadeConfiguration { get; set; }
    public string? ReferencedAttribute { get; set; }
    public string? ReferencedEntity { get; set; }
    public string? ReferencingAttribute { get; set; }
    public string? ReferencingEntity { get; set; }
    public bool? IsHierarchical { get; set; }
    public string? EntityKey { get; set; }
    public List<RelationshipColumnPair>? RelationshipAttributes { get; set; }
    public bool? IsRelationshipAttributeDenormalized { get; set; }
    public string? ReferencedEntityNavigationPropertyName { get; set; }
    public string? ReferencingEntityNavigationPropertyName { get; set; }
    public int? RelationshipBehavior { get; set; }
    public bool? IsDenormalizedLookup { get; set; }
    public string? DenormalizedAttributeName { get; set; }
}

/// <summary>A many-to-many relationship (Web API <c>ManyToManyRelationshipMetadata</c>).</summary>
[WireType("ManyToManyRelationshipMetadata")]
public class ManyToManyRelationship : RelationshipDefinition
{
    public ManyToManyRelationship() => RelationshipType = RelationshipKind.ManyToManyRelationship;

    public MenuSettings? Entity1AssociatedMenuConfiguration { get; set; }
    public MenuSettings? Entity2AssociatedMenuConfiguration { get; set; }
    public string? Entity1LogicalName { get; set; }
    public string? Entity2LogicalName { get; set; }
    public string? IntersectEntityName { get; set; }
    public string? Entity1IntersectAttribute { get; set; }
    public string? Entity2IntersectAttribute { get; set; }
    public string? Entity1NavigationPropertyName { get; set; }
    public string? Entity2NavigationPropertyName { get; set; }
}

/// <summary>What happens to related rows on an action (Web API <c>CascadeConfiguration</c>).</summary>
[WireType("CascadeConfiguration")]
public class CascadeSettings
{
    public CascadeAction? Assign { get; set; }
    public CascadeAction? Delete { get; set; }
    public CascadeAction? Archive { get; set; }
    public CascadeAction? Merge { get; set; }
    public CascadeAction? Reparent { get; set; }
    public CascadeAction? Share { get; set; }
    public CascadeAction? Unshare { get; set; }
    public CascadeAction? RollupView { get; set; }
}

/// <summary>How a relationship shows up in the navigation menu (Web API <c>AssociatedMenuConfiguration</c>).</summary>
[WireType("AssociatedMenuConfiguration")]
public class MenuSettings
{
    public MenuBehavior? Behavior { get; set; }
    public MenuGroup? Group { get; set; }
    public LabelSet? Label { get; set; }
    public int? Order { get; set; }
    public bool IsCustomizable { get; set; }
    public string? Icon { get; set; }
    public Guid ViewId { get; set; }
    public bool AvailableOffline { get; set; }
    public string? MenuId { get; set; }
    public string? QueryApi { get; set; }
}

/// <summary>A referencing/referenced column pair of a relationship (Web API <c>RelationshipAttribute</c>).</summary>
[WireType("RelationshipAttribute")]
public class RelationshipColumnPair
{
    public string? ReferencingAttributeName { get; set; }
    public string? ReferencedAttributeName { get; set; }
}
