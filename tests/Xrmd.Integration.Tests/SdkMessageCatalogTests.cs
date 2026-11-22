using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class SdkMessageCatalogTests(XrmdFixture fx)
{
    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Ensure_creates_messages_and_filters()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureProjectAsync(store);
        var catalog = fx.App.Services.GetRequiredService<SdkMessageCatalog>();

        await catalog.EnsureAsync(default);
        var messages = await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage");
        var filters = await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter");
        await catalog.EnsureAsync(default);

        (await ScalarAsync($"SELECT COUNT(*) FROM data.sdkmessage WHERE name = 'Create' AND sdkmessageid = '{SdkMessageCatalog.MessageId("Create")}'")).Should().Be(1);
        (await ScalarAsync(
            $"SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessagefilterid = '{SdkMessageCatalog.FilterId("Create", "pb_project")}' " +
            $"AND sdkmessageid = '{SdkMessageCatalog.MessageId("Create")}' AND primaryobjecttypecode = 'pb_project' " +
            "AND secondaryobjecttypecode = 'none' AND iscustomprocessingstepallowed = 1")).Should().Be(1);
        messages.Should().Be(SdkMessageCatalog.Messages.Count);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.sdkmessage WHERE name = 'CommitFileBlocksUpload' AND sdkmessageid = '{SdkMessageCatalog.MessageId("CommitFileBlocksUpload")}'")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage")).Should().Be(messages);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter")).Should().Be(filters);
    }

    [Fact]
    public async Task New_entity_gets_filters()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var entity = new EntityMetadata { LogicalName = "pb_new", SchemaName = "pb_New", OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });

        (await ScalarAsync(
            $"SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessagefilterid = '{SdkMessageCatalog.FilterId("Update", "pb_new")}' AND primaryobjecttypecode = 'pb_new'"))
            .Should().Be(1);
    }

    /// <summary>The Learn detection query: filter rows of a table, by message name.</summary>
    private async Task<long> FilterRowsAsync(string table, string message) =>
        await ScalarAsync(
            "SELECT COUNT(*) FROM data.sdkmessagefilter f JOIN data.sdkmessage m ON m.sdkmessageid = f.sdkmessageid " +
            $"WHERE f.primaryobjecttypecode = '{table}' AND m.name = '{message}'");

    private static async Task<string> CreateElasticTableAsync(IServiceProvider services, Guid admin)
    {
        var svc = TestServices.NewOrganizationService(services, admin);
        var name = "pb_catel" + Guid.NewGuid().ToString("N")[..6];
        var entity = TestHelpers.Entity(name, name, "Catalog elastic");
        entity.TableType = ElasticTables.TableTypeElastic;
        await svc.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200, DisplayName = TestHelpers.Label("Name") },
        });
        return name;
    }

    [Fact]
    public async Task Bulk_filters_follow_the_table_type()
    {
        var catalog = fx.App.Services.GetRequiredService<SdkMessageCatalog>();
        var elastic = await CreateElasticTableAsync(fx.App.Services, fx.AdminObjectId);
        await catalog.EnsureAsync(default);

        (await FilterRowsAsync("account", "CreateMultiple")).Should().Be(1);
        (await FilterRowsAsync("account", "UpdateMultiple")).Should().Be(1);
        foreach (var none in new[] { "UpsertMultiple", "Upsert", "DeleteMultiple" })
            (await FilterRowsAsync("account", none)).Should().Be(0, none);
        (await FilterRowsAsync("activitypointer", "CreateMultiple")).Should().Be(0);
        foreach (var message in new[] { "CreateMultiple", "UpdateMultiple", "UpsertMultiple", "DeleteMultiple", "Upsert" })
            (await FilterRowsAsync(elastic, message)).Should().Be(1, message);
    }

    [Fact]
    public async Task EntityMessagesOf_and_IsEventOn_follow_the_table_type()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var account = store.GetEntity("account")!;
        SdkMessageCatalog.IsEventOn("CreateMultiple", account).Should().BeTrue();
        SdkMessageCatalog.IsEventOn("upsert", account).Should().BeFalse();
        SdkMessageCatalog.IsEventOn("UpsertMultiple", account).Should().BeFalse();
        SdkMessageCatalog.IsEventOn("CreateMultiple", store.GetEntity("activitypointer")!).Should().BeFalse();
        SdkMessageCatalog.MergePairs["create"].Should().Be("CreateMultiple");
        SdkMessageCatalog.MergePairs["Update"].Should().Be("UpdateMultiple");
        var elastic = await CreateElasticTableAsync(fx.App.Services, fx.AdminObjectId);
        SdkMessageCatalog.IsEventOn("Upsert", store.GetEntity(elastic)!).Should().BeTrue();
    }

    [Fact]
    public async Task Stale_upsert_filter_without_step_is_deleted_and_one_with_a_step_stays()
    {
        var catalog = fx.App.Services.GetRequiredService<SdkMessageCatalog>();
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await catalog.EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var message = SdkMessageCatalog.MessageId("Upsert");
        async Task ForgeAsync(string table) => await ExecAsync(
            "INSERT INTO data.sdkmessagefilter (sdkmessagefilterid, name, sdkmessageid, primaryobjecttypecode, secondaryobjecttypecode, " +
            "iscustomprocessingstepallowed, isvisible, availability, owningbusinessunit, createdon, modifiedon, statecode, statuscode) " +
            $"SELECT '{SdkMessageCatalog.FilterId("Upsert", table)}', 'Upsert:{table}', '{message}', '{table}', 'none', {TestSql.True}, {TestSql.True}, 1, " +
            "(SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL), SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1");
        await ForgeAsync("account");
        await ForgeAsync("contact");
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(svc, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(svc, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        // The validator refuses a step on this forged filter, so the step row is written directly.
        await ExecAsync(
            "INSERT INTO data.sdkmessageprocessingstep (sdkmessageprocessingstepid, name, sdkmessageid, sdkmessagefilterid, plugintypeid, " +
            "eventhandler, stage, mode, rank, owningbusinessunit, createdon, modifiedon, statecode, statuscode) " +
            $"SELECT NEWID(), 'stale', '{message}', '{SdkMessageCatalog.FilterId("Upsert", "contact")}', '{type}', '{type}', 20, 0, 1, " +
            "(SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL), SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1");

        await catalog.EnsureAsync(default);

        (await FilterRowsAsync("account", "Upsert")).Should().Be(0);
        (await FilterRowsAsync("contact", "Upsert")).Should().Be(1);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.sdkmessagefilter WHERE sdkmessagefilterid = '{SdkMessageCatalog.FilterId("Upsert", "contact")}'");
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
