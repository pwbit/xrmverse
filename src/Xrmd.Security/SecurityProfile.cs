namespace Xrmd.Security;

/// <summary>
/// What a user may do: its business unit, its teams and the privileges of its roles (assigned directly or to one of its
/// owner teams, <c>teamtype</c> 0).
/// </summary>
/// <param name="UserId">The <c>systemuserid</c>.</param>
/// <param name="BusinessUnitId">The user's business unit.</param>
/// <param name="IsDisabled"><c>systemuser.isdisabled</c> (NULL counts as false).</param>
/// <param name="IsSystemAdministrator">One of the roles has the System Administrator template: every privilege at Global.</param>
/// <param name="HasRoles">The user has at least one role, direct or from an owner team.</param>
/// <param name="OwnerTeamIds">The owner teams (<c>teamtype</c> 0) the user is a member of.</param>
/// <param name="TeamIds">Every team the user is a member of.</param>
/// <param name="Privileges">Privilege name (case-insensitive) → the OR of the depths granted by every role.</param>
/// <param name="DirectPrivileges">The privileges of the roles assigned directly to the user (a direct System
/// Administrator role contributes <c>prvActOnBehalfOfAnotherUser</c>).</param>
public sealed record SecurityProfile(Guid UserId, Guid BusinessUnitId, bool IsDisabled, bool IsSystemAdministrator,
    bool HasRoles, IReadOnlySet<Guid> OwnerTeamIds, IReadOnlySet<Guid> TeamIds,
    IReadOnlyDictionary<string, DepthMask> Privileges,
    IReadOnlySet<string> DirectPrivileges)
{
    /// <summary>
    /// What the user reaches through the manager hierarchy (plan 2.5, ruling H4); null when the hierarchy model is disabled
    /// or no report counts for the user.
    /// </summary>
    public HierarchyReach? Hierarchy { get; init; }

    /// <summary>The widest depth granted for <paramref name="privilegeName"/>; Global for a System Administrator.</summary>
    public DepthMask DepthOf(string privilegeName) =>
        IsSystemAdministrator ? DepthMask.Global : DepthMasks.Highest(Privileges.GetValueOrDefault(privilegeName));
}
