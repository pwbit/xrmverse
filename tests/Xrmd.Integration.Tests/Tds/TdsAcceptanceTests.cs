using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Integration.Tests.Tds;

[CollectionDefinition("tds-acceptance")]
public class TdsAcceptanceCollection : ICollectionFixture<TdsAcceptanceFixture>;

/// <summary>
/// Its own SQL Server container and a Local-mode host whose SQL endpoint presents a certificate for <c>localhost</c> issued by a
/// test CA. The CA is trusted in the system the way <see cref="LocalIdpServiceClientFixture"/> trusts its certificate (the Linux
/// system directory, then removed again), so a client connects with <c>Encrypt=True</c> and <em>without</em> TrustServerCertificate.
/// </summary>
public sealed class TdsAcceptanceFixture : IAsyncLifetime
{
    private const string SystemDirectory = "/usr/local/share/ca-certificates";

    private readonly TestDatabase _db = new();
    private X509Certificate2? _ca;
    private X509Store? _trustedIn;
    private string? _systemCertFile;

    public Guid AdminObjectId { get; } = Guid.NewGuid();

    public string ConnectionString => _db.ConnectionString;

    public TdsTestHost Host { get; private set; } = null!;

    /// <summary>The subject of the trusted test CA.</summary>
    public string CaSubject => _ca?.Subject ?? throw new InvalidOperationException("The CA is not created yet.");

    public async Task InitializeAsync()
    {
        try
        {
            await _db.StartAsync();
            SweepStaleCertificates();
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest("CN=xrmd acme test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
            _ca = X509CertificateLoader.LoadCertificate(ca.RawData);
            await TrustAsync(_ca);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var server = request.Create(ca.SubjectName, X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));

            Host = await TdsTestHost.StartAsync(new TdsTestOrg(ConnectionString, AdminObjectId), withCertificate: false);
            await File.WriteAllTextAsync(Host.PrivateKeyPath, key.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(Host.CertificatePath, server.ExportCertificatePem());
            _ = await Host.WaitForListenerAsync(TimeSpan.FromSeconds(30))
                ?? throw new InvalidOperationException("The SQL endpoint did not open with the CA-issued certificate.");
            await TestPublishers.EnsureAsync(ConnectionString);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (Host is not null) await Host.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_trustedIn is not null && _ca is not null) { _trustedIn.Remove(_ca); _trustedIn.Dispose(); _trustedIn = null; }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: could not remove the acceptance test CA from the trust store: {ex.Message}");
            }
            RemoveSystemCertificate();
            _ca?.Dispose();
            _ca = null;
            await _db.DisposeAsync();
        }
    }

    private static void SweepStaleCertificates()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(SystemDirectory)) return;
        var stale = Directory.GetFiles(SystemDirectory, "xrmd-tds-acceptance-*.crt");
        if (stale.Length == 0) return;
        foreach (var file in stale)
        {
            try { File.Delete(file); }
            catch (Exception ex) { Console.Error.WriteLine($"WARNING: could not delete stale test certificate {file}: {ex.Message}"); }
        }
        try { RunAsync("update-ca-certificates").GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine($"WARNING: update-ca-certificates failed after removing stale test certificates: {ex.Message}"); }
    }

    /// <summary>Adds the CA to the machine's trusted roots (else the user's, else the Linux system directory), then checks the chain.</summary>
    private async Task TrustAsync(X509Certificate2 ca)
    {
        var attempts = new List<string>();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadWrite);
                store.Add(ca);
                var trusted = false;
                try { trusted = ChainIsTrusted(ca); }
                finally { if (!trusted) store.Remove(ca); }
                if (!trusted) { attempts.Add($"{location}: added but not trusted"); continue; }
                _trustedIn = new X509Store(StoreName.Root, location);
                _trustedIn.Open(OpenFlags.ReadWrite);
                return;
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or IOException)
            {
                attempts.Add($"{location}: {ex.Message}");
            }
        }
        if (OperatingSystem.IsLinux() && await TrustInSystemDirectoryAsync(ca, attempts)) return;
        throw new InvalidOperationException("The TDS acceptance tests need the test CA in the trust store, and it could not be trusted (" + string.Join("; ", attempts) + ").");
    }

    private async Task<bool> TrustInSystemDirectoryAsync(X509Certificate2 ca, List<string> attempts)
    {
        try
        {
            var file = $"{SystemDirectory}/xrmd-tds-acceptance-{Guid.NewGuid():N}.crt";
            await File.WriteAllTextAsync(file, ca.ExportCertificatePem());
            _systemCertFile = file;
            await RunAsync("update-ca-certificates");
            for (var i = 0; i < 40; i++)
            {
                if (ChainIsTrusted(ca)) return true;
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
            Console.Error.WriteLine($"WARNING: could not delete the acceptance test CA {file} (remove it and run update-ca-certificates): {ex.Message}");
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

    private static bool ChainIsTrusted(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate) && chain.ChainStatus.All(s => s.Status == X509ChainStatusFlags.NoError);
    }
}

/// <summary>
/// Plan 3.3, Task 6: <c>Microsoft.Data.SqlClient</c> configured the way a user would (host and port in <c>Server</c>,
/// <c>Encrypt=True</c>, a certificate trusted through its CA, no <c>TrustServerCertificate</c>) reads Dataverse data over the SQL
/// endpoint. <c>Encrypt=Strict</c> (TDS 8) is not supported: the library speaks TDS 7.x.
/// </summary>
[Collection("tds-acceptance")]
[Trait("Category", "Acceptance")]
public sealed class TdsAcceptanceTests(TdsAcceptanceFixture fx)
{
    [Fact]
    public async Task SqlClient_reads_with_local_sql_login()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var service = TestServices.NewOrganizationService(fx.Host.App.Services, fx.AdminObjectId);
        var ada = await service.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = $"Lovelace-{tag}" });
        var grace = await service.CreateAsync(new Entity("contact") { ["firstname"] = "Grace", ["lastname"] = $"Hopper-{tag}" });
        await service.CreateAsync(new Entity("account")
        {
            ["name"] = $"Acme Trading {tag}",
            ["primarycontactid"] = new EntityReference("contact", ada),
        });
        await service.CreateAsync(new Entity("account")
        {
            ["name"] = $"Acme Freight {tag}",
            ["primarycontactid"] = new EntityReference("contact", grace),
        });
        await service.CreateAsync(new Entity("account") { ["name"] = $"Acme Small {tag}" });
        var closed = await service.CreateAsync(new Entity("account")
        {
            ["name"] = $"Acme Closed {tag}",
            ["primarycontactid"] = new EntityReference("contact", ada),
        });
        await service.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference("account", closed), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        });

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"localhost,{fx.Host.Port}",
            InitialCatalog = "acme",
            UserID = TdsTestHost.AdminUserName,
            Password = TdsTestHost.AdminPassword,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            ConnectTimeout = 15,
            Pooling = false,
        };
        connectionString.TrustServerCertificate.Should().BeFalse("the acceptance test validates the certificate chain");

        await using var connection = new SqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"""
            SELECT a.name, a.statecode, a.createdon, c.lastname, a.accountid
            FROM account a JOIN contact c ON a.primarycontactid = c.contactid
            WHERE a.name LIKE @pattern AND a.statecode = 0
            ORDER BY a.name DESC
            """, connection);
        command.Parameters.AddWithValue("@pattern", $"%{tag}");
        await using var reader = await command.ExecuteReaderAsync();

        reader.GetName(0).Should().Be("name");
        reader.GetFieldType(0).Should().Be(typeof(string));
        reader.GetFieldType(1).Should().Be(typeof(int));
        reader.GetFieldType(2).Should().Be(typeof(DateTime));
        reader.GetFieldType(4).Should().Be(typeof(Guid));
        var rows = new List<(string Name, int State, DateTime Created, string Contact, Guid Id)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetDateTime(2), reader.GetString(3), reader.GetGuid(4)));

        rows.Select(r => (r.Name, r.State, r.Contact)).Should().Equal(
            ($"Acme Trading {tag}", 0, $"Lovelace-{tag}"),
            ($"Acme Freight {tag}", 0, $"Hopper-{tag}"));
        rows.Select(r => r.Name).Should().NotContain($"Acme Closed {tag}", "the deactivated account fails statecode = 0");
        rows.Should().OnlyContain(r => r.Id != Guid.Empty && r.Created > DateTime.UtcNow.AddHours(-1) && r.Created < DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task SqlClient_refuses_a_certificate_from_an_untrusted_issuer()
    {
        await using var other = await TdsTestHost.StartAsync();
        using var presented = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(other.CertificatePath));
        presented.Issuer.Should().NotBe(fx.CaSubject, "the certificate must come from an issuer the client does not trust");

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"localhost,{other.Port}",
            InitialCatalog = "acme",
            UserID = TdsTestHost.AdminUserName,
            Password = TdsTestHost.AdminPassword,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            ConnectTimeout = 15,
            Pooling = false,
        };
        connectionString.TrustServerCertificate.Should().BeFalse();
        await using var connection = new SqlConnection(connectionString.ConnectionString);

        var error = (await FluentActions.Awaiting(() => connection.OpenAsync()).Should().ThrowAsync<SqlException>()).Which;
        // SqlClient reports a failed validation as a pre-login handshake error; the cause is in the inner exceptions.
        var text = string.Join(" | ", Chain(error).Select(e => e.Message));
        text.Should().MatchRegex("UntrustedRoot|failed chain validation");
    }

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException) yield return e;
    }

    [Fact(Skip = "FEDAUTH not supported by TdsServer 2.0.19 (ruling T5)")]
    public async Task SqlClient_reads_with_access_token()
    {
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"localhost,{fx.Host.Port}",
            InitialCatalog = "acme",
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            Pooling = false,
        };
        await using var connection = new SqlConnection(connectionString.ConnectionString) { AccessToken = TdsTestHost.TestToken };
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TOP 1 name FROM account", connection);
        (await command.ExecuteScalarAsync()).Should().NotBeNull();
    }
}
