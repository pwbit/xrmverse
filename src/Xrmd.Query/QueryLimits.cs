namespace Xrmd.Query;

/// <summary>
/// Structural limits of a query, enforced by every frontend (<see cref="FetchXml.FetchXmlParser"/>,
/// <see cref="QueryExpressionReader"/>) and by <see cref="Sql.SqlQueryCompiler"/> before recursing, so that a hostile
/// query is a validation error instead of a stack overflow or a SQL Server error.
/// </summary>
public static class QueryLimits
{
    /// <summary>Maximum nesting depth of filters; the top-level filter of an entity or link-entity is depth 1.</summary>
    public const int MaxFilterDepth = 32;

    /// <summary>Maximum nesting depth of link-entities; a link-entity of the root entity is depth 1.</summary>
    public const int MaxLinkDepth = 10;

    /// <summary>Maximum SQL parameters of a compiled query (SQL Server accepts 2100 per request).</summary>
    public const int MaxParameters = 2000;
}
