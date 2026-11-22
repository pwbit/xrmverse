namespace Xrmd.Metadata;

/// <summary>
/// The seeded tables of the table and column maps (#143): <c>entitymap</c> (a source and a target table) and <c>attributemap</c>
/// (a source and a target column of an entity map), defined in <c>fixtures/seed</c>. Names, type codes and entity sets as an
/// observed Dataverse environment has them; the relationship name is the one Learn's "Customize table and column mappings" gives.
/// </summary>
/// <remarks>
/// The rows hold the user maps a solution imports. The lookup map that Dataverse creates with each one-to-many relationship is not
/// stored: InitializeFrom derives it from the relationship (see <c>docs/model-driven-apps.md</c>, Known limits).
/// </remarks>
public static class AttributeMapSchema
{
    /// <summary>The table of entity maps (organization-owned).</summary>
    public const string EntityMap = "entitymap";

    /// <summary>The table of attribute maps (organization-owned).</summary>
    public const string AttributeMap = "attributemap";

    /// <summary>The object type code of <see cref="EntityMap"/>.</summary>
    public const int EntityMapTypeCode = 4600;

    /// <summary>The object type code of <see cref="AttributeMap"/>.</summary>
    public const int AttributeMapTypeCode = 4601;

    /// <summary>The relationship from <c>attributemap.entitymapid</c> to <c>entitymap</c> (Delete Cascade).</summary>
    public const string Relationship = "entity_map_attribute_maps";

    /// <summary>The columns of <see cref="EntityMap"/>.</summary>
    public const string SourceEntity = "sourceentityname", TargetEntity = "targetentityname";

    /// <summary>The columns of <see cref="AttributeMap"/>.</summary>
    public const string EntityMapLookup = "entitymapid", SourceAttribute = "sourceattributename", TargetAttribute = "targetattributename",
        IsSystem = "issystem";

    /// <summary>The refusal (400) of a write of either table by a request: the solution import writes them.</summary>
    public const string ImportOnly = "Entity and attribute maps are created by solution import in this version.";

    /// <summary>True for <see cref="EntityMap"/> and <see cref="AttributeMap"/>.</summary>
    public static bool IsMapTable(string? entity) => entity is EntityMap or AttributeMap;
}
