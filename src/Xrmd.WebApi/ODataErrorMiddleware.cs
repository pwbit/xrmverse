using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi;

/// <summary>
/// Turns exceptions into the Dataverse Web API error envelope <c>{ "error": { "code": "0x80040217", "message": "..." } }</c>:
/// <see cref="XrmdFaultException"/> keeps its code and status, <see cref="BadHttpRequestException"/> (malformed request,
/// body too large) keeps its status, <see cref="XrmdValidationException"/> is a 400 InvalidArgument,
/// anything else (other <see cref="ArgumentException"/>s included) a 500 with a generic message (details are only logged).
/// The <see cref="XrmdFaultException.ErrorDetails"/> of a fault (plan 3.4: the failed targets of a bulk action under
/// <c>Plugin.BulkApiErrorDetails</c>) are written as annotations <c>@Microsoft.PowerApps.CDS.ErrorDetails.{name}</c> of the error
/// object, only when the request's <c>Prefer: odata.include-annotations</c> names <c>*</c>,
/// <c>Microsoft.PowerApps.CDS.ErrorDetails.*</c> or the annotation itself.
/// </summary>
public sealed class ODataErrorMiddleware(RequestDelegate next, ILogger<ODataErrorMiddleware> logger)
{
    private const string GenericMessage = "An unexpected error occurred.";
    private const string ErrorDetailsTerm = "Microsoft.PowerApps.CDS.ErrorDetails.";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            var sql = SqlFaults.Translate(ex);
            var (status, code, message) = ex switch
            {
                _ when sql is not null => (sql.HttpStatus, sql.ErrorCode, sql.Message),
                XrmdFaultException fault => (fault.HttpStatus, fault.ErrorCode, fault.Message),
                BadHttpRequestException bad => (bad.StatusCode, ErrorCodes.InvalidArgument, bad.Message),
                XrmdValidationException invalid => (StatusCodes.Status400BadRequest, ErrorCodes.InvalidArgument, invalid.Message),
                _ => (StatusCodes.Status500InternalServerError, ErrorCodes.Unexpected, GenericMessage),
            };
            if (sql is not null) logger.LogWarning(ex, "SQL failure {Code} for {Path}", FormatCode(code), context.Request.Path);
            else if (status >= StatusCodes.Status500InternalServerError && status != StatusCodes.Status501NotImplemented) logger.LogError(ex, "Unhandled exception for {Path}", context.Request.Path);

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; odata.metadata=minimal";
            context.Response.Headers["OData-Version"] = "4.0";
            var error = new JsonObject { ["code"] = FormatCode(code), ["message"] = message };
            if (sql is null && ex is XrmdFaultException { ErrorDetails.Count: > 0 } detailed)
                foreach (var (name, value) in detailed.ErrorDetails)
                    if (value is not null && IncludesErrorDetail(context.Request, name))
                        error["@" + ErrorDetailsTerm + name] = value as string ?? JsonSerializer.Serialize(value);
            await JsonSerializer.SerializeAsync(context.Response.Body, new JsonObject { ["error"] = error });
        }
    }

    /// <summary>
    /// True when a <c>Prefer: odata.include-annotations</c> of <paramref name="request"/> includes the error detail annotation of
    /// <paramref name="name"/>: <c>*</c>, <c>Microsoft.PowerApps.CDS.ErrorDetails.*</c> or the annotation's full name.
    /// </summary>
    private static bool IncludesErrorDetail(HttpRequest request, string name)
    {
        foreach (var preference in request.Headers["Prefer"].SelectMany(v => EntitySetController.SplitPreferences(v ?? "")))
        {
            var eq = preference.IndexOf('=');
            if (eq < 0 || !string.Equals(preference[..eq].Trim(), "odata.include-annotations", StringComparison.OrdinalIgnoreCase)) continue;
            if (preference[(eq + 1)..].Trim().Trim('"').Split(',', StringSplitOptions.TrimEntries)
                .Any(t => t is "*" || t == ErrorDetailsTerm + "*" || t == ErrorDetailsTerm + name))
                return true;
        }
        return false;
    }

    /// <summary>Error code as Dataverse writes it: <c>0x</c> + 8 lowercase hex digits.</summary>
    public static string FormatCode(int code) => "0x" + unchecked((uint)code).ToString("x8", CultureInfo.InvariantCulture);
}
