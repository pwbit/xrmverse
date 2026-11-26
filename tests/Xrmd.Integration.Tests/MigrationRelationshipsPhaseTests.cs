using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The N:N and deferred-lookups phases of an import (plan M4a task 4; spec §7.6 steps 5–6): pairs associated through the core,
/// a cycle between account and contact resolved after both tables are written, with the exported <c>modifiedon</c> kept.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationRelationshipsPhaseTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m4a-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime Modified = new(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static LookupValue Ref(string table, Guid id) => new(table, id);

    private static MigrationRecord Contact(Guid id, params (string, MigrationValue)[] more) =>
        Record(id, [("firstname", new StringValue("Ada")), ("lastname", new StringValue("Example")), .. more]);

    private static IMigrationPhase[] AllPhases => [new SecurityPhase(), new RecordsPhase(), new RelationshipsPhase(), new DeferredLookupsPhase()];

    [Fact]
    public async Task NN_pairs_associated_and_missing_endpoint_reported()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        const string schema = "new_account_contact_mig";
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata { SchemaName = schema, Entity1LogicalName = "account", Entity2LogicalName = "contact" },
            IntersectEntitySchemaName = schema,
        });
        var r = (ManyToManyRelationshipMetadata)h.App.Services.GetRequiredService<IMetadataStore>().GetRelationship(schema)!;
        Guid account = Guid.NewGuid(), contact = Guid.NewGuid(), stranger = Guid.NewGuid();
        var package = Build(_root, p => p
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
            .Table("contact", "contactid", [Contact(contact)])
            .Relationship(schema,
            [
                Record(Guid.NewGuid(), (r.Entity1IntersectAttribute!, new GuidValue(account)), (r.Entity2IntersectAttribute!, new GuidValue(contact))),
                Record(Guid.NewGuid(), (r.Entity1IntersectAttribute!, new GuidValue(account)), (r.Entity2IntersectAttribute!, new GuidValue(stranger))),
                Record(Guid.NewGuid(), (r.Entity1IntersectAttribute!, new GuidValue(account))), // an end that cannot be read
            ])
            .Relationship("new_invented_nn", []));
        await h.RunAsync(package, null, AllPhases);
        await h.RunAsync(package, null, new RelationshipsPhase()); // again: the pair is not added twice

        (await h.ScalarAsync($"SELECT COUNT(*) FROM data.[{r.IntersectEntityName}]")).Should().Be(1);
        (await h.ScalarAsync($"SELECT COUNT(*) FROM data.[{r.IntersectEntityName}] WHERE [{r.Entity1IntersectAttribute}] = @a AND [{r.Entity2IntersectAttribute}] = @c",
            ("@a", account), ("@c", contact))).Should().Be(1);
        var issues = await h.IssuesAsync();
        issues.Should().ContainSingle(i => i.Code == "nn-record-missing");
        issues.Should().ContainSingle(i => i.Code == "relationship-missing" && i.Column == "new_invented_nn");
        issues.Should().ContainSingle(i => i.Code == "nn-pair-unreadable", "counted once, though the phase ran twice")
            .Which.Should().Be(new MigrationIssue(RelationshipsPhase.PhaseOrder, MigrationIssue.Warning, "nn-pair-unreadable", r.IntersectEntityName, null, schema, "1 row(s)"));
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
    }

    [Fact]
    public async Task Cycle_resolves_through_deferred_lookups()
    {
        Guid account = Guid.NewGuid(), contact = Guid.NewGuid(), root = Guid.NewGuid(), ada = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p
            .Security("businessunit", "businessunitid", Record(root, ("name", new StringValue("Contoso"))))
            .Security("systemuser", "systemuserid", Record(ada, ("fullname", new StringValue("Ada Example")), ("domainname", new StringValue("ada@example.com")),
                ("businessunitid", Ref("businessunit", root))))
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")), ("primarycontactid", Ref("contact", contact)),
                ("modifiedon", new DateTimeValue(Modified)), ("modifiedby", Ref("systemuser", ada)))])
            .Table("contact", "contactid", [Contact(contact, ("parentcustomerid", Ref("account", account)), ("modifiedon", new DateTimeValue(Modified)),
                ("modifiedby", Ref("systemuser", ada)))])),
            null, AllPhases);

        (await h.ScalarAsync("SELECT primarycontactid FROM data.account WHERE accountid = @id", ("@id", account))).Should().Be(contact);
        (await h.ScalarAsync("SELECT parentcustomerid FROM data.contact WHERE contactid = @id", ("@id", contact))).Should().Be(account);
        ((DateTime)(await h.ScalarAsync("SELECT modifiedon FROM data.account WHERE accountid = @id", ("@id", account)))!).Should().Be(Modified);
        ((DateTime)(await h.ScalarAsync("SELECT modifiedon FROM data.contact WHERE contactid = @id", ("@id", contact)))!).Should().Be(Modified);
        (await h.ScalarAsync("SELECT modifiedby FROM data.account WHERE accountid = @id", ("@id", account))).Should().Be(ada);
        (await h.ScalarAsync("SELECT modifiedby FROM data.contact WHERE contactid = @id", ("@id", contact))).Should().Be(ada);
        (await h.IssuesAsync()).Where(i => i.Code != "user-local").Should().BeEmpty();
    }

    [Fact]
    public async Task Deferred_lookup_to_missing_record_is_nulled_and_counted()
    {
        // The account the contact's parent names is in the package's tables but not among its rows (the exporting user could not read it).
        Guid contact = Guid.NewGuid(), missing = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p
            .Table("contact", "contactid", [Contact(contact, ("parentcustomerid", Ref("account", missing)))])
            .Table("account", "accountid", [Record(Guid.NewGuid(), ("name", new StringValue("Other")))])), null, AllPhases);

        (await h.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE contactid = @id AND parentcustomerid IS NULL", ("@id", contact))).Should().Be(1);
        (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "lookup-nulled" && i.RecordId == contact && i.Column == "parentcustomerid"
            && i.Phase == DeferredLookupsPhase.PhaseOrder);
    }
}
