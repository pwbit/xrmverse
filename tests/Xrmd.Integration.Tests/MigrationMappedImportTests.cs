using System.Net;
using System.Text;
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
using Xrmd.Core;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// A mapped package through every import phase (migration table mapping spec §8.4, GAP-27 task 9): source tables xrmverse lacks
/// (<c>lead</c>, the activity <c>xs_close</c>, <c>xs_rival</c>, the N:N <c>leadcompetitors</c>) land in the tables of the target
/// solution, and every row that points at them (an account's lookup, activities regarding a lead, a party, a note, a share, a file)
/// follows. Invented names, each test on a database of its own.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationMappedImportTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-gap27-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal const string Map = """
        version: 1
        requires: { solution: xvmaptarget, minVersion: 1.0.0.0 }
        tables:
          - source: lead
            target: xv_lead
            columns:
              subject: xv_name
              xs_doc: xv_doc
              leadsourcecode: { target: xv_source, values: { 1: 100000000 }, fallback: 100000006 }
            state:
              - { from: [0, 1], to: [0, 1] }
              - { from: [2, 4], to: [1, 100000001] }
            stateFallback: { 2: [1, 100000001] }
          - source: xs_close
            target: xv_dealclose
            derived:
              xv_outcome: { from: regardingobjectid.statecode, values: { 0: 100000000, 2: 100000001 } }
          - source: xs_rival
            target: xv_rival
            columns:
              xs_name: xv_name
        relationships:
          - { source: leadcompetitors, target: xv_lead_xv_rival }
          - { source: leadrelated, target: xv_lead_xv_lead }
        """;

    /// <summary>The source tables as the export describes them; the custom status 100000002 of a disqualified lead is "Canceled".</summary>
    internal static SourceCatalog Catalog() => new(
    [
        new SourceCatalogTable("lead", "leadid", 4, "user", false,
        [
            new("subject", "String", [], false), new("companyname", "String", [], false), new("leadsourcecode", "Picklist", [], false),
            new("statecode", "State", [], false), new("statuscode", "Status", [], false), new("xs_doc", "Virtual", [], true),
        ]),
        new SourceCatalogTable("xs_close", "activityid", 10001, "user", true,
            [new("subject", "String", [], false), new("regardingobjectid", "Lookup", ["lead"], false)]),
        new SourceCatalogTable("xs_rival", "xs_rivalid", 10002, "organization", false, [new("xs_name", "String", [], true)]),
        new SourceCatalogTable("account", "accountid", 1, "user", false, [new("name", "String", [], false), new("new_leadid", "Lookup", ["lead"], true)]),
        CatalogTable("contact", "contactid", false, "user", ("lastname", "String", [])),
        new SourceCatalogTable("email", "activityid", 4202, "user", true,
            [new("subject", "String", [], false), new("regardingobjectid", "Lookup", ["lead", "account"], false), new("to", "PartyList", [], false)]),
    ],
    [
        new("lead", "statecode", 0, null, "Open"), new("lead", "statecode", 2, null, "Disqualified"),
        new("lead", "statuscode", 1, 0, "New"), new("lead", "statuscode", 4, 2, "Lost"), new("lead", "statuscode", 100000002, 2, "Canceled"),
    ]);

    private static readonly Guid Root = Guid.NewGuid(), Ada = Guid.NewGuid();

    private static LookupValue Ref(string table, Guid id) => new(table, id);

    private static MigrationRecord Lead(Guid id, string subject, int state, int status, int source, params (string, MigrationValue)[] more) => Record(id,
        [("subject", new StringValue(subject)), ("statecode", new OptionValue(state)), ("statuscode", new OptionValue(status)),
            ("leadsourcecode", new OptionValue(source)), .. more]);

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

    /// <summary>
    /// The target organization: the map target solution, the file column <c>xv_lead.xv_doc</c> and the lookup <c>account.new_leadid</c>
    /// to <c>xv_lead</c> (what the schema delta of a mapped export carries: its lookups to a mapped table name the target).
    /// </summary>
    private Task PrepareTargetAsync(MigrationPhaseHarness h) => PrepareTargetAsync(h, fx);

    /// <summary>As <see cref="PrepareTargetAsync(MigrationPhaseHarness)"/>, for the tests of other classes.</summary>
    internal static async Task PrepareTargetAsync(MigrationPhaseHarness h, XrmdFixture fx)
    {
        await InstallTargetSolutionAsync(h);
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = MappingTargetSolution.Lead,
            Attribute = new FileAttributeMetadata { LogicalName = "xv_doc", SchemaName = "xv_doc", MaxSizeInKB = 32768 },
        });
        await admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = "new_xv_lead_account_leadid", ReferencedEntity = MappingTargetSolution.Lead, ReferencingEntity = "account" },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_leadid", SchemaName = "new_leadid" },
        });
    }

    internal sealed record Ids(Guid Lead1, Guid Lead2, Guid Lead3, Guid Rival1, Guid Rival2, Guid Close1, Guid Close2, Guid Close3, Guid Close4,
        Guid Account, Guid Contact, Guid Email, Guid Task, Guid Note, byte[] Body, byte[] Document);

    internal static Ids NewIds() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Encoding.UTF8.GetBytes("The minutes of the first call."), Encoding.UTF8.GetBytes(new string('d', 5000)));

    private string Package(Ids x) => Package(_root, x);

    /// <summary>The package of these tests under <paramref name="root"/>, with what <paramref name="more"/> adds.</summary>
    internal static string Package(string root, Ids x, Action<PackageBuilder>? more = null) => Build(root, p => { p
        .Security("businessunit", "businessunitid", Record(Root, ("name", new StringValue("Contoso"))))
        .Security("systemuser", "systemuserid",
            Record(Ada, ("fullname", new StringValue("Ada Example")), ("domainname", new StringValue("ada@example.com")), ("businessunitid", Ref("businessunit", Root))))
        .Table("lead", "leadid",
        [
            Lead(x.Lead1, "Lead 1", 0, 1, 1, ("companyname", new StringValue("Example Ltd"))),
            Lead(x.Lead2, "Lead 2", 2, 100000002, 1),
            Lead(x.Lead3, "Lead 3", 0, 1, 7,
                ("xs_doc", new FileValue($"files/lead/xs_doc/{x.Lead3}", "plan.txt", "text/plain", x.Document.Length))),
        ])
        .File($"files/lead/xs_doc/{x.Lead3}", x.Document)
        .Table("xs_rival", "xs_rivalid",
            [Record(x.Rival1, ("xs_name", new StringValue("Rival 1"))), Record(x.Rival2, ("xs_name", new StringValue("Rival 2")))])
        .Table("xs_close", "activityid",
        [
            Record(x.Close1, ("subject", new StringValue("Close 1")), ("regardingobjectid", Ref("lead", x.Lead1))),
            Record(x.Close2, ("subject", new StringValue("Close 2")), ("regardingobjectid", Ref("lead", x.Lead2))),
            Record(x.Close3, ("subject", new StringValue("Close 3")), ("regardingobjectid", Ref("lead", x.Lead2))),
            Record(x.Close4, ("subject", new StringValue("Close 4"))),
        ])
        .Relationship("leadcompetitors",
        [
            Record(Guid.NewGuid(), ("leadid", new GuidValue(x.Lead1)), ("xs_rivalid", new GuidValue(x.Rival1))),
            Record(Guid.NewGuid(), ("leadid", new GuidValue(x.Lead2)), ("xs_rivalid", new GuidValue(x.Rival2))),
            Record(Guid.NewGuid(), ("leadid", new GuidValue(x.Lead3))), // an end the export could not read
        ])
        .Relationship("leadrelated", [Record(Guid.NewGuid(), ("leadidone", new GuidValue(x.Lead1)), ("leadidtwo", new GuidValue(x.Lead3)))])
        .Table("account", "accountid", [Record(x.Account, ("name", new StringValue("Fabrikam")), ("new_leadid", Ref("lead", x.Lead1)))])
        .Table("contact", "contactid", [Record(x.Contact, ("lastname", new StringValue("Example")))])
        .Table("email", "activityid",
        [
            Record(x.Email, ("subject", new StringValue("First mail")), ("regardingobjectid", Ref("lead", x.Lead1)),
                ("to", new PartyListValue([new Party(2, "lead", x.Lead1, null), new Party(2, null, null, "someone@example.com"), new Party(2, "contact", x.Contact, null)]))),
        ])
        .Table("task", "activityid", [Record(x.Task, ("subject", new StringValue("Follow up")), ("regardingobjectid", Ref("lead", x.Lead1)))])
        .Table("annotation", "annotationid",
        [
            Record(x.Note, ("subject", new StringValue("Minutes")), ("filename", new StringValue("minutes.txt")), ("mimetype", new StringValue("text/plain")),
                ("isdocument", new BoolValue(true)), ("objectid", Ref("lead", x.Lead1)), ("objecttypecode", new StringValue("lead"))),
        ])
        .File(FilesPhase.NoteBodies + x.Note, x.Body)
        .Security("principalobjectaccess", "principalobjectaccessid", Record(Guid.NewGuid(),
            ("objectid", new GuidValue(x.Lead1)), ("objecttypecode", new StringValue("lead")), ("principalid", new GuidValue(Ada)),
            ("principaltypecode", new StringValue("systemuser")), ("accessrightsmask", new IntValue(3))));
            more?.Invoke(p);
        },
        new PackageMapsPart([("sales", Map)], Catalog()));

    private static IMigrationPhase[] AllPhases =>
    [
        new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase(), new RelationshipsPhase(), new DeferredLookupsPhase(),
        new SharingPhase(), new FilesPhase(), new PostProcessingPhase(), new VerificationPhase(),
    ];

    private async Task<Entity> RetrieveAsync(MigrationPhaseHarness h, string table, Guid id, params string[] columns)
    {
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        return ((RetrieveResponse)await admin.ExecuteAsync(new RetrieveRequest { Target = new EntityReference(table, id), ColumnSet = new ColumnSet(columns) })).Entity;
    }

    private static int Option(Entity e, string column) => e.GetAttributeValue<OptionSetValue>(column).Value;

    /// <summary>What a mapped import must have written (spec §8.4).</summary>
    private async Task AssertImportedAsync(MigrationPhaseHarness h, Ids x)
    {
        // Same ids in the target table; status pairs and choices per the map.
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead WHERE xv_leadid IN (@a, @b, @c)", ("@a", x.Lead1), ("@b", x.Lead2), ("@c", x.Lead3))).Should().Be(3);
        var lead1 = await RetrieveAsync(h, "xv_lead", x.Lead1, "xv_name", "statecode", "statuscode", "xv_source");
        lead1.GetAttributeValue<string>("xv_name").Should().Be("Lead 1");
        (Option(lead1, "statecode"), Option(lead1, "statuscode"), Option(lead1, "xv_source")).Should().Be((0, 1, 100000000));
        var lead2 = await RetrieveAsync(h, "xv_lead", x.Lead2, "statecode", "statuscode");
        (Option(lead2, "statecode"), Option(lead2, "statuscode")).Should().Be((1, 100000004), "the custom status 'Canceled' is found by its label");
        var lead3 = await RetrieveAsync(h, "xv_lead", x.Lead3, "statecode", "statuscode", "xv_source");
        (Option(lead3, "statecode"), Option(lead3, "statuscode"), Option(lead3, "xv_source")).Should().Be((0, 1, 100000006));

        // The mapped activity: regarding the target lead, its outcome derived from the lead's state.
        foreach (var (close, outcome) in new[] { (x.Close1, (int?)100000000), (x.Close2, 100000001), (x.Close3, 100000001), (x.Close4, null) })
        {
            var e = await RetrieveAsync(h, "xv_dealclose", close, "regardingobjectid", "xv_outcome");
            ((int?)e.GetAttributeValue<OptionSetValue>("xv_outcome")?.Value).Should().Be(outcome);
            if (outcome is not null) e.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeEquivalentTo(new { LogicalName = "xv_lead", Id = close == x.Close1 ? x.Lead1 : x.Lead2 });
        }
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_rival WHERE xv_name IN ('Rival 1', 'Rival 2') AND xv_rivalid IN (@a, @b)", ("@a", x.Rival1), ("@b", x.Rival2)))
            .Should().Be(2);

        // Unmapped tables pointing at a mapped one.
        (await RetrieveAsync(h, "account", x.Account, "new_leadid")).GetAttributeValue<EntityReference>("new_leadid")
            .Should().BeEquivalentTo(new { LogicalName = "xv_lead", Id = x.Lead1 });
        (await RetrieveAsync(h, "task", x.Task, "regardingobjectid")).GetAttributeValue<EntityReference>("regardingobjectid")
            .Should().BeEquivalentTo(new { LogicalName = "xv_lead", Id = x.Lead1 });
        var email = await RetrieveAsync(h, "email", x.Email, "regardingobjectid", "to");
        email.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeEquivalentTo(new { LogicalName = "xv_lead", Id = x.Lead1 });
        var parties = email.GetAttributeValue<EntityCollection>("to").Entities;
        parties.Select(p => p.GetAttributeValue<EntityReference>("partyid")?.Id).Should().BeEquivalentTo(new Guid?[] { null, x.Contact },
            "the lead cannot be a party: only the address and the contact are kept");
        parties.Select(p => p.GetAttributeValue<string>("addressused")).Should().Contain("someone@example.com");
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id AND participationtypemask = 2", ("@id", x.Email))).Should().Be(2);

        var note = await RetrieveAsync(h, "annotation", x.Note, "objectid", "documentbody");
        note.GetAttributeValue<EntityReference>("objectid").Should().BeEquivalentTo(new { LogicalName = "xv_lead", Id = x.Lead1 });
        Convert.FromBase64String(note.GetAttributeValue<string>("documentbody")).Should().Equal(x.Body);

        (await h.ScalarAsync("SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @o AND principalid = @p", ("@o", x.Lead1), ("@p", Ada)))
            .Should().Be(3);

        var nn = (ManyToManyRelationshipMetadata)h.App.Services.GetRequiredService<IMetadataStore>().GetRelationship(MappingTargetSolution.LeadRival)!;
        foreach (var (lead, rival) in new[] { (x.Lead1, x.Rival1), (x.Lead2, x.Rival2) })
            (await h.ScalarAsync($"SELECT COUNT(*) FROM data.[{nn.IntersectEntityName}] WHERE [{nn.Entity1IntersectAttribute}] = @l AND [{nn.Entity2IntersectAttribute}] = @r",
                ("@l", lead), ("@r", rival))).Should().Be(1);
        (await h.ScalarAsync($"SELECT COUNT(*) FROM data.[{nn.IntersectEntityName}]")).Should().Be(2);
        // A relationship of the lead with itself: its pairs were exported under leadidone / leadidtwo.
        var self = (ManyToManyRelationshipMetadata)h.App.Services.GetRequiredService<IMetadataStore>().GetRelationship(MappingTargetSolution.LeadLead)!;
        (await h.ScalarAsync($"SELECT COUNT(*) FROM data.[{self.IntersectEntityName}] WHERE [{self.Entity1IntersectAttribute}] = @a AND [{self.Entity2IntersectAttribute}] = @b",
            ("@a", x.Lead1), ("@b", x.Lead3))).Should().Be(1);

        (await h.ScalarAsync("SELECT filesizeinbytes FROM data.fileattachment WHERE objectid = @id AND regardingfieldname = 'xv_doc'", ("@id", x.Lead3)))
            .Should().Be((long)x.Document.Length);
        (await h.ScalarAsync("SELECT filename FROM data.fileattachment WHERE objectid = @id AND regardingfieldname = 'xv_doc'", ("@id", x.Lead3))).Should().Be("plan.txt");
    }

    /// <summary>The census codes the data of <see cref="Package"/> triggers, by (table, column, code), with their row counts.</summary>
    internal static readonly (string Table, string? Column, string Code, string Message)[] CensusIssues =
    [
        ("xv_lead", null, "mapped-table", "lead → xv_lead: 3 rows"),
        ("xv_dealclose", null, "mapped-table", "xs_close → xv_dealclose: 4 rows"),
        ("xv_rival", null, "mapped-table", "xs_rival → xv_rival: 2 rows"),
        ("xv_lead", "companyname", "column-not-mapped", "1 row(s)"),
        ("xv_lead", "statuscode", "status-by-label", "1 row(s)"),
        ("xv_lead", "xv_source", "value-defaulted", "1 row(s)"),
        ("xv_dealclose", "xv_outcome", "derived-shared", "2 row(s)"),
        ("xv_dealclose", "xv_outcome", "derived-defaulted", "1 row(s)"),
        ("email", "to", "party-not-allowed", "1 row(s)"),
    ];

    private static async Task AssertReportAsync(MigrationPhaseHarness h, Guid runId)
    {
        var res = await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}/report");
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var report = JsonNode.Parse(text)!;
        var issues = report["issues"]!.AsArray().Select(i => i!).ToList();
        issues.Where(i => i["severity"]!.GetValue<string>() == MigrationIssue.Error).Select(i => i!.ToJsonString()).Should().BeEmpty();
        issues.Where(i => i["phase"]!.GetValue<int>() == VerifyPackagePhase.PhaseOrder)
            .Select(i => (i["table"]!.GetValue<string>(), i["column"]?.GetValue<string>(), i["code"]!.GetValue<string>(), i["message"]!.GetValue<string>()))
            .Should().BeEquivalentTo(CensusIssues);

        issues.Where(i => i["code"]!.GetValue<string>() == "nn-pair-unreadable")
            .Select(i => (i["severity"]!.GetValue<string>(), i["column"]!.GetValue<string>(), i["message"]!.GetValue<string>()))
            .Should().Equal((MigrationIssue.Warning, MappingTargetSolution.LeadRival, "1 row(s)"));

        var mapped = report["mappedTables"]!.AsArray().ToDictionary(t => t!["target"]!.GetValue<string>());
        mapped.Keys.Should().BeEquivalentTo("xv_lead", "xv_dealclose", "xv_rival");
        foreach (var (table, column, code, message) in CensusIssues.Where(c => c.Code != "mapped-table" && mapped.ContainsKey(c.Table)))
            mapped[table]!["notes"]!.AsArray().Select(n => n!.ToJsonString())
                .Should().Contain(new JsonObject { ["column"] = column, ["code"] = code, ["count"] = long.Parse(message.Split(' ')[0]) }.ToJsonString());
    }

    [Fact]
    public async Task Mapped_package_imports_end_to_end()
    {
        var x = NewIds();
        await using var h = MigrationPhaseHarness.Start(fx);
        await PrepareTargetAsync(h);

        var package = Package(x);

        await h.RunAsync(package, null, AllPhases);

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Code.StartsWith("verify-", StringComparison.Ordinal) && i.Severity == MigrationIssue.Error);
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        await AssertImportedAsync(h, x);
        issues.Should().Contain(i => i.Code == "verify-table" && i.Table == "xv_lead" && i.Message == "xv_lead: 3 exported, 3 present, 3 sampled");
        issues.Should().NotContain(i => i.Table == "lead" || i.Table == "xs_close" || i.Table == "xs_rival", "no phase works under a source name");
        await AssertReportAsync(h, h.RunId);

        // The verification read the file back under the target names: with the file gone, it reports it.
        await TestSql.ExecAsync(h.ConnectionString, "UPDATE data.xv_lead SET xv_doc = NULL WHERE xv_leadid = @id", ("@id", x.Lead3));
        await TestSql.ExecAsync(h.ConnectionString, "DELETE FROM meta.migration_chunk WHERE runid = @run AND entry = 'verification/files'", ("@run", h.RunId));
        await h.RunAsync(package, null, new VerificationPhase());
        (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "verify-file-missing")
            .Which.Should().Match<MigrationIssue>(i => i.Table == "xv_lead" && i.Column == "xv_doc" && i.RecordId == x.Lead3);
    }

    [Fact]
    public async Task Mapped_run_started_with_maps_imports_through_the_job()
    {
        // The Admin API path: the start request's maps are checked and kept, the job attaches them and runs every phase.
        var x = NewIds();
        await using var h = new MigrationPhaseHarness(fx.WithOwnDatabase(b => b.UseSetting("Pipeline:AsyncWorkerEnabled", "true")), fx);
        await PrepareTargetAsync(h);
        var package = await UploadAsync(h.App.CreateClient(), DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl), Package(x));

        var body = new JsonObject { ["passphrase"] = Passphrase, ["packageId"] = package.ToString(), ["maps"] = new JsonArray(Map) };
        var res = await h.SendAsync(HttpMethod.Post, "admin/migrate/runs", body.ToJsonString());
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, text);
        var runId = Guid.Parse(JsonNode.Parse(text)!["runId"]!.GetValue<string>());

        var status = await MigrationRunPolling.WaitSettledAsync(async () =>
            JsonNode.Parse(await (await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}")).Content.ReadAsStringAsync())!, TimeSpan.FromSeconds(180));
        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized, status["message"]?.ToString());

        await AssertImportedAsync(h, x);
        await AssertReportAsync(h, runId);
        var report = JsonNode.Parse(await (await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}/report")).Content.ReadAsStringAsync())!;
        var tables = report["tables"]!.AsArray().ToDictionary(t => t!["table"]!.GetValue<string>(), t => t!);
        foreach (var (table, rows) in new[] { ("xv_lead", 3), ("xv_dealclose", 4), ("xv_rival", 2), ("account", 1) })
            (tables[table]["exported"]!.GetValue<long>(), tables[table]["imported"]!.GetValue<long>(), tables[table]["failed"]!.GetValue<long>())
                .Should().Be((rows, rows, 0), table);
    }

    [Fact]
    public async Task Party_of_a_non_party_target_is_left_out_and_counted()
    {
        Guid lead = Guid.NewGuid(), contact = Guid.NewGuid(), call = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await InstallTargetSolutionAsync(h);
        const string map = """
            version: 1
            tables:
              - source: lead
                target: xv_lead
                columns: { subject: xv_name }
            """;
        var package = Build(_root, p => p
            .Security("businessunit", "businessunitid")
            .Table("lead", "leadid", [Record(lead, ("subject", new StringValue("Lead 1")))])
            .Table("contact", "contactid", [Record(contact, ("lastname", new StringValue("Example")))])
            .Table("phonecall", "activityid",
            [
                Record(call, ("subject", new StringValue("First call")),
                    ("to", new PartyListValue([new Party(2, "lead", lead, null), new Party(2, "contact", contact, null)]))),
            ]),
            new PackageMapsPart([("leads", map)], new SourceCatalog([CatalogTable("lead", "leadid", false, "user", ("subject", "String", []))], [])));

        await h.RunAsync(package, null, new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase());

        (await h.ScalarAsync("SELECT COUNT(*) FROM data.xv_lead WHERE xv_leadid = @id", ("@id", lead))).Should().Be(1);
        // The phone call's recipients (its owner is a party too, mask 9).
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id AND participationtypemask = 2", ("@id", call))).Should().Be(1);
        (await h.ScalarAsync("SELECT partyid FROM data.activityparty WHERE activityid = @id AND participationtypemask = 2", ("@id", call))).Should().Be(contact);
        var issues = await h.IssuesAsync();
        issues.Should().ContainSingle(i => i.Code == "party-not-allowed")
            .Which.Should().Be(new MigrationIssue(1, MigrationIssue.Warning, "party-not-allowed", "phonecall", null, "to", "1 row(s)"));
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        issues.Should().NotContain(i => i.Code == "lookup-nulled", "the party is left out by the map, not nulled as a missing record");

        var res = await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{h.RunId}/report");
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonNode.Parse(text)!["issues"]!.AsArray().Should().Contain(i => i!["code"]!.GetValue<string>() == "party-not-allowed" && i["table"]!.GetValue<string>() == "phonecall");
    }
}
