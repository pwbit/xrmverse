using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;

namespace Xrmd.WebApi;

/// <summary>
/// Association by reference on collection-valued navigation properties (the navigation property is the relationship
/// <c>SchemaName</c>), translated to <see cref="AssociateRequest"/>/<see cref="DisassociateRequest"/> and executed through
/// <see cref="OrganizationService"/> so that every security check and plugin step applies.
/// <list type="bullet">
/// <item><c>POST {entityset}({id})/{relationship}/$ref</c> with <c>{"@odata.id":"&lt;url&gt;"}</c>: 204.</item>
/// <item><c>DELETE {entityset}({id})/{relationship}({relatedid})/$ref</c> or <c>…/{relationship}/$ref?$id=&lt;url&gt;</c>: 204.</item>
/// </list>
/// The URL is absolute (any host, with <c>/api/data/v9.x/</c>) or relative; a navigation property that is not a relationship, a
/// URL of an unknown entity set and a malformed body are 400. PUT on a single-valued navigation property is not routed.
/// </summary>
public static class RefController
{
    private const string ODataId = "@odata.id";

    public static IEndpointRouteBuilder MapRefEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapPost("/{segment}/{navigation}/$ref", PostAsync).InChangeSet();
        routes.MapDelete("/{segment}/{navigation}/$ref", DeleteAsync).InChangeSet();
        return parent;
    }

    private static async Task<IResult> PostAsync(
        HttpContext context, IMetadataStore store, OrganizationService service, string segment, string navigation)
    {
        var target = ParseTarget(store, segment);
        var body = await ODataJson.ReadBodyAsync(context.Request);
        var url = body.TryGetPropertyValue(ODataId, out var node) && node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)
                  && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw XrmdFaultException.InvalidArgument($"The request body must be {{\"{ODataId}\": \"<record url>\"}}.");
        var related = ParseReference(store, url);
        return await ExecuteAsync(context, service, store, target, navigation, related, associate: true);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context, IMetadataStore store, OrganizationService service, string segment, string navigation)
    {
        var target = ParseTarget(store, segment);
        EntityReference related;
        if (ODataJson.TrySplitKeySegment(navigation, out var name, out var key))
        {
            if (!Guid.TryParseExact(key, "D", out var id))
                throw XrmdFaultException.InvalidArgument($"Invalid key '{key}' for navigation property '{name}': a GUID is expected.");
            navigation = name;
            related = new EntityReference(RelatedEntity(store, navigation, target.LogicalName), id);
        }
        else
        {
            var url = context.Request.Query["$id"].ToString();
            related = url.Length > 0
                ? ParseReference(store, url)
                : throw XrmdFaultException.InvalidArgument(
                    $"DELETE on '{navigation}/$ref' requires the record: use '{navigation}(<id>)/$ref' or '$id=<record url>'.");
        }
        return await ExecuteAsync(context, service, store, target, navigation, related, associate: false);
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        EntityReference target, string navigation, EntityReference related, bool associate)
    {
        // Fail explicitly on a navigation property that is not a relationship, or a record of the wrong entity.
        var expected = RelatedEntity(store, navigation, target.LogicalName);
        if (related.LogicalName != expected)
            throw XrmdFaultException.InvalidArgument(
                $"Navigation property '{navigation}' of '{target.LogicalName}' refers to '{expected}' records, not '{related.LogicalName}'.");
        var relationship = new Relationship(navigation);
        var references = new EntityReferenceCollection { related };
        OrganizationRequest request = associate
            ? new AssociateRequest { Target = target, Relationship = relationship, RelatedEntities = references }
            : new DisassociateRequest { Target = target, Relationship = relationship, RelatedEntities = references };
        await service.ExecuteAsync(request, context.RequestAborted);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.NoContent();
    }

    /// <summary>The record of <c>{entityset}({guid})</c>.</summary>
    private static EntityReference ParseTarget(IMetadataStore store, string segment)
    {
        if (!ODataJson.TrySplitKeySegment(segment, out var setName, out var keyText))
            throw XrmdFaultException.InvalidArgument($"The navigation property requires a record: '{setName}(<id>)'.");
        var m = store.GetEntityBySetName(setName)
                ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        return Guid.TryParseExact(keyText, "D", out var id)
            ? new EntityReference(m.LogicalName, id)
            : throw XrmdFaultException.InvalidArgument(
                $"Invalid key '{keyText}' for entity set '{setName}': only the primary key (a GUID) is supported.");
    }

    /// <summary>The record of an <c>@odata.id</c>/<c>$id</c> URL: the last path segment <c>{entityset}({guid})</c> of an absolute or relative URL.</summary>
    public static EntityReference ParseReference(IMetadataStore store, string url)
    {
        var path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)) path = absolute.AbsolutePath;
        else if (url.IndexOfAny(['?', '#']) is var cut and >= 0) path = url[..cut];
        var last = path.TrimEnd('/').Split('/')[^1];
        last = Uri.UnescapeDataString(last);
        if (!ODataJson.TrySplitKeySegment(last, out var setName, out var keyText) || !Guid.TryParseExact(keyText, "D", out var id))
            throw XrmdFaultException.InvalidArgument($"Invalid record reference '{url}': expected \"<service root>/<entityset>(<id>)\".");
        var m = store.GetEntityBySetName(setName)
                ?? throw XrmdFaultException.InvalidArgument($"Invalid record reference '{url}': entity set '{setName}' does not exist.");
        return new EntityReference(m.LogicalName, id);
    }

    /// <summary>The entity on the other side of relationship <paramref name="navigation"/> seen from <paramref name="targetEntity"/>.</summary>
    /// <exception cref="XrmdFaultException">400 when the navigation property is not a relationship of the target entity.</exception>
    private static string RelatedEntity(IMetadataStore store, string navigation, string targetEntity)
    {
        string? other = null;
        if (SecurityAdministration.FindRelationship(navigation) is { } security)
            other = Other(security.Entity1, security.Entity2);
        else
            switch (store.GetRelationship(navigation))
            {
                case ManyToManyRelationshipMetadata mm:
                    other = Other(mm.Entity1LogicalName, mm.Entity2LogicalName);
                    break;
                case OneToManyRelationshipMetadata om:
                    // Collection-valued from the referenced ("one") side only; the single-valued side is not supported.
                    other = targetEntity == om.ReferencedEntity ? om.ReferencingEntity : null;
                    break;
            }
        return other ?? throw XrmdFaultException.InvalidArgument(
            $"'{navigation}' is not a navigation property of '{targetEntity}': association by reference supports relationships only.");

        string? Other(string a, string b) => targetEntity == a ? b : targetEntity == b ? a : null;
    }
}
