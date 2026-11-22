using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;

namespace Xrmd.Query.Sql;

/// <summary>
/// Compiles a <see cref="QueryModel"/> into parameterized SQL over the <c>data.*</c> tables, in the SQL of <paramref name="dialect"/>
/// (plan 2.7a: every engine-specific fragment comes from it).
/// Table aliases are <c>t0</c> (root), <c>t1..tn</c> (link-entities, depth-first in declaration order); column aliases
/// are <c>c0..cn</c>. Table and column names come from metadata only; caller-supplied names (link aliases, column
/// aliases) never reach the SQL text, and every value is a parameter. A calculated column (plan 2.12a) is computed where the query
/// reads it (<see cref="ComputedSql"/>) from the definitions <paramref name="computed"/> translates; without it, calculated columns
/// cannot be read.
/// </summary>
public sealed class SqlQueryCompiler(IMetadataStore store, ISqlDialect dialect, IComputedColumns? computed = null)
{
    /// <summary>The most distinct calculated columns one query may read (plan 2.12a, Global Constraints).</summary>
    public const int MaxCalculatedColumns = 50;

    /// <summary>Maximum rows per page / <c>top</c>.</summary>
    public const int MaxPageSize = 5000;

    /// <summary>Maximum base records an aggregate query may read (<c>AggregateQueryRecordLimitExceeded</c> above).</summary>
    public const int AggregateRecordLimit = 50000;

    private const string TopParameter = "@top";
    private const string OffsetParameter = "@offset";
    private const string FetchParameter = "@fetch";
    private const string KeysetParameter = "@last";

    private static readonly IReadOnlySet<string> NoColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="XrmdValidationException">Unknown entity/attribute/alias, invalid value, limit exceeded or a
    /// feature not supported yet; the message names the offending item.</exception>
    public CompiledQuery Compile(QueryModel query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ValidateLimits(query);
        if (query.Aggregate && query.ReturnTotalRecordCount)
            throw new XrmdValidationException("The total record count is not supported for aggregate queries.");

        return new Compilation(store, dialect, computed, query, context).Run();
    }

    private static void ValidateLimits(QueryModel query)
    {
        if (query.Top is <= 0 or > MaxPageSize)
            throw new XrmdValidationException($"'top' must be between 1 and {MaxPageSize}, got {query.Top}.");
        if (query.PageSize is <= 0 or > MaxPageSize)
            throw new XrmdValidationException($"The page size must be between 1 and {MaxPageSize}, got {query.PageSize}.");
        if (query.PageNumber < 1)
            throw new XrmdValidationException($"The page number must be 1 or greater, got {query.PageNumber}.");
    }

    /// <summary>An entity of the query: the root or a link-entity.</summary>
    /// <param name="SqlAlias"><c>t0..tn</c>.</param>
    /// <param name="Alias">The caller's (or generated) link alias; null for the root.</param>
    /// <param name="Unreadable">The secured columns of the entity the caller cannot read (<see cref="IQuerySecurity.UnreadableColumns"/>).</param>
    private sealed record Scope(string SqlAlias, string? Alias, EntityMetadata Metadata,
        IReadOnlyDictionary<string, AttributeMetadata> Attributes, EntityNode Node, Scope? Parent, IReadOnlySet<string> Unreadable)
    {
        public string EntityName => Metadata.LogicalName;
    }

    /// <summary>State of one compilation; each stage appends to the SQL clauses.</summary>
    private sealed class Compilation(IMetadataStore store, ISqlDialect dialect, IComputedColumns? computed, QueryModel query, QueryContext context)
    {
        /// <summary>The quoted identifier in the dialect's SQL.</summary>
        private string Q(string identifier) => dialect.Quote(identifier);

        /// <summary>The quoted data table of <paramref name="entity"/>.</summary>
        private string DataTable(string entity) => DdlGenerator.Table(entity, dialect);

        private readonly QueryParameters _parameters = new();
        private readonly List<Scope> _scopes = [];
        private readonly List<ResultColumn> _columns = [];
        private readonly List<string> _selectList = [];
        /// <summary>Select expression → its column alias (first occurrence).</summary>
        private readonly Dictionary<string, string> _selectAliases = new(StringComparer.Ordinal);
        /// <summary>Column alias (c0..) → its select expression.</summary>
        private readonly Dictionary<string, string> _aliasExpressions = new(StringComparer.Ordinal);
        private readonly List<string> _groupBy = [];
        /// <summary>Aliases (c0..) of the group-by columns, in declaration order.</summary>
        private readonly List<string> _groupByAliases = [];
        /// <summary>Lookups selected as values: they get a <see cref="ResultKind.LookupName"/> column (see <see cref="AddLookupNames"/>).</summary>
        private readonly List<(Scope Scope, AttributeMetadata Attribute, string ResultName)> _lookups = [];
        private ConditionCompiler? _conditions;
        /// <summary>The scopes of the parent subqueries of <see cref="ParentRecordsNode"/> (p0..pn).</summary>
        private int _parentScopes;

        private ConditionCompiler Conditions => _conditions ??= new ConditionCompiler(_parameters, context, dialect, store);
        private ComputedSql? _computedSql;
        /// <summary>
        /// The expression of each calculated column per scope (scope alias, column), written once: the projection, conditions, orders
        /// and groupings that read it share its text and its parameters.
        /// </summary>
        private readonly Dictionary<(string Scope, string Attribute), string> _calculatedSql = [];
        /// <summary>The distinct calculated columns (<c>table.column</c>) the query reads, at most <see cref="MaxCalculatedColumns"/>.</summary>
        private readonly HashSet<string> _calculated = new(StringComparer.Ordinal);

        private ComputedSql Computed => _computedSql ??= new ComputedSql(_parameters, store.GetEntity, Translator, dialect);

        /// <summary>The translator of calculated column definitions.</summary>
        /// <exception cref="XrmdValidationException">The server has none: calculated columns cannot be read.</exception>
        private IComputedColumns Translator =>
            computed ?? throw new XrmdValidationException("Calculated columns are not available on this server.");

        public CompiledQuery Run()
        {
            var root = ResolveScopes();
            if (query.Aggregate) BuildAggregateProjection();
            else BuildProjection();
            var fromParts = BuildFrom();
            // The read restriction is part of the filter, so that paging, the total count and the aggregate probe see it.
            var filter = And(CompileFilter(root.Node.Filter, root, joined: null), SecurityFilter(root, joined: null));
            AddLookupNames(); // after the filters, so that condition parameters keep their numbering
            // The CTEs of the tree operators head every statement (T-SQL forbids them in subqueries).
            var prelude = _conditions?.Prelude ?? "";
            var orderBy = query.Aggregate ? BuildAggregateOrderBy() : BuildOrderBy(root);

            // ---- paging: Top wins; otherwise keyset (cookie), OFFSET/FETCH (page > 1) or TOP (page size + 1).
            var pageSize = query.PageSize ?? MaxPageSize;
            var keysetLast = query.Top is null ? KeysetStart(root) : null;
            var useOffset = query.Top is null && keysetLast is null && query.PageNumber > 1;

            var where = filter;
            if (keysetLast is { } last)
            {
                var pk = ColumnSql(root, AttributeOf(root, SystemAttributes.PrimaryIdName(root.Metadata)));
                var keyset = $"{pk} > {_parameters.AddNamed(KeysetParameter, DbType.Guid, last)}";
                where = where is null ? keyset : where + " AND " + keyset;
            }
            // Written last: the calculated columns of the filters and orders may need applies of their rows (ComputedSql.Applies).
            var from = AssembleFrom(fromParts);

            // ---- total record count: window column, unless rows are filtered by the keyset or made distinct.
            string? countSql = null;
            if (query.Aggregate)
            {
                var probe = dialect.Limit($"SELECT 1 AS x\n{from}{Where(filter)}", (AggregateRecordLimit + 1).ToString(CultureInfo.InvariantCulture));
                countSql = $"{prelude}SELECT COUNT(*) FROM ({probe}) AS q";
            }
            else if (query.ReturnTotalRecordCount)
            {
                if (keysetLast is not null)
                    countSql = $"{prelude}SELECT COUNT(*)\n{from}{Where(filter)}";
                else if (query.Distinct)
                    countSql = $"{prelude}SELECT COUNT(*) FROM (SELECT DISTINCT {string.Join(", ", _selectList)}\n{from}{Where(filter)}) AS q";
                else
                    AddColumn("COUNT(*) OVER()", root, attribute: null, ResultKind.TotalCount, "totalrecordcount");
            }

            if (useOffset && orderBy.Count == 0) orderBy.Add("(SELECT NULL)");

            var sql = new StringBuilder("SELECT ");
            if (query.Distinct) sql.Append("DISTINCT ");
            string? limit = null;
            if (query.Top is { } top)
                limit = "(" + _parameters.AddNamed(TopParameter, DbType.Int32, top) + ")";
            else if (!useOffset)
                limit = "(" + _parameters.AddNamed(TopParameter, DbType.Int32, pageSize + 1) + ")";
            sql.Append(string.Join(", ", _selectList));
            sql.Append('\n').Append(from);
            sql.Append(Where(where));
            if (_groupBy.Count > 0) sql.Append("\nGROUP BY ").Append(string.Join(", ", _groupBy));
            if (orderBy.Count > 0) sql.Append("\nORDER BY ").Append(string.Join(", ", orderBy));
            if (useOffset)
            {
                var skip = (long)(query.PageNumber - 1) * pageSize;
                if (skip > int.MaxValue)
                    throw new XrmdValidationException($"The page number {query.PageNumber} is too large for page size {pageSize}.");
                var offset = _parameters.AddNamed(OffsetParameter, DbType.Int32, (int)skip);
                var fetch = _parameters.AddNamed(FetchParameter, DbType.Int32, pageSize + 1);
                sql.Append('\n').Append(dialect.Page($"({offset})", $"({fetch})"));
            }

            // SQL Server accepts 2100 parameters per request (error 8003 above).
            if (_parameters.All.Count > QueryLimits.MaxParameters)
                throw new XrmdValidationException(
                    $"The query needs {_parameters.All.Count} parameters, more than the {QueryLimits.MaxParameters} supported; use fewer conditions or an 'in' condition.");

            return new CompiledQuery(prelude + dialect.Limit(sql.ToString(), limit), _parameters.All, _columns, root.Metadata,
                PageSize: pageSize, PageNumber: query.PageNumber, UsesKeyset: keysetLast is not null, CountSql: countSql);
        }

        private static string Where(string? predicate) => predicate is null ? "" : "\nWHERE " + predicate;

        /// <summary>
        /// The <c>last</c> id of the paging cookie when keyset paging applies: not distinct/aggregate, page &gt; 1,
        /// ordered by nothing or by the root primary key ascending only, and a well-formed cookie for the previous page
        /// on the same primary key. Null otherwise (OFFSET fallback, never an error).
        /// </summary>
        private Guid? KeysetStart(Scope root)
        {
            if (query.Distinct || query.Aggregate || query.PageNumber <= 1) return null;
            // A 1:N link-entity repeats root rows, which the keyset on the root primary key would skip: N:1 links only.
            foreach (var scope in _scopes.Skip(1))
                if (((LinkNode)scope.Node).From != SystemAttributes.PrimaryIdName(scope.Metadata)) return null;
            var pk = SystemAttributes.PrimaryIdName(root.Metadata);
            foreach (var scope in _scopes)
                foreach (var order in scope.Node.Orders)
                    if (scope != root || order.Alias is not null || order.Attribute != pk || order.Descending) return null;
            var cookie = PagingCookie.TryParse(query.PagingCookie);
            return cookie is not null && cookie.Page == query.PageNumber - 1 && cookie.PrimaryKey == pk ? cookie.Last : null;
        }

        // ---- scopes -------------------------------------------------------------------------------------------------

        private Scope ResolveScopes()
        {
            var root = NewScope(query.Root, alias: null, parent: null);
            var explicitAliases = new HashSet<string>(StringComparer.Ordinal);
            CollectAliases(query.Root, explicitAliases, depth: 1);
            AddLinks(root, new LinkAliases(explicitAliases));
            return root;
        }

        /// <summary>Collects the explicit link aliases; also enforces <see cref="QueryLimits.MaxLinkDepth"/> before recursing.</summary>
        private static void CollectAliases(EntityNode node, HashSet<string> aliases, int depth)
        {
            foreach (var link in node.Links)
            {
                if (depth > QueryLimits.MaxLinkDepth)
                    throw new XrmdValidationException($"Link-entities are nested more than {QueryLimits.MaxLinkDepth} levels deep.");
                if (link.Alias is { } alias && !aliases.Add(alias))
                    throw new XrmdValidationException($"The link-entity alias '{alias}' is used more than once.");
                CollectAliases(link, aliases, depth + 1);
            }
        }

        /// <summary>Link-entity scopes, depth-first; the depth was checked by <see cref="CollectAliases"/>.</summary>
        private void AddLinks(Scope parent, LinkAliases aliases)
        {
            foreach (var link in parent.Node.Links)
            {
                var scope = NewScope(link, aliases.For(link.EntityName, link.Alias), parent);
                AddLinks(scope, aliases);
            }
        }

        private Scope NewScope(EntityNode node, string? alias, Scope? parent)
        {
            var m = store.GetEntity(node.EntityName)
                    ?? throw new XrmdValidationException($"The entity with a name = '{node.EntityName}' was not found in the MetadataCache.");
            var attrs = (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
            var scope = new Scope("t" + _scopes.Count.ToString(CultureInfo.InvariantCulture), alias, m, attrs, node, parent,
                context.Security?.UnreadableColumns(m) ?? NoColumns);
            _scopes.Add(scope);
            return scope;
        }

        /// <summary>
        /// The scope a condition applies to: the owner, or the link-entity with that alias. In a link-entity's join
        /// condition (<paramref name="joined"/> set) only that link-entity and the entities joined before it can be
        /// referenced: a later link-entity is not in scope of the <c>ON</c> clause yet.
        /// </summary>
        private Scope ScopeOf(string? alias, Scope owner, Scope? joined)
        {
            if (alias is null) return owner;
            var scope = _scopes.FirstOrDefault(s => s.Alias == alias)
                        ?? throw new XrmdValidationException($"No link-entity with alias '{alias}' found in the query.");
            if (joined is not null && _scopes.IndexOf(scope) > _scopes.IndexOf(joined))
                throw new XrmdValidationException(
                    $"A condition of link-entity '{joined.Alias}' references link-entity '{alias}', which is joined after it; move the condition to the main filter or to '{alias}'.");
            return scope;
        }

        /// <summary>
        /// A readable attribute of the scope's entity used other than in the projection (a condition, an order, an aggregate or
        /// a join): it must not be a secured column the caller cannot read (<see cref="DemandReadable"/>).
        /// </summary>
        private AttributeMetadata AttributeOf(Scope scope, string name)
        {
            var a = Selectable(scope, name);
            DemandReadable(scope, a);
            return a;
        }

        /// <summary>A readable attribute of the scope's entity, as the projection reads it (an unreadable secured column too).</summary>
        private AttributeMetadata Selectable(Scope scope, string name) =>
            scope.Attributes.TryGetValue(name, out var a) && IsReadable(scope, a)
                ? a
                : throw new XrmdValidationException($"'{scope.EntityName}' entity doesn't contain attribute with Name = '{name}'.");

        /// <summary>
        /// <see cref="TypeMapping.IsReadable"/>, where a calculated column (and the <c>_base</c> of a calculated money column) is
        /// readable only with a translator of definitions.
        /// </summary>
        private bool IsReadable(Scope scope, AttributeMetadata a) =>
            CalculatedOf(scope, a) is null ? TypeMapping.IsReadable(a) : computed is not null;

        /// <summary>
        /// The calculated column <paramref name="a"/> reads: itself when it is calculated, its money column when it is the <c>_base</c>
        /// of a calculated money column; null otherwise.
        /// </summary>
        private static AttributeMetadata? CalculatedOf(Scope scope, AttributeMetadata a)
        {
            if (ComputedColumnRules.IsComputed(a)) return a;
            if (a is MoneyAttributeMetadata { IsBaseCurrency: true }
                && a.LogicalName.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
                && scope.Attributes.TryGetValue(a.LogicalName[..^TypeMapping.BaseColumnSuffix.Length], out var money)
                && money is MoneyAttributeMetadata { SourceType: ComputedColumnRules.Calculated })
                return money;
            return null;
        }

        /// <summary>
        /// Ruling F3: the server cannot filter, sort, aggregate or join on a value it may not show, so a secured column the caller
        /// cannot read is refused there, before any SQL runs, even when a field share would grant it on some rows.
        /// </summary>
        /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">403 FieldReadDenied (0x8004F504).</exception>
        private void DemandReadable(Scope scope, AttributeMetadata a)
        {
            if (scope.Unreadable.Contains(a.LogicalName))
                throw Xrmd.Core.Errors.XrmdFaultException.FieldReadDenied(scope.EntityName, a.LogicalName, context.UserId);
        }

        /// <summary>
        /// The column expression of an attribute; <c>versionnumber</c> (rowversion) reads as bigint; an EntityName companion reads
        /// its lookup's type column.
        /// </summary>
        /// <exception cref="XrmdValidationException">The computed url of an image column, which has no column: it can be selected
        /// (<see cref="ProjectionSql"/>), not filtered, sorted, joined on or aggregated.</exception>
        private string ColumnSql(Scope scope, AttributeMetadata a)
        {
            if (FileColumns.IsUrlCompanion(a))
                throw new XrmdValidationException(
                    $"Attribute '{a.LogicalName}' of '{scope.EntityName}' is computed from the image id and timestamp: it can be selected but not used in a condition, an order, a join or an aggregate.");
            if (CalculatedOf(scope, a) is { } calculated) return CalculatedSql(scope, a, calculated);
            if (a is EntityNameAttributeMetadata entityName)
                return $"{scope.SqlAlias}.{Q(TypeMapping.EntityNameColumn(entityName))}"; // plan 2.11 A7: an object type code
            return a.LogicalName == TypeMapping.VersionNumber
                ? $"CAST({scope.SqlAlias}.{Q(a.LogicalName)} AS bigint)"
                : $"{scope.SqlAlias}.{Q(a.LogicalName)}";
        }

        /// <summary>
        /// The expression of a calculated column, or of the <c>_base</c> of a calculated money column (<paramref name="a"/>, reading
        /// <paramref name="calculated"/>), written once per scope (<see cref="_calculatedSql"/>). Its parent record is read with system
        /// rights (ruling K10): only the calculated column's own field security applies, as for any column (<see cref="DemandReadable"/>).
        /// </summary>
        /// <exception cref="XrmdValidationException">More than <see cref="MaxCalculatedColumns"/> calculated columns, or a definition
        /// that cannot be read.</exception>
        private string CalculatedSql(Scope scope, AttributeMetadata a, AttributeMetadata calculated)
        {
            if (_calculatedSql.TryGetValue((scope.SqlAlias, a.LogicalName), out var known)) return known;
            var name = $"{scope.EntityName}.{calculated.LogicalName}";
            if (_calculated.Add(name) && _calculated.Count > MaxCalculatedColumns)
                throw new XrmdValidationException(
                    $"A query cannot read more than {MaxCalculatedColumns} calculated columns: '{name}' is one more.");
            var definition = Translator.Translate(scope.Metadata, calculated, store.GetEntity);
            var sql = a == calculated
                ? Computed.Expression(scope.SqlAlias, scope.Metadata, calculated, definition, depth: 1)
                : Computed.BaseExpression(scope.SqlAlias, scope.Metadata, (MoneyAttributeMetadata)calculated, definition, depth: 1);
            _calculatedSql[(scope.SqlAlias, a.LogicalName)] = sql;
            return sql;
        }

        /// <summary>
        /// The selected expression of an attribute: its column, or for the url of an image column the download url computed from
        /// the image id and timestamp (<see cref="FileColumns.IsUrlCompanion"/>), NULL when the image id is.
        /// </summary>
        private string ProjectionSql(Scope scope, AttributeMetadata a)
        {
            if (!FileColumns.IsUrlCompanion(a)) return ColumnSql(scope, a);
            var image = a.AttributeOf!;
            var id = $"{scope.SqlAlias}.{Q(image + FileColumns.ImageIdSuffix)}";
            var prefix = _parameters.Add(DbType.String,
                $"{FileColumns.DownloadPath}?Entity={scope.EntityName}&Attribute={image}&Id=");
            return $"CASE WHEN {id} IS NULL THEN NULL ELSE CONCAT({prefix}, LOWER({dialect.ToText(id, 36)}), {dialect.TextLiteral("&Timestamp=")}, " +
                   $"{dialect.ToText($"{scope.SqlAlias}.{Q(image + FileColumns.TimestampSuffix)}", 20)}) END";
        }

        // ---- projection ---------------------------------------------------------------------------------------------

        private void BuildProjection()
        {
            foreach (var scope in _scopes)
            {
                var unreadable = false;
                var selected = SelectedAttributes(scope);
                foreach (var (attribute, alias) in selected)
                {
                    var resultName = alias ?? (scope.Alias is null ? attribute.LogicalName : $"{scope.Alias}.{attribute.LogicalName}");
                    if (scope.Unreadable.Contains(attribute.LogicalName))
                    {
                        // Read for the rows a field share grants it on (the executor nulls it elsewhere, companions included);
                        // never in a distinct query, whose rows (and their order) the value would shape.
                        AddColumn(query.Distinct ? "NULL" : ProjectionSql(scope, attribute), scope, attribute, ResultKind.Value, resultName,
                            unreadable: true);
                        unreadable = true;
                        if (query.Distinct) continue;
                        if (scope.EntityName == AuditTable.Name && attribute.LogicalName == AuditTable.ChangeData)
                            AddAuditTarget(scope, resultName);
                    }
                    else
                        AddColumn(DistinctKey(ProjectionSql(scope, attribute), attribute), scope, attribute, ResultKind.Value, resultName);
                    if (TypeMapping.IsPolymorphicLookup(attribute))
                        AddColumn($"{scope.SqlAlias}.{Q(attribute.LogicalName + TypeMapping.TypeColumnSuffix)}",
                            scope, attribute, ResultKind.LookupType, resultName);
                    if (attribute is LookupAttributeMetadata) _lookups.Add((scope, attribute, resultName));
                }
                // The record a link-entity's field shares are looked up for (the root always reads its primary key).
                if (unreadable && scope.Parent is not null && !query.Distinct)
                {
                    var pk = Selectable(scope, SystemAttributes.PrimaryIdName(scope.Metadata));
                    AddColumn(ColumnSql(scope, pk), scope, pk, ResultKind.RecordId, $"{scope.Alias}.{pk.LogicalName}");
                }
                if (!query.Distinct) AddCurrency(scope, selected);
            }
            // A distinct query requesting no column at all still reads the primary key (as a non-distinct one does).
            if (_columns.Count == 0)
            {
                var root = _scopes[0];
                var pk = AttributeOf(root, SystemAttributes.PrimaryIdName(root.Metadata));
                AddColumn(ColumnSql(root, pk), root, pk, ResultKind.Value, pk.LogicalName);
            }
        }

        /// <summary>
        /// Ruling X11: the record currency of a scope that selects a money value in that currency (not only <c>{money}_base</c>
        /// values, which take the base currency), as a <see cref="ResultKind.Currency"/> column the executor formats the money with.
        /// Not in a distinct query, whose rows the column would change.
        /// </summary>
        private void AddCurrency(Scope scope, IEnumerable<(AttributeMetadata Attribute, string? Alias)> selected)
        {
            if (!selected.Any(s => s.Attribute is MoneyAttributeMetadata { IsBaseCurrency: not true })
                || !CurrencySchema.HasCurrencyColumns(scope.Metadata)
                || !scope.Attributes.TryGetValue(CurrencySchema.CurrencyId, out var currency))
                return;
            AddColumn(ColumnSql(scope, currency), scope, currency, ResultKind.Currency,
                scope.Alias is null ? currency.LogicalName : $"{scope.Alias}.{currency.LogicalName}");
        }

        /// <summary>
        /// The audited record (<c>objectid</c>) and its table (<c>objecttypecode</c>) of an audit row whose <c>changedata</c> the
        /// caller may read only in part: the executor removes from it the secured columns of that table the caller cannot read.
        /// </summary>
        private void AddAuditTarget(Scope scope, string resultName)
        {
            foreach (var name in new[] { AuditTable.ObjectId, AuditTable.ObjectTypeCodeColumn })
            {
                var a = Selectable(scope, name);
                AddColumn(ColumnSql(scope, a), scope, a, ResultKind.AuditTarget, resultName);
            }
        }

        /// <summary>
        /// A <see cref="ResultKind.LookupName"/> column per selected lookup (see <see cref="LookupNameExpression"/>); a lookup with
        /// no usable target gets no name column.
        /// </summary>
        private void AddLookupNames()
        {
            foreach (var (scope, attribute, resultName) in _lookups)
                if (LookupNameExpression(scope, attribute) is { } expression)
                    AddColumn(expression, scope, attribute, ResultKind.LookupName, resultName);
        }

        /// <summary>
        /// The primary name of the record a lookup points to, as Dataverse's denormalized name column: a scalar subquery on
        /// the target. Owner/Customer/multi-target: <c>CASE {lookup}type WHEN @otc THEN (subquery) … END</c> over the targets
        /// that have an object type code. Targets unknown to the metadata or without a readable primary name are skipped;
        /// null when none is left. Shared by the selected name (<see cref="AddLookupNames"/>), the like-family conditions
        /// (#107) and the order on a lookup (plan 3.5 UI-1a final review, I2), so that what is matched and sorted is what is shown.
        /// </summary>
        private string? LookupNameExpression(Scope scope, AttributeMetadata attribute)
        {
            var targets = ((LookupAttributeMetadata)attribute).Targets ?? [];
            var column = $"{scope.SqlAlias}.{Q(attribute.LogicalName)}";
            if (TypeMapping.IsPolymorphicLookup(attribute))
            {
                var branches = new StringBuilder();
                foreach (var target in targets)
                {
                    var m = store.GetEntity(target);
                    if (m?.ObjectTypeCode is not { } otc || NameSubquery(m, column) is not { } subquery) continue;
                    branches.Append(" WHEN ").Append(_parameters.Add(DbType.Int32, otc)).Append(" THEN ").Append(subquery);
                }
                return branches.Length == 0
                    ? null
                    : $"CASE {scope.SqlAlias}.{Q(attribute.LogicalName + TypeMapping.TypeColumnSuffix)}{branches} END";
            }
            var single = targets.Length > 0 ? store.GetEntity(targets[0]) : null;
            return single is null ? null : NameSubquery(single, column);
        }

        /// <summary><c>(SELECT n.[name] FROM [data].[target] AS n WHERE n.[pk] = {column})</c>, or null without a readable primary name.</summary>
        private string? NameSubquery(EntityMetadata target, string column)
        {
            var name = target.PrimaryNameAttribute;
            if (string.IsNullOrEmpty(name) || target.Attributes?.FirstOrDefault(a => a.LogicalName == name) is not { } a
                || !TypeMapping.IsReadable(a) || ComputedColumnRules.IsComputed(a))
                return null;
            return $"(SELECT n.{Q(a.LogicalName)} FROM {DataTable(target.LogicalName)} AS n " +
                   $"WHERE n.{Q(SystemAttributes.PrimaryIdName(target))} = {column})";
        }

        /// <summary>
        /// Root: primary key first, then the requested columns (all readable ones for AllColumns); a distinct query
        /// selects the primary key only when requested (explicitly or by AllColumns), so that it does not make every row
        /// distinct. Link-entities: only the requested columns.
        /// </summary>
        private IEnumerable<(AttributeMetadata Attribute, string? Alias)> SelectedAttributes(Scope scope)
        {
            var node = scope.Node;
            var pk = Selectable(scope, SystemAttributes.PrimaryIdName(scope.Metadata));
            var result = new List<(AttributeMetadata, string?)>();
            if ((scope.Parent is null && !query.Distinct) || node.AllColumns) result.Add((pk, null));
            if (node.AllColumns)
            {
                // Learn (image column data): all columns do not include image columns, for performance; their companions are included.
                result.AddRange((scope.Metadata.Attributes ?? [])
                    // Nor a column that is not valid for read (post.postregardingid, plan 3.5 UI-1g).
                    .Where(a => a != pk && IsReadable(scope, a) && a is not ImageAttributeMetadata && a.IsValidForRead != false)
                    .Select(a => (a, (string?)null)));
                return result;
            }

            foreach (var column in node.Columns)
            {
                if (column.Aggregate is not null || column.GroupBy || column.DateGrouping is not null || column.Distinct)
                    throw new XrmdValidationException($"Column '{column.Attribute}': aggregate attributes require an aggregate query.");
                var a = Selectable(scope, column.Attribute);
                if (!result.Contains((a, column.Alias))) result.Add((a, column.Alias));
            }
            return result;
        }

        /// <summary>
        /// The selected expression of a distinct query: for a text column a stored value per text key, so that values equal like
        /// SQL Server's <c>_CI_AS</c> make one row that shows a stored value (plan 2.7b, ruling Q1 and Task 2 fix round 1; the column
        /// itself on SQL Server).
        /// </summary>
        private string DistinctKey(string expression, AttributeMetadata attribute) =>
            query.Distinct && ConditionCompiler.IsText(attribute) ? dialect.TextKeyDistinctMember(expression) : expression;

        /// <summary>A text column's text key (plan 2.7b, ruling Q1: grouped and joined on it), any other column as it is.</summary>
        private string TextKeyOf(string expression, AttributeMetadata attribute) =>
            ConditionCompiler.IsText(attribute) ? dialect.TextKey(expression) : expression;

        private string AddColumn(string expression, Scope scope, AttributeMetadata? attribute, ResultKind kind, string resultName,
            bool unreadable = false)
        {
            var sqlAlias = "c" + _columns.Count.ToString(CultureInfo.InvariantCulture);
            _selectList.Add($"{expression} AS {sqlAlias}");
            _selectAliases.TryAdd(expression, sqlAlias);
            _aliasExpressions.Add(sqlAlias, expression);
            _columns.Add(new ResultColumn(sqlAlias, scope.Alias, scope.EntityName, attribute, kind, resultName, unreadable));
            return sqlAlias;
        }

        // ---- aggregate projection -----------------------------------------------------------------------------------

        /// <summary>
        /// Every column needs an alias and either an aggregate or group-by; both produce <see cref="ResultKind.Aggregate"/>
        /// columns named by the alias, with the attribute set. Group-by expressions also go to <c>GROUP BY</c>; a group-by lookup
        /// also gets its <see cref="ResultKind.LookupName"/> column (see <see cref="AddLookupNames"/>).
        /// </summary>
        private void BuildAggregateProjection()
        {
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in _scopes)
            {
                if (scope.Node.AllColumns)
                    throw new XrmdValidationException("An aggregate query cannot request all attributes.");
                foreach (var column in scope.Node.Columns)
                {
                    if (string.IsNullOrEmpty(column.Alias))
                        throw new XrmdValidationException($"Column '{column.Attribute}': an aggregate query requires an alias for every attribute.");
                    if (!aliases.Add(column.Alias))
                        throw new XrmdValidationException($"The alias '{column.Alias}' is used more than once.");
                    // count(*) reads no value of its attribute (ruling F3 refuses every other aggregate and groupby).
                    var a = column.Aggregate == XrmAggregate.Count && !column.GroupBy
                        ? Selectable(scope, column.Attribute)
                        : AttributeOf(scope, column.Attribute);
                    if (column.GroupBy) AddGroupBy(scope, a, column);
                    else if (column.Aggregate is { } aggregate)
                        AddColumn(AggregateSql(scope, a, column, aggregate), scope, a, ResultKind.Aggregate, column.Alias);
                    else
                        throw new XrmdValidationException($"Column '{column.Attribute}': an aggregate query requires an aggregate or groupby on every attribute.");
                }
            }
            if (_columns.Count == 0)
                throw new XrmdValidationException("An aggregate query requires at least one aggregate or groupby attribute.");
        }

        private void AddGroupBy(Scope scope, AttributeMetadata a, ColumnNode column)
        {
            if (column.Aggregate is not null || column.Distinct)
                throw new XrmdValidationException($"Column '{column.Attribute}': groupby cannot be combined with an aggregate or distinct.");
            var expression = ColumnSql(scope, a);
            if (column.DateGrouping is { } grouping)
            {
                if (a is not DateTimeAttributeMetadata)
                    throw new XrmdValidationException($"Column '{column.Attribute}': dategrouping requires a DateTime attribute.");
                expression = grouping switch
                {
                    DateGrouping.Day => $"CAST({expression} AS date)",
                    DateGrouping.Week => dialect.DatePart("week", expression),
                    DateGrouping.Month => dialect.DatePart("month", expression),
                    DateGrouping.Quarter => dialect.DatePart("quarter", expression),
                    DateGrouping.Year => dialect.DatePart("year", expression),
                    _ => throw new XrmdValidationException($"Column '{column.Attribute}': dategrouping {grouping} is not supported."),
                };
            }
            // Plan 2.7b, ruling Q1 and Task 2 fix round 1: a text column is grouped on its text key and shows a stored member.
            var shown = expression;
            if (column.DateGrouping is null && ConditionCompiler.IsText(a))
            {
                shown = dialect.TextKeyMember(expression);
                expression = dialect.TextKey(expression);
            }
            _groupBy.Add(expression);
            _groupByAliases.Add(AddColumn(shown, scope, a, ResultKind.Aggregate, column.Alias!));
            if (column.DateGrouping is null && TypeMapping.IsPolymorphicLookup(a))
            {
                var type = $"{scope.SqlAlias}.{Q(a.LogicalName + TypeMapping.TypeColumnSuffix)}";
                _groupBy.Add(type);
                AddColumn(type, scope, a, ResultKind.LookupType, column.Alias!);
            }
            // The target's primary name, for the formatted value of the group (plan 3.5 UI-1f): a scalar subquery on the grouped
            // id (and type), so that the rows stay grouped by id.
            if (column.DateGrouping is null && a is LookupAttributeMetadata) _lookups.Add((scope, a, column.Alias!));
        }

        private string AggregateSql(Scope scope, AttributeMetadata a, ColumnNode column, XrmAggregate aggregate)
        {
            if (column.DateGrouping is not null)
                throw new XrmdValidationException($"Column '{column.Attribute}': dategrouping requires groupby.");
            if (column.Distinct && aggregate != XrmAggregate.CountColumn)
                throw new XrmdValidationException($"Column '{column.Attribute}': distinct is supported only with countcolumn.");
            var expression = ColumnSql(scope, a);
            var numeric = a is IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata
                or DoubleAttributeMetadata or MoneyAttributeMetadata;
            var integer = a is IntegerAttributeMetadata or BigIntAttributeMetadata;
            if (aggregate is XrmAggregate.Sum or XrmAggregate.Avg && !numeric
                || aggregate is XrmAggregate.Min or XrmAggregate.Max && !numeric && a is not DateTimeAttributeMetadata)
                throw new XrmdValidationException($"Column '{column.Attribute}': aggregate {aggregate} is not supported on attributes of type {a.AttributeType}.");
            return aggregate switch
            {
                XrmAggregate.Count => "COUNT(*)",
                XrmAggregate.CountColumn => column.Distinct ? $"COUNT(DISTINCT {TextKeyOf(expression, a)})" : $"COUNT({expression})",
                // SUM of an int column overflows at the int range (error 8115): sum as bigint.
                // A bigint sum must overflow too (PostgreSQL's SUM(bigint) is a numeric): plan 2.7a, Task 7.
                XrmAggregate.Sum => a switch
                {
                    IntegerAttributeMetadata => dialect.SumBigint(expression),
                    BigIntAttributeMetadata => dialect.SumBigint(expression, bigint: true),
                    _ => $"SUM({expression})",
                },
                // AVG of an integer column would truncate to an integer.
                XrmAggregate.Avg => integer ? $"AVG(CAST({expression} AS decimal(38, 10)))" : $"AVG({expression})",
                XrmAggregate.Min => $"MIN({expression})",
                XrmAggregate.Max => $"MAX({expression})",
                _ => throw new XrmdValidationException($"Column '{column.Attribute}': aggregate {aggregate} is not supported."),
            };
        }

        // ---- from / joins -------------------------------------------------------------------------------------------

        /// <summary>A table of the <c>FROM</c> clause: the root (<paramref name="Join"/> null) or a link-entity with its join condition.</summary>
        private sealed record FromPart(Scope Scope, string? Join, string Table, string? On);

        /// <summary>The tables of the <c>FROM</c> clause; their text is written by <see cref="AssembleFrom"/>.</summary>
        private List<FromPart> BuildFrom()
        {
            var parts = new List<FromPart>();
            var root = _scopes[0];
            parts.Add(new FromPart(root, null, $"{DataTable(root.EntityName)} AS {root.SqlAlias}", null));
            foreach (var scope in _scopes.Skip(1))
            {
                var link = (LinkNode)scope.Node;
                var join = link.JoinType switch
                {
                    JoinOperator.Inner => "INNER JOIN",
                    JoinOperator.LeftOuter => "LEFT OUTER JOIN",
                    _ => throw new XrmdValidationException($"Link-entity join operator {link.JoinType} is not supported."),
                };
                var from = AttributeOf(scope, link.From);
                var to = AttributeOf(scope.Parent!, link.To);
                // Plan 2.7b, ruling Q1: a join of two text columns compares their text keys.
                var text = ConditionCompiler.IsText(from) && ConditionCompiler.IsText(to);
                var fromSql = ColumnSql(scope, from);
                var toSql = ColumnSql(scope.Parent!, to);
                var on = new StringBuilder(text ? $"{dialect.TextKey(fromSql)} = {dialect.TextKey(toSql)}" : $"{fromSql} = {toSql}");
                if (link.ToTypeCode is not null || link.ToTypeAttribute is not null)
                {
                    if (!TypeMapping.IsPolymorphicLookup(to))
                        throw new XrmdValidationException($"'{scope.Parent!.EntityName}.{to.LogicalName}' has no type column to join on.");
                    var parentType = $"{scope.Parent!.SqlAlias}.{Q(to.LogicalName + TypeMapping.TypeColumnSuffix)}";
                    on.Append(" AND ").Append(parentType).Append(" = ").Append(link.ToTypeCode is { } code
                        ? _parameters.Add(DbType.Int32, code)
                        : ColumnSql(scope, AttributeOf(scope, link.ToTypeAttribute!)));
                }
                if (CompileFilter(link.Filter, scope, joined: scope) is { } filter) on.Append(" AND ").Append(filter);
                if (SecurityFilter(scope, joined: scope) is { } security) on.Append(" AND ").Append(security);
                parts.Add(new FromPart(scope, join, $"{DataTable(scope.EntityName)} AS {scope.SqlAlias}", on.ToString()));
            }
            return parts;
        }

        /// <summary>
        /// The <c>FROM</c> clause. The applies of a scope's calculated columns (<see cref="ComputedSql.Applies"/>) follow its table:
        /// after the root's, or inside parentheses with a link-entity's table, so that the link's <c>ON</c> clause can read them.
        /// </summary>
        private string AssembleFrom(List<FromPart> parts)
        {
            var sql = new StringBuilder();
            foreach (var part in parts)
            {
                var applies = _computedSql?.Applies(part.Scope.SqlAlias) ?? "";
                if (part.Join is null)
                    sql.Append("FROM ").Append(part.Table).Append(applies);
                else
                    sql.Append('\n').Append(part.Join).Append(' ')
                        .Append(applies.Length == 0 ? part.Table : $"({part.Table}{applies})").Append(" ON ").Append(part.On);
            }
            return sql.ToString();
        }

        // ---- filters ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The filter as a parenthesized predicate, or null when it has no conditions. <paramref name="joined"/>: the
        /// link-entity whose join condition this is (null for the <c>WHERE</c> clause), see <see cref="ScopeOf"/>.
        /// </summary>
        private string? CompileFilter(FilterNode filter, Scope owner, Scope? joined, int depth = 1)
        {
            if (depth > QueryLimits.MaxFilterDepth)
                throw new XrmdValidationException($"Filters are nested more than {QueryLimits.MaxFilterDepth} levels deep.");
            var parts = new List<string>();
            foreach (var condition in filter.Conditions)
            {
                var scope = ScopeOf(condition.EntityAlias, owner, joined);
                var a = AttributeOf(scope, condition.Attribute);
                parts.Add(Conditions.Compile(new ConditionTarget(ColumnSql(scope, a), a, scope.EntityName)
                {
                    LookupName = a is LookupAttributeMetadata { AttributeType: not AttributeTypeCode.PartyList }
                        ? () => LookupNameExpression(scope, a)
                        : null,
                }, condition));
            }
            foreach (var child in filter.Filters)
                if (CompileFilter(child, owner, joined, depth + 1) is { } nested) parts.Add(nested);
            foreach (var shared in filter.Shared)
                parts.Add(Conditions.SharedRecords(ColumnSql(owner, AttributeOf(owner, SystemAttributes.PrimaryIdName(owner.Metadata))), shared,
                    shared.TypeAttribute is { } typeAttribute ? ColumnSql(owner, AttributeOf(owner, typeAttribute)) : null));
            foreach (var parents in filter.Parents)
                parts.Add(ParentRecords(owner, parents, depth));
            foreach (var owned in filter.Owned)
                parts.Add(Conditions.OwnedRecords(ColumnSql(owner, AttributeOf(owner, "ownerid")), owned));
            foreach (var path in filter.JsonPaths)
            {
                var a = AttributeOf(owner, path.Attribute);
                if (!Xrmd.Query.Cosmos.JsonPaths.IsJson(a))
                    throw new XrmdValidationException($"Attribute '{a.LogicalName}' does not hold JSON.");
                parts.Add(JsonPathConditions.Compile(ColumnSql(owner, a), path, _parameters, dialect));
            }
            foreach (var versions in filter.Versions)
                parts.Add(Conditions.VersionRange($"{owner.SqlAlias}.{Q(AttributeOf(owner, TypeMapping.VersionNumber).LogicalName)}", versions));

            if (parts.Count == 0) return null;
            var op = filter.Operator == LogicalOperator.Or ? " OR " : " AND ";
            return "(" + string.Join(op, parts) + ")";
        }

        /// <summary>
        /// <see cref="ParentRecordsNode"/>: per target, <c>({lookup}type = @otc AND {lookup} IN (SELECT pk FROM target AS pN WHERE
        /// restriction))</c> (no restriction: the type condition alone; a target with a type attribute instead of a code: the
        /// parent row's attribute equals the child's type column inside the subquery),
        /// ORed; no target: <c>(1 = 0)</c>. Each restriction is compiled in a scope of its own (<c>p0..pn</c>), outside the query's
        /// scopes, so its conditions never reach them.
        /// </summary>
        private string ParentRecords(Scope owner, ParentRecordsNode node, int depth)
        {
            var lookup = AttributeOf(owner, node.LookupAttribute);
            // A lookup with one target and no type column (postcomment.postid, plan 3.5 UI-1g): its value can only name that table.
            var polymorphic = TypeMapping.IsPolymorphicLookup(lookup);
            if (!polymorphic && !(lookup is LookupAttributeMetadata { Targets: [var only] }
                                  && node.Targets.All(t => t.EntityName == only && t.TypeAttribute is null)))
                throw new XrmdValidationException($"'{owner.EntityName}.{node.LookupAttribute}' is not a polymorphic lookup.");
            var parts = new List<string>();
            foreach (var target in node.Targets)
            {
                if (target.ObjectTypeCode is null && target.TypeAttribute is null)
                    throw new XrmdValidationException($"The parent target '{target.EntityName}' names neither a type code nor a type attribute.");
                var typeColumn = $"{owner.SqlAlias}.{Q(lookup.LogicalName + TypeMapping.TypeColumnSuffix)}";
                var conditions = new List<string>(3);
                if (polymorphic && target.ObjectTypeCode is { } otc) conditions.Add($"{typeColumn} = {_parameters.Add(DbType.Int32, otc)}");
                else if (!polymorphic) conditions.Add($"{ColumnSql(owner, lookup)} IS NOT NULL");
                if (target.RowFilter is not null)
                    conditions.Add(CompileFilter(target.RowFilter, owner, joined: null, depth + 1) ?? "(1 = 0)");
                if (target.ReadFilter is null && target.TypeAttribute is null)
                {
                    parts.Add($"({string.Join(" AND ", conditions)})");
                    continue;
                }
                var m = store.GetEntity(target.EntityName)
                        ?? throw new XrmdValidationException($"The entity with a name = '{target.EntityName}' was not found in the MetadataCache.");
                var scope = new Scope("p" + (_parentScopes++).ToString(CultureInfo.InvariantCulture), null, m,
                    (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal), new EntityNode { EntityName = m.LogicalName }, null,
                    NoColumns);
                var restriction = target.ReadFilter is null ? "(1 = 1)" : CompileFilter(target.ReadFilter, scope, joined: null, depth + 1) ?? "(1 = 0)";
                // The parent row's type attribute must name the child's type: never the id alone.
                if (target.TypeAttribute is { } typeAttribute)
                    restriction = $"({ColumnSql(scope, AttributeOf(scope, typeAttribute))} = {typeColumn} AND {restriction})";
                var pk = ColumnSql(scope, AttributeOf(scope, SystemAttributes.PrimaryIdName(m)));
                var applies = _computedSql?.Applies(scope.SqlAlias) ?? "";
                conditions.Add($"{ColumnSql(owner, lookup)} IN (SELECT {pk} FROM {DataTable(m.LogicalName)} AS {scope.SqlAlias}{applies} WHERE {restriction})");
                parts.Add($"({string.Join(" AND ", conditions)})");
            }
            return parts.Count == 0 ? "(1 = 0)" : "(" + string.Join(" OR ", parts) + ")";
        }

        /// <summary>
        /// The read restriction of the scope's entity (<see cref="IQuerySecurity.ReadFilter"/>), compiled in that scope;
        /// null without restriction. Fails closed: a filter that has nothing to compile denies every record (<c>1 = 0</c>);
        /// only a null <see cref="IQuerySecurity.ReadFilter"/> means no restriction.
        /// </summary>
        private string? SecurityFilter(Scope scope, Scope? joined) =>
            context.Security?.ReadFilter(scope.Metadata) is { } filter ? CompileFilter(filter, scope, joined) ?? "(1 = 0)" : null;

        private static string? And(string? left, string? right) =>
            left is null ? right : right is null ? left : $"({left} AND {right})";

        // ---- order --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Root orders, then link orders (declaration order), then a tiebreak: the root primary key, or for a distinct
        /// query (whose order columns must be selected) the remaining select columns when paging applies.
        /// </summary>
        private List<string> BuildOrderBy(Scope root)
        {
            var items = new List<string>();
            var ordered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in _scopes)
            {
                foreach (var order in scope.Node.Orders)
                {
                    if (order.Alias is not null)
                        throw new XrmdValidationException($"Ordering by the aggregate alias '{order.Alias}' requires an aggregate query.");
                    var attribute = AttributeOf(scope, order.Attribute);
                    var column = DistinctKey(ColumnSql(scope, attribute), attribute);
                    if (query.Distinct && !_selectAliases.ContainsKey(column))
                        throw new XrmdValidationException(
                            $"A distinct query cannot be ordered by '{order.Attribute}', which is not among the selected attributes.");
                    if (query.OrderByStoredValues) AddOrder(items, ordered, column, order.Descending);
                    else if (attribute is LookupAttributeMetadata { AttributeType: not AttributeTypeCode.PartyList })
                    {
                        // Learn ("Ordering lookup and choice columns"): a lookup is sorted by the related table's primary name.
                        if (query.Distinct)
                        {
                            var name = _columns.FirstOrDefault(c => c.Kind == ResultKind.LookupName && c.EntityAlias == scope.Alias
                                                                    && c.Attribute == attribute)
                                       ?? throw NoLookupName(order.Attribute);
                            AddOrder(items, ordered, _aliasExpressions[name.SqlAlias], order.Descending, name.SqlAlias);
                        }
                        else AddOrder(items, ordered, LookupNameExpression(scope, attribute) ?? throw NoLookupName(order.Attribute), order.Descending);
                    }
                    else if (!query.UseRawOrderBy && ChoiceLabelExpression(column, attribute) is { } label)
                    {
                        // A choice is sorted by its label (the formatted value), unless useraworderby is set.
                        if (query.Distinct)
                            throw new XrmdValidationException(
                                $"A distinct query cannot be ordered by the label of the choice column '{order.Attribute}'; set useraworderby='true' to order by its value.");
                        AddOrder(items, ordered, label, order.Descending);
                    }
                    else AddOrder(items, ordered, column, order.Descending);
                }
            }

            if (!query.Distinct)
                AddOrder(items, ordered, ColumnSql(root, AttributeOf(root, SystemAttributes.PrimaryIdName(root.Metadata))), false);
            else if (query.Top is null)
                foreach (var column in _columns.Where(c => !c.Unreadable)) // select-list order (a Dictionary has no defined order)
                    AddOrder(items, ordered, _aliasExpressions[column.SqlAlias], false, column.SqlAlias);
            return items;
        }

        private static XrmdValidationException NoLookupName(string attribute) => new(
            $"The lookup '{attribute}' cannot be ordered: Dataverse sorts a lookup by the related table's primary name, and none of its targets has one.");

        /// <summary>
        /// <c>CASE {column} WHEN 1 THEN @label1 … END</c> over the options of a choice (picklist, state, status) or a Yes/No
        /// column, with the label its formatted value shows in the caller's language (<see cref="FormattedValues.LabelText"/>, #109); null for any other column
        /// or a choice without a labelled option. An option missing from the metadata sorts as NULL, as its empty formatted value.
        /// The option values come from metadata and are integers, so they are written as literals; the labels are parameters.
        /// </summary>
        private string? ChoiceLabelExpression(string column, AttributeMetadata attribute)
        {
            var options = attribute switch
            {
                EntityNameAttributeMetadata or MultiSelectPicklistAttributeMetadata => null,
                EnumAttributeMetadata e => e.OptionSet?.Options?.Where(o => o.Value is not null).Select(o => (o.Value!.Value, o.Label)).ToList(),
                BooleanAttributeMetadata b => new[] { (1, b.OptionSet?.TrueOption?.Label), (0, b.OptionSet?.FalseOption?.Label) }
                    .Where(o => o.Item2 is not null).Select(o => (o.Item1, o.Item2!)).ToList(),
                _ => null,
            };
            var branches = new StringBuilder();
            foreach (var (value, label) in options ?? [])
            {
                if (FormattedValues.LabelText(label, context.LanguageCode) is not { } text) continue;
                branches.Append(" WHEN ").Append(value.ToString(CultureInfo.InvariantCulture))
                    .Append(" THEN ").Append(_parameters.Add(DbType.String, text));
            }
            return branches.Length == 0 ? null : $"CASE {column}{branches} END";
        }

        /// <summary>Orders by group-by expression or aggregate alias, then (when paging applies) the group-by columns.</summary>
        private List<string> BuildAggregateOrderBy()
        {
            var items = new List<string>();
            var ordered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in _scopes)
            {
                foreach (var order in scope.Node.Orders)
                {
                    if (order.Alias is { } alias)
                    {
                        var column = _columns.FirstOrDefault(c => c.Kind == ResultKind.Aggregate && c.ResultName == alias)
                                     ?? throw new XrmdValidationException($"No aggregate or groupby attribute with alias '{alias}' found in the query.");
                        AddOrder(items, ordered, _aliasExpressions[column.SqlAlias], order.Descending, column.SqlAlias);
                    }
                    else
                    {
                        var attribute = AttributeOf(scope, order.Attribute);
                        var expression = TextKeyOf(ColumnSql(scope, attribute), attribute);
                        if (!_groupBy.Contains(expression))
                            throw new XrmdValidationException(
                                $"An aggregate query can be ordered by '{order.Attribute}' only when it is a groupby attribute; order by its alias instead.");
                        AddOrder(items, ordered, expression, order.Descending);
                    }
                }
            }
            if (query.Top is null)
                foreach (var alias in _groupByAliases)
                    AddOrder(items, ordered, _aliasExpressions[alias], false, alias);
            return items;
        }

        /// <summary>
        /// Adds an order item once per expression. A selected expression is ordered by its column alias when the order
        /// came from the select list (aggregate alias, tiebreak), otherwise by the expression itself.
        /// </summary>
        private static void AddOrder(List<string> items, HashSet<string> ordered, string expression, bool descending, string? sqlAlias = null)
        {
            if (!ordered.Add(expression)) return;
            items.Add((sqlAlias ?? expression) + (descending ? " DESC" : " ASC"));
        }
    }
}
