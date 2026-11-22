using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Xrmd.Query.Model;

public class EntityNode
{
    public required string EntityName { get; set; }
    public bool AllColumns { get; set; }
    public List<ColumnNode> Columns { get; } = [];
    public FilterNode Filter { get; set; } = new();
    public List<LinkNode> Links { get; } = [];
    public List<OrderNode> Orders { get; } = [];
}

public sealed class LinkNode : EntityNode
{
    /// <summary>Attribute of the linked entity (FetchXML semantics).</summary>
    public required string From { get; set; }
    /// <summary>Attribute of the parent entity.</summary>
    public required string To { get; set; }
    public JoinOperator JoinType { get; set; } = JoinOperator.Inner;
    /// <summary>Null: assigned by the compiler ({name}1, {name}2...).</summary>
    public string? Alias { get; set; }
    /// <summary>
    /// When set, the join also requires the type column of the parent's polymorphic lookup <see cref="To"/> to hold this object
    /// type code, so a record of another table with the same id never joins (plan 2.11, A14: <c>$expand</c> of a per-target
    /// navigation property such as <c>partyid_contact</c>). Produced only by the Web API, never by FetchXML.
    /// </summary>
    public int? ToTypeCode { get; set; }
    /// <summary>
    /// As <see cref="ToTypeCode"/>, with the code read from this attribute of the joined row (<c>activityid_activitypointer</c>:
    /// the pointer row's <c>activitytypecode</c>).
    /// </summary>
    public string? ToTypeAttribute { get; set; }
    /// <summary>
    /// Web API <c>$expand</c> (issue #190): the navigation property this link expands, the JSON property of the nested record.
    /// Equal to <see cref="Alias"/> on a first-level expand; a nested expand has an alias of its own. Null elsewhere.
    /// </summary>
    public string? NavigationProperty { get; set; }
}

public sealed record ColumnNode(string Attribute, string? Alias = null, XrmAggregate? Aggregate = null,
    bool GroupBy = false, bool Distinct = false, DateGrouping? DateGrouping = null);

public enum XrmAggregate { Count, CountColumn, Sum, Avg, Min, Max }

public enum DateGrouping { Day, Week, Month, Quarter, Year }

public sealed class FilterNode
{
    public LogicalOperator Operator { get; set; } = LogicalOperator.And;
    public List<ConditionNode> Conditions { get; } = [];
    public List<FilterNode> Filters { get; } = [];
    /// <summary>Records shared with a user; combined with the conditions by <see cref="Operator"/>.</summary>
    public List<SharedRecordsNode> Shared { get; } = [];
    /// <summary>Records whose parent the caller can read; combined with the conditions by <see cref="Operator"/>.</summary>
    public List<ParentRecordsNode> Parents { get; } = [];
    /// <summary>Records owned by one of a set of principals; combined with the conditions by <see cref="Operator"/>.</summary>
    public List<OwnedRecordsNode> Owned { get; } = [];
    /// <summary>Queues a user is a member of; combined with the conditions by <see cref="Operator"/>.</summary>
    public List<QueueMemberNode> QueueMembers { get; } = [];
    /// <summary>Records whose <c>versionnumber</c> lies in a range; combined with the conditions by <see cref="Operator"/>.</summary>
    public List<VersionRangeNode> Versions { get; } = [];
    /// <summary>Conditions on a value inside a JSON column; combined with the conditions by <see cref="Operator"/>.</summary>
    public List<JsonPathConditionNode> JsonPaths { get; } = [];
}

/// <summary>The test of a <see cref="JsonPathConditionNode"/>; comparisons are type-strict (a string never equals a number).</summary>
public enum JsonPathTest { Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, Defined, NotDefined }

/// <summary>
/// A condition on the property <paramref name="Key"/> of the JSON object (or the element of the JSON array) that
/// <paramref name="ParentPath"/> reaches inside the JSON column <paramref name="Attribute"/> (plan #78). The row matches when the
/// property is defined and passes <paramref name="Test"/>; a missing path, a path through a scalar and a column that is null or
/// not valid JSON never match, except <see cref="JsonPathTest.NotDefined"/>. Compiled with <c>OPENJSON</c>; the path and the key
/// travel as SQL parameters. String range comparisons (<c>&lt; &gt; &lt;= &gt;=</c>) use the binary collation, which ignores
/// trailing spaces (<c>"a"</c> and <c>"a "</c> compare equal there); string equality is exact. Produced by the Cosmos SQL translator, never by FetchXML or QueryExpression.
/// </summary>
/// <param name="ParentPath">An SQL/JSON path in lax mode (<c>$</c>, <c>$."a b"[2]</c>), see <c>JsonPaths.Format</c>.</param>
/// <param name="Key">The last step: a property name, or the decimal index of an array element (<c>"2"</c>).</param>
/// <param name="Value">A <see cref="string"/>, a <see cref="double"/>, a <see cref="bool"/> or null (JSON null, with
/// <see cref="JsonPathTest.Equal"/> and <see cref="JsonPathTest.NotEqual"/>); ignored by <see cref="JsonPathTest.Defined"/> and
/// <see cref="JsonPathTest.NotDefined"/>.</param>
/// <param name="KeyIsIndex"><paramref name="Key"/> is an array index: the parent must then be a JSON array, so an object member
/// that happens to be named like the index never matches.</param>
public sealed record JsonPathConditionNode(string Attribute, string ParentPath, string Key, JsonPathTest Test, object? Value,
    bool KeyIsIndex = false);

/// <summary>
/// The records whose <c>versionnumber</c> (rowversion) is strictly between <paramref name="AfterExclusive"/> and
/// <paramref name="BeforeExclusive"/>: the new or updated rows of a change tracking read (plan 2.13). Compiled on the raw
/// rowversion column against the bounds cast to <c>binary(8)</c>, so the <c>IX_{table}_versionnumber</c> index can serve it.
/// Produced only by <c>ChangeTracker</c>, never by a query front end.
/// </summary>
public sealed record VersionRangeNode(long AfterExclusive, long BeforeExclusive);

/// <summary>
/// The records whose <c>ownerid</c> is one of <paramref name="Principals"/> (users and teams, no team lookup): the reports of a
/// manager and their teams (plan 2.5, ruling H4). Compiled as one JSON parameter, so that neither the parameter count nor the
/// plan depends on the number of principals. Empty: no record. Produced only by the read security, never by a query front end.
/// </summary>
public sealed record OwnedRecordsNode(IReadOnlyCollection<Guid> Principals);

/// <summary>
/// The <c>queue</c> rows <paramref name="UserId"/> is a direct member of (a <c>queuemembership</c> row; a team's members reach the
/// queues the team owns through the owner condition, plan 4.1a ruling R9). Produced only by the read security, never by a query
/// front end.
/// </summary>
public sealed record QueueMemberNode(Guid UserId);

/// <summary>
/// The records whose polymorphic lookup <paramref name="LookupAttribute"/> (with its <c>&lt;name&gt;type</c> column) points to a
/// record of one of <paramref name="Targets"/> that satisfies that target's read restriction: a record readable through its
/// parent (e.g. a <c>fileattachment</c> through the record that owns the file). No target: no record. Produced only by the read
/// security, never by a query front end.
/// </summary>
public sealed record ParentRecordsNode(string LookupAttribute, IReadOnlyList<ParentTarget> Targets);

/// <summary>
/// A parent entity of a <see cref="ParentRecordsNode"/> and the read restriction of its records, compiled in the scope of that
/// entity; null: every record of the entity. <paramref name="RowFilter"/>: a further condition on the child rows that point to
/// this entity, compiled in the child's scope (null: none), e.g. the <c>fileattachment</c> rows of secured columns the caller
/// cannot read. <paramref name="ObjectTypeCode"/> null: the lookup's type column is compared with <paramref name="TypeAttribute"/>
/// of the parent row instead (plan 2.11: an <c>activityparty</c> row through the <c>activitypointer</c> row of its activity, which
/// has the id of the activity and its table's code in <c>activitytypecode</c>). One of the two is required.
/// </summary>
public sealed record ParentTarget(string EntityName, int? ObjectTypeCode, FilterNode? ReadFilter, FilterNode? RowFilter = null,
    string? TypeAttribute = null);

/// <summary>
/// The records shared (<c>principalobjectaccess</c>) with <paramref name="UserId"/> or one of its teams with at least one
/// of the rights in <paramref name="AccessMask"/>. Produced only by the read security, never by a query front end.
/// </summary>
/// <param name="ObjectTypeCode">Object type code of the entity of the filter's scope.</param>
/// <param name="Principals">When set, the records shared with exactly these principals (users and teams, no team lookup)
/// instead of <paramref name="UserId"/> and its teams: the reports of a manager and their teams (plan 2.5, ruling H4).
/// <paramref name="UserId"/> then only names the principal the node belongs to. Empty: no record.</param>
/// <param name="TypeAttribute">When set, the share's object type code must equal this attribute of the scope's row instead of
/// <paramref name="ObjectTypeCode"/> (plan 2.11, ruling A13: an <c>activitypointer</c> row is shared through its activity, whose
/// shares carry the code of the activity's table, which the row holds in <c>activitytypecode</c>).</param>
public sealed record SharedRecordsNode(Guid UserId, int ObjectTypeCode, int AccessMask, IReadOnlyCollection<Guid>? Principals = null,
    string? TypeAttribute = null);

/// <param name="BinaryCollation">Compares a string column with the binary collation (exact, case-sensitive) instead of the column's
/// own; set only by the elastic partition filter (plan 3.4) and the Cosmos SQL translator (plan #78), never by a query front end.</param>
public sealed record ConditionNode(string? EntityAlias, string Attribute, ConditionOperator Operator, IReadOnlyList<object?> Values,
    bool BinaryCollation = false);

/// <summary>Alias: order by an aggregate alias.</summary>
public sealed record OrderNode(string Attribute, bool Descending, string? Alias = null);

public sealed class QueryModel
{
    private EntityNode _root = null!;

    public required EntityNode Root { get => _root; init => _root = value; }
    public int? Top { get; set; }
    public int? PageSize { get; set; }
    public int PageNumber { get; set; } = 1;
    public string? PagingCookie { get; set; }
    public bool Distinct { get; set; }
    public bool Aggregate { get; set; }
    public bool ReturnTotalRecordCount { get; set; }

    /// <summary>
    /// FetchXML <c>useraworderby</c>: choice columns are ordered by their stored integer value instead of their label (Learn,
    /// "Ordering lookup and choice columns"). Lookups are ordered by the related primary name either way.
    /// </summary>
    public bool UseRawOrderBy { get; set; }

    /// <summary>
    /// SQL semantics for the order (the TDS endpoint): every column, lookups and choices included, is ordered by its stored
    /// value, as a SQL <c>ORDER BY</c> on that column does; its name or label is a column of its own there.
    /// </summary>
    public bool OrderByStoredValues { get; set; }

    /// <summary>A copy of this query with every setting kept and <paramref name="root"/> as its root.</summary>
    public QueryModel WithRoot(EntityNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var copy = (QueryModel)MemberwiseClone(); // every setting, including those added later
        copy._root = root;
        return copy;
    }
}
