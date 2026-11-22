using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Duplicates;

/// <summary>
/// The match code tables of the rules in Publishing or Published (plan 2.15, ruling D9): <c>meta.matchcode_{ruleid:N}</c>
/// (<c>objectid</c> primary key, <c>matchcode binary(32)</c> indexed) holding the code of each row of the rule's matching
/// table, registered in <c>meta.matchcode_table</c>. Every core write keeps them in its own transaction
/// (<see cref="RefreshAsync(DataSession, EntityMetadata, Guid, CancellationToken)"/>, <see cref="RemoveAsync"/>), and the publish
/// job fills them (<see cref="FillBatchAsync"/>). Table names come from <see cref="MatchCodeTableName"/> only (a GUID), so no
/// text of a user ever reaches the DDL.
/// </summary>
/// <remarks>
/// Each write of a code first checks that the table still exists: a session whose <see cref="DuplicateRuleCache"/> snapshot
/// still lists a rule another session has just unpublished skips it.
/// </remarks>
public sealed class MatchCodeStore(IMetadataStore store, DuplicateRuleCache cache)
{
    /// <summary>The cache of the rules whose tables this store keeps.</summary>
    public DuplicateRuleCache Cache => cache;

    /// <summary>
    /// Re-reads the row <paramref name="id"/> of <paramref name="m"/> in <paramref name="s"/> and, for every rule in Publishing
    /// or Published whose matching table is <paramref name="m"/>, writes its code (or deletes it when the row has none or no
    /// longer exists). Nothing is read when no rule matches the table.
    /// </summary>
    public async Task RefreshAsync(DataSession s, EntityMetadata m, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        await cache.EnsureLoadedAsync(s, ct);
        var rules = cache.ForMatching(m.LogicalName);
        if (rules.Count > 0) await RefreshAsync(s, m, [id], rules, ct);
    }

    /// <summary>
    /// <see cref="RefreshAsync(DataSession, EntityMetadata, Guid, CancellationToken)"/> for an Update that wrote
    /// <paramref name="changed"/>: only when one of them is a matching attribute of a rule (a code depends on nothing else).
    /// </summary>
    public async Task RefreshAsync(DataSession s, EntityMetadata m, Guid id, IEnumerable<string> changed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(changed);
        await cache.EnsureLoadedAsync(s, ct);
        var rules = cache.ForMatching(m.LogicalName);
        if (rules.Count == 0) return;
        var names = changed.ToHashSet(StringComparer.Ordinal);
        var touched = rules.Where(r => r.Conditions.Any(c => names.Contains(c.MatchingAttribute))).ToList();
        if (touched.Count > 0) await RefreshAsync(s, m, [id], touched, ct);
    }

    /// <summary>
    /// The rows of <paramref name="m"/> whose <paramref name="lookup"/> holds <paramref name="id"/>, when a rule matches on that
    /// lookup (empty otherwise): the rows a RemoveLink is about to clear, refreshed by <see cref="RefreshManyAsync"/> once it has.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RowsReferencingAsync(DataSession s, EntityMetadata m, string lookup, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        await cache.EnsureLoadedAsync(s, ct);
        if (!cache.ForMatching(m.LogicalName).Any(r => r.Conditions.Any(c => c.MatchingAttribute == lookup))) return [];
        await using var cmd = s.Command(
            $"SELECT {s.Dialect.Quote(SystemAttributes.PrimaryIdName(m))} FROM {s.DataTable(m.LogicalName)} WHERE {s.Dialect.Quote(lookup)} = @_id");
        cmd.AddParameter("@_id", DbType.Guid, id);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary><see cref="RefreshAsync(DataSession, EntityMetadata, Guid, CancellationToken)"/> for several rows.</summary>
    public async Task RefreshManyAsync(DataSession s, EntityMetadata m, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;
        await cache.EnsureLoadedAsync(s, ct);
        var rules = cache.ForMatching(m.LogicalName);
        if (rules.Count > 0) await RefreshAsync(s, m, ids, rules, ct);
    }

    /// <summary>Deletes the code of the row <paramref name="id"/> of <paramref name="entity"/> from every rule matching that table.</summary>
    public async Task RemoveAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(entity);
        await cache.EnsureLoadedAsync(s, ct);
        var rules = cache.ForMatching(entity);
        if (rules.Count == 0) return;
        var registered = await RegisteredAsync(s, rules.Select(r => r.Id), ct);
        var sql = new StringBuilder();
        foreach (var rule in rules.Where(r => registered.Contains(r.Id)))
            sql.Append($"DELETE FROM meta.{s.Dialect.Quote(MatchCodeTableName(rule.Id))} WHERE objectid = @_id;\n");
        if (sql.Length == 0) return;
        await using var cmd = s.Command(sql.ToString());
        cmd.AddParameter("@_id", DbType.Guid, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The rules of <paramref name="ruleIds"/> whose match code table is registered in <c>meta.matchcode_table</c>, so exists
    /// (plan 2.7b, Task 4: it replaces the <c>IF OBJECT_ID(…)</c> before each statement on a match code table). The registry rows
    /// are read with a shared lock held to the end of the transaction (<c>HOLDLOCK</c>; <c>FOR SHARE</c>): every path that drops
    /// a match code table deletes its registry row first, so the tables of the rules returned stay until then, and a writer takes
    /// the lock order of every other path, registry then table (fix round 1: a lock released at once let a writer holding the
    /// table wait for the registry row while an unpublish holding that row waited for the table). A writer that later unpublishes
    /// or deletes the same rule in its own transaction upgrades its own shared lock (S to X) and waits for the other holders; two
    /// such transactions that both hold S and both upgrade still deadlock, and the engine fails one of them.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> RegisteredAsync(DataSession s, IEnumerable<Guid> ruleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ruleIds);
        var ids = ruleIds.Distinct().ToList();
        var registered = new HashSet<Guid>();
        if (ids.Count == 0) return registered;
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT duplicateruleid FROM meta.matchcode_table{d.LockHint(RowLock.Shared)} " +
            $"WHERE duplicateruleid IN ({string.Join(", ", ids.Select((_, i) => $"@_g{i}"))}){d.LockClause(RowLock.Shared)}");
        for (var i = 0; i < ids.Count; i++) cmd.AddParameter($"@_g{i}", DbType.Guid, ids[i]);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) registered.Add(rd.GetGuid(0));
        return registered;
    }

    /// <summary>
    /// Creates the (empty) match code table of <paramref name="ruleId"/>, dropping the one it had, and registers it with
    /// <paramref name="matchingEntity"/> and the publish job <paramref name="publishJob"/> in <c>meta.matchcode_table</c>: the
    /// job's identity, which every write of the job checks (<see cref="IsCurrentPublishAsync"/>), so a job of an earlier publish of
    /// the same rule never writes into this table. The registry row goes first, as everywhere (lock order: registry, then table).
    /// </summary>
    public static async Task CreateTableAsync(DataSession s, Guid ruleId, string matchingEntity, Guid publishJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(matchingEntity);
        var table = MatchCodeTableName(ruleId);
        // Plan 2.7b, Task 4: written in the dialect (the SQL Server text is the T-SQL it was). The index is created with the
        // table, so it needs no existence check.
        var d = s.Dialect;
        await using var cmd = s.Command($"""
            DELETE FROM meta.matchcode_table WHERE duplicateruleid = @_rule;
            DROP TABLE IF EXISTS meta.{d.Quote(table)};
            CREATE TABLE meta.{d.Quote(table)} (
                objectid  {d.ColumnType(SqlColumnKind.Guid)} NOT NULL CONSTRAINT {d.Quote(d.ObjectName($"PK_{table}"))} PRIMARY KEY,
                matchcode {d.ColumnType(SqlColumnKind.Binary, MatchCode.Length)} NOT NULL
            );
            CREATE INDEX {d.Quote(d.ObjectName($"IX_{table}_matchcode"))} ON meta.{d.Quote(table)} (matchcode);
            INSERT INTO meta.matchcode_table (duplicateruleid, table_name, matching_entity, created_on, publish_job)
                VALUES (@_rule, @_table, @_entity, {d.UtcNow}, @_job);
            """);
        cmd.AddParameter("@_rule", DbType.Guid, ruleId);
        cmd.AddParameter("@_table", DbType.String, table, 128);
        cmd.AddParameter("@_entity", DbType.String, matchingEntity, 128);
        cmd.AddParameter("@_job", DbType.Guid, publishJob);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Drops the match code table of <paramref name="ruleId"/> (when it exists) and its <c>meta.matchcode_table</c> row (the row
    /// first: lock order registry, then table, as a publish job's batch).
    /// </summary>
    public static async Task DropTableAsync(DataSession s, Guid ruleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"""
            DELETE FROM meta.matchcode_table WHERE duplicateruleid = @_rule;
            DROP TABLE IF EXISTS meta.{s.Dialect.Quote(MatchCodeTableName(ruleId))};
            """);
        cmd.AddParameter("@_rule", DbType.Guid, ruleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// True when the match code table of <paramref name="ruleId"/> belongs to the publish job <paramref name="publishJob"/> (its
    /// <c>meta.matchcode_table</c> row names that job). The row is read with a locking read held until the transaction ends
    /// (HOLDLOCK, not the RCSI snapshot): an unpublish, a republish or a delete of the rule, which removes the row before it
    /// drops the table, waits for the transaction, so what the job writes in it lands in its own table or nowhere.
    /// </summary>
    /// <remarks>
    /// Plan 2.7b, Task 4: the row itself is read (PostgreSQL locks no row of a <c>COUNT(*)</c>); on SQL Server the range lock
    /// of the key is the same.
    /// </remarks>
    public static async Task<bool> IsCurrentPublishAsync(DataSession s, Guid ruleId, Guid publishJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT duplicateruleid FROM meta.matchcode_table{d.LockHint(RowLock.Shared)} WHERE duplicateruleid = @_rule AND publish_job = @_job" +
            d.LockClause(RowLock.Shared));
        cmd.AddParameter("@_rule", DbType.Guid, ruleId);
        cmd.AddParameter("@_job", DbType.Guid, publishJob);
        return await cmd.ExecuteScalarAsync(ct) is Guid;
    }

    /// <summary>The rule whose match code table belongs to the publish job <paramref name="publishJob"/>; null when none does.</summary>
    public static async Task<Guid?> RuleOfPublishJobAsync(DataSession s, Guid publishJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(s.Dialect.Limit("SELECT duplicateruleid FROM meta.matchcode_table WHERE publish_job = @_job", "1"));
        cmd.AddParameter("@_job", DbType.Guid, publishJob);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>Tests only: awaited by <see cref="FillBatchAsync"/> between its read and its inserts, with the rule's id.</summary>
    internal Func<Guid, Task>? AfterBackfillReadForTest { get; set; }

    /// <summary>
    /// One batch of the publish job <paramref name="publishJob"/> (ruling D10): reads up to <paramref name="size"/> rows of the
    /// rule's matching table after <paramref name="after"/> (in primary key order) and, when the rule's match code table still
    /// belongs to that job (<see cref="IsCurrentPublishAsync"/>, checked after the read and held to the commit), inserts the code of
    /// each row that has one and has no code yet (<c>INSERT … WHERE NOT EXISTS</c> under an update lock), so a code a core write has
    /// meanwhile written stays. A batch of a job whose publish was superseded (the rule unpublished, republished or deleted
    /// meanwhile) writes nothing.
    /// </summary>
    /// <returns>Whether the publish is still the job's, the number of rows read and the primary key of the last one (null when none).</returns>
    public async Task<(bool Current, int Read, Guid? Last)> FillBatchAsync(DataSession s, PublishedRule rule, Guid publishJob, Guid? after,
        int size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(rule);
        var m = store.GetEntity(rule.MatchingEntity)
                ?? throw new InvalidOperationException($"The matching table '{rule.MatchingEntity}' of duplicate detection rule {rule.Id} does not exist.");
        // PostgreSQL: a row created by a transaction still open is invisible to the batch's FOR SHARE read, where SQL Server's
        // REPEATABLEREAD read waits for it. The first batch waits for every open writer of the table instead (a writer whose
        // rule cache predates the publish wrote no code; every later write reloads the cache and codes its row itself). Nothing
        // is sent on SQL Server.
        if (after is null) await s.Dialect.LockTableSharedAsync(s, DdlGenerator.DataSchema, m.LogicalName, ct);
        var rows = await ReadRowsAsync(s, m, MatchingAttributes([rule]), after, size, null, ct);
        if (AfterBackfillReadForTest is { } hook) await hook(rule.Id);
        if (!await IsCurrentPublishAsync(s, rule.Id, publishJob, ct)) return (false, rows.Count, null);
        var codes = rows.Select(r => (r.Id, Code: Code(rule, m, r.Values))).Where(r => r.Code is not null).ToList();
        var table = MatchCodeTableName(rule.Id);
        // 2 parameters per row, within SQL Server's 2100.
        foreach (var chunk in codes.Chunk(1000))
        {
            var values = string.Join(", ", chunk.Select((_, i) => $"(@i{i}, @c{i})"));
            // On PostgreSQL the NOT EXISTS locks no missing key: a core write of the same row cannot insert its code meanwhile,
            // since the batch's read holds the row (FOR SHARE) and a created row's own transaction is not visible to it.
            await using var cmd = s.Command($"""
                INSERT INTO meta.{s.Dialect.Quote(table)} (objectid, matchcode)
                SELECT v.id, v.code FROM (VALUES {values}) AS v (id, code)
                WHERE NOT EXISTS (SELECT 1 FROM meta.{s.Dialect.Quote(table)} AS m{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE m.objectid = v.id);
                """);
            for (var i = 0; i < chunk.Length; i++)
            {
                cmd.AddParameter($"@i{i}", DbType.Guid, chunk[i].Id);
                cmd.AddParameter($"@c{i}", DbType.Binary, chunk[i].Code, MatchCode.Length);
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return (true, rows.Count, rows.Count == 0 ? null : rows[^1].Id);
    }

    /// <summary>
    /// The startup sweep (ruling D9): drops every <c>meta.matchcode_{guid}</c> table, and removes every <c>meta.matchcode_table</c>
    /// row, whose rule is not in Publishing or Published (a process stopped between a commit and its cleanup, or a database
    /// restored without its rules).
    /// </summary>
    /// <returns>The number of tables dropped.</returns>
    public static async Task<int> DropOrphanTablesAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var d = factory.Dialect;
        string Q(string name) => d.Quote(name);
        await using var conn = await factory.OpenAsync(ct);
        // The sweep runs only when the rule table exists (an organization seeded before plan 2.15 has none).
        await using (var exists = conn.Command(
                         $"SELECT CASE WHEN {d.TableExists(DdlGenerator.DataSchema, DuplicateDetectionSchema.Rule)} THEN 1 ELSE 0 END"))
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0) return 0;

        var liveRules = $"SELECT {Q(RuleId)} FROM {d.DataTable(DuplicateDetectionSchema.Rule)} " +
                        $"WHERE {Q("statuscode")} IN ({RuleStatus.Publishing}, {RuleStatus.Published})";
        var live = new HashSet<Guid>();
        await using (var cmd = conn.Command(liveRules))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) live.Add(rd.GetGuid(0));

        // Plan 2.7b, Task 4: the catalog both engines have (folded to lower case on PostgreSQL).
        var orphans = new List<Guid>();
        await using (var cmd = conn.Command(
                         "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                         "WHERE TABLE_SCHEMA = 'meta' AND TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME LIKE 'matchcode%'"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                if (TryParseTableName(rd.GetString(0)) is { } rule && !live.Contains(rule)) orphans.Add(rule);

        var sql = new StringBuilder();
        // Re-built from the parsed GUID: the name in the catalog never reaches the DDL as such.
        foreach (var rule in orphans) sql.Append($"DROP TABLE IF EXISTS meta.{Q(MatchCodeTableName(rule))};\n");
        sql.Append($"DELETE FROM meta.matchcode_table WHERE duplicateruleid NOT IN ({liveRules});");
        await using (var cmd = conn.Command(sql.ToString())) await cmd.ExecuteNonQueryAsync(ct);
        return orphans.Count;
    }

    /// <summary>The rule of a table named <c>matchcode_{guid:N}</c>; null for any other name (e.g. <c>matchcode_table</c>).</summary>
    private static Guid? TryParseTableName(string name) =>
        name.Length == "matchcode_".Length + 32 && name.StartsWith("matchcode_", StringComparison.Ordinal)
        && Guid.TryParseExact(name["matchcode_".Length..], "N", out var id) && MatchCodeTableName(id) == name
            ? id
            : null;

    // ---- rows and codes -----------------------------------------------------------------------------------------------

    private sealed record Row(Guid Id, Dictionary<string, object?> Values);

    private byte[]? Code(PublishedRule rule, EntityMetadata m, Dictionary<string, object?> values) =>
        MatchCode.Compute(rule, MatchSide.Matching, name => values.GetValueOrDefault(name), name => Attribute(m, name));

    private static AttributeMetadata Attribute(EntityMetadata m, string name) =>
        m.Attributes?.FirstOrDefault(a => a.LogicalName == name)
        ?? throw new InvalidOperationException($"The column '{m.LogicalName}.{name}' of a duplicate detection rule does not exist.");

    /// <summary>Writes (or deletes, for a null code) each row's code in each rule's table, in one batch.</summary>
    private async Task RefreshAsync(DataSession s, EntityMetadata m, IReadOnlyList<Guid> ids, IReadOnlyList<PublishedRule> rules,
        CancellationToken ct)
    {
        // One id and one code per rule for each row: chunks stay within SQL Server's 2100 parameters.
        var chunk = Math.Max(1, 1000 / (rules.Count + 1));
        for (var start = 0; start < ids.Count; start += chunk)
            await RefreshChunkAsync(s, m, ids.Skip(start).Take(chunk).ToList(), rules, ct);
    }

    /// <remarks>
    /// Plan 2.7b, Task 4 (ruling Q5): the codes of rules whose match code table is registered (<see cref="RegisteredAsync"/>,
    /// which replaces the <c>IF OBJECT_ID(…)</c> of each statement); a null code is deleted (one batch), any other written by the
    /// key lock of the row in that table, an <c>UPDATE</c> under a key-range lock (so the publish job's
    /// <c>INSERT … WHERE NOT EXISTS</c> cannot slip in between on SQL Server) and an <c>INSERT</c> when it changed no row.
    /// </remarks>
    private async Task RefreshChunkAsync(DataSession s, EntityMetadata m, IReadOnlyList<Guid> ids, IReadOnlyList<PublishedRule> rules,
        CancellationToken ct)
    {
        var rows = (await ReadRowsAsync(s, m, MatchingAttributes(rules), null, null, ids, ct)).ToDictionary(r => r.Id);
        var registered = await RegisteredAsync(s, rules.Select(r => r.Id), ct);
        var d = s.Dialect;
        var deletes = new StringBuilder();
        var deleted = new List<DbParameter>();
        var writes = new List<(string Table, Guid Id, byte[] Code)>();
        for (var r = 0; r < ids.Count; r++)
        {
            var deletesRow = false;
            foreach (var rule in rules.Where(rule => registered.Contains(rule.Id)))
            {
                var table = MatchCodeTableName(rule.Id);
                if ((rows.TryGetValue(ids[r], out var row) ? Code(rule, m, row.Values) : null) is { } code)
                {
                    writes.Add((table, ids[r], code));
                }
                else
                {
                    deletes.Append($"DELETE FROM meta.{d.Quote(table)} WHERE objectid = @_id{r};\n");
                    deletesRow = true;
                }
            }
            if (deletesRow) deleted.Add(new DetachedParameter($"@_id{r}", DbType.Guid, ids[r]));
        }
        if (deletes.Length > 0)
        {
            await using var cmd = s.Command(deletes.ToString());
            cmd.AddParameters(deleted);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var (table, id, code) in writes)
        {
            await s.KeyLockAsync(table, id, ct);
            await using (var update = s.Command(
                             $"UPDATE meta.{d.Quote(table)}{d.LockHint(RowLock.UpdateSerializable)} SET matchcode = @_c WHERE objectid = @_id"))
            {
                update.AddParameter("@_c", DbType.Binary, code, MatchCode.Length);
                update.AddParameter("@_id", DbType.Guid, id);
                if (await update.ExecuteNonQueryAsync(ct) > 0) continue;
            }
            await using var insert = s.Command($"INSERT INTO meta.{d.Quote(table)} (objectid, matchcode) VALUES (@_id, @_c)");
            insert.AddParameter("@_id", DbType.Guid, id);
            insert.AddParameter("@_c", DbType.Binary, code, MatchCode.Length);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// The stored values of <paramref name="attributes"/> of the row <paramref name="id"/> of <paramref name="m"/>, read in
    /// <paramref name="s"/> as the match codes read them (so a code computed from them equals the row's stored code); null when the
    /// row does not exist.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>?> ReadValuesAsync(DataSession s, EntityMetadata m, Guid id,
        IEnumerable<string> attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(attributes);
        var rows = await ReadRowsAsync(s, m, attributes, null, null, [id], ct);
        return rows.Count == 0 ? null : rows[0].Values;
    }

    private static IEnumerable<string> MatchingAttributes(IEnumerable<PublishedRule> rules) =>
        rules.SelectMany(r => r.Conditions).Select(c => c.MatchingAttribute);

    /// <summary>
    /// Reads <paramref name="attributes"/> of the rows of <paramref name="m"/>: the rows <paramref name="ids"/>, or else a page of at
    /// most <paramref name="size"/> rows after the key <paramref name="after"/> (from the first when null).
    /// </summary>
    private async Task<List<Row>> ReadRowsAsync(DataSession s, EntityMetadata m, IEnumerable<string> attributes, Guid? after, int? size,
        IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var d = s.Dialect;
        var pk = SystemAttributes.PrimaryIdName(m);
        var columns = new List<string> { d.Quote(pk) };
        var reads = new List<(AttributeMetadata Attribute, int Ordinal, int? TypeOrdinal)>();
        foreach (var name in attributes.Distinct(StringComparer.Ordinal))
        {
            var a = Attribute(m, name);
            var physical = TypeMapping.ToColumns(a);
            if (physical.Count == 0)
                throw new InvalidOperationException($"The column '{m.LogicalName}.{name}' of a duplicate detection rule has no value of its own.");
            columns.Add(d.Quote(physical[0].Name));
            var ordinal = columns.Count - 1;
            int? typeOrdinal = null;
            if (TypeMapping.IsPolymorphicLookup(a))
            {
                columns.Add(d.Quote(a.LogicalName + TypeMapping.TypeColumnSuffix));
                typeOrdinal = columns.Count - 1;
            }
            reads.Add((a, ordinal, typeOrdinal));
        }

        var where = "";
        var parameters = new List<DbParameter>();
        if (ids is not null)
        {
            where = $"WHERE {d.Quote(pk)} IN ({string.Join(", ", ids.Select((_, i) => $"@_r{i}"))})";
            parameters.AddRange(ids.Select((id, i) => new DetachedParameter($"@_r{i}", DbType.Guid, id)));
        }
        else if (after is { } key)
        {
            where = $"WHERE {d.Quote(pk)} > @_after";
            parameters.Add(new DetachedParameter("@_after", DbType.Guid, key));
        }
        var top = ids is null ? $"({size ?? FillBatchSize})" : null;
        // The backfill reads with locks, not the RCSI snapshot: a writer whose transaction is still open (and whose rule cache
        // may predate the publish, so it writes no code) is waited for, and its committed value is coded. The shared locks are
        // held until the batch commits (REPEATABLEREAD): a writer that blanks or deletes a row read by the batch waits for its
        // inserts, so its upkeep removes the code they wrote instead of finding none and leaving a stale one.
        // PostgreSQL: FOR SHARE after the LIMIT (plan 2.7b, Task 4).
        var hint = ids is null ? d.LockHint(RowLock.RepeatableRead) : "";
        var clause = ids is null ? d.LockClause(RowLock.RepeatableRead) : "";
        await using var cmd = s.Command(
            d.Limit($"SELECT {string.Join(", ", columns)} FROM {s.DataTable(m.LogicalName)}{hint} {where} ORDER BY {d.Quote(pk)}", top) + clause);
        cmd.AddParameters(parameters);
        var rows = new List<Row>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (a, ordinal, typeOrdinal) in reads)
                values[a.LogicalName] = rd.IsDBNull(ordinal) ? null : EntityRowMapper.ReadValue(rd, ordinal, a, typeOrdinal, store.GetEntityByObjectTypeCode);
            rows.Add(new Row(rd.GetGuid(0), values));
        }
        return rows;
    }

    /// <summary>The rows the publish job reads and codes per transaction (ruling D10).</summary>
    public const int FillBatchSize = 5000;
}
