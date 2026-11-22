using System.Data;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Elastic;

/// <summary>
/// The data steps of the single-record messages on an elastic table (plan 3.4) that <see cref="CoreOperations"/> and the
/// <see cref="OrganizationService"/> call: the row checked in its partition under an update lock before an Update or a Delete,
/// the partition column of a lookup to an elastic row, the session token of a write and the request parameters carried from
/// the organization service to the pipeline. Nothing happens on a standard table, except that key attributes are refused.
/// </summary>
public static class ElasticRecords
{
    /// <summary>The result (SDK) and header (Web API) that carry the session token of a write on an elastic table.</summary>
    public const string SessionToken = "x-ms-session-token";

    private const string IdParameter = "@_id";
    private const string PartitionParameter = "@_partition";

    /// <summary>
    /// Before the Update of <paramref name="target"/> (stage 30): on an elastic table the row must exist in the partition the
    /// request names (the key attribute, the <c>partitionId</c> parameter, else the target's <c>partitionid</c> value; 404
    /// otherwise, Review Focus 1), locked until the end of the transaction, and its <c>partitionid</c> is not changed
    /// (<see cref="ElasticWriteRules.DemandUpdate"/>: a value differing from the stored one is refused, never moved). On a
    /// standard table only key attributes are refused.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist; 400 as <see cref="ElasticRecordKey.From"/> and
    /// <see cref="ElasticWriteRules.DemandUpdate"/>.</exception>
    public static async Task BeforeUpdateAsync(DataSession s, EntityMetadata m, Entity target, OrganizationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(target);
        if (!ElasticTables.IsElastic(m))
        {
            ElasticRecordKey.Of(m, new EntityReference(m.LogicalName, IdOf(m, target)) { KeyAttributes = target.KeyAttributes }, request);
            return;
        }
        ElasticTables.DemandOneRowId(m, target); // final review I4: the row the guard checked
        var key = ElasticRecordKey.From(m, IdOf(m, target), target.KeyAttributes, request, target);
        if (key.Id == Guid.Empty) return; // no id: the Update refuses it
        var stored = await LockInPartitionAsync(s, m, key, ct);
        ElasticWriteRules.DemandUpdate(m, target, stored);
    }

    /// <summary>
    /// Before the Delete of <paramref name="target"/> (stage 30): on an elastic table the row must exist in the partition the
    /// request names (404 otherwise), locked until the end of the transaction. Returns the id to delete.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist; 400 as <see cref="ElasticRecordKey.From"/>.</exception>
    public static async Task<Guid> BeforeDeleteAsync(DataSession s, EntityMetadata m, EntityReference target, OrganizationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ElasticRecordKey.Of(m, target, request) is not { } key) return target.Id;
        await LockInPartitionAsync(s, m, key, ct);
        return key.Id;
    }

    /// <summary>
    /// The row of <paramref name="key"/>, update-locked until the end of the transaction: its stored <c>partitionid</c>.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist when no row has this id in this partition.</exception>
    public static async Task<string?> LockInPartitionAsync(DataSession s, EntityMetadata m, ElasticRecordKey key, CancellationToken ct) =>
        await FindAsync(s, m, key, lockRow: true, ct) is (true, var stored)
            ? stored
            : throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, key.Id);

    /// <summary>
    /// Whether a row has the id and partition of <paramref name="key"/> (with <paramref name="lockRow"/>, update-locked until the
    /// end of the transaction, and its key range held when it does not exist), and its stored <c>partitionid</c>.
    /// </summary>
    public static async Task<(bool Found, string? Partition)> FindAsync(DataSession s, EntityMetadata m, ElasticRecordKey key, bool lockRow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        var d = s.Dialect;
        var column = d.Quote(ElasticTables.PartitionId);
        // Exact (ruling E1): a binary collation, whatever the column's; '=' still ignores trailing spaces on SQL Server, the byte
        // length does not.
        var partition = key.PartitionId is null
            ? $"{column} IS NULL"
            : $"{d.BinaryCollate(column)} = {PartitionParameter} AND {d.DataLength(column)} = {d.DataLength(PartitionParameter)}";
        // Plan 2.7b, Task 5: on PostgreSQL the key lock stands for the key range a missing row holds on SQL Server (an Upsert
        // then creates the row), taken first as every writer of the row does (CoreOperations' row lock).
        if (lockRow) await s.KeyLockAsync(m.LogicalName, key.Id, ct);
        var lockMode = RowLock.UpdateRowRange;
        await using var cmd = s.Command(
            $"SELECT {column} FROM {s.DataTable(m.LogicalName)}{(lockRow ? d.LockHint(lockMode) : "")} " +
            $"WHERE {d.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter} AND {partition}{(lockRow ? d.LockClause(lockMode) : "")}");
        cmd.AddParameter(IdParameter, DbType.Guid, key.Id);
        if (key.PartitionId is not null)
            cmd.AddParameter(PartitionParameter, DbType.String, key.PartitionId, ElasticTables.PartitionIdMaxLength);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return (false, null);
        return (true, rd.IsDBNull(0) ? null : rd.GetString(0));
    }

    /// <summary>
    /// The partition column (<c>{lookup}pid</c>) of each lookup to an elastic table that <paramref name="row"/> writes: the
    /// partition of the key the reference was given with, none when the key has no <c>partitionid</c> (the row must exist in it, 404 otherwise), else the stored partition of
    /// the referenced row; null when the lookup is cleared. A lookup without such a column is left alone.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the row a key names is not in its partition; 400 for an invalid key.</exception>
    public static async Task ApplyLookupPartitionsAsync(DataSession s, IMetadataStore store, IReadOnlyDictionary<string, AttributeMetadata> attrs,
        Entity row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(attrs);
        ArgumentNullException.ThrowIfNull(row);
        foreach (var (name, value) in row.Attributes.ToList())
        {
            if (!attrs.TryGetValue(name, out var a) || a is not LookupAttributeMetadata
                || !attrs.ContainsKey(ElasticTables.PartitionColumnOf(name)))
                continue;
            var pid = ElasticTables.PartitionColumnOf(name);
            if (value is not EntityReference reference)
            {
                row[pid] = null;
                continue;
            }
            if (store.GetEntity(reference.LogicalName ?? "") is not { } target || !ElasticTables.IsElastic(target)) continue;
            if (reference.KeyAttributes is { Count: > 0 } keys)
            {
                // By key, as everywhere (ruling E1): a key without partitionid names a row stored without one.
                var key = ElasticRecordKey.From(target, reference.Id, keys, null);
                if (!(await FindAsync(s, target, key, lockRow: false, ct)).Found)
                    throw XrmdFaultException.ObjectDoesNotExist(target.LogicalName, key.Id);
                row[pid] = key.PartitionId;
                continue;
            }
            // By id alone: the partition the row is stored in (a missing row is the reference check's 404).
            if (await StoredPartitionAsync(s, target, reference.Id, ct) is (true, var stored)) row[pid] = stored;
        }
    }

    /// <summary>
    /// The partition the row <paramref name="id"/> of the elastic table <paramref name="m"/> is stored in, found by id alone (the
    /// id is unique, ruling E1); not found for a missing row.
    /// </summary>
    public static async Task<(bool Found, string? Partition)> StoredPartitionAsync(DataSession s, EntityMetadata m, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        await using var cmd = s.Command(
            $"SELECT {s.Quote(ElasticTables.PartitionId)} FROM {s.DataTable(m.LogicalName)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return (false, null);
        return (true, rd.IsDBNull(0) ? null : rd.GetString(0));
    }

    /// <summary>
    /// The Update a file message (CommitFileBlocksUpload, DeleteFile) runs on the record of <paramref name="target"/>, which it
    /// names by id alone: on an elastic table it carries the partition the row is stored in as the <c>partitionId</c> parameter,
    /// so that a row stored with a partition is found (final review I2). A plain Update by id elsewhere.
    /// </summary>
    public static async Task<UpdateRequest> FileUpdateAsync(DataSession s, EntityMetadata m, Entity target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(target);
        var update = new UpdateRequest { Target = target };
        if (ElasticTables.IsElastic(m) && await StoredPartitionAsync(s, m, target.Id, ct) is (true, { } partition))
            update[ElasticRecordKey.PartitionIdParameter] = partition;
        return update;
    }

    /// <summary>
    /// Adds <see cref="SessionToken"/> to <paramref name="response"/> after a write of the row <paramref name="id"/> of an elastic
    /// table: <c>0:</c> and the row's <c>versionnumber</c>, read in the write's transaction. Nothing for a standard table.
    /// </summary>
    public static async Task<T> WithSessionTokenAsync<T>(DataSession s, EntityMetadata m, Guid id, T response, CancellationToken ct)
        where T : OrganizationResponse
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(response);
        if (!ElasticTables.IsElastic(m)) return response;
        await using var cmd = s.Command(
            $"SELECT CAST({s.Quote("versionnumber")} AS bigint) FROM {s.DataTable(m.LogicalName)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        if (await cmd.ExecuteScalarAsync(ct) is long version)
            response.Results[SessionToken] = "0:" + version.ToString(CultureInfo.InvariantCulture);
        return response;
    }

    /// <summary>
    /// <paramref name="to"/> with the parameters of <paramref name="from"/> that name the row's partition (<c>partitionId</c>), so
    /// the request the organization service rebuilds for the pipeline keeps them.
    /// </summary>
    public static T CarryParameters<T>(OrganizationRequest from, T to) where T : OrganizationRequest
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        foreach (var (name, value) in from.Parameters)
            if (string.Equals(name, ElasticRecordKey.PartitionIdParameter, StringComparison.OrdinalIgnoreCase))
                to[name] = value;
        return to;
    }

    /// <summary><paramref name="to"/> with the <see cref="SessionToken"/> of <paramref name="from"/>, if any.</summary>
    public static T CarrySessionToken<T>(OrganizationResponse from, T to) where T : OrganizationResponse
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Results.TryGetValue(SessionToken, out var token)) to.Results[SessionToken] = token;
        return to;
    }

    /// <summary>
    /// True when the Retrieve or Delete of <paramref name="target"/> must reach the pipeline with the reference as given (its key
    /// attributes and partition): a reference by key, or any reference to an elastic table.
    /// </summary>
    public static bool NeedsKey(EntityMetadata m, EntityReference target)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(target);
        return target.KeyAttributes is { Count: > 0 } || ElasticTables.IsElastic(m);
    }

    /// <summary>The id of <paramref name="target"/>: <see cref="Entity.Id"/>, else its primary key attribute.</summary>
    private static Guid IdOf(EntityMetadata m, Entity target) =>
        target.Id != Guid.Empty ? target.Id
        : target.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(m), out var value) && value is Guid id ? id
        : Guid.Empty;
}
