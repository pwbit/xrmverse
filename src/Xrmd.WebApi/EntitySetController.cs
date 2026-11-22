using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Query.Model;

namespace Xrmd.WebApi;

/// <summary>
/// CRUD on entity sets (mapped on the <c>/api/data/v9.2</c> group; the caller must map to a <c>systemuser</c>).
/// The single path segment is <c>{entityset}</c> or <c>{entityset}({guid})</c>, parsed here; fixed routes
/// (<c>$metadata</c>, <c>EntityDefinitions</c>, functions) take precedence in routing. A GET or POST of a segment that names
/// no entity set goes to <see cref="CustomApiEndpoints.InvokeAsync"/> as a global custom API operation (an unknown one is the
/// same 404); on PATCH and DELETE, an unknown entity set is a 404.
/// <list type="bullet">
/// <item><c>POST {entityset}</c>: 204 with <c>OData-EntityId</c> and <c>Location</c>; with <c>Prefer: return=representation</c>, 201 with
/// the record (honoring <c>$select</c>) and <c>Preference-Applied</c>.</item>
/// <item><c>GET {entityset}?$select&amp;$filter&amp;$orderby&amp;$top&amp;$count</c>: 200 with the collection (see
/// <see cref="ODataQueryParser"/>, <see cref="EntityCollectionJson"/>); pages of <c>Prefer: odata.maxpagesize=N</c>
/// records (default 5000) linked by <c>@odata.nextLink</c> with a <c>$skiptoken</c>.</item>
/// <item><c>GET {entityset}</c> with <c>Prefer: odata.track-changes</c> or <c>$deltatoken</c>, and <c>GET {entityset}/$count?$deltatoken=…</c>:
/// change tracking (plan 2.13), see <see cref="DeltaQuery"/>.</item>
/// <item><c>GET {entityset}?$apply=…</c>: one object per group (see <see cref="ODataQueryParser"/>).</item>
/// <item><c>GET {entityset}?$expand={lookup}($select=…)</c>: each record with the nested record <c>{lookup}</c>; on an activity,
/// <c>$expand={activity}_activity_parties($select=…)</c> adds the array of its <c>activityparty</c> rows (plan 2.11).</item>
/// <item><c>GET {entityset}?fetchXml=…</c>: see <see cref="FetchAsync"/>.</item>
/// <item><c>GET {entityset}({id})?$select=a,b[&amp;$expand=…]</c>: 200 with the record.</item>
/// <item>On both GETs, <c>Prefer: odata.include-annotations="*"</c> (or naming
/// <c>OData.Community.Display.V1.FormattedValue</c>) adds the formatted value annotations and <c>Preference-Applied</c>.</item>
/// <item><c>PATCH {entityset}({id})</c>: 204. On a standard table it is an upsert (#76): a missing row is created with that id;
/// <c>If-Match: *</c> only updates (404 when missing), <c>If-None-Match: *</c> only creates (412 <c>0x80040237</c> when the row
/// exists), both together are a 400; <c>Prefer: return=representation</c> answers 201 when created, 200 when updated.</item>
/// <item>On POST and PATCH, <c>MSCRM.SuppressDuplicateDetection: true|false</c> (any case) becomes the request parameter
/// <c>SuppressDuplicateDetection</c> of the Create or Update (plan 2.15, ruling D1); any other value is a 400. A duplicate is the
/// core's 412 <c>0x80040333</c>. A <c>$batch</c> request (<see cref="BatchRequests"/>) runs these same endpoints.</item>
/// <item><c>DELETE {entityset}({id})</c>: 204.</item>
/// <item>Elastic tables (plan 3.4): the key may be <c>({pk}={id},partitionid='{p}')</c> on GET, PATCH and DELETE; GET (record
/// and collection) and DELETE also take <c>?partitionId={p}</c> (or <c>'{p}'</c>), refused on a standard table and ignored on
/// POST and PATCH (as Dataverse does); a PATCH whose key names no partition finds the row in the partition of the body's
/// <c>partitionid</c>, and a body <c>partitionid</c> other than the row's is refused. The match is
/// exact (case-sensitive). PATCH stays update-only (404 for a missing row; <c>If-None-Match</c> is refused). POST and PATCH
/// answer the <c>x-ms-session-token</c> header of the write (DELETE none); <c>MSCRM.SessionToken</c> is accepted and ignored.
/// A POST with nested related records (deep insert) is a 400. The bulk actions are in <see cref="ElasticEndpoints"/>.</item>
/// </list>
/// </summary>
public static class EntitySetController
{
    private const string ReturnRepresentation = "return=representation";
    private const string IncludeAnnotations = "odata.include-annotations";
    private const string MaxPageSize = "odata.maxpagesize";
    private const string FormattedValueTerm = "OData.Community.Display.V1.FormattedValue";
    private const string SkipToken = "$skiptoken";
    private const string PagingCookieAnnotation = "@Microsoft.Dynamics.CRM.fetchxmlpagingcookie";
    private const string MoreRecordsAnnotation = "@Microsoft.Dynamics.CRM.morerecords";

    /// <summary>The Web API header that asks for (false) or suppresses (true) the duplicate check of a write (plan 2.15, D1).</summary>
    public const string SuppressDuplicateDetectionHeader = "MSCRM.SuppressDuplicateDetection";

    public static IEndpointRouteBuilder MapEntitySetEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapGet("/{segment}", GetAsync);
        routes.MapGet("/{segment}/$count", CountAsync);
        routes.MapPost("/{segment}", PostAsync).InChangeSet();
        routes.MapPatch("/{segment}", PatchAsync).InChangeSet();
        routes.MapDelete("/{segment}", DeleteAsync).InChangeSet();
        return parent;
    }

    private static async Task<IResult> GetAsync(HttpContext context, IMetadataStore store, OrganizationService service,
        ODataQueryParser parser, CustomApiRegistry customApis, string segment)
    {
        // A segment that names no entity set is a global custom API function, or the 404 of an unknown segment.
        if (CustomApiEndpoints.IsGlobalOperation(store, segment))
            return await CustomApiEndpoints.InvokeAsync(context, service, store, customApis, null, segment, isGet: true);
        var (m, id, keyPartition) = Resolve(store, segment);
        UnmaskedData.RefuseQueryOptions(context.Request.Query.Select(q => KeyValuePair.Create(q.Key, q.Value.ToString())));
        var partition = PartitionOf(m, keyPartition, ElasticEndpoints.PartitionIdOf(context.Request));
        var preferences = ReadPreferences(context.Request);
        if (id is null && DeltaQuery.IsDelta(context.Request, preferences.TrackChanges))
        {
            if (partition is not null)
                throw XrmdFaultException.InvalidArgument($"The query parameter {ElasticRecordKey.PartitionIdParameter} cannot be combined with change tracking.");
            return await DeltaQuery.QueryAsync(context, service, store, parser, m, preferences.TrackChanges, preferences.Annotations,
                preferences.MaxPageSize);
        }
        if (id is not { } key)
            return ODataQueryParser.IsFetchXml(context.Request.Query)
                ? await FetchAsync(context, service, parser, m, preferences, partition)
                : await QueryAsync(context, service, store, parser, m, preferences, partition);

        if (ODataQueryParser.IsFetchXml(context.Request.Query))
            throw XrmdFaultException.InvalidArgument($"fetchXml queries the entity set: use '{m.EntitySetName}?fetchXml=…' without a key.");
        if (preferences.Annotations is { } annotations) context.Response.Headers["Preference-Applied"] = annotations;
        if (context.Request.Query.Keys.Any(k => string.Equals(k, "$expand", StringComparison.OrdinalIgnoreCase)))
            return await RetrieveExpandedAsync(context, service, store, parser, m, key, preferences.Annotations is not null, partition);
        return await RetrieveResultAsync(context, service, m, key, StatusCodes.Status200OK, preferences.Annotations is not null, partition);
    }

    /// <summary><c>GET {entityset}/$count</c>: the number of changes since a <c>$deltatoken</c> (plan 2.13, see <see cref="DeltaQuery"/>).</summary>
    private static async Task<IResult> CountAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment)
    {
        var (m, id, _) = Resolve(store, segment);
        if (id is not null) throw XrmdFaultException.InvalidArgument($"$count counts an entity set: use '{m.EntitySetName}/$count' without a key.");
        UnmaskedData.RefuseQueryOptions(context.Request.Query.Select(q => KeyValuePair.Create(q.Key, q.Value.ToString())));
        return await DeltaQuery.CountResultAsync(context, service, store, m);
    }

    /// <summary><c>GET {entityset}</c>: one page of the query, with <c>@odata.nextLink</c> when more records exist.</summary>
    private static async Task<IResult> QueryAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        ODataQueryParser parser, EntityMetadata m, Preferences preferences, string? partition)
    {
        var request = context.Request;
        var model = WithPartition(parser.Parse(m, request.Query, preferences.MaxPageSize, request.Query[SkipToken].ToString(), partyExpand: true), partition);
        var partyExpand = parser.ReadPartyExpand(m, request.Query);
        var result = await RunWithVersionAsync(service, model, m, context.RequestAborted);

        var nextLink = result.MoreRecords && model.Top is null
            ? NextLink(request, ODataQueryParser.FormatSkipToken(model.PageNumber + 1, result.PagingCookie))
            : null;
        var applied = new List<string>();
        if ((preferences.Annotations ?? preferences.CountAnnotations) is { } annotations) applied.Add(annotations);
        if (preferences.MaxPageSize is { } size) applied.Add($"{MaxPageSize}={size.ToString(CultureInfo.InvariantCulture)}");
        if (applied.Count > 0) context.Response.Headers["Preference-Applied"] = string.Join(",", applied);

        var json = EntityCollectionJson.ToJson(result, m, ODataJson.BaseUrl(request), ContextList(request),
            preferences.Annotations is not null, nextLink, Expands(store, model), store.GetOneToManyRelationships(),
            await UnreadableAsync(service, model, result.Entities, context.RequestAborted),
            totalCountAnnotations: preferences.CountAnnotations is not null);
        if (partyExpand is not null)
        {
            var records = json["value"]!.AsArray();
            await WritePartiesAsync(service, store, partyExpand, result.Entities, i => records[i]!.AsObject(),
                preferences.Annotations is not null, context.RequestAborted);
        }
        return ODataJson.Result(context, json);
    }

    /// <summary>
    /// <c>GET {entityset}?fetchXml=…</c>: the FetchXML page with <c>@Microsoft.Dynamics.CRM.fetchxmlpagingcookie</c>
    /// (<c>&lt;cookie pagenumber="N" pagingcookie="{cookie URL-encoded twice}" istracking="False" /&gt;</c>, N the page
    /// returned) and <c>@Microsoft.Dynamics.CRM.morerecords</c> (true) only when more records exist (Dataverse omits both
    /// on the last page); no <c>@odata.nextLink</c>. Link columns and aggregates are written under their alias.
    /// </summary>
    private static async Task<IResult> FetchAsync(
        HttpContext context, OrganizationService service, ODataQueryParser parser, EntityMetadata m, Preferences preferences, string? partition)
    {
        var model = WithPartition(parser.ParseFetchXml(m, context.Request.Query), partition);
        var result = await service.RetrieveMultipleAsync(model, context.RequestAborted);
        if ((preferences.Annotations ?? preferences.CountAnnotations) is { } annotations) context.Response.Headers["Preference-Applied"] = annotations;

        var json = EntityCollectionJson.ToJson(result, m, ODataJson.BaseUrl(context.Request), [], preferences.Annotations is not null, null,
            relationships: Relationships(context), unreadable: await UnreadableAsync(service, model, result.Entities, context.RequestAborted),
            totalCountAnnotations: preferences.CountAnnotations is not null);
        if (result.MoreRecords && !string.IsNullOrEmpty(result.PagingCookie))
            json[PagingCookieAnnotation] = string.Create(CultureInfo.InvariantCulture,
                $"<cookie pagenumber=\"{model.PageNumber}\" pagingcookie=\"{Uri.EscapeDataString(Uri.EscapeDataString(result.PagingCookie))}\" istracking=\"False\" />");
        if (result.MoreRecords) json[MoreRecordsAnnotation] = true;
        return ODataJson.Result(context, json);
    }

    /// <summary><c>GET {entityset}({id})?$expand=…</c>: the record through a query on its key; 404 when missing.</summary>
    private static async Task<IResult> RetrieveExpandedAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        ODataQueryParser parser, EntityMetadata m, Guid id, bool includeFormattedValues, string? partition)
    {
        var model = WithPartition(parser.ParseSingle(m, context.Request.Query, id, partyExpand: true), partition);
        var partyExpand = parser.ReadPartyExpand(m, context.Request.Query);
        var result = await RunWithVersionAsync(service, model, m, context.RequestAborted);
        var entity = result.Entities.Count > 0 ? result.Entities[0] : throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
        var json = ODataJson.ToJson(entity, m, ODataJson.BaseUrl(context.Request), ContextList(context.Request),
            includeFormattedValues, Expands(store, model), store.GetOneToManyRelationships(),
            await UnreadableAsync(service, model, [entity], context.RequestAborted));
        if (partyExpand is not null)
            await WritePartiesAsync(service, store, partyExpand, [entity], _ => json, includeFormattedValues, context.RequestAborted);
        return ODataJson.Result(context, json);
    }

    /// <summary>Ids read per party query: the <c>in</c> condition stays far below SQL Server's 2100 parameters.</summary>
    private const int PartyChunk = 500;

    /// <summary>
    /// <c>$expand={activity}_activity_parties</c> (plan 2.11, A8, A14): the <c>activityparty</c> rows of the records of the page,
    /// read as the caller in one query per chunk of ids (all its pages), so the caller's read restriction on party rows applies
    /// (pre-flight C13), ordered by participation type then creation; each record gets the array of its rows (empty when none),
    /// each row written as a record of <c>activityparty</c> with the expand's <c>$select</c>.
    /// </summary>
    private static async Task WritePartiesAsync(OrganizationService service, IMetadataStore store, PartyExpand expand,
        IReadOnlyList<Microsoft.Xrm.Sdk.Entity> records, Func<int, System.Text.Json.Nodes.JsonObject> json, bool includeFormattedValues,
        CancellationToken ct)
    {
        var party = store.GetEntity(ActivitySchema.Party) ?? throw XrmdFaultException.InvalidArgument($"The table '{ActivitySchema.Party}' does not exist.");
        var byActivity = new Dictionary<Guid, List<Microsoft.Xrm.Sdk.Entity>>();
        var ids = records.Select(r => r.Id).Where(id => id != Guid.Empty).Distinct().ToList();
        foreach (var chunk in ids.Chunk(PartyChunk))
        {
            for (var page = 1; ; page++)
            {
                var root = new EntityNode { EntityName = party.LogicalName, AllColumns = expand.Columns is null };
                foreach (var column in expand.Columns ?? []) root.Columns.Add(new ColumnNode(column));
                if (expand.Columns is not null && !expand.Columns.Contains(ActivitySchema.ActivityId))
                    root.Columns.Add(new ColumnNode(ActivitySchema.ActivityId)); // to group the rows; not written unless selected
                root.Filter.Conditions.Add(new ConditionNode(null, ActivitySchema.ActivityId, ConditionOperator.In, [.. chunk.Cast<object?>()]));
                root.Orders.Add(new OrderNode(ActivitySchema.ParticipationTypeMask, false));
                root.Orders.Add(new OrderNode("createdon", false));
                // The parties are ordered by their participation type's value (sender, to, cc, …), not by its label.
                var rows = await service.RetrieveMultipleAsync(new QueryModel { Root = root, PageNumber = page, UseRawOrderBy = true }, ct);
                foreach (var row in rows.Entities)
                {
                    if (row.GetAttributeValue<Microsoft.Xrm.Sdk.EntityReference>(ActivitySchema.ActivityId)?.Id is not { } activity) continue;
                    if (expand.Columns is not null && !expand.Columns.Contains(ActivitySchema.ActivityId))
                    {
                        row.Attributes.Remove(ActivitySchema.ActivityId);
                        row.FormattedValues.Remove(ActivitySchema.ActivityId);
                    }
                    if (!byActivity.TryGetValue(activity, out var list)) byActivity[activity] = list = [];
                    list.Add(row);
                }
                if (!rows.MoreRecords) break;
            }
        }
        var relationships = store.GetOneToManyRelationships();
        for (var i = 0; i < records.Count; i++)
        {
            var array = new System.Text.Json.Nodes.JsonArray();
            foreach (var row in byActivity.GetValueOrDefault(records[i].Id) ?? [])
                array.Add(ODataJson.ToRecordJson(row, party, includeFormattedValues, relationships: relationships));
            json(i)[expand.Navigation] = array;
        }
    }

    /// <summary>
    /// Runs the query; for a non-aggregate query with explicit root columns, also reads <c>versionnumber</c>, which feeds
    /// <c>@odata.etag</c> (<see cref="Microsoft.Xrm.Sdk.Entity.RowVersion"/>) without being written as a property.
    /// </summary>
    private static async Task<Microsoft.Xrm.Sdk.EntityCollection> RunWithVersionAsync(
        OrganizationService service, QueryModel model, EntityMetadata m, CancellationToken ct)
    {
        var addVersion = !model.Aggregate && !model.Root.AllColumns && model.Root.Columns.All(c => c.Attribute != TypeMapping.VersionNumber)
                         && m.Attributes?.Any(a => a.LogicalName == TypeMapping.VersionNumber) == true;
        if (addVersion) model.Root.Columns.Add(new ColumnNode(TypeMapping.VersionNumber));

        var result = await service.RetrieveMultipleAsync(model, ct);
        if (addVersion)
            foreach (var e in result.Entities)
            {
                if (e.Attributes.TryGetValue(TypeMapping.VersionNumber, out var v) && v is long version)
                    e.RowVersion = version.ToString(CultureInfo.InvariantCulture);
                e.Attributes.Remove(TypeMapping.VersionNumber);
                e.FormattedValues.Remove(TypeMapping.VersionNumber);
            }
        return result;
    }

    /// <summary>
    /// The secured columns the caller cannot read on the tables of <paramref name="model"/> (the root and its links), which the
    /// JSON omits when the organization service returned them as null (ruling F3); not looked up when no value is null.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>?> UnreadableAsync(OrganizationService service, QueryModel model,
        IEnumerable<Microsoft.Xrm.Sdk.Entity> entities, CancellationToken ct)
    {
        if (!HasNull(entities)) return null;
        var tables = new List<string>();
        void Walk(EntityNode node)
        {
            tables.Add(node.EntityName);
            foreach (var link in node.Links) Walk(link);
        }
        Walk(model.Root);
        return await service.UnreadableColumnsAsync(tables, ct);
    }

    /// <summary>True when a record holds a null value, plain or aliased (only a secured column comes back so from a query).</summary>
    private static bool HasNull(IEnumerable<Microsoft.Xrm.Sdk.Entity> entities) =>
        entities.Any(e => e.Attributes.Values.Any(v => v is null or Microsoft.Xrm.Sdk.AliasedValue { Value: null }));

    /// <summary>The <c>$expand</c> links, by navigation property, with their nested ones.</summary>
    private static IReadOnlyDictionary<string, ODataExpand>? Expands(IMetadataStore store, QueryModel model) => ODataExpand.From(store, model);

    /// <summary>
    /// The <c>$select</c> items followed by the <c>$expand</c> items, echoed in <c>@odata.context</c> as Dataverse does
    /// (<c>name,lookup(a,b)</c>: the nested <c>$select=</c> is dropped; a nested <c>$expand</c> follows its <c>$select</c>
    /// items, <c>lookup(a,b,child(c))</c>, issue #190).
    /// </summary>
    private static string[] ContextList(HttpRequest request)
    {
        var expand = request.Query["$expand"].ToString();
        if (expand.Length == 0) return SelectList(request);
        return [.. SelectList(request), expand
            .Replace("($select=", "(", StringComparison.OrdinalIgnoreCase)
            .Replace("($expand=", "(", StringComparison.OrdinalIgnoreCase)
            .Replace(";$expand=", ",", StringComparison.OrdinalIgnoreCase)];
    }

    /// <summary>The request URL without <c>$skiptoken</c>, then <c>$skiptoken=</c> and the URL-encoded token.</summary>
    private static string NextLink(HttpRequest request, string skipToken)
    {
        var parts = (request.QueryString.Value ?? "").TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.Equals(Uri.UnescapeDataString(p.Split('=', 2)[0]), SkipToken, StringComparison.OrdinalIgnoreCase))
            .Append($"{SkipToken}={Uri.EscapeDataString(skipToken)}");
        return $"{ODataJson.BaseUrl(request)}{request.Path}?{string.Join('&', parts)}";
    }

    private static async Task<IResult> PostAsync(HttpContext context, IMetadataStore store, OrganizationService service,
        CustomApiRegistry customApis, string segment)
    {
        // A segment that names no entity set is a global custom API action, or the 404 of an unknown segment.
        if (CustomApiEndpoints.IsGlobalOperation(store, segment))
            return await CustomApiEndpoints.InvokeAsync(context, service, store, customApis, null, segment, isGet: false);
        var (m, key, _) = Resolve(store, segment);
        if (key is not null) throw XrmdFaultException.InvalidArgument($"POST creates a record: use '{m.EntitySetName}' without a key.");

        var suppress = SuppressDuplicateDetection(context.Request);
        var body = await ODataJson.ReadBodyAsync(context.Request);
        ElasticEndpoints.DemandNoDeepInsert(m, body);
        var entity = ODataJson.FromJson(body, m, store);
        var create = new CreateRequest { Target = entity };
        // As in Dataverse, the MSCRM.SolutionUniqueName header adds a component (web resource, view, ...) to that solution.
        var solution = context.Request.Headers["MSCRM.SolutionUniqueName"].ToString();
        if (solution.Length > 0) create["SolutionUniqueName"] = solution;
        if (suppress is { } flag) create[Core.Duplicates.DuplicateFinder.SuppressParameter] = flag;
        var created = await service.ExecuteAsync(create, context.RequestAborted);
        var id = (Guid)created.Results["id"];
        ElasticEndpoints.SetSessionToken(context, created);
        // Both headers carry the record URL: OData clients read OData-EntityId, ServiceClient reads Location.
        var entityUrl = $"{MetadataController.ServiceRoot(context.Request)}{m.EntitySetName}({id})";
        context.Response.Headers["OData-EntityId"] = entityUrl;
        context.Response.Headers.Location = entityUrl;

        if (!PrefersRepresentation(context.Request))
        {
            context.Response.Headers["OData-Version"] = "4.0";
            return Results.NoContent();
        }
        context.Response.Headers["Preference-Applied"] = ReturnRepresentation;
        return await RetrieveResultAsync(context, service, m, id, StatusCodes.Status201Created,
            partition: ElasticTables.IsElastic(m) ? entity.GetAttributeValue<string>(ElasticTables.PartitionId) : null);
    }

    /// <summary>
    /// <c>PATCH {entityset}(id)</c>. On a standard table without <c>If-Match</c> it is an upsert (#76, ruling B11): the row is
    /// created with that id when missing; <c>If-Match: *</c> only updates (404 when missing), <c>If-None-Match: *</c> only creates
    /// (412 DuplicateRecord when the row exists, raised inside the Upsert's transaction), both together are a 400. With
    /// <c>Prefer: return=representation</c> the answer is 201 when created and 200 when updated. An elastic table keeps PATCH as
    /// update only (an elastic Upsert replaces the row, so a PATCH must not silently become one).
    /// </summary>
    private static async Task<IResult> PatchAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment)
    {
        var (m, id, keyPartition) = Resolve(store, segment);
        var key = id ?? throw XrmdFaultException.InvalidArgument($"PATCH requires a key: '{m.EntitySetName}(<id>)'.");
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        if (ifMatch.Length > 0 && ifMatch.Trim() != "*")
            throw XrmdFaultException.InvalidArgument("If-Match with an ETag is not supported yet: use If-Match: * or omit it.");
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (ifNoneMatch.Length > 0 && ifNoneMatch.Trim() != "*")
            throw XrmdFaultException.InvalidArgument("If-None-Match with an ETag is not supported: use If-None-Match: * or omit it.");
        if (ifMatch.Length > 0 && ifNoneMatch.Length > 0)
            throw XrmdFaultException.InvalidArgument("If-Match and If-None-Match cannot be combined on the same request.");

        var suppress = SuppressDuplicateDetection(context.Request);
        var entity = ODataJson.FromJson(await ODataJson.ReadBodyAsync(context.Request), m, store);
        var elastic = ElasticTables.IsElastic(m);
        if (!elastic && entity.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(m), out var bodyKey) && bodyKey is Guid g && g != key)
            throw XrmdFaultException.InvalidArgument("The primary key in the body does not match the URL.");
        entity.Id = key;
        if (elastic && ifNoneMatch.Length > 0)
            throw XrmdFaultException.InvalidArgument("If-None-Match: * is not supported on an elastic table: PATCH only updates there.");
        // Plan 3.4: the row of an elastic table is found in the partition of the key, else (in the core) of the body's partitionid;
        // a body partitionid that differs from the row's is refused there, never moved.
        if (elastic && keyPartition is not null)
            entity.KeyAttributes = ElasticEndpoints.Key(SystemAttributes.PrimaryIdName(m), key, keyPartition);

        var created = false;
        if (!elastic && ifMatch.Length == 0)
        {
            created = (bool)(await service.UpsertAsync(entity, ifNoneMatch.Length > 0, suppress, context.RequestAborted)).Results["RecordCreated"];
        }
        else
        {
            var update = new UpdateRequest { Target = entity };
            if (suppress is { } flag) update[Core.Duplicates.DuplicateFinder.SuppressParameter] = flag;
            ElasticEndpoints.SetSessionToken(context, await service.ExecuteAsync(update, context.RequestAborted));
        }
        context.Response.Headers["OData-Version"] = "4.0";
        if (elastic || !PrefersRepresentation(context.Request)) return Results.NoContent();
        context.Response.Headers["Preference-Applied"] = ReturnRepresentation;
        return await RetrieveResultAsync(context, service, m, key, created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment)
    {
        var (m, id, keyPartition) = Resolve(store, segment);
        var key = id ?? throw XrmdFaultException.InvalidArgument($"DELETE requires a key: '{m.EntitySetName}(<id>)'.");
        var partition = PartitionOf(m, keyPartition, ElasticEndpoints.PartitionIdOf(context.Request));
        if (ElasticTables.IsElastic(m))
            await service.ExecuteAsync(new DeleteRequest { Target = Reference(m, key, partition) }, context.RequestAborted); // plan 3.4
        else
            await service.DeleteAsync(m.LogicalName, key, context.RequestAborted);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.NoContent();
    }

    /// <summary>
    /// The <c>MSCRM.SuppressDuplicateDetection</c> header of a write: null when absent (the core then suppresses the check, as
    /// Dataverse does by default), else <c>true</c> or <c>false</c> in any case.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument for any other value (repeated headers included).</exception>
    private static bool? SuppressDuplicateDetection(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(SuppressDuplicateDetectionHeader, out var values)) return null;
        var text = values.Count == 1 ? values[0]?.Trim() : null;
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return false;
        throw XrmdFaultException.InvalidArgument(
            $"Invalid {SuppressDuplicateDetectionHeader} header value '{values}': expected 'true' or 'false'.");
    }

    /// <summary>
    /// The entity of <c>{entityset}</c>, <c>{entityset}({guid})</c> or, for an elastic table (plan 3.4),
    /// <c>{entityset}({pk}={guid},partitionid='{p}')</c>, with the key when present and the partition the key names (null
    /// when it names none).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 ResourceNotFound for an unknown entity set; InvalidArgument for a key that
    /// is not a GUID on a standard table (alternate keys are not supported), or an invalid elastic key
    /// (<see cref="ParseElasticKey"/>).</exception>
    public static (EntityMetadata Entity, Guid? Id, string? PartitionId) Resolve(IMetadataStore store, string segment)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(segment);
        var hasKey = ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText);
        var m = store.GetEntityBySetName(setName)
                ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (!hasKey) return (m, null, null);
        if (Guid.TryParseExact(keyText, "D", out var id)) return (m, id, null);
        if (!ElasticTables.IsElastic(m))
            throw XrmdFaultException.InvalidArgument(
                $"Invalid key '{keyText}' for entity set '{setName}': only the primary key (a GUID) is supported.");
        var (elasticId, partition) = ParseElasticKey(m, setName, keyText);
        return (m, elasticId, partition);
    }

    /// <summary>
    /// The key of an elastic row written as <c>{pk}={guid},partitionid='{p}'</c> (either order; the names in any case; the
    /// partition single-quoted, a quote inside it doubled), or <c>{pk}={guid}</c> alone (a row stored without partition).
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument naming the problem: another key name, a name given twice, no primary
    /// key, a primary key that is not a GUID, a partition that is not a quoted string or not valid.</exception>
    internal static (Guid Id, string? PartitionId) ParseElasticKey(EntityMetadata m, string setName, string keyText)
    {
        var pk = SystemAttributes.PrimaryIdName(m);
        Guid? id = null;
        string? partition = null;
        var hasPartition = false;
        XrmdFaultException Invalid(string problem) =>
            XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': {problem}");

        var i = 0;
        while (i < keyText.Length)
        {
            var eq = keyText.IndexOf('=', i);
            if (eq < 0) throw Invalid($"expected {pk}=<guid>,{ElasticTables.PartitionId}='<value>'.");
            var name = keyText[i..eq].Trim();
            string value;
            var quoted = false;
            var at = eq + 1;
            if (at < keyText.Length && keyText[at] == '\'')
            {
                quoted = true;
                var text = new System.Text.StringBuilder();
                at++;
                var closed = false;
                while (at < keyText.Length)
                {
                    if (keyText[at] == '\'')
                    {
                        if (at + 1 < keyText.Length && keyText[at + 1] == '\'')
                        {
                            text.Append('\'');
                            at += 2;
                            continue;
                        }
                        closed = true;
                        at++;
                        break;
                    }
                    text.Append(keyText[at++]);
                }
                if (!closed) throw Invalid($"the value of '{name}' has no closing quote.");
                value = text.ToString();
                if (at < keyText.Length && keyText[at] != ',') throw Invalid($"expected ',' after the value of '{name}'.");
            }
            else
            {
                var comma = keyText.IndexOf(',', at);
                value = (comma < 0 ? keyText[at..] : keyText[at..comma]).Trim();
                at = comma < 0 ? keyText.Length : comma;
            }
            i = at + 1;

            if (string.Equals(name, pk, StringComparison.OrdinalIgnoreCase))
            {
                if (id is not null) throw Invalid($"the key '{pk}' is given twice.");
                id = !quoted && Guid.TryParseExact(value, "D", out var parsed)
                    ? parsed
                    : throw Invalid($"the key '{pk}' must be a GUID (not quoted).");
            }
            else if (string.Equals(name, ElasticTables.PartitionId, StringComparison.OrdinalIgnoreCase))
            {
                if (hasPartition) throw Invalid($"the key '{ElasticTables.PartitionId}' is given twice.");
                if (!quoted) throw Invalid($"the key '{ElasticTables.PartitionId}' must be a string in single quotes.");
                ElasticTables.DemandValidPartitionId(value);
                partition = value;
                hasPartition = true;
            }
            else
                throw Invalid($"'{name}' is not a key of the elastic table '{m.LogicalName}': its key is {pk} and {ElasticTables.PartitionId}.");
        }
        return id is { } key ? (key, partition) : throw Invalid($"the key '{pk}' is missing.");
    }

    /// <summary>
    /// The partition a GET or DELETE names (plan 3.4): the one of the key, else the <c>partitionId</c> query parameter; null when
    /// neither names one.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for <c>partitionId</c> on a standard table, or a partition given both ways.</exception>
    private static string? PartitionOf(EntityMetadata m, string? keyPartition, string? parameter)
    {
        if (parameter is null) return keyPartition;
        if (!ElasticTables.IsElastic(m))
            throw XrmdFaultException.InvalidArgument(
                $"The partitionId parameter applies to elastic tables only; '{m.LogicalName}' is not an elastic table.");
        if (keyPartition is not null)
            throw XrmdFaultException.InvalidArgument(
                $"The partition is given twice, by the key and by the query parameter {ElasticRecordKey.PartitionIdParameter}: give it once.");
        return parameter;
    }

    /// <summary><paramref name="q"/> limited to the rows of <paramref name="partition"/> (exactly); <paramref name="q"/> when null.</summary>
    private static QueryModel WithPartition(QueryModel q, string? partition) =>
        partition is null ? q : ElasticQueryRules.WithPartition(q, partition);

    /// <summary>The reference to the row <paramref name="id"/>, by key when a partition is named.</summary>
    private static EntityReference Reference(EntityMetadata m, Guid id, string? partition) =>
        partition is null
            ? new EntityReference(m.LogicalName, id)
            : new EntityReference(m.LogicalName, ElasticEndpoints.Key(SystemAttributes.PrimaryIdName(m), id, partition)) { Id = id };

    private static IReadOnlyList<OneToManyRelationshipMetadata> Relationships(HttpContext context) =>
        context.RequestServices.GetRequiredService<IMetadataStore>().GetOneToManyRelationships();

    /// <summary>Retrieves the record with the <c>$select</c> columns (all when absent) and writes it as Web API JSON.</summary>
    internal static async Task<IResult> RetrieveResultAsync(
        HttpContext context, OrganizationService service, EntityMetadata m, Guid id, int status, bool includeFormattedValues = false,
        string? partition = null)
    {
        var select = SelectList(context.Request);
        foreach (var name in select.Select(AttributeName))
            if (WebApiHiddenAttributes.IsHidden(m.LogicalName, name))
                throw XrmdFaultException.InvalidArgument(WebApiHiddenAttributes.AuditColumnMessage(m.LogicalName, name));
            else if (m.Attributes?.FirstOrDefault(a => a.LogicalName == name) is { } selected && ActivitySchema.IsPartyList(selected))
                throw XrmdFaultException.InvalidArgument(
                    $"Could not find a property named '{name}' on type '{ODataEdmModelBuilder.Namespace}.{m.LogicalName}': a party list is read with " +
                    $"$expand={m.LogicalName}_activity_parties.");
        var columns = select.Length == 0 ? (WebApiHiddenAttributes.VisibleColumns(m) is { } visible ? new ColumnSet(visible.ToArray()) : new ColumnSet(true)) : new ColumnSet(select.Select(AttributeName).Distinct().ToArray());
        // The version feeds @odata.etag even when not selected.
        var addVersion = !columns.AllColumns && !columns.Columns.Contains(TypeMapping.VersionNumber)
                         && m.Attributes?.Any(a => a.LogicalName == TypeMapping.VersionNumber) == true;
        if (addVersion) columns.AddColumn(TypeMapping.VersionNumber);

        // Plan 3.4: a row of an elastic table is read in its partition (none: a row stored without one).
        var entity = ElasticTables.IsElastic(m)
            ? (Entity)(await service.ExecuteAsync(new RetrieveRequest { Target = Reference(m, id, partition), ColumnSet = columns },
                context.RequestAborted)).Results["Entity"]
            : await service.RetrieveAsync(m.LogicalName, id, columns, context.RequestAborted);
        if (addVersion) entity.Attributes.Remove(TypeMapping.VersionNumber);
        var unreadable = HasNull([entity]) ? await service.UnreadableColumnsAsync([m.LogicalName], context.RequestAborted) : null;
        return ODataJson.Result(context, ODataJson.ToJson(entity, m, ODataJson.BaseUrl(context.Request), select, includeFormattedValues,
            relationships: Relationships(context), unreadable: unreadable), status);
    }

    /// <summary>The <c>$select</c> items as written by the client (echoed in <c>@odata.context</c>).</summary>
    private static string[] SelectList(HttpRequest request) => request.Query["$select"].ToString()
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A <c>$select</c> item names an attribute, or a lookup by its <c>_{name}_value</c> property.</summary>
    private static string AttributeName(string property) =>
        property.Length > "__value".Length && property.StartsWith('_') && property.EndsWith("_value", StringComparison.Ordinal)
            ? property[1..^"_value".Length]
            : property;

    private static bool PrefersRepresentation(HttpRequest request) =>
        request.Headers["Prefer"].SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries))
            .Any(p => string.Equals(p, ReturnRepresentation, StringComparison.OrdinalIgnoreCase));

    /// <param name="Annotations">The <c>odata.include-annotations</c> preference as sent, when it asks for formatted values.</param>
    /// <param name="MaxPageSize">The <c>odata.maxpagesize</c> preference.</param>
    /// <param name="TrackChanges">The <c>odata.track-changes</c> preference (plan 2.13, see <see cref="DeltaQuery"/>).</param>
    /// <param name="CountAnnotations">The <c>odata.include-annotations</c> preference as sent, when it asks for
    /// <c>Microsoft.Dynamics.CRM.totalrecordcount</c> or <c>totalrecordcountlimitexceeded</c> (directly, by
    /// <c>Microsoft.Dynamics.CRM.*</c> or by <c>*</c>).</param>
    private sealed record Preferences(string? Annotations, int? MaxPageSize, bool TrackChanges, string? CountAnnotations = null);

    private static readonly string[] CountAnnotationTerms =
        ["*", "Microsoft.Dynamics.CRM.*", "Microsoft.Dynamics.CRM.totalrecordcount", "Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded"];

    /// <summary>
    /// The <c>odata.include-annotations</c> preference of <paramref name="request"/> as sent, when it asks for formatted values;
    /// null otherwise (for the operations that return records, such as the audit functions).
    /// </summary>
    internal static string? FormattedValuesPreference(HttpRequest request) => ReadPreferences(request).Annotations;

    /// <summary>Reads the <c>Prefer</c> preferences of a GET (comma-separated, commas inside quotes kept).</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument for an <c>odata.maxpagesize</c> that is not an integer.</exception>
    private static Preferences ReadPreferences(HttpRequest request)
    {
        string? annotations = null;
        string? countAnnotations = null;
        int? maxPageSize = null;
        var trackChanges = false;
        foreach (var preference in request.Headers["Prefer"].SelectMany(v => SplitPreferences(v ?? "")))
        {
            var eq = preference.IndexOf('=');
            if (eq < 0)
            {
                trackChanges |= string.Equals(preference.Trim(), DeltaQuery.TrackChanges, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var name = preference[..eq].Trim();
            var value = preference[(eq + 1)..].Trim();
            if (string.Equals(name, IncludeAnnotations, StringComparison.OrdinalIgnoreCase))
            {
                var terms = value.Trim('"').Split(',', StringSplitOptions.TrimEntries);
                if (terms.Any(t => t is "*" or "OData.Community.Display.V1.*" || string.Equals(t, FormattedValueTerm, StringComparison.Ordinal)))
                    annotations = preference.Trim();
                if (terms.Any(t => CountAnnotationTerms.Contains(t, StringComparer.Ordinal)))
                    countAnnotations = preference.Trim();
            }
            else if (string.Equals(name, MaxPageSize, StringComparison.OrdinalIgnoreCase))
            {
                maxPageSize = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                    ? size
                    : throw XrmdFaultException.InvalidArgument($"Invalid {MaxPageSize} preference '{value}': expected a positive integer.");
            }
        }
        return new Preferences(annotations, maxPageSize, trackChanges, countAnnotations);
    }

    /// <summary>The preferences of one <c>Prefer</c> header value (comma-separated, commas inside quotes kept).</summary>
    internal static IEnumerable<string> SplitPreferences(string header)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] == '"') quoted = !quoted;
            else if (header[i] == ',' && !quoted)
            {
                if (header[start..i].Trim() is { Length: > 0 } p) yield return p;
                start = i + 1;
            }
        }
        if (header[start..].Trim() is { Length: > 0 } last) yield return last;
    }
}
