using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Xrmd.Cli.Http;

/// <summary>
/// HTTP client for one xrmverse server: Web API under <c>/api/data/v9.2/</c>, Admin API under <c>/admin/</c>.
/// Every failure becomes a <see cref="CliException"/> with the exit code of the table, here and nowhere else:
/// 401/403 → 3, 404 → 4, 400/409/412 → 5, other 4xx/5xx → 1, unreachable/timeout → 6. The message is the
/// server's OData error <c>error.message</c>.
/// </summary>
public sealed class XrmdClient
{
    public const string WebApiPath = "api/data/v9.2/";
    public const string AdminPath = "admin/";
    public const string AuthoringPath = "authoring/";
    public const int MaxPageSize = 5000;

    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>>? _token;

    /// <param name="baseUrl">Server base URL (a trailing slash is added when missing).</param>
    /// <param name="token">Bearer token source, called per request; null sends no Authorization header.</param>
    public XrmdClient(HttpClient http, Uri baseUrl, Func<CancellationToken, Task<string>>? token)
    {
        _http = http;
        _token = token;
        BaseUrl = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
    }

    public Uri BaseUrl { get; }

    /// <summary>GET a Web API path (relative to <c>/api/data/v9.2/</c>, e.g. <c>accounts?$top=1</c>) and return its JSON.</summary>
    public async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(WebApiRequest(HttpMethod.Get, WebApiUri(path)), cancellationToken);
        return response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {path}.");
    }

    /// <summary>
    /// Send a Web API request with an optional JSON body (serialized as is: property names are not changed).
    /// <paramref name="headers"/> adds request headers such as <c>Prefer: return=representation</c> or <c>If-Match</c>.
    /// </summary>
    public Task<XrmdResponse> SendJsonAsync(HttpMethod method, string path, object? body = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var request = WebApiRequest(method, WebApiUri(path));
        if (body is not null) request.Content = JsonContent(body);
        if (headers is not null)
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        return SendAsync(request, cancellationToken);
    }

    /// <summary>GET a Web API collection and every following page (<c>@odata.nextLink</c>); returns all <c>value</c> items.</summary>
    public Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(string path, CancellationToken cancellationToken = default) =>
        GetAllPagesAsync(path, null, cancellationToken);

    /// <summary>As <see cref="GetAllPagesAsync(string, CancellationToken)"/>; <paramref name="prefer"/> is added to the <c>Prefer</c> header of every page (e.g. <c>odata.include-annotations="*"</c>).</summary>
    public async Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(string path, string? prefer, CancellationToken cancellationToken = default)
    {
        var rows = new List<JsonElement>();
        Uri? next = WebApiUri(path);
        while (next is not null)
        {
            var response = await SendAsync(WebApiRequest(HttpMethod.Get, next, prefer), cancellationToken);
            var page = response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {next}.");
            if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                throw new CliException(ExitCodes.Unexpected, $"GET {next} did not return a collection.");
            rows.AddRange(value.EnumerateArray());
            next = page.TryGetProperty("@odata.nextLink", out var link) && link.GetString() is { } s ? SameOrigin(new Uri(BaseUrl, s)) : null;
        }
        return rows;
    }

    /// <summary>Call the Admin API (path relative to <c>/admin/</c>, e.g. <c>orgs</c>). Use <see cref="JsonContent"/> for JSON bodies.</summary>
    public Task<XrmdResponse> AdminAsync(HttpMethod method, string path, HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUrl, AdminPath + path.TrimStart('/'))) { Content = content };
        request.Headers.Accept.Add(AcceptJson);
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Call the authoring API beside the Admin API (path relative to <c>/authoring/</c>, e.g. <c>workflows</c>; plan 2.21, A18):
    /// the same error mapping as <see cref="AdminAsync"/>.
    /// </summary>
    public Task<XrmdResponse> AuthoringAsync(HttpMethod method, string path, HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUrl, AuthoringPath + path.TrimStart('/'))) { Content = content };
        request.Headers.Accept.Add(AcceptJson);
        return SendAsync(request, cancellationToken);
    }

    public static HttpContent JsonContent(object body)
    {
        var json = body is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(body, body.GetType());
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    /// <summary>The exit code for an unsuccessful HTTP status.</summary>
    public static int ExitCodeFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ExitCodes.Auth,
        HttpStatusCode.NotFound => ExitCodes.NotFound,
        HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => ExitCodes.Rejected,
        _ => ExitCodes.Unexpected,
    };

    /// <summary>A next link must stay on the server's origin: the bearer token is never sent elsewhere.</summary>
    private Uri SameOrigin(Uri link) =>
        Uri.Compare(link, BaseUrl, UriComponents.SchemeAndServer | UriComponents.Port, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? link
            : throw new CliException(ExitCodes.Unexpected,
                $"The server returned an @odata.nextLink on another origin ({link}); refusing to follow it with the token for {BaseUrl}.");

    private Uri WebApiUri(string path) => new(BaseUrl, WebApiPath + path.TrimStart('/'));

    private static HttpRequestMessage WebApiRequest(HttpMethod method, Uri uri, string? prefer = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(AcceptJson);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        if (method == HttpMethod.Get) request.Headers.Add("Prefer", prefer is null ? $"odata.maxpagesize={MaxPageSize}" : $"odata.maxpagesize={MaxPageSize},{prefer}");
        return request;
    }

    private async Task<XrmdResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            if (_token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(cancellationToken));

            HttpResponseMessage? response = null;
            byte[] body;
            try
            {
                // Headers first, then the body inside the same try: a transport failure while the body
                // streams (e.g. a large backup download) is "unreachable" too, not an unexpected error.
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                response?.Dispose();
                throw new CliException(ExitCodes.Unreachable, $"Cannot reach {BaseUrl}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw new CliException(ExitCodes.Unreachable, $"Timed out waiting for {BaseUrl}.", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new CliException(ExitCodeFor(response.StatusCode), ErrorMessage(response, body)) { HttpStatus = (int)response.StatusCode, ResponseBody = Encoding.UTF8.GetString(body) };

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                    headers[name] = string.Join(", ", values);
                return new XrmdResponse((int)response.StatusCode, body, response.Content.Headers.ContentType?.MediaType, headers);
            }
        }
    }

    private static string ErrorMessage(HttpResponseMessage response, byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } m)
                return m;
        }
        catch (JsonException) { }
        return $"The server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
    }
}

/// <summary>
/// A successful response. <see cref="Bytes"/> is the raw body (solution exports, backups); <see cref="Text"/>
/// is it decoded as UTF-8 (the server's encoding); <see cref="Json"/> is it parsed when it is JSON.
/// </summary>
public sealed record XrmdResponse(int StatusCode, byte[] Bytes, string? ContentType, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The body as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Bytes);

    /// <summary>The body parsed as JSON (a detached copy), or null when empty or not JSON.</summary>
    public JsonElement? Json { get; } = ParseJson(Bytes, ContentType);

    /// <summary>The id at the end of the <c>OData-EntityId</c> header (Web API create), if any.</summary>
    public Guid? EntityId =>
        Headers.TryGetValue("OData-EntityId", out var url) && url.LastIndexOf('(') is var open and >= 0
        && Guid.TryParse(url.AsSpan(open + 1).TrimEnd(')'), out var id) ? id : null;

    private static JsonElement? ParseJson(byte[] bytes, string? contentType)
    {
        if (bytes.Length == 0 || contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new CliException(ExitCodes.Unexpected, $"The server returned invalid JSON: {ex.Message}", ex);
        }
    }
}
