using System.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Query;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Sql;

namespace Xrmd.Integration.Tests;

/// <summary>RetrieveMultiple through <see cref="OrganizationService"/> (QueryExpression, FetchExpression) on SQL Server.</summary>
[Collection("xrmd")]
public sealed class RetrieveMultipleTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private const string Bulk = "pb_bulk";

    private IMetadataStore _store = null!;
    private IDbConnectionFactory _factory = null!;
    private OrganizationService _svc = null!;
    private Guid _adminId;
    private readonly string _tag = "RM" + Guid.NewGuid().ToString("N");
    private readonly List<Guid> _ids = [];

    public async Task InitializeAsync()
    {
        _store = fx.App.Services.GetRequiredService<IMetadataStore>();
        _factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await TestEntities.EnsureProjectAsync(_store);
        _svc = NewService(fx.AdminObjectId);

        await using (var conn = await _factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid"))
        {
            cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
            _adminId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        for (var i = 1; i <= 12; i++)
            _ids.Add(await _svc.CreateAsync(new Entity(Project)
            {
                ["pb_name"] = $"{_tag}-{i:00}",
                ["pb_count"] = i,
                ["pb_priority"] = new OptionSetValue(i % 3 + 1),
                ["pb_customerid"] = new EntityReference("systemuser", _adminId),
            }));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OrganizationService NewService(Guid objectId) => TestServices.NewOrganizationService(fx.App.Services, objectId);

    private QueryExpression TaggedQuery(params string[] columns)
    {
        var q = new QueryExpression(Project) { ColumnSet = new ColumnSet(columns) };
        q.Criteria.AddCondition("pb_name", ConditionOperator.Like, _tag + "%");
        return q;
    }

    [Fact]
    public async Task QueryExpression_filters_and_orders()
    {
        var q = TaggedQuery("pb_name", "pb_count");
        q.Criteria.AddCondition("pb_count", ConditionOperator.GreaterThan, 6);
        q.AddOrder("pb_count", OrderType.Descending);

        var result = await _svc.RetrieveMultipleAsync(q);

        result.EntityName.Should().Be(Project);
        result.Entities.Should().HaveCount(6);
        result.Entities[0]["pb_count"].Should().Be(12);
        result.Entities[0].Id.Should().Be(_ids[11]);
        result.Entities[0].LogicalName.Should().Be(Project);
        result.Entities.Select(e => (int)e["pb_count"]).Should().BeInDescendingOrder();
        result.MoreRecords.Should().BeFalse();
        result.TotalRecordCount.Should().Be(-1);
    }

    [Fact]
    public void Sync_RetrieveMultiple_works()
    {
        _svc.RetrieveMultiple(TaggedQuery()).Entities.Should().HaveCount(12);
    }

    [Fact]
    public async Task FetchExpression_with_link_returns_aliased_values()
    {
        var fetch = $"""
            <fetch>
              <entity name="{Project}">
                <attribute name="pb_name" />
                <attribute name="pb_customerid" />
                <filter><condition attribute="pb_name" operator="eq" value="{_tag}-03" /></filter>
                <link-entity name="systemuser" from="systemuserid" to="pb_customerid" alias="u">
                  <attribute name="fullname" />
                </link-entity>
              </entity>
            </fetch>
            """;

        var result = await _svc.RetrieveMultipleAsync(new FetchExpression(fetch));

        var e = result.Entities.Should().ContainSingle().Subject;
        e.Id.Should().Be(_ids[2]);
        e["pb_name"].Should().Be($"{_tag}-03");
        e.GetAttributeValue<EntityReference>("pb_customerid").Should().Be(new EntityReference("systemuser", _adminId));
        var aliased = e.GetAttributeValue<AliasedValue>("u.fullname");
        aliased.Value.Should().Be(XrmdFixture.AdminName);
        aliased.EntityLogicalName.Should().Be("systemuser");
        aliased.AttributeLogicalName.Should().Be("fullname");
    }

    [Fact]
    public async Task Results_carry_formatted_values_and_lookup_names()
    {
        var q = TaggedQuery("pb_name", "pb_count", "pb_priority", "pb_customerid", "ownerid");
        q.Criteria.AddCondition("pb_count", ConditionOperator.Equal, 4); // priority 4 % 3 + 1 = 2
        var link = q.AddLink("systemuser", "pb_customerid", "systemuserid");
        link.EntityAlias = "u";
        link.Columns = new ColumnSet("fullname", "businessunitid");

        var e = (await _svc.RetrieveMultipleAsync(q)).Entities.Should().ContainSingle().Subject;

        e.FormattedValues["pb_priority"].Should().Be("Medium");
        e.FormattedValues["pb_count"].Should().Be("4");
        e.GetAttributeValue<EntityReference>("pb_customerid").Name.Should().Be(XrmdFixture.AdminName);
        e.FormattedValues["pb_customerid"].Should().Be(XrmdFixture.AdminName);
        var owner = e.GetAttributeValue<EntityReference>("ownerid");
        owner.Id.Should().Be(_adminId);
        owner.Name.Should().Be(XrmdFixture.AdminName);
        e.FormattedValues["ownerid"].Should().Be(XrmdFixture.AdminName);
        e.FormattedValues.Should().NotContainKey("pb_name");
        // link columns: keyed by the result name; the lookup name sits on the EntityReference inside the AliasedValue.
        var bu = (EntityReference)e.GetAttributeValue<AliasedValue>("u.businessunitid").Value;
        bu.Name.Should().NotBeNullOrEmpty();
        e.FormattedValues["u.businessunitid"].Should().Be(bu.Name);
        e.FormattedValues.Should().NotContainKey("u.fullname");
    }

    [Fact]
    public async Task Arithmetic_overflow_is_InvalidArgument()
    {
        const string big = "pb_bignum";
        if (_store.GetEntity(big) is null)
        {
            var entity = new EntityMetadata { LogicalName = big, SchemaName = "pb_BigNum", OwnershipType = OwnershipTypes.OrganizationOwned };
            entity.SetAttributes([new BigIntAttributeMetadata { LogicalName = "pb_value", SchemaName = "pb_Value" }]);
            await _store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });
        }
        var tag = "OV" + Guid.NewGuid().ToString("N");
        await _svc.CreateAsync(new Entity(big) { ["pb_name"] = tag, ["pb_value"] = long.MaxValue - 1 });
        await _svc.CreateAsync(new Entity(big) { ["pb_name"] = tag, ["pb_value"] = long.MaxValue - 1 });
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{big}">
                <attribute name="pb_value" alias="s" aggregate="sum" />
                <filter><condition attribute="pb_name" operator="eq" value="{tag}" /></filter>
              </entity>
            </fetch>
            """;

        var act = () => _svc.RetrieveMultipleAsync(new FetchExpression(fetch));

        (await act.Should().ThrowAsync<XrmdFaultException>()).Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400
            && e.Message == QueryExecutor.ArithmeticOverflowMessage);
    }

    [Fact]
    public async Task Paging_with_cookie_walks_all_pages()
    {
        var q = TaggedQuery("pb_name");
        q.PageInfo = new PagingInfo { Count = 5, PageNumber = 1 };
        var seen = new List<Guid>();
        var counts = new List<int>();
        var more = new List<bool>();

        while (true)
        {
            var page = await _svc.RetrieveMultipleAsync(q);
            counts.Add(page.Entities.Count);
            more.Add(page.MoreRecords);
            seen.AddRange(page.Entities.Select(e => e.Id));
            page.PagingCookie.Should().StartWith($"<cookie page=\"{q.PageInfo.PageNumber}\"><pb_projectid last=\"");
            if (!page.MoreRecords) break;
            q.PageInfo.PageNumber++;
            q.PageInfo.PagingCookie = page.PagingCookie;
        }

        counts.Should().Equal(5, 5, 2);
        more.Should().Equal(true, true, false);
        seen.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(_ids);
    }

    [Fact]
    public async Task Paging_cookie_holds_first_and_last_returned_ids()
    {
        var q = TaggedQuery();
        q.PageInfo = new PagingInfo { Count = 5, PageNumber = 1 };

        var page = await _svc.RetrieveMultipleAsync(q);

        var cookie = Xrmd.Query.FetchXml.PagingCookie.TryParse(page.PagingCookie)!;
        cookie.Page.Should().Be(1);
        cookie.PrimaryKey.Should().Be("pb_projectid");
        cookie.First.Should().Be(page.Entities[0].Id);
        cookie.Last.Should().Be(page.Entities[4].Id);
    }

    [Fact]
    public async Task Paging_past_last_page_is_empty()
    {
        var q = TaggedQuery();
        q.PageInfo = new PagingInfo { Count = 5, PageNumber = 10 };

        var page = await _svc.RetrieveMultipleAsync(q);

        page.Entities.Should().BeEmpty();
        page.MoreRecords.Should().BeFalse();
    }

    [Fact]
    public async Task Tampered_cookie_does_not_fail()
    {
        var plain = TaggedQuery();
        plain.PageInfo = new PagingInfo { Count = 5, PageNumber = 2 };
        var tampered = TaggedQuery();
        tampered.PageInfo = new PagingInfo { Count = 5, PageNumber = 2, PagingCookie = "garbage" };

        var expected = await _svc.RetrieveMultipleAsync(plain);
        var actual = await _svc.RetrieveMultipleAsync(tampered);

        actual.Entities.Select(e => e.Id).Should().Equal(expected.Entities.Select(e => e.Id));
        actual.Entities.Should().HaveCount(5);
        actual.MoreRecords.Should().BeTrue();
    }

    [Fact]
    public async Task Total_record_count()
    {
        var q = TaggedQuery();
        q.PageInfo = new PagingInfo { Count = 5, PageNumber = 1, ReturnTotalRecordCount = true };

        var page = await _svc.RetrieveMultipleAsync(q);

        page.Entities.Should().HaveCount(5);
        page.TotalRecordCount.Should().Be(12);
        page.TotalRecordCountLimitExceeded.Should().BeFalse();
        page.Entities[0].Attributes.Keys.Should().NotContain("totalrecordcount");
    }

    [Fact]
    public async Task Total_record_count_with_keyset_and_past_last_page()
    {
        var q = TaggedQuery();
        q.PageInfo = new PagingInfo { Count = 5, PageNumber = 1, ReturnTotalRecordCount = true };
        var first = await _svc.RetrieveMultipleAsync(q);
        q.PageInfo.PageNumber = 2;
        q.PageInfo.PagingCookie = first.PagingCookie;

        (await _svc.RetrieveMultipleAsync(q)).TotalRecordCount.Should().Be(12); // keyset: CountSql

        q.PageInfo.PageNumber = 10;
        q.PageInfo.PagingCookie = null;
        var past = await _svc.RetrieveMultipleAsync(q);
        past.Entities.Should().BeEmpty();
        past.TotalRecordCount.Should().Be(12);
    }

    [Fact]
    public async Task Aggregate_sum_groupby()
    {
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{Project}">
                <attribute name="pb_priority" alias="prio" groupby="true" />
                <attribute name="pb_count" alias="total" aggregate="sum" />
                <attribute name="pb_projectid" alias="n" aggregate="count" />
                <attribute name="pb_count" alias="average" aggregate="avg" />
                <attribute name="pb_count" alias="highest" aggregate="max" />
                <filter><condition attribute="pb_name" operator="like" value="{_tag}%" /></filter>
                <order alias="prio" />
              </entity>
            </fetch>
            """;

        var result = await _svc.RetrieveMultipleAsync(new FetchExpression(fetch));

        result.Entities.Should().HaveCount(3);
        // priority i % 3 + 1: 1 ← 3,6,9,12; 2 ← 1,4,7,10; 3 ← 2,5,8,11
        var byPriority = result.Entities.ToDictionary(
            e => ((OptionSetValue)e.GetAttributeValue<AliasedValue>("prio").Value).Value);
        byPriority.Keys.Should().Equal(1, 2, 3);
        byPriority[1].GetAttributeValue<AliasedValue>("total").Value.Should().Be(30);
        byPriority[2].GetAttributeValue<AliasedValue>("total").Value.Should().Be(22);
        byPriority[3].GetAttributeValue<AliasedValue>("total").Value.Should().Be(26);
        byPriority[1].GetAttributeValue<AliasedValue>("n").Value.Should().Be(4);
        byPriority[1].GetAttributeValue<AliasedValue>("average").Value.Should().BeOfType<decimal>().Which.Should().Be(7.5m);
        byPriority[1].GetAttributeValue<AliasedValue>("highest").Value.Should().Be(12);
        var total = byPriority[1].GetAttributeValue<AliasedValue>("total");
        total.EntityLogicalName.Should().Be(Project);
        total.AttributeLogicalName.Should().Be("pb_count");
        result.PagingCookie.Should().BeNull();
    }

    [Fact]
    public async Task Aggregate_over_money_returns_Money()
    {
        var tag = "MN" + Guid.NewGuid().ToString("N");
        await _svc.CreateAsync(new Entity(Project) { ["pb_name"] = tag + "a", ["pb_budget"] = new Money(10.25m) });
        await _svc.CreateAsync(new Entity(Project) { ["pb_name"] = tag + "b", ["pb_budget"] = new Money(20m) });
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{Project}">
                <attribute name="pb_budget" alias="s" aggregate="sum" />
                <attribute name="pb_budget" alias="a" aggregate="avg" />
                <attribute name="pb_budget" alias="c" aggregate="countcolumn" />
                <filter><condition attribute="pb_name" operator="like" value="{tag}%" /></filter>
              </entity>
            </fetch>
            """;

        var e = (await _svc.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Should().ContainSingle().Subject;

        e.GetAttributeValue<AliasedValue>("s").Value.Should().Be(new Money(30.25m));
        e.GetAttributeValue<AliasedValue>("a").Value.Should().BeOfType<Money>().Which.Value.Should().Be(15.125m);
        e.GetAttributeValue<AliasedValue>("c").Value.Should().Be(2);
    }

    [Fact]
    public async Task Day_boundaries_for_on()
    {
        var tag = "DB" + Guid.NewGuid().ToString("N");
        var late = await _svc.CreateAsync(new Entity(Project)
        {
            ["pb_name"] = tag + "late",
            ["pb_startdate"] = new DateTime(2026, 3, 10, 23, 59, 59, DateTimeKind.Utc),
        });
        await _svc.CreateAsync(new Entity(Project)
        {
            ["pb_name"] = tag + "midnight",
            ["pb_startdate"] = new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc),
        });
        var q = new QueryExpression(Project) { ColumnSet = new ColumnSet("pb_startdate") };
        q.Criteria.AddCondition("pb_name", ConditionOperator.Like, tag + "%");
        q.Criteria.AddCondition("pb_startdate", ConditionOperator.On, new DateTime(2026, 3, 10));

        var result = await _svc.RetrieveMultipleAsync(q);

        var e = result.Entities.Should().ContainSingle().Subject;
        e.Id.Should().Be(late);
        e.GetAttributeValue<DateTime>("pb_startdate").Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Unknown_attribute_is_InvalidArgument()
    {
        var act = () => _svc.RetrieveMultipleAsync(TaggedQuery("pb_nope"));

        (await act.Should().ThrowAsync<XrmdFaultException>())
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400 && e.Message.Contains("pb_nope"));
    }

    [Fact]
    public async Task Malformed_fetch_is_InvalidArgument()
    {
        var act = () => _svc.RetrieveMultipleAsync(new FetchExpression("<fetch"));

        (await act.Should().ThrowAsync<XrmdFaultException>()).Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400);
    }

    [Fact]
    public async Task Non_member_gets_403()
    {
        var act = () => NewService(Guid.NewGuid()).RetrieveMultipleAsync(TaggedQuery());

        (await act.Should().ThrowAsync<XrmdFaultException>()).Where(e => e.ErrorCode == ErrorCodes.NotMemberOfOrganization && e.HttpStatus == 403);
    }

    [Fact]
    public async Task QueryByAttribute_matches_values()
    {
        var q = new QueryByAttribute(Project) { ColumnSet = new ColumnSet("pb_count") };
        q.AddAttributeValue("pb_name", $"{_tag}-07");

        var result = await _svc.RetrieveMultipleAsync(q);

        result.Entities.Should().ContainSingle().Which["pb_count"].Should().Be(7);
    }

    [Fact]
    public async Task Aggregate_over_limit()
    {
        await EnsureBulkAsync();
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{Bulk}"><attribute name="{Bulk}id" alias="n" aggregate="count" /></entity>
            </fetch>
            """;

        var act = () => _svc.RetrieveMultipleAsync(new FetchExpression(fetch));

        (await act.Should().ThrowAsync<XrmdFaultException>()).Where(e =>
            e.ErrorCode == ErrorCodes.AggregateQueryRecordLimitExceeded && e.ErrorCode == unchecked((int)0x8004E023)
            && e.HttpStatus == 400 && e.Message == "AggregateQueryRecordLimit exceeded. Cannot perform this operation.");
    }

    // ---- final review: distinct, limits ------------------------------------------------------------------------

    /// <summary>Two records with the same name: a distinct query on the name returns one row, without id or cookie.</summary>
    private async Task<string> CreateDuplicateNamesAsync()
    {
        var name = "DUP" + Guid.NewGuid().ToString("N");
        await _svc.CreateAsync(new Entity(Project) { ["pb_name"] = name, ["pb_count"] = 1 });
        await _svc.CreateAsync(new Entity(Project) { ["pb_name"] = name, ["pb_count"] = 2 });
        return name;
    }

    [Fact]
    public async Task Distinct_QueryExpression_on_root_columns_merges_rows()
    {
        var name = await CreateDuplicateNamesAsync();
        var q = new QueryExpression(Project) { ColumnSet = new ColumnSet("pb_name"), Distinct = true };
        q.Criteria.AddCondition("pb_name", ConditionOperator.Equal, name);

        var result = await _svc.RetrieveMultipleAsync(q);

        result.Entities.Should().ContainSingle();
        result.Entities[0]["pb_name"].Should().Be(name);
        result.Entities[0].Id.Should().Be(Guid.Empty);
        result.PagingCookie.Should().BeNull();
        result.MoreRecords.Should().BeFalse();
    }

    [Fact]
    public async Task Distinct_FetchXml_on_root_columns_merges_rows()
    {
        var name = await CreateDuplicateNamesAsync();
        var result = await _svc.RetrieveMultipleAsync(new FetchExpression(
            $"<fetch distinct='true'><entity name='{Project}'><attribute name='pb_name'/>" +
            $"<filter><condition attribute='pb_name' operator='eq' value='{name}'/></filter></entity></fetch>"));

        result.Entities.Should().ContainSingle();
        result.Entities[0]["pb_name"].Should().Be(name);
    }

    [Fact]
    public async Task Distinct_with_the_primary_key_requested_keeps_every_record()
    {
        var name = await CreateDuplicateNamesAsync();
        var q = new QueryExpression(Project) { ColumnSet = new ColumnSet("pb_name", "pb_projectid"), Distinct = true };
        q.Criteria.AddCondition("pb_name", ConditionOperator.Equal, name);

        var result = await _svc.RetrieveMultipleAsync(q);

        result.Entities.Should().HaveCount(2).And.OnlyContain(e => e.Id != Guid.Empty);
    }

    [Fact]
    public async Task Deeply_nested_fetch_filters_are_InvalidArgument()
    {
        var fetch = $"<fetch><entity name='{Project}'>{string.Concat(Enumerable.Repeat("<filter>", 10_000))}" +
                    $"<condition attribute='pb_name' operator='eq' value='x'/>{string.Concat(Enumerable.Repeat("</filter>", 10_000))}</entity></fetch>";

        var act = () => _svc.RetrieveMultipleAsync(new FetchExpression(fetch));

        (await act.Should().ThrowAsync<XrmdFaultException>())
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400 && e.Message.Contains("nested more than 32"));
    }

    [Fact]
    public async Task Too_many_parameters_are_InvalidArgument()
    {
        var q = TaggedQuery("pb_name");
        var or = q.Criteria.AddFilter(LogicalOperator.Or);
        for (var i = 0; i < 2100; i++) or.AddCondition("pb_count", ConditionOperator.Equal, i);

        var act = () => _svc.RetrieveMultipleAsync(q);

        (await act.Should().ThrowAsync<XrmdFaultException>())
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400 && e.Message.Contains("2000"));
    }

    [Fact]
    public async Task Link_condition_on_a_later_link_is_InvalidArgument()
    {
        var q = TaggedQuery("pb_name");
        var a = q.AddLink("systemuser", "pb_customerid", "systemuserid", JoinOperator.LeftOuter);
        a.EntityAlias = "a";
        a.LinkCriteria.AddCondition("b", "fullname", ConditionOperator.NotNull);
        q.AddLink("systemuser", "createdby", "systemuserid", JoinOperator.LeftOuter).EntityAlias = "b";

        var act = () => _svc.RetrieveMultipleAsync(q);

        (await act.Should().ThrowAsync<XrmdFaultException>())
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400 && e.Message.Contains("joined after"));
    }

    /// <summary><c>pb_bulk</c> (organization-owned, no custom attributes) with 50,001 rows, inserted set-based.</summary>
    private async Task EnsureBulkAsync()
    {
        if (_store.GetEntity(Bulk) is null)
        {
            var entity = new EntityMetadata { LogicalName = Bulk, SchemaName = "pb_Bulk", OwnershipType = OwnershipTypes.OrganizationOwned };
            await _store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });
        }
        await using var conn = await _factory.OpenAsync();
        // 50,001 rows from a cross join of digits (both engines; plan 2.7a, Task 6).
        const string digits = "(VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9))";
        await using var cmd = TestSql.Command(conn, $"""
            INSERT INTO data.{Bulk} ({Bulk}id)
            SELECT NEWID() FROM {digits} AS a(i) CROSS JOIN {digits} AS b(i) CROSS JOIN {digits} AS c(i) CROSS JOIN {digits} AS d(i)
                CROSS JOIN {digits} AS e(i) CROSS JOIN (VALUES (0), (1), (2), (3), (4), (5)) AS f(i)
            WHERE a.i + 10 * b.i + 100 * c.i + 1000 * d.i + 10000 * e.i + 100000 * f.i <= 50000
                AND NOT EXISTS (SELECT 1 FROM data.{Bulk})
            """);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }
}
