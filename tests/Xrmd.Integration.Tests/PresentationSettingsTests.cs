using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// UI-2a (needs a database): the environment values of the settings (<c>organizationsetting</c>), read by every user and written by
/// administrators and customizers, and the renderer and color scheme a user keeps on its own <c>usersettings</c> row.
/// </summary>
[Collection("xrmd")]
public sealed class PresentationSettingsTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, Guid objectId, string? json = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(Guid UserId, Guid ObjectId)> NewBasicUserAsync()
    {
        var objectId = Guid.NewGuid();
        var run = Guid.NewGuid().ToString("N")[..8];
        var userId = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Pia Look " + run, ["domainname"] = $"pia.{run}@example.invalid", ["azureactivedirectoryobjectid"] = objectId,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        return (userId, objectId);
    }

    /// <summary>A definition of the test's own, so that the tests do not share the one-value-per-definition rule.</summary>
    private async Task<Guid> NewDefinitionAsync()
    {
        var name = "xrmverse_Test" + Guid.NewGuid().ToString("N")[..8];
        var (status, body) = await SendAsync(HttpMethod.Post, "settingdefinitions", fx.AdminObjectId,
            $$"""{ "uniquename": "{{name}}", "displayname": "{{name}}", "defaultvalue": "x" }""");
        status.Should().Be(HttpStatusCode.NoContent, body);
        return (Guid)(await ScalarAsync(fx.ConnectionString, $"SELECT settingdefinitionid FROM data.settingdefinition WHERE uniquename = '{name}'"))!;
    }

    private static string ValueBody(string name, Guid definition, string value) =>
        $$"""{ "uniquename": "{{name}}", "displayname": "{{name}}", "value": "{{value}}", "settingdefinitionid@odata.bind": "/settingdefinitions({{definition}})" }""";

    [Fact]
    public async Task Admin_creates_environment_value_and_basic_user_reads_it()
    {
        var definition = await NewDefinitionAsync();
        var (status, body) = await SendAsync(HttpMethod.Post, "organizationsettings", fx.AdminObjectId, ValueBody("xrmverse_Renderer", definition, "modern"));
        status.Should().Be(HttpStatusCode.NoContent, body);

        var (_, objectId) = await NewBasicUserAsync();
        (status, body) = await SendAsync(HttpMethod.Get,
            $"organizationsettings?$select=uniquename,value,_settingdefinitionid_value&$filter=_settingdefinitionid_value eq {definition}", objectId);
        status.Should().Be(HttpStatusCode.OK, body);
        var rows = JsonNode.Parse(body)!["value"]!.AsArray();
        rows.Should().ContainSingle();
        rows[0]!["value"]!.GetValue<string>().Should().Be("modern");
        rows[0]!["_settingdefinitionid_value"]!.GetValue<string>().Should().Be(definition.ToString());
    }

    [Fact]
    public async Task Second_environment_value_for_same_definition_is_rejected()
    {
        var definition = await NewDefinitionAsync();
        var (status, body) = await SendAsync(HttpMethod.Post, "organizationsettings", fx.AdminObjectId, ValueBody("xrmverse_Twice", definition, "a"));
        status.Should().Be(HttpStatusCode.NoContent, body);
        (status, body) = await SendAsync(HttpMethod.Post, "organizationsettings", fx.AdminObjectId, ValueBody("xrmverse_Twice", definition, "b"));
        status.Should().Be(HttpStatusCode.BadRequest, body);
        var error = JsonNode.Parse(body)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be("0x80040237");
        error["message"]!.GetValue<string>().Should().Be("The setting xrmverse_Twice already has an environment value.");
    }

    [Fact]
    public async Task Basic_user_cannot_create_environment_value()
    {
        var definition = await NewDefinitionAsync();
        var (_, objectId) = await NewBasicUserAsync();
        var (status, body) = await SendAsync(HttpMethod.Post, "organizationsettings", objectId, ValueBody("xrmverse_Nope", definition, "a"));
        status.Should().Be(HttpStatusCode.Forbidden, body);
    }

    [Fact]
    public async Task Basic_user_patches_own_renderer_and_colorscheme()
    {
        var (user, objectId) = await NewBasicUserAsync();
        var (status, body) = await SendAsync(HttpMethod.Patch, $"usersettingscollection({user})", objectId,
            """{ "xrmverse_renderer": "modern", "xrmverse_colorscheme": 2 }""");
        status.Should().Be(HttpStatusCode.NoContent, body);
        (status, body) = await SendAsync(HttpMethod.Get, $"usersettingscollection({user})?$select=xrmverse_renderer,xrmverse_colorscheme", objectId);
        status.Should().Be(HttpStatusCode.OK, body);
        var row = JsonNode.Parse(body)!;
        row["xrmverse_renderer"]!.GetValue<string>().Should().Be("modern");
        row["xrmverse_colorscheme"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task New_users_follow_the_app_and_the_system_scheme()
    {
        var (user, _) = await NewBasicUserAsync();
        (await ScalarAsync(fx.ConnectionString, $"SELECT xrmverse_renderer FROM data.usersettings WHERE systemuserid = '{user}'")).Should().BeNull();
        (await ScalarAsync(fx.ConnectionString, $"SELECT xrmverse_colorscheme FROM data.usersettings WHERE systemuserid = '{user}'")).Should().Be(0);
    }

    [Fact]
    public async Task Basic_user_cannot_patch_other_users_settings()
    {
        var (other, _) = await NewBasicUserAsync();
        var (_, objectId) = await NewBasicUserAsync();
        var (status, body) = await SendAsync(HttpMethod.Patch, $"usersettingscollection({other})", objectId, """{ "xrmverse_renderer": "modern" }""");
        status.Should().Be(HttpStatusCode.Forbidden, body);
    }

    /// <summary>A database seeded before the columns: its settings rows follow the app (null renderer) with the System scheme.</summary>
    [Fact]
    public async Task Existing_usersettings_rows_get_null_renderer_and_system_scheme()
    {
        var database = "xrmd_pres_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        try
        {
            await new SchemaMigrator(factory).MigrateAsync();
            async Task<MetadataStore> StoreAsync() { var s = new MetadataStore(factory); await s.LoadAsync(); return s; }
            async Task<object?> Scalar(string sql)
            {
                await using var c = await factory.OpenAsync();
                await using var cmd = TestSql.Command(c, sql);
                var r = await cmd.ExecuteScalarAsync();
                // A COUNT(*) is bigint on PostgreSQL: the counts compare as int.
                return r switch { DBNull => null, long count => checked((int)count), _ => r };
            }
            await new SystemSeed(await StoreAsync(), factory).EnsureAsync(options);

            // A database made before this change: no presentation columns, no organizationsetting table, no definitions.
            await Scalar($"""
                DELETE FROM meta.relationship WHERE JSON_VALUE(definition, '$.ReferencingEntity') = '{AppModuleSchema.OrganizationSetting}';
                DELETE FROM meta.attribute WHERE entity_logical_name = '{AppModuleSchema.OrganizationSetting}';
                DELETE FROM meta.entity WHERE logical_name = '{AppModuleSchema.OrganizationSetting}';
                DROP TABLE data.{AppModuleSchema.OrganizationSetting};
                DELETE FROM data.settingdefinition WHERE uniquename IN ('{AppModuleSchema.Renderer}', '{AppModuleSchema.Presentation}', '{AppModuleSchema.AppOnLoad}');
                DELETE FROM meta.attribute WHERE entity_logical_name = 'usersettings' AND logical_name IN ('xrmverse_renderer', 'xrmverse_colorscheme');
                {TestSql.Dialect.DropColumns("data.usersettings", ["xrmverse_renderer", "xrmverse_colorscheme"])}
                DELETE FROM meta.upgrade_step WHERE name = '{SystemSeed.ColorSchemeDefaultStep}';
                """);

            await new SystemSeed(await StoreAsync(), factory).EnsureAsync(options);

            (await Scalar("SELECT COUNT(*) FROM data.usersettings")).Should().NotBe(0);
            (await Scalar("SELECT COUNT(*) FROM data.usersettings WHERE xrmverse_renderer IS NOT NULL")).Should().Be(0);
            (await Scalar("SELECT COUNT(*) FROM data.usersettings WHERE xrmverse_colorscheme IS NULL OR xrmverse_colorscheme <> 0")).Should().Be(0);
            (await Scalar($"SELECT defaultvalue FROM data.settingdefinition WHERE settingdefinitionid = '{AppModuleSchema.RendererId}'")).Should().Be("classic");
            (await Scalar($"SELECT defaultvalue FROM data.settingdefinition WHERE settingdefinitionid = '{AppModuleSchema.PresentationId}'")).Should().Be("");
            (await Scalar($"SELECT defaultvalue FROM data.settingdefinition WHERE settingdefinitionid = '{AppModuleSchema.TimelinePostsId}'")).Should().Be("false");

            // X.10: the app OnLoad definition is seeded with the well-known id and an empty default, and seeding twice keeps one row.
            string AppOnLoadRow(string column) => $"SELECT {column} FROM data.settingdefinition WHERE uniquename = '{AppModuleSchema.AppOnLoad}'";
            (await Scalar(AppOnLoadRow("settingdefinitionid"))).Should().Be(AppModuleSchema.AppOnLoadId);
            (await Scalar(AppOnLoadRow("defaultvalue"))).Should().Be("");
            await new SystemSeed(await StoreAsync(), factory).EnsureAsync(options);
            (await Scalar($"SELECT COUNT(*) FROM data.settingdefinition WHERE uniquename = '{AppModuleSchema.AppOnLoad}'")).Should().Be(1);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }
}
