using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The verification phase of an import (plan M5 task 1; spec §7.6 step 10): every package record present, a sample compared field by
/// field after normalization, the files checked against the package's checksums; what differs is an error issue.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationVerificationTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m5-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly Guid Root = Guid.NewGuid(), Ada = Guid.NewGuid();

    private static LookupValue Ref(string table, Guid id) => new(table, id);

    private static PackageBuilder Security(PackageBuilder p) => p
        .Security("businessunit", "businessunitid", Record(Root, ("name", new StringValue("Contoso"))))
        .Security("systemuser", "systemuserid",
            Record(Ada, ("fullname", new StringValue("Ada Example")), ("domainname", new StringValue("ada@example.com")), ("businessunitid", Ref("businessunit", Root))));

    private static IMigrationPhase[] ImportPhases =>
        [new SecurityPhase(), new RecordsPhase(), new RelationshipsPhase(), new DeferredLookupsPhase(), new SharingPhase(), new FilesPhase()];

    private static IMigrationPhase[] AllPhases => [.. ImportPhases, new VerificationPhase()];

    private static readonly Guid[] Accounts = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    private static readonly Guid Contact = Guid.NewGuid(), Note = Guid.NewGuid();

    private string Package() => Build(_root, p => Security(p)
        .Table("account", "accountid", Accounts.Select((id, i) => Record(id, ("name", new StringValue($"Example {i}")), ("ownerid", Ref("systemuser", Ada)))))
        .Table("contact", "contactid", [Record(Contact, ("lastname", new StringValue("Example")), ("parentcustomerid", Ref("account", Accounts[0])))])
        .Table("annotation", "annotationid", [Record(Note, ("subject", new StringValue("Contract")), ("filename", new StringValue("a.txt")),
            ("mimetype", new StringValue("text/plain")), ("isdocument", new BoolValue(true)), ("objectid", Ref("account", Accounts[0])))])
        .File(FilesPhase.NoteBodies + Note, "the body"u8.ToArray()));

    private static List<MigrationIssue> Errors(IEnumerable<MigrationIssue> issues) => [.. issues.Where(i => i.Severity == MigrationIssue.Error)];

    [Fact]
    public async Task Clean_import_verifies_without_errors()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Package(), null, AllPhases);

        var issues = await h.IssuesAsync();
        Errors(issues).Should().BeEmpty();
        issues.Should().ContainSingle(i => i.Code == "verify-table" && i.Table == "account").Which.Message.Should().Be("account: 3 exported, 3 present, 3 sampled");
        issues.Should().ContainSingle(i => i.Code == "verify-table" && i.Table == "contact");
    }

    [Fact]
    public async Task Empty_column_the_organization_fills_is_not_a_mismatch()
    {
        // M6 run: the export writes empty columns as null; the core sets the base currency and the caller on create, as Dataverse does.
        var account = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p).Table("account", "accountid",
            [Record(account, ("name", new StringValue("Fabrikam")), ("ownerid", Ref("systemuser", Ada)),
                ("transactioncurrencyid", NullValue.Instance), ("modifiedby", NullValue.Instance), ("createdby", NullValue.Instance))])), null, AllPhases);

        Errors(await h.IssuesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleted_record_is_reported_missing()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package();
        await h.RunAsync(package, null, ImportPhases);
        await h.ScalarAsync("DELETE FROM data.contact WHERE contactid = @id", ("@id", Contact));
        await h.RunAsync(package, null, new VerificationPhase());

        Errors(await h.IssuesAsync()).Should().ContainSingle().Which.Should().Match<MigrationIssue>(i => i.Code == "verify-missing" && i.RecordId == Contact);
    }

    [Fact]
    public async Task Changed_value_is_reported()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package();
        await h.RunAsync(package, null, ImportPhases);
        await h.ScalarAsync("UPDATE data.account SET name = N'Changed' WHERE accountid = @id", ("@id", Accounts[1]));
        await h.RunAsync(package, null, new VerificationPhase());

        var error = Errors(await h.IssuesAsync()).Should().ContainSingle().Subject;
        error.Code.Should().Be("verify-mismatch");
        error.Column.Should().Be("name");
        error.Message.Should().Contain("Example 1").And.Contain("Changed");
    }

    [Fact]
    public async Task Damaged_file_is_reported()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package();
        await h.RunAsync(package, null, ImportPhases);
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new UpdateRequest { Target = new Entity("annotation", Note) { ["documentbody"] = Convert.ToBase64String("another body"u8.ToArray()) } });
        await h.RunAsync(package, null, new VerificationPhase());

        Errors(await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "verify-file-mismatch" && i.RecordId == Note);
    }

    [Fact]
    public async Task Presence_checked_in_batches()
    {
        var ids = Enumerable.Range(0, 2500).Select(_ => Guid.NewGuid()).ToList();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p).Table("account", "accountid", ids.Select((id, i) => Record(id, ("name", new StringValue($"Bulk {i}")))))),
            new Dictionary<string, string> { ["verifySample"] = "5" }, AllPhases);

        var issues = await h.IssuesAsync();
        Errors(issues).Should().BeEmpty();
        issues.Should().ContainSingle(i => i.Code == "verify-table" && i.Table == "account").Which.Message.Should().Be("account: 2500 exported, 2500 present, 5 sampled");
    }

    [Fact]
    public async Task Sampled_values_compare_after_normalization()
    {
        var account = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "account",
            Attribute = new MultiSelectPicklistAttributeMetadata
            {
                LogicalName = "new_tags", SchemaName = "new_tags",
                OptionSet = new OptionSetMetadata([new OptionMetadata(new Label("One", 1033), 1), new OptionMetadata(new Label("Two", 1033), 2),
                    new OptionMetadata(new Label("Three", 1033), 3)]) { IsGlobal = false, OptionSetType = OptionSetType.Picklist },
            },
        });
        await h.RunAsync(Build(_root, p => Security(p).Table("account", "accountid", [Record(account, ("name", new StringValue("Normalized")),
            ("overriddencreatedon", new DateTimeValue(new DateTime(2020, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc))),
            ("revenue", new MoneyValue(12.345678m)),
            ("new_tags", new OptionsValue([3, 1, 2])),
            ("owningbusinessunit", Ref("businessunit", Root)),
            ("ownerid", Ref("systemuser", Ada)))])), null, AllPhases);

        (await h.IssuesAsync()).Where(i => i.Code == "verify-mismatch").Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_row_is_not_reported_missing()
    {
        Guid good = Guid.NewGuid(), bad = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p).Table("account", "accountid", [
            Record(good, ("name", new StringValue("Good"))),
            Record(bad, ("name", new StringValue(new string('x', 500))))])), null, AllPhases);

        var errors = Errors(await h.IssuesAsync());
        errors.Should().ContainSingle(i => i.RecordId == bad).Which.Code.Should().NotStartWith("verify-");
        errors.Should().NotContain(i => i.Code.StartsWith("verify-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Row_mapped_onto_an_existing_one_is_not_compared()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var code = (string)(await h.ScalarAsync("SELECT c.isocurrencycode FROM data.organization o JOIN data.transactioncurrency c ON c.transactioncurrencyid = o.basecurrencyid"))!;
        await h.RunAsync(Build(_root, p => Security(p).Table("transactioncurrency", "transactioncurrencyid", [Record(Guid.NewGuid(),
            ("isocurrencycode", new StringValue(code)), ("currencyname", new StringValue("Source currency name")), ("exchangerate", new DecimalValue(1m)),
            ("overriddencreatedon", new DateTimeValue(new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc))))])), null, AllPhases);

        var issues = await h.IssuesAsync();
        Errors(issues).Should().BeEmpty();
        issues.Should().ContainSingle(i => i.Code == "verify-table" && i.Table == "transactioncurrency").Which.Message.Should().Be("transactioncurrency: 1 exported, 1 present, 0 sampled");
    }
}
