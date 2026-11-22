using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Xrmd.Migration.Tests.Source;

/// <summary>
/// A fake Dataverse Web API for offline tests: routes match the method and the unescaped path and query (regex); the
/// first route that matches answers. Every request is recorded with its body.
/// </summary>
internal sealed class FakeDataverse : HttpMessageHandler
{
    public static readonly Uri Source = new("https://contoso.example.com/");

    private readonly List<(HttpMethod Method, Regex Pattern, Func<HttpRequestMessage, Match, HttpResponseMessage> Respond)> _routes = [];

    private readonly List<(HttpMethod Method, string Url, string? Body, HttpRequestMessage Request)> _requests = [];

    /// <summary>A snapshot of the requests so far (recorded under a lock: the exporter sends in parallel).</summary>
    public List<(HttpMethod Method, string Url, string? Body, HttpRequestMessage Request)> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    /// <summary>An unmatched request fails the test instead of answering 404 (a wrong URL must not pass as an unreadable table).</summary>
    public bool Strict { get; set; }

    public FakeDataverse On(HttpMethod method, string pattern, Func<HttpRequestMessage, Match, HttpResponseMessage> respond)
    {
        _routes.Add((method, new Regex(pattern, RegexOptions.Compiled), respond));
        return this;
    }

    public FakeDataverse Get(string pattern, Func<HttpRequestMessage, Match, HttpResponseMessage> respond) => On(HttpMethod.Get, pattern, respond);

    public FakeDataverse GetJson(string pattern, string json) => Get(pattern, (_, _) => Json(json));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests) _requests.Add((request.Method, url, body, request));
        foreach (var (method, pattern, respond) in _routes)
        {
            if (method != request.Method) continue;
            var match = pattern.Match(url);
            if (match.Success) return respond(request, match);
        }
        if (Strict) throw new InvalidOperationException($"No fake route for {request.Method} {url}");
        return Json("""{"error":{"code":"0x80060888","message":"Resource not found for the segment."}}""", HttpStatusCode.NotFound);
    }
}
