using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Model.Security;

namespace Xrmd.Security.Sdk;

/// <summary>Conversions between the model security types and the SDK's. Names and values are identical (pinned by SecurityTypeParityTests).</summary>
public static class SdkSecurityTypes
{
    // Flags: any bit pattern is carried through unchanged.
    public static AccessMask ToModel(this AccessRights value) => (AccessMask)(int)value;
    public static AccessRights ToSdk(this AccessMask value) => (AccessRights)(int)value;

    public static PrivilegeScope ToModel(this PrivilegeDepth value)
    {
        var result = (PrivilegeScope)(int)value;
        if (!Enum.IsDefined(result)) throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined PrivilegeDepth.");
        return result;
    }

    public static PrivilegeDepth ToSdk(this PrivilegeScope value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined PrivilegeScope.");
        return (PrivilegeDepth)(int)value;
    }

    public static RolePrivilege ToSdk(this PrivilegeGrant grant) => new()
    {
        PrivilegeId = grant.PrivilegeId,
        PrivilegeName = grant.PrivilegeName,
        BusinessUnitId = grant.BusinessUnitId,
        Depth = grant.Depth.ToSdk(),
    };

    public static PrivilegeGrant ToModel(this RolePrivilege privilege) =>
        new(privilege.PrivilegeId, privilege.PrivilegeName, privilege.BusinessUnitId, privilege.Depth.ToModel());
}
