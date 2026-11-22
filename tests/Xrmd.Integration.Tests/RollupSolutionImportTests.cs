using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Solution;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests;

/// <summary>
/// ImportSolution, ExportSolution and uninstall of rollup columns (plan 2.12b, Task 6, ruling R14; Review Focus 5). The fixture
/// solution (<see cref="SolutionFixtureZip"/>) gets three computed columns that read one another, written in customizations.xml in the
/// order that would fail without the import's dependency order:
/// <list type="bullet">
/// <item><c>xv_project.xv_halftaskhours</c> (calculated) = <c>xv_taskhours / 2</c>: reads the rollup (mask 16);</item>
/// <item><c>xv_project.xv_taskhours</c> (rollup) = SUM of <c>xv_task.xv_halfhours</c> over <c>xv_project_xv_task_ProjectId</c>;</item>
/// <item><c>xv_task.xv_halfhours</c> (basic calculated) = <c>xv_hours / 2</c>, with the simple column <c>xv_hours</c>.</item>
/// </list>
/// The definitions are the parity export's <c>account-xrmv_ordercount.xaml</c> and the 2.12a fixture <c>chained.xaml</c>, renamed.
/// Each test has a host and a database of its own (the rollup jobs would otherwise reach the other tests); no worker runs unless the
/// test runs one.
/// </summary>
[Collection("xrmd")]
public sealed class RollupSolutionImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string ProjectTable = "xv_project", TaskTable = "xv_task";
    private const string RollupPath = "Formulas/xv_project-xv_taskhours.xaml";
    private const string HalfTaskHoursPath = "Formulas/xv_project-xv_halftaskhours.xaml";
    private const string HalfHoursPath = "Formulas/xv_task-xv_halfhours.xaml";
    private const int Mass = 58, Calculate = 57;

    private WebApplicationFactory<Program> _app = null!;
    private string _connectionString = "";

    private IServiceProvider Services => _app.Services;
    private MetadataStore Store => (MetadataStore)Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => TestServices.NewOrganizationService(Services, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        _app = fx.WithOwnDatabase(_ => { });
        _connectionString = Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
        await Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    // ---- the solution -----------------------------------------------------------------------------------------------------

    /// <summary>The rollup's XAML: the export's COUNT of orders made a SUM of <c>xv_task.<paramref name="aggregated"/></c>.</summary>
    private static string RollupXaml(string aggregated = "xv_halfhours", string aggregate = "Sum") =>
        File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "rollup", "real", "account-xrmv_ordercount.xaml"))
            .Replace("xrmv_calcorder_AccountId_account", "xv_project_xv_task_ProjectId", StringComparison.Ordinal)
            .Replace("xrmv_calcorderid", aggregated, StringComparison.Ordinal)
            .Replace("xrmv_calcorder", TaskTable, StringComparison.Ordinal)
            .Replace("xrmv_accountid", "xv_projectid", StringComparison.Ordinal)
            .Replace(">Count<", $">{aggregate}<", StringComparison.Ordinal);

    /// <summary>The 2.12a fixture <c>chained.xaml</c> (<c>xrmv_double = xrmv_total / 2</c>) as <paramref name="entity"/>.<paramref name="target"/> = <paramref name="source"/> / 2.</summary>
    private static string HalfXaml(string entity, string source, string target) => CalculatedSolutionFixture.Fixture("chained")
        .Replace("xrmv_calcorder", entity, StringComparison.Ordinal)
        .Replace("\"xrmv_total\"", $"\"{source}\"", StringComparison.Ordinal)
        .Replace("xrmv_double", target, StringComparison.Ordinal);

    private static Dictionary<string, byte[]> Files(string? rollupXaml = null) => new(StringComparer.Ordinal)
    {
        [RollupPath] = Encoding.UTF8.GetBytes(rollupXaml ?? RollupXaml()),
        [HalfTaskHoursPath] = Encoding.UTF8.GetBytes(HalfXaml(ProjectTable, "xv_taskhours", "xv_halftaskhours")),
        [HalfHoursPath] = Encoding.UTF8.GetBytes(HalfXaml(TaskTable, "xv_hours", "xv_halfhours")),
    };

    /// <summary>A decimal <c>attribute</c> element: simple (<paramref name="sourceType"/> 0) or computed with <paramref name="file"/>.</summary>
    private static XElement Decimal(string schemaName, int sourceType, string? file)
    {
        var name = schemaName.ToLowerInvariant();
        var computed = sourceType != 0;
        return new XElement("attribute", new XAttribute("PhysicalName", schemaName),
            new XElement("Type", "decimal"),
            new XElement("Name", name),
            new XElement("LogicalName", name),
            new XElement("RequiredLevel", "none"),
            new XElement("DisplayMask", "ValidForAdvancedFind|ValidForForm|ValidForGrid"),
            new XElement("ImeMode", "auto"),
            new XElement("ValidForUpdateApi", computed ? 0 : 1),
            new XElement("ValidForReadApi", 1),
            new XElement("ValidForCreateApi", computed ? 0 : 1),
            new XElement("IsCustomField", 1),
            new XElement("IsAuditEnabled", 0),
            new XElement("IsSecured", 0),
            new XElement("IntroducedVersion", "1.0.0.0"),
            new XElement("IsCustomizable", 1),
            new XElement("IsRenameable", 1),
            new XElement("CanModifySearchSettings", 1),
            new XElement("CanModifyRequirementLevelSettings", 1),
            new XElement("CanModifyAdditionalSettings", 1),
            new XElement("SourceType", sourceType),
            new XElement("IsGlobalFilterEnabled", 0),
            new XElement("IsSortableEnabled", 0),
            new XElement("AutoNumberFormat", ""),
            new XElement("MinValue", -100000000000),
            new XElement("MaxValue", 100000000000),
            new XElement("Accuracy", 2),
            new XElement("displaynames", new XElement("displayname", new XAttribute("description", schemaName), new XAttribute("languagecode", 1033))),
            new XElement("Descriptions", new XElement("Description", new XAttribute("description", ""), new XAttribute("languagecode", 1033))),
            file is null ? null : new XElement("FormulaDefinitionFileName", "/" + file));
    }

    /// <summary>
    /// Adds the columns, the reader of the rollup first and the rollup's sources last (the order a pass in file order would fail on).
    /// </summary>
    private static void AddColumns(XDocument d)
    {
        CalculatedSolutionFixture.Attributes(d, "xv_Project").Add(
            Decimal("xv_HalfTaskHours", 1, HalfTaskHoursPath),
            Decimal("xv_TaskHours", 2, RollupPath));
        CalculatedSolutionFixture.Attributes(d, "xv_Task").Add(
            Decimal("xv_HalfHours", 1, HalfHoursPath),
            Decimal("xv_Hours", 0, null));
    }

    private static byte[] Build(bool managed = false, string version = "1.0.0.0", string? rollupXaml = null) =>
        SolutionFixtureZip.BuildWithFiles(AddColumns, managed ? CalculatedSolutionFixture.Managed(version) : null, Files(rollupXaml));

    private async Task ImportAsync(byte[] zip)
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        await Store.RollupJobNotifications;
    }

    private AttributeMetadata? Column(string entity, string column) =>
        Store.GetEntity(entity)?.Attributes.SingleOrDefault(a => a.LogicalName == column);

    // ---- jobs -------------------------------------------------------------------------------------------------------------

    private sealed record Job(int Type, string? Entity, string? Attribute, DateTime? CompletedOn, int State, int Status, DateTime? PostponeUntil);

    private async Task<List<Job>> JobsAsync()
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(
            "SELECT operationtype, primaryentitytype, data, statecode, statuscode, postponeuntil FROM data.asyncoperation WHERE operationtype IN (57, 58)", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var jobs = new List<Job>();
        while (await rd.ReadAsync())
        {
            string? attribute = null;
            DateTime? completed = null;
            if (!rd.IsDBNull(2))
            {
                using var data = JsonDocument.Parse(rd.GetString(2));
                if (data.RootElement.TryGetProperty("attribute", out var a) && a.ValueKind == JsonValueKind.String) attribute = a.GetString();
                if (data.RootElement.TryGetProperty("completedOn", out var c) && c.ValueKind == JsonValueKind.String) completed = c.GetDateTime();
            }
            jobs.Add(new Job(rd.GetInt32(0), rd.IsDBNull(1) ? null : rd.GetString(1), attribute, completed, rd.GetInt32(3), rd.GetInt32(4),
                rd.IsDBNull(5) ? null : rd.GetDateTime(5)));
        }
        return jobs;
    }

    private async Task<Job> MassJobAsync() => (await JobsAsync()).Single(j => j.Type == Mass && j.Attribute == "xv_taskhours");

    /// <summary>Runs the jobs that are due (at most 20) with a worker of the test's own; returns how many ran.</summary>
    private async Task<int> RunJobsAsync()
    {
        var worker = new AsyncOperationWorker(Services, Services.GetRequiredService<AsyncOperationQueue>(),
            Microsoft.Extensions.Options.Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }), null,
            TimeProvider.System);
        var ran = 0;
        while (ran < 20 && await worker.RunOnceAsync(default)) ran++;
        return ran;
    }

    private async Task<Guid> SolutionIdAsync(string uniqueName = SolutionFixtureZip.FixtureName)
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command("SELECT solutionid FROM data.solution WHERE uniquename = @n", conn);
        cmd.Parameters.AddWithValue("@n", uniqueName);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- tests ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_orders_basic_calculated_rollup_and_calculated_reading_rollup()
    {
        await ImportAsync(Build());

        var rollup = Column(ProjectTable, "xv_taskhours")!;
        rollup.SourceType.Should().Be(RollupColumns.Rollup);
        ComputedColumnRules.FormulaOf(rollup).Should().Be(RollupXaml(), "the XAML is kept verbatim");
        ComputedColumnRules.MaskOf(rollup).Should().Be(0);
        Column(ProjectTable, "xv_taskhours_date").Should().BeOfType<DateTimeAttributeMetadata>("the server generates the companions");
        Column(ProjectTable, "xv_taskhours_state").Should().BeOfType<IntegerAttributeMetadata>();
        ComputedColumnRules.MaskOf(Column(TaskTable, "xv_halfhours")!).Should().Be(1, "a basic calculated column");
        ComputedColumnRules.MaskOf(Column(ProjectTable, "xv_halftaskhours")!).Should().Be(16, "a calculated column reading a rollup");

        // The definitions work: the Mass job (due at once) sums the halves, the calculated column halves the sum.
        var project = await Admin.CreateAsync(new Entity(ProjectTable) { ["xv_name"] = "Apollo" });
        foreach (var hours in new[] { 10m, 3m })
            await Admin.CreateAsync(new Entity(TaskTable) { ["xv_name"] = "t", ["xv_hours"] = hours, ["xv_projectid"] = new EntityReference(ProjectTable, project) });
        await RunJobsAsync();
        var row = await Admin.RetrieveAsync(ProjectTable, project, new Microsoft.Xrm.Sdk.Query.ColumnSet("xv_taskhours", "xv_taskhours_state", "xv_halftaskhours"));
        row.GetAttributeValue<decimal?>("xv_taskhours").Should().Be(6.5m);
        row.GetAttributeValue<int?>("xv_taskhours_state").Should().Be((int)RollupState.Calculated);
        row.GetAttributeValue<decimal?>("xv_halftaskhours").Should().Be(3.25m);
    }

    [Fact]
    public async Task Import_queues_mass_jobs_immediately()
    {
        var before = DateTime.UtcNow;
        await ImportAsync(Build());

        var mass = await MassJobAsync();
        mass.Entity.Should().Be(ProjectTable);
        (mass.State, mass.Status).Should().Be((1, 10));
        mass.PostponeUntil.Should().BeCloseTo(before, TimeSpan.FromMinutes(1), "R8: the Mass job of an imported rollup is due at once");
        mass.CompletedOn.Should().BeNull();
        (await JobsAsync()).Should().ContainSingle(j => j.Type == Calculate && j.Entity == ProjectTable, "the table's incremental job");
        (await RunJobsAsync()).Should().Be(1, "the Mass job only: the incremental job waits for its hour");
        (await MassJobAsync()).CompletedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task Reimport_with_changed_definition_updates_and_resets_mass_job()
    {
        await ImportAsync(Build());
        await RunJobsAsync();
        (await MassJobAsync()).CompletedOn.Should().NotBeNull();
        (await MassJobAsync()).PostponeUntil.Should().BeAfter(DateTime.UtcNow.AddYears(9), "after a run the Mass job sleeps");

        var before = DateTime.UtcNow;
        var changed = RollupXaml(aggregate: "Maximum");
        await ImportAsync(Build(rollupXaml: changed));

        ComputedColumnRules.FormulaOf(Column(ProjectTable, "xv_taskhours")!).Should().Be(changed);
        var mass = await MassJobAsync();
        mass.CompletedOn.Should().BeNull("the job's last run computed the old definition");
        mass.PostponeUntil.Should().BeCloseTo(before, TimeSpan.FromMinutes(1), "due at once again, as after any import");
        (await JobsAsync()).Count(j => j.Type == Mass).Should().Be(1);
    }

    [Fact]
    public async Task Uninstall_removes_calculated_then_rollup_then_sources()
    {
        await ImportAsync(Build(managed: true));

        // Review Focus 5: a source of the rollup cannot be deleted while the rollup reads it.
        var fault = await FluentActions.Awaiting(() => TestEntities.InSessionAsync(_connectionString,
                s => Store.DeleteAttributeAsync(s, TaskTable, "xv_halfhours", CancellationToken.None)))
            .Should().ThrowAsync<XrmdFaultException>();
        fault.Which.ErrorCode.Should().Be(ErrorCodes.RollupFieldDependentFieldCannotDeleted);
        fault.Which.Message.Should().Contain("xv_project.xv_taskhours");

        // The remover's plan, whatever the order the components are listed in: the calculated column reading the rollup, the rollup,
        // the basic calculated column, then the columns and the relationship they read.
        var ids = new Dictionary<string, Guid>
        {
            ["hours"] = Column(TaskTable, "xv_hours")!.MetadataId!.Value,
            ["halfhours"] = Column(TaskTable, "xv_halfhours")!.MetadataId!.Value,
            ["taskhours"] = Column(ProjectTable, "xv_taskhours")!.MetadataId!.Value,
            ["halftaskhours"] = Column(ProjectTable, "xv_halftaskhours")!.MetadataId!.Value,
            ["relationship"] = Store.GetOneToManyRelationships().Single(r => r.SchemaName == "xv_project_xv_task_ProjectId").MetadataId!.Value,
        };
        using (var scope = Services.CreateScope())
        {
            var remover = scope.ServiceProvider.GetRequiredService<Xrmd.Core.Solutions.ComponentRemover>();
            await using var conn = await TestProvider.OpenAsync(_connectionString);
            var plan = await remover.PlanAsync(new DataSession(conn, null),
                [.. new[] { "hours", "halfhours", "relationship", "taskhours", "halftaskhours" }.Select(k =>
                    (k == "relationship" ? ComponentType.EntityRelationship : ComponentType.Attribute, ids[k]))], default);
            var ordered = plan.Ordered.Select(o => o.Id).ToList();
            ordered.IndexOf(ids["halftaskhours"]).Should().BeLessThan(ordered.IndexOf(ids["taskhours"]));
            ordered.IndexOf(ids["taskhours"]).Should().BeLessThan(ordered.IndexOf(ids["halfhours"]));
            ordered.IndexOf(ids["halfhours"]).Should().BeLessThan(ordered.IndexOf(ids["hours"]));
            ordered.IndexOf(ids["taskhours"]).Should().BeLessThan(ordered.IndexOf(ids["relationship"]));
        }

        await Admin.DeleteAsync("solution", await SolutionIdAsync());

        Store.GetEntity(ProjectTable).Should().BeNull();
        Store.GetEntity(TaskTable).Should().BeNull();
        await Store.RollupJobNotifications;
        (await JobsAsync()).Should().BeEmpty("the jobs of the rollup go with it");
    }

    [Fact]
    public async Task Upgrade_removing_the_rollup_and_its_reader_keeps_the_sources()
    {
        await ImportAsync(Build(managed: true));

        // Version 2 has neither the rollup nor the calculated column reading it: the reader goes first, then the rollup with its
        // companions; the rollup's sources stay.
        await Admin.ExecuteAsync(new StageAndUpgradeRequest
        {
            CustomizationFile = SolutionFixtureZip.BuildWithFiles(d =>
            {
                AddColumns(d);
                CalculatedSolutionFixture.Column(d, "xv_TaskHours").Remove();
                CalculatedSolutionFixture.Column(d, "xv_HalfTaskHours").Remove();
            }, CalculatedSolutionFixture.Managed("2.0.0.0"), new Dictionary<string, byte[]> { [HalfHoursPath] = Files()[HalfHoursPath] }),
            ImportJobId = Guid.NewGuid(),
        });

        Store.GetEntity(ProjectTable)!.Attributes.Select(a => a.LogicalName).Should()
            .NotContain(["xv_taskhours", "xv_taskhours_date", "xv_taskhours_state", "xv_halftaskhours"]);
        Column(TaskTable, "xv_halfhours")!.SourceType.Should().Be(1);
    }

    [Fact]
    public async Task Uninstalling_an_upper_layer_restores_the_lower_rollup_definition_and_its_mass_job_is_due_at_once()
    {
        const string upper = "xv_rollupmax";
        await ImportAsync(Build(managed: true));
        // A second managed solution layers the same columns, the rollup a MAX instead of a SUM.
        var maximum = RollupXaml(aggregate: "Maximum");
        // Only the tables, their option set and relationships (the fixture's role, plug-ins, steps and app are not layered here).
        await ImportAsync(SolutionFixtureZip.BuildWithFiles(d =>
        {
            AddColumns(d);
            foreach (var name in new[] { "Roles", "SolutionPluginAssemblies", "SdkMessageProcessingSteps", "WebResources", "AppModuleSiteMaps", "AppModules" })
                d.Root!.Element(name)?.RemoveNodes();
        }, d =>
        {
            CalculatedSolutionFixture.Managed()(d);
            d.Descendants("UniqueName").First().Value = upper;
            d.Descendants("RootComponent").Where(r => (string?)r.Attribute("type") is not ("1" or "9")).Remove();
        }, Files(maximum)));
        ComputedColumnRules.FormulaOf(Column(ProjectTable, "xv_taskhours")!).Should().Be(maximum);
        await RunJobsAsync();
        (await MassJobAsync()).PostponeUntil.Should().BeAfter(DateTime.UtcNow.AddYears(9), "after a run the Mass job sleeps");

        var before = DateTime.UtcNow;
        await Admin.DeleteAsync("solution", await SolutionIdAsync(upper));
        await Store.RollupJobNotifications;

        ComputedColumnRules.FormulaOf(Column(ProjectTable, "xv_taskhours")!).Should().Be(RollupXaml(), "the lower layer's definition is back");
        var mass = await MassJobAsync();
        mass.CompletedOn.Should().BeNull("the job's last run computed the upper layer's definition");
        mass.PostponeUntil.Should().BeCloseTo(before, TimeSpan.FromMinutes(1), "R8, R14: a restored rollup is due at once, as an imported one");
    }

    [Fact]
    public async Task Export_writes_rollup_files_byte_for_byte_and_no_companions()
    {
        await ImportAsync(Build());

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = SolutionFixtureZip.FixtureName, Managed = false,
        })).ExportSolutionFile;

        var (_, customizations, files) = SolutionZip.Read(exported);
        foreach (var (path, bytes) in Files()) files[path].Should().Equal(bytes, "the XAML of {0} is written back byte for byte", path);
        var rollup = CalculatedSolutionFixture.Column(customizations, "xv_TaskHours");
        rollup.Element("SourceType")!.Value.Should().Be("2");
        rollup.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + RollupPath);
        customizations.Descendants("attribute").Select(a => a.Element("LogicalName")?.Value)
            .Should().NotContain(["xv_taskhours_date", "xv_taskhours_state"], "the companions are the server's");

        // The export imports again (into the same organization: an update of each column).
        await ImportAsync(exported);
        Column(ProjectTable, "xv_taskhours")!.SourceType.Should().Be(RollupColumns.Rollup);
    }
}
