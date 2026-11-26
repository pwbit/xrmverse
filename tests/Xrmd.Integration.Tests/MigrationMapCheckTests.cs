using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Cli;
using Xrmd.Core;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The map check (R9) and the census (R10) of phase 1 (migration table mapping spec §8.2, §8.5, GAP-27 task 8): a map that does not
/// fit the organization fails the run before anything is written, with every problem listed; a map that fits has its translation
/// counted once per (table, column, code), and the report shows a section per mapped table. Invented names, each test on a database
/// of its own.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationMapCheckTests(XrmdFixture fx) : IDisposable
{
    private const string Requires = "requires: { solution: xvmaptarget, minVersion: 1.0.0.0 }";

    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-gap27-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The source tables of the tests as the export describes them.</summary>
    private static SourceCatalog Catalog(params SourceCatalogColumn[] extraLeadColumns) => new(
    [
        new SourceCatalogTable("lead", "leadid", null, "user", false,
        [
            new("subject", "String", [], false), new("leadsourcecode", "Picklist", [], false), new("statecode", "State", [], false),
            new("statuscode", "Status", [], false), new("parentaccountid", "Lookup", ["account"], false), .. extraLeadColumns,
        ]),
        CatalogTable("account", "accountid", false, "user", ("name", "String", []), ("industrycode", "Picklist", [])),
        CatalogTable("opportunityclose", "activityid", true, "user", ("subject", "String", [])),
        CatalogTable("competitor", "competitorid", false, "user", ("name", "String", [])),
    ], []);

    private string Package(string map, SourceCatalog? catalog = null, IEnumerable<MigrationRecord>? leads = null, bool solution = false,
        Action<PackageBuilder>? more = null) => Build(_root, p =>
    {
        more?.Invoke(p);
        if (solution) p.Solution(MappingTargetSolution.Name, MappingTargetSolution.Build(), order: 1);
        p.Security("businessunit", "businessunitid")
            .Table("account", "accountid", [])
            .Table("lead", "leadid", leads ?? [Record(Guid.NewGuid(), ("subject", new StringValue("Lead 1")))])
            .Table("opportunityclose", "activityid", [])
            .Table("competitor", "competitorid", []);
    }, new PackageMapsPart([("test", map)], catalog ?? Catalog()));

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

    /// <summary>Runs phase 1 with <paramref name="map"/> on an organization that has the target solution; returns the run's failure message.</summary>
    private async Task<string> CheckFailsAsync(string map, SourceCatalog? catalog = null, Func<MigrationPhaseHarness, Task>? setup = null)
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        if (setup is not null) await setup(h);
        var act = () => h.RunAsync(Package(map, catalog), null, new VerifyPackagePhase());
        var message = (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message;
        message.Should().StartWith("The map does not fit this organization: ");
        var issues = await h.IssuesAsync();
        issues.Should().NotBeEmpty().And.OnlyContain(i => i.Code == "map-check" && i.Severity == MigrationIssue.Error && i.Phase == 1);
        foreach (var issue in issues.Take(VerifyPackagePhase.MaxListed)) message.Should().Contain(issue.Message);
        return message;
    }

    /// <summary>Runs phase 1 with <paramref name="map"/> on an organization that has the target solution; asserts it passes the check.</summary>
    private async Task CheckPassesAsync(string map, SourceCatalog? catalog = null, Func<MigrationPhaseHarness, Task>? setup = null)
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        if (setup is not null) await setup(h);
        await h.RunAsync(Package(map, catalog), null, new VerifyPackagePhase());
        (await h.IssuesAsync()).Should().NotContain(i => i.Code == "map-check");
    }

    /// <summary>Adds the custom image column <c>xv_photo</c> to <c>xv_lead</c>.</summary>
    private static async Task AddPhotoColumnAsync(MigrationPhaseHarness h)
    {
        var res = await h.SendAsync(HttpMethod.Post, "api/data/v9.2/EntityDefinitions(LogicalName='xv_lead')/Attributes",
            """{ "@odata.type": "Microsoft.Dynamics.CRM.ImageAttributeMetadata", "LogicalName": "xv_photo", "SchemaName": "xv_photo" }""");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
    }

    private static string LeadMap(string body) => $"""
        version: 1
        {Requires}
        tables:
          - source: lead
            target: xv_lead
        {body}
        """;

    [Fact]
    public async Task Missing_required_solution_fails_before_any_write()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        // The package brings the target solution itself: had phase 1 let the run go on, the schema phase would install it.
        var package = Package(LeadMap("    columns: { subject: xv_name }"), solution: true);

        var act = () => h.RunAsync(package, null, new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase());

        var message = (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message;
        message.Should().StartWith("The map does not fit this organization: ")
            .And.Contain("solution 'xvmaptarget' is not installed (the map requires version 1.0.0.0 or later)")
            .And.Contain("table 'xv_lead' (target of 'lead') does not exist");
        (await h.ScalarAsync("SELECT OBJECT_ID('data.xv_lead')")).Should().BeNull();
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = 'xvmaptarget'")).Should().Be(0);
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_chunk WHERE runid = @run", ("@run", h.RunId))).Should().Be(0);
        (await h.IssuesAsync()).Should().OnlyContain(i => i.Code == "map-check");
    }

    [Fact]
    public async Task Too_old_solution_version_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    columns: { subject: xv_name }").Replace("minVersion: 1.0.0.0", "minVersion: 2.0", StringComparison.Ordinal));
        message.Should().Be("The map does not fit this organization: solution 'xvmaptarget' is at version 1.0.0.0, the map requires 2.0 or later.");
    }

    [Fact]
    public async Task Missing_target_table_fails()
    {
        var message = await CheckFailsAsync("""
            version: 1
            tables:
              - source: lead
                target: xv_nope
            """);
        message.Should().Be("The map does not fit this organization: table 'xv_nope' (target of 'lead') does not exist.");
    }

    [Fact]
    public async Task Missing_target_column_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    columns: { subject: xv_nope }"));
        message.Should().Be("The map does not fit this organization: column 'xv_lead.xv_nope' (target of 'lead.subject') does not exist.");
    }

    [Fact]
    public async Task Missing_derived_target_column_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    derived: { xv_nope: { from: parentaccountid.name } }"));
        message.Should().Be("The map does not fit this organization: derived column 'xv_lead.xv_nope' does not exist.");
    }

    [Fact]
    public async Task Missing_choice_value_in_values_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    columns: { leadsourcecode: { target: xv_source, values: { 1: 999 } } }"));
        message.Should().Be("The map does not fit this organization: option 999 of 'xv_lead.xv_source' does not exist (values of 'lead.leadsourcecode').");
    }

    [Fact]
    public async Task Missing_choice_value_in_fallback_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    columns: { leadsourcecode: { target: xv_source, values: { 1: 100000000 }, fallback: 998 } }"));
        message.Should().Be("The map does not fit this organization: option 998 of 'xv_lead.xv_source' does not exist (fallback of 'lead.leadsourcecode').");
    }

    [Fact]
    public async Task Missing_choice_value_in_derived_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    derived: { xv_source: { from: parentaccountid.industrycode, values: { 1: 997 }, fallback: 100000006 } }"));
        message.Should().Be("The map does not fit this organization: option 997 of 'xv_lead.xv_source' does not exist (values of derived 'xv_lead.xv_source').");
    }

    [Fact]
    public async Task Missing_state_pair_fails()
    {
        var message = await CheckFailsAsync(LeadMap("""
                state:
                  - { from: [0, 1], to: [0, 999] }
                stateFallback: { 1: [1, 998] }
            """));
        message.Should().Be("The map does not fit this organization: status pair [0, 999] of 'xv_lead' does not exist (state of 'lead'); " +
            "status pair [1, 998] of 'xv_lead' does not exist (stateFallback of 'lead').");
    }

    [Fact]
    public async Task Missing_relationship_fails()
    {
        var message = await CheckFailsAsync(LeadMap("""
                columns: { subject: xv_name }
            relationships:
              - { source: leadcompetitors, target: xv_lead_xv_nope }
            """));
        message.Should().Be("The map does not fit this organization: relationship 'xv_lead_xv_nope' (target of 'leadcompetitors') does not exist.");
    }

    [Fact]
    public async Task Type_mismatch_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    columns: { subject: xv_source }"));
        message.Should().Be("The map does not fit this organization: column 'lead.subject' (String) cannot be written to 'xv_lead.xv_source' (Picklist).");
    }

    [Fact]
    public async Task Carried_column_clashing_with_a_target_column_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    columns: { subject: xv_name }"),
            Catalog(new SourceCatalogColumn("xv_source", "Integer", [], true)));
        message.Should().Be("The map does not fit this organization: carried custom column 'lead.xv_source' (Integer) clashes with 'xv_lead.xv_source' (Picklist).");
    }

    [Fact]
    public async Task Carried_lookup_to_other_tables_than_the_existing_lookup_fails()
    {
        // xv_lead.xv_parentaccountid points to account; the source's lookup of the same name points to contact.
        var message = await CheckFailsAsync(LeadMap("    columns: { subject: xv_name }"),
            Catalog(new SourceCatalogColumn("xv_parentaccountid", "Lookup", ["contact"], true)));
        message.Should().Be("The map does not fit this organization: carried custom lookup 'lead.xv_parentaccountid' (to contact) clashes with 'xv_lead.xv_parentaccountid' (to account).");
    }

    /// <summary>
    /// An export without a map (table mapping spec §8.7): the candidate <c>invoice</c>, with the custom column <c>new_region</c>, is in the
    /// catalog but not in the delta; <paramref name="deltaColumns"/> are the delta's columns of it.
    /// </summary>
    internal static string UnmappedExport(string root, params string[] deltaColumns)
    {
        var catalog = new SourceCatalog(
        [
            new SourceCatalogTable("invoice", "invoiceid", null, "user", false,
                [new("invoiceid", "Uniqueidentifier", [], false), new("name", "String", [], false), new("new_region", "String", [], true)]),
        ], []);
        var delta = new JsonObject
        {
            ["columns"] = new JsonArray([.. deltaColumns.Select(c => (JsonNode)new JsonObject { ["table"] = "invoice", ["column"] = c, ["type"] = "String" })]),
        };
        return Build(root, p =>
        {
            p.Security("businessunit", "businessunitid")
                .Candidate("invoice", "invoiceid", [Record(Guid.NewGuid(), ("name", new StringValue("Bill 1")), ("new_region", new StringValue("North")))]);
            if (deltaColumns.Length > 0) p.File("schema/delta.json", System.Text.Encoding.UTF8.GetBytes(delta.ToJsonString()));
        }, new PackageMapsPart([], catalog));
    }

    /// <summary>The import map that renames the candidate of <see cref="UnmappedExport"/> into <c>xv_lead</c>.</summary>
    internal const string InvoiceMap = $$"""
        version: 1
        {{Requires}}
        tables:
          - source: invoice
            target: xv_lead
            columns: { name: xv_name }
        """;

    [Fact]
    public async Task Table_renamed_only_at_import_fails_when_its_custom_columns_were_not_exported()
    {
        // The export had no map: its delta holds no column of invoice, and the import's map renames it with customColumns: carry.
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        var options = new Dictionary<string, string> { [MigrationMaps.OptionName] = MigrationMaps.ToOption([InvoiceMap]) };

        var act = () => h.RunAsync(UnmappedExport(_root), options, new VerifyPackagePhase());

        (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message.Should().Be("The map does not fit this organization: " +
            "table 'invoice': its custom columns 'new_region' were not exported with a map; export again with this map.");
        (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "map-check");
    }

    [Fact]
    public async Task Table_renamed_only_at_import_passes_when_the_delta_has_its_custom_columns()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        var options = new Dictionary<string, string> { [MigrationMaps.OptionName] = MigrationMaps.ToOption([InvoiceMap]) };

        await h.RunAsync(UnmappedExport(_root, "new_region"), options, new VerifyPackagePhase());

        (await h.IssuesAsync()).Should().NotContain(i => i.Code == "map-check");
    }

    [Fact]
    public async Task Kind_mismatch_fails()
    {
        var message = await CheckFailsAsync("""
            version: 1
            tables:
              - source: opportunityclose
                target: xv_lead
            """);
        message.Should().Be("The map does not fit this organization: table 'opportunityclose' is an activity table but 'xv_lead' is not.");
    }

    [Fact]
    public async Task Ownership_mismatch_fails()
    {
        var message = await CheckFailsAsync("""
            version: 1
            tables:
              - source: competitor
                target: xv_rival
            """);
        message.Should().Be("The map does not fit this organization: table 'competitor' is user-owned but 'xv_rival' is organization-owned.");
    }

    [Fact]
    public async Task Virtual_columns_are_typed_by_their_kind()
    {
        // Dataverse types images, files and multi-select choices as Virtual; the catalog's options tell a multi-select choice apart.
        var catalog = Catalog(new SourceCatalogColumn("entityimage", "Virtual", [], false), new SourceCatalogColumn("xv_photo", "Virtual", [], true),
            new SourceCatalogColumn("new_tags", "Virtual", [], true)) with
        {
            Options = [new SourceCatalogOption("lead", "new_tags", 1, null, "Hot")],
        };
        // An image renamed onto an image, and a carried image column that the target already has as an image: no problem.
        await CheckPassesAsync(LeadMap("    columns: { entityimage: xv_photo, subject: xv_name }"),
            catalog, AddPhotoColumnAsync);

        var message = await CheckFailsAsync(LeadMap("    columns: { new_tags: xv_photo }"), catalog, AddPhotoColumnAsync);
        message.Should().Be("The map does not fit this organization: column 'lead.new_tags' (Virtual) cannot be written to 'xv_lead.xv_photo' (Image).");
    }

    [Fact]
    public async Task Whole_number_into_a_choice_fails()
    {
        var message = await CheckFailsAsync(LeadMap("    columns: { new_score: { target: xv_source, values: { 1: 100000000 }, fallback: 100000006 } }"),
            Catalog(new SourceCatalogColumn("new_score", "Integer", [], true)));
        message.Should().Be("The map does not fit this organization: column 'lead.new_score' (Integer) cannot be written to 'xv_lead.xv_source' (Picklist).");
    }

    [Fact]
    public async Task Whole_number_target_takes_any_value()
    {
        // Rule 7: a status reason into a whole number column, with values that are no option of anything.
        var catalog = Catalog() with
        {
            Tables = [.. Catalog().Tables.Select(t => t.LogicalName == "opportunityclose"
                ? t with { Columns = [.. t.Columns, new SourceCatalogColumn("statuscode", "Status", [], false)] } : t)],
        };
        await CheckPassesAsync("""
            version: 1
            tables:
              - source: opportunityclose
                target: xv_dealclose
                columns:
                  statuscode: { target: xv_reason, values: { 5: 123456 }, fallback: 42 }
            """, catalog);
    }

    [Fact]
    public async Task Problem_list_stops_after_ten()
    {
        var columns = string.Join(", ", Enumerable.Range(1, 12).Select(i => $"c{i:00}: xv_nope{i:00}"));
        var message = await CheckFailsAsync(LeadMap($"    columns: {{ {columns} }}"));
        message.Should().StartWith("The map does not fit this organization: column 'xv_lead.xv_nope01' (target of 'lead.c01') does not exist; ")
            .And.Contain("column 'xv_lead.xv_nope10' (target of 'lead.c10') does not exist; and 2 more.")
            .And.NotContain("xv_nope11");
    }

    [Fact]
    public async Task Map_check_issues_are_replaced_when_phase_1_runs_again()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package(LeadMap("    columns: { subject: xv_name }"));
        async Task<int> MapCheckIssuesAsync() => (await h.IssuesAsync()).Count(i => i.Code == "map-check");

        await FluentActions.Awaiting(() => h.RunAsync(package, null, new VerifyPackagePhase())).Should().ThrowAsync<MigrationPhaseException>();
        var first = await MapCheckIssuesAsync();
        first.Should().Be(2, "the solution and the target table are missing");

        await FluentActions.Awaiting(() => h.RunAsync(package, null, new VerifyPackagePhase())).Should().ThrowAsync<MigrationPhaseException>();
        (await MapCheckIssuesAsync()).Should().Be(first);

        // The organization fixed, the resumed run passes the check and keeps no stale error.
        await InstallTargetSolutionAsync(h);
        await h.RunAsync(package, null, new VerifyPackagePhase());
        (await MapCheckIssuesAsync()).Should().Be(0);
        (await h.Ledger.HasErrorsAsync(default)).Should().BeFalse();
        (await h.IssuesAsync()).Should().Contain(i => i.Code == "mapped-table");
    }

    [Fact]
    public async Task Census_counts_parties_left_out_of_an_unmapped_activity_table()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        var lead = Guid.NewGuid();
        Party Lead() => new(1, "lead", lead, null);
        var package = Package(LeadMap("    columns: { subject: xv_name }"), leads: [Record(lead, ("subject", new StringValue("Lead 1")))], more: p => p
            .Table("task", "activityid",
            [
                Record(Guid.NewGuid(), ("subject", new StringValue("Call 1")), ("xv_parties", new PartyListValue([Lead()]))),
                Record(Guid.NewGuid(), ("subject", new StringValue("Call 2")), ("xv_parties", new PartyListValue([Lead()]))),
                Record(Guid.NewGuid(), ("subject", new StringValue("Call 3"))),
            ]));

        await h.RunAsync(package, null, new VerifyPackagePhase());

        (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "party-not-allowed")
            .Which.Should().Be(new MigrationIssue(1, MigrationIssue.Warning, "party-not-allowed", "task", null, "xv_parties", "2 row(s)"));
    }

    private const string CensusMap = $$"""
        version: 1
        {{Requires}}
        tables:
          - source: lead
            target: xv_lead
            columns:
              subject: xv_name
              leadsourcecode: { target: xv_source, values: { 1: 100000000 }, fallback: 100000006 }
        """;

    private static IEnumerable<MigrationRecord> CensusLeads() =>
    [
        Record(Guid.NewGuid(), ("subject", new StringValue("Lead 1")), ("leadsourcecode", new OptionValue(1))),
        Record(Guid.NewGuid(), ("subject", new StringValue("Lead 2")), ("leadsourcecode", new OptionValue(5))),
        Record(Guid.NewGuid(), ("subject", new StringValue("Lead 3")), ("leadsourcecode", new OptionValue(7))),
    ];

    [Fact]
    public async Task Census_counts_are_written_once()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        var package = Package(CensusMap, leads: CensusLeads());

        await h.RunAsync(package, null, new VerifyPackagePhase());

        var issues = await h.IssuesAsync();
        issues.Should().ContainSingle(i => i.Code == "value-defaulted")
            .Which.Should().Be(new MigrationIssue(1, MigrationIssue.Warning, "value-defaulted", "xv_lead", null, "xv_source", "2 row(s)"));
        issues.Should().ContainSingle(i => i.Code == "mapped-table")
            .Which.Should().Be(new MigrationIssue(1, MigrationIssue.Info, "mapped-table", "xv_lead", null, null, "lead → xv_lead: 3 rows"));

        // Phase 1 again (a resumed run that failed in it) and the records phase: the census is not counted twice.
        await h.RunAsync(package, null, new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase());

        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead")).Should().Be(3);
        issues = await h.IssuesAsync();
        issues.Where(i => i.Code == "value-defaulted").Should().ContainSingle();
        issues.Where(i => i.Code == "mapped-table").Should().ContainSingle();
    }

    [Fact]
    public async Task Report_has_the_mapped_tables_section()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        await h.RunAsync(Package(CensusMap, leads: CensusLeads()), null, new VerifyPackagePhase());

        var res = await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{h.RunId}/report");
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var mapped = JsonNode.Parse(text)!["mappedTables"]!.AsArray();
        mapped.Should().ContainSingle();
        mapped[0]!.ToJsonString().Should().Be("""{"source":"lead","target":"xv_lead","rows":3,"notes":[{"column":"xv_source","code":"value-defaulted","count":2}]}""");

        var configDir = Path.Combine(_root, "cli");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Task<int> CliAsync(params string[] args)
        {
            output.GetStringBuilder().Clear();
            var env = new CliEnvironment(output, error, new StringReader(""), false, h.App.Server.CreateHandler(), configDir, new NoProcess(), TimeProvider.System)
            {
                GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
            };
            return CliApp.RunAsync(args, env);
        }
        (await CliAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString())).Should().Be(0, error.ToString());
        (await CliAsync("migrate", "report", h.RunId.ToString(), "--format", "md")).Should().Be(0, error.ToString());

        output.ToString().Should().Contain("""
            ## Mapped tables

            ### lead → xv_lead (3 rows)

            | Column | Note | Rows |
            |---|---|---:|
            | xv_source | value-defaulted | 2 |
            """.ReplaceLineEndings());
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
