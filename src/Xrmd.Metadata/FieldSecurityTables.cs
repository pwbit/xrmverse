using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The seeded field security tables (<c>fixtures/seed/fieldsecurityprofile.json</c>, <c>fieldpermission.json</c>,
/// <c>systemuserprofiles.json</c>, <c>teamprofiles.json</c> and <c>principalobjectattributeaccess.json</c>): their names, object
/// type codes, the permission values and the System Administrator profile.
/// </summary>
/// <remarks>
/// The object type codes and the profile id are from memory, not from Learn: the Plan 14 checklist verifies them on a real
/// environment and records the observed value when it differs.
/// </remarks>
public static class FieldSecurityTables
{
    /// <summary>A field security profile: a named set of column permissions given to users and teams.</summary>
    public const string Profile = "fieldsecurityprofile";

    /// <summary>The permission of a profile on one secured column.</summary>
    public const string Permission = "fieldpermission";

    /// <summary>The intersect of <c>systemuserprofiles_association</c> (systemuser and fieldsecurityprofile).</summary>
    public const string UserProfiles = "systemuserprofiles";

    /// <summary>The intersect of <c>teamprofiles_association</c> (team and fieldsecurityprofile).</summary>
    public const string TeamProfiles = "teamprofiles";

    /// <summary>A field share: read and/or update of one secured column of one record, given to a user or a team.</summary>
    public const string Share = "principalobjectattributeaccess";

    public const int ProfileObjectTypeCode = 1200;
    public const int PermissionObjectTypeCode = 1201;
    public const int UserProfilesObjectTypeCode = 1202;
    public const int TeamProfilesObjectTypeCode = 1203;
    public const int ShareObjectTypeCode = 44;

    /// <summary>The many-to-many relationships of the profiles, kept as security relationships (not saved metadata).</summary>
    public const string UserProfilesRelationship = "systemuserprofiles_association", TeamProfilesRelationship = "teamprofiles_association";

    /// <summary>The relationship from a profile to its permissions (Delete: Cascade).</summary>
    public const string PermissionRelationship = "lk_fieldpermission_fieldsecurityprofileid";

    /// <summary>The primary id column of a profile, also the lookup of <c>fieldpermission</c> and a column of both intersects.</summary>
    public const string ProfileId = "fieldsecurityprofileid";

    /// <summary>The columns of <c>fieldpermission</c>.</summary>
    public const string EntityName = "entityname", AttributeLogicalName = "attributelogicalname",
        CanCreate = "cancreate", CanRead = "canread", CanUpdate = "canupdate", CanReadUnmasked = "canreadunmasked";

    /// <summary>The columns of <c>principalobjectattributeaccess</c>.</summary>
    public const string ShareObjectId = "objectid", ShareObjectTypeCodeColumn = "objecttypecode", ShareAttributeId = "attributeid",
        SharePrincipalId = "principalid", ShareReadAccess = "readaccess", ShareUpdateAccess = "updateaccess";

    /// <summary>The unique index of the field shares on (record, column, principal): one share per triple (F6 rule 3).</summary>
    public const string ShareUniqueIndex = "UX_principalobjectattributeaccess_object_attribute_principal";

    /// <summary>The values of <c>cancreate</c>, <c>canread</c> and <c>canupdate</c> (global choice <c>field_security_permission_type</c>).</summary>
    public const int Allowed = 4, NotAllowed = 0;

    /// <summary>The seeded System Administrator profile, which has no permission rows and cannot be changed.</summary>
    public static readonly Guid SystemAdministratorProfileId = new("572329c1-a042-4e22-be47-367c6374ea45");

    /// <summary>The name of the System Administrator profile.</summary>
    public const string SystemAdministratorProfileName = "System Administrator";

    /// <summary>The five tables.</summary>
    public static readonly IReadOnlyList<string> All = [Profile, Permission, UserProfiles, TeamProfiles, Share];

    /// <summary>
    /// True for <c>principalobjectattributeaccess.objectid</c>: it names a record of any table, so it has no fixed targets and
    /// keeps a type column, as <c>audit.objectid</c> does.
    /// </summary>
    public static bool IsTargetlessLookup(AttributeMetadata a) =>
        a is LookupAttributeMetadata && a.EntityLogicalName == Share && a.LogicalName == ShareObjectId;
}
