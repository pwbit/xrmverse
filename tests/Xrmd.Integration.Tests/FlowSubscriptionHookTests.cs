using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.WebApi.Connector;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>Plan X.12b, task 4: every matching write queues one flow notification (operationtype 99014) with the owner's snapshot.</summary>
[Collection("xrmd")]
public sealed class FlowSubscriptionHookTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Callback = "http://localhost:5999/cb";
    private readonly List<Guid> _subscriptions = [];
    private readonly List<Guid> _accounts = [];
    private string? _column;

    /// <summary>A subscriber: the subscription privileges, and global read on accounts (the admin creates them).</summary>
    private static readonly (string, DepthMask)[] Subscriber =
    [
        ("prvCreatexrmverse_FlowSubscription", DepthMask.Basic), ("prvReadxrmverse_FlowSubscription", DepthMask.Basic),
        ("prvReadAccount", DepthMask.Global),
    ];

    /// <summary>The configured callback hosts, restored after each test.</summary>
    private string[] _originalHosts = [];

    private ConnectorOptions Options => fx.App.Services.GetRequiredService<IOptions<ConnectorOptions>>().Value;
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync()
    {
        _originalHosts = Options.CallbackHosts;
        Options.CallbackHosts = ["localhost"];
        Options.AllowPrivateCallbacks = true;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        using (var scope = fx.App.Services.CreateScope())
            foreach (var id in _subscriptions) await scope.ServiceProvider.GetRequiredService<FlowSubscriptions>().EndAsync(id, default);
        _subscriptions.Clear();
        await PluginTestHelper.ClearAsync(fx);
        foreach (var id in _accounts)
            try { await Admin.DeleteAsync("account", id); }
            catch (Xrmd.Core.Errors.XrmdFaultException) { } // already deleted by the test
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.asyncoperation WHERE operationtype = {AsyncOperationQueue.FlowNotification}");
        if (_column is not null) await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = "account", LogicalName = _column });
        Options.CallbackHosts = _originalHosts;
        Options.AllowPrivateCallbacks = false;
    }

    private async Task<Guid> UserAsync()
    {
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), Guid.NewGuid());
        await AssignRoleAsync(fx, userId, await CreateRoleAsync(fx, "flowhook-" + Guid.NewGuid().ToString("N")[..8], Subscriber));
        return userId;
    }

    private async Task<Guid> SubscribeAsync(Guid userId, string changes = "create,update,delete", string? filtering = null)
    {
        using var scope = fx.App.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<FlowSubscriptions>().SubscribeAsync(
            new Caller(userId, await RootBusinessUnitAsync(fx)), new SubscribeBody("account", changes, filtering, Callback), default);
        _subscriptions.Add(id);
        return id;
    }

    private async Task<Guid> CreateAccountAsync(string name, Entity? extra = null)
    {
        var account = extra ?? new Entity("account");
        account["name"] = name;
        var id = await Admin.CreateAsync(account);
        _accounts.Add(id);
        return id;
    }

    private sealed record Job(Guid Owner, string Name, string? Regarding, string? Table, JsonObject Data);

    /// <summary>The flow notifications of the subscription <paramref name="subscription"/>, oldest first.</summary>
    private async Task<List<Job>> JobsAsync(Guid subscription) =>
        (await TestSql.RowsAsync(fx.ConnectionString,
            $"SELECT ownerid, name, regardingobjectid, primaryentitytype, data FROM data.asyncoperation WHERE operationtype = {AsyncOperationQueue.FlowNotification} "
            + "AND data LIKE @sub ORDER BY createdon, name", ("@sub", $"%{subscription}%")))
        .Select(r => new Job((Guid)r[0]!, (string)r[1]!, r[2]?.ToString(), r[3] as string, JsonNode.Parse((string)r[4]!)!.AsObject()))
        .ToList();

    private static JsonObject Body(Job job) => job.Data["body"]!.AsObject();

    [Fact]
    public async Task Create_queues_one_job_with_snapshot()
    {
        var user = await UserAsync();
        var sub = await SubscribeAsync(user);
        var id = await CreateAccountAsync("A1");

        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        job.Owner.Should().Be(user);
        job.Name.Should().Be("Flow notification: account create");
        job.Regarding.Should().BeNull();
        job.Table.Should().Be("account");
        ((string)job.Data["subscriptionId"]!).Should().Be(sub.ToString());
        ((int)job.Data["attempt"]!).Should().Be(1);
        var body = Body(job);
        ((string)body["change"]!).Should().Be("create");
        ((string)body["table"]!).Should().Be("account");
        ((string)body["id"]!).Should().Be(id.ToString());
        var row = body["row"]!.AsObject();
        ((string)row["name"]!).Should().Be("A1");
        ((string)row["accountid"]!).Should().Be(id.ToString());
        row.ContainsKey("@odata.context").Should().BeFalse();
        row.ContainsKey("@odata.etag").Should().BeFalse();
        row.Any(p => p.Key.Contains("FormattedValue", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Fact]
    public async Task Update_respects_filtering_columns()
    {
        var sub = await SubscribeAsync(await UserAsync(), "update", "telephone1");
        var id = await CreateAccountAsync("U1");
        await Admin.UpdateAsync(new Entity("account", id) { ["name"] = "U2" });
        (await JobsAsync(sub)).Should().BeEmpty();

        await Admin.UpdateAsync(new Entity("account", id) { ["telephone1"] = "555" });
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        job.Name.Should().Be("Flow notification: account update");
        ((string)Body(job)["change"]!).Should().Be("update");
        ((string)Body(job)["row"]!["telephone1"]!).Should().Be("555");
    }

    [Fact]
    public async Task Assign_counts_as_update_of_ownerid()
    {
        var user = await UserAsync();
        var sub = await SubscribeAsync(user, "update", "ownerid");
        var id = await CreateAccountAsync("Assigned");
        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference("account", id), Assignee = new EntityReference("systemuser", user) });

        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        ((string)Body(job)["change"]!).Should().Be("update");
        ((string)Body(job)["row"]!["_ownerid_value"]!).Should().Be(user.ToString());
    }

    [Fact]
    public async Task SetState_counts_as_update_of_statecode()
    {
        var sub = await SubscribeAsync(await UserAsync(), "update", "statecode");
        var id = await CreateAccountAsync("Stated");
        await Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference("account", id), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        });

        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        ((string)Body(job)["change"]!).Should().Be("update");
        ((int)Body(job)["row"]!["statecode"]!).Should().Be(1);
    }

    [Fact]
    public async Task Delete_queues_body_without_row()
    {
        var user = await UserAsync();
        var sub = await SubscribeAsync(user, "delete");
        var id = await CreateAccountAsync("D1");
        await Admin.DeleteAsync("account", id);

        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        job.Name.Should().Be("Flow notification: account delete");
        var body = Body(job);
        ((string)body["change"]!).Should().Be("delete");
        ((string)body["id"]!).Should().Be(id.ToString());
        body.ContainsKey("row").Should().BeFalse();

        // Without read on the table, the owner hears of no delete.
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.systemuserroles WHERE systemuserid = @u", ("@u", user));
        Cache(fx).Invalidate();
        var other = await CreateAccountAsync("D2");
        await Admin.DeleteAsync("account", other);
        (await JobsAsync(sub)).Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_respects_record_access()
    {
        // User-depth read on accounts: the subscriber reads its own accounts only.
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), Guid.NewGuid());
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "flowhook-" + Guid.NewGuid().ToString("N")[..8],
        [
            ("prvCreatexrmverse_FlowSubscription", DepthMask.Basic), ("prvReadxrmverse_FlowSubscription", DepthMask.Basic),
            ("prvReadAccount", DepthMask.Basic),
        ]));
        var sub = await SubscribeAsync(user, "delete");

        var others = await CreateAccountAsync("Not mine");
        await Admin.DeleteAsync("account", others);
        (await JobsAsync(sub)).Should().BeEmpty("the subscriber cannot read an account another user owns");

        var mine = await CreateAccountAsync("Mine");
        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference("account", mine), Assignee = new EntityReference("systemuser", user) });
        await Admin.DeleteAsync("account", mine);
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        ((string)Body(job)["id"]!).Should().Be(mine.ToString());
        Body(job).ContainsKey("row").Should().BeFalse();
    }

    [Fact]
    public async Task Failed_delete_queues_nothing()
    {
        var sub = await SubscribeAsync(await UserAsync(), "delete");
        var id = await CreateAccountAsync("Kept");
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Delete", "account", 40, config: "invalid:refused after the delete");

        await FaultAsync(() => Admin.DeleteAsync("account", id));
        (await JobsAsync(sub)).Should().BeEmpty("the job queued before the core delete is rolled back with it");
        await PluginTestHelper.ClearAsync(fx);
    }

    [Fact]
    public async Task Each_change_has_its_own_snapshot()
    {
        var sub = await SubscribeAsync(await UserAsync(), "create,update");
        var id = await CreateAccountAsync("First");
        await Admin.UpdateAsync(new Entity("account", id) { ["name"] = "Second" });

        var jobs = await JobsAsync(sub);
        jobs.Should().HaveCount(2);
        var create = jobs.Single(j => j.Name.EndsWith("create", StringComparison.Ordinal));
        var update = jobs.Single(j => j.Name.EndsWith("update", StringComparison.Ordinal));
        ((string)Body(create)["row"]!["name"]!).Should().Be("First");
        ((string)Body(update)["row"]!["name"]!).Should().Be("Second");
    }

    [Fact]
    public async Task Rolled_back_write_queues_nothing()
    {
        var sub = await SubscribeAsync(await UserAsync(), "create");
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "account", 40, config: "invalid:refused after the create");

        await FaultAsync(() => Admin.CreateAsync(new Entity("account") { ["name"] = "Rolled back" }));
        (await JobsAsync(sub)).Should().BeEmpty();
    }

    [Fact]
    public async Task Owner_without_access_gets_no_event()
    {
        var unroled = await UserAsync();
        var first = await SubscribeAsync(unroled, "create");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.systemuserroles WHERE systemuserid = @u", ("@u", unroled));
        Cache(fx).Invalidate();

        var disabled = await UserAsync();
        var second = await SubscribeAsync(disabled, "create");
        await TestSql.ExecAsync(fx.ConnectionString, $"UPDATE data.systemuser SET isdisabled = {TestSql.True} WHERE systemuserid = @u", ("@u", disabled));
        Cache(fx).Invalidate();

        var enabled = await UserAsync();
        var third = await SubscribeAsync(enabled, "create");

        await CreateAccountAsync("Nobody hears");
        (await JobsAsync(first)).Should().BeEmpty();
        (await JobsAsync(second)).Should().BeEmpty();
        (await JobsAsync(third)).Should().ContainSingle("the write itself succeeds and notifies the other subscribers");
    }

    [Fact]
    public async Task Secured_column_is_left_out()
    {
        _column = "new_flowsecret" + Guid.NewGuid().ToString("N")[..6];
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "account",
            Attribute = new StringAttributeMetadata
            {
                LogicalName = _column, SchemaName = _column, MaxLength = 100, DisplayName = Label("Flow secret"), IsSecured = true,
            },
        });
        var sub = await SubscribeAsync(await UserAsync(), "create");
        await CreateAccountAsync("Secret", new Entity("account") { [_column] = "hidden" });

        var row = Body((await JobsAsync(sub)).Should().ContainSingle().Subject)["row"]!.AsObject();
        ((string)row["name"]!).Should().Be("Secret");
        (row.TryGetPropertyValue(_column, out var secret) ? secret : null).Should().BeNull();
    }

    [Fact]
    public async Task CreateMultiple_queues_one_job_per_row()
    {
        var sub = await SubscribeAsync(await UserAsync(), "create");
        var targets = new EntityCollection { EntityName = "account" };
        targets.Entities.Add(new Entity("account") { ["name"] = "M1" });
        targets.Entities.Add(new Entity("account") { ["name"] = "M2" });
        var response = (CreateMultipleResponse)await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = targets });
        _accounts.AddRange(response.Ids);

        var jobs = await JobsAsync(sub);
        jobs.Should().HaveCount(2);
        jobs.Select(j => (string)Body(j)["row"]!["name"]!).Should().BeEquivalentTo("M1", "M2");
        jobs.Select(j => (string)Body(j)["id"]!).Should().BeEquivalentTo(response.Ids.Select(i => i.ToString()));
    }
}
