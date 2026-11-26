using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin.Migration.Import;
using Xrmd.Core;
using Xrmd.Core.Query;
using Xrmd.Metadata;
using Xrmd.Migration.Records;
using Xrmd.Query.FetchXml;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3b, Decision 9: the local timed comparison of the CRUD paths against <c>main</c> (median of 10 warm runs, after 2 warm-up runs),
/// run by hand on the branch and on <c>main</c> on the same machine (<c>--filter Category=Timing</c>; not in CI): 2,000 Creates of a
/// plain table through the pipeline, a 5,000-row RetrieveMultiple through the Web API, a 5,000-row read through the SDK twin of the
/// query executor, and a migration import of 2,000 rows (<see cref="ImportRecordWriter"/>). Each test prints its median
/// (<c>TIMING name median=… ms</c>); the budget (+10% over <c>main</c>) is checked by the person comparing the two runs. The file uses
/// only members that exist on <c>main</c> too, so it is copied there unchanged. Each test deletes the rows it wrote in a <c>finally</c>
/// (the import rolls its transaction back), so the shared Pipe table keeps no timing rows. CI excludes the class with
/// <c>Category!=Timing</c> in every test filter (<c>ci.yml</c>, <c>devcontainer.yml</c>).
/// </summary>
[Collection("xrmd")]
[Trait("Category", "Timing")]
public sealed class CoreCrudTimingTests(XrmdFixture fx, ITestOutputHelper output)
{
    private const string Pipe = TestEntities.Pipe;
    private const int Warmups = 2;
    private const int Runs = 10;
    private const int ReadRows = 5000;
    private const string ReadPrefix = "tim-read-";

    private OrganizationService Admin => SecurityTestHelper.ServiceFor(fx, fx.AdminObjectId);

    private async Task<double> MedianAsync(string name, Func<Task> run, Func<Task>? cleanup = null)
    {
        var times = new List<double>();
        for (var i = 0; i < Warmups + Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            await run();
            watch.Stop();
            if (i >= Warmups) times.Add(watch.Elapsed.TotalMilliseconds);
            if (cleanup is not null) await cleanup();
        }
        times.Sort();
        var median = (times[Runs / 2 - 1] + times[Runs / 2]) / 2;
        var line = $"TIMING {name} median={median:F1} ms runs=[{string.Join(", ", times.Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]";
        output.WriteLine(line);
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("XRMD_TIMING_OUT") is { Length: > 0 } file) await File.AppendAllTextAsync(file, line + "\n");
        return median;
    }

    /// <summary>
    /// The 5,000 rows a read returns, created by the read test and deleted by it in a <c>finally</c> (<see cref="DeleteReadRowsAsync"/>),
    /// so the shared Pipe table keeps no timing rows.
    /// </summary>
    private async Task SeedReadRowsAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await DeleteReadRowsAsync();
        for (var batch = 0; batch < ReadRows / 500; batch++)
            await Admin.ExecuteAsync(new CreateMultipleRequest
            {
                Targets = new EntityCollection([.. Enumerable.Range(0, 500).Select(i => new Entity(Pipe)
                {
                    ["pb_name"] = $"{ReadPrefix}{batch * 500 + i:D5}", ["pb_count"] = i, ["pb_data"] = "timing row",
                })]) { EntityName = Pipe },
            });
    }

    private Task DeleteReadRowsAsync() => TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{Pipe} WHERE pb_name LIKE '{ReadPrefix}%'");

    private Task DeleteCreatedRowsAsync() => TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{Pipe} WHERE pb_name LIKE 'tim-create-%'");

    [Fact]
    public async Task Creates_through_the_pipeline()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var admin = Admin;
        try
        {
            await MedianAsync("create-2000-pipeline", async () =>
            {
                for (var i = 0; i < 2000; i++) await admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = $"tim-create-{i:D4}", ["pb_count"] = i });
            }, DeleteCreatedRowsAsync);
        }
        finally
        {
            await DeleteCreatedRowsAsync();
        }
    }

    [Fact]
    public async Task Retrieve_multiple_through_the_web_api()
    {
        try
        {
            await SeedReadRowsAsync();
            var path = $"/api/data/v9.2/{TestEntities.PipeSet}?$select=pb_name,pb_count,pb_data,createdon,_ownerid_value&$filter=startswith(pb_name,'{ReadPrefix}')&$orderby=pb_name";
            await MedianAsync("retrievemultiple-5000-webapi", async () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
                req.Headers.Add("Prefer", "odata.include-annotations=\"*\",odata.maxpagesize=5000");
                var res = await fx.Client.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                res.StatusCode.Should().Be(HttpStatusCode.OK, body);
                JsonNode.Parse(body)!["value"]!.AsArray().Count.Should().Be(ReadRows);
            });
        }
        finally
        {
            await DeleteReadRowsAsync();
        }
    }

    [Fact]
    public async Task Query_executor_sdk_read()
    {
        try
        {
            await SeedReadRowsAsync();
            var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
            var fetch = $"""
                <fetch><entity name="{Pipe}"><attribute name="pb_name"/><attribute name="pb_count"/><attribute name="pb_data"/>
                <attribute name="createdon"/><attribute name="ownerid"/>
                <filter><condition attribute="pb_name" operator="like" value="{ReadPrefix}%"/></filter><order attribute="pb_name"/></entity></fetch>
                """;
            await MedianAsync("queryexecutor-5000-sdk", async () =>
            {
                await using var scope = fx.App.Services.CreateAsyncScope();
                var executor = scope.ServiceProvider.GetRequiredService<QueryExecutor>();
                var rows = await executor.ExecuteAsync(FetchXmlParser.Parse(fetch), caller, default);
                rows.Entities.Should().HaveCount(ReadRows);
            });
        }
        finally
        {
            await DeleteReadRowsAsync();
        }
    }

    [Fact]
    public async Task Migration_import()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        await MedianAsync("migration-import-2000", async () =>
        {
            var records = Enumerable.Range(0, 2000).Select(i => new MigrationRecord(Guid.NewGuid(), new Dictionary<string, MigrationValue>
            {
                ["pb_name"] = new StringValue($"tim-import-{i:D4}"), ["pb_count"] = new IntValue(i),
            })).ToList();
            await using var scope = fx.App.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<IMetadataStore>();
            var writer = new ImportRecordWriter(services.GetRequiredService<CoreOperations>(), caller,
                new MigrationLedger(services.GetRequiredService<IDbConnectionFactory>(), Guid.NewGuid()),
                new ImportRecordBuilder(store, new Dictionary<Guid, Guid>(), new HashSet<string>(StringComparer.Ordinal), strictLookups: false), 4, store);
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            using (MigrationScope.Enter())
                await writer.WriteAsync(new DataSession(conn, tx), Pipe, records, null, default);
            await tx.RollbackAsync();
        });
    }
}
