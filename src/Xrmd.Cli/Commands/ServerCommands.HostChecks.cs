using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Commands;

/// <summary>The client-side checks of <c>server doctor</c> for an organization with a public host (H7, H9).</summary>
public static partial class ServerCommands
{
    /// <summary>A certificate that expires sooner than this is a warning (H9).</summary>
    internal static readonly TimeSpan CertificateWarningWindow = TimeSpan.FromDays(14);

    private const int HttpsPort = 443;

    /// <summary>One check of <c>server doctor</c>, shaped as the server's (<c>name</c>, <c>status</c>, <c>detail</c>).</summary>
    private sealed record HostCheck(string Name, string Status, string Detail);

    /// <summary>The server's checks followed by the client-side ones; null when there are neither.</summary>
    private static JsonElement? CombineChecks(JsonElement? serverChecks, List<HostCheck> hostChecks)
    {
        if (hostChecks.Count == 0) return serverChecks;
        var all = new List<object>();
        if (serverChecks is { } server) all.AddRange(server.EnumerateArray().Select(c => (object)c));
        all.AddRange(hostChecks);
        return JsonSerializer.SerializeToElement(all, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>
    /// H9 for an organization with a public host: <c>dns</c> (the host resolves on this machine, which H7 requires of the CLI)
    /// and, when it does, <c>certificate</c> (what is served on <c>&lt;host&gt;:443</c>). None without a host.
    /// </summary>
    private static async Task<List<HostCheck>> HostChecksAsync(CliContext ctx, Installation installation, OrgEntry org, CancellationToken ct)
    {
        if (org.Host is not { } host) return [];
        IPAddress[] addresses;
        string? reason = null;
        try
        {
            addresses = await WithProbeTimeout(ProbeTimeout(ctx), ct, token => ctx.Env.ResolveHost(host, token));
        }
        catch (Exception ex) when (ex is SocketException or CliException or ArgumentException)
        {
            addresses = [];
            reason = ex.Message;
        }
        if (addresses.Length == 0)
            return [new HostCheck("dns", "error",
                $"{host} does not resolve on this machine{(reason is null ? "" : $" ({reason})")}; the CLI and the organization's users reach " +
                $"{org.PublicUrl} by that name. Create a DNS record for {host} pointing at " +
                (installation.Proxy == ProxyMode.External ? "your proxy" : "this server's public address") +
                $", or, on a test machine, add '<server address> {host}' to the hosts file (/etc/hosts; C:\\Windows\\System32\\drivers\\etc\\hosts on Windows).")];

        return
        [
            new HostCheck("dns", "ok", $"{host} resolves to {string.Join(", ", addresses.Select(a => a.ToString()))}."),
            await CertificateCheckAsync(ctx, installation, host, addresses, ct),
        ];
    }

    /// <summary>
    /// H9: a TLS handshake with <paramref name="host"/> on port 443 (whatever serves it: Caddy, or the customer's proxy in
    /// <c>external</c> mode), then the served certificate is judged: not covering the host, expired or not trusted = error
    /// (in <c>internal</c> mode trusted means issued by <c>certs/caddy-root.crt</c>), expiring within 14 days = warning (in
    /// <c>internal</c> mode: the CA root, since Caddy renews its short-lived leaves itself).
    /// </summary>
    private static async Task<HostCheck> CertificateCheckAsync(CliContext ctx, Installation installation, string host, IPAddress[] addresses,
        CancellationToken ct)
    {
        var port = ctx.Env.TlsCheckPort ?? HttpsPort;
        X509Certificate2? leaf = null;
        var intermediates = new List<X509Certificate2>();
        var errors = SslPolicyErrors.None;
        try
        {
            await WithProbeTimeout(ProbeTimeout(ctx), ct, async token =>
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(addresses, port, token);
                await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    // The handshake only reads the certificate; it is judged below, and no request is sent.
                    RemoteCertificateValidationCallback = (_, certificate, chain, policyErrors) =>
                    {
                        if (certificate is not null)
                        {
                            leaf = new X509Certificate2(certificate);
                            intermediates.AddRange(Http.ScopedTrust.Served(chain, leaf)); // the chain the server sent
                        }
                        errors = policyErrors;
                        return true;
                    },
                }, token);
                return true;
            });
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or CliException)
        {
            leaf?.Dispose();
            intermediates.ForEach(c => c.Dispose());
            return new HostCheck("certificate", "error", $"Cannot read the certificate served on {host}:{port}: {ex.Message}");
        }
        if (leaf is null) return new HostCheck("certificate", "error", $"{host}:{port} served no certificate.");

        try
        {
            return JudgeCertificate(ctx, installation, host, port, leaf, intermediates, errors);
        }
        finally
        {
            leaf.Dispose();
            intermediates.ForEach(c => c.Dispose());
        }
    }

    private static HostCheck JudgeCertificate(CliContext ctx, Installation installation, string host, int port, X509Certificate2 leaf,
        List<X509Certificate2> intermediates, SslPolicyErrors errors)
    {
        static string Day(DateTime date) => date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = ctx.Env.Clock.GetUtcNow();
        var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var served = $"The certificate served on {host}:{port}";

        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
            return new HostCheck("certificate", "error",
                $"{served} does not cover {host}: it names {(names.Count == 0 ? leaf.Subject : string.Join(", ", names))}.");
        }
        if (notAfter <= now)
            return new HostCheck("certificate", "error", $"{served} expired on {Day(leaf.NotAfter)}.");
        if (TrustProblem(installation, leaf, intermediates, now) is { } problem)
            return new HostCheck("certificate", "error", $"{served} {problem}");

        if (installation.UsesInternalCa)
        {
            // Caddy's local CA issues short-lived leaves (12 hours) and renews them itself: H9's window applies to what no
            // one renews automatically, the CA root exported to caddy-root.crt.
            using var root = Http.ScopedTrust.LoadRoot(installation.CaddyRootPath);
            var rootLeft = new DateTimeOffset(root.NotAfter.ToUniversalTime(), TimeSpan.Zero) - now;
            var rootDays = (int)Math.Floor(rootLeft.TotalDays);
            if (rootLeft < CertificateWarningWindow)
                return new HostCheck("certificate", "warning",
                    $"The root of Caddy's internal CA ({installation.CaddyRootPath}) expires in {rootDays} days, on {Day(root.NotAfter)}; " +
                    "the certificates it issues stop being trusted then. Re-create the proxy's CA (its caddy-data volume), then run " +
                    $"`xrmd server up --dir {installation.Directory}` to export the new root.");
            return new HostCheck("certificate", "ok",
                $"Issued by Caddy's internal CA, valid until {notAfter.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC " +
                $"(Caddy renews it); root valid until {Day(root.NotAfter)}.");
        }

        var left = notAfter - now;
        var days = (int)Math.Floor(left.TotalDays);
        if (left < CertificateWarningWindow)
            return new HostCheck("certificate", "warning",
                $"{served} expires in {days} days, on {Day(leaf.NotAfter)}. {RenewalHint(installation)}");
        return new HostCheck("certificate", "ok", $"Valid until {Day(leaf.NotAfter)} (expires in {days} days), issued by {leaf.Issuer}.");
    }

    /// <summary>
    /// Null when the certificate chains to a trusted root at <paramref name="now"/>: in <c>internal</c> mode Caddy's exported root
    /// only, otherwise the machine's trust store (the customer's proxy in <c>external</c> mode), through the intermediates the server
    /// sent. The leaf's validity period is judged apart (H9 thresholds); an issuer out of its validity period is a problem here.
    /// </summary>
    private static string? TrustProblem(Installation installation, X509Certificate2 leaf, List<X509Certificate2> intermediates, DateTimeOffset now)
    {
        using var chain = new X509Chain();
        Http.ScopedTrust.Configure(chain.ChainPolicy); // server usage, no revocation check, no downloads: no network calls
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.ExtraStore.AddRange(intermediates.ToArray());
        X509Certificate2? root = null;
        try
        {
            if (installation.UsesInternalCa)
            {
                try
                {
                    root = Http.ScopedTrust.LoadRoot(installation.CaddyRootPath);
                }
                catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
                {
                    return $"cannot be checked against Caddy's root certificate {installation.CaddyRootPath}: {ex.Message} " +
                           $"`xrmd server up --dir {installation.Directory}` exports it.";
                }
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
            }
            if (chain.Build(leaf))
            {
                // IgnoreNotTimeValid is for the leaf only: an expired (or not yet valid) intermediate or root is an error.
                foreach (var issuer in chain.ChainElements.Skip(1).Select(e => e.Certificate))
                {
                    var notAfter = issuer.NotAfter.ToUniversalTime();
                    var notBefore = issuer.NotBefore.ToUniversalTime();
                    if (notAfter <= now.UtcDateTime)
                        return $"is not trusted: its issuer {issuer.Subject} expired on {notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
                    if (notBefore > now.UtcDateTime)
                        return $"is not trusted: its issuer {issuer.Subject} is not valid before {notBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
                }
                return null;
            }
            var status = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
            return installation.UsesInternalCa
                ? $"is not trusted: it is not issued by Caddy's root certificate {installation.CaddyRootPath} ({status}). If the proxy's CA " +
                  $"changed (its data volume was re-created), run `xrmd server up --dir {installation.Directory}` to export it again."
                : $"is not trusted by this machine ({status}).";
        }
        finally
        {
            root?.Dispose();
        }
    }

    internal static string RenewalHint(Installation installation) => installation.Proxy switch
    {
        ProxyMode.External => "Renew it on your proxy.",
        _ => installation.TlsMode switch
        {
            // `caddy reload` skips an unchanged Caddyfile and `server up` leaves a running proxy as it is: force the reload.
            "files" => $"Replace certs/server.crt and certs/server.key, then in {installation.Directory} run " +
                       "`docker compose exec proxy caddy reload --config /etc/caddy/Caddyfile --force` (or `docker compose restart proxy`)." +
                       (installation.HasTds ? $" {TdsCertsService} copies them to the SQL endpoints within {Every(ComposeRenderer.TdsCopyInterval(installation))}." : ""),
            _ => "Caddy renews it on its own (ACME); if it does not, see `xrmd server logs`.",
        },
    };
}
