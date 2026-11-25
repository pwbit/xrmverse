using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Security;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>X.12a, Task 4: the connector's design-time endpoints under /api/connector/v1.</summary>
[Collection("real-export")]
public sealed class ConnectorEndpointTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Root = "/api/connector/v1";
    private readonly List<Guid> _seededApis = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Removes the custom APIs a test seeded: the collection's RealExportImportTests count them.</summary>
    public async Task DisposeAsync()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        foreach (var id in _seededApis) await admin.DeleteAsync(Xrmd.Metadata.CustomApiTables.Api, id);
        _seededApis.Clear();
        fx.App.Services.GetRequiredService<CustomApiRegistry>().Invalidate();
    }

    private async Task<(HttpStatusCode Status, string Text)> GetAsync(string path, Guid? objectId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Root + path);
        if (objectId is { } oid) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        response.Content.Headers.ContentType?.MediaType.Should().Be(response.IsSuccessStatusCode ? "application/json" : response.Content.Headers.ContentType?.MediaType);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<Guid> UserOidAsync(bool withRole, bool disabled = false)
    {
        var bu = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, bu, oid, disabled);
        if (withRole) await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        else
        {
            var role = await CreateRoleAsync(fx, "connector-ep-" + Guid.NewGuid().ToString("N")[..8], ("prvReadcontact", DepthMask.Global));
            await AssignRoleAsync(fx, userId, role);
        }
        return oid;
    }

    [Fact]
    public async Task No_token_is_401() => (await GetAsync("/tables", null)).Status.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task User_without_role_is_403_on_tables()
    {
        var bu = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        await CreateUserAsync(fx, bu, oid);
        (await GetAsync("/tables", oid)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Disabled_user_is_403()
    {
        var oid = await UserOidAsync(true, disabled: true);
        (await GetAsync("/tables", oid)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tables_returns_value_array_with_camelCase_fields()
    {
        var (status, text) = await GetAsync("/tables", fx.AdminObjectId);
        status.Should().Be(HttpStatusCode.OK);
        var account = JsonNode.Parse(text)!["value"]!.AsArray().Single(t => (string?)t!["logicalName"] == "account")!;
        ((string?)account["entitySetName"]).Should().Be("accounts");
        ((string?)account["displayName"]).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Schema_by_logical_or_entity_set_name_is_the_same()
    {
        var (s1, byLogical) = await GetAsync("/tables/account/schema?use=read", fx.AdminObjectId);
        var (s2, bySet) = await GetAsync("/tables/accounts/schema?use=read", fx.AdminObjectId);
        s1.Should().Be(HttpStatusCode.OK);
        s2.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(byLogical)!["properties"]!.AsObject().Count.Should().BeGreaterThan(1, byLogical[..Math.Min(300, byLogical.Length)]);
        JsonNode.DeepEquals(JsonNode.Parse(byLogical), JsonNode.Parse(bySet)).Should().BeTrue();
    }

    [Fact]
    public async Task Real_account_schema_offers_name_and_hides_server_managed_columns_on_create()
    {
        var (_, read) = await GetAsync("/tables/account/schema?use=read", fx.AdminObjectId);
        JsonNode.Parse(read)!["properties"]!["name"].Should().NotBeNull();
        var (_, create) = await GetAsync("/tables/account/schema?use=create", fx.AdminObjectId);
        var props = JsonNode.Parse(create)!["properties"]!.AsObject();
        props.ContainsKey("name").Should().BeTrue();
        props.ContainsKey("accountid").Should().BeFalse();
        props.ContainsKey("createdon").Should().BeFalse(create);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?use=bogus")]
    public async Task Schema_with_bad_use_is_400(string query) =>
        (await GetAsync("/tables/account/schema" + query, fx.AdminObjectId)).Status.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Schema_of_unreadable_table_is_403()
    {
        var oid = await UserOidAsync(false);
        (await GetAsync("/tables/account/schema?use=read", oid)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync("/tables/accounts/schema?use=read", oid)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Customapis_and_schema_round_trip()
    {
        _seededApis.AddRange(await CustomApiTestSolution.CreateAsync(ServiceFor(fx, fx.AdminObjectId),
            new TestApi("xt_ce_Plain", Requests: [("Text", 10, false, null)], Responses: [("Out", 10)])));
        fx.App.Services.GetRequiredService<CustomApiRegistry>().Invalidate();

        var (status, text) = await GetAsync("/customapis", fx.AdminObjectId);
        status.Should().Be(HttpStatusCode.OK);
        var api = JsonNode.Parse(text)!["value"]!.AsArray().Single(a => (string?)a!["uniqueName"] == "xt_ce_Plain")!;
        ((string?)api["displayName"]).Should().Be("xt_ce_Plain");

        var (rs, request) = await GetAsync("/customapis/xt_ce_Plain/schema?part=request", fx.AdminObjectId);
        rs.Should().Be(HttpStatusCode.OK);
        ((string?)JsonNode.Parse(request)!["properties"]!["Text"]!["type"]).Should().Be("string");
        var (ps, response) = await GetAsync("/customapis/xt_ce_Plain/schema?part=response", fx.AdminObjectId);
        ps.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(response)!["properties"]!["Out"].Should().NotBeNull();
        (await GetAsync("/customapis/xt_ce_Plain/schema?part=nope", fx.AdminObjectId)).Status.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync("/customapis/xt_ce_Missing/schema?part=request", fx.AdminObjectId)).Status.Should().Be(HttpStatusCode.NotFound);
    }
}
