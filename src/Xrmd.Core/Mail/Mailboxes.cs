using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Solution;

namespace Xrmd.Core.Mail;

/// <summary>
/// What the outgoing email needs to know about the mailbox of a sender (plan 4.1b): its address, profile, approval, switches and
/// last outgoing test.
/// </summary>
/// <param name="Id">The mailbox, <see cref="Guid.Empty"/> when the sender has none.</param>
/// <param name="Address">The mailbox's email address.</param>
/// <param name="ProfileId">The email server profile the mailbox uses.</param>
/// <param name="Approved">The owner's <c>emailrouteraccessapproval</c> is Approved.</param>
/// <param name="Enabled"><c>enabledforoutgoingemail</c>.</param>
/// <param name="DeliveryMethod"><c>outgoingemaildeliverymethod</c>.</param>
/// <param name="OutgoingStatus"><c>outgoingemailstatus</c>.</param>
/// <param name="Active">The mailbox's <c>statecode</c> is Active.</param>
public sealed record MailboxState(Guid Id, string? Address, Guid? ProfileId, bool Approved, bool Enabled, int DeliveryMethod,
    int OutgoingStatus, bool Active = true)
{
    /// <summary>The sender has no mailbox.</summary>
    public static readonly MailboxState None = new(Guid.Empty, null, null, false, false, MailSchema.DeliveryNone, MailSchema.StatusNotRun, false);

    /// <summary>Approved, a profile set, outgoing method Server-Side Synchronization, enabled, active, and the last outgoing test a Success.</summary>
    public bool CanSend => Id != Guid.Empty && Approved && ProfileId is not null && DeliveryMethod == MailSchema.DeliveryServerSide
                           && Enabled && Active && OutgoingStatus == MailSchema.StatusSuccess;
}

/// <summary>
/// What incoming email needs to know about a mailbox (plan 4.1c): its address, profile, approval, switches, last incoming test, the sync
/// start, and who owns it.
/// </summary>
/// <param name="Id">The mailbox.</param>
/// <param name="Address">The address of the mailbox's user or queue.</param>
/// <param name="ProfileId">The email server profile the mailbox uses.</param>
/// <param name="OwnerId">The mailbox's owner: a user, or a team (the owner of a team's queue).</param>
/// <param name="OwnerTable"><c>systemuser</c> or <c>team</c>.</param>
/// <param name="QueueId">The queue the mailbox belongs to; null for a user's mailbox.</param>
/// <param name="Approved">The address's <c>emailrouteraccessapproval</c> is Approved.</param>
/// <param name="Enabled"><c>enabledforincomingemail</c>.</param>
/// <param name="DeliveryMethod"><c>incomingemaildeliverymethod</c>.</param>
/// <param name="IncomingStatus"><c>incomingemailstatus</c>.</param>
/// <param name="ReceivedAfter"><c>processemailreceivedafter</c>: messages received before are not delivered.</param>
/// <param name="Filter">The incoming email filtering method: the queue's, or the user's <c>usersettings</c>; unset is the default
/// (<see cref="MailSchema.QueueFilterDefault"/>, <see cref="MailSchema.UserFilterDefault"/>).</param>
/// <param name="Active">The mailbox's <c>statecode</c> is Active.</param>
public sealed record MailboxReceiver(Guid Id, string? Address, Guid? ProfileId, Guid OwnerId, string OwnerTable, Guid? QueueId, bool Approved,
    bool Enabled, int DeliveryMethod, int IncomingStatus, DateTime? ReceivedAfter, int Filter, bool Active = true)
{
    /// <summary>Approved, a profile set, incoming method Server-Side Synchronization, enabled, active, and the last incoming test a Success.</summary>
    public bool CanReceive => Approved && ProfileId is not null && DeliveryMethod == MailSchema.DeliveryServerSide && Enabled && Active
                              && IncomingStatus == MailSchema.StatusSuccess;
}

/// <summary>
/// The mailbox of a user or a queue (plan 4.1b): created with its owner (disabled: delivery methods None, not enabled, statuses Not Run),
/// kept in step with the owner's name and address, deleted with a queue, and read to find out whether a sender can send.
/// </summary>
public static class Mailboxes
{
    /// <summary>
    /// The mailbox of a new <c>systemuser</c> or <c>queue</c> (<paramref name="owner"/>), in the transaction of <paramref name="s"/>:
    /// named after the owner, with its address, <c>regardingobjectid</c> the owner and <c>defaultmailbox</c> of the owner pointing to it.
    /// An owner that already has one keeps it.
    /// </summary>
    /// <returns>The mailbox id.</returns>
    public static async Task<Guid> InsertForAsync(DataSession s, Caller caller, EntityReference owner, CancellationToken ct,
        IMetadataStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.LogicalName is not ("systemuser" or QueueSchema.Queue))
            throw new ArgumentException($"A mailbox belongs to a systemuser or a queue, not to '{owner.LogicalName}'.", nameof(owner));
        var by = caller == Caller.Internal ? (Guid?)null : caller.UserId;
        return (await MailboxRows.EnsureAsync(s, store, owner.LogicalName, owner.Id, by, ct)).Id;
    }

    /// <summary>
    /// The mailbox that sends for <paramref name="from"/> (a <c>systemuser</c> or <c>queue</c> party), or <see cref="MailboxState.None"/>.
    /// The approval is the owner's.
    /// </summary>
    public static async Task<MailboxState> ResolveSenderAsync(DataSession s, EntityReference from, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(from);
        if (from.LogicalName is not ("systemuser" or QueueSchema.Queue)) return MailboxState.None;
        // The owner's own pointer picks the mailbox, and the address is the owner's (the mailbox copy cannot diverge).
        var addressColumn = AddressColumn(from.LogicalName)!;
        await using var cmd = s.Command($"""
            SELECT m.mailboxid, o.{addressColumn}, m.emailserverprofile, o.{MailSchema.ApprovalColumn}, m.enabledforoutgoingemail,
                m.outgoingemaildeliverymethod, m.outgoingemailstatus, m.statecode
            FROM {s.DataTable(from.LogicalName)} o JOIN {s.DataTable(MailSchema.Mailbox)} m ON m.mailboxid = o.{MailSchema.DefaultMailbox}
            WHERE o.{MailboxRows.KeyOf(from.LogicalName)} = @id
            """);
        cmd.AddParameter("@id", DbType.Guid, from.Id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return MailboxState.None;
        return new MailboxState(rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetGuid(2),
            !rd.IsDBNull(3) && Convert.ToInt32(rd.GetValue(3)) == MailSchema.ApprovalApproved,
            !rd.IsDBNull(4) && Convert.ToBoolean(rd.GetValue(4)),
            rd.IsDBNull(5) ? MailSchema.DeliveryNone : Convert.ToInt32(rd.GetValue(5)),
            rd.IsDBNull(6) ? MailSchema.StatusNotRun : Convert.ToInt32(rd.GetValue(6)),
            rd.IsDBNull(7) || Convert.ToInt32(rd.GetValue(7)) == 0);
    }

    /// <summary>The mailbox <paramref name="mailboxId"/> as incoming email sees it, or null when there is none.</summary>
    public static async Task<MailboxReceiver?> ResolveReceiverAsync(DataSession s, Guid mailboxId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // The mailbox's user or queue (the ids never collide) gives the address, approval and filtering.
        await using var cmd = s.Command($"""
            SELECT m.mailboxid, COALESCE(u.internalemailaddress, q.emailaddress, m.emailaddress), m.emailserverprofile,
                COALESCE(u.{MailSchema.ApprovalColumn}, q.{MailSchema.ApprovalColumn}), m.enabledforincomingemail, m.incomingemaildeliverymethod,
                m.incomingemailstatus, m.statecode, m.processemailreceivedafter, m.owningteam, m.owninguser, m.ownerid, q.queueid,
                q.{MailSchema.FilteringMethodColumn}, us.{MailSchema.FilteringMethodColumn}
            FROM {s.DataTable(MailSchema.Mailbox)} m
            LEFT JOIN {s.DataTable("systemuser")} u ON u.systemuserid = m.regardingobjectid
            LEFT JOIN {s.DataTable(QueueSchema.Queue)} q ON q.queueid = m.regardingobjectid
            LEFT JOIN {s.DataTable("usersettings")} us ON us.systemuserid = u.systemuserid
            WHERE m.mailboxid = @id
            """);
        cmd.AddParameter("@id", DbType.Guid, mailboxId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var team = rd.IsDBNull(9) ? (Guid?)null : rd.GetGuid(9);
        var owner = team ?? (rd.IsDBNull(10) ? rd.IsDBNull(11) ? Guid.Empty : rd.GetGuid(11) : rd.GetGuid(10));
        var queue = rd.IsDBNull(12) ? (Guid?)null : rd.GetGuid(12);
        var filter = queue is not null
            ? rd.IsDBNull(13) ? MailSchema.QueueFilterDefault : Convert.ToInt32(rd.GetValue(13))
            : rd.IsDBNull(14) ? MailSchema.UserFilterDefault : Convert.ToInt32(rd.GetValue(14));
        return new MailboxReceiver(rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetGuid(2), owner,
            team is null ? "systemuser" : "team", queue, !rd.IsDBNull(3) && Convert.ToInt32(rd.GetValue(3)) == MailSchema.ApprovalApproved,
            !rd.IsDBNull(4) && Convert.ToBoolean(rd.GetValue(4)),
            rd.IsDBNull(5) ? MailSchema.DeliveryNone : Convert.ToInt32(rd.GetValue(5)),
            rd.IsDBNull(6) ? MailSchema.StatusNotRun : Convert.ToInt32(rd.GetValue(6)),
            rd.IsDBNull(8) ? null : DateTime.SpecifyKind(rd.GetDateTime(8), DateTimeKind.Utc), filter,
            rd.IsDBNull(7) || Convert.ToInt32(rd.GetValue(7)) == 0);
    }

    /// <summary>
    /// Who delivers the incoming email of <paramref name="m"/> (plan 4.1c, the contact center contract): the mailbox's owning user, or, for a
    /// mailbox owned by a team, that team's <c>administratorid</c>; with the user's business unit.
    /// </summary>
    /// <exception cref="MailSetupException">The team has no administrator, or the user does not exist or is disabled.</exception>
    public static async Task<Caller> DeliveryUserAsync(DataSession s, MailboxReceiver m, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        var user = m.OwnerId;
        if (m.OwnerTable == "team")
        {
            await using var team = s.Command($"SELECT administratorid FROM {s.DataTable("team")} WHERE teamid = @id");
            team.AddParameter("@id", DbType.Guid, m.OwnerId);
            user = await team.ExecuteScalarAsync(ct) as Guid? ?? throw new MailSetupException(
                $"The team {m.OwnerId} that owns the mailbox has no administrator, so its incoming email cannot be delivered; set the administrator of the team.");
        }
        await using var cmd = s.Command($"SELECT businessunitid, isdisabled FROM {s.DataTable("systemuser")} WHERE systemuserid = @id");
        cmd.AddParameter("@id", DbType.Guid, user);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw new MailSetupException($"The user {user} that delivers the incoming email of the mailbox does not exist.");
        if (!rd.IsDBNull(1) && Convert.ToBoolean(rd.GetValue(1)))
            throw new MailSetupException($"The user {user} that delivers the incoming email of the mailbox is disabled.");
        if (rd.IsDBNull(0)) throw new MailSetupException($"The user {user} that delivers the incoming email of the mailbox has no business unit.");
        return new Caller(user, rd.GetGuid(0));
    }

    /// <summary>
    /// Before an Update of a <c>mailbox</c> writes <paramref name="row"/>: a changed profile, user name, password (<paramref name="secrets"/>),
    /// incoming delivery method or <c>enabledforincomingemail</c> sends <c>incomingemailstatus</c> back to Not Run, as the mailbox must be
    /// tested again (unless the same write sets the status).
    /// </summary>
    public static async Task ApplyIncomingChangeAsync(DataSession s, Entity row, Guid id, IReadOnlyDictionary<string, string?> secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(secrets);
        if (row.Contains("incomingemailstatus")) return;
        var changed = secrets.ContainsKey("password");
        if (!changed && IncomingSettingColumns.Any(row.Contains))
        {
            await using var cmd = s.Command($"SELECT emailserverprofile, username, incomingemaildeliverymethod, enabledforincomingemail FROM {s.DataTable(MailSchema.Mailbox)} WHERE mailboxid = @id");
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                object?[] stored =
                [
                    rd.IsDBNull(0) ? null : rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1) is { Length: > 0 } n ? n : null,
                    rd.IsDBNull(2) ? MailSchema.DeliveryNone : Convert.ToInt32(rd.GetValue(2)), !rd.IsDBNull(3) && Convert.ToBoolean(rd.GetValue(3)),
                ];
                for (var i = 0; i < IncomingSettingColumns.Length && !changed; i++)
                    if (row.Contains(IncomingSettingColumns[i]))
                    {
                        var sent = Normal(row[IncomingSettingColumns[i]]);
                        changed = !Equals(i is 2 ? sent ?? MailSchema.DeliveryNone : i is 3 ? sent ?? false : sent, stored[i]);
                    }
            }
        }
        if (changed) row["incomingemailstatus"] = new OptionSetValue(MailSchema.StatusNotRun);
    }

    private static readonly string[] IncomingSettingColumns = ["emailserverprofile", "username", "incomingemaildeliverymethod", "enabledforincomingemail"];

    /// <summary>
    /// Refuses an incoming or outgoing delivery method of Outlook (1) or Forward Mailbox (3) on <c>mailbox</c>, <c>queue</c> and
    /// <c>systemuser</c>: xrmverse synchronizes server-side only (spec section 9).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c>.</exception>
    public static void DemandSupportedDeliveryMethods(Entity row, string table)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (table is not (MailSchema.Mailbox or "queue" or "systemuser")) return;
        foreach (var column in MailSchema.DeliveryColumns)
            if (row.Attributes.TryGetValue(column, out var raw) && raw is OptionSetValue { Value: MailSchema.DeliveryOutlook or MailSchema.DeliveryForwardMailbox } value)
                throw XrmdFaultException.InvalidArgument(
                    $"{(value.Value == MailSchema.DeliveryOutlook ? "Outlook" : "Forward Mailbox")} ({value.Value}) is not a supported value of '{column}' on '{table}'; " +
                    "use None (0) or Server-Side Synchronization (2).");
    }

    /// <summary>
    /// Ruling P7: the address and the statuses of a mailbox are the server's. A write that changes one of
    /// <see cref="MailSchema.MailboxServerColumns"/> is a 400 (the address: change the owner's), unless it comes from the server itself;
    /// a value sent back as stored is no write.
    /// </summary>
    /// <param name="stored">The stored row (null on a Create).</param>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c>.</exception>
    public static void DemandNotServerMaintained(Entity row, Entity? stored, Caller caller)
    {
        if (caller == Caller.Internal) return;
        foreach (var column in MailSchema.MailboxServerColumns.Where(row.Contains))
        {
            if (Normal(row[column]) is null && stored is null) continue;
            if (stored is not null && Equals(Normal(row[column]), Normal(stored.Contains(column) ? stored[column] : null))) continue;
            throw XrmdFaultException.InvalidArgument(column switch
            {
                "emailaddress" => "The address of a mailbox follows its owner: change the email address of the user or the queue instead.",
                MailSchema.ApprovalColumn => "The approval of a mailbox follows its owner: approve the user or the queue (emailrouteraccessapproval) instead.",
                _ => $"'{column}' of a mailbox is maintained by the server and cannot be written.",
            });
        }
    }

    /// <summary>Ruling P7: the owner of a mailbox is not changed by an Update.</summary>
    public static void DemandOwnerUnchanged(Entity row, Entity stored)
    {
        if (row.Contains("regardingobjectid") && !Equals(Normal(row["regardingobjectid"]), Normal(stored.Contains("regardingobjectid") ? stored["regardingobjectid"] : null)))
            throw XrmdFaultException.InvalidArgument("The user or queue a mailbox belongs to cannot be changed.");
    }

    private static object? Normal(object? v) => v switch { OptionSetValue o => o.Value, EntityReference r => r.Id, string { Length: 0 } => null, var x => x };

    /// <summary>The column that holds the address of the owner's mailbox, or null for a table that has none.</summary>
    public static string? AddressColumn(string table) => table switch
    {
        "systemuser" => "internalemailaddress",
        QueueSchema.Queue => "emailaddress",
        _ => null,
    };

    /// <summary>The column that holds the name of the owner's mailbox.</summary>
    public static string NameColumn(string table) => table == "systemuser" ? "fullname" : "name";

    /// <summary>
    /// Before an Update of a <c>systemuser</c> or <c>queue</c> writes <paramref name="row"/>: when it changes the address, the owner's
    /// approval goes back to Pending Approval (Empty for no address), unless the same write sets it (a System Administrator's decision).
    /// </summary>
    /// <returns>True when the address changes.</returns>
    public static async Task<bool> ApplyAddressChangeAsync(DataSession s, string table, Guid id, Entity row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        if (AddressColumn(table) is not { } column || !row.Contains(column)) return false;
        string? stored;
        int? storedApproval;
        await using (var cmd = s.Command($"SELECT {s.Quote(column)}, {s.Quote(MailSchema.ApprovalColumn)} FROM {s.DataTable(table)} WHERE {s.Quote(MailboxRows.KeyOf(table))} = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            stored = Clean(rd.IsDBNull(0) ? null : rd.GetString(0));
            storedApproval = rd.IsDBNull(1) ? null : Convert.ToInt32(rd.GetValue(1));
        }
        var wanted = Clean(row[column] as string);
        if (string.Equals(stored, wanted, StringComparison.OrdinalIgnoreCase)) return false;
        // A sent approval that is the stored one is a client sending its row back, not a decision (the guard lets only a System
        // Administrator change it): the reset applies.
        if (!row.Contains(MailSchema.ApprovalColumn) || (row[MailSchema.ApprovalColumn] as OptionSetValue)?.Value == (storedApproval ?? MailSchema.ApprovalEmpty))
            row[MailSchema.ApprovalColumn] = new OptionSetValue(wanted is null ? MailSchema.ApprovalEmpty : MailSchema.ApprovalPending);
        return true;
    }

    /// <summary>
    /// Before an Update of a <c>systemuser</c> or <c>queue</c> writes <paramref name="row"/>: the delivery method columns it sends with a
    /// value other than the stored one (an empty value counts as None). A column sent back unchanged is not a change, so it does not
    /// overwrite a delivery method set on the mailbox itself (<c>xrmd mailbox link</c>, the web admin, a PATCH of the mailbox).
    /// </summary>
    public static async Task<IReadOnlySet<string>> ChangedDeliveryColumnsAsync(DataSession s, string table, Guid id, Entity row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        var sent = MailSchema.DeliveryColumns.Where(row.Contains).ToList();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (sent.Count == 0) return changed;
        await using var cmd = s.Command(
            $"SELECT {string.Join(", ", sent.Select(s.Quote))} FROM {s.DataTable(table)} WHERE {s.Quote(MailboxRows.KeyOf(table))} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var found = await rd.ReadAsync(ct);
        for (var i = 0; i < sent.Count; i++)
        {
            var stored = !found || rd.IsDBNull(i) ? MailSchema.DeliveryNone : Convert.ToInt32(rd.GetValue(i));
            if (DeliveryValue(row[sent[i]]) != stored) changed.Add(sent[i]);
        }
        return changed;
    }

    private static int DeliveryValue(object? value) => value switch { OptionSetValue o => o.Value, int i => i, _ => MailSchema.DeliveryNone };

    /// <summary>
    /// After an Update of a <c>systemuser</c> or <c>queue</c> wrote <paramref name="row"/>: the mailbox follows the owner's name,
    /// address and incoming and outgoing delivery methods, and shows its approval; a new address also sends the mailbox's last tests
    /// back to Not Run. Whenever the incoming status goes back to Not Run (a new address, another incoming delivery method), the mailbox's
    /// sync state is reset (<see cref="MailboxSyncState.ResetAsync"/>).
    /// </summary>
    /// <param name="deliveryChanged">The delivery columns whose value differs from the owner's stored one
    /// (<see cref="ChangedDeliveryColumnsAsync"/>, read before the UPDATE); only these are copied to the mailbox.</param>
    public static async Task SyncAsync(DataSession s, string table, Guid id, Entity row, bool addressChanged, CancellationToken ct,
        IMetadataStore? store = null, IReadOnlySet<string>? deliveryChanged = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        var sets = new List<string>();
        var parameters = new List<(string Name, DbType Type, object? Value, int? Size)>();
        if (AddressColumn(table) is { } address && row.Contains(address))
        {
            sets.Add($"emailaddress = @address");
            parameters.Add(("@address", DbType.String, Clean(row[address] as string), 100));
        }
        if (row.Contains(NameColumn(table)) && row[NameColumn(table)] is string name && name.Length > 0)
        {
            sets.Add("name = @name");
            parameters.Add(("@name", DbType.String, name.Length > 200 ? name[..200] : name, 200));
        }
        if (row.Contains(MailSchema.ApprovalColumn) && row[MailSchema.ApprovalColumn] is OptionSetValue approval)
        {
            sets.Add($"{MailSchema.ApprovalColumn} = @approval");
            parameters.Add(("@approval", DbType.Int32, approval.Value, null));
        }
        // The delivery methods of the owner are its mailbox's (as Dataverse; final review I3): an empty one is None. Only a value that
        // changes the owner's stored one is copied (fix-wave N1): a write-back of the stored row leaves a mailbox linked directly alone.
        foreach (var column in MailSchema.DeliveryColumns.Where(c => row.Contains(c) && deliveryChanged?.Contains(c) == true))
        {
            sets.Add($"{column} = @{column}");
            parameters.Add(($"@{column}", DbType.Int32, DeliveryValue(row[column]), null));
        }
        var incomingReset = addressChanged || deliveryChanged?.Contains("incomingemaildeliverymethod") == true;
        if (addressChanged) sets.Add("incomingemailstatus = 0, outgoingemailstatus = 0");
        else if (incomingReset) sets.Add("incomingemailstatus = 0");
        // A queue's mailbox follows the queue's owner (an Assign, or an Update of ownerid): the old owner loses the credentials.
        if (table == QueueSchema.Queue && row.Contains("ownerid") && row["ownerid"] is EntityReference owner)
        {
            var team = owner.LogicalName == "team";
            sets.Add("ownerid = @owner, owneridtype = @ownertype, owninguser = @owninguser, owningteam = @owningteam");
            parameters.Add(("@owner", DbType.Guid, owner.Id, null));
            parameters.Add(("@ownertype", DbType.Int32, store?.GetEntity(owner.LogicalName)?.ObjectTypeCode ?? (team ? 9 : 8), null));
            parameters.Add(("@owninguser", DbType.Guid, team ? null : owner.Id, null));
            parameters.Add(("@owningteam", DbType.Guid, team ? owner.Id : null, null));
            if (row.GetAttributeValue<EntityReference>("owningbusinessunit") is { } unit)
            {
                sets.Add("owningbusinessunit = @bu");
                parameters.Add(("@bu", DbType.Guid, unit.Id, null));
            }
        }
        if (sets.Count == 0) return;
        await using var cmd = s.Command($"UPDATE {s.DataTable(MailSchema.Mailbox)} SET {string.Join(", ", sets)} WHERE regardingobjectid = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        foreach (var (name2, type, value, size) in parameters)
            if (size is { } n) cmd.AddParameter(name2, type, value, n);
            else cmd.AddParameter(name2, type, value);
        await cmd.ExecuteNonQueryAsync(ct);
        if (!incomingReset) return;
        // Final review I3: back to Not Run, the sync state goes too (the UPDATE above holds the mailboxes' row locks), so a mailbox tested
        // again does not resume from a cursor of the old address or method.
        var mailboxes = new List<Guid>();
        await using (var select = s.Command($"SELECT mailboxid FROM {s.DataTable(MailSchema.Mailbox)} WHERE regardingobjectid = @id"))
        {
            select.AddParameter("@id", DbType.Guid, id);
            await using var rd = await select.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) mailboxes.Add(rd.GetGuid(0));
        }
        foreach (var mailbox in mailboxes) await MailboxSyncState.ResetAsync(s, mailbox, ct);
    }

    /// <summary>
    /// The mailboxes of a queue about to be deleted, with the queue's <c>defaultmailbox</c> cleared first (nothing points to them then).
    /// The caller deletes them, secrets included.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> DetachAsync(DataSession s, string ownerTable, Guid ownerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var ids = new List<Guid>();
        await using (var cmd = s.Command($"SELECT mailboxid FROM {s.DataTable(MailSchema.Mailbox)} WHERE regardingobjectid = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, ownerId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        }
        if (ids.Count == 0) return ids;
        await using var clear = s.Command(
            $"UPDATE {s.DataTable(ownerTable)} SET {s.Quote(MailSchema.DefaultMailbox)} = NULL WHERE {s.Quote(MailboxRows.KeyOf(ownerTable))} = @id");
        clear.AddParameter("@id", DbType.Guid, ownerId);
        await clear.ExecuteNonQueryAsync(ct);
        return ids;
    }

    private static string? Clean(string? address) => string.IsNullOrWhiteSpace(address) ? null : address.Trim();
}
