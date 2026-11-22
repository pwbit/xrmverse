using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Xrmd.Core;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi;

/// <summary>
/// <c>POST /api/data/v9.x/$batch</c> (#130), as Learn's "Execute batch operations using the Web API" and OData 4.0 §11.7 describe
/// it: a <c>multipart/mixed</c> body of <c>application/http</c> requests and change sets, run in order, answered in order.
/// <list type="bullet">
/// <item>Each request runs through the rest of the server's pipeline (routing, authorization, the Web API endpoints) as the batch's
/// caller, with its own headers only: the batch's headers do not apply to it. Its URL is absolute, absolute-path or relative to
/// the service root. At most 1000 requests; a batch inside a batch is refused.</item>
/// <item>A change set is atomic: its requests share one transaction (<see cref="ChangeSet"/>), committed when all succeed. A GET
/// in a change set is refused, and so is any endpoint that does not run as a record message in the pipeline (metadata, files,
/// solutions). <c>Content-ID</c> <c>n</c> names the record a request of the change set created: <c>$n</c> at the start of a later
/// request's URL, or as a string value of its JSON body (<c>@odata.bind</c>, <c>@odata.id</c>), is that record's URL; an
/// unknown <c>$n</c> is a 400. A change set that fails answers with the one failed response.</item>
/// <item>The first failure stops the batch, whose status is then the failure's; with <c>Prefer: odata.continue-on-error</c> the
/// batch goes on and answers 200.</item>
/// </list>
/// </summary>
public sealed class BatchRequests(RequestDelegate next)
{
    /// <summary>Learn: a batch can hold up to 1,000 requests.</summary>
    public const int MaxRequests = 1000;

    private static readonly Regex BatchPath = new(@"^/api/data/(v9\.[0-2])/\$batch$", RegexOptions.CultureInvariant);
    private static readonly Regex Reference = new(@"^\$(?<id>[^/?]+)(?<rest>[/?].*)?$", RegexOptions.CultureInvariant);

    public async Task InvokeAsync(HttpContext context)
    {
        var match = BatchPath.Match(context.Request.Path.Value ?? "");
        // Anything else, and an unauthenticated batch (which the /api group answers with its 401), goes on.
        if (!match.Success || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, "The $batch resource only accepts POST.");
            return;
        }

        List<Item> items;
        try
        {
            var boundary = Boundary(context.Request.ContentType)
                           ?? throw new BatchFormatException("The $batch request must have the Content-Type multipart/mixed with a boundary.");
            items = await ReadBatchAsync(context.Request.Body, boundary, context.RequestAborted);
            var count = items.Sum(i => i.ChangeSet?.Count ?? 1);
            if (count > MaxRequests)
                throw new BatchFormatException($"A batch can hold up to {MaxRequests} requests; this one holds {count}.");
        }
        catch (BatchFormatException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }

        var root = $"/api/data/{match.Groups[1].Value}/";
        var continueOnError = context.Request.Headers.GetCommaSeparatedValues("Prefer")
            .Any(p => p.Trim().Equals("odata.continue-on-error", StringComparison.OrdinalIgnoreCase));
        var responseBoundary = "batchresponse_" + Guid.NewGuid();
        var output = new StringBuilder();
        var status = StatusCodes.Status200OK;
        foreach (var item in items)
        {
            int failed;
            if (item.ChangeSet is { } requests)
                failed = await RunChangeSetAsync(context, root, requests, output, responseBoundary);
            else
            {
                var response = await RunAsync(context, root, item.Request!, null);
                AppendPart(output, responseBoundary, response, null);
                failed = response.Status >= 400 ? response.Status : 0;
            }
            if (failed == 0 || continueOnError) continue;
            status = failed;
            break;
        }
        output.Append("--").Append(responseBoundary).Append("--\r\n");

        context.Response.StatusCode = status;
        context.Response.ContentType = $"multipart/mixed; boundary={responseBoundary}";
        context.Response.Headers["OData-Version"] = "4.0";
        await context.Response.WriteAsync(output.ToString(), Encoding.UTF8, context.RequestAborted);
    }

    /// <summary>Runs a change set in one transaction; returns 0 when it committed, else the status of the response it answered with.</summary>
    private async Task<int> RunChangeSetAsync(HttpContext context, string root, List<PartRequest> requests, StringBuilder output, string batchBoundary)
    {
        var changeSet = context.RequestServices.GetRequiredService<ChangeSet>();
        var created = new Dictionary<string, string>(StringComparer.Ordinal);
        var responses = new List<(PartResponse Response, string? ContentId)>();
        PartResponse? failure = null;
        string? failedId = null;
        await changeSet.BeginAsync(context.RequestAborted);
        try
        {
            foreach (var request in requests)
            {
                var response = HttpMethods.IsGet(request.Method)
                    ? ErrorResponse(StatusCodes.Status400BadRequest, "A GET request is not allowed in a change set.")
                    : await RunAsync(context, root, request, created);
                if (response.Status >= 400)
                {
                    failure = response;
                    failedId = request.ContentId;
                    break;
                }
                if (request.ContentId is { } id && response.Headers.TryGetValue("OData-EntityId", out var url)) created[id] = url;
                responses.Add((response, request.ContentId));
            }
            if (failure is null) await changeSet.CommitAsync();
        }
        catch (Exception ex) when (ex is XrmdFaultException or DbException)
        {
            failure = ex is XrmdFaultException fault
                ? ErrorResponse(fault.HttpStatus, fault.Message, fault.ErrorCode)
                : ErrorResponse(StatusCodes.Status500InternalServerError, "The change set could not be committed.");
            failedId = null;
        }
        finally
        {
            await changeSet.RollbackAsync(); // nothing happens once committed
        }

        if (failure is not null)
        {
            // OData §11.7.4: a change set that fails answers with a single response.
            AppendPart(output, batchBoundary, failure, failedId);
            return failure.Status;
        }
        var boundary = "changesetresponse_" + Guid.NewGuid();
        output.Append("--").Append(batchBoundary).Append("\r\n")
            .Append("Content-Type: multipart/mixed; boundary=").Append(boundary).Append("\r\n\r\n");
        foreach (var (response, id) in responses) AppendPart(output, boundary, response, id);
        output.Append("--").Append(boundary).Append("--\r\n");
        return 0;
    }

    /// <summary>Runs one request through the rest of the pipeline in a context of its own.</summary>
    private async Task<PartResponse> RunAsync(HttpContext context, string root, PartRequest request, Dictionary<string, string>? created)
    {
        string target;
        byte[] body;
        try
        {
            target = Target(context, root, request.Url, created);
            body = created is null ? request.Body : Substitute(request.Body, created);
        }
        catch (BatchFormatException ex)
        {
            return ErrorResponse(StatusCodes.Status400BadRequest, ex.Message);
        }

        var sub = new DefaultHttpContext { RequestServices = context.RequestServices, User = context.User };
        sub.Features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = context.RequestAborted });
        if (created is not null) sub.Features.Set(new ChangeSetRequest());
        var r = sub.Request;
        r.Method = request.Method;
        r.Scheme = context.Request.Scheme;
        r.Host = context.Request.Host;
        r.PathBase = context.Request.PathBase;
        r.Protocol = "HTTP/1.1";
        var query = target.IndexOf('?');
        r.Path = new PathString(Uri.UnescapeDataString(query < 0 ? target : target[..query]));
        r.QueryString = new QueryString(query < 0 ? "" : target[query..]);
        foreach (var (name, value) in request.Headers) r.Headers.Append(name, value);
        // The batch's caller: its credentials, not its other headers (Learn: the batch's headers do not apply to its requests).
        foreach (var name in (string[])[HeaderNames.Authorization, HeaderNames.Cookie])
            if (!r.Headers.ContainsKey(name) && context.Request.Headers.TryGetValue(name, out var credential)) r.Headers[name] = credential;
        r.Body = new MemoryStream(body);
        r.ContentLength = body.Length;
        sub.Connection.RemoteIpAddress = context.Connection.RemoteIpAddress;
        var responseBody = new MemoryStream();
        sub.Response.Body = responseBody;

        await next(sub);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in sub.Response.Headers) headers[name] = value.ToString();
        if (sub.Response.ContentType is { } type) headers[HeaderNames.ContentType] = type;
        return new PartResponse(sub.Response.StatusCode, headers, responseBody.ToArray());
    }

    /// <summary>The path and query of a request: <c>$n</c> resolved, an absolute URL reduced to its path, a relative one under the root.</summary>
    private static string Target(HttpContext context, string root, string url, Dictionary<string, string>? created)
    {
        if (url.StartsWith('$') && Reference.Match(url) is { Success: true } reference)
        {
            var id = reference.Groups["id"].Value;
            if (created is null || !created.TryGetValue(id, out var resolved))
                throw new BatchFormatException($"Content-ID Reference: '${id}' does not exist in the batch context.");
            url = resolved + reference.Groups["rest"].Value;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return absolute.PathAndQuery[context.Request.PathBase.Value!.Length..] is var path && path.StartsWith('/') ? path : "/" + path;
        if (url.StartsWith('/')) return url;
        return root + url;
    }

    /// <summary>A JSON body whose string values <c>$n</c> become the URL of the record request <c>n</c> created.</summary>
    private static byte[] Substitute(byte[] body, Dictionary<string, string> created)
    {
        if (body.Length == 0) return body;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body; // the endpoint reports the invalid body
        }
        if (node is null) return body;
        Replace(node, created);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static void Replace(JsonNode node, Dictionary<string, string> created)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && Resolve(text, created) is { } url) o[key] = url;
                    else if (value is not null) Replace(value, created);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var text) && Resolve(text, created) is { } url) a[i] = url;
                    else if (a[i] is { } child) Replace(child, created);
                }
                break;
        }
    }

    /// <summary>The URL <c>$n</c> stands for; null when the string is no reference; a 400 when <c>n</c> is unknown.</summary>
    private static string? Resolve(string text, Dictionary<string, string> created)
    {
        if (text.Length < 2 || text[0] != '$' || text.IndexOfAny(['/', '?', ' ']) >= 0) return null;
        var id = text[1..];
        // Only a Content-ID can follow '$' here: OData's own $-prefixed words never stand alone as a property value.
        return created.TryGetValue(id, out var url)
            ? url
            : throw new BatchFormatException($"Content-ID Reference: '{text}' does not exist in the batch context.");
    }

    // ---- reading ----

    private static string? Boundary(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var type)
        && type.MediaType.Equals("multipart/mixed", StringComparison.OrdinalIgnoreCase)
        && HeaderUtilities.RemoveQuotes(type.Boundary).Value is { Length: > 0 } boundary
            ? boundary
            : null;

    private static async Task<List<Item>> ReadBatchAsync(Stream body, string boundary, CancellationToken ct)
    {
        var items = new List<Item>();
        var reader = new MultipartReader(boundary, body);
        while (await NextSectionAsync(reader, ct) is { } section)
        {
            if (Boundary(section.ContentType) is { } changeSetBoundary)
            {
                var requests = new List<PartRequest>();
                var inner = new MultipartReader(changeSetBoundary, section.Body);
                while (await NextSectionAsync(inner, ct) is { } part)
                {
                    if (Boundary(part.ContentType) is not null) throw new BatchFormatException("A change set cannot hold another change set.");
                    requests.Add(await ReadRequestAsync(part, ct));
                }
                var ids = requests.Where(r => r.ContentId is not null).GroupBy(r => r.ContentId).FirstOrDefault(g => g.Count() > 1);
                if (ids is not null) throw new BatchFormatException($"The Content-ID '{ids.Key}' is used twice in a change set.");
                items.Add(new Item(null, requests));
            }
            else items.Add(new Item(await ReadRequestAsync(section, ct), null));
        }
        return items;
    }

    private static async Task<MultipartSection?> NextSectionAsync(MultipartReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadNextSectionAsync(ct);
        }
        catch (IOException ex)
        {
            throw new BatchFormatException("The $batch body is not valid multipart/mixed content: " + ex.Message);
        }
        catch (InvalidDataException ex)
        {
            throw new BatchFormatException("The $batch body is not valid multipart/mixed content: " + ex.Message);
        }
    }

    /// <summary>An <c>application/http</c> part: request line, headers, empty line, body.</summary>
    private static async Task<PartRequest> ReadRequestAsync(MultipartSection section, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(section.ContentType, out var type)
            || !type.MediaType.Equals("application/http", StringComparison.OrdinalIgnoreCase))
            throw new BatchFormatException($"A batch part must have the Content-Type application/http, not '{section.ContentType}'.");
        string? contentId = section.Headers is { } h && h.TryGetValue("Content-ID", out var cid) ? cid.ToString().Trim() : null;

        using var buffer = new MemoryStream();
        await section.Body.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var (headEnd, bodyStart) = HeadEnd(bytes);
        var head = Encoding.UTF8.GetString(bytes, 0, headEnd).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (head.Count > 0 && head[0].Length == 0) head.RemoveAt(0);
        if (head.Count == 0) throw new BatchFormatException("A batch part holds no request.");
        // METHOD URL [HTTP/1.1]; a URL with unencoded spaces is taken whole.
        var requestLine = head[0].Trim();
        var space = requestLine.IndexOf(' ');
        if (space <= 0) throw new BatchFormatException($"Invalid request line in a batch part: '{head[0]}'.");
        var url = requestLine[(space + 1)..].Trim();
        var version = url.LastIndexOf(" HTTP/", StringComparison.Ordinal);
        if (version >= 0) url = url[..version].TrimEnd();
        if (url.Length == 0) throw new BatchFormatException($"Invalid request line in a batch part: '{head[0]}'.");
        string[] line = [requestLine[..space], url];
        var method = line[0].ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE"))
            throw new BatchFormatException($"The method '{line[0]}' is not supported in a batch.");
        var headers = new List<(string, string)>();
        foreach (var header in head.Skip(1).Where(l => l.Length > 0))
        {
            var colon = header.IndexOf(':');
            if (colon <= 0) throw new BatchFormatException($"Invalid header in a batch part: '{header}'.");
            headers.Add((header[..colon].Trim(), header[(colon + 1)..].Trim()));
        }
        var body = bytes[bodyStart..];
        // The part's trailing line break belongs to the multipart framing, not to the body.
        var end = body.Length;
        while (end > 0 && body[end - 1] is (byte)'\r' or (byte)'\n') end--;
        if (body[..end].All(b => b is (byte)' ' or (byte)'\t')) end = 0;
        if (line[1].Contains("$batch", StringComparison.Ordinal)) throw new BatchFormatException("A batch cannot hold another batch.");
        return new PartRequest(method, line[1], headers, body[..end], contentId);
    }

    /// <summary>Where the head ends and the body starts: the first empty line (CRLF or LF); the whole part when there is none.</summary>
    private static (int HeadEnd, int BodyStart) HeadEnd(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != '\n') continue;
            var j = i + 1;
            if (j < bytes.Length && bytes[j] == '\r') j++;
            if (j < bytes.Length && bytes[j] == '\n') return (i, j + 1);
            if (j >= bytes.Length) return (i, bytes.Length);
        }
        return (bytes.Length, bytes.Length);
    }

    // ---- writing ----

    private static void AppendPart(StringBuilder output, string boundary, PartResponse response, string? contentId)
    {
        output.Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n");
        if (contentId is not null) output.Append("Content-ID: ").Append(contentId).Append("\r\n");
        output.Append("\r\n").Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(ReasonPhrase(response.Status)).Append("\r\n");
        foreach (var (name, value) in response.Headers)
            if (!name.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
                output.Append(name).Append(": ").Append(value).Append("\r\n");
        output.Append("\r\n");
        if (response.Body.Length > 0) output.Append(Encoding.UTF8.GetString(response.Body)).Append("\r\n");
    }

    private static string ReasonPhrase(int status) =>
        Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase ? phrase : ((HttpStatusCode)status).ToString();

    private static PartResponse ErrorResponse(int status, string message, int code = ErrorCodes.InvalidArgument)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = ODataErrorMiddleware.FormatCode(code), ["message"] = message },
        });
        return new PartResponse(status, new Dictionary<string, string>
        {
            [HeaderNames.ContentType] = "application/json; odata.metadata=minimal", ["OData-Version"] = "4.0",
        }, body);
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        var error = ErrorResponse(status, message);
        context.Response.StatusCode = status;
        context.Response.ContentType = error.Headers[HeaderNames.ContentType];
        context.Response.Headers["OData-Version"] = "4.0";
        await context.Response.Body.WriteAsync(error.Body, context.RequestAborted);
    }

    private sealed record Item(PartRequest? Request, List<PartRequest>? ChangeSet);

    private sealed record PartRequest(string Method, string Url, List<(string Name, string Value)> Headers, byte[] Body, string? ContentId);

    private sealed record PartResponse(int Status, Dictionary<string, string> Headers, byte[] Body);

    private sealed class BatchFormatException(string message) : Exception(message);
}

/// <summary>Marks a request of a change set (#130): only an endpoint with <see cref="ChangeSetEndpoint"/> metadata may serve it.</summary>
public sealed class ChangeSetRequest;

/// <summary>Endpoint metadata: the endpoint runs as record messages in the pipeline, so it can join a change set (#130).</summary>
public sealed class ChangeSetEndpoint
{
    public static readonly ChangeSetEndpoint Instance = new();
}

public static class BatchRequestsExtensions
{
    /// <summary>Marks the endpoint as one a change set can hold (see <see cref="BatchRequests"/>).</summary>
    public static TBuilder InChangeSet<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(ChangeSetEndpoint.Instance);

    /// <summary>
    /// Serves <c>$batch</c>. Before routing: each request of a batch runs through routing, authorization and the endpoints. After
    /// routing comes <see cref="UseXrmdChangeSetGuard"/>.
    /// </summary>
    public static IApplicationBuilder UseXrmdBatch(this IApplicationBuilder app) => app.UseMiddleware<BatchRequests>();

    /// <summary>Refuses (400) a change set request whose endpoint cannot join the change set's transaction.</summary>
    public static IApplicationBuilder UseXrmdChangeSetGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Features.Get<ChangeSetRequest>() is not null && context.GetEndpoint() is { } endpoint
            && endpoint.Metadata.GetMetadata<ChangeSetEndpoint>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; odata.metadata=minimal";
            context.Response.Headers["OData-Version"] = "4.0";
            await context.Response.WriteAsync(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = ODataErrorMiddleware.FormatCode(ErrorCodes.InvalidArgument),
                    ["message"] = $"The request '{context.Request.Method} {context.Request.Path}' cannot run in a change set: " +
                                  "only record writes, $ref and actions can.",
                },
            }.ToJsonString());
            return;
        }
        await next(context);
    });
}
