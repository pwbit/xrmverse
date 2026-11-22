using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1a, task 6: the life cycle of a queue item with its record and its queue. A queue-enabled record that leaves the active
/// state (completed, cancelled) deactivates its active items in the same transaction; reactivating the record leaves them inactive.
/// Deleting the record deletes its items (the cascade of task 2); a queue that still holds active items cannot be deleted.
/// </summary>
[Collection("xrmd")]
public sealed class QueueLifecycleTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----

    private string Tag(string label) => $"{label} {_run}";

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private Task<Guid> QueueAsync(string name) => Admin.CreateAsync(new Entity("queue")
    {
        ["name"] = Tag(name), ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
    });

    private Task<Guid> TaskAsync(string subject) => Admin.CreateAsync(new Entity("task") { ["subject"] = Tag(subject) });

    private async Task<Guid> AddAsync(string table, Guid record, Guid queue) =>
        (Guid)(await Admin.ExecuteAsync(new AddToQueueRequest { Target = new EntityReference(table, record), DestinationQueueId = queue }))
            .Results["QueueItemId"];

    private async Task<(int State, int Status)> ItemStateAsync(Guid item)
    {
        var row = await Admin.RetrieveAsync("queueitem", item, new ColumnSet("statecode", "statuscode"));
        return (row.GetAttributeValue<OptionSetValue>("statecode").Value, row.GetAttributeValue<OptionSetValue>("statuscode").Value);
    }

    private Task SetStateAsync(string table, Guid id, int state, int status) => Admin.ExecuteAsync(new SetStateRequest
    {
        EntityMoniker = new EntityReference(table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
    });

    // ---- the record's state ----

    [Fact]
    public async Task CompleteActivity_DeactivatesItsItem_InSameTransaction()
    {
        var task = await TaskAsync("Completed");
        var item = await AddAsync("task", task, await QueueAsync("Work"));
        var cancelled = await TaskAsync("Cancelled");
        var other = await AddAsync("task", cancelled, await QueueAsync("Other work"));

        await SetStateAsync("task", task, 1, 5); // Completed
        await Admin.UpdateAsync(new Entity("task", cancelled) { ["statecode"] = new OptionSetValue(2), ["statuscode"] = new OptionSetValue(6) });

        (await ItemStateAsync(item)).Should().Be((1, 2));
        (await ItemStateAsync(other)).Should().Be((1, 2));
        // An update that keeps the record active leaves its item alone.
        var open = await TaskAsync("Open");
        var openItem = await AddAsync("task", open, await QueueAsync("Open work"));
        await Admin.UpdateAsync(new Entity("task", open) { ["subject"] = Tag("Still open") });
        (await ItemStateAsync(openItem)).Should().Be((0, 1));
    }

    [Fact]
    public async Task EmailStates_KeepAReceivedEmailsItems_ButDeactivateASentOnes()
    {
        var queue = await QueueAsync("Mail");
        var sent = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Sent") });
        var sentItem = await AddAsync("email", sent, queue);
        var received = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Received") });
        var receivedItem = await AddAsync("email", received, queue);

        await SetStateAsync("email", sent, 1, 3); // Completed, Sent
        await SetStateAsync("email", received, 1, 4); // Completed, Received: it may stay in its queues

        (await ItemStateAsync(sentItem)).Should().Be((1, 2));
        (await ItemStateAsync(receivedItem)).Should().Be((0, 1));
    }

    [Fact]
    public async Task SetState_RolledBack_LeavesItemActive()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var thrower = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "ThrowPlugin");
        var task = await TaskAsync("Rolled back");
        var item = await AddAsync("task", task, await QueueAsync("Rollback"));
        // A PostOperation step of SetState (run as the Update of the task) fails after the task and its item were written.
        await PluginTestHelper.RegisterStepAsync(Admin, thrower, "Update", "task", 40, config: "invalid:not completed");

        (await FaultAsync(() => SetStateAsync("task", task, 1, 5))).Message.Should().Contain("not completed");

        (await ItemStateAsync(item)).Should().Be((0, 1));
        (await Admin.RetrieveAsync("task", task, new ColumnSet("statecode"))).GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
    }

    [Fact]
    public async Task ReactivateActivity_DoesNotReactivateItem()
    {
        var task = await TaskAsync("Reopened");
        var item = await AddAsync("task", task, await QueueAsync("Reopen"));
        await SetStateAsync("task", task, 1, 5);

        await SetStateAsync("task", task, 0, 2); // Open, Not Started

        (await ItemStateAsync(item)).Should().Be((1, 2));
        (await CountAsync($"SELECT COUNT(*) FROM data.queueitem WHERE objectid = '{task}' AND statecode = 0")).Should().Be(0);
    }

    // ---- deletes ----

    [Fact]
    public async Task DeleteRecord_DeletesItsItems()
    {
        var task = await TaskAsync("Deleted");
        var item = await AddAsync("task", task, await QueueAsync("Deletion"));
        var completed = await TaskAsync("Deleted later");
        var inactive = await AddAsync("task", completed, await QueueAsync("Deletion two"));
        await SetStateAsync("task", completed, 1, 5);

        await Admin.DeleteAsync("task", task);
        await Admin.DeleteAsync("task", completed);

        (await CountAsync($"SELECT COUNT(*) FROM data.queueitem WHERE queueitemid IN ('{item}', '{inactive}')")).Should().Be(0);
    }

    [Fact]
    public async Task DeleteQueue_WithActiveItems_Is400()
    {
        var queue = await QueueAsync("Busy");
        var task = await TaskAsync("Waiting");
        var item = await AddAsync("task", task, queue);

        var fault = await FaultAsync(() => Admin.DeleteAsync("queue", queue));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("active queue item");
        (await CountAsync($"SELECT COUNT(*) FROM data.queue WHERE queueid = '{queue}'")).Should().Be(1);
        (await ItemStateAsync(item)).Should().Be((0, 1));

        // Once its items are inactive, the queue goes and takes them with it (queue_entries cascades).
        await SetStateAsync("task", task, 1, 5);
        await Admin.DeleteAsync("queue", queue);
        (await CountAsync($"SELECT COUNT(*) FROM data.queue WHERE queueid = '{queue}'")).Should().Be(0);
        (await CountAsync($"SELECT COUNT(*) FROM data.queueitem WHERE queueitemid = '{item}'")).Should().Be(0);
    }
}
