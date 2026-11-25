using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xrmd.Core;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi.Connector;

/// <summary>
/// X.12a: the design-time endpoints of the custom connector under <c>/api/connector/v1</c>: the table and custom API pickers and
/// their JSON Schemas. Errors use the Web API envelope (<see cref="XrmdFaultException"/>).
/// </summary>
public static class ConnectorEndpoints
{
    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder api)
    {
        var routes = api.MapGroup("").RequireOrganizationMember();

        routes.MapGet("/tables", async (HttpContext http, CallerResolver callers, ConnectorCatalog catalog) =>
        {
            var tables = await catalog.ListTablesAsync(await callers.ResolveAsync(http.RequestAborted), http.RequestAborted);
            return Results.Json(new { value = tables.Select(t => new { logicalName = t.LogicalName, entitySetName = t.EntitySetName, displayName = t.DisplayName }) });
        });

        routes.MapGet("/tables/{name}/schema", async (HttpContext http, CallerResolver callers, ConnectorCatalog catalog, string name, string? use) =>
        {
            var parsed = use?.ToLowerInvariant() switch
            {
                "read" => SchemaUse.Read,
                "create" => SchemaUse.Create,
                "update" => SchemaUse.Update,
                _ => throw new XrmdFaultException(ErrorCodes.InvalidArgument, "The query option 'use' must be 'read', 'create' or 'update'.", 400),
            };
            var schema = await catalog.GetTableSchemaAsync(await callers.ResolveAsync(http.RequestAborted), name, parsed, http.RequestAborted);
            return Results.Json(schema);
        });

        routes.MapGet("/customapis", async (HttpContext http, CallerResolver callers, ConnectorCatalog catalog) =>
        {
            var apis = await catalog.ListCustomApisAsync(await callers.ResolveAsync(http.RequestAborted), http.RequestAborted);
            return Results.Json(new { value = apis.Select(a => new { uniqueName = a.UniqueName, displayName = a.DisplayName }) });
        });

        routes.MapGet("/customapis/{uniqueName}/schema", async (HttpContext http, CallerResolver callers, ConnectorCatalog catalog, string uniqueName, string? part) =>
        {
            var request = part?.ToLowerInvariant() switch
            {
                "request" => true,
                "response" => false,
                _ => throw new XrmdFaultException(ErrorCodes.InvalidArgument, "The query option 'part' must be 'request' or 'response'.", 400),
            };
            var api = await catalog.GetRunnableCustomApiAsync(await callers.ResolveAsync(http.RequestAborted), uniqueName, http.RequestAborted);
            return Results.Json(CustomApiSchemaBuilder.Build(api, request));
        });

        // X.12b: the flow trigger's webhook subscriptions. The callback URL in the body is a secret: never logged or echoed.
        routes.MapPost("/subscriptions", async (HttpContext http, CallerResolver callers, FlowSubscriptions subscriptions, SubscribeBody body) =>
        {
            var id = await subscriptions.SubscribeAsync(await callers.ResolveAsync(http.RequestAborted), body, http.RequestAborted);
            var location = $"{ODataJson.BaseUrl(http.Request)}{http.Request.Path.Value!.TrimEnd('/')}/{id}";
            return Results.Created(location, new { id });
        });

        routes.MapDelete("/subscriptions/{id:guid}", async (HttpContext http, CallerResolver callers, FlowSubscriptions subscriptions, Guid id) =>
        {
            await subscriptions.UnsubscribeAsync(await callers.ResolveAsync(http.RequestAborted), id, http.RequestAborted);
            return Results.NoContent();
        });

        return api;
    }
}
