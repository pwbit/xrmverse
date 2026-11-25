using System.Net;
using System.Net.Sockets;

namespace Xrmd.WebApi.Connector;

/// <summary>
/// X.12b: which callback URLs a flow subscription may register. Messages never quote the URL: it carries a signature.
/// </summary>
public static class CallbackUrlPolicy
{
    private const int MaxLength = 2048;

    /// <summary>Null when <paramref name="url"/> is accepted, else the 400 message.</summary>
    public static string? Problem(string? url, ConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (url is { Length: > MaxLength })
            return $"The callback URL must not be longer than {MaxLength} characters.";
        // On Unix "/path" parses as an absolute file URI, so the scheme is checked too.
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile || string.IsNullOrEmpty(uri.Host))
            return "The callback URL must be an absolute URL.";
        var priv = options.AllowPrivateCallbacks;
        if (uri.Scheme != Uri.UriSchemeHttps && !(priv && uri.Scheme == Uri.UriSchemeHttp))
            return "The callback URL must use https.";
        if (!priv && uri.Port != 443)
            return "The callback URL must use port 443.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "The callback URL must not contain user information.";

        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out _) && !priv) return "The callback URL address is not allowed.";
        return Allowed(host, options) ? null : "The callback URL host is not allowed.";
    }

    private static bool Allowed(string host, ConnectorOptions options)
    {
        foreach (var pattern in options.EffectiveCallbackHosts)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..]; // ".example.com"
                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True for an address a callback must not reach. IPv4: "this network" 0.0.0.0/8, loopback 127/8, RFC 1918 (10/8, 172.16/12,
    /// 192.168/16), shared address space 100.64/10, link-local 169.254/16, multicast 224/4 and reserved 240/4 (255.255.255.255
    /// included). IPv6: unspecified, loopback, unique-local fc00::/7, link-local fe80::/10, site-local fec0::/10 and multicast
    /// ff00::/8. The IPv6 forms that carry an IPv4 address (IPv4-mapped ::ffff:0:0/96, NAT64 64:ff9b::/96, 6to4 2002::/16) are judged
    /// by the IPv4 address they carry.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsPrivateV4(b);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)) return true;
        // NAT64 (64:ff9b::/96): the last four bytes are the IPv4 address.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return IsPrivateV4(b.AsSpan(12, 4));
        // 6to4 (2002::/16): bytes 2 to 5 are the IPv4 address.
        if (b[0] == 0x20 && b[1] == 0x02) return IsPrivateV4(b.AsSpan(2, 4));
        return (b[0] & 0xFE) == 0xFC // fc00::/7
               || (b[0] == 0xFE && (b[1] & 0x80) == 0x80) // fe80::/10 and fec0::/10
               || b[0] == 0xFF; // ff00::/8
    }

    private static bool IsPrivateV4(ReadOnlySpan<byte> b) =>
        b[0] is 0 or 10 or 127 or >= 224
        || (b[0] == 100 && (b[1] & 0xC0) == 64)
        || (b[0] == 172 && b[1] is >= 16 and <= 31)
        || (b[0] == 192 && b[1] == 168)
        || (b[0] == 169 && b[1] == 254);
}
