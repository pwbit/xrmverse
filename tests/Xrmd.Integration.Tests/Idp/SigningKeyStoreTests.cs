using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Idp;

namespace Xrmd.Integration.Tests.Idp;

[Collection("xrmd")]
public class SigningKeyStoreTests(XrmdFixture fx)
{
    [Fact]
    public async Task Key_ring_is_created_once()
    {
        await using var db = await IdpDatabase.CreateAsync(fx.ConnectionString);
        var store = new SigningKeyStore(db.Factory);

        var first = await store.GetOrCreateAsync(default);
        var second = await store.GetOrCreateAsync(default);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => new SigningKeyStore(db.Factory).GetOrCreateAsync(default))));

        second.Signing.Thumbprint.Should().Be(first.Signing.Thumbprint);
        second.Encryption.Thumbprint.Should().Be(first.Encryption.Thumbprint);
        first.Signing.Thumbprint.Should().NotBe(first.Encryption.Thumbprint);
        concurrent.Should().OnlyContain(k => k.Signing.Thumbprint == first.Signing.Thumbprint && k.Encryption.Thumbprint == first.Encryption.Thumbprint);

        await using var conn = await db.Factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT [Use], COUNT(*) FROM auth.signing_key GROUP BY [Use] ORDER BY [Use]");
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Use, int Count)>();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), Convert.ToInt32(rd.GetValue(1))));
        rows.Should().Equal([("enc", 1), ("sig", 1)]);
    }

    [Fact]
    public async Task Keys_are_rsa_2048_and_differ_per_org()
    {
        await using var a = await IdpDatabase.CreateAsync(fx.ConnectionString);
        await using var b = await IdpDatabase.CreateAsync(fx.ConnectionString);

        var keysA = await new SigningKeyStore(a.Factory).GetOrCreateAsync(default);
        var keysB = await new SigningKeyStore(b.Factory).GetOrCreateAsync(default);

        keysA.Signing.Thumbprint.Should().NotBe(keysB.Signing.Thumbprint);
        keysA.Encryption.Thumbprint.Should().NotBe(keysB.Encryption.Thumbprint);
        foreach (var cert in new[] { keysA.Signing, keysA.Encryption })
        {
            cert.Subject.Should().Be("CN=xrmverse local IdP");
            cert.HasPrivateKey.Should().BeTrue();
            cert.GetRSAPublicKey()!.KeySize.Should().Be(2048);
            (cert.NotAfter - cert.NotBefore).TotalDays.Should().BeGreaterThan(5 * 365 - 2);
        }
    }
}
