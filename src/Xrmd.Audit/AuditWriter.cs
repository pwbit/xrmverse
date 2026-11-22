using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Audit;

/// <summary>
/// Writes <c>audit</c> rows (plan 13, ruling A6) on the session of the audited operation, so in its transaction: a rollback of
/// the operation removes its rows. A row is written only when the organization and the record's table are audited.
/// </summary>
/// <remarks>
/// <c>userid</c> is the caller (the SYSTEM user for <see cref="Caller.Internal"/>), <c>callinguserid</c> the impersonating user
/// (null without impersonation), <c>transactionid</c> one id per SQL transaction, <c>createdon</c> UTC now and
/// <c>timetoliveinseconds</c> the organization's retention in seconds (-1: forever).
/// </remarks>
public sealed class AuditWriter(IMetadataStore store, AuditSettings settings)
{
    private const int SecondsPerDay = 86_400;

    /// <summary>
    /// True when changes of <paramref name="entity"/> are audited: the table's switch (no I/O), then the organization's (cached).
    /// The operations call it before reading the "before" image of a record, so nothing is read when auditing is off. Always false
    /// inside a <see cref="MigrationScope"/>: the writes of a migration from Dataverse leave no audit row (migration spec §7.5).
    /// </summary>
    public async Task<bool> IsAuditedAsync(DataSession s, EntityMetadata entity, CancellationToken ct) =>
        !MigrationScope.IsActive && AuditPolicy.IsAudited(entity) && (await settings.GetAsync(s, ct)).IsAuditEnabled;

    /// <summary>
    /// Writes the audit row of <paramref name="change"/> on <paramref name="record"/>, in <paramref name="s"/>'s transaction;
    /// nothing when the organization or the record's table is not audited.
    /// </summary>
    /// <exception cref="InvalidOperationException">The record's table does not exist.</exception>
    /// <exception cref="NotSupportedException">A value <see cref="ChangeData"/> cannot store.</exception>
    public async Task WriteAsync(DataSession s, Caller caller, int action, int operation, EntityReference record, AuditChange change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(change);
        var entity = store.GetEntity(record.LogicalName)
            ?? throw new InvalidOperationException($"The audited record's table '{record.LogicalName}' does not exist.");
        if (!await IsAuditedAsync(s, entity, ct)) return;

        var organization = await settings.GetAsync(s, ct);
        var userId = caller == Caller.Internal || caller.UserId == Guid.Empty ? await settings.SystemUserIdAsync(s, ct) : caller.UserId;
        var changeData = ChangeData.Serialize(change);

        await using var cmd = s.Command($"""
            INSERT INTO {s.DataTable(AuditTable.Name)}
                ({s.Quote(AuditTable.AuditId)}, {s.Quote(AuditTable.CreatedOn)}, {s.Quote(AuditTable.Action)}, {s.Quote(AuditTable.Operation)},
                 {s.Quote(AuditTable.ObjectId)}, {s.Quote(AuditTable.ObjectId + TypeMapping.TypeColumnSuffix)}, {s.Quote(AuditTable.ObjectTypeCodeColumn)},
                 {s.Quote(AuditTable.UserId)}, {s.Quote(AuditTable.CallingUserId)}, {s.Quote(AuditTable.ChangeData)}, {s.Quote(AuditTable.TransactionId)},
                 {s.Quote(AuditTable.TimeToLiveInSeconds)})
            VALUES (@id, @createdon, @action, @operation, @objectid, @objecttype, @objecttypecode, @userid, @callinguserid, @changedata,
                @transactionid, @ttl)
            """);
        cmd.AddParameter("@id", DbType.Guid, Guid.NewGuid());
        cmd.AddParameter("@createdon", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@action", DbType.Int32, action);
        cmd.AddParameter("@operation", DbType.Int32, operation);
        cmd.AddParameter("@objectid", DbType.Guid, record.Id);
        cmd.AddParameter("@objecttype", DbType.Int32, (object?)entity.ObjectTypeCode ?? DBNull.Value);
        cmd.AddParameter("@objecttypecode", DbType.String, entity.LogicalName, 64);
        cmd.AddParameter("@userid", DbType.Guid, userId);
        cmd.AddParameter("@callinguserid", DbType.Guid, (object?)caller.ImpersonatorId ?? DBNull.Value);
        cmd.AddParameter("@changedata", DbType.String, changeData, -1);
        cmd.AddParameter("@transactionid", DbType.Guid, TransactionId(s));
        cmd.AddParameter("@ttl", DbType.Int32, TimeToLive(organization.RetentionDays));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The attribute change of an event on a record of <paramref name="entity"/>, over its audited columns
    /// (<see cref="AuditPolicy.AuditedAttributes"/>); a column absent from an image is null in it. A create
    /// (<paramref name="before"/> null) records the non-null values of <paramref name="after"/>, a delete (<paramref name="after"/>
    /// null) those of <paramref name="before"/>, and an update the columns whose value differs (<see cref="AuditPolicy.SameValue"/>)
    /// with both values.
    /// </summary>
    /// <returns>Null when the table is not audited, or for an update when no audited column differs.</returns>
    public static AttributeChange? AttributeChange(EntityMetadata entity, Entity? before, Entity? after)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!AuditPolicy.IsAudited(entity) || (before is null && after is null)) return null;
        var old = new Dictionary<string, object?>(StringComparer.Ordinal);
        var @new = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attribute in AuditPolicy.AuditedAttributes(entity))
        {
            var name = attribute.LogicalName;
            var was = Value(before, name);
            var now = Value(after, name);
            if (before is null)
            {
                if (now is not null) @new[name] = now;
            }
            else if (after is null)
            {
                if (was is not null) old[name] = was;
            }
            else if (!AuditPolicy.SameValue(was, now))
            {
                old[name] = was;
                @new[name] = now;
            }
        }
        if (before is not null && after is not null && old.Count == 0) return null;
        return new AttributeChange(old, @new);
    }

    private static object? Value(Entity? image, string name) =>
        image is not null && image.Attributes.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// The row's time to live: -1 keeps it forever; otherwise the retention in seconds, at most <see cref="int.MaxValue"/> (about 68
    /// years) as the column is an integer.
    /// </summary>
    internal static int TimeToLive(int retentionDays) =>
        retentionDays < 0 ? -1 : (int)Math.Min((long)retentionDays * SecondsPerDay, int.MaxValue);

    /// <summary>
    /// One <c>transactionid</c> per SQL transaction, shared by every row written in it: the session's
    /// <see cref="DataSession.TransactionId"/>, never the provider's transaction object, which Npgsql reuses for the next
    /// transaction of a connection (plan 2.7a, Task 8 fix round 2).
    /// </summary>
    private static Guid TransactionId(DataSession s) =>
        s.Transaction is null
            ? Guid.NewGuid() // auto-commit: each statement is its own transaction
            : s.TransactionId;
}
