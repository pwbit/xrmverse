using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Registration;
using Xrmd.Metadata;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// ImportSolution, uninstall and ExportSolution of formula columns (Power Fx, plan 2.12c, Task 5, ruling F3) on the fixture solution:
/// the formula columns of <c>xv_project</c> read their formulas from <c>Formulas/xv_project-FormulaDefinitions.yaml</c>. Runs on the
/// solution import database (the fixture's tables are created and dropped by every test).
/// </summary>
[Collection("solution-import")]
public sealed class FormulaSolutionImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string FormulaPath = SolutionFixtureZip.RealShapesFormulaPath;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanAsync();
    }

    public Task DisposeAsync() => CleanAsync();

    private async Task CleanAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("DELETE FROM meta.component_layer; DELETE FROM meta.solution_rank;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<OrganizationResponse> ImportAsync(byte[] zip) =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });

    private AttributeMetadata Column(string entity, string column) =>
        Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == column);

    private Task<Guid> FixtureSolutionIdAsync() => SolutionIdAsync(SolutionFixtureZip.FixtureName);

    private async Task<Guid> SolutionIdAsync(string uniqueName)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT solutionid FROM data.solution WHERE uniquename = @n", conn);
        cmd.Parameters.AddWithValue("@n", uniqueName);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>The layer payloads of the attributes that carry the formula file (its archive path).</summary>
    private async Task<int> LayersWithFormulaFileAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(
            "SELECT COUNT(*) FROM meta.component_layer WHERE component_type = 2 AND payload LIKE @p", conn);
        cmd.Parameters.AddWithValue("@p", "%" + FormulaPath + "%");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// The fixture with two formula columns on <c>xv_project</c>: <c>xv_label</c> (the overlay's formula) and <c>xv_shout</c>, which
    /// reads <c>xv_label</c>; their formula file has the two lines.
    /// </summary>
    private static byte[] TwoFormulas(Action<XDocument>? solution = null) => SolutionFixtureZip.BuildWithFiles(d =>
    {
        SolutionFixtureZip.AddFormulaLabel(d);
        SolutionFixtureZip.AddFormulaColumn(d, "xv_Project", "xv_Shout");
    }, solution, new Dictionary<string, byte[]>
    {
        [FormulaPath] = Encoding.UTF8.GetBytes("xv_label: =Concatenate(xv_name, \" (project)\")\r\nxv_shout: =Upper(xv_label)\r\n"),
    });

    /// <summary>
    /// The fixture with <c>xv_shout</c> (reads <c>xv_label</c>) written before <c>xv_label</c>, in customizations.xml and in the formula
    /// file; <paramref name="upper"/> makes it a second managed solution, <c>xv_formulaext</c>, that layers only the tables (with these
    /// columns), their option set and relationships.
    /// </summary>
    private static byte[] ReaderFirst(bool upper = false) => SolutionFixtureZip.BuildWithFiles(d =>
    {
        SolutionFixtureZip.AddFormulaColumn(d, "xv_Project", "xv_Shout");
        SolutionFixtureZip.AddFormulaLabel(d);
        if (upper)
            foreach (var name in new[] { "Roles", "SolutionPluginAssemblies", "SdkMessageProcessingSteps", "WebResources", "AppModuleSiteMaps", "AppModules" })
                d.Root!.Element(name)?.RemoveNodes();
    }, upper ? (Action<XDocument>)(d =>
    {
        CalculatedSolutionFixture.Managed()(d);
        d.Descendants("UniqueName").First().Value = UpperSolution;
        d.Descendants("RootComponent").Where(r => (string?)r.Attribute("type") is not ("1" or "9")).Remove();
    }) : null, new Dictionary<string, byte[]>
    {
        [FormulaPath] = Encoding.UTF8.GetBytes("xv_shout: =Upper(xv_label)\r\nxv_label: =Concatenate(xv_name, \" (project)\")\r\n"),
    });

    private const string UpperSolution = "xv_formulaext";

    private async Task<string?> ShoutAsync(Guid id) =>
        (await Admin.RetrieveAsync("xv_project", id, new ColumnSet("xv_shout"))).GetAttributeValue<string>("xv_shout");

    [Fact]
    public async Task Import_creates_a_formula_column_after_the_one_it_reads_whatever_the_file_order()
    {
        await ImportAsync(ReaderFirst());

        Column("xv_project", "xv_label").SourceType.Should().Be(ComputedColumnRules.Formula);
        ComputedColumnRules.FormulaOf(Column("xv_project", "xv_shout")).Should().Be("Upper(xv_label)");
        ComputedColumnRules.MaskOf(Column("xv_project", "xv_shout")).Should().Be(8);
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        (await ShoutAsync(id)).Should().Be("APOLLO (PROJECT)");
    }

    [Fact]
    public async Task Uninstall_of_an_upper_solution_removes_its_formula_columns_and_keeps_the_table()
    {
        // The base solution (managed) owns the tables; the upper one adds only the two formula columns, the reader first in its files.
        await ImportAsync(SolutionFixtureZip.Build(null, CalculatedSolutionFixture.Managed()));
        await ImportAsync(ReaderFirst(upper: true));
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        (await ShoutAsync(id)).Should().Be("APOLLO (PROJECT)");
        (await LayersWithFormulaFileAsync()).Should().Be(2);

        // End to end: the uninstall deletes xv_shout before xv_label, which K4 refuses while xv_shout reads it.
        await Admin.DeleteAsync("solution", await SolutionIdAsync(UpperSolution));

        var project = Store.GetEntity("xv_project")!;
        project.Attributes.Select(a => a.LogicalName).Should().NotContain(["xv_label", "xv_shout"]).And.Contain("xv_name");
        (await Admin.RetrieveAsync("xv_project", id, new ColumnSet("xv_name"))).GetAttributeValue<string>("xv_name").Should().Be("Apollo");
        (await LayersWithFormulaFileAsync()).Should().Be(0, "the formula file goes with the layers of its columns");
    }

    [Fact]
    public async Task RealExportShapes_formula_imports()
    {
        // The real-shapes overlay's formula file, named by the formula column xv_project.xv_label.
        await ImportAsync(SolutionFixtureZip.BuildWithRealShapes(SolutionFixtureZip.AddFormulaLabel));

        var label = Column("xv_project", "xv_label");
        label.SourceType.Should().Be(ComputedColumnRules.Formula);
        ComputedColumnRules.FormulaOf(label).Should().Be("Concatenate(xv_name, \" (project)\")");
        label.IsValidForCreate.Should().BeFalse();
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        (await Admin.RetrieveAsync("xv_project", id, new ColumnSet("xv_label"))).GetAttributeValue<string>("xv_label")
            .Should().Be("Apollo (project)");

        // The export writes the file (CRLF, as Dataverse writes it) and the column naming it.
        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = SolutionFixtureZip.FixtureName, Managed = false,
        })).ExportSolutionFile;
        var (_, customizations, files) = SolutionZip.Read(exported);
        Encoding.UTF8.GetString(files[FormulaPath]).Should().Be("xv_label: =Concatenate(xv_name, \" (project)\")\r\n");
        var element = customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xv_label");
        element.Element("SourceType")!.Value.Should().Be("3");
        element.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + FormulaPath);
    }

    [Fact]
    public async Task Imported_formula_reads_a_related_column_through_the_navigation_property()
    {
        // Q1 (Dataverse, 2026-11-19, finding 5): a formula reads a related record through the lookup's navigation property, as
        // the fixture's relationship names it (xv_task.xv_projectid: xv_ProjectId).
        await ImportAsync(SolutionFixtureZip.BuildWithFiles(d => SolutionFixtureZip.AddFormulaColumn(d, "xv_Task", "xv_ProjectName"), null,
            new Dictionary<string, byte[]>
            {
                ["Formulas/xv_task-FormulaDefinitions.yaml"] = Encoding.UTF8.GetBytes("xv_projectname: =Upper(xv_ProjectId.xv_name)\r\n"),
            }));

        var column = Column("xv_task", "xv_projectname");
        ComputedColumnRules.FormulaOf(column).Should().Be("Upper(xv_ProjectId.xv_name)");
        ComputedColumnRules.MaskOf(column).Should().Be(2);
        var project = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        var task = await Admin.CreateAsync(new Entity("xv_task") { ["xv_name"] = "t1", ["xv_projectid"] = new EntityReference("xv_project", project) });
        (await Admin.RetrieveAsync("xv_task", task, new ColumnSet("xv_projectname"))).GetAttributeValue<string>("xv_projectname")
            .Should().Be("APOLLO");
    }

    [Fact]
    public async Task Reimport_updates_the_formula()
    {
        await ImportAsync(TwoFormulas());
        ComputedColumnRules.FormulaOf(Column("xv_project", "xv_shout")).Should().Be("Upper(xv_label)");

        await ImportAsync(SolutionFixtureZip.BuildWithFiles(d =>
        {
            SolutionFixtureZip.AddFormulaLabel(d);
            SolutionFixtureZip.AddFormulaColumn(d, "xv_Project", "xv_Shout");
        }, null, new Dictionary<string, byte[]>
        {
            [FormulaPath] = Encoding.UTF8.GetBytes("xv_label: =Concatenate(xv_name, \" [p]\")\r\nxv_shout: =Lower(xv_label)\r\n"),
        }));

        ComputedColumnRules.FormulaOf(Column("xv_project", "xv_shout")).Should().Be("Lower(xv_label)");
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        (await Admin.RetrieveAsync("xv_project", id, new ColumnSet("xv_shout"))).GetAttributeValue<string>("xv_shout")
            .Should().Be("apollo [p]");
    }

    [Fact]
    public async Task Uninstall_removes_formula_columns_and_file()
    {
        await ImportAsync(TwoFormulas(CalculatedSolutionFixture.Managed()));
        Column("xv_project", "xv_label").SourceType.Should().Be(ComputedColumnRules.Formula);
        var shout = Column("xv_project", "xv_shout");
        shout.SourceType.Should().Be(ComputedColumnRules.Formula);
        ComputedColumnRules.MaskOf(shout).Should().Be(8, "xv_shout reads only the formula column xv_label");
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        (await Admin.RetrieveAsync("xv_project", id, new ColumnSet("xv_shout"))).GetAttributeValue<string>("xv_shout")
            .Should().Be("APOLLO (PROJECT)");
        await Admin.DeleteAsync("xv_project", id);
        (await LayersWithFormulaFileAsync()).Should().Be(2, "each formula column's layer carries the formula file");

        // Listed with the column read first, the plan still puts its reader first (K4: a referenced column cannot be deleted).
        var label = Column("xv_project", "xv_label").MetadataId!.Value;
        using (var scope = fx.App.Services.CreateScope())
        {
            var remover = scope.ServiceProvider.GetRequiredService<Xrmd.Core.Solutions.ComponentRemover>();
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            var plan = await remover.PlanAsync(new DataSession(conn, null),
                [(ComponentType.Attribute, label), (ComponentType.Attribute, shout.MetadataId!.Value)], default);
            var ordered = plan.Ordered.Select(o => o.Id).ToList();
            ordered.IndexOf(shout.MetadataId!.Value).Should().BeLessThan(ordered.IndexOf(label));
        }

        await Admin.DeleteAsync("solution", await FixtureSolutionIdAsync());

        Store.GetEntity("xv_project").Should().BeNull();
        (await LayersWithFormulaFileAsync()).Should().Be(0, "the formula file goes with the layers of its columns");
    }
}
