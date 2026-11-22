using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration.Import;

namespace Xrmd.Integration.Tests;

/// <summary>The import ledger of a migration run (migration M4a, task 1): id map, deferred lookups, issues and phase timings.</summary>
[Collection("xrmd")]
public sealed class MigrationLedgerTests(XrmdFixture fx)
{
    private IDbConnectionFactory Factory => fx.App.Services.GetRequiredService<IDbConnectionFactory>();

    private MigrationLedger NewLedger() => new(Factory, Guid.NewGuid());

    [Fact]
    public async Task Idmap_roundtrip_and_overwrite_is_refused()
    {
        var ledger = NewLedger();
        var (source, target) = (Guid.NewGuid(), Guid.NewGuid());

        await ledger.MapAsync(source, target, null, default);
        await ledger.MapAsync(source, target, null, default); // the same pair again (a resume) is fine

        (await ledger.LoadMapAsync(default)).Should().Equal(new Dictionary<Guid, Guid> { [source] = target });
        var other = async () => await ledger.MapAsync(source, Guid.NewGuid(), null, default);
        await other.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{source}*");
    }

    [Fact]
    public async Task Issue_survives_rolled_back_transaction()
    {
        var ledger = NewLedger();
        var record = Guid.NewGuid();
        await using (var conn = await Factory.OpenAsync(default))
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await ledger.IssueAsync(new MigrationIssue(4, MigrationIssue.Error, "0x80040203", "contact", record, "firstname", "Too long."), default);
            await tx.RollbackAsync();
        }

        (await ledger.HasErrorsAsync(default)).Should().BeTrue();
        (await ledger.IssuesAsync(10, default)).Should().ContainSingle().Which.Should()
            .Be(new MigrationIssue(4, MigrationIssue.Error, "0x80040203", "contact", record, "firstname", "Too long."));
    }

    [Fact]
    public async Task Warning_alone_is_no_error()
    {
        var ledger = NewLedger();
        await ledger.IssueAsync(new MigrationIssue(4, MigrationIssue.Warning, "column-missing", "account", null, "x", "Dropped."), default);
        (await ledger.HasErrorsAsync(default)).Should().BeFalse();
    }

    [Fact]
    public async Task Deferred_lookups_enumerate_in_insert_order()
    {
        var ledger = NewLedger();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var modified = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        await using (var conn = await Factory.OpenAsync(default))
        {
            var s = new DataSession(conn, null);
            await ledger.DeferAsync(s, "account", a, "primarycontactid", "contact", b, modified, default);
            await ledger.DeferAsync(s, "contact", b, "parentcustomerid", "account", a, null, default);
        }

        var deferred = new List<DeferredLookup>();
        await foreach (var d in ledger.DeferredAsync(default)) deferred.Add(d);

        deferred.Should().Equal(
            new DeferredLookup("account", a, "primarycontactid", "contact", b, modified),
            new DeferredLookup("contact", b, "parentcustomerid", "account", a, null));
    }

    [Fact]
    public async Task Phase_timings_recorded()
    {
        var ledger = NewLedger();
        await ledger.PhaseStartedAsync(3, "security", default);
        await ledger.PhaseEndedAsync(3, default);

        var phases = await ledger.PhasesAsync(default);
        phases.Should().ContainSingle().Which.Name.Should().Be("security");
        phases[0].EndedOn.Should().NotBeNull();
        phases[0].EndedOn!.Value.Should().BeOnOrAfter(phases[0].StartedOn);
    }
}
