using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Errors;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.4, task 5: queries on an elastic table through the organization service: the <c>partitionId</c> request parameter
/// (QueryExpression and FetchXML; the Web API form belongs to task 7), the default page of 500, the refused constructs and the
/// refused constructs (the Cosmos SQL message has its own tests).
/// </summary>
[Collection("xrmd")]
public sealed class ElasticQueryTests(XrmdFixture fx)
{
    private const int LinkCode = unchecked((int)0x80048d0b);
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _table;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<string> TableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "new_reading" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Reading");
            entity.TableType = "Elastic";
            await Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
            });
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new IntegerAttributeMetadata { LogicalName = "new_value", SchemaName = "new_Value", DisplayName = Label("Value") },
            });
            return _table = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private static string NewPartition() => "p" + Guid.NewGuid().ToString("N")[..10];

    private Task<Guid> CreateAsync(string table, string? partition, int value = 1)
    {
        var e = new Entity(table) { ["new_name"] = "row", ["new_value"] = value };
        if (partition is not null) e["partitionid"] = partition;
        return Admin.CreateAsync(e);
    }

    private async Task<EntityCollection> QueryAsync(QueryBase query, string? partition)
    {
        var request = new RetrieveMultipleRequest { Query = query };
        if (partition is not null) request["partitionId"] = partition;
        return (EntityCollection)(await Admin.ExecuteAsync(request)).Results["EntityCollection"];
    }

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        fault.HttpStatus.Should().Be(400, fault.Message);
        return fault;
    }

    [Fact]
    public async Task PartitionId_parameter_returns_only_that_partitions_rows_for_a_QueryExpression()
    {
        var table = await TableAsync();
        var (p1, p2) = (NewPartition(), NewPartition());
        var a = await CreateAsync(table, p1);
        var b = await CreateAsync(table, p1);
        await CreateAsync(table, p2);
        await CreateAsync(table, null);

        var rows = await QueryAsync(new QueryExpression(table) { ColumnSet = new ColumnSet("new_value", "partitionid") }, p1);

        rows.Entities.Select(e => e.Id).Should().BeEquivalentTo([a, b]);
        rows.Entities.Should().OnlyContain(e => e.GetAttributeValue<string>("partitionid") == p1);
    }

    [Fact]
    public async Task PartitionId_parameter_returns_only_that_partitions_rows_for_FetchXml()
    {
        var table = await TableAsync();
        var (p1, p2) = (NewPartition(), NewPartition());
        var a = await CreateAsync(table, p1);
        await CreateAsync(table, p2);

        var rows = await QueryAsync(new FetchExpression($"<fetch><entity name='{table}'><attribute name='new_value'/></entity></fetch>"), p1);

        rows.Entities.Select(e => e.Id).Should().BeEquivalentTo([a]);
    }

    [Fact]
    public async Task PartitionId_match_is_exact_and_case_sensitive()
    {
        var table = await TableAsync();
        var p = NewPartition();
        await CreateAsync(table, p);

        (await QueryAsync(new QueryExpression(table) { ColumnSet = new ColumnSet(false) }, p.ToUpperInvariant())).Entities.Should().BeEmpty();
        (await QueryAsync(new QueryExpression(table) { ColumnSet = new ColumnSet(false) }, p + " ")).Entities.Should().BeEmpty();
        (await QueryAsync(new QueryExpression(table) { ColumnSet = new ColumnSet(false) }, p)).Entities.Should().HaveCount(1);
    }

    [Fact]
    public async Task PartitionId_parameter_applies_to_the_whole_filter_including_or()
    {
        var table = await TableAsync();
        var (p1, p2) = (NewPartition(), NewPartition());
        var a = await CreateAsync(table, p1, 1);
        await CreateAsync(table, p2, 1);
        var query = new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = new FilterExpression(LogicalOperator.Or)
            {
                Conditions = { new ConditionExpression("new_value", ConditionOperator.Equal, 1), new ConditionExpression("new_value", ConditionOperator.Equal, 2) },
            },
        };

        (await QueryAsync(query, p1)).Entities.Select(e => e.Id).Should().BeEquivalentTo([a]);
    }

    [Fact]
    public async Task PartitionId_parameter_on_a_standard_table_is_refused()
    {
        await FaultAsync(() => QueryAsync(new QueryExpression("account") { ColumnSet = new ColumnSet(false) }, "p1"), ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Query_without_page_info_returns_pages_of_500()
    {
        var table = await TableAsync();
        var p = NewPartition();
        for (var i = 0; i < 501; i++) await CreateAsync(table, p, i);
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet("new_value") };

        var first = await QueryAsync(query, p);

        first.Entities.Should().HaveCount(500);
        first.MoreRecords.Should().BeTrue();

        query.PageInfo = new PagingInfo { PageNumber = 2, PagingCookie = first.PagingCookie };
        var second = await QueryAsync(query, p);
        second.Entities.Should().HaveCount(1);
        second.MoreRecords.Should().BeFalse();

        var small = await QueryAsync(new QueryExpression(table) { ColumnSet = new ColumnSet(false), PageInfo = new PagingInfo { Count = 10, PageNumber = 1 } }, p);
        small.Entities.Should().HaveCount(10);
    }

    [Fact]
    public async Task Link_entity_on_an_elastic_query_is_refused()
    {
        var table = await TableAsync();
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet(false) };
        query.AddLink("systemuser", "ownerid", "systemuserid");

        var fault = await FaultAsync(() => Admin.RetrieveMultipleAsync(query), LinkCode);
        fault.Message.Should().Contain(table);
    }

    [Fact]
    public async Task Link_from_a_standard_table_to_an_elastic_one_is_refused()
    {
        var table = await TableAsync();
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet(false) };
        query.AddLink(table, "accountid", "new_value");

        await FaultAsync(() => Admin.RetrieveMultipleAsync(query), LinkCode);
    }

    [Fact]
    public async Task Order_by_a_lookup_is_refused()
    {
        var table = await TableAsync();
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet(false) };
        query.AddOrder("ownerid", OrderType.Ascending);

        var fault = await FaultAsync(() => Admin.RetrieveMultipleAsync(query), ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("elastic table");
    }

    [Fact]
    public async Task Distinct_on_a_user_owned_elastic_table_is_refused()
    {
        var table = await TableAsync();
        var fault = await FaultAsync(() => Admin.RetrieveMultipleAsync(new QueryExpression(table) { ColumnSet = new ColumnSet("new_value"), Distinct = true }),
            ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("elastic table");
    }

    [Fact]
    public async Task Column_comparison_is_refused()
    {
        var table = await TableAsync();
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet(false) };
        query.Criteria.AddCondition(new ConditionExpression("new_value", ConditionOperator.Equal, "new_value") { CompareColumns = true });

        await FaultAsync(() => Admin.RetrieveMultipleAsync(query), ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Plain_filter_order_and_count_pass()
    {
        var table = await TableAsync();
        var p = NewPartition();
        await CreateAsync(table, p, 3);
        await CreateAsync(table, p, 9);
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet("new_value") };
        query.Criteria.AddCondition("new_value", ConditionOperator.GreaterThan, 1);
        query.AddOrder("new_value", OrderType.Descending);

        (await QueryAsync(query, p)).Entities.Select(e => e.GetAttributeValue<int>("new_value")).Should().Equal(9, 3);

        var count = await QueryAsync(new FetchExpression($"<fetch aggregate='true'><entity name='{table}'><attribute name='new_value' alias='c' aggregate='count'/></entity></fetch>"), p);
        count.Entities.Single().GetAttributeValue<AliasedValue>("c").Value.Should().Be(2);
    }
}
