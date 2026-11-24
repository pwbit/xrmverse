using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol.Server;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Mcp.Tools;

/// <summary>
/// <c>list_tables</c>, <c>describe_table</c>, <c>list_custom_apis</c> and <c>list_glossary_terms</c>: metadata in compact form,
/// for organization members.
/// </summary>
[McpServerToolType]
public static class MetadataTools
{
    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true)]
    [Description("Lists the tables (entities) of the organization: logical name, entity set name, display name, custom or not.")]
    public static Task<string> ListTables(IServiceProvider services, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, (request, _) =>
        {
            var tables = new JsonArray();
            foreach (var m in request.GetRequiredService<IMetadataStore>().GetAllEntities().OrderBy(e => e.LogicalName, StringComparer.Ordinal))
                tables.Add(TableJson.Summary(m));
            return Task.FromResult<JsonNode>(new JsonObject { ["tables"] = tables });
        }, cancellationToken);

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true)]
    [Description("Describes a table in compact form: primary columns, ownership, description, and each column's type, label, description, synonyms, requirement, length, lookup targets, choice options and whether field security secures it; plus the glossary terms of the knowledge sources that contain the table.")]
    public static Task<string> DescribeTable(IServiceProvider services,
        [Description("The table's logical name, e.g. account.")] string table, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, (request, _) => DescribeAsync(request, table, cancellationToken), cancellationToken);

    /// <summary>The JSON of <c>describe_table</c> and <c>xrmverse://tables/{name}</c>, with the knowledge the caller can read.</summary>
    internal static async Task<JsonNode> DescribeAsync(IServiceProvider request, string? table, CancellationToken ct)
    {
        var m = McpCall.Table(request.GetRequiredService<IMetadataStore>(), table);
        var knowledge = await KnowledgeReader.ForTableAsync(request.GetRequiredService<OrganizationService>(), m.LogicalName, ct);
        return TableJson.Describe(m, knowledge);
    }

    [McpServerTool(Name = "list_glossary_terms", ReadOnly = true, Idempotent = true)]
    [Description("Lists the active glossary terms of the knowledge sources, ordered by term, each with its description and the names of its knowledge sources. Read-only.")]
    public static Task<string> ListGlossaryTerms(IServiceProvider services,
        [Description("Only the terms of the knowledge sources that contain this table (logical name, e.g. account).")] string? table = null,
        [Description("Only the terms of the knowledge source with this name.")] string? source = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var terms = await KnowledgeReader.TermsAsync(request.GetRequiredService<OrganizationService>(), table, source, cancellationToken);
            return new JsonObject { ["terms"] = new JsonArray(terms.Select(t => (JsonNode?)TableJson.Term(t, withSources: true)).ToArray()) };
        }, cancellationToken);

    [McpServerTool(Name = "list_custom_apis", ReadOnly = true, Idempotent = true)]
    [Description("Lists the custom APIs (message definitions imported with a solution or authored directly), each with its binding (global, entity:<table> or collection:<table>), kind (action or function), whether it is private, its plug-in type name, and its request parameters and response properties with their type names. Read-only.")]
    public static Task<string> ListCustomApis(IServiceProvider services,
        [Description("Only the API with this unique name, e.g. xt_Echo (case-insensitive).")] string? name = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var service = request.GetRequiredService<OrganizationService>();
            var apiQuery = new QueryExpression(CustomApiTables.Api)
            {
                ColumnSet = new ColumnSet(CustomApiTables.UniqueName, CustomApiTables.Name, CustomApiTables.DisplayName, CustomApiTables.Description,
                    CustomApiTables.BindingType, CustomApiTables.BoundEntityLogicalName, CustomApiTables.IsFunction, CustomApiTables.IsPrivate,
                    CustomApiTables.PluginTypeId),
                Orders = { new OrderExpression(CustomApiTables.UniqueName, OrderType.Ascending) },
            };
            if (!string.IsNullOrWhiteSpace(name)) apiQuery.Criteria.AddCondition(CustomApiTables.UniqueName, ConditionOperator.Equal, name.Trim());
            var apis = await AllAsync(service, apiQuery, cancellationToken);

            var ids = apis.Select(a => (object)a.Id).ToArray();
            var parameters = await FieldsAsync(service, CustomApiTables.RequestParameter, ids, withOptional: true, cancellationToken);
            var properties = await FieldsAsync(service, CustomApiTables.ResponseProperty, ids, withOptional: false, cancellationToken);
            var types = await PluginTypeNamesAsync(service, apis, cancellationToken);

            var list = new JsonArray();
            foreach (var api in apis)
            {
                var table = api.GetAttributeValue<string>(CustomApiTables.BoundEntityLogicalName);
                var binding = api.GetAttributeValue<OptionSetValue>(CustomApiTables.BindingType)?.Value switch
                {
                    1 => "entity:" + table,
                    2 => "collection:" + table,
                    _ => "global",
                };
                var type = api.GetAttributeValue<EntityReference>(CustomApiTables.PluginTypeId);
                list.Add(new JsonObject
                {
                    ["uniqueName"] = api.GetAttributeValue<string>(CustomApiTables.UniqueName),
                    ["name"] = api.GetAttributeValue<string>(CustomApiTables.Name),
                    ["displayName"] = api.GetAttributeValue<string>(CustomApiTables.DisplayName),
                    ["description"] = api.GetAttributeValue<string>(CustomApiTables.Description),
                    ["binding"] = binding,
                    ["kind"] = api.GetAttributeValue<bool>(CustomApiTables.IsFunction) ? "function" : "action",
                    ["private"] = api.GetAttributeValue<bool>(CustomApiTables.IsPrivate),
                    ["pluginType"] = type is not null && types.TryGetValue(type.Id, out var typeName) ? typeName : null,
                    ["parameters"] = parameters.TryGetValue(api.Id, out var p) ? p : new JsonArray(),
                    ["properties"] = properties.TryGetValue(api.Id, out var q) ? q : new JsonArray(),
                });
            }
            return new JsonObject { ["customApis"] = list };
        }, cancellationToken);

    /// <summary>The parameters or properties of the APIs <paramref name="apiIds"/>, as JSON by API id, ordered by unique name.</summary>
    private static async Task<Dictionary<Guid, JsonArray>> FieldsAsync(OrganizationService service, string table, object[] apiIds, bool withOptional,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, JsonArray>();
        if (apiIds.Length == 0) return result;
        var columns = new List<string> { CustomApiTables.UniqueName, CustomApiTables.Type, CustomApiTables.LogicalEntityName, CustomApiTables.ApiId };
        if (withOptional) columns.Add(CustomApiTables.IsOptional);
        var query = new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(columns.ToArray()),
            Orders = { new OrderExpression(CustomApiTables.UniqueName, OrderType.Ascending) },
        };
        query.Criteria.AddCondition(CustomApiTables.ApiId, ConditionOperator.In, apiIds);
        foreach (var row in await AllAsync(service, query, ct))
        {
            var typeValue = row.GetAttributeValue<OptionSetValue>(CustomApiTables.Type)?.Value;
            var field = new JsonObject
            {
                ["uniqueName"] = row.GetAttributeValue<string>(CustomApiTables.UniqueName),
                ["type"] = typeValue is { } v && Enum.IsDefined(typeof(CustomApiType), v) ? ((CustomApiType)v).ToString() : typeValue?.ToString(),
            };
            if (withOptional) field["optional"] = row.GetAttributeValue<bool>(CustomApiTables.IsOptional);
            field["table"] = row.GetAttributeValue<string>(CustomApiTables.LogicalEntityName);
            var owner = row.GetAttributeValue<EntityReference>(CustomApiTables.ApiId).Id;
            if (!result.TryGetValue(owner, out var array)) result[owner] = array = new JsonArray();
            array.Add(field);
        }
        return result;
    }

    private static async Task<Dictionary<Guid, string>> PluginTypeNamesAsync(OrganizationService service, IEnumerable<Entity> apis, CancellationToken ct)
    {
        var ids = apis.Select(a => a.GetAttributeValue<EntityReference>(CustomApiTables.PluginTypeId)?.Id).OfType<Guid>().Distinct().Select(i => (object)i).ToArray();
        var names = new Dictionary<Guid, string>();
        if (ids.Length == 0) return names;
        var query = new QueryExpression("plugintype") { ColumnSet = new ColumnSet("typename") };
        query.Criteria.AddCondition("plugintypeid", ConditionOperator.In, ids);
        foreach (var type in await AllAsync(service, query, ct))
            names[type.Id] = type.GetAttributeValue<string>("typename");
        return names;
    }

    /// <summary>Rows per query page of <c>list_custom_apis</c>; a field of its own so that a test can make a few rows span pages.</summary>
    internal static int PageSize { get; set; } = 500;

    /// <summary>Every row of <paramref name="query"/>: the pages are read with the paging cookie until <c>MoreRecords</c> is false.</summary>
    internal static async Task<IReadOnlyList<Entity>> AllAsync(OrganizationService service, QueryExpression query, CancellationToken ct)
    {
        query.PageInfo = new PagingInfo { PageNumber = 1, Count = PageSize };
        var rows = new List<Entity>();
        while (true)
        {
            var page = await service.RetrieveMultipleAsync(query, ct);
            rows.AddRange(page.Entities);
            if (!page.MoreRecords) return rows;
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }
    }
}
