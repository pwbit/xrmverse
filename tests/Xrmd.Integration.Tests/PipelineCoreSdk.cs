using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;

namespace Xrmd.Pipeline;

/// <summary>
/// Test-side shim (S.2e3c3b1, Decision 7): the extension names and signatures the integration tests called before the SDK boundary
/// (<see cref="CoreSdkBoundary"/>) took over, each one line over the boundary member. The product has no extension method on
/// <see cref="IPipelineCore"/>; the tests keep reading as they did.
/// </summary>
internal static class PipelineCoreSdk
{
    /// <summary><see cref="CoreSdkBoundary.ExecuteCoreAsync"/> without a nested pipeline.</summary>
    public static Task<OrganizationResponse> ExecuteCoreAsync(this IPipelineCore core, OrganizationRequest request, Caller caller,
        DataSession session, CancellationToken ct) =>
        CoreSdkBoundary.ExecuteCoreAsync(core, request, caller, session, null, null, ct);

    /// <summary><see cref="CoreSdkBoundary.ExecuteCoreAsync"/> with <paramref name="nested"/>.</summary>
    public static Task<OrganizationResponse> ExecuteCoreAsync(this IPipelineCore core, OrganizationRequest request, Caller caller,
        DataSession session, SdkNestedRequest? nested, CancellationToken ct) =>
        CoreSdkBoundary.ExecuteCoreAsync(core, request, caller, session, nested, null, ct);

    /// <summary><see cref="CoreSdkBoundary.ExecuteCoreAsync"/> with <paramref name="nested"/> and the server values of a Create.</summary>
    public static Task<OrganizationResponse> ExecuteCoreAsync(this IPipelineCore core, OrganizationRequest request, Caller caller,
        DataSession session, SdkNestedRequest? nested, IReadOnlyDictionary<string, object?>? serverSetValues, CancellationToken ct) =>
        CoreSdkBoundary.ExecuteCoreAsync(core, request, caller, session, nested, serverSetValues, ct);

    /// <inheritdoc cref="CoreSdkBoundary.WrittenEntities"/>
    public static IReadOnlyCollection<string> WrittenEntities(this IPipelineCore core, OrganizationRequest request) =>
        CoreSdkBoundary.WrittenEntities(core, request);

    /// <inheritdoc cref="CoreSdkBoundary.ExecuteNonPipelineAsync"/>
    public static Task<OrganizationResponse> ExecuteNonPipelineAsync(this IPipelineCore core, OrganizationRequest request,
        Caller caller, DataSession session, CancellationToken ct) =>
        CoreSdkBoundary.ExecuteNonPipelineAsync(core, request, caller, session, ct);

    /// <inheritdoc cref="CoreSdkBoundary.LockExistsAsync"/>
    public static Task<bool> LockExistsAsync(this IPipelineCore core, EntityReference target, DataSession session, CancellationToken ct) =>
        CoreSdkBoundary.LockExistsAsync(core, target, session, ct);

    /// <inheritdoc cref="CoreSdkBoundary.RetrieveImageAsync(IPipelineCore, EntityReference, string[], DataSession, CancellationToken)"/>
    public static Task<Entity?> RetrieveImageAsync(this IPipelineCore core, EntityReference target, string[]? attributes,
        DataSession session, CancellationToken ct) =>
        CoreSdkBoundary.RetrieveImageAsync(core, target, attributes, session, ct);

    /// <inheritdoc cref="CoreSdkBoundary.RetrieveImageAsync(IPipelineCore, EntityReference, string[], DataSession, SdkMirror, CancellationToken)"/>
    internal static Task<Entity?> RetrieveImageAsync(IPipelineCore core, EntityReference target, string[]? attributes,
        DataSession session, SdkMirror mirror, CancellationToken ct) =>
        CoreSdkBoundary.RetrieveImageAsync(core, target, attributes, session, mirror, ct);
}
