using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Crm.Sdk.Messages;
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
/// The audit operations of the Web API (plan 13, rulings A4, A7, A8), with the Learn request and response shapes, executed as the
/// SDK requests of the same name by <see cref="OrganizationService.ExecuteAsync"/> (so the privileges and refusals are the SDK
/// path's):
/// <list type="bullet">
/// <item><c>GET audits({id})/Microsoft.Dynamics.CRM.RetrieveAuditDetails</c> → <c>{ AuditDetail }</c>.</item>
/// <item><c>GET RetrieveRecordChangeHistory(Target=@target,PagingInfo=@paginginfo)?@target={'@odata.id':'accounts(id)'}&amp;@paginginfo={...}</c>
/// and <c>GET RetrieveAttributeChangeHistory(Target=…,AttributeLogicalName=…,PagingInfo=…)</c> → <c>{ AuditDetailCollection }</c>.
/// Parameters are inline or <c>@alias</c>es; <c>Target</c> is <c>{'@odata.id':'set(id)'}</c> (or a full URL) or
/// <c>{"@odata.type":"Microsoft.Dynamics.CRM.x","xid":"…"}</c>; single-quoted strings are accepted as in Learn's examples.</item>
/// <item><c>POST DeleteRecordChangeHistory</c> with <c>{ Target }</c> → <c>{ DeletedEntriesCount }</c>; <c>POST DeleteAuditData</c>
/// with <c>{ EndDate }</c> (<c>Edm.DateTimeOffset</c>) → <c>{ PartitionsDeleted }</c>.</item>
/// </list>
/// An <c>AuditDetail</c> is written as in Learn's samples (<see cref="ToJson"/>), without <c>AuditRecord</c>, which the Web API
/// does not return. <c>Prefer: odata.include-annotations</c> adds the formatted values of the old and new values.
/// </summary>
public static class AuditEndpoints
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string ODataType = "@odata.type";
    private const string RetrieveAuditDetails = "RetrieveAuditDetails";
    private const string RecordHistory = "RetrieveRecordChangeHistory";
    private const string AttributeHistory = "RetrieveAttributeChangeHistory";
    private const string DeleteHistory = "DeleteRecordChangeHistory";
    private const string DeleteData = "DeleteAuditData";

    /// <summary>The unbound operations, whose names are reserved as entity set names (the bound function is reached through <c>audits</c>).</summary>
    public static IReadOnlyList<string> OperationNames { get; } = [RecordHistory, AttributeHistory, DeleteHistory, DeleteData];

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder group)
    {
        var routes = group.MapGroup("").RequireOrganizationMember();
        routes.MapGet("/{segment}/" + Ns + "." + RetrieveAuditDetails, RetrieveAuditDetailsAsync);
        routes.MapGet("/" + RecordHistory + "({parameters})",
            (HttpContext c, OrganizationService o, IMetadataStore s, string parameters) => HistoryAsync(c, o, s, RecordHistory, parameters));
        routes.MapGet("/" + AttributeHistory + "({parameters})",
            (HttpContext c, OrganizationService o, IMetadataStore s, string parameters) => HistoryAsync(c, o, s, AttributeHistory, parameters));
        routes.MapPost("/" + DeleteHistory, (HttpContext c, OrganizationService o, IMetadataStore s) => DeleteAsync(c, o, s, DeleteHistory));
        routes.MapPost("/" + DeleteData, (HttpContext c, OrganizationService o, IMetadataStore s) => DeleteAsync(c, o, s, DeleteData));
        return group;
    }

    // ---- handlers ---------------------------------------------------------------------------------------------------

    private static async Task<IResult> RetrieveAuditDetailsAsync(HttpContext context, OrganizationService service, IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The bound function {RetrieveAuditDetails} requires an audit record: 'audits(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != AuditTable.Name)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Resource not found for the segment '{segment}/{Ns}.{RetrieveAuditDetails}': the function is bound to audit.", 404);
        var id = Guid.TryParseExact(keyText, "D", out var key)
            ? key
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");

        var response = (RetrieveAuditDetailsResponse)await service.ExecuteAsync(new RetrieveAuditDetailsRequest { AuditId = id }, context.RequestAborted);
        var formatted = Formatted(context);
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = Context(context.Request, "RetrieveAuditDetailsResponse"),
            ["AuditDetail"] = ToJson(response.AuditDetail, store, ODataJson.BaseUrl(context.Request), formatted),
        });
    }

    private static async Task<IResult> HistoryAsync(HttpContext context, OrganizationService service, IMetadataStore store, string function,
        string parameters)
    {
        var request = ToFunctionRequest(function, parameters, a => context.Request.Query[a].ToString() is { Length: > 0 } v ? v : null, store);
        var response = await service.ExecuteAsync(request, context.RequestAborted);
        var collection = (AuditDetailCollection)response.Results["AuditDetailCollection"];
        var formatted = Formatted(context);
        var baseUrl = ODataJson.BaseUrl(context.Request);
        var details = new JsonArray();
        foreach (var detail in collection.AuditDetails) details.Add(ToJson(detail, store, baseUrl, formatted));
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = Context(context.Request, function + "Response"),
            ["AuditDetailCollection"] = new JsonObject
            {
                ["MoreRecords"] = collection.MoreRecords,
                ["PagingCookie"] = collection.PagingCookie,
                ["TotalRecordCount"] = collection.TotalRecordCount,
                ["AuditDetails"] = details,
            },
        });
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, OrganizationService service, IMetadataStore store, string action)
    {
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        var response = await service.ExecuteAsync(ToActionRequest(action, body, store), context.RequestAborted);
        var property = action == DeleteHistory ? "DeletedEntriesCount" : "PartitionsDeleted";
        return ODataJson.Result(context, new JsonObject
        {
            ["@odata.context"] = Context(context.Request, action + "Response"),
            [property] = (int)response.Results[property],
        });
    }

    /// <summary>True when the request asks for formatted values (<c>Prefer: odata.include-annotations</c>), which is then applied.</summary>
    private static bool Formatted(HttpContext context)
    {
        if (EntitySetController.FormattedValuesPreference(context.Request) is not { } annotations) return false;
        context.Response.Headers["Preference-Applied"] = annotations;
        return true;
    }

    private static string Context(HttpRequest request, string responseType) =>
        $"{MetadataController.ServiceRoot(request)}$metadata#{Ns}.{responseType}";

    // ---- requests ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The SDK request of the history function <paramref name="function"/> for its parameter list <paramref name="parameters"/>
    /// (<c>Name=value</c> pairs, each value inline or an <c>@alias</c> that <paramref name="alias"/> resolves from the query string).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown, repeated or missing parameter, an alias without a value, or a value
    /// of the wrong shape (naming it).</exception>
    public static OrganizationRequest ToFunctionRequest(string function, string parameters, Func<string, string?> alias, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(store);
        string[] allowed = function switch
        {
            RecordHistory => ["Target", "PagingInfo"],
            AttributeHistory => ["Target", "AttributeLogicalName", "PagingInfo"],
            _ => throw new ArgumentException($"'{function}' is not an audit function of this controller.", nameof(function)),
        };
        var request = new OrganizationRequest(function);
        foreach (var pair in SplitTopLevel(parameters))
        {
            var eq = pair.IndexOf('=');
            var name = eq > 0 ? pair[..eq].Trim() : pair.Trim();
            if (eq <= 0 || !allowed.Contains(name, StringComparer.Ordinal))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a valid parameter for the function '{function}'.");
            if (request.Parameters.ContainsKey(name))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{function}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            if (text.StartsWith('@'))
                text = alias(text) ?? throw XrmdFaultException.InvalidArgument($"Parameter alias '{text}' of function '{function}' has no value.");
            if (text == "null") continue;
            request[name] = name switch
            {
                "Target" => ReadReference(function, name, Parse(function, name, text), store),
                "AttributeLogicalName" => ReadString(function, name, text),
                _ => ReadPagingInfo(function, Parse(function, name, text)),
            };
        }
        foreach (var required in allowed.Where(p => p != "PagingInfo"))
            if (!request.Parameters.ContainsKey(required))
                throw XrmdFaultException.InvalidArgument($"Required parameter '{required}' of function '{function}' is missing.");
        return request;
    }

    /// <summary>
    /// The SDK request of the action <paramref name="action"/> for its JSON <paramref name="body"/>: <c>Target</c> (the entity
    /// reference form of the other actions) for DeleteRecordChangeHistory, <c>EndDate</c> (<c>Edm.DateTimeOffset</c>, read as UTC)
    /// for DeleteAuditData.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown parameter or a value of the wrong type, naming it.</exception>
    public static OrganizationRequest ToActionRequest(string action, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        var parameter = action switch
        {
            DeleteHistory => "Target",
            DeleteData => "EndDate",
            _ => throw new ArgumentException($"'{action}' is not an audit action of this controller.", nameof(action)),
        };
        var request = new OrganizationRequest(action);
        foreach (var (name, node) in body)
        {
            if (name.StartsWith('@')) continue; // instance annotations
            if (name != parameter)
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{action}'.");
            if (node is null) throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{action}' must not be null.");
            request[name] = action == DeleteHistory
                ? ActionController.ReadEntityReference(action, name, node, store)
                : ReadDateTimeOffset(action, name, node);
        }
        return request;
    }

    internal static DateTime ReadDateTimeOffset(string action, string name, JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{action}' must be an Edm.DateTimeOffset.");

    /// <summary>
    /// An entity reference parameter of an operation (the target of a history function, a custom API's EntityReference):
    /// <c>{'@odata.id':'set(id)'}</c> (the id may be a full URL) or the entity form of the actions (<c>@odata.type</c> and the
    /// primary key).
    /// </summary>
    internal static EntityReference ReadReference(string operation, string parameter, JsonNode node, IMetadataStore store)
    {
        if (node is not JsonObject target)
            throw XrmdFaultException.InvalidArgument($"The parameter '{parameter}' of operation '{operation}' must be an entity reference object.");
        if (!target.ContainsKey("@odata.id")) return ActionController.ReadEntityReference(operation, parameter, node, store);
        if (target.Count != 1 || target["@odata.id"] is not JsonValue v || !v.TryGetValue<string>(out var url))
            throw XrmdFaultException.InvalidArgument(
                $"The parameter '{parameter}' of operation '{operation}' must be {{'@odata.id':'<entityset>(<id>)'}} with no other property.");
        var segment = url[(url.LastIndexOf('/') + 1)..];
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var key) || !Guid.TryParseExact(key, "D", out var id))
            throw XrmdFaultException.InvalidArgument($"The '@odata.id' '{url}' of parameter '{parameter}' of operation '{operation}' is not '<entityset>(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw XrmdFaultException.InvalidArgument(
                         $"The '@odata.id' '{url}' of parameter '{parameter}' of operation '{operation}' names the entity set '{setName}', which does not exist.");
        return new EntityReference(entity.LogicalName, id);
    }

    internal static string ReadString(string function, string name, string text)
    {
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        return Parse(function, name, text) is JsonValue v && v.TryGetValue<string>(out var value)
            ? value
            : throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{function}' must be an Edm.String ('text').");
    }

    internal static PagingInfo ReadPagingInfo(string function, JsonNode node)
    {
        if (node is not JsonObject properties)
            throw XrmdFaultException.InvalidArgument($"The parameter 'PagingInfo' of function '{function}' must be a {Ns}.PagingInfo object.");
        var paging = new PagingInfo();
        foreach (var (name, value) in properties)
        {
            XrmdFaultException Invalid(string type) =>
                XrmdFaultException.InvalidArgument($"The property '{name}' of parameter 'PagingInfo' of function '{function}' must be an {type}.");
            switch (name)
            {
                case ODataType:
                    break;
                case "PageNumber":
                    paging.PageNumber = value is JsonValue p && p.TryGetValue<int>(out var page) ? page : throw Invalid("Edm.Int32");
                    break;
                case "Count":
                    paging.Count = value is JsonValue c && c.TryGetValue<int>(out var count) ? count : throw Invalid("Edm.Int32");
                    break;
                case "ReturnTotalRecordCount":
                    paging.ReturnTotalRecordCount = value is JsonValue t && t.TryGetValue<bool>(out var total) ? total : throw Invalid("Edm.Boolean");
                    break;
                case "PagingCookie":
                    paging.PagingCookie = value is null ? null : value is JsonValue k && k.TryGetValue<string>(out var cookie) ? cookie : throw Invalid("Edm.String");
                    break;
                default:
                    throw XrmdFaultException.InvalidArgument($"The property '{name}' of parameter 'PagingInfo' is not a property of {Ns}.PagingInfo.");
            }
        }
        return paging;
    }

    /// <summary>A parameter value as JSON; single-quoted strings (Learn's examples) are read as JSON strings.</summary>
    internal static JsonNode Parse(string function, string name, string text)
    {
        try
        {
            return JsonNode.Parse(DoubleQuoted(text)) ?? throw new JsonException("null");
        }
        catch (JsonException)
        {
            throw XrmdFaultException.InvalidArgument($"The value of parameter '{name}' of function '{function}' is not valid JSON.");
        }
    }

    /// <summary><paramref name="text"/> with every single-quoted string ('' for a quote) rewritten as a JSON string.</summary>
    internal static string DoubleQuoted(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                // A JSON string: copied as it is, escapes included.
                var start = i++;
                while (i < text.Length && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
                sb.Append(text, start, Math.Min(i, text.Length - 1) - start + 1);
            }
            else if (ch == '\'')
            {
                var value = new StringBuilder();
                for (i++; i < text.Length; i++)
                {
                    if (text[i] != '\'') value.Append(text[i]);
                    else if (i + 1 < text.Length && text[i + 1] == '\'') value.Append(text[++i]);
                    else break;
                }
                sb.Append(JsonSerializer.Serialize(value.ToString()));
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Splits a parameter list on the commas outside braces, brackets, parentheses and quotes.</summary>
    internal static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote is { } q)
            {
                if (ch == '\\' && q == '"') i++;
                else if (ch == q) quote = null;
            }
            else if (ch is '"' or '\'') quote = ch;
            else if (ch is '{' or '[' or '(') depth++;
            else if (ch is '}' or ']' or ')') depth--;
            else if (ch == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (text[start..].Trim().Length > 0 || start > 0) yield return text[start..];
    }

    // ---- JSON -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An <see cref="AuditDetail"/> as the Web API writes it (Learn's samples): <c>@odata.type</c> of its type, then
    /// <list type="bullet">
    /// <item><c>AttributeAuditDetail</c>: <c>InvalidNewValueAttributes</c>, <c>LocLabelLanguageCode</c>, <c>DeletedAttributes</c>
    /// as <c>{Count, Keys, Values}</c>, <c>OldValue</c> and <c>NewValue</c> as records with their entity's <c>@odata.type</c>
    /// (<see cref="ODataJson"/>'s record shape; only the type when the table has been deleted);</item>
    /// <item><c>RelationshipAuditDetail</c>: <c>RelationshipName</c>, <c>TargetRecords</c>; <c>ShareAuditDetail</c>:
    /// <c>Principal</c>, <c>OldPrivileges</c> and <c>NewPrivileges</c> (<c>AccessRights</c> member names);
    /// <c>RolePrivilegeAuditDetail</c>: <c>OldRolePrivileges</c>, <c>NewRolePrivileges</c>, <c>InvalidNewPrivileges</c>.</item>
    /// </list>
    /// A record reference is <c>{ "@odata.type": "#Microsoft.Dynamics.CRM.x", "xid": "…" }</c>. No <c>AuditRecord</c>.
    /// </summary>
    /// <param name="baseUrl">The server's base URL (kept for the record links of a later version; not written today).</param>
    /// <param name="includeFormattedValues">Writes the formatted value annotations of the old and new values.</param>
    public static JsonObject ToJson(AuditDetail detail, IMetadataStore store, string baseUrl, bool includeFormattedValues = false)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(store);
        _ = baseUrl;
        var json = new JsonObject { [ODataType] = $"#{Ns}.{detail.GetType().Name}" };
        switch (detail)
        {
            case AttributeAuditDetail a:
                json["InvalidNewValueAttributes"] = new JsonArray([.. (a.InvalidNewValueAttributes ?? Enumerable.Empty<string>()).Select(n => (JsonNode?)n)]);
                json["LocLabelLanguageCode"] = a.LocLabelLanguageCode;
                var deleted = a.DeletedAttributes ?? [];
                json["DeletedAttributes"] = new JsonObject
                {
                    ["Count"] = deleted.Count,
                    ["Keys"] = new JsonArray([.. deleted.Keys.Select(k => (JsonNode?)k)]),
                    ["Values"] = new JsonArray([.. deleted.Values.Select(v => (JsonNode?)v)]),
                };
                json["OldValue"] = Record(a.OldValue, store, includeFormattedValues);
                json["NewValue"] = Record(a.NewValue, store, includeFormattedValues);
                break;
            case RelationshipAuditDetail r:
                json["RelationshipName"] = r.RelationshipName;
                json["TargetRecords"] = new JsonArray([.. (r.TargetRecords ?? Enumerable.Empty<EntityReference>()).Select(t => (JsonNode?)Reference(t, store))]);
                break;
            case ShareAuditDetail s:
                json["OldPrivileges"] = s.OldPrivileges.ToString();
                json["NewPrivileges"] = s.NewPrivileges.ToString();
                json["Principal"] = s.Principal is null ? null : Reference(s.Principal, store);
                break;
            case RolePrivilegeAuditDetail p:
                json["InvalidNewPrivileges"] = new JsonArray([.. (p.InvalidNewPrivileges ?? Enumerable.Empty<Guid>()).Select(g => (JsonNode?)g.ToString())]);
                json["OldRolePrivileges"] = Privileges(p.OldRolePrivileges);
                json["NewRolePrivileges"] = Privileges(p.NewRolePrivileges);
                break;
            default:
                throw new InvalidOperationException($"The audit detail type '{detail.GetType().Name}' has no Web API form in xrmverse.");
        }
        return json;
    }

    private static JsonObject Record(Entity? entity, IMetadataStore store, bool includeFormattedValues)
    {
        if (entity is null) return new JsonObject();
        var json = new JsonObject { [ODataType] = $"#{Ns}.{entity.LogicalName}" };
        if (store.GetEntity(entity.LogicalName) is not { } m) return json;
        var record = ODataJson.ToRecordJson(entity, m, includeFormattedValues, relationships: store.GetOneToManyRelationships());
        foreach (var (name, value) in record.ToList())
        {
            record.Remove(name);
            json[name] = value;
        }
        return json;
    }

    internal static JsonObject Reference(EntityReference reference, IMetadataStore store)
    {
        var key = store.GetEntity(reference.LogicalName) is { } m ? SystemAttributes.PrimaryIdName(m) : reference.LogicalName + "id";
        return new JsonObject { [ODataType] = $"#{Ns}.{reference.LogicalName}", [key] = reference.Id.ToString() };
    }

    private static JsonArray Privileges(RolePrivilege[]? privileges) =>
        new([.. (privileges ?? []).Select(p => (JsonNode?)new JsonObject
        {
            ["Depth"] = p.Depth.ToString(),
            ["PrivilegeId"] = p.PrivilegeId.ToString(),
            ["BusinessUnitId"] = p.BusinessUnitId.ToString(),
        })]);

    // ---- $metadata --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Declares the audit operations and their types: <c>PagingInfo</c>, the abstract <c>AuditDetail</c> and its four
    /// derived types (open: the properties of their JSON that have no declared type, such as <c>DeletedAttributes</c>, are
    /// not declared), <c>AuditDetailCollection</c>, the <c>AccessRights</c> flags, the response types, the function bound to
    /// <c>audit</c> and the unbound functions and actions with their imports. Runs after <see cref="ActionController"/>'s, which
    /// declares <c>crmbaseentity</c>.
    /// </summary>
    internal static void AddOperations(EdmModel model, EdmEntityContainer container, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        var core = EdmCoreModel.Instance;
        var crmBase = (IEdmEntityType)(model.FindDeclaredType($"{Ns}.crmbaseentity")
                                       ?? throw new InvalidOperationException("crmbaseentity must be declared before the audit operations."));
        var crmBaseRef = new EdmEntityTypeReference(crmBase, false);
        IEdmTypeReference Collection(IEdmTypeReference element) => new EdmCollectionTypeReference(new EdmCollectionType(element));

        var paging = new EdmComplexType(Ns, "PagingInfo");
        paging.AddStructuralProperty("PageNumber", EdmPrimitiveTypeKind.Int32, isNullable: false);
        paging.AddStructuralProperty("Count", EdmPrimitiveTypeKind.Int32, isNullable: false);
        paging.AddStructuralProperty("PagingCookie", EdmPrimitiveTypeKind.String, isNullable: true);
        paging.AddStructuralProperty("ReturnTotalRecordCount", EdmPrimitiveTypeKind.Boolean, isNullable: false);
        model.AddElement(paging);

        var accessRights = new EdmEnumType(Ns, "AccessRights", EdmPrimitiveTypeKind.Int32, isFlags: true);
        foreach (var right in Enum.GetValues<AccessRights>())
            accessRights.AddMember(right.ToString(), new EdmEnumMemberValue((int)right));
        model.AddElement(accessRights);

        var detail = new EdmComplexType(Ns, "AuditDetail", null, isAbstract: true, isOpen: false);
        detail.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo { Name = "AuditRecord", Target = crmBase, TargetMultiplicity = EdmMultiplicity.ZeroOrOne });
        model.AddElement(detail);
        EdmComplexType Derived(string name)
        {
            var type = new EdmComplexType(Ns, name, detail, isAbstract: false, isOpen: true);
            model.AddElement(type);
            return type;
        }

        var attribute = Derived("AttributeAuditDetail");
        attribute.AddStructuralProperty("InvalidNewValueAttributes", Collection(core.GetString(false)));
        attribute.AddStructuralProperty("LocLabelLanguageCode", EdmPrimitiveTypeKind.Int32, isNullable: false);
        attribute.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo { Name = "OldValue", Target = crmBase, TargetMultiplicity = EdmMultiplicity.ZeroOrOne });
        attribute.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo { Name = "NewValue", Target = crmBase, TargetMultiplicity = EdmMultiplicity.ZeroOrOne });

        var relationship = Derived("RelationshipAuditDetail");
        relationship.AddStructuralProperty("RelationshipName", EdmPrimitiveTypeKind.String, isNullable: true);
        relationship.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo { Name = "TargetRecords", Target = crmBase, TargetMultiplicity = EdmMultiplicity.Many });

        var share = Derived("ShareAuditDetail");
        share.AddStructuralProperty("OldPrivileges", new EdmEnumTypeReference(accessRights, false));
        share.AddStructuralProperty("NewPrivileges", new EdmEnumTypeReference(accessRights, false));
        share.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo { Name = "Principal", Target = crmBase, TargetMultiplicity = EdmMultiplicity.ZeroOrOne });

        var rolePrivilege = Derived("RolePrivilegeAuditDetail");
        rolePrivilege.AddStructuralProperty("InvalidNewPrivileges", Collection(core.GetGuid(false)));
        if (model.FindDeclaredType($"{Ns}.RolePrivilege") is IEdmComplexType privilegeType)
        {
            rolePrivilege.AddStructuralProperty("OldRolePrivileges", Collection(new EdmComplexTypeReference(privilegeType, false)));
            rolePrivilege.AddStructuralProperty("NewRolePrivileges", Collection(new EdmComplexTypeReference(privilegeType, false)));
        }

        var collection = new EdmComplexType(Ns, "AuditDetailCollection");
        collection.AddStructuralProperty("AuditDetails", Collection(new EdmComplexTypeReference(detail, false)));
        collection.AddStructuralProperty("MoreRecords", EdmPrimitiveTypeKind.Boolean, isNullable: false);
        collection.AddStructuralProperty("PagingCookie", EdmPrimitiveTypeKind.String, isNullable: true);
        collection.AddStructuralProperty("TotalRecordCount", EdmPrimitiveTypeKind.Int32, isNullable: false);
        model.AddElement(collection);

        IEdmTypeReference Response(string name, string property, IEdmTypeReference type)
        {
            var response = new EdmComplexType(Ns, name);
            response.AddStructuralProperty(property, type);
            model.AddElement(response);
            return new EdmComplexTypeReference(response, false);
        }

        var details = Response("RetrieveAuditDetailsResponse", "AuditDetail", new EdmComplexTypeReference(detail, true));
        if (entityTypes.TryGetValue(AuditTable.Name, out var audit))
        {
            var bound = new EdmFunction(Ns, RetrieveAuditDetails, details, isBound: true, entitySetPathExpression: null, isComposable: false);
            bound.AddParameter("entity", new EdmEntityTypeReference(audit, false));
            model.AddElement(bound);
        }

        foreach (var (name, withAttribute) in new[] { (RecordHistory, false), (AttributeHistory, true) })
        {
            var function = new EdmFunction(Ns, name, Response(name + "Response", "AuditDetailCollection", new EdmComplexTypeReference(collection, true)),
                isBound: false, entitySetPathExpression: null, isComposable: false);
            function.AddParameter("Target", crmBaseRef);
            if (withAttribute) function.AddParameter("AttributeLogicalName", core.GetString(false));
            function.AddParameter("PagingInfo", new EdmComplexTypeReference(paging, true));
            model.AddElement(function);
            container.AddFunctionImport(name, function, null, includeInServiceDocument: false);
        }

        var deleteHistory = new EdmAction(Ns, DeleteHistory, Response(DeleteHistory + "Response", "DeletedEntriesCount", core.GetInt32(false)),
            isBound: false, entitySetPathExpression: null);
        deleteHistory.AddParameter("Target", crmBaseRef);
        model.AddElement(deleteHistory);
        container.AddActionImport(deleteHistory);

        var deleteData = new EdmAction(Ns, DeleteData, Response(DeleteData + "Response", "PartitionsDeleted", core.GetInt32(false)),
            isBound: false, entitySetPathExpression: null);
        deleteData.AddParameter("EndDate", core.GetDateTimeOffset(false));
        model.AddElement(deleteData);
        container.AddActionImport(deleteData);
    }
}
