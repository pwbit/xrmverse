using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.13, task 2: the deletion log of tracked tables (written in the transaction of the Delete, cascades included) and its
/// retention by <c>organization.expirechangetrackingindays</c>, which raises <c>meta.change_tracking_state.purged_version</c>.
/// </summary>
[Collection("xrmd")]
public sealed class ChangeTrackingLogTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    private async Task<string> NewTableAsync(string label, bool tracked)
    {
        var name = $"new_{label}{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        if (tracked)
            await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, ChangeTrackingEnabled = true } });
        return name;
    }

    private async Task RelateCascadeAsync(string parent, string child)
    {
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"new_{child}_parentid", ReferencedEntity = parent, ReferencingEntity = child,
                CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.Cascade },
            },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_parentid", SchemaName = "new_parentid", DisplayName = Label("Parent") },
        });
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> LogCountAsync(string table) =>
        Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM meta.deleted_record WHERE entity_logical_name = @t", ("@t", table)));

    private async Task<long> LogVersionAsync(string table, Guid id) =>
        (long)(await ScalarAsync("SELECT CAST(version AS bigint) FROM meta.deleted_record WHERE entity_logical_name = @t AND object_id = @id",
            ("@t", table), ("@id", id)))!;

    private async Task<long> PurgedVersionAsync() =>
        (long)(await ScalarAsync("SELECT purged_version FROM meta.change_tracking_state"))!;

    private async Task<long> RowVersionAsync(string table, Guid id) =>
        (long)(await ScalarAsync($"SELECT CAST(versionnumber AS bigint) FROM data.[{table}] WHERE [{table}id] = @id", ("@id", id)))!;

    private async Task<int> PurgeAsync(DateTime now)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await ChangeTrackingRetention.PurgeAsync(new DataSession(conn, null), now, default);
    }

    [Fact]
    public async Task Delete_of_tracked_record_is_logged()
    {
        var table = await NewTableAsync("tl", tracked: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var lastVersion = await RowVersionAsync(table, id);

        await Admin.DeleteAsync(table, id);

        (await LogCountAsync(table)).Should().Be(1);
        (await LogVersionAsync(table, id)).Should().BeGreaterThan(lastVersion);
        ((DateTime)(await ScalarAsync("SELECT deleted_on FROM meta.deleted_record WHERE object_id = @id", ("@id", id)))!)
            .Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Delete_of_untracked_record_is_not_logged()
    {
        var table = await NewTableAsync("tu", tracked: false);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });

        await Admin.DeleteAsync(table, id);

        (await LogCountAsync(table)).Should().Be(0);
    }

    [Fact]
    public async Task Cascade_delete_logs_each_tracked_child()
    {
        var parent = await NewTableAsync("cp", tracked: true);
        var child = await NewTableAsync("cc", tracked: true);
        await RelateCascadeAsync(parent, child);
        var p = await Admin.CreateAsync(new Entity(parent) { ["new_name"] = "p" });
        var c1 = await Admin.CreateAsync(new Entity(child) { ["new_name"] = "c1", ["new_parentid"] = new EntityReference(parent, p) });
        var c2 = await Admin.CreateAsync(new Entity(child) { ["new_name"] = "c2", ["new_parentid"] = new EntityReference(parent, p) });

        await Admin.DeleteAsync(parent, p);

        (await LogCountAsync(parent)).Should().Be(1);
        (await LogCountAsync(child)).Should().Be(2);
        (await LogVersionAsync(child, c1)).Should().BeGreaterThan(0);
        (await LogVersionAsync(child, c2)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Rolled_back_delete_is_not_logged()
    {
        var table = await NewTableAsync("tr", tracked: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Delete", table, stage: 40, config: "invalid:refused after the delete");

        var act = () => Admin.DeleteAsync(table, id);

        await act.Should().ThrowAsync<Exception>();
        (await ScalarAsync($"SELECT COUNT(*) FROM data.[{table}] WHERE [{table}id] = @id", ("@id", id))).Should().Be(1);
        (await LogCountAsync(table)).Should().Be(0);
    }

    [Fact]
    public async Task Purge_removes_rows_older_than_the_setting_and_raises_purged_version()
    {
        var table = await NewTableAsync("tp", tracked: true);
        var old = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "old" });
        var recent = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "recent" });
        await Admin.DeleteAsync(table, old);
        await Admin.DeleteAsync(table, recent);
        await ScalarAsync("UPDATE meta.deleted_record SET deleted_on = DATEADD(day, -8, SYSUTCDATETIME()) WHERE object_id = @id", ("@id", old));
        await ScalarAsync("UPDATE meta.deleted_record SET deleted_on = DATEADD(day, -1, SYSUTCDATETIME()) WHERE object_id = @id", ("@id", recent));
        var oldVersion = await LogVersionAsync(table, old); // the backdating UPDATE gave the row a new version
        await SetExpireDaysAsync(7);

        var purged = await PurgeAsync(DateTime.UtcNow);

        purged.Should().BeGreaterThanOrEqualTo(1);
        (await LogCountAsync(table)).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.deleted_record WHERE object_id = @id", ("@id", recent))).Should().Be(1);
        (await PurgedVersionAsync()).Should().Be(oldVersion);
    }

    [Fact]
    public async Task Purge_with_zero_days_removes_everything_logged_before_now()
    {
        var table = await NewTableAsync("tz", tracked: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        await Admin.DeleteAsync(table, id);
        var version = await LogVersionAsync(table, id);
        await SetExpireDaysAsync(0);
        try
        {
            await PurgeAsync(DateTime.UtcNow.AddSeconds(1));
        }
        finally
        {
            await SetExpireDaysAsync(7);
        }

        (await LogCountAsync(table)).Should().Be(0);
        (await PurgedVersionAsync()).Should().BeGreaterThanOrEqualTo(version);
    }

    private Task SetExpireDaysAsync(int days) => ScalarAsync("UPDATE data.organization SET expirechangetrackingindays = @d", ("@d", days));
}
