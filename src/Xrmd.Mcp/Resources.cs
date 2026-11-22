using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol.Server;
using Xrmd.Abstractions;
using Xrmd.Admin;
using Xrmd.Core;
using Xrmd.Metadata;

namespace Xrmd.Mcp;

/// <summary>
/// The MCP resources: <c>xrmverse://tables/{name}</c> (the compact metadata of <c>describe_table</c>) and
/// <c>xrmverse://schema</c> (the current schema YAML, as <c>GET /admin/schema</c>: System Administrator only; the publisher
/// prefix is the <c>publisher</c> query of the URI, else the default publisher's).
/// </summary>
[McpServerResourceType]
public static class XrmverseResources
{
    [McpServerResource(UriTemplate = "xrmverse://tables/{name}", Name = "table", MimeType = "application/json")]
    [Description("The compact metadata of a table (as describe_table).")]
    public static Task<string> Table(IServiceProvider services, string name, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, (request, _) => Tools.MetadataTools.DescribeAsync(request, name, cancellationToken), cancellationToken);

    [McpServerResource(UriTemplate = "xrmverse://schema{?publisher}", Name = "schema", MimeType = "text/yaml")]
    [Description("The organization's custom schema as YAML (the format of apply_schema), for one publisher prefix (default: the default publisher's). Requires the System Administrator role.")]
    public static Task<string> Schema(IServiceProvider services, string? publisher = null, CancellationToken cancellationToken = default) =>
        McpCall.RunAsync(services, async (request, _) =>
        {
            var prefix = publisher;
            if (string.IsNullOrWhiteSpace(prefix))
            {
                var row = await request.GetRequiredService<OrganizationService>().RetrieveAsync(
                    "publisher", SystemSeed.DefaultPublisherId, new ColumnSet("customizationprefix"), cancellationToken);
                prefix = row.GetAttributeValue<string>("customizationprefix");
            }
            return request.GetRequiredService<SchemaApplier>().Export(prefix);
        }, cancellationToken, adminOnly: true);
}
