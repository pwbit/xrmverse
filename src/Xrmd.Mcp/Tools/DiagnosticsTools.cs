using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol.Server;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.WebApi;

namespace Xrmd.Mcp.Tools;

/// <summary>
/// <c>get_plugin_traces</c> (<c>plugintracelog</c>) and <c>get_async_jobs</c> (<c>asyncoperation</c>): the newest rows
/// first, read by <see cref="OrganizationService"/> as the caller (the caller's read privilege on each table applies).
/// </summary>
[McpServerToolType]
public static class DiagnosticsTools
{
    private static readonly string[] AsyncJobColumns =
    [
        "asyncoperationid", "name", "operationtype", "messagename", "primaryentitytype", "statecode", "statuscode", "message",
        "friendlymessage", "errorcode", "retrycount", "depth", "correlationid", "regardingobjectid", "owningextensionid",
        "createdon", "startedon", "completedon", "postponeuntil",
    ];

    /// <summary>The <c>asyncoperation</c> status reasons by name (Dataverse's <c>statuscode</c> options).</summary>
    private static readonly Dictionary<string, int> AsyncStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WaitingForResources"] = 0, ["Waiting"] = 10, ["InProgress"] = 20, ["Pausing"] = 21, ["Canceling"] = 22,
        ["Succeeded"] = 30, ["Failed"] = 31, ["Canceled"] = 32,
    };

    [McpServerTool(Name = "get_plugin_traces", ReadOnly = true)]
    [Description("Reads the newest plug-in trace logs (plugintracelog), optionally for one plug-in type name.")]
    public static Task<string> GetPluginTraces(IServiceProvider services,
        [Description("How many traces, newest first: 1 to 500 (default 20).")] int take = 20,
        [Description("Only the traces of this plug-in type (typename, e.g. Acme.Plugins.AccountPlugin).")] string? typeName = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var query = new QueryExpression("plugintracelog")
            {
                ColumnSet = new ColumnSet(true),
                TopCount = McpCall.Take(take),
                Orders = { new OrderExpression("createdon", OrderType.Descending) },
            };
            if (!string.IsNullOrWhiteSpace(typeName)) query.Criteria.AddCondition("typename", ConditionOperator.Equal, typeName);
            return await RunAsync(request, query, cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "get_async_jobs", ReadOnly = true)]
    [Description("Reads the newest system jobs (asyncoperation), optionally with one status reason: WaitingForResources, Waiting, InProgress, Pausing, Canceling, Succeeded, Failed, Canceled (or its statuscode value).")]
    public static Task<string> GetAsyncJobs(IServiceProvider services,
        [Description("How many jobs, newest first: 1 to 500 (default 20).")] int take = 20,
        [Description("Only the jobs with this status reason (name or statuscode value).")] string? status = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var m = McpCall.Table(request.GetRequiredService<IMetadataStore>(), "asyncoperation");
            var query = new QueryExpression("asyncoperation")
            {
                ColumnSet = new ColumnSet(AsyncJobColumns.Where(c => m.Attributes.Any(a => a.LogicalName == c)).ToArray()),
                TopCount = McpCall.Take(take),
                Orders = { new OrderExpression("createdon", OrderType.Descending) },
            };
            if (!string.IsNullOrWhiteSpace(status)) query.Criteria.AddCondition("statuscode", ConditionOperator.Equal, StatusCode(status));
            return await RunAsync(request, query, cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "get_audit_history", ReadOnly = true)]
    [Description("Reads the audit history of one record, newest first, as the Web API returns it (RetrieveRecordChangeHistory / RetrieveAttributeChangeHistory): AuditDetailCollection.AuditDetails, each with its OldValue and NewValue, plus an AuditRecord with the entry's id, date, action and user. Needs the audit privileges.")]
    public static Task<string> GetAuditHistory(IServiceProvider services,
        [Description("Logical name of the table of the record.")] string table,
        [Description("Id of the record (a GUID).")] string id,
        [Description("Only the history of this column (logical name).")] string? column = null,
        [Description("How many audit entries, newest first: 1 to 500 (default 20).")] int take = 20,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var store = request.GetRequiredService<IMetadataStore>();
            var target = new EntityReference(McpCall.Table(store, table).LogicalName, McpCall.Id(id));
            var paging = new PagingInfo { PageNumber = 1, Count = McpCall.Take(take) };
            var message = string.IsNullOrWhiteSpace(column) ? "RetrieveRecordChangeHistory" : "RetrieveAttributeChangeHistory";
            var history = new OrganizationRequest(message) { ["Target"] = target, ["PagingInfo"] = paging };
            if (!string.IsNullOrWhiteSpace(column)) history["AttributeLogicalName"] = column;
            var response = await request.GetRequiredService<OrganizationService>().ExecuteAsync(history, cancellationToken);
            var collection = (AuditDetailCollection)response.Results["AuditDetailCollection"];

            var details = new JsonArray();
            foreach (var detail in collection.AuditDetails)
            {
                var json = AuditEndpoints.ToJson(detail, store, "", includeFormattedValues: false);
                if (detail.AuditRecord is { } audit)
                {
                    var user = audit.GetAttributeValue<EntityReference>("userid");
                    json["AuditRecord"] = new JsonObject
                    {
                        ["auditid"] = audit.Id.ToString(),
                        ["createdon"] = audit.GetAttributeValue<DateTime?>("createdon")?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                        ["action"] = audit.FormattedValues.TryGetValue("action", out var label) ? label : audit.GetAttributeValue<OptionSetValue>("action")?.Value.ToString(CultureInfo.InvariantCulture),
                        ["userid"] = user?.Id.ToString(),
                        ["username"] = user?.Name,
                    };
                }
                details.Add(json);
            }
            return new JsonObject
            {
                ["AuditDetailCollection"] = new JsonObject
                {
                    ["MoreRecords"] = collection.MoreRecords,
                    ["PagingCookie"] = collection.PagingCookie,
                    ["TotalRecordCount"] = collection.TotalRecordCount,
                    ["AuditDetails"] = details,
                },
            };
        }, cancellationToken);

    private static int StatusCode(string status) =>
        AsyncStatuses.TryGetValue(status.Trim(), out var code) ? code
        : int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && AsyncStatuses.ContainsValue(value) ? value
        : throw XrmdFaultException.InvalidArgument(
            $"Unknown asyncoperation status '{status}': use one of {string.Join(", ", AsyncStatuses.Keys)} or its statuscode value.");

    private static async Task<JsonNode> RunAsync(IServiceProvider request, QueryExpression query, CancellationToken ct)
    {
        var store = request.GetRequiredService<IMetadataStore>();
        var service = request.GetRequiredService<OrganizationService>();
        var result = await service.RetrieveMultipleAsync(query, ct);
        return new JsonObject
        {
            ["records"] = await McpCall.RecordsAsync(service, result, McpCall.Table(store, query.EntityName), store,
                LinkedTables(query.LinkEntities), ct),
        };
    }

    /// <summary>The tables of <paramref name="links"/> and of the links nested in them, at any depth.</summary>
    private static IEnumerable<string> LinkedTables(IEnumerable<LinkEntity> links) =>
        links.SelectMany(l => LinkedTables(l.LinkEntities).Prepend(l.LinkToEntityName));
}
