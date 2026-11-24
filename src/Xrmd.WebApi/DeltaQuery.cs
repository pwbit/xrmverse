using System.Globalization;
using System.Security;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.WebApi;

/// <summary>
/// The Web API delta queries of change tracking (plan 2.13, ruling C12), run as a <c>RetrieveEntityChanges</c> request through the
/// organization pipeline (<see cref="OrganizationService.ExecuteAsync"/>): the Global read guard, the plug-in steps and the error
/// mapping are those of the SDK message.
/// <list type="bullet">
/// <item><c>GET {entityset}</c> with <c>Prefer: odata.track-changes</c>: every row, <c>Preference-Applied: odata.track-changes</c>
/// and, on the last page, an <c>@odata.deltaLink</c>: the request URL with <c>$deltatoken={DataToken}</c> (URL-encoded).</item>
/// <item><c>GET {entityset}?$deltatoken=…</c> (no header needed): the rows new or updated since the token, then one
/// <see cref="ODataJson.DeletedEntryJson"/> per deletion; <c>@odata.context</c> ends in <c>/$delta</c>.</item>
/// <item>Pages of <c>Prefer: odata.maxpagesize</c> (1 to 5000, default 5000) linked by <c>@odata.nextLink</c>, which keeps the
/// <c>$deltatoken</c> and adds a <c>$skiptoken</c> (<see cref="FormatSkipToken"/>: the next page number and the paging cookie,
/// marked <c>istracking="True"</c>, so a next page is a delta page even without the header).</item>
/// <item><c>{entityset}/$count?$deltatoken=…</c>: the number of changes, all pages, as <c>text/plain</c>
/// (<see cref="CountAsync"/>); <c>$count=true</c> on a delta writes the same number as <c>@odata.count</c>.</item>
/// <item>Only <c>$select</c> and <c>$count</c> combine with tracking; <c>$filter</c>, <c>$orderby</c>, <c>$expand</c>, <c>$top</c> (and
/// <c>$apply</c>, <c>fetchXml</c>) give 400, as does tracking on a table without <c>ChangeTrackingEnabled</c>.</item>
/// </list>
/// </summary>
internal static class DeltaQuery
{
    /// <summary>The preference that starts change tracking.</summary>
    public const string TrackChanges = "odata.track-changes";

    private const string DeltaToken = "$deltatoken";
    private const string SkipToken = "$skiptoken";
    private const string Select = "$select";
    private const string Count = "$count";

    /// <summary>The options a tracked query cannot take (C12), with the ones that make no sense on a delta.</summary>
    private static readonly string[] Refused = ["$filter", "$orderby", "$expand", "$top", "$apply", "fetchXml"];

    /// <summary>The system query options a tracked query takes.</summary>
    private static readonly string[] Allowed = [Select, Count, SkipToken, DeltaToken];

    /// <summary>
    /// True when the GET of a collection is a change tracking read: <c>Prefer: odata.track-changes</c>, a <c>$deltatoken</c>, or the
    /// <c>$skiptoken</c> of a delta page.
    /// </summary>
    public static bool IsDelta(HttpRequest request, bool prefersTracking) =>
        prefersTracking || HasKey(request.Query, DeltaToken)
                        || (Value(request.Query, SkipToken) is { Length: > 0 } skip && IsTrackingSkipToken(skip));

    /// <summary>One page of the changes of <paramref name="m"/>, as a delta response.</summary>
    /// <exception cref="XrmdFaultException">400 for an untracked table, a refused option, a page size out of range, an invalid
    /// token or skip token; the faults of RetrieveEntityChanges (expired token, 403 without Global read).</exception>
    public static async Task<IResult> QueryAsync(HttpContext context, OrganizationService service, IMetadataStore store,
        ODataQueryParser parser, EntityMetadata m, bool prefersTracking, string? annotations, int? maxPageSize)
    {
        var request = context.Request;
        var token = Check(request.Query, store, m);
        var size = maxPageSize ?? ChangeTracker.MaxPageSize;
        if (size is < 1 or > ChangeTracker.MaxPageSize)
            throw XrmdFaultException.InvalidArgument(
                $"The odata.maxpagesize preference must be between 1 and {ChangeTracker.MaxPageSize} on a change tracking read, got {size}.");
        var (page, cookie) = Value(request.Query, SkipToken) is { Length: > 0 } skip ? ReadSkipToken(skip) : (1, null);

        var select = SelectList(request);
        var changes = await ExecuteAsync(service, m, Columns(parser, m, request), token, size, page, cookie, context.RequestAborted);

        var updated = new EntityCollection(changes.Changes.OfType<NewOrUpdatedItem>().Select(i => i.NewOrUpdatedEntity).ToList())
        {
            TotalRecordCount = -1,
        };
        var unreadable = updated.Entities.Any(e => e.Attributes.Values.Any(v => v is null))
            ? await service.UnreadableColumnsAsync([m.LogicalName], context.RequestAborted)
            : null;
        var baseUrl = ODataJson.BaseUrl(request);
        var json = EntityCollectionJson.ToJson(updated, m, baseUrl, select, annotations is not null, null,
            relationships: store.GetOneToManyRelationships(), unreadable: unreadable);
        if (token is not null) json["@odata.context"] = json["@odata.context"]!.GetValue<string>() + "/$delta";
        if (IsCountRequested(request.Query))
        {
            var count = await CountAsync(service, m, token, context.RequestAborted);
            var value = json["value"];
            json.Remove("value");
            json["@odata.count"] = count;
            json["value"] = value;
        }
        var entries = json["value"]!.AsArray();
        foreach (var deleted in changes.Changes.OfType<RemovedOrDeletedItem>())
            entries.Add(ODataJson.DeletedEntryJson(baseUrl, m.EntitySetName!, deleted.RemovedItem.Id));

        if (changes.MoreRecords)
            json["@odata.nextLink"] = Link(request, [SkipToken], (SkipToken, FormatSkipToken(page + 1, changes.PagingCookie)));
        else
            json["@odata.deltaLink"] = Link(request, [SkipToken, DeltaToken], (DeltaToken, changes.DataToken));

        var applied = new List<string>();
        if (prefersTracking) applied.Add(TrackChanges);
        if (annotations is not null) applied.Add(annotations);
        if (maxPageSize is { } max) applied.Add($"odata.maxpagesize={max.ToString(CultureInfo.InvariantCulture)}");
        if (applied.Count > 0) context.Response.Headers["Preference-Applied"] = string.Join(",", applied);
        return ODataJson.Result(context, json);
    }

    /// <summary><c>GET {entityset}/$count?$deltatoken=…</c>: the number of changes since the token, as <c>text/plain</c>.</summary>
    /// <exception cref="XrmdFaultException">400 without a <c>$deltatoken</c>, and as <see cref="QueryAsync"/>.</exception>
    public static async Task<IResult> CountResultAsync(HttpContext context, OrganizationService service, IMetadataStore store, EntityMetadata m)
    {
        var query = context.Request.Query;
        if (!HasKey(query, DeltaToken))
            throw XrmdFaultException.InvalidArgument(
                $"The segment '$count' of '{m.EntitySetName}' is supported with a $deltatoken only: it counts the changes since that token.");
        var token = Check(query, store, m);
        if (HasKey(query, SkipToken))
            throw XrmdFaultException.InvalidArgument("$count of a change tracking read counts every page: send it without $skiptoken.");
        var count = await CountAsync(service, m, token, context.RequestAborted);
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.Text(count.ToString(CultureInfo.InvariantCulture), "text/plain");
    }

    /// <summary>
    /// The skip token of page <paramref name="pageNumber"/>: <c>&lt;cookie pagenumber="N" pagingcookie="…" istracking="True" /&gt;</c>,
    /// the FetchXML cookie shape with <c>istracking</c> set (an opaque value for the client).
    /// </summary>
    internal static string FormatSkipToken(int pageNumber, string? pagingCookie) => string.Create(CultureInfo.InvariantCulture,
        $"<cookie pagenumber=\"{pageNumber}\" pagingcookie=\"{SecurityElement.Escape(pagingCookie ?? "")}\" istracking=\"True\" />");

    /// <summary>
    /// The checks every tracked read makes: the table is tracked, no refused or unknown system option, at most one
    /// <c>$deltatoken</c> and not an empty one. Returns the token (null: the first sync).
    /// </summary>
    private static string? Check(IQueryCollection query, IMetadataStore store, EntityMetadata m)
    {
        if (!store.ChangeTrackingTables.Contains(m.LogicalName))
            throw XrmdFaultException.InvalidArgument(
                $"The entity set '{m.EntitySetName}' cannot be read with odata.track-changes or $deltatoken: change tracking is off on table '{m.LogicalName}'.");
        foreach (var key in query.Keys)
        {
            if (Refused.FirstOrDefault(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase)) is { } refused)
                throw XrmdFaultException.InvalidArgument(
                    $"The query option {refused} cannot be used in a change tracking read (odata.track-changes or $deltatoken): only $select and $count can.");
            if (key.StartsWith('$') && !Allowed.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase)))
                throw XrmdFaultException.InvalidArgument($"The query option {key} is not supported in a change tracking read.");
        }
        if (!HasKey(query, DeltaToken)) return null;
        var values = Values(query, DeltaToken);
        if (values.Count != 1) throw XrmdFaultException.InvalidArgument("The query option $deltatoken is specified more than once.");
        return string.IsNullOrEmpty(values[0])
            ? throw XrmdFaultException.InvalidArgument("The $deltatoken is empty: send the token of an @odata.deltaLink, or odata.track-changes to sync from scratch.")
            : values[0];
    }

    /// <summary>Every change since <paramref name="token"/>, all pages of 5000, through the pipeline.</summary>
    private static async Task<int> CountAsync(OrganizationService service, EntityMetadata m, string? token, CancellationToken ct)
    {
        var columns = new ColumnSet(SystemAttributes.PrimaryIdName(m));
        var total = 0;
        string? cookie = null;
        for (var page = 1; ; page++)
        {
            var changes = await ExecuteAsync(service, m, columns, token, ChangeTracker.MaxPageSize, page, cookie, ct);
            total += changes.Changes.Count;
            if (!changes.MoreRecords) return total;
            cookie = changes.PagingCookie;
        }
    }

    /// <summary>RetrieveEntityChanges through the organization pipeline (plug-in steps, the Global read guard, the error mapping).</summary>
    private static async Task<BusinessEntityChanges> ExecuteAsync(OrganizationService service, EntityMetadata m, ColumnSet columns,
        string? token, int pageSize, int pageNumber, string? cookie, CancellationToken ct)
    {
        var request = new RetrieveEntityChangesRequest
        {
            EntityName = m.LogicalName,
            Columns = columns,
            DataVersion = token,
            PageInfo = new PagingInfo { Count = pageSize, PageNumber = pageNumber, PagingCookie = cookie },
        };
        var response = await service.ExecuteAsync(request, ct);
        return response.Results.TryGetValue("EntityChanges", out var value) && value is BusinessEntityChanges changes
            ? changes
            : throw new InvalidOperationException("RetrieveEntityChanges returned no EntityChanges.");
    }

    /// <summary>The <c>$select</c> columns (validated as on a query), or every visible column without <c>$select</c>.</summary>
    private static ColumnSet Columns(ODataQueryParser parser, EntityMetadata m, HttpRequest request)
    {
        var select = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        if (Value(request.Query, Select) is { } text) select[Select] = text;
        var root = parser.Parse(m, new QueryCollection(select), null, null).Root;
        if (!root.AllColumns && root.Columns.Count > 0) return new ColumnSet(root.Columns.Select(c => c.Attribute).Distinct().ToArray());
        return WebApiHiddenAttributes.VisibleColumns(m) is { } visible ? new ColumnSet(visible.ToArray()) : new ColumnSet(true);
    }

    /// <summary>The <c>$select</c> items as written by the client (echoed in <c>@odata.context</c>).</summary>
    private static string[] SelectList(HttpRequest request) => (Value(request.Query, Select) ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsCountRequested(IQueryCollection query) =>
        Value(query, Count) is { } count && (string.Equals(count, "true", StringComparison.OrdinalIgnoreCase)
            ? true
            : string.Equals(count, "false", StringComparison.OrdinalIgnoreCase)
                ? false
                : throw XrmdFaultException.InvalidArgument($"Invalid $count value '{count}': expected true or false."));

    /// <summary>The request URL without the <paramref name="remove"/> options, then <paramref name="append"/> URL-encoded.</summary>
    private static string Link(HttpRequest request, string[] remove, (string Name, string Value) append)
    {
        var parts = (request.QueryString.Value ?? "").TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !remove.Any(r => string.Equals(Uri.UnescapeDataString(p.Split('=', 2)[0]), r, StringComparison.OrdinalIgnoreCase)))
            .Append($"{append.Name}={Uri.EscapeDataString(append.Value)}");
        return $"{ODataJson.BaseUrl(request)}{request.Path}?{string.Join('&', parts)}";
    }

    /// <summary>True when <paramref name="token"/> is a skip token of <see cref="FormatSkipToken"/>.</summary>
    private static bool IsTrackingSkipToken(string token) =>
        Parse(token) is { } root && string.Equals((string?)root.Attribute("istracking"), "True", StringComparison.OrdinalIgnoreCase);

    /// <summary>The page number and the paging cookie of a skip token of <see cref="FormatSkipToken"/>.</summary>
    /// <exception cref="XrmdFaultException">400 for anything else.</exception>
    private static (int PageNumber, string? Cookie) ReadSkipToken(string token)
    {
        if (!IsTrackingSkipToken(token))
            throw XrmdFaultException.InvalidArgument("The $skiptoken is not the one of a change tracking read: follow its @odata.nextLink as written.");
        var (page, cookie) = ODataQueryParser.ReadSkipToken(token);
        return page >= 2 ? (page, cookie) : throw XrmdFaultException.InvalidArgument("The $skiptoken is not valid.");
    }

    private static XElement? Parse(string token)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StringReader(token);
            using var xr = XmlReader.Create(sr, settings);
            return XDocument.Load(xr).Root is { Name.LocalName: "cookie" } root ? root : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static bool HasKey(IQueryCollection query, string name) =>
        query.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static StringValues Values(IQueryCollection query, string name) =>
        query.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) is { } key ? query[key] : StringValues.Empty;

    private static string? Value(IQueryCollection query, string name) =>
        Values(query, name) is { Count: > 0 } values ? values.ToString() : null;
}
