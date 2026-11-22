using System.Data;
using System.Security.Cryptography;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// The files of the custom controls (plan 3.5 UI-1d, ruling R2): what each control's manifest names (bundle, styles, resource
/// strings, images), kept in <c>meta.customcontrol_file</c> by control id and path (relative to the manifest folder, as the manifest
/// writes it, e.g. <c>bundle.js</c> or <c>strings/Name.1033.resx</c>) with the SHA-256 of the content. The solution import writes them,
/// the export reads them back byte for byte, and the file route serves them.
/// </summary>
public interface ICustomControlFiles
{
    /// <summary>
    /// Makes <paramref name="files"/> the whole file set of the control, in <paramref name="s"/>'s transaction: the paths not named
    /// go, the others are written with their content and hash.
    /// </summary>
    Task ReplaceAsync(DataSession s, Guid controlId, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct);

    /// <summary>
    /// The content of one file and its SHA-256 (64 lowercase hex characters), or null when the control has no such path. Reads on a
    /// connection of its own (the file route has no session).
    /// </summary>
    Task<(byte[] Content, string Sha256)?> GetAsync(Guid controlId, string path, CancellationToken ct);

    /// <summary>Every file of the control by path (empty when it has none), in <paramref name="s"/>'s transaction.</summary>
    Task<IReadOnlyDictionary<string, byte[]>> AllAsync(DataSession s, Guid controlId, CancellationToken ct);

    /// <summary>Removes every file of the control, in <paramref name="s"/>'s transaction.</summary>
    Task DeleteAsync(DataSession s, Guid controlId, CancellationToken ct);
}

/// <summary>The SQL Server <see cref="ICustomControlFiles"/>, over <c>meta.customcontrol_file</c> (migration 0014).</summary>
public sealed class CustomControlFiles(IDbConnectionFactory factory) : ICustomControlFiles
{
    /// <summary>The length of the <c>path</c> column.</summary>
    public const int MaxPathLength = 400;

    /// <inheritdoc />
    public async Task ReplaceAsync(DataSession s, Guid controlId, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(files);
        foreach (var (path, content) in files)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
                throw new ArgumentException($"A custom control file path must have 1 to {MaxPathLength} characters: '{path}'.", nameof(files));
            ArgumentNullException.ThrowIfNull(content, $"{nameof(files)}[{path}]");
        }

        await DeleteRowsAsync(s, controlId, ct);
        foreach (var (path, content) in files)
        {
            await using var cmd = s.Command(
                "INSERT INTO meta.customcontrol_file (customcontrolid, path, content, sha256) VALUES (@id, @path, @content, @sha)");
            cmd.AddParameter("@id", DbType.Guid, controlId);
            cmd.AddParameter("@path", DbType.String, path, MaxPathLength);
            cmd.AddParameter("@content", DbType.Binary, content, -1);
            cmd.AddParameter("@sha", DbType.AnsiStringFixedLength, Sha256Of(content), 64);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task<(byte[] Content, string Sha256)?> GetAsync(Guid controlId, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length is 0 or > MaxPathLength) return null;
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content, sha256 FROM meta.customcontrol_file WHERE customcontrolid = @id AND path = @path";
        cmd.AddParameter("@id", DbType.Guid, controlId);
        cmd.AddParameter("@path", DbType.String, path, MaxPathLength);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ((byte[])reader.GetValue(0), reader.GetString(1));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, byte[]>> AllAsync(DataSession s, Guid controlId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var cmd = s.Command("SELECT path, content FROM meta.customcontrol_file WHERE customcontrolid = @id ORDER BY path");
        cmd.AddParameter("@id", DbType.Guid, controlId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) files[reader.GetString(0)] = (byte[])reader.GetValue(1);
        return files;
    }

    /// <inheritdoc />
    public Task DeleteAsync(DataSession s, Guid controlId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return DeleteRowsAsync(s, controlId, ct);
    }

    /// <summary>
    /// Removes the files of a control in <paramref name="s"/>'s transaction. The core calls it when it deletes a <c>customcontrol</c>
    /// row, so that the files always go with the row (an uninstall, a cascade).
    /// </summary>
    public static async Task DeleteRowsAsync(DataSession s, Guid controlId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command("DELETE FROM meta.customcontrol_file WHERE customcontrolid = @id");
        cmd.AddParameter("@id", DbType.Guid, controlId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The SHA-256 of <paramref name="content"/> as 64 lowercase hex characters (the stored hash and the route's ETag).</summary>
    public static string Sha256Of(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
