using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Solution;

namespace Xrmd.Host.Ui;

/// <summary>
/// <c>GET /Ribbon/{entity}</c> (plan 3.5 UI-1e, task 1, ruling E1): the entity-level <c>RibbonDiffXml</c> that the installed
/// solutions kept in <c>solution.xrmverse_unsupportedxml</c>, as <c>{"entity":"…","diffs":[{"solution":"…","xml":"&lt;RibbonDiffXml&gt;…"}]}</c>,
/// oldest layer first (managed solutions by install time, then the unmanaged ones). The <c>solution</c> rows are read through
/// <see cref="OrganizationService"/> as the caller (403 without read on <c>solution</c>); an entity the metadata does not know is a
/// 404 and one without a diff answers <c>diffs: []</c>. The ETag is the SHA-256 of the body, so an import changes it.
/// </summary>
internal static class RibbonEndpoints
{
    private const string CacheControl = "private, max-age=0, must-revalidate";

    public static void MapRibbon(this IEndpointRouteBuilder app) =>
        app.MapGet("/Ribbon/{entity}", (HttpContext context, string entity, OrganizationService service, IMetadataStore store, ILoggerFactory loggers) =>
            ServeAsync(context, entity, service, store, loggers.CreateLogger("Xrmd.Host.Ui.RibbonEndpoints"))).RequireAuthorization();

    /// <summary>
    /// The non-empty <c>RibbonDiffXml</c> of <paramref name="entity"/> in each solution's kept XML (<see cref="KeptXml.Parse"/>),
    /// in layer order: managed solutions first, each group by <c>InstalledOn</c> (ties by unique name). A solution with no kept XML,
    /// another entity's ribbon or the empty ribbon template of an export contributes nothing.
    /// </summary>
    internal static IReadOnlyList<(string Solution, string Xml)> DiffsFor(string entity,
        IEnumerable<(string UniqueName, bool IsManaged, DateTime InstalledOn, string? KeptXml)> solutions, ILogger? logger = null)
    {
        var result = new List<(string Solution, string Xml)>();
        foreach (var s in solutions
                     .OrderByDescending(s => s.IsManaged).ThenBy(s => s.InstalledOn).ThenBy(s => s.UniqueName, StringComparer.Ordinal))
        {
            Xrmd.Solution.KeptXml kept;
            try
            {
                kept = Xrmd.Solution.KeptXml.Parse(s.KeptXml);
            }
            catch (InvalidOperationException ex)
            {
                // One damaged row must not take the ribbons of every table down: skip it and say so.
                logger?.LogWarning("The kept XML of solution {Solution} cannot be read and is skipped for /Ribbon/{Entity}: {Reason}", s.UniqueName, entity, ex.Message);
                continue;
            }
            result.AddRange(kept.EntityUnsupported
                .Where(p => string.Equals(p.EntityLogicalName, entity, StringComparison.OrdinalIgnoreCase)
                            && p.Xml.Name.LocalName == "RibbonDiffXml" && CustomizationsReader.HasContent(p.Xml))
                .Select(p => (s.UniqueName, p.Xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting))));
        }
        return result;
    }

    private static async Task<IResult> ServeAsync(HttpContext context, string entity, OrganizationService service, IMetadataStore store, ILogger logger)
    {
        if (store.GetEntity(entity.ToLowerInvariant()) is not { } metadata) return Results.NotFound();
        var ct = context.RequestAborted;

        IReadOnlyList<(string Solution, string Xml)> diffs;
        try
        {
            var query = new QueryExpression("solution")
            {
                ColumnSet = new ColumnSet("uniquename", "ismanaged", "installedon", "xrmverse_unsupportedxml"),
            };
            query.Criteria.AddCondition("xrmverse_unsupportedxml", ConditionOperator.NotNull);
            var rows = (await service.RetrieveMultipleAsync(query, ct)).Entities;
            diffs = DiffsFor(metadata.LogicalName, rows.Select(r => (
                r.GetAttributeValue<string>("uniquename"), r.GetAttributeValue<bool?>("ismanaged") ?? false,
                r.GetAttributeValue<DateTime?>("installedon") ?? DateTime.MinValue, r.GetAttributeValue<string?>("xrmverse_unsupportedxml"))), logger);
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus == 403)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            entity = metadata.LogicalName,
            diffs = diffs.Select(d => new { solution = d.Solution, xml = d.Xml }),
        });
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(body)) + "\"";
        var headers = context.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = CacheControl;
        headers.XContentTypeOptions = "nosniff";
        if (context.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Select(t => t.Trim()).Any(t => t == etag || t == "W/" + etag)))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(body, "application/json");
    }
}
