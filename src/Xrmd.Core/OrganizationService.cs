using System.Data;
using System.Data.Common;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Core.Query;
using Xrmd.Core.Solutions;
using Xrmd.Core.Validation;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Bulk;
using Xrmd.Pipeline.Merge;
using Xrmd.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// The internal <see cref="IOrganizationService"/> (spec: every front end goes through it). Scoped: the caller's
/// <c>systemuser</c> comes from the scoped <see cref="CallerResolver"/>, so a request resolves it once.
/// Create, Retrieve, Update, Delete, RetrieveMultiple, Assign, SetState, Associate, Disassociate and custom actions run
/// through the plug-in <see cref="PipelineExecutor"/>;
/// WhoAmI, RetrieveEntity, RetrieveAttribute, the metadata messages (<see cref="MetadataOperations"/>), the audit messages
/// (<see cref="AuditMessages"/>) and the autonumber seed messages (<see cref="AutoNumberMessages"/>) do not.
/// The synchronous SDK members block on the <c>*Async</c> ones, which front ends should call directly.
/// </summary>
public sealed class OrganizationService(
    IDbConnectionFactory factory, CallerResolver callerResolver, CoreOperations ops, PipelineExecutor executor, MetadataOperations metadata,
    SolutionImporter importer, SolutionExporter exporter, AuditMessages audit, Xrmd.Security.FieldSecurity fields,
    AutoNumberMessages autoNumbers, SolutionUninstaller uninstaller, SolutionPromoter promoter, AccessMessages access,
    ChangeSet? changeSet = null, IMigrationLock? migrationLock = null)
    : IOrganizationService
{
    private readonly IMigrationLock _migrationLock = migrationLock ?? NoMigrationLock.Instance;

    /// <summary>
    /// The messages that only read, which go through while the organization is locked for a migration (migration plan M3, R4):
    /// every <c>Retrieve*</c> message and these.
    /// </summary>
    private static readonly HashSet<string> LockFreeMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "WhoAmI", "ExportSolution", "InitializeFrom", "LocalTimeFromUtcTime", "UtcTimeFromLocalTime", "FetchXmlToQueryExpression",
        "QueryExpressionToFetchXml", "RetrieveMultiple",
    };

    /// <summary>The 503 of a migration lock for a request that writes (any message but the reads of <see cref="LockFreeMessages"/>).</summary>
    private void DemandNotLocked(string requestName)
    {
        if (_migrationLock.ActiveRunId is null) return;
        if (requestName.StartsWith("Retrieve", StringComparison.OrdinalIgnoreCase) || LockFreeMessages.Contains(requestName)) return;
        _migrationLock.Demand();
    }

    private Guid? _organizationId;

    public Guid Create(Entity entity) => CreateAsync(entity).GetAwaiter().GetResult();

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
        RetrieveAsync(entityName, id, columnSet).GetAwaiter().GetResult();

    public void Update(Entity entity) => UpdateAsync(entity).GetAwaiter().GetResult();

    public void Delete(string entityName, Guid id) => DeleteAsync(entityName, id).GetAwaiter().GetResult();

    public OrganizationResponse Execute(OrganizationRequest request) => ExecuteAsync(request).GetAwaiter().GetResult();

    /// <summary>Runs an <see cref="AssociateRequest"/> (one-to-many relationships derived from lookups, and the security relationships).</summary>
    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities,
        }).GetAwaiter().GetResult();

    /// <summary>Runs a <see cref="DisassociateRequest"/> (one-to-many relationships derived from lookups, and the security relationships).</summary>
    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        ExecuteAsync(new DisassociateRequest
        {
            Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities,
        }).GetAwaiter().GetResult();

    public EntityCollection RetrieveMultiple(QueryBase query) => RetrieveMultipleAsync(query).GetAwaiter().GetResult();

    /// <summary>
    /// Inserts the record. Id: <see cref="Entity.Id"/>, else the primary key attribute, else a new one. Sets
    /// createdon/modifiedon (UTC now), createdby/modifiedby (caller), owningbusinessunit, statecode 0/statuscode 1 when
    /// not given and, for user-owned entities, ownerid (default: caller) with owninguser.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for invalid input; ObjectDoesNotExist (404) for a
    /// lookup to a missing record; DuplicateRecord (412) when the id is taken; 403 when the caller has no systemuser.</exception>
    public Task<Guid> CreateAsync(Entity entity, CancellationToken ct = default) => CreateAsync(entity, null, ct);

    /// <summary>
    /// <see cref="CreateAsync(Entity, CancellationToken)"/> with the request's <c>SolutionUniqueName</c>: a row that is a
    /// solution component is also added to that solution (404 when it does not exist).
    /// </summary>
    public async Task<Guid> CreateAsync(Entity entity, string? solutionUniqueName, CancellationToken ct = default)
    {
        DemandNotLocked("Create");
        ops.ValidateWrite(entity, isCreate: true); // before any connection: invalid input is a 400 whoever the caller is
        var request = new CreateRequest { Target = entity };
        if (!string.IsNullOrEmpty(solutionUniqueName)) request["SolutionUniqueName"] = solutionUniqueName;
        var response = await RunAsync(request, ct);
        return (Guid)response.Results["id"];
    }

    /// <summary>
    /// Reads one record with the columns of <paramref name="columnSet"/> (primary key first; <c>AllColumns</c> → every
    /// readable attribute; null/empty → the primary key only), through the <see cref="QueryExecutor"/>: lookups carry the
    /// target's name and the entity its formatted values, as in RetrieveMultiple.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an unknown entity or column; ObjectDoesNotExist (404)
    /// when the record does not exist.</exception>
    public async Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken ct = default)
    {
        ops.EntityOf(entityName); // an unknown entity is a 400 whoever the caller is
        var response = await RunAsync(
            new RetrieveRequest { Target = new EntityReference(entityName, id), ColumnSet = columnSet }, ct);
        return (Entity)response.Results["Entity"];
    }

    /// <summary>
    /// The partition the row <paramref name="id"/> of an elastic table is stored in, found by id alone (the id is unique, plan 3.4
    /// ruling E1), for a front end that names a row by id alone and must reach it whatever its partition (the file routes of the
    /// Web API, final review I2); null for a standard table, a missing row or a row stored without partition. It checks no access:
    /// the request that then carries the partition is checked as any other.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 when the caller has no systemuser; 400 for an unknown entity.</exception>
    public async Task<string?> StoredPartitionAsync(string entityName, Guid id, CancellationToken ct = default)
    {
        var m = ops.EntityOf(entityName);
        if (!ElasticTables.IsElastic(m)) return null;
        await using var conn = await factory.OpenAsync(ct);
        await CallerAsync(conn, ct);
        return (await ElasticRecords.StoredPartitionAsync(new DataSession(conn, null), m, id, ct)).Partition;
    }

    /// <summary>
    /// Writes only the given (updatable) attributes plus modifiedon/modifiedby. Read-only attributes are ignored, so a
    /// retrieved entity can be sent back. A new ownerid also moves owninguser and owningbusinessunit.
    /// </summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the record or a referenced record does not exist.</exception>
    public async Task UpdateAsync(Entity entity, CancellationToken ct = default)
    {
        DemandNotLocked("Update");
        ops.ValidateWrite(entity, isCreate: false);
        await RunAsync(new UpdateRequest { Target = entity }, ct);
    }

    /// <summary>
    /// Deletes the record through the pipeline. The Delete of a managed <c>solution</c> row is its uninstall instead (plan 2.6,
    /// ruling M9: <see cref="SolutionUninstaller"/>, outside the pipeline, in a transaction of its own, refused when it would
    /// drop data, ruling M10).
    /// </summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the record does not exist; the refusals of the
    /// uninstall.</exception>
    public async Task DeleteAsync(string entityName, Guid id, CancellationToken ct = default)
    {
        DemandNotLocked("Delete");
        var m = ops.EntityOf(entityName);
        if (m.LogicalName == SolutionDeleter.Table)
        {
            if (changeSet?.Session is not null)
                throw XrmdFaultException.InvalidArgument("A solution cannot be deleted in a change set: its uninstall commits in a transaction of its own.");
            var caller = await callerResolver.ResolveAsync(ct);
            if (await uninstaller.ManagedNameAsync(id, ct) is { } managed)
            {
                await uninstaller.UninstallAsync(caller, managed, new UninstallOptions(DropData: false, DryRun: false, FromSdk: true), ct);
                return;
            }
        }
        await RunAsync(new DeleteRequest { Target = new EntityReference(entityName, id) }, ct);
    }

    /// <summary>
    /// Runs a <see cref="QueryExpression"/>, <see cref="QueryByAttribute"/> or <see cref="FetchExpression"/> (see
    /// <see cref="QueryExecutor.ExecuteAsync"/>). The caller is resolved first.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 when the caller has no systemuser; InvalidArgument (400) for an invalid or
    /// unsupported query; AggregateQueryRecordLimitExceeded (400).</exception>
    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken ct = default) =>
        RetrieveMultipleAsync(query, null, ct);

    private async Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, OrganizationRequest? source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await callerResolver.ResolveAsync(ct); // before parsing, so that an unknown caller is a 403 whatever the query
        CoreOperations.ReadQuery(query); // an invalid query is a 400 before any plug-in runs
        var request = new RetrieveMultipleRequest { Query = query };
        var response = await RunAsync(source is null ? request : ElasticRecords.CarryParameters(source, request), ct); // partitionId, plan 3.4
        return (EntityCollection)response.Results["EntityCollection"];
    }

    /// <summary>
    /// Runs a query already in the <see cref="QueryModel"/> form (frontends that build the AST themselves, such as the
    /// Web API) for the resolved caller (see <see cref="QueryExecutor.ExecuteAsync"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">403 when the caller has no systemuser; InvalidArgument (400) for an invalid or
    /// unsupported query; AggregateQueryRecordLimitExceeded (400).</exception>
    public async Task<EntityCollection> RetrieveMultipleAsync(QueryModel query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var response = await RunAsync(new RetrieveMultipleModelRequest(query), ct);
        return (EntityCollection)response.Results["EntityCollection"];
    }

    /// <summary>
    /// The secured columns the resolved caller cannot read through its field security profiles, for each of
    /// <paramref name="entities"/> (logical names; unknown ones have none); empty when field security does not restrict the
    /// caller (ruling F1). A front end uses it to tell a column the caller may not see (which the organization service returns as
    /// null, ruling F3) from an empty one; field shares are not in it: a column a share grants on a record comes back with its value.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 when the caller has no systemuser.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> UnreadableColumnsAsync(IEnumerable<string> entities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var caller = await callerResolver.ResolveAsync(ct);
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var set = await fields.ForAsync(caller, entity, ct);
            if (ReferenceEquals(set, Xrmd.Security.FieldPermissionSet.Unrestricted)) return new Dictionary<string, IReadOnlySet<string>>();
            if (set.UnreadableSecured.Count > 0) result[entity] = set.UnreadableSecured;
        }
        return result;
    }

    /// <summary>Runs a data message through the pipeline as the resolved caller, at depth 1.</summary>
    /// <exception cref="XrmdFaultException">403 when the caller has no systemuser, else the pipeline's faults.</exception>
    private async Task<OrganizationResponse> RunAsync(OrganizationRequest request, CancellationToken ct)
    {
        var caller = await callerResolver.ResolveAsync(ct);
        var session = changeSet?.Session;
        if (session is not null) RefuseElasticInChangeSet(request);
        return await executor.ExecuteAsync(request, new InvocationContext(caller, caller.UserId, 1, null, session, Guid.NewGuid()), ct);
    }

    /// <summary>
    /// The messages that write outside the pipeline, each in a transaction of its own, cannot join a change set (#130): refused
    /// with a 400 rather than run outside it.
    /// </summary>
    private static readonly HashSet<string> OwnTransactionMessages = new(StringComparer.Ordinal)
    {
        "ImportSolution", "ImportSolutionAsync", "ExecuteAsync", "UninstallSolutionAsync", "StageAndUpgrade", "StageAndUpgradeAsync",
        "DeleteAndPromote", "DeleteAndPromoteAsync",
    };

    private void RefuseOutsideChangeSet(OrganizationRequest request)
    {
        if (changeSet?.Session is null) return;
        var name = request.RequestName;
        if (OwnTransactionMessages.Contains(name) || AuditMessages.IsAuditMessage(name) || AutoNumberMessages.IsAutoNumberMessage(name)
            || MetadataOperations.IsMetadataMessage(name))
            throw XrmdFaultException.InvalidArgument($"The {name} message cannot run in a change set: it commits in a transaction of its own.");
    }

    /// <summary>Learn: elastic tables do not support multi-record transactions, so a change set cannot write one (#130).</summary>
    private void RefuseElasticInChangeSet(OrganizationRequest request)
    {
        var table = request.Parameters.TryGetValue("Target", out var target)
            ? target switch { Entity e => e.LogicalName, EntityReference r => r.LogicalName, _ => null }
            : request.Parameters.TryGetValue(BulkTargets.TargetsParameter, out var targets) && targets is EntityCollection c ? c.EntityName : null;
        if (table is not null && ops.Store.GetEntity(table) is { } m && ElasticTables.IsElastic(m))
            throw XrmdFaultException.InvalidArgument($"The elastic table '{table}' does not support transactions: it cannot be written in a change set.");
    }

    /// <summary>
    /// Upserts <paramref name="target"/>: the <c>Upsert</c> message, as <see cref="ExecuteAsync"/> runs it. With
    /// <paramref name="createOnly"/> an existing row of a standard table is a 412 DuplicateRecord, raised inside the Upsert's
    /// transaction after the id lock (Web API <c>If-None-Match: *</c>, #76 ruling B11); the mark is not a request parameter.
    /// </summary>
    /// <returns>The response with <c>RecordCreated</c> and <c>Target</c>.</returns>
    public Task<UpsertResponse> UpsertAsync(Entity target, bool createOnly, CancellationToken ct = default) =>
        UpsertAsync(target, createOnly, null, ct);

    /// <inheritdoc cref="UpsertAsync(Entity, bool, CancellationToken)"/>
    /// <param name="suppressDuplicateDetection">The duplicate check of the inner Create or Update; null leaves the default.</param>
    public async Task<UpsertResponse> UpsertAsync(Entity target, bool createOnly, bool? suppressDuplicateDetection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var request = new UpsertRequest { Target = target };
        if (suppressDuplicateDetection is { } suppress) request[Core.Duplicates.DuplicateFinder.SuppressParameter] = suppress;
        if (createOnly) StandardUpsert.MarkCreateOnly(request);
        return (UpsertResponse)await ExecuteAsync(request, ct);
    }

    /// <summary>
    /// Supports <see cref="CreateRequest"/>, <see cref="RetrieveRequest"/>, <see cref="UpdateRequest"/>,
    /// <see cref="DeleteRequest"/>, <see cref="RetrieveMultipleRequest"/>, <see cref="WhoAmIRequest"/>, <see cref="RetrieveEntityRequest"/> (by logical name;
    /// the whole definition whatever the filters, relationships derived from lookups) and
    /// <see cref="RetrieveAttributeRequest"/> (by logical names), dispatched on <see cref="OrganizationRequest.RequestName"/>
    /// so a plain <see cref="OrganizationRequest"/> works too. Assign, SetState (as an Update), Associate/Disassociate
    /// (one-to-many and the security relationships), GrantAccess/ModifyAccess/RevokeAccess,
    /// AddPrivilegesRole/ReplacePrivilegesRole/RemovePrivilegeRole and custom actions (custom <c>sdkmessage</c> rows) go to the
    /// pipeline as they are; the metadata messages (CreateEntity, CreateAttribute, CreateOneToMany, CreateManyToMany,
    /// CreateOptionSet, Update* (UpdateOptionValue included), InsertOptionValue, Delete*, RetrieveRelationship, RetrieveOptionSet, RetrieveAllOptionSets,
    /// RetrieveAllEntities, PublishXml, PublishAllXml) go to <see cref="MetadataOperations"/>, each write in a transaction
    /// of its own; ImportSolution goes to the <see cref="SolutionImporter"/> (its own transaction, <c>importjob</c> log);
    /// ImportSolutionAsync and ExecuteAsync (of ImportSolution, StageAndUpgrade or DeleteAndPromote; any other request is a 400) queue the import as a system
    /// job (<see cref="SolutionImporter.EnqueueAsync"/>); ExportSolution goes to the <see cref="SolutionExporter"/> (unmanaged
    /// only, a data read); RetrieveFormattedImportJobResults returns the <c>importjob</c> log as an Excel workbook;
    /// LocalTimeFromUtcTime and UtcTimeFromLocalTime go to <see cref="TimeZoneMessages"/> (#131);
    /// RetrieveAuditDetails, RetrieveRecordChangeHistory, RetrieveAttributeChangeHistory, DeleteRecordChangeHistory and
    /// DeleteAuditData go to <see cref="AuditMessages"/>; UninstallSolutionAsync queues the uninstall of a managed solution as a
    /// system job (<see cref="SolutionUninstaller.EnqueueAsync"/>, and Delete of a managed <c>solution</c> row uninstalls it,
    /// <see cref="DeleteAsync"/>); StageAndUpgrade, StageAndUpgradeAsync, DeleteAndPromote and DeleteAndPromoteAsync go to the
    /// <see cref="SolutionPromoter"/> (plan 2.6, ruling M11); CloneAsPatch and CloneAsSolution are refused (400: solution patches
    /// are not supported yet); Upsert goes to the pipeline (plan 3.4, ruling E6; on a standard table #76, ruling B10); CreateMultiple,
    /// UpdateMultiple, UpsertMultiple and DeleteMultiple run each target as the single-record message through this method on an
    /// elastic table (<see cref="ElasticBulkMessages"/>, ruling E5); on a standard table the first three run as one pipeline
    /// execution, all or nothing (#76, <see cref="BulkTargets.Prepare"/>), and DeleteMultiple is refused; any other request throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DemandNotLocked(request.RequestName);
        RefuseOutsideChangeSet(request);
        switch (request.RequestName)
        {
            case "Create":
                var created = Parameter<Entity>(request, "Target");
                ops.ValidateWrite(created, isCreate: true);
                var create = new CreateRequest { Target = created };
                if (request.Parameters.TryGetValue("SolutionUniqueName", out var solution) && solution is string { Length: > 0 })
                    create["SolutionUniqueName"] = solution;
                CopySuppressDuplicateDetection(request, create);
                var createdResponse = await RunAsync(ElasticRecords.CarryParameters(request, create), ct);
                return ElasticRecords.CarrySessionToken(createdResponse,
                    new CreateResponse { ResponseName = "Create", Results = { ["id"] = (Guid)createdResponse.Results["id"] } });
            case "Retrieve":
                UnmaskedData.Refuse(request);
                var target = Parameter<EntityReference>(request, "Target");
                var entity = ElasticRecords.NeedsKey(ops.EntityOf(target.LogicalName), target)
                    // Plan 3.4: a row by key or of an elastic table keeps its key attributes and partition.
                    ? (Entity)(await RunAsync(ElasticRecords.CarryParameters(request,
                        new RetrieveRequest { Target = target, ColumnSet = Parameter<ColumnSet>(request, "ColumnSet") }), ct)).Results["Entity"]
                    : await RetrieveAsync(target.LogicalName, target.Id, Parameter<ColumnSet>(request, "ColumnSet"), ct);
                return new RetrieveResponse { ResponseName = "Retrieve", Results = { ["Entity"] = entity } };
            case "Update":
                var updated = Parameter<Entity>(request, "Target");
                ops.ValidateWrite(updated, isCreate: false);
                var update = new UpdateRequest { Target = updated };
                CopySuppressDuplicateDetection(request, update);
                return ElasticRecords.CarrySessionToken(await RunAsync(ElasticRecords.CarryParameters(request, update), ct),
                    new UpdateResponse { ResponseName = "Update" });
            case "Delete":
                var deleted = Parameter<EntityReference>(request, "Target");
                if (ElasticRecords.NeedsKey(ops.EntityOf(deleted.LogicalName), deleted))
                    await RunAsync(ElasticRecords.CarryParameters(request, new DeleteRequest { Target = deleted }), ct); // plan 3.4
                else
                    await DeleteAsync(deleted.LogicalName, deleted.Id, ct);
                return new DeleteResponse { ResponseName = "Delete" };
            case ElasticUpsert.MessageName:
                // Plan 3.4, ruling E6 (elastic) and #76, ruling B10 (standard: an inner Create or Update in the pipeline).
                var upserted = Parameter<Entity>(request, "Target");
                var upsert = new UpsertRequest { Target = upserted };
                if (StandardUpsert.IsCreateOnly(request)) StandardUpsert.MarkCreateOnly(upsert); // the mark follows the rebuilt request
                if (ElasticTables.IsElastic(ops.EntityOf(upserted.LogicalName)))
                {
                    // The elastic Upsert replaces the row and never reads the create-only mark: refuse it rather than ignore it.
                    if (StandardUpsert.IsCreateOnly(request))
                        throw XrmdFaultException.InvalidArgument(
                            $"Create-only upsert is not supported for the elastic table '{upserted.LogicalName}'.");
                    ops.ValidateWrite(upserted, isCreate: true);
                }
                else
                    // Standard: the inner Create or Update validates the target with its own rules (key attributes refused there)
                    // and gets the optional parameters a direct Create or Update would get (StandardUpsert.CarryParameters).
                    CopySuppressDuplicateDetection(request, upsert);
                return await RunAsync(ElasticRecords.CarryParameters(request, upsert), ct);
            case var name when ElasticBulkMessages.IsBulkMessage(name):
                var bulk = BulkTargets.Prepare(request, ops.Store, out var bulkTable);
                if (ElasticTables.IsElastic(bulkTable) || bulk.RequestName == BulkTargets.DeleteMultiple)
                    // Plan 3.4, ruling E5: each target as a single-record message through this same entry.
                    return await ElasticBulkMessages.ExecuteAsync(request, single => ExecuteAsync(single, ct), ops.Store, ct);
                // #76, ruling B1: one pipeline execution, all or nothing, with the optional parameters a single message gets.
                // Each target is validated first, as a direct Create or Update is (no plug-in sees an invalid one); an UpsertMultiple
                // target is validated by the branch its inner Upsert takes (ruling B10).
                if (bulk.RequestName != BulkTargets.UpsertMultiple)
                    foreach (var bulkTarget in ((EntityCollection)bulk[BulkTargets.TargetsParameter]).Entities)
                        ops.ValidateWrite(bulkTarget, isCreate: bulk.RequestName == BulkTargets.CreateMultiple);
                CopySuppressDuplicateDetection(request, bulk);
                return await RunAsync(ElasticRecords.CarryParameters(request, bulk), ct);
            case CoreOperations.CalculateRollupFieldMessage:
                // Plan 2.12b: a Target given by key is resolved (or refused) before the security check, which then reads the same row.
                if (request.Parameters.TryGetValue("Target", out var rollupTarget) && rollupTarget is EntityReference rollupRow)
                    ops.NormalizeRollupTarget(rollupRow);
                return await RunAsync(request, ct);
            case "RetrieveMultiple":
                UnmaskedData.Refuse(request);
                var collection = await RetrieveMultipleAsync(Parameter<QueryBase>(request, "Query"), request, ct);
                return new RetrieveMultipleResponse { ResponseName = "RetrieveMultiple", Results = { ["EntityCollection"] = collection } };
            case "WhoAmI":
                return await WhoAmIAsync(ct);
            case "RetrieveUserPrivileges":
                await using (var privilegesConn = await factory.OpenAsync(ct))
                    return await ops.RetrieveUserPrivilegesAsync(request, await callerResolver.ResolveAsync(ct),
                        new DataSession(privilegesConn, null), ct);
            case CoreOperations.RetrieveAppComponents:
                await using (var componentsConn = await factory.OpenAsync(ct))
                    return await ops.RetrieveAppComponentsAsync(request, await callerResolver.ResolveAsync(ct),
                        new DataSession(componentsConn, null), ct);
            case InitializeFrom.MessageName:
                await using (var initializeConn = await factory.OpenAsync(ct))
                    return await ops.InitializeFromAsync(request, await callerResolver.ResolveAsync(ct), new DataSession(initializeConn, null), ct);
            case CoreOperations.RetrieveUnpublished:
                // No unpublished layer: a Retrieve of the published row, through the pipeline as any Retrieve.
                var (unpublished, unpublishedColumns) = CoreOperations.UnpublishedTarget(request);
                return new RetrieveUnpublishedResponse
                {
                    ResponseName = CoreOperations.RetrieveUnpublished,
                    Results = { ["Entity"] = await RetrieveAsync(unpublished.LogicalName, unpublished.Id, unpublishedColumns, ct) },
                };
            case var name when TimeZoneMessages.IsTimeZoneMessage(name):
                // #131: plain conversions, outside the pipeline, for organization members.
                await callerResolver.ResolveAsync(ct);
                return TimeZoneMessages.Execute(request);
            case var name when AccessMessages.IsAccessMessage(name):
                // Plan 3.5 UI-1b task 2: read messages, outside the pipeline (no plug-in step on them), in a connection of their own.
                return await access.ExecuteAsync(request, await callerResolver.ResolveAsync(ct), ct);
            case "RetrieveEntity":
                await callerResolver.ResolveAsync(ct); // metadata is for organization members only
                return ops.RetrieveEntity(request);
            case "RetrieveAttribute":
                await callerResolver.ResolveAsync(ct);
                return ops.RetrieveAttribute(request);
            case "ImportSolution":
                // Outside the pipeline, in a transaction of its own (metadata before data); prvImportCustomization is demanded first.
                var (file, options) = SolutionImporter.ReadRequest(request);
                await importer.ImportInOwnTransactionAsync(await callerResolver.ResolveAsync(ct), file, options, ct);
                return new ImportSolutionResponse { ResponseName = "ImportSolution" };
            case "ExportSolution":
                // A plain data read outside the pipeline; the options are checked and prvExportCustomization demanded first.
                var exported = await exporter.ExportAsync(await callerResolver.ResolveAsync(ct), request, ct);
                return new ExportSolutionResponse { ResponseName = "ExportSolution", Results = { ["ExportSolutionFile"] = exported } };
            case "ImportSolutionAsync":
                var (queuedFile, queuedOptions) = SolutionImporter.ReadRequest(request);
                var (operationId, importJobId) = await importer.EnqueueAsync(await callerResolver.ResolveAsync(ct), queuedFile, queuedOptions, ct);
                return new ImportSolutionAsyncResponse
                {
                    ResponseName = "ImportSolutionAsync",
                    Results = { ["AsyncOperationId"] = operationId, ["ImportJobKey"] = importJobId.ToString() },
                };
            case "ExecuteAsync":
                return await ExecuteAsyncAsync(request, ct);
            case "UninstallSolutionAsync":
                // Queued as a system job (asyncoperation 208, plan 2.6 ruling M9); the job uninstalls with the SDK's rules.
                var uninstalled = request.Parameters.TryGetValue("SolutionUniqueName", out var uninstallName) && uninstallName is string { Length: > 0 } text
                    ? text
                    : throw XrmdFaultException.InvalidArgument("Required parameter 'SolutionUniqueName' of request 'UninstallSolutionAsync' is missing.");
                var uninstallJob = await uninstaller.EnqueueAsync(await callerResolver.ResolveAsync(ct), uninstalled, ct);
                return new UninstallSolutionAsyncResponse { ResponseName = "UninstallSolutionAsync", Results = { ["AsyncOperationId"] = uninstallJob } };
            case "StageAndUpgrade":
                // Staged and promoted in one transaction (plan 2.6, ruling M11); a solution that is not installed is installed.
                var (upgradeFile, upgradeOptions) = SolutionImporter.ReadRequest(request);
                var upgraded = await promoter.StageAndUpgradeAsync(await callerResolver.ResolveAsync(ct), upgradeFile, upgradeOptions, ct);
                return new StageAndUpgradeResponse { ResponseName = "StageAndUpgrade", Results = { ["SolutionId"] = upgraded.SolutionId } };
            case "StageAndUpgradeAsync":
                var (queuedUpgradeFile, queuedUpgradeOptions) = SolutionImporter.ReadRequest(request);
                var (upgradeJob, upgradeImportJob) = await promoter.EnqueueStageAndUpgradeAsync(await callerResolver.ResolveAsync(ct),
                    queuedUpgradeFile, queuedUpgradeOptions, ct);
                return new StageAndUpgradeAsyncResponse
                {
                    ResponseName = "StageAndUpgradeAsync",
                    Results = { ["AsyncOperationId"] = upgradeJob, ["ImportJobKey"] = upgradeImportJob.ToString() },
                };
            case "DeleteAndPromote":
                var (promotedId, _) = await promoter.PromoteInOwnTransactionAsync(await callerResolver.ResolveAsync(ct), PromoteName(request),
                    new PromoteOptions(DropData: false, DryRun: false, FromSdk: true), ct);
                return new DeleteAndPromoteResponse { ResponseName = "DeleteAndPromote", Results = { ["SolutionId"] = promotedId } };
            case "DeleteAndPromoteAsync":
                var promoteJob = await promoter.EnqueueAsync(await callerResolver.ResolveAsync(ct), PromoteName(request), ct);
                return new DeleteAndPromoteAsyncResponse { ResponseName = "DeleteAndPromoteAsync", Results = { ["AsyncOperationId"] = promoteJob } };
            case "CloneAsPatch" or "CloneAsSolution":
                // Solution patches are deferred (plan 2.6, ruling M12): refused with the reason, not as an unknown message.
                await callerResolver.ResolveAsync(ct);
                throw XrmdFaultException.InvalidArgument($"{request.RequestName} is not supported: {PatchDetector.NotSupported}.");
            case "RetrieveFormattedImportJobResults":
                return await RetrieveFormattedImportJobResultsAsync(request, ct);
            case var name when AuditMessages.IsAuditMessage(name):
                // Outside the pipeline, as Dataverse registers no plug-in step on them; each in a connection of its own.
                return await audit.ExecuteAsync(request, await callerResolver.ResolveAsync(ct), null, ct);
            case var name when AutoNumberMessages.IsAutoNumberMessage(name):
                // Outside the pipeline, like the metadata messages.
                return await autoNumbers.ExecuteAsync(request, await callerResolver.ResolveAsync(ct), null, ct);
            case var name when MetadataOperations.IsMetadataMessage(name):
                // Outside the pipeline: no plug-in step can write data before the metadata is written.
                return await metadata.ExecuteAsync(request, await callerResolver.ResolveAsync(ct), null, ct);
            default:
                // Assign, SetState, Associate, Disassociate, the sharing messages, custom actions; the pipeline refuses any other message.
                return await RunAsync(request, ct);
        }
    }

    /// <summary>
    /// Carries the optional parameter <c>SuppressDuplicateDetection</c> of a Create or Update over to the request the pipeline runs
    /// (plan 2.15, ruling D1); the core checks its value.
    /// </summary>
    private static void CopySuppressDuplicateDetection(OrganizationRequest from, OrganizationRequest to)
    {
        if (from.Parameters.TryGetValue(Duplicates.DuplicateFinder.SuppressParameter, out var suppress))
            to[Duplicates.DuplicateFinder.SuppressParameter] = suppress;
    }

    /// <summary>
    /// ExecuteAsync: its <c>Request</c> is queued as its async form would queue it: ImportSolution as ImportSolutionAsync
    /// (<see cref="SolutionImporter.EnqueueAsync"/>, system job 203), StageAndUpgrade as StageAndUpgradeAsync (211) and
    /// DeleteAndPromote as DeleteAndPromoteAsync (207, <see cref="SolutionPromoter"/>; plan 2.6, ruling M11); <c>AsyncJobId</c>
    /// is the <c>asyncoperationid</c>. Merge, which Dataverse also runs this way, and any other request are refused (400).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing request or one xrmverse does not queue; the faults of the async
    /// form.</exception>
    private async Task<OrganizationResponse> ExecuteAsyncAsync(OrganizationRequest request, CancellationToken ct)
    {
        var inner = request.Parameters.TryGetValue("Request", out var value) && value is OrganizationRequest r
            ? r
            : throw XrmdFaultException.InvalidArgument("Required parameter 'Request' of request 'ExecuteAsync' is missing.");
        Guid operationId;
        switch (inner.RequestName)
        {
            case "ImportSolution":
                var (file, options) = SolutionImporter.ReadRequest(inner);
                (operationId, _) = await importer.EnqueueAsync(await callerResolver.ResolveAsync(ct), file, options, ct);
                break;
            case "StageAndUpgrade":
                var (upgradeFile, upgradeOptions) = SolutionImporter.ReadRequest(inner);
                (operationId, _) = await promoter.EnqueueStageAndUpgradeAsync(await callerResolver.ResolveAsync(ct), upgradeFile, upgradeOptions, ct);
                break;
            case "DeleteAndPromote":
                operationId = await promoter.EnqueueAsync(await callerResolver.ResolveAsync(ct), PromoteName(inner), ct);
                break;
            default:
                // Merge, which Dataverse also runs this way, is not queued by xrmverse: one wording for every refusal.
                throw XrmdFaultException.InvalidArgument(
                    $"Request '{inner.RequestName}' cannot be executed asynchronously by xrmverse: ExecuteAsync supports ImportSolution, StageAndUpgrade and DeleteAndPromote only.");
        }
        return new ExecuteAsyncResponse { ResponseName = "ExecuteAsync", Results = { ["AsyncJobId"] = operationId } };
    }

    /// <summary>The <c>UniqueName</c> of a DeleteAndPromote or DeleteAndPromoteAsync request.</summary>
    /// <exception cref="XrmdFaultException">400 when it is missing or empty.</exception>
    private static string PromoteName(OrganizationRequest request) =>
        request.Parameters.TryGetValue("UniqueName", out var value) && value is string { Length: > 0 } name
            ? name
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'UniqueName' of request '{request.RequestName}' is missing.");

    /// <summary>
    /// RetrieveFormattedImportJobResults: the <c>data</c> of the <c>importjob</c> (the raw import log, which stays as it is)
    /// read through the pipeline as a Retrieve of the caller (read privilege and access on <c>importjob</c>; 404 when it does
    /// not exist), returned in <c>FormattedResults</c> as an Excel workbook (<see cref="ImportJobLog.ToSpreadsheet"/>), as
    /// Learn says Dataverse does ("in Microsoft Excel format"; the column layout is not verified).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>ImportJobId</c>; 404 for an unknown import job; the Retrieve's faults.</exception>
    private async Task<OrganizationResponse> RetrieveFormattedImportJobResultsAsync(OrganizationRequest request, CancellationToken ct) =>
        CoreOperations.FormattedImportJobResults(
            await RetrieveAsync("importjob", CoreOperations.ImportJobIdParameter(request), new ColumnSet("data"), ct));

    private static T Parameter<T>(OrganizationRequest request, string name) where T : class =>
        CoreOperations.Parameter<T>(request, name);

    private async Task<WhoAmIResponse> WhoAmIAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var caller = await CallerAsync(conn, ct);
        return CoreOperations.WhoAmI(caller, await OrganizationIdAsync(conn, ct));
    }

    /// <exception cref="XrmdFaultException">403 NotMemberOfOrganization when no systemuser matches.</exception>
    private Task<Caller> CallerAsync(DbConnection conn, CancellationToken ct) => callerResolver.ResolveAsync(conn, ct);

    private async Task<Guid> OrganizationIdAsync(DbConnection conn, CancellationToken ct)
    {
        if (_organizationId is { } cached) return cached;
        var d = SqlDialects.For(conn);
        await using var cmd = conn.Command(d.Limit($"SELECT organizationid FROM {d.DataTable("organization")}", "1"));
        var id = await cmd.ExecuteScalarAsync(ct) as Guid?
                 ?? throw new InvalidOperationException("The organization has not been seeded (data.organization is empty).");
        _organizationId = id;
        return id;
    }
}
