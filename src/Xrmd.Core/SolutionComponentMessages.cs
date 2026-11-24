using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// AddSolutionComponent and RemoveSolutionComponent on a session (routed by <see cref="MetadataOperations"/>, which demands
/// <see cref="Privilege"/>).
/// <list type="bullet">
/// <item>AddSolutionComponent (<c>ComponentId</c>, <c>ComponentType</c>, <c>SolutionUniqueName</c>,
/// <c>AddRequiredComponents</c>, <c>DoNotIncludeSubcomponents</c>; a non-empty <c>IncludedComponentSettingsValues</c> is
/// refused, 400): a webhook (<c>serviceendpoint</c>) or a step on one is refused (400, plan 3.2); the component must exist
/// (404) and be of a type xrmverse tracks (<see cref="ComponentType.IsSupported"/>; 400 otherwise); the built-in System
/// Administrator field security profile is refused (400). It is added to the solution (and to <c>Default</c>);
/// <c>DoNotIncludeSubcomponents</c> sets <c>rootcomponentbehavior</c> 1 on an entity. <c>AddRequiredComponents</c> adds
/// the attributes of an entity, is a no-op for the types that require nothing (option set, web resource, site map, plug-in
/// assembly) and is refused (400) for the others, whose dependencies xrmverse does not compute. Response <c>id</c>: the
/// <c>solutioncomponentid</c>.</item>
/// <item>RemoveSolutionComponent (<c>ComponentId</c>, <c>ComponentType</c>, <c>SolutionUniqueName</c>): removes it (with the
/// subcomponents added with it) from the solution; from <c>Default</c> 400, a component the solution does not hold 404.
/// Response <c>id</c>: the removed <c>solutioncomponentid</c>.</item>
/// </list>
/// Neither changes a managed solution (400, plan 2.6 ruling M14); a managed component may be added to an unmanaged one.
/// </summary>
public sealed class SolutionComponentMessages(IMetadataStore store, SolutionComponentTracker tracker)
{
    /// <summary>The privilege both messages require (Write on <c>solution</c>).</summary>
    public const string Privilege = "prvWriteSolution";

    /// <summary>The entity named in their PrivilegeDenied fault.</summary>
    public const string Entity = "solution";

    /// <summary>The types whose components require no other component: <c>AddRequiredComponents</c> adds nothing.</summary>
    private static readonly HashSet<int> NothingRequired =
        [ComponentType.OptionSet, ComponentType.WebResource, ComponentType.SiteMap, ComponentType.PluginAssembly];

    /// <exception cref="XrmdFaultException">400 for a missing parameter, an unsupported type or <c>AddRequiredComponents</c>
    /// that cannot be honoured, removal from <c>Default</c>; 404 for an unknown solution or component.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        var add = string.Equals(request.RequestName, "AddSolutionComponent", StringComparison.OrdinalIgnoreCase);
        var componentId = Required<Guid>(request, "ComponentId");
        var type = ComponentType.Normalize(Required<int>(request, "ComponentType"));
        var solutionName = request.Parameters.TryGetValue("SolutionUniqueName", out var n) && n is string { Length: > 0 } name
            ? name
            : throw Missing(request, "SolutionUniqueName");
        if (add && request.Parameters.TryGetValue("IncludedComponentSettingsValues", out var settings)
                && settings is System.Collections.ICollection { Count: > 0 })
            throw XrmdFaultException.InvalidArgument(
                "IncludedComponentSettingsValues is not supported: xrmverse keeps no settings with a solution component.");
        // Plan 3.2: webhooks and their steps stay out of solutions (an export never meets one).
        if (add) await Xrmd.Security.SystemTableRules.DemandNotWebhookComponentAsync(type, componentId, session, ct);
        if (!ComponentType.IsSupported(type))
            throw XrmdFaultException.InvalidArgument($"Component type {type} is not supported by xrmverse.");
        if (add && type == ComponentType.FieldSecurityProfile && componentId == FieldSecurityTables.SystemAdministratorProfileId)
            throw XrmdFaultException.InvalidArgument(
                "The System Administrator field security profile is built in: it cannot be added to a solution.");
        var solutionId = await tracker.SolutionIdAsync(session, solutionName, ct);
        if (await IsManagedAsync(session, solutionId, ct))
            throw XrmdFaultException.InvalidArgument(
                $"The solution '{solutionName}' is managed: its components change only by importing, upgrading or uninstalling it, not through {request.RequestName}.");

        if (!add)
        {
            var removed = await tracker.RemoveAsync(session, solutionId, type, componentId, ct)
                          ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist,
                              $"The component {componentId} of type {type} is not in solution '{solutionName}'.", 404);
            return new RemoveSolutionComponentResponse { ResponseName = "RemoveSolutionComponent", Results = { ["id"] = removed } };
        }

        var addRequired = Optional(request, "AddRequiredComponents");
        if (addRequired && type != ComponentType.Entity && !NothingRequired.Contains(type))
            throw XrmdFaultException.InvalidArgument(
                $"AddRequiredComponents is not supported for component type {type}: xrmverse adds the required components of entities only.");
        var attributes = await EnsureExistsAsync(session, type, componentId, ct);
        var behavior = type == ComponentType.Entity && Optional(request, "DoNotIncludeSubcomponents") ? 1 : 0;

        await tracker.AddAsync(session, SystemSeed.DefaultSolutionId, type, componentId, null, ct);
        var id = await tracker.AddAsync(session, solutionId, type, componentId, behavior, ct);
        if (addRequired)
            foreach (var attribute in attributes)
                await tracker.AddAsync(session, solutionId, ComponentType.Attribute, attribute, null, ct, rootSolutionComponentId: id);
        return new AddSolutionComponentResponse { ResponseName = "AddSolutionComponent", Results = { ["id"] = id } };
    }

    /// <summary>
    /// 404 unless the component exists: a metadata component in the published metadata (by <c>MetadataId</c>), a row in its
    /// table. Returns the attribute ids of an entity (empty for the other types).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> EnsureExistsAsync(DataSession session, int type, Guid id, CancellationToken ct)
    {
        if (type == ComponentType.Entity)
            return (store.GetAllEntities().FirstOrDefault(e => e.MetadataId == id) ?? throw NotFound(type, id))
                .Attributes?.Select(a => a.MetadataId).OfType<Guid>().ToList() ?? [];
        var exists = type switch
        {
            ComponentType.Attribute => store.GetAllEntities().Any(e => (e.Attributes ?? []).Any(a => a.MetadataId == id)),
            ComponentType.Relationship or ComponentType.EntityRelationship =>
                store.GetOneToManyRelationships().Any(r => r.MetadataId == id) || store.GetManyToManyRelationships().Any(r => r.MetadataId == id),
            ComponentType.OptionSet => store.GetAllOptionSets().Any(o => o.MetadataId == id),
            _ => await RowExistsAsync(session, ComponentType.TableOf(type)!, id, ct),
        };
        if (!exists) throw NotFound(type, id);
        if (type == ComponentType.Workflow) await DemandProcessDefinitionAsync(session, id, ct);
        return [];
    }

    /// <summary>
    /// 400 unless the process row is a definition: an activation (<c>type</c> 2) is not a solution component (controller
    /// ruling, plan 2.14 Task 3).
    /// </summary>
    private static async Task DemandProcessDefinitionAsync(DataSession session, Guid id, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT {session.Quote(WorkflowSchema.TypeColumn)} FROM {session.DataTable(WorkflowSchema.Table)} WHERE {session.Quote(WorkflowSchema.WorkflowId)} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        if (!ComponentType.IsWorkflowDefinition(await cmd.ExecuteScalarAsync(ct)))
            throw XrmdFaultException.InvalidArgument(
                $"The process row {id} is an activation, not a process definition: only a definition is a solution component.");
    }

    private static XrmdFaultException NotFound(int type, Guid id) =>
        new(ErrorCodes.ObjectDoesNotExist, $"The component {id} of type {type} does not exist.", 404);

    private async Task<bool> RowExistsAsync(DataSession session, string table, Guid id, CancellationToken ct)
    {
        var entity = store.GetEntity(table) ?? throw new InvalidOperationException($"The system table '{table}' is missing.");
        await using var cmd = session.Command(
            session.Dialect.Limit($"SELECT 1 FROM {session.DataTable(entity.LogicalName)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = @id", "1"));
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>True for a managed solution (plan 2.6, ruling M14).</summary>
    private static async Task<bool> IsManagedAsync(DataSession session, Guid solutionId, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT {session.Dialect.IsNull("ismanaged", session.Dialect.BooleanLiteral(false))} FROM {session.DataTable("solution")} WHERE solutionid = @id");
        cmd.AddParameter("@id", DbType.Guid, solutionId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static T Required<T>(OrganizationRequest request, string name) where T : struct =>
        request.Parameters.TryGetValue(name, out var value) && value is T typed ? typed : throw Missing(request, name);

    private static bool Optional(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is true;

    private static XrmdFaultException Missing(OrganizationRequest request, string name) =>
        XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or of the wrong type.");
}
