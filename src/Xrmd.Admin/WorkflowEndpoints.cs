using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Schema;
using Xrmd.Workflows.Authoring;

namespace Xrmd.Admin;

/// <summary>
/// The authoring API beside the Admin API (plan 2.21, A18): workflow authoring as code over <see cref="WorkflowAuthoring"/>.
/// <list type="bullet">
/// <item><c>POST /authoring/workflows/apply?dryRun=true|false&amp;solution=…&amp;activate=true|false</c> (body: the YAML; <c>dryRun</c>
/// defaults to true, as <c>POST /admin/schema/apply</c>): 200 with the <see cref="ApplyResult"/>, 400 with the same body when it has
/// errors; a body over <see cref="WorkflowYaml.MaxBytes"/> is a 400.</item>
/// <item><c>GET /authoring/workflows?kind=&amp;entity=</c>: the <see cref="WorkflowSummary"/> list.</item>
/// <item><c>GET /authoring/workflows/{nameOrId}?format=yaml|xaml</c>: <c>text/yaml</c> (the default) or <c>text/xml</c>.</item>
/// <item><c>POST /authoring/workflows/{nameOrId}/activate</c> and <c>/deactivate</c>: the <see cref="WorkflowSummary"/> after.</item>
/// </list>
/// The group requires a valid token (401) and no role (A18, Q2): every operation is a pipeline request as the caller, so the Dataverse
/// privileges decide (A17), unlike <c>/admin</c>, which stays for System Administrators. Errors use the Web API error envelope.
/// </summary>
public static class WorkflowEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Maps <c>/authoring/workflows</c> and returns the group.</summary>
    public static RouteGroupBuilder MapWorkflowAuthoringEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/authoring/workflows").RequireAuthorization();
        group.MapPost("/apply", (Func<HttpContext, Task<IResult>>)ApplyAsync);
        group.MapGet("", (Func<HttpContext, Task<IResult>>)ListAsync);
        group.MapGet("/{nameOrId}", (Func<HttpContext, Task<IResult>>)ExportAsync);
        group.MapPost("/{nameOrId}/activate", (Func<HttpContext, Task<IResult>>)(c => SetStateAsync(c, activate: true)));
        group.MapPost("/{nameOrId}/deactivate", (Func<HttpContext, Task<IResult>>)(c => SetStateAsync(c, activate: false)));
        return group;
    }

    private static WorkflowAuthoring Authoring(HttpContext context) => context.RequestServices.GetRequiredService<WorkflowAuthoring>();

    /// <summary>The caller of the request (403 when the token's user is no member of the organization, has no role or is disabled).</summary>
    private static Task<Caller> CallerAsync(HttpContext context) =>
        context.RequestServices.GetRequiredService<CallerResolver>().ResolveAsync(context.RequestAborted);

    private static string? Query(HttpContext context, string name) =>
        context.Request.Query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : null;

    private static bool Flag(HttpContext context, string name, bool fallback) => Query(context, name) is not { } text
        ? fallback
        : bool.TryParse(text, out var value)
            ? value
            : throw XrmdFaultException.InvalidArgument($"The query option '{name}' must be true or false; got '{text}'.");

    private static string NameOrId(HttpContext context) => context.Request.RouteValues["nameOrId"] as string ?? "";

    private static async Task<IResult> ApplyAsync(HttpContext context)
    {
        var dryRun = Flag(context, "dryRun", true);
        var activate = Flag(context, "activate", false);
        var solution = Query(context, "solution");
        var caller = await CallerAsync(context);
        var yaml = await BodyAsync(context);
        if (string.IsNullOrWhiteSpace(yaml)) throw XrmdFaultException.InvalidArgument("The request body must be the workflow YAML.");

        var result = await Authoring(context).ApplyAsync(caller, yaml, new ApplyOptions(solution, activate, dryRun), context.RequestAborted);
        return Results.Json(result, Json, statusCode: result.Errors.Count > 0 ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);
    }

    /// <summary>
    /// The request body as text, read through a bound of <see cref="WorkflowYaml.MaxBytes"/> (fix round 1, minor 2): a larger body (by
    /// its <c>Content-Length</c>, or once that many bytes are read) is a 400 before it is held whole.
    /// </summary>
    private static async Task<string> BodyAsync(HttpContext context)
    {
        static XrmdFaultException TooLarge() =>
            XrmdFaultException.InvalidArgument($"The workflow YAML is larger than {WorkflowYaml.MaxBytes / 1_048_576} MiB.");
        if (context.Request.ContentLength > WorkflowYaml.MaxBytes) throw TooLarge();
        using var body = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (body.Length + read > WorkflowYaml.MaxBytes) throw TooLarge();
            body.Write(buffer, 0, read);
        }
        body.Position = 0;
        using var reader = new StreamReader(body, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(context.RequestAborted);
    }

    private static async Task<IResult> ListAsync(HttpContext context)
    {
        var caller = await CallerAsync(context);
        return Results.Json(await Authoring(context).ListAsync(caller, Query(context, "kind"), Query(context, "entity"), context.RequestAborted), Json);
    }

    private static async Task<IResult> ExportAsync(HttpContext context)
    {
        var format = Query(context, "format") ?? "yaml";
        var caller = await CallerAsync(context);
        var text = await Authoring(context).ExportAsync(caller, NameOrId(context), format, context.RequestAborted);
        return Results.Text(text, format.Trim().Equals("xaml", StringComparison.OrdinalIgnoreCase) ? "text/xml" : "text/yaml", Encoding.UTF8);
    }

    private static async Task<IResult> SetStateAsync(HttpContext context, bool activate)
    {
        var caller = await CallerAsync(context);
        return Results.Json(await Authoring(context).SetStateAsync(caller, NameOrId(context), activate, context.RequestAborted), Json);
    }
}
