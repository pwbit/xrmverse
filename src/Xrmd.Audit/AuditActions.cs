namespace Xrmd.Audit;

/// <summary>
/// The values of <c>audit.action</c> (global choice <c>audit_action</c>) that xrmverse writes. The values are the Dataverse ones,
/// listed on the Learn reference of the audit table; the labels live in the seed.
/// </summary>
public static class AuditActions
{
    public const int Create = 1;
    public const int Update = 2;
    public const int Delete = 3;
    public const int Assign = 13;
    public const int Share = 14;
    public const int AssociateEntities = 33;
    public const int DisassociateEntities = 34;
    public const int SetState = 41;
    public const int ModifyShare = 48;
    public const int Unshare = 49;
    public const int AssignRoleToTeam = 53;
    public const int RemoveRoleFromTeam = 54;
    public const int AssignRoleToUser = 55;
    public const int RemoveRoleFromUser = 56;
    public const int AddPrivilegesToRole = 57;
    public const int RemovePrivilegesFromRole = 58;
    public const int ReplacePrivilegesInRole = 59;
}

/// <summary>
/// The values of <c>audit.operation</c> (global choice <c>audit_operation</c>). xrmverse writes <see cref="Create"/>,
/// <see cref="Update"/> and <see cref="Delete"/> only: Assign, SetState, sharing, relationship and role privilege events are
/// updates (ruling A1).
/// </summary>
public static class AuditOperations
{
    public const int Create = 1;
    public const int Update = 2;
    public const int Delete = 3;
    public const int Access = 4;
    public const int Upsert = 5;
    public const int Archive = 115;
    public const int Retain = 116;
    public const int RollbackRetain = 117;
    public const int Restore = 118;
    public const int CustomOperation = 200;
}
