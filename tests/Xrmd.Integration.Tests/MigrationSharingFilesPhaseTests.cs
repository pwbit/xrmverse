using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using SkiaSharp;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The sharing and files phases of an import (plan M4a task 5; spec §7.6 steps 6b–7): shares re-applied with their mask, shares to
/// principals outside the organization reported, note bodies over the organization's upload limit, file and image columns restored
/// from the package, and a resume that skips the files already written.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationSharingFilesPhaseTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m4a-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly Guid Root = Guid.NewGuid(), Ada = Guid.NewGuid();
    private static readonly DateTime Modified = new(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static LookupValue Ref(string table, Guid id) => new(table, id);

    private static PackageBuilder Security(PackageBuilder p) => p
        .Security("businessunit", "businessunitid", Record(Root, ("name", new StringValue("Contoso"))))
        .Security("systemuser", "systemuserid",
            Record(Ada, ("fullname", new StringValue("Ada Example")), ("domainname", new StringValue("ada@example.com")), ("businessunitid", Ref("businessunit", Root))));

    private static IMigrationPhase[] AllPhases =>
        [new SecurityPhase(), new RecordsPhase(), new RelationshipsPhase(), new DeferredLookupsPhase(), new SharingPhase(), new FilesPhase()];

    private static MigrationRecord Share(Guid id, Guid record, string table, Guid principal, int mask) => Record(id,
        ("objectid", new GuidValue(record)), ("objecttypecode", new StringValue(table)), ("principalid", new GuidValue(principal)),
        ("principaltypecode", new StringValue("systemuser")), ("accessrightsmask", new IntValue(mask)));

    [Fact]
    public async Task Share_reapplied_with_mask()
    {
        var account = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p)
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
            .Security("principalobjectaccess", "principalobjectaccessid", Share(Guid.NewGuid(), account, "account", Ada, 3))), null, AllPhases);

        (await h.ScalarAsync("SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @o AND principalid = @p", ("@o", account), ("@p", Ada)))
            .Should().Be(3);
        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
    }

    [Fact]
    public async Task Shares_read_chunk_by_chunk_and_resumed()
    {
        // One record per package chunk: the shares are read and written one chunk at a time, and a resume writes none twice.
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Build(_root, p => Security(p)
            .Table("account", "accountid", [Record(first, ("name", new StringValue("Fabrikam"))), Record(second, ("name", new StringValue("Contoso")))])
            .Security("principalobjectaccess", "principalobjectaccessid", Share(Guid.NewGuid(), first, "account", Ada, 1), Share(Guid.NewGuid(), second, "account", Ada, 3)),
            recordsPerChunk: 1);
        await h.RunAsync(package, null, AllPhases);
        await h.RunAsync(package, null, new SharingPhase());

        (await h.ScalarAsync("SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @o AND principalid = @p", ("@o", first), ("@p", Ada))).Should().Be(1);
        (await h.ScalarAsync("SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @o AND principalid = @p", ("@o", second), ("@p", Ada))).Should().Be(3);
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_chunk WHERE runid = @run AND entry LIKE 'sharing/security/principalobjectaccess/%'", ("@run", h.RunId)))
            .Should().Be(2);
    }

    [Fact]
    public async Task Share_to_missing_principal_reported()
    {
        Guid account = Guid.NewGuid(), stranger = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p)
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
            .Security("principalobjectaccess", "principalobjectaccessid", Share(Guid.NewGuid(), account, "account", stranger, 1))), null, AllPhases);

        (await h.ScalarAsync("SELECT COUNT(*) FROM data.principalobjectaccess WHERE objectid = @o", ("@o", account))).Should().Be(0);
        (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "share-missing" && i.RecordId == account);
    }

    [Fact]
    public async Task Share_with_create_right_applied_without_it()
    {
        // M6 run: Dataverse keeps Create (0x20) in some masks; a share cannot carry it, so the other rights are applied.
        var account = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p)
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
            .Security("principalobjectaccess", "principalobjectaccessid", Share(Guid.NewGuid(), account, "account", Ada, 0x20 | 0x3))), null, AllPhases);

        (await h.ScalarAsync("SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @o AND principalid = @p", ("@o", account), ("@p", Ada)))
            .Should().Be(3);
        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
    }

    [Fact]
    public async Task Share_of_table_without_owner_reported_not_failed()
    {
        // M6 run: Dataverse shares systemuser rows (a business-owned table); xrmverse shares only user or team owned tables.
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p)
            .Security("principalobjectaccess", "principalobjectaccessid", Share(Guid.NewGuid(), Ada, "systemuser", Ada, 1))), null, AllPhases);

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        issues.Should().ContainSingle(i => i.Code == "share-not-shareable" && i.Severity == MigrationIssue.Warning && i.RecordId == Ada);
    }

    [Fact]
    public async Task Note_body_restored()
    {
        Guid account = Guid.NewGuid(), note = Guid.NewGuid();
        var body = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => Security(p)
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
            .Table("annotation", "annotationid", [Record(note, ("subject", new StringValue("Contract")), ("filename", new StringValue("contract.pdf")),
                ("mimetype", new StringValue("application/pdf")), ("isdocument", new BoolValue(true)), ("objectid", Ref("account", account)))])
            .File(FilesPhase.NoteBodies + note, body)), null, AllPhases);

        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        var read = (RetrieveResponse)await admin.ExecuteAsync(new RetrieveRequest { Target = new EntityReference("annotation", note), ColumnSet = new ColumnSet("documentbody") });
        Convert.FromBase64String(read.Entity.GetAttributeValue<string>("documentbody")).Should().Equal(body);
        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
    }

    [Fact]
    public async Task File_column_and_image_restored()
    {
        Guid account = Guid.NewGuid();
        var document = RandomNumberGenerator.GetBytes(9 * 1024 * 1024 + 17); // three blocks
        var picture = Png();
        await using var h = MigrationPhaseHarness.Start(fx);
        await AddFileColumnsAsync(h);
        await h.RunAsync(Build(_root, p => Security(p)
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")),
                ("modifiedon", new DateTimeValue(Modified)), ("modifiedby", new LookupValue("systemuser", Ada)),
                ("new_doc", new FileValue($"files/account/new_doc/{account}", "plan.bin", "application/octet-stream", document.Length)),
                ("new_pic", new ImageValue($"files/account/new_pic/{account}")))])
            .File($"files/account/new_doc/{account}", document)
            .File($"files/account/new_pic/{account}", picture)), null, AllPhases);

        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
        (await h.ScalarAsync("SELECT filesizeinbytes FROM data.fileattachment WHERE objectid = @id AND regardingfieldname = 'new_doc'", ("@id", account)))
            .Should().Be((long)document.Length);
        (await h.ScalarAsync("SELECT filename FROM data.fileattachment WHERE objectid = @id AND regardingfieldname = 'new_doc'", ("@id", account))).Should().Be("plan.bin");
        (await h.ScalarAsync("SELECT new_picid FROM data.account WHERE accountid = @id", ("@id", account))).Should().NotBeNull();
        ((DateTime)(await h.ScalarAsync("SELECT modifiedon FROM data.account WHERE accountid = @id", ("@id", account)))!).Should().Be(Modified);
        (await h.ScalarAsync("SELECT modifiedby FROM data.account WHERE accountid = @id", ("@id", account))).Should().Be(Ada);
    }

    [Fact]
    public async Task Files_resume_skips_done()
    {
        Guid account = Guid.NewGuid(), note = Guid.NewGuid();
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Build(_root, p => Security(p)
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
            .Table("annotation", "annotationid", [Record(note, ("subject", new StringValue("Contract")), ("filename", new StringValue("a.txt")),
                ("mimetype", new StringValue("text/plain")), ("isdocument", new BoolValue(true)), ("objectid", Ref("account", account)))])
            .File(FilesPhase.NoteBodies + note, "first"u8.ToArray()));
        await h.RunAsync(package, null, AllPhases);
        // Change the body in the organization: a resumed files phase must not write it again.
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new UpdateRequest { Target = new Entity("annotation", note) { ["documentbody"] = Convert.ToBase64String("second"u8.ToArray()) } });
        await h.RunAsync(package, null, new FilesPhase());
        var read = (RetrieveResponse)await admin.ExecuteAsync(new RetrieveRequest { Target = new EntityReference("annotation", note), ColumnSet = new ColumnSet("documentbody") });
        Convert.FromBase64String(read.Entity.GetAttributeValue<string>("documentbody")).Should().Equal("second"u8.ToArray());
    }

    private async Task AddFileColumnsAsync(MigrationPhaseHarness h)
    {
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new CreateAttributeRequest { EntityName = "account", Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc", MaxSizeInKB = 32768 } });
        await admin.ExecuteAsync(new CreateAttributeRequest { EntityName = "account", Attribute = new ImageAttributeMetadata { LogicalName = "new_pic", SchemaName = "new_pic" } });
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(20, 20);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
