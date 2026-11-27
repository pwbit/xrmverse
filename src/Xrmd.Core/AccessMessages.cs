using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
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
/// (<c>principalobjectaccess</c> rows with a right, direct or inherited), one <c>PrincipalAccess</c> per user or team.</item>
/// </list>
/// Both need the caller to read the record, as a Retrieve would (403 AccessDenied, 404 for a record that does not exist). An elastic
/// table has no shares (ruling E9): refused with 400, not answered as if the record had none.
/// <para>
/// S.2e3c1 (Task 3): on the model request and model values (<see cref="PrincipalAccessValue"/>, <see cref="AccessMask"/>). The SDK
/// twins (<c>Sdk/AccessMessages.Sdk.cs</c>) keep today's signatures for the Web API and the organization service. The security guard
/// stays on the SDK until S.2e3c2 and is reached through <see cref="CoreSdkBridges"/>.
/// </para>
/// </summary>
public sealed partial class AccessMessages(IMetadataStore store, SecurityGuard guard, IDbConnectionFactory factory)
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

    /// <summary>
    /// Runs <paramref name="request"/> for <paramref name="caller"/> in <paramref name="session"/>: a plug-in's call (plan 3.5 UI-1b
    /// final review, I3) reads in its own transaction, so it sees the rows that transaction has not committed yet. The answer holds
    /// model values: <see cref="AccessMask"/> (<c>AccessRights</c>), <see cref="PrincipalAccessValue"/>[] (<c>PrincipalAccesses</c>).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing or invalid parameter, an unknown or elastic table or a principal that
    /// is not a systemuser or a team; 403 AccessDenied or PrivilegeDenied when the caller cannot read the record; 404 for a record or
    /// principal that does not exist.</exception>
    /// <exception cref="NotSupportedException">Not an access message.</exception>
    public async Task<Response> ExecuteAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);
        var target = Parameter(request, "Target");
        var table = store.GetTable(target.LogicalName ?? "")
                    ?? throw XrmdFaultException.InvalidArgument(
                        $"The entity with a name = '{target.LogicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");
        ElasticTables.DemandNotElastic(table, "Record shares and the access rights they give");
        await CoreSdkBridges.DemandAccessAsync(guard, caller, target, AccessMask.ReadAccess, session, ct);

        if (string.Equals(request.Name, RetrievePrincipalAccess, StringComparison.OrdinalIgnoreCase))
        {
            var principal = Parameter(request, "Principal");
            var rights = await CoreSdkBridges.RetrievePrincipalAccessAsync(guard, principal, target, session, ct);
            return new Response { Name = RetrievePrincipalAccess, Results = { ["AccessRights"] = rights } };
        }
        if (string.Equals(request.Name, RetrieveSharedPrincipalsAndAccess, StringComparison.OrdinalIgnoreCase))
            return new Response
            {
                Name = RetrieveSharedPrincipalsAndAccess,
                Results = { ["PrincipalAccesses"] = await SharesAsync(table, target, session, ct) },
            };
        throw new NotSupportedException($"Request '{request.Name}' is not an access message.");
    }

    /// <summary>A required reference parameter; InvalidArgument (400) when missing or of another type, named as today's SDK type.</summary>
    private static RecordReference Parameter(Request request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is RecordReference reference
            ? reference
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.Name}' is missing or not a EntityReference.");

    /// <summary>The shares of the record, one per principal that holds a right through them.</summary>
    private static async Task<PrincipalAccessValue[]> SharesAsync(TableDefinition table, RecordReference target, DataSession session,
        CancellationToken ct)
    {
        var rows = await CoreSdkBridges.ShareRowsAsync(table, target.Id, principal: null, userAndTeams: false, session, ct);
        return [.. rows
            .Select(r => (Row: r, Table: r.PrincipalType == TeamPrincipalType ? "team" : r.PrincipalType == UserPrincipalType ? "systemuser" : null))
            .Where(x => x.Table is not null)
            .Select(x => new PrincipalAccessValue(new RecordReference { LogicalName = x.Table!, Id = x.Row.PrincipalId }, x.Row.Mask))];
    }
}
