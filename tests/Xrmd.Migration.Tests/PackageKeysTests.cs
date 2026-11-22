using FluentAssertions;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Tests;

public class PackageKeysTests
{
    private const string Passphrase = "correct horse battery";
    private static readonly byte[] Salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void Same_passphrase_and_salt_give_same_keys_and_entry_names()
    {
        var a = PackageKeys.Derive(Passphrase, Salt);
        var b = PackageKeys.Derive(Passphrase, Salt);
        a.EncryptionKey.Should().Equal(b.EncryptionKey);
        a.EntryName("data/account/00001.jsonl.gz").Should().Be(b.EntryName("data/account/00001.jsonl.gz"));
        a.KeyCheck().Should().Equal(b.KeyCheck());
    }

    [Fact]
    public void Entry_name_is_opaque()
    {
        var name = PackageKeys.Derive(Passphrase, Salt).EntryName("data/account/00001.jsonl.gz");
        name.Should().MatchRegex("^e/[0-9a-f]{32}$");
        name.Should().NotContain("account");
    }

    [Fact]
    public void Different_salt_changes_keys()
    {
        var other = Salt.Select(b => (byte)(b + 1)).ToArray();
        PackageKeys.Derive(Passphrase, Salt).EncryptionKey.Should().NotEqual(PackageKeys.Derive(Passphrase, other).EncryptionKey);
    }

    [Fact]
    public void Short_passphrase_is_refused()
    {
        var act = () => PackageKeys.Derive("elevenchars", Salt);
        act.Should().Throw<PackagePassphraseException>().WithMessage("*12*");
    }
}

public class PackageKeysDerivationTests
{
    private static readonly byte[] Salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void Keys_are_one_pbkdf2_block_expanded_with_hkdf()
    {
        var prk = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes("correct horse battery"), Salt, 600_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var expected = System.Security.Cryptography.HKDF.Expand(System.Security.Cryptography.HashAlgorithmName.SHA256, prk, 32, "xrmpkg enc v1"u8.ToArray());
        Xrmd.Migration.Packaging.PackageKeys.Derive("correct horse battery", Salt).EncryptionKey.Should().Equal(expected);
    }

    [Fact]
    public void Composed_and_decomposed_passphrases_give_the_same_keys()
    {
        var composed = "caffè città perché".Normalize(System.Text.NormalizationForm.FormC);
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        decomposed.Should().NotBe(composed);
        Xrmd.Migration.Packaging.PackageKeys.Derive(decomposed, Salt).KeyCheck()
            .Should().Equal(Xrmd.Migration.Packaging.PackageKeys.Derive(composed, Salt).KeyCheck());
    }
}
