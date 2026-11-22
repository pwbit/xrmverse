using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Solutions;
using Xrmd.Admin.Migration;

namespace Xrmd.Admin;

/// <summary>The Admin API: <c>/admin</c>, for System Administrators only. Errors use the Web API error envelope.</summary>
public static class AdminEndpoints
{
    public const int MaxLogTake = 1000;
    private const int DefaultLogTake = 100;

    /// <summary>The log buffer, the queue statistics, the health report and the telemetry of the host.</summary>
    public static WebApplicationBuilder AddXrmdAdmin(this WebApplicationBuilder builder)
    {
        var buffer = new LogRingBuffer();
        builder.Services.AddSingleton(buffer);
        builder.Services.AddSingleton<ILoggerProvider>(buffer);
        builder.Services.AddSingleton<AsyncQueueStats>();
        builder.Services.AddSingleton<HealthReport>();
        builder.Services.AddSingleton<DoctorReport>();
        // Plan 2.7a, Task 5: the provisioner of the server's engine (one provider per server, ruling P2).
        if (StorageSettings.ParseProvider(builder.Configuration[StorageSettings.ProviderKey]) == StorageProvider.PostgreSql)
            builder.Services.AddSingleton<IOrgProvisioner, PostgresOrgProvisioner>();
        else
        {
            builder.Services.AddSingleton<SqlServerOrgProvisioner>();
            builder.Services.AddSingleton<IOrgProvisioner>(sp => sp.GetRequiredService<SqlServerOrgProvisioner>());
        }
        builder.Services.AddScoped<SolutionCascadeDeleter>();
        builder.Services.AddScoped<SolutionLayerLister>();
        builder.Services.AddScoped<SchemaApplier>();
        builder.Services.AddScoped<HierarchySecuritySettings>();
        builder.Services.AddScoped<Migration.MigrationRunStore>();
        builder.Services.AddXrmdTelemetry(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Maps <c>/admin</c> and returns the group: it requires a valid token (401) and the System Administrator role (403, from
    /// <see cref="AdminOnlyFilter"/>), so a route added to it is protected without further work. Handlers must read their
    /// input themselves (<c>HttpRequest</c>, not bound parameters): binding runs before the filter and its 400 would otherwise
    /// precede the 403.
    /// </summary>
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization().AddEndpointFilter<AdminOnlyFilter>();
        admin.MapGet("/health", (Func<HttpContext, Task<IResult>>)HealthAsync);
        admin.MapGet("/doctor", (Func<HttpContext, Task<IResult>>)DoctorAsync);
        admin.MapGet("/logs", (Func<HttpContext, IResult>)Logs);
        admin.MapPost("/orgs", (Func<HttpContext, Task<IResult>>)CreateOrgAsync);
        admin.MapGet("/orgs", (Func<HttpContext, Task<IResult>>)ListOrgsAsync);
        admin.MapDelete("/orgs/{database}", (Func<HttpContext, Task<IResult>>)DeleteOrgAsync);
        admin.MapPost("/orgs/{database}/backup", (Func<HttpContext, Task<IResult>>)BackupOrgAsync);
        admin.MapPost("/orgs/{database}/restore", (Func<HttpContext, Task<IResult>>)RestoreOrgAsync);
        admin.MapDelete("/solutions/{uniquename}", (Func<HttpContext, Task<IResult>>)DeleteSolutionAsync);
        admin.MapPost("/solutions/{uniquename}/uninstall", (Func<HttpContext, Task<IResult>>)UninstallSolutionAsync);
        admin.MapPost("/solutions/{uniquename}/promote", (Func<HttpContext, Task<IResult>>)PromoteSolutionAsync);
        admin.MapGet("/solutions/{uniquename}/layers", (Func<HttpContext, Task<IResult>>)SolutionLayersAsync);
        admin.MapGet("/layers/{componenttype}/{objectid}", (Func<HttpContext, Task<IResult>>)ComponentLayersAsync);
        admin.MapGet("/hierarchy-security", (Func<HttpContext, Task<IResult>>)GetHierarchySecurityAsync);
        admin.MapPut("/hierarchy-security", (Func<HttpContext, Task<IResult>>)PutHierarchySecurityAsync);
        admin.MapSchemaEndpoints();
        admin.MapMigrationPackageEndpoints();
        admin.MapMigrationRunEndpoints();
        return admin;
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static string RouteDatabase(HttpContext context) => context.Request.RouteValues["database"] as string ?? "";

    /// <summary>The JSON object of the request body; an empty body is an empty object, anything else is a 400.</summary>
    private static async Task<JsonObject> ReadBodyAsync(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<JsonObject>(context.RequestAborted) ?? [];
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw XrmdFaultException.InvalidArgument("The request body must be a JSON object: " + ex.Message);
        }
        catch (InvalidOperationException)
        {
            throw XrmdFaultException.InvalidArgument("The request body must be application/json.");
        }
    }

    private static string? OptionalString(JsonObject body, string name) =>
        body[name] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => throw XrmdFaultException.InvalidArgument($"'{name}' must be a string."),
        };

    /// <summary><c>POST /admin/orgs</c> <c>{name, adminObjectId, adminName?}</c>: 201 <c>{name, database, rootBusinessUnitId, adminUserId}</c>.</summary>
    private static async Task<IResult> CreateOrgAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        var name = OptionalString(body, "name");
        var provisioner = context.RequestServices.GetRequiredService<IOrgProvisioner>();
        OrgProvisioning.RequireValidName(name);
        var oidText = OptionalString(body, "adminObjectId");
        if (!Guid.TryParse(oidText, out var oid))
            throw XrmdFaultException.InvalidArgument($"'adminObjectId' must be a GUID; got '{oidText}'.");
        var created = await provisioner.CreateAsync(name, oid, OptionalString(body, "adminName"), context.RequestAborted);
        return Results.Json(created, WebJson, statusCode: StatusCodes.Status201Created);
    }

    /// <summary><c>GET /admin/orgs</c>: <c>[{name, database, schemaVersion, served}]</c>.</summary>
    private static async Task<IResult> ListOrgsAsync(HttpContext context) =>
        Results.Json(await context.RequestServices.GetRequiredService<IOrgProvisioner>().ListAsync(context.RequestAborted), WebJson);

    private static async Task<IResult> DeleteOrgAsync(HttpContext context)
    {
        await context.RequestServices.GetRequiredService<IOrgProvisioner>().DropAsync(RouteDatabase(context), context.RequestAborted);
        return Results.NoContent();
    }

    /// <summary><c>POST /admin/orgs/{database}/backup</c>: <c>{file, sizeBytes}</c>.</summary>
    private static async Task<IResult> BackupOrgAsync(HttpContext context) =>
        Results.Json(await context.RequestServices.GetRequiredService<IOrgProvisioner>()
            .BackupAsync(RouteDatabase(context), context.RequestAborted), WebJson);

    /// <summary><c>POST /admin/orgs/{database}/restore</c> <c>{file, overwrite, files}</c>: 204, or 200 <c>{restored, missingCount, missingFiles[], warning}</c> when the restored database references files the store lacks; <c>files</c> (default true) restores the <c>.files.tar</c> too.</summary>
    private static async Task<IResult> RestoreOrgAsync(HttpContext context)
    {
        var database = RouteDatabase(context);
        OrgProvisioning.RequireValidName(database); // before the body: a bad name is the first thing wrong
        var body = await ReadBodyAsync(context);
        var overwrite = body["overwrite"] switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw XrmdFaultException.InvalidArgument("'overwrite' must be true or false."),
        };
        var files = body["files"] switch
        {
            null => true,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw XrmdFaultException.InvalidArgument("'files' must be true or false."),
        };
        var result = await context.RequestServices.GetRequiredService<IOrgProvisioner>()
            .RestoreAsync(database, OptionalString(body, "file"), overwrite, files, context.RequestAborted);
        if (result.MissingCount == 0) return Results.NoContent();
        return Results.Json(new
        {
            restored = true,
            missingCount = result.MissingCount,
            missingFiles = result.MissingFiles,
            warning = $"The database was restored, but {result.MissingCount} file(s) it references are not in the file store" +
                      (files ? " (deleted or replaced after the backup's database snapshot, before its files archive was written)." : " (the restore ran with files=false).") +
                      " Reading those files fails until they are uploaded again.",
        }, WebJson);
    }

    /// <summary>The 400 of <c>DELETE /admin/solutions/{uniquename}</c> without <c>cascade=true</c>.</summary>
    public const string CascadeRequired =
        "Use the Web API or SDK to delete only the solution container; the Admin API deletes with cascade=true only.";

    /// <summary>
    /// <c>DELETE /admin/solutions/{uniquename}?cascade=true</c>: deletes the solution and the components no other solution
    /// holds (<see cref="SolutionCascadeDeleter"/>), as the caller; <c>{"deleted":[{type,name}],"kept":[{type,name,reason}]}</c>.
    /// </summary>
    private static async Task<IResult> DeleteSolutionAsync(HttpContext context)
    {
        var cascade = context.Request.Query.TryGetValue("cascade", out var cascadeText) ? cascadeText.ToString() : "false";
        if (!bool.TryParse(cascade, out var cascading))
            throw XrmdFaultException.InvalidArgument($"The query option 'cascade' must be true or false; got '{cascade}'.");
        if (!cascading) throw XrmdFaultException.InvalidArgument(CascadeRequired);
        var uniqueName = context.Request.RouteValues["uniquename"] as string ?? "";
        var services = context.RequestServices;
        var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(context.RequestAborted);
        var result = await services.GetRequiredService<SolutionCascadeDeleter>().DeleteAsync(caller, uniqueName, context.RequestAborted);
        return Results.Json(result, WebJson);
    }

    /// <summary>
    /// <c>POST /admin/solutions/{uniquename}/uninstall</c> <c>{dropData?, dryRun?, expectedDataLoss?}</c> (flags false when
    /// absent): uninstalls the managed solution as the caller (<see cref="SolutionUninstaller"/>, plan 2.6, rulings M9 and M10).
    /// Data is dropped only with <c>dropData</c> true; <c>expectedDataLoss</c> (<c>[{table, column?}]</c>, the dry run's
    /// <c>dataLoss</c>) makes the uninstall a 409 when the plan under the lock drops anything else; <c>dryRun</c> true answers
    /// the plan and writes nothing. Answers
    /// <c>{"deleted":[{type,name}],"kept":[{type,name,reason}],"restored":[name],"dataLoss":[{table,column,rows}]}</c>.
    /// </summary>
    private static async Task<IResult> UninstallSolutionAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        var uniqueName = context.Request.RouteValues["uniquename"] as string ?? "";
        var services = context.RequestServices;
        var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(context.RequestAborted);
        var options = new UninstallOptions(BodyFlag(body, "dropData"), BodyFlag(body, "dryRun"), FromSdk: false, ExpectedDataLoss(body));
        var plan = await services.GetRequiredService<SolutionUninstaller>().UninstallAsync(caller, uniqueName, options, context.RequestAborted);
        return Results.Json(plan, WebJson);
    }

    /// <summary>
    /// <c>POST /admin/solutions/{uniquename}/promote</c> <c>{dropData?, dryRun?, expectedDataLoss?}</c> (flags false when
    /// absent): applies the pending upgrade <c>{uniquename}_Upgrade</c> of the managed solution as the caller
    /// (<see cref="SolutionPromoter"/>, plan 2.6, rulings M10 and M11). Data is dropped only with <c>dropData</c> true;
    /// <c>expectedDataLoss</c> as for the uninstall (409 when the plan under the lock drops anything else); <c>dryRun</c> true
    /// answers the plan and writes nothing. Answers <c>{"deleted":[{type,name}],"kept":[{type,name,reason}],"restored":[name],"dataLoss":[{table,column,rows}]}</c>.
    /// </summary>
    private static async Task<IResult> PromoteSolutionAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        var uniqueName = context.Request.RouteValues["uniquename"] as string ?? "";
        var services = context.RequestServices;
        var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(context.RequestAborted);
        var options = new PromoteOptions(BodyFlag(body, "dropData"), BodyFlag(body, "dryRun"), FromSdk: false,
            ExpectedDataLoss: ExpectedDataLoss(body));
        var (_, plan) = await services.GetRequiredService<SolutionPromoter>().PromoteInOwnTransactionAsync(caller, uniqueName, options,
            context.RequestAborted);
        return Results.Json(plan, WebJson);
    }

    /// <summary>
    /// <c>GET /admin/solutions/{uniquename}/layers</c>: the layers the solution owns, <c>[{componentType, objectId, solution, kind,
    /// order, version}]</c> (plan 2.6, ruling M16); 404 for an unknown solution.
    /// </summary>
    private static async Task<IResult> SolutionLayersAsync(HttpContext context)
    {
        var uniqueName = context.Request.RouteValues["uniquename"] as string ?? "";
        var rows = await context.RequestServices.GetRequiredService<SolutionLayerLister>().OfSolutionAsync(uniqueName, context.RequestAborted);
        return Results.Json(rows, WebJson);
    }

    /// <summary>
    /// <c>GET /admin/layers/{componenttype}/{objectid}</c>: the layers of a component, top first, same rows as
    /// <see cref="SolutionLayersAsync"/>; 400 for a type or id that is not a number or GUID; 404 for a component without layers.
    /// </summary>
    private static async Task<IResult> ComponentLayersAsync(HttpContext context)
    {
        var typeText = context.Request.RouteValues["componenttype"] as string ?? "";
        var idText = context.Request.RouteValues["objectid"] as string ?? "";
        if (!int.TryParse(typeText, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
            throw XrmdFaultException.InvalidArgument($"The component type '{typeText}' must be a whole number.");
        if (!Guid.TryParse(idText, out var id))
            throw XrmdFaultException.InvalidArgument($"The object id '{idText}' must be a GUID.");
        var rows = await context.RequestServices.GetRequiredService<SolutionLayerLister>().OfComponentAsync(type, id, context.RequestAborted);
        return Results.Json(rows, WebJson);
    }

    /// <summary>
    /// The <c>expectedDataLoss</c> of a request body (final review M1): <c>[{table, column?}]</c>, as a dry run's <c>dataLoss</c>
    /// answers it (its <c>rows</c> are ignored); null when absent.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when it is present and not such an array.</exception>
    private static List<DataLossKey>? ExpectedDataLoss(JsonObject body)
    {
        const string Shape = "'expectedDataLoss' must be an array of {\"table\": string, \"column\": string or null}.";
        if (body["expectedDataLoss"] is not { } node) return null;
        if (node is not JsonArray items) throw XrmdFaultException.InvalidArgument(Shape);
        var keys = new List<DataLossKey>();
        foreach (var item in items)
        {
            if (item is not JsonObject o || o["table"] is not JsonValue t || !t.TryGetValue<string>(out var table) || string.IsNullOrEmpty(table))
                throw XrmdFaultException.InvalidArgument(Shape);
            string? column = null;
            if (o["column"] is { } c)
            {
                if (c is not JsonValue cv || !cv.TryGetValue<string>(out var given)) throw XrmdFaultException.InvalidArgument(Shape);
                column = given;
            }
            keys.Add(new DataLossKey(table, column));
        }
        return keys;
    }

    /// <summary>The Boolean <paramref name="name"/> of a request body; false when absent.</summary>
    /// <exception cref="XrmdFaultException">400 when it is present and not true or false.</exception>
    private static bool BodyFlag(JsonObject body, string name) => body[name] switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var flag) => flag,
        _ => throw XrmdFaultException.InvalidArgument($"'{name}' must be true or false."),
    };

    /// <summary><c>GET /admin/hierarchy-security</c>: <c>{enabled, depth, excludedTables[]}</c>.</summary>
    private static async Task<IResult> GetHierarchySecurityAsync(HttpContext context) =>
        Results.Json(await context.RequestServices.GetRequiredService<HierarchySecuritySettings>().GetAsync(context.RequestAborted), WebJson);

    /// <summary>
    /// <c>PUT /admin/hierarchy-security</c> <c>{enabled?, depth?, excludedTables?}</c>: the parts given replace the current
    /// ones (<c>excludedTables</c> replaces the whole list), the rest stays; answers the settings as <c>GET</c> does.
    /// An unknown table or a depth outside 1 to 100 is a 400 and nothing is written.
    /// </summary>
    private static async Task<IResult> PutHierarchySecurityAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        bool? enabled = body["enabled"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw XrmdFaultException.InvalidArgument("'enabled' must be true or false."),
        };
        int? depth = body["depth"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<int>(out var d) => d,
            _ => throw XrmdFaultException.InvalidArgument("'depth' must be a whole number from 1 to 100."),
        };
        IReadOnlyList<string>? tables = body["excludedTables"] switch
        {
            null => null,
            JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
                ? s
                : throw XrmdFaultException.InvalidArgument("'excludedTables' must be a list of table logical names.")).ToList(),
            _ => throw XrmdFaultException.InvalidArgument("'excludedTables' must be a list of table logical names."),
        };
        var services = context.RequestServices;
        var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(context.RequestAborted);
        var saved = await services.GetRequiredService<HierarchySecuritySettings>().SetAsync(caller, enabled, depth, tables, context.RequestAborted);
        return Results.Json(saved, WebJson);
    }

    private static async Task<IResult> HealthAsync(HttpContext context)
    {
        var health = await context.RequestServices.GetRequiredService<HealthReport>().GetAsync(context.RequestAborted);
        return Results.Json(health, statusCode: health.Status == HealthReport.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable,
            options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    /// <summary><c>GET /admin/doctor</c>: <c>{"checks":[{name,status,detail}]}</c> (<see cref="DoctorReport"/>).</summary>
    private static async Task<IResult> DoctorAsync(HttpContext context) =>
        Results.Json(new { checks = await context.RequestServices.GetRequiredService<DoctorReport>().GetAsync(context.RequestAborted) }, WebJson);

    /// <summary><c>GET /admin/logs?level=&amp;since=&amp;take=</c>: <c>{"value":[{timestamp,level,category,message,exception}]}</c>, oldest first.</summary>
    private static IResult Logs(HttpContext context)
    {
        var query = context.Request.Query;
        var take = DefaultLogTake;
        if (query.TryGetValue("take", out var takeText)
            && (!int.TryParse(takeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out take) || take < 1 || take > MaxLogTake))
            throw XrmdFaultException.InvalidArgument($"The query option 'take' must be an integer between 1 and {MaxLogTake}; got '{takeText}'.");

        var level = LogLevel.Trace;
        if (query.TryGetValue("level", out var levelText)
            && (!Enum.TryParse(levelText, ignoreCase: true, out level) || !Enum.IsDefined(level) || level == LogLevel.None || int.TryParse(levelText, out _)))
            throw XrmdFaultException.InvalidArgument(
                $"The query option 'level' must be one of Trace, Debug, Information, Warning, Error, Critical; got '{levelText}'.");

        DateTimeOffset? since = null;
        if (query.TryGetValue("since", out var sinceText))
        {
            if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                throw XrmdFaultException.InvalidArgument($"The query option 'since' must be an ISO 8601 date and time; got '{sinceText}'.");
            since = parsed;
        }

        var entries = context.RequestServices.GetRequiredService<LogRingBuffer>().Query(level, since, take);
        return Results.Json(new
        {
            value = entries.Select(e => new
            {
                timestamp = e.Timestamp,
                level = e.Level.ToString(),
                category = e.Category,
                message = e.Message,
                exception = e.Exception,
            }),
        });
    }
}
