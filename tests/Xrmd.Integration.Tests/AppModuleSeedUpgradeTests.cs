using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Core;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1a, task 1: an organization seeded before the app module tables (the plan 3.2 upgrade path of a seeded table)
/// gets them at startup, with the new <c>appmodule</c> and <c>usersettings</c> columns and the default page size; #129 adds the app
/// settings tables and their two definition rows.
/// </summary>
[Collection("xrmd")]
public sealed class AppModuleSeedUpgradeTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private static readonly string[] Tables =
        [AppModuleSchema.Component, AppModuleSchema.RolesIntersect, AppModuleSchema.UserQuery, AppModuleSchema.AppSetting, AppModuleSchema.SettingDefinition];
    private static readonly string[] AppColumns =
        ["appmoduleidunique", "clienttype", "formfactor", "navigationtype", "url", "webresourceid", "welcomepageid", "appmoduleversion",
            "isdefault", "isfeatured", "publisherid"];
    private IDbConnectionFactory _factory = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        _database = "xrmd_app_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, _database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await TestProvider.DropDatabaseAsync(fx.ConnectionString, _database);
    }

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        // A COUNT(*) is bigint on PostgreSQL: the counts compare as int.
        return result switch { DBNull => null, long count => checked((int)count), _ => result };
    }

    private static void AssertSeeded(IMetadataStore store)
    {
        foreach (var table in Tables) store.GetEntity(table).Should().NotBeNull(table);
        foreach (var column in AppColumns) store.GetEntity("appmodule")!.Attributes.Should().Contain(a => a.LogicalName == column, column);
        foreach (var column in new[] { "uilanguageid", AppModuleSchema.PagingLimit })
            store.GetEntity("usersettings")!.Attributes.Should().Contain(a => a.LogicalName == column, column);
        store.GetRelationship(AppModuleSchema.ComponentToApp).Should().NotBeNull();
        store.GetRelationship("publisher_appmodule").Should().NotBeNull();
        store.GetRelationship(AppModuleSchema.AppSettingToApp).Should().NotBeNull();
        store.GetRelationship(AppModuleSchema.AppSettingToDefinition).Should().NotBeNull();
    }

    private Task<object?> DefinitionCountAsync() => ScalarAsync(
        $"SELECT COUNT(*) FROM data.{AppModuleSchema.SettingDefinition} WHERE uniquename IN ('AsyncOnLoad', 'AsyncOnSave', '{AppModuleSchema.TimelineGroupByDate}', '{AppModuleSchema.TimelineRememberFilters}')");

    [Fact]
    public async Task Existing_org_gets_new_tables_at_startup()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        AssertSeeded(await NewStoreAsync());

        // An organization seeded before plan 3.5 UI-1a: none of the three tables, none of the new columns.
        var names = string.Join(", ", Tables.Select(t => $"'{t}'"));
        var columns = string.Join(", ", AppColumns.Select(c => $"'{c}'"));
        await ScalarAsync($"""
            DELETE FROM meta.relationship WHERE JSON_VALUE(definition, '$.ReferencingEntity') IN ({names}, 'appmodule');
            DELETE FROM meta.attribute WHERE entity_logical_name IN ({names});
            DELETE FROM meta.entity WHERE logical_name IN ({names});
            DROP TABLE data.appmodulecomponent; DROP TABLE data.appmoduleroles; DROP TABLE data.userquery;
            DROP TABLE data.appsetting; DROP TABLE data.settingdefinition;
            DELETE FROM meta.attribute WHERE entity_logical_name = 'appmodule' AND logical_name IN ({columns});
            ALTER TABLE data.appmodule DROP COLUMN {string.Join(", ", AppColumns)};
            DELETE FROM meta.attribute WHERE entity_logical_name = 'usersettings' AND logical_name IN ('uilanguageid', 'paginglimit');
            ALTER TABLE data.usersettings DROP COLUMN uilanguageid, paginglimit;
            DELETE FROM meta.upgrade_step WHERE name = '{SystemSeed.PagingLimitDefaultStep}';
            """);
        var stale = await NewStoreAsync();
        foreach (var table in Tables) stale.GetEntity(table).Should().BeNull(table);
        stale.GetEntity("usersettings")!.Attributes.Should().NotContain(a => a.LogicalName == AppModuleSchema.PagingLimit);

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        AssertSeeded(stale);
        AssertSeeded(await NewStoreAsync());
        // The settings rows of the existing users get the default page size.
        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings WHERE paginglimit IS NULL")).Should().Be(0);
        (await ScalarAsync("SELECT MIN(paginglimit) FROM data.usersettings")).Should().Be(AppModuleSchema.DefaultPagingLimit);
        // #129: the two app setting definitions, with their well-known ids and the default false.
        (await DefinitionCountAsync()).Should().Be(4);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.settingdefinition WHERE defaultvalue = N'false' AND settingdefinitionid IN " +
            $"('{AppModuleSchema.AsyncOnLoadId}', '{AppModuleSchema.AsyncOnSaveId}')")).Should().Be(2);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.settingdefinition WHERE defaultvalue = N'false' AND uniquename = displayname AND " +
            $"((settingdefinitionid = '{AppModuleSchema.TimelineGroupByDateId}' AND uniquename = '{AppModuleSchema.TimelineGroupByDate}') OR " +
            $"(settingdefinitionid = '{AppModuleSchema.TimelineRememberFiltersId}' AND uniquename = '{AppModuleSchema.TimelineRememberFilters}'))")).Should().Be(2);

        // A second start changes nothing.
        var relationships = await ScalarAsync("SELECT COUNT(*) FROM meta.relationship");
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship")).Should().Be(relationships);
        (await DefinitionCountAsync()).Should().Be(4);
    }

    /// <summary>UI-1g: an organization seeded before the timeline options gets the two definitions at the next start.</summary>
    [Fact]
    public async Task An_existing_organization_gets_the_timeline_definitions_on_restart()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        await ScalarAsync($"DELETE FROM data.settingdefinition WHERE uniquename IN ('{AppModuleSchema.TimelineGroupByDate}', '{AppModuleSchema.TimelineRememberFilters}')");
        (await DefinitionCountAsync()).Should().Be(2);

        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        (await DefinitionCountAsync()).Should().Be(4);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.settingdefinition WHERE defaultvalue = N'false' AND settingdefinitionid IN " +
            $"('{AppModuleSchema.TimelineGroupByDateId}', '{AppModuleSchema.TimelineRememberFiltersId}')")).Should().Be(2);
    }

    /// <summary>UI-1g: a restart does not undo an administrator's change to a timeline definition.</summary>
    [Fact]
    public async Task An_administrators_change_to_a_timeline_definition_is_kept()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        await ScalarAsync($"UPDATE data.settingdefinition SET defaultvalue = N'true' WHERE settingdefinitionid = '{AppModuleSchema.TimelineGroupByDateId}'");

        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        (await ScalarAsync($"SELECT defaultvalue FROM data.settingdefinition WHERE settingdefinitionid = '{AppModuleSchema.TimelineGroupByDateId}'"))
            .Should().Be("true");
        (await DefinitionCountAsync()).Should().Be(4);
    }

    /// <summary>#129: an organization that ran the UI-1a top-up gets Basic User's Read on the app settings at the next start.</summary>
    [Fact]
    public async Task Basic_user_app_setting_top_up_reaches_an_existing_organization()
    {
        const string step = "app-setting-role-privileges";
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var reads = new[] { AppModuleSchema.SettingDefinition, AppModuleSchema.AppSetting }
            .Select(t => PrivilegeNames.IdOf(PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(t)!))).ToList();
        var basic = $"roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ('{reads[0]}', '{reads[1]}')";
        await MainScalarAsync($"DELETE FROM data.roleprivileges WHERE {basic}; DELETE FROM meta.upgrade_step WHERE name = N'{step}';");
        try
        {
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await MainScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic} AND privilegedepthmask = 8")).Should().Be(2);
            (await MainScalarAsync($"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);
        }
        finally
        {
            foreach (var read in reads)
                await MainScalarAsync($"""
                    IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{read}')
                        INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                        VALUES (NEWID(), '{StandardRoles.BasicUser}', '{read}', 8, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                    """);
            SecurityTestHelper.Cache(fx).Invalidate();
        }
    }

    /// <summary>
    /// UI-2a: an organization that already ran the app setting top-up gets Basic User's Read and System Customizer's privileges on
    /// <c>organizationsetting</c> from a top-up of its own; System Administrator is recognized by its role, so it needs no rows.
    /// </summary>
    [Fact]
    public async Task Organization_setting_privileges_reach_an_existing_organization()
    {
        const string step = "organization-setting-privileges";
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var table = store.GetEntity(AppModuleSchema.OrganizationSetting)!;
        var readName = PrivilegeNames.For(AccessRights.ReadAccess, table);
        var names = new List<string>();
        foreach (var right in new[] { AccessRights.CreateAccess, AccessRights.ReadAccess, AccessRights.WriteAccess, AccessRights.DeleteAccess,
                     AccessRights.AppendAccess, AccessRights.AppendToAccess, AccessRights.AssignAccess, AccessRights.ShareAccess })
            if ((await MainScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE name = N'{PrivilegeNames.For(right, table)}'")) is { } n && Convert.ToInt64(n) > 0)
                names.Add(PrivilegeNames.For(right, table));
        names.Should().Contain(readName);
        var all = string.Join(", ", names.Select(n => $"'{PrivilegeNames.IdOf(n)}'"));
        var read = PrivilegeNames.IdOf(PrivilegeNames.For(AccessRights.ReadAccess, table));
        var rows = $"privilegeid IN ({all}) AND roleid IN ('{StandardRoles.BasicUser}', '{StandardRoles.SystemCustomizer}')";
        var saved = await MainScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {rows}");
        await MainScalarAsync($"DELETE FROM data.roleprivileges WHERE {rows}; DELETE FROM meta.upgrade_step WHERE name = N'{step}';");
        try
        {
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await MainScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{read}' AND privilegedepthmask = 8"))
                .Should().Be(1);
            (await MainScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ({all})"))
                .Should().Be(1, "Basic User only reads the environment values");
            (await MainScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.SystemCustomizer}' AND privilegeid IN ({all}) AND privilegedepthmask = 8"))
                .Should().Be(names.Count);
            (await MainScalarAsync($"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);
            (await MainScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {rows}")).Should().Be(saved);
            // System Administrator holds no rows and still reads the table.
            (await MainScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.SystemAdministrator}' AND privilegeid IN ({all})")).Should().Be(0);
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/organizationsettings?$top=1");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            (await fx.Client.SendAsync(request)).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        }
        finally
        {
            SecurityTestHelper.Cache(fx).Invalidate();
        }
    }

    private async Task<object?> MainScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        // A COUNT(*) is bigint on PostgreSQL: the counts compare as int.
        return result switch { DBNull => null, long count => checked((int)count), _ => result };
    }

    [Fact]
    public async Task Existing_org_gets_dataverse_names_for_appmoduleroles()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        // An organization seeded before #105: type code 9008, set appmoduleroles, key appmodulerolesid; one app role row.
        var row = Guid.NewGuid();
        await ScalarAsync($"""
            EXEC sp_rename N'data.appmoduleroles.appmoduleroleid', N'appmodulerolesid', N'COLUMN';
            UPDATE meta.attribute SET logical_name = 'appmodulerolesid',
                definition = JSON_MODIFY(JSON_MODIFY(definition, '$.LogicalName', 'appmodulerolesid'), '$.SchemaName', 'appmodulerolesid')
                WHERE entity_logical_name = 'appmoduleroles' AND logical_name = 'appmoduleroleid';
            UPDATE meta.entity SET definition = JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(definition,
                '$.ObjectTypeCode', 9008), '$.EntitySetName', 'appmoduleroles'), '$.PrimaryIdAttribute', 'appmodulerolesid')
                WHERE logical_name = 'appmoduleroles';
            """);
        // A batch of its own: the renamed column does not exist when the batch above is compiled.
        await ScalarAsync($"""
            INSERT INTO data.appmoduleroles (appmodulerolesid, appmoduleid, roleid, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES ('{row}', NEWID(), NEWID(), SYSUTCDATETIME(), NEWID(), SYSUTCDATETIME(), NEWID(), 0, 1);
            """);
        var stale = (await NewStoreAsync()).GetEntity(AppModuleSchema.RolesIntersect)!;
        (stale.ObjectTypeCode, stale.EntitySetName, stale.PrimaryIdAttribute).Should().Be((9008, "appmoduleroles", "appmodulerolesid"));

        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        var roles = (await NewStoreAsync()).GetEntity(AppModuleSchema.RolesIntersect)!;
        (roles.ObjectTypeCode, roles.EntitySetName, roles.PrimaryIdAttribute).Should().Be((9009, "appmodulerolescollection", "appmoduleroleid"));
        roles.Attributes.Should().ContainSingle(a => a.IsPrimaryId == true).Which.LogicalName.Should().Be("appmoduleroleid");
        (await ScalarAsync($"SELECT COUNT(*) FROM data.appmoduleroles WHERE appmoduleroleid = '{row}'")).Should().Be(1);
    }

    [Fact]
    public async Task Paging_limit_default_is_applied_only_on_first_seed()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = '{SystemSeed.PagingLimitDefaultStep}'")).Should().Be(1);

        // An administrator clears a user's page size: a restart leaves it cleared (#110).
        await ScalarAsync("UPDATE data.usersettings SET paginglimit = NULL");
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings WHERE paginglimit IS NOT NULL")).Should().Be(0);
    }
}
