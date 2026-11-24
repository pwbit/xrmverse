using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Xrmd.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;

namespace Xrmd.WebApi;

/// <summary>
/// The Web API actions bound to <c>systemuser</c> (plan 2.16, ruling A10), executed as the SDK requests of the same name through
/// <see cref="OrganizationService"/> so the pipeline and the security checks apply:
/// <c>POST systemusers({id})/Microsoft.Dynamics.CRM.AddUserToRecordTeam</c> and <c>…RemoveUserFromRecordTeam</c> with
/// <c>{"Record":{"@odata.type":"Microsoft.Dynamics.CRM.{table}","{table}id":"…"},"TeamTemplate":{"teamtemplateid":"…"}}</c>:
/// 200 with <c>{"@odata.context":"…$metadata#Microsoft.Dynamics.CRM.{Action}Response","AccessTeamId":"…"}</c>.
/// A missing or untyped <c>Record</c>, a missing id or an unknown top-level parameter is 400; other properties of Record and TeamTemplate are ignored.
/// </summary>
public static class RecordTeamActions
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string AddName = "AddUserToRecordTeam";
    private const string RemoveName = "RemoveUserFromRecordTeam";

    public static IEndpointRouteBuilder MapRecordTeamEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapPost("/{segment}/" + Ns + "." + AddName, (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
            PostAsync(c, s, o, segment, AddName)).InChangeSet();
        routes.MapPost("/{segment}/" + Ns + "." + RemoveName, (HttpContext c, IMetadataStore s, OrganizationService o, string segment) =>
            PostAsync(c, s, o, segment, RemoveName)).InChangeSet();
        return parent;
    }

    private static async Task<IResult> PostAsync(HttpContext context, IMetadataStore store, OrganizationService service, string segment, string action)
    {
        var userId = ParseUser(store, segment);
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        foreach (var (name, _) in body)
            if (name is not ("Record" or "TeamTemplate") && !name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{action}'.");
        if (body["Record"] is null)
            throw XrmdFaultException.InvalidArgument($"The parameter 'Record' of operation '{action}' is required.");
        var record = ReadRecord(body["Record"]!, action, store);
        var template = ReadTemplate(body, action);

        OrganizationResponse response = action == AddName
            ? await service.ExecuteAsync(new AddUserToRecordTeamRequest { Record = record, SystemUserId = userId, TeamTemplateId = template }, context.RequestAborted)
            : await service.ExecuteAsync(new RemoveUserFromRecordTeamRequest { Record = record, SystemUserId = userId, TeamTemplateId = template }, context.RequestAborted);
        if (!response.Results.TryGetValue("AccessTeamId", out var value) || value is not Guid team)
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"{action} returned no AccessTeamId.", 500);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.Json(new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{Ns}.{action}Response",
            ["AccessTeamId"] = team,
        });
    }

    private static Guid ParseUser(IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument("The bound action requires a user: 'systemusers(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != "systemuser")
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{segment}/{Ns}.…': the action is bound to systemuser.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
    }

    /// <summary>The <c>Record</c>: its <c>@odata.type</c> names the table and its primary key the record; other columns are ignored.</summary>
    private static EntityReference ReadRecord(JsonNode node, string action, IMetadataStore store)
    {
        if (node is not JsonObject record)
            throw XrmdFaultException.InvalidArgument($"The parameter 'Record' of operation '{action}' must be an entity ({Ns}.crmbaseentity) object.");
        var type = record["@odata.type"] is JsonValue t && t.TryGetValue<string>(out var typeName) ? typeName.TrimStart('#') : null;
        if (type is null)
            throw XrmdFaultException.InvalidArgument($"The parameter 'Record' of operation '{action}' must carry '@odata.type' naming its entity type.");
        if (type.StartsWith(Ns + ".", StringComparison.Ordinal)) type = type[(Ns.Length + 1)..];
        var table = store.GetEntity(type)
                    ?? throw XrmdFaultException.InvalidArgument($"The parameter 'Record' of operation '{action}' names the entity type '{type}', which does not exist.");
        var key = SystemAttributes.PrimaryIdName(table);
        return record[key] is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id)
            ? new EntityReference(table.LogicalName, id)
            : throw XrmdFaultException.InvalidArgument($"The parameter 'Record' of operation '{action}' must carry its key '{key}' as an Edm.Guid.");
    }

    private static Guid ReadTemplate(JsonObject body, string action)
    {
        if (body["TeamTemplate"] is not JsonObject template)
            throw XrmdFaultException.InvalidArgument($"The parameter 'TeamTemplate' of operation '{action}' must be a {Ns}.teamtemplate object.");
        foreach (var (name, value) in template)
        {
            if (name == "@odata.type")
            {
                var type = value is JsonValue t && t.TryGetValue<string>(out var s) ? s.TrimStart('#') : null;
                if (type != Ns + ".teamtemplate")
                    throw XrmdFaultException.InvalidArgument($"The parameter 'TeamTemplate' of operation '{action}' must be a {Ns}.teamtemplate object.");
            }
            // Other properties are ignored: Dataverse accepts a full entity payload.
        }
        return template["teamtemplateid"] is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"The parameter 'TeamTemplate/teamtemplateid' of operation '{action}' must be an Edm.Guid.");
    }

    /// <summary>Declares the two bound actions and their response complex types in <c>$metadata</c>.</summary>
    internal static void AddOperations(EdmModel model, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        if (!entityTypes.TryGetValue("systemuser", out var user) || model.FindDeclaredType($"{Ns}.crmbaseentity") is not IEdmEntityType baseEntity) return;
        foreach (var name in new[] { AddName, RemoveName })
        {
            var response = new EdmComplexType(Ns, name + "Response");
            response.AddStructuralProperty("AccessTeamId", EdmPrimitiveTypeKind.Guid, isNullable: false);
            model.AddElement(response);
            var action = new EdmAction(Ns, name, new EdmComplexTypeReference(response, false), isBound: true, entitySetPathExpression: null);
            action.AddParameter("entity", new EdmEntityTypeReference(user, false));
            action.AddParameter("Record", new EdmEntityTypeReference(baseEntity, false));
            action.AddParameter("TeamTemplate", new EdmEntityTypeReference(
                entityTypes.TryGetValue("teamtemplate", out var template) ? template : baseEntity, false));
            model.AddElement(action);
        }
    }
}
