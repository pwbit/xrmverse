using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Elastic;

/// <summary>
/// Stage 30 of <c>Upsert</c> on an elastic table (plan 3.4, ruling E6): the row of the target's id and partition is created when
/// it is not found, else replaced: every column the caller can write and the target does not carry becomes null. The core
/// Create or Update runs directly (as the <c>Upsert</c> message, so no Create or Update step fires), with its audit and change
/// tracking. A standard table's Upsert never reaches it: the pipeline runs it as an inner Create or Update (#76, ruling B10).
/// </summary>
public sealed class ElasticUpsert(CoreOperations core)
{
    /// <summary>The message name.</summary>
    public const string MessageName = "Upsert";

    /// <summary>
    /// Creates or replaces the row of <paramref name="target"/> (its id: <see cref="Entity.Id"/>, its primary key attribute or its
    /// key attributes; its partition, as for an Update: the key attribute <c>partitionid</c>, else the <c>partitionId</c> parameter
    /// of <paramref name="request"/> when not null, else the <c>partitionid</c> value). A target without id is created with a new one.
    /// </summary>
    /// <returns><c>RecordCreated</c>, <c>Target</c> (a reference to the row) and the session token.</returns>
    /// <exception cref="NotSupportedException">The table is a standard one (the pipeline handles that Upsert itself).</exception>
    /// <exception cref="XrmdFaultException">400 for invalid input; 412 DuplicateRecord when the id is stored in another
    /// partition (ruling E1); 400 when the <c>partitionid</c> value differs from the partition the request names; the faults of
    /// the Create or Update.</exception>
    public async Task<UpsertResponse> ExecuteAsync(DataSession s, Caller caller, Entity target, OrganizationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(target);
        var m = core.EntityOf(target.LogicalName);
        if (!ElasticTables.IsElastic(m)) throw new NotSupportedException($"Request '{MessageName}' is not supported yet.");
        core.ValidateWrite(target, isCreate: true); // the id and partition from the key attributes, the create rules
        var pk = SystemAttributes.PrimaryIdName(m);
        var id = target.Id != Guid.Empty ? target.Id : target.GetAttributeValue<Guid?>(pk) ?? Guid.Empty;
        var partition = ElasticRecordKey.From(m, id, target.KeyAttributes, request, target).PartitionId;
        // A value naming another partition than the key or the parameter is refused, never moved or dropped (as an Update).
        if (target.Attributes.TryGetValue(ElasticTables.PartitionId, out var given) && !Equals(given, partition))
            throw XrmdFaultException.InvalidArgument(
                $"The partitionid value of the '{m.LogicalName}' record differs from the partition the request names.");

        bool created;
        if (id == Guid.Empty || !(await ElasticRecords.FindAsync(s, m, new ElasticRecordKey(id, partition), lockRow: true, ct)).Found)
        {
            // Not found in this partition: created (an id stored in another partition is the Create's DuplicateRecord, ruling E1).
            var row = new Entity(m.LogicalName, id);
            foreach (var (name, value) in target.Attributes) row[name] = value;
            if (partition is not null) row[ElasticTables.PartitionId] = partition;
            id = await core.CreateAsync(s, caller, row, ct);
            created = true;
        }
        else
        {
            await core.UpdateAsync(s, caller, Replacement(m, target, id), ct);
            created = false;
        }
        var response = new UpsertResponse
        {
            ResponseName = MessageName,
            Results = { ["RecordCreated"] = created, ["Target"] = new EntityReference(m.LogicalName, id) },
        };
        return await ElasticRecords.WithSessionTokenAsync(s, m, id, response, ct);
    }

    /// <summary>
    /// The Update that replaces the row <paramref name="id"/> by <paramref name="target"/>: its values, and null for each column
    /// of the table the caller can write that it does not carry (custom columns and <c>ttlinseconds</c>; the id, the partition,
    /// the owner, files and images are kept).
    /// </summary>
    private static Entity Replacement(EntityMetadata m, Entity target, Guid id)
    {
        var pk = SystemAttributes.PrimaryIdName(m);
        var update = new Entity(m.LogicalName, id);
        foreach (var name in ElasticTables.UpsertClearedColumns(m, target)) update[name] = null;
        foreach (var (name, value) in target.Attributes)
            if (name != pk && name != ElasticTables.PartitionId) update[name] = value;
        return update;
    }
}
