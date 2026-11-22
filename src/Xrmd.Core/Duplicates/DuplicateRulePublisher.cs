using System.Data;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Async;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Duplicates;

/// <summary>
/// PublishDuplicateRule and UnpublishDuplicateRule (plan 2.15, rulings D10 and D11), on the caller's session and transaction.
/// The state of a rule is written here as the system (the rules of <see cref="Xrmd.Security.DuplicateRuleRules"/> refuse it to
/// every client), the match code tables through <see cref="MatchCodeStore"/>, and the publish job through the
/// <see cref="AsyncOperationQueue"/> (<see cref="DuplicateRulePublishJob"/> runs it).
/// </summary>
public sealed class DuplicateRulePublisher(IMetadataStore store, MatchCodeStore matchCodes, AsyncOperationQueue queue)
{
    /// <summary>The message names.</summary>
    public const string PublishMessage = "PublishDuplicateRule", UnpublishMessage = "UnpublishDuplicateRule";

    /// <summary>The request parameter naming the rule.</summary>
    public const string RuleIdParameter = "DuplicateRuleId";

    /// <summary>The match code tables this publisher creates and drops.</summary>
    public MatchCodeStore MatchCodes => matchCodes;

    /// <summary>The system job queue the publish jobs (and the bulk detection jobs of <see cref="BulkDetectDuplicatesJob"/>) go to.</summary>
    internal AsyncOperationQueue Queue => queue;

    /// <summary>
    /// PublishDuplicateRule (D10). The publishes of one base table are serialized by an application lock taken before the checks
    /// (<see cref="LockBaseTableAsync"/>), so two of them neither deadlock nor both pass the limit. Checked in this order: the
    /// rule exists (404); it is not Publishing already (its job would lose the codes of a rebuilt table: 400, wait for the
    /// job); <c>isduplicatedetectionenabled</c> is on; both
    /// tables have <c>IsDuplicateDetectionEnabled</c>; the rule has a condition; fewer than
    /// <see cref="MaxPublishedPerBaseTable"/> other rules of its base table are in Publishing or Published; with
    /// <c>excludeinactiverecords</c>, the matching table's <c>statecode</c> has no option but 0 and 1 (only 0 is active). Then,
    /// in the session's transaction: the rule goes to Publishing (0/1) with its match code table names, a job of operationtype 7
    /// is queued (regarding the rule, owned by <paramref name="caller"/>), and its match code table is created empty (a rule
    /// already published is rebuilt) and registered with that job's id, the identity of this publish (a job of an earlier
    /// publish of the rule writes nothing any more); the worker is woken after the commit.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist; 400 InvalidArgument for each refusal.</exception>
    public async Task<Guid> PublishAsync(DataSession s, Caller caller, Guid ruleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        var baseEntity = await BaseEntityAsync(s, ruleId, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(DuplicateDetectionSchema.Rule, ruleId);
        await LockBaseTableAsync(s, baseEntity, ct);
        // PostgreSQL has no key-range lock: an unpublish of every rule of a table or of the organization
        // (DuplicateDetectionSchema.UnpublishRulesAsync) takes this lock too, so this publish waits for it and then reads the
        // switches it turned off (nothing is sent on SQL Server, whose range lock does it).
        await s.Dialect.LockTableWritesAsync(s, DdlGenerator.DataSchema, DuplicateDetectionSchema.Rule, ct);
        var rule = await StoredRuleAsync(s, ruleId, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(DuplicateDetectionSchema.Rule, ruleId);
        if (rule.Status == RuleStatus.Publishing)
            throw XrmdFaultException.InvalidArgument(
                $"The duplicate detection rule {ruleId} is being published: wait for its publish job" +
                (await PendingJobAsync(s, ruleId, ct) is { } pending ? $" ({pending})" : "") + " to end before publishing it again.");
        if (!await OrganizationEnabledAsync(s, ct))
            throw XrmdFaultException.InvalidArgument(
                $"Duplicate detection is off for the organization ({OrgEnabled}): turn it on before publishing a duplicate detection rule.");
        foreach (var table in new[] { rule.Base, rule.Matching }.Distinct(StringComparer.Ordinal))
            if (!store.DuplicateDetectionTables.Contains(table))
                throw XrmdFaultException.InvalidArgument(
                    $"The table '{table}' does not have IsDuplicateDetectionEnabled: turn duplicate detection on for the table before publishing a rule for it.");
        if (rule.Conditions == 0)
            throw XrmdFaultException.InvalidArgument($"The duplicate detection rule {ruleId} has no condition: add one before publishing it.");
        if (await OtherPublishedAsync(s, rule.Base, ruleId, ct) >= MaxPublishedPerBaseTable)
            throw XrmdFaultException.InvalidArgument(
                $"The table '{rule.Base}' already has {MaxPublishedPerBaseTable} published duplicate detection rules: unpublish one first.");
        if (rule.ExcludeInactive && HasOtherStates(store.GetEntity(rule.Matching)))
            throw XrmdFaultException.InvalidArgument(
                $"The rule excludes inactive records, but the table '{rule.Matching}' has states other than Active (0) and Inactive (1): xrmverse treats only 0 as active, so it cannot tell which records to exclude.");

        await SetStateAsync(s, caller, ruleId, RuleState.Inactive, RuleStatus.Publishing, MatchCodeTableName(ruleId), ct);
        var job = await queue.EnqueueSystemJobAsync(s, caller, AsyncOperationQueue.DuplicateRulePublish,
            $"Publish duplicate detection rule {rule.Name}", PublishMessage, DuplicateDetectionSchema.Rule, ruleId, null, ct);
        // The table is registered with the job: this publish's identity, which binds every write of the job to it.
        await MatchCodeStore.CreateTableAsync(s, ruleId, rule.Matching, job, ct);
        matchCodes.Cache.InvalidateWith(s);
        s.OnCommitted(queue.Signal);
        return job;
    }

    /// <summary>
    /// UnpublishDuplicateRule (D11), synchronous: the rule goes to Inactive/Unpublished (0/0) with its match code table names
    /// cleared, its match code table is dropped and its registry row removed, in the session's transaction. A rule already
    /// unpublished stays so.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist.</exception>
    public async Task UnpublishAsync(DataSession s, Caller caller, Guid ruleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        _ = await StoredRuleAsync(s, ruleId, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(DuplicateDetectionSchema.Rule, ruleId);
        await SetStateAsync(s, caller, ruleId, RuleState.Inactive, RuleStatus.Unpublished, null, ct);
        await MatchCodeStore.DropTableAsync(s, ruleId, ct);
        matchCodes.Cache.InvalidateWith(s);
    }

    /// <summary>
    /// Unpublishes every rule in Publishing or Published whose base or matching table is <paramref name="entity"/> (every rule
    /// when null), in the session's transaction (D11: the organization switch turned off).
    /// </summary>
    public async Task UnpublishAllAsync(DataSession s, string? entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await UnpublishRulesAsync(s, entity, ct);
        matchCodes.Cache.InvalidateWith(s);
    }

    /// <summary>
    /// The end of the publish job <paramref name="publishJob"/>: the rule goes from Publishing to Published (1/2). False when the
    /// rule is no longer Publishing for that job (unpublished, republished or deleted meanwhile): nothing changes.
    /// </summary>
    public async Task<bool> CompletePublishAsync(DataSession s, Guid ruleId, Guid publishJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var done = await ChangeStateFromPublishingAsync(s, ruleId, publishJob, RuleState.Active, RuleStatus.Published, ct);
        matchCodes.Cache.InvalidateWith(s);
        return done;
    }

    /// <summary>
    /// A failed, cancelled or deleted publish job <paramref name="publishJob"/> (D10): a rule still Publishing for that job goes
    /// back to Unpublished (0/0) and its match code table is dropped. A rule in any other state, or Publishing for another job
    /// (republished meanwhile), is left as it is.
    /// </summary>
    /// <returns>True when the rule was reset.</returns>
    public async Task<bool> ResetFailedPublishAsync(DataSession s, Guid ruleId, Guid publishJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var reset = await ChangeStateFromPublishingAsync(s, ruleId, publishJob, RuleState.Inactive, RuleStatus.Unpublished, ct);
        if (reset) await MatchCodeStore.DropTableAsync(s, ruleId, ct);
        matchCodes.Cache.InvalidateWith(s);
        return reset;
    }

    /// <summary>
    /// A system job cancelled (3/32) or deleted through the core (<see cref="CoreOperations"/>), in its transaction: when it is the
    /// publish job of a rule still Publishing, the rule goes back to Unpublished (0/0) as for a failed job
    /// (<see cref="ResetFailedPublishAsync"/>), so a republish need not wait for the startup sweep. Nothing for any other job.
    /// </summary>
    public async Task OnJobEndedAsync(DataSession s, Guid asyncOperationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (await MatchCodeStore.RuleOfPublishJobAsync(s, asyncOperationId, ct) is { } ruleId)
            await ResetFailedPublishAsync(s, ruleId, asyncOperationId, ct);
    }

    /// <summary>
    /// The Delete of a rule (D4, any state): its match code table and registry row go in the Delete's transaction.
    /// </summary>
    public async Task OnRuleDeletedAsync(DataSession s, Guid ruleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await MatchCodeStore.DropTableAsync(s, ruleId, ct);
        matchCodes.Cache.InvalidateWith(s);
    }

    /// <summary>
    /// The startup sweep of D10: every rule left Publishing without a publish job that can still run (the job its match code
    /// table is registered with, Ready or Locked/In Progress) goes back to Unpublished (0/0) and its match code table is dropped, as a failed job
    /// would have done (e.g. the process stopped between a failure and its reset).
    /// </summary>
    /// <returns>The number of rules reset.</returns>
    public static async Task<int> ResetStrandedPublishesAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var d = factory.Dialect;
        string Q(string name) => d.Quote(name);
        await using var conn = await factory.OpenAsync(ct);
        // Plan 2.7b, Task 4: the existence check of the rule table is a statement of its own (an organization seeded before plan
        // 2.15 has none).
        await using (var exists = conn.Command(
                         $"SELECT CASE WHEN {d.TableExists(DdlGenerator.DataSchema, DuplicateDetectionSchema.Rule)} THEN 1 ELSE 0 END"))
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0) return 0;
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rules = new List<Guid>();
        await using (var cmd = conn.Command($"""
            SELECT r.{Q(RuleId)} FROM {d.DataTable(DuplicateDetectionSchema.Rule)} AS r{d.LockHint(RowLock.UpdateRange)}
                WHERE r.{Q("statuscode")} = {RuleStatus.Publishing} AND NOT EXISTS (
                    SELECT 1 FROM meta.matchcode_table AS t
                    JOIN {d.DataTable("asyncoperation")} AS j ON j.asyncoperationid = t.publish_job
                    WHERE t.duplicateruleid = r.{Q(RuleId)} AND j.operationtype = {AsyncOperationQueue.DuplicateRulePublish} AND j.statecode IN (0, 2)){d.LockClause(RowLock.UpdateRange, "r")};
            """, tx))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) rules.Add(rd.GetGuid(0));
        var session = new DataSession(conn, tx);
        foreach (var rule in rules)
            if (await ChangeStateFromPublishingAsync(session, rule, null, RuleState.Inactive, RuleStatus.Unpublished, ct))
                await MatchCodeStore.DropTableAsync(session, rule, ct);
        await tx.CommitAsync(ct);
        return rules.Count;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private sealed record StoredRule(string Name, string Base, string Matching, bool ExcludeInactive, int Conditions, int Status);

    /// <summary>The base table of a rule, read without a lock (it never changes, D4); null when the rule does not exist.</summary>
    private static async Task<string?> BaseEntityAsync(DataSession s, Guid id, CancellationToken ct)
    {
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT {d.IsNull(d.Quote(BaseEntityName), d.TextLiteral(""))} FROM {s.DataTable(DuplicateDetectionSchema.Rule)} WHERE {d.Quote(RuleId)} = @_id");
        cmd.AddParameter("@_id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// The application lock (transaction owner, exclusive) of the publishes of <paramref name="baseEntity"/>: taken before any
    /// other lock of the publish, so publishes of one base table run one after the other until their commit.
    /// </summary>
    private static async Task LockBaseTableAsync(DataSession s, string baseEntity, CancellationToken ct)
    {
        try
        {
            await s.Dialect.AcquireAppLockAsync(s, "xrmd-duplicaterule-publish:" + baseEntity, session: false, ct);
        }
        catch (AppLockException)
        {
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"Could not lock the duplicate detection rules of the table '{baseEntity}'.", 500);
        }
    }

    /// <summary>The publish job of the rule that can still run (Ready or Locked/In Progress); null when there is none.</summary>
    private static async Task<Guid?> PendingJobAsync(DataSession s, Guid ruleId, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit(
            $"SELECT asyncoperationid FROM {s.DataTable("asyncoperation")} WHERE regardingobjectid = @_id " +
            $"AND operationtype = {AsyncOperationQueue.DuplicateRulePublish} AND statecode IN (0, 2) ORDER BY createdon DESC", "1"));
        cmd.AddParameter("@_id", DbType.Guid, ruleId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>
    /// The rule (read with UPDLOCK, HOLDLOCK: a concurrent write of it, a condition write included, waits) and its number of
    /// conditions (whose writes lock the rule's row first, so they need no lock of their own here).
    /// </summary>
    private static async Task<StoredRule?> StoredRuleAsync(DataSession s, Guid id, CancellationToken ct)
    {
        var d = s.Dialect;
        string Q(string name) => d.Quote(name);
        await using var cmd = s.Command($"""
            SELECT {d.IsNull($"r.{Q(Name)}", d.TextLiteral(""))}, r.{Q(BaseEntityName)}, r.{Q(MatchingEntityName)}, {d.IsNull($"r.{Q(ExcludeInactiveRecords)}", d.BooleanLiteral(false))},
                (SELECT COUNT(*) FROM {s.DataTable(Condition)} AS c WHERE c.{Q(RegardingObjectId)} = r.{Q(RuleId)}),
                {d.IsNull($"r.{Q("statuscode")}", "0")}
            FROM {s.DataTable(DuplicateDetectionSchema.Rule)} AS r{d.LockHint(RowLock.UpdateRange)} WHERE r.{Q(RuleId)} = @_id{d.LockClause(RowLock.UpdateRange, "r")}
            """);
        cmd.AddParameter("@_id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new StoredRule(rd.GetString(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.IsDBNull(2) ? "" : rd.GetString(2), rd.GetBoolean(3),
            Convert.ToInt32(rd.GetValue(4)), rd.GetInt32(5));
    }

    private static async Task<bool> OrganizationEnabledAsync(DataSession s, CancellationToken ct)
    {
        var d = s.Dialect;
        await using var cmd = s.Command(d.Limit($"SELECT {d.IsNull(d.Quote(OrgEnabled), d.BooleanLiteral(false))} FROM {s.DataTable("organization")}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// The other rules of <paramref name="baseEntity"/> in Publishing or Published. No lock hint: the publishes of the table are
    /// serialized by <see cref="LockBaseTableAsync"/>, and a range lock here could close a cycle with a publish of another table.
    /// </summary>
    private static async Task<int> OtherPublishedAsync(DataSession s, string baseEntity, Guid ruleId, CancellationToken ct)
    {
        string Q(string name) => s.Dialect.Quote(name);
        await using var cmd = s.Command($"""
            SELECT COUNT(*) FROM {s.DataTable(DuplicateDetectionSchema.Rule)}
            WHERE {Q(BaseEntityName)} = @_base AND {Q(RuleId)} <> @_id AND {Q("statuscode")} IN ({RuleStatus.Publishing}, {RuleStatus.Published})
            """);
        cmd.AddParameter("@_base", DbType.String, baseEntity, 160);
        cmd.AddParameter("@_id", DbType.Guid, ruleId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>True when the table's <c>statecode</c> has an option other than 0 and 1.</summary>
    private static bool HasOtherStates(EntityMetadata? entity) =>
        entity?.Attributes?.OfType<StateAttributeMetadata>().FirstOrDefault()?.OptionSet?.Options.Any(o => o.Value is not (0 or 1)) == true;

    private static async Task SetStateAsync(DataSession s, Caller caller, Guid ruleId, int state, int status, string? table, CancellationToken ct)
    {
        var d = s.Dialect;
        string Q(string name) => d.Quote(name);
        await using var cmd = s.Command($"""
            UPDATE {s.DataTable(DuplicateDetectionSchema.Rule)}
            SET {Q("statecode")} = @_state, {Q("statuscode")} = @_status, {Q(BaseEntityMatchCodeTable)} = @_table, {Q(MatchingEntityMatchCodeTable)} = @_table,
                {Q("modifiedon")} = {d.UtcNow}, {Q("modifiedby")} = {d.IsNull("@_by", Q("modifiedby"))}
            WHERE {Q(RuleId)} = @_id
            """);
        cmd.AddParameter("@_state", DbType.Int32, state);
        cmd.AddParameter("@_status", DbType.Int32, status);
        cmd.AddParameter("@_table", DbType.String, (object?)table ?? DBNull.Value, 50);
        cmd.AddParameter("@_by", DbType.Guid, caller.UserId == Guid.Empty ? DBNull.Value : caller.UserId);
        cmd.AddParameter("@_id", DbType.Guid, ruleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Moves a rule in Publishing to <paramref name="state"/>/<paramref name="status"/>; with <paramref name="publishJob"/>, only
    /// while its match code table is still registered with that job (the publish the job belongs to). The rule's row is locked
    /// first by a locking read of its own (UPDLOCK; FOR UPDATE), after the table lock the UPDATE needs (PostgreSQL's ROW EXCLUSIVE,
    /// <see cref="ISqlDialect.LockTableRowWritesAsync"/>), then the UPDATE reads the registry row with a shared lock held to
    /// the end of the transaction: the order of an unpublish, a republish and a delete of the rule (rule row, then registry row),
    /// so they wait for each other instead of deadlocking (fix round 2: PostgreSQL evaluates the subquery's FOR SHARE during the
    /// UPDATE's scan, before it locks the rule's row).
    /// </summary>
    private static async Task<bool> ChangeStateFromPublishingAsync(DataSession s, Guid ruleId, Guid? publishJob, int state, int status,
        CancellationToken ct)
    {
        var clears = status == RuleStatus.Unpublished;
        var d = s.Dialect;
        string Q(string name) => d.Quote(name);
        // The table lock the UPDATE takes, first: the order table, then row of a publish and an unpublish of every rule of a table
        // (LockTableWritesAsync, then their row locks), so they wait for each other in one order (fix round 3; nothing on SQL Server).
        await d.LockTableRowWritesAsync(s, DdlGenerator.DataSchema, DuplicateDetectionSchema.Rule, ct);
        await using (var lockRule = s.Command(
                         $"SELECT {Q(RuleId)} FROM {s.DataTable(DuplicateDetectionSchema.Rule)}{d.LockHint(RowLock.UpdateRow)} " +
                         $"WHERE {Q(RuleId)} = @_id{d.LockClause(RowLock.UpdateRow)}"))
        {
            lockRule.AddParameter("@_id", DbType.Guid, ruleId);
            if (await lockRule.ExecuteScalarAsync(ct) is not Guid) return false;
        }
        // The registry row is read with a shared lock held to the end of the transaction (HOLDLOCK; FOR SHARE in the subquery on
        // PostgreSQL), so an unpublish, a republish or a delete of the rule, which removes the row first, waits for this commit.
        await using var cmd = s.Command($"""
            UPDATE {s.DataTable(DuplicateDetectionSchema.Rule)}
            SET {Q("statecode")} = @_state, {Q("statuscode")} = @_status, {Q("modifiedon")} = {d.UtcNow}
                {(clears ? $", {Q(BaseEntityMatchCodeTable)} = NULL, {Q(MatchingEntityMatchCodeTable)} = NULL" : "")}
            WHERE {Q(RuleId)} = @_id AND {Q("statuscode")} = {RuleStatus.Publishing}
                AND (@_job IS NULL OR EXISTS (
                    SELECT 1 FROM meta.matchcode_table{d.LockHint(RowLock.Shared)} WHERE duplicateruleid = @_id AND publish_job = @_job{d.LockClause(RowLock.Shared)}))
            """);
        cmd.AddParameter("@_state", DbType.Int32, state);
        cmd.AddParameter("@_status", DbType.Int32, status);
        cmd.AddParameter("@_id", DbType.Guid, ruleId);
        cmd.AddParameter("@_job", DbType.Guid, (object?)publishJob ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
