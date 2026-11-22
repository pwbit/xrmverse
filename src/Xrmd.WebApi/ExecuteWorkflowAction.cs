using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// The Web API action <c>ExecuteWorkflow</c> bound to <c>workflow</c> (plan 2.14, ruling W21), executed as the SDK request through
/// <see cref="OrganizationService"/> so the pipeline and the security checks apply: <c>POST workflows({id})/Microsoft.Dynamics.CRM.ExecuteWorkflow</c>
/// with <c>{"EntityId":"…"}</c>: 200 with <c>{"@odata.context":"…$metadata#Microsoft.Dynamics.CRM.ExecuteWorkflowResponse","Id":"…"}</c>
/// (the job id, or the empty Guid for a real-time workflow). A missing or malformed <c>EntityId</c> or any other parameter is 400.
/// </summary>
public static class ExecuteWorkflowAction
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string Name = "ExecuteWorkflow";
    private const string EntityId = "EntityId";

    public static IEndpointRouteBuilder MapExecuteWorkflowEndpoints(this IEndpointRouteBuilder parent)
    {
        parent.MapGroup("").RequireOrganizationMember().MapPost("/{segment}/" + Ns + "." + Name,
            (HttpContext c, IMetadataStore s, OrganizationService o, string segment) => PostAsync(c, s, o, segment));
        return parent;
    }

    private static async Task<IResult> PostAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment)
    {
        var workflowId = ParseWorkflow(store, segment);
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        foreach (var (name, _) in body)
            if (name != EntityId && !name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{Name}'.");
        var entityId = body[EntityId] is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"The parameter '{EntityId}' of operation '{Name}' is required and must be an Edm.Guid.");

        var response = await service.ExecuteAsync(new ExecuteWorkflowRequest { WorkflowId = workflowId, EntityId = entityId }, context.RequestAborted);
        if (!response.Results.TryGetValue("Id", out var value) || value is not Guid job)
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"{Name} returned no Id.", 500);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.Json(new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{Ns}.{Name}Response",
            ["Id"] = job,
        });
    }

    private static Guid ParseWorkflow(IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument("The bound action requires a process: 'workflows(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != WorkflowSchema.Table)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Resource not found for the segment '{segment}/{Ns}.{Name}': the action is bound to workflow.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
    }

    /// <summary>Declares the bound action and its response complex type in <c>$metadata</c>.</summary>
    internal static void AddOperations(EdmModel model, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        if (!entityTypes.TryGetValue(WorkflowSchema.Table, out var workflow)) return;
        var response = new EdmComplexType(Ns, Name + "Response");
        response.AddStructuralProperty("Id", EdmPrimitiveTypeKind.Guid, isNullable: false);
        model.AddElement(response);
        var action = new EdmAction(Ns, Name, new EdmComplexTypeReference(response, false), isBound: true, entitySetPathExpression: null);
        action.AddParameter("entity", new EdmEntityTypeReference(workflow, false));
        action.AddParameter(EntityId, EdmCoreModel.Instance.GetGuid(false));
        model.AddElement(action);
    }
}
