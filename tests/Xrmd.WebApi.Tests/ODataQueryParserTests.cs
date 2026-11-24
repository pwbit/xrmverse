using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Model.Query;
using Xrmd.Query.Model;
using Xrmd.WebApi;

namespace Xrmd.WebApi.Tests;

public class ODataQueryParserTests
{
    private static readonly Guid CustomerId = Guid.Parse("6f1b5c55-2a8e-4b8e-9d55-3c1a1b2c3d4e");

    private static EntityMetadata Entity(string logicalName, string setName, OwnershipTypes ownership, params AttributeMetadata[] attributes)
    {
        var e = new EntityMetadata { LogicalName = logicalName, EntitySetName = setName, OwnershipType = ownership };
        e.SetAttributes(SystemAttributes.Complete(e, attributes));
        return e;
    }

    private static readonly EntityMetadata Project = Entity("pb_project", "pb_projects", OwnershipTypes.UserOwned,
        new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 200 },
        new LookupAttributeMetadata { LogicalName = "pb_customerid", Targets = ["systemuser"] },
        new MoneyAttributeMetadata { LogicalName = "pb_budget" },
        new IntegerAttributeMetadata { LogicalName = "pb_count" },
        new DateTimeAttributeMetadata { LogicalName = "pb_startdate" },
        new PicklistAttributeMetadata { LogicalName = "pb_priority" });

    private static readonly EntityMetadata SystemUser =
        Entity("systemuser", "systemusers", OwnershipTypes.BusinessOwned, new StringAttributeMetadata { LogicalName = "fullname", MaxLength = 200 });

    private static readonly FakeStore Store = new(Project, SystemUser);

    private static QueryModel Parse(string query, int? maxPageSize = null, string? skipToken = null)
    {
        var parser = new ODataQueryParser(new ODataEdmModelBuilder(Store, TestCustomApis.None()), Store);
        return parser.Parse(Project, Query(query), maxPageSize, skipToken);
    }

    /// <summary>A query string such as <c>$filter=a eq 1&amp;$top=2</c> (values unencoded) as a query collection.</summary>
    private static IQueryCollection Query(string query) => new QueryCollection(
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => new StringValues(p.Length > 1 ? p[1] : "")));

    [Fact]
    public void Parse_without_options_selects_all_columns_of_the_entity()
    {
        var model = Parse("");

        model.Root.EntityName.Should().Be("pb_project");
        model.Root.AllColumns.Should().BeTrue();
        model.Root.Filter.Conditions.Should().BeEmpty();
        model.Root.Filter.Filters.Should().BeEmpty();
        model.Top.Should().BeNull();
        model.PageNumber.Should().Be(1);
        model.PagingCookie.Should().BeNull();
        model.ReturnTotalRecordCount.Should().BeFalse();
    }

    [Fact]
    public void Parse_maps_filter_and_or_contains_to_filter_nodes()
    {
        var model = Parse("$filter=pb_count gt 5 and (contains(pb_name,'a') or pb_name eq 'b')");

        var filter = model.Root.Filter;
        filter.Operator.Should().Be(FilterLogic.And);
        filter.Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConditionNode(null, "pb_count", FilterOperator.GreaterThan, [5]));
        var or = filter.Filters.Should().ContainSingle().Subject;
        or.Operator.Should().Be(FilterLogic.Or);
        or.Filters.Should().BeEmpty();
        or.Conditions.Should().HaveCount(2);
        or.Conditions[0].Should().BeEquivalentTo(new ConditionNode(null, "pb_name", FilterOperator.Like, ["%a%"]));
        or.Conditions[1].Should().BeEquivalentTo(new ConditionNode(null, "pb_name", FilterOperator.Equal, ["b"]));
    }

    [Fact]
    public void Parse_flattens_chains_of_the_same_operator_and_maps_comparisons()
    {
        var model = Parse("$filter=pb_count ge 1 and pb_count le 9 and pb_count ne 4 and pb_priority lt 3 and pb_name ne null and pb_startdate eq null");

        var filter = model.Root.Filter;
        filter.Filters.Should().BeEmpty();
        filter.Conditions.Select(c => (c.Attribute, c.Operator)).Should().Equal(
            ("pb_count", FilterOperator.GreaterEqual),
            ("pb_count", FilterOperator.LessEqual),
            ("pb_count", FilterOperator.NotEqual),
            ("pb_priority", FilterOperator.LessThan),
            ("pb_name", FilterOperator.NotNull),
            ("pb_startdate", FilterOperator.Null));
        filter.Conditions[4].Values.Should().BeEmpty();
    }

    [Fact]
    public void Parse_maps_string_functions_and_their_negation()
    {
        var model = Parse("$filter=startswith(pb_name,'A_') or endswith(pb_name,'z') or not contains(pb_name,'%') " +
                          "or not startswith(pb_name,'q') or not endswith(pb_name,'w')");

        var filter = model.Root.Filter;
        filter.Operator.Should().Be(FilterLogic.Or);
        filter.Conditions.Select(c => (c.Operator, c.Values.Single())).Should().Equal(
            (FilterOperator.BeginsWith, "A_"),
            (FilterOperator.EndsWith, "z"),
            (FilterOperator.NotLike, "%[%]%"),
            (FilterOperator.DoesNotBeginWith, "q"),
            (FilterOperator.DoesNotEndWith, "w"));
    }

    [Fact]
    public void Parse_maps_a_comparison_on_the_lookup_value_property_to_the_lookup_attribute()
    {
        var model = Parse($"$filter=_pb_customerid_value eq {CustomerId}");

        model.Root.Filter.Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConditionNode(null, "pb_customerid", FilterOperator.Equal, [CustomerId]));
    }

    [Fact]
    public void Parse_flips_a_comparison_with_the_literal_on_the_left()
    {
        var model = Parse("$filter=5 lt pb_count");

        model.Root.Filter.Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConditionNode(null, "pb_count", FilterOperator.GreaterThan, [5]));
    }

    [Fact]
    public void Parse_maps_select_with_lookup_value_property()
    {
        var model = Parse("$select=pb_name,_pb_customerid_value");

        model.Root.AllColumns.Should().BeFalse();
        model.Root.Columns.Select(c => c.Attribute).Should().Equal("pb_name", "pb_customerid");
    }

    [Fact]
    public void Parse_maps_orderby_top_and_count()
    {
        var model = Parse("$orderby=pb_name desc,pb_count&$top=10&$count=true");

        model.Root.Orders.Should().Equal(new OrderNode("pb_name", true), new OrderNode("pb_count", false));
        model.Top.Should().Be(10);
        model.ReturnTotalRecordCount.Should().BeTrue();
        // Learn ("Order rows by using OData"): $orderby sorts a choice by its integer value (a lookup still by its name).
        model.UseRawOrderBy.Should().BeTrue();
    }

    [Fact]
    public void Parse_uses_max_page_size_as_page_size()
    {
        Parse("", maxPageSize: 5).PageSize.Should().Be(5);
        Parse("").PageSize.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public void Parse_rejects_a_max_page_size_out_of_range(int size)
    {
        var act = () => Parse("", maxPageSize: size);
        act.Should().Throw<XrmdValidationException>().WithMessage("*5000*");
    }

    [Fact]
    public void Parse_rejects_skip()
    {
        var act = () => Parse("$skip=1");
        act.Should().Throw<XrmdValidationException>().WithMessage("The query parameter $skip is not supported");
    }

    [Theory]
    [InlineData("$filter=length(pb_name) eq 3", "length(pb_name) eq 3")]
    [InlineData("$filter=pb_count add 1 eq 3", "pb_count add 1 eq 3")]
    [InlineData("$filter=not (pb_count eq 3)", "not (pb_count eq 3)")]
    [InlineData("$filter=pb_count eq pb_priority", "pb_count eq pb_priority")]
    public void Parse_rejects_unsupported_filter_nodes_naming_the_clause(string query, string clause)
    {
        var act = () => Parse(query);
        act.Should().Throw<XrmdValidationException>().WithMessage($"*{clause}*");
    }

    [Theory]
    [InlineData("$filter=pb_unknown eq 3")]
    [InlineData("$select=pb_unknown")]
    [InlineData("$orderby=pb_unknown")]
    [InlineData("$filter=pb_count eq")]
    [InlineData("$top=abc")]
    [InlineData("$filter=pb_budget gt 1e400")]
    [InlineData("$filter=pb_budget gt INF")]
    [InlineData("$filter=pb_budget gt NaN")]
    [InlineData("$select=pb_name($select=x)")]
    public void Parse_translates_odata_errors_to_validation_errors(string query)
    {
        var act = () => Parse(query);
        act.Should().Throw<XrmdValidationException>();
    }

    [Theory]
    [InlineData("$search=x")]
    [InlineData("savedQuery=x")]
    public void Parse_rejects_options_not_supported_yet(string query)
    {
        var act = () => Parse(query);
        act.Should().Throw<XrmdValidationException>().WithMessage("*not supported*");
    }

    [Fact]
    public void Parse_reads_page_number_and_paging_cookie_from_the_skip_token()
    {
        const string cookie = "<cookie page=\"1\"><pb_projectid last=\"{AAAAAAAA-0000-0000-0000-000000000001}\" first=\"{AAAAAAAA-0000-0000-0000-000000000000}\" /></cookie>";
        var token = ODataQueryParser.FormatSkipToken(2, cookie);
        token.Should().Be("<cookie pagenumber=\"2\" pagingcookie=\"&lt;cookie page=&quot;1&quot;&gt;&lt;pb_projectid last=&quot;{AAAAAAAA-0000-0000-0000-000000000001}&quot; first=&quot;{AAAAAAAA-0000-0000-0000-000000000000}&quot; /&gt;&lt;/cookie&gt;\" istracking=\"False\" />");

        var model = Parse("", maxPageSize: 5, skipToken: token);

        model.PageNumber.Should().Be(2);
        model.PagingCookie.Should().Be(cookie);
        model.PageSize.Should().Be(5);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<cookie pagingcookie=\"x\" />")]
    [InlineData("<cookie pagenumber=\"0\" />")]
    [InlineData("<other pagenumber=\"2\" />")]
    [InlineData("<cookie pagenumber=\"2147483647\" />")]
    [InlineData("<cookie pagenumber=\"1000001\" />")]
    [InlineData("<!DOCTYPE cookie [<!ENTITY x \"y\">]><cookie pagenumber=\"2\" pagingcookie=\"&x;\" />")]
    public void Parse_rejects_a_malformed_skip_token(string token)
    {
        var act = () => Parse("", skipToken: token);
        act.Should().Throw<XrmdValidationException>().WithMessage("*$skiptoken*");
    }
    // ---- $apply ------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_maps_apply_groupby_with_aggregate_to_an_aggregate_query()
    {
        var model = Parse("$apply=groupby((pb_priority),aggregate(pb_count with sum as total))");

        model.Aggregate.Should().BeTrue();
        model.Root.AllColumns.Should().BeFalse();
        model.Root.Columns.Should().Equal(
            new ColumnNode("pb_priority", "pb_priority", GroupBy: true),
            new ColumnNode("pb_count", "total", XrmAggregate.Sum));
    }

    [Fact]
    public void Parse_maps_apply_aggregate_methods_and_count()
    {
        var model = Parse("$apply=aggregate($count as c,pb_count with countdistinct as d,pb_count with average as a," +
                          "pb_budget with min as lo,pb_budget with max as hi)");

        model.Aggregate.Should().BeTrue();
        model.Root.Columns.Should().Equal(
            new ColumnNode("pb_projectid", "c", XrmAggregate.Count),
            new ColumnNode("pb_count", "d", XrmAggregate.CountColumn, Distinct: true),
            new ColumnNode("pb_count", "a", XrmAggregate.Avg),
            new ColumnNode("pb_budget", "lo", XrmAggregate.Min),
            new ColumnNode("pb_budget", "hi", XrmAggregate.Max));
    }

    [Fact]
    public void Parse_maps_apply_groupby_on_a_lookup_value_property_to_the_lookup()
    {
        var model = Parse("$apply=groupby((_pb_customerid_value),aggregate($count as c))");

        model.Root.Columns[0].Should().Be(new ColumnNode("pb_customerid", "_pb_customerid_value", GroupBy: true));
    }

    [Fact]
    public void Parse_maps_apply_groupby_without_aggregate()
    {
        var model = Parse("$apply=groupby((pb_priority,pb_name))");

        model.Root.Columns.Should().Equal(
            new ColumnNode("pb_priority", "pb_priority", GroupBy: true),
            new ColumnNode("pb_name", "pb_name", GroupBy: true));
    }

    [Fact]
    public void Parse_maps_an_apply_filter_before_the_aggregate_to_the_filter()
    {
        var model = Parse("$apply=filter(pb_count gt 5)/aggregate($count as c)");

        model.Root.Filter.Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConditionNode(null, "pb_count", FilterOperator.GreaterThan, [5]));
        model.Root.Columns.Should().Equal(new ColumnNode("pb_projectid", "c", XrmAggregate.Count));
    }

    [Fact]
    public void Parse_maps_orderby_on_apply_aliases()
    {
        var model = Parse("$apply=groupby((pb_priority),aggregate(pb_count with sum as total))&$orderby=total desc,pb_priority&$top=2");

        model.Root.Orders.Should().Equal(new OrderNode("total", true, "total"), new OrderNode("pb_priority", false, "pb_priority"));
        model.Top.Should().Be(2);
    }

    [Theory]
    [InlineData("$apply=groupby((pb_priority),aggregate($count as c))&$select=pb_name")]
    [InlineData("$apply=groupby((pb_priority),aggregate($count as c))&$expand=pb_customerid")]
    [InlineData("$apply=groupby((pb_priority),aggregate($count as c))&$orderby=pb_name")]
    [InlineData("$apply=groupby((pb_priority),aggregate($count as c))&$filter=pb_count gt 1")]
    [InlineData("$apply=filter(pb_count gt 1)")]
    [InlineData("$apply=groupby((pb_priority))/aggregate($count as c)")]
    [InlineData("$apply=aggregate(pb_count with sum as)")]
    [InlineData("$apply=aggregate(pb_unknown with sum as x)")]
    public void Parse_rejects_unsupported_apply(string query)
    {
        var act = () => Parse(query);
        act.Should().Throw<XrmdValidationException>();
    }

    // ---- $expand -----------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_maps_expand_with_select_to_a_left_outer_link()
    {
        var model = Parse("$select=pb_name&$expand=pb_customerid($select=fullname)");

        model.Root.Columns.Select(c => c.Attribute).Should().Equal("pb_name");
        var link = model.Root.Links.Should().ContainSingle().Subject;
        link.EntityName.Should().Be("systemuser");
        link.From.Should().Be("systemuserid");
        link.To.Should().Be("pb_customerid");
        link.JoinType.Should().Be(JoinKind.LeftOuter);
        link.Alias.Should().Be("pb_customerid");
        link.AllColumns.Should().BeFalse();
        link.Columns.Select(c => c.Attribute).Should().BeEquivalentTo("systemuserid", "fullname");
    }

    [Fact]
    public void Parse_maps_expand_without_select_to_all_columns_of_the_target()
    {
        var model = Parse("$expand=pb_customerid");

        model.Root.AllColumns.Should().BeTrue();
        var link = model.Root.Links.Should().ContainSingle().Subject;
        link.AllColumns.Should().BeTrue();
        link.Alias.Should().Be("pb_customerid");
    }

    [Theory]
    [InlineData("$expand=pb_customerid($filter=fullname eq 'x')", "*$expand*")]
    [InlineData("$expand=pb_customerid($top=1)", "*$expand*")]
    [InlineData("$expand=pb_customerid($orderby=fullname)", "*$expand*")]
    [InlineData("$expand=ownerid", "*not supported yet*")]
    [InlineData("$expand=pb_unknown", "*")]
    [InlineData("$expand=pb_customerid($select=nope)", "*")]
    public void Parse_rejects_unsupported_expand(string query, string message)
    {
        var act = () => Parse(query);
        act.Should().Throw<XrmdValidationException>().WithMessage(message);
    }

    [Fact]
    public void ParseSingle_reads_select_and_expand_and_filters_on_the_key()
    {
        var parser = new ODataQueryParser(new ODataEdmModelBuilder(Store, TestCustomApis.None()), Store);
        var model = parser.ParseSingle(Project, Query("$select=pb_name&$expand=pb_customerid($select=fullname)"), CustomerId);

        model.Top.Should().Be(1);
        model.Root.Filter.Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConditionNode(null, "pb_projectid", FilterOperator.Equal, [CustomerId]));
        model.Root.Links.Should().ContainSingle().Which.Alias.Should().Be("pb_customerid");
    }

    // ---- fetchXml ----------------------------------------------------------------------------------------------

    [Fact]
    public void ParseFetchXml_parses_the_fetch_of_the_entity_set()
    {
        var parser = new ODataQueryParser(new ODataEdmModelBuilder(Store, TestCustomApis.None()), Store);
        ODataQueryParser.IsFetchXml(Query("fetchXml=<fetch/>")).Should().BeTrue();
        ODataQueryParser.IsFetchXml(Query("$select=pb_name")).Should().BeFalse();

        var model = parser.ParseFetchXml(Project, Query("fetchXml=<fetch count='5'><entity name='pb_project'><attribute name='pb_name'/></entity></fetch>"));

        model.Root.EntityName.Should().Be("pb_project");
        model.PageSize.Should().Be(5);
    }

    [Theory]
    [InlineData("fetchXml=<fetch><entity name='systemuser'/></fetch>")]
    [InlineData("fetchXml=<fetch><entity name='pb_project'/></fetch>&$select=pb_name")]
    [InlineData("fetchXml=<fetch><entity name='pb_project'/></fetch>&$top=1")]
    [InlineData("fetchXml=not xml")]
    public void ParseFetchXml_rejects_another_entity_other_options_and_invalid_fetch(string query)
    {
        var parser = new ODataQueryParser(new ODataEdmModelBuilder(Store, TestCustomApis.None()), Store);
        var act = () => parser.ParseFetchXml(Project, Query(query));
        act.Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Parse_rejects_fetchXml_combined_with_odata_options()
    {
        var act = () => Parse("fetchXml=<fetch/>&$top=1");
        act.Should().Throw<XrmdValidationException>();
    }

    // ---- $expand of a polymorphic lookup (plan 3.2) ----

    private static LookupAttributeMetadata TwoTargets(string name, bool customer)
    {
        var lookup = new LookupAttributeMetadata { LogicalName = name, SchemaName = name, Targets = ["pb_alpha", "pb_beta"] };
        if (customer)
            typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.SetValue(lookup, AttributeTypeCode.Customer);
        return lookup;
    }

    private static OneToManyRelationshipMetadata Named(string column, string target, string navigation) => new()
    {
        SchemaName = $"pb_holder_{column}_{target}", ReferencingEntity = "pb_holder", ReferencingAttribute = column,
        ReferencedEntity = target, ReferencingEntityNavigationPropertyName = navigation,
    };

    private static QueryModel ParseHolder(string query)
    {
        var holder = Entity("pb_holder", "pb_holders", OwnershipTypes.UserOwned,
            TwoTargets("pb_handler", customer: false), TwoTargets("pb_client", customer: true), TwoTargets("pb_plain", customer: false));
        var alpha = Entity("pb_alpha", "pb_alphas", OwnershipTypes.UserOwned, new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 50 });
        var beta = Entity("pb_beta", "pb_betas", OwnershipTypes.UserOwned, new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 50 });
        var store = new FakeStore(holder, alpha, beta);
        store.OneToMany.AddRange([Named("pb_handler", "pb_alpha", "pb_handler_alpha"), Named("pb_handler", "pb_beta", "pb_handler_beta")]);
        return new ODataQueryParser(new ODataEdmModelBuilder(store, TestCustomApis.None()), store).Parse(holder, Query(query), null, null);
    }

    [Fact]
    public void Expand_of_a_per_target_navigation_resolves_its_target()
    {
        var model = ParseHolder("$expand=pb_handler_beta($select=pb_name)");

        var link = model.Root.Links.Should().ContainSingle().Which;
        link.EntityName.Should().Be("pb_beta");
        link.To.Should().Be("pb_handler");
        link.Alias.Should().Be("pb_handler_beta");
    }

    [Theory]
    [InlineData("pb_client_pb_alpha")] // a Customer column keeps its refusal
    [InlineData("pb_plain")] // a polymorphic lookup with no navigation property per target
    public void Expand_of_other_polymorphic_lookups_is_refused(string navigation)
    {
        var act = () => ParseHolder($"$expand={navigation}");

        act.Should().Throw<XrmdValidationException>().Which.Message.Should().Contain(navigation);
    }
    // ---- nested $expand of single-valued navigation properties (issue #190) ----

    [Fact]
    public void Nested_expand_becomes_a_left_outer_link_of_the_link()
    {
        var model = Parse("$select=pb_name&$expand=pb_customerid($select=fullname;$expand=createdby($select=fullname))");

        var link = model.Root.Links.Should().ContainSingle().Subject;
        link.Alias.Should().Be("pb_customerid");
        link.NavigationProperty.Should().Be("pb_customerid");
        link.Columns.Select(c => c.Attribute).Should().BeEquivalentTo("systemuserid", "fullname");
        var nested = link.Links.Should().ContainSingle().Subject;
        nested.EntityName.Should().Be("systemuser");
        nested.From.Should().Be("systemuserid");
        nested.To.Should().Be("createdby");
        nested.JoinType.Should().Be(JoinKind.LeftOuter);
        nested.Alias.Should().Be("pb_customerid_createdby");
        nested.NavigationProperty.Should().Be("createdby");
        nested.Columns.Select(c => c.Attribute).Should().BeEquivalentTo("systemuserid", "fullname");
    }

    [Fact]
    public void Nested_expand_without_select_reads_all_columns_and_goes_three_levels_deep()
    {
        var model = Parse("$expand=pb_customerid($expand=createdby($expand=modifiedby($select=fullname)))");

        var first = model.Root.Links.Should().ContainSingle().Subject;
        first.AllColumns.Should().BeTrue();
        var second = first.Links.Should().ContainSingle().Subject;
        second.AllColumns.Should().BeTrue();
        var third = second.Links.Should().ContainSingle().Subject;
        third.Alias.Should().Be("pb_customerid_createdby_modifiedby");
        third.NavigationProperty.Should().Be("modifiedby");
    }

    [Fact]
    public void Nested_expand_alias_never_reuses_a_taken_alias()
    {
        var project = Entity("pb_project", "pb_projects", OwnershipTypes.UserOwned,
            new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 200 },
            new LookupAttributeMetadata { LogicalName = "pb_customerid", Targets = ["systemuser"] },
            new LookupAttributeMetadata { LogicalName = "pb_customerid_createdby", Targets = ["systemuser"] });
        var store = new FakeStore(project, SystemUser);
        var parser = new ODataQueryParser(new ODataEdmModelBuilder(store, TestCustomApis.None()), store);

        var model = parser.Parse(project, Query("$expand=pb_customerid_createdby($select=fullname),pb_customerid($expand=createdby($select=fullname))"), null, null);

        model.Root.Links.Select(l => l.Alias).Should().Equal("pb_customerid_createdby", "pb_customerid");
        var nested = model.Root.Links[1].Links.Should().ContainSingle().Subject;
        nested.Alias.Should().Be("pb_customerid_createdby2");
        nested.NavigationProperty.Should().Be("createdby");
    }

    [Fact]
    public void More_than_15_expands_over_all_levels_are_refused()
    {
        string Chain(int depth) => depth == 1 ? "createdby($select=fullname)" : $"createdby($expand={Chain(depth - 1)})";

        Parse($"$expand=pb_customerid($expand={Chain(14)})").Root.Links.Should().ContainSingle();
        var act = () => Parse($"$expand=pb_customerid($expand={Chain(15)})");

        act.Should().Throw<XrmdValidationException>().WithMessage("*at most 15*");
    }

    [Theory]
    [InlineData("$expand=pb_customerid($select=fullname;$expand=createdby($filter=fullname eq 'x'))", "*'createdby'*")]
    [InlineData("$expand=pb_customerid($expand=createdby($top=1))", "*'createdby'*")]
    [InlineData("$expand=pb_customerid($expand=createdby($orderby=fullname))", "*'createdby'*")]
    [InlineData("$expand=pb_customerid($expand=createdby/$ref)", "*'pb_customerid'*")]
    [InlineData("$expand=pb_customerid($expand=pb_unknown)", "*pb_unknown*")]
    public void Nested_expand_keeps_refusing_what_is_not_supported(string query, string message)
    {
        var act = () => Parse(query);
        act.Should().Throw<XrmdValidationException>().WithMessage(message);
    }
}
