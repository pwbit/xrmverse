using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using Xrmd.Security;

namespace Xrmd.Core.Query;

/// <summary>
/// Runs a <see cref="QueryModel"/> (compiled by <see cref="SqlQueryCompiler"/>) and shapes the rows into an
/// <see cref="EntityCollection"/> as Dataverse does: root attributes as values (see <see cref="EntityRowMapper.ReadValue"/>),
/// link-entity and aliased attributes as <see cref="AliasedValue"/>s keyed by their result name, aggregate and groupby
/// columns as <see cref="AliasedValue"/>s keyed by their alias. Selected lookups carry the target's primary name
/// (<see cref="EntityReference.Name"/>) and values their <see cref="FormattedValues"/>, keyed like the value (the logical name
/// for root attributes, the result name such as <c>u.fullname</c> otherwise, the alias for aggregate and groupby columns).
/// The caller's read restriction (<see cref="ReadSecurity"/>) is compiled into the query unless the caller is unrestricted
/// (<see cref="SecurityCache.IsUnrestricted"/>); the business unit hierarchy operators are resolved for every caller, and so are
/// the user hierarchy operators (plan 2.5, ruling H7), whose ids are loaded only for a query that uses them.
/// Field security (plan 14, ruling F3): a secured column the caller cannot read is refused by the compiler in a condition, an
/// order, an aggregate or a join (403 0x8004F504); selected (by name or through all columns), it comes back as the key with a null
/// value (an <see cref="AliasedValue"/> with a null value in a link-entity), unless a field share grants it on that record
/// (<see cref="FieldSecurity.SharesAsync"/>, read once per page). A caller field security does not restrict (ruling F1) skips
/// all of it.
/// Money (plan 2.10, ruling X11) is formatted with the symbol and precision of its row's currency, read from the hidden
/// <see cref="ResultKind.Currency"/> column of its scope, and <c>{money}_base</c> with the base currency's, from one
/// <see cref="CurrencySnapshot"/> of the <see cref="ICurrencyCatalog"/> per query; distinct rows keep <c>N2</c> for money in the
/// record currency, and aggregates, which have no row currency, take the base currency's.
/// Scoped (it has no state; the compiler and the security caches are singletons).
/// </summary>
public sealed class QueryExecutor(SqlQueryCompiler compiler, IDbConnectionFactory factory, IMetadataStore store, TimeProvider clock,
    SecurityCache security, FieldSecurity fields, ICurrencyCatalog currencies, Xrmd.Files.FileColumnService? files = null)
{
    // Exact Dataverse text: client code falls back to paging when the message contains "AggregateQueryRecordLimit exceeded".
    /// <summary>The field security this executor reads with (plan 2.12b: <c>CalculateRollupField</c> masks its answer with it).</summary>
    internal FieldSecurity Fields => fields;

    public const string AggregateRecordLimitMessage = "AggregateQueryRecordLimit exceeded. Cannot perform this operation.";

    public const string ArithmeticOverflowMessage =
        "An arithmetic overflow occurred while computing the query results: a value or aggregate is outside the range of its type.";

    /// <summary>
    /// Executes the query for <paramref name="caller"/> (resolved beforehand; it feeds the user/business-unit and date
    /// operators). Without <c>top</c>, one page: <see cref="EntityCollection.MoreRecords"/> when a further row exists,
    /// and a paging cookie (page, first and last root id) when the page has records, the query is not an aggregate and the
    /// rows carry the root id (a distinct query selects it only on request; without it <see cref="Entity.Id"/> is empty).
    /// <see cref="EntityCollection.TotalRecordCount"/> is −1 unless requested, and capped by <see cref="TotalRecordCount"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an invalid query (the compiler's message) or an
    /// integer sum out of range or an arithmetic overflow in SQL (error 8115, e.g. a SUM beyond the column type);
    /// AggregateQueryRecordLimitExceeded (400) when an aggregate reads more than 50,000 records; PrivilegeDenied (403) when
    /// the caller has no read privilege on the root or a linked entity.</exception>
    public async Task<EntityCollection> ExecuteAsync(QueryModel query, Caller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(caller);
        await using var conn = await factory.OpenAsync(ct);
        return await ExecuteAsync(query, caller, new DataSession(conn, null), ct);
    }

    /// <summary>As <see cref="ExecuteAsync(QueryModel, Caller, CancellationToken)"/>, on the connection and transaction of <paramref name="session"/>.</summary>
    public async Task<EntityCollection> ExecuteAsync(QueryModel query, Caller caller, DataSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);
        Elastic.ElasticQueryRules.Demand(query, store);
        query = Elastic.ElasticQueryRules.WithDefaultPageSize(query, store);
        var activity = ActivityRead.Prepare(query, store);
        query = activity.Query;
        // Plan 2.11 A16: a note body is read from the file store for the notes the query returns (the file service is the
        // optional last dependency: without it, a query that reads a body fails rather than leaving it out).
        var notes = NoteBodyRead.Prepare(query, store);
        query = notes.Query;
        var context = await ContextAsync(caller, query, ct);
        context = context with { LanguageCode = await LanguageCodeAsync(caller, session, ct) };
        var compiled = Compile(query, context);
        try
        {
            var result = await RunAsync(query, caller, context, compiled, session, ct);
            await activity.CompleteAsync(result, store, session, context.LanguageCode, ct);
            await notes.CompleteAsync(result, files, session, ct);
            if (!query.Aggregate) await UserDateFormatRead.CompleteAsync(result, caller, store, session, ct);
            return result;
        }
        catch (DbException ex) when (session.Dialect.Classify(ex) == DbErrorKind.NumericOverflow)
        {
            throw XrmdFaultException.InvalidArgument(ArithmeticOverflowMessage);
        }
    }

    /// <summary>
    /// Ruling F3 outside a query: refuses, as the compiler refuses a condition on it, a lookup that filters on
    /// <paramref name="columns"/> (table and column) when one is a secured column <paramref name="caller"/> cannot read. Callers
    /// the query path does not restrict (internal, unrestricted, or bypassing field security, F1) pass. Used by
    /// <c>RetrieveDuplicates</c> (plan 2.15), whose rule conditions are filters on the matched rows.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 FieldReadDenied (0x8004F504).</exception>
    public async Task DemandFilterableAsync(Caller caller, IEnumerable<(string Entity, string Attribute)> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(columns);
        if (caller == Caller.Internal || security.IsUnrestricted(caller, await security.SystemUserIdAsync(ct))) return;
        foreach (var table in columns.GroupBy(c => c.Entity, StringComparer.OrdinalIgnoreCase))
        {
            var set = await fields.ForAsync(caller, table.Key, ct);
            if (ReferenceEquals(set, FieldPermissionSet.Unrestricted)) return; // every principal bypasses: on every table
            foreach (var (entity, attribute) in table)
                if (set.UnreadableSecured.Contains(attribute))
                    throw XrmdFaultException.FieldReadDenied(entity, attribute, caller.UserId);
        }
    }

    /// <summary>
    /// #109: the caller's UI language (<c>usersettings.uilanguageid</c>), in which choice labels are formatted and choice orders
    /// sort; <see cref="FormattedValues.English"/> for an internal caller, a caller without settings or an unset language.
    /// </summary>
    private async Task<int> LanguageCodeAsync(Caller caller, DataSession session, CancellationToken ct)
    {
        if (caller.UserId == Guid.Empty || store.GetEntity(CurrencySchema.UserSettings)?.Attributes?
                .Any(a => a.LogicalName == UiLanguageId) != true)
            return FormattedValues.English;
        await using var cmd = session.Command(
            $"SELECT {UiLanguageId} FROM {session.DataTable(CurrencySchema.UserSettings)} WHERE systemuserid = @xrmd_user");
        cmd.AddParameter("@xrmd_user", DbType.Guid, caller.UserId);
        return await cmd.ExecuteScalarAsync(ct) is int lcid && lcid > 0 ? lcid : FormattedValues.English;
    }

    private const string UiLanguageId = "uilanguageid";

    /// <summary>
    /// The compilation context of <paramref name="caller"/>: the hierarchy resolver always, the read restriction of the user
    /// (ANDed with the impersonator's when impersonating) unless the caller is unrestricted, with the secured columns it cannot
    /// read on each table of <paramref name="query"/>.
    /// </summary>
    private async Task<QueryContext> ContextAsync(Caller caller, QueryModel query, CancellationToken ct)
    {
        var tree = await security.GetBusinessUnitTreeAsync(ct);
        var context = new QueryContext(caller.UserId, caller.BusinessUnitId, clock.GetUtcNow().UtcDateTime);
        var userHierarchy = UsesUserHierarchy(query.Root) ? await UserHierarchyAsync(caller.UserId, ct) : null;
        if (caller == Caller.Internal || security.IsUnrestricted(caller, await security.SystemUserIdAsync(ct)))
            return context with { Hierarchy = new ReadSecurity([], tree, store, caller.UserId, userHierarchy: userHierarchy) };

        var restriction = new ReadSecurity(await security.PrincipalsAsync(caller, ct), tree, store, caller.UserId,
            await UnreadableColumnsAsync(caller, query.Root, ct), userHierarchy);
        return context with { Security = restriction, Hierarchy = restriction };
    }

    /// <summary>
    /// The ids of the user hierarchy operators for <paramref name="userId"/> (plan 2.5, ruling H7): the user and its reports
    /// down to the depth, with and without their teams; <see cref="UserHierarchyIds.Disabled"/> when the model is disabled.
    /// SYSTEM and internal callers have no reports: their hierarchy is themselves (and their teams).
    /// </summary>
    private async Task<UserHierarchyIds> UserHierarchyAsync(Guid userId, CancellationToken ct)
    {
        if (!(await security.GetHierarchySettingsAsync(ct)).Enabled) return UserHierarchyIds.Disabled;
        return new UserHierarchyIds(true, await security.ReportingHierarchyAsync(userId, includeTeams: false, ct),
            await security.ReportingHierarchyAsync(userId, includeTeams: true, ct));
    }

    /// <summary>True when a filter of the query (root or link-entity, nested at any level) uses a user hierarchy operator.</summary>
    private static bool UsesUserHierarchy(EntityNode node) =>
        UsesUserHierarchy(node.Filter) || node.Links.Any(UsesUserHierarchy);

    private static bool UsesUserHierarchy(FilterNode filter) =>
        filter.Conditions.Any(c => c.Operator is Microsoft.Xrm.Sdk.Query.ConditionOperator.EqualUserOrUserHierarchy
                                   or Microsoft.Xrm.Sdk.Query.ConditionOperator.EqualUserOrUserHierarchyAndTeams)
        || filter.Filters.Any(UsesUserHierarchy);

    /// <summary>
    /// The secured columns <paramref name="caller"/> cannot read on each table of the query (the root and its link-entities);
    /// null when field security does not restrict the caller (<see cref="FieldPermissionSet.Unrestricted"/>, ruling F1).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>?> UnreadableColumnsAsync(Caller caller, EntityNode root, CancellationToken ct)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(EntityNode node)
        {
            tables.Add(node.EntityName);
            foreach (var link in node.Links) Walk(link);
        }
        Walk(root);
        // A fileattachment row is hidden when its column is a secured one the caller cannot read (ReadSecurity): load the
        // tables its rows belong to.
        if (tables.Contains(FileColumns.AttachmentEntity)
            && store.GetEntity(FileColumns.AttachmentEntity)?.Attributes?.OfType<LookupAttributeMetadata>()
                .FirstOrDefault(FileColumns.IsAttachmentObjectId) is { Targets: { } targets })
            tables.UnionWith(targets);

        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var set = await fields.ForAsync(caller, table, ct);
            if (ReferenceEquals(set, FieldPermissionSet.Unrestricted)) return null; // every principal bypasses: on every table
            // F11: the change data of an audit row holds the values of the audited record's columns. It cannot be filtered,
            // sorted or aggregated on; selected, it comes back without the columns the caller cannot read (HideUnreadableAsync).
            result[table] = string.Equals(store.GetEntity(table)?.LogicalName, AuditTable.Name, StringComparison.Ordinal)
                ? new HashSet<string>(set.UnreadableSecured, StringComparer.OrdinalIgnoreCase) { AuditTable.ChangeData }
                : set.UnreadableSecured;
        }
        return result;
    }

    private async Task<EntityCollection> RunAsync(QueryModel query, Caller caller, QueryContext context, CompiledQuery compiled, DataSession conn,
        CancellationToken ct)
    {
        if (query.Aggregate && compiled.CountSql is { } probe
            && await ScalarAsync(conn, probe, compiled, ct) > SqlQueryCompiler.AggregateRecordLimit)
            throw new XrmdFaultException(ErrorCodes.AggregateQueryRecordLimitExceeded, AggregateRecordLimitMessage, 400);

        var paged = query.Top is null;
        var entities = new List<Entity>();
        var more = false;
        int? windowCount = null;
        // The currencies money is formatted with: read before the rows, as the session runs one command at a time.
        var snapshot = compiled.Columns.Any(c => c is { Kind: ResultKind.Value or ResultKind.Aggregate, Attribute: MoneyAttributeMetadata })
            ? await currencies.GetAsync(conn, ct)
            : null;
        RowShaper shaper;
        await using (var cmd = Command(conn, compiled.Sql, compiled))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            shaper = new RowShaper(compiled, rd, query.Aggregate ? AggregateColumns(query.Root) : null, store, snapshot, context.LanguageCode);
            while (await rd.ReadAsync(ct))
            {
                if (paged && entities.Count == compiled.PageSize)
                {
                    more = true; // the probe row (page size + 1): not returned
                    break;
                }
                windowCount ??= shaper.TotalCount();
                entities.Add(shaper.Shape());
            }
        }
        await shaper.HideUnreadableAsync(fields, caller, conn, readShares: !query.Distinct, ct);

        var result = new EntityCollection(entities)
        {
            EntityName = compiled.Root.LogicalName,
            MoreRecords = more,
            TotalRecordCount = -1,
            TotalRecordCountLimitExceeded = false,
        };
        // A distinct query that does not select the primary key has no ids (Entity.Id is Guid.Empty): no cookie.
        if (!query.Aggregate && paged && entities.Count > 0 && entities[0].Id != Guid.Empty)
            result.PagingCookie = new PagingCookie(compiled.PageNumber, SystemAttributes.PrimaryIdName(compiled.Root),
                entities[0].Id, entities[^1].Id).Format();
        if (query.ReturnTotalRecordCount)
        {
            var total = compiled.CountSql is { } countSql
                ? await ScalarAsync(conn, countSql, compiled, ct)
                : windowCount ?? await CountWithoutRowsAsync(conn, query, context, ct);
            (result.TotalRecordCount, result.TotalRecordCountLimitExceeded) = TotalRecordCount.Cap(total, ElasticTables.IsElastic(compiled.Root));
        }
        return result;
    }

    private CompiledQuery Compile(QueryModel query, QueryContext context)
    {
        try
        {
            return compiler.Compile(query, context);
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

    /// <summary>
    /// The total when the page came back empty, so the window count column had no row to ride on (e.g. a page past the
    /// last one): the same query with <c>top 1</c> carries the count of all matching records, or has no row when none match.
    /// </summary>
    private async Task<int> CountWithoutRowsAsync(DataSession conn, QueryModel query, QueryContext context, CancellationToken ct)
    {
        if (query.Top is not null || query.PageNumber == 1) return 0;
        var probe = Compile(new QueryModel
        {
            Root = query.Root,
            Top = 1,
            Distinct = query.Distinct,
            ReturnTotalRecordCount = true,
        }, context);
        if (probe.CountSql is { } countSql) return await ScalarAsync(conn, countSql, probe, ct);
        var total = probe.Columns.Single(c => c.Kind == ResultKind.TotalCount);
        await using var cmd = Command(conn, probe.Sql, probe);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? rd.GetInt32(rd.GetOrdinal(total.SqlAlias)) : 0;
    }

    private static async Task<int> ScalarAsync(DataSession conn, string sql, CompiledQuery compiled, CancellationToken ct)
    {
        await using var cmd = Command(conn, sql, compiled);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A command with copies of the compiled parameters (a parameter belongs to one command only).</summary>
    private static DbCommand Command(DataSession conn, string sql, CompiledQuery compiled)
    {
        var cmd = conn.Command(sql);
        cmd.AddParameters(compiled.Parameters);
        return cmd;
    }

    /// <summary>Aggregate query columns by alias (the compiler requires unique aliases).</summary>
    private static Dictionary<string, ColumnNode> AggregateColumns(EntityNode root)
    {
        var result = new Dictionary<string, ColumnNode>(StringComparer.Ordinal);
        void Walk(EntityNode node)
        {
            foreach (var column in node.Columns)
                if (column.Alias is { } alias) result.TryAdd(alias, column);
            foreach (var link in node.Links) Walk(link);
        }
        Walk(root);
        return result;
    }

    /// <summary>Turns the current row of the reader into an <see cref="Entity"/>; ordinals resolved once.</summary>
    private sealed class RowShaper
    {
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private readonly DbDataReader _rd;
        private readonly EntityMetadata _root;
        private readonly string _primaryKey;
        private readonly IMetadataStore _store;
        private readonly IReadOnlyDictionary<string, ColumnNode>? _aggregates;
        private readonly List<(ResultColumn Column, int Ordinal, int? TypeOrdinal, int? NameOrdinal)> _columns = [];
        private readonly int? _totalOrdinal;
        /// <summary>The ordinal of the record id of each link-entity (by alias) that selects an unreadable column.</summary>
        private readonly Dictionary<string, int> _recordIds = new(StringComparer.Ordinal);
        /// <summary>The currencies money is formatted with; null when no money (selected or aggregated) is in the query.</summary>
        private readonly CurrencySnapshot? _currencies;
        /// <summary>The ordinal of the hidden record currency of each scope (by alias, "" for the root) that selects money.</summary>
        private readonly Dictionary<string, int> _currencyIds = new(StringComparer.Ordinal);
        /// <summary>The ordinal of the root primary key.</summary>
        private int? _rootRecordId;
        /// <summary>The language of the choice labels (#109).</summary>
        private readonly int _languageCode;
        /// <summary>The unreadable secured columns of the rows shaped so far (see <see cref="HideUnreadableAsync"/>).</summary>
        private readonly List<(Entity Entity, string Key, string EntityName, string Attribute, Guid? RecordId, bool Aliased)> _unreadable = [];
        /// <summary>The ordinals of the audited record and table of each selected audit <c>changedata</c>, by its result name.</summary>
        private readonly Dictionary<string, (int Record, int Table)> _auditTargets = new(StringComparer.Ordinal);
        /// <summary>The audit <c>changedata</c> values of the rows shaped so far, with their audited table and record.</summary>
        private readonly List<(Entity Entity, string Key, bool Aliased, string? Table, Guid? Record)> _changeData = [];

        public RowShaper(CompiledQuery compiled, DbDataReader rd, IReadOnlyDictionary<string, ColumnNode>? aggregates, IMetadataStore store,
            CurrencySnapshot? currencies, int languageCode = FormattedValues.English)
        {
            _currencies = currencies;
            _languageCode = languageCode;
            _rd = rd;
            _root = compiled.Root;
            _primaryKey = SystemAttributes.PrimaryIdName(compiled.Root);
            _store = store;
            _aggregates = aggregates;
            var wanted = aggregates is null ? ResultKind.Value : ResultKind.Aggregate;
            foreach (var column in compiled.Columns)
            {
                if (column.Kind == ResultKind.TotalCount) _totalOrdinal = rd.GetOrdinal(column.SqlAlias);
                if (column.Kind == ResultKind.RecordId) _recordIds[column.EntityAlias ?? ""] = rd.GetOrdinal(column.SqlAlias);
                if (column.Kind == ResultKind.Currency) _currencyIds[column.EntityAlias ?? ""] = rd.GetOrdinal(column.SqlAlias);
                if (column.Kind == ResultKind.AuditTarget)
                {
                    var target = _auditTargets.TryGetValue(column.ResultName, out var known) ? known : (Record: -1, Table: -1);
                    _auditTargets[column.ResultName] = column.Attribute?.LogicalName == AuditTable.ObjectId
                        ? (rd.GetOrdinal(column.SqlAlias), target.Table)
                        : (target.Record, rd.GetOrdinal(column.SqlAlias));
                }
                if (aggregates is null && column is { Kind: ResultKind.Value, EntityAlias: null, Unreadable: false } && column.Attribute?.LogicalName == _primaryKey
                    && column.ResultName == _primaryKey)
                    _rootRecordId = rd.GetOrdinal(column.SqlAlias);
                if (column.Kind != wanted || column.Attribute is null) continue;
                // Owner/Customer lookups: the object type code column of the same result names the target entity;
                // the lookup name column of the same result holds the target's primary name.
                var type = Companion(compiled, column, ResultKind.LookupType);
                var name = Companion(compiled, column, ResultKind.LookupName);
                _columns.Add((column, rd.GetOrdinal(column.SqlAlias), type is null ? null : rd.GetOrdinal(type.SqlAlias),
                    name is null ? null : rd.GetOrdinal(name.SqlAlias)));
            }
        }

        private static ResultColumn? Companion(CompiledQuery compiled, ResultColumn column, ResultKind kind) =>
            compiled.Columns.FirstOrDefault(c => c.Kind == kind && c.EntityAlias == column.EntityAlias && c.ResultName == column.ResultName);

        public int? TotalCount() => _totalOrdinal is { } ordinal ? _rd.GetInt32(ordinal) : null;

        /// <summary>
        /// Ruling F3: each unreadable secured column of the shaped rows becomes its key with a null value (an
        /// <see cref="AliasedValue"/> with a null value when aliased) and loses its formatted value, except on the records a field
        /// share grants it on, read once per table for the whole page (<paramref name="readShares"/>: false for a distinct query,
        /// which did not read the values). Nothing to do, and no read, when no unreadable column was selected.
        /// </summary>
        public async Task HideUnreadableAsync(FieldSecurity fields, Caller caller, DataSession session, bool readShares, CancellationToken ct)
        {
            await HideAuditChangeDataAsync(fields, caller, session, readShares, ct);
            if (_unreadable.Count == 0) return;
            var grants = new Dictionary<string, IReadOnlyDictionary<Guid, FieldShareGrant>>(StringComparer.OrdinalIgnoreCase);
            if (readShares)
                foreach (var table in _unreadable.GroupBy(u => u.EntityName, StringComparer.OrdinalIgnoreCase))
                {
                    var ids = table.Where(u => u.RecordId is not null).Select(u => u.RecordId!.Value).Distinct().ToList();
                    var attributes = table.Select(u => u.Attribute).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    grants[table.Key] = await fields.SharesAsync(caller, table.Key, ids, attributes, session, ct);
                }
            foreach (var (entity, key, entityName, attribute, recordId, aliased) in _unreadable)
            {
                if (recordId is { } id && grants.TryGetValue(entityName, out var shares) && shares.TryGetValue(id, out var grant)
                    && grant.Read.Contains(attribute))
                    continue;
                entity[key] = aliased ? new AliasedValue(entityName, attribute, null) : null;
                entity.FormattedValues.Remove(key);
            }
        }

        /// <summary>
        /// Ruling F11: each selected audit <c>changedata</c> loses the values of the secured columns of its audited table the caller
        /// cannot read on its record (<see cref="AuditFieldSecurity"/>); null when its audited record is unknown or in a distinct query.
        /// </summary>
        private async Task HideAuditChangeDataAsync(FieldSecurity fields, Caller caller, DataSession session, bool readShares, CancellationToken ct)
        {
            if (_changeData.Count == 0) return;
            var known = readShares ? _changeData.Where(c => c.Table is not null && c.Record is not null).ToList() : [];
            var hidden = await AuditFieldSecurity.HiddenAsync(fields, caller, known.Select(c => (c.Table!, c.Record!.Value)), session, ct);
            foreach (var (entity, key, aliased, table, record) in _changeData)
            {
                if (!entity.Attributes.TryGetValue(key, out var value)) continue; // no change data on this row
                var json = (value as AliasedValue)?.Value as string ?? value as string;
                var filtered = json is not null && readShares && table is not null && record is { } id
                    ? AuditFieldSecurity.WithoutColumns(json, hidden(table, id))
                    : null;
                entity[key] = aliased ? new AliasedValue(AuditTable.Name, AuditTable.ChangeData, filtered) : filtered;
                entity.FormattedValues.Remove(key);
            }
        }

        public Entity Shape()
        {
            var entity = new Entity(_root.LogicalName);
            foreach (var (column, ordinal, typeOrdinal, nameOrdinal) in _columns)
            {
                if (column.Unreadable && column.EntityName == AuditTable.Name && column.Attribute!.LogicalName == AuditTable.ChangeData)
                {
                    var (record, table) = _auditTargets.GetValueOrDefault(column.ResultName, (-1, -1));
                    _changeData.Add((entity, column.ResultName, column.EntityAlias is not null || column.ResultName != column.Attribute.LogicalName,
                        table >= 0 && !_rd.IsDBNull(table) ? _rd.GetString(table) : null,
                        record >= 0 && !_rd.IsDBNull(record) ? _rd.GetGuid(record) : null));
                }
                else if (column.Unreadable)
                {
                    // Shaped as any value (a null one omitted); HideUnreadableAsync then nulls it unless a field share grants it.
                    var idOrdinal = column.EntityAlias is { } alias ? _recordIds.GetValueOrDefault(alias, -1) : _rootRecordId ?? -1;
                    Guid? recordId = idOrdinal >= 0 && !_rd.IsDBNull(idOrdinal) ? _rd.GetGuid(idOrdinal) : null;
                    _unreadable.Add((entity, column.ResultName, column.EntityName, column.Attribute!.LogicalName, recordId,
                        column.EntityAlias is not null || column.ResultName != column.Attribute.LogicalName));
                }
                if (_rd.IsDBNull(ordinal)) continue; // null values are omitted, as Dataverse does
                var a = column.Attribute!;
                if (_aggregates is not null)
                {
                    var node = _aggregates[column.ResultName];
                    var aggregate = AggregateValue(column, node, ordinal, typeOrdinal);
                    if (aggregate is EntityReference group && nameOrdinal is { } g && !_rd.IsDBNull(g)) group.Name = _rd.GetString(g);
                    entity[column.ResultName] = new AliasedValue(column.EntityName, a.LogicalName, aggregate);
                    if (AggregateFormatted(a, node, aggregate) is { } text) entity.FormattedValues[column.ResultName] = text;
                    continue;
                }

                var value = EntityRowMapper.ReadValue(_rd, ordinal, a, typeOrdinal, _store.GetEntityByObjectTypeCode);
                if (value is EntityReference reference && nameOrdinal is { } n && !_rd.IsDBNull(n)) reference.Name = _rd.GetString(n);
                if (column.EntityAlias is null && column.ResultName == a.LogicalName) entity[a.LogicalName] = value;
                else entity[column.ResultName] = new AliasedValue(column.EntityName, a.LogicalName, value);
                // Keyed like the value: the attribute name for root attributes, the result name ("u.fullname") otherwise.
                if (FormattedValues.Format(a, value, a is MoneyAttributeMetadata money ? FormatOf(column, money) : null, _store.GetEntity, _languageCode) is { } formatted)
                    entity.FormattedValues[column.ResultName] = formatted;
            }

            if (_aggregates is null)
            {
                if (entity.Attributes.TryGetValue(_primaryKey, out var id) && id is Guid guid) entity.Id = guid;
                if (entity.Attributes.TryGetValue(TypeMapping.VersionNumber, out var version) && version is long rowVersion)
                    entity.RowVersion = rowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return entity;
        }

        /// <summary>
        /// The format of the money <paramref name="a"/> of the current row (ruling X11): its scope's record currency, read from the
        /// hidden currency column (none in a distinct query: <c>N2</c>), or the base currency for a <c>{money}_base</c>.
        /// </summary>
        private MoneyFormat? FormatOf(ResultColumn column, MoneyAttributeMetadata a)
        {
            if (_currencies is null) return null;
            Guid? currency = _currencyIds.TryGetValue(column.EntityAlias ?? "", out var ordinal) && !_rd.IsDBNull(ordinal) ? _rd.GetGuid(ordinal) : null;
            return _currencies.FormatFor(a, currency);
        }

        /// <summary>
        /// The formatted value of an aggregate or groupby result (plan 3.5 UI-1f, task 4b), keyed by its alias as the value is:
        /// a group as any value of its attribute (<see cref="FormattedValues.Format"/>: the label of a choice or two-options, the
        /// primary name of a lookup), a day grouping as a date only (<c>M/d/yyyy</c>), the other date groupings as their invariant
        /// integer; count and countcolumn as a whole number, the average of a whole number column as a whole number (as Learn's
        /// example shows it), any other aggregate as a value of its attribute. Money has no row currency here: the base currency
        /// formats it (<c>N2</c> when the organization has none).
        /// </summary>
        private string? AggregateFormatted(AttributeMetadata a, ColumnNode node, object value)
        {
            if (node.GroupBy && node.DateGrouping is { } grouping)
                return grouping == DateGrouping.Day
                    ? value is DateTime day ? day.ToString("M/d/yyyy", EnUs) : null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            return (node.Aggregate, value) switch
            {
                (XrmAggregate.Count or XrmAggregate.CountColumn, int count) => count.ToString("N0", EnUs),
                (XrmAggregate.Avg, decimal average) when a is IntegerAttributeMetadata or BigIntAttributeMetadata =>
                    average.ToString("N0", EnUs),
                _ => FormattedValues.Format(a, value, a is MoneyAttributeMetadata money ? _currencies?.FormatFor(money, _currencies.BaseCurrencyId) : null,
                    _store.GetEntity, _languageCode),
            };
        }

        /// <summary>
        /// Dataverse types of aggregate results: counts → int, SUM of Integer → int (summed as bigint by SQL), AVG of
        /// Integer/BigInt → decimal, Money → Money; groupby columns as the attribute's own value (day grouping → UTC
        /// date, other date groupings → int).
        /// </summary>
        private object AggregateValue(ResultColumn column, ColumnNode node, int ordinal, int? typeOrdinal)
        {
            var a = column.Attribute!;
            if (node.GroupBy)
                return node.DateGrouping switch
                {
                    null => EntityRowMapper.ReadValue(_rd, ordinal, a, typeOrdinal, _store.GetEntityByObjectTypeCode),
                    DateGrouping.Day => DateTime.SpecifyKind(_rd.GetDateTime(ordinal), DateTimeKind.Utc),
                    _ => _rd.GetValue(ordinal),
                };
            switch (node.Aggregate)
            {
                case XrmAggregate.Count or XrmAggregate.CountColumn:
                    return _rd.GetInt32(ordinal);
                case XrmAggregate.Sum when a is IntegerAttributeMetadata:
                    var sum = _rd.GetInt64(ordinal);
                    return sum is >= int.MinValue and <= int.MaxValue
                        ? (int)sum
                        : throw XrmdFaultException.InvalidArgument(
                            $"The sum '{column.ResultName}' of attribute '{a.LogicalName}' is outside the range of an Integer.");
                case XrmAggregate.Avg when a is IntegerAttributeMetadata or BigIntAttributeMetadata:
                    return _rd.GetDecimal(ordinal);
                default:
                    return EntityRowMapper.ReadValue(_rd, ordinal, a, typeOrdinal, _store.GetEntityByObjectTypeCode);
            }
        }
    }
}
