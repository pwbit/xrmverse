namespace Xrmd.Core.Query;

/// <summary>
/// The limit on <c>EntityCollection.TotalRecordCount</c> (Learn: "Count rows using FetchXml", "Count rows using
/// QueryExpression"): the count of matching records stops at 5,000 for standard tables and 500 for elastic tables, and
/// <c>TotalRecordCountLimitExceeded</c> says when more records match.
/// </summary>
public static class TotalRecordCount
{
    /// <summary>The limit for a standard table.</summary>
    public const int StandardLimit = 5000;

    /// <summary>The limit for an elastic table.</summary>
    public const int ElasticLimit = 500;

    /// <summary>The total as returned, and whether the real total is above the limit.</summary>
    public static (int Count, bool LimitExceeded) Cap(int total, bool elastic)
    {
        var limit = elastic ? ElasticLimit : StandardLimit;
        return total > limit ? (limit, true) : (total, false);
    }
}
