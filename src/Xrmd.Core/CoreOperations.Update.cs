using System.Data;
using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the Update body on the model (Assign and SetState come through it with their own audit action, SendEmail with the values
/// the server adds). The SDK twins <c>UpdateAsync(... Entity ...)</c> and <c>UpdateForMessageAsync</c>
/// (<c>Sdk/CoreOperations.Records.Sdk.cs</c>) read the caller's entity into a record and run the same body; a helper of another step
/// that is still on the SDK is reached through <see cref="CoreSdkBridges"/>.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>
    /// What a message writes with its Update besides the caller's values (plan 2.11, SendEmail): read-only columns the server
    /// maintains (such as <c>email.senton</c>), as model values, set after the writable values are taken, and party lists the server
    /// fills (such as the sender), written with the caller's party lists but without the readability check of ruling Y2.
    /// </summary>
    internal sealed record ServerValues(IReadOnlyDictionary<string, object?> Attributes, IReadOnlyDictionary<string, RecordCollection> PartyLists);

    /// <summary>
    /// Writes only the given (updatable) attributes plus modifiedon/modifiedby. Read-only attributes are ignored, so a
    /// retrieved record can be sent back. A new ownerid also moves owninguser and owningbusinessunit. <paramref name="record"/> is
    /// validated in place (<see cref="ValidateRecord"/>) unless the body first drops a value from a copy of it (a committed file, an
    /// unchanged contact full name).
    /// </summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the record or a referenced record does not exist;
    /// DuplicateRecord (412) when a unique index refuses the new values.</exception>
    /// <remarks>With <paramref name="detectDuplicates"/>, the duplicate check of plan 2.15 runs on the stored row overlaid with the
    /// changes, when they touch a column a Published rule matches on (<see cref="Duplicates.DuplicateFinder.DemandNoDuplicateAsync"/>,
    /// 412 DuplicateRecordsFound).</remarks>
    public Task UpdateRecordAsync(DataSession s, Caller caller, Record record, CancellationToken ct, bool detectDuplicates = false) =>
        UpdateRecordAsync(s, caller, record, AuditActions.Update, null, NoMailSecrets, null, ct, detectDuplicates);

    /// <summary>
    /// <see cref="UpdateRecordAsync(DataSession, Caller, Record, CancellationToken, bool)"/> with the mail secrets the pipeline took out of
    /// the request's Target, claimed by the caller (<see cref="Pipeline.Mail.PendingMailSecrets.Claim"/>).
    /// </summary>
    internal Task UpdateRecordAsync(DataSession s, Caller caller, Record record, IReadOnlyDictionary<string, string?> claimedSecrets,
        CancellationToken ct, bool detectDuplicates = false) =>
        UpdateRecordAsync(s, caller, record, AuditActions.Update, null, claimedSecrets, null, ct, detectDuplicates);

    /// <summary>
    /// The Update of <paramref name="record"/> for a message other than Update (SendEmail): the same path, with the values the
    /// server adds (<paramref name="server"/>). No Update step fires: the message's own steps run around it.
    /// </summary>
    internal Task UpdateRecordForMessageAsync(DataSession s, Caller caller, Record record, int auditAction, ServerValues server,
        CancellationToken ct) =>
        UpdateRecordAsync(s, caller, record, auditAction, server, NoMailSecrets, null, ct);

    /// <summary>
    /// The Update of <paramref name="record"/>, audited with <paramref name="auditAction"/> (Update, Assign or Set State). An activity
    /// (plan 2.11) keeps its party rows and its <c>activitypointer</c> row in this session; a closed one changes only its state and
    /// status, unless assigned (A11, pre-flight C12).
    /// </summary>
    /// <param name="validate">The validation of the record the body writes, <see cref="ValidateRecord"/> when null: the SDK twins pass
    /// theirs, which also changes the caller's entity as the validation changed the record.</param>
    private async Task UpdateRecordAsync(DataSession s, Caller caller, Record record, int auditAction, ServerValues? server,
        IReadOnlyDictionary<string, string?> claimedSecrets, Func<Record, (TableDefinition, Dictionary<string, ColumnDefinition>)>? validate,
        CancellationToken ct, bool detectDuplicates = false)
    {
        ArgumentNullException.ThrowIfNull(record);
        // The Update of a CommitFileBlocksUpload carries the new file id: the only value a file column takes (ruling F4).
        var commit = CoreSdkBridges.PendingFileCommit(files, s, record);
        if (commit is not null) record = Without(record, commit.Attribute);
        record = await WithoutUnchangedFullNameAsync(s, record, ct);
        var (m, attrs) = validate is null ? ValidateRecord(record, isCreate: false) : validate(record);
        PostRules.DemandWritable(m, record, isCreate: false); // plan 3.5 UI-1g: posts, comments and postregarding are never updated
        if (IsContact(m)) DemandNoFullName(record);
        var pk = SystemAttributes.PrimaryIdName(m);
        var id = UpdateId(record, m, pk);
        await DemandRowUpdateAllowedAsync(s, caller, m.LogicalName!, id, ct, record);
        await DemandEntityNamesMatchStoredAsync(s, m, attrs, record, id, ct);

        // Plan 2.11: the party lists are written as activityparty rows after the activity row (the caller's record keeps them).
        var activity = ActivitySchema.IsActivityTable(m);
        var partyLists = activity ? ActivityParties.ExtractPartyLists(m, record = Copy(record)) : null;
        if (partyLists is not null) await DemandPartiesReadableAsync(s, caller, partyLists, ct);
        if (partyLists is not null && server is { PartyLists.Count: > 0 })
        {
            var merged = new Dictionary<string, RecordCollection>(partyLists, StringComparer.Ordinal);
            foreach (var (name, list) in server.PartyLists) merged[name] = list;
            partyLists = merged;
        }
        var noteBody = ExtractNoteBody(m, ref record);
        var mailSecrets = CoreSdkBridges.TakeMailSecrets(m, ref record, claimedSecrets);
        var row = WritableValues(record, attrs, isCreate: false);
        if (server is not null)
            foreach (var (name, value) in server.Attributes)
                if (attrs.ContainsKey(name)) row[name] = value; // null clears the column
        if (m.LogicalName == MailSchema.EmailServerProfile && ProfileRules.Columns.Any(row.Contains))
            CoreSdkBridges.ApplyProfileRules(row, await RetrieveRecordAsync(s, Caller.Internal, m.LogicalName, id,
                new ColumnSelection { Columns = [.. ProfileRules.Columns, "name"] }, ct));
        CoreSdkBridges.DemandSupportedDeliveryMethods(row, m.LogicalName!);
        CoreSdkBridges.DemandSupportedOrganizationColumns(row, m.LogicalName!);
        if (m.LogicalName == MailSchema.Mailbox && caller != Caller.Internal)
        {
            var storedMailbox = await RetrieveRecordAsync(s, Caller.Internal, m.LogicalName, id,
                new ColumnSelection { Columns = [.. MailSchema.MailboxServerColumns, "regardingobjectid"] }, ct);
            CoreSdkBridges.DemandMailboxWritable(row, storedMailbox, caller);
        }
        // Plan 4.1c: changed incoming settings send incomingemailstatus back to Not Run; the sync state is then forgotten after the UPDATE.
        var incomingReset = false;
        if (m.LogicalName == MailSchema.Mailbox)
        {
            var statusSent = row.Contains("incomingemailstatus");
            await CoreSdkBridges.ApplyIncomingChangeAsync(s, row, id, mailSecrets, ct);
            incomingReset = !statusSent && row.Contains("incomingemailstatus");
        }
        // Plan 4.1b: a changed address of a user or queue sends its approval back to Pending Approval (the mailbox follows after the UPDATE).
        var mailboxTracked = m.LogicalName is SystemUser or QueueSchema.Queue && !MigrationScope.IsActive && store.GetTable(MailSchema.Mailbox) is not null;
        var addressChanged = mailboxTracked && await CoreSdkBridges.ApplyAddressChangeAsync(s, m.LogicalName!, id, row, ct);
        var deliveryChanged = mailboxTracked ? await CoreSdkBridges.ChangedDeliveryColumnsAsync(s, m.LogicalName!, id, row, ct) : null;
        ApplyIsManaged(m, attrs, record, row, caller, isCreate: false);
        await ApplyAccessTeamRulesAsync(s, m, attrs, row, isCreate: false, id, ct);
        await DuplicateRuleRules.ApplyServerColumnsAsync(s, store, m.LogicalName!, row, isCreate: false, id, ct);
        // #167 (ruling E2): unique and fixed definition names, one value per definition, the value's default name.
        await EnvironmentVariableRules.ApplyRowAsync(s, m.LogicalName!, row, isCreate: false, id, ct);
        await OrganizationSettingRules.ApplyRowAsync(s, m.LogicalName!, row, isCreate: false, id, ct);
        CompleteStateAndStatus(m, attrs, row, id);
        if (activity && !MigrationScope.IsActive)
            await ApplyActivityStateOnUpdateAsync(s, m, attrs, row, partyLists!, commit?.Attribute, id, auditAction, server?.Attributes.Keys, ct);
        // Plan 2.10, final review I1: the currency rules on the final row (after the plug-ins), in the transaction.
        await CurrencyRules.EnforceRowAsync(row, id, isCreate: false, store, s, ct);
        SetIfDefined(row, attrs, "modifiedon", DateTime.UtcNow);
        SetIfDefined(row, attrs, "modifiedby", Reference(SystemUser, caller.UserId));
        // Migration spec §7.5: inside a migration scope the record keeps the system columns it carries.
        var migrated = MigrationScope.IsActive ? MigratedSystemColumns(record, attrs) : null;
        if (migrated is not null) detectDuplicates = false;
        if (row.Contains(OwnerId))
            await ApplyOwnerAsync(s, row, attrs,
                row.GetValue<RecordReference>(OwnerId)
                ?? throw XrmdFaultException.InvalidArgument($"Attribute '{OwnerId}' of entity '{m.LogicalName}' cannot be null."),
                caller, ct);
        await ApplyBusinessUnitOnUpdateAsync(s, m.LogicalName!, id, row, attrs, ct);
        if (migrated is not null) foreach (var (name, value) in migrated) row[name] = value;
        if (m.LogicalName == SystemUser && row.GetValue<RecordReference>(ParentSystemUserId) is { } manager)
            await EnsureNoManagerLoopAsync(s, id, manager.Id, ct);
        if (IsContact(m)) await ApplyFullNameAsync(s, row, id, isCreate: false, ct);
        await CurrencyStamp.ApplyAsync(s, caller, m, attrs, row, id, isCreate: false, ct);

        await CoreSdkBridges.ApplyLookupPartitionsAsync(s, store, attrs, m, row, ct);
        await EnsureReferencesExistAsync(s, row, attrs, ct);
        // Plan 4.1a (ruling R15): an item reactivated, or pointed at another record, keeps one active item per record.
        if (m.LogicalName == QueueSchema.QueueItem && !MigrationScope.IsActive)
            await QueueMessages.DemandItemUpdateAllowedAsync(s, id, row, ct);
        // A file column set to null deletes its file (ruling F4); the commit's column gets its file id and name.
        await CoreSdkBridges.ApplyFilesOnUpdateAsync(files, s, m, attrs, id, row, commit, ct);
        // An image set to a byte[] or null (ruling F4), as on Create; null also releases the full image.
        await CoreSdkBridges.ApplyImagesAsync(files, s, m, attrs, id, row, isCreate: false, ct);
        // Plan 2.15 (ruling D2, RF1): before the UPDATE, on the stored row overlaid with the changes.
        if (detectDuplicates) await CoreSdkBridges.DemandNoDuplicateAsync(DuplicateFinder, s, m, row, id, row.Attributes.Keys.ToList(), ct);
        if (m.LogicalName == ActivitySchema.Annotation) await ApplyNoteBodyAsync(s, m, attrs, row, id, noteBody, isCreate: false, ct);
        if (m.LogicalName == MailSchema.Attachment) await ApplyAttachmentAsync(s, m, attrs, row, id, noteBody, isCreate: false, ct);

        // The audited columns this Update writes, read before it under an update lock (only when the table is audited).
        string[] audited = [];
        Record? before = null;
        if (await CoreSdkBridges.IsAuditedAsync(audit, s, m, ct))
        {
            audited = CoreSdkBridges.AuditedColumns(m).Where(row.Contains).ToArray();
            if (audited.Length > 0 && await LockRowAsync(s, m, id, ct)) before = await AuditImageAsync(s, m, id, audited, ct);
        }

        var parameters = EntityRowMapper.ToParameters(row, attrs, store.GetTable).ToList();
        var sql = $"UPDATE {s.DataTable(m.LogicalName!)} " +
                  $"SET {string.Join(", ", parameters.Select(p => $"{s.Quote(p.ParameterName[1..])} = {p.ParameterName}"))} " +
                  $"WHERE {s.Quote(pk)} = {IdParameter}";
        parameters.Add(new DetachedParameter(IdParameter, DbType.Guid, id));
        int updated;
        try
        {
            updated = await ExecuteNonQueryAsync(s, sql, parameters, ct);
        }
        catch (DbException ex) when (IsUniqueIndexViolation(s, ex))
        {
            throw DuplicateUniqueValue(m.LogicalName!, row);
        }
        if (updated == 0) throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName!, id);
        await MailSecrets.StoreAsync(s, id, mailSecrets, ct);
        // Plan 4.1b, task 7: testemailconfigurationscheduled true queues the mailbox's test, in this transaction.
        if (!MigrationScope.IsActive) await CoreSdkBridges.EnqueueMailboxTestAsync(s, caller, JobQueue, m.LogicalName!, id, row, ct);
        // Plan 4.1c, task 9: another server or account may be read now: the next sync starts again from processemailreceivedafter (the
        // message keys keep what was imported from coming in twice), and a pass still running writes nothing (final review M2).
        if (incomingReset) await MailboxSyncState.ResetAsync(s, id, ct);
        if (mailboxTracked) await CoreSdkBridges.SyncMailboxAsync(s, m.LogicalName!, id, row, addressChanged, store, deliveryChanged, ct);
        if (writes is not null && !MigrationScope.IsActive) await writes.OnWrittenAsync(s, m.LogicalName!, id, ct); // plan 2.14 (W10)
        if (activity) await WriteActivityRowsAsync(s, m, id, partyLists!, isCreate: false, ct);
        // Plan 4.1a: a queue-enabled record that leaves the active state deactivates its active items, in this transaction.
        if (m.IsValidForQueue?.Value == true && !MigrationScope.IsActive && row.GetValue<ChoiceValue>("statecode") is { Value: not 0 })
            await QueueMessages.DeactivateItemsAsync(s, caller, m, id, row.GetValue<ChoiceValue>("statuscode")?.Value, ct);
        // Plan 2.15: the row's match codes when a column a rule matches on changed (Assign and SetState come through here too);
        // the organization switch turned off unpublishes every rule (ruling D11), in this transaction.
        InvalidateDuplicateRulesOnWrite(s, m.LogicalName);
        await CoreSdkBridges.RefreshMatchCodesAsync(duplicates.MatchCodes, s, m, id, row.Attributes.Keys, ct);
        if (m.LogicalName == Organization && row.Contains(DuplicateDetectionSchema.OrgEnabled) && row[DuplicateDetectionSchema.OrgEnabled] is not true)
            await duplicates.UnpublishAllAsync(s, null, ct);
        // A cancelled publish job (the only Update of a system job) puts its rule back to Unpublished at once (D10).
        if (m.LogicalName == AsyncOperation && row.Contains("statecode")) await duplicates.OnJobEndedAsync(s, id, ct);
        // Plan 2.10, ruling X2: the base currency's symbol and precision are copied to the organization in this transaction.
        if (m.LogicalName == CurrencySchema.Table && (row.Contains(CurrencySchema.Symbol) || row.Contains(CurrencySchema.Precision)))
            await CurrencyMessages.CopyBaseCurrencyAsync(s, id, ct);
        if (CurrencyMessages.InvalidatesCatalog(m.LogicalName!)) CurrencyMessages.InvalidateAfterTransaction(currencies, s);
        if (before is not null)
            await CoreSdkBridges.WriteAuditAsync(audit, s, caller, auditAction, AuditOperations.Update, m, id, before,
                await AuditImageAsync(s, m, id, audited, ct), ct);
        if (ComponentType.OfTable(m.LogicalName!) is { } componentType) await MarkChangedAsync(s, caller, componentType, id, ct);
        await MarkParentChangedAsync(s, caller, m.LogicalName, id, null, ct);
    }

    /// <summary>
    /// A retrieved contact sent back to Update carries its <c>fullname</c>: a value equal to the stored one is a no-op and is
    /// dropped (from a copy); a different one stays in the record, which the Update refuses (400).
    /// </summary>
    private async Task<Record> WithoutUnchangedFullNameAsync(DataSession s, Record record, CancellationToken ct)
    {
        if (record.LogicalName != ContactFullName.Contact || !record.Contains(ContactFullName.FullName)
            || store.GetTable(record.LogicalName) is not { } m || !IsContact(m)) return record;
        var id = record.Id != Guid.Empty ? record.Id : record.GetValue<Guid?>("contactid") ?? Guid.Empty;
        if (id == Guid.Empty) return record;
        await using var cmd = s.Command(
            $"SELECT fullname FROM {s.DataTable(ContactFullName.Contact)} WHERE contactid = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return record;
        var stored = rd.IsDBNull(0) ? null : rd.GetString(0);
        return Equals(record[ContactFullName.FullName], stored) ? Without(record, ContactFullName.FullName) : record;
    }
}
