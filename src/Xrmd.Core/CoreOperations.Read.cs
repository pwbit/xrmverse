using Xrmd.Abstractions;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Core.Query;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
using Xrmd.Query;
using Xrmd.Query.Model;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: Retrieve and RetrieveMultiple on the model. The bodies read <see cref="TableDefinition"/> metadata and answer
/// <see cref="Record"/> and <see cref="RecordCollection"/>, shaped by <see cref="QueryExecutor.ExecuteRecordsAsync(QueryModel, Caller, DataSession, CancellationToken)"/>.
/// Their SDK twins (<c>RetrieveAsync</c>, <c>RetrieveMultipleAsync</c>, in <c>Sdk/CoreOperations.Records.Sdk.cs</c>) keep the old
/// signatures and write the answer out once. The model members are named apart from the twins, which differ only in their return
/// types; S.2e3c and S.2f rename them back when the twins go.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>Test seam: runs in <see cref="RetrieveRecordAsync"/> after the caller's read found nothing, before the unrestricted one.</summary>
    internal Func<Task>? AfterRestrictedRetrieveMiss { get; set; }

    /// <summary>
    /// Reads one record with the columns of <paramref name="columns"/> (primary key first; <c>AllColumns</c> → every
    /// readable attribute; null/empty → the primary key only), through the <see cref="QueryExecutor"/>: lookups carry the
    /// target's name and the record its formatted values, as in RetrieveMultiple.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an unknown entity or column; ObjectDoesNotExist (404)
    /// when the record does not exist; PrivilegeDenied (403) without read privilege on the entity; AccessDenied (403, Read)
    /// when the record exists but the caller's read restriction hides it.</exception>
    /// <param name="key">The row of an elastic table: its id and partition (plan 3.4, ruling E1); null finds the row by
    /// <paramref name="id"/> alone.</param>
    public async Task<Record> RetrieveRecordAsync(
        DataSession s, Caller caller, string entityName, Guid id, ColumnSelection? columns, CancellationToken ct, ElasticRecordKey? key = null) =>
        ((ModelRow)await RetrieveRowAsync(s, caller, entityName, id, columns, RowSink.Model, ct, key)).Record;

    /// <summary>
    /// <see cref="RetrieveRecordAsync"/> with its row made by <paramref name="sink"/> (Decision 4: a model record, or the SDK entity of the
    /// SDK twin, shaped from the reader).
    /// </summary>
    internal async Task<QueryRow> RetrieveRowAsync(
        DataSession s, Caller caller, string entityName, Guid id, ColumnSelection? columns, RowSink sink, CancellationToken ct, ElasticRecordKey? key = null)
    {
        var table = TableOf(entityName);
        var name = table.LogicalName!;
        if (key is { } k)
        {
            id = k.Id;
            // Plan 3.4, ruling E1: the partition matches exactly (the query's comparison follows the column's collation).
            if (!(await CoreSdkBridges.FindElasticAsync(s, table, k, lockRow: false, ct)).Found) throw XrmdFaultException.ObjectDoesNotExist(name, id);
        }
        ConditionNode[] identity = key?.Conditions(table) ?? [new ConditionNode(null, SystemAttributes.PrimaryIdName(table), FilterOperator.Equal, [id])];
        var root = new EntityNode { EntityName = name, AllColumns = columns?.AllColumns == true };
        if (!root.AllColumns)
            foreach (var column in columns?.Columns ?? Enumerable.Empty<string>()) root.Columns.Add(new ColumnNode(column));
        root.Filter.Conditions.AddRange(identity);

        var result = await queryExecutor.ExecutePageAsync(new QueryModel { Root = root, Top = 1 }, caller, s, sink, ct);
        if (result.Rows.Count > 0) return result.Rows[0];

        // Not visible to a restricted caller: 403 when the record exists (read unrestricted), 404 otherwise. An unrestricted
        // caller sees every record, so for it the second read finds nothing either.
        if (caller != Caller.Internal)
        {
            if (AfterRestrictedRetrieveMiss is { } hook) await hook();
            var any = new EntityNode { EntityName = name };
            any.Filter.Conditions.AddRange(identity);
            if ((await queryExecutor.ExecuteRecordsAsync(new QueryModel { Root = any, Top = 1 }, Caller.Internal, s, ct)).Records.Count > 0)
            {
                // The two reads are not one snapshot: a record committed by another session after the caller's read is
                // found by the second. Read it as the caller once more before answering 403 (a committed row stays).
                result = await queryExecutor.ExecutePageAsync(new QueryModel { Root = root, Top = 1 }, caller, s, sink, ct);
                if (result.Rows.Count > 0) return result.Rows[0];
                throw XrmdFaultException.AccessDenied(caller.UserId, AccessMask.ReadAccess, name, id);
            }
        }
        throw XrmdFaultException.ObjectDoesNotExist(name, id);
    }

    /// <summary>Runs a query already in the <see cref="QueryModel"/> form (see <see cref="QueryExecutor.ExecuteRecordsAsync(QueryModel, Caller, DataSession, CancellationToken)"/>).</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an invalid or unsupported query;
    /// AggregateQueryRecordLimitExceeded (400).</exception>
    public async Task<RecordCollection> RetrieveRecordsAsync(DataSession s, Caller caller, QueryModel query, CancellationToken ct) =>
        (await RetrievePageAsync(s, caller, query, RowSink.Model, ct)).ToRecords();

    /// <summary><see cref="RetrieveRecordsAsync"/> with its rows made by <paramref name="sink"/> (Decision 4).</summary>
    internal async Task<QueryPage> RetrievePageAsync(DataSession s, Caller caller, QueryModel query, RowSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            return await queryExecutor.ExecutePageAsync(query, caller, s, sink, ct);
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

    /// <summary>The table definition; InvalidArgument (400) when unknown (the model twin of <c>EntityOf</c>).</summary>
    internal TableDefinition TableOf(string? logicalName) =>
        (logicalName is null ? null : store.GetTable(logicalName))
        // Exact Dataverse text: client code matches on "was not found in the MetadataCache" to detect an unknown table.
        ?? throw XrmdFaultException.InvalidArgument(
            $"The entity with a name = '{logicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");
}
