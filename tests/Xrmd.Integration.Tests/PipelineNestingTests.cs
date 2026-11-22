using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The organization service given to plug-ins: nested calls share the transaction, carry depth and parent context,
/// run as SYSTEM or an impersonated user, surface faults as <c>FaultException</c> and stop working once the plug-in ends.
/// </summary>
[Collection("xrmd")]
public sealed class PipelineNestingTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string PipeLog2 = TestEntities.PipeLog2;
    private const string Plugins = "Xrmd.TestPlugins.";

    private OrganizationService _svc = null!;
    private Guid _assemblyId;
    private Caller _admin = null!;
    private Guid _systemId;
    private readonly Dictionary<string, Guid> _types = new();
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.{PipeLog2};");
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using (var cmd = TestSql.Command(conn, "SELECT systemuserid, businessunitid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid"))
        {
            cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
            await using var rd = await cmd.ExecuteReaderAsync();
            await rd.ReadAsync();
            _admin = new Caller(rd.GetGuid(0), rd.GetGuid(1));
        }

        await using (var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE fullname = N'SYSTEM'"))
            _systemId = (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        foreach (var id in _users)
        {
            await ExecAsync($"DELETE FROM data.{Pipe} WHERE ownerid = '{id}'; DELETE FROM data.{PipeLog} WHERE ownerid = '{id}'; " +
                            $"DELETE FROM data.{PipeLog2} WHERE ownerid = '{id}'; DELETE FROM data.systemuser WHERE systemuserid = '{id}';");
        }
    }

    // ---- helpers ----

    private async Task<Guid> StepAsync(string plugin, string message, string entity, int stage, string? config = null, int rank = 1,
        Guid? impersonatingUserId = null)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(_svc, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(_svc, typeId, message, entity, stage, rank: rank, config: config,
            impersonatingUserId: impersonatingUserId);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private async Task<int> CountAsync(string entity) => Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.{entity}"));

    /// <summary>The single row of <paramref name="entity"/>: its name, data lines and creator.</summary>
    private async Task<(string Name, string[] Data, Guid CreatedBy)> SingleAsync(string entity)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data, createdby FROM data.{entity}");
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue($"{entity} should have a row");
        var row = (rd.GetString(0), rd.IsDBNull(1) ? [] : rd.GetString(1).Split('\n'), rd.GetGuid(2));
        (await rd.ReadAsync()).Should().BeFalse($"{entity} should have a single row");
        return row;
    }

    private Task<Guid> CreatePipeAsync(int? count = null)
    {
        var e = new Entity(Pipe) { ["pb_name"] = "p" };
        if (count is not null) e["pb_count"] = count.Value;
        return _svc.CreateAsync(e);
    }

    private async Task<Guid> NewUserAsync()
    {
        var id = Guid.NewGuid();
        _users.Add(id);
        await ExecAsync($"""
            INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES ('{id}', N'Imp User', N'imp', NULL, '{_admin.BusinessUnitId}', '{_admin.BusinessUnitId}', SYSUTCDATETIME(),
                '{_admin.UserId}', SYSUTCDATETIME(), '{_admin.UserId}', 0, 1)
            """);
        return id;
    }

    private static async Task<string?> WaitForVariableAsync(string key)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Environment.GetEnvironmentVariable(key) is { } value) return value;
            await Task.Delay(100);
        }

        return null;
    }

    // ---- tests ----

    [Fact]
    public async Task Nested_create_shares_transaction_and_rolls_back()
    {
        await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, PipeLog, rank: 1);
        var throwStep = await StepAsync("ThrowPlugin", "Create", Pipe, 40, "invalid:x", rank: 2);

        var act = () => CreatePipeAsync();

        await act.Should().ThrowAsync<XrmdFaultException>().Where(f => f.HttpStatus == 400 && f.Message == "x");
        (await CountAsync(Pipe)).Should().Be(0);
        (await CountAsync(PipeLog)).Should().Be(0);

        await _svc.DeleteAsync("sdkmessageprocessingstep", throwStep);
        var id = await CreatePipeAsync();

        (await CountAsync(Pipe)).Should().Be(1);
        (await SingleAsync(PipeLog)).Name.Should().Be($"Create|40|1|{id}");
    }

    [Fact]
    public async Task Nested_call_has_depth_and_parent_context()
    {
        await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Create", PipeLog, 40, PipeLog2);

        await CreatePipeAsync();

        var (name, data, _) = await SingleAsync(PipeLog2);
        name.Should().Be("Create|40|0|2");
        data.Should().Contain("parent=Create");
        data.Should().Contain($"user={_admin.UserId}");
        data.Should().Contain($"initiating={_admin.UserId}");
        data.Should().Contain("intx=True");
    }

    [Fact]
    public async Task Infinite_loop_stops_at_depth_8()
    {
        var id = await CreatePipeAsync(count: 5);
        await StepAsync("UpdateSelfPlugin", "Update", Pipe, 40);

        var act = () => _svc.UpdateAsync(new Entity(Pipe, id) { ["pb_count"] = 100 });

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(unchecked((int)0x80044182));
        fault.Message.Should().Be("This workflow job was canceled because the workflow that started it included an infinite loop. Correct the workflow logic and try again.");
        (await ScalarAsync($"SELECT pb_count FROM data.{Pipe} WHERE {Pipe}id = '{id}'")).Should().Be(5);
    }

    [Fact]
    public async Task CreateOrganizationService_null_runs_as_SYSTEM()
    {
        await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, PipeLog + "|system");
        await StepAsync("ContextRecorderPlugin", "Create", PipeLog, 40, PipeLog2);

        await CreatePipeAsync();

        (await SingleAsync(PipeLog)).CreatedBy.Should().Be(_systemId);
        var (_, data, _) = await SingleAsync(PipeLog2);
        data.Should().Contain($"user={_systemId}");
        data.Should().Contain($"initiating={_admin.UserId}");
    }

    [Fact]
    public async Task Impersonating_step_runs_as_given_user()
    {
        var user = await NewUserAsync();
        // The plug-in's calls run with the impersonated user's security: it needs to create its log.
        await SecurityTestHelper.GrantAsync(fx, user, team: false, ("prvCreatepb_PipeLog", DepthMask.Basic));
        await StepAsync("ContextRecorderPlugin", "Create", Pipe, 40, PipeLog, impersonatingUserId: user);

        await CreatePipeAsync();

        var (_, data, createdBy) = await SingleAsync(PipeLog);
        data.Should().Contain($"user={user}");
        data.Should().Contain($"initiating={_admin.UserId}");
        createdBy.Should().Be(user);
    }

    [Fact]
    public async Task CreateOrganizationService_with_unknown_user_is_a_fault()
    {
        await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, $"{PipeLog}|{Guid.NewGuid()}");

        var act = () => CreatePipeAsync();

        // The plug-in does not catch the FaultException: it surfaces as the plug-in's 400 with the fault's code.
        await act.Should().ThrowAsync<XrmdFaultException>().Where(f => f.HttpStatus == 400 && f.ErrorCode == ErrorCodes.ObjectDoesNotExist);
        (await CountAsync(Pipe)).Should().Be(0);
        (await CountAsync(PipeLog)).Should().Be(0);
    }

    private const string NoActiveTransaction =
        "There is no active transaction. This error is usually caused by custom plug-ins that ignore errors from service calls and continue processing.";

    [Fact]
    public async Task Plugin_swallowing_failed_nested_retrieve_dooms_the_transaction()
    {
        await StepAsync("TryRetrieveMissingPlugin", "Create", Pipe, 20);

        var act = () => CreatePipeAsync();

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().Be(NoActiveTransaction);
        (await CountAsync(Pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Plugin_catching_nested_fault_outside_the_transaction_can_continue()
    {
        // PreValidation at the first level runs outside the transaction: a failed call there dooms nothing.
        await StepAsync("TryRetrieveMissingPlugin", "Create", Pipe, 10);

        var id = await CreatePipeAsync();

        (await ScalarAsync($"SELECT pb_data FROM data.{Pipe} WHERE {Pipe}id = '{id}'"))
            .Should().Be("caught " + ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public async Task Swallowed_nested_failure_rolls_back_the_nested_writes_and_fails_the_request()
    {
        var existing = await _svc.CreateAsync(new Entity(PipeLog) { ["pb_name"] = "existing" });
        // The nested Create of pb_pipelog writes Y (a pb_pipe2log) in its PreOperation, then its core fails (duplicate
        // id); the outer plug-in swallows the fault and returns normally.
        await StepAsync("CreateRecordPlugin", "Create", PipeLog, 20, PipeLog2);
        await StepAsync("TryCreatePlugin", "Create", Pipe, 20, $"{PipeLog}|{existing}");

        var act = () => CreatePipeAsync();

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().Be(NoActiveTransaction);
        (await CountAsync(Pipe)).Should().Be(0);
        (await CountAsync(PipeLog2)).Should().Be(0, "Y was written in the doomed transaction");
        (await CountAsync(PipeLog)).Should().Be(1);
    }

    [Fact]
    public async Task Service_is_revoked_after_plugin_timeout()
    {
        var key = "XRMD_TEST_" + Guid.NewGuid().ToString("N");
        await StepAsync("LateServiceCallPlugin", "Create", Pipe, 40, $"1500|{key}");
        var runner = new PluginRunner(new PipelineOptions { PluginTimeout = TimeSpan.FromMilliseconds(300) });
        var executor = TestServices.NewPipelineExecutor(fx.App.Services, runner);

        var act = () => executor.ExecuteAsync(new CreateRequest { Target = new Entity(Pipe) { ["pb_name"] = "t" } },
            new InvocationContext(_admin, _admin.UserId, 1, null, null, Guid.NewGuid()), default);

        await act.Should().ThrowAsync<XrmdFaultException>().Where(f => f.HttpStatus == 500 && f.Message.Contains("timed-out"));
        (await WaitForVariableAsync(key)).Should().Be("fault " + ErrorCodes.UnexpectedPluginException);
        (await CountAsync(Pipe)).Should().Be(0);
        (await CountAsync(PipeLog)).Should().Be(0);
    }

    [Fact]
    public async Task Service_is_revoked_after_request_cancellation()
    {
        var key = "XRMD_TEST_" + Guid.NewGuid().ToString("N");
        await StepAsync("LateServiceCallPlugin", "Create", Pipe, 40, $"1500|{key}");
        var executor = TestServices.NewPipelineExecutor(fx.App.Services);
        using var cts = new CancellationTokenSource();

        var execution = executor.ExecuteAsync(new CreateRequest { Target = new Entity(Pipe) { ["pb_name"] = "t" } },
            new InvocationContext(_admin, _admin.UserId, 1, null, null, Guid.NewGuid()), cts.Token);
        // Cancel only once the plug-in runs (it marks its start before sleeping), whatever the machine's speed.
        (await WaitForVariableAsync(key + "_STARTED")).Should().Be("1");
        await cts.CancelAsync();

        var act = () => execution;
        await act.Should().ThrowAsync<OperationCanceledException>();
        (await WaitForVariableAsync(key)).Should().Be("fault " + ErrorCodes.UnexpectedPluginException);
        (await CountAsync(Pipe)).Should().Be(0);
        (await CountAsync(PipeLog)).Should().Be(0);
    }

    [Fact]
    public async Task Non_fault_exception_of_nested_call_reaches_plugin_as_unexpected_fault()
    {
        // PreValidation (outside the transaction), so that the swallowed failure dooms nothing.
        await StepAsync("TryExecutePlugin", "Create", Pipe, 10, "pb_NoSuchMessage");

        var id = await CreatePipeAsync();

        var data = (string?)await ScalarAsync($"SELECT pb_data FROM data.{Pipe} WHERE {Pipe}id = '{id}'");
        data.Should().StartWith("caught " + ErrorCodes.Unexpected + "|").And.Contain("pb_NoSuchMessage");
    }

    [Fact]
    public async Task Early_bound_plugin_runs_and_creates_early_bound_records()
    {
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.EarlyBoundPluginBytes,
            "Xrmd.TestPlugins.EarlyBound");
        var typeId = await PluginTestHelper.RegisterTypeAsync(_svc, assemblyId, "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin");
        await PluginTestHelper.RegisterStepAsync(_svc, typeId, "Create", Pipe, 40);

        await CreatePipeAsync();

        (await SingleAsync(PipeLog)).Name.Should().Be("early|p|1");
    }

    /// <summary>
    /// Plan 3.5 UI-1b final review, I3: RetrievePrincipalAccess and RetrieveSharedPrincipalsAndAccess answer a plug-in in its own
    /// transaction (PostOperation: the record is not committed yet) instead of failing with an unexpected fault that dooms it.
    /// </summary>
    [Fact]
    public async Task Access_read_messages_work_from_a_plugin()
    {
        await StepAsync("AccessMessagesPlugin", "Create", Pipe, 40, PipeLog);

        await CreatePipeAsync();

        var name = (await SingleAsync(PipeLog)).Name;
        name.Should().EndWith("|shares=0");
        var rights = (AccessRights)int.Parse(name["rights=".Length..name.IndexOf('|', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
        rights.Should().HaveFlag(AccessRights.ReadAccess).And.HaveFlag(AccessRights.WriteAccess).And.HaveFlag(AccessRights.DeleteAccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhoAmI_RetrieveEntity_and_RetrieveAttribute_work_from_a_plugin(bool asSystem)
    {
        await StepAsync("NonPipelineCallsPlugin", "Create", Pipe, 20, asSystem ? "system" : "caller");
        var organizationId = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        var user = asSystem ? _systemId : _admin.UserId;
        var businessUnit = (Guid)(await ScalarAsync($"SELECT businessunitid FROM data.systemuser WHERE systemuserid = '{user}'"))!;

        var id = await CreatePipeAsync();

        (await ScalarAsync($"SELECT pb_data FROM data.{Pipe} WHERE {Pipe}id = '{id}'"))
            .Should().Be($"user={user}|bu={businessUnit}|org={organizationId}|entity={Pipe}|attr=pb_name");
    }
}
