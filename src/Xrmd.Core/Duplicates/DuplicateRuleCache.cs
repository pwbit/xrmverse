using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Duplicates;

/// <summary>
/// The duplicate detection rules in Publishing or Published with their conditions, process-wide (plan 2.15). A snapshot is
/// loaded through the caller's session (<see cref="EnsureLoadedAsync"/>: a read of the committed rows, or of the session's own
/// uncommitted ones) and kept until <see cref="Invalidate"/> or a change of the metadata version (a table switched off or
/// deleted unpublishes or deletes its rules in a metadata transaction). The core invalidates it when a write of
/// <c>duplicaterule</c>, <c>duplicaterulecondition</c> or <c>organization</c> happens, and again after its commit or rollback
/// (<see cref="InvalidateWith"/>), as the publishing messages and their job do.
/// </summary>
/// <remarks>
/// A snapshot can be briefly stale for another session: a write that misses a rule just published has no code until the
/// publish job fills it; a write that still sees a rule just unpublished finds its match code table gone and skips it
/// (<see cref="MatchCodeStore"/>). Neither makes a lookup find a false duplicate.
/// </remarks>
public sealed class DuplicateRuleCache(IMetadataStore store)
{
    private sealed record Snapshot(long Version, long MetadataStamp, IReadOnlyDictionary<string, IReadOnlyList<PublishedRule>> ByBase,
        IReadOnlyDictionary<string, IReadOnlyList<PublishedRule>> ByMatching);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PublishedRule>> None =
        new Dictionary<string, IReadOnlyList<PublishedRule>>(StringComparer.Ordinal);

    private long _version;
    private Snapshot? _snapshot;

    /// <summary>Drops the snapshot: the next <see cref="EnsureLoadedAsync"/> reloads it.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>
    /// <see cref="Invalidate"/> now and again when <paramref name="session"/> commits or rolls back: for a write of the rules or
    /// of the organization switch in the session's transaction (a load in the transaction may have seen its uncommitted rows).
    /// </summary>
    public void InvalidateWith(DataSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Invalidate();
        session.OnCommitted(Invalidate);
        session.OnRolledBack(Invalidate);
    }

    /// <summary>Loads the snapshot through <paramref name="session"/> unless the current one is still valid.</summary>
    public async ValueTask EnsureLoadedAsync(DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var version = Interlocked.Read(ref _version);
        var stamp = store.VersionStamp;
        if (Volatile.Read(ref _snapshot) is { } current && current.Version == version && current.MetadataStamp == stamp) return;

        var rules = await LoadAsync(session, null, ct);
        var loaded = new Snapshot(version, stamp, Group(rules, r => r.BaseEntity), Group(rules, r => r.MatchingEntity));
        // A newer snapshot loaded meanwhile wins.
        while (Volatile.Read(ref _snapshot) is var previous && (previous is null || previous.Version <= version))
            if (Interlocked.CompareExchange(ref _snapshot, loaded, previous) == previous) break;
    }

    /// <summary>
    /// The rules in Publishing or Published whose base table is <paramref name="entity"/>, from the last snapshot loaded (none
    /// before the first <see cref="EnsureLoadedAsync"/>). A Published-only use filters on <see cref="PublishedRule.StatusCode"/>.
    /// </summary>
    public IReadOnlyList<PublishedRule> ForBase(string entity) => Of(Volatile.Read(ref _snapshot)?.ByBase, entity);

    /// <summary>The rules in Publishing or Published whose matching table is <paramref name="entity"/> (see <see cref="ForBase"/>).</summary>
    public IReadOnlyList<PublishedRule> ForMatching(string entity) => Of(Volatile.Read(ref _snapshot)?.ByMatching, entity);

    private static IReadOnlyList<PublishedRule> Of(IReadOnlyDictionary<string, IReadOnlyList<PublishedRule>>? map, string entity) =>
        map is not null && map.TryGetValue(entity, out var rules) ? rules : [];

    private static IReadOnlyDictionary<string, IReadOnlyList<PublishedRule>> Group(IReadOnlyList<PublishedRule> rules, Func<PublishedRule, string> key) =>
        rules.Count == 0 ? None : rules.GroupBy(key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<PublishedRule>)g.ToList(), StringComparer.Ordinal);

    /// <summary>
    /// The rules in Publishing or Published with their conditions (ordered by <c>duplicateruleconditionid</c>), read in
    /// <paramref name="session"/>; only <paramref name="ruleId"/> when given. A rule without a condition is left out (it cannot
    /// be published). Empty on an organization without the duplicate detection tables.
    /// </summary>
    public async Task<IReadOnlyList<PublishedRule>> LoadAsync(DataSession session, Guid? ruleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (store.GetEntity(DuplicateDetectionSchema.Rule) is null || store.GetEntity(Condition) is null) return [];
        // Plan 2.7b, Task 4: written in the dialect (the SQL Server text is the T-SQL it was).
        var d = session.Dialect;
        string Q(string name) => d.Quote(name);
        var no = d.BooleanLiteral(false);
        await using var cmd = session.Command($"""
            SELECT r.{Q(RuleId)}, r.{Q(BaseEntityName)}, r.{Q(MatchingEntityName)}, {d.IsNull($"r.{Q(IsCaseSensitive)}", no)},
                {d.IsNull($"r.{Q(ExcludeInactiveRecords)}", no)}, r.{Q("statuscode")},
                c.{Q(ConditionId)}, c.{Q(BaseAttributeName)}, c.{Q(MatchingAttributeName)}, c.{Q(OperatorCode)}, c.{Q(OperatorParam)},
                {d.IsNull($"c.{Q(IgnoreBlankValues)}", no)}
            FROM {session.DataTable(DuplicateDetectionSchema.Rule)} AS r
            JOIN {session.DataTable(Condition)} AS c ON c.{Q(RegardingObjectId)} = r.{Q(RuleId)}
            WHERE r.{Q("statuscode")} IN ({RuleStatus.Publishing}, {RuleStatus.Published}) AND (@_rule IS NULL OR r.{Q(RuleId)} = @_rule)
            ORDER BY r.{Q(RuleId)}, c.{Q(ConditionId)}
            """);
        cmd.AddParameter("@_rule", DbType.Guid, (object?)ruleId ?? DBNull.Value);
        var rules = new List<PublishedRule>();
        List<DuplicateRuleCondition>? conditions = null;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var id = rd.GetGuid(0);
            if (rules.Count == 0 || rules[^1].Id != id)
            {
                conditions = [];
                rules.Add(new PublishedRule(id, rd.GetString(1), rd.GetString(2), rd.GetBoolean(3), rd.GetBoolean(4), rd.GetInt32(5), conditions));
            }
            conditions!.Add(new DuplicateRuleCondition(rd.GetGuid(6), rd.GetString(7), rd.GetString(8), (DuplicateOperator)rd.GetInt32(9),
                rd.IsDBNull(10) ? null : rd.GetInt32(10), rd.GetBoolean(11)));
        }
        return rules;
    }
}
