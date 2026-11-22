using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1a, task 4: queue and queue item security. A public queue is readable by anyone with Read Queue, a private queue by its
/// members (direct, or as a member of the team that owns it) and by a System Administrator; a queue item is readable when its
/// queue and its record are, in every query form. Membership is granted by Associate on <c>queuemembership_association</c> (ruling
/// R1; the AddPrincipalToQueue message is task 5), which needs Append To on the queue and runs its pipeline steps (decision 4).
/// A direct Create of a queue item needs its queue readable and Read on its record; working an item also needs Read on its record
/// (ruling R23).
/// </summary>
[Collection("xrmd")]
public sealed class QueueSecurityTests(XrmdFixture fx) : IAsyncLifetime
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

    /// <summary>A user with the Basic User role (Read Queue and queue item rights, Global) and Read on activities at <paramref name="activityDepth"/>.</summary>
    private async Task<(Guid Id, Guid Oid, OrganizationService Service)> UserAsync(DepthMask activityDepth = DepthMask.Global,
        params (string Privilege, DepthMask Depth)[] more)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, id, StandardRoles.BasicUser);
        await GrantAsync(fx, id, team: false, [("prvReadActivity", activityDepth), .. more]);
        return (id, oid, ServiceFor(fx, oid));
    }

    private async Task<Guid> QueueAsync(string name, int viewType, EntityReference? owner = null)
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

    private Task<Guid> ItemAsync(Guid queue, Guid email, string title) => Admin.CreateAsync(new Entity("queueitem")
    {
        ["queueid"] = new EntityReference("queue", queue), ["objectid"] = new EntityReference("email", email), ["title"] = Tag(title),
    });

    private static async Task<List<string>> NamesAsync(OrganizationService service, string table, string column, string run)
    {
        var q = new QueryExpression(table) { ColumnSet = new ColumnSet(column) };
        q.Criteria.AddCondition(column, ConditionOperator.EndsWith, run);
        return [.. (await service.RetrieveMultipleAsync(q)).Entities.Select(e => e.GetAttributeValue<string>(column)).Order()];
    }

    private Task<List<string>> QueueNamesAsync(OrganizationService service) => NamesAsync(service, "queue", "name", _run);

    private Task<List<string>> ItemTitlesAsync(OrganizationService service) => NamesAsync(service, "queueitem", "title", _run);

    private async Task<JsonObject> GetJsonAsync(Guid oid, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private Task AssociateMembersAsync(Guid queue, params Guid[] users) => Admin.ExecuteAsync(new AssociateRequest
    {
        Target = new EntityReference("queue", queue),
        Relationship = new Relationship(QueueSchema.MembershipRelationshipName),
        RelatedEntities = [.. users.Select(u => new EntityReference("systemuser", u))],
    });

    private Task DisassociateMembersAsync(Guid queue, params Guid[] users) => Admin.ExecuteAsync(new DisassociateRequest
    {
        Target = new EntityReference("queue", queue),
        Relationship = new Relationship(QueueSchema.MembershipRelationshipName),
        RelatedEntities = [.. users.Select(u => new EntityReference("systemuser", u))],
    });

    private Task<long> MembersAsync(Guid queue) => CountAsync($"SELECT COUNT(*) FROM data.queuemembership WHERE queueid = '{queue}'");

    // ---- read: queues ----

    [Fact]
    public async Task PublicQueue_ReadableByAnyBasicUser()
    {
        var (_, _, user) = await UserAsync();
        var queue = await QueueAsync("Public", viewType: 0);

        (await QueueNamesAsync(user)).Should().Equal(Tag("Public"));
        (await user.RetrieveAsync("queue", queue, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be(Tag("Public"));
    }

    [Fact]
    public async Task PrivateQueue_InvisibleToNonMember()
    {
        var (_, _, user) = await UserAsync();
        var (otherId, _, other) = await UserAsync();
        var queue = await QueueAsync("Private", viewType: 1, owner: new EntityReference("systemuser", otherId));

        (await QueueNamesAsync(user)).Should().BeEmpty();
        (await FaultAsync(() => user.RetrieveAsync("queue", queue, new ColumnSet("name")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        // Its owner, and a System Administrator, read it.
        (await QueueNamesAsync(other)).Should().Equal(Tag("Private"));
        (await QueueNamesAsync(Admin)).Should().Equal(Tag("Private"));
    }

    [Fact]
    public async Task PrivateQueue_OwnedByTeam_IsReadableByTheTeamMembers()
    {
        var (memberId, _, member) = await UserAsync();
        var (_, _, outsider) = await UserAsync();
        var team = await CreateTeamAsync(fx, await RootBusinessUnitAsync(fx), 0, memberId);
        await QueueAsync("TeamQueue", viewType: 1, owner: new EntityReference("team", team));

        (await QueueNamesAsync(member)).Should().Equal(Tag("TeamQueue"));
        (await QueueNamesAsync(outsider)).Should().BeEmpty();
    }

    // ---- read: queue items ----

    [Fact]
    public async Task NonMember_SeesNoItemsOfPrivateQueue_InAnyQueryForm()
    {
        var (memberId, memberOid, member) = await UserAsync();
        var (_, nonMemberOid, nonMember) = await UserAsync();
        var privateQueue = await QueueAsync("Closed", viewType: 1);
        var publicQueue = await QueueAsync("Open", viewType: 0);
        await AssociateMembersAsync(privateQueue, memberId);
        var hiddenEmail = await EmailAsync("Hidden");
        var shownEmail = await EmailAsync("Shown");
        await ItemAsync(privateQueue, hiddenEmail, "InClosed");
        await ItemAsync(publicQueue, shownEmail, "InOpen");

        // The member sees both (so that an empty answer below is the filter, not a broken query).
        (await ItemTitlesAsync(member)).Should().Equal(Tag("InClosed"), Tag("InOpen"));

        // RetrieveMultiple (QueryExpression), by id of the private queue too.
        (await ItemTitlesAsync(nonMember)).Should().Equal(Tag("InOpen"));
        var byQueue = new QueryExpression("queueitem") { ColumnSet = new ColumnSet("title") };
        byQueue.Criteria.AddCondition("queueid", ConditionOperator.Equal, privateQueue);
        (await nonMember.RetrieveMultipleAsync(byQueue)).Entities.Should().BeEmpty();

        // FetchXML.
        var fetch = new FetchExpression($"""
            <fetch><entity name="queueitem"><attribute name="title"/>
              <filter><condition attribute="queueid" operator="eq" value="{privateQueue}"/></filter></entity></fetch>
            """);
        (await nonMember.RetrieveMultipleAsync(fetch)).Entities.Should().BeEmpty();
        (await member.RetrieveMultipleAsync(fetch)).Entities.Should().HaveCount(1);

        // Web API $filter.
        var viaApi = await GetJsonAsync(nonMemberOid, $"queueitems?$select=title&$filter=_queueid_value eq {privateQueue}");
        viaApi["value"]!.AsArray().Should().BeEmpty();
        (await GetJsonAsync(memberOid, $"queueitems?$select=title&$filter=_queueid_value eq {privateQueue}"))["value"]!.AsArray().Should().HaveCount(1);

        // A link-entity from email to queueitem: the item row is not joined for the non-member.
        var link = new QueryExpression("email") { ColumnSet = new ColumnSet("subject") };
        link.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        var join = link.AddLink("queueitem", "activityid", "objectid", JoinOperator.Inner);
        join.Columns = new ColumnSet("title");
        join.EntityAlias = "qi";
        var seen = (await nonMember.RetrieveMultipleAsync(link)).Entities
            .Select(e => e.GetAttributeValue<AliasedValue>("qi.title").Value).ToList();
        seen.Should().Equal(Tag("InOpen"));
        (await member.RetrieveMultipleAsync(link)).Entities.Should().HaveCount(2);

        // Retrieve of the item itself.
        var item = (await Admin.RetrieveMultipleAsync(byQueue)).Entities.Single().Id;
        (await FaultAsync(() => nonMember.RetrieveAsync("queueitem", item, new ColumnSet("title")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
    }

    [Fact]
    public async Task Item_OfUnreadableRecord_IsInvisible()
    {
        // Read on activities at Basic depth: only the user's own emails are readable.
        var (userId, _, user) = await UserAsync(DepthMask.Basic);
        var queue = await QueueAsync("Shared", viewType: 0);
        await ItemAsync(queue, await EmailAsync("Own", owner: userId), "OwnRecord");
        await ItemAsync(queue, await EmailAsync("Foreign"), "ForeignRecord");

        (await ItemTitlesAsync(user)).Should().Equal(Tag("OwnRecord"));
        (await ItemTitlesAsync(Admin)).Should().Equal(Tag("ForeignRecord"), Tag("OwnRecord"));
    }

    // ---- membership ----

    [Fact]
    public async Task Membership_Associate_GrantsRead()
    {
        var (userId, _, user) = await UserAsync();
        var queue = await QueueAsync("Members", viewType: 1);
        await ItemAsync(queue, await EmailAsync("Work"), "MemberWork");
        (await QueueNamesAsync(user)).Should().BeEmpty();
        (await ItemTitlesAsync(user)).Should().BeEmpty();

        await AssociateMembersAsync(queue, userId);

        (await MembersAsync(queue)).Should().Be(1);
        (await QueueNamesAsync(user)).Should().Equal(Tag("Members"));
        (await ItemTitlesAsync(user)).Should().Equal(Tag("MemberWork"));

        await DisassociateMembersAsync(queue, userId);

        (await MembersAsync(queue)).Should().Be(0);
        (await QueueNamesAsync(user)).Should().BeEmpty();
        (await ItemTitlesAsync(user)).Should().BeEmpty();
    }

    [Fact]
    public async Task Membership_Associate_NeedsAppendToOnTheQueue()
    {
        var (userId, _, user) = await UserAsync();
        var (ownerId, _, owner) = await UserAsync(DepthMask.Global, ("prvAppendToQueue", DepthMask.Basic));
        var queue = await QueueAsync("Own", viewType: 0, owner: new EntityReference("systemuser", ownerId));
        var request = new AssociateRequest
        {
            Target = new EntityReference("queue", queue),
            Relationship = new Relationship(QueueSchema.MembershipRelationshipName),
            RelatedEntities = [new EntityReference("systemuser", userId)],
        };

        // Read Queue alone is not enough (the queue's owner without Append To is refused too).
        (await FaultAsync(() => user.ExecuteAsync(request))).HttpStatus.Should().Be(403);
        (await MembersAsync(queue)).Should().Be(0);

        await owner.ExecuteAsync(request);
        (await MembersAsync(queue)).Should().Be(1);
    }

    [Fact]
    public async Task QueueMembership_AssociateAndDisassociate_FireTheirSteps()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var recorder = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "ContextRecorderPlugin");
        var thrower = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "ThrowPlugin");
        var (firstId, _, _) = await UserAsync();
        var (secondId, _, _) = await UserAsync();
        var queue = await QueueAsync("Steps", viewType: 1);

        // A synchronous PreOperation step runs once per message, however many users the message names.
        var recordAssociate = await PluginTestHelper.RegisterStepAsync(Admin, recorder, "Associate", null, 20, config: TestEntities.PipeLog);
        var recordDisassociate = await PluginTestHelper.RegisterStepAsync(Admin, recorder, "Disassociate", null, 20, config: TestEntities.PipeLog);
        await AssociateMembersAsync(queue, firstId, secondId);
        (await CountAsync($"SELECT COUNT(*) FROM data.{TestEntities.PipeLog} WHERE pb_name = 'Associate|20|0|1'")).Should().Be(1);
        await DisassociateMembersAsync(queue, firstId);
        (await CountAsync($"SELECT COUNT(*) FROM data.{TestEntities.PipeLog} WHERE pb_name = 'Disassociate|20|0|1'")).Should().Be(1);
        (await MembersAsync(queue)).Should().Be(1);
        await Admin.DeleteAsync("sdkmessageprocessingstep", recordAssociate);
        await Admin.DeleteAsync("sdkmessageprocessingstep", recordDisassociate);

        // A step that throws rolls the association (and the disassociation) back.
        var failAssociate = await PluginTestHelper.RegisterStepAsync(Admin, thrower, "Associate", null, 20, config: "invalid:no members today");
        (await FaultAsync(() => AssociateMembersAsync(queue, firstId))).Message.Should().Contain("no members today");
        (await MembersAsync(queue)).Should().Be(1);
        await Admin.DeleteAsync("sdkmessageprocessingstep", failAssociate);

        await PluginTestHelper.RegisterStepAsync(Admin, thrower, "Disassociate", null, 20, config: "invalid:stay");
        (await FaultAsync(() => DisassociateMembersAsync(queue, secondId))).Message.Should().Contain("stay");
        (await MembersAsync(queue)).Should().Be(1);
    }

    // ---- write: queue items ----

    [Fact]
    public async Task QueueItem_DirectCreate_RequiresQueueReadable_AndReadOnRecord()
    {
        // Ruling R23: Basic User (Create Queue Item) reads activities of its own only and holds no Write on any queue record.
        var (userId, _, user) = await UserAsync(DepthMask.Basic, ("prvCreateActivity", DepthMask.Basic));
        var open = await QueueAsync("Public", viewType: 0);
        var hidden = await QueueAsync("Private", viewType: 1);
        var ownEmail = await EmailAsync("Mine", owner: userId);
        var foreignEmail = await EmailAsync("NotMine");

        Entity Item(Guid queue, Guid email, string title) => new("queueitem")
        {
            ["queueid"] = new EntityReference("queue", queue), ["objectid"] = new EntityReference("email", email), ["title"] = Tag(title),
        };

        // A private queue it is no member of: refused, nothing created.
        (await FaultAsync(() => user.CreateAsync(Item(hidden, ownEmail, "NoQueueRead")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        // A readable queue but no Read on the record: refused.
        (await FaultAsync(() => user.CreateAsync(Item(open, foreignEmail, "NoRecordRead")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        (await ItemTitlesAsync(Admin)).Should().BeEmpty();

        var id = await user.CreateAsync(Item(open, ownEmail, "Allowed"));

        (await ItemTitlesAsync(Admin)).Should().Equal(Tag("Allowed"));
        (await user.RetrieveAsync("queueitem", id, new ColumnSet("title"))).GetAttributeValue<string>("title").Should().Be(Tag("Allowed"));
    }

    [Fact]
    public async Task QueueItem_OfUnreadableRecord_CannotBeWorked()
    {
        // Ruling R23: the queue is public and the user a member, but it reads activities of its own only, so it cannot work the
        // item of another user's email.
        var (userId, _, user) = await UserAsync(DepthMask.Basic);
        var queue = await QueueAsync("Shared desk", viewType: 0);
        var other = await QueueAsync("Other desk", viewType: 0);
        await AssociateMembersAsync(queue, userId);
        var email = await EmailAsync("Someone else's");
        var item = await ItemAsync(queue, email, "Unreadable");

        (await FaultAsync(() => user.ExecuteAsync(new PickFromQueueRequest { QueueItemId = item, WorkerId = userId }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = item }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = item }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", other), QueueItemId = item })))
            .HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.UpdateAsync(new Entity("queueitem", item) { ["priority"] = 3 }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => user.DeleteAsync("queueitem", item))).HttpStatus.Should().Be(403);

        var stored = await Admin.RetrieveAsync("queueitem", item, new ColumnSet("queueid", "workerid"));
        stored.GetAttributeValue<EntityReference>("queueid").Id.Should().Be(queue);
        stored.GetAttributeValue<EntityReference>("workerid").Should().BeNull();
    }

    [Fact]
    public async Task QueueItem_RepointedThroughAssociate_FollowsTheUpdateRule()
    {
        // Final re-review N1 (ruling R24): Basic User holds Append Queue Item, but a 1:N Associate that repoints an item is working
        // it: the item's private queue must be readable. The user reads its own activities only and is no member.
        var (userId, _, user) = await UserAsync(DepthMask.Basic);
        var hidden = await QueueAsync("Associate hidden", viewType: 1);
        var foreignEmail = await EmailAsync("Escalated");
        var ownEmail = await EmailAsync("Mine", owner: userId);
        var item = await ItemAsync(hidden, foreignEmail, "Repointed");

        // objectid (Email_QueueItem): from the referenced side and from the item's side.
        var fault = await FaultAsync(() => user.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("email", ownEmail), Relationship = new Relationship("Email_QueueItem"),
            RelatedEntities = [new EntityReference("queueitem", item)],
        }));
        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        fault.Message.Should().Contain(hidden.ToString(), "the item's private queue is the one refused");
        (await FaultAsync(() => user.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("queueitem", item), Relationship = new Relationship("Email_QueueItem"),
            RelatedEntities = [new EntityReference("email", ownEmail)],
        }))).HttpStatus.Should().Be(403);

        // queueid (queue_entries): even with Append To on every queue, an item cannot be pulled out of a queue it cannot read.
        var (_, _, owner) = await UserAsync(DepthMask.Global, ("prvAppendToQueue", DepthMask.Global));
        var open = await QueueAsync("Associate open", viewType: 0);
        (await FaultAsync(() => owner.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("queue", open), Relationship = new Relationship("queue_entries"),
            RelatedEntities = [new EntityReference("queueitem", item)],
        }))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);

        var stored = await Admin.RetrieveAsync("queueitem", item, new ColumnSet("queueid", "objectid"));
        stored.GetAttributeValue<EntityReference>("queueid").Id.Should().Be(hidden);
        stored.GetAttributeValue<EntityReference>("objectid").Id.Should().Be(foreignEmail);

        // A member who reads both records may repoint it (the single-active-item rule still applies in the core).
        await AssociateMembersAsync(hidden, userId);
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference("email", foreignEmail),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", userId), AccessMask = AccessRights.ReadAccess },
        });
        await user.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("email", ownEmail), Relationship = new Relationship("Email_QueueItem"),
            RelatedEntities = [new EntityReference("queueitem", item)],
        });
        (await Admin.RetrieveAsync("queueitem", item, new ColumnSet("objectid"))).GetAttributeValue<EntityReference>("objectid").Id.Should().Be(ownEmail);
    }

    [Fact]
    public async Task AddToQueue_RecordTheCallerCannotRead_Is403()
    {
        // T5 #2: a public queue, but the email belongs to someone else and the user reads its own activities only.
        var (_, _, user) = await UserAsync(DepthMask.Basic);
        var queue = await QueueAsync("Add unreadable", viewType: 0);
        var email = await EmailAsync("Not readable");

        var fault = await FaultAsync(() => user.ExecuteAsync(new AddToQueueRequest { Target = new EntityReference("email", email), DestinationQueueId = queue }));
        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        fault.Message.Should().Contain(email.ToString(), "the record is the one refused");
        (await CountAsync($"SELECT COUNT(*) FROM data.queueitem WHERE objectid = '{email}'")).Should().Be(0);
    }
}
