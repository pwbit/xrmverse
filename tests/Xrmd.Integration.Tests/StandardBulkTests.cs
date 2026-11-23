using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #76, Task 3: CreateMultiple, UpdateMultiple and UpsertMultiple on a standard table (<c>account</c>). The request is one pipeline
/// execution of the bulk message in one owned transaction (ruling B1); its stage 30 runs each target as an inner execution of
/// the single-record message (B4), at the same depth, with the bulk context as <c>ParentContext</c>.
/// </summary>
[Collection("xrmd")]
public sealed class StandardBulkTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account";
    private const string PipeLog = TestEntities.PipeLog;
    private const string Plugins = "Xrmd.TestPlugins.";
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _elastic;

    private readonly string _tag = "SB" + Guid.NewGuid().ToString("N")[..10];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private Guid _assemblyId;
    private readonly Dictionary<string, Guid> _types = new();

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{PipeLog};");
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<Guid> StepAsync(string plugin, string message, string? entity, int stage, string? config = null, int mode = 0,
        Guid? impersonatingUserId = null)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(Admin, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(Admin, typeId, message, entity, stage, mode: mode, config: config,
            impersonatingUserId: impersonatingUserId);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<List<Entity>> AccountsAsync(string prefix) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(Account)
        {
            ColumnSet = new ColumnSet("name", "emailaddress1"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.BeginsWith, prefix) } },
        })).Entities.ToList();

    private async Task<Entity?> AccountAsync(Guid id)
    {
        var query = new QueryExpression(Account) { ColumnSet = new ColumnSet("name", "emailaddress1") };
        query.Criteria.AddCondition("accountid", ConditionOperator.Equal, id);
        return (await Admin.RetrieveMultipleAsync(query)).Entities.SingleOrDefault();
    }

    /// <summary>The <c>pb_name</c> and <c>pb_data</c> lines of the rows the recorder plug-in wrote, in creation order.</summary>
    private async Task<List<(string Name, string[] Data)>> LogAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{PipeLog} ORDER BY createdon");
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string[])>();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? [] : rd.GetString(1).Split('\n')));
        return rows;
    }

    private static EntityCollection Targets(params Entity[] targets) => new(targets.ToList()) { EntityName = Account };

    private Entity NewAccount(string suffix, Guid? id = null) =>
        id is { } given ? new Entity(Account, given) { ["name"] = _tag + suffix } : new Entity(Account) { ["name"] = _tag + suffix };

    /// <summary>An elastic table (<c>pb_name</c> primary name), created once.</summary>
    private async Task<string> ElasticTableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_elastic is not null) return _elastic;
            var name = "pb_sbulk" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Standard bulk elastic");
            entity.TableType = ElasticTables.TableTypeElastic;
            await Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200, DisplayName = Label("Name") },
            });
            return _elastic = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    // ---- outcomes -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateMultiple_returns_ids_in_order()
    {
        var targets = Enumerable.Range(0, 100).Select(i => NewAccount($"-{i:D3}")).ToArray();

        var response = (CreateMultipleResponse)await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(targets) });

        response.Ids.Should().HaveCount(100).And.OnlyHaveUniqueItems();
        var rows = (await AccountsAsync(_tag)).ToDictionary(r => r.Id, r => r.GetAttributeValue<string>("name"));
        rows.Should().HaveCount(100);
        for (var i = 0; i < 100; i++) rows[response.Ids[i]].Should().Be($"{_tag}-{i:D3}");
        response.Results.Should().NotContainKey("x-ms-session-token");
    }

    [Fact]
    public async Task UpdateMultiple_updates_all()
    {
        var a = await Admin.CreateAsync(NewAccount("-a"));
        var b = await Admin.CreateAsync(NewAccount("-b"));

        var response = await Admin.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(new Entity(Account, a) { ["emailaddress1"] = "a@example.com" }, new Entity(Account, b) { ["emailaddress1"] = "b@example.com" }),
        });

        response.Should().BeOfType<UpdateMultipleResponse>();
        response.Results.Should().BeEmpty();
        (await AccountAsync(a))!.GetAttributeValue<string>("emailaddress1").Should().Be("a@example.com");
        (await AccountAsync(b))!.GetAttributeValue<string>("emailaddress1").Should().Be("b@example.com");
    }

    [Fact]
    public async Task UpsertMultiple_mixes_create_and_update()
    {
        var existing = await Admin.CreateAsync(NewAccount("-old"));
        var missing = Guid.NewGuid();

        var response = (UpsertMultipleResponse)await Admin.ExecuteAsync(new UpsertMultipleRequest
        {
            Targets = Targets(NewAccount("-upd", existing), NewAccount("-new", missing), NewAccount("-noid")),
        });

        response.Results.Should().HaveCount(3);
        response.Results.Select(r => r.RecordCreated).Should().Equal(false, true, true);
        response.Results[0].Target.Id.Should().Be(existing);
        response.Results[1].Target.Id.Should().Be(missing);
        (await AccountAsync(existing))!.GetAttributeValue<string>("name").Should().Be(_tag + "-upd");
        (await AccountAsync(missing))!.GetAttributeValue<string>("name").Should().Be(_tag + "-new");
        (await AccountAsync(response.Results[2].Target.Id))!.GetAttributeValue<string>("name").Should().Be(_tag + "-noid");
    }

    // ---- all or nothing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Failure_rolls_back_every_target()
    {
        // Review Focus 1: a PostOperation Create step throws on the 3rd target; nothing of the first two remains.
        await SetAuditAsync(true);
        Guid? queued = null;
        try
        {
            // An asynchronous step queues a job per target; the synchronous step refuses the third target only.
            await StepAsync("ContextRecorderPlugin", "Create", Account, 40, PipeLog, mode: 1);
            await StepAsync("ThrowOnNamePlugin", "Create", Account, 40, $"{_tag}-2|third refused");

            // Auditing is on: a successful bulk writes audit rows (the check below is not vacuous).
            var ok = (CreateMultipleResponse)await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-ok")) });
            (await CountAsync($"SELECT COUNT(*) FROM data.audit WHERE objectid = '{ok.Ids[0]}'")).Should().BeGreaterThan(0);
            // ... and the asynchronous step queues a job regarding the record (so is the asyncoperation check below).
            queued = ok.Ids[0];
            (await CountAsync($"SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = '{ok.Ids[0]}'")).Should().BeGreaterThan(0);

            var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var fault = await FaultAsync(() => Admin.ExecuteAsync(new CreateMultipleRequest
            {
                Targets = Targets(NewAccount("-0", ids[0]), NewAccount("-1", ids[1]), NewAccount("-2", ids[2])),
            }));

            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Be("third refused");
            fault.ErrorDetails.Should().NotContainKey("Plugin.BulkApiErrorDetails");
            var list = string.Join(",", ids.Select(id => $"'{id}'"));
            (await CountAsync($"SELECT COUNT(*) FROM data.account WHERE accountid IN ({list})")).Should().Be(0);
            (await CountAsync($"SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid IN ({list})")).Should().Be(0);
            (await CountAsync($"SELECT COUNT(*) FROM data.audit WHERE objectid IN ({list})")).Should().Be(0);
        }
        finally
        {
            if (queued is { } job) await ExecAsync($"DELETE FROM data.asyncoperation WHERE regardingobjectid = '{job}'");
            await SetAuditAsync(false);
        }
    }

    private async Task SetAuditAsync(bool enabled)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT TOP 1 organizationid FROM data.organization");
        var org = (Guid)(await cmd.ExecuteScalarAsync())!;
        await Admin.UpdateAsync(new Entity("organization", org) { ["isauditenabled"] = enabled });
    }

    [Fact]
    public async Task Core_failure_rolls_back()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var tooLong = new Entity(Account, ids[2]) { ["name"] = _tag + new string('x', 5000) };

        var message = await BadRequestAsync(() => Admin.ExecuteAsync(new CreateMultipleRequest
        {
            Targets = Targets(NewAccount("-0", ids[0]), NewAccount("-1", ids[1]), tooLong),
        }));

        message.Should().Contain("name");
        (await AccountsAsync(_tag)).Should().BeEmpty();
    }

    [Fact]
    public async Task NullRow_InTargets_Is400_NotAnException()
    {
        // Plan 4.1b (P6): the mail secret strip skips a null row, so BulkTargets still reports it as a 400.
        var message = await BadRequestAsync(() => Admin.ExecuteAsync(new CreateMultipleRequest
        {
            Targets = new EntityCollection(new List<Entity> { NewAccount("-good"), null! }) { EntityName = Account },
        }));

        message.Should().Contain("Target 1").And.Contain("(none)");
        (await AccountsAsync(_tag)).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_target_runs_no_prevalidation_step()
    {
        // Validated before the pipeline, as a direct Create: the bulk's PreValidation step (outside the transaction) never runs.
        var admin = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
        await StepAsync("ContextRecorderPlugin", "CreateMultiple", Account, 10, PipeLog, impersonatingUserId: admin);
        var invalid = new Entity(Account) { ["name"] = _tag + "-bad", ["pb_nosuchcolumn"] = "x" };

        var message = await BadRequestAsync(() => Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-good"), invalid) }));

        message.Should().Contain("pb_nosuchcolumn");
        (await LogAsync()).Should().BeEmpty();
        (await AccountsAsync(_tag)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateMultiple_checks_each_target()
    {
        // B13: each target is checked in full by its inner Update. User-depth Write covers the caller's account, not the other one.
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        await GrantAsync(fx, user, false, ("prvReadAccount", DepthMask.Global), ("prvCreateAccount", DepthMask.Basic),
            ("prvWriteAccount", DepthMask.Basic));
        var service = ServiceFor(fx, oid);
        var mine = await service.CreateAsync(NewAccount("-mine"));
        var other = await Admin.CreateAsync(NewAccount("-other"));

        var fault = await FaultAsync(() => service.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(NewAccount("-mine2", mine), NewAccount("-other2", other)),
        }));

        fault.HttpStatus.Should().Be(403);
        (await AccountAsync(mine))!.GetAttributeValue<string>("name").Should().Be(_tag + "-mine");
        (await AccountAsync(other))!.GetAttributeValue<string>("name").Should().Be(_tag + "-other");
    }

    // ---- steps --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_steps_fire_per_target()
    {
        await StepAsync("ContextRecorderPlugin", "Create", Account, 20, PipeLog);

        await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-0"), NewAccount("-1"), NewAccount("-2")) });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("Create|20|0|1", "Create|20|0|1", "Create|20|0|1");
        // The ParentContext reports the outer's stage (MainOperation, 30), not the base context's 0.
        log.Should().OnlyContain(r => r.Data.Contains("parentstage=30"));
        log.Should().OnlyContain(r => r.Data.Contains("parent=CreateMultiple") && r.Data.Contains("intx=True")
                                      && r.Data.Contains("primary=account") && r.Data.Contains("targets="));
    }

    [Fact]
    public async Task Single_steps_keep_depth_one()
    {
        // Review Focus 2 (bulk half): a plug-in guarding on Depth > 1 still runs its single-record logic once per target.
        await StepAsync("ContextRecorderPlugin", "Create", Account, 40, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Update", Account, 20, PipeLog);

        var created = (CreateMultipleResponse)await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-0"), NewAccount("-1")) });
        await Admin.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(NewAccount("-0b", created.Ids[0]), NewAccount("-1b", created.Ids[1])),
        });

        (await LogAsync()).Select(r => r.Name).Should().Equal("Create|40|0|1", "Create|40|0|1", "Update|20|0|1", "Update|20|0|1");
    }

    [Fact]
    public async Task Multiple_steps_fire_once()
    {
        await StepAsync("ContextRecorderPlugin", "CreateMultiple", Account, 20, PipeLog);
        await StepAsync("ContextRecorderPlugin", "CreateMultiple", Account, 40, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Create", Account, 20, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Create", Account, 40, PipeLog);

        await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-0"), NewAccount("-1"), NewAccount("-2")) });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal(
            "CreateMultiple|20|0|1",
            "Create|20|0|1", "Create|40|0|1", "Create|20|0|1", "Create|40|0|1", "Create|20|0|1", "Create|40|0|1",
            "CreateMultiple|40|0|1");
        log[0].Data.Should().Contain(["targets=3", "primary=account", "parent=", "target=", "ids="]);
        log[^1].Data.Should().Contain(["targets=3", "ids=3", "output=Ids"]);
    }

    [Fact]
    public async Task UpdateMultiple_ignores_repeated_id()
    {
        var id = await Admin.CreateAsync(NewAccount("-orig"));
        await StepAsync("ContextRecorderPlugin", "Update", Account, 20, PipeLog);

        await Admin.ExecuteAsync(new UpdateMultipleRequest { Targets = Targets(NewAccount("-first", id), NewAccount("-second", id)) });

        (await AccountAsync(id))!.GetAttributeValue<string>("name").Should().Be(_tag + "-first");
        (await LogAsync()).Select(r => r.Name).Should().Equal("Update|20|0|1");
    }

    [Fact]
    public async Task UpsertMultiple_repeated_id_fails_before_any_step()
    {
        await StepAsync("ThrowPlugin", "Create", Account, 10, "invalid:a step ran");
        var id = Guid.NewGuid();

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new UpsertMultipleRequest
        {
            Targets = Targets(NewAccount("-a", id), NewAccount("-b", id)),
        }));

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Be($"UpsertMultiple names the 'account' record {id} more than once: each target must be a different record.");
        (await AccountAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteMultiple_on_standard_keeps_refusal()
    {
        var id = await Admin.CreateAsync(NewAccount("-dm"));

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new DeleteMultipleRequest
        {
            Targets = new EntityReferenceCollection { new EntityReference(Account, id) },
        }));

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Be("DeleteMultiple has not yet been implemented.");
        (await AccountAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Bulk_from_plugin_is_refused()
    {
        var elastic = await ElasticTableAsync();
        foreach (var (message, table, attribute) in new[]
                 {
                     ("CreateMultiple", Account, "name"), ("UpdateMultiple", Account, "name"), ("UpsertMultiple", Account, "name"),
                     ("CreateMultiple", elastic, "pb_name"),
                 })
        {
            await PluginTestHelper.ClearAsync(fx);
            _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            _types.Clear();
            // PreValidation: outside the transaction, so the refused call does not doom it.
            await StepAsync("TryBulkPlugin", "Create", TestEntities.Pipe, 10, $"{message}|{table}|{attribute}");

            var id = await Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = _tag + message });

            var row = await Admin.RetrieveAsync(TestEntities.Pipe, id, new ColumnSet("pb_data"));
            row.GetAttributeValue<string>("pb_data").Should()
                .Be($"caught {unchecked((int)ErrorCodes.InvalidArgument)}|{message} cannot be called from plug-in code.", $"{message} on {table}");
        }

        (await CountAsync($"SELECT COUNT(*) FROM data.{elastic} WHERE pb_name = N'from plugin'")).Should().Be(0);
    }

    [Fact]
    public async Task Bulk_without_privilege_runs_no_plugin()
    {
        // B13: the table's Create privilege is demanded before stage 10 of CreateMultiple.
        var admin = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
        await StepAsync("ContextRecorderPlugin", "CreateMultiple", Account, 10, PipeLog, impersonatingUserId: admin);
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        await GrantAsync(fx, user, false, ("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global));

        var fault = await FaultAsync(() => ServiceFor(fx, oid).ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-denied")) }));

        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        fault.Message.Should().Contain("prvCreateAccount");
        (await LogAsync()).Should().BeEmpty();
        (await AccountsAsync(_tag)).Should().BeEmpty();

        // UpdateMultiple demands Write: this user holds it, so the PreValidation step of UpdateMultiple runs.
        var id = await Admin.CreateAsync(NewAccount("-mine"));
        // That single Create ran the CreateMultiple step (Task 4, ruling B5): only the UpdateMultiple call is checked below.
        await ExecAsync($"DELETE FROM data.{PipeLog};");
        await StepAsync("ContextRecorderPlugin", "UpdateMultiple", Account, 10, PipeLog, impersonatingUserId: admin);
        await ServiceFor(fx, oid).ExecuteAsync(new UpdateMultipleRequest { Targets = Targets(NewAccount("-mine2", id)) });
        (await LogAsync()).Select(r => r.Name).Should().Equal("UpdateMultiple|10|0|1");
    }
}
