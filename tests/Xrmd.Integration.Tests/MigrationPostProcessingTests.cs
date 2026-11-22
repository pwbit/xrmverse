using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Admin.Migration.Import;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The post-processing phase of an import (plan M4b task 3; spec §7.6 step 9): autonumber sequences above the imported values, and
/// the caches read again so that what was imported works straight away.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationPostProcessingTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m4b-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Autonumber_seed_above_imported_values()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "account",
            Attribute = new StringAttributeMetadata { SchemaName = "new_mignum", LogicalName = "new_mignum", MaxLength = 100, AutoNumberFormat = "MIG-{SEQNUM:5}-{RANDSTRING:2}" },
        });
        await h.RunAsync(Build(_root, p => p.Table("account", "accountid",
        [
            Record(Guid.NewGuid(), ("name", new StringValue("A")), ("new_mignum", new StringValue("MIG-00042-X1"))),
            Record(Guid.NewGuid(), ("name", new StringValue("B")), ("new_mignum", new StringValue("MIG-01500-QZ"))),
            Record(Guid.NewGuid(), ("name", new StringValue("C")), ("new_mignum", new StringValue("legacy 99999"))),
        ])), null, new RecordsPhase(), new PostProcessingPhase());

        var id = await admin.CreateAsync(new Entity("account") { ["name"] = "After the run" });
        var created = await admin.RetrieveAsync("account", id, new ColumnSet("new_mignum"));
        created.GetAttributeValue<string>("new_mignum").Should().StartWith("MIG-01501-");

        // Never lowered: a second run with lower values keeps the sequence where it is.
        await h.RunAsync(Build(_root, p => p.Table("account", "accountid", [Record(Guid.NewGuid(), ("name", new StringValue("D")), ("new_mignum", new StringValue("MIG-00001-AA")))])),
            null, new RecordsPhase());
        await h.ScalarAsync("DELETE FROM meta.migration_chunk WHERE runid = @run AND entry LIKE 'post/%'", ("@run", h.RunId));
        await h.RunAsync(Build(_root, p => p.Table("account", "accountid", [])), null, new PostProcessingPhase());
        var next = await admin.CreateAsync(new Entity("account") { ["name"] = "Later" });
        (await admin.RetrieveAsync("account", next, new ColumnSet("new_mignum"))).GetAttributeValue<string>("new_mignum").Should().StartWith("MIG-01502-");
    }

    [Fact]
    public async Task Security_cache_sees_imported_roles()
    {
        Guid root = Guid.NewGuid(), ada = Guid.NewGuid(), oid = Guid.NewGuid(), role = Guid.NewGuid(), account = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        // The organization reads the security tables once before the import (the cache is warm).
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.RetrieveMultipleAsync(new QueryExpression("account") { ColumnSet = new ColumnSet("name"), TopCount = 1 });

        await h.RunAsync(Build(_root, p => p
            .Security("businessunit", "businessunitid", Record(root, ("name", new StringValue("Contoso"))))
            .Security("systemuser", "systemuserid", Record(ada, ("fullname", new StringValue("Ada Example")), ("domainname", new StringValue("ada@example.com")),
                ("azureactivedirectoryobjectid", new GuidValue(oid)), ("businessunitid", new LookupValue("businessunit", root))))
            .Security("role", "roleid", Record(role, ("name", new StringValue("System Administrator")),
                ("roletemplateid", new LookupValue("role", new Guid("627090ff-40a3-4053-8790-584edc5be201")))))
            .Security("systemuserroles", "systemuserroleid", Record(Guid.NewGuid(), ("systemuserid", new GuidValue(ada)), ("roleid", new GuidValue(role))))
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])),
            null, new SecurityPhase(), new RecordsPhase(), new PostProcessingPhase());

        var asAda = TestServices.NewOrganizationService(h.App.Services, oid);
        var read = await asAda.RetrieveAsync("account", account, new ColumnSet("name"));
        read.GetAttributeValue<string>("name").Should().Be("Fabrikam");
    }
}
