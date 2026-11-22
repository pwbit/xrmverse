using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>An environment variable of an <see cref="EnvironmentVariableImportTests"/> solution file.</summary>
/// <param name="SchemaName">The schema name, also its folder's name.</param>
/// <param name="Type">The <c>type</c> choice.</param>
/// <param name="Default">The <c>defaultvalue</c>; null for none.</param>
/// <param name="Value">The value of <c>environmentvariablevalues.json</c>; null for no value file.</param>
/// <param name="DisplayName">The base-language display name; null for no <c>displayname</c> element.</param>
public sealed record TestVariable(string SchemaName, int Type = (int)VariableType.String, string? Default = null, string? Value = null,
    string? DisplayName = null);

/// <summary>
/// #167, task 3 (rulings E3 and E4): the solution import writes the definitions of <c>environmentvariabledefinitions/</c> as
/// components of type 380 and their values as plain rows, re-imports update them, a managed uninstall removes them; the export
/// writes the folder again from the rows.
/// </summary>
[Collection("solution-import")]
public sealed class EnvironmentVariableImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XvEnvironmentVariables";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    // ---- the solution file ------------------------------------------------------------------------------------------

    /// <summary>
    /// A solution file holding only <paramref name="variables"/>, without <c>RootComponent</c> for them (as the OpenProject365
    /// export); <paramref name="files"/> may add or change archive entries.
    /// </summary>
    private static byte[] Build(IReadOnlyList<TestVariable> variables, string version = "1.0.0.0", bool managed = false,
        Action<IDictionary<string, byte[]>>? files = null, string solution = Solution)
    {
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["solution.xml"] = Bytes(SolutionXml(solution, version, managed)),
            ["customizations.xml"] = Bytes(new XDocument(new XElement("ImportExportXml",
                new XElement("Entities"), new XElement("Roles"), new XElement("Workflows"), new XElement("FieldSecurityProfiles"),
                new XElement("Templates"), new XElement("EntityMaps"), new XElement("EntityRelationships"), new XElement("OrganizationSettings"),
                new XElement("optionsets"), new XElement("CustomControls"), new XElement("SolutionPluginAssemblies"),
                new XElement("SdkMessageProcessingSteps"), new XElement("Languages", new XElement("Language", 1033))))),
        };
        foreach (var variable in variables)
        {
            var folder = $"environmentvariabledefinitions/{variable.SchemaName}";
            entries[$"{folder}/environmentvariabledefinition.xml"] = Encoding.UTF8.GetBytes(Definition(variable).ToString());
            if (variable.Value is { } value)
                entries[$"{folder}/environmentvariablevalues.json"] = Encoding.UTF8.GetBytes(
                    """{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":"1","value":""" + $"\"{value}\"" + "}}}");
        }
        files?.Invoke(entries);

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

    private static XElement Definition(TestVariable variable) => new("environmentvariabledefinition",
        new XAttribute("schemaname", variable.SchemaName),
        variable.Default is null ? null : new XElement("defaultvalue", variable.Default),
        Labels("description", $"The test variable {variable.SchemaName}"),
        variable.DisplayName is null ? null : Labels("displayname", variable.DisplayName),
        new XElement("introducedversion", "1.0.0.0"),
        new XElement("iscustomizable", 1),
        new XElement("isrequired", 0),
        new XElement("secretstore", 0),
        new XElement("type", variable.Type));

    private static XElement Labels(string name, string text) => new(name, new XAttribute("default", text),
        new XElement("label", new XAttribute("description", text), new XAttribute("languagecode", 1033)),
        new XElement("label", new XAttribute("description", text + " (it)"), new XAttribute("languagecode", 1040)));

    private static XDocument SolutionXml(string solution, string version, bool managed) => new(new XElement("ImportExportXml",
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
            new XElement("RootComponents"),
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

    /// <summary>The id the import gives a new definition (<c>SolutionImporter.DerivedId</c>).</summary>
    private static Guid DerivedId(string schemaName) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"xrmd:{EnvironmentVariableSchema.Definition}:{schemaName.ToLowerInvariant()}")));

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

    private Task<int> DefinitionsAsync() => CountAsync("SELECT COUNT(*) FROM data.environmentvariabledefinition");

    private Task<int> ValuesAsync() => CountAsync("SELECT COUNT(*) FROM data.environmentvariablevalue");

    private async Task<string?> ValueOfAsync(string schemaName) => (string?)await ScalarAsync("""
        SELECT v.value FROM data.environmentvariablevalue v
        JOIN data.environmentvariabledefinition d ON d.environmentvariabledefinitionid = v.environmentvariabledefinitionid
        WHERE d.schemaname = @n
        """, ("@n", schemaName));

    private async Task<string> JobDataAsync(Guid jobId) =>
        (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;

    private async Task<Guid> SolutionIdAsync(string solution = Solution) =>
        (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", solution)))!;

    private static string Missing(string schemaName) =>
        $"Environment variable '{schemaName}' has no value and no default value: set one with `xrmd envvar set`.";

    private static readonly TestVariable ServiceUrl = new("xv_ServiceUrl", Default: "https://default.example.com", Value: "https://service.example.org",
        DisplayName: "Service URL");

    private static readonly TestVariable Enabled = new("xv_Enabled", (int)VariableType.Boolean, Default: "no", DisplayName: "Enabled");

    // ---- tests --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_creates_definitions_and_values()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, Build([ServiceUrl, Enabled]), jobId);

        var definition = await Admin.RetrieveAsync(EnvironmentVariableSchema.Definition, DerivedId("xv_ServiceUrl"), new ColumnSet(true));
        definition.GetAttributeValue<string>(EnvironmentVariableSchema.SchemaName).Should().Be("xv_ServiceUrl");
        definition.GetAttributeValue<string>(EnvironmentVariableSchema.DisplayName).Should().Be("Service URL", "the base language's label");
        definition.GetAttributeValue<string>(EnvironmentVariableSchema.Description).Should().Be("The test variable xv_ServiceUrl");
        definition.GetAttributeValue<string>(EnvironmentVariableSchema.DefaultValue).Should().Be("https://default.example.com");
        definition.GetAttributeValue<OptionSetValue>(EnvironmentVariableSchema.Type).Value.Should().Be((int)VariableType.String);
        definition.GetAttributeValue<bool>(EnvironmentVariableSchema.IsRequired).Should().BeFalse();
        definition.GetAttributeValue<OptionSetValue>(EnvironmentVariableSchema.SecretStore).Value.Should().Be(0);
        definition.GetAttributeValue<string>(EnvironmentVariableSchema.IntroducedVersion).Should().Be("1.0.0.0");
        (await Admin.RetrieveAsync(EnvironmentVariableSchema.Definition, DerivedId("xv_Enabled"), new ColumnSet(EnvironmentVariableSchema.Type)))
            .GetAttributeValue<OptionSetValue>(EnvironmentVariableSchema.Type).Value.Should().Be((int)VariableType.Boolean);

        (await ValueOfAsync("xv_ServiceUrl")).Should().Be("https://service.example.org");
        (await ValuesAsync()).Should().Be(1, "only xv_ServiceUrl has a value file");
        (await ScalarAsync("SELECT schemaname FROM data.environmentvariablevalue")).Should().Be("xv_ServiceUrl");

        // Both definitions are components of type 380 of the solution, with no RootComponent in solution.xml; the value is none.
        (await CountAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = @n AND sc.componenttype = 380
            """, ("@n", Solution))).Should().Be(2);
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE componenttype = 381")).Should().Be(0);
        var log = await JobDataAsync(jobId);
        log.Should().NotContain("environmentvariabledefinitions/", "the folder is applied, not kept as unread entries")
            .And.NotContain("Root component");
        XElement.Parse(log).Element("environmentVariables")!.Elements("environmentVariable").Should().HaveCount(2);
    }

    [Fact]
    public async Task Definition_without_value_or_default_is_logged()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, Build([ServiceUrl, Enabled, new TestVariable("xv_Approver"), new TestVariable("xv_Defaulted", Default: "x")]), jobId);

        var log = await JobDataAsync(jobId);
        log.Should().Contain(Missing("xv_Approver"));
        foreach (var name in new[] { "xv_ServiceUrl", "xv_Enabled", "xv_Defaulted" }) log.Should().NotContain(Missing(name));

        // Once a value is set, a re-import does not warn again.
        await Admin.CreateAsync(new Entity(EnvironmentVariableSchema.Value)
        {
            [EnvironmentVariableSchema.ValueColumn] = "someone@example.com",
            [EnvironmentVariableSchema.DefinitionLookup] = new EntityReference(EnvironmentVariableSchema.Definition, DerivedId("xv_Approver")),
        });
        var again = Guid.NewGuid();
        await ImportAsync(Admin, Build([ServiceUrl, Enabled, new TestVariable("xv_Approver")]), again);
        (await JobDataAsync(again)).Should().NotContain("has no value and no default value");
    }

    [Fact]
    public async Task Reimport_updates_without_duplicates()
    {
        await ImportAsync(Admin, Build([ServiceUrl, Enabled]));
        await ImportAsync(Admin, Build([ServiceUrl, Enabled]));
        await ImportAsync(Admin, Build([ServiceUrl with { Default = "https://new.example.com", Value = "https://other.example.org" }, Enabled],
            version: "1.1.0.0"));

        (await DefinitionsAsync()).Should().Be(2);
        (await ValuesAsync()).Should().Be(1);
        (await ScalarAsync("SELECT defaultvalue FROM data.environmentvariabledefinition WHERE schemaname = N'xv_ServiceUrl'"))
            .Should().Be("https://new.example.com");
        (await ValueOfAsync("xv_ServiceUrl")).Should().Be("https://other.example.org");
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE componenttype = 380 AND solutionid = @s", ("@s", await SolutionIdAsync())))
            .Should().Be(2);
    }

    [Fact]
    public async Task Value_set_by_admin_survives_import_without_value_file()
    {
        await ImportAsync(Admin, Build([Enabled]));
        await Admin.CreateAsync(new Entity(EnvironmentVariableSchema.Value)
        {
            [EnvironmentVariableSchema.ValueColumn] = "yes",
            [EnvironmentVariableSchema.DefinitionLookup] = new EntityReference(EnvironmentVariableSchema.Definition, DerivedId("xv_Enabled")),
        });

        await ImportAsync(Admin, Build([Enabled with { Default = "maybe" }], version: "1.0.1.0"));

        (await ValueOfAsync("xv_Enabled")).Should().Be("yes", "a file without a value file leaves the value as it is");
        (await ValuesAsync()).Should().Be(1);
        (await ScalarAsync("SELECT defaultvalue FROM data.environmentvariabledefinition WHERE schemaname = N'xv_Enabled'")).Should().Be("maybe");
    }

    [Fact]
    public async Task Value_file_overwrites_existing_value()
    {
        await ImportAsync(Admin, Build([ServiceUrl]));
        var valueId = (Guid)(await ScalarAsync("SELECT environmentvariablevalueid FROM data.environmentvariablevalue"))!;
        await Admin.UpdateAsync(new Entity(EnvironmentVariableSchema.Value, valueId) { [EnvironmentVariableSchema.ValueColumn] = "https://admin.example.org" });

        await ImportAsync(Admin, Build([ServiceUrl]));

        (await ValueOfAsync("xv_ServiceUrl")).Should().Be("https://service.example.org", "the value file wins");
        (await ScalarAsync("SELECT environmentvariablevalueid FROM data.environmentvariablevalue")).Should().Be(valueId, "the value row is updated, not replaced");
    }

    [Fact]
    public async Task Existing_definition_is_found_ignoring_case()
    {
        var id = await Admin.CreateAsync(new Entity(EnvironmentVariableSchema.Definition)
        {
            [EnvironmentVariableSchema.SchemaName] = "XV_SERVICEURL",
            [EnvironmentVariableSchema.Type] = new OptionSetValue((int)VariableType.String),
        });

        await ImportAsync(Admin, Build([ServiceUrl]));

        (await DefinitionsAsync()).Should().Be(1);
        (await ScalarAsync("SELECT schemaname FROM data.environmentvariabledefinition WHERE environmentvariabledefinitionid = @id", ("@id", id)))
            .Should().Be("XV_SERVICEURL", "a schema name does not change once created");
        (await ScalarAsync("SELECT defaultvalue FROM data.environmentvariabledefinition")).Should().Be("https://default.example.com");
        (await ValueOfAsync("XV_SERVICEURL")).Should().Be("https://service.example.org");
    }

    [Fact]
    public async Task Case_duplicate_in_file_fails_the_import()
    {
        // A folder is named by its schema name (ignoring case), so two definitions differing only by case are two entries
        // differing only by case: the file is refused before anything is read.
        var zip = Build([ServiceUrl, Enabled], files: f =>
            f["environmentvariabledefinitions/XV_ENABLED/environmentvariabledefinition.xml"] =
                Encoding.UTF8.GetBytes(Definition(new TestVariable("XV_ENABLED")).ToString()));

        var fault = await FaultAsync(() => ImportAsync(Admin, zip));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().ContainEquivalentOf("environmentvariabledefinitions/xv_enabled/environmentvariabledefinition.xml");
        (await DefinitionsAsync()).Should().Be(0);
        (await ValuesAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Solution))).Should().Be(0);
    }

    [Fact]
    public async Task Root_component_of_type_380_is_tracked_with_its_behavior()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, Build([ServiceUrl], files: f =>
        {
            var solution = XDocument.Load(new MemoryStream(f["solution.xml"]));
            solution.Descendants("RootComponents").Single().Add(new XElement("RootComponent",
                new XAttribute("type", 380), new XAttribute("schemaName", "xv_serviceurl"), new XAttribute("behavior", 0)));
            f["solution.xml"] = Bytes(solution);
        }), jobId);

        (await JobDataAsync(jobId)).Should().NotContain("Root component");
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE componenttype = 380 AND objectid = @id AND solutionid = @s",
            ("@id", DerivedId("xv_ServiceUrl")), ("@s", await SolutionIdAsync()))).Should().Be(1);
    }

    [Fact]
    public async Task Managed_uninstall_removes_definitions_and_values()
    {
        await ImportAsync(Admin, Build([ServiceUrl, Enabled], managed: true));
        (await DefinitionsAsync()).Should().Be(2);
        (await ScalarAsync("SELECT ismanaged FROM data.environmentvariabledefinition WHERE schemaname = N'xv_ServiceUrl'")).Should().Be(true);
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE component_type = 380 AND payload IS NOT NULL")).Should().Be(2, "each definition has its managed layer");

        await Admin.DeleteAsync("solution", await SolutionIdAsync());

        (await DefinitionsAsync()).Should().Be(0);
        (await ValuesAsync()).Should().Be(0, "the value goes with its definition (Delete Cascade)");
    }

    [Fact]
    public async Task Uninstalling_the_upper_managed_solution_restores_the_definition_below()
    {
        await ImportAsync(Admin, Build([ServiceUrl], managed: true));
        await ImportAsync(Admin, Build([ServiceUrl with { Default = "https://upper.example.com", DisplayName = "Upper" }], managed: true,
            solution: "XvEnvironmentVariablesUpper"));
        (await ScalarAsync("SELECT defaultvalue FROM data.environmentvariabledefinition")).Should().Be("https://upper.example.com");

        await Admin.DeleteAsync("solution", await SolutionIdAsync("XvEnvironmentVariablesUpper"));

        (await DefinitionsAsync()).Should().Be(1, "the lower solution still holds it");
        (await ScalarAsync("SELECT defaultvalue FROM data.environmentvariabledefinition")).Should().Be("https://default.example.com");
        (await ScalarAsync("SELECT displayname FROM data.environmentvariabledefinition")).Should().Be("Service URL");
        (await ValuesAsync()).Should().Be(1, "a restore leaves the value as it is");
    }

    [Fact]
    public async Task Deleting_an_unmanaged_solution_leaves_its_definitions()
    {
        await ImportAsync(Admin, Build([ServiceUrl, Enabled]));

        await Admin.DeleteAsync("solution", await SolutionIdAsync());

        (await DefinitionsAsync()).Should().Be(2);
        (await ValuesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Export_writes_the_rows_with_their_current_labels()
    {
        await ImportAsync(Admin, Build([ServiceUrl, Enabled]));
        await Admin.UpdateAsync(new Entity(EnvironmentVariableSchema.Definition, DerivedId("xv_Enabled"))
        {
            [EnvironmentVariableSchema.DisplayName] = "Feature switch",
        });
        // A definition made through the API, added to the solution: written with the shape of an export.
        var made = await Admin.CreateAsync(new Entity(EnvironmentVariableSchema.Definition)
        {
            [EnvironmentVariableSchema.SchemaName] = "xv_Made",
            [EnvironmentVariableSchema.DisplayName] = "Made here",
            [EnvironmentVariableSchema.Type] = new OptionSetValue((int)VariableType.Number),
            [EnvironmentVariableSchema.DefaultValue] = "3",
        });
        await Admin.ExecuteAsync(new AddSolutionComponentRequest { ComponentType = 380, ComponentId = made, SolutionUniqueName = Solution });

        var (solution, _, files) = SolutionZip.Read(((ExportSolutionResponse)await Admin.ExecuteAsync(
            new ExportSolutionRequest { SolutionName = Solution, Managed = false })).ExportSolutionFile);

        solution.Descendants("RootComponent").Where(r => (string?)r.Attribute("type") == "380")
            .Select(r => (string?)r.Attribute("schemaName")).Should().BeEquivalentTo("xv_Enabled", "xv_Made", "xv_ServiceUrl");
        var enabled = XElement.Parse(Encoding.UTF8.GetString(files["environmentvariabledefinitions/xv_Enabled/environmentvariabledefinition.xml"]));
        var display = enabled.Element("displayname")!;
        ((string?)display.Attribute("default")).Should().Be("Feature switch");
        display.Elements("label").Select(l => ((string?)l.Attribute("languagecode"), (string?)l.Attribute("description")))
            .Should().Equal(("1033", "Feature switch"), ("1040", "Enabled (it)"));
        var madeXml = XElement.Parse(Encoding.UTF8.GetString(files["environmentvariabledefinitions/xv_Made/environmentvariabledefinition.xml"]));
        madeXml.Elements().Select(e => e.Name.LocalName).Should().Equal(
            "defaultvalue", "displayname", "introducedversion", "iscustomizable", "isrequired", "secretstore", "type");
        ((string?)madeXml.Element("displayname")!.Element("label")!.Attribute("description")).Should().Be("Made here");
        files.Should().NotContainKey("environmentvariabledefinitions/xv_Made/environmentvariablevalues.json");
        Encoding.UTF8.GetString(files["environmentvariabledefinitions/xv_ServiceUrl/environmentvariablevalues.json"]).Should()
            .Be("""{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":"1","value":"https://service.example.org"}}}""");
    }

    [Fact]
    public async Task Export_round_trips_into_second_org()
    {
        await ImportAsync(Admin, Build([ServiceUrl, Enabled, new TestVariable("xv_Approver", DisplayName: "Approver")]));
        await Admin.CreateAsync(new Entity(EnvironmentVariableSchema.Value)
        {
            [EnvironmentVariableSchema.ValueColumn] = "someone@example.com",
            [EnvironmentVariableSchema.DefinitionLookup] = new EntityReference(EnvironmentVariableSchema.Definition, DerivedId("xv_Approver")),
        });
        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = Solution, Managed = false }))
            .ExportSolutionFile;

        await using var app = fx.WithOwnDatabase(_ => { });
        await TestEntities.EnsurePipeAsync(app.Services.GetRequiredService<IMetadataStore>());
        var other = TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);
        await ImportAsync(other, exported);

        string[] columns =
        [
            EnvironmentVariableSchema.SchemaName, EnvironmentVariableSchema.DisplayName, EnvironmentVariableSchema.Description,
            EnvironmentVariableSchema.DefaultValue, EnvironmentVariableSchema.Type, EnvironmentVariableSchema.IsRequired,
            EnvironmentVariableSchema.SecretStore, EnvironmentVariableSchema.IntroducedVersion,
        ];
        async Task<List<string>> RowsAsync(OrganizationService service)
        {
            var query = new QueryExpression(EnvironmentVariableSchema.Definition) { ColumnSet = new ColumnSet(columns) };
            query.AddOrder(EnvironmentVariableSchema.SchemaName, OrderType.Ascending);
            var link = query.AddLink(EnvironmentVariableSchema.Value, $"{EnvironmentVariableSchema.Definition}id",
                EnvironmentVariableSchema.DefinitionLookup, JoinOperator.LeftOuter);
            link.EntityAlias = "v";
            link.Columns = new ColumnSet(EnvironmentVariableSchema.ValueColumn);
            var rows = await service.RetrieveMultipleAsync(query);
            return rows.Entities.Select(e => string.Join("|", columns.Select(c => e.GetAttributeValue<object>(c) switch
            {
                OptionSetValue o => o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture),
            }).Append(e.GetAttributeValue<AliasedValue>("v.value")?.Value as string))).ToList();
        }

        var source = await RowsAsync(Admin);
        source.Should().HaveCount(3);
        (await RowsAsync(other)).Should().Equal(source, "definitions and current values round-trip unchanged");
    }
}
