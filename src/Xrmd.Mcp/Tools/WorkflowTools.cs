using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xrmd.Core.Errors;
using Xrmd.WebApi;
using Xrmd.Workflows.Authoring;

namespace Xrmd.Mcp.Tools;

/// <summary>
/// Workflow authoring as code (plan 2.21, A18): <c>apply_workflow</c>, <c>export_workflow</c>, <c>list_workflows</c> and
/// <c>set_workflow_state</c>, through <see cref="WorkflowAuthoring"/> as the caller (no System Administrator role: the Dataverse
/// privileges decide, as for <c>/authoring/workflows</c>).
/// </summary>
[McpServerToolType]
public static class WorkflowTools
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Web)!;

    [McpServerTool(Name = "apply_workflow", Destructive = true, Idempotent = true)]
    [Description("Applies a workflow, business rule or action written as YAML (kind: workflow | businessRule | action): creates it, or updates the "
                 + "process with the same identity (its id, else its kind, table and name). Defaults to a dry run that only reports what would "
                 + "change: pass dryRun=false to write. Returns {id, name, action (created|updated|unchanged), state (draft|activated), stateChanged, "
                 + "assumedShapes, errors}. A refused document is an error whose text holds the result with every error (line and column in the YAML); "
                 + "nothing is written then. Needs the workflow privileges (prvReadWorkflow even for a dry run).")]
    public static Task<string> ApplyWorkflow(IServiceProvider services,
        [Description("The workflow document, YAML text (at most 1 MiB).")] string yaml,
        [Description("Unique name of the unmanaged solution that receives the process.")] string? solution = null,
        [Description("Activate the process after writing it.")] bool activate = false,
        [Description("true (the default) writes nothing and reports the outcome; false applies.")] bool dryRun = true,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, caller) =>
        {
            if (string.IsNullOrWhiteSpace(yaml)) throw XrmdFaultException.InvalidArgument("'yaml' must be the workflow document.");
            var result = await request.GetRequiredService<WorkflowAuthoring>()
                .ApplyAsync(caller, yaml, new ApplyOptions(string.IsNullOrWhiteSpace(solution) ? null : solution, activate, dryRun), cancellationToken);
            var node = Node(result);
            if (result.Errors.Count > 0)
                throw new McpException($"{ODataErrorMiddleware.FormatCode(ErrorCodes.InvalidArgument)}: The workflow was refused with "
                                       + $"{result.Errors.Count} error(s); nothing was written. {node.ToJsonString(McpCall.Json)}");
            return node;
        }, cancellationToken);

    [McpServerTool(Name = "export_workflow", ReadOnly = true, Idempotent = true)]
    [Description("Exports a workflow, business rule or action as YAML (the default; a process xrmverse cannot represent is refused with the reasons) "
                 + "or as its XAML. Returns {format, text}.")]
    public static Task<string> ExportWorkflow(IServiceProvider services,
        [Description("The name or the id of the process.")] string nameOrId,
        [Description("yaml (default) or xaml.")] string format = "yaml",
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, caller) =>
        {
            var text = await request.GetRequiredService<WorkflowAuthoring>().ExportAsync(caller, nameOrId, format, cancellationToken);
            return new JsonObject { ["format"] = format.Trim().ToLowerInvariant(), ["text"] = text };
        }, cancellationToken);

    [McpServerTool(Name = "list_workflows", ReadOnly = true, Idempotent = true)]
    [Description("Lists the workflows, business rules and actions: id, name, kind (workflow, businessRule, action), table, mode (realtime, "
                 + "background; null for the other kinds), state (draft, activated) and the number of steps xrmverse cannot run.")]
    public static Task<string> ListWorkflows(IServiceProvider services,
        [Description("Only this kind: workflow, businessRule or action.")] string? kind = null,
        [Description("Only the processes of this table (logical name).")] string? entity = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, caller) =>
            new JsonObject
            {
                ["workflows"] = Node(await request.GetRequiredService<WorkflowAuthoring>()
                    .ListAsync(caller, string.IsNullOrWhiteSpace(kind) ? null : kind, string.IsNullOrWhiteSpace(entity) ? null : entity, cancellationToken)),
            }, cancellationToken);

    [McpServerTool(Name = "set_workflow_state", Destructive = false, Idempotent = true)]
    [Description("Activates (activated=true) or deactivates (activated=false, back to draft) a process by name or id. Returns its summary after.")]
    public static Task<string> SetWorkflowState(IServiceProvider services,
        [Description("The name or the id of the process.")] string nameOrId,
        [Description("true activates, false deactivates.")] bool activated,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, caller) =>
            Node(await request.GetRequiredService<WorkflowAuthoring>().SetStateAsync(caller, nameOrId, activated, cancellationToken)),
            cancellationToken);
}
