using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// The Web API side of the queue messages (plan 4.1a, task 5), with the bindings and parameters of the Learn Web API reference, run
/// as the SDK requests through <see cref="OrganizationService"/> so the pipeline and the security checks apply:
/// <c>POST queues({id})/Microsoft.Dynamics.CRM.AddToQueue</c> with <c>Target</c> (the record: <c>@odata.type</c> and its key),
/// optional <c>SourceQueue</c> (<c>{"queueid":…}</c>) and <c>QueueItemProperties</c> (queue item columns), answering 200 with
/// <c>QueueItemId</c>; <c>POST queueitems({id})/Microsoft.Dynamics.CRM.RemoveFromQueue</c>; unbound <c>POST RouteTo</c> with
/// <c>Target</c> (a queue, user or team) and <c>QueueItem</c> (<c>{"queueitemid":…}</c>); and
/// <c>POST queues({id})/Microsoft.Dynamics.CRM.AddPrincipalToQueue</c> with <c>Principal</c> (a user or team). The last three answer
/// 204. Task 6: <c>POST queueitems({id})/Microsoft.Dynamics.CRM.PickFromQueue</c> with <c>SystemUser</c> (<c>{"systemuserid":…}</c>)
/// and <c>RemoveQueueItem</c>, and <c>POST queueitems({id})/Microsoft.Dynamics.CRM.ReleaseToQueue</c>, both answering 204; and the
/// function <c>GET systemusers({id})/Microsoft.Dynamics.CRM.RetrieveUserQueues(IncludePublic=…)</c> (the value inline or an
/// <c>@alias</c>), answering a collection of <c>queue</c>. A missing or mistyped parameter, or an unknown one, is 400; a bound
/// operation on another entity set is 404.
/// </summary>
public static class QueueActions
{
    private const string Ns = ODataEdmModelBuilder.Namespace;

    public static IEndpointRouteBuilder MapQueueEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapPost("/{segment}/" + Ns + "." + QueueSchema.AddToQueue, async (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
        {
            var response = await o.ExecuteAsync(ParseAddToQueue(segment, await ReadAsync(c), s), c.RequestAborted);
            c.Response.Headers["OData-Version"] = "4.0";
            return Results.Json(new JsonObject
            {
                ["@odata.context"] = $"{MetadataController.ServiceRoot(c.Request)}$metadata#{Ns}.{QueueSchema.AddToQueue}Response",
                ["QueueItemId"] = response.Results.TryGetValue("QueueItemId", out var id) && id is Guid item ? item : null,
            });
        }).InChangeSet();
        routes.MapPost("/{segment}/" + Ns + "." + QueueSchema.RemoveFromQueue, async (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
            await NoContentAsync(c, o, ParseRemoveFromQueue(segment, await ReadAsync(c), s))).InChangeSet();
        routes.MapPost("/" + QueueSchema.RouteTo, async (HttpContext c, IMetadataStore s, OrganizationService o) =>
            await NoContentAsync(c, o, ParseRouteTo(await ReadAsync(c), s))).InChangeSet();
        routes.MapPost("/{segment}/" + Ns + "." + QueueSchema.AddPrincipalToQueue, async (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
            await NoContentAsync(c, o, ParseAddPrincipalToQueue(segment, await ReadAsync(c), s))).InChangeSet();
        routes.MapPost("/{segment}/" + Ns + "." + QueueSchema.PickFromQueue, async (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
            await NoContentAsync(c, o, ParsePickFromQueue(segment, await ReadAsync(c), s))).InChangeSet();
        routes.MapPost("/{segment}/" + Ns + "." + QueueSchema.ReleaseToQueue, async (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
            await NoContentAsync(c, o, ParseReleaseToQueue(segment, await ReadAsync(c), s))).InChangeSet();
        // The function's segment carries its parameter list, so it is not a literal one: an order ahead of the custom API bound route
        // (whose segments it also fits) picks it, as for RetrievePrincipalAccess.
        routes.MapGet("/{segment}/" + Ns + "." + QueueSchema.RetrieveUserQueues + "({parameters})", RetrieveUserQueuesAsync).WithOrder(-1);
        return parent;
    }

    private static Task<JsonObject> ReadAsync(HttpContext context) => ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);

    private static async Task<IResult> NoContentAsync(HttpContext context, OrganizationService service, OrganizationRequest request)
    {
        await service.ExecuteAsync(request, context.RequestAborted);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.NoContent();
    }

    /// <summary>AddToQueue bound to the queue of <paramref name="segment"/> (<c>queues({id})</c>).</summary>
    /// <exception cref="XrmdFaultException">400 for a wrong parameter; 404 for another entity set.</exception>
    public static AddToQueueRequest ParseAddToQueue(string segment, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        const string action = QueueSchema.AddToQueue;
        var queue = BoundId(segment, QueueSchema.Queue, action, store);
        Allow(body, action, "Target", "SourceQueue", "QueueItemProperties");
        var request = new AddToQueueRequest { DestinationQueueId = queue, Target = Record(body, "Target", action, store) };
        if (body["SourceQueue"] is { } source) request.SourceQueueId = Key(source, "SourceQueue", QueueSchema.Queue, action, store);
        if (body["QueueItemProperties"] is { } properties)
            request.QueueItemProperties = properties is JsonObject columns
                ? ODataJson.FromJson(Untyped(columns), Definition(store, QueueSchema.QueueItem), store)
                : throw XrmdFaultException.InvalidArgument($"The parameter 'QueueItemProperties' of operation '{action}' must be a {Ns}.queueitem object.");
        return request;
    }

    /// <summary>RemoveFromQueue bound to the queue item of <paramref name="segment"/> (<c>queueitems({id})</c>); it takes no parameter.</summary>
    /// <exception cref="XrmdFaultException">400 for a parameter; 404 for another entity set.</exception>
    public static RemoveFromQueueRequest ParseRemoveFromQueue(string segment, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        var item = BoundId(segment, QueueSchema.QueueItem, QueueSchema.RemoveFromQueue, store);
        Allow(body, QueueSchema.RemoveFromQueue);
        return new RemoveFromQueueRequest { QueueItemId = item };
    }

    /// <summary>The unbound RouteTo: <c>Target</c> (a queue, user or team) and <c>QueueItem</c>.</summary>
    /// <exception cref="XrmdFaultException">400 for a missing, mistyped or unknown parameter.</exception>
    public static RouteToRequest ParseRouteTo(JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        const string action = QueueSchema.RouteTo;
        Allow(body, action, "Target", "QueueItem");
        var target = Record(body, "Target", action, store);
        var item = body["QueueItem"] is { } node
            ? Key(node, "QueueItem", QueueSchema.QueueItem, action, store)
            : throw XrmdFaultException.InvalidArgument($"The parameter 'QueueItem' of operation '{action}' is required.");
        return new RouteToRequest { Target = target, QueueItemId = item };
    }

    /// <summary>AddPrincipalToQueue bound to the queue of <paramref name="segment"/>: <c>Principal</c> (a user or team).</summary>
    /// <exception cref="XrmdFaultException">400 for a wrong parameter; 404 for another entity set.</exception>
    public static AddPrincipalToQueueRequest ParseAddPrincipalToQueue(string segment, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        const string action = QueueSchema.AddPrincipalToQueue;
        var queue = BoundId(segment, QueueSchema.Queue, action, store);
        Allow(body, action, "Principal");
        var principal = Record(body, "Principal", action, store);
        return new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity(principal.LogicalName, principal.Id) };
    }

    /// <summary>PickFromQueue bound to the queue item of <paramref name="segment"/>: <c>SystemUser</c> (the worker) and <c>RemoveQueueItem</c>.</summary>
    /// <exception cref="XrmdFaultException">400 for a wrong parameter; 404 for another entity set.</exception>
    public static PickFromQueueRequest ParsePickFromQueue(string segment, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        const string action = QueueSchema.PickFromQueue;
        var item = BoundId(segment, QueueSchema.QueueItem, action, store);
        Allow(body, action, "SystemUser", "RemoveQueueItem");
        if (body["SystemUser"] is not JsonObject user)
            throw XrmdFaultException.InvalidArgument($"The parameter 'SystemUser' of operation '{action}' is required and must be a {Ns}.systemuser object.");
        if (user["@odata.type"] is JsonValue t && t.TryGetValue<string>(out var type)
            && type.TrimStart('#') is var name && name != "systemuser" && name != Ns + ".systemuser")
            throw XrmdFaultException.InvalidArgument($"The parameter 'SystemUser' of operation '{action}' must be a {Ns}.systemuser, not '{type}'.");
        var worker = Key(user, "SystemUser", "systemuser", action, store);
        var remove = body["RemoveQueueItem"] is JsonValue r && r.TryGetValue<bool>(out var flag)
            ? flag
            : throw XrmdFaultException.InvalidArgument($"The parameter 'RemoveQueueItem' of operation '{action}' is required and must be an Edm.Boolean.");
        return new PickFromQueueRequest { QueueItemId = item, WorkerId = worker, RemoveQueueItem = remove };
    }

    /// <summary>ReleaseToQueue bound to the queue item of <paramref name="segment"/> (<c>queueitems({id})</c>); it takes no parameter.</summary>
    /// <exception cref="XrmdFaultException">400 for a parameter; 404 for another entity set.</exception>
    public static ReleaseToQueueRequest ParseReleaseToQueue(string segment, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        var item = BoundId(segment, QueueSchema.QueueItem, QueueSchema.ReleaseToQueue, store);
        Allow(body, QueueSchema.ReleaseToQueue);
        return new ReleaseToQueueRequest { QueueItemId = item };
    }

    /// <summary>
    /// The function RetrieveUserQueues bound to the user of <paramref name="segment"/> (<c>systemusers({id})</c>), with its one
    /// parameter <c>IncludePublic</c> (<c>true</c> or <c>false</c>, inline or an <c>@alias</c> that <paramref name="alias"/> resolves).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing, repeated, unknown or mistyped parameter; 404 for another entity set.</exception>
    public static RetrieveUserQueuesRequest ParseRetrieveUserQueues(string segment, string parameters, Func<string, string?> alias, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(alias);
        const string function = QueueSchema.RetrieveUserQueues;
        var user = BoundId(segment, "systemuser", function, store);
        bool? includePublic = null;
        foreach (var pair in AuditEndpoints.SplitTopLevel(parameters).Where(p => p.Trim().Length > 0))
        {
            var eq = pair.IndexOf('=');
            var name = eq > 0 ? pair[..eq].Trim() : pair.Trim();
            if (eq <= 0 || name != "IncludePublic")
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a valid parameter for the function '{function}'.");
            if (includePublic is not null)
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{function}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            if (text.StartsWith('@'))
                text = alias(text) ?? throw XrmdFaultException.InvalidArgument($"Parameter alias '{text}' of function '{function}' has no value.");
            includePublic = text switch
            {
                "true" => true,
                "false" => false,
                _ => throw XrmdFaultException.InvalidArgument($"The parameter 'IncludePublic' of function '{function}' must be an Edm.Boolean (true or false)."),
            };
        }
        return new RetrieveUserQueuesRequest
        {
            UserId = user,
            IncludePublic = includePublic ?? throw XrmdFaultException.InvalidArgument($"Required parameter 'IncludePublic' of function '{function}' is missing."),
        };
    }

    private static async Task<IResult> RetrieveUserQueuesAsync(HttpContext context, IMetadataStore store, OrganizationService service,
        string segment, string parameters)
    {
        var request = ParseRetrieveUserQueues(segment, parameters,
            alias => context.Request.Query[alias].ToString() is { Length: > 0 } v ? v : null, store);
        var response = await service.ExecuteAsync(request, context.RequestAborted);
        var collection = (EntityCollection)response.Results["EntityCollection"];
        var queue = Definition(store, QueueSchema.Queue);
        var unreadable = collection.Entities.Any(e => e.Attributes.Values.Any(v => v is null))
            ? await service.UnreadableColumnsAsync([queue.LogicalName], context.RequestAborted)
            : null;
        return ODataJson.Result(context, EntityCollectionJson.ToJson(collection, queue, ODataJson.BaseUrl(context.Request), [],
            includeFormattedValues: false, nextLink: null, relationships: store.GetOneToManyRelationships(), unreadable: unreadable));
    }

    private static void Allow(JsonObject body, string action, params string[] names)
    {
        foreach (var (name, _) in body)
            if (!names.Contains(name) && !name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' in the request payload is not a valid parameter for the operation '{action}'.");
    }

    private static Microsoft.Xrm.Sdk.Metadata.EntityMetadata Definition(IMetadataStore store, string table) =>
        store.GetEntity(table) ?? throw new InvalidOperationException($"The table '{table}' is not seeded.");

    /// <summary>The id of <c>{set}({id})</c>, whose entity set must be <paramref name="table"/>'s (404 otherwise).</summary>
    private static Guid BoundId(string segment, string table, string action, IMetadataStore store)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The bound action {action} requires a {table}: '{Definition(store, table).EntitySetName}(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != table)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Resource not found for the segment '{segment}/{Ns}.{action}': the action is bound to {table}.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
    }

    /// <summary>A required <c>crmbaseentity</c> parameter: its <c>@odata.type</c> names the table and its primary key the record.</summary>
    private static EntityReference Record(JsonObject body, string name, string action, IMetadataStore store)
    {
        if (body[name] is not JsonObject record)
            throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{action}' is required and must be an entity ({Ns}.crmbaseentity) object.");
        var type = record["@odata.type"] is JsonValue t && t.TryGetValue<string>(out var typeName) ? typeName.TrimStart('#') : null;
        if (type is null)
            throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{action}' must carry '@odata.type' naming its entity type.");
        if (type.StartsWith(Ns + ".", StringComparison.Ordinal)) type = type[(Ns.Length + 1)..];
        var table = store.GetEntity(type)
                    ?? throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{action}' names an entity type that does not exist.");
        return new EntityReference(table.LogicalName, Key(record, name, table.LogicalName, action, store));
    }

    /// <summary>The primary key of an entity parameter of a known table (<c>{"queueid":…}</c>).</summary>
    private static Guid Key(JsonNode node, string name, string table, string action, IMetadataStore store)
    {
        var key = SystemAttributes.PrimaryIdName(Definition(store, table));
        return node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{action}' must carry its key '{key}' as an Edm.Guid.");
    }

    /// <summary>The columns of an entity parameter without its <c>@odata.type</c> annotation.</summary>
    private static JsonObject Untyped(JsonObject columns)
    {
        var copy = new JsonObject();
        foreach (var (name, value) in columns)
            if (name != "@odata.type") copy[name] = value?.DeepClone();
        return copy;
    }

    /// <summary>
    /// Declares the six actions, the function RetrieveUserQueues and the AddToQueue response complex type in <c>$metadata</c> (Learn
    /// Web API reference).
    /// </summary>
    internal static void AddOperations(EdmModel model, EdmEntityContainer container, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        if (!entityTypes.TryGetValue(QueueSchema.Queue, out var queue) || !entityTypes.TryGetValue(QueueSchema.QueueItem, out var item)
            || model.FindDeclaredType($"{Ns}.crmbaseentity") is not IEdmEntityType baseEntity) return;
        var record = new EdmEntityTypeReference(baseEntity, false);

        var response = new EdmComplexType(Ns, QueueSchema.AddToQueue + "Response");
        response.AddStructuralProperty("QueueItemId", EdmPrimitiveTypeKind.Guid, isNullable: false);
        model.AddElement(response);
        var add = new EdmAction(Ns, QueueSchema.AddToQueue, new EdmComplexTypeReference(response, false), isBound: true, entitySetPathExpression: null);
        add.AddParameter("entity", new EdmEntityTypeReference(queue, false));
        add.AddParameter("Target", record);
        add.AddParameter("SourceQueue", new EdmEntityTypeReference(queue, true));
        add.AddParameter("QueueItemProperties", new EdmEntityTypeReference(item, true));
        model.AddElement(add);

        var remove = new EdmAction(Ns, QueueSchema.RemoveFromQueue, null, isBound: true, entitySetPathExpression: null);
        remove.AddParameter("entity", new EdmEntityTypeReference(item, false));
        model.AddElement(remove);

        var route = new EdmAction(Ns, QueueSchema.RouteTo, null, isBound: false, entitySetPathExpression: null);
        route.AddParameter("Target", record);
        route.AddParameter("QueueItem", new EdmEntityTypeReference(item, true));
        model.AddElement(route);
        container.AddActionImport(QueueSchema.RouteTo, route);

        var principal = new EdmAction(Ns, QueueSchema.AddPrincipalToQueue, null, isBound: true, entitySetPathExpression: null);
        principal.AddParameter("entity", new EdmEntityTypeReference(queue, false));
        principal.AddParameter("Principal", record);
        model.AddElement(principal);

        if (!entityTypes.TryGetValue("systemuser", out var systemUser)) return;
        var pick = new EdmAction(Ns, QueueSchema.PickFromQueue, null, isBound: true, entitySetPathExpression: null);
        pick.AddParameter("entity", new EdmEntityTypeReference(item, false));
        pick.AddParameter("SystemUser", new EdmEntityTypeReference(systemUser, false));
        pick.AddParameter("RemoveQueueItem", EdmCoreModel.Instance.GetBoolean(false));
        model.AddElement(pick);

        var release = new EdmAction(Ns, QueueSchema.ReleaseToQueue, null, isBound: true, entitySetPathExpression: null);
        release.AddParameter("entity", new EdmEntityTypeReference(item, false));
        model.AddElement(release);

        var queues = new EdmFunction(Ns, QueueSchema.RetrieveUserQueues,
            new EdmCollectionTypeReference(new EdmCollectionType(new EdmEntityTypeReference(queue, false))), isBound: true,
            entitySetPathExpression: null, isComposable: false);
        queues.AddParameter("entity", new EdmEntityTypeReference(systemUser, false));
        queues.AddParameter("IncludePublic", EdmCoreModel.Instance.GetBoolean(false));
        model.AddElement(queues);
    }
}
