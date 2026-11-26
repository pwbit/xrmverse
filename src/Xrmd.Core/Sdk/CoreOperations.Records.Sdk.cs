using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Audit;
using Xrmd.Core.Elastic;
using Xrmd.Core.Query;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Query.Model;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the SDK twins of the public CRUD members of <see cref="CoreOperations"/> whose bodies moved to the model. Each keeps the old
/// name and signature, so the callers still on the SDK (the other modules, the SDK handlers of <c>CoreOperations.cs</c>) are not edited,
/// and writes the model answer out once. Transitional: removed with their callers' moves (S.2e3c, S.2f).
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>The SDK twin of <see cref="RetrieveRecordAsync"/>: the columns read in, the entity shaped from the reader (Decision 4).</summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="RetrieveRecordAsync"/>.</exception>
    public async Task<Entity> RetrieveAsync(
        DataSession s, Caller caller, string entityName, Guid id, ColumnSet? columns, CancellationToken ct, ElasticRecordKey? key = null) =>
        QueryExecutorSdk.ToSdk(await RetrieveRowAsync(s, caller, entityName, id, columns?.ToModel(), QueryExecutorSdk.Rows, ct, key));

    /// <summary>The SDK twin of <see cref="RetrieveRecordsAsync"/>: the entities shaped from the reader (Decision 4).</summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="RetrieveRecordsAsync"/>.</exception>
    public async Task<EntityCollection> RetrieveMultipleAsync(DataSession s, Caller caller, QueryModel query, CancellationToken ct) =>
        QueryExecutorSdk.ToSdk(await RetrievePageAsync(s, caller, query, QueryExecutorSdk.Rows, ct));

    /// <summary>
    /// The SDK twin of <see cref="ValidateRecord"/>, for the callers still on the SDK (<c>ElasticUpsert</c>, the
    /// <c>OrganizationService</c>): it changes <paramref name="entity"/> in place as before (the ids of key
    /// attributes filled in, the values of calculated columns removed), through a mirror of the call.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="ValidateRecord"/>.</exception>
    internal (EntityMetadata Metadata, Dictionary<string, AttributeMetadata> Attributes) ValidateWrite(Entity entity, bool isCreate)
    {
        var (table, _, _) = ValidateInPlace(entity, isCreate);
        var view = table.SdkView();
        return (view, AttributesOf(view));
    }

    /// <summary>
    /// <see cref="ValidateRecord"/> on the mirror twin of <paramref name="entity"/>, written back into it (also when the validation
    /// throws, as the SDK body changed it in place up to the fault); the twin is returned, holding the same values.
    /// </summary>
    private (TableDefinition Table, Dictionary<string, ColumnDefinition> Columns, Record Record) ValidateInPlace(Entity entity, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var mirror = new SdkMirror();
        var record = (Record)mirror.In(entity)!;
        try
        {
            var (table, columns) = ValidateRecord(record, isCreate);
            return (table, columns, record);
        }
        finally
        {
            mirror.Out(record);
        }
    }

    /// <summary>
    /// The SDK twin of <see cref="CreateRecordAsync(DataSession, Caller, Record, CancellationToken, bool, IReadOnlyDictionary{string, object?}?)"/>:
    /// the caller's entity read in as a copy (<see cref="WriteTwin"/>, which leaves the entity as before: only the validation changes it
    /// in place), the server values converted, the mail secrets claimed by the entity, and the model body run.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As the model member.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="serverSetValues"/> names a column the table does not have.</exception>
    public async Task<Guid> CreateAsync(DataSession s, Caller caller, Entity entity, CancellationToken ct, bool detectDuplicates = false,
        IReadOnlyDictionary<string, object?>? serverSetValues = null)
    {
        var (record, validate) = WriteTwin(entity, isCreate: true);
        var (m, attrs) = validate(record);
        // Plan 4.1b: the secrets the pipeline took out of the caller's entity are claimed by that instance. This runs before the post,
        // parent and table checks of the body (it ran after them before S.2e3b): harmless, as a Create refused by them fails as a whole
        // and nothing else ever claims the values kept for this instance.
        var claimed = CoreSdkBridges.ClaimMailSecrets(entity);
        return await CreateValidatedAsync(s, caller, m, attrs, record, claimed, ct, detectDuplicates, ModelValues(serverSetValues));
    }

    /// <summary>
    /// The SDK twin of <see cref="UpdateRecordAsync(DataSession, Caller, Record, CancellationToken, bool)"/>: the caller's entity read in
    /// as a copy (<see cref="WriteTwin"/>), the mail secrets claimed by the entity, and the model body run.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As the model member.</exception>
    public Task UpdateAsync(DataSession s, Caller caller, Entity entity, CancellationToken ct, bool detectDuplicates = false) =>
        UpdateAsync(s, caller, entity, AuditActions.Update, null, ct, detectDuplicates);

    /// <summary>The SDK twin of <see cref="UpdateRecordForMessageAsync"/> (SendEmail and its send job). <paramref name="server"/> carries
    /// model values (<see cref="ServerValues"/>), not SDK ones.</summary>
    internal Task UpdateForMessageAsync(DataSession s, Caller caller, Entity entity, int auditAction, ServerValues server, CancellationToken ct) =>
        UpdateAsync(s, caller, entity, auditAction, server, ct);

    private Task UpdateAsync(DataSession s, Caller caller, Entity entity, int auditAction, ServerValues? server, CancellationToken ct,
        bool detectDuplicates = false)
    {
        var (record, validate) = WriteTwin(entity, isCreate: false);
        // Plan 4.1b: the secrets the pipeline took out of the entity are claimed by that instance. This runs before the post, contact and
        // managed checks of the body (it ran after them before S.2e3b): harmless, as an Update refused by them fails as a whole and
        // nothing else ever claims the values kept for this instance.
        var claimed = CoreSdkBridges.ClaimMailSecrets(entity);
        return UpdateRecordAsync(s, caller, record, auditAction, server, claimed, validate, ct, detectDuplicates);
    }

    /// <summary>
    /// The SDK twin of <see cref="DeleteRecordAsync"/> outside a pipeline: its cascaded children are deleted directly by the same body.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="DeleteRecordAsync"/>.</exception>
    public Task DeleteAsync(DataSession s, Caller caller, string entityName, Guid id, CancellationToken ct) =>
        DeleteRecordAsync(s, caller, entityName, id, null, ct);

    /// <summary>
    /// The SDK twin of <see cref="AssignRecordAsync"/>. The assignee is the value of the Update's <c>ownerid</c>: a reference by key
    /// gets its id filled in by the validation (<see cref="CoreSdkBridges.NormalizeElasticKeys"/>), which lands in the caller's
    /// <paramref name="assignee"/> through a mirror of the call, as before S.2e3b; any other assignee is read in as a copy.
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="AssignRecordAsync"/>.</exception>
    public async Task AssignAsync(DataSession s, Caller caller, EntityReference target, EntityReference assignee, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assignee);
        if (assignee.KeyAttributes is not { Count: > 0 })
        {
            await AssignRecordAsync(s, caller, target.ToModel(), assignee.ToModel(), ct);
            return;
        }
        var mirror = new SdkMirror();
        var twin = (RecordReference)mirror.In(assignee)!;
        try
        {
            await AssignRecordAsync(s, caller, target.ToModel(), twin, ct);
        }
        finally
        {
            mirror.Out(twin);
        }
    }

    /// <summary>
    /// The SDK twin of <see cref="AssociateRecordsAsync"/>: the target, the relationship and the related records read in as copies (the
    /// body changes none of them).
    /// </summary>
    /// <exception cref="Errors.XrmdFaultException">As <see cref="AssociateRecordsAsync"/>.</exception>
    public Task AssociateAsync(DataSession s, Caller caller, EntityReference target, Relationship relationship,
        EntityReferenceCollection related, bool associate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentNullException.ThrowIfNull(related);
        return AssociateRecordsAsync(s, caller, target.ToModel(), relationship.ToModel(), related.ToModel(), associate, ct);
    }

    /// <summary>
    /// The record an SDK write works on: a copy of <paramref name="entity"/> (one <c>ToModel</c>), so the body never writes the caller's
    /// entity; and the validation to run on it (<see cref="ValidateRecord"/>), which changes the entity in place as the SDK body did
    /// before S.2e3b (pinned on <c>main</c>): the values of calculated columns are removed from it, and a write by key gets its id and
    /// <c>partitionid</c> in it. When the body validates another record (a copy that dropped a committed file or an unchanged full name),
    /// the entity is left as it is, as before.
    /// </summary>
    /// <remarks>
    /// A write by key goes through a mirror of the call instead, so every in-place change of the key normalization lands in the entity
    /// (its id, its <c>partitionid</c>, the ids of the references by key). Guard (<see cref="CoreSdkBridges.NormalizeElasticKeys"/>'s): an
    /// elastic table, or key attributes on the entity or on a reference value. Otherwise the validation only removes values, which are
    /// removed from the entity in turn, also when it throws (as the SDK body changed the entity up to a fault).
    /// </remarks>
    private (Record Record, Func<Record, (TableDefinition, Dictionary<string, ColumnDefinition>)> Validate) WriteTwin(Entity entity, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if ((store.GetTable(entity.LogicalName ?? "") is { } table && ElasticTables.IsElastic(table)) || entity.KeyAttributes is { Count: > 0 }
            || entity.Attributes.Values.Any(v => v is EntityReference { KeyAttributes.Count: > 0 }))
        {
            var mirror = new SdkMirror();
            var twin = (Record)mirror.In(entity)!;
            return (twin, record =>
            {
                if (!ReferenceEquals(record, twin)) return ValidateRecord(record, isCreate);
                try
                {
                    return ValidateRecord(record, isCreate);
                }
                finally
                {
                    mirror.Out(record);
                }
            });
        }
        var copy = entity.ToModel();
        return (copy, record =>
        {
            if (!ReferenceEquals(record, copy)) return ValidateRecord(record, isCreate);
            try
            {
                return ValidateRecord(record, isCreate);
            }
            finally
            {
                foreach (var name in entity.Attributes.Keys.Where(name => !record.Contains(name)).ToList()) entity.Attributes.Remove(name);
            }
        });
    }

    /// <summary>The server values of an SDK caller as model values (<see cref="SdkRecords.ValueToModel"/>).</summary>
    private static Dictionary<string, object?>? ModelValues(IReadOnlyDictionary<string, object?>? values) =>
        values?.ToDictionary(kv => kv.Key, kv => SdkRecords.ValueToModel(kv.Value), StringComparer.Ordinal);
}
