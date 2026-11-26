using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Core;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Which map a migration run uses (migration table mapping spec §8.1, GAP-27 task 7): the start request's <c>maps</c> checked and
/// kept in the run's options, the empty-organization check on the target tables, and a resumed run that translates with the map it
/// started with. Invented names, each test on a database of its own.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationMapStartTests(XrmdFixture fx) : IDisposable
{
    private const string Api = "api/data/v9.2/";

    private const string LeadMap = """
        version: 1
        tables:
          - source: lead
            target: xv_lead
            columns:
              subject: xv_name
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-gap27-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static MigrationRecord Lead(string subject) => Record(Guid.NewGuid(), ("subject", new StringValue(subject)));

    /// <summary>A package with the table <c>lead</c> (and the security tables of a minimal package).</summary>
    private string LeadPackage(PackageMapsPart? maps = null, int leads = 2, int recordsPerChunk = 5000) => Build(_root, p =>
    {
        p.Security("businessunit", "businessunitid")
            .Table("lead", "leadid", Enumerable.Range(1, leads).Select(i => Lead($"Lead {i}")));
    }, maps, recordsPerChunk);

    private static JsonObject StartBody(Guid packageId, params string[] maps)
    {
        var body = new JsonObject { ["passphrase"] = Passphrase, ["packageId"] = packageId.ToString() };
        if (maps.Length > 0) body["maps"] = new JsonArray([.. maps.Select(m => (JsonNode)m)]);
        return body;
    }

    private static async Task<(HttpStatusCode Status, string Text)> StartAsync(MigrationPhaseHarness h, JsonObject body)
    {
        var res = await h.SendAsync(HttpMethod.Post, "admin/migrate/runs", body.ToJsonString());
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    private static string Message(string text) => JsonNode.Parse(text)!["error"]!["message"]!.GetValue<string>();

    private async Task<Guid> UploadAsync(MigrationPhaseHarness h, string file) =>
        await MigrationTestPackage.UploadAsync(h.App.CreateClient(), DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl), file);

    /// <summary>Imports the map target solution as the migration's schema phase does.</summary>
    private static async Task InstallTargetSolutionAsync(MigrationPhaseHarness h)
    {
        using var scope = h.App.Services.CreateScope();
        Caller caller;
        await using (var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync(default))
            caller = await scope.ServiceProvider.GetRequiredService<CoreOperations>().ResolveUserAsync(await h.AdminIdAsync(), new DataSession(conn, null), default);
        await scope.ServiceProvider.GetRequiredService<SolutionImporter>().ImportInOwnTransactionAsync(caller, MappingTargetSolution.Build(),
            new ImportOptions(null, OverwriteUnmanagedCustomizations: true, PublishWorkflows: false), default);
    }

    [Fact]
    public async Task Start_with_maps_stores_them_in_the_options()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = await UploadAsync(h, LeadPackage());

        var (status, text) = await StartAsync(h, StartBody(package, LeadMap));

        status.Should().Be(HttpStatusCode.Accepted, text);
        var runId = Guid.Parse(JsonNode.Parse(text)!["runId"]!.GetValue<string>());
        var options = JsonNode.Parse((string)(await h.ScalarAsync("SELECT options FROM meta.migration_run WHERE runid = @id", ("@id", runId)))!)!;
        var maps = JsonNode.Parse(options["maps"]!.GetValue<string>())!.AsArray();
        maps.Select(m => m!.GetValue<string>()).Should().Equal(LeadMap);
    }

    [Fact]
    public async Task Start_refuses_an_invalid_map()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = await UploadAsync(h, LeadPackage());

        var (status, text) = await StartAsync(h, StartBody(package, LeadMap.Replace("columns:", "colums:", StringComparison.Ordinal)));

        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("colums");
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(0);
    }

    [Fact]
    public async Task Start_refuses_maps_whose_sources_are_not_in_the_package()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = await UploadAsync(h, LeadPackage());
        const string rivals = """
            version: 1
            tables:
              - source: competitor
                target: xv_rival
              - source: opportunityclose
                target: xv_dealclose
            """;

        var (status, text) = await StartAsync(h, StartBody(package, LeadMap, rivals));

        status.Should().Be(HttpStatusCode.BadRequest, text);
        Message(text).Should().Be("The maps name sources that are not in the package: table 'competitor', table 'opportunityclose'.");
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(0);
    }

    [Fact]
    public async Task Start_refuses_maps_whose_relationships_are_not_in_the_package()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var file = Build(_root, p => p.Security("businessunit", "businessunitid")
            .Table("lead", "leadid", [Lead("Lead 1")])
            .Relationship("leadcompetitors", []));
        var package = await UploadAsync(h, file);
        const string relationships = """
            version: 1
            relationships:
              - { source: leadcompetitors, target: xv_lead_xv_rival }
              - { source: leadrivals, target: xv_lead_xv_other }
            """;

        var (status, text) = await StartAsync(h, StartBody(package, LeadMap, relationships));

        status.Should().Be(HttpStatusCode.BadRequest, text);
        Message(text).Should().Be("The maps name sources that are not in the package: relationship 'leadrivals'.");
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(0);
    }

    [Fact]
    public async Task Start_refuses_maps_that_send_two_tables_to_one()
    {
        // lead goes to contact, which the package also holds: both would be written to contact.
        await using var h = MigrationPhaseHarness.Start(fx);
        var file = Build(_root, p => p.Security("businessunit", "businessunitid")
            .Table("lead", "leadid", [Lead("Lead 1")])
            .Table("contact", "contactid", [Record(Guid.NewGuid(), ("lastname", new StringValue("Contact 1")))]));
        var package = await UploadAsync(h, file);
        const string intoContact = """
            version: 1
            tables:
              - source: lead
                target: contact
            """;

        var (status, text) = await StartAsync(h, StartBody(package, intoContact));

        status.Should().Be(HttpStatusCode.BadRequest, text);
        Message(text).Should().Be("The maps send more than one table of the package to the same table: 'contact' (from 'lead', 'contact').");
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(0);
    }

    [Fact]
    public async Task Resume_with_maps_is_refused()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(LeadPackage(), null);
        await TestSql.ExecAsync(h.ConnectionString, "UPDATE meta.migration_run SET state = 'failed' WHERE runid = @run", ("@run", h.RunId));
        var body = new JsonObject { ["passphrase"] = Passphrase, ["maps"] = new JsonArray(LeadMap) };

        var res = await h.SendAsync(HttpMethod.Post, $"admin/migrate/runs/{h.RunId}/resume", body.ToJsonString());

        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("A resumed run keeps the map it started with.");
        (await h.ScalarAsync("SELECT state FROM meta.migration_run WHERE runid = @run", ("@run", h.RunId))).Should().Be(MigrationRunStates.Failed);
    }

    [Fact]
    public async Task Empty_organization_check_uses_target_names()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        (await h.SendAsync(HttpMethod.Post, Api + "xv_leads", """{"xv_name":"existing"}""")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var maps = new PackageMapsPart([("leads", LeadMap)],
            new SourceCatalog([CatalogTable("lead", "leadid", false, "user", ("subject", "String", []))], []));
        var package = await UploadAsync(h, LeadPackage(maps));

        var (status, text) = await StartAsync(h, StartBody(package));

        status.Should().Be(HttpStatusCode.Conflict, text);
        JsonNode.Parse(text)!["tables"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal(MappingTargetSolution.Lead);
    }

    [Fact]
    public async Task Resume_translates_with_the_stored_map()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        // The map check of phase 1 needs the target table before anything is written: the solution is installed first.
        await InstallTargetSolutionAsync(h);
        var package = LeadPackage(leads: 3, recordsPerChunk: 2);
        var options = new Dictionary<string, string> { [MigrationMaps.OptionName] = new JsonArray(LeadMap).ToJsonString() };
        await h.RunAsync(package, options, new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase());
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead")).Should().Be(3);

        // A failure after the first sub-chunk: the rows of the later ones were never written nor marked.
        var entries = (await TestSql.RowsAsync(h.ConnectionString,
            "SELECT entry FROM meta.migration_chunk WHERE runid = @run AND entry LIKE 'data/lead/%' ORDER BY entry", ("@run", h.RunId)))
            .Select(r => (string)r[0]!).ToList();
        entries.Should().HaveCount(2);
        await TestSql.ExecAsync(h.ConnectionString, "DELETE FROM meta.migration_chunk WHERE runid = @run AND entry = @entry", ("@run", h.RunId), ("@entry", entries[1]));
        await TestSql.ExecAsync(h.ConnectionString, "DELETE FROM data.xv_lead WHERE xv_name = 'Lead 3'");
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead")).Should().Be(2);

        // The resume opens the package again and attaches the map of the run's options.
        await h.RunAsync(package, options, new RecordsPhase());

        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead")).Should().Be(3);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead WHERE xv_name IN ('Lead 1', 'Lead 2', 'Lead 3')")).Should().Be(3);
        (await h.IssuesAsync()).Should().NotContain(i => i.Table == "lead", "no row is attempted under the source name");
    }
}
