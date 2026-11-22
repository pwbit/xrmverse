using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Audit;

/// <summary>
/// The retention of audit data (plan 13, ruling A8): a row expires when its <c>createdon</c> plus its <c>timetoliveinseconds</c>
/// is in the past; -1 keeps it forever. The time to live is stamped when the row is written, so a new
/// <c>auditretentionperiodv2</c> applies to new rows only.
/// </summary>
public static class AuditRetention
{
    /// <summary>Deletes the rows expired at <paramref name="nowUtc"/>; returns how many.</summary>
    public static Task<int> PurgeAsync(DataSession s, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // The time to live is at most int.MaxValue seconds (about 68 years): subtracting it from now stays in range.
        return AuditReader.DeleteInBatchesAsync(s,
            $"{s.Quote(AuditTable.TimeToLiveInSeconds)} >= 0 AND {s.Quote(AuditTable.CreatedOn)} < {s.Dialect.AddInterval("second", $"-{s.Quote(AuditTable.TimeToLiveInSeconds)}", "@now")}",
            cmd => cmd.AddParameter("@now", DbType.DateTime2, nowUtc), ct);
    }
}
