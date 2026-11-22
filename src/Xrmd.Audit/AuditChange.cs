using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Audit;

/// <summary>What an audit row records about its event, stored as <c>audit.changedata</c> by <see cref="ChangeData"/>.</summary>
public abstract record AuditChange;

/// <summary>
/// The column values of a Create, Update, Assign, SetState or Delete, by logical name: <paramref name="Old"/> before the event
/// and <paramref name="New"/> after it (a create has no old values, a delete no new ones). Values are SDK values
/// (<see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="bool"/>, <see cref="decimal"/>, <see cref="double"/>,
/// <see cref="DateTime"/>, <see cref="Guid"/>, <see cref="OptionSetValue"/>, <see cref="OptionSetValueCollection"/>,
/// <see cref="Money"/>, <see cref="EntityReference"/>) or null for a cleared value.
/// </summary>
public sealed record AttributeChange(IReadOnlyDictionary<string, object?> Old, IReadOnlyDictionary<string, object?> New) : AuditChange;

/// <summary>An Associate or Disassociate: the relationship's schema name and the records on the other side.</summary>
public sealed record RelationshipChange(string Relationship, IReadOnlyList<EntityReference> Targets) : AuditChange;

/// <summary>A GrantAccess, ModifyAccess or RevokeAccess: the principal and its access rights before and after.</summary>
public sealed record ShareChange(EntityReference Principal, AccessRights Old, AccessRights New) : AuditChange;

/// <summary>An AddPrivilegesRole, RemovePrivilegeRole or ReplacePrivilegesRole: the role's privileges before and after.</summary>
public sealed record RolePrivilegesChange(IReadOnlyList<RolePrivilege> Old, IReadOnlyList<RolePrivilege> New) : AuditChange;
