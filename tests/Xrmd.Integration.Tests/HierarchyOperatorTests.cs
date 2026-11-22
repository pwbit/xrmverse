using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.5, task 2: <c>under</c>, <c>eq-or-under</c>, <c>above</c>, <c>eq-or-above</c> and <c>not-under</c> on the account tree
/// (recursive CTE on real SQL Server), through FetchXML and QueryExpression, with paging and count, cycles and read security.
/// Tree: A → B → C → D (four levels) and the unrelated E; every test run tags its accounts.
/// </summary>
[Collection("xrmd")]
public sealed class HierarchyOperatorTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _tag = "HO" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _a, _b, _c, _d, _e;

    public async Task InitializeAsync()
    {
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _a = await AccountAsync("A", null);
        _b = await AccountAsync("B", _a);
        _c = await AccountAsync("C", _b);
        _d = await AccountAsync("D", _c);
        _e = await AccountAsync("E", null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Guid> AccountAsync(string label, Guid? parent, Guid? owner = null)
    {
        var account = new Entity("account") { ["name"] = $"{_tag}-{label}" };
        if (parent is { } p) account["parentaccountid"] = new EntityReference("account", p);
        if (owner is { } o) account["ownerid"] = new EntityReference("systemuser", o);
        return _admin.CreateAsync(account);
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string FetchOperator(ConditionOperator op) => op switch
    {
        ConditionOperator.Under => "under",
        ConditionOperator.UnderOrEqual => "eq-or-under",
        ConditionOperator.Above => "above",
        ConditionOperator.AboveOrEqual => "eq-or-above",
        _ => "not-under",
    };

    /// <summary>The ids of this run's accounts matching the operator, through QueryExpression.</summary>
    private async Task<List<Guid>> QueryAsync(OrganizationService service, string attribute, ConditionOperator op, Guid id)
    {
        var q = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
        q.Criteria.AddCondition("name", ConditionOperator.BeginsWith, _tag);
        q.Criteria.AddCondition(attribute, op, id);
        return (await service.RetrieveMultipleAsync(q)).Entities.Select(e => e.Id).ToList();
    }

    /// <summary>The same through FetchXML.</summary>
    private async Task<List<Guid>> FetchAsync(OrganizationService service, string attribute, ConditionOperator op, Guid id) =>
        (await service.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch><entity name="account"><attribute name="name"/>
              <filter>
                <condition attribute="name" operator="begins-with" value="{_tag}"/>
                <condition attribute="{attribute}" operator="{FetchOperator(op)}" value="{id}"/>
              </filter>
            </entity></fetch>
            """))).Entities.Select(e => e.Id).ToList();

    private async Task AssertBothAsync(string attribute, ConditionOperator op, Guid id, params Guid[] expected)
    {
        (await QueryAsync(_admin, attribute, op, id)).Should().BeEquivalentTo(expected, $"QueryExpression {op}");
        (await FetchAsync(_admin, attribute, op, id)).Should().BeEquivalentTo(expected, $"FetchXML {op}");
    }

    [Fact]
    public async Task Tree_operators_on_accounts()
    {
        await AssertBothAsync("accountid", ConditionOperator.Under, _b, _c, _d);
        await AssertBothAsync("accountid", ConditionOperator.UnderOrEqual, _b, _b, _c, _d);
        await AssertBothAsync("accountid", ConditionOperator.Above, _c, _a, _b);
        await AssertBothAsync("accountid", ConditionOperator.AboveOrEqual, _c, _a, _b, _c);
        await AssertBothAsync("accountid", ConditionOperator.NotUnder, _b, _a, _e);
        // The root and a leaf.
        await AssertBothAsync("accountid", ConditionOperator.Under, _a, _b, _c, _d);
        await AssertBothAsync("accountid", ConditionOperator.Under, _d);
        await AssertBothAsync("accountid", ConditionOperator.Above, _a);
        await AssertBothAsync("accountid", ConditionOperator.AboveOrEqual, _a, _a);
        // Through a lookup to the hierarchical table: the accounts whose parent is under / above the reference.
        await AssertBothAsync("parentaccountid", ConditionOperator.Under, _a, _c, _d);
        await AssertBothAsync("parentaccountid", ConditionOperator.UnderOrEqual, _b, _c, _d);
        await AssertBothAsync("parentaccountid", ConditionOperator.Above, _d, _b, _c, _d);
        await AssertBothAsync("parentaccountid", ConditionOperator.AboveOrEqual, _c, _b, _c, _d);
        // A lookup that is null matches not-under (the root accounts have no parent).
        await AssertBothAsync("parentaccountid", ConditionOperator.NotUnder, _b, _b, _a, _e);
    }

    [Fact]
    public async Task A_missing_id_matches_nothing_and_not_under_everything()
    {
        var all = new[] { _a, _b, _c, _d, _e };
        foreach (var op in new[] { ConditionOperator.Under, ConditionOperator.UnderOrEqual, ConditionOperator.Above, ConditionOperator.AboveOrEqual })
            await AssertBothAsync("accountid", op, Guid.NewGuid());
        await AssertBothAsync("accountid", ConditionOperator.NotUnder, Guid.NewGuid(), all);
    }

    [Fact]
    public async Task Tree_operators_stop_on_cycles()
    {
        var x = await AccountAsync("X", null);
        var y = await AccountAsync("Y", x);
        var z = await AccountAsync("Z", y);
        // Close the loop in SQL (the API refuses nothing here: accounts have no loop check): X → Y → Z → X.
        await ExecAsync("UPDATE data.account SET parentaccountid = @z WHERE accountid = @x", ("@z", z), ("@x", x));

        await AssertBothAsync("accountid", ConditionOperator.Under, x, y, z);
        await AssertBothAsync("accountid", ConditionOperator.UnderOrEqual, x, x, y, z);
        await AssertBothAsync("accountid", ConditionOperator.Above, x, y, z);
        await AssertBothAsync("accountid", ConditionOperator.AboveOrEqual, x, x, y, z);
        await AssertBothAsync("accountid", ConditionOperator.NotUnder, x, _a, _b, _c, _d, _e);

        // A two-account loop.
        await ExecAsync("UPDATE data.account SET parentaccountid = @b WHERE accountid = @a", ("@b", _b), ("@a", _a));
        await AssertBothAsync("accountid", ConditionOperator.Under, _a, _b, _c, _d);
        await AssertBothAsync("accountid", ConditionOperator.UnderOrEqual, _a, _a, _b, _c, _d);
        await AssertBothAsync("accountid", ConditionOperator.Above, _a, _b);
        await ExecAsync("UPDATE data.account SET parentaccountid = NULL WHERE accountid = @a", ("@a", _a));
    }

    [Fact]
    public async Task Tree_operators_survive_a_loop_that_does_not_contain_the_start()
    {
        // S -> X <-> Y: the upward walk from S enters the loop and runs until the 100-level guard (SQL Server's default
        // MAXRECURSION is 100, so an off-by-one in the guard would raise error 530).
        var x = await AccountAsync("LX", null);
        var y = await AccountAsync("LY", x);
        var s = await AccountAsync("LS", x);
        await ExecAsync("UPDATE data.account SET parentaccountid = @y WHERE accountid = @x", ("@y", y), ("@x", x));

        await AssertBothAsync("accountid", ConditionOperator.Above, s, x, y);
        await AssertBothAsync("accountid", ConditionOperator.AboveOrEqual, s, s, x, y);
        // Downward: from X the loop X <-> Y (and S below X) also ends at the guard without error.
        await AssertBothAsync("accountid", ConditionOperator.Under, x, y, s);
    }

    [Fact]
    public async Task Tree_operators_respect_read_security()
    {
        var user = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), user);
        await GrantAsync(fx, userId, team: false, ("prvReadAccount", DepthMask.Basic));
        var mine = await AccountAsync("M", _b, userId);
        var theirs = await AccountAsync("T", _b);
        var service = ServiceFor(fx, user);

        // Basic depth: only the user's own accounts among the subtree of A (the traversal itself ignores security).
        (await QueryAsync(service, "accountid", ConditionOperator.Under, _a)).Should().BeEquivalentTo([mine]);
        (await FetchAsync(service, "accountid", ConditionOperator.Under, _a)).Should().BeEquivalentTo([mine]);
        (await QueryAsync(service, "accountid", ConditionOperator.Above, mine)).Should().BeEmpty(); // A and B belong to the admin
        (await QueryAsync(_admin, "accountid", ConditionOperator.Under, _a)).Should().Contain([mine, theirs]);
    }

    [Fact]
    public async Task Paging_and_total_count_work_with_the_prelude()
    {
        string Fetch(int page, string? cookie, bool count) => $"""
            <fetch count="2" page="{page}" returntotalrecordcount="{(count ? "true" : "false")}" {(cookie is null ? "" : $"paging-cookie=\"{System.Net.WebUtility.HtmlEncode(cookie)}\"")}>
              <entity name="account"><attribute name="name"/>
                <filter>
                  <condition attribute="name" operator="begins-with" value="{_tag}"/>
                  <condition attribute="accountid" operator="eq-or-under" value="{_a}"/>
                </filter>
                <order attribute="accountid"/>
              </entity>
            </fetch>
            """;

        var first = await _admin.RetrieveMultipleAsync(new FetchExpression(Fetch(1, null, true)));
        first.Entities.Should().HaveCount(2);
        first.MoreRecords.Should().BeTrue();
        first.TotalRecordCount.Should().Be(4);

        // Keyset paging (cookie) with a count: the separate count statement carries the prelude too.
        var second = await _admin.RetrieveMultipleAsync(new FetchExpression(Fetch(2, first.PagingCookie, true)));
        second.Entities.Should().HaveCount(2);
        second.MoreRecords.Should().BeFalse();
        second.TotalRecordCount.Should().Be(4);
        first.Entities.Concat(second.Entities).Select(e => e.Id).Should().BeEquivalentTo([_a, _b, _c, _d]);

        // OFFSET paging (page 2 without a cookie) and distinct, which counts through a subquery.
        var offset = await _admin.RetrieveMultipleAsync(new FetchExpression(Fetch(2, null, false)));
        offset.Entities.Should().HaveCount(2);

        var distinct = await _admin.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch distinct="true" returntotalrecordcount="true"><entity name="account"><attribute name="name"/>
              <filter>
                <condition attribute="name" operator="begins-with" value="{_tag}"/>
                <condition attribute="accountid" operator="under" value="{_a}"/>
              </filter>
            </entity></fetch>
            """));
        distinct.Entities.Should().HaveCount(3);
        distinct.TotalRecordCount.Should().Be(3);

        // The aggregate probe.
        var aggregate = await _admin.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch aggregate="true"><entity name="account"><attribute name="accountid" aggregate="count" alias="n"/>
              <filter>
                <condition attribute="name" operator="begins-with" value="{_tag}"/>
                <condition attribute="accountid" operator="above" value="{_d}"/>
              </filter>
            </entity></fetch>
            """));
        aggregate.Entities.Single().GetAttributeValue<AliasedValue>("n").Value.Should().Be(3);
    }

    [Fact]
    public async Task Tree_operators_on_a_table_without_hierarchy_fail_with_400()
    {
        var act = () => _admin.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch><entity name="contact"><attribute name="fullname"/>
              <filter><condition attribute="contactid" operator="under" value="{Guid.NewGuid()}"/></filter>
            </entity></fetch>
            """));
        var fault = (await act.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("hierarchical relationship");
    }

    [Fact]
    public async Task Tree_operator_in_a_link_entity_filter_runs()
    {
        // The CTE of a condition in a link-entity's ON clause must be in the prelude (SQL Server would answer "Invalid object
        // name 'h0'" otherwise): the accounts whose parent is under A are C (parent B) and D (parent C).
        var ids = (await _admin.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch returntotalrecordcount="true"><entity name="account"><attribute name="name"/>
              <filter><condition attribute="name" operator="begins-with" value="{_tag}"/></filter>
              <link-entity name="account" from="accountid" to="parentaccountid" alias="p">
                <filter><condition attribute="accountid" operator="under" value="{_a}"/></filter>
              </link-entity>
            </entity></fetch>
            """))).Entities.Select(e => e.Id);
        ids.Should().BeEquivalentTo([_c, _d]);
    }

    [Fact]
    public async Task Ownerid_walks_the_user_tree()
    {
        var root = await RootBusinessUnitAsync(fx);
        var manager = await CreateUserAsync(fx, root);
        var report = await CreateUserAsync(fx, root);
        await _admin.UpdateAsync(new Entity("systemuser", report) { ["parentsystemuserid"] = new EntityReference("systemuser", manager) });
        try
        {
            var ofReport = await AccountAsync("OR", null, report);
            var ofManager = await AccountAsync("OM", null, manager);
            await AssertBothAsync("ownerid", ConditionOperator.Under, manager, ofReport);
            await AssertBothAsync("ownerid", ConditionOperator.UnderOrEqual, manager, ofReport, ofManager);
            await AssertBothAsync("ownerid", ConditionOperator.Above, report, ofManager);
        }
        finally
        {
            await ExecAsync("UPDATE data.systemuser SET parentsystemuserid = NULL WHERE systemuserid = @u", ("@u", report));
        }
    }

    [Theory]
    [InlineData("under")]
    [InlineData("eq-or-under")]
    [InlineData("above")]
    [InlineData("eq-or-above")]
    [InlineData("not-under")]
    public async Task Tree_operators_on_a_customer_lookup_fail_with_400(string op)
    {
        // contact.parentcustomerid targets account and contact: the tree would depend on the value's table, so it is refused
        // instead of silently walking the account tree.
        var act = () => _admin.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch><entity name="contact"><attribute name="fullname"/>
              <filter><condition attribute="parentcustomerid" operator="{op}" value="{_a}"/></filter>
            </entity></fetch>
            """));
        var fault = (await act.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("multi-target lookup 'parentcustomerid'");
    }

    [Fact]
    public async Task RowAggregate_is_refused_with_400()
    {
        var act = () => _admin.RetrieveMultipleAsync(new FetchExpression("""
            <fetch><entity name="account"><attribute name="name"/><attribute name="accountid" rowaggregate="CountChildren" alias="kids"/></entity></fetch>
            """));
        var fault = (await act.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("rowaggregate");
    }
}
