using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Security;

/// <summary>
/// The rules of duplicate rule and condition writes (plan 2.15, rulings D3 to D6). <see cref="SystemTableRules"/> runs the
/// demands on the parameters a caller sent, for every caller but <see cref="Caller.Internal"/> (the publishing messages and
/// jobs write the state of a rule as Internal); the core fills the server columns at stage 30
/// (<see cref="ApplyServerColumnsAsync"/>). Write on the parent rule of a condition is the <c>SecurityGuard</c>'s (D6).
/// </summary>
public static class DuplicateRuleRules
{
    private const string StateCode = "statecode", StatusCode = "statuscode";

    /// <summary>
    /// D3 to D5 for a rule written by Create (<paramref name="isCreate"/>) or Update (<paramref name="id"/>), in this order:
    /// <list type="bullet">
    /// <item>a server-filled column (<see cref="ServerFilledRuleColumns"/>) is refused (D5);</item>
    /// <item><c>statecode</c>/<c>statuscode</c> change only through PublishDuplicateRule and UnpublishDuplicateRule (D3): a Create
    /// gives them 0 or nothing, an Update their stored values or nothing;</item>
    /// <item>Update: a rule in Publishing or Published is not updated, and its tables do not change (D4);</item>
    /// <item>Create: both tables are given, exist and have <c>IsDuplicateDetectionEnabled</c> (D4).</item>
    /// </list>
    /// A missing rule on Update is left to the core operation's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    public static async Task DemandRuleWriteAsync(DataSession s, IMetadataStore store, Entity target, bool isCreate, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        if (ServerFilledRuleColumns.FirstOrDefault(target.Attributes.ContainsKey) is { } filled)
            throw XrmdFaultException.InvalidArgument($"The attribute {filled} of {DuplicateDetectionSchema.Rule} is maintained by the system and cannot be set.");

        if (isCreate)
        {
            if (OptionValue(target, StateCode) is not (null or 0) || OptionValue(target, StatusCode) is not (null or 0))
                throw StatusFixed();
            var baseName = target.GetAttributeValue<string>(BaseEntityName);
            var matchingName = target.GetAttributeValue<string>(MatchingEntityName);
            DemandEnabledTable(store, BaseEntityName, baseName);
            DemandEnabledTable(store, MatchingEntityName, matchingName);
            return;
        }

        if (id == Guid.Empty || await StoredRuleAsync(s, id, ct) is not { } stored) return;
        if ((target.Attributes.ContainsKey(StateCode) && OptionValue(target, StateCode) != stored.State)
            || (target.Attributes.ContainsKey(StatusCode) && OptionValue(target, StatusCode) != stored.Status))
            throw StatusFixed();
        if (stored.Status != RuleStatus.Unpublished) throw Published(id);
        foreach (var (column, value) in new[] { (BaseEntityName, stored.Base), (MatchingEntityName, stored.Matching) })
            if (target.Attributes.TryGetValue(column, out var given) && !string.Equals(given as string, value, StringComparison.Ordinal))
                throw XrmdFaultException.InvalidArgument(
                    $"The {column} of a duplicate detection rule cannot change (it is '{value}'); create another rule instead.");
    }

    /// <summary>
    /// D4 to D6 for a condition written by Create (<paramref name="isCreate"/>) or Update (<paramref name="id"/>), on its
    /// resulting values (given, else stored): <c>uniquerulename</c> is not given (D5); <c>regardingobjectid</c> names an
    /// existing rule (404) that is Unpublished, as is the rule the condition leaves (400); both attributes exist on the rule's
    /// tables, are not virtual, file, image or multi-select columns, and have compatible types; <c>operatorcode</c> and
    /// <c>operatorparam</c> fit their type (Pick List Label, 5, is refused). A missing condition on Update is left to the core
    /// operation's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument; 404 ObjectDoesNotExist for a missing rule.</exception>
    public static async Task DemandConditionWriteAsync(DataSession s, IMetadataStore store, Entity target, bool isCreate, Guid id,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Attributes.ContainsKey(UniqueRuleName))
            throw XrmdFaultException.InvalidArgument($"The attribute {UniqueRuleName} of {Condition} is maintained by the system and cannot be set.");

        StoredCondition? stored = null;
        if (!isCreate)
        {
            if (id == Guid.Empty || (stored = await StoredConditionAsync(s, id, ct)) is null) return;
            if (stored.ParentRule is { } previous && await StoredRuleAsync(s, previous, ct) is { } previousRule
                && previousRule.Status != RuleStatus.Unpublished)
                throw Published(previous);
        }

        var ruleGiven = target.Attributes.TryGetValue(RegardingObjectId, out var ruleValue);
        var ruleId = ruleGiven ? IdOf(ruleValue) : stored?.ParentRule;
        if (ruleId is not { } rid || rid == Guid.Empty)
            throw XrmdFaultException.InvalidArgument($"A duplicate detection rule condition needs {RegardingObjectId}, the rule it belongs to.");
        var rule = await StoredRuleAsync(s, rid, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(DuplicateDetectionSchema.Rule, rid);
        if (rule.Status != RuleStatus.Unpublished) throw Published(rid);

        var baseAttribute = Resulting(target, BaseAttributeName, stored?.BaseAttribute);
        var matchingAttribute = Resulting(target, MatchingAttributeName, stored?.MatchingAttribute);
        var operatorCode = target.Attributes.ContainsKey(OperatorCode) ? OptionValue(target, OperatorCode) : stored?.Operator;
        var operatorParam = target.Attributes.TryGetValue(OperatorParam, out var param) ? param : stored?.Param;

        var baseColumn = Column(store, rule.Base, baseAttribute, BaseAttributeName);
        var matchingColumn = Column(store, rule.Matching, matchingAttribute, MatchingAttributeName);
        if (!Compatible(baseColumn, matchingColumn))
            throw XrmdFaultException.InvalidArgument(
                $"The columns '{rule.Base}.{baseAttribute}' ({baseColumn.AttributeType}) and '{rule.Matching}.{matchingAttribute}' " +
                $"({matchingColumn.AttributeType}) cannot be compared: a condition needs columns of the same type (text with text, a lookup with a lookup to a common table).");
        DemandOperator(baseColumn, operatorCode, operatorParam);
    }

    /// <summary>
    /// D4: a condition of a rule in Publishing or Published is not deleted, unless the rule itself is being deleted
    /// (<paramref name="ruleBeingDeleted"/>: the delete cascade of a rule, allowed in any state). Called by the core's Delete,
    /// for every caller. A missing condition is left to the core's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    public static async Task DemandConditionDeleteAsync(DataSession s, Guid id, Func<Guid, bool> ruleBeingDeleted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ruleBeingDeleted);
        if (await StoredConditionAsync(s, id, ct) is { ParentRule: { } ruleId } && !ruleBeingDeleted(ruleId)
            && await StoredRuleAsync(s, ruleId, ct) is { } rule && rule.Status != RuleStatus.Unpublished)
            throw Published(ruleId);
    }

    /// <summary>
    /// The rule a condition belongs to, as stored (null when the condition does not exist or has none): the rule whose Write a
    /// condition's Update or Delete demands (D6).
    /// </summary>
    public static async Task<Guid?> StoredConditionRuleAsync(DataSession s, Guid conditionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return (await StoredConditionAsync(s, conditionId, ct))?.ParentRule;
    }

    /// <summary>The id of a lookup value: an <see cref="EntityReference"/> or a <see cref="Guid"/>; null otherwise.</summary>
    public static Guid? IdOf(object? value) => value switch
    {
        EntityReference reference => reference.Id,
        Guid guid => guid,
        _ => null,
    };

    /// <summary>
    /// Stage 30 of a Create or Update of a rule or a condition, in the request's transaction (D5): a rule gets
    /// <c>baseentitytypecode</c>/<c>matchingentitytypecode</c> from its tables, <c>uniquename</c> when empty
    /// (<c>duplicaterule_{id:N}</c>) and its two flags false when not given; a condition gets <c>uniquerulename</c> from its rule
    /// and <c>ignoreblankvalues</c> false when not given. A rule's new <c>uniquename</c> reaches its conditions. Other tables:
    /// nothing.
    /// </summary>
    public static async Task ApplyServerColumnsAsync(DataSession s, IMetadataStore store, string entityLogicalName, Entity row, bool isCreate,
        Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);
        if (entityLogicalName == DuplicateDetectionSchema.Rule)
        {
            if (isCreate)
            {
                foreach (var (name, code) in new[] { (BaseEntityName, BaseEntityTypeCode), (MatchingEntityName, MatchingEntityTypeCode) })
                    if (store.GetEntity(row.GetAttributeValue<string>(name) ?? "")?.ObjectTypeCode is { } otc)
                        row[code] = new OptionSetValue(otc);
                foreach (var flag in new[] { ExcludeInactiveRecords, IsCaseSensitive })
                    if (!row.Contains(flag) || row[flag] is null) row[flag] = false;
            }
            if ((isCreate || row.Contains(UniqueName)) && string.IsNullOrEmpty(row.GetAttributeValue<string>(UniqueName)))
                row[UniqueName] = DefaultUniqueName(id);
            if (!isCreate && row.Contains(UniqueName))
            {
                await using var cmd = s.Command(
                    $"UPDATE {s.DataTable(Condition)} SET {s.Quote(UniqueRuleName)} = @_name WHERE {s.Quote(RegardingObjectId)} = @_id");
                cmd.AddParameter("@_name", DbType.String, row.GetAttributeValue<string>(UniqueName), 100);
                cmd.AddParameter("@_id", DbType.Guid, id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        else if (entityLogicalName == Condition)
        {
            if (isCreate && (!row.Contains(IgnoreBlankValues) || row[IgnoreBlankValues] is null)) row[IgnoreBlankValues] = false;
            if ((isCreate || row.Contains(RegardingObjectId)) && IdOf(row.Contains(RegardingObjectId) ? row[RegardingObjectId] : null) is { } ruleId)
                row[UniqueRuleName] = (await StoredRuleAsync(s, ruleId, ct))?.UniqueName;
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static XrmdFaultException StatusFixed() => XrmdFaultException.InvalidArgument(
        "The statecode and statuscode of a duplicate detection rule change only through PublishDuplicateRule and UnpublishDuplicateRule.");

    private static XrmdFaultException Published(Guid ruleId) => XrmdFaultException.InvalidArgument(
        $"The duplicate detection rule {ruleId} is published or being published: unpublish the rule first (UnpublishDuplicateRule), then change it or its conditions.");

    /// <summary>A rule's table: given, existing and with <c>IsDuplicateDetectionEnabled</c>.</summary>
    private static void DemandEnabledTable(IMetadataStore store, string column, string? table)
    {
        if (string.IsNullOrEmpty(table))
            throw XrmdFaultException.InvalidArgument($"A duplicate detection rule needs {column}, the logical name of a table.");
        if (store.GetEntity(table) is null)
            throw XrmdFaultException.InvalidArgument($"The {column} '{table}' of the duplicate detection rule names no table.");
        if (!store.DuplicateDetectionTables.Contains(table))
            throw XrmdFaultException.InvalidArgument(
                $"The table '{table}' does not have IsDuplicateDetectionEnabled: turn duplicate detection on for the table before creating a rule for it.");
    }

    private static string? Resulting(Entity target, string column, string? stored) =>
        target.Attributes.TryGetValue(column, out var given) ? given as string : stored;

    /// <summary>The column a condition names: it exists on the table and can be compared (not virtual, file, image or multi-select).</summary>
    private static AttributeMetadata Column(IMetadataStore store, string table, string? attribute, string what)
    {
        if (string.IsNullOrEmpty(attribute))
            throw XrmdFaultException.InvalidArgument($"A duplicate detection rule condition needs {what}.");
        var column = store.GetEntity(table)?.Attributes?.FirstOrDefault(a => a.LogicalName == attribute)
                     ?? throw XrmdFaultException.InvalidArgument($"The {what} '{attribute}' is not a column of the table '{table}'.");
        if (column.AttributeType is AttributeTypeCode.Virtual or AttributeTypeCode.EntityName or AttributeTypeCode.ManagedProperty
            || column is FileAttributeMetadata or ImageAttributeMetadata or MultiSelectPicklistAttributeMetadata
            || TypeMapping.IsStoredByOtherAttribute(column) || column.AttributeOf is not null)
            throw XrmdFaultException.InvalidArgument(
                $"The column '{table}.{attribute}' cannot be used by a duplicate detection rule: virtual, file, image and multi-select columns cannot be compared.");
        // Plans 2.12a and 2.12c: a calculated or formula column has no stored value to build match codes from.
        if (ComputedColumnRules.IsComputed(column))
            throw XrmdFaultException.InvalidArgument(
                $"Duplicate detection rules cannot use the {(column.SourceType == ComputedColumnRules.Formula ? "formula" : "calculated")} column '{table}.{attribute}'.");
        // Plan 2.12b: a rollup column's value is written by its jobs outside the pipeline, so match codes would go stale (its companions
        // are refused above, as columns of another column).
        if (RollupColumns.IsRollup(column))
            throw XrmdFaultException.InvalidArgument($"Duplicate detection rules cannot use the rollup column '{table}.{attribute}'.");
        return column;
    }

    private static bool IsText(AttributeMetadata a) => a.AttributeType is AttributeTypeCode.String or AttributeTypeCode.Memo;

    private static bool IsReference(AttributeMetadata a) =>
        a.AttributeType is AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner;

    private static IEnumerable<string> Targets(AttributeMetadata a) => a switch
    {
        LookupAttributeMetadata lookup when lookup.AttributeType == AttributeTypeCode.Owner && (lookup.Targets ?? []).Length == 0 => ["systemuser", "team"],
        LookupAttributeMetadata lookup => lookup.Targets ?? [],
        _ => [],
    };

    private static bool Compatible(AttributeMetadata a, AttributeMetadata b) =>
        (IsText(a) && IsText(b))
        || (IsReference(a) && IsReference(b) && Targets(a).Intersect(Targets(b), StringComparer.Ordinal).Any())
        || (!IsText(a) && !IsReference(a) && a.AttributeType == b.AttributeType);

    /// <summary>The operator fits the column's type, and <c>operatorparam</c> is given exactly for 1 and 2 (D6).</summary>
    private static void DemandOperator(AttributeMetadata column, int? code, object? param)
    {
        var type = column.AttributeType;
        var fits = code switch
        {
            (int)DuplicateOperator.ExactMatch => IsText(column) || IsReference(column) || type is AttributeTypeCode.Integer or AttributeTypeCode.BigInt
                or AttributeTypeCode.Decimal or AttributeTypeCode.Double or AttributeTypeCode.Money or AttributeTypeCode.Boolean
                or AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status,
            (int)DuplicateOperator.SameFirstCharacters or (int)DuplicateOperator.SameLastCharacters => IsText(column),
            (int)DuplicateOperator.SameDate or (int)DuplicateOperator.SameDateAndTime => type is AttributeTypeCode.DateTime,
            (int)DuplicateOperator.PickListLabel => throw XrmdFaultException.InvalidArgument(
                "The operator Exact Match (Pick List Label) (5) is not supported by xrmverse: option labels depend on the language. Use Exact Match (Pick List Value) (6)."),
            (int)DuplicateOperator.PickListValue => type is AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status,
            null => throw XrmdFaultException.InvalidArgument($"A duplicate detection rule condition needs {OperatorCode}."),
            _ => throw XrmdFaultException.InvalidArgument($"The {OperatorCode} {code} is not an operator of a duplicate detection rule condition."),
        };
        if (!fits)
            throw XrmdFaultException.InvalidArgument(
                $"The {OperatorCode} {code} does not fit the column '{column.EntityLogicalName}.{column.LogicalName}' ({type}).");
        var counted = code is (int)DuplicateOperator.SameFirstCharacters or (int)DuplicateOperator.SameLastCharacters;
        if (counted && !(param is int n && n >= 1))
            throw XrmdFaultException.InvalidArgument(
                $"The operators Same First Characters (1) and Same Last Characters (2) need {OperatorParam}, the number of characters (1 or more).");
        if (!counted && param is not null)
            throw XrmdFaultException.InvalidArgument($"The {OperatorParam} is set only with Same First Characters (1) or Same Last Characters (2).");
    }

    /// <summary>An option value given as an <see cref="OptionSetValue"/> or an <see cref="int"/>; null for null or absent.</summary>
    private static int? OptionValue(Entity target, string column) =>
        target.Attributes.TryGetValue(column, out var value) ? value switch
        {
            OptionSetValue option => option.Value,
            int number => number,
            null => null,
            _ => int.MinValue,
        } : null;

    private sealed record StoredRule(string Base, string Matching, int State, int Status, string? UniqueName);

    private sealed record StoredCondition(Guid? ParentRule, string? BaseAttribute, string? MatchingAttribute, int? Operator, int? Param);

    /// <summary>The stored rule (read with UPDLOCK, HOLDLOCK: a publish waits for the write); null when it does not exist.</summary>
    private static async Task<StoredRule?> StoredRuleAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(BaseEntityName)}, {s.Quote(MatchingEntityName)}, {s.Dialect.IsNull(s.Quote(StateCode), "0")}, {s.Dialect.IsNull(s.Quote(StatusCode), "0")}, {s.Quote(UniqueName)} " +
            $"FROM {s.DataTable(DuplicateDetectionSchema.Rule)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {s.Quote(RuleId)} = @_id{s.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@_id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new StoredRule(rd.IsDBNull(0) ? "" : rd.GetString(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3),
            rd.IsDBNull(4) ? null : rd.GetString(4));
    }

    private static async Task<StoredCondition?> StoredConditionAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(RegardingObjectId)}, {s.Quote(BaseAttributeName)}, {s.Quote(MatchingAttributeName)}, {s.Quote(OperatorCode)}, {s.Quote(OperatorParam)} " +
            $"FROM {s.DataTable(Condition)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {s.Quote(ConditionId)} = @_id{s.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@_id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new StoredCondition(rd.IsDBNull(0) ? null : rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1),
            rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetInt32(3), rd.IsDBNull(4) ? null : rd.GetInt32(4));
    }
}
