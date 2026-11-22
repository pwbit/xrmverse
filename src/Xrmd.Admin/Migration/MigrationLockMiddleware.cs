using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Xrmd.Abstractions;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The HTTP side of the migration lock (plan M3 R4): while the organization is locked, a request with a method other than GET, HEAD
/// or OPTIONS is answered 503 with the Web API error envelope, except the migration's own routes (<c>/admin/migrate</c>), sign-in
/// (<c>/connect</c>, <c>/adfs</c>, <c>/api/session</c>), the health probe, and the two endpoints whose reads are POSTs (the SOAP
/// endpoint and <c>/mcp</c>): their messages are checked one by one by the organization service. The parts of a Web API
/// <c>$batch</c> come through here one by one.
/// </summary>
public sealed class MigrationLockMiddleware(RequestDelegate next, IMigrationLock migrationLock)
{
    private static readonly string[] Allowed =
        ["/admin/migrate", "/connect", "/adfs", "/api/session", "/XRMServices", "/mcp", "/healthz"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (migrationLock.ActiveRunId is { } run && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && !Allowed.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            var fault = MigrationLocks.Locked(run);
            context.Response.StatusCode = fault.HttpStatus;
            context.Response.ContentType = "application/json; odata.metadata=minimal";
            context.Response.Headers["OData-Version"] = "4.0";
            var error = new JsonObject
            {
                ["code"] = "0x" + unchecked((uint)fault.ErrorCode).ToString("x8", CultureInfo.InvariantCulture), ["message"] = fault.Message,
            };
            await JsonSerializer.SerializeAsync(context.Response.Body, new JsonObject { ["error"] = error }, cancellationToken: context.RequestAborted);
            return;
        }
        await next(context);
    }
}
