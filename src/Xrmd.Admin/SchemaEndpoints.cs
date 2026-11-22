using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Core.Errors;

namespace Xrmd.Admin;

/// <summary><c>GET /admin/schema</c> and <c>POST /admin/schema/apply</c>: metadata as code (<see cref="SchemaApplier"/>).</summary>
public static class SchemaEndpoints
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Maps the schema routes on the admin group (so they are protected by its filter; the handlers read the request themselves).</summary>
    public static RouteGroupBuilder MapSchemaEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/schema", (Func<HttpContext, Task<IResult>>)GetSchemaAsync);
        admin.MapPost("/schema/apply", (Func<HttpContext, Task<IResult>>)ApplyAsync);
        return admin;
    }

    /// <summary><c>GET /admin/schema?publisher=xv</c>: the org's schema with that prefix as <c>text/yaml</c>.</summary>
    private static Task<IResult> GetSchemaAsync(HttpContext context)
    {
        var publisher = context.Request.Query.TryGetValue("publisher", out var value) ? value.ToString() : "";
        if (string.IsNullOrWhiteSpace(publisher))
            throw XrmdFaultException.InvalidArgument("The query option 'publisher' (the publisher prefix, e.g. 'xv') is required.");
        var yaml = context.RequestServices.GetRequiredService<SchemaApplier>().Export(publisher);
        return Task.FromResult(Results.Text(yaml, "text/yaml", Encoding.UTF8));
    }

    /// <summary>
    /// <c>POST /admin/schema/apply?dryRun=true|false</c> (body: the schema YAML; <c>dryRun</c> defaults to true, so a caller
    /// writes only when it asks for it, as with the MCP tool):
    /// <c>{changes,extras,rejections,applied}</c>; 400 with the same body when there are rejections.
    /// </summary>
    private static async Task<IResult> ApplyAsync(HttpContext context)
    {
        var dryRun = true;
        if (context.Request.Query.TryGetValue("dryRun", out var text) && !bool.TryParse(text, out dryRun))
            throw XrmdFaultException.InvalidArgument($"The query option 'dryRun' must be true or false; got '{text}'.");

        string yaml;
        using (var reader = new StreamReader(context.Request.Body, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            yaml = await reader.ReadToEndAsync(context.RequestAborted);
        if (string.IsNullOrWhiteSpace(yaml)) throw XrmdFaultException.InvalidArgument("The request body must be the schema YAML.");

        var result = await context.RequestServices.GetRequiredService<SchemaApplier>().ApplyAsync(yaml, dryRun, context.RequestAborted);
        return Results.Json(result, WebJson, statusCode: result.Rejections.Count > 0 ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);
    }
}
