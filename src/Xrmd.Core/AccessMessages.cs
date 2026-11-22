using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// The two read messages of record access (plan 3.5 UI-1b task 2), answered outside the plug-in pipeline: for the front ends in a
/// read connection of their own, for a plug-in in its session (<see cref="CoreOperations.ExecuteNonPipelineAsync"/>). The share
/// messages GrantAccess, ModifyAccess and RevokeAccess are <see cref="CoreOperations"/>' and run in the pipeline.
/// The two messages:
/// <list type="bullet">
/// <item>RetrievePrincipalAccess (<c>Target</c>, <c>Principal</c>) → <c>AccessRights</c>: what <see cref="SecurityGuard.RetrievePrincipalAccessAsync"/>
/// answers for the principal (a systemuser or a team); no access rule of its own (ruling B9).</item>
/// <item>RetrieveSharedPrincipalsAndAccess (<c>Target</c>) → <c>PrincipalAccesses</c>: the shares of the record
/// (<c>principalobjectaccess</c> rows with a right, direct or inherited), one <see cref="PrincipalAccess"/> per user or team.</item>
/// </list>
/// Both need the caller to read the record, as a Retrieve would (403 AccessDenied, 404 for a record that does not exist). An elastic
/// table has no shares (ruling E9): refused with 400, not answered as if the record had none.
/// </summary>
public sealed class AccessMessages(IMetadataStore store, SecurityGuard guard, IDbConnectionFactory factory)
{
    /// <summary>The RetrievePrincipalAccess message.</summary>
    public const string RetrievePrincipalAccess = "RetrievePrincipalAccess";

    /// <summary>The RetrieveSharedPrincipalsAndAccess message.</summary>
    public const string RetrieveSharedPrincipalsAndAccess = "RetrieveSharedPrincipalsAndAccess";

    private const int UserPrincipalType = 8;
    private const int TeamPrincipalType = 9;

    /// <summary>True for one of the two messages (ignoring case).</summary>
    public static bool IsAccessMessage(string? name) =>
        string.Equals(name, RetrievePrincipalAccess, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, RetrieveSharedPrincipalsAndAccess, StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs <paramref name="request"/> for <paramref name="caller"/>.</summary>
    /// <exception cref="XrmdFaultException">400 for a missing or invalid parameter, an unknown or elastic table or a principal that
    /// is not a systemuser or a team; 403 AccessDenied or PrivilegeDenied when the caller cannot read the record; 404 for a record or
    /// principal that does not exist.</exception>
    /// <exception cref="NotSupportedException">Not an access message.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var conn = await factory.OpenAsync(ct);
        return await ExecuteAsync(request, caller, new DataSession(conn, null), ct);
    }

    /// <summary>
    /// Runs <paramref name="request"/> for <paramref name="caller"/> in <paramref name="session"/>: a plug-in's call (plan 3.5 UI-1b
    /// final review, I3) reads in its own transaction, so it sees the rows that transaction has not committed yet.
    /// </summary>
    /// <exception cref="XrmdFaultException">As <see cref="ExecuteAsync(OrganizationRequest, Caller, CancellationToken)"/>.</exception>
    /// <exception cref="NotSupportedException">Not an access message.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);
        var target = CoreOperations.Parameter<EntityReference>(request, "Target");
        var entity = store.GetEntity(target.LogicalName ?? "")
                     ?? throw XrmdFaultException.InvalidArgument(
                         $"The entity with a name = '{target.LogicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");
        ElasticTables.DemandNotElastic(entity, "Record shares and the access rights they give");
        await guard.DemandAccessAsync(caller, target, AccessRights.ReadAccess, session, ct);

        if (string.Equals(request.RequestName, RetrievePrincipalAccess, StringComparison.OrdinalIgnoreCase))
        {
            var principal = CoreOperations.Parameter<EntityReference>(request, "Principal");
            var rights = await guard.RetrievePrincipalAccessAsync(principal, target, session, ct);
            return new RetrievePrincipalAccessResponse { ResponseName = RetrievePrincipalAccess, Results = { ["AccessRights"] = rights } };
        }
        if (string.Equals(request.RequestName, RetrieveSharedPrincipalsAndAccess, StringComparison.OrdinalIgnoreCase))
            return new RetrieveSharedPrincipalsAndAccessResponse
            {
                ResponseName = RetrieveSharedPrincipalsAndAccess,
                Results = { ["PrincipalAccesses"] = await SharesAsync(entity, target, session, ct) },
            };
        throw new NotSupportedException($"Request '{request.RequestName}' is not an access message.");
    }

    /// <summary>The shares of the record, one per principal that holds a right through them.</summary>
    private static async Task<PrincipalAccess[]> SharesAsync(EntityMetadata entity, EntityReference target, DataSession session, CancellationToken ct)
    {
        var rows = await SecurityGuard.ShareRowsAsync(entity, target.Id, principal: null, userAndTeams: false, session, ct);
        return [.. rows
            .Select(r => (Row: r, Table: r.PrincipalType == TeamPrincipalType ? "team" : r.PrincipalType == UserPrincipalType ? "systemuser" : null))
            .Where(x => x.Table is not null)
            .Select(x => new PrincipalAccess { AccessMask = x.Row.Mask, Principal = new EntityReference(x.Table!, x.Row.PrincipalId) })];
    }
}
