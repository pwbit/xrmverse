using System.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Host;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>Plan 3.4, task 8: the sweep that deletes the elastic rows whose time to live has passed (ruling E7).</summary>
[Collection("xrmd")]
public sealed class ElasticTtlTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Plugins = "Xrmd.TestPlugins.";
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

    /// <summary>The audited elastic table <c>pb_elttl…</c> whose primary name is <c>pb_name</c>, created once.</summary>
    private async Task<string> TableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "pb_elttl" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Elastic ttl");
            entity.TableType = ElasticTables.TableTypeElastic;
            entity.IsAuditEnabled = new BooleanManagedProperty(true);
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

    private async Task<bool> ExistsAsync(string table, Guid id) =>
        (await _svc.RetrieveMultipleAsync(new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = { Conditions = { new ConditionExpression(table + "id", ConditionOperator.Equal, id) } },
        })).Entities.Count > 0;

    private async Task<DateTime> ModifiedOnAsync(string table, Guid id) =>
        (await _svc.RetrieveAsync(table, id, new ColumnSet("modifiedon"))).GetAttributeValue<DateTime>("modifiedon");

    private async Task<Guid> NewRowAsync(string table, int? ttl)
    {
        var row = new Entity(table) { ["pb_name"] = "row" };
        if (ttl is not null) row[ElasticTables.TtlInSeconds] = ttl;
        return await _svc.CreateAsync(row);
    }

    /// <summary>One sweep at <paramref name="now"/> through a scope of the host (what the hosted service does).</summary>
    private async Task<int> PurgeAsync(DateTime now)
    {
        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        return await ElasticTtl.PurgeAsync(scope.ServiceProvider.GetRequiredService<CoreOperations>(), new DataSession(conn, null), now, 500, default);
    }

    private async Task<Guid> SystemUserIdAsync()
    {
        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        return await scope.ServiceProvider.GetRequiredService<CoreOperations>().SystemUserIdAsync(new DataSession(conn, null), default);
    }

    private async Task<List<(int Action, Guid UserId)>> AuditAsync(Guid id)
    {
        var rows = await TestSql.RowsAsync(fx.ConnectionString, "SELECT action, userid FROM data.audit WHERE objectid = @id ORDER BY createdon",
            ("@id", id));
        return rows.Select(r => (Convert.ToInt32(r[0]), (Guid)r[1]!)).ToList();
    }

    private async Task SetOrganizationAuditAsync(bool enabled)
    {
        var org = (Guid)(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT TOP 1 organizationid FROM data.organization"))!;
        await _svc.UpdateAsync(new Entity("organization", org) { ["isauditenabled"] = enabled });
    }

    [Fact]
    public async Task Expired_rows_are_deleted()
    {
        var table = await TableAsync();
        await SetOrganizationAuditAsync(true);
        try
        {
            var id = await NewRowAsync(table, 60);
            var modified = await ModifiedOnAsync(table, id);

            (await PurgeAsync(modified.AddSeconds(61))).Should().BeGreaterThanOrEqualTo(1);

            (await ExistsAsync(table, id)).Should().BeFalse();
            var system = await SystemUserIdAsync();
            (await AuditAsync(id)).Should().Contain((AuditActions.Delete, system));
        }
        finally
        {
            await SetOrganizationAuditAsync(false);
        }
    }

    [Fact]
    public async Task Row_at_exactly_the_expiry_instant_is_deleted()
    {
        var table = await TableAsync();
        var id = await NewRowAsync(table, 60);
        var modified = await ModifiedOnAsync(table, id);

        await PurgeAsync(modified.AddSeconds(60));

        (await ExistsAsync(table, id)).Should().BeFalse();
    }

    [Fact]
    public async Task Unexpired_and_null_ttl_rows_stay()
    {
        var table = await TableAsync();
        var young = await NewRowAsync(table, 3600);
        var forever = await NewRowAsync(table, null);
        var modified = await ModifiedOnAsync(table, young);

        await PurgeAsync(modified.AddSeconds(61));

        (await ExistsAsync(table, young)).Should().BeTrue();
        (await ExistsAsync(table, forever)).Should().BeTrue();
    }

    [Fact]
    public async Task Update_extends_the_ttl()
    {
        var table = await TableAsync();
        var id = await NewRowAsync(table, 60);
        var first = await ModifiedOnAsync(table, id);
        await Task.Delay(1100);
        await _svc.UpdateAsync(new Entity(table, id) { ["pb_name"] = "touched" });
        (await ModifiedOnAsync(table, id)).Should().BeAfter(first);

        await PurgeAsync(first.AddSeconds(60) + TimeSpan.FromMilliseconds(500)); // past the first expiry, before the renewed one

        (await ExistsAsync(table, id)).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_step_waits_for_an_update_that_renews_the_row_then_keeps_it()
    {
        // Plan 2.7b, Task 5 fix round 1: the sweep's re-check takes the row's key lock, then its row lock, in the order of an
        // Update. An Update in progress (both locks held) makes the delete step wait; once the renewal commits the row is no
        // longer expired, so it is kept.
        var table = await TableAsync();
        var id = await NewRowAsync(table, 60);
        var sweepNow = (await ModifiedOnAsync(table, id)).AddSeconds(61); // expired at that instant before the renewal

        await using var scope = fx.App.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var m = scope.ServiceProvider.GetRequiredService<IMetadataStore>().GetEntity(table)!;
        await using var updater = await factory.OpenAsync();
        int spid;
        await using (var cmd = TestSql.Command(updater, TestSql.SessionIdQuery)) spid = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Task<bool> delete;
        await using (var tx = await updater.BeginTransactionAsync())
        {
            await core.UpdateAsync(new DataSession(updater, tx), Caller.Internal,
                new Entity(table, id) { [ElasticTables.TtlInSeconds] = 3600 }, default);

            delete = Task.Run(async () =>
            {
                await using var sweepScope = fx.App.Services.CreateAsyncScope();
                await using var conn = await sweepScope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
                await using var sweepTx = await conn.BeginTransactionAsync();
                var gone = await ElasticTtl.DeleteIfExpiredAsync(sweepScope.ServiceProvider.GetRequiredService<CoreOperations>(),
                    new DataSession(conn, sweepTx), m, id, sweepNow, default);
                await sweepTx.CommitAsync();
                return gone;
            });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, TestSql.BlockedByQuery, ("@spid", spid))) == 0)
            {
                delete.IsCompleted.Should().BeFalse("the delete step must wait for the Update's transaction");
                DateTime.UtcNow.Should().BeBefore(deadline);
                await Task.Delay(20);
            }
            await tx.CommitAsync();
        }

        (await delete).Should().BeFalse();
        (await ExistsAsync(table, id)).Should().BeTrue();
    }

    [Fact]
    public async Task Row_renewed_after_the_page_read_is_not_deleted()
    {
        // Final review I1: the sweep read the row as expired, then an Update renewed it before its delete ran.
        var table = await TableAsync();
        var id = await NewRowAsync(table, 60);
        var first = await ModifiedOnAsync(table, id);
        var sweepNow = first.AddSeconds(61); // the instant the page was read at: the row was expired then
        await _svc.UpdateAsync(new Entity(table, id) { [ElasticTables.TtlInSeconds] = 3600 }); // renewed since

        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var m = scope.ServiceProvider.GetRequiredService<IMetadataStore>().GetEntity(table)!;
        var deleted = await ElasticTtl.DeleteIfExpiredAsync(scope.ServiceProvider.GetRequiredService<CoreOperations>(),
            new DataSession(conn, tx), m, id, sweepNow, default);
        await tx.CommitAsync();

        deleted.Should().BeFalse();
        (await ExistsAsync(table, id)).Should().BeTrue();
    }

    [Fact]
    public async Task Row_still_expired_at_the_delete_step_is_deleted()
    {
        var table = await TableAsync();
        var id = await NewRowAsync(table, 60);
        var modified = await ModifiedOnAsync(table, id);

        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var m = scope.ServiceProvider.GetRequiredService<IMetadataStore>().GetEntity(table)!;
        var deleted = await ElasticTtl.DeleteIfExpiredAsync(scope.ServiceProvider.GetRequiredService<CoreOperations>(),
            new DataSession(conn, tx), m, id, modified.AddSeconds(61), default);
        await tx.CommitAsync();

        deleted.Should().BeTrue();
        (await ExistsAsync(table, id)).Should().BeFalse();
    }

    [Fact]
    public async Task No_plugin_step_fires_on_ttl_delete()
    {
        var table = await TableAsync();
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
        var typeId = await PluginTestHelper.RegisterTypeAsync(_svc, assemblyId, Plugins + "ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(_svc, typeId, "Delete", table, 20, rank: 1, config: "invalid:Must not run");
        await PluginTestHelper.RegisterStepAsync(_svc, typeId, "Delete", table, 40, rank: 1, config: "invalid:Must not run");
        var id = await NewRowAsync(table, 60);
        var modified = await ModifiedOnAsync(table, id);

        await PurgeAsync(modified.AddSeconds(61));

        (await ExistsAsync(table, id)).Should().BeFalse();
    }

    [Fact]
    public async Task Failing_rows_are_skipped_and_later_expired_rows_are_deleted()
    {
        var table = await TableAsync();
        var poison = new[] { await NewPoisonAsync(table), await NewPoisonAsync(table) }; // the oldest expired rows
        var good = await NewRowAsync(table, 60);
        var modified = await ModifiedOnAsync(table, good);
        var trigger = $"trg_{table}_poison";
        await ExecAsync(TestProvider.IsPostgres
            ? $"""
              CREATE OR REPLACE FUNCTION data."{trigger}"() RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN IF OLD.pb_name = 'poison' THEN RAISE EXCEPTION 'forced failure'; END IF; RETURN OLD; END $$;
              CREATE TRIGGER "{trigger}" AFTER DELETE ON data."{table}" FOR EACH ROW EXECUTE FUNCTION data."{trigger}"();
              """
            : $"CREATE OR ALTER TRIGGER data.[{trigger}] ON data.[{table}] AFTER DELETE AS "
              + "IF EXISTS (SELECT 1 FROM deleted WHERE pb_name = 'poison') THROW 51000, 'forced failure', 1;");
        try
        {
            await using var scope = fx.App.Services.CreateAsyncScope();
            await using var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
            // A batch of 2 is filled by the two failing rows; the sweep must still reach the row behind them.
            await ElasticTtl.PurgeAsync(scope.ServiceProvider.GetRequiredService<CoreOperations>(), new DataSession(conn, null),
                modified.AddSeconds(61), 2, default);
        }
        finally
        {
            await ExecAsync(TestProvider.IsPostgres
                ? $"""DROP TRIGGER IF EXISTS "{trigger}" ON data."{table}"; DROP FUNCTION IF EXISTS data."{trigger}"();"""
                : $"DROP TRIGGER IF EXISTS data.[{trigger}]");
        }

        (await ExistsAsync(table, good)).Should().BeFalse();
        foreach (var id in poison) (await ExistsAsync(table, id)).Should().BeTrue();
        foreach (var id in poison) await _svc.DeleteAsync(table, id);
    }

    private async Task<Guid> NewPoisonAsync(string table)
    {
        var id = await _svc.CreateAsync(new Entity(table) { ["pb_name"] = "poison", [ElasticTables.TtlInSeconds] = 60 });
        await Task.Delay(20);
        return id;
    }

    private Task ExecAsync(string sql) => TestSql.ExecAsync(fx.ConnectionString, sql);

    [Fact]
    public async Task Sweep_disabled_with_zero()
    {
        var table = await TableAsync();
        var id = await NewRowAsync(table, 1);
        var opened = 0;
        var service = new ElasticTtlService(() => { opened++; return fx.App.Services.GetRequiredService<IDbConnectionFactory>(); }, 0,
            fx.App.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ElasticTtlService>.Instance, TimeProvider.System);

        await service.StartAsync(default);
        await service.ExecuteTask!;
        await service.StopAsync(default);

        opened.Should().Be(0);
        (await ExistsAsync(table, id)).Should().BeTrue();
    }

    [Fact]
    public async Task Service_sweep_deletes_expired_rows()
    {
        var table = await TableAsync();
        var id = await NewRowAsync(table, 1);
        var service = new ElasticTtlService(fx.App.Services.GetRequiredService<IDbConnectionFactory>, 60,
            fx.App.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ElasticTtlService>.Instance, new OffsetTimeProvider(TimeSpan.FromHours(1)));

        (await service.SweepOnceAsync(default)).Should().BeGreaterThanOrEqualTo(1);

        (await ExistsAsync(table, id)).Should().BeFalse();
    }

    /// <summary>The system clock shifted forward.</summary>
    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
