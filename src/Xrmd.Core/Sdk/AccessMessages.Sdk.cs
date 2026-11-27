using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.Core;

/// <summary>
/// S.2e3c1 (Task 3, Decision 5): the SDK twins of <see cref="AccessMessages"/>, with today's names and signatures, for the callers still
/// on the SDK (the Web API's access actions and the organization service). Each reads the request in through a mirror of the call, runs
/// the model member and answers with today's typed response (<see cref="CoreResponses.Typed"/>), filled from the model one.
/// Transitional: removed with their callers' moves (S.2e3c3, S.2f).
/// </summary>
public sealed partial class AccessMessages
{
    /// <summary>Runs <paramref name="request"/> for <paramref name="caller"/>, in a read connection of its own.</summary>
    /// <exception cref="XrmdFaultException">As <see cref="ExecuteAsync(Xrmd.Model.Messages.Request, Caller, DataSession, CancellationToken)"/>.</exception>
    /// <exception cref="NotSupportedException">Not an access message.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var conn = await factory.OpenAsync(ct);
        return await ExecuteAsync(request, caller, new DataSession(conn, null), ct);
    }

    /// <summary>
    /// The SDK twin of <see cref="ExecuteAsync(Xrmd.Model.Messages.Request, Caller, DataSession, CancellationToken)"/>: the answer is
    /// today's <c>RetrievePrincipalAccessResponse</c> or <c>RetrieveSharedPrincipalsAndAccessResponse</c>.
    /// </summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    /// <exception cref="NotSupportedException">Not an access message.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);
        var mirror = new SdkMirror();
        var response = await ExecuteAsync(mirror.In(request)!, caller, session, ct);
        mirror.Pair(response, CoreResponses.Typed(response.Name!));
        return mirror.Out(response)!;
    }
}
