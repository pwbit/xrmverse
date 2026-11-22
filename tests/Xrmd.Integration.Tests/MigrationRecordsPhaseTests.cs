using FluentAssertions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The records phase of an import (plan M4a task 3; spec §7.5, §7.6 step 4): records keep their ids, system columns and state, are
/// written in dependency order in sub-chunks of 1000, a bad row is isolated, lookups outside the package are nulled and counted, and
/// a resume skips the committed sub-chunks. Synthetic package, invented names, each test on a database of its own.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationRecordsPhaseTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m4a-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly Guid Root = Guid.NewGuid(), Child = Guid.NewGuid(), Ada = Guid.NewGuid();
    private static readonly DateTime Created = new(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc), Modified = new(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static LookupValue Ref(string table, Guid id) => new(table, id);

    /// <summary>A root and a child business unit and one user in the child.</summary>
    private static MigrationPackageBuilderSteps Security => p => p
        .Security("businessunit", "businessunitid",
            Record(Root, ("name", new StringValue("Contoso"))),
            Record(Child, ("name", new StringValue("Contoso Sales")), ("parentbusinessunitid", Ref("businessunit", Root))))
        .Security("systemuser", "systemuserid",
            Record(Ada, ("fullname", new StringValue("Ada Example")), ("domainname", new StringValue("ada@example.com")),
                ("businessunitid", Ref("businessunit", Child))));

    private delegate PackageBuilder MigrationPackageBuilderSteps(PackageBuilder p);

    private string Package(Action<PackageBuilder> data) => Build(_root, p => data(Security(p)));

    private static MigrationRecord Contact(Guid id, string firstName, params (string, MigrationValue)[] more) =>
        Record(id, [("firstname", new StringValue(firstName)), ("lastname", new StringValue("Example")), .. more]);

    private async Task<MigrationPhaseHarness> ImportAsync(string package, Dictionary<string, string>? options = null)
    {
        var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(package, options, new SecurityPhase(), new RecordsPhase());
        return h;
    }

    [Fact]
    public async Task Records_keep_ids_and_system_columns()
    {
        var account = Guid.NewGuid();
        await using var h = await ImportAsync(Package(p => p.Table("account", "accountid",
        [
            Record(account, ("name", new StringValue("Fabrikam")), ("createdon", new DateTimeValue(Created)), ("modifiedon", new DateTimeValue(Modified)),
                ("createdby", Ref("systemuser", Ada)), ("modifiedby", Ref("systemuser", Ada)), ("ownerid", Ref("systemuser", Ada)),
                ("owningbusinessunit", Ref("businessunit", Child))),
        ])));
        async Task<object?> Column(string c) => await h.ScalarAsync($"SELECT {c} FROM data.account WHERE accountid = @id", ("@id", account));
        (await Column("name")).Should().Be("Fabrikam");
        ((DateTime)(await Column("createdon"))!).Should().Be(Created);
        ((DateTime)(await Column("modifiedon"))!).Should().Be(Modified);
        (await Column("createdby")).Should().Be(Ada);
        (await Column("modifiedby")).Should().Be(Ada);
        (await Column("ownerid")).Should().Be(Ada);
        (await Column("owningbusinessunit")).Should().Be(Child);
    }

    [Fact]
    public async Task Inactive_account_and_completed_task_keep_state()
    {
        Guid account = Guid.NewGuid(), task = Guid.NewGuid();
        await using var h = await ImportAsync(Package(p => p
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Closed")), ("statecode", new OptionValue(1)), ("statuscode", new OptionValue(2)))])
            .Table("task", "activityid", [Record(task, ("subject", new StringValue("Call back")), ("statecode", new OptionValue(1)), ("statuscode", new OptionValue(5)),
                ("regardingobjectid", Ref("account", account)))])));
        (await h.ScalarAsync("SELECT statecode FROM data.account WHERE accountid = @id", ("@id", account))).Should().Be(1);
        (await h.ScalarAsync("SELECT statuscode FROM data.account WHERE accountid = @id", ("@id", account))).Should().Be(2);
        (await h.ScalarAsync("SELECT statecode FROM data.task WHERE activityid = @id", ("@id", task))).Should().Be(1);
        (await h.ScalarAsync("SELECT statuscode FROM data.task WHERE activityid = @id", ("@id", task))).Should().Be(5);
        (await h.ScalarAsync("SELECT regardingobjectid FROM data.task WHERE activityid = @id", ("@id", task))).Should().Be(account);
    }

    [Fact]
    public async Task Activity_parties_written_and_missing_party_reported()
    {
        Guid contact = Guid.NewGuid(), call = Guid.NewGuid(), stranger = Guid.NewGuid();
        await using var h = await ImportAsync(Package(p => p
            .Table("contact", "contactid", [Contact(contact, "Ada")])
            .Table("phonecall", "activityid",
            [
                Record(call, ("subject", new StringValue("Intro")),
                    ("from", new PartyListValue([new Party(1, "systemuser", Ada, null)])),
                    ("to", new PartyListValue([new Party(2, "contact", contact, null), new Party(2, "lead", stranger, null)]))),
            ])));
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id AND partyid = @p", ("@id", call), ("@p", contact))).Should().Be(1);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id AND partyid = @p", ("@id", call), ("@p", Ada))).Should().Be(1);
        (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "party-dropped" && i.RecordId == call && i.Column == "to");
    }

    [Fact]
    public async Task Lookup_outside_package_is_nulled_and_counted()
    {
        Guid contact = Guid.NewGuid(), missing = Guid.NewGuid();
        var package = Package(p => p.Table("contact", "contactid", [Contact(contact, "Ada", ("parentcustomerid", Ref("account", missing)))]));
        await using (var h = await ImportAsync(package))
        {
            (await h.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE contactid = @id AND parentcustomerid IS NULL", ("@id", contact))).Should().Be(1);
            (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "lookup-nulled" && i.RecordId == contact && i.Column == "parentcustomerid"
                && i.Severity == MigrationIssue.Warning);
        }

        await using var strict = await ImportAsync(package, new Dictionary<string, string> { ["strictLookups"] = "true" });
        (await strict.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE contactid = @id", ("@id", contact))).Should().Be(0);
        (await strict.IssuesAsync()).Should().ContainSingle(i => i.Severity == MigrationIssue.Error && i.RecordId == contact && i.Code == "0x80040217");
    }

    [Fact]
    public async Task Guid_value_written_to_single_table_lookup()
    {
        // M6 run: Dataverse types owningbusinessunit of organization-owned tables as Uniqueidentifier; xrmverse as a lookup.
        var account = Guid.NewGuid();
        await using var h = await ImportAsync(Package(p => p.Table("account", "accountid",
            [Record(account, ("name", new StringValue("Fabrikam")), ("ownerid", Ref("systemuser", Ada)), ("owningbusinessunit", new GuidValue(Child)))])));
        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
        (await h.ScalarAsync("SELECT owningbusinessunit FROM data.account WHERE accountid = @id", ("@id", account))).Should().Be(Child);
    }

    [Fact]
    public async Task Object_type_code_number_written_as_table_name()
    {
        // M6 run: Dataverse types userentityuisettings.objecttypecode as a number; xrmverse as the table's logical name.
        var settings = Guid.NewGuid();
        await using var h = await ImportAsync(Package(p => p.Table("userentityuisettings", "userentityuisettingsid",
            [Record(settings, ("objecttypecode", new IntValue(1)), ("ownerid", Ref("systemuser", Ada)))])));
        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
        (await h.ScalarAsync("SELECT objecttypecode FROM data.userentityuisettings WHERE userentityuisettingsid = @id", ("@id", settings)))
            .Should().Be("account");
    }

    [Fact]
    public async Task Unknown_object_type_code_reported()
    {
        var settings = Guid.NewGuid();
        await using var h = await ImportAsync(Package(p => p.Table("userentityuisettings", "userentityuisettingsid",
            [Record(settings, ("objecttypecode", new IntValue(10574)), ("ownerid", Ref("systemuser", Ada)))])));
        (await h.IssuesAsync()).Should().ContainSingle(i => i.Severity == MigrationIssue.Error && i.RecordId == settings)
            .Which.Message.Should().Contain("10574");
    }

    [Fact]
    public async Task Column_missing_in_target_is_dropped_and_reported()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        await using var h = await ImportAsync(Package(p => p.Table("contact", "contactid",
            ids.Select(id => Contact(id, "Ada", ("new_invented", new StringValue("x")))))));
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.contact")).Should().Be(3);
        (await h.IssuesAsync()).Where(i => i.Code == "column-missing").Should().ContainSingle()
            .Which.Should().Match<MigrationIssue>(i => i.Table == "contact" && i.Column == "new_invented");
    }

    [Fact]
    public async Task Bad_row_is_isolated_and_reported()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToList();
        var bad = ids[613];
        await using var h = await ImportAsync(Package(p => p.Table("contact", "contactid",
            ids.Select(id => Contact(id, id == bad ? new string('n', 5000) : "Ada")))));
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.contact")).Should().Be(999);
        var errors = (await h.IssuesAsync()).Where(i => i.Severity == MigrationIssue.Error).ToList();
        errors.Should().ContainSingle().Which.RecordId.Should().Be(bad);
        errors[0].Code.Should().StartWith("0x8004");
        (await h.ScalarAsync("SELECT failedrows FROM meta.migration_chunk WHERE runid = @run AND entry = 'data/contact/00001.jsonl.gz#0'", ("@run", h.RunId)))
            .Should().Be(1);
    }

    [Fact]
    public async Task Resume_mid_records_skips_committed_subchunks()
    {
        var ids = Enumerable.Range(0, 1500).Select(_ => Guid.NewGuid()).ToList();
        var package = Package(p => p.Table("contact", "contactid", ids.Select(id => Contact(id, "Ada"))));

        // Sub-chunk 0 recorded as committed before the run: its rows are never written.
        await using (var h = MigrationPhaseHarness.Start(fx))
        {
            await h.RunAsync(package, null, new SecurityPhase());
            await h.ScalarAsync("INSERT INTO meta.migration_chunk (runid, entry, phase, rows, failedrows, committedon) " +
                "VALUES (@run, 'data/contact/00001.jsonl.gz#0', 4, 1000, 0, SYSUTCDATETIME())", ("@run", h.RunId));
            await h.RunAsync(package, null, new RecordsPhase());
            (await h.ScalarAsync("SELECT COUNT(*) FROM data.contact")).Should().Be(500);
            (await h.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE contactid = @id", ("@id", ids[0]))).Should().Be(0);
        }

        // A crash after sub-chunk 0 but before its mark: the phase runs again and writes nothing twice.
        await using var again = await ImportAsync(package);
        await again.ScalarAsync("DELETE FROM meta.migration_chunk WHERE runid = @run AND entry LIKE 'data/%'", ("@run", again.RunId));
        await again.RunAsync(package, null, new RecordsPhase());
        (await again.ScalarAsync("SELECT COUNT(*) FROM data.contact")).Should().Be(1500);
        (await again.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
    }

    [Fact]
    public async Task Tables_in_dependency_order_and_forward_lookup_deferred()
    {
        Guid account = Guid.NewGuid(), contact = Guid.NewGuid();
        await using var h = await ImportAsync(Package(p => p
            .Table("contact", "contactid", [Contact(contact, "Ada", ("parentcustomerid", Ref("account", account)))])
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")), ("primarycontactid", Ref("contact", contact)))])));
        var deferred = new List<DeferredLookup>();
        await foreach (var d in h.Ledger.DeferredAsync(default)) deferred.Add(d);
        // account → contact and contact → account: one of the two is written, the other deferred.
        // A count is int on SQL Server, bigint on PostgreSQL.
        (Convert.ToInt64(await h.ScalarAsync("SELECT COUNT(*) FROM data.account WHERE primarycontactid = @c", ("@c", contact))) == 1
            ^ Convert.ToInt64(await h.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE parentcustomerid = @a", ("@a", account))) == 1).Should().BeTrue();
        deferred.Should().ContainSingle();
        TableOrder.Sort(["contact", "account", "task"], t => t switch
        {
            "task" => [("regardingobjectid", "account")],
            "account" => [("primarycontactid", "contact")],
            _ => [],
        }).Should().Equal("contact", "account", "task");
    }
}
