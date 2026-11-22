using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Pipeline.Registration;
using Xrmd.Schema;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// ImportSolution and ExportSolution of calculated columns (plan 2.12a, Task 5, ruling K13; Review Focus 4): the fixture solution with
/// <c>xv_project.xv_total</c> (two columns of its own record) and <c>xv_task.xv_label</c> (a column of the parent record through the
/// lookup <c>xv_projectid</c>), all created by the same import (<see cref="CalculatedSolutionFixture"/>). Runs on the solution import
/// database (the fixture's tables are created and dropped by every test).
/// </summary>
[Collection("solution-import")]
public sealed class CalculatedSolutionImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanAsync();
    }

    public Task DisposeAsync() => CleanAsync();

    /// <summary>What an import of the fixture creates, and the layers of a managed one.</summary>
    private async Task CleanAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("DELETE FROM meta.component_layer; DELETE FROM meta.solution_rank;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> FixtureSolutionIdAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT solutionid FROM data.solution WHERE uniquename = @n", conn);
        cmd.Parameters.AddWithValue("@n", SolutionFixtureZip.FixtureName);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<OrganizationResponse> ImportAsync(byte[] zip) =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });

    private AttributeMetadata Column(string entity, string column) =>
        Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == column);

    private async Task<(Guid Project, Guid Task)> RowsAsync()
    {
        var project = await Admin.CreateAsync(new Entity("xv_project")
        {
            ["xv_name"] = "Apollo", ["xv_budget"] = new Money(12.5m), ["xv_count"] = 4,
        });
        var task = await Admin.CreateAsync(new Entity("xv_task") { ["xv_name"] = "Design", ["xv_projectid"] = new EntityReference("xv_project", project) });
        return (project, task);
    }

    private async Task<object?> ValueAsync(string entity, Guid id, string column)
    {
        var row = await Admin.RetrieveAsync(entity, id, new ColumnSet(column));
        return row.Contains(column) ? row[column] : null;
    }

    [Fact]
    public async Task Import_resolves_same_solution_references_and_updates_formula()
    {
        await ImportAsync(CalculatedSolutionFixture.Build());

        var label = Column("xv_task", "xv_label");
        label.SourceType.Should().Be(1);
        ComputedColumnRules.FormulaOf(label).Should().Be(CalculatedSolutionFixture.LabelXaml());
        label.IsValidForCreate.Should().BeFalse();
        ComputedColumnRules.MaskOf(label).Should().Be(1 | 2, "it reads a column of its own record and one of the parent record");
        Column("xv_project", "xv_total").SourceType.Should().Be(1);
        var (project, task) = await RowsAsync();
        (await ValueAsync("xv_task", task, "xv_label")).Should().Be("Design / Apollo");
        ((Money)(await ValueAsync("xv_project", project, "xv_total"))!).Value.Should().Be(50m);

        // A re-import (upgrade) of the same solution with another formula: the definition and the values change.
        await ImportAsync(CalculatedSolutionFixture.Build(separator: " - "));

        ComputedColumnRules.FormulaOf(Column("xv_task", "xv_label")).Should().Be(CalculatedSolutionFixture.LabelXaml(" - "));
        (await ValueAsync("xv_task", task, "xv_label")).Should().Be("Design - Apollo");
    }

    [Fact]
    public async Task Export_writes_the_formula_files_byte_for_byte()
    {
        await ImportAsync(CalculatedSolutionFixture.Build());

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = SolutionFixtureZip.FixtureName, Managed = false,
        })).ExportSolutionFile;

        var (_, customizations, files) = SolutionZip.Read(exported);
        var expected = CalculatedSolutionFixture.Files();
        files[CalculatedSolutionFixture.LabelPath].Should().Equal(expected[CalculatedSolutionFixture.LabelPath]);
        files[CalculatedSolutionFixture.TotalPath].Should().Equal(expected[CalculatedSolutionFixture.TotalPath]);
        var element = CalculatedSolutionFixture.Column(customizations, "xv_Label");
        element.Element("SourceType")!.Value.Should().Be("1");
        element.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + CalculatedSolutionFixture.LabelPath);

        // The export imports again unchanged.
        await ImportAsync(exported);
        ComputedColumnRules.FormulaOf(Column("xv_task", "xv_label")).Should().Be(CalculatedSolutionFixture.LabelXaml());
    }

    [Fact]
    public async Task Schema_export_and_apply_round_trip_calculated_on_the_server()
    {
        await ImportAsync(CalculatedSolutionFixture.Build());
        var (_, task) = await RowsAsync();

        // Export: the calculated columns with their XAML; applying the export plans nothing.
        var yaml = SchemaYaml.Write(SchemaExporter.Export(Store.GetAllEntities(), Store.GetAllOptionSets(), "xv"));
        var document = SchemaYaml.Read(yaml);
        var label = document.Tables.Single(t => t.Name == "xv_task").Columns.Single(c => c.Name == "xv_label");
        label.Calculated.Should().Be(CalculatedSolutionFixture.LabelXaml());
        SchemaDiff.Compute(document, Store.GetAllEntities(), Store.GetAllOptionSets()).Changes.Should().BeEmpty();

        // Apply: a changed formula is an UpdateAttribute, a new calculated column a CreateAttribute (K3: the server checks both).
        label.Calculated = CalculatedSolutionFixture.LabelXaml(" | ");
        document.Tables.Single(t => t.Name == "xv_task").Columns.Add(new SchemaColumn
        {
            Name = "xv_label2", Type = SchemaColumnTypes.String, MaxLength = 300, Label = "Label 2",
            Calculated = CalculatedSolutionFixture.LabelXaml(" + ").Replace("xv_label", "xv_label2", StringComparison.Ordinal),
        });
        var plan = SchemaDiff.Compute(document, Store.GetAllEntities(), Store.GetAllOptionSets());
        plan.Rejections.Should().BeEmpty();
        plan.Changes.Should().HaveCount(2);
        foreach (var request in plan.Changes) await Admin.ExecuteAsync(request);

        (await ValueAsync("xv_task", task, "xv_label")).Should().Be("Design | Apollo");
        (await ValueAsync("xv_task", task, "xv_label2")).Should().Be("Design + Apollo");
        Column("xv_task", "xv_label2").SourceType.Should().Be(1);
    }

    // ---- removal (fix round 1): the calculated columns of the removed set go first ----------------------------------

    [Fact]
    public async Task Cascade_delete_of_the_unmanaged_solution_removes_its_calculated_columns()
    {
        await ImportAsync(CalculatedSolutionFixture.Build());

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/admin/solutions/{SolutionFixtureZip.FixtureName}?cascade=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Store.GetEntity("xv_task").Should().BeNull();
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task Uninstall_of_the_managed_solution_removes_its_calculated_columns()
    {
        await ImportAsync(CalculatedSolutionFixture.Build(CalculatedSolutionFixture.DefaultSeparator, null, CalculatedSolutionFixture.Managed()));
        Column("xv_task", "xv_label").SourceType.Should().Be(1);

        await Admin.DeleteAsync("solution", await FixtureSolutionIdAsync());

        Store.GetEntity("xv_task").Should().BeNull();
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task Upgrade_removing_a_calculated_column_and_a_column_it_reads_succeeds()
    {
        await ImportAsync(CalculatedSolutionFixture.Build(CalculatedSolutionFixture.DefaultSeparator, null, CalculatedSolutionFixture.Managed()));

        // Version 2 no longer has xv_total nor xv_count, which xv_total reads: whatever their ids, xv_total goes first.
        await Admin.ExecuteAsync(new StageAndUpgradeRequest
        {
            CustomizationFile = CalculatedSolutionFixture.Build(CalculatedSolutionFixture.DefaultSeparator,
                d =>
                {
                    CalculatedSolutionFixture.Column(d, "xv_Total").Remove();
                    CalculatedSolutionFixture.Column(d, "xv_Count").Remove();
                },
                CalculatedSolutionFixture.Managed("2.0.0.0"), CalculatedSolutionFixture.TotalPath),
            ImportJobId = Guid.NewGuid(),
        });

        var project = Store.GetEntity("xv_project")!.Attributes.Select(a => a.LogicalName).ToList();
        project.Should().NotContain(["xv_total", "xv_count"]).And.Contain("xv_budget");
        Column("xv_task", "xv_label").SourceType.Should().Be(1);
    }

    [Fact]
    public async Task The_remover_plans_calculated_columns_before_the_columns_they_read()
    {
        await ImportAsync(CalculatedSolutionFixture.Build());
        var total = Column("xv_project", "xv_total").MetadataId!.Value;
        var count = Column("xv_project", "xv_count").MetadataId!.Value;
        var relationship = Store.GetOneToManyRelationships().Single(r => r.SchemaName == "xv_project_xv_task_ProjectId").MetadataId!.Value;
        var label = Column("xv_task", "xv_label").MetadataId!.Value;

        using var scope = fx.App.Services.CreateScope();
        var remover = scope.ServiceProvider.GetRequiredService<Xrmd.Core.Solutions.ComponentRemover>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var plan = await remover.PlanAsync(new DataSession(conn, null),
            [(ComponentType.Attribute, count), (ComponentType.Attribute, total), (ComponentType.EntityRelationship, relationship), (ComponentType.Attribute, label)],
            default);

        var ordered = plan.Ordered.Select(o => o.Id).ToList();
        ordered.IndexOf(total).Should().BeLessThan(ordered.IndexOf(count));
        ordered.IndexOf(label).Should().BeLessThan(ordered.IndexOf(relationship));
    }

    [Fact]
    public async Task A_calculated_primary_name_fails_the_import_and_writes_nothing()
    {
        // Final review I3: xv_tag's primary name column made calculated.
        const string path = "Formulas/xv_tag-xv_name.xaml";
        var files = CalculatedSolutionFixture.Files();
        files[path] = System.Text.Encoding.UTF8.GetBytes(CalculatedSolutionFixture.LabelXaml()
            .Replace("xv_task", "xv_tag", StringComparison.Ordinal).Replace("xv_label", "xv_name", StringComparison.Ordinal));

        var message = await TestHelpers.BadRequestAsync(() => ImportAsync(SolutionFixtureZip.BuildWithFiles(d =>
        {
            CalculatedSolutionFixture.AddColumns(d);
            var name = CalculatedSolutionFixture.Attributes(d, "xv_Tag").Elements("attribute")
                .Single(a => (string?)a.Attribute("PhysicalName") == "xv_Name");
            name.Element("SourceType")!.Value = "1";
            name.Element("SourceType")!.AddAfterSelf(new System.Xml.Linq.XElement("FormulaDefinitionFileName", "/" + path));
        }, null, files)));

        message.Should().Contain("The primary name column of a table cannot be a calculated column: 'xv_tag.xv_name'.");
        Store.GetEntity("xv_tag").Should().BeNull();
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task Uninstall_removes_a_calculated_column_that_reads_another_one_of_the_solution()
    {
        // Deferred item 10: xv_project.xv_half = xv_total / 2, both calculated and in the solution. Whatever their ids, xv_half
        // goes before xv_total, then the columns they read.
        const string path = "Formulas/xv_project-xv_half.xaml";
        var files = CalculatedSolutionFixture.Files();
        files[path] = System.Text.Encoding.UTF8.GetBytes(CalculatedSolutionFixture.Fixture("chained")
            .Replace("xrmv_calcorder", "xv_project", StringComparison.Ordinal).Replace("xrmv_double", "xv_half", StringComparison.Ordinal)
            .Replace("\"xrmv_total\"", "\"xv_total\"", StringComparison.Ordinal)
            // xv_half is a Currency column: its steps declare Money, as the 2.12a parity export writes the column's type.
            .Replace("Value=\"x:Decimal\"", "Value=\"mxs:Money\"", StringComparison.Ordinal));
        var zip = SolutionFixtureZip.BuildWithFiles(d =>
        {
            CalculatedSolutionFixture.AddColumns(d);
            var half = new System.Xml.Linq.XElement(CalculatedSolutionFixture.Column(d, "xv_Total"));
            half.SetAttributeValue("PhysicalName", "xv_Half");
            half.Element("Name")!.Value = "xv_half";
            half.Element("LogicalName")!.Value = "xv_half";
            half.Element("FormulaDefinitionFileName")!.Value = "/" + path;
            CalculatedSolutionFixture.Attributes(d, "xv_Project").Add(half);
        }, CalculatedSolutionFixture.Managed(), files);
        await ImportAsync(zip);
        Column("xv_project", "xv_half").SourceType.Should().Be(1);
        var (project, task) = await RowsAsync();
        ((Money)(await ValueAsync("xv_project", project, "xv_half"))!).Value.Should().Be(25m);
        // An uninstall that would drop rows needs the Admin API: the rows go first.
        await Admin.DeleteAsync("xv_task", task);
        await Admin.DeleteAsync("xv_project", project);

        var half = Column("xv_project", "xv_half").MetadataId!.Value;
        var total = Column("xv_project", "xv_total").MetadataId!.Value;
        using (var scope = fx.App.Services.CreateScope())
        {
            var remover = scope.ServiceProvider.GetRequiredService<Xrmd.Core.Solutions.ComponentRemover>();
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            // Listed with the column read first: the plan still puts its reader first.
            var plan = await remover.PlanAsync(new DataSession(conn, null),
                [(ComponentType.Attribute, total), (ComponentType.Attribute, half)], default);
            var ordered = plan.Ordered.Select(o => o.Id).ToList();
            ordered.IndexOf(half).Should().BeLessThan(ordered.IndexOf(total));
        }

        await Admin.DeleteAsync("solution", await FixtureSolutionIdAsync());

        Store.GetEntity("xv_project").Should().BeNull();
        Store.GetEntity("xv_task").Should().BeNull();
    }

    [Fact]
    public async Task A_missing_formula_file_fails_the_import_and_writes_nothing()
    {
        var message = await TestHelpers.BadRequestAsync(() => ImportAsync(CalculatedSolutionFixture.Build(omit: CalculatedSolutionFixture.TotalPath)));

        message.Should().Contain("'Formulas/xv_project-xv_total.xaml'");
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task A_formula_the_server_cannot_compute_fails_the_import_and_writes_nothing()
    {
        var files = CalculatedSolutionFixture.Files();
        files[CalculatedSolutionFixture.LabelPath] = System.Text.Encoding.UTF8.GetBytes(
            CalculatedSolutionFixture.LabelXaml().Replace("Attribute=\"xv_name\" Entity=\"[InputEntities(&quot;related_", "Attribute=\"xv_nothere\" Entity=\"[InputEntities(&quot;related_", StringComparison.Ordinal));

        var message = await TestHelpers.BadRequestAsync(() => ImportAsync(SolutionFixtureZip.Build(CalculatedSolutionFixture.AddColumns, files)));

        message.Should().Contain("xv_nothere");
        Store.GetEntity("xv_task").Should().BeNull();
    }
}
