using Xrmd.Model.Metadata;
using Xrmd.Model.Security;

namespace Xrmd.Security;

/// <summary>The owner columns of a record: <c>owninguser</c>, <c>owningteam</c>, <c>owningbusinessunit</c>.</summary>
public sealed record RecordOwnership(Guid? OwningUser, Guid? OwningTeam, Guid? OwningBusinessUnit);

/// <summary>Whether a privilege depth (or a share) gives a user an access right on one record. Pure: no I/O.</summary>
public static partial class AccessEvaluator
{
    /// <summary>
    /// True when <paramref name="depth"/> covers the record's owner, or when <paramref name="sharedMask"/> contains
    /// <paramref name="right"/> and <paramref name="depth"/> is not <see cref="DepthMask.None"/>. A System Administrator
    /// always has access.
    /// </summary>
    /// <remarks>
    /// User/team-owned entities: Global covers every record, Deep the records of the user's business unit subtree, Local
    /// those of the user's business unit, Basic those owned by the user or one of its teams; every depth also covers what
    /// Basic covers. Business-owned entities are judged on <c>owningbusinessunit</c>, with Basic as Local.
    /// Organization-owned entities (and <see cref="TableOwnership.None"/>): any depth covers every record.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Any other ownership type (fails closed).</exception>
    public static bool HasAccess(SecurityProfile p, BusinessUnitTree tree, TableDefinition entity, DepthMask depth,
        RecordOwnership owner, AccessMask right, AccessMask sharedMask)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(owner);

        if (p.IsSystemAdministrator) return true;
        if (depth == DepthMask.None) return false;
        if (right != 0 && (sharedMask & right) == right) return true;
        if (depth.HasFlag(DepthMask.Global)) return true;

        var ownerBu = owner.OwningBusinessUnit;
        var inSubtree = depth.HasFlag(DepthMask.Deep) && ownerBu is { } bu && tree.SubtreeOf(p.BusinessUnitId).Contains(bu);
        var inUnit = ownerBu == p.BusinessUnitId;

        switch (entity.OwnershipType)
        {
            case TableOwnership.UserOwned or TableOwnership.TeamOwned:
                var own = (owner.OwningUser is { } user && user == p.UserId) || (owner.OwningTeam is { } team && p.TeamIds.Contains(team));
                return own || inSubtree || (depth.HasFlag(DepthMask.Local) && inUnit);
            case TableOwnership.BusinessOwned:
                return inSubtree || inUnit; // depth ≠ None here: Basic and Local both cover the user's unit
            case TableOwnership.OrganizationOwned or TableOwnership.None:
                return true;
            default:
                throw new InvalidOperationException(
                    $"Entity '{entity.LogicalName}' has an unsupported ownership type {entity.OwnershipType}."); // fail closed
        }
    }

    /// <summary>The rights the manager hierarchy can give (plan 2.5, ruling H4): never Create, Delete, Assign or Share.</summary>
    public const AccessMask HierarchyRights =
        AccessMask.ReadAccess | AccessMask.WriteAccess | AccessMask.AppendAccess | AccessMask.AppendToAccess;

    /// <summary>
    /// True when the manager hierarchy (plan 2.5, ruling H4) gives <paramref name="p"/> <paramref name="right"/> on a record of
    /// <paramref name="entity"/>: the right is Read, Write, Append or AppendTo, <paramref name="p"/> holds its privilege at any
    /// depth, its <see cref="SecurityProfile.Hierarchy"/> covers the entity (ruling H3), and the record is owned by one of the
    /// reach's principals (<see cref="HierarchyReach.ReadPrincipals"/> for Read, <see cref="HierarchyReach.WritePrincipals"/>
    /// for the other rights), or <paramref name="reportShares"/> contains the right.
    /// </summary>
    /// <param name="reportShares">The rights shared on the record with the reach: <see cref="AccessMask.ReadAccess"/> from a
    /// share with any of the <see cref="HierarchyReach.ReadPrincipals"/>, the other rights only from a share with one of the
    /// <see cref="HierarchyReach.WritePrincipals"/> (a manager never gets more than the share gives).</param>
    public static bool HasHierarchyAccess(SecurityProfile p, TableDefinition entity, RecordOwnership owner, AccessMask right,
        AccessMask reportShares)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(owner);

        if (right == 0 || (right & ~HierarchyRights) != 0 || !IsSingleRight(right)) return false;
        if (p.Hierarchy is not { } reach || !reach.Covers(entity)) return false;
        if (p.DepthOf(PrivilegeNames.For(right, entity)) == DepthMask.None) return false;

        var principals = right == AccessMask.ReadAccess ? reach.ReadPrincipals : reach.WritePrincipals;
        if ((owner.OwningUser is { } user && principals.Contains(user)) || (owner.OwningTeam is { } team && principals.Contains(team)))
            return true;
        return (reportShares & right) == right;
    }

    private static bool IsSingleRight(AccessMask right) => ((int)right & ((int)right - 1)) == 0;
}
