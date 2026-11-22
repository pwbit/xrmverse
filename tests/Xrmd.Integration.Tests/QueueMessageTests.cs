using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1a, task 5: the messages that place queue items. AddToQueue creates the record's active item in the destination queue,
/// or moves the one it has (a received email may be in several queues); RemoveFromQueue deletes an item; RouteTo moves an item to a
/// queue, or to the default queue of a user or team; AddPrincipalToQueue makes a user, or a team's members, members of a queue.
/// Each is a pipeline message with its own steps, and the item writes run as queueitem Create, Update and Delete (decision 4).
/// </summary>
[Collection("xrmd")]
public sealed class QueueMessageTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeParentAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");
    }

    // ---- helpers ----

    private string Tag(string label) => $"{label} {_run}";

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<Guid> QueueAsync(string name, int viewType = 0, EntityReference? owner = null)
    {
        var queue = new Entity("queue") { ["name"] = Tag(name), ["queueviewtype"] = new OptionSetValue(viewType), ["queuetypecode"] = new OptionSetValue(1) };
        if (owner is not null) queue["ownerid"] = owner;
        return await Admin.CreateAsync(queue);
    }

    private Task<Guid> EmailAsync(string subject, Guid? owner = null)
    {
        var email = new Entity("email") { ["subject"] = Tag(subject) };
        if (owner is { } o) email["ownerid"] = new EntityReference("systemuser", o);
        return Admin.CreateAsync(email);
    }

    private static EntityReference Email(Guid id) => new("email", id);

    private static AddToQueueRequest Add(Guid email, Guid queue, Guid? source = null, Entity? properties = null)
    {
        var request = new AddToQueueRequest { Target = Email(email), DestinationQueueId = queue };
        if (source is { } s) request.SourceQueueId = s;
        if (properties is not null) request.QueueItemProperties = properties;
        return request;
    }

    private static async Task<Guid> AddAsync(OrganizationService service, Guid email, Guid queue, Guid? source = null, Entity? properties = null) =>
        (Guid)(await service.ExecuteAsync(Add(email, queue, source, properties))).Results["QueueItemId"];

    /// <summary>The active items of the record, as (item, queue), ordered by queue.</summary>
    private async Task<List<(Guid Item, Guid Queue)>> ActiveItemsAsync(Guid record)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT queueitemid, queueid FROM data.queueitem WHERE objectid = '{record}' AND statecode = 0");
        var items = new List<(Guid, Guid)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) items.Add((rd.GetGuid(0), rd.GetGuid(1)));
        return [.. items.OrderBy(i => i.Item2)];
    }

    private Task<long> ItemCountAsync(Guid record) => CountAsync($"SELECT COUNT(*) FROM data.queueitem WHERE objectid = '{record}'");

    private Task<long> MembersAsync(Guid queue) => CountAsync($"SELECT COUNT(*) FROM data.queuemembership WHERE queueid = '{queue}'");

    private Task<long> LogAsync(string name) => CountAsync($"SELECT COUNT(*) FROM data.{TestEntities.PipeLog} WHERE pb_name = '{name}'");

    private async Task<(Guid Id, Guid Oid, OrganizationService Service)> UserAsync(params (string Privilege, DepthMask Depth)[] more)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, id, StandardRoles.BasicUser);
        await GrantAsync(fx, id, team: false, [("prvReadActivity", DepthMask.Global), .. more]);
        return (id, oid, ServiceFor(fx, oid));
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    // ---- AddToQueue ----

    [Fact]
    public async Task AddToQueue_CreatesActiveItem_WithTitleAndEnteredOn()
    {
        var queue = await QueueAsync("Inbox");
        var elsewhere = await QueueAsync("Elsewhere");
        var email = await EmailAsync("Invoice question");
        var other = await EmailAsync("Other record");
        var before = DateTime.UtcNow.AddMinutes(-1);

        // The properties' own queueid and objectid are ignored: the message's destination and target win.
        var id = await AddAsync(Admin, email, queue, properties: new Entity("queueitem")
        {
            ["priority"] = 7, ["queueid"] = new EntityReference("queue", elsewhere), ["objectid"] = Email(other),
        });

        var item = await Admin.RetrieveAsync("queueitem", id,
            new ColumnSet("title", "enteredon", "priority", "queueid", "objectid", "statecode", "statuscode"));
        item.GetAttributeValue<string>("title").Should().Be(Tag("Invoice question"));
        item.GetAttributeValue<DateTime>("enteredon").Should().BeAfter(before).And.BeBefore(DateTime.UtcNow.AddMinutes(1));
        item.GetAttributeValue<int>("priority").Should().Be(7);
        item.GetAttributeValue<EntityReference>("queueid").Id.Should().Be(queue);
        item.GetAttributeValue<EntityReference>("objectid").Should().Match<EntityReference>(o => o.LogicalName == "email" && o.Id == email);
        item.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));
        (await ItemCountAsync(other)).Should().Be(0);
    }

    [Fact]
    public async Task AddToQueue_RecordInOtherQueue_MovesTheSingleItem()
    {
        var first = await QueueAsync("First");
        var second = await QueueAsync("Second");
        var third = await QueueAsync("Third");
        var email = await EmailAsync("Moving");
        var id = await AddAsync(Admin, email, first);

        // Without a source queue: the item moves from wherever it is.
        (await AddAsync(Admin, email, second)).Should().Be(id);
        (await ActiveItemsAsync(email)).Should().Equal((id, second));

        // With the source queue it is in.
        (await AddAsync(Admin, email, third, source: second)).Should().Be(id);
        (await ActiveItemsAsync(email)).Should().Equal((id, third));

        // A source queue the record is not in: 400, nothing moves.
        var fault = await FaultAsync(() => AddAsync(Admin, email, second, source: first));
        fault.HttpStatus.Should().Be(400);
        (await ActiveItemsAsync(email)).Should().Equal((id, third));
        (await ItemCountAsync(email)).Should().Be(1);
    }

    [Fact]
    public async Task AddToQueue_Concurrent_LeavesOneActiveItem()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var sleeper = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "ThrowPlugin");
        // A PreOperation step on the item's Create that waits: without the lock on the record, every request would read "no active
        // item" before any inserts, and each would create one.
        await PluginTestHelper.RegisterStepAsync(Admin, sleeper, "Create", "queueitem", 20, config: "sleep:400");
        var queues = new[] { await QueueAsync("Race A"), await QueueAsync("Race B"), await QueueAsync("Race C") };
        var email = await EmailAsync("Raced");
        using var start = new ManualResetEventSlim();

        var requests = queues.Select(queue => Task.Run(async () =>
        {
            var service = ServiceFor(fx, fx.AdminObjectId);
            start.Wait();
            return await AddAsync(service, email, queue);
        })).ToList();
        start.Set();
        var ids = await Task.WhenAll(requests);

        ids.Distinct().Should().ContainSingle("every request placed the same item");
        (await ActiveItemsAsync(email)).Should().ContainSingle().Which.Item.Should().Be(ids[0]);
        (await ItemCountAsync(email)).Should().Be(1);
    }

    [Fact]
    public async Task AddToQueue_ReceivedEmail_MayBeInTwoQueues()
    {
        var first = await QueueAsync("Sales");
        var second = await QueueAsync("Support");
        var received = await EmailAsync("Received");
        await ExecAsync($"UPDATE data.email SET statecode = 1, statuscode = 4 WHERE activityid = '{received}'");
        var draft = await EmailAsync("Draft");

        var a = await AddAsync(Admin, received, first);
        var b = await AddAsync(Admin, received, second);
        b.Should().NotBe(a);
        (await ActiveItemsAsync(received)).Select(i => i.Queue).Should().BeEquivalentTo([first, second]);
        // Added again to a queue it is in: the same item, no third one.
        (await AddAsync(Admin, received, first)).Should().Be(a);
        (await ItemCountAsync(received)).Should().Be(2);

        // Any other status: the item moves.
        var c = await AddAsync(Admin, draft, first);
        (await AddAsync(Admin, draft, second)).Should().Be(c);
        (await ActiveItemsAsync(draft)).Should().Equal((c, second));
    }

    [Fact]
    public async Task AddToQueue_TableNotQueueEnabled_Is400()
    {
        var queue = await QueueAsync("Accounts");
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = Tag("Not queued") });

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new AddToQueueRequest
        {
            Target = new EntityReference("account", account), DestinationQueueId = queue,
        }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("account");
        (await ItemCountAsync(account)).Should().Be(0);
    }

    [Fact]
    public async Task AddToQueue_InactiveRecordOrQueue_Is400()
    {
        var queue = await QueueAsync("Active");
        var inactive = await QueueAsync("Inactive");
        await ExecAsync($"UPDATE data.queue SET statecode = 1, statuscode = 2 WHERE queueid = '{inactive}'");
        var completed = await EmailAsync("Completed");
        await ExecAsync($"UPDATE data.email SET statecode = 1, statuscode = 2 WHERE activityid = '{completed}'");
        var open = await EmailAsync("Open");

        (await FaultAsync(() => AddAsync(Admin, completed, queue))).HttpStatus.Should().Be(400);
        (await FaultAsync(() => AddAsync(Admin, open, inactive))).HttpStatus.Should().Be(400);

        (await ItemCountAsync(completed)).Should().Be(0);
        (await ItemCountAsync(open)).Should().Be(0);
    }

    [Fact]
    public async Task AddToQueue_PrivateQueueTheCallerCannotRead_Is403()
    {
        // Write on queues in its business unit (Local), but the private queue is neither the user's nor one it is a member of.
        var (userId, _, user) = await UserAsync(("prvWriteQueue", DepthMask.Local));
        var queue = await QueueAsync("Hidden", viewType: 1);
        var email = await EmailAsync("Placed");

        (await FaultAsync(() => AddAsync(user, email, queue))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        var direct = new Entity("queueitem")
        {
            ["queueid"] = new EntityReference("queue", queue), ["objectid"] = Email(email), ["title"] = Tag("Direct"),
        };
        (await FaultAsync(() => user.CreateAsync(direct))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        (await ItemCountAsync(email)).Should().Be(0);

        // Once a member, the queue is readable and the item is placed.
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", userId) });
        var id = await AddAsync(user, email, queue);
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));
    }

    // ---- direct Create (ruling R15) ----

    private Entity DirectItem(Guid queue, Guid email) => new("queueitem")
    {
        ["queueid"] = new EntityReference("queue", queue), ["objectid"] = Email(email), ["title"] = Tag("Direct"),
    };

    [Fact]
    public async Task QueueItemCreate_RecordWithActiveItem_Is400_AndLeavesOneItem()
    {
        var first = await QueueAsync("Held");
        var second = await QueueAsync("Other");
        var email = await EmailAsync("Single");
        var id = await AddAsync(Admin, email, first);

        var fault = await FaultAsync(() => Admin.CreateAsync(DirectItem(second, email)));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("already has an active queue item");
        (await ActiveItemsAsync(email)).Should().Equal((id, first));
        (await ItemCountAsync(email)).Should().Be(1);
    }

    [Fact]
    public async Task QueueItemCreate_ReceivedEmail_InAnotherQueue_Succeeds()
    {
        var first = await QueueAsync("Mail A");
        var second = await QueueAsync("Mail B");
        var email = await EmailAsync("Received direct");
        await ExecAsync($"UPDATE data.email SET statecode = 1, statuscode = 4 WHERE activityid = '{email}'");
        var a = await AddAsync(Admin, email, first);

        var b = await Admin.CreateAsync(DirectItem(second, email));

        (await ActiveItemsAsync(email)).Select(i => i.Item).Should().BeEquivalentTo([a, b]);
    }

    // ---- RemoveFromQueue ----

    [Fact]
    public async Task RemoveFromQueue_DeletesTheItem_KeepsTheRecord()
    {
        var queue = await QueueAsync("Removal");
        var email = await EmailAsync("Kept");
        var id = await AddAsync(Admin, email, queue);

        await Admin.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = id });

        (await ItemCountAsync(email)).Should().Be(0);
        (await Admin.RetrieveAsync("email", email, new ColumnSet("subject"))).GetAttributeValue<string>("subject").Should().Be(Tag("Kept"));
        (await FaultAsync(() => Admin.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = id }))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task RemoveFromQueue_NeedsTheItemsQueueReadable()
    {
        // Ruling R23: Basic User holds Delete Queue Item, but the private queue is neither the user's nor one it is a member of.
        var (_, _, user) = await UserAsync();
        var queue = await QueueAsync("NotYours", viewType: 1);
        var email = await EmailAsync("Stays");
        var id = await AddAsync(Admin, email, queue);

        (await FaultAsync(() => user.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = id }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.DeleteAsync("queueitem", id))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.UpdateAsync(new Entity("queueitem", id) { ["priority"] = 3 }))).HttpStatus.Should().Be(403);
        (await ItemCountAsync(email)).Should().Be(1);
    }

    // ---- RouteTo ----

    [Fact]
    public async Task RouteTo_Queue_MovesItem()
    {
        var from = await QueueAsync("From");
        var to = await QueueAsync("To");
        var email = await EmailAsync("Routed");
        var id = await AddAsync(Admin, email, from);

        await Admin.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", to), QueueItemId = id });

        (await ActiveItemsAsync(email)).Should().Equal((id, to));
    }

    [Fact]
    public async Task RouteTo_User_MovesItemToUsersDefaultQueue()
    {
        var user = await Admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = Tag("Router"), ["domainname"] = $"{_run}.{Guid.NewGuid():N}@example.invalid", ["azureactivedirectoryobjectid"] = Guid.NewGuid(),
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        var defaultQueue = (await Admin.RetrieveAsync("systemuser", user, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;
        var email = await EmailAsync("ToUser");
        var id = await AddAsync(Admin, email, await QueueAsync("Start"));

        await Admin.ExecuteAsync(new RouteToRequest { Target = new EntityReference("systemuser", user), QueueItemId = id });

        (await ActiveItemsAsync(email)).Should().Equal((id, defaultQueue));
    }

    [Fact]
    public async Task RouteTo_Team_MovesItemToTeamsDefaultQueue()
    {
        var team = await Admin.CreateAsync(new Entity("team")
        {
            ["name"] = Tag("Routing team"), ["teamtype"] = new OptionSetValue(0),
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        var defaultQueue = (await Admin.RetrieveAsync("team", team, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;
        var email = await EmailAsync("ToTeam");
        var id = await AddAsync(Admin, email, await QueueAsync("Begin"));

        await Admin.ExecuteAsync(new RouteToRequest { Target = new EntityReference("team", team), QueueItemId = id });

        (await ActiveItemsAsync(email)).Should().Equal((id, defaultQueue));
        // Its items must go before the team does (task 3): move it on.
        await Admin.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = id });
    }

    // ---- AddPrincipalToQueue ----

    [Fact]
    public async Task AddPrincipalToQueue_User_AndTeam_AddsMemberships()
    {
        var (userId, _, user) = await UserAsync();
        var (secondId, _, _) = await UserAsync();
        var (thirdId, _, _) = await UserAsync();
        var queue = await QueueAsync("Members", viewType: 1);

        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", userId) });
        (await MembersAsync(queue)).Should().Be(1);
        (await user.RetrieveAsync("queue", queue, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be(Tag("Members"));
        // Again: nothing to add, no error.
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", userId) });
        (await MembersAsync(queue)).Should().Be(1);

        // A team: each of its members (one is a member already).
        var team = await CreateTeamAsync(fx, await RootBusinessUnitAsync(fx), 0, userId, secondId, thirdId);
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("team", team) });
        (await MembersAsync(queue)).Should().Be(3);
        (await CountAsync($"SELECT COUNT(*) FROM data.queuemembership WHERE queueid = '{queue}' AND systemuserid IN ('{userId}', '{secondId}', '{thirdId}')"))
            .Should().Be(3);

        // Another kind of principal: 400.
        (await FaultAsync(() => Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("account", Guid.NewGuid()) })))
            .HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task AddPrincipalToQueue_WithoutAppendTo_Is403()
    {
        // Basic User reads the public queue but holds no Append To on it: it cannot make anyone a member.
        var (userId, _, user) = await UserAsync();
        var queue = await QueueAsync("Closed membership");

        var fault = await FaultAsync(() => user.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", userId) }));
        fault.HttpStatus.Should().Be(403);
        (await MembersAsync(queue)).Should().Be(0);
    }

    // ---- working items (ruling R23) ----

    [Fact]
    public async Task BasicUser_MemberOfPrivateQueue_WorksItsItems()
    {
        // Basic User only (no privilege on the queue record beyond Read), made a member of a private queue.
        var (userId, _, user) = await UserAsync();
        var queue = await QueueAsync("Team inbox", viewType: 1);
        var open = await QueueAsync("Open desk");
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", userId) });
        var email = await EmailAsync("Worked");
        var other = await EmailAsync("Removed");

        // Place: AddToQueue into the private queue it is a member of.
        var id = await AddAsync(user, email, queue);
        var second = await AddAsync(user, other, queue);
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));

        // Pick and Release.
        await user.ExecuteAsync(Pick(id, userId));
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(userId);
        await user.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = id });
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Should().BeNull();

        // Route out to a public queue, and back.
        await user.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", open), QueueItemId = id });
        (await ActiveItemsAsync(email)).Should().Equal((id, open));
        await user.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", queue), QueueItemId = id });
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));

        // Remove.
        await user.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = second });
        (await ItemCountAsync(other)).Should().Be(0);
    }

    [Fact]
    public async Task NonMember_WithWriteQueue_CannotWorkItemsOfPrivateQueue()
    {
        // Global Write on every queue record, but not a member of the private queue: its items cannot be worked or pulled out.
        var (userId, _, user) = await UserAsync(("prvWriteQueue", DepthMask.Global));
        var hidden = await QueueAsync("Escalations", viewType: 1);
        var open = await QueueAsync("Public desk");
        var email = await EmailAsync("Escalated");
        var id = await AddAsync(Admin, email, hidden);

        (await FaultAsync(() => user.ExecuteAsync(Pick(id, userId)))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = id }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = id }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", open), QueueItemId = id })))
            .HttpStatus.Should().Be(403);
        // AddToQueue of the record (which it can read) into a public queue would move the item out of the private one.
        var pulled = await FaultAsync(() => AddAsync(user, email, open));
        pulled.HttpStatus.Should().Be(403);
        pulled.ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        pulled.Message.Should().Contain(hidden.ToString(), "the queue it leaves is the one refused");
        (await FaultAsync(() => user.UpdateAsync(new Entity("queueitem", id) { ["priority"] = 3 }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.DeleteAsync("queueitem", id))).HttpStatus.Should().Be(403);

        var item = await ItemAsync(id);
        item.GetAttributeValue<EntityReference>("workerid").Should().BeNull();
        (await ActiveItemsAsync(email)).Should().Equal((id, hidden));
    }

    [Fact]
    public async Task RouteTo_UnreadableDestination_Is403()
    {
        var (userId, _, user) = await UserAsync();
        var from = await QueueAsync("Route source");
        var hidden = await QueueAsync("Route hidden", viewType: 1);
        var email = await EmailAsync("Not routed");
        var id = await AddAsync(Admin, email, from);

        (await FaultAsync(() => user.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", hidden), QueueItemId = id })))
            .HttpStatus.Should().Be(403);
        (await ActiveItemsAsync(email)).Should().Equal((id, from));

        // Once a member, the destination is readable.
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = hidden, Principal = new Entity("systemuser", userId) });
        await user.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", hidden), QueueItemId = id });
        (await ActiveItemsAsync(email)).Should().Equal((id, hidden));
    }

    // ---- queue item Update (ruling R15) ----

    private Task SetItemStateAsync(Guid item, int state) => Admin.ExecuteAsync(new SetStateRequest
    {
        EntityMoniker = new EntityReference("queueitem", item), State = new OptionSetValue(state), Status = new OptionSetValue(state == 0 ? 1 : 2),
    });

    [Fact]
    public async Task QueueItemUpdate_ReactivatingASecondItem_Is400()
    {
        var first = await QueueAsync("Was here");
        var second = await QueueAsync("Is here");
        var email = await EmailAsync("Reactivated");
        var old = await AddAsync(Admin, email, first);
        await SetItemStateAsync(old, 1);
        var current = await AddAsync(Admin, email, second);
        current.Should().NotBe(old, "an inactive item is not moved: a new one is created");

        var fault = await FaultAsync(() => SetItemStateAsync(old, 0));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("already has an active queue item");
        (await FaultAsync(() => Admin.UpdateAsync(new Entity("queueitem", old) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(1) })))
            .HttpStatus.Should().Be(400);
        (await ActiveItemsAsync(email)).Should().Equal((current, second));

        // Once the other is inactive, the old one may come back.
        await SetItemStateAsync(current, 1);
        await SetItemStateAsync(old, 0);
        (await ActiveItemsAsync(email)).Should().Equal((old, first));
    }

    [Fact]
    public async Task QueueItemUpdate_MovingToARecordWithAnActiveItem_Is400()
    {
        var queue = await QueueAsync("Objects");
        var held = await EmailAsync("Held");
        var free = await EmailAsync("Free");
        var heldItem = await AddAsync(Admin, held, queue);
        var freeItem = await AddAsync(Admin, free, await QueueAsync("Objects two"));

        var fault = await FaultAsync(() => Admin.UpdateAsync(new Entity("queueitem", freeItem) { ["objectid"] = Email(held) }));

        fault.HttpStatus.Should().Be(400);
        (await ActiveItemsAsync(held)).Should().Equal((heldItem, queue));
        (await ItemCountAsync(free)).Should().Be(1);
        // An inactive item may point to it; reactivating it is then refused.
        await SetItemStateAsync(freeItem, 1);
        await Admin.UpdateAsync(new Entity("queueitem", freeItem) { ["objectid"] = Email(held) });
        (await FaultAsync(() => SetItemStateAsync(freeItem, 0))).HttpStatus.Should().Be(400);
        (await ActiveItemsAsync(held)).Should().Equal((heldItem, queue));
        // An update of its own item that keeps the record is no second item.
        await Admin.UpdateAsync(new Entity("queueitem", heldItem) { ["objectid"] = Email(held), ["priority"] = 2 });
    }

    [Fact]
    public async Task QueueItemUpdate_ReceivedEmail_MayReactivateASecondItem()
    {
        var first = await QueueAsync("Inbound A");
        var second = await QueueAsync("Inbound B");
        var email = await EmailAsync("Received twice");
        await ExecAsync($"UPDATE data.email SET statecode = 1, statuscode = 4 WHERE activityid = '{email}'");
        var a = await AddAsync(Admin, email, first);
        var b = await AddAsync(Admin, email, second);
        await SetItemStateAsync(a, 1);

        await SetItemStateAsync(a, 0);

        (await ActiveItemsAsync(email)).Select(i => i.Item).Should().BeEquivalentTo([a, b]);
    }

    // ---- PickFromQueue and ReleaseToQueue ----

    private async Task<Entity> ItemAsync(Guid item) =>
        await Admin.RetrieveAsync("queueitem", item, new ColumnSet("workerid", "workeridmodifiedon", "statecode", "queueid"));

    private static PickFromQueueRequest Pick(Guid item, Guid worker, bool remove = false) =>
        new() { QueueItemId = item, WorkerId = worker, RemoveQueueItem = remove };

    [Fact]
    public async Task PickFromQueue_SetsWorker_AndWorkerModifiedOn()
    {
        var (agent, _, _) = await UserAsync();
        var (colleague, _, _) = await UserAsync();
        var queue = await QueueAsync("Picking");
        var email = await EmailAsync("Picked");
        var id = await AddAsync(Admin, email, queue);
        var before = DateTime.UtcNow.AddMinutes(-1);

        await Admin.ExecuteAsync(Pick(id, agent));

        var item = await ItemAsync(id);
        item.GetAttributeValue<EntityReference>("workerid").Should().Match<EntityReference>(w => w.LogicalName == "systemuser" && w.Id == agent);
        item.GetAttributeValue<DateTime>("workeridmodifiedon").Should().BeAfter(before).And.BeBefore(DateTime.UtcNow.AddMinutes(1));
        item.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));

        // Another user picks the item someone works on: the worker is overwritten (from memory, checklist 5).
        await Admin.ExecuteAsync(Pick(id, colleague));
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(colleague);

        // A worker that does not exist: 404, nothing changes.
        (await FaultAsync(() => Admin.ExecuteAsync(Pick(id, Guid.NewGuid())))).HttpStatus.Should().Be(404);
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(colleague);

        // RemoveQueueItem: the item is picked and removed from the queue.
        await Admin.ExecuteAsync(Pick(id, agent, remove: true));
        (await ItemCountAsync(email)).Should().Be(0);
        (await FaultAsync(() => Admin.ExecuteAsync(Pick(id, agent)))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task PickFromQueue_InactiveItem_Is400()
    {
        var (agent, _, _) = await UserAsync();
        var id = await AddAsync(Admin, await EmailAsync("Done"), await QueueAsync("Done"));
        await SetItemStateAsync(id, 1);

        (await FaultAsync(() => Admin.ExecuteAsync(Pick(id, agent)))).HttpStatus.Should().Be(400);
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Should().BeNull();
    }

    [Fact]
    public async Task ReleaseToQueue_ClearsWorker()
    {
        var (agent, _, _) = await UserAsync();
        var queue = await QueueAsync("Release");
        var email = await EmailAsync("Released");
        var id = await AddAsync(Admin, email, queue);
        await Admin.ExecuteAsync(Pick(id, agent));

        await Admin.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = id });

        var item = await ItemAsync(id);
        item.GetAttributeValue<EntityReference>("workerid").Should().BeNull();
        item.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));
        (await FaultAsync(() => Admin.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = Guid.NewGuid() }))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task ApplicationUser_PickFromQueue_WithOtherWorker()
    {
        // Decision 4: the contact center's routing service, an application user that works the item (Write Queue Item, its private
        // queue readable as a member: ruling R23), picks items on behalf of agents.
        var (router, _, routing) = await UserAsync();
        await Admin.UpdateAsync(new Entity("systemuser", router) { ["applicationid"] = Guid.NewGuid() });
        var (agent, _, agentService) = await UserAsync();
        var queue = await QueueAsync("Contact center", viewType: 1);
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", router) });
        var email = await EmailAsync("Routed call");
        var id = await AddAsync(Admin, email, queue);

        await routing.ExecuteAsync(Pick(id, agent));
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(agent);
        await routing.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = id });
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Should().BeNull();

        // The agent is no member of the private queue: 403, for both messages.
        (await FaultAsync(() => agentService.ExecuteAsync(Pick(id, agent)))).HttpStatus.Should().Be(403);
        await routing.ExecuteAsync(Pick(id, agent));
        (await FaultAsync(() => agentService.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = id }))).HttpStatus.Should().Be(403);
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(agent);
    }

    [Fact]
    public async Task PickAndRelease_SyncStepCanCancel()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var thrower = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "ThrowPlugin");
        var (agent, _, _) = await UserAsync();
        var (other, _, _) = await UserAsync();
        var email = await EmailAsync("Guarded pick");
        var id = await AddAsync(Admin, email, await QueueAsync("Guarded pick"));

        var pick = await PluginTestHelper.RegisterStepAsync(Admin, thrower, "PickFromQueue", null, 20, config: "invalid:no pick");
        (await FaultAsync(() => Admin.ExecuteAsync(Pick(id, agent)))).Message.Should().Contain("no pick");
        (await FaultAsync(() => Admin.ExecuteAsync(Pick(id, agent, remove: true)))).Message.Should().Contain("no pick");
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Should().BeNull();
        (await ItemCountAsync(email)).Should().Be(1);
        await Admin.DeleteAsync("sdkmessageprocessingstep", pick);

        await Admin.ExecuteAsync(Pick(id, agent));
        var release = await PluginTestHelper.RegisterStepAsync(Admin, thrower, "ReleaseToQueue", null, 20, config: "invalid:no release");
        (await FaultAsync(() => Admin.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = id }))).Message.Should().Contain("no release");
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(agent);
        await Admin.DeleteAsync("sdkmessageprocessingstep", release);

        // The item's own Update runs inside the message, one level deeper: its step cancels the message too.
        await PluginTestHelper.RegisterStepAsync(Admin, thrower, "Update", "queueitem", 20, config: "invalid:item frozen");
        (await FaultAsync(() => Admin.ExecuteAsync(Pick(id, other)))).Message.Should().Contain("item frozen");
        (await FaultAsync(() => Admin.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = id }))).Message.Should().Contain("item frozen");
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(agent);
    }

    // ---- RetrieveUserQueues ----

    /// <summary>A user created through the service, so with its default queue (task 3); returns its id, service and default queue.</summary>
    private async Task<(Guid Id, OrganizationService Service, Guid DefaultQueue)> UserWithQueueAsync(string name,
        params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var id = await Admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = Tag(name), ["domainname"] = $"{_run}.{Guid.NewGuid():N}@example.invalid", ["azureactivedirectoryobjectid"] = oid,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        if (privileges.Length == 0) await AssignRoleAsync(fx, id, StandardRoles.BasicUser);
        else await GrantAsync(fx, id, team: false, privileges);
        var queue = (await Admin.RetrieveAsync("systemuser", id, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;
        return (id, ServiceFor(fx, oid), queue);
    }

    private static async Task<List<Guid>> UserQueuesAsync(OrganizationService service, Guid user, bool includePublic)
    {
        var response = (RetrieveUserQueuesResponse)await service.ExecuteAsync(new RetrieveUserQueuesRequest { UserId = user, IncludePublic = includePublic });
        response.EntityCollection.EntityName.Should().Be("queue");
        response.EntityCollection.Entities.Where(e => e.LogicalName != "queue" || !e.Contains("name")).Should().BeEmpty();
        return [.. response.EntityCollection.Entities.Select(e => e.Id)];
    }

    [Fact]
    public async Task RetrieveUserQueues_ReturnsMemberQueues_AndPublicWhenAsked()
    {
        var (userId, user, own) = await UserWithQueueAsync("Queue reader");
        var member = await QueueAsync("Member of", viewType: 1);
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = member, Principal = new Entity("systemuser", userId) });
        var stranger = await QueueAsync("Not a member", viewType: 1);
        var open = await QueueAsync("Everyone", viewType: 0);
        // An owner team the user is in: its default queue has no membership rows, its members are resolved when read.
        var team = await Admin.CreateAsync(new Entity("team")
        {
            ["name"] = Tag("Queue team"), ["teamtype"] = new OptionSetValue(0),
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("team", team), Relationship = new Relationship("teammembership_association"),
            RelatedEntities = [new EntityReference("systemuser", userId)],
        });
        var teamQueue = (await Admin.RetrieveAsync("team", team, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;

        var mine = await UserQueuesAsync(user, userId, includePublic: false);
        mine.Should().Contain([own, member, teamQueue]);
        mine.Should().NotContain([stranger, open]);

        var all = await UserQueuesAsync(user, userId, includePublic: true);
        all.Should().Contain([own, member, teamQueue, open]);
        all.Should().NotContain(stranger);

        // Another user's queues: Global Read Queue (the administrator), and only the queues the caller can read.
        (await UserQueuesAsync(Admin, userId, includePublic: false)).Should().Contain([own, member, teamQueue]);
        var (_, colleague, colleagueQueue) = await UserWithQueueAsync("Colleague");
        var (otherId, _, _) = await UserWithQueueAsync("Other");
        var seen = await UserQueuesAsync(colleague, userId, includePublic: true);
        seen.Should().Contain(open).And.NotContain([own, member, teamQueue, colleagueQueue]);
        (await UserQueuesAsync(colleague, otherId, includePublic: false)).Should().BeEmpty();

        // Without Global Read Queue: only one's own.
        var (localId, local, localQueue) = await UserWithQueueAsync("Local reader", ("prvReadQueue", DepthMask.Local));
        (await UserQueuesAsync(local, localId, includePublic: false)).Should().Contain(localQueue);
        var fault = await FaultAsync(() => local.ExecuteAsync(new RetrieveUserQueuesRequest { UserId = userId, IncludePublic = false }));
        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);

        // Its own team cleanup: the team's queue holds no items, so the team may go.
        await Admin.DeleteAsync("team", team);
    }

    [Fact]
    public async Task WebApi_PickRelease_AndRetrieveUserQueues()
    {
        var (agent, _, _) = await UserAsync();
        var queue = await QueueAsync("Web pick");
        var email = await EmailAsync("Web picked");
        var id = await AddAsync(Admin, email, queue);

        var picked = await PostAsync($"queueitems({id})/Microsoft.Dynamics.CRM.PickFromQueue",
            $$$"""{"SystemUser":{"@odata.type":"Microsoft.Dynamics.CRM.systemuser","systemuserid":"{{{agent}}}"},"RemoveQueueItem":false}""");
        picked.StatusCode.Should().Be(HttpStatusCode.NoContent, await picked.Content.ReadAsStringAsync());
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Id.Should().Be(agent);

        var released = await PostAsync($"queueitems({id})/Microsoft.Dynamics.CRM.ReleaseToQueue", "");
        released.StatusCode.Should().Be(HttpStatusCode.NoContent, await released.Content.ReadAsStringAsync());
        (await ItemAsync(id)).GetAttributeValue<EntityReference>("workerid").Should().BeNull();

        var adminId = (await Admin.ExecuteAsync(new WhoAmIRequest())).Results["UserId"];
        var req = new HttpRequestMessage(HttpMethod.Get, Api + $"systemusers({adminId})/Microsoft.Dynamics.CRM.RetrieveUserQueues(IncludePublic=@p)?@p=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var queues = await fx.Client.SendAsync(req);
        var body = await queues.Content.ReadAsStringAsync();
        queues.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!.AsObject();
        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#queues");
        json["value"]!.AsArray().Select(q => Guid.Parse(q!["queueid"]!.GetValue<string>())).Should().Contain(queue);

        var removed = await PostAsync($"queueitems({id})/Microsoft.Dynamics.CRM.PickFromQueue",
            $$$"""{"SystemUser":{"@odata.type":"Microsoft.Dynamics.CRM.systemuser","systemuserid":"{{{agent}}}"},"RemoveQueueItem":true}""");
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
        (await ItemCountAsync(email)).Should().Be(0);
    }

    // ---- pipeline (decision 4) ----

    [Fact]
    public async Task QueueMessages_FireTheirOwnPipelineSteps()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var recorder = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "ContextRecorderPlugin");
        foreach (var message in new[] { "AddToQueue", "RouteTo", "RemoveFromQueue", "AddPrincipalToQueue" })
            await PluginTestHelper.RegisterStepAsync(Admin, recorder, message, null, 20, config: TestEntities.PipeLog);
        // The item's own Create runs inside AddToQueue, one level deeper.
        await PluginTestHelper.RegisterStepAsync(Admin, recorder, "Create", "queueitem", 20, config: TestEntities.PipeLog);
        var (userId, _, _) = await UserAsync();
        var queue = await QueueAsync("Steps");
        var email = await EmailAsync("Stepped");

        var id = await AddAsync(Admin, email, queue);
        await Admin.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", await QueueAsync("Next")), QueueItemId = id });
        await Admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", userId) });
        await Admin.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = id });

        (await LogAsync("AddToQueue|20|0|1")).Should().Be(1);
        (await LogAsync("Create|20|0|2")).Should().Be(1);
        (await LogAsync("RouteTo|20|0|1")).Should().Be(1);
        (await LogAsync("AddPrincipalToQueue|20|0|1")).Should().Be(1);
        (await LogAsync("RemoveFromQueue|20|0|1")).Should().Be(1);
        (await CountAsync($"SELECT COUNT(*) FROM data.{TestEntities.PipeLog} WHERE pb_name = 'Create|20|0|2' AND pb_data LIKE '%parent=AddToQueue%'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task QueueMessages_SyncStepCanCancel()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var thrower = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "ThrowPlugin");
        var queue = await QueueAsync("Guarded");
        var other = await QueueAsync("Elsewhere");
        var email = await EmailAsync("Cancelled");

        var add = await PluginTestHelper.RegisterStepAsync(Admin, thrower, "AddToQueue", null, 20, config: "invalid:not today");
        (await FaultAsync(() => AddAsync(Admin, email, queue))).Message.Should().Contain("not today");
        (await ItemCountAsync(email)).Should().Be(0);
        await Admin.DeleteAsync("sdkmessageprocessingstep", add);

        // A PostOperation step that throws after the item was written: the item's Create rolls back with the message.
        var late = await PluginTestHelper.RegisterStepAsync(Admin, thrower, "AddToQueue", null, 40, config: "invalid:too late");
        (await FaultAsync(() => AddAsync(Admin, email, queue))).Message.Should().Contain("too late");
        (await ItemCountAsync(email)).Should().Be(0);
        await Admin.DeleteAsync("sdkmessageprocessingstep", late);

        var id = await AddAsync(Admin, email, queue);
        await PluginTestHelper.RegisterStepAsync(Admin, thrower, "RouteTo", null, 20, config: "invalid:stay put");
        (await FaultAsync(() => Admin.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", other), QueueItemId = id })))
            .Message.Should().Contain("stay put");
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));
    }

    [Fact]
    public async Task QueueItemCreate_FiresWebhook()
    {
        var options = fx.App.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;
        options.WebhookAllowAnyPort = true;
        using var receiver = new Receiver();
        try
        {
            await ExecAsync("DELETE FROM data.asyncoperation");
            var endpoint = await Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
            {
                ["name"] = Tag("queue hook"), ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
                ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = receiver.Url,
                [ServiceEndpointSchema.AuthValue] = "qu3ue-k3y",
            });
            foreach (var message in new[] { "Create", "Update" })
                await Admin.CreateAsync(new Entity("sdkmessageprocessingstep")
                {
                    ["name"] = $"queue hook {message}",
                    ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId(message)),
                    ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId(message, "queueitem")),
                    ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint),
                    ["stage"] = new OptionSetValue(40), ["mode"] = new OptionSetValue(1), ["rank"] = 1,
                });
            var email = await EmailAsync("Hooked");

            var id = await AddAsync(Admin, email, await QueueAsync("Hook start"));
            await Admin.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", await QueueAsync("Hook end")), QueueItemId = id });

            var worker = new AsyncOperationWorker(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
                Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100), AsyncRetryDelay = TimeSpan.FromMilliseconds(100) }));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (receiver.Requests.Count < 2 && DateTime.UtcNow < deadline)
                if (!await worker.RunOnceAsync(default)) await Task.Delay(50);

            var received = receiver.Requests.ToList();
            received.Select(r => r.Message).Should().BeEquivalentTo(["Create", "Update"]);
            received.Should().OnlyContain(r => r.Entity == "queueitem" && r.Body.GetProperty("PrimaryEntityId").GetGuid() == id);
        }
        finally
        {
            options.WebhookAllowAnyPort = false;
            await ExecAsync("DELETE FROM data.asyncoperation; DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;");
        }
    }

    // ---- Web API ----

    [Fact]
    public async Task WebApi_QueueActions_RunTheMessages()
    {
        var queue = await QueueAsync("Web");
        var next = await QueueAsync("Web next");
        var email = await EmailAsync("Web email");
        var (userId, _, _) = await UserAsync();

        var added = await PostAsync($"queues({queue})/Microsoft.Dynamics.CRM.AddToQueue",
            $$$"""{"Target":{"@odata.type":"Microsoft.Dynamics.CRM.email","activityid":"{{{email}}}"},"QueueItemProperties":{"priority":5}}""");
        var addedBody = await added.Content.ReadAsStringAsync();
        added.StatusCode.Should().Be(HttpStatusCode.OK, addedBody);
        var json = JsonNode.Parse(addedBody)!.AsObject();
        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.AddToQueueResponse");
        var id = Guid.Parse(json["QueueItemId"]!.GetValue<string>());
        (await ActiveItemsAsync(email)).Should().Equal((id, queue));

        var routed = await PostAsync("RouteTo",
            $$$"""{"Target":{"@odata.type":"Microsoft.Dynamics.CRM.queue","queueid":"{{{next}}}"},"QueueItem":{"queueitemid":"{{{id}}}"}}""");
        routed.StatusCode.Should().Be(HttpStatusCode.NoContent, await routed.Content.ReadAsStringAsync());
        (await ActiveItemsAsync(email)).Should().Equal((id, next));

        var member = await PostAsync($"queues({next})/Microsoft.Dynamics.CRM.AddPrincipalToQueue",
            $$$"""{"Principal":{"@odata.type":"Microsoft.Dynamics.CRM.systemuser","systemuserid":"{{{userId}}}"}}""");
        member.StatusCode.Should().Be(HttpStatusCode.NoContent, await member.Content.ReadAsStringAsync());
        (await MembersAsync(next)).Should().Be(1);

        var removed = await PostAsync($"queueitems({id})/Microsoft.Dynamics.CRM.RemoveFromQueue", "");
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
        (await ItemCountAsync(email)).Should().Be(0);
    }

    // ---- webhook receiver ----

    private sealed record Received(string Message, string Entity, JsonElement Body);

    /// <summary>An <see cref="HttpListener"/> on 127.0.0.1 and a free port that answers 200 to every POST.</summary>
    private sealed class Receiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public ConcurrentQueue<Received> Requests { get; } = new();
        public string Url { get; }

        public Receiver()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/hook/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                using (var reader = new StreamReader(ctx.Request.InputStream))
                    Requests.Enqueue(new Received(ctx.Request.Headers["x-ms-dynamics-request-name"] ?? "",
                        ctx.Request.Headers["x-ms-dynamics-entity-name"] ?? "", JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone()));
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
