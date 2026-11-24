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
/// The Web API actions bound to <c>role</c> (Learn shapes), executed as the SDK requests of the same name through
/// <see cref="OrganizationService"/> so the pipeline and the security checks apply:
/// <list type="bullet">
/// <item><c>POST roles({id})/Microsoft.Dynamics.CRM.AddPrivilegesRole</c> with
/// <c>{"Privileges":[{"Depth":"Basic|Local|Deep|Global","PrivilegeId":"…","BusinessUnitId":"…"}]}</c>: 204. <c>Depth</c> is the
/// <c>PrivilegeDepth</c> enum member name (or its number 0-3, the SDK's numbering).</item>
/// <item><c>POST roles({id})/Microsoft.Dynamics.CRM.RemovePrivilegeRole</c> with
/// <c>{"Privilege":{"@odata.type":"Microsoft.Dynamics.CRM.privilege","privilegeid":"…"}}</c>: 204.</item>
/// </list>
/// A missing or unknown <c>Depth</c>, a malformed id or an unknown property is 400.
/// </summary>
public static class RolePrivilegeActions
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string AddName = "AddPrivilegesRole";
    private const string RemoveName = "RemovePrivilegeRole";

    public static IEndpointRouteBuilder MapRolePrivilegeEndpoints(this IEndpointRouteBuilder parent)
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
        var roleId = ParseRole(store, segment);
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        OrganizationRequest request = action == AddName
            ? new AddPrivilegesRoleRequest { RoleId = roleId, Privileges = ReadPrivileges(body) }
            : new RemovePrivilegeRoleRequest { RoleId = roleId, PrivilegeId = ReadPrivilegeId(body) };
        await service.ExecuteAsync(request, context.RequestAborted);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.NoContent();
    }

    private static Guid ParseRole(IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The bound action requires a role: 'roles(<id>)'.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        if (entity.LogicalName != "role")
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{segment}/{Ns}.…': the action is bound to role.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid key '{keyText}' for entity set '{setName}': a GUID is expected.");
    }

    private static RolePrivilege[] ReadPrivileges(JsonObject body)
    {
        foreach (var (name, _) in body)
            if (name != "Privileges" && !name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{AddName}'.");
        if (body["Privileges"] is not JsonArray items)
            throw XrmdFaultException.InvalidArgument($"The parameter 'Privileges' of operation '{AddName}' must be a Collection({Ns}.RolePrivilege).");
        var result = new List<RolePrivilege>();
        foreach (var item in items)
        {
            if (item is not JsonObject o)
                throw XrmdFaultException.InvalidArgument($"The parameter 'Privileges' of operation '{AddName}' must be a Collection({Ns}.RolePrivilege).");
            foreach (var (name, _) in o)
                if (name is not ("Depth" or "PrivilegeId" or "BusinessUnitId" or "PrivilegeName") && !name.StartsWith('@'))
                    throw XrmdFaultException.InvalidArgument($"The property '{name}' of parameter 'Privileges' is not a property of {Ns}.RolePrivilege.");
            var privilege = new RolePrivilege
            {
                PrivilegeId = ReadGuid(o["PrivilegeId"], "Privileges/PrivilegeId", required: true) ?? Guid.Empty,
                Depth = ReadDepth(o["Depth"]),
                BusinessUnitId = ReadGuid(o["BusinessUnitId"], "Privileges/BusinessUnitId", required: false) ?? Guid.Empty,
            };
            result.Add(privilege);
        }
        return result.ToArray();
    }

    private static readonly PrivilegeDepth[] Depths = [PrivilegeDepth.Basic, PrivilegeDepth.Local, PrivilegeDepth.Deep, PrivilegeDepth.Global];

    private static PrivilegeDepth ReadDepth(JsonNode? node)
    {
        const string problem = "The property 'Depth' of parameter 'Privileges' must be Basic, Local, Deep or Global (or 0 to 3).";
        if (node is not JsonValue v) throw XrmdFaultException.InvalidArgument(problem);
        if (v.TryGetValue<string>(out var text))
        {
            // Exactly one member name: Enum.TryParse would also accept "Local,Deep" (OR-ed to Global) or a number.
            foreach (var depth in Depths)
                if (string.Equals(text, depth.ToString(), StringComparison.OrdinalIgnoreCase)) return depth;
            throw XrmdFaultException.InvalidArgument(problem);
        }
        return v.TryGetValue<int>(out var number) && number is >= (int)PrivilegeDepth.Basic and <= (int)PrivilegeDepth.Global
            ? (PrivilegeDepth)number
            : throw XrmdFaultException.InvalidArgument(problem);
    }

    private static Guid ReadPrivilegeId(JsonObject body)
    {
        foreach (var (name, _) in body)
            if (name != "Privilege" && !name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument(
                    $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{RemoveName}'.");
        if (body["Privilege"] is not JsonObject privilege)
            throw XrmdFaultException.InvalidArgument($"The parameter 'Privilege' of operation '{RemoveName}' must be a {Ns}.privilege object.");
        foreach (var (name, value) in privilege)
        {
            if (name == "@odata.type")
            {
                var type = value is JsonValue t && t.TryGetValue<string>(out var s) ? s.TrimStart('#') : null;
                if (type != Ns + ".privilege")
                    throw XrmdFaultException.InvalidArgument($"The parameter 'Privilege' of operation '{RemoveName}' must be a {Ns}.privilege object.");
            }
            else if (name != "privilegeid")
                throw XrmdFaultException.InvalidArgument(
                    $"The property '{name}' of parameter 'Privilege' is not supported: only 'privilegeid' identifies the privilege.");
        }
        return ReadGuid(privilege["privilegeid"], "Privilege/privilegeid", required: true)!.Value;
    }

    private static Guid? ReadGuid(JsonNode? node, string parameter, bool required)
    {
        if (node is null)
            return required ? throw XrmdFaultException.InvalidArgument($"The parameter '{parameter}' is required.") : null;
        return node is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"The parameter '{parameter}' must be an Edm.Guid.");
    }

    /// <summary>Declares the two bound actions, <c>PrivilegeDepth</c> and <c>RolePrivilege</c> in <c>$metadata</c>.</summary>
    internal static void AddOperations(EdmModel model, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        if (!entityTypes.TryGetValue("role", out var role) || !entityTypes.TryGetValue("privilege", out var privilege)) return;
        var depth = new EdmEnumType(Ns, "PrivilegeDepth");
        foreach (var (name, value) in new[] { ("Basic", 0L), ("Local", 1L), ("Deep", 2L), ("Global", 3L) }) depth.AddMember(name, new EdmEnumMemberValue(value));
        model.AddElement(depth);
        var rolePrivilege = new EdmComplexType(Ns, "RolePrivilege");
        rolePrivilege.AddStructuralProperty("BusinessUnitId", EdmPrimitiveTypeKind.Guid, isNullable: true);
        rolePrivilege.AddStructuralProperty("Depth", new EdmEnumTypeReference(depth, false));
        rolePrivilege.AddStructuralProperty("PrivilegeId", EdmPrimitiveTypeKind.Guid, isNullable: false);
        rolePrivilege.AddStructuralProperty("PrivilegeName", EdmPrimitiveTypeKind.String, isNullable: true); // returned by RetrieveUserPrivileges
        model.AddElement(rolePrivilege);

        var add = new EdmAction(Ns, AddName, null, isBound: true, entitySetPathExpression: null);
        add.AddParameter("entity", new EdmEntityTypeReference(role, false));
        add.AddParameter("Privileges", new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(rolePrivilege, false))));
        model.AddElement(add);
        var remove = new EdmAction(Ns, RemoveName, null, isBound: true, entitySetPathExpression: null);
        remove.AddParameter("entity", new EdmEntityTypeReference(role, false));
        remove.AddParameter("Privilege", new EdmEntityTypeReference(privilege, false));
        model.AddElement(remove);

        // RetrieveUserPrivileges, bound to systemuser (declared here: it returns RolePrivilege).
        if (!entityTypes.TryGetValue("systemuser", out var user)) return;
        var response = new EdmComplexType(Ns, "RetrieveUserPrivilegesResponse");
        response.AddStructuralProperty("RolePrivileges", new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(rolePrivilege, false))));
        model.AddElement(response);
        var retrieve = new EdmFunction(Ns, "RetrieveUserPrivileges", new EdmComplexTypeReference(response, false), isBound: true,
            entitySetPathExpression: null, isComposable: false);
        retrieve.AddParameter("entity", new EdmEntityTypeReference(user, false));
        model.AddElement(retrieve);
    }
}
