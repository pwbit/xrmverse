using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol.Server;
using Xrmd.Abstractions;
using Xrmd.Admin;
using Xrmd.Core;
using Xrmd.Core.Errors;

namespace Xrmd.Mcp.Tools;

/// <summary><c>list_solutions</c> and <c>export_solution</c>, through <see cref="OrganizationService"/> as the caller.</summary>
[McpServerToolType]
public static class SolutionTools
{
    private static readonly string[] SolutionColumns =
        ["solutionid", "uniquename", "friendlyname", "version", "ismanaged", "publisherid", "installedon", "description"];

    [McpServerTool(Name = "list_solutions", ReadOnly = true, Idempotent = true)]
    [Description("Lists the solutions of the organization (unique name, display name, version, managed, publisher, holding: true for the pending upgrade {name}_Upgrade of another solution).")]
    public static Task<string> ListSolutions(IServiceProvider services, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var store = request.GetRequiredService<IMetadataStore>();
            var m = McpCall.Table(store, "solution");
            var query = new QueryExpression("solution")
            {
                ColumnSet = new ColumnSet(SolutionColumns.Where(c => m.Attributes.Any(a => a.LogicalName == c)).ToArray()),
                Orders = { new OrderExpression("uniquename", OrderType.Ascending) },
            };
            var service = request.GetRequiredService<OrganizationService>();
            var result = await service.RetrieveMultipleAsync(query, cancellationToken);
            var records = await McpCall.RecordsAsync(service, result, m, store, [], cancellationToken);
            // holding: the pending upgrade "{name}_Upgrade" of another installed solution (plan 2.6, ruling M11).
            var names = records.Select(r => r?["uniquename"]?.GetValue<string>()).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
            foreach (var record in records.OfType<JsonObject>())
            {
                var name = record["uniquename"]?.GetValue<string>();
                record["holding"] = name is not null && name.EndsWith(HoldingSuffix, StringComparison.Ordinal)
                    && names.Contains(name[..^HoldingSuffix.Length]);
            }
            return new JsonObject { ["solutions"] = records };
        }, cancellationToken);

    private const string HoldingSuffix = "_Upgrade";

    [McpServerTool(Name = "list_component_layers", ReadOnly = true, Idempotent = true)]
    [Description("Lists the layers of managed solution components (System Administrator only). Give solutionName for the layers one solution owns, "
                 + "or componentType and componentId for the stack of one component, top layer first. Each row: componentType, objectId, solution "
                 + "(unique name, System or Active), kind (system, managed, active), order (higher on top) and version.")]
    public static Task<string> ListComponentLayers(IServiceProvider services,
        [Description("Unique name of a solution: lists the layers it owns.")] string? solutionName = null,
        [Description("Component type number (1 table, 2 column, 26 view, ...); with componentId.")] int? componentType = null,
        [Description("MetadataId or row id of the component; with componentType.")] string? componentId = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var lister = request.GetRequiredService<SolutionLayerLister>();
            if (!string.IsNullOrEmpty(solutionName))
            {
                if (componentType is not null || componentId is not null)
                    throw XrmdFaultException.InvalidArgument("Give either solutionName or componentType with componentId, not both.");
                return new JsonObject { ["layers"] = McpCall.ToNode(await lister.OfSolutionAsync(solutionName, cancellationToken)) };
            }
            if (componentType is not { } type || !Guid.TryParse(componentId, out var id))
                throw XrmdFaultException.InvalidArgument("Give solutionName, or componentType (a number) with componentId (a GUID).");
            return new JsonObject { ["layers"] = McpCall.ToNode(await lister.OfComponentAsync(type, id, cancellationToken)) };
        }, cancellationToken, adminOnly: true);

    [McpServerTool(Name = "export_solution", ReadOnly = true)]
    [Description("Exports an unmanaged solution file. Returns {fileName, sizeBytes, base64}; a file over 10 MB is refused (use `xrmd solution export`).")]
    public static Task<string> ExportSolution(IServiceProvider services,
        [Description("The solution's unique name.")] string solutionName, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var response = await request.GetRequiredService<OrganizationService>().ExecuteAsync(
                new ExportSolutionRequest { SolutionName = solutionName, Managed = false }, cancellationToken);
            var file = (byte[])response.Results["ExportSolutionFile"];
            var limit = request.GetRequiredService<IConfiguration>().GetValue<long?>(McpLimits.ExportSolutionMaxBytesKey)
                        ?? McpLimits.ExportSolutionMaxBytes;
            if (file.LongLength > limit)
                throw XrmdFaultException.InvalidArgument(string.Create(CultureInfo.InvariantCulture,
                    $"The solution file of '{solutionName}' is {file.LongLength} bytes, over the {limit}-byte limit of the MCP tool (10 MB by default): use `xrmd solution export` instead."));
            return new JsonObject
            {
                ["fileName"] = solutionName + ".zip",
                ["sizeBytes"] = file.LongLength,
                ["base64"] = Convert.ToBase64String(file),
            };
        }, cancellationToken);
}
