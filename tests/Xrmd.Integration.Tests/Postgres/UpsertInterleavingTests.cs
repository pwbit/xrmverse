using System.Data.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Webhooks;
using Xrmd.Metadata;
using Xrmd.Workflows.Background;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7b, Task 7, ruling Q5: the update-else-insert batches of a running job's wait (<see cref="WorkflowWaits"/>) and of a webhook
/// secret (<see cref="WebhookSecrets"/>) became the key lock of the row, its update, then its insert when the update found no row.
/// Two writers of one missing row: the second waits for the first one's transaction, then updates the row it inserted (no key
/// violation, one row). Fix round 1 adds the registration of a wait against a wake (both ways, one key) and the locked version read
/// of a wait's record against an open writer. Runs on the run's provider (<c>XRMD_TEST_PROVIDER</c>).
/// </summary>
[Collection("xrmd")]
public sealed class UpsertInterleavingTests(XrmdFixture fx)
{
    private static readonly TimeSpan Blocked = TimeSpan.FromSeconds(1);

    /// <summary>A workflow job running (2/20), with no wait row; removed with its wait by <see cref="DropJobAsync"/>.</summary>
    private async Task<Guid> RunningJobAsync(string name)
    {
        var admin = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        Guid job;
        await using (var conn = await Factory.OpenAsync())
            job = await fx.App.Services.GetRequiredService<AsyncOperationQueue>().EnqueueWorkflowAsync(new DataSession(conn, null),
                new WorkflowJobRequest(Guid.NewGuid(), name, "Create", new EntityReference("account", Guid.NewGuid()), admin.UserId,
                    admin.UserId, 1, "{}"), default);
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.asyncoperation SET statecode = 2, statuscode = 20 WHERE asyncoperationid = @job", ("@job", job));
        return job;
    }

    private async Task DropJobAsync(Guid job)
    {
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.workflow_wait WHERE asyncoperationid = @job", ("@job", job));
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.asyncoperation WHERE asyncoperationid = @job", ("@job", job));
    }

    private IDbConnectionFactory Factory => fx.App.Services.GetRequiredService<IDbConnectionFactory>();

    private WorkflowWaits Waits => fx.App.Services.GetRequiredService<WorkflowWaits>();

    /// <summary>The job's wait rows: entity, record and whether <c>wake_at</c> is the far future (a wait not woken).</summary>
    private async Task<List<(string Entity, Guid Record, bool Asleep)>> WaitRowsAsync(Guid job) =>
        (await TestSql.RowsAsync(fx.ConnectionString, "SELECT entity, objectid, wake_at FROM meta.workflow_wait WHERE asyncoperationid = @job",
            ("@job", job)))
        .Select(r => ((string)r[0]!, (Guid)r[1]!, (DateTime)r[2]! > DateTime.UtcNow.AddYears(1))).ToList();

    /// <summary>
    /// Runs <paramref name="first"/> in a transaction left open, then <paramref name="second"/> on another connection: the second
    /// must still be waiting after <see cref="Blocked"/>, and finish once the first commits.
    /// </summary>
    private async Task InterleaveAsync(Func<DataSession, Task> first, Func<DbConnection, Task> second, string because)
    {
        await using var conn = await Factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await first(new DataSession(conn, tx));

        var waiting = Task.Run(async () =>
        {
            await using var other = await Factory.OpenAsync();
            await second(other);
        });
        await Task.Delay(Blocked);
        waiting.IsCompleted.Should().BeFalse(because);

        await tx.CommitAsync();
        await waiting;
    }

    [Fact]
    public async Task Second_wake_of_a_running_job_waits_for_the_first_and_keeps_one_wait()
    {
        var job = await RunningJobAsync("Wake twice");
        try
        {
            var record = Guid.NewGuid();
            await InterleaveAsync(s => Waits.WakeRunningAsync(s, [job], "account", record, default), async conn =>
            {
                await using var tx = await conn.BeginTransactionAsync();
                await Waits.WakeRunningAsync(new DataSession(conn, tx), [job], "account", record, default);
                await tx.CommitAsync();
            }, "the second wake waits for the key the first one holds");

            (await WaitRowsAsync(job)).Should().ContainSingle("the second wake updated the row the first one inserted");
        }
        finally
        {
            await DropJobAsync(job);
        }
    }

    [Fact]
    public async Task Registration_waits_for_an_open_wake_then_replaces_its_row()
    {
        // Fix round 1: a run registering its wait (on its autocommit session) while a delete's wake of the same job is uncommitted.
        var job = await RunningJobAsync("Register after wake");
        try
        {
            var deleted = Guid.NewGuid();
            var waitedOn = new EntityReference("contact", Guid.NewGuid());
            await InterleaveAsync(s => Waits.WakeRunningAsync(s, [job], "account", deleted, default),
                conn => Waits.RegisterAsync(new DataSession(conn, null), job, waitedOn, null, default),
                "the registration waits for the key the wake holds");

            (await WaitRowsAsync(job)).Should().Equal([("contact", waitedOn.Id, true)], "the registration replaced the woken row");
        }
        finally
        {
            await DropJobAsync(job);
        }
    }

    [Fact]
    public async Task Wake_waits_for_an_open_registration_then_wakes_it()
    {
        // Fix round 1: a delete waking a running job while the job's registration is uncommitted. The wake must not miss the row
        // and fail the user's delete on the key.
        var job = await RunningJobAsync("Wake after register");
        try
        {
            var waitedOn = new EntityReference("contact", Guid.NewGuid());
            await InterleaveAsync(s => Waits.RegisterAsync(s, job, waitedOn, null, default), async conn =>
            {
                await using var tx = await conn.BeginTransactionAsync();
                await Waits.WakeRunningAsync(new DataSession(conn, tx), [job], "account", Guid.NewGuid(), default);
                await tx.CommitAsync();
            }, "the wake waits for the key the registration holds");

            (await WaitRowsAsync(job)).Should().Equal([("contact", waitedOn.Id, false)], "the wake set wake_at of the registered row");
        }
        finally
        {
            await DropJobAsync(job);
        }
    }

    [Fact]
    public async Task Registration_waits_for_an_open_delete_of_its_record_without_deadlock()
    {
        // Final review I3: the delete of a record a job waits on removes the wait (RemoveForRecordAsync), then wakes the job if it
        // runs (WakeRunningAsync), in one transaction. The job's run registers a new wait meanwhile. On PostgreSQL the delete takes
        // the job's key before its row, as the registration does: the registration waits for the delete, which ends without a
        // deadlock (row then key there, against key then row, was one). On SQL Server the registration's DELETE waits for the row.
        var job = await RunningJobAsync("Register during delete");
        try
        {
            var deleted = new EntityReference("account", Guid.NewGuid());
            var next = new EntityReference("contact", Guid.NewGuid());
            await using (var conn = await Factory.OpenAsync())
                await Waits.RegisterAsync(new DataSession(conn, null), job, deleted, null, default);

            await using var delete = await Factory.OpenAsync();
            await using var tx = await delete.BeginTransactionAsync();
            var s = new DataSession(delete, tx);
            (await Waits.RemoveForRecordAsync(s, "account", deleted.Id, default)).Should().Equal([job]);

            var registration = Task.Run(async () =>
            {
                await using var other = await Factory.OpenAsync();
                await Waits.RegisterAsync(new DataSession(other, null), job, next, null, default);
            });
            await Task.Delay(Blocked);
            registration.IsCompleted.Should().BeFalse("the registration waits for the delete that removed the job's wait");

            await Waits.WakeRunningAsync(s, [job], "account", deleted.Id, default);
            await tx.CommitAsync();
            await registration;

            (await WaitRowsAsync(job)).Should().Equal([("contact", next.Id, true)], "the registration replaced the woken row");
        }
        finally
        {
            await DropJobAsync(job);
        }
    }

    [Fact]
    public async Task Delete_of_the_record_waits_for_an_open_registration_then_finds_no_wait_on_it()
    {
        // Final review I3, the other way: a registration that moves the job's wait off the record is uncommitted; the record's
        // delete waits for it (the job's key on PostgreSQL, the row on SQL Server), then has no wait on the record to remove.
        var job = await RunningJobAsync("Delete during register");
        try
        {
            var deleted = new EntityReference("account", Guid.NewGuid());
            var next = new EntityReference("contact", Guid.NewGuid());
            await using (var conn = await Factory.OpenAsync())
                await Waits.RegisterAsync(new DataSession(conn, null), job, deleted, null, default);

            IReadOnlyList<Guid>? removed = null;
            await InterleaveAsync(s => Waits.RegisterAsync(s, job, next, null, default), async conn =>
            {
                await using var tx = await conn.BeginTransactionAsync();
                removed = await Waits.RemoveForRecordAsync(new DataSession(conn, tx), "account", deleted.Id, default);
                await tx.CommitAsync();
            }, "the delete waits for the registration of the job");

            removed.Should().BeEmpty("the registration moved the wait off the deleted record");
            (await WaitRowsAsync(job)).Should().Equal([("contact", next.Id, true)]);
        }
        finally
        {
            await DropJobAsync(job);
        }
    }

    [Fact]
    public async Task Locked_version_read_waits_for_an_open_writer_and_sees_its_version()
    {
        // Fix round 1: the version read of a wait's record (READCOMMITTEDLOCK on SQL Server, FOR SHARE on PostgreSQL) waits for a
        // writer of the record in flight, then reads the version it committed, not the one before.
        var account = await TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId)
            .CreateAsync(new Entity("account") { ["name"] = "Version read" });
        var record = new EntityReference("account", account);
        var m = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity("account");
        try
        {
            long? before;
            await using (var conn = await Factory.OpenAsync())
                before = await WorkflowWaits.VersionOfAsync(new DataSession(conn, null), record, m, default);
            before.Should().NotBeNull();

            long? read = null;
            await InterleaveAsync(async s =>
            {
                await using var update = s.Command("UPDATE data.account SET name = 'Version read, written' WHERE accountid = @id");
                update.AddParameter("@id", System.Data.DbType.Guid, account);
                await update.ExecuteNonQueryAsync();
            }, async conn => read = await WorkflowWaits.VersionOfAsync(new DataSession(conn, null), record, m, default),
                "the locked read waits for the writer of the record");

            read.Should().BeGreaterThan(before!.Value, "the read sees the version the writer committed");
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.account WHERE accountid = @id", ("@id", account));
        }
    }

    [Fact]
    public async Task Second_secret_of_a_new_endpoint_waits_for_the_first_and_wins()
    {
        var factory = Factory;
        var secrets = fx.App.Services.GetRequiredService<WebhookSecrets>();
        var id = Guid.NewGuid();
        Entity Target(string value) => new(ServiceEndpointSchema.Table, id) { [ServiceEndpointSchema.AuthValue] = value };
        try
        {
            await using var first = await factory.OpenAsync();
            await using var firstTx = await first.BeginTransactionAsync();
            (await secrets.TakeAsync(Target("first"), new DataSession(first, firstTx), default)).Should().Be(id);

            var second = Task.Run(async () =>
            {
                await using var conn = await factory.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();
                await secrets.TakeAsync(Target("second"), new DataSession(conn, tx), default);
                await tx.CommitAsync();
            });
            await Task.Delay(Blocked);
            second.IsCompleted.Should().BeFalse("the second write waits for the key the first one holds");

            await firstTx.CommitAsync();
            await second;

            (await TestSql.RowsAsync(fx.ConnectionString, "SELECT authvalue FROM meta.serviceendpoint_secret WHERE serviceendpointid = @id",
                ("@id", id))).Select(r => r[0]).Should().Equal(["second"], "the second write updated the row the first one inserted");
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.serviceendpoint_secret WHERE serviceendpointid = @id", ("@id", id));
        }
    }
}
