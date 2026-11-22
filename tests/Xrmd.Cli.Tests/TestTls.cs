using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Xrmd.Cli.Tests;

/// <summary>A private root CA and leaf certificates it signs, generated in memory for TLS tests.</summary>
internal static class TestCertificates
{
    /// <summary>A self-signed CA root valid from <paramref name="now"/> − 1 year to + 10 years, with its private key.</summary>
    public static X509Certificate2 CreateRoot(DateTimeOffset now, string name = "xrmd test root", DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var root = request.CreateSelfSigned(now.AddYears(-1), notAfter ?? now.AddYears(10));
        return Reimport(root);
    }

    /// <summary>
    /// An intermediate CA signed by <paramref name="root"/>, valid from <paramref name="notBefore"/> to <paramref name="notAfter"/>
    /// (Caddy's local CA is root → intermediate → leaf, and serves the leaf with the intermediate).
    /// </summary>
    public static X509Certificate2 CreateIntermediate(X509Certificate2 root, DateTimeOffset notBefore, DateTimeOffset notAfter,
        string name = "xrmd test intermediate")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7f;
        using var signed = request.Create(root, notBefore, notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        return Reimport(withKey);
    }

    /// <summary>A server certificate for <paramref name="dnsNames"/> (an IP address goes in as such) signed by <paramref name="root"/>.</summary>
    public static X509Certificate2 CreateLeaf(X509Certificate2 root, DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] dnsNames) =>
        CreateLeaf(root, notBefore, notAfter, ["1.3.6.1.5.5.7.3.1"], dnsNames);

    /// <summary>As above, with the given extended key usages (OIDs).</summary>
    public static X509Certificate2 CreateLeaf(X509Certificate2 root, DateTimeOffset notBefore, DateTimeOffset notAfter, string[] usages,
        params string[] dnsNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsNames[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(Oids(usages), false));
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7f;
        // Signed with the issuer's key directly: unlike Create(issuer, …) this allows a leaf that outlives its issuer (an
        // expired intermediate under a leaf that is still in date).
        using var issuerKey = root.GetECDsaPrivateKey()!;
        using var signed = request.Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), notBefore, notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        return Reimport(withKey);
    }

    private static OidCollection Oids(string[] values)
    {
        var oids = new OidCollection();
        foreach (var value in values) oids.Add(new Oid(value));
        return oids;
    }

    /// <summary>The root as a PEM file, the way <c>docker compose cp</c> exports Caddy's <c>root.crt</c>.</summary>
    public static void WritePem(X509Certificate2 certificate, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, certificate.ExportCertificatePem());
    }

    /// <summary>An ephemeral key cannot serve TLS on every platform: round-trip through PFX.</summary>
    private static X509Certificate2 Reimport(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
}

/// <summary>
/// A TLS server on a loopback port that presents <see cref="Certificate"/> and answers every HTTP request with
/// <c>200</c> and <see cref="Body"/>; a handshake the client refuses just ends that connection.
/// </summary>
internal sealed class TlsTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly SslStreamCertificateContext _context;

    public TlsTestServer(X509Certificate2 certificate, string body = "{}", params X509Certificate2[] intermediates)
    {
        // The served chain: the leaf and its intermediates, as Caddy serves them.
        _context = SslStreamCertificateContext.Create(certificate, new X509Certificate2Collection(intermediates), offline: true);
        Certificate = certificate;
        Body = body;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public X509Certificate2 Certificate { get; }
    public string Body { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _context }, _stop.Token);
                var buffer = new byte[8192];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var read = await ssl.ReadAsync(buffer, _stop.Token);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                var body = Encoding.UTF8.GetBytes(Body);
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await ssl.WriteAsync(body, _stop.Token);
                await ssl.FlushAsync(_stop.Token);
            }
            catch (Exception)
            {
                // A refused handshake or a client that went away: nothing to serve.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
        _stop.Dispose();
    }
}
