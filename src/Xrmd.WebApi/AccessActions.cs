using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// The access and sharing messages of the Web API (plan 3.5 UI-1b task 2), with the Learn request and response shapes, executed as
/// the SDK requests of the same name by <see cref="OrganizationService.ExecuteAsync"/> (so the privileges, the checks and the
/// faults are the SDK path's):
/// <list type="bullet">
/// <item><c>GET systemusers({id})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)?@t={"@odata.id":"accounts({id})"}</c>
/// (also bound to <c>teams({id})</c>) → <c>{ "AccessRights": "ReadAccess, WriteAccess" }</c>. Learn also binds it to the
/// organization, which xrmverse does not offer (404).</item>
/// <item><c>GET RetrieveSharedPrincipalsAndAccess(Target=@t)?@t=…</c> → <c>{ "PrincipalAccesses": [ { "AccessMask", "Principal" } ] }</c>.</item>
/// <item><c>POST GrantAccess</c> and <c>POST ModifyAccess</c> with <c>{ "Target", "PrincipalAccess": { "AccessMask":
/// "ReadAccess, WriteAccess", "Principal": { "@odata.type": "Microsoft.Dynamics.CRM.systemuser", "systemuserid": "…" } } }</c>;
/// <c>POST RevokeAccess</c> with <c>{ "Target", "Revokee" }</c>: 204.</item>
/// </list>
/// <c>Target</c> and a principal are <c>{"@odata.id":"set(id)"}</c> (a function takes it as a parameter alias, <c>?@t=…</c>: inline in the path only without slashes, so a full-URL id must be an alias) or
/// the entity form of the other actions (<c>@odata.type</c> and the primary key). <c>AccessRights</c> is the enum string form: the
/// member names (<c>None</c>, <c>ReadAccess</c>, <c>WriteAccess</c>, <c>AppendAccess</c>, <c>AppendToAccess</c>, <c>CreateAccess</c>,
/// <c>DeleteAccess</c>, <c>ShareAccess</c>, <c>AssignAccess</c>) joined by commas; a number or an unknown name is 400. An unknown
/// parameter or property, or a missing one, is 400 naming it.
/// </summary>
public static class AccessActions
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string ODataType = "@odata.type";
    private const string RetrievePrincipalAccess = AccessMessages.RetrievePrincipalAccess;
    private const string RetrieveShared = AccessMessages.RetrieveSharedPrincipalsAndAccess;
    private const string Grant = "GrantAccess";
    private const string Modify = "ModifyAccess";
    private const string Revoke = "RevokeAccess";
    private const string BoundPrefix = Ns + "." + RetrievePrincipalAccess + "(";

    /// <summary>The unbound operation names (reserved as entity set names, see <see cref="ActionController.OperationNames"/>).</summary>
    public static IReadOnlyList<string> OperationNames { get; } = [RetrieveShared, Grant, Modify, Revoke];

    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        // Bound to systemuser and team. The operation segment carries its parameter list, so it is not a literal one: a constraint
        // names it, and an order ahead of the custom API bound route and the file route (whose segments it also fits) picks it.
        var bound = RoutePatternFactory.Parse("/{segment}/{operation}", defaults: null,
            parameterPolicies: new { operation = new PrincipalAccessSegmentConstraint() });
        routes.Map(bound, PrincipalAccessAsync).WithMetadata(new HttpMethodMetadata([HttpMethods.Get])).WithOrder(-1);
        routes.MapGet("/" + RetrieveShared + "({parameters})", SharedAsync);
        foreach (var action in (string[])[Grant, Modify, Revoke])
            routes.MapPost("/" + action, (HttpContext c, OrganizationService o, IMetadataStore s) => ActionAsync(c, o, s, action)).InChangeSet();
        return parent;
    }

    /// <summary>The route constraint of the bound function's <c>{operation}</c>: <c>Microsoft.Dynamics.CRM.RetrievePrincipalAccess(…)</c>.</summary>
    private sealed class PrincipalAccessSegmentConstraint : IRouteConstraint
    {
        public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values,
            RouteDirection routeDirection) =>
            values.TryGetValue(routeKey, out var value) && value?.ToString() is { } text
            && text.StartsWith(BoundPrefix, StringComparison.Ordinal) && text.EndsWith(')');
    }

    // ---- handlers ---------------------------------------------------------------------------------------------------

    private static async Task<IResult> PrincipalAccessAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        string segment, string operation)
    {
        var parameters = operation[BoundPrefix.Length..^1];
        var request = ToRetrievePrincipalAccess(segment, parameters, Alias(context), store);
        var response = await service.ExecuteAsync(request, context.RequestAborted);
        return ODataJson.Result(context, ToJson(Context(context.Request, RetrievePrincipalAccess + "Response"), (AccessRights)response.Results["AccessRights"]));
    }

    private static async Task<IResult> SharedAsync(HttpContext context, OrganizationService service, IMetadataStore store, string parameters)
    {
        var response = await service.ExecuteAsync(ToRetrieveShared(parameters, Alias(context), store), context.RequestAborted);
        return ODataJson.Result(context, ToJson(Context(context.Request, RetrieveShared + "Response"),
            (PrincipalAccess[])response.Results["PrincipalAccesses"], store));
    }

    private static async Task<IResult> ActionAsync(HttpContext context, OrganizationService service, IMetadataStore store, string action)
    {
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        await service.ExecuteAsync(ToActionRequest(action, body, store), context.RequestAborted);
        return Results.NoContent();
    }

    private static Func<string, string?> Alias(HttpContext context) =>
        a => context.Request.Query[a].ToString() is { Length: > 0 } value ? value : null;

    private static string Context(HttpRequest request, string responseType) =>
        $"{MetadataController.ServiceRoot(request)}$metadata#{Ns}.{responseType}";

    // ---- requests ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The RetrievePrincipalAccess request of the bound segment <paramref name="segment"/> (<c>systemusers(&lt;id&gt;)</c> or
    /// <c>teams(&lt;id&gt;)</c>, the principal) and the parameter list <paramref name="parameters"/> (<c>Target=…</c>, inline or an
    /// <c>@alias</c> that <paramref name="alias"/> resolves from the query string).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a segment without a GUID key or a bad parameter; 404 for an unknown entity set
    /// or one other than systemuser and team.</exception>
    public static OrganizationRequest ToRetrievePrincipalAccess(string segment, string parameters, Func<string, string?> alias, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(store);
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument(
                $"The bound function {RetrievePrincipalAccess} requires a user or a team: 'systemusers(<id>)' or 'teams(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName is not ("systemuser" or "team"))
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Resource not found for the segment '{segment}/{Ns}.{RetrievePrincipalAccess}': the function is bound to systemuser and team.", 404);
        var id = Guid.TryParseExact(keyText, "D", out var key)
            ? key
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
        return new RetrievePrincipalAccessRequest { Target = TargetOf(RetrievePrincipalAccess, parameters, alias, store), Principal = new EntityReference(entity.LogicalName, id) };
    }

    /// <summary>The RetrieveSharedPrincipalsAndAccess request of the parameter list <paramref name="parameters"/> (<c>Target=…</c>).</summary>
    /// <exception cref="XrmdFaultException">400 for a missing or bad parameter.</exception>
    public static OrganizationRequest ToRetrieveShared(string parameters, Func<string, string?> alias, IMetadataStore store) =>
        new RetrieveSharedPrincipalsAndAccessRequest { Target = TargetOf(RetrieveShared, parameters, alias, store) };

    /// <summary>The only parameter of the two functions: <c>Target</c>, as an <c>@odata.id</c> or an entity.</summary>
    private static EntityReference TargetOf(string function, string parameters, Func<string, string?> alias, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(alias);
        JsonNode? target = null;
        foreach (var pair in AuditEndpoints.SplitTopLevel(parameters))
        {
            var eq = pair.IndexOf('=');
            var name = eq > 0 ? pair[..eq].Trim() : pair.Trim();
            if (eq <= 0 || name != "Target")
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a valid parameter for the function '{function}'.");
            if (target is not null)
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{function}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            if (text.StartsWith('@'))
                text = alias(text) ?? throw XrmdFaultException.InvalidArgument($"Parameter alias '{text}' of function '{function}' has no value.");
            target = AuditEndpoints.Parse(function, name, text);
        }
        return AuditEndpoints.ReadReference(function, "Target",
            target ?? throw XrmdFaultException.InvalidArgument($"Required parameter 'Target' of function '{function}' is missing."), store);
    }

    /// <summary>
    /// The SDK request of the action <paramref name="action"/> (GrantAccess, ModifyAccess or RevokeAccess) for its JSON <paramref name="body"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown, missing or wrongly typed parameter or property, naming it.</exception>
    public static OrganizationRequest ToActionRequest(string action, JsonObject body, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        var second = action switch
        {
            Grant or Modify => "PrincipalAccess",
            Revoke => "Revokee",
            _ => throw new ArgumentException($"'{action}' is not an access action of this controller.", nameof(action)),
        };
        foreach (var (name, _) in body)
            if (name != "Target" && name != second && !name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{action}'.");
        var target = AuditEndpoints.ReadReference(action, "Target", Required(action, body, "Target"), store);
        var node = Required(action, body, second);
        return action switch
        {
            Grant => new GrantAccessRequest { Target = target, PrincipalAccess = ReadPrincipalAccess(action, node, store) },
            Modify => new ModifyAccessRequest { Target = target, PrincipalAccess = ReadPrincipalAccess(action, node, store) },
            _ => new RevokeAccessRequest { Target = target, Revokee = AuditEndpoints.ReadReference(action, second, node, store) },
        };
    }

    private static JsonNode Required(string action, JsonObject body, string name) =>
        body[name] ?? throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of operation '{action}' is required.");

    /// <summary>A <c>PrincipalAccess</c>: <c>AccessMask</c> (<c>AccessRights</c>) and <c>Principal</c> (a systemuser or a team record).</summary>
    private static PrincipalAccess ReadPrincipalAccess(string action, JsonNode node, IMetadataStore store)
    {
        if (node is not JsonObject json)
            throw XrmdFaultException.InvalidArgument($"The parameter 'PrincipalAccess' of operation '{action}' must be a {Ns}.PrincipalAccess object.");
        foreach (var (name, _) in json)
            if (name is not ("AccessMask" or "Principal") && name != ODataType)
                throw XrmdFaultException.InvalidArgument(
                    $"The property '{name}' of parameter 'PrincipalAccess' of operation '{action}' is not a property of {Ns}.PrincipalAccess.");
        var mask = json["AccessMask"] ?? throw XrmdFaultException.InvalidArgument(
            $"The property 'AccessMask' of parameter 'PrincipalAccess' of operation '{action}' is required.");
        var principal = json["Principal"] ?? throw XrmdFaultException.InvalidArgument(
            $"The property 'Principal' of parameter 'PrincipalAccess' of operation '{action}' is required.");
        return new PrincipalAccess
        {
            AccessMask = ReadAccessRights(action, "AccessMask", mask),
            Principal = AuditEndpoints.ReadReference(action, "PrincipalAccess/Principal", principal, store),
        };
    }

    /// <summary>
    /// An <c>AccessRights</c> value in the enum string form: member names joined by commas (case-insensitive, spaces allowed).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a value that is not a string, an empty list or element, a number or an unknown name.</exception>
    public static AccessRights ReadAccessRights(string operation, string name, JsonNode node)
    {
        var names = Enum.GetNames<AccessRights>();
        var rights = AccessRights.None;
        if (node is JsonValue v && v.TryGetValue<string>(out var text))
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var matched = parts.Select(p => names.FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matched.All(m => m is not null))
            {
                foreach (var member in matched) rights |= Enum.Parse<AccessRights>(member!);
                return rights;
            }
        }
        throw XrmdFaultException.InvalidArgument(
            $"The parameter '{name}' of operation '{operation}' must be a {Ns}.AccessRights value: member names joined by commas ({string.Join(", ", names)}).");
    }

    // ---- JSON -------------------------------------------------------------------------------------------------------

    /// <summary>The <c>AccessRights</c> enum string form: the member names joined by <c>", "</c> (<c>None</c> for no right).</summary>
    public static string FormatRights(AccessRights rights) => rights.ToString();

    /// <summary>The RetrievePrincipalAccessResponse JSON.</summary>
    public static JsonObject ToJson(string context, AccessRights rights) => new()
    {
        ["@odata.context"] = context,
        ["AccessRights"] = FormatRights(rights),
    };

    /// <summary>The RetrieveSharedPrincipalsAndAccessResponse JSON: each <c>PrincipalAccess</c> as <c>{ AccessMask, Principal }</c>.</summary>
    public static JsonObject ToJson(string context, IEnumerable<PrincipalAccess> accesses, IMetadataStore store) => new()
    {
        ["@odata.context"] = context,
        ["PrincipalAccesses"] = new JsonArray([.. accesses.Select(a => (JsonNode?)new JsonObject
        {
            ["AccessMask"] = FormatRights(a.AccessMask),
            ["Principal"] = AuditEndpoints.Reference(a.Principal, store),
        })]),
    };

    // ---- $metadata --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Declares <c>PrincipalAccess</c>, the two function responses, RetrievePrincipalAccess (bound to systemuser and team),
    /// RetrieveSharedPrincipalsAndAccess (unbound) and the three actions. Runs after <see cref="AuditEndpoints"/>' operations, which
    /// declare <c>crmbaseentity</c> and <c>AccessRights</c>.
    /// </summary>
    internal static void AddOperations(EdmModel model, EdmEntityContainer container, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        var crmBase = (IEdmEntityType)(model.FindDeclaredType($"{Ns}.crmbaseentity")
                                       ?? throw new InvalidOperationException("crmbaseentity must be declared before the access operations."));
        var rights = (IEdmEnumType)(model.FindDeclaredType($"{Ns}.AccessRights")
                                    ?? throw new InvalidOperationException("AccessRights must be declared before the access operations."));
        var crmBaseRef = new EdmEntityTypeReference(crmBase, false);

        var access = new EdmComplexType(Ns, "PrincipalAccess");
        access.AddStructuralProperty("AccessMask", new EdmEnumTypeReference(rights, false));
        access.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo { Name = "Principal", Target = crmBase, TargetMultiplicity = EdmMultiplicity.ZeroOrOne });
        model.AddElement(access);

        var principalResponse = new EdmComplexType(Ns, RetrievePrincipalAccess + "Response");
        principalResponse.AddStructuralProperty("AccessRights", new EdmEnumTypeReference(rights, false));
        model.AddElement(principalResponse);
        foreach (var table in (string[])["systemuser", "team"])
        {
            if (!entityTypes.TryGetValue(table, out var type)) continue;
            var bound = new EdmFunction(Ns, RetrievePrincipalAccess, new EdmComplexTypeReference(principalResponse, false), isBound: true,
                entitySetPathExpression: null, isComposable: false);
            bound.AddParameter("entity", new EdmEntityTypeReference(type, false));
            bound.AddParameter("Target", crmBaseRef);
            model.AddElement(bound);
        }

        var sharedResponse = new EdmComplexType(Ns, RetrieveShared + "Response");
        sharedResponse.AddStructuralProperty("PrincipalAccesses",
            new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(access, false))));
        model.AddElement(sharedResponse);
        var shared = new EdmFunction(Ns, RetrieveShared, new EdmComplexTypeReference(sharedResponse, false), isBound: false,
            entitySetPathExpression: null, isComposable: false);
        shared.AddParameter("Target", crmBaseRef);
        model.AddElement(shared);
        container.AddFunctionImport(RetrieveShared, shared, null, includeInServiceDocument: false);

        foreach (var name in (string[])[Grant, Modify])
        {
            var action = new EdmAction(Ns, name, null, isBound: false, entitySetPathExpression: null);
            action.AddParameter("Target", crmBaseRef);
            action.AddParameter("PrincipalAccess", new EdmComplexTypeReference(access, true));
            model.AddElement(action);
            container.AddActionImport(action);
        }
        var revoke = new EdmAction(Ns, Revoke, null, isBound: false, entitySetPathExpression: null);
        revoke.AddParameter("Target", crmBaseRef);
        revoke.AddParameter("Revokee", crmBaseRef);
        model.AddElement(revoke);
        container.AddActionImport(revoke);
    }
}
