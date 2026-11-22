using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Access team templates (plan 2.16): the seeded <c>teamtemplate</c> table (<c>fixtures/seed/teamtemplate.json</c>), the
/// columns it brings to <c>team</c> (<c>fixtures/seed/team.json</c>) and the limit on the tables with
/// <c>AutoCreateAccessTeams</c>.
/// </summary>
/// <remarks>
/// The object type code and the cascade of <see cref="TeamsRelationship"/> are from memory, not from Learn: the plan 2.16
/// checklist verifies them on a real environment and records the observed value when it differs.
/// </remarks>
public static class AccessTeamSchema
{
    /// <summary>The logical name of the template table.</summary>
    public const string TeamTemplate = "teamtemplate";

    /// <summary>The object type code of <see cref="TeamTemplate"/>.</summary>
    public const int TeamTemplateObjectTypeCode = 92;

    /// <summary>The columns of <see cref="TeamTemplate"/>.</summary>
    public const string TeamTemplateId = "teamtemplateid", TeamTemplateName = "teamtemplatename", ObjectTypeCode = "objecttypecode",
        DefaultAccessRightsMask = "defaultaccessrightsmask", Description = "description", IsSystem = "issystem";

    /// <summary>The team table and the columns this plan adds to it (<see cref="TeamTemplateId"/> is the third).</summary>
    public const string Team = "team", RegardingObjectId = "regardingobjectid", SystemManaged = "systemmanaged";

    /// <summary>The relationship from a template to its teams (Delete: Cascade, everything else NoCascade).</summary>
    public const string TeamsRelationship = "teamtemplate_Teams";

    /// <summary>
    /// The most tables that can have <c>AutoCreateAccessTeams</c> (the default of the Dataverse organization setting
    /// <c>MaxEntitiesEnabledForAutoCreatedAccessTeams</c>, which xrmverse does not model: a fixed constant). The security
    /// rules (<c>Xrmd.Security</c>) use the same value.
    /// </summary>
    public const int MaxTables = 100;

    /// <summary>
    /// The most templates a table can have (the default of the Dataverse organization setting
    /// <c>MaxAutoCreatedAccessTeamsPerEntity</c>, not modelled: a fixed constant). The security rules (<c>Xrmd.Security</c>)
    /// count it when a template is created.
    /// </summary>
    public const int MaxTemplatesPerTable = 4;

    /// <summary>
    /// True for <c>team.regardingobjectid</c>: the record an auto-created access team belongs to. Its targets are maintained by
    /// the metadata store (each table with <c>AutoCreateAccessTeams</c> adds itself), so it starts without any and keeps a type
    /// column whatever their number, as <c>fileattachment.objectid</c> does.
    /// </summary>
    public static bool IsRegardingLookup(AttributeMetadata a) =>
        a is LookupAttributeMetadata && a.EntityLogicalName == Team && a.LogicalName == RegardingObjectId;

    /// <summary>The ownership a table needs for <c>AutoCreateAccessTeams</c>: owned by a user or a team.</summary>
    public static bool AllowsAccessTeams(OwnershipTypes? ownership) =>
        ownership is OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned;
}
