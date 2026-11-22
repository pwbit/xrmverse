using System.Data;
using System.Text.Json;
using Xrmd.Abstractions.Storage;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata;

/// <summary>
/// The table flag <c>IsDuplicateDetectionEnabled</c> of <see cref="MetadataStore"/> (plan 2.15). It is kept in the stored
/// definition and set by CreateEntity and UpdateEntity (and so by a solution import). Enabling a table needs an eligible table
/// (<see cref="DuplicateDetectionSchema.CanBeEnabled"/>); it adds the table to the targets of <c>duplicaterecord.baserecordid</c>
/// and <c>duplicaterecord.duplicaterecordid</c>, in the same session. Disabling it leaves the targets (the records a bulk job
/// wrote keep their meaning) and unpublishes the rules whose base or matching table it is (ruling D11). Deleting a table removes its targets, the duplicate records of its rows and the rules that name
/// it (ruling D6); deleting a column named by a condition is refused.
/// </summary>
public sealed partial class MetadataStore
{
    /// <inheritdoc />
    public IReadOnlySet<string> DuplicateDetectionTables => _cache.DuplicateDetectionTables;

    /// <summary>The stored form of the flag: the value, changeable, under the managed property Dataverse names.</summary>
    private static BooleanManagedProperty DuplicateDetectionFlag(bool value)
    {
        var property = new BooleanManagedProperty(value) { CanBeChanged = true };
        SdkProperty.Set(property, nameof(ManagedProperty<bool>.ManagedPropertyLogicalName), "canmodifyduplicatedetectionsettings");
        return property;
    }

    /// <summary>The refusal of <c>IsDuplicateDetectionEnabled</c> on a table that cannot have it.</summary>
    private static XrmdValidationException DuplicateDetectionNotAllowed(string entityLogicalName) => new(
        $"IsDuplicateDetectionEnabled cannot be enabled on entity '{entityLogicalName}': intersect tables and the system tables other than account and contact do not support duplicate detection.");

    /// <summary>Adds <paramref name="entityLogicalName"/> to the targets of both record lookups of <c>duplicaterecord</c> (once).</summary>
    private static Task AddDuplicateRecordTargetAsync(DataSession session, CacheState cache, string entityLogicalName, long stamp, CancellationToken ct) =>
        SetDuplicateRecordTargetsAsync(session, cache,
            targets => targets.Contains(entityLogicalName) ? targets : [.. targets, entityLogicalName], stamp, ct);

    /// <summary>Removes <paramref name="entityLogicalName"/> from the targets of both record lookups of <c>duplicaterecord</c>.</summary>
    private static Task RemoveDuplicateRecordTargetAsync(DataSession session, CacheState cache, string entityLogicalName, long stamp, CancellationToken ct) =>
        SetDuplicateRecordTargetsAsync(session, cache, targets => targets.Where(t => t != entityLogicalName).ToArray(), stamp, ct);

    private static async Task SetDuplicateRecordTargetsAsync(DataSession session, CacheState cache, Func<string[], string[]> change, long stamp,
        CancellationToken ct)
    {
        if (cache.ByLogicalName.GetValueOrDefault(DuplicateDetectionSchema.Record) is not { } record) return;
        var attributes = (record.Attributes ?? []).ToList();
        var changed = false;
        for (var i = 0; i < attributes.Count; i++)
        {
            if (attributes[i] is not LookupAttributeMetadata lookup || !DuplicateDetectionSchema.IsRecordLookup(lookup)) continue;
            var targets = change(lookup.Targets ?? []);
            if (targets.SequenceEqual(lookup.Targets ?? [])) continue;
            var updated = MetadataSerializer.Clone(lookup);
            updated.Targets = targets;
            await StoreAttributeAsync(session, record.LogicalName, updated, stamp, ct);
            attributes[i] = updated;
            changed = true;
        }
        if (!changed) return;
        var copy = MetadataSerializer.Clone(record);
        copy.SetAttributes(attributes);
        cache.Put(copy, previous: record);
    }

    /// <summary>
    /// No duplicate rule condition names <paramref name="attributeLogicalName"/> of <paramref name="entityLogicalName"/> (as a
    /// base attribute of a rule whose base table it is, or as a matching attribute of a rule whose matching table it is).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the rule.</exception>
    private static async Task DemandNoDuplicateConditionAsync(DataSession session, CacheState cache, string entityLogicalName,
        string attributeLogicalName, CancellationToken ct)
    {
        if (!cache.ByLogicalName.ContainsKey(DuplicateDetectionSchema.Rule) || !cache.ByLogicalName.ContainsKey(DuplicateDetectionSchema.Condition)) return;
        // Plan 2.7a, Task 6: every column delete runs it, so it is written in the dialect.
        var d = session.Dialect;
        string Q(string name) => d.Quote(name);
        var first = d.Limit($"""
            SELECT {d.IsNull($"r.{Q(DuplicateDetectionSchema.Name)}", $"CAST(r.{Q(DuplicateDetectionSchema.RuleId)} AS {d.ColumnType(SqlColumnKind.String, 36)})")}
                FROM {session.DataTable(DuplicateDetectionSchema.Rule)} AS r
                JOIN {session.DataTable(DuplicateDetectionSchema.Condition)} AS c
                    ON c.{Q(DuplicateDetectionSchema.RegardingObjectId)} = r.{Q(DuplicateDetectionSchema.RuleId)}
                WHERE (r.{Q(DuplicateDetectionSchema.BaseEntityName)} = @entity AND c.{Q(DuplicateDetectionSchema.BaseAttributeName)} = @attribute)
                   OR (r.{Q(DuplicateDetectionSchema.MatchingEntityName)} = @entity AND c.{Q(DuplicateDetectionSchema.MatchingAttributeName)} = @attribute)
                ORDER BY r.{Q(DuplicateDetectionSchema.Name)}
            """, "1");
        var rule = await ScalarAsync<string>(session, $"SELECT {d.IsNull($"({first})", "''")}", ct,
            ("@entity", entityLogicalName), ("@attribute", attributeLogicalName));
        if (rule.Length > 0)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The component cannot be deleted because it is referenced by duplicate detection rule '{rule}'. Delete the rule's condition first.", 400);
    }

    /// <summary>
    /// The duplicate detection rows that go with a deleted table, in the session: the rules naming it as base or matching table
    /// (with their conditions, shares and match code tables; a duplicate record keeps its row, its rule link cleared) and the
    /// duplicate records of its rows.
    /// </summary>
    private static async Task DeleteDuplicateDetectionRowsAsync(DataSession session, CacheState cache, EntityMetadata entity, CancellationToken ct)
    {
        var hasRecords = cache.ByLogicalName.ContainsKey(DuplicateDetectionSchema.Record);
        if (cache.ByLogicalName.ContainsKey(DuplicateDetectionSchema.Rule) && cache.ByLogicalName.ContainsKey(DuplicateDetectionSchema.Condition))
            await DeleteRulesOfAsync(session, entity.LogicalName, cache.ByLogicalName[DuplicateDetectionSchema.Rule].ObjectTypeCode ?? 0, hasRecords, ct);
        if (hasRecords && entity.ObjectTypeCode is { } code)
            await ExecuteAsync(session, $"""
                DELETE FROM {session.DataTable(DuplicateDetectionSchema.Record)}
                WHERE {session.Quote(DuplicateDetectionSchema.BaseRecordId + TypeMapping.TypeColumnSuffix)} = @code
                   OR {session.Quote(DuplicateDetectionSchema.DuplicateRecordId + TypeMapping.TypeColumnSuffix)} = @code
                """, ct, ("@code", code));
    }

    /// <summary>
    /// The rules naming <paramref name="table"/> as base or matching table, locked, then deleted with their conditions, shares and
    /// match code tables; a duplicate record keeps its row, its rule link cleared (plan 2.7a, Task 6: the T-SQL batch with its
    /// table variable became statements of their own in the session's transaction, the rule ids a JSON parameter).
    /// </summary>
    private static async Task DeleteRulesOfAsync(DataSession session, string table, int ruleCode, bool hasRecords, CancellationToken ct)
    {
        var d = session.Dialect;
        string Q(string name) => d.Quote(name);
        var rules = new List<Guid>();
        await using (var read = session.Command($"""
            SELECT {Q(DuplicateDetectionSchema.RuleId)} FROM {session.DataTable(DuplicateDetectionSchema.Rule)}{d.LockHint(RowLock.UpdateRange)}
            WHERE {Q(DuplicateDetectionSchema.BaseEntityName)} = @name OR {Q(DuplicateDetectionSchema.MatchingEntityName)} = @name{d.LockClause(RowLock.UpdateRange)}
            """))
        {
            read.AddParameter("@name", DbType.String, table);
            await using var rd = await read.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rules.Add(rd.GetGuid(0));
        }
        if (rules.Count == 0) return;
        var ids = JsonSerializer.Serialize(rules);
        var inRules = $"IN (SELECT j.id FROM {d.JsonRecords("@rules", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")])} AS j)";
        await ExecuteAsync(session, $"DELETE FROM {session.DataTable(DuplicateDetectionSchema.Condition)} WHERE {Q(DuplicateDetectionSchema.RegardingObjectId)} {inRules}", ct, ("@rules", ids));
        if (hasRecords)
            await ExecuteAsync(session, $"UPDATE {session.DataTable(DuplicateDetectionSchema.Record)} SET {Q(DuplicateDetectionSchema.DuplicateRuleId)} = NULL WHERE {Q(DuplicateDetectionSchema.DuplicateRuleId)} {inRules}",
                ct, ("@rules", ids));
        // Final review C2: the rules' own shares only, never those of another table's record with a rule's id.
        await ExecuteAsync(session, $"DELETE FROM {session.DataTable("principalobjectaccess")} WHERE objectid {inRules} AND objecttypecode = @rulecode", ct,
            ("@rules", ids), ("@rulecode", ruleCode));
        var matchCodeTables = new List<string>();
        await using (var read = session.Command($"SELECT table_name FROM meta.matchcode_table WHERE duplicateruleid {inRules}"))
        {
            read.AddParameter("@rules", DbType.String, ids, -1);
            await using var rd = await read.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) matchCodeTables.Add(rd.GetString(0));
        }
        // The registry row first, then the table: the order of a publish job's batch (registry, then table), so they never deadlock.
        await ExecuteAsync(session, $"DELETE FROM meta.matchcode_table WHERE duplicateruleid {inRules}", ct, ("@rules", ids));
        foreach (var matchCodeTable in matchCodeTables)
            await ExecuteAsync(session, $"DROP TABLE IF EXISTS meta.{Q(matchCodeTable)}", ct);
        await ExecuteAsync(session, $"DELETE FROM {session.DataTable(DuplicateDetectionSchema.Rule)} WHERE {Q(DuplicateDetectionSchema.RuleId)} {inRules}", ct, ("@rules", ids));
    }
}
