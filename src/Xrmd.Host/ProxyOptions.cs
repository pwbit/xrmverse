using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Primitives;
using AspNetIPNetwork = System.Net.IPNetwork;

namespace Xrmd.Host;

/// <summary>
/// The reverse proxy in front of the host (ruling H4): <c>Proxy:TrustedProxies</c>, comma-separated IP addresses and CIDR
/// networks whose <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> are honored, and <c>Proxy:AllowedHosts</c>,
/// comma-separated host names a forwarded host must match (empty: any). No trusted proxy: forwarded headers are ignored.
/// </summary>
/// <remarks>
/// ASP.NET Core's <see cref="ForwardedHeadersMiddleware"/> with <see cref="ForwardedHeadersOptions.AllowedHosts"/> stops at a
/// forwarded host that is not allowed, but still applies the scheme (and the client address) of that same entry: a request for
/// <c>evil.example.com</c> would get <c>https://localhost</c>. <see cref="ForwardedHostGuard"/>, which runs before it, drops
/// every forwarded header of such a request, so the request keeps its own scheme, host and client address.
/// </remarks>
public sealed partial class ProxyOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "Proxy";

    /// <summary>The exit code of a host refused at startup because of its configuration (the same as <see cref="SdkBootstrap.ExitCode"/>).</summary>
    public const int ExitCode = SdkBootstrap.ExitCode;

    private ProxyOptions(IReadOnlyList<IPAddress> proxies, IReadOnlyList<AspNetIPNetwork> networks, IReadOnlyList<string> allowedHosts)
    {
        KnownProxies = proxies;
        KnownNetworks = networks;
        AllowedHosts = allowedHosts;
    }

    public IReadOnlyList<IPAddress> KnownProxies { get; }

    public IReadOnlyList<AspNetIPNetwork> KnownNetworks { get; }

    public IReadOnlyList<string> AllowedHosts { get; }

    /// <summary>True when at least one proxy is trusted; otherwise forwarded headers are ignored.</summary>
    public bool Enabled => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    /// <summary>The options read from <paramref name="configuration"/>.</summary>
    /// <exception cref="InvalidOperationException">An entry is not an IP address, a CIDR network or a host name; the message names it.</exception>
    public static ProxyOptions Parse(IConfiguration configuration)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<AspNetIPNetwork>();
        foreach (var entry in Entries(configuration[Section + ":TrustedProxies"]))
        {
            if (TryParseNetwork(entry) is { } network) networks.Add(network);
            else if (TryParseAddress(entry) is { } address) proxies.Add(address);
            else
                throw new InvalidOperationException(
                    $"Proxy:TrustedProxies: '{entry}' is not an IP address or a CIDR network (e.g. 172.30.0.2 or 172.30.0.0/24).");
        }

        var hosts = new List<string>();
        foreach (var entry in Entries(configuration[Section + ":AllowedHosts"]))
        {
            if (!HostPattern().IsMatch(entry))
                throw new InvalidOperationException(
                    $"Proxy:AllowedHosts: '{entry}' is not a host name (e.g. acme.example.com, without scheme or path).");
            hosts.Add(entry);
        }

        return new ProxyOptions(proxies, networks, hosts);
    }

    /// <summary>Null when the configuration is valid, else the reason the host must not start (the caller exits with <see cref="ExitCode"/>).</summary>
    public static string? Validate(IConfiguration configuration)
    {
        try
        {
            Parse(configuration);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The options of ASP.NET Core's forwarded headers middleware: one proxy hop, only the configured proxies.</summary>
    public ForwardedHeadersOptions ToForwardedHeadersOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            ForwardLimit = 1,
        };
        options.KnownIPNetworks.Clear(); // the defaults trust loopback
        options.KnownProxies.Clear();
        foreach (var network in KnownNetworks) options.KnownIPNetworks.Add(network);
        foreach (var proxy in KnownProxies) options.KnownProxies.Add(proxy);
        foreach (var host in AllowedHosts) options.AllowedHosts.Add(host);
        return options;
    }

    /// <summary>True when <paramref name="address"/> is a configured proxy or in a configured network (IPv4-mapped IPv6 included).</summary>
    public bool IsTrusted(IPAddress? address)
    {
        if (address is null) return false;
        if (KnownProxies.Contains(address) || KnownNetworks.Any(n => n.Contains(address))) return true;
        return address.IsIPv4MappedToIPv6 && IsTrusted(address.MapToIPv4());
    }

    /// <summary>True when <paramref name="host"/> matches <see cref="AllowedHosts"/> (always when it is empty).</summary>
    public bool IsAllowedHost(string host) =>
        AllowedHosts.Count == 0 || HostString.MatchesAny(new StringSegment(host), AllowedHosts.Select(h => new StringSegment(h)).ToList());

    /// <summary>
    /// True when <paramref name="value"/> is a host name, an IPv4 address or a bracketed IPv6 address, with an optional numeric port:
    /// what a proxy forwards. Anything else is refused whole, as a host outside <see cref="AllowedHosts"/> is.
    /// </summary>
    public static bool IsWellFormedHost(string value)
    {
        string host, port;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0) return false;
            host = value[..(close + 1)];
            var rest = value[(close + 1)..];
            if (rest.Length > 0 && rest[0] != ':') return false;
            port = rest.Length > 0 ? rest[1..] : "";
            if (rest.Length > 0 && port.Length == 0) return false;
            if (Uri.CheckHostName(host[1..^1]) != UriHostNameType.IPv6) return false;
        }
        else
        {
            var colon = value.IndexOf(':');
            if (colon >= 0 && value.IndexOf(':', colon + 1) >= 0) return false; // an unbracketed IPv6 address
            host = colon < 0 ? value : value[..colon];
            port = colon < 0 ? "" : value[(colon + 1)..];
            if (colon >= 0 && port.Length == 0) return false;
            if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        }

        return port.Length == 0
               || (port.Length <= 5 && port.All(char.IsAsciiDigit) && int.Parse(port, CultureInfo.InvariantCulture) <= 65535);
    }

    private static IEnumerable<string> Entries(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static AspNetIPNetwork? TryParseNetwork(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash < 0) return null;
        if (TryParseAddress(entry[..slash]) is not { } prefix) return null;
        var max = prefix.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var length = entry[(slash + 1)..];
        return length.Length is > 0 and <= 3 && length.All(char.IsAsciiDigit)
               && int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) && bits <= max
            && HasNoHostBits(prefix, bits)
            ? new AspNetIPNetwork(prefix, bits)
            : null;
    }

    /// <summary>
    /// True when <paramref name="address"/> is the base address of its <paramref name="bits"/>-bit network. .NET 10 parses
    /// <c>10.1.0.5/16</c> as <c>10.1.0.0/16</c> without complaint; a typo must not silently shift a trust boundary.
    /// </summary>
    private static bool HasNoHostBits(IPAddress address, int bits)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var hostBits = Math.Clamp(8 * (i + 1) - bits, 0, 8);
            if ((bytes[i] & ((1 << hostBits) - 1)) != 0) return false;
        }

        return true;
    }

    /// <summary>A dotted IPv4 address of four parts or an IPv6 address; not the short forms IPAddress also accepts (e.g. "10.5").</summary>
    private static IPAddress? TryParseAddress(string entry)
    {
        if (entry.Contains(':')) return IPAddress.TryParse(entry, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : null;
        return DottedIPv4().IsMatch(entry) && IPAddress.TryParse(entry, out var v4) ? v4 : null;
    }

    [GeneratedRegex(@"\A\d{1,3}(\.\d{1,3}){3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DottedIPv4();

    /// <summary>A host name, optionally <c>*.</c>-prefixed and with a port, as <see cref="ForwardedHeadersOptions.AllowedHosts"/> takes it.</summary>
    [GeneratedRegex(@"\A(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*(:\d{1,5})?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}
