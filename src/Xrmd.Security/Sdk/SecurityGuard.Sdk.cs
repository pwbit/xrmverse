using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Security.Sdk;

namespace Xrmd.Security;

// S.2e3c2a Decision 3 (Task 5): the SDK twins of the SecurityGuard, for the callers still on the SDK: AuthorizeAsync for the solution
// uninstall and cascade delete and the pipeline's create-only Upsert (until S.2e3c2b), DemandAccessAsync for the workflows, the incoming
// mail and the solution uninstall and cascade delete. Each reads its arguments in with a mirror of the call and runs the model member.
// Decision 5: the pipeline crosses its request itself and runs the guard on the model request through the crossing form below.

public sealed partial class SecurityGuard
{
    /// <summary>
    /// The SDK twin of <see cref="AuthorizeAsync(Request, Caller, DataSession, CancellationToken)"/> (the solution uninstall and cascade
    /// delete, the pipeline's create-only Upsert): the request, and its cascade mark, are read in with a mirror of the call, and what the
    /// guard changed in it is written back into the caller's request (<see cref="AuthorizeAsync(Request, SdkMirror, Caller, DataSession, CancellationToken)"/>),
    /// as the guard changed the SDK request in place.
    /// </summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    /// <exception cref="InvalidOperationException">As the model member.</exception>
    public Task AuthorizeAsync(OrganizationRequest request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);
        var mirror = new SdkMirror();
        var model = mirror.In(request)!;
        if (CascadeHint.IsMarked(request)) CascadeHint.Mark(model);
        return AuthorizeAsync(model, mirror, caller, session, ct);
    }

    /// <summary>
    /// <see cref="AuthorizeAsync(Request, Caller, DataSession, CancellationToken)"/> on the model request <paramref name="request"/> that
    /// <paramref name="mirror"/> read in (the pipeline's crossing, Decision 5), which then writes what the guard changed out into its SDK
    /// twin: once after a check that changed it (a field share Create, whose Target gets <c>objectid</c>, <c>objecttypecode</c> and the
    /// access flags), and on a refusal of a request the guard may have changed before refusing it (ruling 4a: a field share refused after
    /// its Target was filled leaves the caller's Target filled, as the guard did on the SDK request). Nothing is written otherwise.
    /// </summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    /// <exception cref="InvalidOperationException">As the model member.</exception>
    public async Task AuthorizeAsync(Request request, SdkMirror mirror, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mirror);
        bool changed;
        try
        {
            changed = await AuthorizeAsync(request, caller, session, ct);
        }
        catch (Exception) when (MayChange(request))
        {
            mirror.Out(request);
            throw;
        }
        if (changed) mirror.Out(request);
    }

    /// <summary>
    /// The SDK twin of <see cref="DemandAccessAsync(Caller, RecordReference, Model.Security.AccessMask, DataSession, CancellationToken)"/>
    /// (the workflows, the incoming mail, the solution uninstall and cascade delete): the record is read in with a mirror of the call.
    /// </summary>
    /// <exception cref="XrmdFaultException">As the model member.</exception>
    public Task DemandAccessAsync(Caller caller, EntityReference record, AccessRights right, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(record);
        return DemandAccessAsync(caller, (RecordReference)new SdkMirror().In(record)!, right.ToModel(), session, ct);
    }
}
