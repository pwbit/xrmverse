using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xrmd.Admin;
using Xrmd.Core.Errors;

namespace Xrmd.Mcp.Tools;

/// <summary>
/// <c>apply_schema</c>: metadata as code through <see cref="SchemaApplier"/> (the pipeline of <c>POST /admin/schema/apply</c>
/// and <c>xrmd apply</c>). Only for a System Administrator, checked before the YAML is read; <c>dryRun</c> defaults to
/// true, so an agent must ask for the write explicitly.
/// </summary>
[McpServerToolType]
public static class SchemaTools
{
    [McpServerTool(Name = "apply_schema", Destructive = true)]
    [Description("Plans (dryRun, the default) or applies a schema YAML (tables, columns, choices, relationships; additive only). Requires the System Administrator role. Returns {changes, extras, rejections, applied}; with dryRun false, rejections are an error carrying that plan.")]
    public static Task<string> ApplySchema(IServiceProvider services,
        [Description("The schema YAML (publisher, choices, tables).")] string yaml,
        [Description("true (default): only plan; false: apply the plan in one transaction.")] bool dryRun = true,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
        {
            var result = await request.GetRequiredService<SchemaApplier>().ApplyAsync(yaml ?? "", dryRun, cancellationToken);
            // A write that was refused is an error (isError), never a normal result an agent could read as success; the
            // plan goes with it. A dry run with rejections is a normal result: it is the answer to "what would happen".
            if (!dryRun && result.Rejections.Count > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"The schema was rejected ({result.Rejections.Count} rejection(s)); nothing was applied. {McpCall.ToNode(result).ToJsonString(McpCall.Json)}");
            return McpCall.ToNode(result);
        }, cancellationToken, adminOnly: true);
}
