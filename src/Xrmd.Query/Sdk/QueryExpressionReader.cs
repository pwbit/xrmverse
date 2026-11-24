using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Query.Model;

namespace Xrmd.Query;

/// <summary>The SDK twins of the reader: they check the nesting, then convert to the model.</summary>
public static partial class QueryExpressionReader
{
    public static QueryModel Read(QueryExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        SdkNestingGuard.Check(query);
        return Read(query.ToModel());
    }

    public static QueryModel Read(QueryByAttribute query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Read(query.ToModel());
    }
}

/// <summary>
/// A bounded walk of an SDK query before it is converted: the conversion recurses without a limit, so a hostile query
/// nested tens of thousands of levels deep would overflow the stack. The walk follows the reader's order and throws its
/// refusals, so the first refusal is the same as when the reader walked the SDK query itself. The members the reader ignores
/// (any/all link of a filter, sub-query) are walked for depth only, within the filter limit.
/// </summary>
internal static class SdkNestingGuard
{
    internal static void Check(QueryExpression query)
    {
        Columns(query.ColumnSet, 0);
        Filter(query.Criteria, 1, 0);
        Links(query.LinkEntities, 1, 0);
        SubQuery(query.SubQueryExpression, 1);
    }

    // ignored > 0: inside a member the reader does not read; only the depth caps apply there.
    private static void Columns(ColumnSet? columns, int ignored)
    {
        if (ignored == 0 && columns?.AttributeExpressions is { Count: > 0 }) throw QueryExpressionReader.ExpressionsRefusal();
    }

    private static void Links(IEnumerable<LinkEntity?>? links, int depth, int ignored)
    {
        if (links == null) return;
        foreach (var l in links)
        {
            if (ignored == 0 && depth > QueryLimits.MaxLinkDepth) throw QueryExpressionReader.LinkDepthRefusal();
            if (ignored > 0 && depth > QueryLimits.MaxFilterDepth) throw QueryExpressionReader.FilterDepthRefusal();
            if (l == null) continue;
            Columns(l.Columns, ignored);
            Filter(l.LinkCriteria, 1, ignored);
            Links(l.LinkEntities, depth + 1, ignored);
        }
    }

    private static void Filter(FilterExpression? f, int depth, int ignored)
    {
        if (depth > QueryLimits.MaxFilterDepth) throw QueryExpressionReader.FilterDepthRefusal();
        if (f == null) return;
        foreach (var c in f.Conditions)
            if (ignored == 0 && c is { CompareColumns: true }) throw QueryExpressionReader.CompareColumnsRefusal(c.AttributeName);
        foreach (var sub in f.Filters) Filter(sub, depth + 1, ignored);
        if (f.AnyAllFilterLinkEntity is { } link)
        {
            if (ignored + 1 > QueryLimits.MaxFilterDepth) throw QueryExpressionReader.FilterDepthRefusal();
            Filter(link.LinkCriteria, 1, ignored + 1);
            Links(link.LinkEntities, 1, ignored + 1);
        }
    }

    private static void SubQuery(QueryExpression? sub, int ignored)
    {
        if (sub == null) return;
        if (ignored > QueryLimits.MaxFilterDepth) throw QueryExpressionReader.FilterDepthRefusal();
        Columns(sub.ColumnSet, ignored);
        Filter(sub.Criteria, 1, ignored);
        Links(sub.LinkEntities, 1, ignored);
        SubQuery(sub.SubQueryExpression, ignored + 1);
    }
}
