using System.Reflection;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Query;
using Xrmd.Query.Model;

namespace Xrmd.Core.Tests;

/// <summary>Plan 3.4, task 5: what a query on an elastic table may do (<see cref="ElasticQueryRules.Demand"/>).</summary>
public sealed class ElasticQueryRulesTests
{
    private const int LinkCode = unchecked((int)0x80048d0b);

    /// <summary>
    /// A metadata store that answers <c>GetTable</c> only. Each SDK fixture is converted to the model when the store is made
    /// (<c>SdkMetadataView.ModelOf</c>): a fixture mutated after that is not seen.
    /// </summary>
    private class StoreProxy : DispatchProxy
    {
        public Dictionary<string, TableDefinition> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IMetadataStore.GetTable) && args is [string name]
                ? Entities.GetValueOrDefault(name)
                : throw new NotSupportedException(targetMethod.Name);
    }

    private static EntityMetadata Table(string name, bool elastic, OwnershipTypes ownership = OwnershipTypes.OrganizationOwned)
    {
        var e = new EntityMetadata { LogicalName = name, TableType = elastic ? "Elastic" : "Standard" };
        e.OwnershipType = ownership;
        e.SetAttributes([
            new StringAttributeMetadata { LogicalName = "new_name" },
            new IntegerAttributeMetadata { LogicalName = "new_value" },
            new LookupAttributeMetadata { LogicalName = "new_parent" },
        ]);
        return e;
    }

    private static IMetadataStore Store(params EntityMetadata[] entities)
    {
        var store = DispatchProxy.Create<IMetadataStore, StoreProxy>();
        foreach (var e in entities) ((StoreProxy)(object)store).Entities[e.LogicalName] = SdkMetadataView.ModelOf(e);
        return store;
    }

    private static readonly IMetadataStore Default = Store(
        Table("new_sensor", true), Table("new_usersensor", true, OwnershipTypes.UserOwned), Table("account", false));

    private static QueryModel Query(string table, Action<QueryModel>? configure = null)
    {
        var q = new QueryModel { Root = new EntityNode { EntityName = table } };
        q.Root.Columns.Add(new ColumnNode("new_name"));
        configure?.Invoke(q);
        return q;
    }

    private static Action Demand(QueryModel q) => () => ElasticQueryRules.Demand(q, Default);

    [Fact]
    public void Link_from_elastic_root_is_refused_with_the_link_code()
    {
        var q = Query("new_sensor", q => q.Root.Links.Add(new LinkNode { EntityName = "account", From = "accountid", To = "new_parent" }));
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == LinkCode && e.Message.Contains("new_sensor"));
    }

    [Fact]
    public void Link_from_standard_root_to_elastic_table_is_refused_with_the_link_code()
    {
        var q = Query("account", q => q.Root.Links.Add(new LinkNode { EntityName = "new_sensor", From = "new_parent", To = "accountid" }));
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == LinkCode);
    }

    [Fact]
    public void Nested_link_to_elastic_table_is_refused()
    {
        var q = Query("account", q =>
        {
            var inner = new LinkNode { EntityName = "account", From = "accountid", To = "parentaccountid" };
            inner.Links.Add(new LinkNode { EntityName = "new_sensor", From = "new_parent", To = "accountid" });
            q.Root.Links.Add(inner);
        });
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == LinkCode);
    }

    [Fact]
    public void Link_between_standard_tables_passes()
    {
        var q = Query("account", q => q.Root.Links.Add(new LinkNode { EntityName = "account", From = "accountid", To = "parentaccountid" }));
        Demand(q).Should().NotThrow();
    }

    [Fact]
    public void Order_by_lookup_is_refused()
    {
        var q = Query("new_sensor", q => q.Root.Orders.Add(new OrderNode("new_parent", false)));
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("elastic table"));
    }

    [Fact]
    public void Order_by_lookup_on_standard_table_passes()
    {
        var q = Query("account", q => q.Root.Orders.Add(new OrderNode("new_parent", false)));
        Demand(q).Should().NotThrow();
    }

    [Fact]
    public void Plain_filter_and_order_by_non_lookup_pass()
    {
        var q = Query("new_sensor", q =>
        {
            q.Root.Filter.Conditions.Add(new ConditionNode(null, "new_value", FilterOperator.GreaterThan, [3]));
            q.Root.Orders.Add(new OrderNode("new_value", true));
        });
        Demand(q).Should().NotThrow();
    }

    [Fact]
    public void Distinct_with_order_on_another_attribute_is_refused()
    {
        var q = Query("new_sensor", q =>
        {
            q.Distinct = true;
            q.Root.Orders.Add(new OrderNode("new_value", false));
        });
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("elastic table"));
    }

    [Fact]
    public void Distinct_with_order_on_selected_attribute_passes()
    {
        var q = Query("new_sensor", q =>
        {
            q.Distinct = true;
            q.Root.Orders.Add(new OrderNode("new_name", false));
        });
        Demand(q).Should().NotThrow();
    }

    [Fact]
    public void Distinct_with_more_than_one_order_is_refused()
    {
        var q = Query("new_sensor", q =>
        {
            q.Distinct = true;
            q.Root.Columns.Add(new ColumnNode("new_value"));
            q.Root.Orders.Add(new OrderNode("new_name", false));
            q.Root.Orders.Add(new OrderNode("new_value", false));
        });
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument);
    }

    [Fact]
    public void Distinct_on_user_owned_elastic_table_is_refused()
    {
        var q = Query("new_usersensor", q => q.Distinct = true);
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("user-owned"));
    }

    [Fact]
    public void Distinct_on_organization_owned_elastic_table_passes()
    {
        Demand(Query("new_sensor", q => q.Distinct = true)).Should().NotThrow();
    }

    [Fact]
    public void Order_with_group_by_in_aggregate_is_refused()
    {
        var q = Query("new_sensor", q =>
        {
            q.Aggregate = true;
            q.Root.Columns.Clear();
            q.Root.Columns.Add(new ColumnNode("new_name", "n", GroupBy: true));
            q.Root.Columns.Add(new ColumnNode("new_value", "total", XrmAggregate.Sum));
            q.Root.Orders.Add(new OrderNode("new_name", false, "n"));
        });
        Demand(q).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("elastic table"));
    }

    [Fact]
    public void Count_aggregate_passes()
    {
        var q = Query("new_sensor", q =>
        {
            q.Aggregate = true;
            q.Root.Columns.Clear();
            q.Root.Columns.Add(new ColumnNode("new_sensorid", "c", XrmAggregate.Count));
        });
        Demand(q).Should().NotThrow();
    }

    [Fact]
    public void WithPartition_keeps_an_or_filter_inside_the_partition_and_is_binary()
    {
        var q = Query("new_sensor", q =>
        {
            q.Root.Filter.Operator = FilterLogic.Or;
            q.Root.Filter.Conditions.Add(new ConditionNode(null, "new_value", FilterOperator.Equal, [1]));
        });
        var scoped = ElasticQueryRules.WithPartition(q, "p1");
        scoped.Root.Filter.Operator.Should().Be(FilterLogic.And);
        scoped.Root.Filter.Filters.Should().ContainSingle().Which.Operator.Should().Be(FilterLogic.Or);
        scoped.Root.Filter.Conditions.Should().ContainSingle().Which.Should().Match<ConditionNode>(c =>
            c.Attribute == "partitionid" && c.BinaryCollation && (string)c.Values[0]! == "p1");
        q.Root.Filter.Operator.Should().Be(FilterLogic.Or, "the original model is not changed");
    }

    [Fact]
    public void WithPartition_refuses_an_invalid_partition()
    {
        Action act = () => ElasticQueryRules.WithPartition(Query("new_sensor"), "a/b");
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument);
    }

    [Fact]
    public void Default_page_size_applies_to_elastic_roots_only_and_never_overrides()
    {
        ElasticQueryRules.WithDefaultPageSize(Query("new_sensor"), Default).PageSize.Should().Be(500);
        ElasticQueryRules.WithDefaultPageSize(Query("new_sensor", q => q.PageSize = 10), Default).PageSize.Should().Be(10);
        ElasticQueryRules.WithDefaultPageSize(Query("account"), Default).PageSize.Should().BeNull();
    }
}
