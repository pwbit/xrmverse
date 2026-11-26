using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Query;
using Xrmd.Query.Model;

namespace Xrmd.Core.Elastic;

/// <summary>
/// The identity of a row of an elastic table (plan 3.4, ruling E1): its id and its partition id, null for a row stored
/// without one. A row is found only with the partition it was stored with: a row with a partition id only when the same one is
/// given, a row without one only when none (or null) is given.
/// </summary>
/// <param name="Id">The row's id (the primary key stays unique on its own, ruling E1).</param>
/// <param name="PartitionId">The partition id given, null when none was.</param>
public readonly record struct ElasticRecordKey(Guid Id, string? PartitionId)
{
    /// <summary>The optional request parameter that names the partition of the row a request reads or writes.</summary>
    public const string PartitionIdParameter = "partitionId";

    /// <summary>
    /// The key of a request's row: the partition comes from <paramref name="keys"/> (<c>KeyAttributes</c> holding the primary id
    /// and/or <c>partitionid</c>), else from the request's <c>partitionId</c> parameter (when not null), else from the
    /// <c>partitionid</c> value of <paramref name="target"/> (an Update names its row so, as Learn's examples do), else there is
    /// none. A primary id in <paramref name="keys"/> fills an empty <paramref name="id"/>.
    /// </summary>
    /// <param name="target">The record an Update writes, whose <c>partitionid</c> value names the partition last; null for the
    /// other messages.</param>
    /// <exception cref="XrmdFaultException">400 for any other key attribute, key attributes on a standard table, a key id that
    /// differs from <paramref name="id"/>, a partition id that is not a string or not valid
    /// (<see cref="ElasticTables.DemandValidPartitionId"/>).</exception>
    public static ElasticRecordKey From(EntityMetadata m, Guid id, KeyAttributeCollection? keys, OrganizationRequest? request,
        Entity? target = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        var pk = SystemAttributes.PrimaryIdName(m);
        string? partition = null;
        var partitionFromKeys = false;
        if (keys is { Count: > 0 })
        {
            DemandKeys(m, keys);
            if (keys.TryGetValue(pk, out var keyId))
            {
                var given = AsGuid(m, pk, keyId);
                if (id != Guid.Empty && id != given)
                    throw XrmdFaultException.InvalidArgument(
                        $"The record id {id} and the key attribute {pk} = {given} of entity '{m.LogicalName}' differ.");
                id = given;
            }
            if (keys.TryGetValue(ElasticTables.PartitionId, out var keyPartition))
            {
                partition = AsPartition(keyPartition);
                partitionFromKeys = true;
            }
        }
        var partitionFromParameter = false;
        if (!partitionFromKeys && request is not null)
            foreach (var (name, value) in request.Parameters)
                if (string.Equals(name, PartitionIdParameter, StringComparison.OrdinalIgnoreCase) && value is not null)
                {
                    partition = AsPartition(value);
                    partitionFromParameter = true;
                }
        if (!partitionFromKeys && !partitionFromParameter && target is not null
            && target.Attributes.TryGetValue(ElasticTables.PartitionId, out var attribute))
            partition = AsPartition(attribute);
        ElasticTables.DemandValidPartitionId(partition);
        return new ElasticRecordKey(id, partition);
    }

    /// <summary>
    /// The key of <paramref name="target"/> on an elastic table, null on a standard one (whose rows are found by id alone).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 as <see cref="From"/>, so for key attributes on a standard table.</exception>
    public static ElasticRecordKey? Of(EntityMetadata m, EntityReference target, OrganizationRequest? request)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(target);
        if (!ElasticTables.IsElastic(m))
        {
            if (target.KeyAttributes is { Count: > 0 } keys) DemandKeys(m, keys);
            return null;
        }
        return From(m, target.Id, target.KeyAttributes, request);
    }

    /// <summary>
    /// The conditions that find the row: <c>{primary id} = Id</c> and <c>partitionid = PartitionId</c> (or null). On the model
    /// (S.2e3b: the read path).
    /// </summary>
    public ConditionNode[] Conditions(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return
        [
            new ConditionNode(null, SystemAttributes.PrimaryIdName(table), FilterOperator.Equal, [Id]),
            PartitionId is null
                ? new ConditionNode(null, ElasticTables.PartitionId, FilterOperator.Null, [])
                : new ConditionNode(null, ElasticTables.PartitionId, FilterOperator.Equal, [PartitionId]),
        ];
    }

    /// <summary>
    /// Fills the ids a write names by key, before anything reads them (the security checks, the validation): the id of
    /// <paramref name="entity"/> from its <c>KeyAttributes</c>, and on a Create its <c>partitionid</c> from them too; the id of
    /// each lookup value given by key (a reference to an elastic row). The key attributes stay, so the partition is still known.
    /// </summary>
    /// <param name="entityOf">The definition of a referenced table (null when unknown: the validation refuses it).</param>
    /// <exception cref="XrmdFaultException">400 as <see cref="From"/>; 400 when the key's partition differs from the
    /// <c>partitionid</c> value of a Create; 400 when the ids the entity gives differ (<see cref="ElasticTables.DemandOneRowId"/>).</exception>
    public static void Normalize(EntityMetadata m, Entity entity, bool isCreate, Func<string, EntityMetadata?> entityOf)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(entityOf);
        ElasticTables.DemandOneRowId(m, entity); // final review I4: one row, whichever id the guard or the core reads
        if (entity.KeyAttributes is { Count: > 0 } keys)
        {
            var key = From(m, entity.Id, keys, null);
            if (key.Id != Guid.Empty) entity.Id = key.Id;
            if (isCreate && keys.ContainsKey(ElasticTables.PartitionId))
            {
                if (entity.Attributes.TryGetValue(ElasticTables.PartitionId, out var given) && !Equals(given, key.PartitionId))
                    throw XrmdFaultException.InvalidArgument(
                        $"The key attribute partitionid and the partitionid value of the new '{m.LogicalName}' record differ.");
                entity[ElasticTables.PartitionId] = key.PartitionId;
            }
        }
        foreach (var value in entity.Attributes.Values)
            if (value is EntityReference { KeyAttributes.Count: > 0 } reference && entityOf(reference.LogicalName ?? "") is { } target)
                Normalize(target, reference);
    }

    /// <summary>Fills the id of <paramref name="reference"/> from its <c>KeyAttributes</c>.</summary>
    /// <exception cref="XrmdFaultException">400 as <see cref="From"/>.</exception>
    public static void Normalize(EntityMetadata m, EntityReference reference)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.KeyAttributes is not { Count: > 0 } keys) return;
        var key = From(m, reference.Id, keys, null);
        if (key.Id != Guid.Empty) reference.Id = key.Id;
    }

    /// <summary>
    /// Key attributes name an elastic row only, by its primary id and <c>partitionid</c>: xrmverse has no other alternate key.
    /// </summary>
    private static void DemandKeys(EntityMetadata m, KeyAttributeCollection keys)
    {
        if (!ElasticTables.IsElastic(m))
            throw XrmdFaultException.InvalidArgument(
                $"Key attributes ({string.Join(", ", keys.Keys)}) are not supported for entity '{m.LogicalName}': xrmverse supports alternate keys only for elastic tables.");
        var pk = SystemAttributes.PrimaryIdName(m);
        if (keys.Keys.FirstOrDefault(k => k != pk && k != ElasticTables.PartitionId) is { } other)
            throw XrmdFaultException.InvalidArgument(
                $"Key attribute '{other}' is not a key of the elastic table '{m.LogicalName}': its only key is {pk} and {ElasticTables.PartitionId}.");
    }

    private static Guid AsGuid(EntityMetadata m, string pk, object? value) => value switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out var parsed) => parsed,
        _ => throw XrmdFaultException.InvalidArgument($"The key attribute {pk} of entity '{m.LogicalName}' must be a Guid."),
    };

    private static string? AsPartition(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => throw XrmdFaultException.InvalidArgument("Invalid partitionid value: it must be a string."),
    };
}
