namespace Xrmd.Mcp;

/// <summary>Limits of the MCP tools.</summary>
public static class McpLimits
{
    /// <summary>The configuration key that overrides <see cref="ExportSolutionMaxBytes"/>.</summary>
    public const string ExportSolutionMaxBytesKey = "Mcp:ExportSolutionMaxBytes";

    /// <summary>The largest solution file <c>export_solution</c> returns inline: 10 MB (base64 makes it a third larger).</summary>
    public const long ExportSolutionMaxBytes = 10L * 1024 * 1024;

    /// <summary>The default page size of <c>query</c>.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The largest page (and <c>$top</c>/<c>top</c>) of <c>query</c>, and the largest <c>take</c> of the diagnostics tools.</summary>
    public const int MaxPageSize = 500;
}
