using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using AccessRights = Microsoft.Crm.Sdk.Messages.AccessRights;
using Xrmd.Audit;
using Xrmd.Core.Duplicates;
using Xrmd.Core.Elastic;
using Xrmd.Core.Mail;
using Xrmd.Core.Rollups;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Mail;
using Xrmd.Security;
using Xrmd.Security.Sdk;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b (plan Decision 5): the only place where the CRUD bodies on the model reach the SDK-typed helpers owned by later steps
/// (Mail, Elastic, Duplicates, Rollups, Files; the Security ones went with S.2e3c2a). Each row bridge first runs the helper's own
/// guard, copied from the helper (the comment names it): when the guard says the helper does nothing, nothing is converted and
/// nothing is counted.
/// Otherwise it counts one crossing in <see cref="Crossings"/>, writes the row out through a mirror of the call, runs the helper
/// on the SDK twin and reads the twin back into the same <see cref="Record"/>, so the helper's changes land in place, in the order
/// the helper made them. The metadata a helper takes is the cached SDK view of the table (<see cref="SdkMetadataView.SdkView(TableDefinition)"/>,
/// no conversion). Transitional: each bridge goes when its helper moves to the model.
/// </summary>
internal static class CoreSdkBridges
{
    private static long _crossings;

    /// <summary>
    /// Test seam: the number of bridge calls since the process started whose helper applied, so a row (or a reference of it) was
    /// converted to the SDK and back. A bridge whose guard says the helper does nothing is not counted, and neither is a call that
    /// only passes the cached SDK view of a table or a reference built from a table name and an id (<see cref="FindElasticAsync"/> on
    /// a standard table, the email attachment reads, <see cref="IsAuditedAsync"/>, <see cref="AuditedColumns"/>,
    /// <see cref="RefreshMatchCodesAsync"/>, <see cref="RowsReferencingAsync"/>, <see cref="RefreshManyMatchCodesAsync"/>,
    /// <see cref="InsertMailboxAsync"/>, <see cref="DeleteRecordFilesAsync"/>,
    /// <see cref="PendingFileCommit"/>, <see cref="ClaimMailSecrets(Entity)"/>, <see cref="WriteRelationshipAuditAsync"/>;
    /// S.2e3c1 Task 3: the access, share and record team bridges, which pass references built from a table name and an id, the cached
    /// SDK view of a table, access masks cast to the SDK enum, and the request's own SDK twin; Task 4:
    /// <see cref="WriteRolePrivilegesAuditAsync"/>, which converts privilege values and a reference built from a table name and an id, <see cref="Paging"/>, and <see cref="EntityChanges"/>, which builds an answer; Task 5:
    /// <see cref="NestedResponse"/>, which reads in an answer; the queue item bridges are gone, their helpers run on the model).
    /// </summary>
    internal static long Crossings => Interlocked.Read(ref _crossings);

    private static readonly AsyncLocal<StrongBox<long>?> Scope = new();

    /// <summary>
    /// Test seam: counts the crossings of this asynchronous flow (the calls awaited under it) from now until disposed, so that a test
    /// is not disturbed by the crossings of tests running in parallel, which <see cref="Crossings"/> also counts.
    /// </summary>
    internal static CrossingCount CountCrossings() => new();

    /// <summary>The crossings of one asynchronous flow (<see cref="CountCrossings"/>).</summary>
    internal sealed class CrossingCount : IDisposable
    {
        private readonly StrongBox<long>? _previous = Scope.Value;
        private readonly StrongBox<long> _box = new();

        internal CrossingCount() => Scope.Value = _box;

        /// <summary>The crossings counted so far.</summary>
        public long Value => Interlocked.Read(ref _box.Value);

        public void Dispose() => Scope.Value = _previous;
    }

    private static void Count()
    {
        Interlocked.Increment(ref _crossings);
        if (Scope.Value is { } box) Interlocked.Increment(ref box.Value);
    }

    /// <summary>
    /// <see cref="ElasticRecords.FindAsync"/> for the row <paramref name="key"/> of <paramref name="table"/>, which takes the SDK view of the
    /// table (the cached view, so nothing is converted). The helper runs on every table: on a standard table it reads the row too, so
    /// the call is not skipped. Its premise is that a key names a row of an elastic table (<see cref="ElasticRecordKey.Of"/> gives none
    /// on a standard table), checked with <see cref="ElasticTables.IsElastic(TableDefinition)"/>: only a call on an elastic table
    /// counts as a crossing.
    /// </summary>
    public static async Task<(bool Found, string? Partition)> FindElasticAsync(DataSession s, TableDefinition table, ElasticRecordKey key,
        bool lockRow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (ElasticTables.IsElastic(table)) Count();
        EntityMetadata view = table.SdkView();
        return await ElasticRecords.FindAsync(s, view, key, lockRow, ct);
    }

    // ---- the elastic parameters of the CRUD messages (S.2e3b Task 8) ---------------------------------------------------------------

    /// <summary>
    /// The SDK request <see cref="ElasticRecordKey"/> and <see cref="ElasticQueryRules"/> read the <c>partitionId</c> parameter from: a
    /// request of <paramref name="request"/>'s name holding that parameter only (any case, as they match it), its value converted as a
    /// value (<see cref="SdkRecords.ValueToSdk"/>). Nothing else of the request is converted.
    /// </summary>
    private static OrganizationRequest PartitionParameters(Xrmd.Model.Messages.Request request)
    {
        var sdk = new OrganizationRequest(request.Name);
        foreach (var (name, value) in request.Parameters)
            if (string.Equals(name, ElasticRecordKey.PartitionIdParameter, StringComparison.OrdinalIgnoreCase))
                sdk.Parameters[name] = SdkRecords.ValueToSdk(value);
        return sdk;
    }

    /// <summary>
    /// <see cref="ElasticRecordKey.Of"/> for the model Target of a Retrieve. Guard (the helper's): on a standard table it only refuses key
    /// attributes, so a standard table without them gives null and converts nothing; otherwise the reference is converted once.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">400 as <see cref="ElasticRecordKey.Of"/>.</exception>
    public static ElasticRecordKey? ElasticKeyOf(TableDefinition table, RecordReference target, Xrmd.Model.Messages.Request request)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(target);
        if (!ElasticTables.IsElastic(table) && target.KeyAttributes.Count == 0) return null;
        Count();
        return ElasticRecordKey.Of(table.SdkView(), target.ToSdk(), PartitionParameters(request));
    }

    /// <summary>
    /// <see cref="ElasticRecords.BeforeUpdateAsync"/> for the model Target of an Update. Guard (the helper's): on a standard table it only
    /// refuses key attributes, so a standard table without them does nothing and converts nothing; otherwise the helper runs on a copy of
    /// the Target (it reads it and changes nothing).
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="ElasticRecords.BeforeUpdateAsync"/>.</exception>
    public static Task BeforeElasticUpdateAsync(DataSession s, TableDefinition table, Record target, Xrmd.Model.Messages.Request request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(target);
        if (!ElasticTables.IsElastic(table) && target.KeyAttributes.Count == 0) return Task.CompletedTask;
        Count();
        return ElasticRecords.BeforeUpdateAsync(s, table.SdkView(), target.ToSdk(), PartitionParameters(request), ct);
    }

    /// <summary>
    /// <see cref="ElasticRecords.BeforeDeleteAsync"/> for the model Target of a Delete: the id to delete. Guard (<see cref="ElasticRecordKey.Of"/>'s):
    /// a standard table without key attributes deletes the Target's id and converts nothing.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="ElasticRecords.BeforeDeleteAsync"/>.</exception>
    public static async Task<Guid> BeforeElasticDeleteAsync(DataSession s, TableDefinition table, RecordReference target,
        Xrmd.Model.Messages.Request request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(target);
        if (!ElasticTables.IsElastic(table) && target.KeyAttributes.Count == 0) return target.Id;
        Count();
        return await ElasticRecords.BeforeDeleteAsync(s, table.SdkView(), target.ToSdk(), PartitionParameters(request), ct);
    }

    /// <summary>
    /// <see cref="ElasticQueryRules.ApplyRequest"/> for a model RetrieveMultiple: only its <c>partitionId</c> parameter is read, so a
    /// request without one returns <paramref name="query"/> as it is.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">400 as <see cref="ElasticQueryRules.ApplyRequest"/>.</exception>
    public static Xrmd.Query.Model.QueryModel ApplyElasticRequest(Xrmd.Query.Model.QueryModel query, Xrmd.Model.Messages.Request request, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Parameters.Keys.Any(k => string.Equals(k, ElasticRecordKey.PartitionIdParameter, StringComparison.OrdinalIgnoreCase))
            ? ElasticQueryRules.ApplyRequest(query, PartitionParameters(request), store)
            : query;
    }

    /// <summary>
    /// The session token <see cref="ElasticRecords.WithSessionTokenAsync"/> adds after the write of <paramref name="id"/>, or null.
    /// Guard (the helper's): a standard table has none, read nothing.
    /// </summary>
    public static async Task<string?> ElasticSessionTokenAsync(DataSession s, TableDefinition table, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!ElasticTables.IsElastic(table)) return null;
        var response = await ElasticRecords.WithSessionTokenAsync(s, table.SdkView(), id, new OrganizationResponse(), ct);
        return response.Results.TryGetValue(ElasticRecords.SessionToken, out var token) ? token as string : null;
    }

    /// <summary>
    /// The <c>Query</c> of a model RetrieveMultiple that is not a model query definition: an SDK query the mirror keeps as its own twin
    /// (a <c>QueryBase</c> subclass it does not twin) is read or refused by <see cref="CoreOperations.ReadQuery(Microsoft.Xrm.Sdk.Query.QueryBase)"/>
    /// as today; anything else, a missing value included, is today's 400 for a parameter that is missing or not a <c>QueryBase</c>.
    /// </summary>
    public static Xrmd.Query.Model.QueryModel ReadQuery(object? query, string? requestName) =>
        query is Microsoft.Xrm.Sdk.Query.QueryBase sdk
            ? CoreOperations.ReadQuery(sdk)
            : throw Errors.XrmdFaultException.InvalidArgument($"Required parameter 'Query' of request '{requestName}' is missing or not a QueryBase.");

    // ---- the crossing --------------------------------------------------------------------------------------------------

    /// <summary>Counts a crossing, writes <paramref name="row"/> out, runs <paramref name="helper"/> on its twin and reads it back.</summary>
    private static T Cross<T>(Record row, Func<Entity, T> helper)
    {
        Count();
        var mirror = new SdkMirror();
        var entity = (Entity)mirror.Out(row)!;
        try
        {
            return helper(entity);
        }
        finally
        {
            mirror.In(entity);
        }
    }

    private static void Cross(Record row, Action<Entity> helper) => Cross(row, entity =>
    {
        helper(entity);
        return 0;
    });

    /// <summary>The asynchronous <see cref="Cross{T}(Record, Func{Entity, T})"/>.</summary>
    private static async Task<T> CrossAsync<T>(Record row, Func<Entity, Task<T>> helper)
    {
        Count();
        var mirror = new SdkMirror();
        var entity = (Entity)mirror.Out(row)!;
        try
        {
            return await helper(entity);
        }
        finally
        {
            mirror.In(entity);
        }
    }

    private static Task CrossAsync(Record row, Func<Entity, Task> helper) => CrossAsync(row, async entity =>
    {
        await helper(entity);
        return 0;
    });

    /// <summary>The SDK attributes of <paramref name="table"/> by logical name (cached views).</summary>
    private static Dictionary<string, AttributeMetadata> SdkColumns(TableDefinition table) =>
        (table.SdkView().Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);

    // ---- validation (plan 3.4, 2.10) -------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ElasticRecordKey.Normalize(EntityMetadata, Entity, bool, Func{string, EntityMetadata?})"/>. Guard (the helper's
    /// own): <c>DemandOneRowId</c> passes a standard table, and without key attributes on the record or on a reference value there is
    /// nothing to fill in.
    /// </summary>
    public static void NormalizeElasticKeys(TableDefinition table, Record record, bool isCreate, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(record);
        if (!ElasticTables.IsElastic(table) && record.KeyAttributes is not { Count: > 0 }
            && !record.Attributes.Values.Any(v => v is RecordReference { KeyAttributes.Count: > 0 }))
            return;
        Cross(record, e => ElasticRecordKey.Normalize(table.SdkView(), e, isCreate, store.GetEntity));
    }

    /// <summary><see cref="ElasticWriteRules.DemandWrite"/>. Guard (the helper's own): an elastic table.</summary>
    public static void DemandElasticWrite(TableDefinition table, Record record, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!ElasticTables.IsElastic(table)) return;
        Cross(record, e => ElasticWriteRules.DemandWrite(table.SdkView(), e, isCreate));
    }

    // ---- Mail (plan 4.1b, 4.1c) --------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="MailSecrets.Take"/> on the model: the secrets <paramref name="claimed"/> from the pipeline (claimed at the SDK edge,
    /// keyed on the caller's entity) and those still in <paramref name="record"/>, which becomes a copy without them. Guard (the
    /// helper's own, <see cref="PendingMailSecrets.Carries"/>): the record carries a secret column.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> TakeMailSecrets(TableDefinition table, ref Record record,
        IReadOnlyDictionary<string, string?> claimed)
    {
        ArgumentNullException.ThrowIfNull(record);
        var taken = new Dictionary<string, string?>(claimed, StringComparer.Ordinal);
        var name = record.LogicalName;
        if (!record.Attributes.Keys.Any(k => MailSchema.SecretColumns.ContainsKey(name + "." + k))) return taken;
        var copy = new Record(record.LogicalName, record.Id);
        foreach (var (column, value) in record.Attributes) copy[column] = value;
        Cross(copy, e =>
        {
            foreach (var (column, value) in PendingMailSecrets.Extract(e, table.SdkView())) taken[column] = value;
        });
        record = copy;
        return taken;
    }

    /// <summary>
    /// The profile rules (<see cref="ProfileRules"/>) on <paramref name="row"/> as the row will be: the stored profile
    /// (<paramref name="stored"/>, none on a Create) with the change on top. An SMTP profile's Auto Detect is written as Basic.
    /// Guard: an email server profile (the caller's own condition).
    /// </summary>
    public static void ApplyProfileRules(Record row, Record? stored)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.LogicalName != MailSchema.EmailServerProfile) return;
        var storedEntity = stored?.ToSdk();
        Cross(row, entity =>
        {
            var merged = new Entity(entity.LogicalName);
            if (storedEntity is not null) foreach (var (name, value) in storedEntity.Attributes) merged[name] = value;
            foreach (var (name, value) in entity.Attributes) merged[name] = value;
            ProfileRules.Validate(merged);
            // The values the rules settled on (Auto Detect stored as Basic, an absent Graph incoming side taking the outgoing one).
            foreach (var column in new[] { "outgoingauthenticationprotocol", "incomingauthenticationprotocol", "incomingcredentialretrieval" })
            {
                var settled = merged.GetAttributeValue<OptionSetValue>(column);
                if (settled is not null && settled.Value != storedEntity?.GetAttributeValue<OptionSetValue>(column)?.Value)
                    entity[column] = settled;
            }
        });
    }

    /// <summary>
    /// <see cref="Mailboxes.DemandSupportedDeliveryMethods"/>. Guard (the helper's own): a mailbox, queue or user row that carries a
    /// delivery method column.
    /// </summary>
    public static void DemandSupportedDeliveryMethods(Record row, string table)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (table is not (MailSchema.Mailbox or "queue" or "systemuser") || !MailSchema.DeliveryColumns.Any(row.Contains)) return;
        Cross(row, e => Mailboxes.DemandSupportedDeliveryMethods(e, table));
    }

    /// <summary>
    /// <see cref="TrackingTokens.DemandSupportedOrganizationColumns"/>. Guard (the helper's own): the organization row carries a
    /// tracking token id base other than empty or 0.
    /// </summary>
    public static void DemandSupportedOrganizationColumns(Record row, string table)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (table != "organization" || !row.Attributes.TryGetValue(TrackingTokens.IdBaseColumn, out var raw) || raw is null or 0) return;
        Cross(row, e => TrackingTokens.DemandSupportedOrganizationColumns(e, table));
    }

    /// <summary>
    /// <see cref="Mailboxes.DemandNotServerMaintained"/> and <see cref="Mailboxes.DemandOwnerUnchanged"/> on the Update of a mailbox
    /// by a caller other than the server. Guard (the helpers' own): the row carries a server column or the owner.
    /// </summary>
    public static void DemandMailboxWritable(Record row, Record stored, Caller caller)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(stored);
        if (caller == Caller.Internal || !(MailSchema.MailboxServerColumns.Any(row.Contains) || row.Contains("regardingobjectid"))) return;
        var storedEntity = stored.ToSdk();
        Cross(row, e =>
        {
            Mailboxes.DemandNotServerMaintained(e, storedEntity, caller);
            Mailboxes.DemandOwnerUnchanged(e, storedEntity);
        });
    }

    private static readonly string[] IncomingSettingColumns = ["emailserverprofile", "username", "incomingemaildeliverymethod", "enabledforincomingemail"];

    /// <summary>
    /// <see cref="Mailboxes.ApplyIncomingChangeAsync"/>. Guard (the helper's own): the row does not set the status and changes the
    /// password or carries an incoming setting.
    /// </summary>
    public static Task ApplyIncomingChangeAsync(DataSession s, Record row, Guid id, IReadOnlyDictionary<string, string?> secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(secrets);
        if (row.Contains("incomingemailstatus") || (!secrets.ContainsKey("password") && !IncomingSettingColumns.Any(row.Contains)))
            return Task.CompletedTask;
        return CrossAsync(row, e => Mailboxes.ApplyIncomingChangeAsync(s, e, id, secrets, ct));
    }

    /// <summary>
    /// <see cref="Mailboxes.ApplyAddressChangeAsync"/>. Guard (the helper's own): the table has an address column and the row carries it.
    /// </summary>
    public static Task<bool> ApplyAddressChangeAsync(DataSession s, string table, Guid id, Record row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Mailboxes.AddressColumn(table) is not { } column || !row.Contains(column)) return Task.FromResult(false);
        return CrossAsync(row, e => Mailboxes.ApplyAddressChangeAsync(s, table, id, e, ct));
    }

    /// <summary>
    /// <see cref="Mailboxes.ChangedDeliveryColumnsAsync"/>. Guard (the helper's own): the row carries a delivery method column.
    /// </summary>
    public static Task<IReadOnlySet<string>> ChangedDeliveryColumnsAsync(DataSession s, string table, Guid id, Record row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!MailSchema.DeliveryColumns.Any(row.Contains))
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
        return CrossAsync(row, e => Mailboxes.ChangedDeliveryColumnsAsync(s, table, id, e, ct));
    }

    // ---- email attachments (plan 4.1b): metadata views only, nothing converted but a reference, not counted -----------------

    /// <summary><see cref="EmailAttachments.DemandOpenEmailAsync"/>.</summary>
    public static Task<Guid> DemandOpenEmailAsync(DataSession s, TableDefinition email, RecordReference? objectId, CancellationToken ct) =>
        EmailAttachments.DemandOpenEmailAsync(s, email.SdkView(), (EntityReference?)SdkRecords.ValueToSdk(objectId), ct);

    /// <summary><see cref="EmailAttachments.DemandOpenAsync"/>.</summary>
    public static Task DemandOpenEmailAsync(DataSession s, TableDefinition email, Guid emailId, CancellationToken ct) =>
        EmailAttachments.DemandOpenAsync(s, email.SdkView(), emailId, ct);

    /// <summary><see cref="EmailAttachments.NextNumberAsync"/>.</summary>
    public static Task<int> NextAttachmentNumberAsync(DataSession s, TableDefinition attachment, Guid emailId, CancellationToken ct) =>
        EmailAttachments.NextNumberAsync(s, attachment.SdkView(), emailId, ct);

    /// <summary><see cref="EmailAttachments.RecountAsync"/>.</summary>
    public static Task RecountAttachmentsAsync(DataSession s, TableDefinition email, TableDefinition attachment, Guid emailId, int adjust,
        CancellationToken ct) =>
        EmailAttachments.RecountAsync(s, email.SdkView(), attachment.SdkView(), emailId, adjust, ct);

    /// <summary><see cref="EmailAttachments.StoredEmailAsync"/>.</summary>
    public static Task<Guid?> StoredEmailAsync(DataSession s, TableDefinition attachment, Guid id, CancellationToken ct) =>
        EmailAttachments.StoredEmailAsync(s, attachment.SdkView(), id, ct);

    /// <summary><see cref="EmailAttachments.StoredStringAsync"/>.</summary>
    public static Task<string?> StoredAttachmentStringAsync(DataSession s, TableDefinition attachment, Guid id, string column, CancellationToken ct) =>
        EmailAttachments.StoredStringAsync(s, attachment.SdkView(), id, column, ct);

    // ---- Elastic (plan 3.4), queues (plan 4.1a) ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ElasticRecords.ApplyLookupPartitionsAsync"/>. Guard (the helper's own): the row writes a lookup that has a partition
    /// column (<c>{lookup}pid</c>).
    /// </summary>
    public static Task ApplyLookupPartitionsAsync(DataSession s, IMetadataStore store, IReadOnlyDictionary<string, ColumnDefinition> columns,
        TableDefinition table, Record row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Attributes.Keys.Any(name => columns.GetValueOrDefault(name) is LookupColumn
                                             && columns.ContainsKey(ElasticTables.PartitionColumnOf(name))))
            return Task.CompletedTask;
        var attrs = SdkColumns(table);
        return CrossAsync(row, e => ElasticRecords.ApplyLookupPartitionsAsync(s, store, attrs, e, ct));
    }

    // ---- Files (ruling F4), duplicates (plan 2.15), rollups (plan 2.12b) ----------------------------------------------------

    /// <summary>
    /// <see cref="FileColumnService.ApplyImagesAsync"/>. Guard (the helper's own): the row writes an image column.
    /// </summary>
    public static Task ApplyImagesAsync(FileColumnService files, DataSession s, TableDefinition table,
        IReadOnlyDictionary<string, ColumnDefinition> columns, Guid id, Record row, bool isCreate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Attributes.Keys.Any(name => columns.GetValueOrDefault(name) is ImageColumn)) return Task.CompletedTask;
        return CrossAsync(row, e => files.ApplyImagesAsync(s, table.SdkView(), id, e, isCreate, ct));
    }

    /// <summary>
    /// <see cref="FileColumnService.ApplyOnUpdateAsync"/>. Guard (the helper's own): a commit, or a file column set to null.
    /// </summary>
    public static Task ApplyFilesOnUpdateAsync(FileColumnService files, DataSession s, TableDefinition table,
        IReadOnlyDictionary<string, ColumnDefinition> columns, Guid id, Record row, FileCommitGrant? commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (commit is null && !row.Attributes.Any(kv => kv.Value is null && columns.GetValueOrDefault(kv.Key) is FileColumn))
            return Task.CompletedTask;
        return CrossAsync(row, e => files.ApplyOnUpdateAsync(s, table.SdkView(), id, e, commit, ct));
    }

    /// <summary>
    /// <see cref="DuplicateFinder.DemandNoDuplicateAsync"/>. Guard (the helper's own): the table has duplicate detection enabled.
    /// </summary>
    public static Task DemandNoDuplicateAsync(DuplicateFinder finder, DataSession s, TableDefinition table, Record row, Guid id,
        IReadOnlyCollection<string>? changedColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.IsDuplicateDetectionEnabled?.Value != true) return Task.CompletedTask;
        return CrossAsync(row, e => finder.DemandNoDuplicateAsync(s, table.SdkView(), e, id, changedColumns, ct));
    }

    /// <summary>
    /// <see cref="FileColumnService.DeleteRecordFilesAsync"/> (ruling F8: the files of a deleted record; the cached view of the table
    /// only, nothing converted, not counted). Guard (the helper's own): a table with a file or image column, or a permanent target
    /// (a note's body), that has an object type code.
    /// </summary>
    public static Task DeleteRecordFilesAsync(FileColumnService files, DataSession s, TableDefinition table, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(table);
        if (!((table.Attributes ?? []).Any(FileColumns.IsFileOrImage) || FileColumns.IsPermanentTarget(table.LogicalName))
            || table.ObjectTypeCode is null) return Task.CompletedTask;
        return files.DeleteRecordFilesAsync(s, table.SdkView(), id, ct);
    }

    /// <summary>
    /// <see cref="RollupCalculator.ApplyInitialValues"/>. Guard (the helper's own): a calculator, and a table with a rollup column.
    /// </summary>
    public static void ApplyRollupInitialValues(RollupCalculator? rollups, TableDefinition table, Record row, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (rollups is null || !(table.Attributes ?? []).Any(RollupColumns.IsRollup)) return;
        Cross(row, e => rollups.ApplyInitialValues(table.SdkView(), e, nowUtc));
    }

    // ---- after the write: mail (plan 4.1b, 4.1c), match codes (plan 2.15), queues (plan 4.1a), audit (plan 13) -------------------

    private static readonly IReadOnlyDictionary<string, string?> NoSecrets = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="PendingMailSecrets.Claim"/>: the secrets the pipeline took out of <paramref name="target"/>, the Create or Update
    /// Target it stripped (the claim is keyed by that SDK instance), once. Guard (<see cref="PendingMailSecrets.Strip"/> keeps values only
    /// for them): a table with secret columns.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ClaimMailSecrets(Entity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return HasSecretColumns(target.LogicalName) ? PendingMailSecrets.Claim(target) : NoSecrets;
    }

    /// <summary>
    /// <see cref="ClaimMailSecrets(Entity)"/> for a model Target: the claim of the SDK instance <paramref name="mirror"/> paired with
    /// <paramref name="target"/> (the request's chain mirror, <c>SdkMirror.For(request)</c>), none without one. The seam for a model
    /// request that reaches the core with its Target already twinned (S.2e3b Task 8): the adapter claims with it and passes the result
    /// to the model Create or Update, the same values the SDK twins claim.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ClaimMailSecrets(Record target, SdkMirror? mirror)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!HasSecretColumns(target.LogicalName) || mirror is null || !mirror.TryGetSdk(target, out var sdk) || sdk is not Entity entity)
            return NoSecrets;
        return PendingMailSecrets.Claim(entity);
    }

    /// <summary>
    /// <see cref="ClaimMailSecrets(Record, SdkMirror?)"/> with the chain mirror of <paramref name="request"/>, the model request whose
    /// Target <paramref name="target"/> is (S.2e3b Task 8: the dispatch of Create and Update on the model).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ClaimRequestMailSecrets(Record target, Xrmd.Model.Messages.Request request)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        return HasSecretColumns(target.LogicalName) ? ClaimMailSecrets(target, SdkMirror.For(request)) : NoSecrets;
    }

    private static bool HasSecretColumns(string? table) =>
        MailSchema.SecretColumns.Keys.Any(k => k.StartsWith(table + ".", StringComparison.Ordinal));

    /// <summary>
    /// <see cref="FileColumnService.PendingCommit"/>, keyed by the table and id of <paramref name="target"/> only (the helper reads nothing
    /// else): the commit of a file the Update writes, if any.
    /// </summary>
    public static FileCommitGrant? PendingFileCommit(FileColumnService files, DataSession s, Record target)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(target);
        return files.PendingCommit(s, new Entity(target.LogicalName, target.Id));
    }

    /// <summary>
    /// <see cref="MailboxTestJob.EnqueueIfScheduledAsync"/>. Guard (the helper's own): a mailbox written by a caller other than the server
    /// with <c>testemailconfigurationscheduled</c> true.
    /// </summary>
    public static Task EnqueueMailboxTestAsync(DataSession s, Caller caller, AsyncOperationQueue queue, string table, Guid id, Record row,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (table != MailSchema.Mailbox || caller == Caller.Internal || row.GetValue<bool?>(MailSchema.TestScheduled) != true) return Task.CompletedTask;
        return CrossAsync(row, e => MailboxTestJob.EnqueueIfScheduledAsync(s, caller, queue, table, id, e, ct));
    }

    /// <summary><see cref="Mailboxes.InsertForAsync"/>: the mailbox of a new user or queue (a reference built from its table and id).</summary>
    public static Task InsertMailboxAsync(DataSession s, Caller caller, string table, Guid id, IMetadataStore store, CancellationToken ct) =>
        Mailboxes.InsertForAsync(s, caller, new EntityReference(table, id), ct, store);

    /// <summary>
    /// <see cref="Mailboxes.SyncAsync"/>. Guard (the helper's own): it has something to set on the mailbox: the address, a name, the
    /// approval, a changed delivery method, an incoming reset (a changed address or incoming method), or a queue's owner.
    /// </summary>
    public static Task SyncMailboxAsync(DataSession s, string table, Guid id, Record row, bool addressChanged, IMetadataStore store,
        IReadOnlySet<string>? deliveryChanged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        var incomingReset = addressChanged || deliveryChanged?.Contains("incomingemaildeliverymethod") == true;
        if (!incomingReset
            && !(Mailboxes.AddressColumn(table) is { } address && row.Contains(address))
            && !(row.Attributes.TryGetValue(Mailboxes.NameColumn(table), out var name) && name is string { Length: > 0 })
            && !(row.Attributes.TryGetValue(MailSchema.ApprovalColumn, out var approval) && approval is ChoiceValue)
            && !MailSchema.DeliveryColumns.Any(c => row.Contains(c) && deliveryChanged?.Contains(c) == true)
            && !(table == QueueSchema.Queue && row.Attributes.TryGetValue("ownerid", out var owner) && owner is RecordReference))
            return Task.CompletedTask;
        return CrossAsync(row, e => Mailboxes.SyncAsync(s, table, id, e, addressChanged, ct, store, deliveryChanged));
    }

    /// <summary>
    /// <see cref="MatchCodeStore.RefreshAsync(DataSession, EntityMetadata, Guid, CancellationToken)"/>, or with the
    /// <paramref name="changed"/> columns of an Update its overload (the cached view of the table only).
    /// </summary>
    public static Task RefreshMatchCodesAsync(MatchCodeStore codes, DataSession s, TableDefinition table, Guid id, IEnumerable<string>? changed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return changed is null ? codes.RefreshAsync(s, table.SdkView(), id, ct) : codes.RefreshAsync(s, table.SdkView(), id, changed, ct);
    }

    /// <summary><see cref="MatchCodeStore.RowsReferencingAsync"/> (the cached view of the table only).</summary>
    public static Task<IReadOnlyList<Guid>> RowsReferencingAsync(MatchCodeStore codes, DataSession s, TableDefinition table, string lookup, Guid id,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return codes.RowsReferencingAsync(s, table.SdkView(), lookup, id, ct);
    }

    /// <summary><see cref="MatchCodeStore.RefreshManyAsync"/> (the cached view of the table only).</summary>
    public static Task RefreshManyMatchCodesAsync(MatchCodeStore codes, DataSession s, TableDefinition table, IReadOnlyList<Guid> ids,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return codes.RefreshManyAsync(s, table.SdkView(), ids, ct);
    }

    /// <summary>
    /// The answer of a request the core issued through the nested delegate (S.2e3c1 Task 5: the <c>queueitem</c> Create of
    /// AddToQueue), current: when the request's chain mirror holds the SDK twin of <paramref name="response"/> (an unread shell, or a
    /// response the pipeline answered after its steps), that twin is read in first, so its results are the ones the SDK response holds,
    /// as the SDK body read them; otherwise <paramref name="response"/> as it is. Not counted in <see cref="Crossings"/>: it converts an
    /// answer, no row of the core.
    /// </summary>
    public static Response NestedResponse(Request request, Response response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        return SdkMirror.For(request) is { } mirror && mirror.TryGetSdk(response, out var twin) && twin is OrganizationResponse sdk
            ? mirror.In(sdk)!
            : response;
    }

    /// <summary><see cref="AuditWriter.IsAuditedAsync"/>: whether a write of <paramref name="table"/> is audited now (the cached view only).</summary>
    public static Task<bool> IsAuditedAsync(AuditWriter audit, DataSession s, TableDefinition table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return audit.IsAuditedAsync(s, table.SdkView(), ct);
    }

    /// <summary><see cref="AuditPolicy.AuditedAttributes"/> by logical name (the cached view only).</summary>
    public static IEnumerable<string> AuditedColumns(TableDefinition table) =>
        AuditPolicy.AuditedAttributes(table.SdkView()).Select(a => a.LogicalName);

    /// <summary>
    /// The audit row of a write: <see cref="AuditWriter.AttributeChange"/> of the images (none before a Create), read on the model, and
    /// <see cref="AuditWriter.WriteAsync"/>. Guard (<see cref="AuditWriter.AttributeChange"/>'s own): an audited table and an image; the
    /// caller reads the images only when <see cref="IsAuditedAsync"/> says the write is audited. The images are what crosses: an audited
    /// table converts exactly its audit images.
    /// </summary>
    public static Task WriteAuditAsync(AuditWriter audit, DataSession s, Caller caller, int action, int operation, TableDefinition table, Guid id,
        Record? before, Record? after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(table);
        var view = table.SdkView();
        if (!AuditPolicy.IsAudited(view) || (before is null && after is null)) return Task.CompletedTask;
        Count();
        return WriteAuditAsync(audit, s, caller, action, operation, view, id, before?.ToSdk(), after?.ToSdk(), ct);
    }

    private static async Task WriteAuditAsync(AuditWriter audit, DataSession s, Caller caller, int action, int operation, EntityMetadata view,
        Guid id, Entity? before, Entity? after, CancellationToken ct)
    {
        if (AuditWriter.AttributeChange(view, before, after) is { } change)
            await audit.WriteAsync(s, caller, action, operation, new EntityReference(view.LogicalName, id), change, ct);
    }

    // ---- associate (security relationships, relationship audit) -------------------------------------------------------------

    /// <summary>
    /// <see cref="AuditWriter.WriteAsync"/> of a relationship change on <paramref name="target"/>: the records that really changed
    /// (<paramref name="changed"/>, at least one; the caller writes nothing otherwise). The writer takes references built from a table
    /// name and an id, as the SDK body built them, so nothing is converted.
    /// </summary>
    public static Task WriteRelationshipAuditAsync(AuditWriter audit, DataSession s, Caller caller, int action, RecordReference target,
        string relationship, IReadOnlyList<RecordReference> changed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(changed);
        return audit.WriteAsync(s, caller, action, AuditOperations.Update, new EntityReference(target.LogicalName, target.Id),
            new RelationshipChange(relationship, changed.Select(e => new EntityReference(e.LogicalName, e.Id)).ToList()), ct);
    }

    // ---- sharing (S.2e3c1 Task 3: the audit writer until S.2f) --------------------------------------------------------------------

    /// <summary>
    /// The audit row of a share change on <paramref name="target"/> (<see cref="AuditWriter.WriteAsync"/> of a <see cref="ShareChange"/>):
    /// the writer takes references built from a table name and an id, as the SDK body built them (it reads nothing else of the
    /// target), so nothing is converted.
    /// </summary>
    public static Task WriteShareAuditAsync(AuditWriter audit, DataSession s, Caller caller, int action, RecordReference target,
        RecordReference principal, AccessMask old, AccessMask now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return audit.WriteAsync(s, caller, action, AuditOperations.Update, Reference(target),
            new ShareChange(Reference(principal), (AccessRights)(int)old, (AccessRights)(int)now), ct);
    }

    // ---- role privileges, change tracking (S.2e3c1 Task 4: the audit writer until S.2f; the change tracking answer and paging, which
    // have no model twin as parameter values) ------------------------------------------------------------------------------------------

    /// <summary>
    /// The audit row of a role privilege change on the role <paramref name="roleId"/> (<see cref="AuditWriter.WriteAsync"/> of a
    /// <see cref="RolePrivilegesChange"/>): the grants of <see cref="SecurityAdministration.RoleGrantsAsync"/> go back to the SDK privileges they were read from
    /// (no name, no record filter), and the role is a reference built from the table name and the id, as the SDK body built it.
    /// </summary>
    public static Task WriteRolePrivilegesAuditAsync(AuditWriter audit, DataSession s, Caller caller, int action, Guid roleId,
        IReadOnlyList<PrivilegeGrant> old, IReadOnlyList<PrivilegeGrant> now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(now);
        return audit.WriteAsync(s, caller, action, AuditOperations.Update, new EntityReference("role", roleId),
            new RolePrivilegesChange([.. old.Select(g => g.ToSdk())], [.. now.Select(g => g.ToSdk())]), ct);
    }

    /// <summary>
    /// The paging of a RetrieveEntityChanges <c>PageInfo</c> parameter: an SDK <c>PagingInfo</c> crosses the mirror as its own twin (it
    /// is a query part only inside a query), so it is read here as a model copy; null for a value of any other type, as the SDK body's
    /// <c>as PagingInfo</c>.
    /// </summary>
    public static Model.Query.QueryPaging? Paging(object? value) => value is Microsoft.Xrm.Sdk.Query.PagingInfo paging ? paging.ToModel() : null;

    /// <summary>
    /// The <c>EntityChanges</c> result of RetrieveEntityChanges for <paramref name="page"/>: the SDK <c>BusinessEntityChanges</c> (no model
    /// twin; the mirror carries it as its own twin, as the SDK body's answer was), the updates as <c>NewOrUpdatedItem</c>s of the
    /// records written out as plain copies (as the read answers are, ruling 8a), then the deletions as <c>RemovedOrDeletedItem</c>s of
    /// references built from the table name and the id. This builds the answer and runs no helper, so it is not counted in
    /// <see cref="Crossings"/>.
    /// </summary>
    public static object EntityChanges(ChangePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var changes = new BusinessEntityChangesCollection();
        foreach (var record in page.Updated) changes.Add(new NewOrUpdatedItem(ChangeType.NewOrUpdated, record.ToSdk()));
        foreach (var deleted in page.Deleted)
            changes.Add(new RemovedOrDeletedItem(ChangeType.RemoveOrDeleted, new EntityReference(deleted.LogicalName, deleted.Id)));
        return new BusinessEntityChanges
        {
            Changes = changes, DataToken = page.DataToken, MoreRecords = page.MoreRecords, PagingCookie = page.PagingCookie,
            GlobalMetadataVersion = page.GlobalMetadataVersion,
        };
    }

    /// <summary>A reference built from the table name and the id of <paramref name="reference"/>.</summary>
    private static EntityReference Reference(RecordReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new EntityReference(reference.LogicalName, reference.Id);
    }
}
