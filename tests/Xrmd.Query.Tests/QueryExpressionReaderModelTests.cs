using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using FilterOperator = Xrmd.Model.Query.FilterOperator;

namespace Xrmd.Query.Tests;

/// <summary>The reader works on the model types; the SDK twins convert first and read exactly as before.</summary>
public class QueryExpressionReaderModelTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));

    private static QueryExpression Sink()
    {
        var nested = new FilterExpression(LogicalOperator.And);
        nested.AddCondition("pb_count", ConditionOperator.NotNull);
        var criteria = new FilterExpression(LogicalOperator.Or);
        criteria.Filters.Add(nested);
        criteria.AddCondition("pb_budget", ConditionOperator.GreaterThan, new Money(5m));
        criteria.AddCondition("pb_priority", ConditionOperator.Equal, new OptionSetValue(2));
        criteria.AddCondition("pb_name", ConditionOperator.Like, "Con%");
        criteria.AddCondition("pb_count", ConditionOperator.In, new[] { 1, 2, 3 });
        criteria.Conditions.Add(new ConditionExpression("u", "fullname", ConditionOperator.Equal, "x"));

        var link = new LinkEntity("pb_project", "systemuser", "pb_customerid", "systemuserid", JoinOperator.LeftOuter)
        {
            EntityAlias = "u",
            Columns = new ColumnSet("fullname"),
        };
        link.LinkCriteria.AddCondition("statecode", ConditionOperator.Equal, 0);
        link.Orders.Add(new OrderExpression("fullname", OrderType.Descending));
        link.LinkEntities.Add(new LinkEntity("systemuser", "team", "ownerid", "teamid", JoinOperator.Inner));

        var q = new QueryExpression("pb_project")
        {
            ColumnSet = new ColumnSet("pb_name", "pb_count"),
            Criteria = criteria,
            Distinct = true,
            TopCount = 7,
            PageInfo = new PagingInfo { PageNumber = 2, Count = 50, ReturnTotalRecordCount = true, PagingCookie = "<cookie/>" },
        };
        q.Orders.Add(new OrderExpression("pb_name", OrderType.Descending));
        q.LinkEntities.Add(link);
        return q;
    }

    [Fact]
    public void The_sdk_twins_delegate_to_the_model_reader()
    {
        var q = Sink();
        Same(QueryExpressionReader.Read(q), QueryExpressionReader.Read(q.ToModel()));
        QueryExpressionReader.Read(q).Root.Links.Single().Links.Should().HaveCount(1);

        var byAttribute = new QueryByAttribute("pb_project")
        {
            ColumnSet = new ColumnSet("pb_name"),
            TopCount = 3,
            PageInfo = new PagingInfo { PageNumber = 2, Count = 25, PagingCookie = "k" },
        };
        byAttribute.AddAttributeValue("pb_name", "x");
        byAttribute.AddAttributeValue("pb_budget", new Money(2m));
        byAttribute.Orders.Add(new OrderExpression("pb_name", OrderType.Descending));
        Same(QueryExpressionReader.Read(byAttribute), QueryExpressionReader.Read(byAttribute.ToModel()));
    }

    private static void Same(QueryModel sdk, QueryModel model)
    {
        sdk.Should().BeEquivalentTo(model, o => o.RespectingRuntimeTypes().WithStrictOrdering());
        model.Root.Filter.Conditions.Should().NotBeEmpty();
    }

    private static CompiledQuery Compile(QueryModel model) =>
        new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(model, Context);

    private static RecordQuery ModelQuery(string attribute, object? value)
    {
        var q = new RecordQuery { EntityName = "pb_project", Columns = new ColumnSelection { Columns = { "pb_name" } } };
        q.Criteria!.Conditions.Add(new QueryCondition { AttributeName = attribute, Operator = FilterOperator.Equal, Values = { value } });
        return q;
    }

    private static QueryExpression SdkQuery(string attribute, object value)
    {
        var q = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        q.Criteria.AddCondition(attribute, ConditionOperator.Equal, value);
        return q;
    }

    [Fact]
    public void Model_values_coerce_like_sdk_values()
    {
        var id = Guid.NewGuid();
        var cases = new (string Attribute, object Sdk, object Model)[]
        {
            ("pb_budget", new Money(5m), new MoneyValue(5m)),
            ("pb_priority", new OptionSetValue(3), new ChoiceValue(3)),
            ("pb_customerid", new EntityReference("systemuser", id), new RecordReference { LogicalName = "systemuser", Id = id }),
        };
        foreach (var (attribute, sdk, model) in cases)
        {
            var expected = Compile(QueryExpressionReader.Read(SdkQuery(attribute, sdk)));
            var actual = Compile(QueryExpressionReader.Read(ModelQuery(attribute, model)));

            actual.Sql.Should().Be(expected.Sql, attribute);
            actual.Parameters.Select(p => (p.ParameterName, p.Value)).Should().Equal(expected.Parameters.Select(p => (p.ParameterName, p.Value)), attribute);
        }
    }

    [Fact]
    public void Refusals_are_unchanged()
    {
        var expressions = new RecordQuery { EntityName = "a", Columns = new ColumnSelection { Expressions = { new ColumnExpression { AttributeName = "x" } } } };
        ((Action)(() => QueryExpressionReader.Read(expressions))).Should().Throw<XrmdValidationException>()
            .WithMessage("ColumnSet.AttributeExpressions is not supported.");

        var compare = new RecordQuery { EntityName = "a" };
        compare.Criteria!.Conditions.Add(new QueryCondition { AttributeName = "x", CompareColumns = true });
        ((Action)(() => QueryExpressionReader.Read(compare))).Should().Throw<XrmdValidationException>()
            .WithMessage("Condition on 'x': column comparison (CompareColumns) is not supported.");

        var deepFilter = new RecordQuery { EntityName = "a" };
        var f = deepFilter.Criteria!;
        for (var i = 0; i < QueryLimits.MaxFilterDepth; i++)
        {
            var sub = new QueryFilter();
            f.Filters.Add(sub);
            f = sub;
        }
        ((Action)(() => QueryExpressionReader.Read(deepFilter))).Should().Throw<XrmdValidationException>()
            .WithMessage($"Filters are nested more than {QueryLimits.MaxFilterDepth} levels deep.");

        var deepLinks = new RecordQuery { EntityName = "a" };
        var links = deepLinks.Links;
        for (var i = 0; i <= QueryLimits.MaxLinkDepth; i++)
        {
            var l = new QueryLink { ToEntityName = "b", ToAttributeName = "id", FromAttributeName = "bid" };
            links.Add(l);
            links = l.Links;
        }
        ((Action)(() => QueryExpressionReader.Read(deepLinks))).Should().Throw<XrmdValidationException>()
            .WithMessage($"Link-entities are nested more than {QueryLimits.MaxLinkDepth} levels deep.");

        var undefinedOperator = ModelQuery("pb_name", "a");
        undefinedOperator.Criteria!.Conditions[0].Operator = (FilterOperator)999;
        ((Action)(() => Compile(QueryExpressionReader.Read(undefinedOperator)))).Should().Throw<XrmdValidationException>()
            .WithMessage("Operator 999 is not supported (attribute 'pb_name').");

        var undefinedLogic = new RecordQuery { EntityName = "pb_project", Columns = new ColumnSelection { Columns = { "pb_name" } } };
        undefinedLogic.Criteria!.Logic = (FilterLogic)7;
        undefinedLogic.Criteria.Conditions.Add(new QueryCondition { AttributeName = "pb_name", Operator = FilterOperator.Equal, Values = { "a" } });
        undefinedLogic.Criteria.Conditions.Add(new QueryCondition { AttributeName = "pb_count", Operator = FilterOperator.Equal, Values = { 1 } });
        var asAnd = new RecordQuery { EntityName = "pb_project", Columns = undefinedLogic.Columns, Criteria = new QueryFilter { Conditions = undefinedLogic.Criteria.Conditions } };
        Compile(QueryExpressionReader.Read(undefinedLogic)).Sql.Should().Be(Compile(QueryExpressionReader.Read(asAnd)).Sql).And.Contain(" AND ");
    }

    [Fact]
    public void The_model_value_types_print_as_the_sdk_value_types_did()
    {
        new Money(1m).ToString().Should().Be("Microsoft.Xrm.Sdk.Money");
        new OptionSetValue(1).ToString().Should().Be("Microsoft.Xrm.Sdk.OptionSetValue");
        new EntityReference("a", Guid.Empty).ToString().Should().Be("Microsoft.Xrm.Sdk.EntityReference");
    }

    [Theory]
    [InlineData("money")]
    [InlineData("choice")]
    [InlineData("reference")]
    public void An_sdk_value_that_cannot_be_coerced_keeps_its_fault_message(string kind)
    {
        (object value, string text) = kind switch
        {
            "money" => (new Money(5m), "Microsoft.Xrm.Sdk.Money"),
            "choice" => (new OptionSetValue(3), "Microsoft.Xrm.Sdk.OptionSetValue"),
            _ => ((object)new EntityReference("systemuser", Guid.NewGuid()), "Microsoft.Xrm.Sdk.EntityReference"),
        };

        var invalid = () => Compile(QueryExpressionReader.Read(SdkQuery("pb_name", value)));
        invalid.Should().Throw<XrmdValidationException>().WithMessage($"Invalid value '{text}' for attribute 'pb_name' of type *");

        var count = () => Compile(QueryExpressionReader.Read(SetOperator(SdkQuery("pb_startdate", value), ConditionOperator.LastXDays)));
        count.Should().Throw<XrmdValidationException>()
            .WithMessage($"Operator LastXDays on attribute 'pb_startdate' requires a positive integer, got '{text}'.");
    }

    private static QueryExpression SetOperator(QueryExpression q, ConditionOperator op)
    {
        q.Criteria.Conditions[0].Operator = op;
        return q;
    }

    [Fact]
    public void A_null_sub_filter_reads_as_an_empty_filter_as_before()
    {
        var q = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        q.Criteria.Filters.Add(null!);

        var model = QueryExpressionReader.Read(q);

        model.Root.Filter.Filters.Should().ContainSingle().Which.Conditions.Should().BeEmpty();
    }

    private static void OnDefaultStack(Action act)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { act(); } catch (Exception e) { caught = e; }
        });
        thread.Start();
        thread.Join();
        caught.Should().BeOfType<XrmdValidationException>();
        caught!.Message.Should().NotBeEmpty();
    }

    [Fact]
    public void A_hugely_nested_sdk_query_is_a_validation_error_not_a_stack_overflow()
    {
        var filters = new QueryExpression("a");
        var f = filters.Criteria;
        for (var i = 0; i < 50_000; i++)
        {
            var sub = new FilterExpression();
            f.Filters.Add(sub);
            f = sub;
        }
        OnDefaultStack(() => QueryExpressionReader.Read(filters));

        var links = new QueryExpression("a");
        var l = links.AddLink("b", "bid", "id");
        for (var i = 0; i < 50_000; i++) l = l.AddLink("b", "bid", "id");
        OnDefaultStack(() => QueryExpressionReader.Read(links));
    }

    private static QueryExpression IgnoredMembers(bool inAnyAll)
    {
        var holder = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        var inner = new LinkEntity("a", "b", "x", "y", JoinOperator.Any) { Columns = new ColumnSet("c") };
        inner.Columns.AttributeExpressions.Add(new XrmAttributeExpression("c", XrmAggregateType.Count, "n"));
        inner.LinkCriteria.Conditions.Add(new ConditionExpression { AttributeName = "a", Operator = ConditionOperator.Equal, CompareColumns = true });
        var l = inner;
        for (var i = 0; i < 11; i++) l = l.AddLink("b", "x", "y");
        if (inAnyAll)
        {
            holder.Criteria.AnyAllFilterLinkEntity = inner;
            return holder;
        }
        var sub = new QueryExpression("b") { ColumnSet = inner.Columns, Criteria = inner.LinkCriteria };
        sub.LinkEntities.Add(inner);
        holder.SubQueryExpression = sub;
        return holder;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Members_the_reader_ignores_are_not_refused(bool inAnyAll)
    {
        var act = () => QueryExpressionReader.Read(IgnoredMembers(inAnyAll));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hugely_nested_link_chain_in_an_ignored_member_is_a_validation_error(bool inAnyAll)
    {
        var holder = new QueryExpression("a");
        var inner = new LinkEntity("a", "b", "x", "y", JoinOperator.Any);
        var l = inner;
        for (var i = 0; i < 50_000; i++) l = l.AddLink("b", "x", "y");
        if (inAnyAll) holder.Criteria.AnyAllFilterLinkEntity = inner;
        else
        {
            var sub = new QueryExpression("b");
            sub.LinkEntities.Add(inner);
            holder.SubQueryExpression = sub;
        }
        OnDefaultStack(() => QueryExpressionReader.Read(holder));
    }

    [Fact]
    public void A_null_attribute_expression_round_trips_and_is_refused_by_the_reader()
    {
        var cs = new ColumnSet("a");
        cs.AttributeExpressions.Add(null!);
        var model = cs.ToModel();
        model.Expressions.Should().ContainSingle().Which.Should().BeNull();
        model.ToSdk().AttributeExpressions.Should().ContainSingle().Which.Should().BeNull();

        var byAttribute = new QueryByAttribute("pb_project") { ColumnSet = cs };
        var expression = new QueryExpression("pb_project") { ColumnSet = cs };
        foreach (var act in new Action[] { () => QueryExpressionReader.Read(byAttribute), () => QueryExpressionReader.Read(expression) })
            act.Should().Throw<XrmdValidationException>().WithMessage("ColumnSet.AttributeExpressions is not supported.");
    }
}
