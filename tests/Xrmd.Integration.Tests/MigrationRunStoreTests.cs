using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The ledger of migration runs (migration M3, task 2): one open run per organization, the state stamps, chunks recorded in the
/// caller's transaction, and the async job type 99010 in the seed.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationRunStoreTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly List<Guid> _runs = [];

    private MigrationRunStore Store => new(fx.App.Services.GetRequiredService<IDbConnectionFactory>());

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Finalizes every run the test opened, so the shared database ends with no open run.</summary>
    public async Task DisposeAsync()
    {
        foreach (var run in _runs)
            if (await Store.GetRunAsync(run, default) is { State: not MigrationRunStates.Finalized })
                await Store.SetStateAsync(run, MigrationRunStates.Finalized, null, default);
    }

    private async Task<Guid> NewRunAsync()
    {
        var run = new MigrationRunRow(Guid.NewGuid(), null, "/data/import/test.xrmpkg", MigrationRunStates.Queued, 0, "{}", null, null,
            (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId, DateTime.UtcNow, null, null, null);
        await Store.CreateRunAsync(run, default);
        _runs.Add(run.RunId);
        return run.RunId;
    }

    [Fact]
    public async Task Second_open_run_is_refused()
    {
        var first = await NewRunAsync();

        var second = async () => await NewRunAsync();
        await second.Should().ThrowAsync<MigrationRunConflictException>();

        (await Store.GetOpenRunAsync(default))!.RunId.Should().Be(first);
        await Store.SetStateAsync(first, MigrationRunStates.Finalized, null, default);
        (await Store.GetOpenRunAsync(default)).Should().BeNull();
        await NewRunAsync();
    }

    [Fact]
    public async Task SetState_from_other_state_changes_nothing()
    {
        var id = await NewRunAsync();
        (await Store.SetStateAsync(id, MigrationRunStates.Running, null, default, MigrationRunStates.Queued)).Should().BeTrue();
        (await Store.SetStateAsync(id, MigrationRunStates.Finalized, null, default, MigrationRunStates.Failed, MigrationRunStates.Cancelled))
            .Should().BeFalse("a running run is not finalized");

        (await Store.SetStateAsync(id, MigrationRunStates.Cancelled, "late", default, MigrationRunStates.Queued)).Should().BeFalse();

        var run = (await Store.GetRunAsync(id, default))!;
        (run.State, run.Message).Should().Be((MigrationRunStates.Running, null));
        await Store.SetStateAsync(id, MigrationRunStates.Finalized, null, default);
    }

    /// <summary>
    /// Plan 2.7b, Task 10 (interleaving, both engines): a start whose run row is not committed yet holds the open-run key, so a
    /// second start waits for it and is refused once it commits: the unique open-run index is the run lock on either engine.
    /// </summary>
    [Fact]
    public async Task Start_waits_for_an_uncommitted_start_and_is_refused()
    {
        var by = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
        var held = Guid.NewGuid();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        await using (var insert = TestSql.Command(conn, tx,
            "INSERT INTO meta.migration_run (runid, state, phase, options, createdby, createdon) VALUES (@id, 'queued', 0, '{}', @by, SYSUTCDATETIME())",
            ("@id", held), ("@by", by)))
            await insert.ExecuteNonQueryAsync();
        _runs.Add(held);

        var second = NewRunAsync();
        await Task.Delay(1500);
        second.IsCompleted.Should().BeFalse("the second start waits on the open-run key the first one holds");

        await tx.CommitAsync();
        await second.Awaiting(t => t).Should().ThrowAsync<MigrationRunConflictException>().WithMessage($"*{held}*");
        (await Store.GetOpenRunAsync(default))!.RunId.Should().Be(held);
    }

    /// <summary>
    /// Plan 2.7b, Task 10 (interleaving, both engines): the compare and set of a run's state reads the state the other writer
    /// committed: a job's late "completed" waits for an administrator's uncommitted cancel and then changes nothing.
    /// </summary>
    [Fact]
    public async Task Compare_and_set_waits_for_a_concurrent_change_and_loses()
    {
        var id = await NewRunAsync();
        (await Store.SetStateAsync(id, MigrationRunStates.Running, null, default, MigrationRunStates.Queued)).Should().BeTrue();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cancel = TestSql.Command(conn, tx,
            "UPDATE meta.migration_run SET state = 'cancelled' WHERE runid = @id AND state = 'running'", ("@id", id)))
            (await cancel.ExecuteNonQueryAsync()).Should().Be(1);

        var late = Store.SetStateAsync(id, MigrationRunStates.Completed, null, default, MigrationRunStates.Running);
        await Task.Delay(1500);
        late.IsCompleted.Should().BeFalse("the job's write waits for the administrator's");

        await tx.CommitAsync();
        (await late).Should().BeFalse("the run is no longer running when the job's write gets the row");
        (await Store.GetRunAsync(id, default))!.State.Should().Be(MigrationRunStates.Cancelled);
    }

    [Fact]
    public void Cancellations_belong_to_their_registration()
    {
        var cancellations = new MigrationCancellations();
        var run = Guid.NewGuid();
        var old = cancellations.Register(run);
        var current = cancellations.Register(run);

        old.Dispose();

        cancellations.Cancel(run).Should().BeTrue("the newer job's registration stays when the older job ends");
        current.Token.IsCancellationRequested.Should().BeTrue();
        current.IsCancelled.Should().BeTrue();
        old.IsCancelled.Should().BeFalse();
        current.Dispose();
        cancellations.Cancel(run).Should().BeFalse();
    }

    [Fact]
    public async Task SetState_stamps_times()
    {
        var id = await NewRunAsync();

        await Store.SetStateAsync(id, MigrationRunStates.Running, null, default);
        var running = (await Store.GetRunAsync(id, default))!;
        running.StartedOn.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        running.EndedOn.Should().BeNull();

        await Store.SetStateAsync(id, MigrationRunStates.Failed, "boom", default);
        var failed = (await Store.GetRunAsync(id, default))!;
        (failed.State, failed.Message).Should().Be((MigrationRunStates.Failed, "boom"));
        failed.EndedOn.Should().NotBeNull();
        failed.StartedOn.Should().Be(running.StartedOn, "a later state keeps the start");

        await Store.SetPhaseAsync(id, 3, default);
        (await Store.GetRunAsync(id, default))!.Phase.Should().Be(3);

        await Store.SetStateAsync(id, MigrationRunStates.Finalized, null, default);
        (await Store.GetRunAsync(id, default))!.FinalizedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task Chunk_marked_in_rolled_back_transaction_is_not_done()
    {
        var id = await NewRunAsync();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);

        await using (var tx = await conn.BeginTransactionAsync())
        {
            await Store.MarkChunkAsync(id, 4, "data/account/000001", 10, 0, new DataSession(conn, tx), default);
            await tx.RollbackAsync();
        }
        (await Store.IsChunkDoneAsync(id, "data/account/000001", default)).Should().BeFalse();

        await using (var tx = await conn.BeginTransactionAsync())
        {
            await Store.MarkChunkAsync(id, 4, "data/account/000001", 10, 1, new DataSession(conn, tx), default);
            await tx.CommitAsync();
        }
        (await Store.IsChunkDoneAsync(id, "data/account/000001", default)).Should().BeTrue();
    }

    [Fact]
    public async Task Packages_are_created_and_completed()
    {
        var by = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
        var id = await Store.CreatePackageAsync(by, default);
        (await Store.GetPackageAsync(id, default))!.State.Should().Be(MigrationPackageStates.Uploading);

        await Store.CompletePackageAsync(id, 42, new string('a', 64), default);
        var row = (await Store.GetPackageAsync(id, default))!;
        (row.State, row.Bytes, row.Sha256).Should().Be((MigrationPackageStates.Complete, 42L, new string('a', 64)));

        await Store.DeletePackageAsync(id, default);
        (await Store.GetPackageAsync(id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Seed_has_operationtype_99010()
    {
        var response = (RetrieveAttributeResponse)await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = "asyncoperation", LogicalName = "operationtype" });
        ((PicklistAttributeMetadata)response.AttributeMetadata).OptionSet.Options.Select(o => o.Value).Should().Contain(99010);
    }
}
