using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Xrmd.Migration.Source;

/// <summary>A request to the source environment failed for good (after retries, or with a client error).</summary>
public sealed class DataverseException(HttpStatusCode? status, string message, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Reads the source environment through the Web API (<c>/api/data/v9.2/</c>), read only: GET requests and the
/// <c>ExportSolution</c> action, nothing else. The bearer token comes from a delegate, so this library carries no
/// authentication code. Throttling (429) waits <c>Retry-After</c> (5 s when absent) and retries while the token allows;
/// 5xx and network errors are retried 5 times (1, 2, 4, 8, 16 s); other errors fail at once.
/// </summary>
public sealed class DataverseClient : IDisposable
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal) { "ExportSolution" };
    private const int ServerRetries = 5;

    // Action parameters are case-sensitive (SolutionName, Managed): property names are sent as declared.
    private static readonly JsonSerializerOptions ActionJson = new() { PropertyNamingPolicy = null };

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DataverseClient(HttpMessageHandler handler, Uri source, Func<CancellationToken, Task<string>> token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Source = source;
        _http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(source, "api/data/v9.2/"), Timeout = Timeout.InfiniteTimeSpan };
        _token = token;
        _delay = delay ?? Task.Delay;
    }

    public Uri Source { get; }

    /// <summary>How long one attempt may wait for an answer (the body of a download is not limited); then it counts as a network error.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public async Task<JsonDocument> GetJsonAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relative), relative, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    /// <summary>
    /// Every row of an OData query, following <c>@odata.nextLink</c>; a next link that leaves the source environment is refused.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetAllAsync(string relative, CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        string? next = relative;
        while (next is not null)
        {
            using var doc = await GetJsonAsync(next, ct);
            rows.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.GetString() is { } url ? Relative(url) : null;
        }
        return rows;
    }

    private string Relative(string link)
    {
        var baseUri = _http.BaseAddress!;
        var uri = new Uri(baseUri, link);
        if (!baseUri.IsBaseOf(uri))
            throw new DataverseException(null, "The source environment returned a next-page link to another address.");
        return baseUri.MakeRelativeUri(uri).OriginalString;
    }

    /// <summary>One page of a FetchXML query on <paramref name="entitySet"/> (the caller pages by key).</summary>
    public async Task<IReadOnlyList<JsonElement>> FetchAsync(string entitySet, string fetchXml, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{entitySet}?fetchXml={Uri.EscapeDataString(fetchXml)}", ct);
        return [.. doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone())];
    }

    /// <summary>A response body as a stream (file downloads), optionally a byte range; the caller disposes the response.</summary>
    public async Task<HttpResponseMessage> GetStreamAsync(string relative, (long From, long To)? range, CancellationToken ct) =>
        await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, relative);
            if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
            return request;
        }, relative, ct, streaming: true);

    /// <summary>Calls one of the read-only actions (<c>ExportSolution</c>); any other action is refused.</summary>
    public async Task<JsonDocument> PostActionAsync(string action, object body, CancellationToken ct)
    {
        if (!AllowedActions.Contains(action))
            throw new InvalidOperationException($"The migration reads the source only: the action '{action}' is not allowed.");
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, action) { Content = JsonContent.Create(body, options: ActionJson) }, action, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, string what, CancellationToken ct, bool streaming = false)
    {
        var serverFailures = 0;
        while (true)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
            request.Headers.Add("OData-MaxVersion", "4.0");
            request.Headers.Add("OData-Version", "4.0");
            request.Headers.Add("Prefer", "odata.include-annotations=\"*\"");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(AttemptTimeout);
            try
            {
                response = await _http.SendAsync(request, streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, attempt.Token);
            }
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (++serverFailures > ServerRetries) throw new DataverseException(null, $"The source environment did not answer '{Describe(what)}': {e.Message}", e);
                await _delay(Backoff(serverFailures), ct);
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            if (status == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? DefaultRetryAfter;
                response.Dispose();
                await _delay(wait, ct);
                continue;
            }
            if ((int)status >= 500 && ++serverFailures <= ServerRetries)
            {
                response.Dispose();
                await _delay(Backoff(serverFailures), ct);
                continue;
            }
            var detail = await ErrorMessageAsync(response, ct);
            response.Dispose();
            throw new DataverseException(status, $"The source environment refused '{Describe(what)}' ({(int)status}){detail}");
        }
    }

    private static TimeSpan Backoff(int failure) => TimeSpan.FromSeconds(1 << (failure - 1));

    private static string Describe(string relative)
    {
        var q = relative.IndexOf('?');
        return q < 0 ? relative : relative[..q] + "?…";
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? $": {m.GetString()}" : "";
        }
        catch (JsonException) { return ""; }
    }

    public void Dispose() => _http.Dispose();
}
