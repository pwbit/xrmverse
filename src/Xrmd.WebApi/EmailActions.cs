using System.Text.Json;
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
using Xrmd.Metadata.Sdk;

namespace Xrmd.WebApi;

/// <summary>
/// The Web API side of the activity messages (plan 2.11). <c>POST emails({id})/Microsoft.Dynamics.CRM.SendEmail</c> with
/// <c>{"IssueSend":false,"TrackingToken":"…"}</c> runs the SDK's SendEmail through <see cref="OrganizationService"/>, so the
/// pipeline and the security checks apply, and answers 200 with
/// <c>{"@odata.context":"…$metadata#Microsoft.Dynamics.CRM.SendEmailResponse","Subject":"…"}</c> (A17). A missing
/// <c>IssueSend</c>, a value of the wrong type or an unknown parameter is 400; another entity set is 404. The refused activity
/// and note messages (A18) answer 400 "not supported" as unbound actions (<c>POST /{name}</c>), as bound actions
/// (<c>POST {set}({id})/Microsoft.Dynamics.CRM.{name}</c>) and as functions (<c>GET /{name}(…)</c>) instead of the generic
/// "resource not found". They throw the 400 fault themselves: the Web API answers a <see cref="NotSupportedException"/> with 500.
/// </summary>
public static class EmailActions
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string SendEmail = EmailMessages.SendEmail;

    public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapPost("/{segment}/" + Ns + "." + SendEmail, (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
            SendAsync(c, s, o, segment));
        foreach (var name in EmailMessages.Refused)
        {
            routes.MapPost("/" + name, () => Refuse(name));
            routes.MapGet("/" + name + "({parameters})", (string parameters) => Refuse(name));
            // The bound form (AddRecurrence on appointment, …), for any entity set (review M2).
            routes.MapPost("/{segment}/" + Ns + "." + name, (string segment) => Refuse(name));
        }
        return parent;
    }

    private static IResult Refuse(string name) => throw XrmdFaultException.InvalidArgument(ActivitySchema.RefusedMessageText(name));

    private static async Task<IResult> SendAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment)
    {
        var emailId = ParseEmail(store, segment);
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        foreach (var (name, _) in body)
            if (name is not ("IssueSend" or "TrackingToken") && !name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{SendEmail}'.");
        var request = new SendEmailRequest
        {
            EmailId = emailId,
            IssueSend = body["IssueSend"] is JsonValue issue && issue.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? issue.GetValue<bool>()
                : throw XrmdFaultException.InvalidArgument($"The parameter 'IssueSend' of operation '{SendEmail}' is required and must be an Edm.Boolean."),
        };
        switch (body["TrackingToken"])
        {
            case null:
                break;
            case JsonValue token when token.GetValueKind() == JsonValueKind.String:
                request.TrackingToken = token.GetValue<string>();
                break;
            default:
                throw XrmdFaultException.InvalidArgument($"The parameter 'TrackingToken' of operation '{SendEmail}' must be an Edm.String.");
        }

        var response = await service.ExecuteAsync(request, context.RequestAborted);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.Json(new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{Ns}.{SendEmail}Response",
            ["Subject"] = response.Results.TryGetValue("Subject", out var subject) ? subject as string : null,
        });
    }

    /// <summary>The email of <c>emails({id})</c>; another entity set is 404 (the action is bound to email).</summary>
    private static Guid ParseEmail(IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The bound action {SendEmail} requires an email: 'emails(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != ActivitySchema.Email)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound,
                $"Resource not found for the segment '{segment}/{Ns}.{SendEmail}': the action is bound to email.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
    }

    /// <summary>Declares the bound SendEmail action and its response complex type in <c>$metadata</c>.</summary>
    internal static void AddOperations(EdmModel model, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        if (!entityTypes.TryGetValue(ActivitySchema.Email, out var email)) return;
        var response = new EdmComplexType(Ns, SendEmail + "Response");
        response.AddStructuralProperty("Subject", EdmPrimitiveTypeKind.String, isNullable: true);
        model.AddElement(response);
        var action = new EdmAction(Ns, SendEmail, new EdmComplexTypeReference(response, false), isBound: true, entitySetPathExpression: null);
        action.AddParameter("entity", new EdmEntityTypeReference(email, false));
        action.AddParameter("IssueSend", EdmCoreModel.Instance.GetBoolean(false));
        action.AddParameter("TrackingToken", EdmCoreModel.Instance.GetString(true));
        model.AddElement(action);
    }
}
