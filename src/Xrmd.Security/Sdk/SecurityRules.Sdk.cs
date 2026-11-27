using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
using Xrmd.Security.Sdk;

namespace Xrmd.Security;

// S.2e3c2a Decision 3: the SDK twins of the Security rules, for the callers still on the SDK (the solution import and delete, the workflows
// until S.2e3c2b, the migration's SecurityPhase, and tests). Each converts its arguments in with a mirror of the call and
// runs the model member. Transitional: a twin goes when nothing outside Security calls it with SDK types (Task 5 removed those only the
// SDK SecurityGuard called). Decision 6: a record team mark a direct SDK caller of the core leaves on its SDK request is carried onto the
// model request at the crossing (AccessTeamRules.CopyMark).

public static partial class SystemTableRules
{
    /// <summary>
    /// The SDK twin of <see cref="EnforceAsync(Request, Caller, Guid, SecurityCache, IMetadataStore, DataSession, CancellationToken)"/>
    /// (the solution import and delete, tests). The request is read in as a copy; the rules
    /// change nothing in it. A message these rules do not know (the model member's own first check) is passed without being read in.
    /// </summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    public static Task EnforceAsync(OrganizationRequest request, Caller caller, Guid systemUserId, SecurityCache cache,
        IMetadataStore store, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        if (!Messages.ContainsKey(request.RequestName?.ToLowerInvariant() ?? "")) return Task.CompletedTask;
        return EnforceAsync(new SdkMirror().In(request)!, caller, systemUserId, cache, store, session, ct);
    }
}

public static partial class CurrencyRules
{
    /// <summary>The SDK twin of <see cref="DemandRateInRange(Record)"/> (tests).</summary>
    /// <exception cref="Core.Errors.XrmdFaultException">InvalidExchangeRate (400).</exception>
    public static void DemandRateInRange(Entity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        DemandRateInRange((Record)new SdkMirror().In(target)!);
    }
}

public static partial class AccessTeamRules
{
    /// <summary>The SDK twin of the record team target's constructor (tests): the table's model, the mask as the model mask.</summary>
    public sealed partial record RecordTeamTarget
    {
        public RecordTeamTarget(EntityMetadata table, Guid recordId, Guid userId, Guid templateId, AccessRights mask, Guid? owningBusinessUnit)
            : this(table.ToModel(), recordId, userId, templateId, mask.ToModel(), owningBusinessUnit)
        {
        }
    }

    /// <summary>The record team marks a direct SDK caller of the core leaves on its SDK request (tests), until it crosses (<see cref="CopyMark"/>).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OrganizationRequest, RecordTeamTarget> SdkChecked = new();

    /// <summary>
    /// The SDK twin of <see cref="MarkChecked(Request, RecordTeamTarget)"/> (tests): the mark stays on <paramref name="request"/> until
    /// its crossing into the model copies it onto the model request (<see cref="CopyMark"/>).
    /// </summary>
    public static void MarkChecked(OrganizationRequest request, RecordTeamTarget target)
    {
        ArgumentNullException.ThrowIfNull(request);
        SdkChecked.AddOrUpdate(request, target);
    }

    /// <summary>
    /// Decision 6: the record team mark of the SDK request <paramref name="from"/> (<see cref="MarkChecked(OrganizationRequest, RecordTeamTarget)"/>),
    /// copied onto its model request <paramref name="to"/> when the request crosses into the model outside the pipeline
    /// (<c>PipelineCoreSdk</c>), as the pipeline copies the audit action. Nothing when it has none.
    /// </summary>
    public static void CopyMark(OrganizationRequest from, Request to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (SdkChecked.TryGetValue(from, out var target)) MarkChecked(to, target);
    }
}

public sealed partial class SecurityAdministration
{
    /// <summary>
    /// The privileges of SDK <see cref="RolePrivilege"/>s (<see cref="FromGrants"/> of their model values; the depth is cast by value, so
    /// one that is not an SDK depth stays undefined and gives <see cref="DepthMask.None"/>). For tests.
    /// </summary>
    public static IReadOnlyList<(Guid PrivilegeId, DepthMask Depth)> FromSdk(IEnumerable<RolePrivilege?> privileges)
    {
        ArgumentNullException.ThrowIfNull(privileges);
        return FromGrants(privileges.Select(p => p is null
            ? null
            : new PrivilegeGrant(p.PrivilegeId, p.PrivilegeName, p.BusinessUnitId, (PrivilegeScope)(int)p.Depth)));
    }

    /// <summary>The SDK twin of <see cref="AddRoleAsync(DataSession, RecordReference, Guid, CancellationToken)"/> (the migration, tests).</summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    public Task AddRoleAsync(DataSession s, EntityReference principal, Guid roleId, CancellationToken ct) =>
        AddRoleAsync(s, Principal(principal), roleId, ct);

    /// <summary>The SDK twin of <see cref="RemoveRoleAsync(DataSession, RecordReference, Guid, CancellationToken)"/> (tests).</summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    public Task<bool> RemoveRoleAsync(DataSession s, EntityReference principal, Guid roleId, CancellationToken ct) =>
        RemoveRoleAsync(s, Principal(principal), roleId, ct);

    /// <summary>The SDK twin of <see cref="AddProfileAsync(DataSession, RecordReference, Guid, CancellationToken)"/> (the migration).</summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    public Task AddProfileAsync(DataSession s, EntityReference principal, Guid profileId, CancellationToken ct) =>
        AddProfileAsync(s, Principal(principal), profileId, ct);

    /// <summary>
    /// The SDK twin of <see cref="RoleGrantsAsync"/> (tests, ruling P6): the grants as the SDK <see cref="RolePrivilege"/>s it answered
    /// before (depth, privilege id, the role's business unit).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the role does not exist.</exception>
    public async Task<IReadOnlyList<RolePrivilege>> RolePrivilegesAsync(DataSession s, Guid roleId, CancellationToken ct) =>
        [.. (await RoleGrantsAsync(s, roleId, ct)).Select(g => new RolePrivilege((int)g.Depth, g.PrivilegeId, g.BusinessUnitId))];

    /// <summary>The model of an SDK principal, read in (null stays null, for the model member's own argument check).</summary>
    private static RecordReference Principal(EntityReference principal) => (RecordReference)new SdkMirror().In(principal)!;
}
