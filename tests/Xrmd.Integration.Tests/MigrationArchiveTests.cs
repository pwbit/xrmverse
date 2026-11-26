using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Core;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;
using Xrmd.Migration.Source;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Archive tables at import (migration table mapping spec §8.7, GAP-27 task 16): the standard tables the target lacks whose rows the
/// export kept (archive candidates) are listed by a dry run; those a map marks <c>archive: true</c> are created at the start of the
/// schema phase as <c>xrmva_</c> tables of the solution <c>XrmverseMigrationArchive</c> and filled as they are. Invented names and
/// labels, each test on a database of its own.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationArchiveTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-gap27-archive-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The map a dry run's choice of <c>quote</c> writes (GAP-27 task 15).</summary>
    private static readonly string ArchiveQuote = ArchiveMapWriter.Append(null, ["quote"], Catalog(), new Dictionary<string, long> { ["quote"] = 2 });

    private sealed record Ids(Guid Quote1, Guid Quote2, Guid Invoice, Guid Letter, Guid Account, Guid Note, Guid Deal, Guid Deal2);

    private static Ids NewIds() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    /// <summary>
    /// A quote's lookup to an opportunity as the export writes it for an archive candidate (<see cref="ValueMapper.Map"/> with lookup names):
    /// the value and its name, the source's formatted value or, without one, the id.
    /// </summary>
    private static (string, MigrationValue)[] Exported(Guid quote, Guid deal, string? name)
    {
        var table = new SourceTable("quote", "quotes", "quoteid", false, false, false,
            [new SourceColumn("quoteid", "Uniqueidentifier", null, [], false, true), new SourceColumn("opportunityid", "Lookup", null, ["opportunity"], false, true)]);
        var formatted = name is null ? "" : $",\"_opportunityid_value@OData.Community.Display.V1.FormattedValue\":\"{name}\"";
        using var row = JsonDocument.Parse($"{{\"quoteid\":\"{quote}\",\"_opportunityid_value\":\"{deal}\"{formatted}}}");
        return [.. ValueMapper.Map(table, row.RootElement, (_, _) => null, lookupNames: true).Values.Select(v => (v.Key, v.Value))];
    }

    private static SourceCatalogColumn Column(string name, string type, string? label = null, params string[] targets) =>
        new(name, type, targets, false, label);

    /// <summary>The source as the export describes it: display names and option labels are the customer's own (invented here).</summary>
    private static SourceCatalog Catalog() => new(
    [
        new SourceCatalogTable("account", "accountid", 1, "user", false, [Column("accountid", "Uniqueidentifier"), Column("name", "String", "Name")]),
        new SourceCatalogTable("quote", "quoteid", 1084, "user", false,
        [
            Column("quoteid", "Uniqueidentifier"), Column("name", "String", "Offer name") with { MaxLength = 300 },
            Column("ownerid", "Owner", "Owner", "systemuser", "team"), Column("statecode", "State", "State"), Column("statuscode", "Status", "Status reason"),
            Column("paymenttermscode", "Picklist", "Terms"), Column("totalamount", "Money", "Total") with { Precision = 2, MinValue = 0, MaxValue = 1_000_000_000 },
            Column("totalamount_base", "Money", "Total (base)"), Column("effectivefrom", "DateTime", "Valid from") with { DateBehavior = "DateOnly", Format = "DateOnly" },
            Column("description", "Memo", "Notes") with { MaxLength = 2000 }, Column("customerid", "Customer", "Customer", "account", "contact"),
            Column("opportunityid", "Lookup", "Deal", "opportunity"), Column("new_progettoid", "Lookup", "Project", "new_progetto"),
        ], "Offer"),
        new SourceCatalogTable("invoice", "invoiceid", 1090, "user", false, [Column("invoiceid", "Uniqueidentifier"), Column("name", "String", "Name")], "Bill"),
        new SourceCatalogTable("letter", "activityid", 4207, "user", true,
        [
            Column("activityid", "Uniqueidentifier"), Column("subject", "String", "Subject") with { MaxLength = 200 }, Column("address", "String", "Address"),
            Column("regardingobjectid", "Lookup", "Regarding", "account"), Column("to", "PartyList", "To"), Column("statecode", "State", "State"),
            Column("statuscode", "Status", "Status reason"), Column("activitytypecode", "EntityName"),
        ], "Paper mail"),
    ],
    [
        new("quote", "statecode", 0, null, "Draft"), new("quote", "statecode", 1, null, "Active"), new("quote", "statecode", 2, null, "Won"),
        new("quote", "statuscode", 1, 0, "In progress"), new("quote", "statuscode", 3, 1, "Open"), new("quote", "statuscode", 4, 2, "Won"),
        new("quote", "paymenttermscode", 1, null, "Net 30"), new("quote", "paymenttermscode", 2, null, "Net 60"),
        new("letter", "statecode", 0, null, "Open"), new("letter", "statecode", 1, null, "Done"),
        new("letter", "statuscode", 1, 0, "Open"), new("letter", "statuscode", 4, 1, "Posted"),
    ]);

    /// <summary>
    /// A package without maps whose candidates are <c>quote</c> (2 rows: choice, status, money, a customer lookup to account and a lookup
    /// to an opportunity the package does not hold), <c>invoice</c> and the activity <c>letter</c>, with a note on a quote.
    /// </summary>
    private string Package(Ids x) => Build(_root, p => p
        .Security("businessunit", "businessunitid")
        .Table("account", "accountid", [Record(x.Account, ("name", new StringValue("Fabrikam")))])
        .Candidate("quote", "quoteid",
        [
            Record(x.Quote1, [("name", new StringValue("Offer 1")), ("statecode", new OptionValue(2)), ("statuscode", new OptionValue(4)),
                ("paymenttermscode", new OptionValue(2)), ("totalamount", new MoneyValue(1234.50m)), ("totalamount_base", new MoneyValue(1234.50m)),
                ("effectivefrom", new DateOnlyValue(new DateOnly(2026, 3, 1))), ("description", new StringValue("Two items")),
                ("customerid", new LookupValue("account", x.Account)), .. Exported(x.Quote1, x.Deal, "Example deal")]),
            Record(x.Quote2, [("name", new StringValue("Offer 2")), ("statecode", new OptionValue(0)), ("statuscode", new OptionValue(1)),
                ("customerid", new LookupValue("account", x.Account)), .. Exported(x.Quote2, x.Deal2, null)]),
        ])
        .Candidate("invoice", "invoiceid", [Record(x.Invoice, ("name", new StringValue("Bill 1")))])
        .Candidate("letter", "activityid",
        [
            Record(x.Letter, ("subject", new StringValue("Letter 1")), ("address", new StringValue("1 Example Road")),
                ("regardingobjectid", new LookupValue("account", x.Account)), ("to", new PartyListValue([new Party(2, "account", x.Account, null)])),
                ("statecode", new OptionValue(1)), ("statuscode", new OptionValue(4)), ("activitytypecode", new StringValue("letter"))),
        ])
        .Table("annotation", "annotationid",
        [
            Record(x.Note, ("subject", new StringValue("About the offer")), ("notetext", new StringValue("Call back in March.")),
                ("objectid", new LookupValue("quote", x.Quote1)), ("objecttypecode", new StringValue("quote"))),
        ]),
        new PackageMapsPart([], Catalog()));

    private static Dictionary<string, string> Options(string map) => new() { [MigrationMaps.OptionName] = MigrationMaps.ToOption([map]) };

    private static IMigrationPhase[] AllPhases =>
    [
        new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase(), new RelationshipsPhase(), new DeferredLookupsPhase(),
        new SharingPhase(), new FilesPhase(), new PostProcessingPhase(), new VerificationPhase(),
    ];

    private OrganizationService Admin(MigrationPhaseHarness h) => TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);

    private async Task<Entity> RetrieveAsync(MigrationPhaseHarness h, string table, Guid id, params string[] columns) =>
        ((RetrieveResponse)await Admin(h).ExecuteAsync(new RetrieveRequest { Target = new EntityReference(table, id), ColumnSet = new ColumnSet(columns) })).Entity;

    private static IMetadataStore Store(MigrationPhaseHarness h) => h.App.Services.GetRequiredService<IMetadataStore>();

    private static async Task<long> CountAsync(MigrationPhaseHarness h, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToInt64(await h.ScalarAsync(sql, parameters));

    private static IEnumerable<(int Value, string Label)> OptionsOf(AttributeMetadata attribute) =>
        ((EnumAttributeMetadata)attribute).OptionSet.Options.Select(o => (o.Value!.Value, o.Label.LocalizedLabels.Single(l => l.LanguageCode == 1033).Label));

    private static string LabelOf(Label label) => label.LocalizedLabels.Single(l => l.LanguageCode == 1033).Label;

    // ---- dry run --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dry_run_lists_candidates_and_creates_nothing()
    {
        await using var h = new MigrationPhaseHarness(fx.WithOwnDatabase(b => b.UseSetting("Pipeline:AsyncWorkerEnabled", "true")), fx);
        var package = await UploadAsync(h.App.CreateClient(), DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl), Package(NewIds()));
        var solutions = await CountAsync(h, "SELECT COUNT(*) FROM data.solution");

        async Task<JsonObject> DryRunAsync(string? map)
        {
            var body = new JsonObject { ["passphrase"] = Passphrase, ["packageId"] = package.ToString(), ["options"] = new JsonObject { ["dryRun"] = true } };
            if (map is not null) body["maps"] = new JsonArray(map);
            var res = await h.SendAsync(HttpMethod.Post, "admin/migrate/runs", body.ToJsonString());
            var text = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.Accepted, text);
            var runId = JsonNode.Parse(text)!["runId"]!.GetValue<string>();
            var until = DateTime.UtcNow.AddSeconds(180);
            JsonNode status;
            do
            {
                await Task.Delay(250);
                status = JsonNode.Parse(await (await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}")).Content.ReadAsStringAsync())!;
            }
            while (status["state"]!.GetValue<string>() is MigrationRunStates.Queued or MigrationRunStates.Running && DateTime.UtcNow < until);
            status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Completed, status["message"]?.ToString());
            return JsonNode.Parse(await (await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}/report")).Content.ReadAsStringAsync())!.AsObject();
        }

        static List<(string Severity, string? Table, string Message)> Candidates(JsonObject report) =>
        [
            .. report["issues"]!.AsArray().Where(i => i!["code"]!.GetValue<string>() == "archive-candidate")
                .Select(i => (i!["severity"]!.GetValue<string>(), i["table"]?.GetValue<string>(), i["message"]!.GetValue<string>())),
        ];

        // Without a map: every candidate, with its rows.
        var plain = await DryRunAsync(null);
        Candidates(plain).Should().BeEquivalentTo(new (string, string?, string)[]
        {
            (MigrationIssue.Info, "quote", "2 row(s)"), (MigrationIssue.Info, "invoice", "1 row(s)"), (MigrationIssue.Info, "letter", "1 row(s)"),
        });
        plain["archiveCandidates"]!.AsArray().Select(c => (c!["table"]!.GetValue<string>(), c["rows"]!.GetValue<long>()))
            .Should().BeEquivalentTo(new[] { ("quote", 2L), ("invoice", 1L), ("letter", 1L) });

        // With quote archived: quote is a choice made, and the real run would create it (not a missing table).
        var archived = await DryRunAsync(ArchiveQuote);
        Candidates(archived).Select(c => c.Table).Should().BeEquivalentTo("invoice", "letter");
        archived["issues"]!.AsArray().Should().NotContain(i => i!["code"]!.GetValue<string>() == "dry-run-table-missing"
            && (i["table"]!.GetValue<string>() == "xrmva_quote" || i["table"]!.GetValue<string>() == "quote"));
        archived["issues"]!.AsArray().Should().NotContain(i => i!["severity"]!.GetValue<string>() == MigrationIssue.Error);

        Store(h).GetEntity("xrmva_quote").Should().BeNull("a dry run creates nothing");
        (await CountAsync(h, "SELECT COUNT(*) FROM data.solution")).Should().Be(solutions);
        (await CountAsync(h, "SELECT COUNT(*) FROM data.publisher WHERE customizationprefix = 'xrmva'")).Should().Be(0);
    }

    // ---- the real run -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Archive_table_is_created_and_filled()
    {
        var x = NewIds();
        await using var h = MigrationPhaseHarness.Start(fx);

        await h.RunAsync(Package(x), Options(ArchiveQuote), AllPhases);

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        var table = Store(h).GetEntity("xrmva_quote")!;
        table.Should().NotBeNull();
        table.IsActivity.Should().NotBe(true);
        LabelOf(table.DisplayName).Should().Be("Offer", "the source's display name");

        // The table is in the archive solution, of the xrmverse archive publisher.
        (await CountAsync(h, """
            SELECT COUNT(*) FROM data.solutioncomponent c JOIN data.solution s ON s.solutionid = c.solutionid
            WHERE s.uniquename = 'XrmverseMigrationArchive' AND c.objectid = @id
            """, ("@id", table.MetadataId!.Value))).Should().Be(1);
        var publisher = (await TestSql.RowsAsync(h.ConnectionString, """
            SELECT p.customizationprefix, p.customizationoptionvalueprefix, p.friendlyname FROM data.publisher p
            JOIN data.solution s ON s.publisherid = p.publisherid WHERE s.uniquename = 'XrmverseMigrationArchive'
            """)).Single();
        (publisher[0], Convert.ToInt32(publisher[1]), publisher[2]).Should().Be(("xrmva", 72000, "xrmverse archive"));

        // Same ids; values as they are, choices and statuses with the source's values and labels.
        (await CountAsync(h, "SELECT COUNT(*) FROM data.xrmva_quote WHERE xrmva_quoteid IN (@a, @b)", ("@a", x.Quote1), ("@b", x.Quote2))).Should().Be(2);
        var quote1 = await RetrieveAsync(h, "xrmva_quote", x.Quote1, "xrmva_name", "xrmva_statecode", "xrmva_statuscode", "xrmva_paymenttermscode",
            "xrmva_totalamount", "xrmva_description", "xrmva_customerid", "xrmva_opportunityidname");
        quote1.GetAttributeValue<string>("xrmva_name").Should().Be("Offer 1");
        quote1.GetAttributeValue<OptionSetValue>("xrmva_statecode").Value.Should().Be(2);
        quote1.GetAttributeValue<OptionSetValue>("xrmva_statuscode").Value.Should().Be(4);
        quote1.GetAttributeValue<OptionSetValue>("xrmva_paymenttermscode").Value.Should().Be(2);
        quote1.GetAttributeValue<Money>("xrmva_totalamount").Value.Should().Be(1234.50m);
        quote1.GetAttributeValue<string>("xrmva_description").Should().Be("Two items");
        quote1.GetAttributeValue<EntityReference>("xrmva_customerid").Should().BeEquivalentTo(new { LogicalName = "account", Id = x.Account });
        quote1.GetAttributeValue<string>("xrmva_opportunityidname").Should().Be("Example deal", "the lookup to a table the target lacks is kept as text");
        (await RetrieveAsync(h, "xrmva_quote", x.Quote2, "xrmva_opportunityidname")).GetAttributeValue<string>("xrmva_opportunityidname")
            .Should().Be(x.Deal2.ToString("D"), "the source gave no name: the referenced id stands in");

        var attributes = table.Attributes.ToDictionary(a => a.LogicalName);
        OptionsOf(attributes["xrmva_statuscode"]).Should().Equal((1, "In progress"), (3, "Open"), (4, "Won"));
        OptionsOf(attributes["xrmva_statecode"]).Should().Equal((0, "Draft"), (1, "Active"), (2, "Won"));
        OptionsOf(attributes["xrmva_paymenttermscode"]).Should().Equal((1, "Net 30"), (2, "Net 60"));
        LabelOf(attributes["xrmva_paymenttermscode"].DisplayName).Should().Be("Terms");
        attributes["xrmva_name"].Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(300);
        attributes["xrmva_opportunityidname"].Should().BeOfType<StringAttributeMetadata>();
        attributes["xrmva_effectivefrom"].Should().BeOfType<DateTimeAttributeMetadata>();
        attributes.Should().NotContainKey("xrmva_opportunityid", "its table is not in the target");

        // A note on a quote stays attached: its objectid follows the archive table (R5).
        (await RetrieveAsync(h, "annotation", x.Note, "objectid")).GetAttributeValue<EntityReference>("objectid")
            .Should().BeEquivalentTo(new { LogicalName = "xrmva_quote", Id = x.Quote1 });

        // One main form listing the columns, one active view.
        var form = (string)(await h.ScalarAsync("SELECT formxml FROM data.systemform WHERE objecttypecode = 'xrmva_quote' AND type = 2"))!;
        form.Should().Contain("datafieldname=\"xrmva_totalamount\"").And.Contain("datafieldname=\"xrmva_customerid\"")
            .And.Contain("datafieldname=\"xrmva_opportunityidname\"").And.NotContain("datafieldname=\"xrmva_opportunityid\"");
        (await CountAsync(h, "SELECT COUNT(*) FROM data.savedquery WHERE returnedtypecode = 'xrmva_quote' AND querytype = 0")).Should().Be(1);

        issues.Should().ContainSingle(i => i.Code == "archive-created").Which.Table.Should().Be("xrmva_quote");
        issues.Should().Contain(new MigrationIssue(VerifyPackagePhase.PhaseOrder, MigrationIssue.Warning, "archive-lookup-as-text", "xrmva_quote", null,
            "xrmva_opportunityidname", "2 row(s)"));
        issues.Should().Contain(i => i.Code == "verify-table" && i.Table == "xrmva_quote" && i.Message.StartsWith("xrmva_quote: 2 exported, 2 present"));
    }

    [Fact]
    public async Task Lookup_of_a_customer_solution_to_an_archived_table_points_to_the_archive()
    {
        // R5 and R11 (spec §8.3): the solution's lookup account.new_quoteid to quote is retargeted in the XML to the archive table,
        // which exists by then (it is created before the solutions), and the account's value follows.
        var x = NewIds();
        await using var h = MigrationPhaseHarness.Start(fx);
        var solution = MigrationSolutionMapTests.SolutionFile(false,
            [MigrationSolutionMapTests.AccountNode(MigrationSolutionMapTests.Attribute("new_quoteid", "lookup", "Offer", custom: true))],
            [MigrationSolutionMapTests.OneToMany("new_quote_account_quoteid", referenced: "quote", referencing: "account", "new_quoteid")],
            [(1, "account", 2)]);
        var package = Build(_root, p => p
            .Solution("xvcust", solution)
            .Security("businessunit", "businessunitid")
            .Table("account", "accountid", [Record(x.Account, ("name", new StringValue("Fabrikam")), ("new_quoteid", new LookupValue("quote", x.Quote1)))])
            .Candidate("quote", "quoteid", [Record(x.Quote1, ("name", new StringValue("Offer 1")))]),
            new PackageMapsPart([], Catalog()));

        await h.RunAsync(package, Options(ArchiveQuote), AllPhases);

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        Store(h).GetEntity("account")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "new_quoteid").Targets
            .Should().Equal("xrmva_quote");
        (await RetrieveAsync(h, "account", x.Account, "new_quoteid")).GetAttributeValue<EntityReference>("new_quoteid")
            .Should().BeEquivalentTo(new { LogicalName = "xrmva_quote", Id = x.Quote1 });
    }

    [Fact]
    public async Task Lookup_to_a_table_of_the_package_solution_keeps_the_id()
    {
        // The customer's solution brings new_progetto; the archive's lookup to it is created after the solutions (spec §8.7), and the
        // census of phase 1 already counts it as kept, as the schema phase decides.
        var x = NewIds();
        var project = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        var solution = MigrationSolutionMapTests.SolutionFile(false, [MigrationSolutionMapTests.Progetto()], [], [(1, "new_progetto", 0)]);
        var package = Build(_root, p => p
            .Solution("xvcust", solution)
            .Security("businessunit", "businessunitid")
            .Table("new_progetto", "new_progettoid", [Record(project, ("new_name", new StringValue("Example project")))])
            .Candidate("quote", "quoteid", [Record(x.Quote1, ("name", new StringValue("Offer 1")), ("new_progettoid", new LookupValue("new_progetto", project)))]),
            new PackageMapsPart([], Catalog()));

        await h.RunAsync(package, Options(ArchiveQuote), AllPhases);

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        issues.Should().NotContain(i => i.Code == "archive-lookup-as-text" && i.Column == "xrmva_new_progettoidname");
        var attributes = Store(h).GetEntity("xrmva_quote")!.Attributes;
        attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "xrmva_new_progettoid").Targets.Should().Equal("new_progetto");
        attributes.Should().NotContain(a => a.LogicalName == "xrmva_new_progettoidname");
        (await RetrieveAsync(h, "xrmva_quote", x.Quote1, "xrmva_new_progettoid")).GetAttributeValue<EntityReference>("xrmva_new_progettoid")
            .Should().BeEquivalentTo(new { LogicalName = "new_progetto", Id = project });
        (await new MigrationRunStore(h.App.Services.GetRequiredService<IDbConnectionFactory>())
            .IsChunkDoneAsync(h.RunId, ArchiveSchema.LookupsChunkKey("xrmva_quote"), default)).Should().BeTrue();
    }

    [Fact]
    public async Task Deferred_lookup_whose_table_is_not_created_becomes_text_on_the_form_too()
    {
        // The delta lists new_progetto, so the archive waits for it; its item cannot be created (no metadata), so the lookup falls back
        // to the text column after the delta, and the main form shows that column instead of the lookup that was never made.
        var x = NewIds();
        await using var h = MigrationPhaseHarness.Start(fx);
        var delta = new JsonObject { ["tables"] = new JsonArray(new JsonObject { ["table"] = "new_progetto" }) };
        var package = Build(_root, p => p
            .File("schema/delta.json", System.Text.Encoding.UTF8.GetBytes(delta.ToJsonString()))
            .Security("businessunit", "businessunitid")
            .Candidate("quote", "quoteid", [Record(x.Quote1, ("name", new StringValue("Offer 1")))]),
            new PackageMapsPart([], Catalog()));

        await h.RunAsync(package, Options(ArchiveQuote), AllPhases);

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        issues.Should().Contain(i => i.Code == "archive-lookup-as-text" && i.Column == "xrmva_new_progettoidname" && i.Phase == SchemaPhase.PhaseOrder);
        var attributes = Store(h).GetEntity("xrmva_quote")!.Attributes;
        attributes.Should().Contain(a => a.LogicalName == "xrmva_new_progettoidname").And.NotContain(a => a.LogicalName == "xrmva_new_progettoid");
        var form = (string)(await h.ScalarAsync("SELECT formxml FROM data.systemform WHERE objecttypecode = 'xrmva_quote' AND type = 2"))!;
        form.Should().Contain("datafieldname=\"xrmva_new_progettoidname\"").And.NotContain("datafieldname=\"xrmva_new_progettoid\"");
        (await CountAsync(h, "SELECT COUNT(*) FROM data.systemform WHERE objecttypecode = 'xrmva_quote'")).Should().Be(1);
    }

    [Fact]
    public async Task Renamed_archive_column_that_clashes_fails_the_map_check()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var map = "version: 1\ntables:\n  - source: quote\n    archive: true\n    columns: { name: xrmva_description }\n";
        var act = () => h.RunAsync(Package(NewIds()), Options(map), new VerifyPackagePhase());

        (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message
            .Should().Contain("column 'xrmva_quote.xrmva_description' is the archive column of 'quote.name' and 'quote.description'");
    }

    [Fact]
    public async Task Unchosen_candidate_is_not_created()
    {
        await using var h = MigrationPhaseHarness.Start(fx);

        await h.RunAsync(Package(NewIds()), Options(ArchiveQuote), AllPhases);

        Store(h).GetEntity("xrmva_quote").Should().NotBeNull();
        Store(h).GetEntity("xrmva_invoice").Should().BeNull();
        Store(h).GetEntity("xrmva_letter").Should().BeNull();
        var issues = await h.IssuesAsync();
        issues.Should().Contain(i => i.Code == "table-missing" && i.Table == "invoice" && i.Message.Contains("archive candidate") && i.Message.Contains("archive: true"),
            "its rows are not imported and the report says how to keep them");
        issues.Should().Contain(i => i.Code == "table-missing" && i.Table == "letter");
    }

    [Fact]
    public async Task Archive_target_that_exists_fails_the_map_check()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package(NewIds());
        await h.RunAsync(package, Options(ArchiveQuote), new VerifyPackagePhase(), new SchemaPhase());
        Store(h).GetEntity("xrmva_quote").Should().NotBeNull();

        // Another run on the same organization (the first one closed): the archive table exists (not from this run), and account is no candidate.
        await TestSql.ExecAsync(h.ConnectionString, "UPDATE meta.migration_run SET state = @state WHERE runid = @run",
            ("@state", MigrationRunStates.Finalized), ("@run", h.RunId));
        var other = new MigrationPhaseHarness(h.App, fx);
        var map = ArchiveQuote + "  - { source: account, archive: true }\n";
        var act = () => other.RunAsync(package, Options(map), new VerifyPackagePhase());

        var failure = (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message;
        failure.Should().StartWith("The map does not fit this organization: ")
            .And.Contain("table 'xrmva_quote' (archive of 'quote') already exists")
            .And.Contain("table 'account' is not an archive candidate of the package");
        (await other.IssuesAsync()).Where(i => i.Code == "map-check").Should().HaveCount(2);
    }

    [Fact]
    public async Task Resume_does_not_create_the_archive_table_twice()
    {
        var x = NewIds();
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package(x);
        await h.RunAsync(package, Options(ArchiveQuote), new VerifyPackagePhase(), new SchemaPhase());
        var tables = Store(h).GetAllEntities().Count;

        // The resume runs phase 1 again (the map check accepts the table this run created) and the schema phase again.
        await h.RunAsync(package, Options(ArchiveQuote), AllPhases);

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        Store(h).GetAllEntities().Count.Should().Be(tables);
        issues.Where(i => i.Code == "archive-created").Should().ContainSingle();
        (await CountAsync(h, "SELECT COUNT(*) FROM data.systemform WHERE objecttypecode = 'xrmva_quote'")).Should().Be(1);
        (await CountAsync(h, "SELECT COUNT(*) FROM data.savedquery WHERE returnedtypecode = 'xrmva_quote'")).Should().Be(1);
        (await CountAsync(h, "SELECT COUNT(*) FROM data.solution WHERE uniquename = 'XrmverseMigrationArchive'")).Should().Be(1);
        (await CountAsync(h, "SELECT COUNT(*) FROM data.xrmva_quote")).Should().Be(2);
    }

    [Fact]
    public async Task Activity_source_becomes_an_activity_archive()
    {
        var x = NewIds();
        await using var h = MigrationPhaseHarness.Start(fx);

        await h.RunAsync(Package(x), Options("version: 1\ntables:\n  - { source: letter, archive: true }\n"), AllPhases);

        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
        var table = Store(h).GetEntity("xrmva_letter")!;
        table.Should().NotBeNull();
        table.IsActivity.Should().BeTrue();
        LabelOf(table.DisplayName).Should().Be("Paper mail");
        var letter = await RetrieveAsync(h, "xrmva_letter", x.Letter, "subject", "xrmva_address", "regardingobjectid", "xrmva_statuscode", "to");
        letter.GetAttributeValue<string>("subject").Should().Be("Letter 1");
        letter.GetAttributeValue<string>("xrmva_address").Should().Be("1 Example Road");
        letter.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeEquivalentTo(new { LogicalName = "account", Id = x.Account });
        letter.GetAttributeValue<OptionSetValue>("xrmva_statuscode").Value.Should().Be(4);
        OptionsOf(table.Attributes.Single(a => a.LogicalName == "xrmva_statuscode")).Should().Equal((1, "Open"), (4, "Posted"));
        // The activity's own state follows the source (completed), with that state's default status; the source's exact pair stays in xrmva_*.
        var state = await RetrieveAsync(h, "xrmva_letter", x.Letter, "statecode", "statuscode");
        state.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1, "a completed letter is not shown open");
        state.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(new MetadataMapTarget(Store(h)).DefaultStatusOf("xrmva_letter", 1));
        letter.GetAttributeValue<EntityCollection>("to").Entities.Select(p => p.GetAttributeValue<EntityReference>("partyid")?.Id).Should().Equal(x.Account);
    }
}
