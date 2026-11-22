using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// The Web API surface of elastic tables (plan 3.4) beyond the record routes of <see cref="EntitySetController"/>: the four bulk
/// actions bound to an entity set (they serve every table: on a standard table, #76, CreateMultiple, UpdateMultiple and
/// UpsertMultiple run all or nothing and DeleteMultiple is refused), each executed as the SDK request of the same name through <see cref="OrganizationService"/>
/// (ruling E5), and the helpers the record routes share (the <c>partitionId</c> query parameter, the session token header, the
/// deep insert refusal).
/// <list type="bullet">
/// <item><c>POST {entityset}/Microsoft.Dynamics.CRM.CreateMultiple</c> <c>{ "Targets": [ { "@odata.type": …, columns } ] }</c>:
/// 200 with <c>{ "@odata.context": "…$metadata#Microsoft.Dynamics.CRM.CreateMultipleResponse", "Ids": [ … ] }</c>.</item>
/// <item><c>…/Microsoft.Dynamics.CRM.UpdateMultiple</c> and <c>…/UpsertMultiple</c> (same body, each target with its primary key
/// and, when the row has one, its <c>partitionid</c>): 204.</item>
/// <item><c>…/Microsoft.Dynamics.CRM.DeleteMultiple</c> (each target only <c>@odata.type</c>, the primary key and
/// <c>partitionid</c>): 204.</item>
/// </list>
/// Every target must carry <c>@odata.type</c> naming the table of the entity set; a <c>partitionId</c> query parameter is ignored,
/// as Dataverse does on writes. The writes answer the <c>x-ms-session-token</c>
/// header of the last row written; a failure is the first failed target's error, with the failed targets in the
/// <c>@Microsoft.PowerApps.CDS.ErrorDetails.Plugin.BulkApiErrorDetails</c> annotation when asked for
/// (<see cref="ODataErrorMiddleware"/>).
/// </summary>
public static class ElasticEndpoints
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string ODataType = "@odata.type";
    private const string TargetsParameter = "Targets";
    private const string CreateMultiple = "CreateMultiple";
    private const string UpdateMultiple = "UpdateMultiple";
    private const string UpsertMultiple = "UpsertMultiple";
    private const string DeleteMultiple = "DeleteMultiple";

    /// <summary>The four bulk actions, in the order they are mapped.</summary>
    public static IReadOnlyList<string> BulkActions { get; } = [CreateMultiple, UpdateMultiple, UpsertMultiple, DeleteMultiple];

    public static IEndpointRouteBuilder MapElasticEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        foreach (var action in BulkActions)
            routes.MapPost("/{segment}/" + Ns + "." + action,
                (HttpContext context, IMetadataStore store, OrganizationService service, string segment) =>
                    BulkAsync(context, store, service, segment, action));
        return parent;
    }

    private static async Task<IResult> BulkAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment,
        string action)
    {
        var (m, id, _) = EntitySetController.Resolve(store, segment);
        if (id is not null)
            throw XrmdFaultException.InvalidArgument($"{action} is bound to the entity set: use '{m.EntitySetName}/{Ns}.{action}' without a key.");
        var request = ToRequest(action, m, await ODataJson.ReadBodyAsync(context.Request), store);
        var response = await service.ExecuteAsync(request, context.RequestAborted);
        SetSessionToken(context, response);
        if (action != CreateMultiple)
        {
            context.Response.Headers["OData-Version"] = "4.0";
            return Results.NoContent();
        }
        var ids = new JsonArray();
        foreach (var created in (Guid[])response.Results["Ids"]) ids.Add(created.ToString());
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{Ns}.{CreateMultiple}Response",
            ["Ids"] = ids,
        });
    }

    /// <summary>
    /// The SDK request of bulk action <paramref name="action"/> bound to the table <paramref name="m"/>: its <c>Targets</c> as
    /// records of <paramref name="m"/> (<see cref="CreateMultipleRequest"/>, <see cref="UpdateMultipleRequest"/>,
    /// <see cref="UpsertMultipleRequest"/>) or, for DeleteMultiple, as references in an <see cref="EntityReferenceCollection"/>.
    /// An UpdateMultiple or DeleteMultiple target with a <c>partitionid</c> names its row by key (primary key and
    /// <c>partitionid</c>), so the row is found in that partition; an UpsertMultiple target keeps the value, which the Upsert
    /// reads.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the problem: a parameter other than <c>Targets</c>, <c>Targets</c> missing
    /// or not an array, a target without <c>@odata.type</c> or of another table, a related record (deep insert), a column the
    /// table does not have, a DeleteMultiple target with a column other than its key or without primary key.</exception>
    public static OrganizationRequest ToRequest(string action, EntityMetadata m, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        foreach (var (name, _) in body)
            if (name != TargetsParameter)
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a parameter of operation '{action}'.");
        if (body[TargetsParameter] is not JsonArray items)
            throw XrmdFaultException.InvalidArgument(
                $"The parameter '{TargetsParameter}' of operation '{action}' is required and must be a Collection({Ns}.crmbaseentity).");

        var pk = SystemAttributes.PrimaryIdName(m);
        var rows = new List<Entity>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject item)
                throw XrmdFaultException.InvalidArgument($"Target {i} of operation '{action}' must be a {Ns}.crmbaseentity object.");
            DemandNoDeepInsert(m, item);
            if (action == DeleteMultiple)
                foreach (var (name, _) in item)
                    if (name != ODataType && name != pk && name != ElasticTables.PartitionId)
                        throw XrmdFaultException.InvalidArgument(
                            $"Target {i} of operation '{action}' has the property '{name}': only {pk} and {ElasticTables.PartitionId} identify the row to delete.");
            var row = ActionController.ReadEntity(action, TargetsParameter, item, store, defaultTable: null);
            if (row.LogicalName != m.LogicalName)
                throw XrmdFaultException.InvalidArgument(
                    $"Target {i} of operation '{action}' is a '{row.LogicalName}' record: the action is bound to '{m.LogicalName}' ({m.EntitySetName}).");
            if (action == DeleteMultiple && row.Id == Guid.Empty)
                throw XrmdFaultException.InvalidArgument($"Target {i} of operation '{action}' must carry its key '{pk}'.");
            rows.Add(row);
        }

        if (action == DeleteMultiple)
        {
            var references = new EntityReferenceCollection();
            foreach (var row in rows)
                references.Add(row.GetAttributeValue<string>(ElasticTables.PartitionId) is { } partition
                    ? new EntityReference(m.LogicalName, Key(pk, row.Id, partition)) { Id = row.Id }
                    : new EntityReference(m.LogicalName, row.Id));
            return new OrganizationRequest(DeleteMultiple) { [TargetsParameter] = references };
        }
        if (action == UpdateMultiple)
            foreach (var row in rows)
                if (row.Id != Guid.Empty && row.GetAttributeValue<string>(ElasticTables.PartitionId) is { } partition)
                    row.KeyAttributes = Key(pk, row.Id, partition);
        var targets = new EntityCollection(rows) { EntityName = m.LogicalName };
        return action switch
        {
            CreateMultiple => new CreateMultipleRequest { Targets = targets },
            UpdateMultiple => new UpdateMultipleRequest { Targets = targets },
            UpsertMultiple => new UpsertMultipleRequest { Targets = targets },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a bulk action."),
        };
    }

    /// <summary>The key of an elastic row: its primary id and <c>partitionid</c>.</summary>
    internal static KeyAttributeCollection Key(string pk, Guid id, string partition) =>
        new() { { pk, id }, { ElasticTables.PartitionId, partition } };

    /// <summary>
    /// The <c>partitionId</c> query parameter (any case), null when absent. The value is taken as given, or without its single
    /// quotes when quoted (<c>'p'</c>, a quote inside doubled), as both forms appear in Dataverse examples.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when given more than once, or not a valid partition id.</exception>
    internal static string? PartitionIdOf(HttpRequest request)
    {
        string? found = null;
        foreach (var (name, values) in request.Query)
        {
            if (!string.Equals(name, ElasticRecordKey.PartitionIdParameter, StringComparison.OrdinalIgnoreCase)) continue;
            if (values.Count != 1 || found is not null)
                throw XrmdFaultException.InvalidArgument($"The query parameter {ElasticRecordKey.PartitionIdParameter} is specified more than once.");
            var value = values[0] ?? "";
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            ElasticTables.DemandValidPartitionId(value);
            found = value;
        }
        return found;
    }

    /// <summary>Copies the session token of a write on an elastic table, if any, to the <c>x-ms-session-token</c> header.</summary>
    internal static void SetSessionToken(HttpContext context, OrganizationResponse response)
    {
        if (response.Results.TryGetValue(ElasticRecords.SessionToken, out var token) && token is string text)
            context.Response.Headers[ElasticRecords.SessionToken] = text;
    }

    /// <summary>
    /// A body that creates related records with a record of an elastic table (a property holding an object or an array that is
    /// not a column: a navigation property with nested records) is refused, as the SDK path refuses <c>RelatedEntities</c>
    /// (ruling E9). A standard table passes: its deep insert is answered as before.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 (<see cref="ElasticWriteRules.DemandNoRelatedEntities"/>).</exception>
    internal static void DemandNoDeepInsert(EntityMetadata m, JsonObject body)
    {
        if (!ElasticTables.IsElastic(m)) return;
        var columns = (m.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        var related = new Entity(m.LogicalName);
        foreach (var (name, node) in body)
            if (!name.Contains('@') && node is JsonObject or JsonArray && !columns.Contains(name))
                related.RelatedEntities[new Relationship(name)] = new EntityCollection();
        ElasticWriteRules.DemandNoRelatedEntities(related);
    }
}
