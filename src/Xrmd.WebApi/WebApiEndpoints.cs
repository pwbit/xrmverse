using Microsoft.AspNetCore.Routing;

namespace Xrmd.WebApi;

public static class WebApiEndpoints
{
    /// <summary>
    /// Every route of the Web API on <paramref name="parent"/> (a <c>/api/data/v9.x</c> group). Routing chooses by precedence,
    /// not by this order: the fixed routes (<c>$metadata</c>, <c>EntityDefinitions</c>, the operations, <c>$ref</c>, the bound
    /// actions, the file properties) win over the generic <c>{entityset}</c> routes because their segments are more specific.
    /// </summary>
    public static IEndpointRouteBuilder MapWebApiEndpoints(this IEndpointRouteBuilder parent)
    {
        parent.MapMetadataEndpoints();
        parent.MapFunctionEndpoints();
        parent.MapActionEndpoints();
        parent.MapRefEndpoints();
        parent.MapRolePrivilegeEndpoints();
        parent.MapRecordTeamEndpoints();
        parent.MapAccessEndpoints();
        parent.MapExecuteWorkflowEndpoints();
        parent.MapEmailEndpoints();
        parent.MapDuplicateDetectionEndpoints();
        parent.MapRollupEndpoints();
        // The bulk actions of elastic tables: {set}/Microsoft.Dynamics.CRM.{CreateMultiple,…}, literal segments above the custom APIs.
        parent.MapElasticEndpoints();
        // Before the file and entity set routes, whose /{segment} patterns would otherwise also match the audit operations.
        parent.MapAuditEndpoints();
        // The bound custom API operations: {set}({id})/{operation} and {set}/{operation}, ranked above the file property
        // route by the operation segment constraint. The global ones go through the entity set routes.
        parent.MapCustomApiEndpoints();
        parent.MapFileEndpoints();
        parent.MapEntitySetEndpoints();
        return parent;
    }
}
