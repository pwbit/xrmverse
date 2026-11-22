using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>
/// The hidden currency column (plan 2.10, ruling X11): each scope that selects a money value in its record's currency also reads
/// its <c>transactioncurrencyid</c> as a <see cref="ResultKind.Currency"/> column, which the executor formats the money with and
/// never returns. Aggregate and distinct queries get none (the column would change their groups and rows).
/// </summary>
public sealed class CurrencyCompilerTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));

    private static CompiledQuery Compile(QueryModel q) => new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, Context);

    private static EntityNode Project(params string[] columns)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return root;
    }

    private static LinkNode Linked(string alias, params string[] columns)
    {
        var link = new LinkNode { EntityName = "pb_project", From = "pb_projectid", To = "pb_projectid", Alias = alias, JoinType = JoinOperator.LeftOuter };
        link.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return link;
    }

    private static List<ResultColumn> CurrencyColumns(CompiledQuery c) => c.Columns.Where(x => x.Kind == ResultKind.Currency).ToList();

    [Fact]
    public void Money_selection_adds_hidden_currency_column_per_scope()
    {
        var root = Project("pb_name", "pb_budget");
        root.Links.Add(Linked("other", "pb_budget"));
        root.Links.Add(Linked("nomoney", "pb_name"));

        var c = Compile(new QueryModel { Root = root });

        var currencies = CurrencyColumns(c);
        currencies.Select(x => x.EntityAlias).Should().Equal(null, "other");
        currencies.Should().OnlyContain(x => x.Attribute!.LogicalName == "transactioncurrencyid" && x.EntityName == "pb_project");
        currencies.Select(x => x.ResultName).Should().Equal("transactioncurrencyid", "other.transactioncurrencyid");
        c.Sql.Should().Contain($"t0.[transactioncurrencyid] AS {currencies[0].SqlAlias}").And.Contain($"t1.[transactioncurrencyid] AS {currencies[1].SqlAlias}");
        // The requested values are unchanged: the hidden column is not a value of the result.
        c.Columns.Where(x => x.Kind == ResultKind.Value).Select(x => x.ResultName)
            .Should().Equal("pb_projectid", "pb_name", "pb_budget", "other.pb_budget", "nomoney.pb_name");
    }

    [Fact]
    public void All_columns_read_the_currency_once_as_value_and_once_hidden()
    {
        var root = new EntityNode { EntityName = "pb_project", AllColumns = true };

        var c = Compile(new QueryModel { Root = root });

        CurrencyColumns(c).Should().ContainSingle().Which.EntityAlias.Should().BeNull();
        c.Columns.Should().ContainSingle(x => x.Kind == ResultKind.Value && x.ResultName == "transactioncurrencyid");
    }

    [Fact]
    public void No_money_or_only_base_money_adds_no_currency_column()
    {
        Compile(new QueryModel { Root = Project("pb_name") }).Columns.Should().NotContain(x => x.Kind == ResultKind.Currency);
        Compile(new QueryModel { Root = Project("pb_budget_base") }).Columns.Should().NotContain(x => x.Kind == ResultKind.Currency);
    }

    [Fact]
    public void Aggregate_and_distinct_queries_get_no_hidden_currency_column()
    {
        var aggregate = Project();
        aggregate.Columns.Add(new ColumnNode("pb_budget", "total", XrmAggregate.Sum));
        aggregate.Columns.Add(new ColumnNode("pb_priority", "prio", GroupBy: true));
        var grouped = Compile(new QueryModel { Root = aggregate, Aggregate = true });
        grouped.Columns.Should().NotContain(x => x.Kind == ResultKind.Currency);
        grouped.Sql.Should().NotContain("transactioncurrencyid");

        var distinct = Compile(new QueryModel { Root = Project("pb_budget"), Distinct = true });
        distinct.Columns.Should().NotContain(x => x.Kind == ResultKind.Currency);
        distinct.Sql.Should().NotContain("transactioncurrencyid");
    }
}
