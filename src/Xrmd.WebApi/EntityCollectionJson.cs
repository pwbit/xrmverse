using System.Text.Json.Nodes;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.WebApi;

/// <summary>Writes the result of <c>GET {entityset}</c> as a Web API collection response.</summary>
public static class EntityCollectionJson
{
    /// <summary>
    /// <c>{ "@odata.context": "{baseUrl}/api/data/v9.2/$metadata#{entityset}[(select)]", "@odata.count"?: n, "value": [...],
    /// "@odata.nextLink"?: url }</c>: <c>@odata.count</c> when the total was requested
    /// (<see cref="EntityCollection.TotalRecordCount"/> ≥ 0); each record as <see cref="ODataJson.ToJson"/> writes it,
    /// without <c>@odata.context</c>.
    /// </summary>
    /// <param name="baseUrl">Scheme, host and path base of the server (a trailing slash is ignored).</param>
    /// <param name="select">The <c>$select</c> list, echoed in <c>@odata.context</c>.</param>
    /// <param name="includeFormattedValues">Writes the formatted value annotations of each record.</param>
    /// <param name="nextLink">The URL of the next page; null on the last page.</param>
    /// <param name="expands">The <c>$expand</c> links by alias with their target entity (see <see cref="ODataJson.ToJson"/>).</param>
    /// <param name="unreadable">The secured columns the caller cannot read, by table (see <see cref="ODataJson.ToJson"/>).</param>
    /// <param name="totalCountAnnotations">Writes <c>@Microsoft.Dynamics.CRM.totalrecordcount</c> (−1 when no count was
    /// requested) and <c>@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded</c>, as the <c>Prefer</c> header asked (Learn:
    /// "Count rows by using OData").</param>
    public static JsonObject ToJson(EntityCollection c, EntityMetadata m, string baseUrl, IReadOnlyList<string> select,
        bool includeFormattedValues, string? nextLink, IReadOnlyDictionary<string, ODataExpand>? expands = null,
        IReadOnlyList<OneToManyRelationshipMetadata>? relationships = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? unreadable = null,
        bool totalCountAnnotations = false)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(m);
        var selectClause = select is { Count: > 0 } ? $"({string.Join(',', select)})" : "";
        var json = new JsonObject
        {
            ["@odata.context"] = $"{baseUrl.TrimEnd('/')}{MetadataController.ServiceRootPath}$metadata#{m.EntitySetName}{selectClause}",
        };
        if (c.TotalRecordCount >= 0) json["@odata.count"] = c.TotalRecordCount;
        if (totalCountAnnotations)
        {
            json["@Microsoft.Dynamics.CRM.totalrecordcount"] = c.TotalRecordCount;
            json["@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded"] = c.TotalRecordCountLimitExceeded;
        }
        var value = new JsonArray();
        foreach (var e in c.Entities) value.Add(ODataJson.ToRecordJson(e, m, includeFormattedValues, expands, relationships, unreadable));
        json["value"] = value;
        if (nextLink is not null) json["@odata.nextLink"] = nextLink;
        return json;
    }
}
