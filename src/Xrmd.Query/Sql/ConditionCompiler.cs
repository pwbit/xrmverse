using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Query.Model;

namespace Xrmd.Query.Sql;

/// <summary>The parameters of one compilation, named <c>@p0..@pn</c> in order of creation.</summary>
internal sealed class QueryParameters
{
    private readonly List<DbParameter> _all = [];

    public IReadOnlyList<DbParameter> All => _all;

    /// <summary>Adds a typed parameter for an already coerced value and returns its name.</summary>
    public string Add(DbType type, object value) => AddNamed("@p" + _all.Count.ToString(CultureInfo.InvariantCulture), type, value);

    public string AddNamed(string name, DbType type, object value)
    {
        var p = new DetachedParameter(name, type, value);
        switch (type)
        {
            // Fixed sizes keep one cached plan per shape instead of one per value length.
            case DbType.String:
                p.Size = value is string { Length: > TypeMapping.MaxNvarcharLength } ? -1 : TypeMapping.MaxNvarcharLength;
                break;
            case DbType.Decimal:
                p.Precision = 38;
                p.Scale = 10;
                break;
        }
        _all.Add(p);
        return name;
    }
}

/// <summary>The column a condition applies to.</summary>
/// <param name="Column">SQL expression of the column (e.g. <c>t0.[pb_name]</c>), built from metadata only.</param>
internal sealed record ConditionTarget(string Column, AttributeMetadata Attribute, string EntityName)
{
    /// <summary>
    /// For a lookup (not a party list): builds the SQL expression of the related record's primary name, null when no target has
    /// one. Lazy, so that its type code parameters are added only when a like-family operator is compiled on the lookup.
    /// </summary>
    public Func<string?>? LookupName { get; init; }
}

/// <summary>
/// Compiles one condition into a T-SQL predicate. Dispatches per <see cref="ConditionOperator"/>; operators not handled
/// here are rejected with <see cref="XrmdValidationException"/>. Values are coerced to the column type
/// (<see cref="ValueCoercer"/>) and always passed as parameters.
/// </summary>
internal sealed class ConditionCompiler(QueryParameters parameters, QueryContext context, ISqlDialect dialect, IMetadataStore? store = null)
{
    /// <summary>The quoted identifier in the dialect's SQL (plan 2.7a).</summary>
    private string Q(string identifier) => dialect.Quote(identifier);

    /// <summary>The quoted data table of <paramref name="entity"/> (plan 2.7a).</summary>
    private string DataTable(string entity) => DdlGenerator.Table(entity, dialect);

    /// <summary>Above this many values, <c>In</c>/<c>NotIn</c> pass the values as one JSON parameter (OPENJSON).</summary>
    public const int MaxInlineInValues = 100;

    /// <summary>Caller context (user, business unit, current time) for context-dependent operators.</summary>
    public QueryContext Context { get; } = context;

    public string Compile(ConditionTarget t, ConditionNode c)
    {
        try
        {
            return CompileCore(t, EntityNameCodes(t, AuditObjectTypeCodes(t, c)));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new XrmdValidationException(
                $"Value of operator {c.Operator} on attribute '{t.Attribute.LogicalName}' is out of the supported date range.");
        }
    }

    /// <summary>
    /// <c>audit.objecttypecode</c> holds the logical name of the table, while Dataverse's samples filter it with the integer
    /// object type code (<c>value='2'</c>): an integer value is replaced by the logical name of the table with that code, and
    /// a code no table has is refused (400) instead of matching nothing.
    /// </summary>
    private ConditionNode AuditObjectTypeCodes(ConditionTarget t, ConditionNode c)
    {
        if (store is null || t.EntityName != AuditTable.Name || t.Attribute.LogicalName != AuditTable.ObjectTypeCodeColumn
            || c.Operator is not (ConditionOperator.Equal or ConditionOperator.NotEqual or ConditionOperator.In or ConditionOperator.NotIn))
            return c;
        var values = new List<object?>(c.Values.Count);
        foreach (var value in c.Values)
        {
            var text = value as string ?? (value is int or long or short ? Convert.ToString(value, CultureInfo.InvariantCulture) : null);
            if (text is not null && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                values.Add(store.GetEntityByObjectTypeCode(code)?.LogicalName
                           ?? throw new XrmdValidationException($"No table has the object type code {code} (attribute '{t.Attribute.LogicalName}')."));
            else values.Add(value);
        }
        return c with { Values = values };
    }

    /// <summary>
    /// An EntityName column (plan 2.11 A7: <c>activitytypecode</c>, <c>regardingobjecttypecode</c>, <c>objecttypecode</c>, …) holds
    /// an object type code, while its value is the table's logical name: a logical name is replaced by the code of that table, an
    /// integer is kept, and a name no table has is refused (400) instead of matching nothing. Only equality, <c>in</c> and the null
    /// operators apply to it; any other operator is refused (400).
    /// </summary>
    private ConditionNode EntityNameCodes(ConditionTarget t, ConditionNode c)
    {
        if (t.Attribute is not EntityNameAttributeMetadata) return c;
        if (c.Operator is ConditionOperator.Null or ConditionOperator.NotNull) return c;
        if (c.Operator is not (ConditionOperator.Equal or ConditionOperator.NotEqual or ConditionOperator.In or ConditionOperator.NotIn))
            throw new XrmdValidationException(
                $"Operator {c.Operator} cannot be used on attribute '{t.Attribute.LogicalName}', which names a table: use eq, ne, in, not-in, null or not-null.");
        if (store is null) return c;
        var values = new List<object?>(c.Values.Count);
        foreach (var value in c.Values)
        {
            if (value is string text && !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                values.Add(store.GetEntity(text)?.ObjectTypeCode
                           ?? throw new XrmdValidationException($"No table is named '{text}' (attribute '{t.Attribute.LogicalName}')."));
            else values.Add(value);
        }
        return c with { Values = values };
    }

    private string CompileCore(ConditionTarget t, ConditionNode c) => c.Operator switch
    {
        ConditionOperator.Equal => Comparison(t, c, "="),
        ConditionOperator.NotEqual => Comparison(t, c, "<>"),
        ConditionOperator.GreaterThan => Comparison(t, c, ">"),
        ConditionOperator.GreaterEqual => Comparison(t, c, ">="),
        ConditionOperator.LessThan => Comparison(t, c, "<"),
        ConditionOperator.LessEqual => Comparison(t, c, "<="),
        ConditionOperator.Null => NoValue(t, c, "IS NULL"),
        ConditionOperator.NotNull => NoValue(t, c, "IS NOT NULL"),
        ConditionOperator.Like => Like(t, c, "LIKE", v => v),
        ConditionOperator.NotLike => Like(t, c, "NOT LIKE", v => v),
        ConditionOperator.BeginsWith => Like(t, c, "LIKE", v => EscapeLike(v) + "%"),
        ConditionOperator.DoesNotBeginWith => Like(t, c, "NOT LIKE", v => EscapeLike(v) + "%"),
        ConditionOperator.EndsWith => Like(t, c, "LIKE", v => "%" + EscapeLike(v)),
        ConditionOperator.DoesNotEndWith => Like(t, c, "NOT LIKE", v => "%" + EscapeLike(v)),
        ConditionOperator.In => In(t, c, "IN"),
        ConditionOperator.NotIn => In(t, c, "NOT IN"),
        ConditionOperator.Between => Between(t, c, "BETWEEN"),
        ConditionOperator.NotBetween => Between(t, c, "NOT BETWEEN"),
        ConditionOperator.On => DateOn(t, c),
        ConditionOperator.OnOrAfter => DateRange(t, c, OnDate(t, c), null, false),
        ConditionOperator.OnOrBefore => DateRange(t, c, null, OnDate(t, c).AddDays(1), false),
        ConditionOperator.LastXDays => DateRange(t, c, Today.AddDays(-Count(t, c)), Context.UtcNow, true),
        ConditionOperator.NextXDays => DateRange(t, c, Context.UtcNow, Today.AddDays(Count(t, c)).AddDays(1), false),
        ConditionOperator.LastXMonths => DateRange(t, c, Context.UtcNow.AddMonths(-Count(t, c)), Context.UtcNow, true),
        ConditionOperator.ThisMonth => DateRange(t, c, MonthStart, MonthStart.AddMonths(1), false, noValue: true),
        ConditionOperator.ThisYear => DateRange(t, c, YearStart, YearStart.AddYears(1), false, noValue: true),
        ConditionOperator.EqualUserId => ContextId(t, c, "=", Context.UserId),
        ConditionOperator.NotEqualUserId => ContextId(t, c, "<>", Context.UserId),
        ConditionOperator.EqualBusinessId => ContextId(t, c, "=", Context.BusinessUnitId),
        ConditionOperator.EqualUserTeams => UserTeams(t, c, includeUser: false),
        ConditionOperator.EqualUserOrUserTeams => UserTeams(t, c, includeUser: true),
        ConditionOperator.EqualUserOrUserHierarchy => UserHierarchy(t, c, includeTeams: false),
        ConditionOperator.EqualUserOrUserHierarchyAndTeams => UserHierarchy(t, c, includeTeams: true),
        ConditionOperator.Under or ConditionOperator.UnderOrEqual or ConditionOperator.Above
            or ConditionOperator.AboveOrEqual => Hierarchy(t, c, "IN"),
        ConditionOperator.NotUnder => Hierarchy(t, c, "NOT IN"),
        _ => throw new XrmdValidationException($"Operator {c.Operator} is not supported (attribute '{c.Attribute}')."),
    };

    /// <summary>Escapes the LIKE wildcards <c>[</c>, <c>%</c>, <c>_</c> so that the value matches literally.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

    private DateTime Today => Context.UtcNow.Date;

    private DateTime MonthStart => new(Context.UtcNow.Year, Context.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private DateTime YearStart => new(Context.UtcNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void RequireDateTime(ConditionTarget t, ConditionNode c)
    {
        if (t.Attribute is not DateTimeAttributeMetadata)
            throw new XrmdValidationException(
                $"Operator {c.Operator} requires a DateTime attribute; '{t.Attribute.LogicalName}' is {t.Attribute.AttributeType}.");
    }

    /// <summary>The value's date (time truncated, UTC) for On/OnOrAfter/OnOrBefore.</summary>
    private static DateTime OnDate(ConditionTarget t, ConditionNode c)
    {
        RequireDateTime(t, c);
        return DateTime.SpecifyKind(((DateTime)Single(t, c)).Date, DateTimeKind.Utc);
    }

    /// <summary>The positive integer "x" of LastXDays/NextXDays/LastXMonths.</summary>
    private static int Count(ConditionTarget t, ConditionNode c)
    {
        RequireDateTime(t, c);
        if (c.Values.Count != 1)
            throw new XrmdValidationException(
                $"Operator {c.Operator} on attribute '{t.Attribute.LogicalName}' requires exactly one value, got {c.Values.Count}.");
        var n = c.Values[0] switch
        {
            int i => i,
            long l when l is > 0 and <= int.MaxValue => (int)l,
            string s when int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var r) => r,
            _ => 0,
        };
        if (n <= 0)
            throw new XrmdValidationException(
                $"Operator {c.Operator} on attribute '{t.Attribute.LogicalName}' requires a positive integer, got '{c.Values[0]}'.");
        return n;
    }

    private string DateOn(ConditionTarget t, ConditionNode c)
    {
        var d = OnDate(t, c);
        return DateRange(t, c, d, d.AddDays(1), false, alreadyValidated: true);
    }

    /// <summary>
    /// <c>col &gt;= from [AND col &lt; to]</c> (or <c>&lt;=</c> when <paramref name="inclusiveTo"/>), bounds as parameters.
    /// No-value operators accept and ignore stray values (FetchXML builders may send an empty one).
    /// </summary>
    private string DateRange(ConditionTarget t, ConditionNode c, DateTime? from, DateTime? to, bool inclusiveTo,
        bool noValue = false, bool alreadyValidated = false)
    {
        if (noValue || !alreadyValidated) RequireDateTime(t, c);
        var parts = new List<string>(2);
        if (from is { } f) parts.Add($"{t.Column} >= {parameters.Add(DbType.DateTime2, f)}");
        if (to is { } e) parts.Add($"{t.Column} {(inclusiveTo ? "<=" : "<")} {parameters.Add(DbType.DateTime2, e)}");
        return parts.Count == 1 ? parts[0] : $"({string.Join(" AND ", parts)})";
    }

    /// <summary>
    /// The records shared with the node's user or one of its teams (<c>principalobjectaccess</c>, direct or inherited
    /// rights) with any right of the mask and the scope's object type code (or the type column of
    /// <see cref="SharedRecordsNode.TypeAttribute"/>): <c>{primaryKey} IN (SELECT poa.[objectid] …)</c>.
    /// </summary>
    /// <param name="primaryKey">Primary key column of the scope the node belongs to.</param>
    /// <remarks>With <see cref="SharedRecordsNode.Principals"/>: shared with one of those principals exactly (one JSON parameter,
    /// so that the plan does not depend on their number); an empty set matches nothing.</remarks>
    /// <param name="typeColumn">The column expression of <see cref="SharedRecordsNode.TypeAttribute"/> in the scope (required when it
    /// is set): the share's object type code must equal it.</param>
    public string SharedRecords(string primaryKey, SharedRecordsNode node, string? typeColumn = null)
    {
        if (node.Principals is { Count: 0 }) return "1 = 0";
        if (node.TypeAttribute is not null && typeColumn is null)
            throw new ArgumentException($"The share node compares the type with '{node.TypeAttribute}': its column is required.", nameof(typeColumn));
        var type = $"poa.{Q("objecttypecode")} = {(node.TypeAttribute is not null ? typeColumn : parameters.Add(DbType.Int32, node.ObjectTypeCode))} AND ";
        var mask = parameters.Add(DbType.Int32, node.AccessMask);
        string principal;
        if (node.Principals is { } principals)
            principal = $"poa.{Q("principalid")} IN {GuidList(principals)}";
        else
        {
            var user = parameters.Add(DbType.Guid, node.UserId);
            principal = $"(poa.{Q("principalid")} = {user} OR poa.{Q("principalid")} IN {TeamsOf(user)})";
        }
        return $"{primaryKey} IN (SELECT poa.{Q("objectid")} FROM {DataTable("principalobjectaccess")} AS poa " +
               $"WHERE {type}" +
               $"(({dialect.IsNull($"poa.{Q("accessrightsmask")}", "0")} | {dialect.IsNull($"poa.{Q("inheritedaccessrightsmask")}", "0")}) & {mask}) <> 0 " +
               $"AND {principal})";
    }

    /// <summary>
    /// <see cref="OwnedRecordsNode"/>: <c>{ownerColumn} IN (SELECT … FROM OPENJSON(@p))</c>, one parameter whatever the number of
    /// principals; an empty set matches nothing.
    /// </summary>
    public string OwnedRecords(string ownerColumn, OwnedRecordsNode node) =>
        node.Principals.Count == 0 ? "1 = 0" : $"{ownerColumn} IN {GuidList(node.Principals)}";

    /// <summary>
    /// <see cref="QueueMemberNode"/>: <c>{queueKey} IN (SELECT m.[queueid] FROM queuemembership AS m WHERE m.[systemuserid] = @user)</c>.
    /// </summary>
    /// <param name="queueKey">Primary key column of the <c>queue</c> scope the node belongs to.</param>
    public string QueueMembers(string queueKey, QueueMemberNode node) =>
        $"{queueKey} IN (SELECT m.{Q(QueueSchema.QueueIdColumn)} FROM {DataTable(QueueSchema.QueueMembership)} AS m " +
        $"WHERE m.{Q("systemuserid")} = {parameters.Add(DbType.Guid, node.UserId)})";

    /// <summary>
    /// <see cref="VersionRangeNode"/>: <c>({column} &gt; CAST(@after AS binary(8)) AND {column} &lt; CAST(@before AS binary(8)))</c>
    /// on the raw rowversion column (plan 2.13). The bounds are bigint parameters cast to <c>binary(8)</c>, whose big-endian order
    /// is the order of the versions, so the predicate stays sargable on <c>IX_{table}_versionnumber</c>
    /// (<see cref="ISqlDialect.RowVersionValue"/>: the bigint itself on PostgreSQL, plan 2.7a Task 7).
    /// </summary>
    /// <param name="rowVersionColumn">The raw <c>versionnumber</c> column of the scope (not the bigint cast of the projection).</param>
    public string VersionRange(string rowVersionColumn, VersionRangeNode node)
    {
        var after = parameters.Add(DbType.Int64, node.AfterExclusive);
        var before = parameters.Add(DbType.Int64, node.BeforeExclusive);
        return $"({rowVersionColumn} > {dialect.RowVersionValue(after)} AND {rowVersionColumn} < {dialect.RowVersionValue(before)})";
    }

    /// <summary>The ids as one JSON parameter: <c>(SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p))</c>.</summary>
    private string GuidList(IEnumerable<Guid> ids)
    {
        var json = parameters.Add(DbType.String, ToJsonArray(ids.Cast<object>()));
        return GuidsOf(json);
    }

    /// <summary>
    /// EqualUserOrUserHierarchy/EqualUserOrUserHierarchyAndTeams (plan 2.5, ruling H7): the caller and its reports down to the
    /// hierarchy depth (and, with <paramref name="includeTeams"/>, the teams of all of them), from
    /// <see cref="IHierarchyResolver.ReportingHierarchy"/>; values are ignored.
    /// </summary>
    private string UserHierarchy(ConditionTarget t, ConditionNode c, bool includeTeams)
    {
        RequireId(t, c);
        var resolver = Context.Hierarchy
                       ?? throw new XrmdValidationException($"Operator {c.Operator} (attribute '{t.Attribute.LogicalName}') is not supported in this context.");
        var ids = resolver.ReportingHierarchy(includeTeams);
        return ids.Count == 0 ? "1 = 0" : $"{t.Column} IN {GuidList(ids)}";
    }

    /// <summary>The teams the user parameter is a member of, as a subquery.</summary>
    private string TeamsOf(string userParameter) =>
        $"(SELECT tm.{Q("teamid")} FROM {DataTable("teammembership")} AS tm WHERE tm.{Q("systemuserid")} = {userParameter})";

    private static void RequireId(ConditionTarget t, ConditionNode c)
    {
        if (t.Attribute is not (LookupAttributeMetadata or UniqueIdentifierAttributeMetadata))
            throw new XrmdValidationException(
                $"Operator {c.Operator} requires a lookup or uniqueidentifier attribute; '{t.Attribute.LogicalName}' is {t.Attribute.AttributeType}.");
    }

    /// <summary>EqualUserTeams/EqualUserOrUserTeams: the caller's teams (and the caller); values are ignored.</summary>
    private string UserTeams(ConditionTarget t, ConditionNode c, bool includeUser)
    {
        RequireId(t, c);
        var user = parameters.Add(DbType.Guid, Context.UserId);
        var teams = $"{t.Column} IN {TeamsOf(user)}";
        return includeUser ? $"({t.Column} = {user} OR {teams})" : teams;
    }

    /// <summary>
    /// The recursive CTEs of the tree operators compiled so far (<c>h0 (...) AS (...)</c>), in order. The query compiler hoists
    /// them to the head of every statement (<see cref="Prelude"/>), because T-SQL forbids a CTE inside a subquery.
    /// </summary>
    private readonly List<string> _ctes = [];

    /// <summary>The <c>WITH h0 ..., h1 ...</c> prefix of every statement that uses the compiled conditions; empty without tree operators.</summary>
    public string Prelude => _ctes.Count == 0 ? "" : dialect.RecursiveWith + string.Join(",\n", _ctes) + "\n";

    /// <summary>
    /// Under/UnderOrEqual/Above/AboveOrEqual (<c>IN</c>) and NotUnder (<c>NOT IN</c>, nulls included). The hierarchy is the
    /// attribute's entity for its primary key, otherwise the target of the lookup (<see cref="TreeEntity"/>). The business unit
    /// tree is expanded in memory (<see cref="IHierarchyResolver.Expand"/>, passed as one JSON parameter); any other table needs
    /// a hierarchical relationship and is walked by a recursive CTE (<see cref="TreeCte"/>), ignoring read security.
    /// </summary>
    private string Hierarchy(ConditionTarget t, ConditionNode c, string op)
    {
        RequireId(t, c);
        var entity = TreeEntity(t, c);
        var notIn = op == "NOT IN";
        if (entity == "businessunit")
        {
            var resolver = Context.Hierarchy
                           ?? throw new XrmdValidationException($"Operator {c.Operator} (attribute '{t.Attribute.LogicalName}') is not supported in this context.");
            var id = (Guid)Single(t, c);
            var ids = resolver.Expand(entity, id, c.Operator);
            if (ids.Count == 0) return notIn ? "1 = 1" : "1 = 0";
            var json = parameters.Add(DbType.String, ToJsonArray(ids.Cast<object>()));
            var predicate = $"{t.Column} {op} {GuidsOf(json)}";
            return notIn ? $"({predicate} OR {t.Column} IS NULL)" : predicate;
        }

        var relationship = store?.GetHierarchicalRelationship(entity);
        var metadata = store?.GetEntity(entity);
        if (relationship?.ReferencingAttribute is not { } parent || metadata is null)
            throw new XrmdValidationException(
                $"Operator {c.Operator} (attribute '{t.Attribute.LogicalName}') needs a table with a hierarchical relationship; '{entity}' has none.");
        var cte = TreeCte(entity, SystemAttributes.PrimaryIdName(metadata), parent, (Guid)Single(t, c), c.Operator);
        return c.Operator switch
        {
            ConditionOperator.Under or ConditionOperator.Above => $"{t.Column} IN (SELECT id FROM {cte} WHERE lvl > 0)",
            ConditionOperator.NotUnder => $"({t.Column} NOT IN (SELECT id FROM {cte}) OR {t.Column} IS NULL)",
            _ => $"{t.Column} IN (SELECT id FROM {cte})",
        };
    }

    /// <summary>
    /// The table whose tree a tree operator walks: the attribute's entity for its primary key, the single target of a lookup,
    /// and <c>systemuser</c> for <c>ownerid</c> (the user hierarchy is the one that applies to owners; a team id matches
    /// nothing). Any other multi-target lookup (Customer, other polymorphic lookups) is refused: the tree would depend on the
    /// table of the value, and Dataverse's behaviour there is not known (checklist, hierarchy security (u)).
    /// </summary>
    private static string TreeEntity(ConditionTarget t, ConditionNode c)
    {
        if (t.Attribute.IsPrimaryId == true) return t.EntityName;
        var targets = (t.Attribute as LookupAttributeMetadata)?.Targets;
        if (targets is not { Length: > 0 })
            throw new XrmdValidationException(
                $"Operator {c.Operator} requires a lookup or the primary key; '{t.Attribute.LogicalName}' is {t.Attribute.AttributeType}.");
        if (targets.Length == 1) return targets[0];
        if (t.Attribute.LogicalName == OwnerIdAttribute && targets.Contains(SystemUser)) return SystemUser;
        throw new XrmdValidationException(
            $"Operator {c.Operator} is not supported on the multi-target lookup '{t.Attribute.LogicalName}' " +
            $"(targets: {string.Join(", ", targets)}): use the primary key of one target table, or a link-entity to it.");
    }

    private const string OwnerIdAttribute = "ownerid";
    private const string SystemUser = "systemuser";

    /// <summary>
    /// Registers the recursive CTE of one tree condition and returns its name (<c>h0..hn</c>, per compilation). It starts at the
    /// referenced record (level 0) and walks down (<c>under</c>: children join on <c>[p] = h.id</c>) or up (<c>above</c>: the parent
    /// joins on <c>[k] = h.parent</c>), at most 100 levels (no error at the limit). The start record is never visited again,
    /// so a loop through it does not return it as its own descendant or ancestor. Names come from metadata, the id is a parameter.
    /// </summary>
    private string TreeCte(string entity, string key, string parent, Guid id, ConditionOperator op)
    {
        var name = "h" + _ctes.Count.ToString(CultureInfo.InvariantCulture);
        var table = $"{DataTable(entity)}";
        var start = parameters.Add(DbType.Guid, id);
        var up = op is ConditionOperator.Above or ConditionOperator.AboveOrEqual;
        _ctes.Add(up
            ? $"{name} (id, parent, lvl) AS (SELECT t.{Q(key)}, t.{Q(parent)}, 0 FROM {table} AS t WHERE t.{Q(key)} = {start} " +
              $"UNION ALL SELECT c.{Q(key)}, c.{Q(parent)}, h.lvl + 1 FROM {table} AS c JOIN {name} AS h ON c.{Q(key)} = h.parent " +
              $"WHERE h.lvl < {MaxTreeLevels} AND c.{Q(key)} <> {start})"
            : $"{name} (id, lvl) AS (SELECT t.{Q(key)}, 0 FROM {table} AS t WHERE t.{Q(key)} = {start} " +
              $"UNION ALL SELECT c.{Q(key)}, h.lvl + 1 FROM {table} AS c JOIN {name} AS h ON c.{Q(parent)} = h.id " +
              $"WHERE h.lvl < {MaxTreeLevels} AND c.{Q(key)} <> {start})");
        return name;
    }

    /// <summary>Recursion limit of the tree operators (Dataverse: 100 levels), a guard in the CTE and never an error.</summary>
    private const int MaxTreeLevels = 100;

    /// <summary>EqualUserId/NotEqualUserId/EqualBusinessId; extra values are ignored.</summary>
    private string ContextId(ConditionTarget t, ConditionNode c, string op, Guid id)
    {
        RequireId(t, c);
        var p = parameters.Add(DbType.Guid, id);
        return op == "=" ? $"{t.Column} = {p}" : $"({t.Column} <> {p} OR {t.Column} IS NULL)";
    }

    private string Comparison(ConditionTarget t, ConditionNode c, string op)
    {
        var value = Single(t, c);
        if (c.BinaryCollation && t.Attribute is StringAttributeMetadata or MemoAttributeMetadata)
        {
            var p = Parameter(t, value);
            if (op == "<>")
            {
                // A null column is undefined for the Cosmos translator and never matches (plan #78, ruling J4).
                return $"({t.Column} IS NOT NULL AND NOT ({dialect.BinaryEquals(t.Column, p)}))";
            }
            return op == "=" ? dialect.BinaryEquals(t.Column, p) : $"{dialect.BinaryCollate(t.Column)} {op} {p}";
        }
        // Plan 2.7b, ruling Q1 (#299): text equality compares the text keys (the values themselves on SQL Server).
        if (op is "=" or "<>" && IsText(t.Attribute))
            return $"{dialect.TextKey(t.Column)} {op} {dialect.TextKey(Parameter(t, value))}";
        return $"{t.Column} {op} {Parameter(t, value)}";
    }

    /// <summary>A text column (<c>citext</c> on PostgreSQL), compared through <see cref="ISqlDialect.TextKey"/> (plan 2.7b, ruling Q1).</summary>
    internal static bool IsText(AttributeMetadata a) => a is StringAttributeMetadata or MemoAttributeMetadata;

    private static string NoValue(ConditionTarget t, ConditionNode c, string predicate)
    {
        if (c.Values.Count != 0)
            throw new XrmdValidationException($"Operator {c.Operator} on attribute '{t.Attribute.LogicalName}' takes no value.");
        return $"{t.Column} {predicate}";
    }

    private string Like(ConditionTarget t, ConditionNode c, string op, Func<string, string> pattern)
    {
        if (t.Attribute is LookupAttributeMetadata lookup)
        {
            // Dataverse matches a like-family condition on a lookup against the related record's primary name (#107). A scalar
            // name expression keeps the NULL semantics of its name column: an empty lookup matches neither LIKE nor NOT LIKE.
            if (lookup.AttributeType == AttributeTypeCode.PartyList)
                throw new XrmdValidationException(
                    $"Operator {c.Operator} cannot be used on '{t.Attribute.LogicalName}', a party list, which has no column to match.");
            if (c.Values is not [string text])
                throw new XrmdValidationException(
                    $"Operator {c.Operator} on lookup '{t.Attribute.LogicalName}' requires exactly one text value.");
            var name = t.LookupName?.Invoke() ?? throw new XrmdValidationException(
                $"Operator {c.Operator} on lookup '{t.Attribute.LogicalName}' needs the related table's primary name; none of its targets has one.");
            return dialect.Like(name, pattern(text), op == "NOT LIKE", p => parameters.Add(DbType.String, p));
        }
        if (t.Attribute is not (StringAttributeMetadata or MemoAttributeMetadata))
            throw new XrmdValidationException(
                $"Operator {c.Operator} requires a string attribute; '{t.Attribute.LogicalName}' is {t.Attribute.AttributeType}.");
        var value = (string)Single(t, c);
        // The pattern is SQL Server's LIKE syntax (Dataverse's), matched as the dialect can (plan 2.7a, Task 7 fix round 1): a
        // range PostgreSQL cannot order as SQL Server is the 501 of ruling P15.
        return dialect.Like(t.Column, pattern(value), op == "NOT LIKE", p => parameters.Add(DbType.String, p));
    }

    private string In(ConditionTarget t, ConditionNode c, string op)
    {
        if (c.Values.Count == 0)
            throw new XrmdValidationException($"Operator {c.Operator} on attribute '{t.Attribute.LogicalName}' requires at least one value.");
        var values = c.Values.Select(v => ValueCoercer.Coerce(t.Attribute, v)).ToList();
        // Plan 2.7b, ruling Q1 (#299): a text column and its values compare as text keys (themselves on SQL Server).
        Func<string, string> key = IsText(t.Attribute) ? dialect.TextKey : e => e;
        if (values.Count <= MaxInlineInValues)
            return $"{key(t.Column)} {op} ({string.Join(", ", values.Select(v => key(Parameter(t, v))))})";

        var json = parameters.Add(DbType.String, ToJsonArray(values));
        return $"{key(t.Column)} {op} (SELECT {key("value")} FROM {dialect.JsonRecords(json, [JsonValueColumn(t.Attribute)])})";
    }

    private string Between(ConditionTarget t, ConditionNode c, string op)
    {
        if (c.Values.Count != 2)
            throw new XrmdValidationException(
                $"Operator {c.Operator} on attribute '{t.Attribute.LogicalName}' requires exactly two values, got {c.Values.Count}.");
        var from = Parameter(t, ValueCoercer.Coerce(t.Attribute, c.Values[0]));
        var to = Parameter(t, ValueCoercer.Coerce(t.Attribute, c.Values[1]));
        return $"{t.Column} {op} {from} AND {to}";
    }

    private static object Single(ConditionTarget t, ConditionNode c)
    {
        if (c.Values.Count != 1)
            throw new XrmdValidationException(
                $"Operator {c.Operator} on attribute '{t.Attribute.LogicalName}' requires exactly one value, got {c.Values.Count}.");
        return ValueCoercer.Coerce(t.Attribute, c.Values[0]);
    }

    private string Parameter(ConditionTarget t, object coerced) => parameters.Add(ValueCoercer.DbTypeOf(t.Attribute), coerced);

    /// <summary>
    /// The <c>value</c> column of the JSON values of <c>In</c> (ruling P8): the column type, widened where a narrower type would
    /// silently truncate or round the value (strings, decimals) and produce false matches.
    /// </summary>
    private static JsonColumn JsonValueColumn(AttributeMetadata a) => a switch
    {
        StringAttributeMetadata or MemoAttributeMetadata => new("value", SqlColumnKind.Text, Path: "$"),
        DecimalAttributeMetadata or MoneyAttributeMetadata => new("value", SqlColumnKind.Decimal, Path: "$", Precision: 38, Scale: 10),
        _ => new("value", ValueCoercer.DbTypeOf(a) switch
        {
            DbType.Int32 => SqlColumnKind.Int32,
            DbType.Int64 => SqlColumnKind.Int64,
            DbType.Double => SqlColumnKind.Double,
            DbType.Boolean => SqlColumnKind.Boolean,
            DbType.DateTime2 => SqlColumnKind.DateTime,
            DbType.Guid => SqlColumnKind.Guid,
            var other => throw new InvalidOperationException($"No JSON column type for {other}."),
        }, Path: "$"),
    };

    /// <summary>The ids of the JSON array parameter <paramref name="json"/> (ruling P8): <c>(SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p))</c>.</summary>
    private string GuidsOf(string json) =>
        $"(SELECT CAST({Q("value")} AS {dialect.ColumnType(SqlColumnKind.Guid)}) FROM {dialect.JsonArray(json)})";

    private static string ToJsonArray(IEnumerable<object> values)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var v in values)
            {
                switch (v)
                {
                    case string s: w.WriteStringValue(s); break;
                    case int i: w.WriteNumberValue(i); break;
                    case long l: w.WriteNumberValue(l); break;
                    case decimal d: w.WriteNumberValue(d); break;
                    case double d: w.WriteNumberValue(d); break;
                    case bool b: w.WriteBooleanValue(b); break;
                    case Guid g: w.WriteStringValue(g); break;
                    // ISO 8601 without offset: the values are UTC and datetime2 has no offset.
                    case DateTime dt: w.WriteStringValue(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)); break;
                    default: throw new InvalidOperationException($"Unexpected coerced value type {v.GetType()}.");
                }
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
