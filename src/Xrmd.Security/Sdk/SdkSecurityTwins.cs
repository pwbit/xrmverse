using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using Xrmd.Security.Sdk;

namespace Xrmd.Security;

// The SDK twins of the security core (S.2d, rulings R1/R2): the SDK-typed members of earlier versions, with the same names and
// signatures, delegating to the model members after ToModel(). Transitional: removed with the SDK view (S.2i).

public static partial class AccessEvaluator
{
    /// <summary>The SDK twin of <see cref="HasAccess(SecurityProfile, BusinessUnitTree, Xrmd.Model.Metadata.TableDefinition, DepthMask, RecordOwnership, Xrmd.Model.Security.AccessMask, Xrmd.Model.Security.AccessMask)"/>.</summary>
    public static bool HasAccess(SecurityProfile p, BusinessUnitTree tree, EntityMetadata entity, DepthMask depth,
        RecordOwnership owner, AccessRights right, AccessRights sharedMask)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return HasAccess(p, tree, entity.ToModel(), depth, owner, right.ToModel(), sharedMask.ToModel());
    }

    /// <summary>The SDK twin of <c>HasHierarchyAccess</c> on the model.</summary>
    public static bool HasHierarchyAccess(SecurityProfile p, EntityMetadata entity, RecordOwnership owner, AccessRights right,
        AccessRights reportShares)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return HasHierarchyAccess(p, entity.ToModel(), owner, right.ToModel(), reportShares.ToModel());
    }
}

public static partial class PrivilegeNames
{
    /// <summary>The SDK twin of <c>Stem</c> on the model.</summary>
    public static string Stem(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Stem(entity.ToModel());
    }

    /// <summary>The SDK twin of <c>For</c> on the model.</summary>
    public static string For(AccessRights right, EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return For(right.ToModel(), entity.ToModel());
    }
}

/// <summary>The SDK twin of <see cref="HierarchyReach.Covers(Xrmd.Model.Metadata.TableDefinition)"/>, an extension so call sites stay unchanged.</summary>
public static class SdkHierarchyReach
{
    public static bool Covers(this HierarchyReach reach, EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(reach);
        ArgumentNullException.ThrowIfNull(entity);
        return reach.Covers(entity.ToModel());
    }
}

public static partial class DepthMasks
{
    /// <summary>The mask of an SDK depth: Basic 0→1, Local 1→2, Deep 2→4, Global 3→8.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of the four SDK depths (the model member throws with ParamName "depth").</exception>
    public static DepthMask FromSdk(PrivilegeDepth depth) => From((Xrmd.Model.Security.PrivilegeScope)(int)depth);
}

public static partial class UserPrivileges
{
    /// <summary>The SDK twin of <c>GrantsForUserAsync</c>: the grants as <see cref="RolePrivilege"/>.</summary>
    public static async Task<IReadOnlyList<RolePrivilege>> ForUserAsync(Guid userId, DataSession session, CancellationToken ct) =>
        [.. (await GrantsForUserAsync(userId, session, ct)).Select(g => g.ToSdk())];

    /// <summary>The SDK twin of <c>CombineGrants</c>: the grants as <see cref="RolePrivilege"/>.</summary>
    public static IReadOnlyList<RolePrivilege> Combine(Guid businessUnitId, IEnumerable<Row> rows) =>
        [.. CombineGrants(businessUnitId, rows).Select(g => g.ToSdk())];
}
