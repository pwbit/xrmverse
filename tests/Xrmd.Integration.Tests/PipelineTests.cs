using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Integration.Tests;

/// <summary>The synchronous plug-in pipeline (stages 10/20/40) through <see cref="OrganizationService"/> and the Web API.</summary>
[Collection("xrmd")]
public sealed class PipelineTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string Plugins = "Xrmd.TestPlugins.";
    private const string UnexpectedPrefix = "Unexpected exception from plug-in (Execute): ";

    private OrganizationService _svc = null!;
    private Guid _assemblyId;
    private readonly Dictionary<string, Guid> _types = new();

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.plugintracelog;");
        await SetTraceSettingAsync(1);
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await SetTraceSettingAsync(1);
    }

    // ---- helpers ----

    private async Task<Guid> StepAsync(string plugin, string message, int stage, string? config = null, int rank = 1,
        string? filteringAttributes = null, Guid? assemblyId = null)
    {
        var key = (assemblyId ?? _assemblyId) + plugin;
        if (!_types.TryGetValue(key, out var typeId))
            _types[key] = typeId = await PluginTestHelper.RegisterTypeAsync(_svc, assemblyId ?? _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(_svc, typeId, message, Pipe, stage, rank: rank, config: config,
            filteringAttributes: filteringAttributes);
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

    private Task SetTraceSettingAsync(int value) => ExecAsync($"UPDATE data.organization SET plugintracelogsetting = {value}");

    private async Task<int> CountAsync(string entity) => Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.{entity}"));

    /// <summary>The <c>pb_pipelog</c> rows written by ContextRecorderPlugin: name <c>Message|Stage|Mode|Depth</c> → data lines.</summary>
    private async Task<Dictionary<string, string[]>> LogsAsync()
    {
        var logs = new Dictionary<string, string[]>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{PipeLog}");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) logs[rd.GetString(0)] = rd.IsDBNull(1) ? [] : rd.GetString(1).Split('\n');
        return logs;
    }

    private sealed record TraceRow(string TypeName, string? MessageBlock, string? ExceptionDetails, string? MessageName,
        string? PrimaryEntity, int? Depth, int? Mode);

    private async Task<List<TraceRow>> TracesAsync(Guid stepId)
    {
        var rows = new List<TraceRow>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT typename, messageblock, exceptiondetails, messagename, primaryentity, depth, mode FROM data.plugintracelog WHERE pluginstepid = @id");
        cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = stepId;
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add(new TraceRow(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetInt32(5), rd.IsDBNull(6) ? null : rd.GetInt32(6)));
        return rows;
    }

    private Task<HttpResponseMessage> PostPipeAsync(string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + TestEntities.PipeSet)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> ErrorAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!["error"]!.AsObject();
    }

    private Task<Guid> CreatePipeAsync(string name = "p", int? count = null)
    {
        var e = new Entity(Pipe) { ["pb_name"] = name };
        if (count is not null) e["pb_count"] = count.Value;
        return _svc.CreateAsync(e);
    }

    private async Task<string?> DataOfAsync(Guid id) =>
        (await _svc.RetrieveAsync(Pipe, id, new ColumnSet("pb_data"))).GetAttributeValue<string>("pb_data");

    // ---- tests ----

    [Fact]
    public async Task PreOperation_modifies_target_before_insert()
    {
        await StepAsync("SetAttributePlugin", "Create", 20, "pb_data=from-plugin");

        var id = await CreatePipeAsync();

        (await DataOfAsync(id)).Should().Be("from-plugin");
    }

    [Fact]
    public async Task PreValidation_runs_outside_transaction()
    {
        await StepAsync("ContextRecorderPlugin", "Create", 10, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Create", 40, PipeLog);

        await CreatePipeAsync();

        var logs = await LogsAsync();
        logs["Create|10|0|1"].Should().Contain("intx=False");
        logs["Create|40|0|1"].Should().Contain("intx=True");
    }

    [Fact]
    public async Task PostOperation_sees_output_id()
    {
        await StepAsync("ContextRecorderPlugin", "Create", 10, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Create", 40, PipeLog);

        await CreatePipeAsync();

        var logs = await LogsAsync();
        logs["Create|10|0|1"].Should().Contain("output=");
        logs["Create|40|0|1"].Should().Contain("output=id");
        logs["Create|40|0|1"].Should().Contain("target=pb_name");
    }

    [Fact]
    public async Task Invalid_plugin_exception_rolls_back_and_returns_400()
    {
        await StepAsync("ThrowPlugin", "Create", 40, "invalid:Stop!");

        var act = () => CreatePipeAsync();

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
        fault.Message.Should().Be("Stop!");
        (await CountAsync(Pipe)).Should().Be(0);

        var error = await ErrorAsync(await PostPipeAsync("""{"pb_name":"web"}"""), HttpStatusCode.BadRequest);
        error["code"]!.GetValue<string>().Should().Be("0x80040265");
        error["message"]!.GetValue<string>().Should().Be("Stop!");
        (await CountAsync(Pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Unexpected_exception_returns_500_and_writes_trace_even_when_setting_is_off()
    {
        await SetTraceSettingAsync(0);
        var step = await StepAsync("ThrowPlugin", "Create", 20, "crash:boom");

        var act = () => CreatePipeAsync();

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().Be(UnexpectedPrefix + "Xrmd.TestPlugins.ThrowPlugin: System.InvalidOperationException: boom");
        (await CountAsync(Pipe)).Should().Be(0);

        var trace = (await TracesAsync(step)).Should().ContainSingle().Which;
        trace.TypeName.Should().Be("Xrmd.TestPlugins.ThrowPlugin");
        trace.ExceptionDetails.Should().Contain("boom");
        trace.MessageBlock.Should().Contain("ThrowPlugin start");

        var error = await ErrorAsync(await PostPipeAsync("""{"pb_name":"web"}"""), HttpStatusCode.InternalServerError);
        error["code"]!.GetValue<string>().Should().Be("0x80040224");
        error["message"]!.GetValue<string>().Should().Be(fault.Message);
        (await TracesAsync(step)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Trace_setting_all_logs_successful_plugins()
    {
        await SetTraceSettingAsync(2);
        var step = await StepAsync("ThrowPlugin", "Create", 20, "ok");

        await CreatePipeAsync();

        var trace = (await TracesAsync(step)).Should().ContainSingle().Which;
        trace.TypeName.Should().Be("Xrmd.TestPlugins.ThrowPlugin");
        trace.MessageBlock.Should().Be("ThrowPlugin start");
        trace.ExceptionDetails.Should().BeNullOrEmpty();
        trace.MessageName.Should().Be("Create");
        trace.PrimaryEntity.Should().Be(Pipe);
        trace.Depth.Should().Be(1);
        trace.Mode.Should().Be(0);
    }

    [Fact]
    public async Task Trace_setting_exception_logs_only_failures()
    {
        await SetTraceSettingAsync(1);
        var ok = await StepAsync("ThrowPlugin", "Create", 20, "ok");
        var failing = await StepAsync("ThrowPlugin", "Create", 40, "invalid:no");

        var act = () => CreatePipeAsync();

        await act.Should().ThrowAsync<XrmdFaultException>().Where(f => f.HttpStatus == 400 && f.Message == "no");
        (await TracesAsync(ok)).Should().BeEmpty();
        var trace = (await TracesAsync(failing)).Should().ContainSingle().Which;
        trace.MessageBlock.Should().Be("ThrowPlugin start");
        trace.ExceptionDetails.Should().Contain("no");
    }

    [Fact]
    public async Task Images_are_projected_per_step()
    {
        var id = await CreatePipeAsync("before", 1);
        var step = await StepAsync("ContextRecorderPlugin", "Update", 40, PipeLog);
        await PluginTestHelper.RegisterImageAsync(_svc, step, 0, "pre", "pb_name");
        await PluginTestHelper.RegisterImageAsync(_svc, step, 1, "post");

        await _svc.UpdateAsync(new Entity(Pipe, id) { ["pb_count"] = 5 });

        var log = (await LogsAsync())["Update|40|0|1"];
        log.Should().Contain("pre=pre:pb_name");
        var post = log.Single(l => l.StartsWith("post=", StringComparison.Ordinal));
        post.Should().StartWith("post=post:");
        post.Split(':')[1].Split(',').Should().Contain(["pb_count", "pb_name", "pb_pipeid"]);
    }

    [Fact]
    public async Task Filtering_attributes_skip_step()
    {
        var id = await CreatePipeAsync("f", 1);
        await StepAsync("SetAttributePlugin", "Update", 20, "pb_data=fired", filteringAttributes: "pb_count");

        await _svc.UpdateAsync(new Entity(Pipe, id) { ["pb_name"] = "renamed" });
        (await DataOfAsync(id)).Should().BeNull();

        await _svc.UpdateAsync(new Entity(Pipe, id) { ["pb_count"] = 2 });
        (await DataOfAsync(id)).Should().Be("fired");
    }

    [Fact]
    public async Task Rank_orders_steps_and_shared_variables_flow_from_pre_to_post()
    {
        // Registered in reverse order: the rank, not the registration order, decides.
        await StepAsync("SetAttributePlugin", "Create", 20, "pb_data=second", rank: 2);
        await StepAsync("SetAttributePlugin", "Create", 20, "pb_data=first", rank: 1);
        await StepAsync("ContextRecorderPlugin", "Create", 20, PipeLog, rank: 3);
        await StepAsync("ContextRecorderPlugin", "Create", 40, PipeLog);

        var id = await CreatePipeAsync();

        (await DataOfAsync(id)).Should().Be("second");
        var logs = await LogsAsync();
        logs["Create|20|0|1"].Should().Contain("shared=");
        logs["Create|40|0|1"].Should().Contain("shared=recorder");
    }

    [Fact]
    public async Task Retrieve_postoperation_can_change_output()
    {
        var id = await CreatePipeAsync();
        await StepAsync("ContextRecorderPlugin", "Retrieve", 40, PipeLog);

        var entity = await _svc.RetrieveAsync(Pipe, id, new ColumnSet("pb_name"));

        entity.GetAttributeValue<string>("pb_name").Should().Be("p");
        var log = (await LogsAsync())["Retrieve|40|0|1"];
        log.Should().Contain("output=BusinessEntity");
        log.Should().Contain("target=");
    }

    [Fact]
    public async Task RetrieveMultiple_postoperation_sees_business_entity_collection()
    {
        await CreatePipeAsync();
        await StepAsync("ContextRecorderPlugin", "RetrieveMultiple", 40, PipeLog);

        var result = await _svc.RetrieveMultipleAsync(new QueryExpression(Pipe) { ColumnSet = new ColumnSet("pb_name") });

        result.Entities.Should().ContainSingle();
        (await LogsAsync())["RetrieveMultiple|40|0|1"].Should().Contain("output=BusinessEntityCollection");
    }

    [Fact]
    public async Task PostOperation_changes_to_the_output_reach_the_caller()
    {
        var id = await CreatePipeAsync();
        await StepAsync("OutputEditorPlugin", "Retrieve", 40, "pb_data=edited");
        await StepAsync("OutputEditorPlugin", "RetrieveMultiple", 40, "pb_data=edited-many");

        (await _svc.RetrieveAsync(Pipe, id, new ColumnSet("pb_name"))).GetAttributeValue<string>("pb_data").Should().Be("edited");
        var many = await _svc.RetrieveMultipleAsync(new QueryExpression(Pipe) { ColumnSet = new ColumnSet("pb_name") });
        many.Entities.Should().ContainSingle().Which.GetAttributeValue<string>("pb_data").Should().Be("edited-many");

        // The Web API (query model path) returns the edited output too.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/data/v9.2/{TestEntities.PipeSet}({id})?$select=pb_name");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!["pb_data"]!.GetValue<string>().Should().Be("edited");
    }

    [Fact]
    public async Task Broken_step_does_not_affect_other_messages()
    {
        var id = await CreatePipeAsync("ok", 1);
        var corrupt = PluginTestHelper.TestPluginBytes.Take(512).Concat(Enumerable.Repeat((byte)0xAB, 4096)).ToArray();
        // Registration refuses corrupt content: register a valid assembly, then corrupt it behind the server's back.
        var broken = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes, "Xrmd.TestPlugins.Broken");
        var step = await StepAsync("SetAttributePlugin", "Create", 20, "pb_data=x", assemblyId: broken);
        await ExecAsync($"UPDATE data.pluginassembly SET content = '{Convert.ToBase64String(corrupt)}' WHERE pluginassemblyid = '{broken}'");
        fx.App.Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();

        var act = () => CreatePipeAsync("fails");

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().Be(
            "The plug-in type 'Xrmd.TestPlugins.SetAttributePlugin' could not be loaded: The assembly is not a valid plug-in assembly.");
        (await TracesAsync(step)).Should().ContainSingle();

        await _svc.UpdateAsync(new Entity(Pipe, id) { ["pb_count"] = 2 });
        (await _svc.RetrieveAsync(Pipe, id, new ColumnSet("pb_count"))).GetAttributeValue<int?>("pb_count").Should().Be(2);
    }

    [Fact]
    public async Task Type_missing_after_a_content_update_is_a_500_with_its_own_message_and_a_trace()
    {
        var step = await StepAsync("SetAttributePlugin", "Create", 20, "pb_data=v");
        // A content update does not re-check the registered types: the new assembly lacks this one.
        await _svc.UpdateAsync(new Entity("pluginassembly", _assemblyId)
        {
            ["content"] = Convert.ToBase64String(PluginTestHelper.EarlyBoundPluginBytes),
        });

        var act = () => CreatePipeAsync("fails");

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().Be("The plug-in type 'Xrmd.TestPlugins.SetAttributePlugin' could not be loaded: " +
                                  "Type 'Xrmd.TestPlugins.SetAttributePlugin' was not found in the plug-in assembly.");
        fault.Message.Should().NotContain("Xrmd.Core").And.NotContain("Exception");
        var trace = (await TracesAsync(step)).Should().ContainSingle().Which;
        trace.ExceptionDetails.Should().Be(fault.Message);
        (await CountAsync(Pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Missing_dependency_of_a_plugin_is_a_500_with_a_trace()
    {
        await SetTraceSettingAsync(0); // traced whatever the setting
        var step = await StepAsync("MissingDependencyPlugin", "Create", 20);

        var act = () => CreatePipeAsync("fails");

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().StartWith(UnexpectedPrefix + "Xrmd.TestPlugins.MissingDependencyPlugin: System.IO.FileNotFoundException: ")
            .And.Contain("Xrmd.TestPlugins.Dependency");
        var trace = (await TracesAsync(step)).Should().ContainSingle().Which;
        trace.ExceptionDetails.Should().Contain("Xrmd.TestPlugins.Dependency");
        (await CountAsync(Pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Assembly_update_takes_effect_without_restart()
    {
        await StepAsync("SetAttributePlugin", "Create", 20, "pb_data=v");
        (await DataOfAsync(await CreatePipeAsync("one"))).Should().Be("v");
        var loader = fx.App.Services.GetRequiredService<PluginAssemblyLoader>();
        var before = (long)(await ScalarAsync($"SELECT CAST(versionnumber AS bigint) FROM data.pluginassembly WHERE pluginassemblyid = '{_assemblyId}'"))!;
        loader.IsLoaded(_assemblyId, before).Should().BeTrue();

        await _svc.UpdateAsync(new Entity("pluginassembly", _assemblyId)
        {
            ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
            ["version"] = "2.0.0.0",
        });
        var after = (long)(await ScalarAsync($"SELECT CAST(versionnumber AS bigint) FROM data.pluginassembly WHERE pluginassemblyid = '{_assemblyId}'"))!;
        after.Should().NotBe(before);

        (await DataOfAsync(await CreatePipeAsync("two"))).Should().Be("v");
        loader.IsLoaded(_assemblyId, after).Should().BeTrue();
        loader.IsLoaded(_assemblyId, before).Should().BeFalse();
    }

    [Fact]
    public async Task PreValidation_abort_returns_400_before_anything_is_written()
    {
        await StepAsync("ThrowPlugin", "Create", 10, "invalid:Too early");

        var act = () => CreatePipeAsync();

        await act.Should().ThrowAsync<XrmdFaultException>()
            .Where(f => f.HttpStatus == 400 && f.ErrorCode == ErrorCodes.ISVAborted && f.Message == "Too early");
        (await CountAsync(Pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Depth_beyond_8_is_an_infinite_loop_fault()
    {
        var executor = TestServices.NewPipelineExecutor(fx.App.Services);
        var caller = new Caller(Guid.NewGuid(), Guid.NewGuid());

        var act = () => executor.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateRequest { Target = new Entity(Pipe) },
            new InvocationContext(caller, caller.UserId, 9, null, null, Guid.NewGuid()), default);

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(unchecked((int)0x80044182));
        fault.Message.Should().Be("This workflow job was canceled because the workflow that started it included an infinite loop. Correct the workflow logic and try again.");
        (await CountAsync(Pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Pipeline_options_default_to_a_two_minute_plugin_timeout()
    {
        fx.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PipelineOptions>>().Value.PluginTimeout
            .Should().Be(TimeSpan.FromMinutes(2));
        await Task.CompletedTask;
    }
}
