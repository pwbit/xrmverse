using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Pipeline.Bulk;

/// <summary>
/// The targets of the bulk messages (<see cref="SdkMessageCatalog.BulkMessages"/>), shared by the elastic path (plan 3.4, ruling
/// E5: <c>ElasticBulkMessages</c>) and the standard-table pipeline (#76, rulings B1–B4 and B14): reading <c>Targets</c>, the one
/// table they belong to, the record id each names, the repeated-id rules and the response built from the single-record responses.
/// It lives in the pipeline project so the pipeline's bulk stage and the organization service can both use it.
/// </summary>
public static class BulkTargets
{
    /// <summary>The bulk message names.</summary>
    public const string CreateMultiple = "CreateMultiple";

    /// <inheritdoc cref="CreateMultiple"/>
    public const string UpdateMultiple = "UpdateMultiple";

    /// <inheritdoc cref="CreateMultiple"/>
    public const string UpsertMultiple = "UpsertMultiple";

    /// <inheritdoc cref="CreateMultiple"/>
    public const string DeleteMultiple = "DeleteMultiple";

    /// <summary>The parameter that holds the targets.</summary>
    public const string TargetsParameter = "Targets";

    /// <summary>The canonical name of the bulk message <paramref name="name"/> (ignoring case); null for any other message.</summary>
    public static string? MessageOf(string? name) =>
        SdkMessageCatalog.BulkMessages.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The targets of a bulk request: each target (<see cref="Entity"/>, or <see cref="EntityReference"/> for DeleteMultiple)
    /// with its table name, the collection's <c>EntityName</c>, and the single-record request of a target.
    /// </summary>
    public sealed record TargetList(string Message, string? CollectionName, string?[] Names, object?[] Items, Func<int, OrganizationRequest> Single)
    {
        /// <summary>The number of targets.</summary>
        public int Count => Names.Length;
    }

    /// <summary>Reads the targets of <paramref name="request"/>, a bulk message.</summary>
    /// <exception cref="XrmdFaultException">400 when <c>Targets</c> is missing, of the wrong type or empty.</exception>
    public static TargetList Read(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = MessageOf(request.RequestName)
                      ?? throw new ArgumentException($"'{request.RequestName}' is not a bulk message.", nameof(request));
        request.Parameters.TryGetValue(TargetsParameter, out var value);
        if (message == DeleteMultiple)
        {
            var references = value as EntityReferenceCollection
                             ?? throw XrmdFaultException.InvalidArgument($"Required parameter 'Targets' of request '{message}' is missing.");
            DemandAny(message, references.Count);
            var list = references.ToList();
            return new TargetList(message, null, [.. list.Select(r => r?.LogicalName)], [.. list],
                i => new DeleteRequest { Target = list[i] });
        }
        var entities = value as EntityCollection
                       ?? throw XrmdFaultException.InvalidArgument($"Required parameter 'Targets' of request '{message}' is missing.");
        DemandAny(message, entities.Entities.Count);
        var rows = entities.Entities.ToList();
        return new TargetList(message, entities.EntityName, [.. rows.Select(e => e?.LogicalName)], [.. rows],
            i => message switch
            {
                CreateMultiple => new CreateRequest { Target = rows[i] },
                UpdateMultiple => new UpdateRequest { Target = rows[i] },
                _ => new UpsertRequest { Target = rows[i] },
            });
    }

    /// <summary>The one table every target belongs to: the collection's <c>EntityName</c>, else the first target's.</summary>
    /// <exception cref="XrmdFaultException">400 for a target of another table or an unknown table.</exception>
    public static EntityMetadata TableOf(TargetList targets, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(store);
        var name = string.IsNullOrEmpty(targets.CollectionName) ? targets.Names[0] : targets.CollectionName;
        DemandOneTable(targets, name);
        return (name is null ? null : store.GetEntity(name))
               // Exact Dataverse text, as CoreOperations.EntityOf: client code matches on it to detect an unknown table.
               ?? throw XrmdFaultException.InvalidArgument(
                   $"The entity with a name = '{name}' with namemapping = 'Logical' was not found in the MetadataCache.");
    }

    /// <summary>Every target must be a record of <paramref name="table"/>.</summary>
    /// <exception cref="XrmdFaultException">400 naming the first target of another table.</exception>
    public static void DemandOneTable(TargetList targets, string? table)
    {
        ArgumentNullException.ThrowIfNull(targets);
        for (var i = 0; i < targets.Count; i++)
            if (!string.Equals(targets.Names[i], table, StringComparison.Ordinal))
                throw XrmdFaultException.InvalidArgument(
                    $"Target {i} of request '{targets.Message}' is a '{targets.Names[i] ?? "(none)"}' record: every target must be a record of '{table}'.");
    }

    /// <summary>The table a bulk request's <c>Targets</c> name (the collection's <c>EntityName</c>, else the first target's);
    /// null when it names none. No check: <see cref="TableOf"/> validates.</summary>
    public static string? TableNameOf(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Parameters.TryGetValue(TargetsParameter, out var value) && value is EntityCollection targets
            ? string.IsNullOrEmpty(targets.EntityName) ? targets.Entities.FirstOrDefault()?.LogicalName : targets.EntityName
            : null;
    }

    /// <summary>
    /// The id a target names: <see cref="Entity.Id"/> (<see cref="EntityReference.Id"/>), else the primary id in its key
    /// attributes, else (an entity) its primary id attribute; empty when none.
    /// </summary>
    public static Guid IdOf(EntityMetadata table, object? target)
    {
        ArgumentNullException.ThrowIfNull(table);
        var pk = SystemAttributes.PrimaryIdName(table);
        var (id, keys, attributes) = target switch
        {
            Entity e => (e.Id, e.KeyAttributes, (AttributeCollection?)e.Attributes),
            EntityReference r => (r.Id, r.KeyAttributes, null),
            _ => (Guid.Empty, null, null),
        };
        if (id != Guid.Empty) return id;
        if (keys is not null && keys.TryGetValue(pk, out var key))
            return key switch { Guid g => g, string text when Guid.TryParse(text, out var parsed) => parsed, _ => Guid.Empty };
        return attributes is not null && attributes.TryGetValue(pk, out var value) && value is Guid attribute ? attribute : Guid.Empty;
    }

    /// <summary>
    /// The record id of each target of a bulk execution, in target order, for its image collections (ruling B6): from the
    /// <c>Ids</c> of <paramref name="response"/> when given (a CreateMultiple after stage 30), else from <c>Targets</c>
    /// (<see cref="IdOf"/>; empty for a target that names none).
    /// </summary>
    public static Guid[] TargetIds(OrganizationRequest bulk, EntityMetadata table, OrganizationResponse? response)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        ArgumentNullException.ThrowIfNull(table);
        if (response is not null && response.Results.TryGetValue("Ids", out var ids) && ids is Guid[] created) return created;
        return bulk.Parameters.TryGetValue(TargetsParameter, out var t) && t is EntityCollection targets
            ? [.. targets.Entities.Select(e => IdOf(table, e))]
            : [];
    }

    /// <summary>UpsertMultiple: the same id twice fails the whole request, before anything runs (ruling B3).</summary>
    /// <exception cref="XrmdFaultException">400 naming the repeated record.</exception>
    public static void DemandDistinctIds(EntityMetadata table, IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(ids);
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
            if (id != Guid.Empty && !seen.Add(id))
                throw XrmdFaultException.InvalidArgument(
                    $"UpsertMultiple names the '{table.LogicalName}' record {id} more than once: each target must be a different record.");
    }

    /// <summary>
    /// UpdateMultiple: true when <paramref name="id"/> was already named by an earlier target (added to <paramref name="seen"/>
    /// otherwise): the first one wins, the later one is ignored (ruling B3 and plan 3.4).
    /// </summary>
    public static bool IsRepeated(Guid id, ISet<Guid> seen) => id != Guid.Empty && !seen.Add(id);

    /// <summary>
    /// Validates a bulk request for the standard-table pipeline (#76) and returns the request to run there: the same message with
    /// only its <c>Targets</c> (the caller copies the optional parameters it forwards), an UpdateMultiple without its repeated
    /// ids (ruling B3: no plug-in sees them).
    /// </summary>
    /// <param name="table">The one table of the targets.</param>
    /// <exception cref="XrmdFaultException">400 for missing or empty targets, targets of another table, an unknown table, or an
    /// UpsertMultiple naming the same id twice.</exception>
    public static OrganizationRequest Prepare(OrganizationRequest bulk, IMetadataStore store, out EntityMetadata table)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        ArgumentNullException.ThrowIfNull(store);
        var targets = Read(bulk);
        table = TableOf(targets, store);
        var entityTable = table;
        if (targets.Message == DeleteMultiple) return bulk;
        var ids = targets.Items.Select(item => IdOf(entityTable, item)).ToArray();
        if (targets.Message == UpsertMultiple) DemandDistinctIds(table, ids);

        var seen = new HashSet<Guid>();
        var kept = new EntityCollection { EntityName = table.LogicalName };
        for (var i = 0; i < targets.Count; i++)
            if (targets.Message != UpdateMultiple || !IsRepeated(ids[i], seen))
                kept.Entities.Add((Entity)targets.Items[i]!);
        return new OrganizationRequest(targets.Message) { RequestId = bulk.RequestId, [TargetsParameter] = kept };
    }

    /// <summary>
    /// The response of a bulk message from the responses of its single-record requests, in target order (ruling B14):
    /// <see cref="CreateMultipleResponse"/> (<c>Ids</c>), an empty <see cref="UpdateMultipleResponse"/>,
    /// <see cref="UpsertMultipleResponse"/> (one <see cref="UpsertResponse"/> per target) or an empty <c>DeleteMultiple</c>
    /// response. A null response (a target that did not run) has no entry, except in <c>Ids</c>/<c>Results</c> where it fails.
    /// </summary>
    public static OrganizationResponse Response(string message, IReadOnlyList<OrganizationResponse?> singles)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(singles);
        return MessageOf(message) switch
        {
            CreateMultiple => new CreateMultipleResponse
            {
                ResponseName = CreateMultiple,
                Results = { ["Ids"] = singles.Select(r => (Guid)r!.Results["id"]).ToArray() },
            },
            UpdateMultiple => new UpdateMultipleResponse { ResponseName = UpdateMultiple },
            UpsertMultiple => UpsertResults(singles),
            _ => new OrganizationResponse { ResponseName = DeleteMultiple },
        };
    }

    private static void DemandAny(string message, int count)
    {
        if (count == 0) throw XrmdFaultException.InvalidArgument($"Request '{message}' needs at least one target.");
    }

    private static UpsertMultipleResponse UpsertResults(IReadOnlyList<OrganizationResponse?> responses)
    {
        var response = new UpsertMultipleResponse { ResponseName = UpsertMultiple };
        // UpsertMultipleResponse.Results (the typed array) hides the parameter collection of the same name.
        ((OrganizationResponse)response).Results["Results"] = responses.Select(AsUpsertResponse).ToArray();
        return response;
    }

    private static UpsertResponse AsUpsertResponse(OrganizationResponse? response)
    {
        if (response is UpsertResponse upsert) return upsert;
        var converted = new UpsertResponse { ResponseName = "Upsert" };
        if (response is not null) converted.Results.AddRange(response.Results);
        return converted;
    }
}
