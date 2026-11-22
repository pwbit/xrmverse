using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #119 (ruling R2): <c>userentityuisettings</c> keeps each user's last form of a table. A Basic User creates, reads and updates
/// only its own rows; an organization seeded before the table gets it, and the Basic User grants, at the next start.
/// </summary>
[Collection("xrmd")]
public sealed class UserEntityUiSettingsTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Set = "userentityuisettingsset";

    private static string Mru(Guid formId) => $"<MRUForm><Form Type=\"Main\" Id=\"{{{formId}}}\" /></MRUForm>";

    private async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(HttpMethod method, string path, Guid objectId, string? json = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private async Task NewBasicUserAsync(Guid objectId)
    {
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
    }

    private static Guid IdOf(HttpResponseMessage response)
    {
        var entityId = response.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(entityId[(entityId.LastIndexOf('(') + 1)..^1]);
    }

    [Fact]
    public async Task Basic_user_keeps_its_own_last_form()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await NewBasicUserAsync(alice);
        await NewBasicUserAsync(bob);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var (created, body, response) = await SendAsync(HttpMethod.Post, Set, alice,
            $$"""{"objecttypecode":"account","lastviewedformxml":{{System.Text.Json.JsonSerializer.Serialize(Mru(first))}}}""");
        created.Should().Be(HttpStatusCode.NoContent, body);
        var mine = IdOf(response);
        var (patched, patchBody, _) = await SendAsync(HttpMethod.Patch, $"{Set}({mine})", alice,
            $$"""{"lastviewedformxml":{{System.Text.Json.JsonSerializer.Serialize(Mru(second))}}}""");
        patched.Should().Be(HttpStatusCode.NoContent, patchBody);

        var (read, list, _) = await SendAsync(HttpMethod.Get, $"{Set}?$select=objecttypecode,lastviewedformxml&$filter=objecttypecode eq 'account'", alice);
        read.Should().Be(HttpStatusCode.OK, list);
        var rows = JsonNode.Parse(list)!["value"]!.AsArray();
        rows.Should().ContainSingle();
        rows[0]!["lastviewedformxml"]!.GetValue<string>().Should().Be(Mru(second));

        // Bob neither lists nor changes Alice's row.
        var (bobRead, bobList, _) = await SendAsync(HttpMethod.Get, $"{Set}?$select=objecttypecode", bob);
        bobRead.Should().Be(HttpStatusCode.OK, bobList);
        JsonNode.Parse(bobList)!["value"]!.AsArray().Should().BeEmpty();
        var (bobPatch, bobPatchBody, _) = await SendAsync(HttpMethod.Patch, $"{Set}({mine})", bob, """{"lastviewedformxml":"<MRUForm />"}""");
        new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden }.Should().Contain(bobPatch, bobPatchBody);
    }

    [Fact]
    public async Task Seed_upgrade_adds_the_table_and_the_grants_to_an_existing_database()
    {
        var database = "xrmd_uiset_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        try
        {
            var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
            await new SchemaMigrator(factory).MigrateAsync();
            var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
            await new SystemSeed(await StoreAsync(factory), factory).EnsureAsync(options);

            // An organization seeded before #119.
            await ExecuteAsync(factory, """
                DELETE FROM meta.relationship WHERE JSON_VALUE(definition, '$.ReferencingEntity') = 'userentityuisettings';
                DELETE FROM meta.attribute WHERE entity_logical_name = 'userentityuisettings';
                DELETE FROM meta.entity WHERE logical_name = 'userentityuisettings';
                DROP TABLE data.userentityuisettings;
                """);
            (await StoreAsync(factory)).GetEntity(AppModuleSchema.UserEntityUiSettings).Should().BeNull();

            await new SystemSeed(await StoreAsync(factory), factory).EnsureAsync(options);

            var store = await StoreAsync(factory);
            store.GetEntity(AppModuleSchema.UserEntityUiSettings)!.Attributes.Should().Contain(a => a.LogicalName == "lastviewedformxml");
        }
        finally
        {
            await fx.DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Basic_user_top_up_reaches_an_existing_organization()
    {
        const string step = "user-entity-ui-settings-privileges";
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var read = PrivilegeNames.IdOf(PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(AppModuleSchema.UserEntityUiSettings)!));
        var basic = $"roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{read}'";
        await ScalarAsync($"DELETE FROM data.roleprivileges WHERE {basic}; DELETE FROM meta.upgrade_step WHERE name = N'{step}';");
        try
        {
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync($"SELECT privilegedepthmask FROM data.roleprivileges WHERE {basic}")).Should().Be(1);
            (await ScalarAsync($"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);
        }
        finally
        {
            await ScalarAsync($"""
                IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE {basic})
                    INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                    VALUES (NEWID(), '{StandardRoles.BasicUser}', '{read}', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """);
            Cache(fx).Invalidate();
        }
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task<MetadataStore> StoreAsync(IDbConnectionFactory factory)
    {
        var store = new MetadataStore(factory);
        await store.LoadAsync();
        return store;
    }

    private static async Task ExecuteAsync(IDbConnectionFactory factory, string sql)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
