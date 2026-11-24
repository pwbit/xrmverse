using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xunit;

namespace Xrmd.Model.Tests.Query;

public class QueryDefinitionTests
{
    [Fact]
    public void Collections_start_empty()
    {
        var q = new RecordQuery();
        Assert.Empty(q.Orders);
        Assert.Empty(q.Links);
        Assert.Empty(q.Columns!.Expressions);
        Assert.Empty(q.Criteria!.Conditions);
        Assert.Empty(q.Criteria.Filters);
        var a = new AttributeQuery();
        Assert.Empty(a.Attributes);
        Assert.Empty(a.Values);
        Assert.Empty(a.Orders);
        Assert.Empty(new QueryCondition().Values);
        var l = new QueryLink();
        Assert.Empty(l.Links);
        Assert.Empty(l.Orders);
    }

    [Fact]
    public void Collections_are_independent_between_instances()
    {
        var a = new RecordQuery();
        var b = new RecordQuery();
        a.Orders.Add(new QueryOrder());
        a.Links.Add(new QueryLink());
        a.Criteria!.Conditions.Add(new QueryCondition());
        a.Columns!.Expressions.Add(new ColumnExpression());
        Assert.Empty(b.Orders);
        Assert.Empty(b.Links);
        Assert.Empty(b.Criteria!.Conditions);
        Assert.Empty(b.Columns!.Expressions);
        Assert.NotSame(a.Criteria, b.Criteria);
        Assert.NotSame(a.Columns, b.Columns);
        Assert.NotSame(a.Paging, b.Paging);

        var c = new AttributeQuery();
        var d = new AttributeQuery();
        c.Attributes.Add("x");
        c.Values.Add(1);
        Assert.Empty(d.Attributes);
        Assert.Empty(d.Values);
    }
}
