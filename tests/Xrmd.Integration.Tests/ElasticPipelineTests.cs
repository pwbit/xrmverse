using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.4, task 4: a first-level write on an elastic table commits after each of stages 20, 30 and 40 (ruling E3); an
/// elastic write nested in another message's transaction joins it (ruling E4).
/// </summary>
[Collection("xrmd")]
public sealed class ElasticPipelineTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Plugins = "Xrmd.TestPlugins.";
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _table;

    private OrganizationService _svc = null!;
    private Guid _assemblyId;
    private readonly Dictionary<string, Guid> _types = new();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    /// <summary>The elastic user-owned table <c>pb_elpipe…</c> whose primary name is <c>pb_name</c> (what the test plug-ins write), created once.</summary>
    private async Task<string> TableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "pb_elpipe" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Elastic pipe");
            entity.TableType = ElasticTables.TableTypeElastic;
            await _svc.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200, DisplayName = Label("Name") },
            });
            return _table = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private async Task StepAsync(string plugin, string message, string entity, int stage, string config, int rank = 1)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(_svc, _assemblyId, Plugins + plugin);
        await PluginTestHelper.RegisterStepAsync(_svc, typeId, message, entity, stage, rank: rank, config: config);
    }

    private async Task<bool> ExistsAsync(string entity, Guid id) =>
        (await _svc.RetrieveMultipleAsync(new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = { Conditions = { new ConditionExpression(entity + "id", ConditionOperator.Equal, id) } },
        })).Entities.Count > 0;

    /// <summary>The rows of <paramref name="entity"/> written by <c>CreateRecordPlugin</c> for the record <paramref name="primaryId"/>.</summary>
    private async Task<int> PluginRowsAsync(string entity, Guid primaryId) =>
        (await _svc.RetrieveMultipleAsync(new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet("pb_name"),
            Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.EndsWith, "|" + primaryId) } },
        })).Entities.Count;

    [Fact]
    public async Task PostOperation_failure_keeps_the_elastic_row()
    {
        var table = await TableAsync();
        await StepAsync("ThrowPlugin", "Create", table, 40, "invalid:Stop!");
        var id = Guid.NewGuid();

        var fault = await FaultAsync(() => _svc.CreateAsync(new Entity(table, id) { ["pb_name"] = "kept", [ElasticTables.PartitionId] = "p1" }));

        fault.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
        fault.Message.Should().Be("Stop!");
        (await ExistsAsync(table, id)).Should().BeTrue();
    }

    [Fact]
    public async Task PostOperation_failure_rolls_back_standard_row()
    {
        await StepAsync("ThrowPlugin", "Create", TestEntities.Pipe, 40, "invalid:Stop!");
        var id = Guid.NewGuid();

        var fault = await FaultAsync(() => _svc.CreateAsync(new Entity(TestEntities.Pipe, id) { ["pb_name"] = "gone" }));

        fault.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
        (await ExistsAsync(TestEntities.Pipe, id)).Should().BeFalse();
    }

    [Fact]
    public async Task PreOperation_write_survives_main_failure()
    {
        var table = await TableAsync();
        var id = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "first", [ElasticTables.PartitionId] = "p1" });
        await StepAsync("CreateRecordPlugin", "Create", table, 20, TestEntities.PipeLog);

        // The same id in another partition: stage 30 fails with DuplicateRecord (ruling E1).
        var fault = await FaultAsync(() => _svc.CreateAsync(new Entity(table, id) { ["pb_name"] = "second", [ElasticTables.PartitionId] = "p2" }));

        fault.ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        (await PluginRowsAsync(TestEntities.PipeLog, id)).Should().Be(1);
    }

    [Fact]
    public async Task PreValidation_failure_writes_nothing()
    {
        var table = await TableAsync();
        await StepAsync("ThrowPlugin", "Create", table, 10, "invalid:Too early");
        await StepAsync("CreateRecordPlugin", "Create", table, 20, TestEntities.PipeLog);
        var id = Guid.NewGuid();

        var fault = await FaultAsync(() => _svc.CreateAsync(new Entity(table, id) { ["pb_name"] = "never" }));

        fault.Message.Should().Be("Too early");
        (await ExistsAsync(table, id)).Should().BeFalse();
        (await PluginRowsAsync(TestEntities.PipeLog, id)).Should().Be(0);
    }

    [Fact]
    public async Task Nested_elastic_write_rolls_back_with_outer()
    {
        var table = await TableAsync();
        await StepAsync("CreateRecordPlugin", "Create", "account", 40, table, rank: 1);
        await StepAsync("ThrowPlugin", "Create", "account", 40, "invalid:Outer fails", rank: 2);
        var id = Guid.NewGuid();

        var fault = await FaultAsync(() => _svc.CreateAsync(new Entity("account", id) { ["name"] = "outer" }));

        fault.Message.Should().Be("Outer fails");
        (await ExistsAsync("account", id)).Should().BeFalse();
        (await PluginRowsAsync(table, id)).Should().Be(0);
    }
}
