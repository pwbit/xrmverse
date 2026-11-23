using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using static Xrmd.Admin.Migration.MigrationPackageEndpoints;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The migration runs under the Admin API (migration spec §7.1, §7.7; plan M3 tasks 5 and 6): start, list, status, report, and the
/// cancel, resume, finalize and activate of a run. The work is <see cref="MigrationRunService"/>'s, which the MCP tools share.
/// </summary>
public static class MigrationRunEndpoints
{
    /// <summary>The minimum length of a package passphrase (migration M1).</summary>
    public const int MinPassphraseLength = MigrationRunService.MinPassphraseLength;

    public static RouteGroupBuilder MapMigrationRunEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapPost("/migrate/runs", (Func<HttpContext, Task<IResult>>)StartAsync);
        admin.MapGet("/migrate/runs", (Func<HttpContext, Task<IResult>>)ListAsync);
        admin.MapGet("/migrate/runs/{id}", (Func<HttpContext, Task<IResult>>)GetAsync);
        admin.MapGet("/migrate/runs/{id}/report", (Func<HttpContext, Task<IResult>>)ReportAsync);
        admin.MapPost("/migrate/runs/{id}/cancel", (Func<HttpContext, Task<IResult>>)CancelAsync);
        admin.MapPost("/migrate/runs/{id}/resume", (Func<HttpContext, Task<IResult>>)ResumeAsync);
        admin.MapPost("/migrate/runs/{id}/finalize", (Func<HttpContext, Task<IResult>>)FinalizeAsync);
        admin.MapPost("/migrate/runs/{id}/activate", (Func<HttpContext, Task<IResult>>)ActivateAsync);
        return admin;
    }

    private static IResult Json(MigrationResult result) => Results.Json(result.Body, statusCode: result.Status);

    private static Task<Caller> CallerAsync(HttpContext context) =>
        context.RequestServices.GetRequiredService<CallerResolver>().ResolveAsync(context.RequestAborted);

    private static async Task<IResult> StartAsync(HttpContext context)
    {
        // An SDK-free server refuses the run before its body is read (plan S.2a); MigrationRunService.StartAsync checks it again for MCP.
        DemandNotSdkFree(context.RequestServices);
        var body = await ReadBodyAsync(context);
        return Json(await MigrationRunService.StartAsync(context.RequestServices, await CallerAsync(context), body, context.RequestAborted));
    }

    private static async Task<IResult> ListAsync(HttpContext context) =>
        Results.Json(await MigrationRunService.ListAsync(context.RequestServices, context.RequestAborted));

    private static async Task<IResult> GetAsync(HttpContext context) =>
        Results.Json(await MigrationRunService.StatusAsync(context.RequestServices, RouteId(context), context.RequestAborted));

    private static async Task<IResult> ReportAsync(HttpContext context) =>
        Results.Json(await MigrationRunService.ReportAsync(context.RequestServices, RouteId(context), context.RequestAborted));

    private static async Task<IResult> CancelAsync(HttpContext context) =>
        Json(await MigrationRunService.CancelAsync(context.RequestServices, RouteId(context), context.RequestAborted));

    private static async Task<IResult> ResumeAsync(HttpContext context)
    {
        var id = RouteId(context);
        var body = await ReadBodyAsync(context);
        return Json(await MigrationRunService.ResumeAsync(context.RequestServices, await CallerAsync(context), id, body, context.RequestAborted));
    }

    private static async Task<IResult> FinalizeAsync(HttpContext context)
    {
        var id = RouteId(context);
        var body = await ReadBodyAsync(context);
        return Results.Json(await MigrationRunService.FinalizeAsync(context.RequestServices, id, body, context.RequestAborted));
    }

    private static async Task<IResult> ActivateAsync(HttpContext context)
    {
        var id = RouteId(context);
        var body = await ReadBodyAsync(context);
        return Results.Json(await MigrationRunService.ActivateAsync(context.RequestServices, await CallerAsync(context), id, body, context.RequestAborted));
    }
}
