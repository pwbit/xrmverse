using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Xrmd.Cli.Http;

/// <summary>
/// H7, Review Focus 7: trust a private root (Caddy's internal CA) for one host name only, without touching the machine's
/// trust store. Every other host keeps the default validation; for the host itself only the chain is re-evaluated against
/// the root, so the name check and the validity period still apply.
/// </summary>
public static class ScopedTrust
{
    /// <summary>
    /// A handler whose server certificate validation accepts a chain built on <paramref name="root"/> (custom root trust)
    /// only when the request's host equals <paramref name="host"/> (case-insensitive).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(string host, X509Certificate2 root) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                Validate(host, root, TargetHost(sender), certificate, chain, errors),
        },
    };

    /// <summary>Loads a PEM root certificate file (Caddy's <c>root.crt</c>): its first <c>CERTIFICATE</c> block.</summary>
    public static X509Certificate2 LoadRoot(string path)
    {
        var text = File.ReadAllText(path);
        var rest = text.AsSpan();
        while (PemEncoding.TryFind(rest, out var fields))
        {
            if (rest[fields.Label].SequenceEqual("CERTIFICATE"))
                return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(rest[fields.Base64Data].ToString()));
            rest = rest[fields.Location.End..];
        }
        throw new CryptographicException($"{path} holds no PEM certificate (-----BEGIN CERTIFICATE-----).");
    }

    /// <summary>The host name the connection is for: the TLS target host, or the request's host when the sender is the request.</summary>
    private static string? TargetHost(object sender) => sender switch
    {
        SslStream ssl => ssl.TargetHostName,
        HttpRequestMessage request => request.RequestUri?.IdnHost,
        _ => null,
    };

    /// <summary>The OID of the server authentication extended key usage.</summary>
    internal const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// The policy shared with <c>server doctor</c>: a server certificate (server authentication usage), no revocation check
    /// (Caddy's local CA publishes no revocation list) and no certificate downloads (the served chain is what counts).
    /// </summary>
    internal static void Configure(X509ChainPolicy policy)
    {
        policy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.DisableCertificateDownloads = true;
    }

    /// <summary>
    /// The certificates the server sent besides <paramref name="leaf"/> (its intermediates), from the handshake's chain: its
    /// extra store and its elements. Copies, owned by the caller's chain policy.
    /// </summary>
    internal static IEnumerable<X509Certificate2> Served(X509Chain? chain, X509Certificate2 leaf)
    {
        if (chain is null) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal) { leaf.Thumbprint };
        foreach (var certificate in chain.ChainPolicy.ExtraStore.Concat(chain.ChainElements.Select(e => e.Certificate)))
            if (seen.Add(certificate.Thumbprint)) yield return new X509Certificate2(certificate);
    }

    /// <summary>The decision of <see cref="CreateHandler"/>'s callback.</summary>
    internal static bool Validate(string host, X509Certificate2 root, string? targetHost, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true; // trusted by the machine: nothing to add
        // Another host, a name the certificate does not cover, or no certificate: the default verdict stands.
        if (targetHost is null || !string.Equals(targetHost, host, StringComparison.OrdinalIgnoreCase)) return false;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null) return false;

        using var leaf = new X509Certificate2(certificate);
        using var custom = new X509Chain();
        Configure(custom.ChainPolicy);
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.Add(root);
        custom.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag; // the validity period of every certificate is checked
        var served = Served(chain, leaf).ToArray(); // copies of the intermediate Caddy serves with the leaf, owned here
        try
        {
            custom.ChainPolicy.ExtraStore.AddRange(served);
            return custom.Build(leaf);
        }
        finally
        {
            foreach (var copy in served) copy.Dispose();
        }
    }
}
