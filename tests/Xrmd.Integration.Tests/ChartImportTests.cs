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
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1f, task 1 (ruling F2): the <c>Visualizations</c> of an entity are imported as <c>savedqueryvisualization</c> rows
/// (component type 59), exported back under their entity, no longer kept as unsupported XML, and read by a Basic User. The
/// charts are written by hand (<see cref="SolutionFixtureZip.AddCharts"/>).
/// </summary>
[Collection("solution-import")]
public sealed class ChartImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    private Task ImportAsync(Action<XDocument>? customizations = null) => Admin.ExecuteAsync(new ImportSolutionRequest
    {
        CustomizationFile = SolutionFixtureZip.Build(customizations ?? SolutionFixtureZip.AddCharts), ImportJobId = Guid.NewGuid(),
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

    private static XElement ProjectEntity(XDocument customizations) =>
        customizations.Root!.Element("Entities")!.Elements("Entity").Single(e => e.Element("Name")!.Value == "xv_Project");

    [Fact]
    public async Task Import_reads_visualizations_into_rows()
    {
        await ImportAsync();

        var rows = await RowsAsync("""
            SELECT savedqueryvisualizationid, name, primaryentitytypecode, datadescription, presentationdescription, isdefault, charttype,
                type, description, introducedversion, webresourceid, xrmverse_componentxml
            FROM data.savedqueryvisualization ORDER BY savedqueryvisualizationid
            """);
        rows.Should().HaveCount(2);
        var amount = rows.Single(r => (Guid)r["savedqueryvisualizationid"]! == SolutionFixtureZip.AmountChartId);
        amount["name"].Should().Be("Budget by Priority", "the name is the 1033 LocalizedName");
        amount["primaryentitytypecode"].Should().Be("xv_project");
        var data = XElement.Parse((string)amount["datadescription"]!);
        data.Name.LocalName.Should().Be("datadefinition");
        data.Descendants("attribute").Select(a => (string?)a.Attribute("name")).Should().Equal("xv_budget", "xv_priority");
        var presentation = XElement.Parse((string)amount["presentationdescription"]!);
        presentation.Name.LocalName.Should().Be("Chart");
        presentation.Descendants("Series").Last().Attribute("ChartType")!.Value.Should().Be("Bar");
        amount["isdefault"].Should().Be(false);
        amount["charttype"].Should().Be(0);
        amount["type"].Should().Be(0);
        amount["description"].Should().Be("Budget of the projects by priority");
        amount["introducedversion"].Should().Be("1.0.0.0");
        amount["webresourceid"].Should().BeNull();
        XElement.Parse((string)amount["xrmverse_componentxml"]!).Name.LocalName.Should().Be("visualization");
        var count = rows.Single(r => (Guid)r["savedqueryvisualizationid"]! == SolutionFixtureZip.CountChartId);
        count["name"].Should().Be("Projects by Status");
        count["isdefault"].Should().Be(true);

        var components = await RowsAsync($"""
            SELECT sc.objectid FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = N'{SolutionFixtureZip.FixtureName}' AND sc.componenttype = 59
            """);
        components.Select(c => (Guid)c["objectid"]!).Should().BeEquivalentTo([SolutionFixtureZip.AmountChartId, SolutionFixtureZip.CountChartId]);

        // Idempotent: a second import updates the rows.
        await ImportAsync();
        (await RowsAsync("SELECT savedqueryvisualizationid FROM data.savedqueryvisualization")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Export_writes_visualizations_back()
    {
        await ImportAsync();

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(
            new ExportSolutionRequest { SolutionName = SolutionFixtureZip.FixtureName, Managed = false })).ExportSolutionFile;
        var (_, customizations, _) = SolutionZip.Read(exported);
        var original = XDocument.Load(Path.Combine(SolutionFixtureZip.FixtureDirectory, "customizations.xml"));
        SolutionFixtureZip.AddCharts(original);

        var project = ProjectEntity(customizations);
        project.Elements().Select(e => e.Name.LocalName).Should().ContainInOrder("SavedQueries", "Visualizations", "RibbonDiffXml");
        var written = project.Element("Visualizations")!;
        written.Elements("visualization").Should().HaveCount(2);
        XNode.DeepEquals(Normalized(written), Normalized(ProjectEntity(original).Element("Visualizations")!))
            .Should().BeTrue("the export writes the charts as they were imported:\n{0}", written);
    }

    [Fact]
    public async Task Visualizations_are_no_longer_kept_xml()
    {
        await ImportAsync();

        var kept = (string?)(await AuthoringTestHelper.ScalarAsync(fx,
            "SELECT xrmverse_unsupportedxml FROM data.solution WHERE uniquename = @n", ("@n", SolutionFixtureZip.FixtureName)));
        if (kept is not null) XElement.Parse(kept).Descendants("Visualizations").Should().BeEmpty();
    }

    [Fact]
    public async Task App_module_chart_component_finds_its_row()
    {
        // #110: an app component of type 59 that names an imported chart is linked without the "no row" warning.
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
            {
                SolutionFixtureZip.AddCharts(c);
                c.Descendants("AppModuleComponents").Single().Add(new XElement("AppModuleComponent", new XAttribute("type", "59"),
                    new XAttribute("id", SolutionFixtureZip.AmountChartId.ToString("B"))));
            }),
            ImportJobId = jobId,
        });

        var log = (string)(await AuthoringTestHelper.ScalarAsync(fx, $"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        log.Should().NotContain("no savedqueryvisualization row");
        (await AuthoringTestHelper.ScalarAsync(fx,
            $"SELECT COUNT(*) FROM data.appmodulecomponent WHERE componenttype = 59 AND objectid = '{SolutionFixtureZip.AmountChartId}'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Basic_user_reads_charts()
    {
        await ImportAsync();
        var alice = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), alice);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/data/v9.2/savedqueryvisualizations({SolutionFixtureZip.AmountChartId})" +
            "?$select=name,primaryentitytypecode,datadescription,presentationdescription,charttype,_webresourceid_value");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(alice, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("name").GetString().Should().Be("Budget by Priority");
        json.RootElement.GetProperty("primaryentitytypecode").GetString().Should().Be("xv_project");
        json.RootElement.GetProperty("datadescription").GetString().Should().StartWith("<datadefinition");
        json.RootElement.GetProperty("presentationdescription").GetString().Should().StartWith("<Chart");
        json.RootElement.GetProperty("charttype").GetInt32().Should().Be(0);
        // No web resource: the lookup is null or left out (the server writes the columns of the row it read).
        if (json.RootElement.TryGetProperty("_webresourceid_value", out var webResource)) webResource.ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// An organization from before plan 3.5 UI-1f (Basic User without Read on the charts, no top-up recorded): startup grants it
    /// once at Global depth; a grant removed afterwards stays removed.
    /// </summary>
    [Fact]
    public async Task Chart_read_top_up_reaches_an_existing_organization_once()
    {
        const string step = "savedqueryvisualization-read";
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var read = PrivilegeNames.IdOf(PrivilegeNames.For(Microsoft.Crm.Sdk.Messages.AccessRights.ReadAccess, store.GetEntity(ChartSchema.Table)!));
        var basic = $"roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{read}'";
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        await AuthoringTestHelper.ScalarAsync(fx, $"DELETE FROM data.roleprivileges WHERE {basic}; DELETE FROM meta.upgrade_step WHERE name = N'{step}';");
        try
        {
            await bootstrap.EnsureAsync(default);
            (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT privilegedepthmask FROM data.roleprivileges WHERE {basic}")).Should().Be(8);
            (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);

            await AuthoringTestHelper.ScalarAsync(fx, $"DELETE FROM data.roleprivileges WHERE {basic}");
            await bootstrap.EnsureAsync(default);
            (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic}")).Should().Be(0);
        }
        finally
        {
            await AuthoringTestHelper.ScalarAsync(fx, $"""
                IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE {basic})
                    INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                    VALUES (NEWID(), '{StandardRoles.BasicUser}', '{read}', 8, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """);
            Cache(fx).Invalidate();
        }
    }

    // ---- fix round 1 ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_keeps_the_webresourceid_as_it_came()
    {
        await ImportAsync(SolutionFixtureZip.AddChartsWithWebResource);
        (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT webresourceid FROM data.savedqueryvisualization WHERE savedqueryvisualizationid = '{SolutionFixtureZip.CountChartId}'"))
            .Should().Be(Guid.Parse(SolutionFixtureZip.WebResourceIdAsWritten));

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(
            new ExportSolutionRequest { SolutionName = SolutionFixtureZip.FixtureName, Managed = false })).ExportSolutionFile;
        var (_, customizations, _) = SolutionZip.Read(exported);

        var pie = ProjectEntity(customizations).Element("Visualizations")!.Elements("visualization")
            .Single(v => Guid.Parse(v.Element("savedqueryvisualizationid")!.Value) == SolutionFixtureZip.CountChartId);
        pie.Element("webresourceid")!.Value.Should().Be(SolutionFixtureZip.WebResourceIdAsWritten, "an id is written as it came (braces and case)");
    }

    [Fact]
    public async Task Uninstall_removes_a_chart_and_the_web_resource_it_names()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(SolutionFixtureZip.AddChartsWithWebResource,
                solution => solution.Descendants("Managed").Single().Value = "1"),
            ImportJobId = Guid.NewGuid(),
        });
        var solutionId = (Guid)(await AuthoringTestHelper.ScalarAsync(fx, "SELECT solutionid FROM data.solution WHERE uniquename = @n",
            ("@n", SolutionFixtureZip.FixtureName)))!;

        await Admin.DeleteAsync("solution", solutionId);

        (await AuthoringTestHelper.ScalarAsync(fx, "SELECT COUNT(*) FROM data.savedqueryvisualization")).Should().Be(0);
        (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT COUNT(*) FROM data.webresource WHERE webresourceid = '{Guid.Parse(SolutionFixtureZip.WebResourceIdAsWritten)}'"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Chart_isdefault_error_names_the_chart()
    {
        var act = () => ImportAsync(c =>
        {
            SolutionFixtureZip.AddCharts(c);
            c.Descendants("visualization").First().Element("isdefault")!.Value = "2";
        });

        (await act.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which.Message
            .Should().Contain($"Chart {SolutionFixtureZip.AmountChartId:D} has the value '2' for isdefault");
    }

    [Fact]
    public async Task System_customizer_creates_and_reads_charts()
    {
        await ImportAsync();
        var customizer = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), customizer);
        await AssignRoleAsync(fx, userId, StandardRoles.SystemCustomizer);
        var service = ServiceFor(fx, customizer);

        var id = await service.CreateAsync(new Microsoft.Xrm.Sdk.Entity(ChartSchema.Table)
        {
            [ChartSchema.Name] = "Made by a customizer", [ChartSchema.PrimaryEntityTypeCode] = "xv_project",
            [ChartSchema.DataDescription] = "<datadefinition />", [ChartSchema.PresentationDescription] = "<Chart />",
        });
        (await service.RetrieveAsync(ChartSchema.Table, id, new Microsoft.Xrm.Sdk.Query.ColumnSet(ChartSchema.Name)))
            .GetAttributeValue<string>(ChartSchema.Name).Should().Be("Made by a customizer");
    }

    /// <summary>An organization from before the fix: System Customizer gets its chart privileges once, by the top-up.</summary>
    [Fact]
    public async Task Chart_customizer_top_up_reaches_an_existing_organization_once()
    {
        const string step = "savedqueryvisualization-customizer-privileges";
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var create = PrivilegeNames.IdOf(PrivilegeNames.For(Microsoft.Crm.Sdk.Messages.AccessRights.CreateAccess, store.GetEntity(ChartSchema.Table)!));
        var customizer = $"roleid = '{StandardRoles.SystemCustomizer}' AND privilegeid = '{create}'";
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        await AuthoringTestHelper.ScalarAsync(fx, $"DELETE FROM data.roleprivileges WHERE {customizer}; DELETE FROM meta.upgrade_step WHERE name = N'{step}';");
        try
        {
            await bootstrap.EnsureAsync(default);
            (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT privilegedepthmask FROM data.roleprivileges WHERE {customizer}")).Should().Be(8);
            (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);

            await AuthoringTestHelper.ScalarAsync(fx, $"DELETE FROM data.roleprivileges WHERE {customizer}");
            await bootstrap.EnsureAsync(default);
            (await AuthoringTestHelper.ScalarAsync(fx, $"SELECT COUNT(*) FROM data.roleprivileges WHERE {customizer}")).Should().Be(0);
        }
        finally
        {
            await AuthoringTestHelper.ScalarAsync(fx, $"""
                IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE {customizer})
                    INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                    VALUES (NEWID(), '{StandardRoles.SystemCustomizer}', '{create}', 8, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """);
            Cache(fx).Invalidate();
        }
    }
}

/// <summary>
/// Plan 3.5 UI-1f (Appendix B): the three charts of <c>op365_purchaseorder</c> in the unmodified OpenProject365 export become
/// rows. On the real-export database, as the other tests of the export.
/// </summary>
[Collection("real-export")]
public sealed class OpenProject365ChartImportTests(XrmdFixture fx)
{
    private static readonly string Directory = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports");

    [OpenProject365Fact]
    public async Task OpenProject365_charts_import()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = File.ReadAllBytes(Path.Combine(Directory, "OpenProject365_1_0_1_1.zip")), ImportJobId = Guid.NewGuid(),
        });

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(
            "SELECT name, primaryentitytypecode FROM data.savedqueryvisualization WHERE primaryentitytypecode = N'op365_purchaseorder'", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var charts = new List<(string, string)>();
        while (await rd.ReadAsync()) charts.Add((rd.GetString(0), rd.GetString(1)));

        charts.Select(c => c.Item1).Should().BeEquivalentTo("Spend by Supplier", "Spend by Project", "Purchase Orders by Status");
    }
}
