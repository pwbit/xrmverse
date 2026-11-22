using System.Collections.Concurrent;

namespace Xrmd.Host;

/// <summary>
/// Runs just before ASP.NET Core's forwarded headers middleware (ruling H4): a request from a trusted proxy whose forwarded host
/// is malformed or not in <c>Proxy:AllowedHosts</c> loses all its forwarded headers, so it keeps its own scheme, host and client address
/// (see <see cref="ProxyOptions"/> for why the middleware alone is not enough). Each rejected host is logged once, at warning.
/// </summary>
public sealed class ForwardedHostGuard(RequestDelegate next, ProxyOptions proxy, ILogger<ForwardedHostGuard> logger)
{
    /// <summary>At most this many distinct rejected hosts are remembered (and logged); the set never grows past it.</summary>
    internal const int MaxLoggedHosts = 256;

    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.OrdinalIgnoreCase);

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Request.Headers;
        // ForwardLimit is 1: the middleware reads the last (rightmost) value, the one the trusted proxy added. Read the way it reads
        // them (empty segments skipped, quotes removed), so "evil.example.com, " cannot pass here as an empty host.
        var values = headers.GetCommaSeparatedValues("X-Forwarded-Host");
        if (values.Length > 0 && proxy.IsTrusted(context.Connection.RemoteIpAddress))
        {
            var host = values[^1];
            var reason = !ProxyOptions.IsWellFormedHost(host) ? "which is not a valid host"
                : !proxy.IsAllowedHost(host) ? $"which is not in Proxy:AllowedHosts ({string.Join(", ", proxy.AllowedHosts)})"
                : null;
            if (reason is not null)
            {
                if (_logged.Count < MaxLoggedHosts && _logged.TryAdd(host, 0))
                    logger.LogWarning(
                        "Ignored the forwarded headers of a request from proxy {Proxy} for host '{Host}', {Reason}. Further requests for this host are ignored without a log.",
                        context.Connection.RemoteIpAddress, host, reason);
                headers.Remove("X-Forwarded-Host");
                headers.Remove("X-Forwarded-Proto");
                headers.Remove("X-Forwarded-For");
            }
        }

        return next(context);
    }
}
