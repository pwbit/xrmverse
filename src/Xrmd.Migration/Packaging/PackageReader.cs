using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xrmd.Migration.Mapping;

namespace Xrmd.Migration.Packaging;

/// <summary>Reads a package, packed (<c>.xrmpkg</c> zip) or as the folder an export writes.</summary>
public sealed class PackageReader : IDisposable
{
    private readonly ZipArchive? _zip;
    private readonly string? _folder;
    private readonly PackageKeys _keys;
    private IPackageTranslator? _translator;
    private PackageManifest _manifest;

    private PackageReader(ZipArchive? zip, string? folder, PackageHeader header, PackageKeys keys)
    {
        _zip = zip;
        _folder = folder;
        Header = header;
        _keys = keys;
        RawManifest = ReadManifest();
        _manifest = RawManifest;
    }

    public PackageHeader Header { get; }

    /// <summary>The manifest as exported; entries, hashes and chunk paths are always the ones of this manifest.</summary>
    public PackageManifest RawManifest { get; }

    /// <summary>The manifest under the target names when a <see cref="Translator"/> is set, else <see cref="RawManifest"/>.</summary>
    public PackageManifest Manifest => _manifest;

    /// <summary>Shows the package under the names of the target; setting it (or null) recomputes <see cref="Manifest"/>.</summary>
    public IPackageTranslator? Translator
    {
        get => _translator;
        set
        {
            _translator = value;
            _manifest = value?.TranslateManifest(RawManifest) ?? RawManifest;
        }
    }

    /// <exception cref="PackageFormatException">Not a package, an unknown format version, or an export that is not complete.</exception>
    /// <exception cref="PackagePassphraseException">The passphrase is wrong for this package.</exception>
    public static PackageReader Open(string path, string passphrase)
    {
        if (Directory.Exists(path))
        {
            var headerFile = Path.Combine(path, PackageHeader.FileName);
            if (!File.Exists(headerFile)) throw new PackageFormatException($"The folder '{path}' holds no package.");
            var header = PackageHeader.Parse(File.ReadAllBytes(headerFile));
            return new PackageReader(null, path, header, header.Open(passphrase));
        }

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new PackageFormatException($"'{path}' is not an xrmverse migration package."); }
        return OpenZip(zip, $"'{path}'", passphrase);
    }

    /// <summary>
    /// Opens a packed package (<c>.xrmpkg</c> zip) from a seekable <paramref name="seekable"/> stream, e.g. a blob of the server's
    /// file store read in place (migration plan M3, R5). The stream is disposed with the reader unless <paramref name="leaveOpen"/>.
    /// </summary>
    /// <exception cref="PackageFormatException">Not a package, an unknown format version, or an export that is not complete.</exception>
    /// <exception cref="PackagePassphraseException">The passphrase is wrong for this package.</exception>
    public static PackageReader Open(Stream seekable, string passphrase, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        if (!seekable.CanSeek) throw new ArgumentException("The package stream must be seekable.", nameof(seekable));
        ZipArchive zip;
        try { zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen); }
        catch (InvalidDataException)
        {
            if (!leaveOpen) seekable.Dispose();
            throw new PackageFormatException("The stream is not an xrmverse migration package.");
        }
        return OpenZip(zip, "The stream", passphrase);
    }

    private static PackageReader OpenZip(ZipArchive zip, string what, string passphrase)
    {
        try
        {
            var headerEntry = zip.GetEntry(PackageHeader.FileName) ?? throw new PackageFormatException($"{what} is not an xrmverse migration package.");
            using var buffer = new MemoryStream();
            using (var s = headerEntry.Open()) s.CopyTo(buffer);
            var header = PackageHeader.Parse(buffer.ToArray());
            return new PackageReader(zip, null, header, header.Open(passphrase));
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>A read-only stream over the plaintext of an entry.</summary>
    /// <exception cref="PackageIntegrityException">The entry is missing; on read, when it was changed.</exception>
    public Stream OpenEntry(string logicalPath) =>
        EntryCipher.OpenRead(OpenBlob(_keys.EntryName(logicalPath)) ?? throw new PackageIntegrityException(logicalPath, $"The package entry '{logicalPath}' is missing."), _keys, logicalPath);

    /// <summary>Decrypts every entry listed in the manifest and compares its size and SHA-256 with the manifest.</summary>
    public async Task<PackageVerification> VerifyAsync(IProgress<PackageEntry>? progress, CancellationToken ct)
    {
        var problems = new List<PackageEntryProblem>();
        long bytes = 0;
        var buffer = new byte[81920];
        foreach (var entry in RawManifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var blob = OpenBlob(_keys.EntryName(entry.Path));
            if (blob is null)
            {
                problems.Add(new PackageEntryProblem(entry.Path, "missing"));
                continue;
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            try
            {
                await using var plain = EntryCipher.OpenRead(blob, _keys, entry.Path);
                int n;
                while ((n = await plain.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    size += n;
                }
            }
            catch (PackageIntegrityException)
            {
                problems.Add(new PackageEntryProblem(entry.Path, "tampered"));
                continue;
            }
            bytes += size;
            if (size != entry.Bytes) problems.Add(new PackageEntryProblem(entry.Path, "size"));
            else if (Convert.ToHexStringLower(hash.GetHashAndReset()) != entry.Sha256) problems.Add(new PackageEntryProblem(entry.Path, "sha256"));
            progress?.Report(entry);
        }
        return new PackageVerification(RawManifest.Entries.Count, bytes, problems);
    }

    public void Dispose() => _zip?.Dispose();

    private PackageManifest ReadManifest()
    {
        var blob = OpenBlob(PackageWriter.ManifestFile) ?? throw new PackageFormatException("The package has no manifest: the export that wrote it is not complete.");
        using var plain = EntryCipher.OpenRead(blob, _keys, PackageWriter.ManifestPath);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(plain, PackageJson.Options)
            ?? throw new PackageFormatException("The package manifest is empty.");
        if (!PackageHeader.SupportedVersions.Contains(manifest.FormatVersion)) throw PackageHeader.UnsupportedVersion(manifest.FormatVersion);
        return manifest;
    }

    private Stream? OpenBlob(string name)
    {
        if (_zip is not null) return _zip.GetEntry(name)?.Open();
        var file = Path.Combine(_folder!, name);
        return File.Exists(file) ? File.OpenRead(file) : null;
    }
}

public sealed record PackageVerification(int EntriesChecked, long BytesChecked, IReadOnlyList<PackageEntryProblem> Problems);

/// <summary>A problem found by verification: <c>missing</c>, <c>size</c>, <c>sha256</c> or <c>tampered</c>.</summary>
public sealed record PackageEntryProblem(string Path, string Problem);
