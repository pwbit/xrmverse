using System.Data;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7a, Task 6 (Global Constraints: concurrent writes on PostgreSQL behave as on SQL Server). Runs on the run's provider
/// (<c>XRMD_TEST_PROVIDER</c>): the unique index of the alternate key decides the race of parallel creates (one row, Dataverse's
/// duplicate-key fault for the others, never a 500), and parallel updates of one row each get a version of their own.
/// </summary>
[Collection("xrmd")]
public sealed class ConcurrencyParityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const int Parallel = 10;

    public Task InitializeAsync() => ParityTestKit.EnsureKeyTableAsync(fx);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Parallel_creates_with_the_same_alternate_key_give_one_row_and_duplicate_faults()
    {
        var set = ParityTestKit.SetOf(fx);
        var name = "Race " + Guid.NewGuid().ToString("N")[..8];

        var responses = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => Task.Run(async () =>
        {
            // Each create spells the value in its own case: the key ignores case.
            var spelled = i % 2 == 0 ? name.ToUpperInvariant() : name.ToLowerInvariant();
            var res = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "{{spelled}}" }""");
            return (res.StatusCode, Body: await res.Content.ReadAsStringAsync());
        })));

        responses.Should().NotContain(r => (int)r.StatusCode >= 500);
        responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).Should().Be(1, string.Join("\n", responses.Select(r => r.Body)));
        var faults = responses.Where(r => r.StatusCode != HttpStatusCode.NoContent).ToList();
        faults.Should().HaveCount(Parallel - 1);
        foreach (var (status, body) in faults)
        {
            status.Should().Be(HttpStatusCode.PreconditionFailed, body);
            JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040237");
        }

        var query = new QueryExpression(ParityTestKit.KeyTable) { ColumnSet = new ColumnSet(ParityTestKit.KeyName) };
        query.Criteria.AddCondition(ParityTestKit.KeyName, ConditionOperator.Equal, name);
        (await ServiceFor(fx, fx.AdminObjectId).RetrieveMultipleAsync(query)).Entities.Should().ContainSingle();
    }

    [Fact]
    public async Task Parallel_updates_of_one_row_each_get_a_new_increasing_version()
    {
        var svc = ServiceFor(fx, fx.AdminObjectId);
        var id = await svc.CreateAsync(new Entity("account") { ["name"] = "Versions " + Guid.NewGuid().ToString("N")[..8] });
        var initial = await VersionAsync(id);
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var caller = await AdminCallerAsync(factory);
        var order = 0;

        // Each update runs in a transaction of its own and reads, in that transaction, the version its own write gave the row:
        // the row stays locked by the update until the commit, so no other update can move it in between. The sequence number
        // is drawn while the lock is still held, so it is the order of the commits.
        var written = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => Task.Run(async () =>
        {
            using var scope = fx.App.Services.CreateScope();
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await factory.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            await core.UpdateAsync(session, caller, new Entity("account", id) { ["name"] = $"Version {i}" }, default);
            var own = (await core.RetrieveAsync(session, caller, "account", id, new ColumnSet("versionnumber"), default))
                .GetAttributeValue<long>("versionnumber");
            var sequence = Interlocked.Increment(ref order);
            await tx.CommitAsync();
            return (Sequence: sequence, Version: own);
        })));

        var versions = written.OrderBy(w => w.Sequence).Select(w => w.Version).ToList();
        versions.Should().OnlyHaveUniqueItems();
        versions.Should().OnlyContain(v => v > initial);
        versions.Should().BeInAscendingOrder("each commit gives the row a version above the one before it");
        (await VersionAsync(id)).Should().Be(versions.Max(), "the last commit's version is the row's");
    }

    private async Task<Caller> AdminCallerAsync(IDbConnectionFactory factory)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid, businessunitid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        return new Caller(rd.GetGuid(0), rd.GetGuid(1));
    }

    private async Task<long> VersionAsync(Guid id)
    {
        var row = await ServiceFor(fx, fx.AdminObjectId).RetrieveAsync("account", id, new ColumnSet("versionnumber"));
        return row.GetAttributeValue<long>("versionnumber");
    }
}
