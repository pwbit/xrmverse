using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm.Csdl;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.WebApi;

/// <summary>
/// <c>$metadata</c> and <c>EntityDefinitions</c> of the Web API (minimal API endpoints, mapped on the
/// <c>/api/data/v9.2</c> group):
/// <list type="bullet">
/// <item><c>GET $metadata</c>: CSDL of <see cref="ODataEdmModelBuilder"/>.</item>
/// <item><c>GET EntityDefinitions</c> with <c>$select</c>, <c>$filter=LogicalName eq 'x'</c>, <c>$expand=Attributes</c>.</item>
/// <item><c>GET EntityDefinitions(LogicalName='x')</c> or <c>EntityDefinitions({MetadataId})</c>, and <c>.../Attributes</c>.</item>
/// <item><c>GET EntityDefinitions(...)/Attributes(LogicalName='y')</c> or <c>Attributes({MetadataId})</c>, optionally cast
/// (<c>.../Microsoft.Dynamics.CRM.PicklistAttributeMetadata</c>), with <c>$select</c> and <c>$expand=OptionSet,GlobalOptionSet</c>.</item>
/// <item><c>GET GlobalOptionSetDefinitions</c> with <c>$select</c>, and <c>GlobalOptionSetDefinitions(Name='x')</c> or
/// <c>GlobalOptionSetDefinitions({MetadataId})</c>.</item>
/// <item><c>POST EntityDefinitions</c> (the primary name is the attribute with <c>IsPrimaryName: true</c>) and
/// <c>POST EntityDefinitions(...)/Attributes</c>: 204 with <c>OData-EntityId</c>; they require <c>prvCreateEntity</c> and
/// <c>prvCreateAttribute</c> (403 PrivilegeDenied), ignore the server-owned properties (<c>IsIntersect</c>, <c>IsManaged</c>,
/// ...) and refuse a <c>SchemaName</c> already used by another entity, ignoring case (400). The created entity or attribute
/// is recorded in the <c>Default</c> solution and in the <c>MSCRM.SolutionUniqueName</c> solution (404 when it does not
/// exist), in the transaction that creates it.</item>
/// <item><c>GET RelationshipDefinitions</c> and <c>RelationshipDefinitions(SchemaName='x')</c> or
/// <c>RelationshipDefinitions({MetadataId})</c>.</item>
/// <item>Through <see cref="MetadataOperations"/> (its privileges, 404/412, the components a delete must not orphan):
/// <c>POST RelationshipDefinitions</c> (a <c>ManyToManyRelationshipMetadata</c>, or a <c>OneToManyRelationshipMetadata</c>
/// with its <c>Lookup</c>) and <c>POST GlobalOptionSetDefinitions</c>: 204 with <c>OData-EntityId</c>; <c>PUT</c> of
/// <c>EntityDefinitions(...)</c> and <c>EntityDefinitions(...)/Attributes(...)</c> (the body's <c>LogicalName</c>, when
/// given, is the URL's); <c>DELETE</c> of <c>EntityDefinitions(...)</c>, <c>EntityDefinitions(...)/Attributes(...)</c>,
/// <c>RelationshipDefinitions(...)</c> and <c>GlobalOptionSetDefinitions(...)</c>: 204. The
/// <c>MSCRM.SolutionUniqueName</c> header becomes the message's <c>SolutionUniqueName</c>.</item>
/// </list>
/// Metadata JSON is written by <see cref="MetadataSerializer"/>, with <c>@odata.type</c> values prefixed by <c>#</c>;
/// request bodies accept <c>@odata.type</c> with or without <c>#</c>, anywhere in the object.
/// </summary>
public static partial class MetadataController
{
    public const string ServiceRootPath = "/api/data/v9.2/";
    private const string JsonContentType = "application/json; odata.metadata=minimal";
    private const string EntityDefinitions = "EntityDefinitions";
    private const string AttributesSegment = "Attributes";
    private const string GlobalOptionSetDefinitions = "GlobalOptionSetDefinitions";
    private const string RelationshipDefinitions = "RelationshipDefinitions";
    private const string SolutionHeader = "MSCRM.SolutionUniqueName";

    /// <summary>Request-body names of the polymorphic SDK base types: they mean "no subtype" and are dropped.</summary>
    private static readonly HashSet<string> BaseTypeNames = new(StringComparer.Ordinal)
    {
        MetadataSerializer.ODataTypePrefix + nameof(AttributeMetadata),
        MetadataSerializer.ODataTypePrefix + nameof(OptionMetadata),
        MetadataSerializer.ODataTypePrefix + nameof(OptionSetMetadataBase),
    };

    private static readonly HashSet<string> EntityProperties =
        typeof(EntityMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    /// <param name="parent">The <c>/api/data/v9.2</c> route group.</param>
    /// <remarks>Every endpoint requires the caller to map to a <c>systemuser</c> (403 NotMemberOfOrganization otherwise).</remarks>
    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapGet("/$metadata", GetMetadata);
        routes.MapGet("/" + EntityDefinitions, ListEntities);
        routes.MapGet("/" + EntityDefinitions + "({key})", GetEntity);
        routes.MapGet("/" + EntityDefinitions + "({key})/Keys", ListKeys);
        routes.MapGet("/" + EntityDefinitions + "({key})/" + AttributesSegment, ListAttributes);
        routes.MapGet("/" + EntityDefinitions + "({key})/" + AttributesSegment + "({attributeKey})",
            (HttpContext context, IMetadataStore store, string key, string attributeKey) => GetAttribute(context, store, key, attributeKey, null));
        routes.MapGet("/" + EntityDefinitions + "({key})/" + AttributesSegment + "({attributeKey})/{cast}", GetAttribute);
        routes.MapGet("/" + GlobalOptionSetDefinitions, ListOptionSets);
        routes.MapGet("/" + GlobalOptionSetDefinitions + "({key})", GetOptionSet);
        routes.MapPost("/" + EntityDefinitions, CreateEntityAsync);
        routes.MapPost("/" + EntityDefinitions + "({key})/" + AttributesSegment, CreateAttributeAsync);
        routes.MapGet("/" + RelationshipDefinitions, ListRelationships);
        routes.MapGet("/" + RelationshipDefinitions + "({key})", GetRelationship);
        routes.MapPost("/" + RelationshipDefinitions, CreateRelationshipAsync);
        routes.MapPost("/" + GlobalOptionSetDefinitions, CreateOptionSetAsync);
        routes.MapPost("/" + CustomerColumns.CreateMessage, CreateCustomerRelationshipsAsync);
        routes.MapPut("/" + EntityDefinitions + "({key})", UpdateEntityAsync);
        routes.MapPut("/" + EntityDefinitions + "({key})/" + AttributesSegment + "({attributeKey})", UpdateAttributeAsync);
        routes.MapDelete("/" + EntityDefinitions + "({key})", DeleteEntityAsync);
        routes.MapDelete("/" + EntityDefinitions + "({key})/" + AttributesSegment + "({attributeKey})", DeleteAttributeAsync);
        routes.MapDelete("/" + RelationshipDefinitions + "({key})", DeleteRelationshipAsync);
        routes.MapDelete("/" + GlobalOptionSetDefinitions + "({key})", DeleteOptionSetAsync);
        return parent;
    }

    private static async Task<IResult> GetMetadata(HttpContext context, ODataEdmModelBuilder builder, CustomApiRegistry customApis)
    {
        // Load the committed custom APIs first: Build reads the loaded snapshot without waiting.
        await customApis.AllAsync(context.RequestAborted);
        var model = builder.Build();
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            if (!CsdlWriter.TryWriteCsdl(model, writer, CsdlTarget.OData, out var errors))
                throw new InvalidOperationException("CSDL generation failed: " + string.Join("; ", errors.Select(e => e.ErrorMessage)));
        }
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.Bytes(stream.ToArray(), "application/xml");
    }

    private static IResult ListEntities(HttpContext context, IMetadataStore store)
    {
        var query = ReadQuery(context.Request);
        IEnumerable<EntityMetadata> entities = store.GetAllEntities();
        if (context.Request.Query["$filter"].ToString() is { Length: > 0 } filter)
        {
            var match = LogicalNameFilter().Match(filter);
            if (!match.Success)
                throw XrmdFaultException.InvalidArgument($"Unsupported $filter '{filter}': only LogicalName eq '<name>' is supported.");
            entities = entities.Where(e => e.LogicalName == match.Groups[1].Value.Replace("''", "'"));
        }

        var value = new JsonArray(entities.Select(e => (JsonNode)ToJson(ExportedSettings.Entity(store, e, query.ExpandAttributes), query)).ToArray());
        var odataContext = ServiceRoot(context.Request) + "$metadata#" + EntityDefinitions + SelectClause(query.Select);
        return Json(context, new JsonObject { ["@odata.context"] = odataContext, ["value"] = value });
    }

    private static IResult GetEntity(HttpContext context, IMetadataStore store, string key)
    {
        var query = ReadQuery(context.Request);
        var json = ToJson(ExportedSettings.Entity(store, FindEntity(store, key), query.ExpandAttributes), query);
        var odataContext = ServiceRoot(context.Request) + "$metadata#" + EntityDefinitions + SelectClause(query.Select) + "/$entity";
        return Json(context, Prepend("@odata.context", odataContext, json));
    }

    /// <summary>The alternate keys of a table: the one an elastic table reports, none for a standard one.</summary>
    private static IResult ListKeys(HttpContext context, IMetadataStore store, string key)
    {
        var entity = FindEntity(store, key);
        var select = SplitList(context.Request.Query["$select"].ToString());
        var value = new JsonArray((ExportedSettings.Entity(store, entity, attributes: false).Keys ?? [])
            .Select(k => (JsonNode)Project(JsonSerializer.SerializeToNode(k, MetadataSerializer.Options)!.AsObject(), select)).ToArray());
        var odataContext = $"{ServiceRoot(context.Request)}$metadata#{EntityDefinitions}('{entity.LogicalName}')/Keys";
        return Json(context, new JsonObject { ["@odata.context"] = odataContext, ["value"] = value });
    }

    private static IResult ListAttributes(HttpContext context, IMetadataStore store, string key)
    {
        var entity = FindEntity(store, key);
        var select = SplitList(context.Request.Query["$select"].ToString());
        var value = new JsonArray((entity.Attributes ?? [])
            .Select(a => (JsonNode)AttributeToJson(ExportedSettings.Attribute(store, entity.LogicalName, a), select)).ToArray());
        var odataContext = $"{ServiceRoot(context.Request)}$metadata#{EntityDefinitions}('{entity.LogicalName}')/{AttributesSegment}";
        return Json(context, new JsonObject { ["@odata.context"] = odataContext, ["value"] = value });
    }

    private static IResult GetAttribute(HttpContext context, IMetadataStore store, string key, string attributeKey, string? cast)
    {
        var entity = FindEntity(store, key);
        var attribute = FindAttribute(entity, attributeKey);
        if (cast is not null) EnsureCast(attribute, cast);

        var json = AttributeToJson(ExportedSettings.Attribute(store, entity.LogicalName, attribute), SplitList(context.Request.Query["$select"].ToString()));
        var optionSet = attribute switch
        {
            EnumAttributeMetadata e => (OptionSetMetadataBase?)e.OptionSet,
            BooleanAttributeMetadata b => b.OptionSet,
            _ => null,
        };
        foreach (var name in SplitList(context.Request.Query["$expand"].ToString()))
        {
            json[name] = name switch
            {
                "OptionSet" when attribute is EnumAttributeMetadata or BooleanAttributeMetadata =>
                    optionSet is null ? null : OptionSetToJson(optionSet),
                "GlobalOptionSet" when attribute is EnumAttributeMetadata or BooleanAttributeMetadata =>
                    optionSet?.IsGlobal == true ? OptionSetToJson(optionSet) : null,
                _ => throw XrmdFaultException.InvalidArgument(
                    $"Unsupported $expand '{name}': only OptionSet and GlobalOptionSet of an option set attribute are supported."),
            };
        }

        var odataContext = $"{ServiceRoot(context.Request)}$metadata#{EntityDefinitions}('{entity.LogicalName}')/{AttributesSegment}" +
                           (cast is null ? "" : $"({cast})") + "/$entity";
        return Json(context, Prepend("@odata.context", odataContext, json));
    }

    private static IResult ListOptionSets(HttpContext context, IMetadataStore store)
    {
        var select = SplitList(context.Request.Query["$select"].ToString());
        var value = new JsonArray(store.GetAllOptionSets().Select(o => (JsonNode)Project(OptionSetToJson(o), select)).ToArray());
        var odataContext = ServiceRoot(context.Request) + "$metadata#" + GlobalOptionSetDefinitions + SelectClause(select);
        return Json(context, new JsonObject { ["@odata.context"] = odataContext, ["value"] = value });
    }

    private static IResult GetOptionSet(HttpContext context, IMetadataStore store, string key)
    {
        var select = SplitList(context.Request.Query["$select"].ToString());
        var json = Project(OptionSetToJson(FindOptionSet(store, key)), select);
        var odataContext = ServiceRoot(context.Request) + "$metadata#" + GlobalOptionSetDefinitions + SelectClause(select) + "/$entity";
        return Json(context, Prepend("@odata.context", odataContext, json));
    }

    /// <summary>The option set as <see cref="OptionSetMetadataBase"/> (so with its <c>@odata.type</c>, prefixed by <c>#</c>).</summary>
    private static JsonObject OptionSetToJson(OptionSetMetadataBase optionSet)
    {
        var json = JsonSerializer.SerializeToNode(optionSet, MetadataSerializer.Options)!.AsObject();
        PrefixODataTypes(json);
        return json;
    }

    private static IResult ListRelationships(HttpContext context, IMetadataStore store)
    {
        var select = SplitList(context.Request.Query["$select"].ToString());
        var all = store.GetManyToManyRelationships().Cast<RelationshipMetadataBase>().Concat(store.GetOneToManyRelationships())
            .OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase);
        var value = new JsonArray(all.Select(r => (JsonNode)Project(RelationshipToJson(r), select)).ToArray());
        var odataContext = ServiceRoot(context.Request) + "$metadata#" + RelationshipDefinitions + SelectClause(select);
        return Json(context, new JsonObject { ["@odata.context"] = odataContext, ["value"] = value });
    }

    private static IResult GetRelationship(HttpContext context, IMetadataStore store, string key)
    {
        var select = SplitList(context.Request.Query["$select"].ToString());
        var json = Project(RelationshipToJson(FindRelationship(store, key)), select);
        var odataContext = ServiceRoot(context.Request) + "$metadata#" + RelationshipDefinitions + SelectClause(select) + "/$entity";
        return Json(context, Prepend("@odata.context", odataContext, json));
    }

    /// <summary>The relationship as <see cref="RelationshipMetadataBase"/> (so with its <c>@odata.type</c>).</summary>
    private static JsonObject RelationshipToJson(RelationshipMetadataBase relationship) =>
        JsonSerializer.SerializeToNode(relationship, MetadataSerializer.Options)!.AsObject();

    /// <summary>
    /// <c>POST RelationshipDefinitions</c>: CreateManyToMany for a <c>ManyToManyRelationshipMetadata</c> (the intersect
    /// entity named by <c>IntersectEntityName</c>, else by the SchemaName), CreateOneToMany for a
    /// <c>OneToManyRelationshipMetadata</c> whose <c>Lookup</c> is a <c>LookupAttributeMetadata</c>.
    /// </summary>
    private static async Task<IResult> CreateRelationshipAsync(HttpContext context, CallerResolver callers, MetadataOperations operations)
    {
        JsonObject? lookupJson = null;
        var relationship = await ReadBodyAsync<RelationshipMetadataBase>(context.Request, body =>
        {
            lookupJson = body["Lookup"] as JsonObject;
            body.Remove("Lookup");
            body.Remove(nameof(RelationshipMetadataBase.MetadataId));
        });

        OrganizationRequest request;
        string idName;
        switch (relationship)
        {
            case ManyToManyRelationshipMetadata manyToMany:
                request = new CreateManyToManyRequest
                {
                    ManyToManyRelationship = manyToMany,
                    IntersectEntitySchemaName = manyToMany.IntersectEntityName ?? manyToMany.SchemaName,
                };
                idName = "ManyToManyRelationshipId";
                break;
            case OneToManyRelationshipMetadata oneToMany:
                if (lookupJson is null)
                    throw XrmdFaultException.InvalidArgument("A OneToManyRelationshipMetadata needs its Lookup (a LookupAttributeMetadata).");
                RemoveServerOwnedAttributeProperties(lookupJson);
                if (Deserialize<AttributeMetadata>(lookupJson) is not LookupAttributeMetadata lookup)
                    throw XrmdFaultException.InvalidArgument("Lookup must be a Microsoft.Dynamics.CRM.LookupAttributeMetadata.");
                request = new CreateOneToManyRequest { OneToManyRelationship = oneToMany, Lookup = lookup };
                idName = "RelationshipId";
                break;
            default:
                throw XrmdFaultException.InvalidArgument(
                    "The body must be a Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata or OneToManyRelationshipMetadata (@odata.type).");
        }

        var response = await ExecuteAsync(context, callers, operations, request);
        return NoContentWithEntityId(context, $"{RelationshipDefinitions}({(Guid)response.Results[idName]})");
    }

    /// <summary>
    /// <c>POST CreateCustomerRelationships</c> (the Dataverse action): <c>{ "Lookup": {...}, "OneToManyRelationships": [{...}, {...}],
    /// "SolutionUniqueName": "..." }</c> becomes the SDK request of the same name, so its privileges, checks and refusals are the
    /// SDK path's. Answers 200 with <c>{ AttributeId, RelationshipIds }</c>. <c>@odata.type</c> of the lookup and of each
    /// relationship may be left out (LookupAttributeMetadata and OneToManyRelationshipMetadata); the lookup may also be typed
    /// <c>ComplexLookupAttributeMetadata</c>, the action's own parameter type. A lookup with a SchemaName and no LogicalName gets
    /// the SchemaName in lower case as its LogicalName, as Dataverse does.
    /// </summary>
    private static async Task<IResult> CreateCustomerRelationshipsAsync(HttpContext context, CallerResolver callers, MetadataOperations operations)
    {
        var body = await ODataJson.ReadBodyAsync(context.Request);
        var request = new CreateCustomerRelationshipsRequest();
        foreach (var (name, node) in body)
        {
            if (name.StartsWith('@')) continue;
            switch (name)
            {
                case "Lookup":
                    if (node is not JsonObject lookupJson)
                        throw XrmdFaultException.InvalidArgument("Lookup must be a Microsoft.Dynamics.CRM.LookupAttributeMetadata object.");
                    var typed = TypedMetadata(lookupJson, nameof(LookupAttributeMetadata));
                    // The action's parameter type is ComplexLookupAttributeMetadata (Learn's example names it): the same properties.
                    if (typed[MetadataSerializer.ODataTypeProperty] is JsonValue type && type.TryGetValue<string>(out var typeName)
                        && typeName.TrimStart('#') == MetadataSerializer.ODataTypePrefix + ComplexLookupType)
                        typed[MetadataSerializer.ODataTypeProperty] = MetadataSerializer.ODataTypePrefix + nameof(LookupAttributeMetadata);
                    RemoveServerOwnedAttributeProperties(typed);
                    request.Lookup = Deserialize<AttributeMetadata>(typed) as LookupAttributeMetadata
                                     ?? throw XrmdFaultException.InvalidArgument("Lookup must be a Microsoft.Dynamics.CRM.LookupAttributeMetadata.");
                    break;
                case "OneToManyRelationships":
                    if (node is not JsonArray items)
                        throw XrmdFaultException.InvalidArgument("OneToManyRelationships must be a Collection(Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata).");
                    request.OneToManyRelationships = items.Select(item =>
                    {
                        if (item is not JsonObject relationshipJson)
                            throw XrmdFaultException.InvalidArgument("OneToManyRelationships must hold OneToManyRelationshipMetadata objects.");
                        var relationship = TypedMetadata(relationshipJson, nameof(OneToManyRelationshipMetadata));
                        relationship.Remove(nameof(RelationshipMetadataBase.MetadataId));
                        return Deserialize<RelationshipMetadataBase>(relationship) as OneToManyRelationshipMetadata
                               ?? throw XrmdFaultException.InvalidArgument("OneToManyRelationships must hold OneToManyRelationshipMetadata objects.");
                    }).ToArray();
                    break;
                case "SolutionUniqueName":
                    request.SolutionUniqueName = node is JsonValue v && v.TryGetValue<string>(out var solution)
                        ? solution
                        : throw XrmdFaultException.InvalidArgument("SolutionUniqueName must be a string.");
                    break;
                default:
                    throw XrmdFaultException.InvalidArgument(
                        $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{CustomerColumns.CreateMessage}'.");
            }
        }
        if (request.Lookup is null)
            throw XrmdFaultException.InvalidArgument("The parameter 'Lookup' of the operation 'CreateCustomerRelationships' is required.");
        // Dataverse derives the logical name from the SchemaName when the body gives only the latter (Learn's example does).
        if (string.IsNullOrEmpty(request.Lookup.LogicalName) && !string.IsNullOrEmpty(request.Lookup.SchemaName))
            request.Lookup.LogicalName = request.Lookup.SchemaName.ToLowerInvariant();
        if (request.OneToManyRelationships is null)
            throw XrmdFaultException.InvalidArgument("The parameter 'OneToManyRelationships' of the operation 'CreateCustomerRelationships' is required.");

        var response = await ExecuteAsync(context, callers, operations, request);
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = $"{ServiceRoot(context.Request)}$metadata#Microsoft.Dynamics.CRM.CreateCustomerRelationshipsResponse",
            ["AttributeId"] = (Guid)response.Results["AttributeId"],
            ["RelationshipIds"] = new JsonArray(((Guid[])response.Results["RelationshipIds"]).Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
        });
    }

    /// <summary>The object normalized (see <see cref="NormalizeInput"/>), with the <c>@odata.type</c> of <paramref name="defaultType"/> when it has none.</summary>
    /// <summary>The Web API type of the <c>Lookup</c> parameter of <c>CreateCustomerRelationships</c>.</summary>
    private const string ComplexLookupType = "ComplexLookupAttributeMetadata";

    private static JsonObject TypedMetadata(JsonObject json, string defaultType)
    {
        var normalized = (JsonObject)NormalizeInput(json)!;
        if (normalized.ContainsKey(MetadataSerializer.ODataTypeProperty)) return normalized;
        var typed = new JsonObject { [MetadataSerializer.ODataTypeProperty] = MetadataSerializer.ODataTypePrefix + defaultType };
        foreach (var (name, child) in normalized.ToList())
        {
            normalized.Remove(name);
            typed[name] = child;
        }
        return typed;
    }

    private static async Task<IResult> CreateOptionSetAsync(HttpContext context, CallerResolver callers, MetadataOperations operations)
    {
        var optionSet = await ReadBodyAsync<OptionSetMetadataBase>(context.Request, body => body.Remove(nameof(OptionSetMetadataBase.MetadataId)));
        var response = await ExecuteAsync(context, callers, operations, new CreateOptionSetRequest { OptionSet = optionSet });
        return NoContentWithEntityId(context, $"{GlobalOptionSetDefinitions}({(Guid)response.Results["OptionSetId"]})");
    }

    private static async Task<IResult> UpdateEntityAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        MetadataOperations operations, string key)
    {
        var current = FindEntity(store, key);
        var entity = await ReadBodyAsync<EntityMetadata>(context.Request, body => body.Remove(nameof(EntityMetadata.Attributes)));
        entity.LogicalName = SameName(entity.LogicalName, current.LogicalName, "entity");
        await ExecuteAsync(context, callers, operations, new UpdateEntityRequest { Entity = entity });
        return NoContent(context);
    }

    private static async Task<IResult> UpdateAttributeAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        MetadataOperations operations, string key, string attributeKey)
    {
        var entity = FindEntity(store, key);
        var current = FindAttribute(entity, attributeKey);
        var attribute = await ReadBodyAsync<AttributeMetadata>(context.Request, RemoveServerOwnedAttributeProperties);
        attribute.LogicalName = SameName(attribute.LogicalName, current.LogicalName, "attribute");
        await ExecuteAsync(context, callers, operations, new UpdateAttributeRequest { EntityName = entity.LogicalName, Attribute = attribute });
        return NoContent(context);
    }

    /// <summary>The URL's name, when the body names the same component or none (400 otherwise).</summary>
    private static string SameName(string? bodyName, string urlName, string kind) =>
        bodyName is null || bodyName == urlName
            ? urlName
            : throw XrmdFaultException.InvalidArgument($"The body names {kind} '{bodyName}', the URL {kind} '{urlName}'.");

    private static async Task<IResult> DeleteEntityAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        MetadataOperations operations, string key)
    {
        var entity = FindEntity(store, key);
        await ExecuteAsync(context, callers, operations, new DeleteEntityRequest { LogicalName = entity.LogicalName });
        return NoContent(context);
    }

    private static async Task<IResult> DeleteAttributeAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        MetadataOperations operations, string key, string attributeKey)
    {
        var entity = FindEntity(store, key);
        var attribute = FindAttribute(entity, attributeKey);
        await ExecuteAsync(context, callers, operations,
            new DeleteAttributeRequest { EntityLogicalName = entity.LogicalName, LogicalName = attribute.LogicalName });
        return NoContent(context);
    }

    private static async Task<IResult> DeleteRelationshipAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        MetadataOperations operations, string key)
    {
        var relationship = FindRelationship(store, key);
        await ExecuteAsync(context, callers, operations, new DeleteRelationshipRequest { Name = relationship.SchemaName });
        return NoContent(context);
    }

    private static async Task<IResult> DeleteOptionSetAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        MetadataOperations operations, string key)
    {
        var optionSet = FindOptionSet(store, key);
        await ExecuteAsync(context, callers, operations, new DeleteOptionSetRequest { Name = optionSet.Name });
        return NoContent(context);
    }

    /// <summary>The <c>MSCRM.SolutionUniqueName</c> header; null when absent or empty.</summary>
    private static string? SolutionUniqueName(HttpContext context) =>
        context.Request.Headers[SolutionHeader].ToString() is { Length: > 0 } solution ? solution : null;

    /// <summary>Runs a metadata message as the caller; the <c>MSCRM.SolutionUniqueName</c> header is its <c>SolutionUniqueName</c>.</summary>
    private static async Task<OrganizationResponse> ExecuteAsync(HttpContext context, CallerResolver callers, MetadataOperations operations,
        OrganizationRequest request)
    {
        if (SolutionUniqueName(context) is { } solution) request["SolutionUniqueName"] = solution;
        var caller = await callers.ResolveAsync(context.RequestAborted);
        return await operations.ExecuteAsync(request, caller, null, context.RequestAborted);
    }

    private static IResult NoContent(HttpContext context)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.NoContent();
    }

    private static async Task<IResult> CreateEntityAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        SecurityGuard guard, MetadataOperations operations, PublisherPrefixRule prefixes)
    {
        await guard.DemandPrivilegeAsync(await callers.ResolveAsync(context.RequestAborted), PrivilegeNames.CreateEntity,
            EntityDefinitions, context.RequestAborted);
        var entity = await ReadBodyAsync<EntityMetadata>(context.Request, body =>
        {
            foreach (var name in ServerOwnedProperties.Entity) body.Remove(name);
            if (body[nameof(EntityMetadata.Attributes)] is JsonArray attributes)
                foreach (var a in attributes.OfType<JsonObject>()) RemoveServerOwnedAttributeProperties(a);
        });

        LogicalNameValidator.EnsureValid(entity.LogicalName, "entity");
        entity.EntitySetName ??= entity.LogicalName + "s";
        if (!LogicalNameValidator.IsValidEntitySetName(entity.EntitySetName))
            throw XrmdFaultException.InvalidArgument(LogicalNameValidator.InvalidEntitySetNameMessage(entity.EntitySetName));

        var primaryNames = (entity.Attributes ?? []).Where(a => a.IsPrimaryName == true).ToList();
        if (primaryNames is not [StringAttributeMetadata primaryName])
            throw XrmdFaultException.InvalidArgument(
                "Attributes must contain exactly one StringAttributeMetadata with IsPrimaryName = true (the primary name attribute).");
        // Every name the body supplies (the primary name is one of its Attributes).
        prefixes.DemandEntity(entity, null);
        if ((entity.Attributes ?? []).GroupBy(a => a.LogicalName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw XrmdFaultException.InvalidArgument($"Attribute '{duplicate.Key}' appears more than once in Attributes.");
        // The same rules as the SDK's CreateEntity and the solution import.
        MetadataRequestRules.DemandSupportedEntity(entity, isNew: true);
        MetadataRequestRules.DemandOnlyModelledSettings(entity, primaryName.LogicalName);
        foreach (var given in entity.Attributes ?? []) MetadataRequestRules.DemandOnlyModelledAttributeSettings(given, entity.LogicalName);

        if (EntityExists(store, entity)) throw DuplicateEntity(entity);
        // Privilege names are built from the SchemaName (compared ignoring case): two entities must not share one.
        var schemaName = entity.SchemaName ?? entity.LogicalName;
        if (store.GetAllEntities().FirstOrDefault(e =>
                string.Equals(e.SchemaName ?? e.LogicalName, schemaName, StringComparison.OrdinalIgnoreCase)) is { } clash)
            throw XrmdFaultException.InvalidArgument(
                $"SchemaName '{schemaName}' is already used by entity '{clash.LogicalName}' (SchemaName is unique, ignoring case).");

        entity.MetadataId = Guid.NewGuid();
        try
        {
            await operations.CreateTrackedAsync((session, ct) => store.CreateEntityAsync(session, entity, primaryName, ct),
                ComponentType.Entity, entity.MetadataId.Value, SolutionUniqueName(context), context.RequestAborted);
        }
        catch (MetadataComponentExistsException) when (EntityExists(store, entity))
        {
            throw DuplicateEntity(entity); // lost a race with a concurrent create
        }

        return NoContentWithEntityId(context, $"{EntityDefinitions}({entity.MetadataId})");
    }

    private static async Task<IResult> CreateAttributeAsync(HttpContext context, IMetadataStore store, CallerResolver callers,
        SecurityGuard guard, MetadataOperations operations, PublisherPrefixRule prefixes, string key)
    {
        await guard.DemandPrivilegeAsync(await callers.ResolveAsync(context.RequestAborted), PrivilegeNames.CreateAttribute,
            AttributesSegment, context.RequestAborted);
        var entity = FindEntity(store, key);
        var attribute = await ReadBodyAsync<AttributeMetadata>(context.Request, RemoveServerOwnedAttributeProperties);
        LogicalNameValidator.EnsureValid(attribute.LogicalName, "attribute"); // a missing or malformed name before the prefix rule
        prefixes.Demand(attribute.LogicalName, "column");
        MetadataRequestRules.DemandOnlyModelledAttributeSettings(attribute, entity.LogicalName);

        if (HasAttribute(store, entity.LogicalName, attribute.LogicalName)) throw DuplicateAttribute(entity, attribute);

        attribute.MetadataId = Guid.NewGuid();
        try
        {
            await operations.CreateTrackedAsync((session, ct) => store.AddAttributeAsync(session, entity.LogicalName, attribute, ct),
                ComponentType.Attribute, attribute.MetadataId.Value, SolutionUniqueName(context), context.RequestAborted,
                addedTo: (entity.LogicalName, $"attribute '{attribute.LogicalName}'"));
        }
        catch (MetadataComponentExistsException) when (HasAttribute(store, entity.LogicalName, attribute.LogicalName))
        {
            throw DuplicateAttribute(entity, attribute); // lost a race with a concurrent add
        }

        return NoContentWithEntityId(context, $"{EntityDefinitions}({entity.MetadataId})/{AttributesSegment}({attribute.MetadataId})");
    }

    private static void RemoveServerOwnedAttributeProperties(JsonObject attribute)
    {
        foreach (var name in ServerOwnedProperties.Attribute) attribute.Remove(name);
        attribute.Remove(ServerOwnedProperties.SourceTypeMask); // declared by the column types that can be calculated (ruling K12)
    }

    /// <summary>Same LogicalName, or same EntitySetName ignoring case (routing ignores case).</summary>
    private static bool EntityExists(IMetadataStore store, EntityMetadata entity) =>
        store.GetEntity(entity.LogicalName) is not null
        || store.GetAllEntities().Any(e => string.Equals(e.EntitySetName, entity.EntitySetName, StringComparison.OrdinalIgnoreCase));

    private static bool HasAttribute(IMetadataStore store, string entityLogicalName, string? attributeLogicalName) =>
        store.GetEntity(entityLogicalName)?.Attributes?.Any(a => a.LogicalName == attributeLogicalName) == true;

    private static XrmdFaultException DuplicateEntity(EntityMetadata entity) =>
        new(ErrorCodes.DuplicateRecord,
            $"An entity with LogicalName '{entity.LogicalName}' or EntitySetName '{entity.EntitySetName}' already exists.", 412);

    private static XrmdFaultException DuplicateAttribute(EntityMetadata entity, AttributeMetadata attribute) =>
        new(ErrorCodes.DuplicateRecord, $"Attribute '{attribute.LogicalName}' already exists on entity '{entity.LogicalName}'.", 412);

    private static IResult NoContentWithEntityId(HttpContext context, string relativeUrl)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        context.Response.Headers["OData-EntityId"] = ServiceRoot(context.Request) + relativeUrl;
        return Results.NoContent();
    }

    /// <summary>Resolves <c>LogicalName='x'</c> or a MetadataId GUID.</summary>
    private static EntityMetadata FindEntity(IMetadataStore store, string key)
    {
        var match = LogicalNameKey().Match(key);
        if (match.Success)
        {
            var name = match.Groups[1].Value.Replace("''", "'");
            return store.GetEntity(name)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"Could not find entity with LogicalName '{name}'.", 404);
        }
        if (Guid.TryParse(key, out var id))
        {
            return store.GetAllEntities().FirstOrDefault(e => e.MetadataId == id)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"EntityMetadata With Id = {id} Does Not Exist", 404);
        }
        throw XrmdFaultException.InvalidArgument($"Invalid EntityDefinitions key '{key}': use LogicalName='<name>' or a MetadataId.");
    }

    /// <summary>Resolves <c>LogicalName='x'</c> or a MetadataId GUID among the entity's attributes.</summary>
    private static AttributeMetadata FindAttribute(EntityMetadata entity, string key)
    {
        var match = LogicalNameKey().Match(key);
        if (match.Success)
        {
            var name = match.Groups[1].Value.Replace("''", "'");
            return (entity.Attributes ?? []).FirstOrDefault(a => a.LogicalName == name)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist,
                       $"Could not find attribute with LogicalName '{name}' on entity '{entity.LogicalName}'.", 404);
        }
        if (Guid.TryParse(key, out var id))
        {
            return (entity.Attributes ?? []).FirstOrDefault(a => a.MetadataId == id)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"AttributeMetadata With Id = {id} Does Not Exist", 404);
        }
        throw XrmdFaultException.InvalidArgument($"Invalid Attributes key '{key}': use LogicalName='<name>' or a MetadataId.");
    }

    /// <summary>A cast segment names the attribute's metadata type or one of its base types.</summary>
    private static void EnsureCast(AttributeMetadata attribute, string cast)
    {
        var type = cast.StartsWith(MetadataSerializer.ODataTypePrefix, StringComparison.Ordinal)
            ? typeof(AttributeMetadata).Assembly.GetType(typeof(AttributeMetadata).Namespace + "." + cast[MetadataSerializer.ODataTypePrefix.Length..])
            : null;
        if (type is null || !typeof(AttributeMetadata).IsAssignableFrom(type))
            throw XrmdFaultException.InvalidArgument($"Unknown attribute metadata type '{cast}'.");
        if (!type.IsInstanceOfType(attribute))
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{attribute.LogicalName}' is a {attribute.GetType().Name}, not a {type.Name}.");
    }

    /// <summary>Resolves <c>Name='x'</c> or a MetadataId GUID among the global option sets.</summary>
    private static OptionSetMetadataBase FindOptionSet(IMetadataStore store, string key)
    {
        var match = NameKey().Match(key);
        if (match.Success)
        {
            var name = match.Groups[1].Value.Replace("''", "'");
            return store.GetOptionSet(name)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"Could not find optionset with Name '{name}'.", 404);
        }
        if (Guid.TryParse(key, out var id))
        {
            return store.GetAllOptionSets().FirstOrDefault(o => o.MetadataId == id)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"OptionSetMetadata With Id = {id} Does Not Exist", 404);
        }
        throw XrmdFaultException.InvalidArgument($"Invalid GlobalOptionSetDefinitions key '{key}': use Name='<name>' or a MetadataId.");
    }

    /// <summary>Resolves <c>SchemaName='x'</c> or a MetadataId GUID among the relationships.</summary>
    private static RelationshipMetadataBase FindRelationship(IMetadataStore store, string key)
    {
        var match = SchemaNameKey().Match(key);
        if (match.Success)
        {
            var name = match.Groups[1].Value.Replace("''", "'");
            return store.GetRelationship(name)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"Could not find relationship with SchemaName '{name}'.", 404);
        }
        if (Guid.TryParse(key, out var id))
        {
            return store.GetManyToManyRelationships().Cast<RelationshipMetadataBase>().Concat(store.GetOneToManyRelationships())
                       .FirstOrDefault(r => r.MetadataId == id)
                   ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"RelationshipMetadata With Id = {id} Does Not Exist", 404);
        }
        throw XrmdFaultException.InvalidArgument($"Invalid RelationshipDefinitions key '{key}': use SchemaName='<name>' or a MetadataId.");
    }

    private sealed record Query(IReadOnlyList<string> Select, bool ExpandAttributes, IReadOnlyList<string> AttributeSelect);

    private static Query ReadQuery(HttpRequest request)
    {
        var select = SplitList(request.Query["$select"].ToString());
        foreach (var name in select)
            if (!EntityProperties.Contains(name))
                throw XrmdFaultException.InvalidArgument($"Could not find a property named '{name}' on type 'Microsoft.Dynamics.CRM.EntityMetadata'.");

        var expand = request.Query["$expand"].ToString();
        if (expand.Length == 0) return new Query(select, false, []);
        var match = AttributesExpand().Match(expand);
        if (!match.Success)
            throw XrmdFaultException.InvalidArgument($"Unsupported $expand '{expand}': only Attributes or Attributes($select=...) is supported.");
        return new Query(select, true, SplitList(match.Groups[1].Value));
    }

    private static List<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string SelectClause(IReadOnlyList<string> select) => select.Count == 0 ? "" : $"({string.Join(',', select)})";

    private static JsonObject ToJson(EntityMetadata entity, Query query)
    {
        var json = JsonSerializer.SerializeToNode(entity, MetadataSerializer.Options)!.AsObject();
        json.Remove(nameof(EntityMetadata.Attributes));
        json = Project(json, query.Select);
        if (query.ExpandAttributes)
            json[nameof(EntityMetadata.Attributes)] =
                new JsonArray((entity.Attributes ?? []).Select(a => (JsonNode)AttributeToJson(a, query.AttributeSelect)).ToArray());
        return json;
    }

    private static JsonObject AttributeToJson(AttributeMetadata attribute, IReadOnlyList<string> select)
    {
        var json = JsonSerializer.SerializeToNode(attribute, MetadataSerializer.Options)!.AsObject();
        return Project(json, select);
    }

    /// <summary>Keeps the selected properties (plus <c>@odata.type</c> and <c>MetadataId</c>), in serializer order.</summary>
    private static JsonObject Project(JsonObject json, IReadOnlyList<string> select)
    {
        PrefixODataTypes(json);
        if (select.Count == 0) return json;
        var keep = new HashSet<string>(select, StringComparer.Ordinal) { MetadataSerializer.ODataTypeProperty, "MetadataId" };
        var result = new JsonObject();
        foreach (var (name, value) in json.ToList())
        {
            if (!keep.Contains(name)) continue;
            json.Remove(name);
            result[name] = value;
        }
        return result;
    }

    /// <summary>Web API responses write <c>"@odata.type": "#Microsoft.Dynamics.CRM.Xxx"</c> (with <c>#</c>), at any depth.</summary>
    private static void PrefixODataTypes(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj[MetadataSerializer.ODataTypeProperty] is JsonValue v && v.TryGetValue<string>(out var type) && !type.StartsWith('#'))
                    obj[MetadataSerializer.ODataTypeProperty] = "#" + type;
                foreach (var (_, child) in obj.ToList()) PrefixODataTypes(child);
                break;
            case JsonArray array:
                foreach (var child in array) PrefixODataTypes(child);
                break;
        }
    }

    /// <summary>
    /// Reads a metadata body. <c>@odata.type</c> may appear anywhere in an object and with or without <c>#</c>;
    /// System.Text.Json needs it first and unprefixed, so the body is normalized before deserializing.
    /// </summary>
    /// <param name="strip">Removes server-owned properties from the normalized body before it is deserialized.</param>
    private static async Task<T> ReadBodyAsync<T>(HttpRequest request, Action<JsonObject> strip) where T : class
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        try
        {
            if (NormalizeInput(JsonNode.Parse(text)) is not JsonObject body)
                throw XrmdFaultException.InvalidArgument("The request body must be a JSON object.");
            strip(body);
            return Deserialize<T>(body);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw XrmdFaultException.InvalidArgument("Invalid request body: " + ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Duplicate property names: JsonObject reads its properties lazily and throws on the first access.
            throw XrmdFaultException.InvalidArgument("Invalid request body: " + ex.Message);
        }
    }

    /// <summary>Deserializes a normalized body (see <see cref="ReadBodyAsync"/>).</summary>
    private static T Deserialize<T>(JsonObject body) where T : class
    {
        try
        {
            return body.Deserialize<T>(MetadataSerializer.Options)
                   ?? throw XrmdFaultException.InvalidArgument("The request body must be a JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw XrmdFaultException.InvalidArgument("Invalid request body: " + ex.Message);
        }
    }

    private static JsonNode? NormalizeInput(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                if (obj[MetadataSerializer.ODataTypeProperty] is JsonValue v && v.TryGetValue<string>(out var type))
                {
                    type = type.TrimStart('#');
                    if (!BaseTypeNames.Contains(type)) result[MetadataSerializer.ODataTypeProperty] = type;
                }
                else if (obj.ContainsKey(MetadataSerializer.ODataTypeProperty))
                {
                    throw XrmdFaultException.InvalidArgument("@odata.type must be a string.");
                }
                foreach (var (name, child) in obj)
                    if (name != MetadataSerializer.ODataTypeProperty) result[name] = NormalizeInput(child);
                return result;
            case JsonArray array:
                return new JsonArray(array.Select(NormalizeInput).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    private static JsonObject Prepend(string name, JsonNode value, JsonObject json)
    {
        var result = new JsonObject { [name] = value };
        foreach (var (key, child) in json.ToList())
        {
            json.Remove(key);
            result[key] = child;
        }
        return result;
    }

    private static IResult Json(HttpContext context, JsonObject json)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.Text(json.ToJsonString(MetadataSerializer.Options), JsonContentType, Encoding.UTF8);
    }

    internal static string ServiceRoot(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}{ServiceRootPath}";

    [GeneratedRegex(@"\A\s*LogicalName\s+eq\s+'((?:[^']|'')*)'\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalNameFilter();

    [GeneratedRegex(@"\ALogicalName='((?:[^']|'')*)'\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalNameKey();

    [GeneratedRegex(@"\AName='((?:[^']|'')*)'\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameKey();

    [GeneratedRegex(@"\ASchemaName='((?:[^']|'')*)'\z", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNameKey();

    [GeneratedRegex(@"\A\s*Attributes(?:\(\s*\$select=([^)]*)\))?\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AttributesExpand();
}
