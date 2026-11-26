using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xrmd.Migration.Packaging;

/// <summary>
/// <c>header.json</c>, the only clear file of a package: what is needed to derive the keys and to tell a wrong passphrase.
/// </summary>
public sealed record PackageHeader(string Format, int FormatVersion, string Kdf, int Iterations, string Salt, string Cipher, int SegmentSize, string KeyCheck)
{
    public const string FileName = "header.json";
    public const string FormatName = "xrmpkg";
    public const int CurrentVersion = 1;
    /// <summary>The format versions this xrmd reads: 1, and 2 (a package that carries maps and a source catalog).</summary>
    public static readonly IReadOnlyList<int> SupportedVersions = [1, 2];
    /// <summary>The format version of a package that carries maps.</summary>
    public const int MappedVersion = 2;
    public const string KdfName = "PBKDF2-HMAC-SHA256+HKDF-SHA256";
    public const string CipherName = "AES-256-GCM";

    internal static PackageHeader For(byte[] salt, PackageKeys keys, int formatVersion) =>
        new(FormatName, formatVersion, KdfName, PackageKeys.Iterations, Convert.ToBase64String(salt), CipherName,
            EntryCipher.SegmentSize, Convert.ToBase64String(keys.KeyCheck()));

    internal static PackageHeader Parse(byte[] json)
    {
        PackageHeader? header;
        try { header = JsonSerializer.Deserialize<PackageHeader>(json, PackageJson.Options); }
        catch (JsonException e) { throw new PackageFormatException($"header.json is not valid: {e.Message}"); }
        if (header is null || header.Format != FormatName) throw new PackageFormatException("This file is not an xrmverse migration package.");
        if (!SupportedVersions.Contains(header.FormatVersion)) throw UnsupportedVersion(header.FormatVersion);
        if (header.Kdf != KdfName || header.Cipher != CipherName || header.SegmentSize != EntryCipher.SegmentSize || header.Iterations != PackageKeys.Iterations)
            throw new PackageFormatException("The package uses a key derivation or cipher setting this xrmd does not support.");
        return header;
    }

    internal static PackageFormatException UnsupportedVersion(int version) =>
        new($"Package format version {version} is not supported (this xrmd reads versions {string.Join(", ", SupportedVersions)}).");

    /// <summary>Derives the keys and checks the passphrase against <see cref="KeyCheck"/>.</summary>
    /// <exception cref="PackagePassphraseException">The passphrase is wrong for this package.</exception>
    internal PackageKeys Open(string passphrase)
    {
        var keys = PackageKeys.Derive(passphrase, Convert.FromBase64String(Salt));
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(keys.KeyCheck(), Convert.FromBase64String(KeyCheck)))
            throw new PackagePassphraseException("The passphrase is wrong for this package.");
        return keys;
    }
}

internal static class PackageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
