using System.Data;
using System.Data.Common;
using Xrmd.Model.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// Duplicate detection (plan 2.15): the seeded tables <c>duplicaterule</c>, <c>duplicaterulecondition</c> and
/// <c>duplicaterecord</c> (<c>fixtures/seed</c>), their columns and options, the four organization settings, the limits and
/// which tables can have <c>IsDuplicateDetectionEnabled</c>.
/// </summary>
/// <remarks>
/// The object type codes 4414, 4415 and 4416 are from memory, not from Learn: the plan 2.15 checklist verifies them on a real
/// environment. The match codes of a published rule live in <c>meta.matchcode_{ruleid:N}</c>, registered in
/// <c>meta.matchcode_table</c> (migration 0009).
/// </remarks>
public static partial class DuplicateDetectionSchema
{
    /// <summary>The logical names of the three tables.</summary>
    public const string Rule = "duplicaterule", Condition = "duplicaterulecondition", Record = "duplicaterecord";

    /// <summary>The object type codes of the three tables.</summary>
    public const int RuleObjectTypeCode = 4414, RecordObjectTypeCode = 4415, ConditionObjectTypeCode = 4416;

    /// <summary>The most rules in Publishing or Published per base table (Learn).</summary>
    public const int MaxPublishedPerBaseTable = 5;

    /// <summary>The most <c>duplicaterecord</c> rows a bulk detection job writes (Learn).</summary>
    public const int MaxBulkRecords = 5000;

    /// <summary>The largest page size of <c>RetrieveDuplicates</c>.</summary>
    public const int MaxPageSize = 5000;

    /// <summary>The columns of <see cref="Rule"/>.</summary>
    public const string RuleId = "duplicateruleid", Name = "name", BaseEntityName = "baseentityname", MatchingEntityName = "matchingentityname",
        Description = "description", ExcludeInactiveRecords = "excludeinactiverecords", IsCaseSensitive = "iscasesensitive",
        UniqueName = "uniquename", BaseEntityTypeCode = "baseentitytypecode", MatchingEntityTypeCode = "matchingentitytypecode",
        BaseEntityMatchCodeTable = "baseentitymatchcodetable", MatchingEntityMatchCodeTable = "matchingentitymatchcodetable";

    /// <summary>The columns of <see cref="Condition"/>.</summary>
    public const string ConditionId = "duplicateruleconditionid", BaseAttributeName = "baseattributename",
        MatchingAttributeName = "matchingattributename", OperatorCode = "operatorcode", OperatorParam = "operatorparam",
        IgnoreBlankValues = "ignoreblankvalues", RegardingObjectId = "regardingobjectid", UniqueRuleName = "uniquerulename";

    /// <summary>The columns of <see cref="Record"/> (its primary key is <see cref="RecordId"/>, as in Dataverse).</summary>
    public const string RecordId = "duplicateid", AsyncOperationId = "asyncoperationid", BaseRecordId = "baserecordid",
        DuplicateRecordId = "duplicaterecordid", DuplicateRuleId = "duplicateruleid";

    /// <summary>The relationship from a rule to its conditions (Delete: Cascade).</summary>
    public const string ConditionsRelationship = "DuplicateRule_DuplicateRuleConditions";

    /// <summary>The columns a client never writes on a rule (D5): the server fills them.</summary>
    public static readonly string[] ServerFilledRuleColumns =
        [BaseEntityTypeCode, MatchingEntityTypeCode, BaseEntityMatchCodeTable, MatchingEntityMatchCodeTable];

    /// <summary>The four organization settings (Learn: Boolean, SystemRequired, default false).</summary>
    public const string OrgEnabled = "isduplicatedetectionenabled",
        OrgEnabledForOnlineCreateUpdate = "isduplicatedetectionenabledforonlinecreateupdate",
        OrgEnabledForImport = "isduplicatedetectionenabledforimport",
        OrgEnabledForOfflineSync = "isduplicatedetectionenabledforofflinesync";

    /// <summary>The four organization settings, in the order of <see cref="OrgEnabled"/> and the others.</summary>
    public static readonly string[] OrganizationColumns = [OrgEnabled, OrgEnabledForOnlineCreateUpdate, OrgEnabledForImport, OrgEnabledForOfflineSync];

    /// <summary>The seeded tables that can have <c>IsDuplicateDetectionEnabled</c>; every other seeded table cannot.</summary>
    public static readonly IReadOnlySet<string> EligibleSystemTables = new HashSet<string>(StringComparer.Ordinal) { "account", "contact" };

    /// <summary>The operators of a condition (<c>operatorcode</c>).</summary>
    public enum DuplicateOperator
    {
        ExactMatch = 0,
        SameFirstCharacters = 1,
        SameLastCharacters = 2,
        SameDate = 3,
        SameDateAndTime = 4,
        PickListLabel = 5,
        PickListValue = 6,
    }

    /// <summary>The <c>statuscode</c> values of a rule (state 0 for the first two, 1 for Published).</summary>
    public static class RuleStatus
    {
        public const int Unpublished = 0, Publishing = 1, Published = 2;
    }

    /// <summary>The <c>statecode</c> values of a rule: 0 Inactive (Unpublished, Publishing), 1 Active (Published).</summary>
    public static class RuleState
    {
        public const int Inactive = 0, Active = 1;
    }

    /// <summary>The name of a rule's match code table in <c>meta</c>: <c>matchcode_{ruleid:N}</c>.</summary>
    public static string MatchCodeTableName(Guid ruleId) => "matchcode_" + ruleId.ToString("N");

    /// <summary>
    /// Unpublishes, in <paramref name="session"/>, every rule in Publishing or Published whose base or matching table is
    /// <paramref name="entity"/> (every rule when null): the rule goes to Inactive/Unpublished (0/0) with its match code table
    /// columns cleared, its match code table is dropped and its <c>meta.matchcode_table</c> row removed (ruling D11). The rows
    /// are locked (UPDLOCK, HOLDLOCK), so a concurrent PublishDuplicateRule waits. Nothing happens on an organization without
    /// the <c>duplicaterule</c> table.
    /// </summary>
    /// <returns>The rules unpublished.</returns>
    public static async Task<IReadOnlyList<Guid>> UnpublishRulesAsync(DataSession session, string? entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Plan 2.7b, Task 4: the T-SQL batch (table variable, dynamic DROP) became statements of their own in the session's
        // transaction, as DuplicateDetectionStore's rule delete did in 2.7a.
        var d = session.Dialect;
        string Q(string name) => d.Quote(name);
        await using (var exists = session.Command($"SELECT CASE WHEN {d.TableExists(DdlGenerator.DataSchema, Rule)} THEN 1 ELSE 0 END"))
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0) return [];
        // PostgreSQL has no key-range lock: the rule table's writers wait to the end of the transaction instead, so a concurrent
        // PublishDuplicateRule (which takes the same lock) waits and then sees the switch off (nothing is sent on SQL Server).
        await d.LockTableWritesAsync(session, DdlGenerator.DataSchema, Rule, ct);
        var rules = new List<Guid>();
        await using (var read = session.Command($"""
            SELECT {Q(RuleId)} FROM {session.DataTable(Rule)}{d.LockHint(RowLock.UpdateRange)}
                WHERE {Q("statuscode")} IN ({RuleStatus.Publishing}, {RuleStatus.Published})
                  AND (@_entity IS NULL OR {Q(BaseEntityName)} = @_entity OR {Q(MatchingEntityName)} = @_entity){d.LockClause(RowLock.UpdateRange)}
            """))
        {
            read.AddParameter("@_entity", DbType.String, (object?)entity ?? DBNull.Value, 128);
            await using var rd = await read.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rules.Add(rd.GetGuid(0));
        }
        if (rules.Count == 0) return rules;
        // Fix round 1: at most 1000 ids per statement (within SQL Server's 2100 parameters).
        var tables = new List<string>();
        foreach (var chunk in rules.Chunk(1000))
        {
            var inRules = $"IN ({string.Join(", ", chunk.Select((_, i) => $"@_r{i}"))})";
            void AddRules(DbCommand cmd)
            {
                for (var i = 0; i < chunk.Length; i++) cmd.AddParameter($"@_r{i}", DbType.Guid, chunk[i]);
            }
            await using (var update = session.Command($"""
                UPDATE {session.DataTable(Rule)}
                    SET {Q("statecode")} = {RuleState.Inactive}, {Q("statuscode")} = {RuleStatus.Unpublished},
                        {Q(BaseEntityMatchCodeTable)} = NULL, {Q(MatchingEntityMatchCodeTable)} = NULL, {Q("modifiedon")} = {d.UtcNow}
                    WHERE {Q(RuleId)} {inRules}
                """))
            {
                AddRules(update);
                await update.ExecuteNonQueryAsync(ct);
            }
            // The table names come from meta.matchcode_table, which only this code writes (matchcode_{ruleid:N}); quoted anyway.
            await using (var read = session.Command($"SELECT table_name FROM meta.matchcode_table WHERE duplicateruleid {inRules}"))
            {
                AddRules(read);
                await using var rd = await read.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) tables.Add(rd.GetString(0));
            }
            // The registry row first, then the table: the order of a publish job's batch (registry, then table), so they never deadlock.
            await using (var delete = session.Command($"DELETE FROM meta.matchcode_table WHERE duplicateruleid {inRules}"))
            {
                AddRules(delete);
                await delete.ExecuteNonQueryAsync(ct);
            }
        }
        foreach (var table in tables)
        {
            await using var drop = session.Command($"DROP TABLE IF EXISTS meta.{Q(table)}");
            await drop.ExecuteNonQueryAsync(ct);
        }
        return rules;
    }

    /// <summary>The default <c>uniquename</c> of a rule created without one (D5).</summary>
    public static string DefaultUniqueName(Guid ruleId) => "duplicaterule_" + ruleId.ToString("N");

    /// <summary>
    /// True when <paramref name="entity"/> can have <c>IsDuplicateDetectionEnabled</c>: not an intersect table, and not a
    /// seeded system table other than <see cref="EligibleSystemTables"/>.
    /// </summary>
    public static bool CanBeEnabled(TableDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.IsIntersect == true || entity.LogicalName is not { } name) return false;
        return EligibleSystemTables.Contains(name) || !MetadataStore.IsSystemEntity(entity);
    }

    /// <summary>
    /// True for <c>duplicaterecord.baserecordid</c> and <c>duplicaterecord.duplicaterecordid</c>: their targets are the tables
    /// with <c>IsDuplicateDetectionEnabled</c>, maintained by the metadata store, so they start without any and keep a type column.
    /// </summary>
    public static bool IsRecordLookup(ColumnDefinition a) =>
        a is LookupColumn && a.EntityLogicalName == Record && a.LogicalName is BaseRecordId or DuplicateRecordId;
}
