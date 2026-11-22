using System.Security.Cryptography;
using System.Text;

namespace Xrmd.Migration.Packaging;

/// <summary>
/// The keys of a package, derived from the passphrase (normalized to Unicode NFC, so that the same passphrase typed on
/// any system gives the same keys) and the salt in <c>header.json</c>: one PBKDF2-HMAC-SHA256 block of
/// <see cref="Iterations"/> iterations, expanded with HKDF-SHA256 into an encryption key (info <c>xrmpkg enc v1</c>,
/// AES-256-GCM) and a naming key (info <c>xrmpkg name v1</c>) that names the entries (<see cref="EntryName"/>) and proves
/// the passphrase (<see cref="KeyCheck"/>) without decrypting anything.
/// </summary>
public sealed class PackageKeys
{
    public const int Iterations = 600_000;
    public const int MinPassphraseLength = 12;
    public const int SaltLength = 16;

    private static readonly byte[] KeyCheckLabel = Encoding.UTF8.GetBytes("xrmpkg key check v1");
    private static readonly byte[] EncryptionInfo = Encoding.UTF8.GetBytes("xrmpkg enc v1");
    private static readonly byte[] NamingInfo = Encoding.UTF8.GetBytes("xrmpkg name v1");

    private readonly byte[] _namingKey;

    private PackageKeys(byte[] encryptionKey, byte[] namingKey)
    {
        EncryptionKey = encryptionKey;
        _namingKey = namingKey;
    }

    /// <summary>The AES-256-GCM key of every entry.</summary>
    public byte[] EncryptionKey { get; }

    /// <exception cref="PackagePassphraseException">The passphrase has fewer than <see cref="MinPassphraseLength"/> characters.</exception>
    public static PackageKeys Derive(string passphrase, ReadOnlySpan<byte> salt)
    {
        passphrase = passphrase.Normalize(NormalizationForm.FormC);
        if (passphrase.Length < MinPassphraseLength)
            throw new PackagePassphraseException($"The passphrase must have at least {MinPassphraseLength} characters.");
        // One PBKDF2 block, then HKDF: asking PBKDF2 for 64 bytes would cost a legitimate open two blocks and a guesser one.
        var prk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new PackageKeys(HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, EncryptionInfo), HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, NamingInfo));
    }

    /// <summary>
    /// The opaque name an entry is stored under: <c>e/</c> and the hex of the first 16 bytes of
    /// HMAC-SHA256(naming key, logical path), so that table names do not show in the package.
    /// </summary>
    public string EntryName(string logicalPath) =>
        "e/" + Convert.ToHexStringLower(HMACSHA256.HashData(_namingKey, Encoding.UTF8.GetBytes(logicalPath)).AsSpan(0, 16));

    /// <summary>The value stored in <c>header.json</c> to tell a wrong passphrase before reading any entry.</summary>
    public byte[] KeyCheck() => HMACSHA256.HashData(_namingKey, KeyCheckLabel);
}
