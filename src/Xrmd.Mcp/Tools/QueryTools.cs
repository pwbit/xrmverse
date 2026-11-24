using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using ModelContextProtocol.Server;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.WebApi;

namespace Xrmd.Mcp.Tools;

/// <summary>
/// <c>query</c>: FetchXML (parsed as the Web API's <c>fetchXml</c> option) or an OData query in Web API syntax
/// (<see cref="ODataQueryParser"/>), run by <see cref="OrganizationService"/> as the caller.
/// Output: <c>{"records":[…],"moreRecords":bool,"pagingCookie":string?}</c>, each record as the Web API writes it.
/// <para>
/// <c>pageSize</c> defaults to 50 (FetchXML: its <c>count</c>, else 50) and may not exceed 500, nor may <c>$top</c>/<c>top</c>.
/// <c>pagingCookie</c> is opaque: the Web API's <c>$skiptoken</c> form
/// (<c>&lt;cookie pagenumber="N" pagingcookie="…" istracking="False" /&gt;</c>) of the next page, to be sent back with the same
/// query; absent on the last page and with <c>top</c>.
/// </para>
/// </summary>
[McpServerToolType]
public static class QueryTools
{
    [McpServerTool(Name = "query", ReadOnly = true)]
    [Description("Queries records with FetchXML or an OData query in Web API syntax (\"accounts?$select=name&$filter=startswith(name,'A')\"). Give exactly one of fetchXml or odata. Returns {records, moreRecords, pagingCookie}; pass pagingCookie back with the same query for the next page.")]
    public static Task<string> Query(IServiceProvider services,
        [Description("A FetchXML query.")] string? fetchXml = null,
        [Description("An OData query in Web API syntax: {entityset}?$select=…&$filter=…&$orderby=…&$top=…&$expand=…&$apply=…")] string? odata = null,
        [Description("Records per page: default 50, maximum 500.")] int? pageSize = null,
        [Description("The pagingCookie of the previous page, to read the next one.")] string? pagingCookie = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            if (string.IsNullOrWhiteSpace(fetchXml) == string.IsNullOrWhiteSpace(odata))
                throw XrmdFaultException.InvalidArgument("Give exactly one of 'fetchXml' or 'odata'.");
            if (pageSize is { } size && size is < 1 or > McpLimits.MaxPageSize)
                throw XrmdFaultException.InvalidArgument($"'pageSize' must be between 1 and {McpLimits.MaxPageSize}, got {size}.");

            var store = request.GetRequiredService<IMetadataStore>();
            var (m, model) = string.IsNullOrWhiteSpace(odata)
                ? FetchModel(store, fetchXml!, pageSize, pagingCookie)
                : ODataModel(store, request.GetRequiredService<ODataQueryParser>(), odata, pageSize, pagingCookie);
            if (model.Top is > McpLimits.MaxPageSize)
                throw XrmdFaultException.InvalidArgument($"top must be between 1 and {McpLimits.MaxPageSize} in the MCP query tool, got {model.Top}.");

            var service = request.GetRequiredService<OrganizationService>();
            var result = await service.RetrieveMultipleAsync(model, cancellationToken);
            var tables = new List<string>();
            void Walk(EntityNode node)
            {
                tables.Add(node.EntityName);
                foreach (var link in node.Links) Walk(link);
            }
            Walk(model.Root);
            var expands = string.IsNullOrWhiteSpace(odata) ? null : ODataExpand.From(store, model);
            var json = new JsonObject
            {
                ["records"] = await McpCall.RecordsAsync(service, result, m, store, tables, cancellationToken, expands),
                ["moreRecords"] = result.MoreRecords,
            };
            if (result.MoreRecords && model.Top is null)
                json["pagingCookie"] = ODataQueryParser.FormatSkipToken(model.PageNumber + 1, result.PagingCookie);
            return json;
        }, cancellationToken);

    /// <summary>The FetchXML as the Web API's <c>fetchXml</c> option reads it, with the tool's paging.</summary>
    private static (EntityMetadata, QueryModel) FetchModel(IMetadataStore store, string fetchXml, int? pageSize, string? pagingCookie)
    {
        var model = FetchXmlParser.Parse(fetchXml);
        var m = McpCall.Table(store, model.Root.EntityName);
        if (model.Top is null && !model.Aggregate)
        {
            model.PageSize = pageSize ?? model.PageSize ?? McpLimits.DefaultPageSize;
            if (model.PageSize > McpLimits.MaxPageSize)
                throw XrmdFaultException.InvalidArgument(
                    $"The FetchXML count must be at most {McpLimits.MaxPageSize} in the MCP query tool, got {model.PageSize}.");
        }
        else if (pageSize is not null)
        {
            throw XrmdFaultException.InvalidArgument("'pageSize' cannot be combined with a FetchXML 'top' or an aggregate query.");
        }
        if (!string.IsNullOrEmpty(pagingCookie)) (model.PageNumber, model.PagingCookie) = ODataQueryParser.ReadSkipToken(pagingCookie);
        return (m, model);
    }

    /// <summary><c>{entityset}?{options}</c> as <c>GET /api/data/v9.2/{entityset}?{options}</c> reads it, with the tool's paging.</summary>
    private static (EntityMetadata, QueryModel) ODataModel(IMetadataStore store, ODataQueryParser parser, string odata, int? pageSize,
        string? pagingCookie)
    {
        var text = odata.Trim().TrimStart('/');
        var question = text.IndexOf('?', StringComparison.Ordinal);
        var setName = question < 0 ? text : text[..question];
        var m = store.GetEntityBySetName(setName)
                ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        var query = new QueryCollection(QueryHelpers.ParseQuery(question < 0 ? "" : text[question..]));
        if (ODataQueryParser.IsFetchXml(query))
            throw XrmdFaultException.InvalidArgument("Use the 'fetchXml' argument for FetchXML, not the fetchXml query option.");
        if (query.Keys.Any(k => string.Equals(k, "$skiptoken", StringComparison.OrdinalIgnoreCase)))
            throw XrmdFaultException.InvalidArgument("Use the 'pagingCookie' argument to read the next page, not $skiptoken.");
        var model = parser.Parse(m, query, pageSize ?? McpLimits.DefaultPageSize, string.IsNullOrEmpty(pagingCookie) ? null : pagingCookie);
        return (store.GetEntity(m.LogicalName) ?? m, model);
    }
}
