using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The real export tests run on a database of their own: the solutions they import are never removed.</summary>
[CollectionDefinition("real-export")]
public class RealExportCollection : ICollectionFixture<XrmdFixture>;

/// <summary>
/// ImportSolution of unmodified exports of real Dataverse environments (<c>tests/fixtures/solutions/RealExports</c>): what
/// a real file contains that the hand-written fixture does not (sdkmessage ids, archive layout, component shapes).
/// </summary>
[Collection("real-export")]
public sealed class RealExportImportTests(XrmdFixture fx) : IAsyncLifetime
{
    // The processes its tests activate run on every matching write of the collection (plan 2.14, Task 5): none is left activated.
    public Task InitializeAsync() => WorkflowTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => WorkflowTestHelper.ClearAsync(fx);

    private static readonly string Directory = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports");

    private static byte[] OpenProject365() => File.ReadAllBytes(Path.Combine(Directory, "OpenProject365_1_0_1_1.zip"));

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    [OpenProject365Fact]
    public async Task OpenProject365_imports_its_plug_ins_and_steps_and_round_trips_what_it_does_not_apply()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var admin = ServiceFor(fx, fx.AdminObjectId);

        // The unmodified export: its six autonumber columns (five of them primary names) are applied.
        await admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = OpenProject365(), ImportJobId = Guid.NewGuid() });

        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        store.GetEntity("op365_project").Should().NotBeNull();
        var autoNumbered = store.GetAllEntities().SelectMany(e => (e.Attributes ?? []).Where(a => !string.IsNullOrEmpty(a.AutoNumberFormat))
            .Select(a => $"{e.LogicalName}.{a.LogicalName}={a.AutoNumberFormat}")).ToList();
        autoNumbered.Should().HaveCount(6).And.Contain("op365_project.op365_code=PRJ-{SEQNUM:6}");
        var code = store.GetEntity("op365_project")!.Attributes.Single(a => a.LogicalName == "op365_code");
        (await ScalarAsync(TestProvider.IsPostgres
            ? $"SELECT COUNT(*) FROM pg_sequences WHERE schemaname = 'autonumber' AND sequencename = '{code.MetadataId:D}'"
            : $"SELECT COUNT(*) FROM sys.sequences WHERE object_id = OBJECT_ID(N'[autonumber].[{code.MetadataId:D}]', N'SO')")).Should().Be(1);
        store.GetEntity("op365_wbsnode").Should().NotBeNull();
        (await ScalarAsync("SELECT COUNT(*) FROM data.plugintype")).Should().Be(75);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep")).Should().Be(108);
        foreach (var message in new[] { "Create", "Update", "Delete", "Retrieve", "RetrieveMultiple", "Associate" })
            (await ScalarAsync($"SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageid = '{SdkMessageCatalog.MessageId(message)}'"))
                .Should().NotBe(0, "the export has steps on {0}", message);

        // Its field security profile (component type 70) with its nine permissions, all on columns the export secures.
        (await ScalarAsync("SELECT name FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = '85a50f28-d6cf-f111-a05b-7ced8d2d53e4'"))
            .Should().Be("OP365 Cost Visibility");
        (await ScalarAsync("SELECT COUNT(*) FROM data.fieldpermission WHERE fieldsecurityprofileid = '85a50f28-d6cf-f111-a05b-7ced8d2d53e4'"))
            .Should().Be(9);
        (await ScalarAsync("""
            SELECT canupdate FROM data.fieldpermission WHERE fieldsecurityprofileid = '85a50f28-d6cf-f111-a05b-7ced8d2d53e4'
            AND entityname = 'op365_agentproposal' AND attributelogicalname = 'op365_evidence'
            """)).Should().Be(0);

        var exported = ((ExportSolutionResponse)await admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "OpenProject365", Managed = false }))
            .ExportSolutionFile;
        var (exportedSolution, exportedCustomizations, files) = SolutionZip.Read(exported);
        exportedCustomizations.ToString().Should().Contain("<AutoNumberFormat>PRJ-{SEQNUM:6}</AutoNumberFormat>");
        var (originalSolution, originalCustomizations, original) = SolutionZip.Read(OpenProject365());
        foreach (var folder in new[] { "Controls/", "bots/", "botcomponents/", "skills/" })
            foreach (var (path, content) in original.Where(f => f.Key.StartsWith(folder, StringComparison.Ordinal)))
                files.Should().ContainKey(path).WhoseValue.Should().Equal(content, "entry {0} is kept byte for byte", path);
        // The environment variables are applied (#167) and written again from the rows: the same definitions, fields and labels.
        var before = CustomizationsReader.Read(originalSolution, originalCustomizations, original).EnvironmentVariables;
        var after = CustomizationsReader.Read(exportedSolution, exportedCustomizations, files).EnvironmentVariables;
        before.Should().HaveCount(19);
        after.Should().BeEquivalentTo(before, o => o.Excluding(v => v.Source));
        foreach (var (path, content) in original.Where(f => f.Key.StartsWith("environmentvariabledefinitions/", StringComparison.Ordinal)))
            XNode.DeepEquals(XElement.Parse(System.Text.Encoding.UTF8.GetString(files.Should().ContainKey(path).WhoseValue)),
                    XElement.Parse(System.Text.Encoding.UTF8.GetString(content)))
                .Should().BeTrue("entry {0} is written again with the same elements, labels included", path);
        files.Keys.Count(k => k.StartsWith("environmentvariabledefinitions/", StringComparison.Ordinal))
            .Should().Be(original.Keys.Count(k => k.StartsWith("environmentvariabledefinitions/", StringComparison.Ordinal)));
        // The custom controls are applied (plan 3.5 UI-1d) and written again from the rows and their files: the same CustomControl
        // entries (in name order) and the same Controls/ entries, byte for byte (checked above), no more.
        static IEnumerable<string> ControlEntries(XDocument customizations) => customizations.Root!.Element("CustomControls")!.Elements("CustomControl")
            .Select(c => $"{((string?)c.Element("Name"))?.Trim()}|{((string?)c.Element("FileName"))?.Trim()}");
        ControlEntries(exportedCustomizations).Should().BeEquivalentTo(ControlEntries(originalCustomizations)).And.HaveCount(8);
        files.Keys.Count(k => k.StartsWith("Controls/", StringComparison.Ordinal))
            .Should().Be(original.Keys.Count(k => k.StartsWith("Controls/", StringComparison.Ordinal)));
        exportedSolution.Descendants("RootComponent").Where(r => (string?)r.Attribute("type") == "66").Select(r => (string?)r.Attribute("schemaName"))
            .Should().BeEquivalentTo(originalSolution.Descendants("RootComponent").Where(r => (string?)r.Attribute("type") == "66")
                .Select(r => (string?)r.Attribute("schemaName")));
    }

    [OpenProject365Fact]
    public async Task OpenProject365_imports_its_custom_controls()
    {
        var jobId = await ImportOpenProject365Async();

        (await ScalarAsync("SELECT COUNT(*) FROM data.customcontrol WHERE name LIKE N'op365[_]%'")).Should().Be(8);
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = N'OpenProject365' AND sc.componenttype = 66
            """)).Should().Be(8);
        (await ScalarAsync("SELECT compatibledatatypes FROM data.customcontrol WHERE name = N'op365_OP365.ProgressField'")).Should().Be("Decimal");
        var (_, _, original) = SolutionZip.Read(OpenProject365());
        (await ScalarAsync("""
            SELECT COUNT(*) FROM meta.customcontrol_file f JOIN data.customcontrol c ON c.customcontrolid = f.customcontrolid
            WHERE c.name LIKE N'op365[_]%'
            """)).Should().Be(original.Keys.Count(k => k.StartsWith("Controls/", StringComparison.Ordinal)));
        var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        log.Should().NotContain("'Controls/'").And.NotContain("'CustomControls'").And.NotContain("type 66");
    }

    [OpenProject365Fact]
    public async Task OpenProject365_imports_its_environment_variables()
    {
        var jobId = await ImportOpenProject365Async();

        (await ScalarAsync("SELECT COUNT(*) FROM data.environmentvariabledefinition WHERE schemaname LIKE N'op365[_]%'")).Should().Be(19);
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = N'OpenProject365' AND sc.componenttype = 380
            """)).Should().Be(19, "a definition is a component without a RootComponent in solution.xml");
        (await ScalarAsync("SELECT defaultvalue FROM data.environmentvariabledefinition WHERE schemaname = N'op365_InvoiceAiExtraction'"))
            .Should().Be("yes");
        var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        log.Should().NotContain("environmentvariabledefinitions/");
        var (_, _, original) = SolutionZip.Read(OpenProject365());
        var withoutDefault = original.Where(f => f.Key.EndsWith("/environmentvariabledefinition.xml", StringComparison.Ordinal))
            .Select(f => XElement.Parse(System.Text.Encoding.UTF8.GetString(f.Value)))
            .Where(d => string.IsNullOrEmpty((string?)d.Element("defaultvalue")))
            .Select(d => (string)d.Attribute("schemaname")!).ToList();
        withoutDefault.Should().HaveCount(10).And.Contain("op365_PurchasingApprover");
        foreach (var name in original.Keys.Where(k => k.EndsWith("/environmentvariabledefinition.xml", StringComparison.Ordinal))
                     .Select(k => k.Split('/')[1]).Except(withoutDefault))
            log.Should().NotContain($"Environment variable '{name}' has no value");
        foreach (var name in withoutDefault)
            log.Should().Contain($"Environment variable '{name}' has no value and no default value: set one with `xrmd envvar set`.");
    }

    /// <summary>Imports the unmodified OpenProject365 export: the first time, or as an idempotent upgrade.</summary>
    private async Task<Guid> ImportOpenProject365Async()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var zip = OpenProject365();
        var jobId = Guid.NewGuid();
        await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = jobId });
        return jobId;
    }

    [OpenProject365Fact]
    public async Task Real_export_imports_its_custom_apis()
    {
        var jobId = await ImportOpenProject365Async();

        (await ScalarAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(27);
        (await ScalarAsync("SELECT COUNT(*) FROM data.customapirequestparameter")).Should().Be(32);
        (await ScalarAsync("SELECT COUNT(*) FROM data.customapiresponseproperty")).Should().Be(23);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE categoryname = N'CustomAPI'")).Should().Be(27);
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.customapi c JOIN data.sdkmessagefilter f ON f.sdkmessageid = c.sdkmessageid
            WHERE c.uniquename = N'op365_RegisterGoodsReceipt' AND c.bindingtype = 1 AND f.primaryobjecttypecode = N'op365_purchaseorder'
            """)).Should().Be(1);
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.customapi c WHERE c.plugintypeid IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM data.plugintype t WHERE t.plugintypeid = c.plugintypeid)
            """)).Should().Be(0, "every main operation is a plug-in type of the export");
        (await ScalarAsync("SELECT COUNT(*) FROM data.customapi WHERE plugintypeid IS NOT NULL")).Should().NotBe(0);
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = N'OpenProject365' AND sc.componenttype = 9990
            """)).Should().Be(27);
        var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        log.Should().NotContain("customapis/", "the custom API files are read, not kept as unread entries");
        XElement.Parse(log).Element("customApis")!.Elements("customApi").Should().HaveCount(27);
    }

    [OpenProject365Fact]
    public async Task OpenProject365_modern_flows_stay_kept_and_not_run()
    {
        // Plan 2.14, rulings W2 and W22: the nine modern flows (category 5, no XAML) keep the kept-XML path.
        var jobId = await ImportOpenProject365Async();

        var kept = XElement.Parse((string)(await ScalarAsync(
            "SELECT xrmverse_unsupportedxml FROM data.solution WHERE uniquename = N'OpenProject365'"))!);
        var flows = kept.Descendants("Workflows").Single().Elements("Workflow").ToList();
        flows.Should().HaveCount(9).And.OnlyContain(w => (string?)w.Element("Category") == "5");
        // By the nine flows' ids: none is a workflow row, whatever its category (the collection database holds other processes).
        var flowIds = string.Join(", ", flows.Select(f => $"'{Guid.Parse((string)f.Attribute("WorkflowId")!):D}'"));
        (await ScalarAsync($"SELECT COUNT(*) FROM data.workflow WHERE workflowid IN ({flowIds})")).Should().Be(0, "a modern flow is not a workflow row");
        var log = XElement.Parse((string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!);
        foreach (var flow in flows)
            log.ToString().Should().Contain($"Process '{(string)flow.Attribute("Name")!}' (category 5)");

        var exported = ((ExportSolutionResponse)await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new ExportSolutionRequest { SolutionName = "OpenProject365", Managed = false })).ExportSolutionFile;
        var (_, customizations, files) = SolutionZip.Read(exported);
        var (_, originalCustomizations, original) = SolutionZip.Read(OpenProject365());
        XNode.DeepEquals(customizations.Root!.Element("Workflows"), originalCustomizations.Root!.Element("Workflows"))
            .Should().BeTrue("the export writes the kept Workflows node as it was imported");
        foreach (var (path, content) in original.Where(f => f.Key.StartsWith("Workflows/", StringComparison.Ordinal)))
            files.Should().ContainKey(path).WhoseValue.Should().Equal(content, "entry {0} is kept byte for byte", path);
    }

    [OpenProject365Fact]
    public async Task Export_round_trips_custom_apis()
    {
        await ImportOpenProject365Async();
        var exported = ((ExportSolutionResponse)await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new ExportSolutionRequest { SolutionName = "OpenProject365", Managed = false })).ExportSolutionFile;
        var (solution, _, files) = SolutionZip.Read(exported);
        var (_, _, original) = SolutionZip.Read(OpenProject365());

        // The export writes every file of the custom APIs again, with the same content.
        var customApis = original.Where(f => f.Key.StartsWith("customapis/", StringComparison.Ordinal)).ToList();
        customApis.Should().HaveCount(27 + 32 + 23);
        foreach (var (path, content) in customApis)
            Normalized(files.Should().ContainKey(path).WhoseValue).Should().Be(Normalized(content), "entry {0} is written back as it was read", path);
        files.Keys.Count(k => k.StartsWith("customapis/", StringComparison.Ordinal)).Should().Be(customApis.Count);

        // An API is no root component of solution.xml (it travels in customapis/).
        solution.Descendants("RootComponent").Should().NotContain(r => (string?)r.Attribute("type") == "9990");
    }

    // ---- xrmdWfTest: the classic processes of the parity export (plan 2.14, Task 3b) ----------------------------------------

    private static byte[] XrmdWfTest() => File.ReadAllBytes(Path.Combine(Directory, "xrmdWfTest_1_0_0_2.zip"));

    /// <summary>The five processes of the export by name: their id and the values the file gives their row.</summary>
    private static readonly (string Name, Guid Id, int Category, int Mode)[] WfTestProcesses =
    [
        ("Child", Guid.Parse("0fe1b782-e5de-f111-84e8-002248872b66"), 0, 1),
        ("OnCreate", Guid.Parse("3e1f0307-d8de-f111-84e8-002248872b66"), 0, 1),
        ("OnUpdate", Guid.Parse("bc9a625b-e4de-f111-84e8-002248872b66"), 0, 0),
        ("New business rule", Guid.Parse("216fa344-e5de-f111-84e8-002248872b66"), 2, 1),
        ("Recalculate", Guid.Parse("9df696a1-e6de-f111-84e8-002248872b66"), 3, 0),
    ];

    private async Task<(Guid JobId, Dictionary<string, Entity> Rows)> ImportXrmdWfTestAsync(bool publishWorkflows)
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var jobId = Guid.NewGuid();
        await admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = XrmdWfTest(), ImportJobId = jobId, PublishWorkflows = publishWorkflows });
        var rows = new Dictionary<string, Entity>();
        foreach (var (name, id, _, _) in WfTestProcesses)
            rows[name] = await admin.RetrieveAsync(WorkflowSchema.Table, id, new ColumnSet(true));
        return (jobId, rows);
    }

    private async Task<string> ImportLogAsync(Guid jobId) =>
        (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;

    [Fact]
    public async Task XrmdWfTest_processes_import_and_round_trip()
    {
        var (jobId, rows) = await ImportXrmdWfTestAsync(publishWorkflows: false);

        fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity("xrmv_order").Should().NotBeNull();
        foreach (var (name, id, category, mode) in WfTestProcesses)
        {
            var row = rows[name];
            (row.GetAttributeValue<string>(WorkflowSchema.Name), row.GetAttributeValue<OptionSetValue>(WorkflowSchema.CategoryColumn).Value,
                row.GetAttributeValue<OptionSetValue>(WorkflowSchema.ModeColumn).Value, row.GetAttributeValue<string>(WorkflowSchema.PrimaryEntity))
                .Should().Be((name, category, mode, "xrmv_order"));
            row.GetAttributeValue<OptionSetValue>(WorkflowSchema.TypeColumn).Value.Should().Be(1);
            row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(WorkflowSchema.DraftState, "the export has every process as Draft");
            row.GetAttributeValue<string>(WorkflowSchema.UnsupportedStepsColumn).Should().BeNull();
        }
        (rows["Child"].GetAttributeValue<bool>(WorkflowSchema.Subprocess), rows["Child"].GetAttributeValue<OptionSetValue>(WorkflowSchema.CreateStage).Value)
            .Should().Be((true, 40));
        (rows["OnCreate"].GetAttributeValue<bool>(WorkflowSchema.TriggerOnCreate), rows["OnCreate"].GetAttributeValue<OptionSetValue>(WorkflowSchema.CreateStage).Value)
            .Should().Be((true, 40));
        (rows["OnUpdate"].GetAttributeValue<string>(WorkflowSchema.TriggerOnUpdateAttributeList), rows["OnUpdate"].GetAttributeValue<bool>(WorkflowSchema.AsyncAutoDelete),
            rows["OnUpdate"].GetAttributeValue<bool>(WorkflowSchema.TriggerOnCreate)).Should().Be(("xrmv_status", true, false));
        rows["New business rule"].GetAttributeValue<OptionSetValue>(WorkflowSchema.ProcessTriggerScope).Value.Should().Be(WorkflowSchema.EntityTriggerScope);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.processtrigger WHERE processid = '{WfTestProcesses[3].Id}'"))
            .Should().Be(0, "the rule has no ProcessTriggers element");
        rows["Recalculate"].GetAttributeValue<string>(WorkflowSchema.UniqueName).Should().Be("Recalculate");
        foreach (var name in new[] { "Child", "OnCreate", "OnUpdate", "Recalculate" })
            rows[name].GetAttributeValue<OptionSetValue>(WorkflowSchema.ProcessTriggerScope).Should().BeNull("{0} has no ProcessTriggerScope", name);
        (await ImportLogAsync(jobId)).Should().NotContain("steps xrmverse cannot run");

        // The export writes each Workflow element as it was imported and each XAML file byte for byte.
        var exported = ((ExportSolutionResponse)await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new ExportSolutionRequest { SolutionName = "xrmdWfTest", Managed = false })).ExportSolutionFile;
        var (_, customizations, files) = SolutionZip.Read(exported);
        var (_, originalCustomizations, original) = SolutionZip.Read(XrmdWfTest());
        static XElement Normalized(XElement e) => XElement.Parse(e.ToString(SaveOptions.DisableFormatting));
        var written = customizations.Root!.Element("Workflows")!.Elements("Workflow").ToDictionary(w => Guid.Parse((string)w.Attribute("WorkflowId")!));
        written.Should().HaveCount(5);
        foreach (var source in originalCustomizations.Root!.Element("Workflows")!.Elements("Workflow"))
        {
            var element = written[Guid.Parse((string)source.Attribute("WorkflowId")!)];
            element.Elements().Select(e => e.Name.LocalName).Should().Equal(source.Elements().Select(e => e.Name.LocalName));
            XNode.DeepEquals(Normalized(element), Normalized(source)).Should().BeTrue("process {0} is exported as it was imported:\n{1}",
                (string)source.Attribute("Name")!, element);
            var path = source.Element("XamlFileName")!.Value.TrimStart('/');
            files.Should().ContainKey(path).WhoseValue.Should().Equal(original[path], "the XAML {0} is exported byte for byte", path);
        }
    }

    [Fact]
    public async Task XrmdWfTest_processes_activate_with_PublishWorkflows()
    {
        var (jobId, rows) = await ImportXrmdWfTestAsync(publishWorkflows: true);

        var log = await ImportLogAsync(jobId);
        foreach (var (name, _, _, _) in WfTestProcesses)
        {
            rows[name].GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(WorkflowSchema.ActivatedState, "{0} is published; the log:\n{1}", name, log);
            rows[name].GetAttributeValue<string>(WorkflowSchema.UnsupportedStepsColumn).Should().BeNull("{0} has no unsupported step", name);
            rows[name].GetAttributeValue<EntityReference>(WorkflowSchema.ActiveWorkflowId).Should().NotBeNull();
        }
        log.Should().NotContain("steps xrmverse cannot run").And.NotContain("is imported as Draft");
    }

    private static string Normalized(byte[] xml)
    {
        using var stream = new MemoryStream(xml);
        return XmlNormalizer.Normalize(XDocument.Load(stream));
    }
}
