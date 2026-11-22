using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.4, task 6: CreateMultiple, UpdateMultiple, UpsertMultiple and DeleteMultiple on an elastic table run the single-record
/// message through the pipeline for each target (ruling E5); the failed targets are reported in the fault's error details.
/// </summary>
[Collection("xrmd")]
public sealed class ElasticBulkTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string SessionToken = "x-ms-session-token";
    private const string BulkDetails = "Plugin.BulkApiErrorDetails";
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _table;

    private OrganizationService _svc = null!;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    /// <summary>The elastic user-owned table <c>pb_elbulk…</c> (<c>pb_name</c> primary name, <c>pb_value</c> Integer), created once.</summary>
    private async Task<string> TableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "pb_elbulk" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Elastic bulk");
            entity.TableType = ElasticTables.TableTypeElastic;
            await _svc.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200, DisplayName = Label("Name") },
            });
            await _svc.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new IntegerAttributeMetadata { LogicalName = "pb_value", SchemaName = "pb_Value", DisplayName = Label("Value") },
            });
            return _table = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private static string Pk(string table) => table + "id";

    private async Task<List<Entity>> RowsAsync(string table, string namePrefix) =>
        (await _svc.RetrieveMultipleAsync(new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.BeginsWith, namePrefix) } },
        })).Entities.ToList();

    private async Task<Entity?> RowAsync(string table, Guid id) =>
        (await _svc.RetrieveMultipleAsync(new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = { Conditions = { new ConditionExpression(Pk(table), ConditionOperator.Equal, id) } },
        })).Entities.SingleOrDefault();

    private static EntityCollection Targets(string table, params Entity[] targets) => new(targets.ToList()) { EntityName = table };

    private static string Prefix() => "b" + Guid.NewGuid().ToString("N")[..8] + "-";

    [Fact]
    public async Task CreateMultiple_returns_ids_in_order()
    {
        var table = await TableAsync();
        var prefix = Prefix();
        var targets = Enumerable.Range(0, 100)
            .Select(i => new Entity(table) { ["pb_name"] = prefix + i.ToString("D3"), ["pb_value"] = i, [ElasticTables.PartitionId] = "bulk" })
            .ToArray();

        var response = (CreateMultipleResponse)await _svc.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(table, targets) });

        response.Ids.Should().HaveCount(100);
        var rows = await RowsAsync(table, prefix);
        rows.Should().HaveCount(100);
        for (var i = 0; i < 100; i++)
            rows.Single(r => r.Id == response.Ids[i]).GetAttributeValue<int>("pb_value").Should().Be(i);
        response.Results[SessionToken].Should().BeOfType<string>().Which.Should().StartWith("0:");
    }

    [Fact]
    public async Task Partial_success_reports_failed_indexes()
    {
        var table = await TableAsync();
        var prefix = Prefix();

        var fault = await FaultAsync(() => _svc.ExecuteAsync(new CreateMultipleRequest
        {
            Targets = Targets(table,
                new Entity(table) { ["pb_name"] = prefix + "0" },
                new Entity(table) { ["pb_name"] = prefix + "1", [ElasticTables.TtlInSeconds] = 0 }, // out of range: 400
                new Entity(table) { ["pb_name"] = prefix + "2" }),
        }));

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.HttpStatus.Should().Be(400);
        var details = JsonSerializer.Deserialize<BulkApiErrorDetail[]>((string)fault.ErrorDetails[BulkDetails]!);
        details.Should().Equal(new BulkApiErrorDetail(1, "", 400));
        (await RowsAsync(table, prefix)).Select(r => r.GetAttributeValue<string>("pb_name"))
            .Should().BeEquivalentTo(prefix + "0", prefix + "2");
    }

    [Fact]
    public async Task Failure_detail_carries_the_target_id()
    {
        var table = await TableAsync();
        var id = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "x", [ElasticTables.PartitionId] = "p1" });
        var missing = Guid.NewGuid();

        var fault = await FaultAsync(() => _svc.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(table, new Entity(table, id) { ["pb_value"] = 5 }, new Entity(table, missing) { ["pb_value"] = 6 }),
        }));

        // The first failure (index 0: the row is in partition p1, none was given) sets the code.
        fault.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        JsonSerializer.Deserialize<BulkApiErrorDetail[]>((string)fault.ErrorDetails[BulkDetails]!)
            .Should().Equal(new BulkApiErrorDetail(0, id.ToString(), 404), new BulkApiErrorDetail(1, missing.ToString(), 404));
    }

    [Fact]
    public async Task UpdateMultiple_ignores_repeated_id()
    {
        var table = await TableAsync();
        var id = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "u", ["pb_value"] = 0 });

        var response = await _svc.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(table, new Entity(table, id) { ["pb_value"] = 1 }, new Entity(table, id) { ["pb_value"] = 2 }),
        });

        response.Should().BeOfType<UpdateMultipleResponse>();
        response.Results[SessionToken].Should().BeOfType<string>();
        (await RowAsync(table, id))!.GetAttributeValue<int>("pb_value").Should().Be(1);
    }

    [Fact]
    public async Task UpdateMultiple_finds_rows_by_their_partitionid_values()
    {
        var table = await TableAsync();
        var a = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "pa", [ElasticTables.PartitionId] = "dev-1" });
        var b = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "pb", [ElasticTables.PartitionId] = "dev-2" });

        // Learn's UpdateMultiple sample: each target names its partition by its partitionid value.
        await _svc.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(table,
                new Entity(table, a) { ["pb_value"] = 45, [ElasticTables.PartitionId] = "dev-1" },
                new Entity(table, b) { ["pb_value"] = 15, [ElasticTables.PartitionId] = "dev-2" }),
        });

        (await RowAsync(table, a))!.GetAttributeValue<int>("pb_value").Should().Be(45);
        (await RowAsync(table, b))!.GetAttributeValue<int>("pb_value").Should().Be(15);
    }

    [Fact]
    public async Task UpsertMultiple_repeated_id_fails_before_writing()
    {
        var table = await TableAsync();
        var (first, repeated) = (Guid.NewGuid(), Guid.NewGuid());

        var fault = await FaultAsync(() => _svc.ExecuteAsync(new UpsertMultipleRequest
        {
            Targets = Targets(table,
                new Entity(table, first) { ["pb_name"] = "a" },
                new Entity(table, repeated) { ["pb_name"] = "b" },
                new Entity(table, repeated) { ["pb_name"] = "c" }),
        }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(repeated.ToString());
        (await RowAsync(table, first)).Should().BeNull();
        (await RowAsync(table, repeated)).Should().BeNull();
    }

    [Fact]
    public async Task UpsertMultiple_creates_and_replaces()
    {
        var table = await TableAsync();
        var existing = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "old", ["pb_value"] = 3, [ElasticTables.PartitionId] = "p1" });
        var created = Guid.NewGuid();

        var response = (UpsertMultipleResponse)await _svc.ExecuteAsync(new UpsertMultipleRequest
        {
            Targets = Targets(table,
                new Entity(table, existing) { ["pb_name"] = "new", [ElasticTables.PartitionId] = "p1" },
                new Entity(table, created) { ["pb_name"] = "fresh", [ElasticTables.PartitionId] = "p1" }),
        });

        response.Results.Select(r => r.RecordCreated).Should().Equal(false, true);
        response.Results.Select(r => r.Target.Id).Should().Equal(existing, created);
        ((OrganizationResponse)response).Results[SessionToken].Should().BeOfType<string>();
        var replaced = await RowAsync(table, existing);
        replaced!.GetAttributeValue<string>("pb_name").Should().Be("new");
        replaced.Contains("pb_value").Should().BeFalse();
    }

    [Fact]
    public async Task Create_only_upsert_is_refused_on_an_elastic_table()
    {
        // Final review (#76): the elastic Upsert replaces the row and never reads the create-only mark, so the call is refused
        // instead of silently ignoring it. The existing row stays as it is.
        var table = await TableAsync();
        var existing = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "keep", ["pb_value"] = 3, [ElasticTables.PartitionId] = "p1" });

        var fault = await FluentActions.Invoking(() => _svc.UpsertAsync(
                new Entity(table, existing) { ["pb_name"] = "replaced", [ElasticTables.PartitionId] = "p1" }, createOnly: true))
            .Should().ThrowAsync<XrmdFaultException>();

        fault.Which.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Which.Message.Should().Be($"Create-only upsert is not supported for the elastic table '{table}'.");
        var row = await RowAsync(table, existing);
        row!.GetAttributeValue<string>("pb_name").Should().Be("keep");
        row.GetAttributeValue<int>("pb_value").Should().Be(3);
    }

    [Fact]
    public async Task UpsertMultiple_honours_the_partitionId_parameter()
    {
        // Final review I3: the request's partitionId reaches each single Upsert.
        var table = await TableAsync();
        var existing = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "old", ["pb_value"] = 3, [ElasticTables.PartitionId] = "pm-1" });
        var created = Guid.NewGuid();

        var response = (UpsertMultipleResponse)await _svc.ExecuteAsync(new UpsertMultipleRequest
        {
            Targets = Targets(table, new Entity(table, existing) { ["pb_name"] = "new" }, new Entity(table, created) { ["pb_name"] = "fresh" }),
            ["partitionId"] = "pm-1",
        });

        response.Results.Select(r => r.RecordCreated).Should().Equal(false, true);
        (await RowAsync(table, existing))!.GetAttributeValue<string>("pb_name").Should().Be("new");
        (await RowAsync(table, created))!.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("pm-1");
    }

    [Fact]
    public async Task DeleteMultiple_by_key_attributes()
    {
        var table = await TableAsync();
        var a = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "d", [ElasticTables.PartitionId] = "p1" });
        var b = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "d", [ElasticTables.PartitionId] = "p1" });
        EntityReference ByKey(Guid id) => new(table, new KeyAttributeCollection { { Pk(table), id }, { ElasticTables.PartitionId, "p1" } });

        var response = await _svc.ExecuteAsync(new DeleteMultipleRequest { Targets = new EntityReferenceCollection { ByKey(a), ByKey(b) } });

        response.ResponseName.Should().Be("DeleteMultiple");
        response.Results.Should().NotContainKey(SessionToken);
        (await RowAsync(table, a)).Should().BeNull();
        (await RowAsync(table, b)).Should().BeNull();
    }

    [Fact]
    public async Task Create_steps_fire_per_target()
    {
        var table = await TableAsync();
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(_svc, assembly, "Xrmd.TestPlugins.CreateRecordPlugin");
        await PluginTestHelper.RegisterStepAsync(_svc, type, "Create", table, 40, config: TestEntities.PipeLog);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        await _svc.ExecuteAsync(new CreateMultipleRequest
        {
            Targets = Targets(table, ids.Select(id => new Entity(table, id) { ["pb_name"] = "s" }).ToArray()),
        });

        var log = (await _svc.RetrieveMultipleAsync(new QueryExpression(TestEntities.PipeLog)
        {
            ColumnSet = new ColumnSet("pb_name"),
            Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.In, ids.Select(id => $"Create|40|1|{id}").ToArray<object>()) } },
        })).Entities;
        log.Should().HaveCount(3, "the Create step runs once per target, each as a first-level message");
    }

    [Fact]
    public async Task Step_on_a_bulk_message_of_an_elastic_table_is_refused()
    {
        var table = await TableAsync();
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(_svc, assembly, "Xrmd.TestPlugins.ThrowPlugin");

        foreach (var message in new[] { "CreateMultiple", "UpdateMultiple", "UpsertMultiple", "DeleteMultiple" })
        {
            var fault = await FaultAsync(() => PluginTestHelper.RegisterStepAsync(_svc, type, message, table, 20, config: "invalid:x"));
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument, message);
            fault.Message.Should().Be(
                $"Plug-in steps on '{message}' are not supported for the elastic table '{table}': register on the single-record message.");
        }
    }

    [Fact]
    public async Task Targets_of_other_table_are_refused()
    {
        var table = await TableAsync();
        var prefix = Prefix();

        var fault = await FaultAsync(() => _svc.ExecuteAsync(new CreateMultipleRequest
        {
            Targets = Targets(table, new Entity(table) { ["pb_name"] = prefix + "0" }, new Entity("account") { ["name"] = prefix }),
        }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("account");
        (await RowsAsync(table, prefix)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteMultiple_on_standard()
    {
        var id = await _svc.CreateAsync(new Entity("account") { ["name"] = "dm" });

        var fault = await FaultAsync(() => _svc.ExecuteAsync(new DeleteMultipleRequest
        {
            Targets = new EntityReferenceCollection { new EntityReference("account", id) },
        }));

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Be("DeleteMultiple has not yet been implemented.");
    }
}
