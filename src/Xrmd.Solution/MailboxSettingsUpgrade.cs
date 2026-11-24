using System.Data;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization made before plan 4.1b: every user and every queue (the default queues of the teams
/// included) gets the mailbox a new one gets (<see cref="MailboxRows.EnsureAsync"/>). Runs after <see cref="QueueSettingsUpgrade"/> (whose
/// backfill creates the default queues) and after the administrator of the start is inserted. The built-in SYSTEM user and its queues are skipped.
/// </summary>
/// <remarks>
/// Idempotent: only an owner without a <c>defaultmailbox</c> is touched, in one transaction per run, so a second run finds none and
/// writes nothing. Nothing is deleted.
/// </remarks>
public static class MailboxSettingsUpgrade
{
    /// <summary>Runs the upgrade.</summary>
    public static async Task EnsureAsync(IMetadataStore store, IDbConnectionFactory factory, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        if (store.GetEntity(MailSchema.Mailbox) is null) return;
        foreach (var table in new[] { "systemuser", QueueSchema.Queue })
        {
            if (store.GetEntity(table) is null) continue;
            await using var conn = await factory.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var s = new DataSession(conn, tx);
            await s.KeyLockAsync(MailSchema.Mailbox, "mailbox-backfill", ct);
            var pending = new List<Guid>();
            // The built-in SYSTEM user (no Entra identity) and the queues it owns get no mailbox: nothing sends from them.
            var notSystem = table == "systemuser"
                ? "NOT (fullname = 'SYSTEM' AND azureactivedirectoryobjectid IS NULL)"
                : $"NOT EXISTS (SELECT 1 FROM {s.DataTable("systemuser")} u WHERE u.systemuserid = {s.DataTable(table)}.ownerid AND u.fullname = 'SYSTEM' AND u.azureactivedirectoryobjectid IS NULL)";
            await using (var cmd = s.Command($"SELECT {s.Quote(MailboxRows.KeyOf(table))} FROM {s.DataTable(table)} WHERE {s.Quote(MailSchema.DefaultMailbox)} IS NULL AND {notSystem}"))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) pending.Add(rd.GetGuid(0));
            foreach (var id in pending) await MailboxRows.EnsureAsync(s, store, table, id, null, ct);
            await tx.CommitAsync(ct);
            if (pending.Count > 0) logger?.LogInformation("Created {Count} mailboxes for {Table}.", pending.Count, table);
        }
    }
}

/// <summary>
/// The mailbox row of a user or a queue, written the same way for a new principal (<c>Mailboxes.InsertForAsync</c> of the core) and by the
/// startup upgrade: a disabled mailbox (not enabled, statuses Not Run) named after its owner, with the owner's address and delivery
/// methods (None when the owner has none), the organization's <c>defaultemailserverprofileid</c> when one is set, linked from the
/// owner's <c>defaultmailbox</c>.
/// </summary>
public static class MailboxRows
{
    /// <summary>The primary key column of <paramref name="ownerTable"/> (<c>systemuser</c> or <c>queue</c>).</summary>
    public static string KeyOf(string ownerTable) => ownerTable == "systemuser" ? "systemuserid" : "queueid";

    /// <summary>
    /// The mailbox of the owner, created when it has none: the owner's <c>defaultmailbox</c> is linked first and only where none is set
    /// (a concurrent run, or the owner's own Create, leaves the owner with one mailbox), then the row is inserted. An owner with an address and no
    /// approval gets Pending Approval. Nothing is written for an owner that already has one.
    /// </summary>
    /// <param name="by">The creator stamped on the row (null: the owner's own id for a user, none for a queue).</param>
    /// <returns>The mailbox id and whether it was just created.</returns>
    public static async Task<(Guid Id, bool Created)> EnsureAsync(DataSession s, IMetadataStore? store, string ownerTable, Guid ownerId,
        Guid? by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var isUser = ownerTable == "systemuser";
        var key = KeyOf(ownerTable);
        await s.KeyLockAsync(MailSchema.Mailbox, ownerId, ct);

        string? name, address;
        Guid? existing, owner, owningUser, owningTeam, unit;
        int? ownerType, approval;
        await using (var cmd = s.Command(isUser
                         ? $"SELECT fullname, internalemailaddress, businessunitid, {MailSchema.ApprovalColumn}, {MailSchema.DefaultMailbox} FROM {s.DataTable(ownerTable)} WHERE {key} = @id"
                         : $"SELECT name, emailaddress, owningbusinessunit, {MailSchema.ApprovalColumn}, {MailSchema.DefaultMailbox}, ownerid, owneridtype, owninguser, owningteam " +
                           $"FROM {s.DataTable(ownerTable)} WHERE {key} = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, ownerId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"The {ownerTable} {ownerId} does not exist.");
            name = rd.IsDBNull(0) ? null : rd.GetString(0);
            address = rd.IsDBNull(1) ? null : rd.GetString(1);
            unit = rd.IsDBNull(2) ? null : rd.GetGuid(2);
            approval = rd.IsDBNull(3) ? null : Convert.ToInt32(rd.GetValue(3));
            existing = rd.IsDBNull(4) ? null : rd.GetGuid(4);
            owner = isUser || rd.IsDBNull(5) ? ownerId : rd.GetGuid(5);
            ownerType = isUser || rd.IsDBNull(6) ? null : Convert.ToInt32(rd.GetValue(6));
            owningUser = isUser ? ownerId : rd.IsDBNull(7) ? null : rd.GetGuid(7);
            owningTeam = isUser || rd.IsDBNull(8) ? null : rd.GetGuid(8);
        }
        if (existing is { } already) return (already, false);
        var (incoming, outgoing) = await DeliveryMethodsAsync(s, store, ownerTable, ownerId, ct);
        var profile = await DefaultProfileAsync(s, store, ct);

        var mailbox = Guid.NewGuid();
        var pending = !string.IsNullOrWhiteSpace(address) && (approval ?? MailSchema.ApprovalEmpty) == MailSchema.ApprovalEmpty;
        await using (var link = s.Command(
                         $"UPDATE {s.DataTable(ownerTable)} SET {s.Quote(MailSchema.DefaultMailbox)} = @mailbox" +
                         (pending ? $", {s.Quote(MailSchema.ApprovalColumn)} = {MailSchema.ApprovalPending}" : "") +
                         $" WHERE {s.Quote(key)} = @id AND {s.Quote(MailSchema.DefaultMailbox)} IS NULL"))
        {
            link.AddParameter("@mailbox", DbType.Guid, mailbox);
            link.AddParameter("@id", DbType.Guid, ownerId);
            if (await link.ExecuteNonQueryAsync(ct) == 0)
            {
                await using var again = s.Command($"SELECT {s.Quote(MailSchema.DefaultMailbox)} FROM {s.DataTable(ownerTable)} WHERE {s.Quote(key)} = @id");
                again.AddParameter("@id", DbType.Guid, ownerId);
                return (await again.ExecuteScalarAsync(ct) is Guid g ? g : mailbox, false);
            }
        }
        var ownerOtc = store?.GetEntity(ownerTable)?.ObjectTypeCode ?? (isUser ? 8 : 2020);
        var userOtc = store?.GetEntity("systemuser")?.ObjectTypeCode ?? 8;
        var d = s.Dialect;
        await using var insert = s.Command($"""
            INSERT INTO {s.DataTable(MailSchema.Mailbox)} (mailboxid, name, regardingobjectid, regardingobjectidtype, emailaddress,
                incomingemaildeliverymethod, outgoingemaildeliverymethod, enabledforincomingemail, enabledforoutgoingemail,
                incomingemailstatus, outgoingemailstatus, emailrouteraccessapproval, isemailaddressapprovedbyo365admin,
                testemailconfigurationscheduled, ispasswordset, ownerid, owneridtype, owninguser, owningteam, owningbusinessunit,
                createdon, createdby, modifiedon, modifiedby, statecode, statuscode, {MailSchema.ProfileColumn})
            VALUES (@mailbox, @name, @regarding, @otc, @address, @incoming, @outgoing, {d.BooleanLiteral(false)}, {d.BooleanLiteral(false)}, 0, 0, @approval,
                {d.BooleanLiteral(false)}, {d.BooleanLiteral(false)}, {d.BooleanLiteral(false)}, @owner, @ownertype, @owninguser, @owningteam, @bu,
                {d.UtcNow}, @by, {d.UtcNow}, @by, 0, 1, @profile)
            """);
        insert.AddParameter("@mailbox", DbType.Guid, mailbox);
        insert.AddParameter("@name", DbType.String, Truncate(string.IsNullOrWhiteSpace(name) ? ownerId.ToString() : name, 200), 200);
        insert.AddParameter("@regarding", DbType.Guid, ownerId);
        insert.AddParameter("@otc", DbType.Int32, ownerOtc);
        insert.AddParameter("@address", DbType.String, string.IsNullOrWhiteSpace(address) ? null : Truncate(address, 100), 100);
        insert.AddParameter("@approval", DbType.Int32, pending ? MailSchema.ApprovalPending : approval ?? MailSchema.ApprovalEmpty);
        insert.AddParameter("@owner", DbType.Guid, owner);
        insert.AddParameter("@ownertype", DbType.Int32, isUser ? userOtc : ownerType);
        insert.AddParameter("@owninguser", DbType.Guid, owningUser);
        insert.AddParameter("@owningteam", DbType.Guid, owningTeam);
        insert.AddParameter("@bu", DbType.Guid, unit);
        insert.AddParameter("@by", DbType.Guid, by ?? (isUser ? ownerId : null));
        insert.AddParameter("@incoming", DbType.Int32, incoming);
        insert.AddParameter("@outgoing", DbType.Int32, outgoing);
        insert.AddParameter("@profile", DbType.Guid, profile);
        await insert.ExecuteNonQueryAsync(ct);
        return (mailbox, true);
    }

    /// <summary>
    /// The owner's <c>incomingemaildeliverymethod</c> and <c>outgoingemaildeliverymethod</c>, which its mailbox mirrors (as Dataverse;
    /// final review I3): None (0) when the owner's table lacks the column (no store, or an organization older than the columns) or
    /// the value is empty.
    /// </summary>
    private static async Task<(int Incoming, int Outgoing)> DeliveryMethodsAsync(DataSession s, IMetadataStore? store, string ownerTable,
        Guid ownerId, CancellationToken ct)
    {
        var attributes = store?.GetEntity(ownerTable)?.Attributes;
        if (attributes is null || !MailSchema.DeliveryColumns.All(c => attributes.Any(a => a.LogicalName == c)))
            return (MailSchema.DeliveryNone, MailSchema.DeliveryNone);
        await using var cmd = s.Command(
            $"SELECT {string.Join(", ", MailSchema.DeliveryColumns.Select(s.Quote))} FROM {s.DataTable(ownerTable)} WHERE {s.Quote(KeyOf(ownerTable))} = @id");
        cmd.AddParameter("@id", DbType.Guid, ownerId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return (MailSchema.DeliveryNone, MailSchema.DeliveryNone);
        return (rd.IsDBNull(0) ? MailSchema.DeliveryNone : Convert.ToInt32(rd.GetValue(0)),
            rd.IsDBNull(1) ? MailSchema.DeliveryNone : Convert.ToInt32(rd.GetValue(1)));
    }

    /// <summary>
    /// The organization's <c>defaultemailserverprofileid</c> (final review I3), which a new mailbox gets; null when none is set, when
    /// the profile no longer exists, or when the column is not there (no store, or an organization older than the column).
    /// </summary>
    private static async Task<Guid?> DefaultProfileAsync(DataSession s, IMetadataStore? store, CancellationToken ct)
    {
        if (store?.GetEntity("organization")?.Attributes?.Any(a => a.LogicalName == MailSchema.DefaultProfileColumn) != true
            || store.GetEntity(MailSchema.EmailServerProfile) is null)
            return null;
        await using var cmd = s.Command(s.Dialect.Limit(
            $"SELECT p.emailserverprofileid FROM {s.DataTable("organization")} o JOIN {s.DataTable(MailSchema.EmailServerProfile)} p " +
            $"ON p.emailserverprofileid = o.{s.Quote(MailSchema.DefaultProfileColumn)}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
