using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1a, task 1 (needs a database): the seeded app module tables, <c>userquery</c> and the user settings columns, as a
/// Basic User and an administrator meet them through the services and the Web API.
/// </summary>
[Collection("xrmd")]
public sealed class AppModuleTablesTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<Guid> NewBasicUserAsync(Guid objectId)
    {
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        return userId;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, Guid objectId, string? json = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    [Fact]
    public void Server_has_the_app_module_tables()
    {
        Store.GetEntityByObjectTypeCode(AppModuleSchema.UserQueryTypeCode)!.LogicalName.Should().Be(AppModuleSchema.UserQuery);
        Store.GetEntityBySetName("appmodulecomponents")!.LogicalName.Should().Be(AppModuleSchema.Component);
        Store.GetEntity(AppModuleSchema.RolesIntersect)!.IsIntersect.Should().BeTrue();
        Store.GetEntity("appmodule")!.Attributes.Should().Contain(a => a.LogicalName == "appmoduleidunique")
            .And.Contain(a => a.LogicalName == "publisherid").And.Contain(a => a.LogicalName == "statecode");
        Store.GetEntity("usersettings")!.Attributes.Should().Contain(a => a.LogicalName == "uilanguageid")
            .And.Contain(a => a.LogicalName == AppModuleSchema.PagingLimit);
        Store.GetRelationship(AppModuleSchema.ComponentToApp).Should().NotBeNull();
    }

    [Fact]
    public async Task Basic_user_reads_apps_and_owns_userqueries()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await NewBasicUserAsync(alice);
        await NewBasicUserAsync(bob);

        // Reads the apps, their components, the site maps and the views.
        foreach (var set in new[] { "appmodules", "appmodulecomponents", "sitemaps", "savedqueries", "appmodulerolescollection" })
        {
            var (status, body) = await SendAsync(HttpMethod.Get, set + "?$top=1", alice);
            status.Should().Be(HttpStatusCode.OK, set + ": " + body);
        }

        var mine = await ServiceFor(fx, alice).CreateAsync(new Entity(AppModuleSchema.UserQuery)
        {
            ["name"] = "Alice's accounts", ["returnedtypecode"] = "account", ["fetchxml"] = "<fetch><entity name=\"account\"/></fetch>",
            ["querytype"] = 0,
        });
        var theirs = await ServiceFor(fx, bob).CreateAsync(new Entity(AppModuleSchema.UserQuery)
        {
            ["name"] = "Bob's accounts", ["returnedtypecode"] = "account", ["fetchxml"] = "<fetch><entity name=\"account\"/></fetch>",
            ["querytype"] = 0,
        });

        var row = await ServiceFor(fx, alice).RetrieveAsync(AppModuleSchema.UserQuery, mine, new ColumnSet("name", "statecode", "statuscode"));
        row.GetAttributeValue<string>("name").Should().Be("Alice's accounts");
        (row.GetAttributeValue<OptionSetValue>("statecode").Value, row.GetAttributeValue<OptionSetValue>("statuscode").Value).Should().Be((0, 1));

        var (status1, list) = await SendAsync(HttpMethod.Get, "userqueries?$select=name", alice);
        status1.Should().Be(HttpStatusCode.OK, list);
        JsonNode.Parse(list)!["value"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Contain("Alice's accounts")
            .And.NotContain("Bob's accounts");
        var (status2, body2) = await SendAsync(HttpMethod.Get, $"userqueries({theirs})?$select=name", alice);
        new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden }.Should().Contain(status2, body2);

        // An administrator reads both.
        var (statusAdmin, admin) = await SendAsync(HttpMethod.Get, "userqueries?$select=name", fx.AdminObjectId);
        statusAdmin.Should().Be(HttpStatusCode.OK, admin);
        JsonNode.Parse(admin)!["value"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Contain(["Alice's accounts", "Bob's accounts"]);
    }

    /// <summary>
    /// Plan 3.5 UI-1b final review, C1: Basic User reads the forms (Read System Form at organization depth, as in Dataverse), so a
    /// user without System Customizer opens a record's form.
    /// </summary>
    [Fact]
    public async Task Basic_user_reads_system_forms()
    {
        var alice = Guid.NewGuid();
        await NewBasicUserAsync(alice);

        var (status, body) = await SendAsync(HttpMethod.Get, "systemforms?$select=systemformid,name,formxml&$top=1", alice);

        status.Should().Be(HttpStatusCode.OK, body);
    }

    /// <summary>
    /// An organization from before plan 3.5 UI-1b (Basic User without Read System Form, no top-up recorded): startup grants it
    /// once at Global depth; a grant removed afterwards stays removed.
    /// </summary>
    [Fact]
    public async Task System_form_top_up_reaches_an_existing_organization()
    {
        const string step = "system-form-role-privileges";
        var read = PrivilegeNames.IdOf(PrivilegeNames.For(AccessRights.ReadAccess, Store.GetEntity("systemform")!));
        var basic = $"roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{read}'";
        await ScalarAsync(fx.ConnectionString, $"DELETE FROM data.roleprivileges WHERE {basic}; DELETE FROM meta.upgrade_step WHERE name = N'{step}';");
        try
        {
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync(fx.ConnectionString, $"SELECT privilegedepthmask FROM data.roleprivileges WHERE {basic}")).Should().Be(8);
            (await ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);

            // Once: a grant removed afterwards stays removed.
            await ScalarAsync(fx.ConnectionString, $"DELETE FROM data.roleprivileges WHERE {basic}");
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic}")).Should().Be(0);
        }
        finally
        {
            await ScalarAsync(fx.ConnectionString, $"""
                IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE {basic})
                    INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                    VALUES (NEWID(), '{StandardRoles.BasicUser}', '{read}', 8, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """);
            Cache(fx).Invalidate();
        }
    }

    [Theory]
    [InlineData("appmodulecomponents", """{ "componenttype": 1 }""")]
    [InlineData("appmodulerolescollection", """{ }""")]
    public async Task App_module_tables_are_import_only(string set, string body)
    {
        var (status, text) = await SendAsync(HttpMethod.Post, set, fx.AdminObjectId, body);
        status.Should().Be(HttpStatusCode.BadRequest, text);
        JsonNode.Parse(text)!["error"]!["message"]!.GetValue<string>().Should().Be(AppModuleSchema.ImportOnly);
    }

    [Fact]
    public async Task Unmodelled_userquery_column_is_refused()
    {
        var alice = Guid.NewGuid();
        await NewBasicUserAsync(alice);
        var (status, body) = await SendAsync(HttpMethod.Post, "userqueries", alice,
            """{ "name": "v", "returnedtypecode": "account", "fetchxml": "<fetch/>", "querytype": 0, "layoutjson": "{}" }""");
        status.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Contain("layoutjson");
    }

    [Fact]
    public async Task User_settings_default_to_fifty_rows_per_page()
    {
        // A user the Core creates (the test helper inserts users with SQL, which writes no settings row).
        var user = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Pat Paging", ["domainname"] = "pat.paging@example.invalid", ["azureactivedirectoryobjectid"] = Guid.NewGuid(),
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        (await ScalarAsync(fx.ConnectionString, $"SELECT paginglimit FROM data.usersettings WHERE systemuserid = '{user}'")).Should().Be(50);
    }

    // ---- plan 3.5 UI-1a final review, I3: deleting an app or a role ----------------------------------------------------

    /// <summary>An app module with one component (written as the import writes it, internally) and a link to <paramref name="roleId"/>.</summary>
    private async Task<Guid> NewAppWithLinksAsync(Guid roleId)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var appId = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("appmodule") { ["name"] = "App " + run, ["uniquename"] = "new_app" + run });
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<Xrmd.Core.CoreOperations>();
        await TestEntities.InSessionAsync(fx.ConnectionString, s => core.ExecuteCoreAsync(new Microsoft.Xrm.Sdk.Messages.CreateRequest
        {
            Target = new Entity(AppModuleSchema.Component, Guid.NewGuid())
            {
                ["appmoduleidunique"] = new EntityReference("appmodule", appId),
                ["componenttype"] = new OptionSetValue(AppModuleSchema.ComponentType.Entity),
                ["objectid"] = Store.GetEntity("account")!.MetadataId!.Value,
                ["rootcomponentbehavior"] = new OptionSetValue(0),
                ["ismetadata"] = true,
                ["exempt"] = false,
            },
        }, Caller.Internal, s, default));
        var key = SystemAttributes.PrimaryIdName(Store.GetEntity(AppModuleSchema.RolesIntersect)!);
        await ScalarAsync(fx.ConnectionString, $"""
            INSERT INTO data.{AppModuleSchema.RolesIntersect} ([{key}], appmoduleid, roleid, createdon, modifiedon, statecode, statuscode)
            VALUES (NEWID(), '{appId}', '{roleId}', SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """);
        return appId;
    }

    private async Task<int> CountAsync(string table, string column, Guid id) =>
        Convert.ToInt32(await ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.{table} WHERE {column} = '{id}'"));

    [Fact]
    public async Task Delete_app_module_removes_components_and_role_links()
    {
        var roleId = await CreateRoleAsync(fx, "App role " + Guid.NewGuid().ToString("N")[..8]);
        var appId = await NewAppWithLinksAsync(roleId);
        (await CountAsync(AppModuleSchema.Component, "appmoduleidunique", appId)).Should().Be(1);
        (await CountAsync(AppModuleSchema.RolesIntersect, "appmoduleid", appId)).Should().Be(1);

        // A component cannot be deleted on its own through the API (import-only)...
        var component = (Guid)(await ScalarAsync(fx.ConnectionString,
            $"SELECT appmodulecomponentid FROM data.{AppModuleSchema.Component} WHERE appmoduleidunique = '{appId}'"))!;
        var (refused, refusedBody) = await SendAsync(HttpMethod.Delete, $"appmodulecomponents({component})", fx.AdminObjectId);
        refused.Should().Be(HttpStatusCode.BadRequest, refusedBody);
        JsonNode.Parse(refusedBody)!["error"]!["message"]!.GetValue<string>().Should().Be(AppModuleSchema.ImportOnly);

        // ...but the app's Delete takes its components (cascade) and its role links with it.
        var (status, body) = await SendAsync(HttpMethod.Delete, $"appmodules({appId})", fx.AdminObjectId);
        status.Should().Be(HttpStatusCode.NoContent, body);
        (await CountAsync("appmodule", "appmoduleid", appId)).Should().Be(0);
        (await CountAsync(AppModuleSchema.Component, "appmoduleidunique", appId)).Should().Be(0);
        (await CountAsync(AppModuleSchema.RolesIntersect, "appmoduleid", appId)).Should().Be(0);
    }

    [Fact]
    public async Task Delete_role_removes_app_role_links()
    {
        var roleId = await CreateRoleAsync(fx, "App role " + Guid.NewGuid().ToString("N")[..8]);
        var appId = await NewAppWithLinksAsync(roleId);

        var (status, body) = await SendAsync(HttpMethod.Delete, $"roles({roleId})", fx.AdminObjectId);
        status.Should().Be(HttpStatusCode.NoContent, body);
        (await CountAsync(AppModuleSchema.RolesIntersect, "roleid", roleId)).Should().Be(0);
        (await CountAsync("appmodule", "appmoduleid", appId)).Should().Be(1); // the app stays
    }
}
