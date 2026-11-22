using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Xrmd.Host;

namespace Xrmd.Integration.Tests;

[CollectionDefinition("serviceclient-local", DisableParallelization = true)]
public class LocalIdpServiceClientCollection : ICollectionFixture<LocalIdpServiceClientFixture>;

/// <summary>
/// Its own SQL Server container + the xrmd host in <c>Auth:Mode=Local</c> on Kestrel at <c>https://127.0.0.1:443</c>. ServiceClient
/// drops the port of the instance URI and validates the TLS chain, so the host listens on 443 with a self-signed certificate that
/// this fixture adds to the trust store (LocalMachine, else CurrentUser) and removes again.
/// </summary>
public sealed class LocalIdpServiceClientFixture : IAsyncLifetime
{
    public const string Host = "127.0.0.1";
    public const string BaseUrl = "https://127.0.0.1/";
    public const string OrgName = "Test Org";

    private readonly TestDatabase _db = new();
    private X509Certificate2? _certificate;
    private X509Store? _trustedIn;
    private string? _systemCertFile;
    private const string SystemDirectory = "/usr/local/share/ca-certificates";

    public WebApplication App { get; private set; } = null!;
    public Guid AdminObjectId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        SweepStaleCertificates();
        _certificate = CreateCertificate();
        try
        {
            await TrustAsync(_certificate);
            await StartHostAsync();
        }
        catch
        {
            await CleanUpAsync();
            throw;
        }
    }

    private async Task StartHostAsync()
    {

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 443, l =>
        {
            l.Protocols = HttpProtocols.Http1AndHttp2;
            l.UseHttps(_certificate!);
        }));
        builder.Configuration["ConnectionStrings:Org"] = _db.ConnectionString;
        builder.Configuration[Xrmd.Abstractions.Storage.StorageSettings.ProviderKey] = TestProvider.Setting(TestProvider.Current); // plan 2.7b
        builder.Configuration["Auth:Mode"] = "Local";
        builder.Configuration["Auth:Audience"] = BaseUrl;
        builder.Configuration["Seed:OrgName"] = OrgName;
        builder.Configuration["Seed:AdminObjectId"] = AdminObjectId.ToString();
        builder.Configuration["Seed:AdminName"] = "Test Admin";
        builder.AddXrmd();

        App = builder.Build();
        await App.UseXrmdAsync();
        try
        {
            await App.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The local IdP acceptance tests need to bind https://127.0.0.1:443 (binding failed: {ex.Message}). " +
                "Run as root / with CAP_NET_BIND_SERVICE and with port 443 free.", ex);
        }
    }

    public string ConnectionString => _db.ConnectionString;

    public async Task DisposeAsync()
    {
        await CleanUpAsync();
    }

    private async Task CleanUpAsync()
    {
        try
        {
            if (App is not null) await App.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_trustedIn is not null && _certificate is not null)
                {
                    using var publicOnly = X509CertificateLoader.LoadCertificate(_certificate.RawData);
                    _trustedIn.Remove(publicOnly);
                    _trustedIn.Dispose();
                    _trustedIn = null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: could not remove the acceptance test certificate from the trust store: {ex.Message}");
            }
            RemoveSystemCertificate();
            _certificate?.Dispose();
            _certificate = null;
            await _db.DisposeAsync();
        }
    }

    /// <summary>Removes certificate files that an earlier run left in the Linux system directory (a killed test process).</summary>
    private static void SweepStaleCertificates()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(SystemDirectory)) return;
        var stale = Directory.GetFiles(SystemDirectory, "xrmd-acceptance-*.crt");
        if (stale.Length == 0) return;
        foreach (var file in stale)
        {
            try { File.Delete(file); }
            catch (Exception ex) { Console.Error.WriteLine($"WARNING: could not delete stale test certificate {file}: {ex.Message}"); }
        }
        try { RunAsync("update-ca-certificates").GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine($"WARNING: update-ca-certificates failed after removing stale test certificates: {ex.Message}"); }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Adds the certificate to the machine's trusted roots (else the user's), then checks that the chain is accepted.</summary>
    private async Task TrustAsync(X509Certificate2 certificate)
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        var attempts = new List<string>();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadWrite);
                store.Add(publicOnly);
                if (!ChainIsTrusted(publicOnly)) { store.Remove(publicOnly); attempts.Add($"{location}: added but not trusted ({string.Join(',', ChainStatus(publicOnly))})"); continue; }
                _trustedIn = new X509Store(StoreName.Root, location); // reopened at clean-up; the certificate stays in the store until then
                _trustedIn.Open(OpenFlags.ReadWrite);
                return;
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or IOException)
            {
                attempts.Add($"{location}: {ex.Message}");
            }
        }
        // .NET on Linux trusts the system directory only (the Unix stores above do not grant trust): add the PEM there.
        if (OperatingSystem.IsLinux() && await TrustInSystemDirectoryAsync(publicOnly, attempts)) return;
        throw new InvalidOperationException(
            "The local IdP acceptance tests need the test certificate in the trust store, and it could not be trusted (" +
            string.Join("; ", attempts) + "). Run elevated (Windows/macOS) or as a user who can write the root store.");
    }

    private async Task<bool> TrustInSystemDirectoryAsync(X509Certificate2 publicOnly, List<string> attempts)
    {
        try
        {
            var file = $"{SystemDirectory}/xrmd-acceptance-{Guid.NewGuid():N}.crt";
            await File.WriteAllTextAsync(file, publicOnly.ExportCertificatePem());
            _systemCertFile = file;
            await RunAsync("update-ca-certificates");
            for (var i = 0; i < 40; i++)
            {
                if (ChainIsTrusted(publicOnly)) return true;
                await Task.Delay(1000);
            }
            attempts.Add("system directory: not trusted after update-ca-certificates");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            attempts.Add("system directory: " + ex.Message);
        }
        RemoveSystemCertificate();
        return false;
    }

    private void RemoveSystemCertificate()
    {
        if (_systemCertFile is null) return;
        var file = _systemCertFile;
        _systemCertFile = null;
        try { File.Delete(file); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: could not delete the acceptance test certificate {file} (remove it and run update-ca-certificates): {ex.Message}");
            return;
        }
        try { RunAsync("update-ca-certificates").GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine($"WARNING: update-ca-certificates failed after deleting {file}: {ex.Message}"); }
    }

    private static async Task RunAsync(string program, params string[] args)
    {
        await SystemTrustStoreLock.Gate.WaitAsync();
        try { await RunCoreAsync(program, args); }
        finally { SystemTrustStoreLock.Gate.Release(); }
    }

    private static async Task RunCoreAsync(string program, string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException($"{program} did not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{program} exited with {process.ExitCode}: {await process.StandardError.ReadToEndAsync()}");
    }

    private static IEnumerable<string> ChainStatus(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate);
        return chain.ChainStatus.Select(s => s.Status.ToString());
    }

    private static bool ChainIsTrusted(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate) && chain.ChainStatus.All(s => s.Status == X509ChainStatusFlags.NoError);
    }
}
