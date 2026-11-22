using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The identity plug-ins run with: an async job runs as the owner of its <c>asyncoperation</c> row (never as the user its
/// payload names), its payload is read through the SDK type allowlist, and <c>CreateOrganizationService(userId)</c> and
/// impersonating steps apply the target user's security (a disabled one is <c>0x80040225</c>). User L can create
/// <c>pb_pipe</c> but not <c>pb_pipelog</c>, which the <c>CreateRecordPlugin</c> steps write.
/// </summary>
[Collection("xrmd")]
public sealed class PipelineSecurityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string PipeLog2 = TestEntities.PipeLog2;
    private const string Plugins = "Xrmd.TestPlugins.";
    private const int Async = 1;
    private const string OwnerMessage = "The owner of the system job does not exist or is disabled.";

    private OrganizationService _admin = null!;
    private Guid _adminId;
    private Guid _assemblyId;
    private (Guid Id, Guid Oid) _l;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.asyncoperation;");
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _adminId = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId)))!;
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);

        var oid = Guid.NewGuid();
        _l = (await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid), oid);
        await GrantAsync(fx, _l.Id, team: false, ("prvCreatepb_Pipe", DepthMask.Global), ("prvReadpb_Pipe", DepthMask.Global));
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.asyncoperation; DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.{PipeLog2};");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100), AsyncMaxRetries = 0 }));

    private async Task<Guid> StepAsync(string config, int mode = 0, Guid? impersonatingUserId = null)
    {
        var type = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "CreateRecordPlugin");
        return await PluginTestHelper.RegisterStepAsync(_admin, type, "Create", Pipe, 40, mode: mode, config: config,
            impersonatingUserId: impersonatingUserId);
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<(int State, int Status, int? ErrorCode, string? Message)> JobAsync(Guid regarding)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT statecode, statuscode, errorcode, message FROM data.asyncoperation WHERE regardingobjectid = @id");
        cmd.Parameters.AddWithValue("@id", regarding);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue("the job of the record is expected");
        return (rd.GetInt32(0), rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetInt32(2), rd.IsDBNull(3) ? null : rd.GetString(3));
    }

    private async Task<int> LogCountAsync(Guid pipe) =>
        Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.{PipeLog} WHERE pb_name = @name", ("@name", $"Create|40|1|{pipe}")));

    /// <summary>L creates a <c>pb_pipe</c> (queuing the async job it owns) and the job is run once.</summary>
    private async Task<Guid> CreateAsLAndRunAsync(Func<Guid, Task>? tamper = null)
    {
        var pipe = await ServiceFor(fx, _l.Oid).CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });
        if (tamper is not null) await tamper(pipe);
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();
        return pipe;
    }

    private async Task SetDisabledAsync(Guid userId, bool disabled)
    {
        await ExecAsync("UPDATE data.systemuser SET isdisabled = @disabled WHERE systemuserid = @id", ("@disabled", disabled), ("@id", userId));
        Cache(fx).Invalidate();
    }

    private static Task FaultAsync(Func<Task> act, int errorCode) =>
        act.Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == errorCode);

    // ---- async jobs -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Async_job_runs_as_row_owner_not_payload_user()
    {
        await StepAsync(PipeLog, mode: Async);

        // The payload is rewritten to name the System Administrator: the job still runs as L, its owner.
        var tampered = await CreateAsLAndRunAsync(async pipe =>
        {
            await ExecAsync("UPDATE data.asyncoperation SET data = REPLACE(data, @l, @admin) WHERE regardingobjectid = @pipe",
                ("@l", _l.Id.ToString()), ("@admin", _adminId.ToString()), ("@pipe", pipe));
            var data = (string)(await ScalarAsync("SELECT data FROM data.asyncoperation WHERE regardingobjectid = @pipe", ("@pipe", pipe)))!;
            data.Should().Contain(_adminId.ToString()).And.NotContain(_l.Id.ToString(), "the tamper must rewrite every user of the payload");
        });
        var job = await JobAsync(tampered);
        (job.State, job.Status, job.ErrorCode).Should().Be((3, 31, ErrorCodes.PrivilegeDenied));
        job.Message.Should().Contain("prvCreatepb_PipeLog");
        (await LogCountAsync(tampered)).Should().Be(0);

        var plain = await CreateAsLAndRunAsync();
        (await JobAsync(plain)).Should().Match<(int State, int Status, int? ErrorCode, string? Message)>(
            j => j.State == 3 && j.Status == 31 && j.ErrorCode == ErrorCodes.PrivilegeDenied);

        await GrantAsync(fx, _l.Id, team: false, ("prvCreatepb_PipeLog", DepthMask.Basic));
        var allowed = await CreateAsLAndRunAsync();
        (await JobAsync(allowed)).Should().Match<(int State, int Status, int? ErrorCode, string? Message)>(j => j.State == 3 && j.Status == 30);
        (await ScalarAsync($"SELECT createdby FROM data.{PipeLog} WHERE pb_name = @name", ("@name", $"Create|40|1|{allowed}")))
            .Should().Be(_l.Id);
    }

    [Fact]
    public async Task Async_job_of_disabled_owner_fails_without_running()
    {
        await StepAsync(PipeLog, mode: Async);
        await GrantAsync(fx, _l.Id, team: false, ("prvCreatepb_PipeLog", DepthMask.Basic));

        var disabled = await CreateAsLAndRunAsync(_ => SetDisabledAsync(_l.Id, true));
        var job = await JobAsync(disabled);
        (job.State, job.Status, job.Message).Should().Be((3, 31, OwnerMessage));
        job.ErrorCode.Should().Be(ErrorCodes.UserDisabled);
        (await LogCountAsync(disabled)).Should().Be(0);

        // An owner that does not exist (the row rewritten) fails the same way.
        await SetDisabledAsync(_l.Id, false);
        var missing = await CreateAsLAndRunAsync(pipe => ExecAsync(
            "UPDATE data.asyncoperation SET ownerid = @other WHERE regardingobjectid = @pipe", ("@other", Guid.NewGuid()), ("@pipe", pipe)));
        job = await JobAsync(missing);
        (job.State, job.Status, job.Message).Should().Be((3, 31, OwnerMessage));
        job.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await LogCountAsync(missing)).Should().Be(0);
    }

    [Fact]
    public async Task Async_job_queued_by_a_SYSTEM_call_keeps_the_initiating_user()
    {
        var system = await Cache(fx).SystemUserIdAsync(default);
        // L creates a pipe; a synchronous plug-in creates a pipelog as SYSTEM, which queues the recorder.
        await StepAsync(PipeLog + "|system");
        var recorder = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(_admin, recorder, "Create", PipeLog, 40, mode: Async, config: PipeLog2);

        await ServiceFor(fx, _l.Oid).CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });

        (await ScalarAsync("SELECT createdby FROM data.asyncoperation")).Should().Be(_l.Id);
        (await ScalarAsync("SELECT ownerid FROM data.asyncoperation")).Should().Be(system);
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();
        var data = (string)(await ScalarAsync($"SELECT pb_data FROM data.{PipeLog2}"))!;
        data.Should().Contain($"user={system}");
        data.Should().Contain($"initiating={_l.Id}");
        data.Should().Contain($"initiatingoid={_l.Oid}");
    }

    [Fact]
    public async Task Async_job_reads_Entra_object_ids_from_its_users_not_the_payload()
    {
        await GrantAsync(fx, _l.Id, team: false, ("prvCreatepb_PipeLog", DepthMask.Basic));
        var recorder = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(_admin, recorder, "Create", Pipe, 40, mode: Async, config: PipeLog);

        // The payload's object ids are rewritten to the System Administrator's.
        await CreateAsLAndRunAsync(async pipe =>
        {
            var data = (string)(await ScalarAsync("SELECT data FROM data.asyncoperation WHERE regardingobjectid = @pipe", ("@pipe", pipe)))!;
            var doc = XDocument.Parse(data);
            var forged = doc.Descendants()
                .Where(e => e.Name.LocalName is "UserAzureActiveDirectoryObjectId" or "InitiatingUserAzureActiveDirectoryObjectId").ToList();
            forged.Should().HaveCount(2);
            foreach (var element in forged) element.Value = fx.AdminObjectId.ToString();
            await ExecAsync("UPDATE data.asyncoperation SET data = @data WHERE regardingobjectid = @pipe",
                ("@data", doc.ToString(SaveOptions.DisableFormatting)), ("@pipe", pipe));
        });

        var recorded = (string)(await ScalarAsync($"SELECT pb_data FROM data.{PipeLog}"))!;
        recorded.Should().Contain($"useroid={_l.Oid}");
        recorded.Should().Contain($"initiatingoid={_l.Oid}");
        recorded.Should().NotContain(fx.AdminObjectId.ToString());
    }

    [Fact]
    public async Task Payload_with_disallowed_type_fails()
    {
        await StepAsync(PipeLog, mode: Async);
        await GrantAsync(fx, _l.Id, team: false, ("prvCreatepb_PipeLog", DepthMask.Basic));
        var pipe = await CreateAsLAndRunAsync(async id =>
        {
            var data = (string)(await ScalarAsync("SELECT data FROM data.asyncoperation WHERE regardingobjectid = @id", ("@id", id)))!;
            // A copy of the Target parameter retyped as System.Version.
            var doc = XDocument.Parse(data);
            var target = doc.Descendants().First(e => e.Name.LocalName == "KeyValuePairOfstringanyType");
            var copy = new XElement(target);
            copy.Elements().First(e => e.Name.LocalName == "key").Value = "version";
            var value = copy.Elements().First(e => e.Name.LocalName == "value");
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            value.ReplaceAll(new XAttribute(xsi + "type", "v:Version"),
                new XAttribute(XNamespace.Xmlns + "v", "http://schemas.datacontract.org/2004/07/System"),
                new XElement(XName.Get("_Major", "http://schemas.datacontract.org/2004/07/System"), 1));
            target.AddAfterSelf(copy);
            await ExecAsync("UPDATE data.asyncoperation SET data = @data WHERE regardingobjectid = @id",
                ("@data", doc.ToString(SaveOptions.DisableFormatting)), ("@id", id));
        });

        var job = await JobAsync(pipe);
        (job.State, job.Status, job.ErrorCode).Should().Be((3, 31, ErrorCodes.Unexpected));
        (await LogCountAsync(pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Payload_with_DBNull_fails()
    {
        await StepAsync(PipeLog, mode: Async);
        await GrantAsync(fx, _l.Id, team: false, ("prvCreatepb_PipeLog", DepthMask.Basic));
        var pipe = await CreateAsLAndRunAsync(async id =>
        {
            var data = (string)(await ScalarAsync("SELECT data FROM data.asyncoperation WHERE regardingobjectid = @id", ("@id", id)))!;
            // A copy of the Target parameter retyped as xsd:DBNull, which KnownTypesResolver maps to System.DBNull.
            var doc = XDocument.Parse(data);
            var target = doc.Descendants().First(e => e.Name.LocalName == "KeyValuePairOfstringanyType");
            var copy = new XElement(target);
            copy.Elements().First(e => e.Name.LocalName == "key").Value = "dbnull";
            var value = copy.Elements().First(e => e.Name.LocalName == "value");
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            value.ReplaceAll(new XAttribute(xsi + "type", "x:DBNull"),
                new XAttribute(XNamespace.Xmlns + "x", "http://www.w3.org/2001/XMLSchema"));
            target.AddAfterSelf(copy);
            await ExecAsync("UPDATE data.asyncoperation SET data = @data WHERE regardingobjectid = @id",
                ("@data", doc.ToString(SaveOptions.DisableFormatting)), ("@id", id));
        });

        var job = await JobAsync(pipe);
        (job.State, job.Status, job.ErrorCode).Should().Be((3, 31, ErrorCodes.Unexpected));
        (await LogCountAsync(pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Async_step_impersonating_an_unavailable_user_fails_at_once()
    {
        var oid = Guid.NewGuid();
        var impersonated = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, impersonated, team: false, ("prvCreatepb_PipeLog", DepthMask.Basic));
        var step = await StepAsync(PipeLog, mode: Async, impersonatingUserId: impersonated);
        var worker = new AsyncOperationWorker(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100), AsyncMaxRetries = 3 }));

        var disabled = await ServiceFor(fx, _l.Oid).CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });
        await SetDisabledAsync(impersonated, true);
        (await worker.RunOnceAsync(default)).Should().BeTrue();
        var job = await JobAsync(disabled);
        (job.State, job.Status, job.ErrorCode).Should().Be((3, 31, ErrorCodes.UserDisabled));
        (await ScalarAsync("SELECT retrycount FROM data.asyncoperation WHERE regardingobjectid = @id", ("@id", disabled))).Should().Be(0);
        (await LogCountAsync(disabled)).Should().Be(0);

        // The impersonated user removed from under the step (direct SQL: a request cannot point it to a missing user).
        await ExecAsync("UPDATE data.sdkmessageprocessingstep SET impersonatinguserid = @missing WHERE sdkmessageprocessingstepid = @step",
            ("@missing", Guid.NewGuid()), ("@step", step));
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
        var missing = await ServiceFor(fx, _l.Oid).CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });
        (await worker.RunOnceAsync(default)).Should().BeTrue();
        job = await JobAsync(missing);
        (job.State, job.Status, job.ErrorCode).Should().Be((3, 31, ErrorCodes.ObjectDoesNotExist));
        (await LogCountAsync(missing)).Should().Be(0);
    }

    // ---- plug-in users ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateOrganizationService_applies_target_user_security()
    {
        await StepAsync($"{PipeLog}|user:{_l.Id}");

        await FaultAsync(() => _admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" }), ErrorCodes.PrivilegeDenied);

        await SetDisabledAsync(_l.Id, true);
        await FaultAsync(() => _admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" }), ErrorCodes.UserDisabled);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{Pipe}")).Should().Be(0);
    }

    [Fact]
    public async Task Impersonating_step_runs_with_impersonated_security()
    {
        await StepAsync(PipeLog, impersonatingUserId: _l.Id);

        await FaultAsync(() => _admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" }), ErrorCodes.PrivilegeDenied);

        await SetDisabledAsync(_l.Id, true);
        await FaultAsync(() => _admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" }), ErrorCodes.UserDisabled);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{Pipe}")).Should().Be(0);
    }
}
