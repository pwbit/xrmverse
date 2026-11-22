using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Commands;

/// <summary>
/// The SQL (TDS) endpoint of the installation's organizations (plan 3.3, rulings T2, T5, T12): what <c>init</c> and <c>org add</c>
/// print, and what <c>status</c> and <c>doctor</c> read of the certificates <c>tds-certs</c> copies into <c>tds/&lt;org&gt;/</c>.
/// </summary>
public static partial class ServerCommands
{
    /// <summary>The compose service that copies the SQL endpoint certificates (ruling T12).</summary>
    internal const string TdsCertsService = "tds-certs";

    /// <summary>How <paramref name="orgs"/> are reached over SQL, one line each, and where the certificate comes from.</summary>
    private static IEnumerable<string> TdsLines(Installation installation, IEnumerable<OrgEntry> orgs)
    {
        var withTds = orgs.Where(o => o.TdsServer is not null).ToList();
        if (withTds.Count == 0) yield break;
        yield return "SQL endpoints (read-only; SQL logins of the local identity provider, TLS required):";
        foreach (var org in withTds)
            yield return $"  {org.Name}: {org.TdsServer} (host port {org.TdsPort}; open it in your firewall)";
        yield return $"  {TdsCertsService} copies each certificate from {TdsSource(installation)} into {installation.TdsPath}/<org>/; " +
                     "an organization's endpoint starts once its certificate is there.";
    }

    /// <summary>Where <c>tds-certs</c> takes the pairs from, for messages.</summary>
    private static string TdsSource(Installation installation) => installation.TdsPairSource switch
    {
        "" => $"the proxy's certificate storage (every {Every(ComposeRenderer.TdsCopyInterval(installation))})",
        var name => $"certs/{name}.crt and certs/{name}.key (every {Every(ComposeRenderer.TdsCopyInterval(installation))})",
    };

    private static string Every(int seconds) => seconds switch
    {
        >= 3600 when seconds % 3600 == 0 => seconds == 3600 ? "hour" : $"{seconds / 3600} hours",
        _ => $"{seconds / 60} minutes",
    };

    /// <summary>What renews the SQL endpoint certificate, for <c>doctor</c>'s <c>tds-certificate</c> check.</summary>
    internal static string TdsRenewalHint(Installation installation)
    {
        var copy = $"{TdsCertsService} copies it within {Every(ComposeRenderer.TdsCopyInterval(installation))}.";
        return installation.TdsPairSource switch
        {
            Installation.TdsPairName => $"Replace certs/{Installation.TdsPairName}.crt and certs/{Installation.TdsPairName}.key; {copy}",
            Installation.FilesPairName => $"Replace certs/{Installation.FilesPairName}.crt and certs/{Installation.FilesPairName}.key (see the proxy's certificate check); {copy}",
            _ => $"Caddy renews it on its own; {copy} If it does not, see `xrmd server logs`.",
        };
    }

    /// <summary>Exit message when the pair of the SQL endpoints behind the customer's proxy is missing (ruling T12).</summary>
    private static string TdsPairMissingMessage(Installation installation, IReadOnlyList<string> missing) =>
        $"{string.Join(" and ", missing)} not found in {installation.Directory}: behind your own proxy the SQL endpoints serve the PEM pair " +
        $"{Path.Combine("certs", Installation.TdsPairName + ".crt")} and {Path.Combine("certs", Installation.TdsPairName + ".key")} (one certificate " +
        "covering every host, the key unencrypted). Copy them there (mode 600) first.";

    /// <summary>The expiry of the certificate <c>tds-certs</c> copied for <paramref name="org"/>; null when there is none (or it cannot be read).</summary>
    private static DateTime? TdsCertificateExpiry(Installation installation, OrgEntry org)
    {
        if (installation.TdsCertificatePath(org) is not { } path) return null;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            return certificate.NotAfter.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static string Day(DateTime date) => date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>doctor</c>'s <c>tds-certificate</c> check of an organization with a SQL endpoint, on the copy in <c>tds/&lt;org&gt;/</c>:
    /// missing, unreadable or expired = error (the endpoint does not start, or serves an expired certificate), expiring within
    /// 14 days = warning (not with Caddy's internal CA, whose leaves live 12 hours and are renewed by Caddy). None without an endpoint.
    /// </summary>
    private static List<HostCheck> TdsChecks(CliContext ctx, Installation installation, OrgEntry org)
    {
        const string name = "tds-certificate";
        if (org.TdsPort is not { } port || installation.TdsCertificatePath(org) is not { } path) return [];
        if (!File.Exists(path))
            return [new HostCheck(name, "error",
                $"{path} not found: the SQL endpoint on port {port} does not start without it. {TdsCertsService} copies it from " +
                $"{TdsSource(installation)}; see `xrmd server logs --dir {installation.Directory}`.")];
        DateTime notAfter;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            notAfter = certificate.NotAfter.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return [new HostCheck(name, "error", $"{path} cannot be read as a PEM certificate: {ex.Message}")];
        }
        var left = new DateTimeOffset(notAfter, TimeSpan.Zero) - ctx.Env.Clock.GetUtcNow();
        if (left <= TimeSpan.Zero)
            return [new HostCheck(name, "error", $"The SQL endpoint certificate {path} (port {port}) expired on {Day(notAfter)}. {TdsRenewalHint(installation)}")];
        var days = (int)Math.Floor(left.TotalDays);
        if (!installation.UsesInternalCa && left < CertificateWarningWindow)
            return [new HostCheck(name, "warning",
                $"The SQL endpoint certificate {path} (port {port}) expires in {days} days, on {Day(notAfter)}. {TdsRenewalHint(installation)}")];
        return [new HostCheck(name, "ok", $"Port {port}: valid until {Day(notAfter)} (expires in {days} days).")];
    }

    /// <summary>The <c>status</c> cell of the SQL endpoint certificate: its expiry day, <c>missing</c>, or empty without an endpoint.</summary>
    private static string? TdsCertificateCell(Installation installation, OrgEntry org) =>
        org.TdsPort is null ? null : TdsCertificateExpiry(installation, org) is { } expiry ? Day(expiry) : "missing";
}
