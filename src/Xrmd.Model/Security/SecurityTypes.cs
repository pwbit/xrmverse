using Xrmd.Model.Records;

namespace Xrmd.Model.Security;

// Member names and numeric values are the SDK's public ones (AccessRights, PrivilegeDepth); the CLR names are our own.

/// <summary>Record-level access rights as a bit mask.</summary>
[Flags]
public enum AccessMask
{
    None = 0,
    ReadAccess = 1,
    WriteAccess = 2,
    AppendAccess = 4,
    AppendToAccess = 16,
    CreateAccess = 32,
    DeleteAccess = 65536,
    ShareAccess = 262144,
    AssignAccess = 524288,
}

/// <summary>How far a privilege reaches: the user's own records up to the whole organization.</summary>
public enum PrivilegeScope { Basic = 0, Local = 1, Deep = 2, Global = 3, RecordFilter = 4 }

/// <summary>A privilege granted to a role at a depth within a business unit.</summary>
public sealed record PrivilegeGrant(Guid PrivilegeId, string? PrivilegeName, Guid BusinessUnitId, PrivilegeScope Depth);

/// <summary>A principal (user or team) together with the access it holds on a record.</summary>
public sealed record PrincipalAccessValue(RecordReference Principal, AccessMask AccessMask);
