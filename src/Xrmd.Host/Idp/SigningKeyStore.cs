using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Host.Idp;

/// <summary>
/// The organization's key ring (plan 2.8, L4): one RSA 2048 signing certificate and one encryption certificate, generated at
/// the first start in Local mode and kept as PFX bytes in <c>auth.signing_key</c>. A transaction application lock (the dialect's:
/// <c>sp_getapplock</c> on SQL Server, an advisory lock on PostgreSQL) makes concurrent first starts create a single pair.
/// </summary>
public sealed class SigningKeyStore(IDbConnectionFactory factory)
{
    private const string Subject = "CN=xrmverse local IdP";
    private static readonly TimeSpan Validity = TimeSpan.FromDays(5 * 365);

    // The PFX is protected by nothing: the organization database is already the trust boundary (L4).
    private const string PfxPassword = "";

    public async Task<(X509Certificate2 Signing, X509Certificate2 Encryption)> GetOrCreateAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = new DataSession(conn, tx);
        try
        {
            await session.Dialect.AcquireAppLockAsync(session, "xrmd_idp_keys", session: false, TimeSpan.FromMilliseconds(30000), ct);
        }
        catch (AppLockException ex)
        {
            throw new InvalidOperationException("Could not take the key ring lock (" + ex.Detail + ").", ex);
        }

        var d = session.Dialect;
        var pfx = await ReadAsync(conn, tx, d, ct);
        foreach (var use in new[] { "sig", "enc" })
        {
            if (pfx.ContainsKey(use)) continue;
            var bytes = CreateCertificate();
            await using var insert = conn.Command(
                $"INSERT INTO auth.signing_key ({d.Quote("Id")}, {d.Quote("Use")}, {d.Quote("Pfx")}, {d.Quote("CreatedOnUtc")}) VALUES (@id, @use, @pfx, {d.UtcNow})", tx);
            insert.AddParameter("@id", DbType.Guid, Guid.NewGuid());
            insert.AddParameter("@use", DbType.AnsiString, use, 3);
            insert.AddParameter("@pfx", DbType.Binary, bytes, -1);
            await insert.ExecuteNonQueryAsync(ct);
            pfx[use] = bytes;
        }

        await tx.CommitAsync(ct);
        return (Load(pfx["sig"]), Load(pfx["enc"]));
    }

    private static async Task<Dictionary<string, byte[]>> ReadAsync(DbConnection conn, DbTransaction tx, ISqlDialect d, CancellationToken ct)
    {
        var rows = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var cmd = conn.Command($"SELECT {d.Quote("Use")}, {d.Quote("Pfx")} FROM auth.signing_key", tx);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) rows[rd.GetString(0)] = (byte[])rd.GetValue(1);
        return rows;
    }

    // macOS does not support EphemeralKeySet (PlatformNotSupportedException): there the key is loaded with the default
    // storage, which is only reached when the host runs directly on a Mac (development, E2E); the image runs on Linux.
    private static readonly X509KeyStorageFlags LoadFlags = OperatingSystem.IsMacOS()
        ? X509KeyStorageFlags.Exportable
        : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

    private static X509Certificate2 Load(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, PfxPassword, LoadFlags);

    private static byte[] CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Validity));
        return cert.Export(X509ContentType.Pfx, PfxPassword);
    }
}
