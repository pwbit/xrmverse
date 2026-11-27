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
using Xrmd.Solution.Layers;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the Create body on the model. The SDK twin <c>CreateAsync(... Entity ...)</c> (<c>Sdk/CoreOperations.Records.Sdk.cs</c>) reads
/// the caller's entity into a record and runs the same body; a helper of another step that is still on the SDK is reached through
/// <see cref="CoreSdkBridges"/>.
/// </summary>
public sealed partial class CoreOperations
{
    private static readonly IReadOnlyDictionary<string, string?> NoMailSecrets = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Inserts the record. Id: <see cref="Record.Id"/>, else the primary key attribute, else a new one. Sets
    /// createdon/modifiedon (UTC now), createdby/modifiedby (caller), owningbusinessunit, statecode 0/statuscode (the DefaultStatus of state 0, else 1) when
    /// not given and, for user-owned entities, ownerid (default: caller) with owninguser. An activity (plan 2.11) also gets its
    /// party rows and its <c>activitypointer</c> row in this session. With <paramref name="detectDuplicates"/>, the duplicate
    /// check of plan 2.15 runs on the values about to be inserted (<see cref="Duplicates.DuplicateFinder.DemandNoDuplicateAsync"/>).
    /// <paramref name="record"/> is validated in place (<see cref="ValidateRecord"/>); the body then works on copies.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for invalid input; ObjectDoesNotExist (404) for a
    /// lookup or party to a missing record; DuplicateRecord (412) when the id is taken or a unique index refuses a value;
    /// DuplicateRecordsFound (412) when the duplicate check finds a duplicate.</exception>
    /// <param name="serverSetValues">Plan 4.1c: columns the server writes besides the caller's values (such as <c>email.senton</c> of
    /// an incoming email), as model values, set after <paramref name="record"/> is validated, so a column not valid for Create may be
    /// among them; the caller's record never holds them. Null for a caller's Create.</param>
    /// <exception cref="InvalidOperationException"><paramref name="serverSetValues"/> names a column the table does not have.</exception>
    public Task<Guid> CreateRecordAsync(DataSession s, Caller caller, Record record, CancellationToken ct, bool detectDuplicates = false,
        IReadOnlyDictionary<string, object?>? serverSetValues = null) =>
        CreateRecordAsync(s, caller, record, NoMailSecrets, ct, detectDuplicates, serverSetValues);

    /// <summary>
    /// <see cref="CreateRecordAsync(DataSession, Caller, Record, CancellationToken, bool, IReadOnlyDictionary{string, object?}?)"/> with
    /// the mail secrets the pipeline took out of the request's Target, claimed by the caller
    /// (<see cref="CoreSdkBridges.ClaimMailSecrets(Record, Abstractions.Sdk.SdkMirror?)"/>).
    /// </summary>
    internal Task<Guid> CreateRecordAsync(DataSession s, Caller caller, Record record, IReadOnlyDictionary<string, string?> claimedSecrets,
        CancellationToken ct, bool detectDuplicates = false, IReadOnlyDictionary<string, object?>? serverSetValues = null)
    {
        var (m, attrs) = ValidateRecord(record, isCreate: true);
        return CreateValidatedAsync(s, caller, m, attrs, record, claimedSecrets, ct, detectDuplicates, serverSetValues);
    }

    /// <summary>The Create of <paramref name="record"/>, validated (<see cref="ValidateRecord"/>) as <paramref name="m"/>.</summary>
    private async Task<Guid> CreateValidatedAsync(DataSession s, Caller caller, TableDefinition m, Dictionary<string, ColumnDefinition> attrs,
        Record record, IReadOnlyDictionary<string, string?> claimedSecrets, CancellationToken ct, bool detectDuplicates,
        IReadOnlyDictionary<string, object?>? serverSetValues)
    {
        var validated = record;
        // Plan 3.5 UI-1g: the refusals of the post tables, on the values after the plug-ins.
        PostRules.DemandWritable(m, record, isCreate: true);
        if (!ManagedProperties.Bypassed(caller) && ComponentChildren.TryGetValue(m.LogicalName!, out var parentOfNew)
            && record.Attributes.TryGetValue(parentOfNew.Column, out var parentValue) && parentValue is RecordReference parentRef)
            await DemandParentCustomizableAsync(s, m.LogicalName!, parentRef.Id, ct);
        if (!ManagedProperties.Bypassed(caller)) await DemandTableAllowsAsync(s, record, ct);
        var pk = SystemAttributes.PrimaryIdName(m);
        var id = record.Id != Guid.Empty ? record.Id
            : record.GetValue<Guid?>(pk) is { } given && given != Guid.Empty ? given
            : Guid.NewGuid();

        // Plan 2.11: the party lists are written as activityparty rows after the activity row (the caller's record keeps them).
        var activity = ActivitySchema.IsActivityTable(m);
        var partyLists = activity ? ActivityParties.ExtractPartyLists(m, record = Copy(record)) : null;
        if (partyLists is not null) await DemandPartiesReadableAsync(s, caller, partyLists, ct);
        // Plan 2.11 A16: a note body has no column; it is stored in the file store before the note's row is written.
        var noteBody = ExtractNoteBody(m, ref record);
        // Plan 4.1b: the write-only mail secrets have no column; they are stored after the row is written.
        var mailSecrets = CoreSdkBridges.TakeMailSecrets(m, ref record, claimedSecrets);
        var row = WritableValues(record, attrs, isCreate: true);
        if (serverSetValues is not null)
            foreach (var (name, value) in serverSetValues)
                row[name] = attrs.ContainsKey(name)
                    ? value
                    : throw new InvalidOperationException($"The server value '{name}' is not a column of '{m.LogicalName}'.");
        row[pk] = id;
        CoreSdkBridges.ApplyProfileRules(row, null);
        CoreSdkBridges.DemandSupportedDeliveryMethods(row, m.LogicalName!);
        CoreSdkBridges.DemandSupportedOrganizationColumns(row, m.LogicalName!);
        // Migration spec §7.5: inside a migration scope the record keeps the system columns it carries.
        var migrated = MigrationScope.IsActive ? MigratedSystemColumns(record, attrs) : null;
        if (migrated is not null) detectDuplicates = false;
        // Plan 2.10, final review I1: the currency rules on the final row (after the plug-ins), in the transaction.
        await CoreSdkBridges.EnforceCurrencyRulesAsync(row, id, isCreate: true, store, s, ct);
        await ApplyAccessTeamRulesAsync(s, m, attrs, row, isCreate: true, id, ct);
        await CoreSdkBridges.ApplyDuplicateRuleColumnsAsync(s, store, m.LogicalName!, row, isCreate: true, id, ct);
        // #167 (ruling E2): unique and fixed definition names, one value per definition, the value's default name.
        await CoreSdkBridges.ApplyEnvironmentVariableRowAsync(s, m.LogicalName!, row, isCreate: true, id, ct);
        await CoreSdkBridges.ApplyOrganizationSettingRowAsync(s, m.LogicalName!, row, isCreate: true, id, ct);
        var now = DateTime.UtcNow;
        var callerRef = Reference(SystemUser, caller.UserId);
        SetIfDefined(row, attrs, "createdon", now);
        // #343.5 (Learn, "Import auditing data"): a given overriddencreatedon becomes createdon; the column keeps the time of the Create.
        // A migration then puts back the source's createdon and overriddencreatedon (MigrationSystemColumns).
        if (row.GetValue<DateTime?>(SystemAttributes.OverriddenCreatedOnName) is { } original && attrs.ContainsKey("createdon"))
        {
            row["createdon"] = original;
            row[SystemAttributes.OverriddenCreatedOnName] = now;
        }
        SetIfDefined(row, attrs, "modifiedon", now);
        SetIfDefined(row, attrs, "createdby", callerRef);
        SetIfDefined(row, attrs, "modifiedby", callerRef);
        SetIfDefined(row, attrs, "owningbusinessunit", Reference(BusinessUnit, caller.BusinessUnitId));
        ApplyIsManaged(m, attrs, record, row, caller, isCreate: true);
        if (activity) ApplyActivityStateOnCreate(m, attrs, row, id, now);
        if (row.GetValue<ChoiceValue>("statecode") is null) SetIfDefined(row, attrs, "statecode", new ChoiceValue(0));
        if (row.GetValue<ChoiceValue>("statuscode") is null) SetIfDefined(row, attrs, "statuscode", new ChoiceValue(DefaultStatus(attrs)));
        if (attrs.ContainsKey(OwnerId))
            await ApplyOwnerAsync(s, row, attrs, row.GetValue<RecordReference>(OwnerId) ?? callerRef, caller, ct);
        ApplyBusinessUnitOnCreate(m.LogicalName!, row, attrs, caller);
        if (migrated is not null) foreach (var (name, value) in migrated) row[name] = value;
        if (m.LogicalName == SystemUser && row.GetValue<RecordReference>(ParentSystemUserId)?.Id == id)
            throw XrmdFaultException.InvalidArgument(OwnManager);
        if (IsContact(m)) await ApplyFullNameAsync(s, row, id, isCreate: true, ct);
        await CurrencyStamp.ApplyAsync(s, caller, m, attrs, row, id, isCreate: true, ct);
        await ApplyAutoNumbersAsync(s, m, row, attrs, ct);
        // Plan 3.5 UI-1g: a post's regarding columns and postregarding row; a post's or comment's organization and delegate.
        await PostRules.ApplyCreateAsync(s, store, caller, m, attrs, row, ct);

        await CoreSdkBridges.ApplyLookupPartitionsAsync(s, store, attrs, m, row, ct);
        await EnsureReferencesExistAsync(s, row, attrs, ct);
        // Plan 4.1a (ruling R15): one active queue item per record (a received email excepted), under the record's lock. A migration
        // imports the source's items as they are.
        if (m.LogicalName == QueueSchema.QueueItem && migrated is null) await QueueMessages.DemandNewItemAllowedAsync(s, store, row, ct);
        // An image set to a byte[] becomes its thumbnail, image id, timestamp and (CanStoreFullImage) full image (ruling F4).
        await CoreSdkBridges.ApplyImagesAsync(files, s, m, attrs, id, row, isCreate: true, ct);
        // Plan 2.15 (ruling D2): before the INSERT, so a refused Create writes neither the row nor its match codes (RF3).
        if (detectDuplicates) await CoreSdkBridges.DemandNoDuplicateAsync(DuplicateFinder, s, m, row, id, null, ct);
        if (m.LogicalName == ActivitySchema.Annotation) await ApplyNoteBodyAsync(s, m, attrs, row, id, noteBody, isCreate: true, ct);
        if (m.LogicalName == MailSchema.Attachment) await ApplyAttachmentAsync(s, m, attrs, row, id, noteBody, isCreate: true, ct);

        // Plan 2.12b, ruling R7: a new row starts with its rollups not calculated (state 0, _date now, 0 or null).
        CoreSdkBridges.ApplyRollupInitialValues(rollups, m, row, now);
        var parameters = EntityRowMapper.ToParameters(row, attrs, store.GetTable);
        var sql = $"INSERT INTO {s.DataTable(m.LogicalName!)} " +
                  $"({string.Join(", ", parameters.Select(p => s.Quote(p.ParameterName[1..])))}) " +
                  $"VALUES ({string.Join(", ", parameters.Select(p => p.ParameterName))})";
        try
        {
            await ExecuteNonQueryAsync(s, sql, parameters, ct);
        }
        catch (DbException ex) when (s.Dialect.IsPrimaryKeyViolation(ex))
        {
            throw new XrmdFaultException(ErrorCodes.DuplicateRecord,
                $"A record that has the attribute values {pk} = {id} already exists on entity '{m.LogicalName}'.", 412);
        }
        catch (DbException ex) when (s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation
                                      && ex.Message.Contains(FieldSecurityTables.ShareUniqueIndex, StringComparison.Ordinal))
        {
            // A concurrent Create of the same field share passed the guard's check first (F6 rule 3).
            throw DuplicateShare(validated);
        }
        catch (DbException ex) when (IsUniqueIndexViolation(s, ex))
        {
            throw DuplicateUniqueValue(m.LogicalName!, row);
        }
        await MailSecrets.StoreAsync(s, id, mailSecrets, ct);
        // Plan 4.1b, task 7: testemailconfigurationscheduled true queues the mailbox's test, in this transaction.
        if (!MigrationScope.IsActive) await CoreSdkBridges.EnqueueMailboxTestAsync(s, caller, JobQueue, m.LogicalName!, id, row, ct);
        // Plan 2.15: the row's match codes, in this transaction (nothing is read when no rule matches the table).
        InvalidateDuplicateRulesOnWrite(s, m.LogicalName);
        await CoreSdkBridges.RefreshMatchCodesAsync(duplicates.MatchCodes, s, m, id, null, ct);
        if (m.LogicalName == SystemUser) await InsertUserSettingsAsync(s, caller, id, ct);
        // Plan 4.1a: a user and an owner team get a private default queue, in this transaction.
        // Plan 4.1b: a user and a queue get their mailbox, in this transaction (a user's before its default queue, whose Create makes the queue's).
        if (m.LogicalName is SystemUser or QueueSchema.Queue && !MigrationScope.IsActive && store.GetTable(MailSchema.Mailbox) is not null)
            await CoreSdkBridges.InsertMailboxAsync(s, caller, m.LogicalName, id, store, ct);
        if (m.LogicalName is SystemUser or Team) await InsertDefaultQueueAsync(s, caller, m, id, ct);
        if (activity) await WriteActivityRowsAsync(s, m, id, partyLists!, isCreate: true, ct);
        if (CurrencyMessages.InvalidatesCatalog(m.LogicalName!)) CurrencyMessages.InvalidateAfterTransaction(currencies, s);
        if (await CoreSdkBridges.IsAuditedAsync(audit, s, m, ct))
            await CoreSdkBridges.WriteAuditAsync(audit, s, caller, AuditActions.Create, AuditOperations.Create, m, id, null,
                await AuditImageAsync(s, m, id, CoreSdkBridges.AuditedColumns(m), ct), ct);
        return id;
    }

    /// <summary>The record with <paramref name="columns"/>, read unrestricted in <paramref name="s"/> (lookups carry their name).</summary>
    private Task<Record> AuditImageAsync(DataSession s, TableDefinition m, Guid id, IEnumerable<string> columns, CancellationToken ct) =>
        RetrieveRecordAsync(s, Caller.Internal, m.LogicalName!, id, new ColumnSelection { Columns = [.. columns] }, ct);

    /// <summary>
    /// 400 for the Create of a form (<c>systemform</c>), view (<c>savedquery</c>) or system chart (<c>savedqueryvisualization</c>)
    /// of a table that a managed solution delivers with <c>CanCreateForms</c>, <c>CanCreateViews</c> or <c>CanCreateCharts</c>
    /// false (ruling M13; final review I4).
    /// </summary>
    private async Task DemandTableAllowsAsync(DataSession s, Record record, CancellationToken ct)
    {
        if (!TableBoundRows.TryGetValue(record.LogicalName, out var bound)
            || !record.Attributes.TryGetValue(bound.Column, out var value) || value is not string table
            || store.GetTable(table) is not { MetadataId: { } tableId }
            || await ManagedProperties.StateAsync(s, ComponentType.Entity, tableId, ct) is not { } state) return;
        var allowed = bound.Flag switch
        {
            "CanCreateForms" => state.Flags.CanCreateForms,
            "CanCreateCharts" => state.Flags.CanCreateCharts,
            _ => state.Flags.CanCreateViews,
        };
        if (!allowed)
            throw ManagedProperties.TableFlagFalse($"{bound.What} '{record.GetValue<string>("name") ?? record.Id.ToString()}'", table,
                bound.Flag, state);
    }

    /// <summary>400 0x8004F50B for a field share whose (record, column, principal) the unique index already holds.</summary>
    private XrmdFaultException DuplicateShare(Record share)
    {
        var record = share.GetValue<RecordReference>(FieldSecurityTables.ShareObjectId);
        var attributeId = share.GetValue<Guid?>(FieldSecurityTables.ShareAttributeId);
        var column = store.GetTable(record?.LogicalName ?? "")?.Attributes?.FirstOrDefault(a => a.MetadataId == attributeId)?.LogicalName;
        return XrmdFaultException.ShareDuplicate(column ?? attributeId?.ToString() ?? "", record?.Id ?? Guid.Empty,
            share.GetValue<RecordReference>(FieldSecurityTables.SharePrincipalId)?.Id ?? Guid.Empty);
    }

    /// <summary>
    /// 412 DuplicateRecord for a write that a unique index refuses (e.g. <c>systemuser.azureactivedirectoryobjectid</c>,
    /// unique among the non-null values); on <c>transactioncurrency</c>, whose only unique index is on <c>isocurrencycode</c>, 400
    /// DuplicateIsoCurrencyCode (plan 2.10, ruling X3: a concurrent write took the code after the request rules checked it).
    /// </summary>
    private static XrmdFaultException DuplicateUniqueValue(string entity, Record row) =>
        entity == CurrencySchema.Table
            ? CurrencyRules.DuplicateCode(row.GetValue<string>(CurrencySchema.IsoCode) ?? "")
            : new(ErrorCodes.DuplicateRecord, $"A record with the same value of a unique attribute already exists on entity '{entity}'.", 412);
}
