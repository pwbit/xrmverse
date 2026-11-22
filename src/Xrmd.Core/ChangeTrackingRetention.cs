using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// The retention of the deletion log (plan 2.13, ruling C4): a row expires when its <c>deleted_on</c> is older than
/// <c>organization.expirechangetrackingindays</c>. Each batch raises <c>meta.change_tracking_state.purged_version</c> to the
/// highest version it removed, in the same transaction, so a token below it is known to be expired.
/// </summary>
public static class ChangeTrackingRetention
{
    /// <summary>Rows per batch, under the lock-escalation threshold.</summary>
    public const int Batch = 5000;

    /// <summary>Deletes the log rows expired at <paramref name="nowUtc"/> in batches; returns how many.</summary>
    public static async Task<int> PurgeAsync(DataSession s, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        int days;
        await using (var cmd = s.Command(s.Dialect.Limit($"SELECT {s.Quote(ChangeTrackingSchema.ExpireDaysColumn)} FROM data.organization", "1")))
            days = await cmd.ExecuteScalarAsync(ct) is int d ? d : ChangeTrackingSchema.DefaultExpireDays;

        var total = 0;
        while (true)
        {
            var (deleted, last) = await PurgeBatchAsync(s, days, nowUtc, ct);
            total += deleted;
            if (deleted < Batch) return total;
        }
    }

    /// <summary>A version as read: a <c>rowversion</c>'s 8 big-endian bytes (SQL Server) or a <c>bigint</c> (PostgreSQL).</summary>
    private static long Version(object value) => value switch
    {
        byte[] { Length: 8 } bytes => BinaryPrimitives.ReadInt64BigEndian(bytes),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    /// <summary>One batch and the purged-version raise in one transaction (the caller's, or one of its own when there is none).</summary>
    private static async Task<(int Deleted, long Last)> PurgeBatchAsync(DataSession s, int days, DateTime nowUtc, CancellationToken ct)
    {
        var own = s.Transaction is null ? await s.Connection.BeginTransactionAsync(ct) : null;
        var session = own is null ? s : new DataSession(s.Connection, own);
        try
        {
            // Plan 2.7a, Task 7: one statement on both engines; the count and the highest version are taken from the rows it
            // returns (a rowversion's 8 big-endian bytes on SQL Server, a bigint on PostgreSQL).
            var d = session.Dialect;
            var expired = d.Limit("SELECT version FROM meta.deleted_record WHERE deleted_on < @cutoff ORDER BY version",
                $"({Batch.ToString(CultureInfo.InvariantCulture)})");
            var deleted = 0;
            long last = 0;
            await using (var cmd = session.Command(
                             $"DELETE FROM meta.deleted_record{d.ReturningInline(["version"], RowImage.Deleted)} WHERE version IN ({expired})" +
                             d.ReturningTrailing(["version"], RowImage.Deleted) + ";"))
            {
                cmd.AddParameter("@cutoff", DbType.DateTime2, (nowUtc - DateTime.MinValue).TotalDays > days ? nowUtc.AddDays(-days) : DateTime.MinValue);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    deleted++;
                    last = Math.Max(last, Version(reader.GetValue(0)));
                }
            }
            if (deleted > 0)
            {
                await using var raise = session.Command(
                    "UPDATE meta.change_tracking_state SET purged_version = CASE WHEN @max > purged_version THEN @max ELSE purged_version END;");
                raise.AddParameter("@max", DbType.Int64, last);
                await raise.ExecuteNonQueryAsync(ct);
            }
            if (own is not null) await own.CommitAsync(ct);
            return (deleted, last);
        }
        catch
        {
            if (own is not null) await own.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
    }
}
