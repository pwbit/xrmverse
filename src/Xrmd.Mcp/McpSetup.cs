using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Xrmd.Mcp;

/// <summary>
/// The MCP server on <c>/mcp</c> (spec §9): streamable HTTP, the same JWT authentication as the Web API, the tools of
/// <c>Tools/</c> and the resources of <see cref="XrmverseResources"/>.
/// <para>
/// The transport is stateless: every JSON-RPC request is its own HTTP request, so each tool call runs in that request's
/// execution context and scope, where <see cref="Xrmd.Core.CallerResolver"/> resolves the token's user (see
/// <see cref="McpCall"/>). A stateful session would run tools under the context of the request that opened it.
/// </para>
/// </summary>
public static class McpSetup
{
    /// <summary>Registers the MCP server with its tools and resources (the endpoint is mapped by <see cref="MapXrmdMcp"/>).</summary>
    /// <remarks>
    /// The tools and resources are built on serializer options of this host. The function factory (<c>AIFunctionFactory</c>)
    /// caches what it builds per options instance, and each entry holds the host's services: on the shared default options,
    /// every host built in the process (the test suites build hundreds) would stay in memory for the life of the process.
    /// </remarks>
    public static IServiceCollection AddXrmdMcp(this IServiceCollection services)
    {
        var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        services.AddHttpContextAccessor();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "xrmverse",
                    Version = typeof(McpSetup).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                };
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpSetup).Assembly, serializerOptions);

        // As WithResourcesFromAssembly registers them (a singleton per [McpServerResource] method, built with the host's
        // services), but on the host's serializer options, which that method does not take. The resources are static methods.
        foreach (var method in typeof(McpSetup).Assembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<McpServerResourceTypeAttribute>() is not null)
                     .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() is not null))
        {
            services.AddSingleton(sp => McpServerResource.Create(method, (object?)null,
                new McpServerResourceCreateOptions { Services = sp, SerializerOptions = serializerOptions }));
        }
        return services;
    }

    /// <summary>Maps the MCP endpoint on <paramref name="pattern"/>; a request without a valid token is a 401.</summary>
    public static IEndpointConventionBuilder MapXrmdMcp(this IEndpointRouteBuilder endpoints, string pattern = "/mcp") =>
        endpoints.MapMcp(pattern).RequireAuthorization();
}
