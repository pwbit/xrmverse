using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.CustomApis;

namespace Xrmd.WebApi;

/// <summary>
/// Custom APIs as Web API operations (plan 15, C8, ruling X11). <see cref="InvokeAsync"/> is the one entry point for every form:
/// <list type="bullet">
/// <item>global: <c>POST {name}</c> (action), <c>GET {name}(…)</c> or <c>GET {name}</c> (function), each also with the
/// <c>Microsoft.Dynamics.CRM.</c> prefix. One segment, like an entity set: the entity set routes hand a segment that names no
/// entity set over (<see cref="IsGlobalOperation"/>);</item>
/// <item>entity-bound: <c>POST</c> or <c>GET {set}({id})/Microsoft.Dynamics.CRM.{name}</c>, the record passed as
/// <c>Target</c>;</item>
/// <item>collection-bound: <c>POST</c> or <c>GET {set}/Microsoft.Dynamics.CRM.{name}</c>, without <c>Target</c>.</item>
/// </list>
/// The bound forms have their own routes, whose second segment must be an operation segment (<see cref="IsOperationSegment"/>):
/// that constraint ranks them above the file property route <c>{segment}/{property}</c>, and leaves a plain property name to
/// it. The literal routes of the built-in operations (<c>RetrieveAuditDetails</c>, <c>AddPrivilegesRole</c>, …) rank above
/// both. An operation that does not exist, is not bound to the set of the URL, is called on a key-less set while bound to a
/// record (or the reverse), or is an action called with GET or a function with POST, is the 404 of an unknown segment.
/// The request is the API's <see cref="OrganizationRequest"/> (<see cref="CustomApiJson"/>), executed by
/// <see cref="OrganizationService.ExecuteAsync"/>: every check and error is the SDK path's.
/// </summary>
public static class CustomApiEndpoints
{
    /// <summary>The display name of the two bound routes.</summary>
    public const string EndpointName = "Custom API operation";

    private const string Qualifier = ODataEdmModelBuilder.Namespace + ".";
    private const string Target = "Target";

    public static IEndpointRouteBuilder MapCustomApiEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        var pattern = RoutePatternFactory.Parse("/{segment}/{operation}", defaults: null,
            parameterPolicies: new { operation = new OperationSegmentConstraint() });
        foreach (var isGet in (bool[])[false, true])
            routes.Map(pattern, (HttpContext context, OrganizationService service, IMetadataStore store, CustomApiRegistry registry,
                        string segment, string operation) => InvokeAsync(context, service, store, registry, segment, operation, isGet))
                .WithMetadata(new HttpMethodMetadata([isGet ? HttpMethods.Get : HttpMethods.Post]))
                .WithDisplayName(EndpointName)
                .InChangeSet();
        return parent;
    }

    /// <summary>
    /// True when <paramref name="segment"/> can only name an operation: it carries the <c>Microsoft.Dynamics.CRM.</c> prefix,
    /// or a parameter list in parentheses after a name.
    /// </summary>
    public static bool IsOperationSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.StartsWith(Qualifier, StringComparison.Ordinal) || (segment.IndexOf('(') > 0 && segment.EndsWith(')'));
    }

    /// <summary>
    /// True when the single segment <paramref name="segment"/> (<c>{name}</c> or <c>{name}(…)</c>) names no entity set: the
    /// entity set routes then hand it to <see cref="InvokeAsync"/> as a global operation.
    /// </summary>
    public static bool IsGlobalOperation(IMetadataStore store, string segment)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(segment);
        ODataJson.TrySplitKeySegment(segment, out var name, out _);
        return store.GetEntityBySetName(name) is null;
    }

    /// <summary>
    /// Calls the custom API that <paramref name="operation"/> names, bound to <paramref name="boundSegment"/> (<c>{set}({id})</c>
    /// or <c>{set}</c>; null for a global call), as an action (<paramref name="isGet"/> false: parameters in the JSON body) or
    /// a function (true: parameters in the parentheses and the query string).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 <c>0x80060888</c> when the segment names no operation of this form (see the
    /// class); 400 for a navigation property of the bound record in place of the operation; 404 <c>0x8006088a</c> for a
    /// function without response properties; 400 for a value of the wrong type (<see cref="CustomApiJson"/>); any error of the
    /// organization service.</exception>
    public static async Task<IResult> InvokeAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        CustomApiRegistry registry, string? boundSegment, string operation, bool isGet)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(operation);

        var qualified = operation.StartsWith(Qualifier, StringComparison.Ordinal);
        var call = qualified ? operation[Qualifier.Length..] : operation;
        var hasParameterList = ODataJson.TrySplitKeySegment(call, out var name, out var inline);
        var notFound = new XrmdFaultException(ErrorCodes.ResourceNotFound,
            $"Resource not found for the segment '{(qualified ? Qualifier : "")}{name}'.", 404);

        // A bound operation is always named with its namespace, as Dataverse writes them. Without it, a navigation property of
        // the record with a key ({set}({id})/{nav}({key})) gets the file route's explicit 400, not a 404 that hides $expand.
        if (boundSegment is not null && !qualified)
        {
            var (bound, _, _) = EntitySetController.Resolve(store, boundSegment);
            if (FileEndpoints.IsNavigationProperty(store, bound, name)) throw FileEndpoints.NavigationPathSegment(name);
            throw notFound;
        }
        var api = await registry.FindAsync(name, context.RequestAborted);
        if (api is null || !string.Equals(api.UniqueName, name, StringComparison.Ordinal)) throw notFound;

        EntityReference? target = null;
        if (boundSegment is null)
        {
            if (api.BindingType != 0) throw notFound;
        }
        else
        {
            var (set, key, _) = EntitySetController.Resolve(store, boundSegment);
            if (api.BindingType != (key is null ? 2 : 1) || !string.Equals(api.BoundEntity, set.LogicalName, StringComparison.OrdinalIgnoreCase))
                throw notFound;
            if (key is { } id) target = new EntityReference(set.LogicalName, id);
        }
        if (api.IsFunction != isGet || (!api.IsFunction && hasParameterList)) throw notFound;
        if (api.IsFunction && api.ResponseProperties.Count == 0) throw XrmdFaultException.FunctionWithoutResponseNotFound(api.UniqueName);

        var parameters = isGet
            ? CustomApiJson.ReadFunctionParameters(api, inline, context.Request.Query, store)
            : CustomApiJson.ReadBody(api, await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true), store,
                ActionController.IsIeee754Compatible(context.Request.ContentType));
        CustomApiJson.ReadTag(api, parameters, context.Request.Query);
        var request = new OrganizationRequest(api.UniqueName) { Parameters = parameters };
        if (target is not null) request[Target] = target;

        var response = await service.ExecuteAsync(request, context.RequestAborted);
        return CustomApiJson.WriteResponse(api, response, ODataJson.BaseUrl(context.Request), store);
    }

    /// <summary>The route constraint of the bound routes' <c>{operation}</c>: <see cref="IsOperationSegment"/>.</summary>
    private sealed class OperationSegmentConstraint : IRouteConstraint
    {
        public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
            values.TryGetValue(routeKey, out var value) && value?.ToString() is { } segment && IsOperationSegment(segment);
    }
}
