using System.Data;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Audit;

/// <summary>The organization's audit settings: <c>isauditenabled</c> and <c>auditretentionperiodv2</c> (days, -1 forever).</summary>
public sealed record OrganizationAuditSettings(bool IsAuditEnabled, int RetentionDays);

/// <summary>
/// The organization's audit settings, read once and cached: every write consults them, so with auditing off a write costs a
/// cached flag check. The pipeline calls <see cref="Invalidate"/> after a commit that wrote the <c>organization</c> row.
/// </summary>
public sealed class AuditSettings
{
    private sealed record Snapshot(OrganizationAuditSettings Settings, Guid SystemUserId);

    private readonly object _gate = new();
    private volatile Snapshot? _snapshot;
    private int _version;

    /// <summary>The settings, read in <paramref name="s"/> when not cached.</summary>
    /// <exception cref="InvalidOperationException">The organization has not been seeded.</exception>
    public async Task<OrganizationAuditSettings> GetAsync(DataSession s, CancellationToken ct) => (await SnapshotAsync(s, ct)).Settings;

    /// <summary>The id of the organization's SYSTEM user, to whom internal writes are attributed.</summary>
    public async Task<Guid> SystemUserIdAsync(DataSession s, CancellationToken ct) => (await SnapshotAsync(s, ct)).SystemUserId;

    /// <summary>Forgets the cached settings: the next <see cref="GetAsync"/> reads them again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _snapshot = null;
        }
    }

    private async Task<Snapshot> SnapshotAsync(DataSession s, CancellationToken ct)
    {
        if (_snapshot is { } cached) return cached;
        ArgumentNullException.ThrowIfNull(s);
        int version;
        lock (_gate) version = _version;
        await using var cmd = s.Command(
            s.Dialect.Limit($"SELECT isauditenabled, auditretentionperiodv2, name FROM {s.DataTable("organization")}", "1"));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct))
            throw new InvalidOperationException("The organization has not been seeded (data.organization is empty).");
        var snapshot = new Snapshot(
            new OrganizationAuditSettings(!rd.IsDBNull(0) && rd.GetBoolean(0), rd.IsDBNull(1) ? 30 : rd.GetInt32(1)),
            SeedOptions.DeriveSystemUserId(rd.IsDBNull(2) ? string.Empty : rd.GetString(2)));
        // A read that raced with Invalidate may be stale: use it, but do not cache it.
        lock (_gate)
            if (_version == version) _snapshot = snapshot;
        return snapshot;
    }
}
