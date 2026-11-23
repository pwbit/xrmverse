using System.Data;
using System.Text.Json;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Core.Mail;

/// <summary>
/// The synchronization state of one mailbox (plan 4.1c), in <c>meta.mailbox_sync</c>: the provider's cursor (where the next listing
/// resumes), and the key of the message whose import failed last with its count of failures (ruling PF11: only the pipeline's own
/// failures count), and the keys of the messages skipped since the cursor last moved (ruling T9-a: a listing without resume points,
/// such as a Graph page, lists a skipped message again until the cursor moves past the whole page), how many passes in a row the
/// message <c>failkey</c> was refused for a missing privilege of the delivery user (final review M4), and the generation of the state: a
/// reset of the mailbox's incoming settings (<see cref="ResetAsync"/>) adds one, and a pass that read an older generation writes nothing
/// (final review M2).
/// </summary>
/// <param name="Cursor">The provider's cursor; null starts from the mailbox's <c>processemailreceivedafter</c>.</param>
/// <param name="FailKey">The key of the message that failed last (<see cref="MailboxSyncJob"/>), or null.</param>
/// <param name="FailCount">How many passes in a row the message <paramref name="FailKey"/> failed.</param>
/// <param name="Skipped">The keys (lower-case hex) of the messages skipped since the cursor last moved; null is none.</param>
/// <param name="DeniedCount">How many passes in a row the message <paramref name="FailKey"/> was refused for a missing privilege (403).</param>
/// <param name="Generation">The generation the state was read at; 0 when the mailbox has no state row.</param>
public sealed record MailboxSyncState(string? Cursor, byte[]? FailKey, int FailCount, IReadOnlyList<string>? Skipped = null, int DeniedCount = 0,
    int Generation = 0)
{
    private const string Table = "meta.mailbox_sync";

    /// <summary>A mailbox that never synchronized.</summary>
    public static readonly MailboxSyncState Empty = new(null, null, 0);

    /// <summary>Whether the message of <paramref name="key"/> was skipped since the cursor last moved.</summary>
    public bool IsSkipped(byte[] key) => Skipped?.Contains(Convert.ToHexStringLower(key)) == true;

    /// <summary>This state with <paramref name="cursor"/>; a cursor that moves forgets the skipped keys.</summary>
    public MailboxSyncState WithCursor(string? cursor) =>
        cursor is null || cursor == Cursor ? this : this with { Cursor = cursor, Skipped = null };

    /// <summary>This state with <paramref name="key"/> skipped, keeping at most the last <paramref name="max"/> keys.</summary>
    public MailboxSyncState WithSkipped(byte[] key, int max) =>
        this with { Skipped = [.. (Skipped ?? []).Append(Convert.ToHexStringLower(key)).TakeLast(Math.Max(1, max))] };

    /// <summary>The state of <paramref name="mailboxId"/>; <see cref="Empty"/> when it has none.</summary>
    public static async Task<MailboxSyncState> ReadAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT {s.Quote("cursor")}, failkey, failcount, skipped, deniedcount, generation FROM {Table} WHERE mailboxid = @id");
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return Empty;
        return new MailboxSyncState(rd.IsDBNull(0) ? null : rd.GetString(0), rd.IsDBNull(1) ? null : (byte[])rd.GetValue(1),
            rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2)),
            rd.IsDBNull(3) ? null : ParseSkipped(rd.GetString(3)),
            rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4)),
            rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5)));
    }

    /// <summary>The stored generation of <paramref name="mailboxId"/>'s state; 0 when it has none.</summary>
    public static async Task<int> GenerationAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT generation FROM {Table} WHERE mailboxid = @id");
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        return await cmd.ExecuteScalarAsync(ct) is { } value and not DBNull ? Convert.ToInt32(value) : 0;
    }

    /// <summary>The skipped keys of a stored JSON array; a malformed value counts as none (the worst case is one more failed attempt).</summary>
    private static string[]? ParseSkipped(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) is { Length: > 0 } keys ? keys : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores <paramref name="state"/>, with its generation, as the state of <paramref name="mailboxId"/> (inserted or replaced) in
    /// <paramref name="s"/>'s transaction. The sync job checks the generation first, under the mailbox's row lock.
    /// </summary>
    public static async Task WriteAsync(DataSession s, Guid mailboxId, MailboxSyncState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(state);
        await using var cmd = s.Command(s.Dialect.Upsert(Table, ["mailboxid"], ["cursor", "failkey", "failcount", "skipped", "deniedcount", "generation", "updatedon"],
            $"SELECT @id AS mailboxid, @cursor AS {s.Quote("cursor")}, @failkey AS failkey, @failcount AS failcount, @skipped AS skipped, " +
            $"@denied AS deniedcount, @generation AS generation, {s.Dialect.UtcNow} AS updatedon"));
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        cmd.AddParameter("@cursor", DbType.String, (object?)state.Cursor ?? DBNull.Value, -1);
        cmd.AddParameter("@failkey", DbType.Binary, (object?)state.FailKey ?? DBNull.Value, 32);
        cmd.AddParameter("@failcount", DbType.Int32, state.FailCount);
        cmd.AddParameter("@skipped", DbType.String, state.Skipped is { Count: > 0 } skipped ? JsonSerializer.Serialize(skipped) : DBNull.Value, -1);
        cmd.AddParameter("@denied", DbType.Int32, state.DeniedCount);
        cmd.AddParameter("@generation", DbType.Int32, state.Generation);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Resets the state of <paramref name="mailboxId"/> in <paramref name="s"/>'s transaction, which holds the mailbox's row lock (its
    /// incoming settings were changed, so another server or account may be read now; every path that sends <c>incomingemailstatus</c>
    /// back to Not Run calls it): the cursor, the failure counts and the skipped keys are forgotten, so the next pass starts again from
    /// <c>processemailreceivedafter</c> (the message keys keep what was imported from being imported again), and the generation goes up by
    /// one, so a pass still running with the old state writes nothing (final review M2).
    /// </summary>
    public static async Task ResetAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var generation = await GenerationAsync(s, mailboxId, ct);
        await WriteAsync(s, mailboxId, Empty with { Generation = generation + 1 }, ct);
    }

    /// <summary>Deletes the state of <paramref name="mailboxId"/> in <paramref name="s"/>'s transaction: the mailbox is deleted.</summary>
    public static async Task DeleteAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"DELETE FROM {Table} WHERE mailboxid = @id");
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
