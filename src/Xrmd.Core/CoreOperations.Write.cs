using System.Data;
using System.Globalization;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Core.Mail;
using Xrmd.Core.Validation;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the write helpers of Create and Update on the model (validation, owner, state and status, business unit, full name, note
/// and attachment bodies, auto-numbers, references, activity rows). They read <see cref="TableDefinition"/> and
/// <see cref="ColumnDefinition"/> metadata and change <see cref="Record"/> rows; a helper of another step that still takes SDK records
/// is reached through <see cref="CoreSdkBridges"/>.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>
    /// The checks of Create/Update that need no database (so that they precede any connection). Changes <paramref name="record"/>
    /// in place: the ids of key attributes are filled in and the values of calculated columns are removed (ignored, plan 2.12a ruling
    /// K5). The model twin of <c>ValidateWrite</c>.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an unknown table or an invalid value.</exception>
    internal (TableDefinition Table, Dictionary<string, ColumnDefinition> Columns) ValidateRecord(Record record, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(record);
        var m = TableOf(record.LogicalName);
        // Plan 3.4: ids given by key attributes are filled in first (refused on a standard table).
        CoreSdkBridges.NormalizeElasticKeys(m, record, isCreate, store);
        var attrs = ColumnsOf(m);
        // Plan 2.10, ruling X3: a currency's rate out of range is InvalidExchangeRate, not the generic range error.
        CoreSdkBridges.DemandRateInRange(record);
        EntityValidator.Validate(record, m, attrs, isCreate);
        // Plan 2.12a, ruling K5 (and 2.12c F9 for a formula column): a value sent for a calculated column is ignored; it is dropped here, so that it never reaches a
        // plug-in's Target nor the audit of the write. Plan 2.12b, ruling R11: the same for a rollup column, its companions and the
        // _base of a Currency rollup (only the calculation writes them).
        foreach (var name in record.Attributes.Keys.Where(n => attrs.GetValueOrDefault(n) is { } a
                     && (ComputedColumnRules.IsComputed(a) || RollupColumns.IsRollupValue(a, attrs.GetValueOrDefault))).ToList())
            record.Attributes.Remove(name);
        // The full name of a contact is the server's (ruling C3): a value is refused rather than silently replaced.
        // On update a value equal to the stored one is a no-op, which only the database can tell: UpdateAsync refuses the rest.
        if (isCreate && IsContact(m)) DemandNoFullName(record);
        CoreSdkBridges.DemandElasticWrite(m, record, isCreate);
        if (!isCreate) UpdateId(record, m, SystemAttributes.PrimaryIdName(m));
        return (m, attrs);
    }

    /// <summary>The columns of <paramref name="m"/> by logical name.</summary>
    private static Dictionary<string, ColumnDefinition> ColumnsOf(TableDefinition m) =>
        (m.Attributes ?? []).ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);

    private static void DemandNoFullName(Record entity)
    {
        if (entity.Contains(ContactFullName.FullName))
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{ContactFullName.FullName}' of entity '{ContactFullName.Contact}' is computed by the server " +
                $"from {string.Join(", ", ContactFullName.Parts)} and cannot be set.");
    }

    private static bool IsContact(TableDefinition m) =>
        m.LogicalName == ContactFullName.Contact && MetadataStore.IsSystemEntity(m);

    /// <summary>
    /// Sets <c>fullname</c> of a contact row from its name parts (those not in the row are read from the stored record on an
    /// update) in the order of <c>organization.fullnameconventioncode</c>. An update that touches no name part leaves it alone.
    /// </summary>
    private static async Task ApplyFullNameAsync(DataSession s, Record row, Guid id, bool isCreate, CancellationToken ct)
    {
        if (!ContactFullName.Parts.Any(row.Contains)) return;
        var parts = ContactFullName.Parts.ToDictionary(p => p, p => row.Contains(p) ? row.GetValue<string>(p) : null);
        if (!isCreate && ContactFullName.Parts.Any(p => !row.Contains(p)))
        {
            await using var cmd = s.Command(
                $"SELECT firstname, middlename, lastname FROM {s.DataTable(ContactFullName.Contact)} WHERE contactid = {IdParameter}");
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return; // no such record: the update reports it
            for (var i = 0; i < ContactFullName.Parts.Count; i++)
                if (!row.Contains(ContactFullName.Parts[i])) parts[ContactFullName.Parts[i]] = rd.IsDBNull(i) ? null : rd.GetString(i);
        }

        int convention;
        await using (var cmd = s.Command(s.Dialect.Limit($"SELECT fullnameconventioncode FROM {s.DataTable("organization")}", "1")))
            convention = await cmd.ExecuteScalarAsync(ct) is int code && code is >= 0 and <= 7 ? code : ContactFullName.DefaultConvention; // a stored value outside 0..7 falls back to the default
        row[ContactFullName.FullName] = ContactFullName.Compose(parts["firstname"], parts["middlename"], parts["lastname"], convention);
    }

    private static Guid UpdateId(Record entity, TableDefinition m, string pk) =>
        entity.Id != Guid.Empty ? entity.Id
        : entity.Attributes.TryGetValue(pk, out var value) && value is Guid g ? g
        : throw XrmdFaultException.InvalidArgument($"Update of '{m.LogicalName}' requires the record id.");

    /// <summary>A copy of <paramref name="entity"/> (its attribute collection; the values are shared).</summary>
    private static Record Copy(Record entity)
    {
        var copy = new Record(entity.LogicalName, entity.Id);
        foreach (var (name, value) in entity.Attributes) copy[name] = value;
        return copy;
    }

    /// <summary>A copy of <paramref name="entity"/> without <paramref name="attribute"/>.</summary>
    private static Record Without(Record entity, string attribute)
    {
        var copy = new Record(entity.LogicalName, entity.Id);
        foreach (var (name, value) in entity.Attributes)
            if (name != attribute) copy[name] = value;
        return copy;
    }

    /// <summary>
    /// The read-only <c>ismanaged</c> column of a row component (plan 2.6, ruling M14): written only by the layer operations,
    /// as <see cref="Caller.Internal"/> (any other caller's value is refused by <see cref="SystemTableRules"/> before this);
    /// a Create by anyone else writes false.
    /// </summary>
    private static void ApplyIsManaged(TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs, Record entity, Record row,
        Caller caller, bool isCreate)
    {
        if (ComponentType.OfTable(m.LogicalName) is null || !attrs.ContainsKey(SystemTableRules.IsManaged)) return;
        if (caller == Caller.Internal && entity.GetValue<bool?>(SystemTableRules.IsManaged) is { } given)
            row[SystemTableRules.IsManaged] = given;
        else if (isCreate) row[SystemTableRules.IsManaged] = false;
    }

    /// <summary>The note body of a write (plan 2.11, A16): <see cref="Given"/> false when the write does not carry <c>documentbody</c>.</summary>
    private readonly record struct NoteBody(bool Given, string? Base64);

    /// <summary>
    /// Takes <c>documentbody</c> out of a write of <c>annotation</c> (it has no column: <see cref="ApplyNoteBodyAsync"/> stores it);
    /// <paramref name="entity"/> becomes a copy without it. Any other table: nothing given.
    /// </summary>
    private static NoteBody ExtractNoteBody(TableDefinition m, ref Record entity)
    {
        // Plan 4.1b: the body of an email attachment (activitymimeattachment.body) is kept in the file store the same way.
        var attribute = m.LogicalName switch
        {
            ActivitySchema.Annotation => ActivitySchema.DocumentBody,
            MailSchema.Attachment => MailSchema.AttachmentBody,
            _ => null,
        };
        if (attribute is null || !entity.Attributes.TryGetValue(attribute, out var value)) return default;
        entity = Copy(entity);
        entity.Attributes.Remove(attribute);
        return new NoteBody(true, value as string);
    }

    /// <summary>
    /// The attachment of an email, on its Create or Update (plan 4.1b, ruling P4), before its row is written: the email must be an open
    /// one (its row is locked), the file name not a blocked extension, and the body (base64, checked against
    /// <c>organization.maxuploadfilesize</c>) is stored by <see cref="AttachmentBodies"/>, replacing the previous one. <c>filesize</c>
    /// follows it; a new attachment takes the next <c>attachmentnumber</c> (when it names none) and <c>mimetype</c>, when not sent, the
    /// one of the file name's extension. A body sent null or empty clears it. An Update that sends no body keeps it, and a new
    /// <c>filename</c> or <c>mimetype</c> is copied to its <c>fileattachment</c> row. <c>email.attachmentcount</c> follows a Create.
    /// An attachment never moves to another email.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for a table other than an email, an email that is not open, a blocked
    /// extension, a body that is not base64 or a change of the email; 400 0x80044a02 over the limit.</exception>
    private async Task ApplyAttachmentAsync(DataSession s, TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs, Record row,
        Guid id, NoteBody body, bool isCreate, CancellationToken ct)
    {
        var email = TableOf(ActivitySchema.Email);
        string? fileName;
        if (isCreate)
        {
            var emailId = await CoreSdkBridges.DemandOpenEmailAsync(s, email, row.GetValue<RecordReference>(ActivitySchema.ObjectId), ct);
            fileName = row.GetValue<string>(ActivitySchema.FileName);
            await EmailAttachments.DemandNotBlockedAsync(s, fileName, ct);
            if (!row.Contains(MailSchema.AttachmentNumber))
                SetIfDefined(row, attrs, MailSchema.AttachmentNumber, await CoreSdkBridges.NextAttachmentNumberAsync(s, m, emailId, ct));
            await CoreSdkBridges.RecountAttachmentsAsync(s, email, m, emailId, adjust: 1, ct);
        }
        else
        {
            var emailId = await CoreSdkBridges.StoredEmailAsync(s, m, id, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName!, id);
            // Any objectid in the write must be the stored email: a null (also a Disassociate) detaches the attachment.
            if (row.Contains(ActivitySchema.ObjectId)
                && (row.GetValue<RecordReference>(ActivitySchema.ObjectId) is not { } moved || moved.Id != emailId || moved.LogicalName != ActivitySchema.Email))
                throw XrmdFaultException.InvalidArgument($"The attachment {id} belongs to the email {emailId}: it cannot be moved to another record or detached.");
            await CoreSdkBridges.DemandOpenEmailAsync(s, email, emailId, ct);
            await LockRowAsync(s, m, id, ct);
            fileName = row.Contains(ActivitySchema.FileName)
                ? row.GetValue<string>(ActivitySchema.FileName)
                : await CoreSdkBridges.StoredAttachmentStringAsync(s, m, id, ActivitySchema.FileName, ct);
            if (row.Contains(ActivitySchema.FileName) || body.Given) await EmailAttachments.DemandNotBlockedAsync(s, fileName, ct);
        }
        var bodies = files.Attachments;
        if (!body.Given)
        {
            if (isCreate) SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
            else if (row.Contains(ActivitySchema.FileName) || row.Contains(ActivitySchema.MimeType))
                await bodies.DescribeAsync(s, id, row.Contains(ActivitySchema.FileName) ? row.GetValue<string>(ActivitySchema.FileName) ?? "" : null,
                    row.Contains(ActivitySchema.MimeType) ? row.GetValue<string>(ActivitySchema.MimeType) ?? "" : null, ct);
            return;
        }
        if (string.IsNullOrEmpty(body.Base64))
        {
            if (!isCreate) await bodies.ClearAsync(s, id, ct);
            SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
            return;
        }
        var mimeType = row.GetValue<string>(ActivitySchema.MimeType);
        var stored = await bodies.WriteAsync(s, id, body.Base64, fileName, mimeType,
            MigrationScope.IsActive ? int.MaxValue : await MaxUploadFileSizeAsync(s, ct), ct);
        SetIfDefined(row, attrs, ActivitySchema.FileSize, checked((int)stored.Size));
        if (string.IsNullOrEmpty(mimeType)) SetIfDefined(row, attrs, ActivitySchema.MimeType, stored.MimeType);
    }

    /// <summary>
    /// The note body of a Create or Update of <c>annotation</c> (plan 2.11, A16), before the note's row is written. A body sent
    /// (base64) is checked against <c>organization.maxuploadfilesize</c> and stored by <see cref="AnnotationBodies"/> (replacing the
    /// previous one); <c>filesize</c> and <c>isdocument</c> follow it (a client's <c>isdocument</c> is ignored on every write) and <c>mimetype</c>, when not sent, is the one of the file name's
    /// extension (from memory: checklist 3g). A body sent null or empty clears it: <c>filesize</c> 0 and <c>isdocument</c> false. A
    /// Create without a body gets those two values too; an Update without one keeps the body, and a new <c>filename</c> or
    /// <c>mimetype</c> is copied to its <c>fileattachment</c> row.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for a body that is not base64; 400 0x80044a02 over the limit.</exception>
    private async Task ApplyNoteBodyAsync(DataSession s, TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs, Record row,
        Guid id, NoteBody body, bool isCreate, CancellationToken ct)
    {
        var bodies = files.Annotations;
        // An Update locks the note first, as a body write of another transaction does: one body per note.
        if (!isCreate && (body.Given || row.Contains(ActivitySchema.FileName) || row.Contains(ActivitySchema.MimeType)))
            await LockRowAsync(s, m, id, ct);
        // isdocument is derived by the server from whether the note has a body, on every write: a client value is ignored (from
        // memory: checklist 3g).
        if (!body.Given)
        {
            row.Attributes.Remove(ActivitySchema.IsDocument);
            if (isCreate)
            {
                SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
                SetIfDefined(row, attrs, ActivitySchema.IsDocument, false);
            }
            else if (row.Contains(ActivitySchema.FileName) || row.Contains(ActivitySchema.MimeType))
                await bodies.DescribeAsync(s, id, row.Contains(ActivitySchema.FileName) ? row.GetValue<string>(ActivitySchema.FileName) ?? "" : null,
                    row.Contains(ActivitySchema.MimeType) ? row.GetValue<string>(ActivitySchema.MimeType) ?? "" : null, ct);
            return;
        }
        if (string.IsNullOrEmpty(body.Base64))
        {
            if (!isCreate) await bodies.ClearAsync(s, id, ct);
            SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
            SetIfDefined(row, attrs, ActivitySchema.IsDocument, false);
            return;
        }
        var fileName = row.Contains(ActivitySchema.FileName) || isCreate
            ? row.GetValue<string>(ActivitySchema.FileName)
            : await StoredStringAsync(s, m, id, ActivitySchema.FileName, ct);
        var mimeType = row.GetValue<string>(ActivitySchema.MimeType);
        var stored = await bodies.WriteAsync(s, id, body.Base64, fileName, mimeType,
            MigrationScope.IsActive ? int.MaxValue : await MaxUploadFileSizeAsync(s, ct), ct);
        SetIfDefined(row, attrs, ActivitySchema.FileSize, checked((int)stored.Size));
        SetIfDefined(row, attrs, ActivitySchema.IsDocument, true);
        // A new body without a mimetype takes the one of the file name's extension (from memory: checklist 3g).
        if (string.IsNullOrEmpty(mimeType)) SetIfDefined(row, attrs, ActivitySchema.MimeType, stored.MimeType);
    }

    /// <summary>A string column of a stored row (null when empty or when the row does not exist).</summary>
    private static async Task<string?> StoredStringAsync(DataSession s, TableDefinition m, Guid id, string column, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(column)} FROM {s.DataTable(m.LogicalName!)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Takes an update lock on the record's row for the rest of the transaction, so that the audited "before" image read next
    /// is the one the following UPDATE or DELETE replaces; false when the row does not exist.
    /// </summary>
    private static async Task<bool> LockRowAsync(DataSession s, TableDefinition m, Guid id, CancellationToken ct)
    {
        await s.KeyLockAsync(m.LogicalName!, id, ct);
        // Plan 2.7a, Task 6: the key read with the lock clause (PostgreSQL's FOR UPDATE takes no aggregate), so that the row lock
        // is taken on both engines: a writer that locked the row first (a record team add on its template) is waited for.
        var key = s.Quote(SystemAttributes.PrimaryIdName(m));
        await using var cmd = s.Command(
            $"SELECT {key} FROM {s.DataTable(m.LogicalName!)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
            $"WHERE {key} = {IdParameter}{s.Dialect.LockClause(RowLock.UpdateRow)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) is not (null or DBNull);
    }

    /// <summary>
    /// Fills each autonumber column the Create leaves without a value (absent, null or empty) from its
    /// <c>AutoNumberFormat</c> (autonumber plan, rulings N2 and N4): the format is checked here, at the save of a record,
    /// as Dataverse does; a value the caller gives is kept.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for a format that is not valid or a value longer than the
    /// column's MaxLength.</exception>
    private static async Task ApplyAutoNumbersAsync(DataSession s, TableDefinition m, Record row,
        IReadOnlyDictionary<string, ColumnDefinition> attrs, CancellationToken ct)
    {
        foreach (var a in attrs.Values.OfType<TextColumn>().Where(a => !string.IsNullOrEmpty(a.AutoNumberFormat)))
        {
            if (row.GetValue<string>(a.LogicalName!) is { Length: > 0 }) continue;
            AutoNumberFormat format;
            try
            {
                format = AutoNumberFormat.Parse(a.AutoNumberFormat!);
            }
            catch (XrmdValidationException ex)
            {
                throw XrmdFaultException.InvalidArgument($"Attribute '{m.LogicalName}.{a.LogicalName}': {ex.Message}");
            }
            long sequence = 0;
            if (format.UsesSequence)
            {
                await using var cmd = s.Command(AutoNumberSequences.NextValueSql(a, s.Dialect));
                sequence = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            var value = format.Render(sequence, DateTime.UtcNow);
            if (a.MaxLength is { } max && value.Length > max)
                throw XrmdFaultException.InvalidArgument(
                    $"Attribute '{m.LogicalName}.{a.LogicalName}': the autonumber value '{value}' is longer than its MaxLength {max}.");
            row[a.LogicalName!] = value;
        }
    }

    /// <summary>
    /// Plan 2.16 at stage 30, in the request's transaction: a template's values are checked again on what is written (ruling A2,
    /// <see cref="AccessTeamRules.DemandTemplateAsync"/>, which on Create takes the lock that serializes it with disabling the
    /// table and counts the templates under it). xrmverse applies no column default on insert, so a Create writes
    /// <c>teamtemplate.issystem</c> false and, when not given, <c>team.systemmanaged</c> false.
    /// </summary>
    private async Task ApplyAccessTeamRulesAsync(DataSession s, TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs,
        Record row, bool isCreate, Guid id, CancellationToken ct)
    {
        if (m.LogicalName == AccessTeamSchema.TeamTemplate)
        {
            await CoreSdkBridges.DemandAccessTeamTemplateAsync(s, store, row, isCreate, id, ct);
            if (isCreate) SetIfDefined(row, attrs, AccessTeamSchema.IsSystem, false);
        }
        else if (isCreate && m.LogicalName == Team && !row.Contains(AccessTeamSchema.SystemManaged))
        {
            SetIfDefined(row, attrs, AccessTeamSchema.SystemManaged, false);
        }
    }

    /// <summary>
    /// Create rules on business units: a systemuser or team gets <c>businessunitid</c> (default: the caller's) and
    /// <c>owningbusinessunit</c> follows it; a business unit must have a parent.
    /// </summary>
    private static void ApplyBusinessUnitOnCreate(string entity, Record row, IReadOnlyDictionary<string, ColumnDefinition> attrs, Caller caller)
    {
        switch (entity)
        {
            case SystemUser or Team:
                var bu = row.GetValue<RecordReference>(BusinessUnitId);
                if (bu is null)
                {
                    if (caller.BusinessUnitId == Guid.Empty)
                        throw XrmdFaultException.InvalidArgument($"Attribute '{BusinessUnitId}' of entity '{entity}' is required.");
                    bu = Reference(BusinessUnit, caller.BusinessUnitId);
                    row[BusinessUnitId] = bu;
                }
                SetIfDefined(row, attrs, "owningbusinessunit", Reference(BusinessUnit, bu.Id));
                break;
            case BusinessUnit when row.GetValue<RecordReference>(ParentBusinessUnitId) is null:
                throw XrmdFaultException.InvalidArgument("A business unit must have a parent business unit.");
        }
    }

    /// <summary>
    /// Update rules on business units: <c>owningbusinessunit</c> of a systemuser or team follows a new <c>businessunitid</c>;
    /// the root business unit keeps no parent and the others keep one that does not close a loop.
    /// </summary>
    private static async Task ApplyBusinessUnitOnUpdateAsync(
        DataSession s, string entity, Guid id, Record row, IReadOnlyDictionary<string, ColumnDefinition> attrs, CancellationToken ct)
    {
        switch (entity)
        {
            case SystemUser or Team when row.Contains(BusinessUnitId):
                var bu = row.GetValue<RecordReference>(BusinessUnitId)
                         ?? throw XrmdFaultException.InvalidArgument($"Attribute '{BusinessUnitId}' of entity '{entity}' cannot be null.");
                SetIfDefined(row, attrs, "owningbusinessunit", Reference(BusinessUnit, bu.Id));
                break;
            case BusinessUnit when row.Contains(ParentBusinessUnitId):
                var parent = row.GetValue<RecordReference>(ParentBusinessUnitId);
                Guid? current;
                await using (var cmd = s.Command(
                    $"SELECT parentbusinessunitid FROM {s.DataTable(BusinessUnit)}{s.Dialect.LockHint(RowLock.Shared)} WHERE businessunitid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}"))
                {
                    cmd.AddParameter(IdParameter, DbType.Guid, id);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(BusinessUnit, id);
                    current = rd.IsDBNull(0) ? null : rd.GetGuid(0);
                }

                if (current is null)
                {
                    if (parent is not null)
                        throw XrmdFaultException.InvalidArgument("The root business unit cannot have a parent business unit.");
                    row.Attributes.Remove(ParentBusinessUnitId); // root stays root
                }
                else if (parent is null)
                {
                    throw XrmdFaultException.InvalidArgument("A business unit must have a parent business unit.");
                }
                else if (parent.Id != current)
                {
                    await EnsureNoLoopAsync(s, id, parent.Id, ct);
                }
                break;
        }
    }

    /// <summary>Walks up from <paramref name="newParent"/>; reaching <paramref name="id"/> means the hierarchy would loop.</summary>
    private static async Task EnsureNoLoopAsync(DataSession s, Guid id, Guid newParent, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        Guid? cursor = newParent;
        while (cursor is { } node)
        {
            if (node == id || !visited.Add(node))
                throw XrmdFaultException.InvalidArgument("Business unit hierarchy cannot contain a loop.");
            await using var cmd = s.Command(
                $"SELECT parentbusinessunitid FROM {s.DataTable(BusinessUnit)}{s.Dialect.LockHint(RowLock.Shared)} WHERE businessunitid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, node);
            var next = await cmd.ExecuteScalarAsync(ct);
            cursor = next is Guid g ? g : null;
        }
    }

    /// <summary>
    /// Plan 2.11, after the activity row is written: its party rows (<see cref="ActivityParties.WriteAsync"/>), its pointer row
    /// (<see cref="ActivityPointerSync.WriteAsync"/>), then its Owner and Regarding party rows, read from the pointer row. On
    /// Create the pointer row comes first: its plain INSERT refuses an id another activity holds (412, final review C1) before a
    /// party row is written.
    /// </summary>
    /// <remarks>
    /// Lock order (final review I2), as the Delete of an activity and of a party record: the activity row (locked by the Update,
    /// inserted by the Create) before everything else; then the party records (read with HOLDLOCK by
    /// <see cref="ActivityParties.WriteAsync"/>) before the <c>activityparty</c> rows; the pointer row last on Update.
    /// </remarks>
    private async Task WriteActivityRowsAsync(DataSession s, TableDefinition m, Guid id, IReadOnlyDictionary<string, RecordCollection> partyLists,
        bool isCreate, CancellationToken ct)
    {
        if (isCreate) await _pointer.WriteAsync(s, m, id, isCreate: true, ct);
        await _parties.WriteAsync(s, m, id, partyLists, isCreate, ct);
        if (!isCreate) await _pointer.WriteAsync(s, m, id, isCreate: false, ct);
        await ActivityParties.SyncSystemPartiesAsync(s, id, ct);
    }

    /// <summary>
    /// Ruling Y2: every party record named in <paramref name="partyLists"/> must be readable by <paramref name="caller"/>, checked
    /// as a Retrieve of it (PrivilegeDenied without Read on its table, AccessDenied when the record is out of the caller's reach,
    /// 404 when it does not exist) before anything is written or its address is read, so a party list discloses nothing the
    /// caller cannot read. AppendTo on the party is not required (A9). From memory: checklist 3d (party without Read / without
    /// AppendTo).
    /// </summary>
    private async Task DemandPartiesReadableAsync(DataSession s, Caller caller, IReadOnlyDictionary<string, RecordCollection> partyLists,
        CancellationToken ct)
    {
        var seen = new HashSet<(string, Guid)>();
        foreach (var party in partyLists.Values.SelectMany(l => l.Records)
                     .Select(e => e.GetValue<RecordReference>(ActivitySchema.PartyId)).OfType<RecordReference>())
            if (seen.Add((party.LogicalName, party.Id)))
                await RetrieveRecordAsync(s, caller, party.LogicalName, party.Id, null, ct);
    }

    /// <summary>The closed states of an activity (A11): Completed and Canceled.</summary>
    private static bool IsClosedState(int? state) => state is 1 or 2;

    /// <summary>
    /// Plan 2.11 on the Create of an activity: <c>activitytypecode</c> names the table (A7); without state or status the record
    /// starts in state 0 with that state's default status from the table's metadata (pre-flight C11: a task starts Not Started,
    /// 2), and a status must belong to its state (A11); a record created closed gets <c>actualend</c> now when none is given
    /// (from memory: checklist 3e).
    /// </summary>
    private static void ApplyActivityStateOnCreate(TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs, Record row, Guid id,
        DateTime now)
    {
        SetIfDefined(row, attrs, ActivitySchema.ActivityTypeCode, m.LogicalName!);
        if (row.GetValue<ChoiceValue>("statecode") is null && row.GetValue<ChoiceValue>("statuscode") is null)
            SetIfDefined(row, attrs, "statecode", new ChoiceValue(0));
        CompleteStateAndStatus(m, attrs, row, id);
        if (IsClosedState(row.GetValue<ChoiceValue>("statecode")?.Value) && row.GetValue<DateTime?>(ActualEnd) is null)
            SetIfDefined(row, attrs, ActualEnd, now);
    }

    private const string ActualEnd = "actualend";

    /// <summary>
    /// Plan 2.11 on the Update of an activity (A11), with the stored state read under an update lock: a closed activity (state 1
    /// or 2) is read-only, only its state, status and regarding change (a SetState, a Set Regarding), except by Assign (pre-flight
    /// C12: Dataverse assigns closed activities; checklist 4); moving it to a closed state sets <c>actualend</c> to now when it is null (checklist 3e). A
    /// missing record is left to the Update's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for a change of a closed activity.</exception>
    private static async Task ApplyActivityStateOnUpdateAsync(DataSession s, TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs,
        Record row, IReadOnlyDictionary<string, RecordCollection> partyLists, string? committedFile, Guid id, int auditAction,
        IEnumerable<string>? serverColumns, CancellationToken ct)
    {
        int? state;
        DateTime? actualEnd;
        await using (var cmd = s.Command(
                         $"SELECT statecode, {s.Quote(ActualEnd)} FROM {s.DataTable(m.LogicalName!)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
                         $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}{s.Dialect.LockClause(RowLock.UpdateRow)}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return;
            state = rd.IsDBNull(0) ? null : rd.GetInt32(0);
            actualEnd = rd.IsDBNull(1) ? null : rd.GetDateTime(1);
        }

        if (IsClosedState(state) && auditAction != AuditActions.Assign)
        {
            // A file commit's column was taken out of the entity before (ruling F4): it is a change all the same.
            // The columns a message's server writes with the state (plan 4.1b: the send job's senton on a Pending Send email) are no change.
            var server = serverColumns?.ToHashSet(StringComparer.Ordinal) ?? [];
            // The regarding (and its type name) can be set on a closed activity, as Dataverse's Set Regarding does on a closed email (from memory, #339).
            var changed = row.Attributes.Keys.Where(k => k is not ("statecode" or "statuscode" or "regardingobjectid" or "regardingobjecttypecode") && !server.Contains(k)).Concat(partyLists.Keys)
                .Concat(committedFile is null ? [] : [committedFile]).Distinct().ToList();
            if (changed.Count > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"The {m.LogicalName} record {id} is closed (statecode {state}): only its state, status and regarding can change, so " +
                    $"'{string.Join("', '", changed)}' cannot be written. Reopen it first (statecode 0).");
        }
        if (row.GetValue<ChoiceValue>("statecode") is { } newState && IsClosedState(newState.Value) && actualEnd is null
            && row.GetValue<DateTime?>(ActualEnd) is null)
            SetIfDefined(row, attrs, ActualEnd, DateTime.UtcNow);
    }

    /// <summary>
    /// Plan 2.11 A7 on Update: an EntityName companion (<c>regardingobjecttypecode</c>, …) sent without its lookup must name the
    /// stored target of the lookup (read from its type column), or be null when the lookup is empty. With the lookup in the
    /// request, the <c>EntityValidator</c> has already compared them.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for another table.</exception>
    private async Task DemandEntityNamesMatchStoredAsync(DataSession s, TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs,
        Record entity, Guid id, CancellationToken ct)
    {
        foreach (var (name, value) in entity.Attributes)
        {
            if (attrs[name] is not EntityNameColumn { AttributeOf: { Length: > 0 } lookup } companion || entity.Contains(lookup)) continue;
            await using var cmd = s.Command(
                $"SELECT {s.Quote(lookup)}, {s.Quote(lookup + TypeMapping.TypeColumnSuffix)} FROM {s.DataTable(m.LogicalName!)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
                $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}{s.Dialect.LockClause(RowLock.UpdateRow)}");
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return; // the Update answers 404
            var stored = rd.IsDBNull(0) || rd.IsDBNull(1) ? null : store.GetTableByObjectTypeCode(rd.GetInt32(1))?.LogicalName;
            if (!string.Equals(value as string, stored, StringComparison.Ordinal))
                throw EntityValidator.MismatchedCompanion(m, companion, value as string, stored);
        }
    }

    /// <summary>The system columns a migrated record keeps (migration spec §7.5), as written by the source.</summary>
    private static readonly string[] MigrationSystemColumns =
        ["createdon", "createdby", "modifiedon", "modifiedby", "owningbusinessunit", SystemAttributes.OverriddenCreatedOnName];

    /// <summary>
    /// The values of <see cref="MigrationSystemColumns"/> that <paramref name="entity"/> carries (not null) and its table has: inside
    /// a migration scope they replace what the server would set.
    /// </summary>
    private static List<KeyValuePair<string, object>> MigratedSystemColumns(Record entity, IReadOnlyDictionary<string, ColumnDefinition> attrs) =>
        [.. MigrationSystemColumns.Where(attrs.ContainsKey)
            .Where(name => entity.Attributes.TryGetValue(name, out var value) && value is not null)
            .Select(name => new KeyValuePair<string, object>(name, entity[name]!))];

    /// <summary>
    /// The caller's attributes that this operation may write (validated beforehand), plus, on Update, the file columns set to
    /// null (not valid for update, but null deletes the file: ruling F4).
    /// </summary>
    private static Record WritableValues(Record entity, IReadOnlyDictionary<string, ColumnDefinition> attrs, bool isCreate)
    {
        var row = new Record(entity.LogicalName);
        foreach (var (name, value) in entity.Attributes)
            if (EntityValidator.IsWritable(attrs[name], isCreate) || (!isCreate && value is null && attrs[name] is FileColumn))
                row[name] = value;
        return row;
    }

    /// <summary>
    /// Keeps <c>statecode</c>/<c>statuscode</c> consistent: a <c>statuscode</c> must belong to the given <c>statecode</c>
    /// (else 400) and sets it when not given; a <c>statecode</c> alone brings its default status.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for a status that belongs to another state.</exception>
    private static void CompleteStateAndStatus(TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs, Record row, Guid id)
    {
        var state = row.GetValue<ChoiceValue>("statecode");
        var status = row.GetValue<ChoiceValue>("statuscode");
        if (status is not null)
        {
            var owning = (attrs.GetValueOrDefault("statuscode") as StatusColumn)?.OptionSet?.Options?
                .OfType<StatusOption>().FirstOrDefault(o => o.Value == status.Value)?.State;
            if (owning is not { } owningState) return;
            if (state is null) SetIfDefined(row, attrs, "statecode", new ChoiceValue(owningState));
            else if (state.Value != owningState)
                throw XrmdFaultException.InvalidArgument(
                    $"{status.Value} is not a valid status code for state code {state.Value} on {m.LogicalName} with Id {id}.");
        }
        else if (state is not null)
        {
            var defaultStatus = (attrs.GetValueOrDefault("statecode") as StateColumn)?.OptionSet?.Options?
                .OfType<StateOption>().FirstOrDefault(o => o.Value == state.Value)?.DefaultStatus;
            if (defaultStatus is { } d) SetIfDefined(row, attrs, "statuscode", new ChoiceValue(d));
        }
    }

    /// <summary>
    /// The <c>statuscode</c> a Create writes when none is given (plan 2.15, ruling D3): the <c>DefaultStatus</c> of the state
    /// option 0, else 1. A new duplicate rule is so Unpublished (0); the tables whose state 0 defaults to 1 keep 1.
    /// </summary>
    private static int DefaultStatus(IReadOnlyDictionary<string, ColumnDefinition> attrs) =>
        (attrs.GetValueOrDefault("statecode") as StateColumn)?.OptionSet?.Options?
        .OfType<StateOption>().FirstOrDefault(o => o.Value == 0)?.DefaultStatus ?? 1;

    private static void SetIfDefined(Record row, IReadOnlyDictionary<string, ColumnDefinition> attrs, string name, object value)
    {
        if (attrs.ContainsKey(name)) row[name] = value;
    }

    /// <summary>A reference to the row <paramref name="id"/> of <paramref name="table"/>.</summary>
    private static RecordReference Reference(string table, Guid id) => new() { LogicalName = table, Id = id };

    /// <summary>
    /// Sets <c>ownerid</c> and the owning columns: a systemuser owner gives owninguser (owningteam cleared) and the user's
    /// business unit, an owner team gives owningteam (owninguser cleared) and the team's business unit.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an owner of another type or a team that is not an owner
    /// team; ObjectDoesNotExist (404) for a missing owner.</exception>
    private static async Task ApplyOwnerAsync(
        DataSession s, Record row, IReadOnlyDictionary<string, ColumnDefinition> attrs,
        RecordReference owner, Caller caller, CancellationToken ct)
    {
        if (owner.LogicalName is not (SystemUser or Team))
            throw XrmdFaultException.InvalidArgument(
                $"Owner of type '{owner.LogicalName}' is not supported: '{OwnerId}' must reference a {SystemUser} or a {Team}.");

        Guid? ownerBusinessUnit = caller.BusinessUnitId;
        if (owner.LogicalName == Team)
        {
            await using var cmd = s.Command(
                $"SELECT businessunitid, teamtype, name FROM {s.DataTable(Team)}{s.Dialect.LockHint(RowLock.Shared)} WHERE teamid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, owner.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(Team, owner.Id);
            if (!rd.IsDBNull(1) && rd.GetInt32(1) != 0)
                throw XrmdFaultException.InvalidArgument(
                    $"Team '{(rd.IsDBNull(2) ? owner.Id.ToString() : rd.GetString(2))}' is not an owner team and cannot own records.");
            ownerBusinessUnit = rd.IsDBNull(0) ? null : rd.GetGuid(0);
        }
        else if (owner.Id != caller.UserId)
        {
            await using var cmd = s.Command(
                $"SELECT businessunitid FROM {s.DataTable(SystemUser)}{s.Dialect.LockHint(RowLock.Shared)} WHERE systemuserid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, owner.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(SystemUser, owner.Id);
            ownerBusinessUnit = rd.IsDBNull(0) ? null : rd.GetGuid(0);
        }

        var isTeam = owner.LogicalName == Team;
        row[OwnerId] = Reference(owner.LogicalName, owner.Id);
        SetIfDefined(row, attrs, "owninguser", isTeam ? null! : Reference(SystemUser, owner.Id));
        SetIfDefined(row, attrs, "owningteam", isTeam ? Reference(Team, owner.Id) : null!);
        if (ownerBusinessUnit is { } bu) SetIfDefined(row, attrs, "owningbusinessunit", Reference(BusinessUnit, bu));
    }

    /// <summary>
    /// Every caller-supplied lookup must point to an existing record of a known entity; the check runs in the write
    /// transaction (HOLDLOCK) since <c>data.*</c> tables have no foreign keys.
    /// </summary>
    private async Task EnsureReferencesExistAsync(
        DataSession s, Record row, IReadOnlyDictionary<string, ColumnDefinition> attrs, CancellationToken ct)
    {
        foreach (var (name, value) in row.Attributes)
        {
            if (value is not RecordReference reference || attrs[name] is not LookupColumn || name == OwnerId) continue;
            if (IsSystemSet(name)) continue;

            var target = (reference.LogicalName is null ? null : store.GetTable(reference.LogicalName))
                         ?? throw XrmdFaultException.InvalidArgument(
                             $"Attribute '{name}' references entity '{reference.LogicalName}', which does not exist.");
            await using var cmd = s.Command(
                $"SELECT 1 FROM {s.DataTable(target.LogicalName!)}{s.Dialect.LockHint(RowLock.Shared)} " +
                $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(target))} = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, reference.Id);
            if (await cmd.ExecuteScalarAsync(ct) is null) throw XrmdFaultException.ObjectDoesNotExist(target.LogicalName!, reference.Id);
        }
    }

    /// <summary>Lookups the service fills itself from known records (caller, caller's or owner's business unit).</summary>
    private static bool IsSystemSet(string name) =>
        name is "createdby" or "modifiedby" or "owninguser" or "owningteam" or "owningbusinessunit";
}
