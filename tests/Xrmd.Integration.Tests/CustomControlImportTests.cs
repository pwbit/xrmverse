using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1d, task 2 (rulings R3 and R4): the solution import writes each <c>CustomControl</c> as a <c>customcontrol</c> row
/// (component type 66) with the files of its <c>Controls/</c> folder, a re-import replaces them, a managed uninstall removes them,
/// and the export writes the node and the files back byte for byte. The controls are written here by hand.
/// </summary>
[Collection("solution-import")]
public sealed class CustomControlImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XvCustomControls";
    private const string Gauge = "xv_Xv.Gauge";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private ICustomControlFiles Files => fx.App.Services.GetRequiredService<ICustomControlFiles>();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    // ---- the solution file ------------------------------------------------------------------------------------------

    private static string Manifest(string constructor, string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <manifest>
          <control namespace="Xv" constructor="{constructor}" version="{version}" display-name-key="Name" control-type="standard">
            <property name="value" display-name-key="Value" of-type="Decimal" usage="bound" required="true" />
            <resources>
              <code path="bundle.js" order="1" />
              <resx path="strings/{constructor}.1033.resx" version="1.0.0" />
            </resources>
          </control>
        </manifest>
        """;

    /// <summary>The files of a control written for the test: its manifest, a bundle with <paramref name="bundle"/> and a resx.</summary>
    private static Dictionary<string, byte[]> ControlFiles(string name, string version = "1.0.0", string bundle = "var v = 1;")
    {
        var constructor = name[(name.LastIndexOf('.') + 1)..];
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"Controls/{name}/ControlManifest.xml"] = Encoding.UTF8.GetBytes(Manifest(constructor, version)),
            [$"Controls/{name}/bundle.js"] = Encoding.UTF8.GetBytes(bundle + "\n"),
            [$"Controls/{name}/strings/{constructor}.1033.resx"] =
                Encoding.UTF8.GetBytes($"<root><data name=\"Name\"><value>{constructor}</value></data></root>"),
        };
    }

    /// <summary>A solution file holding only <paramref name="controls"/> (each listed under <c>RootComponents</c> by name).</summary>
    private static byte[] Build(IReadOnlyDictionary<string, Dictionary<string, byte[]>> controls, string version = "1.0.0.0", bool managed = false,
        string solution = Solution)
    {
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["solution.xml"] = Bytes(SolutionXml(solution, version, managed, controls.Keys)),
            ["customizations.xml"] = Bytes(new XDocument(new XElement("ImportExportXml",
                new XElement("Entities"), new XElement("Roles"), new XElement("Workflows"), new XElement("FieldSecurityProfiles"),
                new XElement("Templates"), new XElement("EntityMaps"), new XElement("EntityRelationships"), new XElement("OrganizationSettings"),
                new XElement("optionsets"),
                new XElement("CustomControls", controls.Keys.Select(name => new XElement("CustomControl",
                    new XElement("Name", name), new XElement("FileName", $"/Controls/{name}/ControlManifest.xml")))),
                new XElement("SolutionPluginAssemblies"), new XElement("SdkMessageProcessingSteps"),
                new XElement("Languages", new XElement("Language", 1033))))),
        };
        foreach (var files in controls.Values)
            foreach (var (path, content) in files) entries[path] = content;

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static XDocument SolutionXml(string solution, string version, bool managed, IEnumerable<string> controls) => new(new XElement("ImportExportXml",
        new XAttribute("version", "9.2.24093.200"), new XAttribute("SolutionPackageVersion", "9.2"), new XAttribute("languagecode", 1033),
        new XAttribute("generatedBy", "CrmLive"),
        new XElement("SolutionManifest",
            new XElement("UniqueName", solution),
            new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", solution), new XAttribute("languagecode", 1033))),
            new XElement("Descriptions"),
            new XElement("Version", version),
            new XElement("Managed", managed ? 1 : 0),
            new XElement("Publisher",
                new XElement("UniqueName", "xrmverse"),
                new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", "xrmverse"), new XAttribute("languagecode", 1033))),
                new XElement("Descriptions"),
                new XElement("CustomizationPrefix", "xv"),
                new XElement("CustomizationOptionValuePrefix", 10000)),
            new XElement("RootComponents", controls.Select(name => new XElement("RootComponent",
                new XAttribute("type", 66), new XAttribute("schemaName", name), new XAttribute("behavior", 0)))),
            new XElement("MissingDependencies"))));

    private static byte[] Bytes(XDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static Task<OrganizationResponse> ImportAsync(OrganizationService service, byte[] zip, Guid? jobId = null) =>
        service.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = jobId ?? Guid.NewGuid() });

    /// <summary>The id the import gives a new control (<c>SolutionImporter.DerivedId</c>).</summary>
    private static Guid DerivedId(string name) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"xrmd:{CustomControlSchema.Table}:{name.ToLowerInvariant()}")));

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(sql, parameters));

    private async Task<Guid> SolutionIdAsync(string solution = Solution) =>
        (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", solution)))!;

    private Task<int> FileCountAsync() => CountAsync("SELECT COUNT(*) FROM meta.customcontrol_file");

    // ---- tests --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_creates_row_files_and_component()
    {
        var files = ControlFiles(Gauge);
        var jobId = Guid.NewGuid();

        await ImportAsync(Admin, Build(new Dictionary<string, Dictionary<string, byte[]>> { [Gauge] = files }), jobId);

        var row = await Admin.RetrieveAsync(CustomControlSchema.Table, DerivedId(Gauge), new Microsoft.Xrm.Sdk.Query.ColumnSet(true));
        row.GetAttributeValue<string>(CustomControlSchema.Name).Should().Be(Gauge);
        row.GetAttributeValue<string>(CustomControlSchema.Version).Should().Be("1.0.0");
        row.GetAttributeValue<string>(CustomControlSchema.CompatibleDataTypes).Should().Be("Decimal");
        row.GetAttributeValue<string>(CustomControlSchema.Manifest).Should().Contain("constructor=\"Gauge\"");
        var bundle = await Files.GetAsync(DerivedId(Gauge), "bundle.js", default);
        bundle.Should().NotBeNull();
        bundle!.Value.Content.Should().Equal(files[$"Controls/{Gauge}/bundle.js"]);
        (await Files.GetAsync(DerivedId(Gauge), "strings/Gauge.1033.resx", default)).Should().NotBeNull();
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE componenttype = 66 AND objectid = @id AND solutionid = @s",
            ("@id", DerivedId(Gauge)), ("@s", await SolutionIdAsync()))).Should().Be(1);
        var log = (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
        log.Should().NotContain("not applied").And.NotContain("type 66");
    }

    [Fact]
    public async Task Reimport_with_new_bundle_replaces_files_and_version()
    {
        var first = ControlFiles(Gauge);
        first[$"Controls/{Gauge}/old.txt"] = Encoding.UTF8.GetBytes("only in the first version");
        await ImportAsync(Admin, Build(new Dictionary<string, Dictionary<string, byte[]>> { [Gauge] = first }));

        await ImportAsync(Admin, Build(new Dictionary<string, Dictionary<string, byte[]>> { [Gauge] = ControlFiles(Gauge, "1.1.0", "var v = 2;") },
            version: "1.1.0.0"));

        (await CountAsync("SELECT COUNT(*) FROM data.customcontrol")).Should().Be(1);
        (await ScalarAsync("SELECT version FROM data.customcontrol WHERE customcontrolid = @id", ("@id", DerivedId(Gauge)))).Should().Be("1.1.0");
        Encoding.UTF8.GetString((await Files.GetAsync(DerivedId(Gauge), "bundle.js", default))!.Value.Content).Should().Be("var v = 2;\n");
        (await Files.GetAsync(DerivedId(Gauge), "old.txt", default)).Should().BeNull("the files of the control are replaced");
    }

    [Fact]
    public async Task Managed_uninstall_removes_row_and_files()
    {
        await ImportAsync(Admin, Build(new Dictionary<string, Dictionary<string, byte[]>> { [Gauge] = ControlFiles(Gauge) }, managed: true));
        (await ScalarAsync("SELECT ismanaged FROM data.customcontrol WHERE customcontrolid = @id", ("@id", DerivedId(Gauge)))).Should().Be(true);
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE component_type = 66 AND payload IS NOT NULL")).Should().Be(1);
        (await FileCountAsync()).Should().Be(3);

        await Admin.DeleteAsync("solution", await SolutionIdAsync());

        (await CountAsync("SELECT COUNT(*) FROM data.customcontrol")).Should().Be(0);
        (await FileCountAsync()).Should().Be(0, "the files go with the control");
    }

    [Fact]
    public async Task Uninstalling_the_upper_managed_solution_restores_the_control_below()
    {
        await ImportAsync(Admin, Build(new Dictionary<string, Dictionary<string, byte[]>> { [Gauge] = ControlFiles(Gauge) }, managed: true));
        await ImportAsync(Admin, Build(new Dictionary<string, Dictionary<string, byte[]>> { [Gauge] = ControlFiles(Gauge, "2.0.0", "var upper;") },
            managed: true, solution: "XvCustomControlsUpper"));
        (await ScalarAsync("SELECT version FROM data.customcontrol")).Should().Be("2.0.0");

        await Admin.DeleteAsync("solution", await SolutionIdAsync("XvCustomControlsUpper"));

        (await ScalarAsync("SELECT version FROM data.customcontrol")).Should().Be("1.0.0");
        Encoding.UTF8.GetString((await Files.GetAsync(DerivedId(Gauge), "bundle.js", default))!.Value.Content).Should().Be("var v = 1;\n");
    }

    [Fact]
    public async Task Export_writes_node_and_files_back_byte_for_byte()
    {
        var files = ControlFiles(Gauge);
        await ImportAsync(Admin, Build(new Dictionary<string, Dictionary<string, byte[]>> { [Gauge] = files }));

        var (solution, customizations, exported) = SolutionZip.Read(((ExportSolutionResponse)await Admin.ExecuteAsync(
            new ExportSolutionRequest { SolutionName = Solution, Managed = false })).ExportSolutionFile);

        foreach (var (path, content) in files) exported.Should().ContainKey(path).WhoseValue.Should().Equal(content, "entry {0} is written back as imported", path);
        var entry = customizations.Root!.Element("CustomControls")!.Elements("CustomControl").Should().ContainSingle().Subject;
        ((string?)entry.Element("Name")).Should().Be(Gauge);
        ((string?)entry.Element("FileName")).Should().Be($"/Controls/{Gauge}/ControlManifest.xml");
        solution.Descendants("RootComponent").Where(r => (string?)r.Attribute("type") == "66")
            .Select(r => (string?)r.Attribute("schemaName")).Should().Equal(Gauge);

        // The export imports again unchanged.
        await ImportAsync(Admin, SolutionZip.Write(solution, customizations,
            exported.Where(f => f.Key != "[Content_Types].xml").ToDictionary(f => f.Key, f => f.Value)));
        (await CountAsync("SELECT COUNT(*) FROM data.customcontrol")).Should().Be(1);
    }
}
