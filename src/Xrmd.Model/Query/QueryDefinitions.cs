using Xrmd.Model.Records;

namespace Xrmd.Model.Query;

/// <summary>Base of the queries that travel in messages.</summary>
public abstract class QueryDefinition;

/// <summary>Our counterpart of the SDK's public <c>QueryExpression</c>.</summary>
public sealed class RecordQuery : QueryDefinition
{
    public string? EntityName { get; set; }

    public ColumnSelection? Columns { get; set; } = new();

    public QueryFilter? Criteria { get; set; } = new();

    public List<QueryOrder> Orders { get; set; } = new();

    public List<QueryLink> Links { get; set; } = new();

    public bool Distinct { get; set; }

    public bool NoLock { get; set; }

    public int? TopCount { get; set; }

    public QueryPaging? Paging { get; set; } = new();

    public string? QueryHints { get; set; }

    public string? ForceSeek { get; set; }

    public string? DataSource { get; set; }

    public RecordQuery? SubQuery { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>QueryByAttribute</c>.</summary>
public sealed class AttributeQuery : QueryDefinition
{
    public string? EntityName { get; set; }

    public ColumnSelection? Columns { get; set; } = new();

    public List<string> Attributes { get; set; } = new();

    public List<object?> Values { get; set; } = new();

    public List<QueryOrder> Orders { get; set; } = new();

    public QueryPaging? Paging { get; set; } = new();

    public int? TopCount { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>FetchExpression</c>.</summary>
public sealed class FetchQuery : QueryDefinition
{
    public string? Text { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>FilterExpression</c>.</summary>
public sealed class QueryFilter
{
    public FilterLogic Logic { get; set; }

    public List<QueryCondition> Conditions { get; set; } = new();

    public List<QueryFilter> Filters { get; set; } = new();

    public bool IsQuickFindFilter { get; set; }

    public string? FilterHint { get; set; }

    public QueryLink? AnyAllLink { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>ConditionExpression</c>.</summary>
public sealed class QueryCondition
{
    public string? EntityName { get; set; }

    public string? AttributeName { get; set; }

    public FilterOperator Operator { get; set; }

    public List<object?> Values { get; set; } = new();

    public bool CompareColumns { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>LinkEntity</c>.</summary>
public sealed class QueryLink
{
    public string? FromEntityName { get; set; }

    public string? FromAttributeName { get; set; }

    public string? ToEntityName { get; set; }

    public string? ToAttributeName { get; set; }

    public JoinKind Join { get; set; }

    public ColumnSelection? Columns { get; set; } = new();

    public QueryFilter? Criteria { get; set; } = new();

    public List<QueryLink> Links { get; set; } = new();

    public string? Alias { get; set; }

    public string? ForceSeek { get; set; }

    public List<QueryOrder> Orders { get; set; } = new();
}

/// <summary>Our counterpart of the SDK's public <c>OrderExpression</c>.</summary>
public sealed class QueryOrder
{
    public string? AttributeName { get; set; }

    public SortDirection Direction { get; set; }

    public string? Alias { get; set; }

    public string? EntityName { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>PagingInfo</c>.</summary>
public sealed class QueryPaging
{
    public int PageNumber { get; set; }

    public int Count { get; set; }

    public bool ReturnTotalRecordCount { get; set; }

    public bool ReturnRecordCountLessThanMarv { get; set; }

    public string? PagingCookie { get; set; }
}
