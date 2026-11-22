using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xrmd.Cli.Auth;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd mcp</c>: a stdio MCP server for clients that only speak stdio. Tools and resources are forwarded to the
/// server's <c>/mcp</c> endpoint (stateless streamable HTTP) with the profile's bearer token, so every call runs
/// through the Core as the signed-in user. stdout is the MCP channel: nothing else is written to it in this mode.
/// </summary>
public static class McpCommands
{
    public static Command Build(CliContext ctx)
    {
        var command = new Command("mcp", "Run a stdio MCP server that forwards to the server's /mcp endpoint with the profile's token.");
        command.SetAction(async (_, ct) =>
        {
            var (_, profile) = ctx.RequireProfile();
            var tokens = TokenProviders.Create(profile, ctx.Env);
            var endpoint = new Uri(new Uri(profile.Url.EndsWith('/') ? profile.Url : profile.Url + "/"), "mcp");

            using var http = new HttpClient(new BearerHandler(ctx.CreateHandler(profile), tokens.GetTokenAsync, ownsInner: ctx.Env.Handler is null))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            McpClient remote;
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(ctx.Env.McpStartupTimeout ?? TimeSpan.FromSeconds(30));
            try
            {
                // GET /mcp is off on the server: streamable HTTP only, no SSE / auto-detect.
                remote = await McpClient.CreateAsync(
                    new HttpClientTransport(
                        new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, Name = "xrmverse" },
                        http, ownsHttpClient: false),
                    cancellationToken: startup.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CliException(ExitCodes.Unreachable, $"The MCP endpoint {endpoint} did not respond in time.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Token providers throw CliException (exit 3) inside the handler; the SDK may wrap it.
                for (var inner = ex; inner is not null; inner = inner.InnerException)
                {
                    if (inner is CliException cli) throw cli;
                    if (inner is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } http401)
                        throw new CliException(ExitCodes.Auth, $"The server at {endpoint} refused the token ({(int)http401.StatusCode!}).", ex);
                }
                throw new CliException(ExitCodes.Unreachable, $"Cannot reach the MCP endpoint {endpoint}: {ex.Message}", ex);
            }

            await using (remote)
            {
                var options = new McpServerOptions
                {
                    ServerInfo = remote.ServerInfo,
                    ServerInstructions = remote.ServerInstructions,
                    Capabilities = new ServerCapabilities
                    {
                        Tools = remote.ServerCapabilities.Tools is null ? null : new ToolsCapability(),
                        Resources = remote.ServerCapabilities.Resources is null ? null : new ResourcesCapability(),
                    },
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = async (request, token) =>
                            await remote.ListToolsAsync(request.Params ?? new ListToolsRequestParams(), token),
                        CallToolHandler = async (request, token) =>
                            await remote.CallToolAsync(request.Params!, token),
                        ListResourcesHandler = async (request, token) =>
                            await remote.ListResourcesAsync(request.Params ?? new ListResourcesRequestParams(), token),
                        ListResourceTemplatesHandler = async (request, token) =>
                            await remote.ListResourceTemplatesAsync(request.Params ?? new ListResourceTemplatesRequestParams(), token),
                        ReadResourceHandler = async (request, token) =>
                            await remote.ReadResourceAsync(request.Params!, token),
                    },
                };

                using var input = ctx.Env.McpInput is null ? Console.OpenStandardInput() : null;
                using var output = ctx.Env.McpOutput is null ? Console.OpenStandardOutput() : null;
                await using var transport = new StreamServerTransport(ctx.Env.McpInput ?? input!, ctx.Env.McpOutput ?? output!, "xrmd");
                await using var server = McpServer.Create(transport, options);
                using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ended = false;
                var watch = remote.Completion.ContinueWith(done => { ended = true; run.Cancel(); }, TaskScheduler.Default);
                try { await server.RunAsync(run.Token); }
                catch (OperationCanceledException) when (ended && !ct.IsCancellationRequested) { }
                if (ended && !ct.IsCancellationRequested)
                    throw new CliException(ExitCodes.Unreachable, $"The session with {endpoint} ended.");
            }
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>Adds the profile's bearer token to every request (the token is read per request, so it can refresh).</summary>
    private sealed class BearerHandler(HttpMessageHandler inner, Func<CancellationToken, Task<string>> token, bool ownsInner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // The test handler belongs to the environment: it outlives this command.
            if (ownsInner) base.Dispose(disposing);
        }
    }
}
