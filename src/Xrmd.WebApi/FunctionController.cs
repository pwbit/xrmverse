using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// Unbound Web API functions (mapped on the <c>/api/data/v9.2</c> group; the caller must map to a <c>systemuser</c>):
/// <list type="bullet">
/// <item><c>GET WhoAmI</c> → <c>{ BusinessUnitId, UserId, OrganizationId }</c>.</item>
/// <item>bound to systemuser: <c>GET systemusers({id})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()</c> → <c>{ RolePrivileges }</c>.</item>
/// <item><c>GET RetrieveCurrentOrganization(AccessType=@p)?@p=Microsoft.Dynamics.CRM.EndpointAccessType'Default'</c>
/// (or the value inline) → <c>{ Detail: OrganizationDetail }</c>, endpoints relative to the request's base URL.</item>
/// <item><c>GET LocalTimeFromUtcTime(TimeZoneCode=…,UtcTime=…)</c> (values inline or as aliases) → <c>{ LocalTime }</c> (#131).</item>
/// </list>
/// Response properties are PascalCase, as Dataverse writes them.
/// </summary>
public static partial class FunctionController
{
    public const string OrganizationVersion = "9.2.0.0";
    private const string Geo = "on-prem";
    private static readonly string[] AccessTypes = ["Default", "Internet", "Intranet"];

    public static IEndpointRouteBuilder MapFunctionEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapGet("/WhoAmI", WhoAmIAsync);
        routes.MapGet("/RetrieveCurrentOrganization({parameters})", RetrieveCurrentOrganizationAsync);
        // #131 (ruling Z5): Learn lists no Web API operation for UtcTimeFromLocalTime, which stays an SDK message.
        routes.MapGet("/" + TimeZoneMessages.LocalTimeFromUtcTime + "({parameters})", LocalTimeFromUtcTimeAsync);
        // Bound to systemuser; these literal routes rank above the custom API bound route (an operation segment) and the file route.
        routes.MapGet("/{segment}/" + ODataEdmModelBuilder.Namespace + "." + RetrieveUserPrivileges + "()", RetrieveUserPrivilegesAsync);
        routes.MapGet("/{segment}/" + ODataEdmModelBuilder.Namespace + "." + RetrieveUserPrivileges, RetrieveUserPrivilegesAsync);
        // Plan 3.5 UI-1b: the app's components, and the published row of a form, view, site map, app or web resource.
        routes.MapGet("/RetrieveAppComponents({parameters})", RetrieveAppComponentsAsync);
        routes.MapGet("/{segment}/" + ODataEdmModelBuilder.Namespace + "." + CoreOperations.RetrieveUnpublished + "()", RetrieveUnpublishedAsync);
        routes.MapGet("/{segment}/" + ODataEdmModelBuilder.Namespace + "." + CoreOperations.RetrieveUnpublished, RetrieveUnpublishedAsync);
        // #143: the values a new record starts with, from another record and the maps between their tables.
        routes.MapGet("/" + InitializeFrom.MessageName + "({parameters})", InitializeFromAsync);
        return parent;
    }

    private static async Task<IResult> WhoAmIAsync(HttpContext context, OrganizationService service)
    {
        var response = await ExecuteWhoAmIAsync(service, context.RequestAborted);
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = Context(context.Request, "WhoAmIResponse"),
            ["BusinessUnitId"] = response.BusinessUnitId,
            ["UserId"] = response.UserId,
            ["OrganizationId"] = response.OrganizationId,
        });
    }

    private const string RetrieveUserPrivileges = "RetrieveUserPrivileges";

    /// <summary>
    /// <c>GET systemusers({id})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()</c> → <c>{ RolePrivileges: [ { Depth, PrivilegeId,
    /// BusinessUnitId, PrivilegeName } ] }</c>, executed as the SDK request so the read check on the user and the 404 are the
    /// Retrieve's.
    /// </summary>
    private static async Task<IResult> RetrieveUserPrivilegesAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        string segment)
    {
        var request = new RetrieveUserPrivilegesRequest { UserId = ParseUser(store, segment) };
        var response = (RetrieveUserPrivilegesResponse)await service.ExecuteAsync(request, context.RequestAborted);
        return ODataJson.Result(context, ToJson(Context(context.Request, "RetrieveUserPrivilegesResponse"), response.RolePrivileges));
    }

    /// <summary>
    /// <c>GET RetrieveAppComponents(AppModuleId=@p)</c> (the GUID may also be written inline): the rows of the app's
    /// <c>appmodulecomponent</c>, executed as the SDK request so the read check on the app and the 404 are the Retrieve's.
    /// Learn types the return as <c>Collection(crmbaseentity)</c>: the rows are written as a collection of <c>appmodulecomponent</c>
    /// records with their <c>@odata.type</c>.
    /// </summary>
    private static async Task<IResult> RetrieveAppComponentsAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        string parameters)
    {
        var request = new RetrieveAppComponentsRequest { AppModuleId = ParseAppModuleId(context.Request, parameters) };
        var response = (RetrieveAppComponentsResponse)await service.ExecuteAsync(request, context.RequestAborted);
        var component = store.GetEntity(AppModuleSchema.Component)
                        ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"The table '{AppModuleSchema.Component}' does not exist.", 404);
        var value = new JsonArray();
        foreach (var row in response.AppComponents.Entities)
        {
            var typed = new JsonObject { ["@odata.type"] = "#" + ODataEdmModelBuilder.Namespace + "." + AppModuleSchema.Component };
            foreach (var (name, node) in ODataJson.ToRecordJson(row, component, false).ToArray()) typed[name] = node?.DeepClone();
            value.Add(typed);
        }
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#Collection({ODataEdmModelBuilder.Namespace}.crmbaseentity)",
            ["value"] = value,
        });
    }

    /// <summary>The <c>AppModuleId</c> of <c>RetrieveAppComponents(AppModuleId=…)</c>: a GUID or a parameter alias (<c>@p</c>) with its query value.</summary>
    /// <exception cref="XrmdFaultException">400 for a missing, unknown or invalid parameter.</exception>
    public static Guid ParseAppModuleId(HttpRequest request, string parameters)
    {
        var match = AppModuleIdParameter().Match(parameters);
        if (!match.Success)
            throw XrmdFaultException.InvalidArgument("The function RetrieveAppComponents requires the parameter AppModuleId: 'RetrieveAppComponents(AppModuleId=@p)'.");
        var value = match.Groups[1].Value;
        if (value.StartsWith('@'))
            value = request.Query[value].ToString() is { Length: > 0 } aliased
                ? aliased
                : throw XrmdFaultException.InvalidArgument($"Parameter alias '{value}' of function RetrieveAppComponents has no value.");
        return Guid.TryParse(value.Trim('\''), out var id) && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid AppModuleId '{value}': a GUID is expected.");
    }

    /// <summary>
    /// <c>GET {set}({id})/Microsoft.Dynamics.CRM.RetrieveUnpublished()</c> with the optional <c>$select</c>: xrmverse has no
    /// unpublished layer, so this is the Retrieve of the published row, for systemform, savedquery, sitemap, appmodule and webresource.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for another table (<c>0x80040203</c>) or a segment without a GUID key; 404 for an unknown entity set.</exception>
    private static async Task<IResult> RetrieveUnpublishedAsync(HttpContext context, OrganizationService service, IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The bound function {CoreOperations.RetrieveUnpublished} requires a row: '{{set}}(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        CoreOperations.RequireUnpublishedTable(entity.LogicalName);
        var id = Guid.TryParseExact(keyText, "D", out var parsed)
            ? parsed
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
        return await EntitySetController.RetrieveResultAsync(context, service, entity, id, StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>GET InitializeFrom(EntityMoniker=@p1,TargetEntityName=@p2,TargetFieldType=@p3)?@p1={'@odata.id':'accounts(…)'}&amp;…</c>
    /// (#143), executed as the SDK request: the target record as JSON with its <c>@odata.context</c> and <c>@odata.type</c>, only the
    /// values that are set, each lookup as <c>{navigation}@odata.bind: "{set}({id})"</c> (no leading slash), as an observed
    /// environment answers.
    /// </summary>
    private static async Task<IResult> InitializeFromAsync(HttpContext context, OrganizationService service, IMetadataStore store, string parameters)
    {
        var request = ParseInitializeFrom(context.Request, store, parameters);
        var response = (InitializeFromResponse)await service.ExecuteAsync(request, context.RequestAborted);
        return ODataJson.Result(context, InitializedJson(MetadataController.ServiceRoot(context.Request), store, response.Entity));
    }

    /// <summary>The JSON of an InitializeFrom result <paramref name="entity"/> (see <see cref="InitializeFromAsync"/>).</summary>
    public static JsonObject InitializedJson(string serviceRoot, IMetadataStore store, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entity);
        var m = store.GetEntity(entity.LogicalName)
                ?? throw XrmdFaultException.InvalidArgument($"The table '{entity.LogicalName}' does not exist.");
        var json = new JsonObject
        {
            ["@odata.context"] = $"{serviceRoot}$metadata#{m.EntitySetName}/$entity",
            ["@odata.type"] = $"#{ODataEdmModelBuilder.Namespace}.{m.LogicalName}",
        };
        var relationships = store.GetOneToManyRelationships();
        foreach (var (name, value) in entity.Attributes)
        {
            if (value is null) continue;
            if (value is EntityReference reference)
            {
                var lookup = m.Attributes?.OfType<LookupAttributeMetadata>().FirstOrDefault(a => a.LogicalName == name);
                var navigation = lookup is null ? null : ODataJson.NavigationOf(m, lookup, relationships, reference.LogicalName);
                var set = store.GetEntity(reference.LogicalName)?.EntitySetName;
                // A lookup that has no navigation property for its table cannot be bound: it is left out, as a client could not send it.
                if (navigation is null || set is null) continue;
                json[$"{navigation}@odata.bind"] = $"{set}({reference.Id:D})";
                continue;
            }
            json[name] = ODataJson.ToJsonValue(value);
        }
        return json;
    }

    private static readonly string[] TargetFieldTypes = ["All", "ValidForCreate", "ValidForUpdate", "ValidForRead"];

    /// <summary>
    /// The SDK request of <c>InitializeFrom(…)</c>: each parameter inline or as a <c>@alias</c> of the query string.
    /// <c>EntityMoniker</c> is <c>{'@odata.id':'&lt;set&gt;(&lt;id&gt;)'}</c> (single or double quotes; the id may be a full URL);
    /// <c>TargetEntityName</c> a quoted logical name; <c>TargetFieldType</c> the enum literal
    /// <c>Microsoft.Dynamics.CRM.TargetFieldType'&lt;member&gt;'</c> or <c>'&lt;member&gt;'</c> (a number is refused with 0x80060888,
    /// as observed); <c>SkipParentalRelationshipMapping</c> <c>true</c> or <c>false</c>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing, unknown or invalid parameter (0x80060888 for a numeric
    /// <c>TargetFieldType</c>); 404 for an unknown entity set.</exception>
    public static InitializeFromRequest ParseInitializeFrom(HttpRequest request, IMetadataStore store, string parameters)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in FunctionParameter().Matches(parameters ?? ""))
        {
            var name = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();
            if (value.StartsWith('@'))
                value = request.Query[value].ToString() is { Length: > 0 } aliased
                    ? aliased.Trim()
                    : throw XrmdFaultException.InvalidArgument($"Parameter alias '{value}' of function InitializeFrom has no value.");
            values[name] = value;
        }
        foreach (var name in values.Keys)
            if (name is not ("EntityMoniker" or "TargetEntityName" or "TargetFieldType" or "SkipParentalRelationshipMapping"))
                throw XrmdFaultException.InvalidArgument($"Unknown parameter '{name}' of function InitializeFrom.");
        string Required(string name) => values.TryGetValue(name, out var v) && v.Length > 0
            ? v
            : throw XrmdFaultException.InvalidArgument($"The function InitializeFrom requires the parameter {name}.");

        var moniker = Required("EntityMoniker");
        var id = MonikerId().Match(moniker);
        if (!id.Success)
            throw XrmdFaultException.InvalidArgument($"Invalid EntityMoniker '{moniker}': expected {{'@odata.id':'<set>(<id>)'}}.");
        var path = id.Groups[1].Value;
        var segment = path[(path.LastIndexOf('/') + 1)..];
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText) || !Guid.TryParse(keyText, out var key))
            throw XrmdFaultException.InvalidArgument($"Invalid EntityMoniker '{moniker}': expected {{'@odata.id':'<set>(<id>)'}}.");
        var source = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);

        var targetText = Required("TargetEntityName");
        var target = QuotedValue().Match(targetText) is { Success: true } quoted
            ? quoted.Groups[1].Value
            : throw XrmdFaultException.InvalidArgument($"Invalid TargetEntityName {targetText}: a quoted table name is expected.");

        var typeText = Required("TargetFieldType");
        var literal = TargetFieldTypeLiteral().Match(typeText);
        var member = literal.Success ? Array.IndexOf(TargetFieldTypes, literal.Groups[1].Value) : -1;
        if (member < 0)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Invalid TargetFieldType {typeText}: expected {ODataEdmModelBuilder.Namespace}.TargetFieldType'<All|ValidForCreate|ValidForUpdate|ValidForRead>'.",
                400);

        var initialize = new InitializeFromRequest
        {
            EntityMoniker = new EntityReference(source.LogicalName, key), TargetEntityName = target, TargetFieldType = (TargetFieldType)member,
        };
        if (values.TryGetValue("SkipParentalRelationshipMapping", out var skip))
            initialize.SkipParentalRelationshipMapping = skip.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => throw XrmdFaultException.InvalidArgument($"Invalid SkipParentalRelationshipMapping '{skip}': true or false is expected."),
            };
        return initialize;
    }

    [GeneratedRegex(@"([A-Za-z]+)\s*=\s*(\{[^}]*\}|'(?:[^']|'')*'|[^,]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionParameter();

    [GeneratedRegex(@"@odata\.id['""]\s*:\s*['""]([^'""]+)['""]", RegexOptions.CultureInvariant)]
    private static partial Regex MonikerId();

    [GeneratedRegex(@"\A'([A-Za-z_][A-Za-z0-9_]*)'\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValue();

    [GeneratedRegex(@"\A(?:Microsoft\.Dynamics\.CRM\.TargetFieldType)?'([A-Za-z]+)'\z", RegexOptions.CultureInvariant)]
    private static partial Regex TargetFieldTypeLiteral();

    /// <c>GET LocalTimeFromUtcTime(TimeZoneCode=110,UtcTime=2026-03-29T01:30:00Z)</c> → <c>{ "LocalTime": "2026-03-29T03:30:00Z" }</c>:
    /// run as the SDK message, the local time written with a <c>Z</c> as Dataverse writes it (#131, ruling Z5).
    /// </summary>
    private static async Task<IResult> LocalTimeFromUtcTimeAsync(HttpContext context, OrganizationService service, string parameters)
    {
        var (code, utc) = ParseLocalTimeParameters(context.Request, parameters);
        var response = await service.ExecuteAsync(new OrganizationRequest(TimeZoneMessages.LocalTimeFromUtcTime)
        {
            ["TimeZoneCode"] = code, ["UtcTime"] = utc,
        }, context.RequestAborted);
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = Context(context.Request, "LocalTimeFromUtcTimeResponse"),
            ["LocalTime"] = LocalTimeText((DateTime)response.Results["LocalTime"]),
        });
    }

    /// <summary>
    /// The local time as <c>LocalTimeFromUtcTime</c> writes it: <c>yyyy-MM-ddTHH:mm:ssZ</c>. The <c>Z</c> is observed on Dataverse (a
    /// trial environment, 2026-11-15) although the value is not UTC; clients that parse the response expect it.
    /// </summary>
    public static string LocalTimeText(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>TimeZoneCode</c> (Edm.Int32) and <c>UtcTime</c> (Edm.DateTimeOffset, read as UTC) of
    /// <c>LocalTimeFromUtcTime(…)</c>, in any order, each inline or a <c>@alias</c> from the query string.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing, unknown, repeated or invalid parameter.</exception>
    public static (int Code, DateTime Utc) ParseLocalTimeParameters(HttpRequest request, string parameters)
    {
        const string function = TimeZoneMessages.LocalTimeFromUtcTime;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parameters.Split(',', StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            var name = equals > 0 ? part[..equals].Trim() : part;
            if (name is not ("TimeZoneCode" or "UtcTime") || !values.TryAdd(name, equals > 0 ? part[(equals + 1)..].Trim() : ""))
                throw XrmdFaultException.InvalidArgument(
                    $"Invalid parameters '{parameters}' for function {function}: expected TimeZoneCode=<Int32>,UtcTime=<DateTimeOffset>.");
        }
        string Value(string name)
        {
            if (!values.TryGetValue(name, out var value) || value.Length == 0)
                throw XrmdFaultException.InvalidArgument($"The function {function} requires the parameter {name}.");
            if (!value.StartsWith('@')) return value.Trim('\'');
            return request.Query[value].ToString() is { Length: > 0 } aliased
                ? aliased.Trim('\'')
                : throw XrmdFaultException.InvalidArgument($"Parameter alias '{value}' of function {function} has no value.");
        }
        var codeText = Value("TimeZoneCode");
        var timeText = Value("UtcTime");
        if (!int.TryParse(codeText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
            throw XrmdFaultException.InvalidArgument($"Invalid TimeZoneCode '{codeText}': an integer is expected.");
        if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            || !timeText.Contains('T'))
            throw XrmdFaultException.InvalidArgument($"Invalid UtcTime '{timeText}': a date and time such as 2026-03-29T01:30:00Z is expected.");
        return (code, time.UtcDateTime);
    }

    [GeneratedRegex(@"\A\s*AppModuleId\s*=\s*(\S+?)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AppModuleIdParameter();

    // ---- $metadata --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Declares RetrieveAppComponents (unbound, <c>AppModuleId</c>, returning <c>Collection(crmbaseentity)</c>) and RetrieveUnpublished
    /// (composable, bound to each table it supports that the organization has). Runs after <see cref="ActionController"/>'s
    /// operations, which declare <c>crmbaseentity</c>.
    /// </summary>
    internal static void AddFormOperations(EdmModel model, EdmEntityContainer container, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        const string ns = ODataEdmModelBuilder.Namespace;
        var crmBase = (IEdmEntityType)(model.FindDeclaredType($"{ns}.crmbaseentity")
                                       ?? throw new InvalidOperationException("crmbaseentity must be declared before the form operations."));
        var components = new EdmFunction(ns, "RetrieveAppComponents",
            new EdmCollectionTypeReference(new EdmCollectionType(new EdmEntityTypeReference(crmBase, false))),
            isBound: false, entitySetPathExpression: null, isComposable: false);
        components.AddParameter("AppModuleId", EdmCoreModel.Instance.GetGuid(false));
        model.AddElement(components);
        container.AddFunctionImport("RetrieveAppComponents", components, null, includeInServiceDocument: false);

        // #143: InitializeFrom and its TargetFieldType (Learn's enum members and values).
        var fieldType = new EdmEnumType(ns, "TargetFieldType", EdmPrimitiveTypeKind.Int32, isFlags: false);
        for (var i = 0; i < TargetFieldTypes.Length; i++) fieldType.AddMember(TargetFieldTypes[i], new EdmEnumMemberValue(i));
        model.AddElement(fieldType);
        var initialize = new EdmFunction(ns, InitializeFrom.MessageName, new EdmEntityTypeReference(crmBase, false), isBound: false,
            entitySetPathExpression: null, isComposable: false);
        initialize.AddParameter("EntityMoniker", new EdmEntityTypeReference(crmBase, false));
        initialize.AddParameter("TargetEntityName", EdmCoreModel.Instance.GetString(false));
        initialize.AddParameter("TargetFieldType", new EdmEnumTypeReference(fieldType, false));
        initialize.AddParameter("SkipParentalRelationshipMapping", EdmCoreModel.Instance.GetBoolean(true));
        model.AddElement(initialize);
        container.AddFunctionImport(InitializeFrom.MessageName, initialize, null, includeInServiceDocument: false);
        // #131: LocalTimeFromUtcTime (Learn: TimeZoneCode Edm.Int32, UtcTime Edm.DateTimeOffset → LocalTimeFromUtcTimeResponse).
        var localTimeResponse = new EdmComplexType(ns, "LocalTimeFromUtcTimeResponse");
        localTimeResponse.AddStructuralProperty("LocalTime", EdmPrimitiveTypeKind.DateTimeOffset);
        model.AddElement(localTimeResponse);
        var localTime = new EdmFunction(ns, TimeZoneMessages.LocalTimeFromUtcTime, new EdmComplexTypeReference(localTimeResponse, false),
            isBound: false, entitySetPathExpression: null, isComposable: false);
        localTime.AddParameter("TimeZoneCode", EdmCoreModel.Instance.GetInt32(false));
        localTime.AddParameter("UtcTime", EdmCoreModel.Instance.GetDateTimeOffset(false));
        model.AddElement(localTime);
        container.AddFunctionImport(TimeZoneMessages.LocalTimeFromUtcTime, localTime, null, includeInServiceDocument: false);

        foreach (var table in CoreOperations.UnpublishedSupportedTables)
        {
            if (!entityTypes.TryGetValue(table, out var type)) continue;
            var unpublished = new EdmFunction(ns, CoreOperations.RetrieveUnpublished, new EdmEntityTypeReference(type, false), isBound: true,
                entitySetPathExpression: null, isComposable: true);
            unpublished.AddParameter("entity", new EdmEntityTypeReference(type, false));
            model.AddElement(unpublished);
        }
    }

    /// <summary>The user of the bound segment <c>systemusers(&lt;id&gt;)</c>.</summary>
    /// <exception cref="XrmdFaultException">400 for a segment without a GUID key; 404 for an unknown entity set or one other than
    /// <c>systemuser</c> (the function is bound to it).</exception>
    public static Guid ParseUser(IMetadataStore store, string segment)
    {
        const string bound = ODataEdmModelBuilder.Namespace + "." + RetrieveUserPrivileges;
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The bound function {RetrieveUserPrivileges} requires a user: 'systemusers(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != "systemuser")
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Resource not found for the segment '{segment}/{bound}': the function is bound to systemuser.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
    }

    /// <summary>The RetrieveUserPrivilegesResponse JSON of <paramref name="privileges"/>.</summary>
    public static JsonObject ToJson(string context, IEnumerable<RolePrivilege> privileges) => new()
    {
        ["@odata.context"] = context,
        ["RolePrivileges"] = new JsonArray([.. privileges.Select(p => (JsonNode?)new JsonObject
        {
            ["Depth"] = p.Depth.ToString(),
            ["PrivilegeId"] = p.PrivilegeId.ToString(),
            ["BusinessUnitId"] = p.BusinessUnitId.ToString(),
            ["PrivilegeName"] = p.PrivilegeName,
        })]),
    };

    private static async Task<IResult> RetrieveCurrentOrganizationAsync(
        HttpContext context, OrganizationService service, IConfiguration configuration, string parameters)
    {
        ValidateAccessType(context.Request, parameters);

        var organizationId = (await ExecuteWhoAmIAsync(service, context.RequestAborted)).OrganizationId;
        var organization = await service.RetrieveAsync("organization", organizationId, new ColumnSet("name"), context.RequestAborted);
        var name = organization.GetAttributeValue<string>("name") ?? "";
        var urlName = new string(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        var tenantId = Guid.TryParse(configuration["Auth:TenantId"], out var tenant) ? tenant : Guid.Empty;
        var baseUrl = ODataJson.BaseUrl(context.Request);

        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = Context(context.Request, "RetrieveCurrentOrganizationResponse"),
            ["Detail"] = new JsonObject
            {
                ["OrganizationId"] = organizationId,
                ["FriendlyName"] = name,
                ["OrganizationVersion"] = OrganizationVersion,
                ["EnvironmentId"] = organizationId.ToString(),
                ["DatacenterId"] = Guid.Empty,
                ["Geo"] = Geo,
                ["TenantId"] = tenantId,
                ["UrlName"] = urlName,
                ["UniqueName"] = urlName,
                // EndpointCollection serialized as the Dataverse Web API does (a dictionary of EndpointType → URL).
                ["Endpoints"] = new JsonObject
                {
                    ["Count"] = 3,
                    ["IsReadOnly"] = false,
                    ["Keys"] = new JsonArray("WebApplication", "OrganizationService", "OrganizationDataService"),
                    ["Values"] = new JsonArray(
                        baseUrl + "/",
                        baseUrl + "/XRMServices/2011/Organization.svc",
                        baseUrl + "/XRMServices/2011/OrganizationData.svc"),
                },
                ["State"] = "Enabled",
                ["OrganizationType"] = "Customer",
            },
        });
    }

    private static async Task<WhoAmIResponse> ExecuteWhoAmIAsync(OrganizationService service, CancellationToken ct) =>
        (WhoAmIResponse)await service.ExecuteAsync(new WhoAmIRequest(), ct);

    /// <summary>
    /// The only parameter is <c>AccessType</c>, inline or as a <c>@alias</c> from the query string, with value
    /// <c>Microsoft.Dynamics.CRM.EndpointAccessType'X'</c>, <c>'X'</c>, <c>X</c> or its number, X in Default/Internet/Intranet.
    /// It does not change the response: xrmd has a single set of endpoints.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument for any other parameter or value.</exception>
    private static void ValidateAccessType(HttpRequest request, string parameters)
    {
        var match = AccessTypeParameter().Match(parameters);
        if (!match.Success)
            throw XrmdFaultException.InvalidArgument(
                $"Invalid parameters '{parameters}' for function RetrieveCurrentOrganization: expected AccessType=<EndpointAccessType>.");

        var value = match.Groups[1].Value;
        if (value.StartsWith('@'))
            value = request.Query[value].ToString() is { Length: > 0 } aliased
                ? aliased
                : throw XrmdFaultException.InvalidArgument($"Parameter alias '{value}' of function RetrieveCurrentOrganization has no value.");

        var literal = AccessTypeLiteral().Match(value);
        var accessType = literal.Success ? literal.Groups[1].Value : value;
        if (!AccessTypes.Contains(accessType, StringComparer.Ordinal) && !(int.TryParse(accessType, out var n) && n is >= 0 and <= 2))
            throw XrmdFaultException.InvalidArgument(
                $"Invalid AccessType '{value}': expected Microsoft.Dynamics.CRM.EndpointAccessType'Default', 'Internet' or 'Intranet'.");
    }

    private static string Context(HttpRequest request, string responseType) =>
        $"{MetadataController.ServiceRoot(request)}$metadata#{ODataEdmModelBuilder.Namespace}.{responseType}";

    [GeneratedRegex(@"\A\s*AccessType\s*=\s*(\S+?)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTypeParameter();

    [GeneratedRegex(@"\A(?:Microsoft\.Dynamics\.CRM\.EndpointAccessType)?'([A-Za-z]+)'\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTypeLiteral();
}
