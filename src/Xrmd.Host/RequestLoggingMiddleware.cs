namespace Xrmd.Host;

/// <summary>Logs every request at Debug (method, path, query, status) — used to discover client handshake calls.</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    /// <summary>
    /// The query parameters whose value is a credential: the Web API's chunked upload <c>sessiontoken</c>; the local identity
    /// provider's <c>id_token_hint</c> (sign-out) and <c>user_code</c> (device verification), and the authorization <c>code</c> of the redirect back to a client.
    /// </summary>
    private static readonly string[] Secrets = ["sessiontoken", "id_token_hint", "user_code", "code"];

    /// <summary>The query with the value of every secret parameter (any case) replaced by <c>***</c>.</summary>
    public static string RedactQuery(QueryString query)
    {
        if (!query.HasValue) return "";
        var parts = query.Value!.TrimStart('?').Split('&').Select(part =>
        {
            var eq = part.IndexOf('=');
            var name = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            if (Secrets.Any(s => string.Equals(s, name.Trim(), StringComparison.OrdinalIgnoreCase))) return (eq < 0 ? part : part[..eq]) + "=***";
            // The IdP login page's returnUrl carries the request it returns to, user_code included: redacted inside too.
            if (eq >= 0 && string.Equals(name.Trim(), "returnUrl", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                var q = value.IndexOf('?');
                if (q >= 0) return part[..eq] + "=" + Uri.EscapeDataString(value[..q] + RedactQuery(new QueryString(value[q..])));
            }
            return part;
        });
        return "?" + string.Join('&', parts);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var request = context.Request;
                // An exception escaping the pipeline becomes a 500 after this middleware has returned.
                var status = failed && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
                logger.LogDebug("{Method} {Path}{Query} -> {Status}", request.Method, request.Path, RedactQuery(request.QueryString), status);
            }
        }
    }
}
