using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.WebApi;

/// <summary>
/// The duplicate detection operations of the Web API (plan 2.15, rulings D10, D11, D14, D15), each executed as the SDK request of the
/// same name through <see cref="OrganizationService"/>, so every check, privilege and refusal is the SDK path's:
/// <list type="bullet">
/// <item><c>POST duplicaterules({id})/Microsoft.Dynamics.CRM.PublishDuplicateRule</c> (no body): 200 with the queued
/// <c>asyncoperation</c> (<c>$metadata#asyncoperations/$entity</c>) as known without a read: <c>asyncoperationid</c> (the job id)
/// and <c>operationtype</c> 7. It is not read back, so a caller without Read on <c>asyncoperation</c> still gets the job id of the
/// publish that committed.</item>
/// <item><c>POST UnpublishDuplicateRule</c> <c>{ "DuplicateRuleId": "…" }</c>: 204.</item>
/// <item><c>GET RetrieveDuplicates(BusinessEntity=…,MatchingEntityName=…,PagingInfo=…)</c>, each value inline or an <c>@alias</c>
/// of the query string (single-quoted JSON as in Learn's example accepted): 200 with the matches as a collection of the matching
/// table (<c>$metadata#{set}</c>, all columns), with <c>@Microsoft.Dynamics.CRM.morerecords</c> true when a next page exists. <c>BusinessEntity</c> is a record body with <c>@odata.type</c>; its key, when
/// given, names the stored row.</item>
/// <item><c>POST BulkDetectDuplicates</c> with Learn's body: 200 with <c>{ "JobId": "…" }</c>
/// (<c>$metadata#Microsoft.Dynamics.CRM.BulkDetectDuplicatesResponse</c>). <c>Query</c> is a <c>QueryExpression</c> with
/// <c>EntityName</c> only (any other property is refused, explicitly) or a <c>FetchExpression</c> with <c>Query</c>.
/// <c>ToRecipients</c>/<c>CCRecipients</c> take user ids or <c>activityparty</c> objects (Learn's type), read and discarded:
/// email notification is refused.</item>
/// </list>
/// A parameter an operation does not have, or a value of the wrong type, is a 400 naming it.
/// </summary>
public static class DuplicateDetectionActions
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string ODataType = "@odata.type";
    private const string Publish = "PublishDuplicateRule";
    private const string Unpublish = "UnpublishDuplicateRule";
    private const string Retrieve = "RetrieveDuplicates";
    private const string Bulk = "BulkDetectDuplicates";
    private const string RuleId = "DuplicateRuleId";
    private const string Rule = DuplicateDetectionSchema.Rule;
    private const string AsyncOperation = "asyncoperation";
    private const string MoreRecordsAnnotation = "@Microsoft.Dynamics.CRM.morerecords";

    /// <summary><c>asyncoperation.operationtype</c> of a duplicate rule publish job (Learn: Duplicate Detection Rule Publish).</summary>
    private const int PublishOperationType = 7;

    /// <summary>The unbound operation names (reserved as entity set names, see <see cref="ActionController.OperationNames"/>).</summary>
    public static IReadOnlyList<string> OperationNames { get; } = [Unpublish, Retrieve, Bulk];

    private static readonly string[] RetrieveParameters = ["BusinessEntity", "MatchingEntityName", "PagingInfo"];

    private static readonly string[] BulkParameters =
        ["Query", "JobName", "SendEmailNotification", "TemplateId", "ToRecipients", "CCRecipients", "RecurrencePattern", "RecurrenceStartTime"];

    public static IEndpointRouteBuilder MapDuplicateDetectionEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapPost("/{segment}/" + Ns + "." + Publish, PublishAsync);
        routes.MapPost("/" + Unpublish, UnpublishAsync);
        routes.MapGet("/" + Retrieve + "({parameters})", RetrieveAsync);
        routes.MapPost("/" + Bulk, BulkAsync);
        return parent;
    }

    // ---- PublishDuplicateRule / UnpublishDuplicateRule -------------------------------------------------------------------

    private static async Task<IResult> PublishAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment)
    {
        var rule = ParseRule(store, segment);
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        RefuseUnknown(body, Publish, []);
        var response = await service.ExecuteAsync(new OrganizationRequest(Publish) { [RuleId] = rule }, context.RequestAborted);
        if (!response.Results.TryGetValue("JobId", out var value) || value is not Guid job)
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"{Publish} returned no JobId.", 500);
        var setName = store.GetEntity(AsyncOperation)?.EntitySetName ?? "asyncoperations";
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{setName}/$entity",
            ["asyncoperationid"] = job,
            ["operationtype"] = PublishOperationType,
        });
    }

    private static async Task<IResult> UnpublishAsync(HttpContext context, OrganizationService service)
    {
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        RefuseUnknown(body, Unpublish, [RuleId]);
        var request = new OrganizationRequest(Unpublish);
        if (body[RuleId] is { } node) request[RuleId] = ReadGuid(Unpublish, RuleId, node);
        await service.ExecuteAsync(request, context.RequestAborted);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.NoContent();
    }

    /// <summary>The rule of <c>duplicaterules({id})</c>; another entity set is a 404, as for the other bound actions.</summary>
    private static Guid ParseRule(IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The bound action requires a duplicate detection rule: 'duplicaterules(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != Rule)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Resource not found for the segment '{segment}/{Ns}.{Publish}': the action is bound to {Rule}.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
    }

    // ---- RetrieveDuplicates -----------------------------------------------------------------------------------------------

    private static async Task<IResult> RetrieveAsync(HttpContext context, IMetadataStore store, OrganizationService service, string parameters)
    {
        var request = ToRetrieveRequest(parameters, alias => context.Request.Query[alias].ToString() is { Length: > 0 } v ? v : null, store);
        var response = await service.ExecuteAsync(request, context.RequestAborted);
        var collection = (EntityCollection)response.Results["DuplicateCollection"];
        var m = store.GetEntity((string)request["MatchingEntityName"])
                ?? throw new XrmdFaultException(ErrorCodes.Unexpected, $"The matching table of {Retrieve} does not exist.", 500);
        var unreadable = collection.Entities.Any(e => e.Attributes.Values.Any(v => v is null))
            ? await service.UnreadableColumnsAsync([m.LogicalName], context.RequestAborted)
            : null;
        var json = EntityCollectionJson.ToJson(collection, m, ODataJson.BaseUrl(context.Request), [], includeFormattedValues: false, nextLink: null,
            relationships: store.GetOneToManyRelationships(), unreadable: unreadable);
        if (collection.MoreRecords) json[MoreRecordsAnnotation] = true;
        return ODataJson.Result(context, json);
    }

    /// <summary>
    /// The SDK request of <c>RetrieveDuplicates</c> for its parameter list (<c>Name=value</c> pairs, each value inline or an
    /// <c>@alias</c> that <paramref name="alias"/> resolves). <c>PagingInfo</c> is nullable (absent or <c>null</c>: unset).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown, repeated or missing parameter, an alias without a value, or a value
    /// of the wrong shape.</exception>
    internal static OrganizationRequest ToRetrieveRequest(string parameters, Func<string, string?> alias, IMetadataStore store)
    {
        var request = new OrganizationRequest(Retrieve);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in AuditEndpoints.SplitTopLevel(parameters))
        {
            var eq = pair.IndexOf('=');
            var name = eq > 0 ? pair[..eq].Trim() : pair.Trim();
            if (eq <= 0 || !RetrieveParameters.Contains(name, StringComparer.Ordinal))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a valid parameter for the function '{Retrieve}'.");
            if (!seen.Add(name))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{Retrieve}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            if (text.StartsWith('@'))
                text = alias(text) ?? throw XrmdFaultException.InvalidArgument($"Parameter alias '{text}' of function '{Retrieve}' has no value.");
            if (text == "null")
            {
                if (name == "PagingInfo") continue;
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{Retrieve}' must not be null.");
            }
            request[name] = name switch
            {
                "BusinessEntity" => ActionController.ReadEntity(Retrieve, name, AuditEndpoints.Parse(Retrieve, name, text), store, defaultTable: null),
                "MatchingEntityName" => AuditEndpoints.ReadString(Retrieve, name, text),
                _ => AuditEndpoints.ReadPagingInfo(Retrieve, AuditEndpoints.Parse(Retrieve, name, text)),
            };
        }
        foreach (var required in RetrieveParameters.Where(p => p != "PagingInfo"))
            if (!request.Parameters.ContainsKey(required))
                throw XrmdFaultException.InvalidArgument($"Required parameter '{required}' of function '{Retrieve}' is missing.");
        return request;
    }

    // ---- BulkDetectDuplicates ---------------------------------------------------------------------------------------------

    private static async Task<IResult> BulkAsync(HttpContext context, OrganizationService service)
    {
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        var response = await service.ExecuteAsync(ToBulkRequest(body), context.RequestAborted);
        if (!response.Results.TryGetValue("JobId", out var value) || value is not Guid job)
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"{Bulk} returned no JobId.", 500);
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{Ns}.{Bulk}Response",
            ["JobId"] = job,
        });
    }

    /// <summary>
    /// The SDK request of <c>BulkDetectDuplicates</c> for its JSON body, with the SDK's types (<c>ToRecipients</c> and
    /// <c>CCRecipients</c>: <c>Guid[]</c>, sent as arrays of <c>Edm.Guid</c>; <c>RecurrenceStartTime</c>: UTC). A <c>null</c> leaves the
    /// parameter unset; the core then refuses a missing <c>Query</c>, email notification and recurrence.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown parameter or a value of the wrong type, naming it.</exception>
    internal static OrganizationRequest ToBulkRequest(JsonObject body)
    {
        RefuseUnknown(body, Bulk, BulkParameters);
        var request = new OrganizationRequest(Bulk);
        foreach (var (name, node) in body)
        {
            if (name.StartsWith('@') || node is null) continue;
            request[name] = name switch
            {
                "Query" => ReadQuery(node),
                "JobName" or "RecurrencePattern" => node is JsonValue v && v.TryGetValue<string>(out var text)
                    ? text
                    : throw Invalid(Bulk, name, "must be an Edm.String"),
                "SendEmailNotification" => node is JsonValue b && b.TryGetValue<bool>(out var flag) ? flag : throw Invalid(Bulk, name, "must be an Edm.Boolean"),
                "TemplateId" => ReadGuid(Bulk, name, node),
                "ToRecipients" or "CCRecipients" => ReadRecipients(name, node),
                _ => AuditEndpoints.ReadDateTimeOffset(Bulk, name, node), // RecurrenceStartTime
            };
        }
        return request;
    }

    /// <summary>
    /// <c>ToRecipients</c>/<c>CCRecipients</c>: an array of user ids (<c>Edm.Guid</c>, the SDK's shape) or of <c>activityparty</c>
    /// objects (Learn's type). The ids are passed on; an object is read as an entity and discarded, since the email notification
    /// these lists address is refused by the core (no recipient is ever used).
    /// </summary>
    private static Guid[] ReadRecipients(string name, JsonNode node)
    {
        const string expected = "must be a collection of Edm.Guid or of activityparty objects";
        if (node is not JsonArray items) throw Invalid(Bulk, name, expected);
        var ids = new List<Guid>();
        foreach (var item in items)
        {
            if (item is JsonObject) continue; // an activityparty: not used (no email is sent)
            ids.Add(item is JsonValue ? ReadGuid(Bulk, name, item) : throw Invalid(Bulk, name, expected));
        }
        return [.. ids];
    }

    /// <summary>
    /// The <c>Query</c> of BulkDetectDuplicates: <c>{"@odata.type":"#Microsoft.Dynamics.CRM.QueryExpression","EntityName":"…"}</c>
    /// (Learn's example; the job reads every row of the table) or <c>{"@odata.type":"#Microsoft.Dynamics.CRM.FetchExpression","Query":"&lt;fetch…"}</c>
    /// (which can filter). The other <c>QueryExpression</c> properties are not read by xrmverse: they are refused, not ignored.
    /// </summary>
    private static QueryBase ReadQuery(JsonNode node)
    {
        const string name = "Query";
        if (node is not JsonObject query) throw Invalid(Bulk, name, $"must be a {Ns}.QueryBase object");
        var type = query[ODataType] is JsonValue t && t.TryGetValue<string>(out var typeName) ? typeName.TrimStart('#') : null;
        if (type is not null && type.StartsWith(Ns + ".", StringComparison.Ordinal)) type = type[(Ns.Length + 1)..];
        var (property, result) = type switch
        {
            "QueryExpression" => ("EntityName", (Func<string, QueryBase>)(text => new QueryExpression(text))),
            "FetchExpression" => ("Query", text => new FetchExpression(text)),
            _ => throw Invalid(Bulk, name, $"must carry '{ODataType}' naming {Ns}.QueryExpression or {Ns}.FetchExpression"),
        };
        foreach (var (key, _) in query)
            if (key != ODataType && key != property)
                throw Invalid(Bulk, $"{name}/{key}",
                    type == "QueryExpression"
                        ? $"is not supported: a {Ns}.QueryExpression gives only its EntityName here; send a {Ns}.FetchExpression to filter the rows"
                        : $"is not a property of {Ns}.{type}");
        return query[property] is JsonValue v && v.TryGetValue<string>(out var value) && value.Length > 0
            ? result(value)
            : throw Invalid(Bulk, $"{name}/{property}", "must be a non-empty Edm.String");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private static void RefuseUnknown(JsonObject body, string operation, IReadOnlyCollection<string> allowed)
    {
        foreach (var (name, _) in body)
            if (!name.StartsWith('@') && !allowed.Contains(name))
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{operation}'.");
    }

    private static Guid ReadGuid(string operation, string name, JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id)
            ? id
            : throw Invalid(operation, name, "must be an Edm.Guid");

    private static XrmdFaultException Invalid(string operation, string name, string problem) =>
        XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{operation}' {problem}.");

    // ---- $metadata --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Declares <c>PublishDuplicateRule</c> (bound to <c>duplicaterule</c>, returning <c>asyncoperation</c>; left out when either table
    /// is missing), <c>UnpublishDuplicateRule</c>, <c>RetrieveDuplicates</c> (returning <c>Collection(crmbaseentity)</c>) and
    /// <c>BulkDetectDuplicates</c> with <c>BulkDetectDuplicatesResponse</c>, and the abstract <c>QueryBase</c> with
    /// <c>QueryExpression</c> (open: only <c>EntityName</c> is declared) and <c>FetchExpression</c>. Runs after
    /// <see cref="AuditEndpoints"/>'s, which declares <c>PagingInfo</c>.
    /// </summary>
    internal static void AddOperations(EdmModel model, EdmEntityContainer container, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        var core = EdmCoreModel.Instance;
        var crmBase = (IEdmEntityType)(model.FindDeclaredType($"{Ns}.crmbaseentity")
                                       ?? throw new InvalidOperationException("crmbaseentity must be declared before the duplicate detection operations."));
        var paging = (IEdmComplexType)(model.FindDeclaredType($"{Ns}.PagingInfo")
                                       ?? throw new InvalidOperationException("PagingInfo must be declared before the duplicate detection operations."));

        if (entityTypes.TryGetValue(Rule, out var rule) && entityTypes.TryGetValue(AsyncOperation, out var job))
        {
            var publish = new EdmAction(Ns, Publish, new EdmEntityTypeReference(job, false), isBound: true, entitySetPathExpression: null);
            publish.AddParameter("entity", new EdmEntityTypeReference(rule, false));
            model.AddElement(publish);
        }

        var unpublish = new EdmAction(Ns, Unpublish, null, isBound: false, entitySetPathExpression: null);
        unpublish.AddParameter(RuleId, core.GetGuid(false));
        model.AddElement(unpublish);
        container.AddActionImport(unpublish);

        var retrieve = new EdmFunction(Ns, Retrieve,
            new EdmCollectionTypeReference(new EdmCollectionType(new EdmEntityTypeReference(crmBase, false))),
            isBound: false, entitySetPathExpression: null, isComposable: false);
        retrieve.AddParameter("BusinessEntity", new EdmEntityTypeReference(crmBase, false));
        retrieve.AddParameter("MatchingEntityName", core.GetString(false));
        retrieve.AddParameter("PagingInfo", new EdmComplexTypeReference(paging, true));
        model.AddElement(retrieve);
        container.AddFunctionImport(Retrieve, retrieve, null, includeInServiceDocument: false);

        var queryBase = new EdmComplexType(Ns, "QueryBase", null, isAbstract: true, isOpen: false);
        model.AddElement(queryBase);
        var queryExpression = new EdmComplexType(Ns, "QueryExpression", queryBase, isAbstract: false, isOpen: true);
        queryExpression.AddStructuralProperty("EntityName", EdmPrimitiveTypeKind.String, isNullable: true);
        model.AddElement(queryExpression);
        var fetchExpression = new EdmComplexType(Ns, "FetchExpression", queryBase, isAbstract: false, isOpen: false);
        fetchExpression.AddStructuralProperty("Query", EdmPrimitiveTypeKind.String, isNullable: true);
        model.AddElement(fetchExpression);

        var response = new EdmComplexType(Ns, Bulk + "Response");
        response.AddStructuralProperty("JobId", EdmPrimitiveTypeKind.Guid, isNullable: false);
        model.AddElement(response);
        var bulk = new EdmAction(Ns, Bulk, new EdmComplexTypeReference(response, false), isBound: false, entitySetPathExpression: null);
        bulk.AddParameter("Query", new EdmComplexTypeReference(queryBase, true));
        bulk.AddParameter("JobName", core.GetString(false));
        bulk.AddParameter("SendEmailNotification", core.GetBoolean(false));
        bulk.AddParameter("TemplateId", core.GetGuid(false));
        // Learn types the recipients Collection(activityparty); the SDK request takes the users' ids, which xrmverse reads.
        bulk.AddParameter("ToRecipients", new EdmCollectionTypeReference(new EdmCollectionType(core.GetGuid(false))));
        bulk.AddParameter("CCRecipients", new EdmCollectionTypeReference(new EdmCollectionType(core.GetGuid(false))));
        bulk.AddParameter("RecurrencePattern", core.GetString(false));
        bulk.AddParameter("RecurrenceStartTime", core.GetDateTimeOffset(false));
        model.AddElement(bulk);
        container.AddActionImport(bulk);
    }
}
