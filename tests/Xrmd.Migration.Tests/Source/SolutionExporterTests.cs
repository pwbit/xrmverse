using System.Text.Json;
using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public sealed class SolutionExporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static DataverseClient Client(FakeDataverse fake) => new(fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask);

    private static SourceSolution Solution(string name, bool managed, string prefix = "xv", string version = "1.0.0.0") =>
        new(Guid.NewGuid(), name, version, managed, prefix, prefix + "publisher");

    private static FakeDataverse WithExport(byte[] zip) => new FakeDataverse().On(HttpMethod.Post, "ExportSolution$",
        (_, _) => FakeDataverse.Json(JsonSerializer.Serialize(new { ExportSolutionFile = Convert.ToBase64String(zip) })));

    private async Task<byte[]> Entry(string folder, string path)
    {
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        using var s = reader.OpenEntry(path);
        using var m = new MemoryStream();
        await s.CopyToAsync(m);
        return m.ToArray();
    }

    [Fact]
    public async Task Lists_visible_solutions_without_system_ones()
    {
        var fake = new FakeDataverse().GetJson(@"^/api/data/v9\.2/solutions\?", """
            {"value":[
              {"solutionid":"00000000-0000-0000-0000-000000000001","uniquename":"Default","version":"1.0","ismanaged":false,"publisherid":{"uniquename":"DefaultPublisher","customizationprefix":"new"}},
              {"solutionid":"00000000-0000-0000-0000-000000000002","uniquename":"xv_core","version":"1.2.0.0","ismanaged":false,"publisherid":{"uniquename":"xvpublisher","customizationprefix":"xv"}},
              {"solutionid":"00000000-0000-0000-0000-000000000003","uniquename":"Active","version":"1.0","ismanaged":false,"publisherid":{"uniquename":"MicrosoftCorporation","customizationprefix":""}}
            ]}
            """);
        using var client = Client(fake);
        var list = await SolutionExporter.ListAsync(client, CancellationToken.None);
        list.Should().ContainSingle().Which.Should().Be(new SourceSolution(Guid.Parse("00000000-0000-0000-0000-000000000002"), "xv_core", "1.2.0.0", false, "xv", "xvpublisher"));
        fake.Requests.Single().Url.Should().Contain("$orderby=createdon asc").And.Contain("isvisible eq true");
    }

    [Fact]
    public async Task Exports_unmanaged_with_ExportSolution_body()
    {
        var zip = SolutionZip.Build("xv_core", "1.0.0.0", false);
        var fake = WithExport(zip);
        var folder = Path.Combine(_root, "pkg");
        IReadOnlyList<PackageSolution> exported;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(fake))
        {
            exported = await SolutionExporter.ExportAsync(client, writer, [Solution("xv_core", false)], new Dictionary<string, string>(), _ => { }, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        exported.Should().Equal(new PackageSolution(1, "xv_core", "1.0.0.0", false, "solutions/1-xv_core.zip"));
        using var body = JsonDocument.Parse(fake.Requests.Single().Body!);
        body.RootElement.GetProperty("SolutionName").GetString().Should().Be("xv_core");
        body.RootElement.GetProperty("Managed").GetBoolean().Should().BeFalse();
        (await Entry(folder, "solutions/1-xv_core.zip")).Should().Equal(zip);
    }

    [Fact]
    public async Task Managed_solution_comes_from_the_given_file()
    {
        var file = Path.Combine(_root, "isv_app_managed.zip");
        var zip = SolutionZip.Build("isv_app", "2.0.0.1", true);
        await File.WriteAllBytesAsync(file, zip);
        var fake = WithExport(SolutionZip.Build("xv_core", "1.0.0.0", false));
        var folder = Path.Combine(_root, "pkg");
        IReadOnlyList<PackageSolution> exported;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(fake))
        {
            exported = await SolutionExporter.ExportAsync(client, writer, [Solution("isv_app", true, "isv", "2.0.0.1"), Solution("xv_core", false)],
                new Dictionary<string, string> { ["isv_app"] = file }, _ => { }, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        exported.Select(s => s.Path).Should().Equal("solutions/1-isv_app.zip", "solutions/2-xv_core.zip");
        exported[0].Managed.Should().BeTrue();
        (await Entry(folder, "solutions/1-isv_app.zip")).Should().Equal(zip);
    }

    [Fact]
    public async Task Managed_without_file_stops_before_downloads()
    {
        var fake = WithExport(SolutionZip.Build("xv_core", "1.0.0.0", false));
        using var writer = PackageWriter.Create(Path.Combine(_root, "pkg"), SyntheticPackage.Passphrase);
        using var client = Client(fake);
        var act = () => SolutionExporter.ExportAsync(client, writer,
            [Solution("xv_core", false), Solution("isv_one", true, "isv"), Solution("isv_two", true, "isv")], new Dictionary<string, string>(), _ => { }, CancellationToken.None);
        (await act.Should().ThrowAsync<MigrationExportException>()).Which.Message.Should().Contain("isv_one").And.Contain("isv_two").And.Contain("--solution-file");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Managed_file_version_mismatch_is_an_error()
    {
        var file = Path.Combine(_root, "isv_app.zip");
        await File.WriteAllBytesAsync(file, SolutionZip.Build("isv_app", "1.9.0.0", true));
        using var writer = PackageWriter.Create(Path.Combine(_root, "pkg"), SyntheticPackage.Passphrase);
        using var client = Client(new FakeDataverse());
        var act = () => SolutionExporter.ExportAsync(client, writer, [Solution("isv_app", true, "isv", "2.0.0.1")],
            new Dictionary<string, string> { ["isv_app"] = file }, _ => { }, CancellationToken.None);
        (await act.Should().ThrowAsync<MigrationExportException>()).Which.Message.Should().Contain("1.9.0.0").And.Contain("2.0.0.1");
    }

    [Fact]
    public async Task Microsoft_solution_is_skipped_with_warning()
    {
        var fake = WithExport(SolutionZip.Build("xv_core", "1.0.0.0", false));
        var warnings = new List<PackageWarning>();
        using var writer = PackageWriter.Create(Path.Combine(_root, "pkg"), SyntheticPackage.Passphrase);
        using var client = Client(fake);
        var exported = await SolutionExporter.ExportAsync(client, writer, [Solution("msdynce_Sales", true, "msdynce"), Solution("xv_core", false)],
            new Dictionary<string, string>(), warnings.Add, CancellationToken.None);
        exported.Should().ContainSingle().Which.Should().Be(new PackageSolution(1, "xv_core", "1.0.0.0", false, "solutions/1-xv_core.zip"));
        warnings.Should().ContainSingle().Which.Code.Should().Be("microsoft-solution");
        warnings[0].Message.Should().Contain("msdynce_Sales");
    }
}

public sealed class DefaultOnlyComponentsTests
{
    [Fact]
    public async Task Default_only_form_becomes_warning()
    {
        var core = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
        var fake = new FakeDataverse()
            .GetJson(@"^/api/data/v9\.2/solutioncomponents\?.*_solutionid_value eq 00000000-0000-0000-0000-0000000000c1", """
                {"value":[{"objectid":"00000000-0000-0000-0000-00000000f001"}]}
                """)
            .GetJson(@"^/api/data/v9\.2/systemforms\?.*ismanaged eq false", """
                {"value":[
                  {"formid":"00000000-0000-0000-0000-00000000f001","name":"Project main","objecttypecode":"xv_project"},
                  {"formid":"00000000-0000-0000-0000-00000000f002","name":"Account quick","objecttypecode":"account"}
                ]}
                """)
            .GetJson(@"^/api/data/v9\.2/workflows\?", """
                {"value":[{"workflowid":"00000000-0000-0000-0000-00000000b001","name":"Require phone","category":2,"primaryentity":"contact"}]}
                """)
            .GetJson(@"^/api/data/v9\.2/(savedqueries|savedqueryvisualizations|roles)\?", """{"value":[]}""");
        using var client = new DataverseClient(fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask);
        var warnings = await DefaultOnlyComponents.FindAsync(client, [core], CancellationToken.None);
        warnings.Should().OnlyContain(w => w.Code == "default-solution-component");
        warnings.Select(w => w.Message).Should().HaveCount(2)
            .And.Contain(m => m.Contains("form 'Account quick' on account"))
            .And.Contain(m => m.Contains("business rule 'Require phone' on contact"));
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }
}
