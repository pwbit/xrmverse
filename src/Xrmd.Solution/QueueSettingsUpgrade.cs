using System.Data;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization made before plan 4.1a: the element <c>IsValidForQueue</c> of the kept XML of each table
/// becomes a marker (the metadata holds the setting now), and a table whose kept value was <c>1</c> gets <c>IsValidForQueue</c> in
/// its metadata, so that a read shows the same value as before; a kept <c>0</c> on a table without a value stores <c>false</c>
/// (final review I3). Every activity table that has neither a value nor a kept element (the four seeded ones and the custom
/// activities of an older organization) is enabled for queues, as a new one is. Then <c>queueitem.objectid</c> gets its targets
/// and relationships. Runs after <see cref="ActivitySettingsUpgrade"/>.
/// </summary>
/// <remarks>
/// A kept <c>1</c> that the table cannot have (<c>MetadataStore.QueueSettingRefusal</c>) is reported and not applied; a kept
/// <c>0</c> on a table that has the setting keeps it (it is one-way). Idempotent: only a kept element that is not a marker and an
/// activity table without a value are touched, and the targets are written only when missing, so a second run writes nothing. A
/// kept <c>0</c> is never overridden by the activity default: the element becomes a marker in the same run that stores the
/// explicit <c>false</c>, so a later run sees a value and leaves the table off.
/// </remarks>
public static class QueueSettingsUpgrade
{
    private const string Element = "IsValidForQueue";

    /// <summary>Runs the upgrade.</summary>
    /// <param name="store">The organization's metadata store (loaded, seeded).</param>
    /// <param name="factory">The organization's database (the signature of the other startup upgrades; the store writes through its own sessions).</param>
    /// <param name="logger">Receives a warning for each kept value that is not applied.</param>
    /// <exception cref="NotSupportedException"><paramref name="store"/> is not the server's <see cref="MetadataStore"/>.</exception>
    /// <exception cref="InvalidOperationException">A kept value is not a flag: it is not dropped silently.</exception>
    public static async Task EnsureAsync(IMetadataStore store, IDbConnectionFactory factory, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        if (store is not MetadataStore concrete)
            throw new NotSupportedException("The queue settings upgrade needs the metadata store of the server.");

        var kept = new List<(int Type, string Key, string Xml)>();
        var enable = new List<string>();
        var disable = new List<string>();
        foreach (var entity in store.GetAllEntities())
        {
            var on = entity.IsValidForQueue?.Value == true;
            var wanted = false;
            bool? keptOn = null;
            var (type, key) = UnmodelledXml.EntityKey(entity.LogicalName);
            if (SolutionExporter.Kept(store.GetUnmodelledXml(type, key), "entity", $"entity '{key}'") is { } element
                && UnmodelledXml.TakeModelled(element, Element) is [var (_, value), ..])
            {
                kept.Add((type, key, element.ToString(SaveOptions.DisableFormatting)));
                keptOn = Flag(value, entity.LogicalName);
                if (keptOn == false && on)
                    logger?.LogWarning("{Element} of '{Entity}' is 0 in its kept XML, but the table has the setting, which stays on.", Element, entity.LogicalName);
                else if (keptOn == false && entity.IsValidForQueue is null)
                    disable.Add(entity.LogicalName); // the imported solution said 0: an explicit value, so the activity default below never applies
                else if (keptOn == true && !on)
                {
                    if (concrete.QueueSettingRefusal(entity) is { } refusal)
                        logger?.LogWarning("{Element} of '{Entity}' is 1 in its kept XML, but it is not applied: {Refusal}", Element, entity.LogicalName, refusal);
                    else wanted = true;
                }
            }
            // An activity table of an older organization that its solution said nothing about: queue-enabled, as a new one is (Global
            // Constraints of plan 4.1a). A kept value, 0 included, wins over the default.
            if (!on && keptOn is null && entity.IsValidForQueue is null && ActivitySchema.IsActivityTable(entity) && concrete.QueueSettingRefusal(entity) is null)
                wanted = true;
            if (wanted) enable.Add(entity.LogicalName);
        }

        await concrete.InUpgradeSessionAsync(async session =>
        {
            foreach (var name in enable)
                await store.UpdateEntityAsync(session, new EntityMetadata { LogicalName = name, IsValidForQueue = QueueSchema.SettingFlag(true) }, ct);
            foreach (var name in disable)
                await store.UpdateEntityAsync(session, new EntityMetadata { LogicalName = name, IsValidForQueue = QueueSchema.SettingFlag(false) }, ct);
            foreach (var (type, key, xml) in kept) await store.SetUnmodelledXmlAsync(session, type, key, xml, ct);
            await concrete.EnsureActivityLinksAsync(session, ct);
        }, ct);
        await BackfillDefaultQueuesAsync(store, factory, ct);
    }

    /// <summary>
    /// Every user and every owner team of an organization made before plan 4.1a gets its private default queue, as one created
    /// now does (<c>CoreOperations.InsertDefaultQueueAsync</c>): the same name, owner and view type, the user a member of its own
    /// queue, <c>queueid</c> of the principal pointing to it. Only a principal without a <c>queueid</c> is touched, in one
    /// transaction, so a second run finds none and writes nothing. An access or group team has no default queue.
    /// </summary>
    /// <remarks>
    /// Two runs at once (two processes starting together; final review minor 3) give each principal one queue. What guarantees it is
    /// the conditional link (<c>queueid IS NULL</c>), done before the queue is inserted in the same transaction: the UPDATE re-checks
    /// the predicate on the committed row, so a principal linked meanwhile is left alone and gets no second queue. The key lock taken
    /// first only lowers contention: on PostgreSQL it is a transaction advisory lock that serializes the runs, on SQL Server it sends
    /// nothing. Nothing is deleted. There is no foreign key from <c>queueid</c> to <c>queue</c>, so linking before inserting is valid.
    /// </remarks>
    internal static async Task BackfillDefaultQueuesAsync(IMetadataStore store, IDbConnectionFactory factory, CancellationToken ct)
    {
        if (store.GetEntity(QueueSchema.Queue) is not { } queue) return;
        var userType = store.GetEntity("systemuser")?.ObjectTypeCode ?? 8;
        var teamType = store.GetEntity("team")?.ObjectTypeCode ?? 9;
        await using var conn = await factory.OpenAsync(ct);
        var d = SqlDialects.For(conn);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await new DataSession(conn, tx).KeyLockAsync(QueueSchema.Queue, "default-queue-backfill", ct);

        async Task<List<(Guid Id, string? Name, Guid? BusinessUnit)>> PendingAsync(string sql)
        {
            var rows = new List<(Guid, string?, Guid?)>();
            await using var cmd = conn.Command(sql, tx);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add((rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetGuid(2)));
            return rows;
        }

        var users = await PendingAsync($"SELECT systemuserid, fullname, businessunitid FROM {d.DataTable("systemuser")} WHERE {d.Quote(QueueSchema.QueueIdColumn)} IS NULL");
        var teams = await PendingAsync($"SELECT teamid, name, businessunitid FROM {d.DataTable("team")} WHERE {d.Quote(QueueSchema.QueueIdColumn)} IS NULL " +
                                       $"AND ({d.Quote("teamtype")} IS NULL OR {d.Quote("teamtype")} = 0)");
        foreach (var (table, key, type, owner, rows) in new[]
                 {
                     ("systemuser", "systemuserid", userType, "owninguser", users),
                     ("team", "teamid", teamType, "owningteam", teams),
                 })
            foreach (var (id, name, businessUnit) in rows)
            {
                // The link first, and only where none is set yet: a principal linked meanwhile (its own Create, or another run)
                // keeps its queue, and no queue is inserted for it, so nothing is ever deleted (no raw delete on business data).
                var queueId = Guid.NewGuid();
                int linked;
                await using (var link = conn.Command(
                                 $"UPDATE {d.DataTable(table)} SET {d.Quote(QueueSchema.QueueIdColumn)} = @queue " +
                                 $"WHERE {d.Quote(key)} = @id AND {d.Quote(QueueSchema.QueueIdColumn)} IS NULL", tx))
                {
                    link.AddParameter("@queue", DbType.Guid, queueId);
                    link.AddParameter("@id", DbType.Guid, id);
                    linked = await link.ExecuteNonQueryAsync(ct);
                }
                if (linked == 0) continue;
                await using (var insert = conn.Command($"""
                    INSERT INTO {d.DataTable(QueueSchema.Queue)} ({d.Quote(SystemAttributes.PrimaryIdName(queue))}, name, queueviewtype, queuetypecode,
                        ownerid, owneridtype, {owner}, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                    VALUES (@queue, @name, {QueueSchema.PrivateViewType}, {QueueSchema.TypeCode}, @id, @type, @id, @bu, {d.UtcNow}, {d.UtcNow}, 0, 1)
                    """, tx))
                {
                    insert.AddParameter("@queue", DbType.Guid, queueId);
                    insert.AddParameter("@name", DbType.String, QueueSchema.DefaultQueueName(name), QueueSchema.NameMaxLength);
                    insert.AddParameter("@id", DbType.Guid, id);
                    insert.AddParameter("@type", DbType.Int32, type);
                    insert.AddParameter("@bu", DbType.Guid, businessUnit);
                    await insert.ExecuteNonQueryAsync(ct);
                }
                if (table != "systemuser" || store.GetEntity(QueueSchema.QueueMembership) is not { } membership) continue;
                await using var member = conn.Command($"""
                    INSERT INTO {d.DataTable(membership.LogicalName)} ({d.Quote(SystemAttributes.PrimaryIdName(membership))}, {d.Quote(QueueSchema.QueueIdColumn)},
                        systemuserid, createdon, modifiedon, statecode, statuscode)
                    VALUES (@member, @queue, @id, {d.UtcNow}, {d.UtcNow}, 0, 1)
                    """, tx);
                member.AddParameter("@member", DbType.Guid, Guid.NewGuid());
                member.AddParameter("@queue", DbType.Guid, queueId);
                member.AddParameter("@id", DbType.Guid, id);
                await member.ExecuteNonQueryAsync(ct);
            }
        await tx.CommitAsync(ct);
    }

    private static bool Flag(string value, string entity) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" or "" => false,
        _ => throw new InvalidOperationException($"The kept XML of '{entity}' gives {Element} the value '{value}', which is not a flag (1 or 0)."),
    };
}
