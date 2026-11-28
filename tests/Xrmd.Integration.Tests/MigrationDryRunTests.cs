using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The dry run of an import (migration table mapping spec §8.6, GAP-27 task 13): phase 1 and a read-only preview, started with the
/// option <c>dryRun</c>. It takes no lock, may run on an organization with data, writes nothing but its ledger, lists what the real
/// run would remove, leave empty or not import, fails on a wrong map like the real run, and is neither resumed nor finalized. Each
/// test serves a database of its own with the async worker on; invented names.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationDryRunTests(XrmdFixture fx) : IDisposable
{
    private const string Api = "api/data/v9.2/";
    private const string Customer = "xvcust";
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-gap27-dry-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private MigrationPhaseHarness Host(Action<IServiceCollection>? services = null) => new(fx.WithOwnDatabase(b =>
    {
        b.UseSetting("Pipeline:AsyncWorkerEnabled", "true");
        if (services is not null) b.ConfigureServices(services);
    }), fx);

    private async Task<Guid> UploadAsync(MigrationPhaseHarness h, string file) =>
        await MigrationTestPackage.UploadAsync(h.App.CreateClient(), DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl), file);

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static Task<HttpResponseMessage> StartAsync(MigrationPhaseHarness h, Guid packageId, bool dryRun = true, JsonObject? options = null)
    {
        options ??= [];
        if (dryRun) options["dryRun"] = true;
        return h.SendAsync(HttpMethod.Post, "admin/migrate/runs",
            new JsonObject { ["passphrase"] = Passphrase, ["packageId"] = packageId.ToString(), ["options"] = options }.ToJsonString());
    }

    private static async Task<Guid> StartDryRunAsync(MigrationPhaseHarness h, Guid packageId, JsonObject? options = null)
    {
        var started = await ReadAsync(await StartAsync(h, packageId, options: options), HttpStatusCode.Accepted);
        started["dryRun"]!.GetValue<bool>().Should().BeTrue();
        return Guid.Parse(started["runId"]!.GetValue<string>());
    }

    /// <summary>Polls the run until it has settled (<see cref="MigrationRunPolling.IsSettled"/>), at most 180 s.</summary>
    private static Task<JsonObject> WaitAsync(MigrationPhaseHarness h, Guid runId) =>
        MigrationRunPolling.WaitSettledAsync(async () => await ReadAsync(await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}"), HttpStatusCode.OK),
            TimeSpan.FromSeconds(180));

    private static async Task<JsonObject> ReportAsync(MigrationPhaseHarness h, Guid runId) =>
        await ReadAsync(await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}/report"), HttpStatusCode.OK);

    private static List<(int Phase, string Severity, string Code, string? Table, string? Column, string Message)> Issues(JsonObject report) =>
        [.. report["issues"]!.AsArray().Select(i => (i!["phase"]!.GetValue<int>(), i["severity"]!.GetValue<string>(), i["code"]!.GetValue<string>(),
            i["table"]?.GetValue<string>(), i["column"]?.GetValue<string>(), i["message"]!.GetValue<string>()))];

    // ---- the package: task 9's, with a customer solution, a table the target lacks and a lookup to quote ---------------------

    private static readonly Guid QuoteFormId = Guid.Parse("6e1f7b2d-3c4d-4e5f-8a71-82b3c4d5e601");

    /// <summary>
    /// The customer solution: the <c>lead</c> node with the custom column <c>new_score</c> (mapped: R11), the <c>quote</c> node with a
    /// form and the lookup <c>account.new_quoteid</c> to it (missing: R11b), and the customer's own table <c>new_progetto</c>.
    /// </summary>
    private static byte[] CustomerSolution() => MigrationSolutionMapTests.SolutionFile(false,
        [
            new XElement("Entity",
                new XElement("Name", new XAttribute("LocalizedName", "Lead"), new XAttribute("OriginalName", "Lead"), "Lead"),
                new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", "Lead"),
                    new XElement("LocalizedNames", MigrationSolutionMapTests.Label("LocalizedName", "Lead")),
                    new XElement("attributes",
                        MigrationSolutionMapTests.Attribute("leadid", "primarykey", "Lead", custom: false),
                        MigrationSolutionMapTests.Attribute("new_score", "int", "Score", custom: true)),
                    new XElement("OwnershipTypeMask", "UserOwned")))),
            new XElement("Entity",
                new XElement("Name", new XAttribute("LocalizedName", "Quote"), new XAttribute("OriginalName", "Quote"), "Quote"),
                new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", "Quote"),
                    new XElement("LocalizedNames", MigrationSolutionMapTests.Label("LocalizedName", "Quote")),
                    new XElement("attributes", MigrationSolutionMapTests.Attribute("quoteid", "primarykey", "Quote", custom: false)),
                    new XElement("OwnershipTypeMask", "UserOwned"))),
                new XElement("FormXml", new XElement("forms", new XAttribute("type", "main"), new XElement("systemform",
                    new XElement("formid", QuoteFormId.ToString("B")), new XElement("form"),
                    new XElement("LocalizedNames", MigrationSolutionMapTests.Label("LocalizedName", "Quote form")))))),
            MigrationSolutionMapTests.AccountNode(MigrationSolutionMapTests.Attribute("new_quoteid", "lookup", "Quote", custom: true)),
            MigrationSolutionMapTests.Progetto(),
        ],
        [MigrationSolutionMapTests.OneToMany("new_quote_account_quoteid", referenced: "quote", referencing: "account", "new_quoteid")],
        [(1, "lead", 0), (1, "quote", 0), (1, "account", 2), (1, "new_progetto", 0)]);

    /// <summary>
    /// Task 9's package plus <see cref="CustomerSolution"/>, rows of <c>invoice</c> (a table the target lacks) and of
    /// <c>new_progetto</c> (created by the solution), with lookups to a <c>quote</c> the package does not hold and to an <c>invoice</c>.
    /// </summary>
    private string Package(MigrationMappedImportTests.Ids x) => MigrationMappedImportTests.Package(_root, x, p => p
        .Solution(Customer, CustomerSolution())
        .Table("invoice", "invoiceid", [Record(Guid.NewGuid(), ("name", new StringValue("Invoice 1")))])
        .Table("new_progetto", "new_progettoid",
        [
            Record(Guid.NewGuid(), ("new_name", new StringValue("Progetto 1")), ("new_quoteid", new LookupValue("quote", Guid.NewGuid()))),
            Record(Guid.NewGuid(), ("new_name", new StringValue("Progetto 2")), ("new_invoiceid", new LookupValue("invoice", Guid.NewGuid()))),
        ]));

    private static string Mapped(string component) => $"{Customer}: {component} removed (lead is mapped to xv_lead).";

    private static string Missing(string component) => $"{Customer}: {component} removed (quote is not in this organization and no map names it).";

    /// <summary>The rows of every <c>xv_</c> table and of <c>account</c>, and the solutions and tables of the organization.</summary>
    private static async Task<Dictionary<string, long>> CountsAsync(MigrationPhaseHarness h)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var tables = h.App.Services.GetRequiredService<IMetadataStore>().GetAllEntities()
            .Where(e => e.LogicalName.StartsWith("xv_", StringComparison.Ordinal) && e.IsIntersect != true).Select(e => e.LogicalName).Append("account");
        foreach (var table in tables) counts[table] = Convert.ToInt64(await h.ScalarAsync($"SELECT COUNT(*) FROM data.{table}"));
        counts["(solutions)"] = Convert.ToInt64(await h.ScalarAsync("SELECT COUNT(*) FROM data.solution"));
        counts["(tables)"] = h.App.Services.GetRequiredService<IMetadataStore>().GetAllEntities().Count;
        return counts;
    }

    /// <summary>Phase 1 that signals <paramref name="entered"/> and waits for <paramref name="gate"/> before it verifies.</summary>
    private sealed class GatedVerify(TaskCompletionSource entered, TaskCompletionSource gate) : IMigrationPhase
    {
        private readonly VerifyPackagePhase _inner = new();
        public int Order => _inner.Order;
        public string Name => _inner.Name;

        public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            await _inner.RunAsync(context, ct);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Dry_run_writes_nothing_and_takes_no_lock()
    {
        TaskCompletionSource entered = NewSignal(), gate = NewSignal();
        await using var h = Host(s =>
        {
            foreach (var d in s.Where(d => d.ImplementationType == typeof(VerifyPackagePhase)).ToList()) s.Remove(d);
            s.AddSingleton<IMigrationPhase>(new GatedVerify(entered, gate));
        });
        await MigrationMappedImportTests.PrepareTargetAsync(h, fx);
        var package = await UploadAsync(h, Package(MigrationMappedImportTests.NewIds()));
        var before = await CountsAsync(h);
        before.Keys.Should().Contain(["xv_lead", "xv_dealclose", "xv_rival"]);

        var run = await StartDryRunAsync(h, package);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        h.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull("a dry run never takes the lock");
        (await h.SendAsync(HttpMethod.Post, Api + "accounts", """{"name":"written during the dry run"}""")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        gate.SetResult();
        var status = await WaitAsync(h, run);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed, status["message"]?.ToString());
        status["dryRun"]!.GetValue<bool>().Should().BeTrue();
        var after = await CountsAsync(h);
        after.Should().BeEquivalentTo(new Dictionary<string, long>(before) { ["account"] = before["account"] + 1 }, "only the write of the test is new");
        var store = h.App.Services.GetRequiredService<IMetadataStore>();
        store.GetTable("new_progetto").Should().BeNull("no solution is imported");
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_idmap WHERE runid = @id", ("@id", run))).Should().Be(0);
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_chunk WHERE runid = @id AND entry <> @census", ("@id", run), ("@census", MapCensus.ChunkKey)))
            .Should().Be(0, "nothing but the census mark is committed");
        h.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();
        (await h.SendAsync(HttpMethod.Get, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.OK, "the package stays for the real run");
    }

    [Fact]
    public async Task Dry_run_works_on_an_organization_with_data()
    {
        await using var h = Host();
        (await h.SendAsync(HttpMethod.Post, Api + "accounts", """{"name":"existing"}""")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var package = await UploadAsync(h, Build(_root));

        var run = await StartDryRunAsync(h, package);
        var status = await WaitAsync(h, run);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed, status["message"]?.ToString());
        status["phases"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal("verify", DryRunPreview.Name);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.account")).Should().Be(1);
        var report = await ReportAsync(h, run);
        report["dryRun"]!.GetValue<bool>().Should().BeTrue();
        report["phases"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).Should().Equal("verify", DryRunPreview.Name);

        // The ended dry run blocks no run: a real start is refused for the data only.
        var real = await ReadAsync(await StartAsync(h, package, dryRun: false), HttpStatusCode.Conflict);
        real["error"]!["message"]!.GetValue<string>().Should().Contain("already holds business data");
        h.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();
    }

    [Fact]
    public async Task Dry_run_reports_what_the_real_run_would_remove_and_empty()
    {
        await using var h = Host();
        await MigrationMappedImportTests.PrepareTargetAsync(h, fx);
        var package = await UploadAsync(h, Package(MigrationMappedImportTests.NewIds()));

        var run = await StartDryRunAsync(h, package);
        var status = await WaitAsync(h, run);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed, status["message"]?.ToString());
        status["message"].Should().BeNull();
        var report = await ReportAsync(h, run);
        report["dryRun"]!.GetValue<bool>().Should().BeTrue();
        var issues = Issues(report);
        issues.Where(i => i.Severity == MigrationIssue.Error).Should().BeEmpty();

        // R11 and R11b, as the schema phase of the real run lists them.
        issues.Where(i => i.Code.StartsWith("component-of-", StringComparison.Ordinal)).Select(i => (i.Phase, i.Code, i.Table, i.Column, i.Message))
            .Should().BeEquivalentTo(new (int, string, string?, string?, string)[]
            {
                (SchemaPhase.PhaseOrder, "component-of-mapped-table", "xv_lead", null, Mapped("entity lead")),
                (SchemaPhase.PhaseOrder, "component-of-mapped-table", "xv_lead", "new_score", Mapped("attribute lead.new_score")),
                (SchemaPhase.PhaseOrder, "component-of-missing-table", "quote", null, Missing("entity quote")),
                (SchemaPhase.PhaseOrder, "component-of-missing-table", "quote", null, Missing("form quote/Quote form")),
                (SchemaPhase.PhaseOrder, "component-of-missing-table", "quote", "new_quoteid", Missing("attribute account.new_quoteid")),
                (SchemaPhase.PhaseOrder, "component-of-missing-table", "quote", null, Missing("relationship new_quote_account_quoteid")),
            });
        // The table the target lacks (new_progetto is the solution's own: it would be created), and the lookup to quote.
        issues.Where(i => i.Code.StartsWith("dry-run-", StringComparison.Ordinal)).Select(i => (i.Severity, i.Code, i.Table, i.Column, i.Message))
            .Should().BeEquivalentTo(new (string, string, string?, string?, string)[]
            {
                (MigrationIssue.Warning, "dry-run-table-missing", "invoice", null, "1 row(s)"),
                (MigrationIssue.Warning, "dry-run-lookup-empty", "new_progetto", "new_quoteid", "1 row(s)"),
                // invoice is in the package but missing in the organization: its rows are not imported, the lookup stays empty.
                (MigrationIssue.Warning, "dry-run-lookup-empty", "new_progetto", "new_invoiceid", "1 row(s)"),
            });
        // The census of phase 1: columns not mapped, values defaulted, parties left out.
        issues.Where(i => i.Phase == VerifyPackagePhase.PhaseOrder).Select(i => (i.Table!, i.Column, i.Code, i.Message))
            .Should().BeEquivalentTo(MigrationMappedImportTests.CensusIssues);
        report["mappedTables"]!.AsArray().Select(t => t!["target"]!.GetValue<string>()).Should().BeEquivalentTo("xv_lead", "xv_dealclose", "xv_rival");
        h.App.Services.GetRequiredService<IMetadataStore>().GetTable("new_progetto").Should().BeNull();
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead")).Should().Be(0);
    }

    [Fact]
    public async Task Dry_run_reports_a_managed_solution_the_real_run_refuses()
    {
        await using var h = Host();
        await MigrationMappedImportTests.PrepareTargetAsync(h, fx);
        var file = MigrationMappedImportTests.Package(_root, MigrationMappedImportTests.NewIds(), p => p.Solution(Customer, CustomerSolution(), managed: true));
        var run = await StartDryRunAsync(h, await UploadAsync(h, file));

        var status = await WaitAsync(h, run);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed, status["message"]?.ToString());
        status["message"]!.GetValue<string>().Should().Be(Xrmd.Admin.Migration.MigrationImportJob.DryRunErrorsMessage);
        Issues(await ReportAsync(h, run)).Should().ContainSingle(i => i.Severity == MigrationIssue.Error)
            .Which.Should().Match<(int Phase, string Severity, string Code, string? Table, string? Column, string Message)>(i =>
                i.Code == DryRunPreview.WouldFailCode && i.Table == null && i.Message.Contains($"managed solution '{Customer}'") && i.Message.Contains("entity quote"));
    }

    [Fact]
    public async Task Dry_run_reports_the_strict_failure_of_unsupported_items()
    {
        await using var h = Host();
        var unsupported = new JsonArray(new JsonObject { ["kind"] = "column", ["name"] = "account.new_geo", ["reason"] = "an invented unsupported type" });
        var package = await UploadAsync(h, Build(_root, p => p
            .Security("businessunit", "businessunitid")
            .File(SchemaDeltaApplier.UnsupportedPath, System.Text.Encoding.UTF8.GetBytes(unsupported.ToJsonString()))));

        var run = await StartDryRunAsync(h, package, new JsonObject { ["strict"] = "true" });
        var status = await WaitAsync(h, run);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed, status["message"]?.ToString());
        status["message"]!.GetValue<string>().Should().Be(Xrmd.Admin.Migration.MigrationImportJob.DryRunErrorsMessage);
        var report = await ReportAsync(h, run);
        report["hasErrors"]!.GetValue<bool>().Should().BeTrue();
        var issues = Issues(report);
        issues.Should().Contain(i => i.Code == "unsupported" && i.Table == "account" && i.Column == "new_geo");
        issues.Should().ContainSingle(i => i.Severity == MigrationIssue.Error).Which.Should()
            .Match<(int Phase, string Severity, string Code, string? Table, string? Column, string Message)>(i =>
                i.Code == DryRunPreview.WouldFailCode && i.Table == null && i.Message.StartsWith("The package has items xrmverse cannot import (option strict)"));
    }

    [Fact]
    public async Task Real_run_after_an_ended_dry_run_takes_the_lock_and_closes_it()
    {
        TaskCompletionSource entered = NewSignal(), gate = NewSignal();
        Guid? lockDuringRun = null;
        await using var h = Host(s => s.AddSingleton<IMigrationPhase>(new GatePhase(entered, gate)));
        var package = await UploadAsync(h, Build(_root));
        var dry = await StartDryRunAsync(h, package);
        (await WaitAsync(h, dry))["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed);

        var real = Guid.Parse((await ReadAsync(await StartAsync(h, package, dryRun: false), HttpStatusCode.Accepted))["runId"]!.GetValue<string>());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        lockDuringRun = h.App.Services.GetRequiredService<MigrationLock>().ActiveRunId;
        gate.SetResult();
        var done = await WaitAsync(h, real);

        lockDuringRun.Should().Be(real, "the real run takes the lock as usual");
        done["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized, done["message"]?.ToString());
        var closed = await ReadAsync(await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{dry}"), HttpStatusCode.OK);
        closed["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized);
        closed["message"]!.GetValue<string>().Should().Be("Dry run ended completed.");
    }

    [Fact]
    public async Task Dry_run_can_be_cancelled_and_blocks_nothing_after()
    {
        TaskCompletionSource entered = NewSignal(), gate = NewSignal();
        await using var h = Host(s =>
        {
            foreach (var d in s.Where(d => d.ImplementationType == typeof(VerifyPackagePhase)).ToList()) s.Remove(d);
            s.AddSingleton<IMigrationPhase>(new GatedVerify(entered, gate));
        });
        var package = await UploadAsync(h, Build(_root));
        var run = await StartDryRunAsync(h, package);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));

        (await h.SendAsync(HttpMethod.Post, $"admin/migrate/runs/{run}/cancel")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await WaitAsync(h, run))["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Cancelled);

        h.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();
        (await ReadAsync(await h.SendAsync(HttpMethod.Post, $"admin/migrate/runs/{run}/resume", new JsonObject { ["passphrase"] = Passphrase }.ToJsonString()),
            HttpStatusCode.Conflict))["error"]!["message"]!.GetValue<string>().Should().Be("A dry run cannot be resumed.");
        gate.SetResult();
        var next = await StartDryRunAsync(h, package);
        (await WaitAsync(h, next))["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed);
        (await ReadAsync(await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{run}"), HttpStatusCode.OK))["message"]!.GetValue<string>()
            .Should().Be("Dry run ended cancelled; Cancelled by an administrator.");
    }

    /// <summary>A phase after the others that a real run reaches (a dry run never does): it waits for <paramref name="gate"/>.</summary>
    private sealed class GatePhase(TaskCompletionSource entered, TaskCompletionSource gate) : IMigrationPhase
    {
        public int Order => 50;
        public string Name => "gate";

        public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task Dry_run_fails_on_a_wrong_map_like_the_real_run()
    {
        // The map's required solution and target tables are not installed: the map check (R9) fails the dry run.
        await using var h = Host();
        var package = await UploadAsync(h, MigrationMappedImportTests.Package(_root, MigrationMappedImportTests.NewIds()));

        var run = await StartDryRunAsync(h, package);
        var status = await WaitAsync(h, run);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Failed);
        status["message"]!.GetValue<string>().Should().StartWith("The map does not fit this organization: ").And.Contain("xvmaptarget");
        var issues = Issues(await ReportAsync(h, run));
        issues.Should().Contain(i => i.Code == "map-check" && i.Severity == MigrationIssue.Error);
        issues.Should().NotContain(i => i.Phase == DryRunPreview.PhaseOrder, "the preview does not run after a failed map check");
        h.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();

        // After the map fix the dry run is simply started again: the failed one blocks nothing.
        await StartDryRunAsync(h, package);
    }

    [Fact]
    public async Task Dry_run_fails_when_an_import_map_renames_a_table_whose_custom_columns_were_not_exported()
    {
        await using var h = Host();
        await MigrationMappedImportTests.PrepareTargetAsync(h, fx);
        var package = await UploadAsync(h, MigrationMapCheckTests.UnmappedExport(_root));
        var body = new JsonObject
        {
            ["passphrase"] = Passphrase, ["packageId"] = package.ToString(), ["options"] = new JsonObject { ["dryRun"] = true },
            ["maps"] = new JsonArray(MigrationMapCheckTests.InvoiceMap),
        };

        var started = await ReadAsync(await h.SendAsync(HttpMethod.Post, "admin/migrate/runs", body.ToJsonString()), HttpStatusCode.Accepted);
        var run = Guid.Parse(started["runId"]!.GetValue<string>());
        var status = await WaitAsync(h, run);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Failed);
        status["message"]!.GetValue<string>().Should().Be("The map does not fit this organization: " +
            "table 'invoice': its custom columns 'new_region' were not exported with a map; export again with this map.");
    }

    [Fact]
    public async Task Dry_run_cannot_be_resumed_or_finalized()
    {
        await using var h = Host();
        var package = await UploadAsync(h, Build(_root));
        var run = await StartDryRunAsync(h, package);
        (await WaitAsync(h, run))["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed);

        var resume = await ReadAsync(await h.SendAsync(HttpMethod.Post, $"admin/migrate/runs/{run}/resume",
            new JsonObject { ["passphrase"] = Passphrase }.ToJsonString()), HttpStatusCode.Conflict);
        resume["error"]!["message"]!.GetValue<string>().Should().Be("A dry run cannot be resumed.");
        var finalize = await ReadAsync(await h.SendAsync(HttpMethod.Post, $"admin/migrate/runs/{run}/finalize", "{}"), HttpStatusCode.Conflict);
        finalize["error"]!["message"]!.GetValue<string>().Should().Be("A dry run needs no finalize.");

        (await ReadAsync(await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{run}"), HttpStatusCode.OK))["state"]!.GetValue<string>()
            .Should().Be(MigrationRunStates.Completed);
        (await h.SendAsync(HttpMethod.Get, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dry_run_refused_while_a_run_is_open()
    {
        await using var h = Host();
        var damaged = await UploadAsync(h, Build(_root, damage: true));
        var open = Guid.Parse((await ReadAsync(await StartAsync(h, damaged, dryRun: false), HttpStatusCode.Accepted))["runId"]!.GetValue<string>());
        (await WaitAsync(h, open))["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Failed);
        var package = await UploadAsync(h, Build(_root));

        var res = await ReadAsync(await StartAsync(h, package), HttpStatusCode.Conflict);

        res["error"]!["message"]!.GetValue<string>().Should().Be($"Another migration run is open: {open}.");
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(1);
        h.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().Be(open, "the real run keeps its lock");
    }
}
