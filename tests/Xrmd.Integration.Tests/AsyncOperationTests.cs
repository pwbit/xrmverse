using Xrmd.Abstractions.Storage;
using System.Data.Common;
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

namespace Xrmd.Integration.Tests;

/// <summary>
/// Asynchronous (mode 1) post-operation steps: queued in <c>asyncoperation</c> in the operation's transaction and run by
/// <see cref="AsyncOperationWorker"/>. The fixture host's own worker is disabled (<c>Pipeline:AsyncWorkerEnabled=false</c>),
/// so these tests run jobs deterministically with <see cref="AsyncOperationWorker.RunOnceAsync"/> on a worker of their own;
/// only <see cref="Background_worker_processes_queue"/> starts a worker loop (and stops it before returning).
/// </summary>
[Collection("xrmd")]
public sealed class AsyncOperationTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string Plugins = "Xrmd.TestPlugins.";
    private const int Async = 1;

    private OrganizationService _svc = null!;
    private Guid _assemblyId;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.plugintracelog; DELETE FROM data.asyncoperation;");
        await ExecAsync("UPDATE data.organization SET plugintracelogsetting = 1");
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation; UPDATE data.organization SET plugintracelogsetting = 1");
    }

    // ---- helpers ----

    private AsyncOperationWorker NewWorker(int maxRetries = 3) =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions
            {
                AsyncPollInterval = TimeSpan.FromMilliseconds(100),
                AsyncRetryDelay = TimeSpan.FromMilliseconds(100),
                AsyncMaxRetries = maxRetries,
            }));

    private async Task<Guid> StepAsync(string plugin, int stage, int mode, string? config)
    {
        var typeId = await PluginTestHelper.RegisterTypeAsync(_svc, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(_svc, typeId, "Create", Pipe, stage, mode: mode, config: config);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string entity)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{entity}");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<List<string>> LogNamesAsync()
    {
        var names = new List<string>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name FROM data.{PipeLog}");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    private sealed record Job(Guid Id, int StateCode, int StatusCode, Guid? OwningExtensionId, string? MessageName,
        string? PrimaryEntityType, int? RetryCount, string? Message, int? ErrorCode, DateTime? PostponeUntil, Guid? OwnerId,
        int? OperationType, int? Depth, string? Data, DateTime? StartedOn, DateTime? CompletedOn);

    private async Task<Job> SingleJobAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT asyncoperationid, statecode, statuscode, owningextensionid, messagename, primaryentitytype, retrycount, message, " +
            "errorcode, postponeuntil, ownerid, operationtype, depth, data, startedon, completedon FROM data.asyncoperation");
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue("one asyncoperation is expected");
        T? N<T>(int i) => rd.IsDBNull(i) ? default : rd.GetFieldValue<T>(i);
        var job = new Job(rd.GetGuid(0), rd.GetInt32(1), rd.GetInt32(2), N<Guid?>(3), N<string>(4), N<string>(5), N<int?>(6),
            N<string>(7), N<int?>(8), N<DateTime?>(9), N<Guid?>(10), N<int?>(11), N<int?>(12), N<string>(13), N<DateTime?>(14),
            N<DateTime?>(15));
        (await rd.ReadAsync()).Should().BeFalse("only one asyncoperation is expected");
        return job;
    }

    private Task<Guid> CreatePipeAsync() => _svc.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });

    /// <summary>Runs jobs until one is taken (a retried job waits for its postponeuntil); fails after 10 s.</summary>
    private static async Task RunNextAsync(AsyncOperationWorker worker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await worker.RunOnceAsync(default))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "a job should become ready");
            await Task.Delay(20);
        }
    }

    // ---- tests ----

    [Fact]
    public async Task Async_step_enqueues_in_transaction_and_runs_after_commit()
    {
        var stepId = await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        var id = await CreatePipeAsync();

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((0, 0));
        job.OwningExtensionId.Should().Be(stepId);
        job.MessageName.Should().Be("Create");
        job.PrimaryEntityType.Should().Be(Pipe);
        job.OperationType.Should().Be(1);
        job.Depth.Should().Be(1);
        job.OwnerId.Should().NotBeNull().And.NotBe(Guid.Empty);
        job.Data.Should().Contain("RemoteExecutionContext").And.Contain(id.ToString());
        (await CountAsync(PipeLog)).Should().Be(0);

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var names = await LogNamesAsync();
        names.Should().Equal("Create|40|1|1"); // Message|Stage|Mode|Depth
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{PipeLog}");
            var data = (string)(await cmd.ExecuteScalarAsync())!;
            data.Should().Contain("intx=False").And.Contain("output=id").And.Contain("target=pb_name");
        }

        job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 30));
        job.StartedOn.Should().NotBeNull();
        job.CompletedOn.Should().NotBeNull();
        (await NewWorker().RunOnceAsync(default)).Should().BeFalse();
    }

    [Fact]
    public async Task Async_autodelete_removes_succeeded_job()
    {
        var stepId = await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        await _svc.UpdateAsync(new Entity("sdkmessageprocessingstep", stepId) { ["asyncautodelete"] = true });
        await CreatePipeAsync();
        (await CountAsync("asyncoperation")).Should().Be(1);

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        (await LogNamesAsync()).Should().Equal("Create|40|1|1");
        (await CountAsync("asyncoperation")).Should().Be(0, "asyncautodelete deletes the job once it succeeded");
    }

    [Fact]
    public async Task Rolled_back_operation_does_not_enqueue()
    {
        await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        await StepAsync("ThrowPlugin", 40, 0, "invalid:no");

        var act = () => CreatePipeAsync();

        await act.Should().ThrowAsync<XrmdFaultException>();
        (await CountAsync("asyncoperation")).Should().Be(0);
        (await CountAsync(Pipe)).Should().Be(0);
    }

    [Fact]
    public async Task Invalid_plugin_exception_fails_without_retry()
    {
        var stepId = await StepAsync("ThrowPlugin", 40, Async, "invalid:async refused");
        await CreatePipeAsync();

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.RetryCount.Should().Be(0);
        job.Message.Should().Be("async refused");
        job.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
        job.PostponeUntil.Should().BeNull();
        (await NewWorker().RunOnceAsync(default)).Should().BeFalse();

        // Trace setting 1 (Exception): the failed asynchronous execution is traced, with mode 1.
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT mode FROM data.plugintracelog WHERE pluginstepid = @id");
        cmd.Parameters.AddWithValue("@id", stepId);
        (await cmd.ExecuteScalarAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Unexpected_exception_retries_then_fails()
    {
        await StepAsync("ThrowPlugin", 40, Async, "crash:x");
        await CreatePipeAsync();
        var worker = NewWorker(maxRetries: 2);

        var before = DateTime.UtcNow;
        (await worker.RunOnceAsync(default)).Should().BeTrue();
        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((1, 10));
        job.RetryCount.Should().Be(1);
        job.PostponeUntil.Should().NotBeNull();
        job.PostponeUntil!.Value.Should().BeOnOrAfter(before.AddMilliseconds(100));
        job.Message.Should().Contain("Unexpected exception from plug-in (Execute)").And.Contain("System.InvalidOperationException: x");
        // Not ready before postponeuntil.
        if (DateTime.UtcNow < job.PostponeUntil.Value.AddMilliseconds(-20)) (await worker.RunOnceAsync(default)).Should().BeFalse();

        await RunNextAsync(worker);
        job = await SingleJobAsync();
        (job.StateCode, job.StatusCode, job.RetryCount).Should().Be((1, 10, 2));

        await RunNextAsync(worker);
        job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.RetryCount.Should().Be(2);
        job.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        job.Message.Should().Contain("System.InvalidOperationException: x");
        (await worker.RunOnceAsync(default)).Should().BeFalse();
    }

    /// <summary>The host's services, but an <see cref="IDbConnectionFactory"/> whose opens number <c>failing</c> throw.</summary>
    private sealed class FlakyServices(IServiceProvider inner, params int[] failing) : IServiceProvider, IDbConnectionFactory
    {
        private int _opens;

        public Xrmd.Abstractions.Storage.ISqlDialect Dialect => inner.GetRequiredService<IDbConnectionFactory>().Dialect;

        public Type ConnectionType => inner.GetRequiredService<IDbConnectionFactory>().ConnectionType;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IDbConnectionFactory) ? this : inner.GetService(serviceType);

        public Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct = default) =>
            failing.Contains(Interlocked.Increment(ref _opens))
                ? throw new InvalidOperationException("connection refused (test)")
                : inner.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
    }

    [Fact]
    public async Task Outcome_write_is_retried_when_it_fails()
    {
        await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        await CreatePipeAsync();
        // Opens: 1 claim, 2 the job's connection, 3 and 4 the outcome (fail), 5 the outcome (works).
        var worker = new AsyncOperationWorker(new FlakyServices(fx.App.Services, 3, 4),
            fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }));

        (await worker.RunOnceAsync(default)).Should().BeTrue();

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 30));
        (await CountAsync(PipeLog)).Should().Be(1);
    }

    [Theory]
    [InlineData(true, "The plug-in step was deleted.")]
    [InlineData(false, "The plug-in step is disabled.")]
    public async Task Deleted_or_disabled_step_cancels_job(bool delete, string message)
    {
        var stepId = await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        await CreatePipeAsync();
        if (delete) await _svc.DeleteAsync("sdkmessageprocessingstep", stepId);
        else
            await _svc.UpdateAsync(new Entity("sdkmessageprocessingstep", stepId)
            {
                ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2),
            });

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 32));
        job.Message.Should().Be(message);
        (await CountAsync(PipeLog)).Should().Be(0);
    }

    [Fact]
    public async Task Canceled_job_is_not_picked()
    {
        await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        await CreatePipeAsync();
        var job = await SingleJobAsync();

        await _svc.UpdateAsync(new Entity("asyncoperation", job.Id)
        {
            ["statecode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(32),
        });

        (await NewWorker().RunOnceAsync(default)).Should().BeFalse();
        (await SingleJobAsync()).StatusCode.Should().Be(32);
        (await CountAsync(PipeLog)).Should().Be(0);
    }

    [Fact]
    public async Task Interrupted_job_is_recovered_and_runs()
    {
        await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        await CreatePipeAsync();
        await ExecAsync("UPDATE data.asyncoperation SET statecode = 2, statuscode = 20, startedon = SYSUTCDATETIME()");
        var worker = NewWorker();
        (await worker.RunOnceAsync(default)).Should().BeFalse("a Locked job is not taken");

        (await worker.RecoverAsync(default)).Should().Be(1);

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((0, 0));
        job.StartedOn.Should().BeNull();
        (await worker.RunOnceAsync(default)).Should().BeTrue();
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await LogNamesAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Jobs_are_taken_oldest_first()
    {
        await StepAsync("CreateRecordPlugin", 40, Async, PipeLog);
        var first = await CreatePipeAsync();
        var second = await CreatePipeAsync();
        // The second job is made the older one.
        await ExecAsync($"UPDATE data.asyncoperation SET createdon = DATEADD(minute, -10, createdon) WHERE regardingobjectid = '{second}'");
        var worker = NewWorker();

        (await worker.RunOnceAsync(default)).Should().BeTrue();
        (await LogNamesAsync()).Should().Equal($"Create|40|1|{second}");
        (await worker.RunOnceAsync(default)).Should().BeTrue();
        (await LogNamesAsync()).Should().BeEquivalentTo($"Create|40|1|{second}", $"Create|40|1|{first}");
    }

    [Fact]
    public async Task Corrupt_payload_fails_without_retry()
    {
        await StepAsync("ContextRecorderPlugin", 40, Async, PipeLog);
        await CreatePipeAsync();
        await ExecAsync("UPDATE data.asyncoperation SET data = N'<not-a-context'");

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode, job.RetryCount).Should().Be((3, 31, 0));
        job.Message.Should().StartWith("The execution context of the job cannot be read");
        (await CountAsync(PipeLog)).Should().Be(0);
    }

    [Fact]
    public async Task Background_worker_processes_queue()
    {
        await StepAsync("CreateRecordPlugin", 40, Async, PipeLog);
        var worker = NewWorker();
        await worker.StartAsync(default);
        try
        {
            var id = await CreatePipeAsync();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            List<string> names;
            while ((names = await LogNamesAsync()).Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            names.Should().Equal($"Create|40|1|{id}"); // Message|Stage|Depth|PrimaryEntityId

            while ((await SingleJobAsync()).StatusCode != 30 && DateTime.UtcNow < deadline) await Task.Delay(50);
            (await SingleJobAsync()).StatusCode.Should().Be(30);
        }
        finally
        {
            await worker.StopAsync(default);
            worker.Dispose();
        }
    }
}
