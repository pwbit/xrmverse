using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Files;

/// <summary>
/// The continuation token of a file session (ruling F6): the session id followed by an HMAC-SHA256 over (session id, caller),
/// base64url-encoded so that it can travel in a URL query (the Web API's <c>sessiontoken</c>). The token proves nothing by
/// itself: the session row is also checked against the caller, so a forged token for another caller's session is refused
/// either way; the HMAC keeps tampered and guessed tokens from reaching the database lookup.
/// </summary>
public sealed class FileSessionTokens
{
    private const int IdLength = 16;
    private const int MacLength = 32;
    private const int TokenLength = (IdLength + MacLength) / 3 * 4; // 48 bytes: 64 base64 characters, no padding
    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("xrmd:filesession:v1");

    private readonly byte[] _key;

    private FileSessionTokens(byte[] key) => _key = key;

    /// <summary>
    /// The tokens of an organization: the key is HMAC-SHA256(<paramref name="secret"/>, purpose ‖ organization id), so the same
    /// secret and organization always give the same key (tokens survive a restart and work on every instance).
    /// </summary>
    /// <param name="secret">At least 32 bytes that only the server knows (<c>meta.filesecret</c>, created by migration 0004).</param>
    /// <exception cref="ArgumentException">A secret shorter than 32 bytes.</exception>
    public static FileSessionTokens Derive(ReadOnlySpan<byte> secret, Guid organizationId)
    {
        if (secret.Length < 32) throw new ArgumentException("The file session secret must be at least 32 bytes long.", nameof(secret));
        var input = new byte[Purpose.Length + IdLength];
        Purpose.CopyTo(input, 0);
        organizationId.ToByteArray().CopyTo(input, Purpose.Length);
        return new FileSessionTokens(HMACSHA256.HashData(secret, input));
    }

    /// <summary>The token of <paramref name="sessionId"/> for <paramref name="callerId"/>.</summary>
    public string Issue(Guid sessionId, Guid callerId)
    {
        var token = new byte[IdLength + MacLength];
        sessionId.ToByteArray().CopyTo(token, 0);
        Mac(sessionId, callerId).CopyTo(token, IdLength);
        return Base64UrlEncode(token);
    }

    /// <summary>
    /// True when <paramref name="token"/> was issued by this key for <paramref name="callerId"/>; <paramref name="sessionId"/>
    /// is then its session. Any other input (malformed, tampered, another caller or key) is false, with no detail.
    /// </summary>
    public bool TryRead(string? token, Guid callerId, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (token is not { Length: TokenLength } || !TryBase64UrlDecode(token, out var bytes)
            || bytes.Length != IdLength + MacLength)
            return false;
        var id = new Guid(bytes.AsSpan(0, IdLength));
        if (!CryptographicOperations.FixedTimeEquals(Mac(id, callerId), bytes.AsSpan(IdLength))) return false;
        sessionId = id;
        return true;
    }

    private byte[] Mac(Guid sessionId, Guid callerId)
    {
        var input = new byte[IdLength * 2];
        sessionId.ToByteArray().CopyTo(input, 0);
        callerId.ToByteArray().CopyTo(input, IdLength);
        return HMACSHA256.HashData(_key, input);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string text, out byte[] bytes)
    {
        bytes = [];
        foreach (var c in text)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            // A canonical encoding only: another text decoding to the same bytes is a changed token.
            return Base64UrlEncode(bytes) == text;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The kinds of file session (<c>meta.filesession.kind</c>).</summary>
public static class FileSessionKind
{
    public const string Upload = "upload";
    public const string Download = "download";
}

/// <summary>A block staged by UploadBlock: its id (Base64 as sent), its index in the staging keys and its size.</summary>
public sealed record StagedBlock(string Id, int Index, long Size);

/// <summary>A row of <c>meta.filesession</c> (ruling F6).</summary>
public sealed record FileSession(Guid Id, string Kind, Guid CallerId, string Entity, Guid RecordId, string Attribute, string? FileName,
    DateTime CreatedOn, DateTime LastUsedOn, Guid? FileAttachmentId, IReadOnlyList<StagedBlock> Blocks);

/// <summary>
/// The rows of <c>meta.filesession</c> (migration 0004): upload and download sessions, kept in the database so that they
/// survive a restart. Every call runs on the given <see cref="DataSession"/>, so its writes share the caller's transaction.
/// </summary>
public static class FileSessionStore
{
    /// <summary>A session expires this long after its last use (Dataverse: the continuation token is valid for 24 hours).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private const string Columns =
        "id, kind, caller_id, entity_logical_name, record_id, attribute_logical_name, file_name, created_on, last_used_on, fileattachment_id, block_ids";

    public static async Task InsertAsync(DataSession s, FileSession session, CancellationToken ct)
    {
        await using var cmd = s.Command($"INSERT INTO meta.filesession ({Columns}) VALUES " +
                                        "(@id, @kind, @caller, @entity, @record, @attribute, @name, @created, @used, @file, @blocks)");
        Add(cmd, session);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The session, read with an update lock that its transaction holds (concurrent blocks of one upload queue up); null when absent.</summary>
    public static async Task<FileSession?> ReadForUpdateAsync(DataSession s, Guid id, CancellationToken ct)
    {
        // Plan 2.7b, Task 6: the dialect's row lock (FOR UPDATE on PostgreSQL); a missing session is refused, so no key lock.
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT {Columns} FROM meta.filesession{d.LockHint(RowLock.UpdateRowRange)} WHERE id = @id{d.LockClause(RowLock.UpdateRowRange)}");
        cmd.AddParameter("@id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new FileSession(rd.GetGuid(0), rd.GetString(1), rd.GetGuid(2), rd.GetString(3), rd.GetGuid(4), rd.GetString(5),
            rd.IsDBNull(6) ? null : rd.GetString(6), DateTime.SpecifyKind(rd.GetDateTime(7), DateTimeKind.Utc),
            DateTime.SpecifyKind(rd.GetDateTime(8), DateTimeKind.Utc), rd.IsDBNull(9) ? null : rd.GetGuid(9),
            JsonSerializer.Deserialize<List<StagedBlock>>(rd.GetString(10)) ?? []);
    }

    /// <summary>Writes the staged blocks and the last use of the session.</summary>
    public static async Task UpdateAsync(DataSession s, FileSession session, CancellationToken ct)
    {
        await using var cmd = s.Command("UPDATE meta.filesession SET last_used_on = @used, block_ids = @blocks WHERE id = @id");
        cmd.AddParameter("@id", DbType.Guid, session.Id);
        cmd.AddParameter("@used", DbType.DateTime2, session.LastUsedOn);
        cmd.AddParameter("@blocks", DbType.String, JsonSerializer.Serialize(session.Blocks), -1);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task DeleteAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command("DELETE FROM meta.filesession WHERE id = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deletes the sessions last used before <paramref name="cutoff"/> and returns their ids.</summary>
    public static async Task<IReadOnlyList<Guid>> DeleteExpiredAsync(DataSession s, DateTime cutoff, CancellationToken ct)
    {
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"DELETE FROM meta.filesession{d.ReturningInline(["id"], RowImage.Deleted)} WHERE last_used_on < @cutoff{d.ReturningTrailing(["id"], RowImage.Deleted)}");
        cmd.AddParameter("@cutoff", DbType.DateTime2, cutoff);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// The secret the session tokens are derived from (<c>meta.filesecret</c>, 32 random bytes written by migration 0004).
    /// </summary>
    /// <exception cref="InvalidOperationException">The migration has not run.</exception>
    public static async Task<byte[]> SecretAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit("SELECT secret FROM meta.filesecret", "1"));
        return await cmd.ExecuteScalarAsync(ct) as byte[]
               ?? throw new InvalidOperationException("meta.filesecret is empty: migration 0004_file_sessions has not run.");
    }

    private static void Add(DbCommand cmd, FileSession session)
    {
        cmd.AddParameter("@id", DbType.Guid, session.Id);
        cmd.AddParameter("@kind", DbType.String, session.Kind, 16);
        cmd.AddParameter("@caller", DbType.Guid, session.CallerId);
        cmd.AddParameter("@entity", DbType.String, session.Entity, 128);
        cmd.AddParameter("@record", DbType.Guid, session.RecordId);
        cmd.AddParameter("@attribute", DbType.String, session.Attribute, 128);
        cmd.AddParameter("@name", DbType.String, (object?)session.FileName ?? DBNull.Value, -1);
        cmd.AddParameter("@created", DbType.DateTime2, session.CreatedOn);
        cmd.AddParameter("@used", DbType.DateTime2, session.LastUsedOn);
        cmd.AddParameter("@file", DbType.Guid, (object?)session.FileAttachmentId ?? DBNull.Value);
        cmd.AddParameter("@blocks", DbType.String, JsonSerializer.Serialize(session.Blocks), -1);
    }
}
