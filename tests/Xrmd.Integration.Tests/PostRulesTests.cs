using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1g (posts), task 2: the server rules of <c>post</c>, <c>postcomment</c> and <c>postregarding</c> (Create sets the
/// regarding columns and keeps the <c>postregarding</c> row; Update is refused; auto posts are refused; <c>postregarding</c> is
/// maintained by the server), the cascades, and the read rule: a post is readable when its regarding record is, a comment when its
/// post is, and creating either needs that read.
/// </summary>
[Collection("xrmd")]
public sealed class PostRulesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string NotUpdatable = "Posts and comments cannot be updated";
    private const string ServerMaintained = "postregarding is maintained by the server";
    private const string AutoPosts = "Auto posts are not supported in this version";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private (Guid Id, Guid Oid) _uA, _uB;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private OrganizationService As((Guid Id, Guid Oid) user) => ServiceFor(fx, user.Oid);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        var root = await RootBusinessUnitAsync(fx);
        _uA = await BasicUserAsync(root);
        _uB = await BasicUserAsync(root);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----

    /// <summary>A Basic User (not an administrator) who also reads, writes and creates the accounts it owns (Basic depth).</summary>
    private async Task<(Guid Id, Guid Oid)> BasicUserAsync(Guid businessUnit)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, businessUnit, oid);
        await AssignRoleAsync(fx, id, StandardRoles.BasicUser);
        await GrantAsync(fx, id, team: false, ("prvCreateAccount", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic),
            ("prvWriteAccount", DepthMask.Basic), ("prvDeleteAccount", DepthMask.Basic));
        return (id, oid);
    }

    private async Task<EntityReference> AccountAsync(OrganizationService? service = null) =>
        new("account", await (service ?? Admin).CreateAsync(new Entity("account") { ["name"] = $"Posts {_run}" }));

    private static Entity Post(EntityReference regarding, string text = "Hello", int? source = null)
    {
        var post = new Entity(PostSchema.Post) { ["text"] = text, ["regardingobjectid"] = regarding };
        if (source is { } s) post["source"] = new OptionSetValue(s);
        return post;
    }

    private static Entity Comment(Guid post, string text = "Reply") =>
        new(PostSchema.Comment) { ["text"] = text, ["postid"] = new EntityReference(PostSchema.Post, post) };

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string table, string column, Guid value) =>
        Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.[{table}] WHERE [{column}] = @v", ("@v", value)));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid oid, JsonObject? body = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static Guid CreatedId(HttpResponseMessage res)
    {
        var url = res.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
    }

    private static async Task ShouldBe403Async(Func<Task> act) =>
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(403);

    // ---- create ----

    [Fact]
    public async Task Create_post_sets_regarding_row()
    {
        var account = await AccountAsync();
        var first = await Admin.CreateAsync(Post(account, "First"));
        var second = await Admin.CreateAsync(Post(account, "Second"));

        (await CountAsync(PostSchema.Regarding, "regardingobjectid", account.Id)).Should().Be(1);
        var regarding = (Guid)(await ScalarAsync("SELECT postregardingid FROM data.postregarding WHERE regardingobjectid = @id", ("@id", account.Id)))!;
        foreach (var post in new[] { first, second })
            (await ScalarAsync("SELECT postregardingid FROM data.post WHERE postid = @id", ("@id", post))).Should().Be(regarding);
        var latest = (DateTime)(await ScalarAsync("SELECT latestmanualpostmodifiedon FROM data.postregarding WHERE postregardingid = @id", ("@id", regarding)))!;
        var createdOn = (DateTime)(await ScalarAsync("SELECT createdon FROM data.post WHERE postid = @id", ("@id", second)))!;
        latest.Should().Be(createdOn);

        var row = await Admin.RetrieveAsync(PostSchema.Post, second, new ColumnSet(true));
        row.GetAttributeValue<string>("regardingobjecttypecode").Should().Be("account");
        row.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(account.Id);
        row.GetAttributeValue<OptionSetValue>("source").Value.Should().Be(2);
        var owner = await Admin.RetrieveAsync("account", account.Id, new ColumnSet("ownerid", "owningbusinessunit"));
        row.GetAttributeValue<EntityReference>("regardingobjectownerid").Id.Should().Be(owner.GetAttributeValue<EntityReference>("ownerid").Id);
        row.GetAttributeValue<EntityReference>("regardingobjectowningbusinessunit").Id
            .Should().Be(owner.GetAttributeValue<EntityReference>("owningbusinessunit").Id);
        row.GetAttributeValue<EntityReference>("createdby").Should().NotBeNull();
        row.GetAttributeValue<EntityReference>("organizationid").Should().NotBeNull();
        row.Contains(PostSchema.RegardingId).Should().BeFalse("postregardingid is not valid for read");

        var regardingRow = await Admin.RetrieveAsync(PostSchema.Regarding, regarding, new ColumnSet(true));
        regardingRow.GetAttributeValue<string>("regardingobjecttypecode").Should().Be("account");
        regardingRow.GetAttributeValue<EntityReference>("regardingobjectownerid").Id.Should().Be(owner.GetAttributeValue<EntityReference>("ownerid").Id);

        var comment = await Admin.RetrieveAsync(PostSchema.Comment, await Admin.CreateAsync(Comment(second)), new ColumnSet(true));
        comment.GetAttributeValue<EntityReference>("createdby").Should().NotBeNull();
        comment.GetAttributeValue<EntityReference>("organizationid").Should().NotBeNull();
    }

    [Fact]
    public async Task Basic_user_creates_post_and_comment_on_a_readable_record()
    {
        var account = await AccountAsync(As(_uA));

        var created = await SendAsync(HttpMethod.Post, "posts", _uA.Oid, new JsonObject
        {
            ["text"] = "Web post", ["type"] = 7, ["regardingobjectid_account@odata.bind"] = $"/accounts({account.Id})",
        });
        created.StatusCode.Should().Be(HttpStatusCode.NoContent, await created.Content.ReadAsStringAsync());
        var post = CreatedId(created);

        var reply = await SendAsync(HttpMethod.Post, "postcomments", _uA.Oid, new JsonObject
        {
            ["text"] = "Web reply", ["postid@odata.bind"] = $"/posts({post})",
        });
        reply.StatusCode.Should().Be(HttpStatusCode.NoContent, await reply.Content.ReadAsStringAsync());
        var comment = CreatedId(reply);

        var read = await JsonAsync(await SendAsync(HttpMethod.Get, $"posts({post})", _uA.Oid), HttpStatusCode.OK);
        read["text"]!.GetValue<string>().Should().Be("Web post");
        read["type"]!.GetValue<int>().Should().Be(7);
        read.ContainsKey("_postregardingid_value").Should().BeFalse();
        var comments = await JsonAsync(await SendAsync(HttpMethod.Get, $"postcomments?$filter=_postid_value eq {post}", _uA.Oid), HttpStatusCode.OK);
        comments["value"]!.AsArray().Select(v => Guid.Parse(v!["postcommentid"]!.GetValue<string>())).Should().Equal(comment);

        (await SendAsync(HttpMethod.Delete, $"postcomments({comment})", _uA.Oid)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, $"posts({post})", _uA.Oid)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CountAsync(PostSchema.Post, "postid", post)).Should().Be(0);
    }

    [Fact]
    public async Task Update_post_is_refused()
    {
        var post = await As(_uA).CreateAsync(Post(await AccountAsync(As(_uA))));
        foreach (var service in new[] { Admin, As(_uA) })
            (await BadRequestAsync(() => service.UpdateAsync(new Entity(PostSchema.Post, post) { ["text"] = "Changed" }))).Should().Be(NotUpdatable);
    }

    [Fact]
    public async Task Update_comment_is_refused()
    {
        var post = await As(_uA).CreateAsync(Post(await AccountAsync(As(_uA))));
        var comment = await As(_uA).CreateAsync(Comment(post));
        foreach (var service in new[] { Admin, As(_uA) })
            (await BadRequestAsync(() => service.UpdateAsync(new Entity(PostSchema.Comment, comment) { ["text"] = "Changed" }))).Should().Be(NotUpdatable);
    }

    [Fact]
    public async Task Auto_post_is_refused()
    {
        var account = await AccountAsync();
        foreach (var source in new[] { 1, 3 })
            (await BadRequestAsync(() => Admin.CreateAsync(Post(account, source: source)))).Should().Be(AutoPosts);
        (await CountAsync(PostSchema.Post, "regardingobjectid", account.Id)).Should().Be(0);
        await Admin.CreateAsync(Post(account, source: 2));
    }

    [Fact]
    public async Task Post_without_regardingobjectid_is_refused()
    {
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(PostSchema.Post) { ["text"] = "Hello" })))
            .Should().Be("A post needs 'regardingobjectid': the record it is about.");
    }

    [Fact]
    public async Task Comment_without_postid_is_refused()
    {
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(PostSchema.Comment) { ["text"] = "Reply" })))
            .Should().Be("A postcomment needs 'postid': the post it replies to.");
    }

    [Fact]
    public async Task Postregarding_writes_are_refused()
    {
        var account = await AccountAsync();
        await Admin.CreateAsync(Post(account));
        var regarding = (Guid)(await ScalarAsync("SELECT postregardingid FROM data.postregarding WHERE regardingobjectid = @id", ("@id", account.Id)))!;

        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(PostSchema.Regarding) { ["regardingobjectid"] = account })))
            .Should().Be(ServerMaintained);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(PostSchema.Regarding, regarding) { ["latestmanualpostmodifiedon"] = DateTime.UtcNow })))
            .Should().Be(ServerMaintained);
        (await BadRequestAsync(() => Admin.DeleteAsync(PostSchema.Regarding, regarding))).Should().Be(ServerMaintained);
        (await CountAsync(PostSchema.Regarding, "postregardingid", regarding)).Should().Be(1);
    }

    // ---- read rule ----

    [Fact]
    public async Task Reader_without_record_access_sees_no_posts()
    {
        var account = await AccountAsync(As(_uA));
        var post = await As(_uA).CreateAsync(Post(account));
        var comment = await As(_uA).CreateAsync(Comment(post));

        QueryExpression Posts() => new(PostSchema.Post) { ColumnSet = new ColumnSet("text"), Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, account.Id) } } };
        QueryExpression Comments() => new(PostSchema.Comment) { ColumnSet = new ColumnSet("text"), Criteria = { Conditions = { new ConditionExpression("postid", ConditionOperator.Equal, post) } } };
        QueryExpression Regardings() => new(PostSchema.Regarding) { ColumnSet = new ColumnSet("regardingobjectid"), Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, account.Id) } } };

        (await As(_uA).RetrieveMultipleAsync(Posts())).Entities.Select(e => e.Id).Should().Equal(post);
        (await As(_uA).RetrieveMultipleAsync(Comments())).Entities.Select(e => e.Id).Should().Equal(comment);
        (await As(_uA).RetrieveMultipleAsync(Regardings())).Entities.Should().ContainSingle();

        (await As(_uB).RetrieveMultipleAsync(Posts())).Entities.Should().BeEmpty();
        (await As(_uB).RetrieveMultipleAsync(Comments())).Entities.Should().BeEmpty();
        (await As(_uB).RetrieveMultipleAsync(Regardings())).Entities.Should().BeEmpty();
        await ShouldBe403Async(() => As(_uB).RetrieveAsync(PostSchema.Post, post, new ColumnSet("text")));
        await ShouldBe403Async(() => As(_uB).RetrieveAsync(PostSchema.Comment, comment, new ColumnSet("text")));

        var web = await JsonAsync(await SendAsync(HttpMethod.Get, $"posts?$filter=_regardingobjectid_value eq {account.Id}", _uB.Oid), HttpStatusCode.OK);
        web["value"]!.AsArray().Should().BeEmpty();
        (await SendAsync(HttpMethod.Get, $"posts({post})", _uB.Oid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Counts_hide_posts_of_unreadable_records()
    {
        var account = await AccountAsync(As(_uA));
        var post = await As(_uA).CreateAsync(Post(account));
        await As(_uA).CreateAsync(Comment(post));

        async Task<int> ODataCountAsync((Guid Id, Guid Oid) user, string set, string filter) =>
            (await JsonAsync(await SendAsync(HttpMethod.Get, $"{set}?$filter={filter}&$count=true", user.Oid), HttpStatusCode.OK))["@odata.count"]!.GetValue<int>();
        async Task<int> FetchCountAsync((Guid Id, Guid Oid) user, string entity, string attribute, Guid value) =>
            (int)(await As(user).RetrieveMultipleAsync(new FetchExpression($"""
                <fetch aggregate="true"><entity name="{entity}"><attribute name="{entity}id" alias="n" aggregate="count"/>
                <filter><condition attribute="{attribute}" operator="eq" value="{value}"/></filter></entity></fetch>
                """))).Entities.Single().GetAttributeValue<AliasedValue>("n").Value;

        (await ODataCountAsync(_uA, "posts", $"_regardingobjectid_value eq {account.Id}")).Should().Be(1);
        (await FetchCountAsync(_uA, PostSchema.Post, "regardingobjectid", account.Id)).Should().Be(1);
        (await ODataCountAsync(_uB, "posts", $"_regardingobjectid_value eq {account.Id}")).Should().Be(0);
        (await FetchCountAsync(_uB, PostSchema.Post, "regardingobjectid", account.Id)).Should().Be(0);
        (await ODataCountAsync(_uB, "postcomments", $"_postid_value eq {post}")).Should().Be(0);
        (await FetchCountAsync(_uB, PostSchema.Comment, "postid", post)).Should().Be(0);
    }

    [Fact]
    public async Task Create_post_needs_record_read()
    {
        var account = await AccountAsync(As(_uA));
        var post = await As(_uA).CreateAsync(Post(account));

        await ShouldBe403Async(() => As(_uB).CreateAsync(Post(account)));
        await ShouldBe403Async(() => As(_uB).CreateAsync(Comment(post)));
        await ShouldBe403Async(() => As(_uB).DeleteAsync(PostSchema.Post, post));
        (await CountAsync(PostSchema.Post, "regardingobjectid", account.Id)).Should().Be(1);
        (await CountAsync(PostSchema.Comment, "postid", post)).Should().Be(0);
    }

    // ---- cascades ----

    [Fact]
    public async Task Deleting_record_deletes_posts_and_comments()
    {
        var account = await AccountAsync();
        var post = await Admin.CreateAsync(Post(account));
        var comment = await Admin.CreateAsync(Comment(post));

        await Admin.DeleteAsync("account", account.Id);

        (await CountAsync(PostSchema.Post, "postid", post)).Should().Be(0);
        (await CountAsync(PostSchema.Comment, "postcommentid", comment)).Should().Be(0);
        (await CountAsync(PostSchema.Regarding, "regardingobjectid", account.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_post_deletes_comments()
    {
        var account = await AccountAsync();
        var post = await Admin.CreateAsync(Post(account));
        var comments = new[] { await Admin.CreateAsync(Comment(post, "One")), await Admin.CreateAsync(Comment(post, "Two")) };

        await Admin.DeleteAsync(PostSchema.Post, post);

        (await CountAsync(PostSchema.Post, "postid", post)).Should().Be(0);
        foreach (var comment in comments) (await CountAsync(PostSchema.Comment, "postcommentid", comment)).Should().Be(0);
    }

    // ---- pipeline ----

    [Fact]
    public async Task Plugin_sees_post_create()
    {
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", PostSchema.Post, 40, config: TestEntities.PipeLog);

        await Admin.CreateAsync(Post(await AccountAsync()));

        var data = (string?)await ScalarAsync($"SELECT TOP 1 pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE N'Create|40|%' ORDER BY createdon DESC");
        data.Should().NotBeNull();
        data!.Split('\n').Should().Contain("primary=post");
    }
}
