using System.Globalization;
using System.Text;

namespace Xrmd.Cli.Server;

/// <summary>
/// Renders the <c>Caddyfile</c> of the <c>proxy</c> service (<see cref="ProxyMode.Caddy"/>, rulings H3, H5, H8) from an
/// <see cref="Installation"/>: <c>Templates/Caddyfile</c>, then one <c>Templates/caddy-site</c> block per organization with a
/// public host, which proxies to <c>xrmd-&lt;org&gt;:5000</c> on the <c>edge</c> network.
/// </summary>
/// <remarks>
/// Sections (see <see cref="ComposeRenderer"/>): <c>ACME</c> (the global <c>email</c> of <c>TLS_MODE=acme</c>), and per site
/// <c>FILES</c> (<c>tls</c> with the pair mounted at <c>/certs</c>) or <c>INTERNAL</c> (<c>tls internal</c>). With
/// <c>acme</c> Caddy's automatic HTTPS gets the certificates; in every mode it redirects port 80 to 443.
/// </remarks>
public static class CaddyfileRenderer
{
    /// <summary>
    /// The request body limit of every site, in bytes: the server's limit for a solution import or a file upload,
    /// <c>Xrmd.WebApi.SolutionFileSizeLimit.MaxBytes</c> (128 MiB; this project does not reference the server, a test
    /// compares the two). Written in bytes because Caddy reads <c>128MB</c> as 128,000,000.
    /// </summary>
    public const long RequestBodyMaxBytes = 128L * 1024 * 1024;

    /// <summary>Where the proxy container reads the certificate pair of <c>TLS_MODE=files</c> (<c>./certs</c>, read-only).</summary>
    private static readonly string[] CertificateFiles = ["server.crt", "server.key"];

    private const string SitesMarker = "#{{SITES}}";

    /// <summary>
    /// The Caddyfile text. <paramref name="upstream"/> replaces the default <c>xrmd-&lt;org&gt;:5000</c> (for a proxy outside the
    /// compose network, such as the acceptance test's). Throws <see cref="InvalidOperationException"/> unless the installation
    /// has the Caddy proxy.
    /// </summary>
    public static string Render(Installation installation, Func<OrgEntry, string>? upstream = null)
    {
        if (installation.Proxy != ProxyMode.Caddy)
            throw new InvalidOperationException($"The installation in {installation.Directory} has no Caddy proxy (no public host, or {Installation.ProxyModeKey}={Installation.ExternalProxyMode}).");
        var tls = installation.TlsMode;
        if (tls is null || !Installation.TlsModes.Contains(tls))
            throw new InvalidOperationException($"{Installation.TlsModeKey} must be one of {string.Join(", ", Installation.TlsModes)} with the Caddy proxy: '{tls}'.");

        var headSections = new HashSet<string>();
        var email = "";
        if (tls == "acme")
        {
            email = installation.AcmeEmail ?? throw new InvalidOperationException($"{Installation.TlsModeKey}=acme needs {Installation.AcmeEmailKey}.");
            if (Installation.ValidateAcmeEmail(email) is { } problem) throw new InvalidOperationException(problem); // pasted verbatim
            headSections.Add("ACME");
        }

        var siteTemplate = ComposeRenderer.ReadTemplate("caddy-site");
        var siteSections = tls switch
        {
            "files" => new HashSet<string> { "FILES" },
            "internal" => new HashSet<string> { "INTERNAL" },
            _ => new HashSet<string>(),
        };
        var sites = new StringBuilder();
        foreach (var org in installation.Orgs.Where(o => o.Host is not null))
        {
            if (Installation.ValidateHost(org.Host!) is { } problem) throw new InvalidOperationException(problem); // pasted verbatim
            Installation.ValidateOrgName(org.Name);
            var target = upstream?.Invoke(org) ?? $"xrmd-{org.Name}:5000";
            if (target.Length == 0 || target.Any(c => char.IsWhiteSpace(c) || c is '{' or '}' or '"' or '#'))
                throw new InvalidOperationException($"Invalid upstream for '{org.Name}': '{target}'.");
            sites.Append(ComposeRenderer.Sections(siteTemplate, siteSections)
                .Replace("{{HOST}}", org.Host, StringComparison.Ordinal)
                .Replace("{{UPSTREAM}}", target, StringComparison.Ordinal)
                .Replace("{{BODY_LIMIT}}", RequestBodyMaxBytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{{BODY_LIMIT_TEXT}}", $"{(RequestBodyMaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB", StringComparison.Ordinal));
        }

        var head = ComposeRenderer.Sections(ComposeRenderer.ReadTemplate("Caddyfile"), headSections)
            .Replace("{{ACME_EMAIL}}", email, StringComparison.Ordinal);
        if (!head.Contains(SitesMarker + "\n", StringComparison.Ordinal))
            throw new InvalidOperationException($"The Caddyfile template has no {SitesMarker} line.");
        var text = head.Replace(SitesMarker + "\n", sites.ToString(), StringComparison.Ordinal);
        if (text.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException("The rendered Caddyfile still holds a template placeholder.");
        return text;
    }

    /// <summary>
    /// The certificate files of <c>TLS_MODE=files</c> that are missing from <see cref="Installation.CertsPath"/>, as paths
    /// relative to the installation (<c>certs/server.crt</c>); empty in every other mode (H5, Review Focus 6).
    /// </summary>
    public static IReadOnlyList<string> MissingCertificates(Installation installation)
    {
        if (installation.Proxy != ProxyMode.Caddy || installation.TlsMode != "files") return [];
        return CertificateFiles
            .Where(name => !File.Exists(Path.Combine(installation.CertsPath, name)))
            .Select(name => "certs/" + name)
            .ToList();
    }
}
