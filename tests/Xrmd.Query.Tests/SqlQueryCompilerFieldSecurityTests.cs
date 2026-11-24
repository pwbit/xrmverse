using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Model.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>
/// Field-level security in the compiler (plan 14, task 4, ruling F3): the secured columns the caller cannot read
/// (<see cref="IQuerySecurity.UnreadableColumns"/>) are marked in the projection, so that the executor nulls them per row, and
/// are refused (0x8004F504) in a condition, an order, an aggregate or a link join, before any SQL runs.
/// </summary>
public class SqlQueryCompilerFieldSecurityTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    /// <summary><c>pb_project.pb_budget</c>, <c>pb_project.pb_count</c> and <c>systemuser.domainname</c> are unreadable.</summary>
    private sealed class FieldSecurity : IQuerySecurity
    {
        public FilterNode? ReadFilter(EntityMetadata entity) => null;

        public IReadOnlySet<string> UnreadableColumns(EntityMetadata entity) => entity.LogicalName switch
        {
            "pb_project" => new HashSet<string>(["pb_budget", "pb_count"], StringComparer.OrdinalIgnoreCase),
            "systemuser" => new HashSet<string>(["domainname"], StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(),
        };
    }

    private static readonly QueryContext Context =
        new(UserId, Guid.NewGuid(), new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc)) { Security = new FieldSecurity() };

    private static CompiledQuery Compile(QueryModel q, QueryContext? context = null) =>
        new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, context ?? Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static EntityNode Project(params string[] columns)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        foreach (var c in columns) root.Columns.Add(new ColumnNode(c));
        return root;
    }

    private static LinkNode Customer(params string[] columns)
    {
        var link = new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", Alias = "u" };
        foreach (var c in columns) link.Columns.Add(new ColumnNode(c));
        return link;
    }

    private static void Refused(EntityNode root, string attribute, bool aggregate = false)
    {
        var act = () => Compile(new QueryModel { Root = root, Aggregate = aggregate });
        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.FieldReadDenied);
        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain(attribute).And.Contain(UserId.ToString());
    }

    // ---- projection ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Named_unreadable_column_is_marked_for_the_row_check()
    {
        var c = Compile(new QueryModel { Root = Project("pb_name", "pb_budget") });
        c.Columns.Single(x => x.ResultName == "pb_budget").Unreadable.Should().BeTrue();
        c.Columns.Single(x => x.ResultName == "pb_name").Unreadable.Should().BeFalse();
        c.Columns.Single(x => x.ResultName == "pb_projectid").Unreadable.Should().BeFalse();
    }

    [Fact]
    public void All_columns_marks_every_unreadable_column()
    {
        var root = new EntityNode { EntityName = "pb_project", AllColumns = true };
        var c = Compile(new QueryModel { Root = root });
        c.Columns.Where(x => x.Unreadable).Select(x => x.ResultName).Should().BeEquivalentTo(["pb_budget", "pb_count"]);
    }

    [Fact]
    public void Link_scope_with_an_unreadable_column_reads_its_record_id()
    {
        var root = Project("pb_name");
        root.Links.Add(Customer("fullname", "domainname"));
        var c = Compile(new QueryModel { Root = root });
        c.Columns.Single(x => x.ResultName == "u.domainname").Unreadable.Should().BeTrue();
        c.Columns.Single(x => x.ResultName == "u.fullname").Unreadable.Should().BeFalse();
        var id = c.Columns.Single(x => x.Kind == ResultKind.RecordId);
        id.EntityAlias.Should().Be("u");
        Normalize(c.Sql).Should().Contain($"t1.[systemuserid] AS {id.SqlAlias}");
    }

    [Fact]
    public void Scope_without_unreadable_column_reads_no_record_id()
    {
        var root = Project("pb_name", "pb_budget");
        root.Links.Add(Customer("fullname"));
        Compile(new QueryModel { Root = root }).Columns.Should().NotContain(x => x.Kind == ResultKind.RecordId,
            "the root reads its primary key anyway, and the link has no unreadable column");
    }

    /// <summary>The polymorphic <c>ownerid</c> of <c>pb_project</c> is unreadable.</summary>
    private sealed class OwnerSecurity : IQuerySecurity
    {
        public FilterNode? ReadFilter(EntityMetadata entity) => null;

        public IReadOnlySet<string> UnreadableColumns(EntityMetadata entity) =>
            new HashSet<string>(entity.LogicalName == "pb_project" ? ["ownerid"] : [], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unreadable_lookup_keeps_its_type_and_name_companions_for_a_share()
    {
        var context = new QueryContext(UserId, Guid.NewGuid(), DateTime.UtcNow) { Security = new OwnerSecurity() };
        var c = Compile(new QueryModel { Root = Project("pb_name", "ownerid") }, context);
        c.Columns.Single(x => x.Kind == ResultKind.Value && x.ResultName == "ownerid").Unreadable.Should().BeTrue();
        c.Columns.Should().ContainSingle(x => x.Kind == ResultKind.LookupType && x.ResultName == "ownerid");
        c.Columns.Should().ContainSingle(x => x.Kind == ResultKind.LookupName && x.ResultName == "ownerid");

        var distinct = Compile(new QueryModel { Root = Project("pb_name", "ownerid"), Distinct = true }, context);
        distinct.Columns.Should().NotContain(x => x.ResultName == "ownerid" && x.Kind != ResultKind.Value,
            "a distinct query reads no value of the column, nor its companions");
        Normalize(distinct.Sql).Should().NotContain("[ownerid");
    }

    [Fact]
    public void Distinct_query_never_reads_an_unreadable_column()
    {
        var c = Compile(new QueryModel { Root = Project("pb_name", "pb_budget"), Distinct = true });
        var sql = Normalize(c.Sql);
        sql.Should().NotContain("[pb_budget]", "the value would make rows distinct and order them");
        c.Columns.Single(x => x.ResultName == "pb_budget").Unreadable.Should().BeTrue();
    }

    [Fact]
    public void Without_field_restriction_nothing_is_marked()
    {
        var root = new EntityNode { EntityName = "pb_project", AllColumns = true };
        root.Filter.Conditions.Add(new ConditionNode(null, "pb_budget", FilterOperator.NotNull, []));
        var c = Compile(new QueryModel { Root = root }, new QueryContext(UserId, Guid.NewGuid(), DateTime.UtcNow));
        c.Columns.Should().NotContain(x => x.Unreadable || x.Kind == ResultKind.RecordId);
    }

    // ---- refusals -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Condition_on_unreadable_column_is_refused()
    {
        var root = Project("pb_name");
        root.Filter.Conditions.Add(new ConditionNode(null, "pb_budget", FilterOperator.GreaterThan, [10m]));
        Refused(root, "pb_budget");
    }

    [Fact]
    public void Condition_in_nested_filter_is_refused()
    {
        var root = Project("pb_name");
        var or = new FilterNode { Operator = FilterLogic.Or };
        or.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.Equal, ["a"]));
        var inner = new FilterNode();
        inner.Conditions.Add(new ConditionNode(null, "pb_count", FilterOperator.Equal, [1]));
        or.Filters.Add(inner);
        root.Filter.Filters.Add(or);
        Refused(root, "pb_count");
    }

    [Fact]
    public void Condition_on_link_column_through_its_alias_is_refused()
    {
        var root = Project("pb_name");
        root.Links.Add(Customer("fullname"));
        root.Filter.Conditions.Add(new ConditionNode("u", "domainname", FilterOperator.Like, ["%x%"]));
        Refused(root, "domainname");
    }

    [Fact]
    public void Condition_in_link_filter_is_refused()
    {
        var root = Project("pb_name");
        var link = Customer("fullname");
        link.Filter.Conditions.Add(new ConditionNode(null, "domainname", FilterOperator.Equal, ["x"]));
        root.Links.Add(link);
        Refused(root, "domainname");
    }

    [Fact]
    public void Order_on_unreadable_column_is_refused()
    {
        var root = Project("pb_name");
        root.Orders.Add(new OrderNode("pb_budget", Descending: false));
        Refused(root, "pb_budget");

        var linked = Project("pb_name");
        var link = Customer("fullname");
        link.Orders.Add(new OrderNode("domainname", Descending: true));
        linked.Links.Add(link);
        Refused(linked, "domainname");
    }

    [Fact]
    public void Aggregates_on_unreadable_column_are_refused()
    {
        foreach (var column in new[]
                 {
                     new ColumnNode("pb_count", "s", XrmAggregate.Sum), new ColumnNode("pb_count", "a", XrmAggregate.Avg),
                     new ColumnNode("pb_budget", "m", XrmAggregate.Min), new ColumnNode("pb_budget", "x", XrmAggregate.Max),
                     new ColumnNode("pb_count", "c", XrmAggregate.CountColumn), new ColumnNode("pb_count", "g", GroupBy: true),
                 })
        {
            var root = new EntityNode { EntityName = "pb_project" };
            root.Columns.Add(column);
            Refused(root, column.Attribute, aggregate: true);
        }
    }

    [Fact]
    public void Count_rows_and_countcolumn_on_primary_key_are_allowed()
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Columns.Add(new ColumnNode("pb_budget", "n", XrmAggregate.Count)); // count(*): the value is not read
        root.Columns.Add(new ColumnNode("pb_projectid", "ids", XrmAggregate.CountColumn));
        var sql = Normalize(Compile(new QueryModel { Root = root, Aggregate = true }).Sql);
        sql.Should().Contain("COUNT(*)").And.Contain("COUNT(t0.[pb_projectid])").And.NotContain("[pb_budget]");
    }

    [Fact]
    public void Link_join_on_unreadable_column_is_refused()
    {
        var from = Project("pb_name");
        from.Links.Add(new LinkNode { EntityName = "systemuser", From = "domainname", To = "pb_name", Alias = "u" });
        Refused(from, "domainname");

        var to = Project("pb_name");
        to.Links.Add(new LinkNode { EntityName = "systemuser", From = "fullname", To = "pb_count", Alias = "u" });
        Refused(to, "pb_count");
    }
}
