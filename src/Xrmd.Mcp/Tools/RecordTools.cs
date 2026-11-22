using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.WebApi;

namespace Xrmd.Mcp.Tools;

/// <summary>
/// <c>create_record</c>, <c>update_record</c>, <c>delete_record</c>: the Web API's POST/PATCH/DELETE on a table, through
/// <see cref="OrganizationService"/> as the caller (plug-ins, privileges and record access apply). <c>data</c> is read
/// by <see cref="ODataJson.FromJson"/>: column values by logical name, lookups as <c>{nav}@odata.bind</c>.
/// </summary>
[McpServerToolType]
public static class RecordTools
{
    private const string DataDescription =
        "Column values in Web API form, by logical name, e.g. {\"name\":\"Acme\",\"parentaccountid@odata.bind\":\"/accounts(<id>)\"}.";

    [McpServerTool(Name = "create_record", Destructive = false)]
    [Description("Creates a record with the caller's security. Returns {id}.")]
    public static Task<string> CreateRecord(IServiceProvider services,
        [Description("The table's logical name, e.g. account.")] string table,
        [Description(DataDescription)] JsonElement data, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var store = request.GetRequiredService<IMetadataStore>();
            var entity = ODataJson.FromJson(McpCall.Data(data), McpCall.Table(store, table), store);
            var id = await request.GetRequiredService<OrganizationService>().CreateAsync(entity, cancellationToken);
            return new JsonObject { ["id"] = id.ToString() };
        }, cancellationToken);

    [McpServerTool(Name = "update_record", Destructive = true, Idempotent = true)]
    [Description("Updates the given columns of a record with the caller's security. Returns {id}.")]
    public static Task<string> UpdateRecord(IServiceProvider services,
        [Description("The table's logical name, e.g. account.")] string table,
        [Description("The record's id (GUID).")] string id,
        [Description(DataDescription)] JsonElement data, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var store = request.GetRequiredService<IMetadataStore>();
            var key = McpCall.Id(id);
            var entity = ODataJson.FromJson(McpCall.Data(data), McpCall.Table(store, table), store);
            entity.Id = key;
            await request.GetRequiredService<OrganizationService>().UpdateAsync(entity, cancellationToken);
            return new JsonObject { ["id"] = key.ToString() };
        }, cancellationToken);

    [McpServerTool(Name = "delete_record", Destructive = true)]
    [Description("Deletes a record with the caller's security. Returns {id}.")]
    public static Task<string> DeleteRecord(IServiceProvider services,
        [Description("The table's logical name, e.g. account.")] string table,
        [Description("The record's id (GUID).")] string id, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var m = McpCall.Table(request.GetRequiredService<IMetadataStore>(), table);
            var key = McpCall.Id(id);
            await request.GetRequiredService<OrganizationService>().DeleteAsync(m.LogicalName, key, cancellationToken);
            return new JsonObject { ["id"] = key.ToString() };
        }, cancellationToken);
}
