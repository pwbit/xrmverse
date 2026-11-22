using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Writes the rows of one sub-chunk of a phase (plan M4a): all of them in one transaction, committed with the sub-chunk's ledger row
/// so a resume skips it. A failing sub-chunk is rolled back and halved until the bad row is alone; that row becomes an error issue
/// and the others commit. The write must skip rows that already exist (a sub-chunk halved before a crash is written again).
/// </summary>
public sealed class ImportChunkWriter(IDbConnectionFactory factory, MigrationRunStore store, MigrationLedger ledger)
{
    /// <summary>The most rows in one transaction (spec §7.6 step 4).</summary>
    public const int SubChunkSize = 1000;

    /// <summary>Writes one row, or several in one transaction.</summary>
    public delegate Task WriteRows(DataSession session, IReadOnlyList<MigrationRecord> rows, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="rows"/> under the ledger key <paramref name="entry"/> unless it is done; returns the number of rows that
    /// failed (each one an error issue of <paramref name="phase"/> about <paramref name="table"/>).
    /// </summary>
    public async Task<int> WriteAsync(int phase, string entry, string table, IReadOnlyList<MigrationRecord> rows, WriteRows write,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(write);
        if (await store.IsChunkDoneAsync(ledger.RunId, entry, ct)) return 0;
        if (await TryAsync(rows, write, mark: (entry, phase), ct) is null) return 0;
        var failed = await HalveAsync(phase, table, rows, write, ct);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await store.MarkChunkAsync(ledger.RunId, phase, entry, rows.Count, failed, new DataSession(conn, tx), ct);
        await tx.CommitAsync(ct);
        return failed;
    }

    /// <summary>Writes the rows in halves, recursively; a single row that fails is an error issue. Returns the rows that failed.</summary>
    private async Task<int> HalveAsync(int phase, string table, IReadOnlyList<MigrationRecord> rows, WriteRows write, CancellationToken ct)
    {
        if (rows.Count == 1)
        {
            if (await TryAsync(rows, write, null, ct) is not { } error) return 0;
            await ledger.IssueAsync(new MigrationIssue(phase, MigrationIssue.Error, Code(error), table, rows[0].Id, null, error.Message), ct);
            return 1;
        }
        var half = rows.Count / 2;
        var failed = 0;
        foreach (var part in new[] { rows.Take(half).ToList(), rows.Skip(half).ToList() })
            failed += await TryAsync(part, write, null, ct) is null ? 0 : await HalveAsync(phase, table, part, write, ct);
        return failed;
    }

    /// <summary>Writes the rows in one transaction (with the ledger row when <paramref name="mark"/> is given); the error, or null.</summary>
    private async Task<Exception?> TryAsync(IReadOnlyList<MigrationRecord> rows, WriteRows write, (string Entry, int Phase)? mark, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var session = new DataSession(conn, tx);
        try
        {
            await write(session, rows, ct);
            if (mark is { } m) await store.MarkChunkAsync(ledger.RunId, m.Phase, m.Entry, rows.Count, 0, session, ct);
            await tx.CommitAsync(ct);
            session.Committed();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MigrationPhaseException)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            session.RolledBack();
            return ex;
        }
    }

    /// <summary>The error code of a failed row: the core's <c>0x8004xxxx</c> code, else the generic one.</summary>
    public static string Code(Exception error) => error switch
    {
        XrmdFaultException f => $"0x{f.ErrorCode:x8}",
        ImportValueException v => v.Code,
        _ => "0x80040216",
    };

    /// <summary>The rows in slices of <see cref="SubChunkSize"/>.</summary>
    public static IEnumerable<IReadOnlyList<MigrationRecord>> Slices(IReadOnlyList<MigrationRecord> rows) =>
        rows.Chunk(SubChunkSize).Select(c => (IReadOnlyList<MigrationRecord>)c);
}
