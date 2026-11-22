using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Pipeline.Async;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7b, Task 7: the claim of a background workflow job (<see cref="AsyncOperationWorker"/>: <c>UPDLOCK, READPAST</c> on SQL
/// Server, <c>FOR UPDATE SKIP LOCKED</c> on PostgreSQL) gives each waiting job to one worker only. Runs on the run's provider
/// (<c>XRMD_TEST_PROVIDER</c>). The fixture host's own worker is off (<c>Pipeline:AsyncWorkerEnabled=false</c>), so only the two
/// workers of the test claim.
/// </summary>
[Collection("xrmd")]
public sealed class WorkflowJobClaimTests(XrmdFixture fx) : IAsyncLifetime
{
    private const int Jobs = 50;

    public Task InitializeAsync() => ClearAsync();

    public Task DisposeAsync() => ClearAsync();

    private async Task ClearAsync()
    {
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.workflow_wait");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.asyncoperation");
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_job()
    {
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var queue = fx.App.Services.GetRequiredService<AsyncOperationQueue>();
        var admin = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var now = DateTime.UtcNow;

        // 50 waiting workflow jobs (Suspended / Waiting, 1/10): half woken by a write of their record (a wait row whose wake_at
        // has come), half with no wait row and no postponeuntil.
        var queued = new List<Guid>();
        await using (var conn = await factory.OpenAsync())
        {
            var s = new DataSession(conn, null);
            for (var i = 0; i < Jobs; i++)
                queued.Add(await queue.EnqueueWorkflowAsync(s, new WorkflowJobRequest(Guid.NewGuid(), $"Claim {i}", "Create",
                    new EntityReference("account", Guid.NewGuid()), admin.UserId, admin.UserId, 1, "{}"), default));
        }
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.asyncoperation SET statecode = 1, statuscode = 10, postponeuntil = NULL WHERE operationtype = 10");
        for (var i = 0; i < Jobs; i += 2)
            await TestSql.ExecAsync(fx.ConnectionString,
                "INSERT INTO meta.workflow_wait (asyncoperationid, entity, objectid, wake_at) VALUES (@job, 'account', @record, @wake)",
                ("@job", queued[i]), ("@record", Guid.NewGuid()), ("@wake", now.AddMinutes(-1)));

        // Two workers claim in parallel loops until neither finds a ready job.
        using var start = new ManualResetEventSlim();
        async Task<List<Guid>> WorkerAsync()
        {
            await Task.Yield();
            start.Wait();
            var claimed = new List<Guid>();
            while (await AsyncOperationWorker.ClaimAsync(factory, now, null, default) is { } job) claimed.Add(job.Id);
            return claimed;
        }
        var workers = new[] { Task.Run(WorkerAsync), Task.Run(WorkerAsync) };
        start.Set();
        var claims = await Task.WhenAll(workers);

        var all = claims.SelectMany(c => c).ToList();
        all.Should().OnlyHaveUniqueItems("a job is claimed by one worker only");
        all.Should().HaveCount(Jobs).And.BeEquivalentTo(queued);
        (await TestSql.CountAsync(fx.ConnectionString,
            "SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 10 AND statecode = 2 AND statuscode = 20")).Should().Be(Jobs);
    }
}
