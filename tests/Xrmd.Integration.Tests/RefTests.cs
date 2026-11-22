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
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>Web API <c>$ref</c>: association and disassociation by reference, translated to Associate/Disassociate requests.</summary>
[Collection("xrmd")]
public sealed class RefTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService _admin = null!;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureProjectTagAsync(store, fx.ConnectionString);
        await TestEntities.EnsurePipeParentAsync(store);
        _admin = ServiceFor(fx, fx.AdminObjectId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, Guid? objectId = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private string Url(string set, Guid id) => $"{fx.BaseUrl.TrimEnd('/')}{Api}{set}({id})";

    private static string Body(string url) => new JsonObject { ["@odata.id"] = url }.ToJsonString();

    private async Task<int> LinkCountAsync(Guid project, Guid tag)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{TestEntities.ProjectTag} WHERE pb_projectid = @p AND new_tagid = @t");
        cmd.Parameters.AddWithValue("@p", project);
        cmd.Parameters.AddWithValue("@t", tag);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<(Guid Project, Guid Tag)> NewPairAsync() => (
        await _admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = $"ref-{_run}" }),
        await _admin.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = $"ref-{_run}" }));

    private async Task AssertErrorAsync(HttpResponseMessage res, HttpStatusCode status, string? code = null)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        if (code is not null) JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be(code);
    }

    [Fact]
    public async Task Post_ref_associates_many_to_many()
    {
        var (project, tag) = await NewPairAsync();

        var res = await SendAsync(HttpMethod.Post, $"{TestEntities.ProjectSet}({project})/{TestEntities.ProjectTag}/$ref", Body(Url(TestEntities.TagSet, tag)));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await LinkCountAsync(project, tag)).Should().Be(1);

        // From the other side, with a relative URL: a duplicate is refused like in Dataverse.
        var dup = await SendAsync(HttpMethod.Post, $"{TestEntities.TagSet}({tag})/{TestEntities.ProjectTag}/$ref", Body($"{Api}{TestEntities.ProjectSet}({project})"));
        dup.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, await dup.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delete_ref_disassociates()
    {
        var (project, tag) = await NewPairAsync();
        var (project2, tag2) = await NewPairAsync();
        await SendAsync(HttpMethod.Post, $"{TestEntities.ProjectSet}({project})/{TestEntities.ProjectTag}/$ref", Body(Url(TestEntities.TagSet, tag)));
        await SendAsync(HttpMethod.Post, $"{TestEntities.ProjectSet}({project2})/{TestEntities.ProjectTag}/$ref", Body(Url(TestEntities.TagSet, tag2)));

        var byKey = await SendAsync(HttpMethod.Delete, $"{TestEntities.ProjectSet}({project})/{TestEntities.ProjectTag}({tag})/$ref");
        var byId = await SendAsync(HttpMethod.Delete,
            $"{TestEntities.ProjectSet}({project2})/{TestEntities.ProjectTag}/$ref?$id={Uri.EscapeDataString(Url(TestEntities.TagSet, tag2))}");

        byKey.StatusCode.Should().Be(HttpStatusCode.NoContent, await byKey.Content.ReadAsStringAsync());
        byId.StatusCode.Should().Be(HttpStatusCode.NoContent, await byId.Content.ReadAsStringAsync());
        (await LinkCountAsync(project, tag)).Should().Be(0);
        (await LinkCountAsync(project2, tag2)).Should().Be(0);
    }

    [Fact]
    public async Task Ref_assigns_a_role_to_a_user()
    {
        var root = await RootBusinessUnitAsync(fx);
        var target = await CreateUserAsync(fx, root);
        var harmless = await CreateRoleAsync(fx, "ref-harmless-" + _run);
        var escalating = await CreateRoleAsync(fx, "ref-escalating-" + _run, ("prvDeletepb_Project", DepthMask.Basic));

        var ok = await SendAsync(HttpMethod.Post, $"systemusers({target})/systemuserroles_association/$ref", Body(Url("roles", harmless)));
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync());

        // A manager with Write on systemuser and role, but without the privilege the role grants, cannot escalate.
        var manager = Guid.NewGuid();
        var managerId = await CreateUserAsync(fx, root, manager);
        await GrantAsync(fx, managerId, false, ("prvWritesystemuser", DepthMask.Global), ("prvWriterole", DepthMask.Global));
        var denied = await SendAsync(HttpMethod.Post, $"systemusers({target})/systemuserroles_association/$ref", Body(Url("roles", escalating)), manager);
        await AssertErrorAsync(denied, HttpStatusCode.Forbidden, "0x80048d3c");

        var revoke = await SendAsync(HttpMethod.Delete, $"systemusers({target})/systemuserroles_association({harmless})/$ref");
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent, await revoke.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ref_on_collection_valued_one_to_many_sets_the_lookup()
    {
        var parent = await _admin.CreateAsync(new Entity(TestEntities.PipeParent) { ["pb_name"] = $"ref-{_run}" });
        var pipe = await _admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = $"ref-{_run}" });

        var res = await SendAsync(HttpMethod.Post, $"pb_pipeparents({parent})/{TestEntities.PipeParentRelationship}/$ref", Body(Url(TestEntities.PipeSet, pipe)));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await _admin.RetrieveAsync(TestEntities.Pipe, pipe, new Microsoft.Xrm.Sdk.Query.ColumnSet("pb_parentid")))
            .GetAttributeValue<EntityReference>("pb_parentid").Id.Should().Be(parent);

        var del = await SendAsync(HttpMethod.Delete, $"pb_pipeparents({parent})/{TestEntities.PipeParentRelationship}({pipe})/$ref");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent, await del.Content.ReadAsStringAsync());
        (await _admin.RetrieveAsync(TestEntities.Pipe, pipe, new Microsoft.Xrm.Sdk.Query.ColumnSet("pb_parentid")))
            .GetAttributeValue<EntityReference>("pb_parentid").Should().BeNull();
    }

    [Fact]
    public async Task Ref_with_unknown_navigation_is_400()
    {
        var (project, tag) = await NewPairAsync();

        var post = await SendAsync(HttpMethod.Post, $"{TestEntities.ProjectSet}({project})/no_such_relationship/$ref", Body(Url(TestEntities.TagSet, tag)));
        var del = await SendAsync(HttpMethod.Delete, $"{TestEntities.ProjectSet}({project})/no_such_relationship({tag})/$ref");
        var badBody = await SendAsync(HttpMethod.Post, $"{TestEntities.ProjectSet}({project})/{TestEntities.ProjectTag}/$ref", "{}");

        await AssertErrorAsync(post, HttpStatusCode.BadRequest);
        await AssertErrorAsync(del, HttpStatusCode.BadRequest);
        await AssertErrorAsync(badBody, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ref_to_a_record_of_the_wrong_entity_is_400_and_writes_nothing()
    {
        var (project, tag) = await NewPairAsync();
        var other = await _admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = $"ref-other-{_run}" });
        var rel = $"{TestEntities.ProjectSet}({project})/{TestEntities.ProjectTag}/$ref";

        var post = await SendAsync(HttpMethod.Post, rel, Body(Url(TestEntities.ProjectSet, other)));
        var del = await SendAsync(HttpMethod.Delete, $"{rel}?$id={Uri.EscapeDataString(Url(TestEntities.ProjectSet, other))}");

        await AssertErrorAsync(post, HttpStatusCode.BadRequest);
        await AssertErrorAsync(del, HttpStatusCode.BadRequest);
        (await LinkCountAsync(project, tag)).Should().Be(0);
    }

    [Fact]
    public async Task Ref_from_the_child_side_of_a_one_to_many_is_400_and_writes_nothing()
    {
        var parent = await _admin.CreateAsync(new Entity(TestEntities.PipeParent) { ["pb_name"] = $"ref-{_run}" });
        var pipe = await _admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = $"ref-{_run}" });

        var res = await SendAsync(HttpMethod.Post, $"{TestEntities.PipeSet}({pipe})/{TestEntities.PipeParentRelationship}/$ref",
            Body(Url("pb_pipeparents", parent)));

        await AssertErrorAsync(res, HttpStatusCode.BadRequest);
        (await _admin.RetrieveAsync(TestEntities.Pipe, pipe, new Microsoft.Xrm.Sdk.Query.ColumnSet("pb_parentid")))
            .GetAttributeValue<EntityReference>("pb_parentid").Should().BeNull();
    }

    [Theory]
    [InlineData("POST", "pb_projects(not-a-guid)/new_project_new_tag/$ref", true)]
    [InlineData("POST", "pb_projects(pb_name='x')/new_project_new_tag/$ref", true)]
    [InlineData("DELETE", "pb_projects(not-a-guid)/new_project_new_tag/$ref?$id=ID", false)]
    [InlineData("DELETE", "pb_projects(PROJECT)/new_project_new_tag(not-a-guid)/$ref", false)]
    [InlineData("DELETE", "pb_projects(PROJECT)/new_project_new_tag(new_name='x')/$ref", false)]
    [InlineData("DELETE", "pb_projects(PROJECT)/new_project_new_tag/$ref?$id=ID_BAD", false)]
    [InlineData("POST", "pb_projects(PROJECT)/new_project_new_tag/$ref", true)]
    public async Task Malformed_ids_are_400(string method, string path, bool withBody)
    {
        var (project, tag) = await NewPairAsync();
        path = path.Replace("PROJECT", project.ToString()).Replace("ID_BAD", Uri.EscapeDataString($"{Api}new_tags(not-a-guid)"))
            .Replace("ID", Uri.EscapeDataString(Url(TestEntities.TagSet, tag)));
        // The last POST case carries an @odata.id with a bad GUID.
        var body = withBody ? Body(path.StartsWith("pb_projects(" + project, StringComparison.Ordinal) ? $"{Api}new_tags(not-a-guid)" : Url(TestEntities.TagSet, tag)) : null;

        var res = await SendAsync(new HttpMethod(method), path, body);

        await AssertErrorAsync(res, HttpStatusCode.BadRequest);
    }
}
