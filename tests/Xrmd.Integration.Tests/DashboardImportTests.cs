using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1f, task 2 (ruling F1): the root <c>Dashboards</c> of a solution are imported as <c>systemform</c> rows of type 0
/// with <c>objecttypecode</c> <c>none</c> (component type 60), exported back under a root <c>Dashboards</c>, no longer kept as
/// unsupported XML, resolved by an app that lists them, and removed by an uninstall. The dashboards are written by hand
/// (<see cref="SolutionFixtureZip.AddDashboards"/>).
/// </summary>
[Collection("solution-import")]
public sealed class DashboardImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    private static void WithDashboards(XDocument customizations)
    {
        SolutionFixtureZip.AddCharts(customizations);
        SolutionFixtureZip.AddDashboards(customizations);
    }

    private Task ImportAsync(Action<XDocument>? customizations = null, Action<XDocument>? solution = null, Guid? jobId = null) =>
        Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(customizations ?? WithDashboards, solution ?? SolutionFixtureZip.AddDashboardRoots),
            ImportJobId = jobId ?? Guid.NewGuid(),
        });

    private async Task<List<Dictionary<string, object?>>> RowsAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await rd.ReadAsync())
            rows.Add(Enumerable.Range(0, rd.FieldCount).ToDictionary(rd.GetName, i => rd.IsDBNull(i) ? null : rd.GetValue(i)));
        return rows;
    }

    private static XElement Normalized(XElement element) => XElement.Parse(element.ToString(SaveOptions.DisableFormatting), LoadOptions.None);

    private static Guid FormId(XElement dashboard) => Guid.Parse(dashboard.Element("FormId")!.Value);

    [Fact]
    public async Task Import_reads_dashboards_into_systemform()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(jobId: jobId);

        var rows = await RowsAsync("""
            SELECT systemformid, name, description, objecttypecode, type, formxml, isdefault, formactivationstate, xrmverse_componentxml
            FROM data.systemform WHERE type = 0 ORDER BY systemformid
            """);
        rows.Select(r => (Guid)r["systemformid"]!).Should().Equal(SolutionFixtureZip.OverviewDashboardId, SolutionFixtureZip.ChartsDashboardId);
        var overview = rows[0];
        overview["name"].Should().Be("Project Overview", "the name is the 1033 LocalizedName");
        overview["description"].Should().Be("The open projects", "the description is the 1033 Description");
        overview["objecttypecode"].Should().Be("none");
        overview["type"].Should().Be(0);
        overview["isdefault"].Should().Be(false);
        overview["formactivationstate"].Should().Be(1);
        var form = XElement.Parse((string)overview["formxml"]!);
        form.Name.LocalName.Should().Be("form", "formxml is the dashboard's <form>");
        form.Descendants("control").Single().Element("parameters")!.Element("ChartGridMode")!.Value.Should().Be("Grid");
        XElement.Parse((string)overview["xrmverse_componentxml"]!).Name.LocalName.Should().Be("Dashboard");
        rows[1]["name"].Should().Be("Project Charts");

        var components = await RowsAsync($"""
            SELECT sc.objectid FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = N'{SolutionFixtureZip.FixtureName}' AND sc.componenttype = 60
            """);
        components.Select(c => (Guid)c["objectid"]!).Should().Contain([SolutionFixtureZip.OverviewDashboardId, SolutionFixtureZip.ChartsDashboardId]);
        var log = (string)(await AuthoringTestHelper.ScalarAsync(fx, $"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        log.Should().NotContain("is not in the solution file", "the root components of type 60 are the dashboards of the file");

        // Idempotent: a second import updates the rows.
        await ImportAsync();
        (await RowsAsync("SELECT systemformid FROM data.systemform WHERE type = 0")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Dashboards_are_read_through_the_web_api()
    {
        await ImportAsync();

        var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/data/v9.2/systemforms?$filter=type eq 0&$select=systemformid,name,formxml,description,objecttypecode");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var charts = json.RootElement.GetProperty("value").EnumerateArray()
            .Single(v => v.GetProperty("systemformid").GetGuid() == SolutionFixtureZip.ChartsDashboardId);
        charts.GetProperty("name").GetString().Should().Be("Project Charts");
        charts.GetProperty("description").GetString().Should().Be("Budget of the projects");
        charts.GetProperty("objecttypecode").GetString().Should().Be("none");
        charts.GetProperty("formxml").GetString().Should().StartWith("<form");
    }

    [Fact]
    public async Task Export_writes_dashboards_back()
    {
        await ImportAsync();

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(
            new ExportSolutionRequest { SolutionName = SolutionFixtureZip.FixtureName, Managed = false })).ExportSolutionFile;
        var (solution, customizations, _) = SolutionZip.Read(exported);
        var original = XDocument.Load(Path.Combine(SolutionFixtureZip.FixtureDirectory, "customizations.xml"));
        WithDashboards(original);

        customizations.Root!.Elements().Select(e => e.Name.LocalName).Should().ContainInOrder("optionsets", "Dashboards", "WebResources")
            .And.ContainSingle(n => n == "Dashboards");
        var written = customizations.Root!.Element("Dashboards")!.Elements("Dashboard").ToList();
        written.Should().HaveCount(2);
        foreach (var dashboard in original.Root!.Element("Dashboards")!.Elements("Dashboard"))
            XNode.DeepEquals(Normalized(written.Single(d => FormId(d) == FormId(dashboard))), Normalized(dashboard))
                .Should().BeTrue("the export writes each dashboard as it was imported:\n{0}", written.Single(d => FormId(d) == FormId(dashboard)));
        written.Select(d => d.Element("FormId")!.Value).Should().Contain(SolutionFixtureZip.OverviewDashboardId.ToString("B").ToUpperInvariant(),
            "an id is written as it came (braces and case)");
        customizations.Root!.Element("Entities")!.Descendants("forms").Where(f => (string?)f.Attribute("type") == "dashboard")
            .Should().BeEmpty("no dashboard is written under an entity");
        solution.Descendants("RootComponent").Where(r => (string?)r.Attribute("type") == "60")
            .Select(r => Guid.Parse((string)r.Attribute("id")!))
            .Should().Contain([SolutionFixtureZip.OverviewDashboardId, SolutionFixtureZip.ChartsDashboardId]);

        // The export imports again as it is.
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = exported, ImportJobId = Guid.NewGuid() });
        (await RowsAsync("SELECT systemformid FROM data.systemform WHERE type = 0")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Dashboards_are_no_longer_kept_xml()
    {
        await ImportAsync();

        var kept = (string?)(await AuthoringTestHelper.ScalarAsync(fx,
            "SELECT xrmverse_unsupportedxml FROM data.solution WHERE uniquename = @n", ("@n", SolutionFixtureZip.FixtureName)));
        if (kept is not null) XElement.Parse(kept).DescendantsAndSelf("Dashboards").Should().BeEmpty();
    }

    [Fact]
    public async Task App_module_dashboard_component_resolves()
    {
        // #110: an app component of type 60 that names an imported dashboard is linked without the "no row" warning.
        var jobId = Guid.NewGuid();
        await ImportAsync(c =>
        {
            WithDashboards(c);
            c.Descendants("AppModuleComponents").Single().Add(new XElement("AppModuleComponent", new XAttribute("type", "60"),
                new XAttribute("id", SolutionFixtureZip.OverviewDashboardId.ToString("B"))));
        }, jobId: jobId);

        var log = (string)(await AuthoringTestHelper.ScalarAsync(fx, $"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        log.Should().NotContain("no systemform row");
        (await AuthoringTestHelper.ScalarAsync(fx,
            $"SELECT COUNT(*) FROM data.appmodulecomponent WHERE componenttype = 60 AND objectid = '{SolutionFixtureZip.OverviewDashboardId}'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Uninstall_removes_the_dashboards()
    {
        await ImportAsync(solution: s =>
        {
            SolutionFixtureZip.AddDashboardRoots(s);
            s.Descendants("Managed").Single().Value = "1";
        });
        var solutionId = (Guid)(await AuthoringTestHelper.ScalarAsync(fx, "SELECT solutionid FROM data.solution WHERE uniquename = @n",
            ("@n", SolutionFixtureZip.FixtureName)))!;
        (await AuthoringTestHelper.ScalarAsync(fx, "SELECT COUNT(*) FROM data.systemform WHERE type = 0")).Should().Be(2);

        await Admin.DeleteAsync("solution", solutionId);

        (await AuthoringTestHelper.ScalarAsync(fx, "SELECT COUNT(*) FROM data.systemform WHERE type = 0")).Should().Be(0);
    }

    [Fact]
    public async Task Dashboard_without_form_id_is_refused()
    {
        var act = () => ImportAsync(c =>
        {
            WithDashboards(c);
            c.Descendants("Dashboard").First().Element("FormId")!.Remove();
        }, solution: _ => { });

        (await act.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which.Message.Should().Contain("FormId");
    }
}

/// <summary>
/// Plan 3.5 UI-1f (Appendix B): the four dashboards of the unmodified OpenProject365 export become <c>systemform</c> rows of type
/// 0, and its export writes them back. On the real-export database, as the other tests of the export.
/// </summary>
[Collection("real-export")]
public sealed class OpenProject365DashboardImportTests(XrmdFixture fx)
{
    private static readonly string Directory = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports");

    [OpenProject365Fact]
    public async Task OpenProject365_dashboards_import()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var file = File.ReadAllBytes(Path.Combine(Directory, "OpenProject365_1_0_1_1.zip"));
        await admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });

        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("SELECT name, objecttypecode FROM data.systemform WHERE type = 0", conn);
            await using var rd = await cmd.ExecuteReaderAsync();
            var dashboards = new List<(string Name, string Table)>();
            while (await rd.ReadAsync()) dashboards.Add((rd.GetString(0), rd.GetString(1)));
            dashboards.Select(d => d.Name).Should().BeEquivalentTo("Resource Load", "Spend", "Time Approval", "Timesheet");
            dashboards.Select(d => d.Table).Should().AllBe("none");
        }

        // The export writes the four back under the root Dashboards, each equal to the original after normalising whitespace.
        var exported = ((ExportSolutionResponse)await admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "OpenProject365", Managed = false }))
            .ExportSolutionFile;
        var (_, customizations, _) = SolutionZip.Read(exported);
        var (_, original, _) = SolutionZip.Read(file);
        static Dictionary<Guid, XElement> Dashboards(XDocument d) => d.Root!.Element("Dashboards")!.Elements("Dashboard")
            .ToDictionary(e => Guid.Parse(e.Element("FormId")!.Value), e => XElement.Parse(e.ToString(SaveOptions.DisableFormatting)));
        var before = Dashboards(original);
        var after = Dashboards(customizations);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach (var (id, dashboard) in before)
            XNode.DeepEquals(after[id], dashboard).Should().BeTrue("dashboard {0} is written back as it came", id);
    }
}
