using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>Plan 3.5 UI-1c, task 1: <c>GET /WebResources/{name}</c> serves a web resource with its type, ETag and read check.</summary>
[Collection("real-export")]
public sealed class WebResourceEndpointTests(XrmdFixture fx)
{
    private const string Script = "op365_/scripts/account.form.js";
    private const string Page = "op365_/pages/decision.html";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>
    /// The two web resources the tests read (plan 2.7a, Task 6: made through the SDK instead of importing the OpenProject365
    /// export, so that the class runs on PostgreSQL too), with their original content again when a test changed it.
    /// </summary>
    private async Task ImportAsync()
    {
        await EnsureWebResourceAsync(Script, 3, "function onLoad(executionContext) { return executionContext; }");
        await EnsureWebResourceAsync(Page, 1, "<!DOCTYPE html><html><head><title>Decision</title></head><body></body></html>");
    }

    private async Task EnsureWebResourceAsync(string name, int type, string content)
    {
        var q = new Microsoft.Xrm.Sdk.Query.QueryExpression("webresource") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(false) };
        q.Criteria.AddCondition("name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, name);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        if ((await Admin.RetrieveMultipleAsync(q)).Entities.SingleOrDefault() is { } existing)
            await Admin.UpdateAsync(new Entity("webresource", existing.Id) { ["content"] = encoded });
        else
            await Admin.CreateAsync(new Entity("webresource")
            {
                ["name"] = name, ["displayname"] = name, ["webresourcetype"] = new OptionSetValue(type), ["content"] = encoded,
            });
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Guid? objectId = null, string? ifNoneMatch = null, bool anonymous = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (!anonymous) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await fx.Client.SendAsync(req);
    }

    private async Task<Guid> IdOfAsync(string name)
    {
        var q = new Microsoft.Xrm.Sdk.Query.QueryExpression("webresource") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(false) };
        q.Criteria.AddCondition("name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, name);
        return (await Admin.RetrieveMultipleAsync(q)).Entities.Single().Id;
    }

    [Fact]
    public async Task Serves_script_with_type_and_utf8()
    {
        await ImportAsync();
        var response = await GetAsync("/WebResources/" + Script);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/javascript; charset=utf-8");
        (await response.Content.ReadAsStringAsync()).Should().Contain("function");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Headers.CacheControl!.Private.Should().BeTrue();
    }

    [Fact]
    public async Task Serves_html_page()
    {
        await ImportAsync();
        var response = await GetAsync("/WebResources/" + Page);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("<html");
    }

    [Fact]
    public async Task Name_is_case_insensitive()
    {
        await ImportAsync();
        (await GetAsync("/WebResources/" + Script.ToUpperInvariant())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/%7b000000000000001%7d/WebResources/" + Page)]
    [InlineData("/{000000000000001}/WebResources/" + Page)]
    public async Task Cache_busting_prefix_is_accepted(string path)
    {
        await ImportAsync();
        var response = await GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Etag_and_304()
    {
        await ImportAsync();
        var first = await GetAsync("/WebResources/" + Script);
        var etag = first.Headers.ETag!.ToString();
        etag.Should().StartWith("W/\"");

        var second = await GetAsync("/WebResources/" + Script, ifNoneMatch: etag);
        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        second.Headers.ETag!.ToString().Should().Be(etag);

        await Admin.UpdateAsync(new Entity("webresource", await IdOfAsync(Script))
            { ["content"] = Convert.ToBase64String("// changed"u8.ToArray()) });
        var third = await GetAsync("/WebResources/" + Script, ifNoneMatch: etag);
        third.StatusCode.Should().Be(HttpStatusCode.OK);
        third.Headers.ETag!.ToString().Should().NotBe(etag);
        (await third.Content.ReadAsStringAsync()).Should().Be("// changed");
    }

    [Fact]
    public async Task Unknown_is_404()
    {
        await ImportAsync();
        (await GetAsync("/WebResources/op365_/scripts/none.js")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Xap_is_415()
    {
        await ImportAsync();
        await Admin.CreateAsync(new Entity("webresource")
        {
            ["name"] = "op365_/bin/app" + Guid.NewGuid().ToString("N")[..6] + ".xap", ["webresourcetype"] = new OptionSetValue(8),
            ["content"] = Convert.ToBase64String([1, 2, 3]),
        });
        var created = (await Admin.RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression("webresource")
            { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("name"), Criteria = { Conditions = { new Microsoft.Xrm.Sdk.Query.ConditionExpression("webresourcetype", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, 8) } } }))
            .Entities.First().GetAttributeValue<string>("name");

        (await GetAsync("/WebResources/" + created)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Needs_read_on_webresource()
    {
        await ImportAsync();
        var root = await RootBusinessUnitAsync(fx);
        var none = Guid.NewGuid();
        await CreateUserAsync(fx, root, none);
        var basic = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, basic), StandardRoles.BasicUser);

        (await GetAsync("/WebResources/" + Script, none)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync("/WebResources/" + Script, basic)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Ruling R14 (plan 3.5 UI-1c, task 5): the app shell reads the Client API's global context for every user. A Basic User reads
    /// the organization, its own systemuser row, its role assignments and the names of its roles, with the shell's own queries.
    /// </summary>
    [Fact]
    public async Task Basic_user_reads_the_client_api_global_context()
    {
        var root = await RootBusinessUnitAsync(fx);
        var objectId = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, root, objectId);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);

        async Task<System.Text.Json.JsonElement> ReadAsync(string path)
        {
            var response = await GetAsync("/api/data/v9.2/" + path, objectId);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            return System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        var org = await ReadAsync("organizations?$select=organizationid,name,_basecurrencyid_value&$top=1");
        org.GetProperty("value").GetArrayLength().Should().Be(1);
        (await ReadAsync($"systemusers({userId})?$select=fullname")).TryGetProperty("fullname", out _).Should().BeTrue();
        var assigned = await ReadAsync($"systemuserrolescollection?$select=roleid&$filter=systemuserid eq {userId}");
        var roleId = assigned.GetProperty("value")[0].GetProperty("roleid").GetString();
        roleId.Should().NotBeNullOrEmpty();
        var roles = await ReadAsync($"roles?$select=roleid,name&$filter=roleid eq {roleId}");
        roles.GetProperty("value")[0].GetProperty("name").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        await ImportAsync();
        (await GetAsync("/WebResources/" + Script, anonymous: true)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync("/{1}/WebResources/" + Script, anonymous: true)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Nosniff_header()
    {
        await ImportAsync();
        var response = await GetAsync("/WebResources/" + Script);
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
    }

    /// <summary>The scalar of <paramref name="sql"/>; a count as <see cref="int"/> (bigint on PostgreSQL).</summary>
    private static async Task<object?> ScalarAsync(string connectionString, string sql) =>
        await TestSql.ScalarAsync(connectionString, sql) switch { long count => (int)count, var value => value };

    /// <summary>
    /// An organization from before plan 3.5 UI-1c (Basic User without Read Web Resource, no top-up recorded): startup grants it
    /// once at Global depth; running the bootstrap again changes nothing, even after the grant was removed.
    /// </summary>
    [Fact]
    public async Task Web_resource_top_up_reaches_an_existing_organization_once()
    {
        const string step = "web-resource-role-privileges";
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var read = PrivilegeNames.IdOf(PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity("webresource")!));
        var basic = $"roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{read}'";
        await ScalarAsync(fx.ConnectionString, $"DELETE FROM data.roleprivileges WHERE {basic}");
        await ScalarAsync(fx.ConnectionString, $"DELETE FROM meta.upgrade_step WHERE name = N'{step}'");
        try
        {
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync(fx.ConnectionString, $"SELECT privilegedepthmask FROM data.roleprivileges WHERE {basic}")).Should().Be(8);
            (await ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);

            // Second run: the row and the step record are unchanged.
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic}")).Should().Be(1);
            (await ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);

            // Once: a grant removed afterwards stays removed.
            await ScalarAsync(fx.ConnectionString, $"DELETE FROM data.roleprivileges WHERE {basic}");
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic}")).Should().Be(0);
        }
        finally
        {
            await ScalarAsync(fx.ConnectionString, $"""
                INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                SELECT NEWID(), '{StandardRoles.BasicUser}', '{read}', 8, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
                WHERE NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE {basic})
                """);
            Cache(fx).Invalidate();
        }
    }
}
