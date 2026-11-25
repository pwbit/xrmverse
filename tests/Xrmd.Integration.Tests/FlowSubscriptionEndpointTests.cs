using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.WebApi.Connector;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>Plan X.12b, task 3: <c>POST /api/connector/v1/subscriptions</c> and <c>DELETE …/subscriptions/{id}</c>.</summary>
[Collection("xrmd")]
public sealed class FlowSubscriptionEndpointTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Root = "/api/connector/v1/subscriptions";
    private const string Callback = "http://localhost:5999/cb";
    private string? _elastic;
    private readonly List<Guid> _created = [];

    /// <summary>A subscriber: Basic User's privileges on the subscriptions, and read on the watched tables.</summary>
    private static readonly (string, DepthMask)[] Subscriber =
    [
        ("prvCreatexrmverse_FlowSubscription", DepthMask.Basic), ("prvReadxrmverse_FlowSubscription", DepthMask.Basic),
        ("prvReadAccount", DepthMask.Basic), ("prvReadContact", DepthMask.Basic),
    ];

    /// <summary>The configured callback hosts, restored after each test.</summary>
    private string[] _originalHosts = [];

    private ConnectorOptions Options => fx.App.Services.GetRequiredService<IOptions<ConnectorOptions>>().Value;

    public Task InitializeAsync()
    {
        _originalHosts = Options.CallbackHosts;
        Options.CallbackHosts = ["localhost"];
        Options.AllowPrivateCallbacks = true;
        return Task.CompletedTask;
    }

    /// <summary>Ends what a test left behind (the next tasks notify the callbacks), and restores the options.</summary>
    public async Task DisposeAsync()
    {
        foreach (var id in _created) await SendAsync(HttpMethod.Delete, $"{Root}/{id}", null, fx.AdminObjectId);
        _created.Clear();
        if (_elastic is not null) await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new DeleteEntityRequest { LogicalName = _elastic });
        _elastic = null;
        Options.CallbackHosts = _originalHosts;
        Options.AllowPrivateCallbacks = false;
        Options.MaxSubscriptionsPerUser = 50;
    }

    private async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> SendAsync(HttpMethod method, string path, string? body, Guid oid)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private static string Body(string table = "account", string? changes = "create,update", string? filtering = null, string? callback = Callback)
    {
        var o = new JsonObject { ["table"] = table, ["changes"] = changes, ["callbackUrl"] = callback };
        if (filtering is not null) o["filteringColumns"] = filtering;
        return o.ToJsonString();
    }

    private async Task<(HttpStatusCode Status, string Text, Guid? Id, HttpResponseMessage Response)> SubscribeAsync(string body, Guid oid)
    {
        var (status, text, response) = await SendAsync(HttpMethod.Post, Root, body, oid);
        Guid? id = status == HttpStatusCode.Created ? Guid.Parse((string)JsonNode.Parse(text)!["id"]!) : null;
        if (id is { } created) _created.Add(created);
        return (status, text, id, response);
    }

    private async Task<(Guid UserId, Guid Oid)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var bu = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, bu, oid);
        if (privileges.Length == 0) privileges = Subscriber;
        await AssignRoleAsync(fx, userId, await CreateRoleAsync(fx, "flowsub-" + Guid.NewGuid().ToString("N")[..8], privileges));
        return (userId, oid);
    }

    private Task<List<object?[]>> RowAsync(Guid id) => TestSql.RowsAsync(fx.ConnectionString,
        $"SELECT ownerid, {FlowSubscriptionSchema.Changes}, {FlowSubscriptionSchema.FilteringColumns}, {FlowSubscriptionSchema.CallbackHost}, "
        + $"{FlowSubscriptionSchema.Name}, {FlowSubscriptionSchema.TableColumn} FROM data.{FlowSubscriptionSchema.Table} WHERE {FlowSubscriptionSchema.Id} = @id",
        ("@id", id));

    private Task<List<object?[]>> CallbackRowAsync(Guid id) => TestSql.RowsAsync(fx.ConnectionString,
        "SELECT callbackurl FROM meta.flow_subscription_callback WHERE subscriptionid = @id", ("@id", id));

    private static string Message(string text) => (string)JsonNode.Parse(text)!["error"]!["message"]!;

    [Fact]
    public async Task Subscribe_returns_201_with_absolute_location_and_stores_row_and_callback()
    {
        var (userId, oid) = await UserAsync();
        var (status, text, id, response) = await SubscribeAsync(
            """{ "table":"account","changes":"create,update","filteringColumns":"name,telephone1","callbackUrl":"http://localhost:5999/cb" }""", oid);

        status.Should().Be(HttpStatusCode.Created, text);
        var location = response.Headers.Location!;
        location.IsAbsoluteUri.Should().BeTrue();
        location.ToString().Should().Be($"{fx.Client.BaseAddress!.GetLeftPart(UriPartial.Authority)}{Root}/{id}");
        JsonNode.Parse(text)!.AsObject().Select(p => p.Key).Should().Equal("id");

        var row = (await RowAsync(id!.Value)).Single();
        ((Guid)row[0]!).Should().Be(userId);
        Convert.ToInt32(row[1]).Should().Be(3);
        row[2].Should().Be("name,telephone1");
        row[3].Should().Be("localhost");
        row[4].Should().Be("account: create,update");
        row[5].Should().Be("account");
        (await CallbackRowAsync(id.Value)).Single()[0].Should().Be(Callback);
        var system = (await TestSql.RowsAsync(fx.ConnectionString,
            $"SELECT owningbusinessunit, owninguser, createdby, modifiedby FROM data.{FlowSubscriptionSchema.Table} WHERE {FlowSubscriptionSchema.Id} = @id",
            ("@id", id))).Single();
        ((Guid)system[0]!).Should().Be(await RootBusinessUnitAsync(fx));
        ((Guid)system[1]!).Should().Be(userId);
        ((Guid)system[2]!).Should().Be(userId, "the subscriber is the creator");
        ((Guid)system[3]!).Should().Be(userId);

        var (getStatus, getText, _) = await SendAsync(HttpMethod.Get, $"/api/data/v9.2/{FlowSubscriptionSchema.EntitySet}({id})", null, oid);
        getStatus.Should().Be(HttpStatusCode.OK, getText);
        getText.Should().NotContain("5999").And.NotContain("/cb");
    }

    [Fact]
    public async Task Subscribe_refuses_bad_input()
    {
        var elastic = await ElasticTableAsync();
        var bodies = new[]
        {
            Body(changes: "create,upsert"),
            Body(changes: ""),
            Body(changes: null),
            Body(changes: "update", filtering: "name,nosuchcolumn"),
            Body(changes: "create,delete", filtering: "name"),
            Body(table: FlowSubscriptionSchema.Table),
            Body(table: elastic),
            Body(callback: "http://evil.example.com/cb"),
        };
        foreach (var body in bodies)
        {
            var (status, text, _, _) = await SubscribeAsync(body, fx.AdminObjectId);
            status.Should().Be(HttpStatusCode.BadRequest, body + " " + text);
            text.Should().NotContain("evil.example.com");
        }
        var (_, refused, _, _) = await SubscribeAsync(Body(callback: "http://evil.example.com/cb"), fx.AdminObjectId);
        Message(refused).Should().Be("The callback URL host is not allowed.");
    }

    [Fact]
    public async Task Subscribe_needs_privilege_and_table_read()
    {
        var (_, noCreate) = await UserAsync(("prvReadAccount", DepthMask.Global));
        var (status, text, _, _) = await SubscribeAsync(Body(), noCreate);
        status.Should().Be(HttpStatusCode.Forbidden, text);
        ((string)JsonNode.Parse(text)!["error"]!["code"]!).Should().Be("0x80040220");
        Message(text).Should().Contain("prvCreatexrmverse_FlowSubscription").And.NotContain("prvReadAccount");

        var (_, noRead) = await UserAsync(("prvCreatexrmverse_FlowSubscription", DepthMask.Basic), ("prvReadxrmverse_FlowSubscription", DepthMask.Basic));
        (status, text, _, _) = await SubscribeAsync(Body(), noRead);
        status.Should().Be(HttpStatusCode.Forbidden, text);
        ((string)JsonNode.Parse(text)!["error"]!["code"]!).Should().Be("0x80040220");
        Message(text).Should().Contain("prvReadAccount").And.NotContain("prvCreatexrmverse_FlowSubscription");

        (await SubscribeAsync(Body(table: "nosuchtable"), fx.AdminObjectId)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_limit_per_user()
    {
        Options.MaxSubscriptionsPerUser = 2;
        var (_, oid) = await UserAsync();
        (await SubscribeAsync(Body(), oid)).Status.Should().Be(HttpStatusCode.Created);
        (await SubscribeAsync(Body(), oid)).Status.Should().Be(HttpStatusCode.Created);
        var (status, text, _, _) = await SubscribeAsync(Body(), oid);
        status.Should().Be((HttpStatusCode)429, text);
        var error = JsonNode.Parse(text)!["error"]!;
        ((string)error["code"]!).Should().Be("0x80090910");
        ((string)error["message"]!).Should().Be("You have reached the limit of 2 flow subscriptions.");
    }

    [Fact]
    public async Task Unsubscribe_by_owner_or_admin_only()
    {
        var (_, owner) = await UserAsync();
        var (_, other) = await UserAsync();

        var (_, _, first, _) = await SubscribeAsync(Body(), owner);
        (await SendAsync(HttpMethod.Delete, $"{Root}/{first}", null, owner)).Status.Should().Be(HttpStatusCode.NoContent);
        (await RowAsync(first!.Value)).Should().BeEmpty();
        (await CallbackRowAsync(first.Value)).Should().BeEmpty();

        var (_, _, second, _) = await SubscribeAsync(Body(), owner);
        (await SendAsync(HttpMethod.Delete, $"{Root}/{second}", null, other)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await RowAsync(second!.Value)).Should().ContainSingle();
        (await SendAsync(HttpMethod.Delete, $"{Root}/{second}", null, fx.AdminObjectId)).Status.Should().Be(HttpStatusCode.NoContent);
        (await RowAsync(second.Value)).Should().BeEmpty();
        (await CallbackRowAsync(second.Value)).Should().BeEmpty();

        (await SendAsync(HttpMethod.Delete, $"{Root}/{Guid.NewGuid()}", null, fx.AdminObjectId)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cache_sees_a_subscription_after_subscribe_and_not_after_unsubscribe()
    {
        var cache = fx.App.Services.GetRequiredService<FlowSubscriptionCache>();
        var (_, oid) = await UserAsync();
        await cache.ForTableAsync("contact", CancellationToken.None); // loaded before the subscribe: the subscribe must invalidate it
        var (status, text, id, _) = await SubscribeAsync(Body(table: "contact", changes: "update,create", filtering: " Telephone1 , FullName,fullname"), oid);
        status.Should().Be(HttpStatusCode.Created, text);

        cache.MayHave("contact").Should().BeTrue();
        var sub = (await cache.ForTableAsync("contact", CancellationToken.None)).Single(s => s.Id == id);
        sub.Changes.Should().Be(FlowSubscriptionSchema.Create | FlowSubscriptionSchema.Update);
        sub.FilteringColumns.Should().BeEquivalentTo(["fullname", "telephone1"]);
        (await RowAsync(id!.Value)).Single()[4].Should().Be("contact: create,update");

        (await SendAsync(HttpMethod.Delete, $"{Root}/{id}", null, oid)).Status.Should().Be(HttpStatusCode.NoContent);
        (await cache.ForTableAsync("contact", CancellationToken.None)).Should().NotContain(s => s.Id == id);
    }

    /// <summary>An elastic table of this test, removed in <see cref="DisposeAsync"/>.</summary>
    private async Task<string> ElasticTableAsync()
    {
        var name = "new_flowelastic" + Guid.NewGuid().ToString("N")[..6];
        var entity = Entity(name, name, "Flow elastic");
        entity.TableType = "Elastic";
        await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = new("Name", 1033) },
        });
        return _elastic = name;
    }
}
