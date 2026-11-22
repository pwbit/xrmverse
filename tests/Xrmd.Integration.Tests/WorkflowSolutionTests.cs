using System.Data.Common;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using Xrmd.Workflows.Authoring;
using Xrmd.Workflows.Xaml;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The process solution tests run on a database of their own: the processes they import are activated (real-time and background
/// triggers on <c>account</c>), which must not reach the tests of another collection once the engine runs them.
/// </summary>
[CollectionDefinition("workflow-solution")]
public class WorkflowSolutionCollection : ICollectionFixture<XrmdFixture>;

/// <summary>
/// Plan 2.14, task 3 (rulings W2, W4, W20 and W22): classic processes in solution import and export. Processes of category 0, 2 and 3
/// become <c>workflow</c> rows written through the <c>WorkflowStore</c>, activated as the file (or <c>PublishWorkflows</c>) asks; an
/// activation that fails validation is a warning of the import job log and leaves the process Draft; any other category keeps the
/// kept-XML path.
/// </summary>
[Collection("workflow-solution")]
public sealed class WorkflowSolutionTests(XrmdFixture fx) : IAsyncLifetime
{
    // The processes its tests activate run on every matching write of the collection (plan 2.14, Task 5): none is left activated.
    public Task InitializeAsync() => WorkflowTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => WorkflowTestHelper.ClearAsync(fx);

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private string Solution(string name) => $"xw_{name}_{_run}";

    private TestProcess Process(string name, string? fixture, int category = 0, int mode = 0, int state = 1, int? createStage = null,
        string primaryEntity = "account", bool onCreate = true, string? uniqueName = null, IReadOnlyList<TestProcessTrigger>? triggers = null,
        int scope = 4) =>
        new($"{name} {_run}", fixture, category, mode, primaryEntity, state, onCreate, CreateStage: createStage, UniqueName: uniqueName,
            Triggers: triggers, Scope: scope);

    private static async Task<Guid> ImportAsync(OrganizationService service, byte[] zip, bool publishWorkflows = false)
    {
        var jobId = Guid.NewGuid();
        await service.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = jobId, PublishWorkflows = publishWorkflows });
        return jobId;
    }

    private Task<Guid> ImportAsync(byte[] zip, bool publishWorkflows = false) => ImportAsync(Admin, zip, publishWorkflows);

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private Task<Entity> RowAsync(Guid id) => Admin.RetrieveAsync(S.Table, id, new ColumnSet(true));

    private async Task<List<Entity>> ActivationsOfAsync(Guid definitionId) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.Table)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = { Conditions = { new ConditionExpression(S.ParentWorkflowId, ConditionOperator.Equal, definitionId) } },
        })).Entities.ToList();

    private async Task<List<Entity>> TriggersOfAsync(Guid definitionId) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.TriggerTable)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = { Conditions = { new ConditionExpression(S.ProcessId, ConditionOperator.Equal, definitionId) } },
        })).Entities.ToList();

    private async Task<XElement> LogAsync(Guid jobId) =>
        XElement.Parse((string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!);

    private static int State(Entity row) => row.GetAttributeValue<OptionSetValue>("statecode").Value;

    [Fact]
    public async Task Import_creates_rows_and_activates_per_file_state()
    {
        var realtime = Process("Create follow-up", "realtime-create-update", mode: 1, createStage: 40);
        var draft = Process("Background", "background-wait", state: 0);
        var solution = Solution("state");

        var jobId = await ImportAsync(WorkflowSolutionBuilder.Build(solution, realtime, draft));

        var active = await RowAsync(realtime.Id);
        (State(active), active.GetAttributeValue<OptionSetValue>("statuscode").Value).Should().Be((1, 2));
        active.GetAttributeValue<OptionSetValue>(S.TypeColumn).Value.Should().Be(1);
        active.GetAttributeValue<OptionSetValue>(S.CategoryColumn).Value.Should().Be(0);
        active.GetAttributeValue<OptionSetValue>(S.ModeColumn).Value.Should().Be(1);
        active.GetAttributeValue<OptionSetValue>(S.ScopeColumn).Value.Should().Be(4);
        active.GetAttributeValue<OptionSetValue>(S.CreateStage).Value.Should().Be(40);
        active.GetAttributeValue<bool>(S.TriggerOnCreate).Should().BeTrue();
        active.GetAttributeValue<string>(S.PrimaryEntity).Should().Be("account");
        active.GetAttributeValue<string>(S.Xaml).Should().Be(WorkflowSolutionBuilder.Xaml("realtime-create-update"));
        active.GetAttributeValue<string>(S.Description).Should().Be($"The test process {realtime.Name}");
        var activation = (await ActivationsOfAsync(realtime.Id)).Should().ContainSingle().Subject;
        active.GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Id.Should().Be(activation.Id);
        (State(activation), activation.GetAttributeValue<OptionSetValue>(S.TypeColumn).Value).Should().Be((1, 2));
        // Only a definition is a solution component (controller ruling): the activation is in no solution, Default included.
        (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid = @id", ("@id", activation.Id)))
            .Should().Be(0, "an activation is not a solution component");

        var kept = await RowAsync(draft.Id);
        State(kept).Should().Be(0);
        kept.GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Should().BeNull();
        (await ActivationsOfAsync(draft.Id)).Should().BeEmpty();

        // Component type 29 of the solution, and one entry per process in the import job log.
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = @name AND sc.componenttype = 29 AND sc.objectid IN (@a, @b)
            """, ("@name", solution), ("@a", realtime.Id), ("@b", draft.Id))).Should().Be(2);
        var log = await LogAsync(jobId);
        log.Element("workflows")!.Elements("workflow").Select(w => (string)w.Attribute("id")!)
            .Should().BeEquivalentTo(realtime.Id.ToString("D"), draft.Id.ToString("D"));

        // A second import of the same file: the activated process is updated and activated again, once.
        await ImportAsync(WorkflowSolutionBuilder.Build(solution, realtime, draft));
        State(await RowAsync(realtime.Id)).Should().Be(1);
        (await ActivationsOfAsync(realtime.Id)).Should().ContainSingle();
    }

    private async Task<List<(Guid Owner, int OperationType)>> JobsRegardingAsync(Guid record)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT ownerid, operationtype FROM data.asyncoperation WHERE regardingobjectid = @id");
        cmd.AddParameter("@id", record);
        await using var rd = await cmd.ExecuteReaderAsync();
        var jobs = new List<(Guid, int)>();
        while (await rd.ReadAsync()) jobs.Add((rd.GetGuid(0), rd.GetInt32(1)));
        return jobs;
    }

    [Fact]
    public async Task Imported_process_is_owned_by_the_importing_user()
    {
        // A System Customizer (not an administrator) who may create and read their own accounts imports a User-scope background
        // workflow: the definition and its activation are the importer's, so the scope and the job owner are the importer
        // (final review I1; Learn: background workflows run as their owner).
        var customizerOid = Guid.NewGuid();
        var customizer = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), customizerOid);
        await AssignRoleAsync(fx, customizer, Xrmd.Security.StandardRoles.SystemCustomizer);
        await GrantAsync(fx, customizer, false, ("prvCreateAccount", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic),
            ("prvWriteAccount", DepthMask.Basic));
        var importer = ServiceFor(fx, customizerOid);
        var process = Process("User scope", "background-wait", scope: 1);
        var solution = Solution("owner");

        await ImportAsync(importer, WorkflowSolutionBuilder.Build(solution, process));

        var definition = await RowAsync(process.Id);
        State(definition).Should().Be(1);
        definition.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(customizer, "a new imported process is the importer's");
        var activation = (await ActivationsOfAsync(process.Id)).Should().ContainSingle().Subject;
        activation.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(customizer, "the activation copies the definition's owner");

        // Scope 1: the importer's account queues a job owned by the importer; the administrator's account does not match.
        var own = await importer.CreateAsync(new Entity("account") { ["name"] = "Owned by the importer " + _run });
        (await JobsRegardingAsync(own)).Should().Equal([(customizer, 10)]);
        var other = await Admin.CreateAsync(new Entity("account") { ["name"] = "Owned by the administrator " + _run });
        (await JobsRegardingAsync(other)).Should().BeEmpty("a User-scope process fires only for records of its owner");

        // A re-import by another user (the administrator) keeps the owner of the existing process, and of its new activation (the
        // former activation is kept while its job waits, W4).
        await ImportAsync(WorkflowSolutionBuilder.Build(solution, process));
        var reimported = await RowAsync(process.Id);
        reimported.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(customizer);
        var current = reimported.GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Id;
        current.Should().NotBe(activation.Id);
        (await RowAsync(current)).GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(customizer);
    }

    [Fact]
    public async Task PublishWorkflows_true_activates_supported_processes()
    {
        var draft = Process("Published", "background-wait", state: 0);

        await ImportAsync(WorkflowSolutionBuilder.Build(Solution("publish"), draft), publishWorkflows: true);

        State(await RowAsync(draft.Id)).Should().Be(1);
        (await ActivationsOfAsync(draft.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Queued_PublishWorkflows_activates_draft_process()
    {
        var draft = Process("Queued publish", "background-wait", state: 0);

        var response = (ImportSolutionAsyncResponse)await Admin.ExecuteAsync(new ImportSolutionAsyncRequest
        {
            CustomizationFile = WorkflowSolutionBuilder.Build(Solution("queued"), draft), PublishWorkflows = true,
        });
        ((string)(await ScalarAsync("SELECT data FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", response.AsyncOperationId)))!)
            .Should().StartWith(SolutionImporter.PublishWorkflowsPrefix);

        var worker = new Xrmd.Pipeline.Async.AsyncOperationWorker(fx.App.Services,
            fx.App.Services.GetRequiredService<Xrmd.Pipeline.Async.AsyncOperationQueue>(),
            Microsoft.Extensions.Options.Options.Create(new Xrmd.Pipeline.PipelineOptions()));
        for (var i = 0; i < 20 && (int)(await ScalarAsync("SELECT statecode FROM data.asyncoperation WHERE asyncoperationid = @id",
                 ("@id", response.AsyncOperationId)))! != 3; i++)
            await worker.RunOnceAsync(default);

        (await ScalarAsync("SELECT statuscode FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", response.AsyncOperationId)))
            .Should().Be(30, "the queued import succeeds");
        State(await RowAsync(draft.Id)).Should().Be(1, "the queued PublishWorkflows activates a process the file has as Draft");
        (await ActivationsOfAsync(draft.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task File_state_other_than_0_or_1_is_a_warning_and_stays_draft()
    {
        var process = Process("Odd state", "realtime-create-update", mode: 1, createStage: 40, state: 2);

        var jobId = await ImportAsync(WorkflowSolutionBuilder.Build(Solution("oddstate"), process), publishWorkflows: true);

        State(await RowAsync(process.Id)).Should().Be(0);
        (await ActivationsOfAsync(process.Id)).Should().BeEmpty();
        (await LogAsync(jobId)).Element("workflows")!.Elements("workflow").Single(w => (string)w.Attribute("id")! == process.Id.ToString("D"))
            .Elements("result").Should().Contain(r => (string?)r.Attribute("result") == "warning"
                && ((string)r.Attribute("errortext")!).Contains(process.Name) && ((string)r.Attribute("errortext")!).Contains("StateCode 2"));
    }

    [Fact]
    public async Task AddSolutionComponent_of_an_activation_is_400()
    {
        var process = Process("Activation component", "realtime-create-update", mode: 1, createStage: 40);
        var solution = Solution("activation");
        await ImportAsync(WorkflowSolutionBuilder.Build(solution, process));
        var activation = (await ActivationsOfAsync(process.Id)).Should().ContainSingle().Subject.Id;

        var fault = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = activation, ComponentType = ComponentType.Workflow, SolutionUniqueName = solution,
        }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(activation.ToString("D")).And.Contain("activation");
        (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid = @id", ("@id", activation))).Should().Be(0);
    }

    [Fact]
    public async Task Unsupported_step_is_flagged_and_rest_imports()
    {
        var process = Process("Unsupported", "unsupported-expression");
        var solution = Solution("unsupported");
        var expected = XamlWorkflowParser.Parse(WorkflowSolutionBuilder.Xaml("unsupported-expression"), 0).Unsupported;
        expected.Should().NotBeEmpty();

        var jobId = await ImportAsync(WorkflowSolutionBuilder.Build(solution, process));

        // RF5: activated with the steps it cannot run listed; a run fails explicitly (W20).
        var row = await RowAsync(process.Id);
        State(row).Should().Be(1);
        var steps = row.GetAttributeValue<string>(S.UnsupportedStepsColumn);
        steps.Should().NotBeNullOrEmpty();
        foreach (var step in expected) steps.Should().Contain(step.Label);

        // The import job log names the process and each step.
        var warnings = (await LogAsync(jobId)).Element("workflows")!.Elements("workflow")
            .Single(w => (string)w.Attribute("id")! == process.Id.ToString("D"))
            .Elements("result").Where(r => (string?)r.Attribute("result") == "warning").Select(r => (string)r.Attribute("errortext")!).ToList();
        warnings.Should().Contain(w => w.Contains(process.Name) && expected.All(step => w.Contains(step.Label)));

        // The other components of the file are imported.
        (await ScalarAsync("SELECT COUNT(*) FROM data.webresource WHERE webresourceid = @id", ("@id", WorkflowSolutionBuilder.WebResourceIdOf(solution))))
            .Should().Be(1);
    }

    private const string ActivityAssembly = "Xrmd.TestWorkflowActivities, Version=1.0.0.0, Culture=neutral, PublicKeyToken=a15f04ad73430aaf";
    private const string ActivityType = "Xrmd.TestWorkflowActivities.CreateTaskActivity";

    /// <summary>
    /// The ids of the test activity assembly and its type in every solution of this collection: a plug-in assembly name is unique in
    /// the organization (issue #70), so a second import of the same assembly under a new id would be refused, depending on test order.
    /// </summary>
    private static readonly Guid ActivityAssemblyId = Guid.Parse("3f6c1d2e-8b4a-4c7e-9a1f-5d2b7e9c0a31"),
        ActivityTypeId = Guid.Parse("3f6c1d2e-8b4a-4c7e-9a1f-5d2b7e9c0a32");

    /// <summary>
    /// <paramref name="processes"/> with the test activity assembly: a <c>PluginAssembly</c> whose type carries a
    /// <c>WorkflowActivityGroupName</c> (plan 2.14, W17), as an export of a custom workflow activity has.
    /// </summary>
    private byte[] WithActivityAssembly(string solution, Guid assemblyId, Guid typeId, params TestProcess[] processes) =>
        WorkflowSolutionBuilder.Build(solution, false, entries =>
        {
            var path = $"PluginAssemblies/XrmdTestWorkflowActivities-{assemblyId.ToString("D").ToUpperInvariant()}/XrmdTestWorkflowActivities.dll";
            entries[path] = PluginTestHelper.TestWorkflowActivityBytes;
            var customizations = XDocument.Load(new MemoryStream(entries["customizations.xml"]));
            customizations.Root!.Element("SolutionPluginAssemblies")!.Add(new XElement("PluginAssembly",
                new XAttribute("FullName", ActivityAssembly), new XAttribute("PluginAssemblyId", assemblyId.ToString("D")),
                new XAttribute("CustomizationLevel", 1),
                new XElement("IsolationMode", 2),
                new XElement("SourceType", 0),
                new XElement("IntroducedVersion", "1.0"),
                new XElement("FileName", "/" + path),
                new XElement("PluginTypes", new XElement("PluginType",
                    new XAttribute("AssemblyQualifiedName", $"{ActivityType}, {ActivityAssembly}"),
                    new XAttribute("PluginTypeId", typeId.ToString("D")),
                    new XAttribute("Name", ActivityType),
                    new XElement("FriendlyName", "Create a task"),
                    new XElement("WorkflowActivityGroupName", "Xrmd.TestWorkflowActivities (1.0.0.0)")))));
            var stream = new MemoryStream();
            customizations.Save(stream);
            entries["customizations.xml"] = stream.ToArray();
            var manifest = XDocument.Load(new MemoryStream(entries["solution.xml"]));
            manifest.Descendants("RootComponents").Single().Add(new XElement("RootComponent", new XAttribute("type", 91),
                new XAttribute("id", assemblyId.ToString("B")), new XAttribute("schemaName", ActivityAssembly), new XAttribute("behavior", 0)));
            stream = new MemoryStream();
            manifest.Save(stream);
            entries["solution.xml"] = stream.ToArray();
        }, processes);

    [Fact]
    public async Task Imported_assembly_with_workflow_activity_group_marks_type()
    {
        await ImportAsync(WithActivityAssembly(Solution("activity"), ActivityAssemblyId, ActivityTypeId));

        var type = await Admin.RetrieveAsync("plugintype", ActivityTypeId, new ColumnSet("typename", "isworkflowactivity", "workflowactivitygroupname", "friendlyname"));
        type.GetAttributeValue<string>("typename").Should().Be(ActivityType);
        type.GetAttributeValue<bool?>("isworkflowactivity").Should().BeTrue();
        type.GetAttributeValue<string>("workflowactivitygroupname").Should().Be("Xrmd.TestWorkflowActivities (1.0.0.0)");
        type.GetAttributeValue<string>("friendlyname").Should().Be("Create a task");
    }

    [Fact]
    public async Task Custom_activity_step_without_the_workflow_sdk_is_flagged_on_import()
    {
        // RF5 (W17): no Microsoft.Xrm.Sdk.Workflow.dll in the SDK folder of the test host. The process is imported and activated
        // with the custom activity step listed as one it cannot run, and the reason names the command that installs the SDK.
        Xrmd.Workflows.Custom.WorkflowSdk.IsAvailable.Should().BeFalse();
        var process = Process("Custom activity", "custom-activity");

        var jobId = await ImportAsync(WithActivityAssembly(Solution("custom"), ActivityAssemblyId, ActivityTypeId, process));

        var row = await RowAsync(process.Id);
        row.GetAttributeValue<string>(S.UnsupportedStepsColumn).Should()
            .Contain("custom workflow activities need the Microsoft workflow SDK: run xrmd sdk install");
        (await LogAsync(jobId)).Element("workflows")!.Elements("workflow")
            .Single(w => (string)w.Attribute("id")! == process.Id.ToString("D"))
            .Elements("result").Should().Contain(r => (string?)r.Attribute("result") == "warning"
                && ((string)r.Attribute("errortext")!).Contains("xrmd sdk install"));
    }

    [Fact]
    public async Task Invalid_activation_becomes_warning_and_stays_draft()
    {
        // A wait in a real-time workflow: the activation's validation refuses it (W4, W10).
        var process = Process("Realtime wait", "background-wait", mode: 1, createStage: 40);
        var solution = Solution("invalid");

        var jobId = await ImportAsync(WorkflowSolutionBuilder.Build(solution, process));

        var row = await RowAsync(process.Id);
        State(row).Should().Be(0);
        row.GetAttributeValue<string>(S.Xaml).Should().Be(WorkflowSolutionBuilder.Xaml("background-wait"));
        (await ActivationsOfAsync(process.Id)).Should().BeEmpty();
        var log = await LogAsync(jobId);
        log.Element("workflows")!.Elements("workflow").Single(w => (string)w.Attribute("id")! == process.Id.ToString("D"))
            .Elements("result").Should().Contain(r => (string?)r.Attribute("result") == "warning"
                && ((string)r.Attribute("errortext")!).Contains(process.Name) && ((string)r.Attribute("errortext")!).Contains("wait"));
        (await ScalarAsync("SELECT COUNT(*) FROM data.webresource WHERE webresourceid = @id", ("@id", WorkflowSolutionBuilder.WebResourceIdOf(solution))))
            .Should().Be(1, "the warning does not stop the import");
    }

    [Fact]
    public async Task Business_rule_process_triggers_imported()
    {
        var rule = Process("Rule", "business-rule-entity", category: 2, onCreate: false,
            triggers: [new("change", "account", 2, "name"), new("load", "account", 1)]);

        await ImportAsync(WorkflowSolutionBuilder.Build(Solution("rule"), rule));

        var row = await RowAsync(rule.Id);
        (row.GetAttributeValue<OptionSetValue>(S.CategoryColumn).Value, State(row)).Should().Be((2, 1));
        var triggers = await TriggersOfAsync(rule.Id);
        triggers.Select(t => t.Id).Should().BeEquivalentTo([WorkflowSolutionBuilder.TriggerIdOf(rule.Name, 0), WorkflowSolutionBuilder.TriggerIdOf(rule.Name, 1)]);
        var change = triggers.Single(t => t.Id == WorkflowSolutionBuilder.TriggerIdOf(rule.Name, 0));
        (change.GetAttributeValue<string>(S.Event), change.GetAttributeValue<string>(S.PrimaryEntityTypeCode), change.GetAttributeValue<string>(S.ControlName),
            change.GetAttributeValue<OptionSetValue>(S.TriggerScope).Value).Should().Be(("change", "account", "name", 2));
        triggers.Single(t => t.Id == WorkflowSolutionBuilder.TriggerIdOf(rule.Name, 1)).GetAttributeValue<OptionSetValue>(S.TriggerScope).Value.Should().Be(1);

        // A re-import without the form-scope trigger removes it.
        await ImportAsync(WorkflowSolutionBuilder.Build(Solution("rule"), rule with { Triggers = [rule.Triggers![0]] }));
        (await TriggersOfAsync(rule.Id)).Should().ContainSingle().Which.Id.Should().Be(WorkflowSolutionBuilder.TriggerIdOf(rule.Name, 0));
    }

    /// <summary>The columns of a process and of its triggers that an export and import keep, as one comparable text.</summary>
    private static async Task<string> SnapshotAsync(DbConnection conn, IEnumerable<Guid> ids)
    {
        var text = new System.Text.StringBuilder();
        foreach (var id in ids.Order())
        {
            await using (var cmd = TestSql.Command(conn, $"""
                             SELECT name, uniquename, description, category, mode, scope, type, primaryentity, xaml, ondemand, subprocess,
                                    triggeroncreate, triggerondelete, triggeronupdateattributelist, asyncautodelete, syncworkflowlogonfailure,
                                    istransacted, createstage, updatestage, deletestage, runas, rank, introducedversion, iscustomizable,
                                    statecode, statuscode, {S.UnsupportedStepsColumn}, CASE WHEN activeworkflowid IS NULL THEN 0 ELSE 1 END
                             FROM data.workflow WHERE workflowid = @id
                             """))
            {
                cmd.AddParameter("@id", id);
                await using var rd = await cmd.ExecuteReaderAsync();
                (await rd.ReadAsync()).Should().BeTrue("process {0} exists", id);
                for (var i = 0; i < rd.FieldCount; i++) text.Append(rd.GetName(i)).Append('=').Append(rd.GetValue(i)).Append('|');
            }
            await using (var cmd = TestSql.Command(conn, """
                             SELECT processtriggerid, event, primaryentitytypecode, controlname, controltype, scope
                             FROM data.processtrigger WHERE processid = @id ORDER BY processtriggerid
                             """))
            {
                cmd.AddParameter("@id", id);
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    for (var i = 0; i < rd.FieldCount; i++) text.Append(rd.GetName(i)).Append('=').Append(rd.GetValue(i)).Append('|');
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    [Fact]
    public async Task Export_round_trips_processes()
    {
        var realtime = Process("Exported workflow", "realtime-create-update", mode: 1, createStage: 40);
        var rule = Process("Exported rule", "business-rule-entity", category: 2, onCreate: false, triggers: [new("change", "account", 2, "name")]);
        var action = Process("Exported action", "action-echo", category: 3, primaryEntity: "none", state: 0, onCreate: false, uniqueName: $"xw_Echo{_run}");
        var unsupported = Process("Exported unsupported", "unsupported-expression");
        var solution = Solution("export");
        var original = WorkflowSolutionBuilder.Build(solution, realtime, rule, action, unsupported);
        await ImportAsync(original);

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = solution, Managed = false }))
            .ExportSolutionFile;

        // The file: the same Workflow elements, the XAML byte for byte, the processes as root components of type 29.
        var (solutionXml, customizations, files) = SolutionZip.Read(exported);
        var (_, originalCustomizations, originalFiles) = SolutionZip.Read(original);
        var processes = new[] { realtime, rule, action, unsupported };
        foreach (var process in processes)
        {
            files.Should().ContainKey(process.FilePath).WhoseValue.Should().Equal(originalFiles[process.FilePath], "the XAML of {0} is exported byte for byte", process.Name);
            var element = customizations.Root!.Element("Workflows")!.Elements("Workflow").Single(w => Guid.Parse((string)w.Attribute("WorkflowId")!) == process.Id);
            var source = originalCustomizations.Root!.Element("Workflows")!.Elements("Workflow").Single(w => Guid.Parse((string)w.Attribute("WorkflowId")!) == process.Id);
            XNode.DeepEquals(element, source).Should().BeTrue("process {0} is exported as it was imported:\n{1}\n{2}", process.Name, element, source);
            solutionXml.Descendants("RootComponent").Should().Contain(r => (string?)r.Attribute("type") == "29"
                && Guid.Parse((string)r.Attribute("id")!) == process.Id);
        }

        // Re-imported into a second organization: identical rows.
        await using var app = fx.WithOwnDatabase(_ => { });
        var second = TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);
        await ImportAsync(second, exported);
        await using var first = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var other = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        var ids = processes.Select(p => p.Id).ToList();
        (await SnapshotAsync(other, ids)).Should().Be(await SnapshotAsync(first, ids));
    }

    // ---- Plan 2.21, Task 6 (A15): processes compiled from YAML, without kept XML ---------------------------------------------------

    private async Task<ApplyResult> ApplyAsync(string yaml, string solution, bool activate = false)
    {
        await using var scope = fx.App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<WorkflowAuthoring>().ApplyAsync(
            await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId), yaml, new ApplyOptions(solution, activate, DryRun: false), default);
        result.Errors.Should().BeEmpty();
        return result;
    }

    private string CompiledRule() => $$"""
        kind: businessRule
        name: Compiled rule {{_run}}
        description: Checks the name
        entity: account
        steps:
          - if:
              when: { field: name, op: null }
              then:
                - showError: { field: name, message: The name is missing }

        """;

    [Fact]
    public async Task Compiled_business_rule_exports_step_labels_and_descriptions()
    {
        var solution = await AuthoringTestHelper.SolutionAsync(fx, Solution("labels"), prefix: "xv");
        var rule = await ApplyAsync(CompiledRule(), solution);

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = solution, Managed = false }))
            .ExportSolutionFile;

        var (_, customizations, _) = SolutionZip.Read(exported);
        var element = customizations.Root!.Element("Workflows")!.Elements("Workflow").Single(w => Guid.Parse((string)w.Attribute("WorkflowId")!) == rule.Id);
        var description = element.Element("Descriptions")!.Elements("Description").Should().ContainSingle().Subject;
        ((string?)description.Attribute("description"), (string?)description.Attribute("languagecode")).Should().Be(("Checks the name", "1033"));
        // The real export writes the labels after the descriptions, one steplabels per StepLabel of the XAML.
        element.Elements().Select(e => e.Name.LocalName).Should().EndWith(["LocalizedNames", "Descriptions", "labels"]);
        var xaml = XDocument.Parse((await RowAsync(rule.Id)).GetAttributeValue<string>(S.Xaml));
        var label = xaml.Descendants().Single(e => e.Name.LocalName == "StepLabel");
        var steplabels = element.Element("labels")!.Elements("steplabels").Should().ContainSingle().Subject;
        ((string?)steplabels.Attribute("id")).Should().Be(Guid.Parse((string)label.Attribute("LabelId")!).ToString("B"));
        var text = steplabels.Elements("label").Should().ContainSingle().Subject;
        ((string?)text.Attribute("languagecode"), (string?)text.Attribute("description")).Should().Be(("1033", "The name is missing"));
    }

    [Fact]
    public async Task Exported_compiled_processes_reimport_into_second_org_with_equal_rows()
    {
        var solution = await AuthoringTestHelper.SolutionAsync(fx, Solution("compiled"), prefix: "xv");
        var realtime = await ApplyAsync($$"""
            kind: workflow
            name: Compiled workflow {{_run}}
            entity: account
            mode: realtime
            trigger:
              create: true
            steps:
              - create: { entity: task, set: { subject: Compiled, regardingobjectid: { record: primary } } }

            """, solution, activate: true);
        var rule = await ApplyAsync(CompiledRule(), solution);
        var action = await ApplyAsync($$"""
            kind: action
            name: Compiled action {{_run}}
            entity: account
            message: xv_Compiled{{_run}}
            arguments:
              - { name: Label, direction: input, type: string }
            steps:
              - update: { record: primary, set: { name: { arg: Label } } }

            """, solution);

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = solution, Managed = false }))
            .ExportSolutionFile;
        await using var app = fx.WithOwnDatabase(_ => { });
        await ImportAsync(TestServices.NewOrganizationService(app.Services, fx.AdminObjectId), exported);

        await using var first = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var other = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        var ids = new[] { realtime.Id, rule.Id, action.Id };
        var snapshot = await SnapshotAsync(first, ids);
        (await SnapshotAsync(other, ids)).Should().Be(snapshot);
        snapshot.Should().Contain("statecode=1|", "the real-time workflow is exported Activated");
    }

    [Fact]
    public async Task Authored_update_of_imported_process_exports_fresh_labels()
    {
        // Ruling R10: the real export imported, its business rule changed by an apply with its id (a new name, description and error
        // message): the export writes the labels of the row and the XAML, not the ones the import kept; the rest of the kept XML stays.
        await using var app = fx.WithOwnDatabase(_ => { });
        await app.Services.GetRequiredService<Xrmd.Metadata.SdkMessageCatalog>().EnsureAsync(default);
        var admin = TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);
        await ImportAsync(admin, File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports",
            "xrmdWfTest_1_0_0_2.zip")));
        var rule = Guid.Parse("216fa344-e5de-f111-84e8-002248872b66");
        var yaml = AuthoringTestHelper.Fixture("business-rule.yaml")
            .Replace("name: New business rule", "name: Renamed rule", StringComparison.Ordinal)
            .Replace("description: Click to add description", "description: Checks the amount", StringComparison.Ordinal)
            .Replace("message: fdsfds", "message: The amount is needed", StringComparison.Ordinal);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<WorkflowAuthoring>().ApplyAsync(
                await TestServices.CallerAsync(app.Services, fx.AdminObjectId), yaml, new ApplyOptions(null, false, DryRun: false), default);
            result.Errors.Should().BeEmpty();
            (result.Id, result.Action).Should().Be((rule, ApplyAction.Updated));
        }
        var exported = ((ExportSolutionResponse)await admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "xrmdWfTest", Managed = false }))
            .ExportSolutionFile;

        var (_, customizations, _) = SolutionZip.Read(exported);
        var element = customizations.Root!.Element("Workflows")!.Elements("Workflow").Single(w => Guid.Parse((string)w.Attribute("WorkflowId")!) == rule);
        ((string?)element.Attribute("Name"), (string?)element.Attribute("Description")).Should().Be(("Renamed rule", "Checks the amount"));
        element.Attribute(S.StaleLabelsAttribute).Should().BeNull();
        element.Element("LocalizedNames")!.Elements("LocalizedName").Select(l => (string?)l.Attribute("description")).Should().Equal("Renamed rule");
        element.Element("Descriptions")!.Elements("Description").Select(l => (string?)l.Attribute("description")).Should().Equal("Checks the amount");
        var labels = element.Element("labels")!.Descendants("label").Select(l => (string?)l.Attribute("description")).ToList();
        labels.Should().Contain("The amount is needed").And.NotContain("fdsfds");
        // The elements the import kept and xrmverse does not model are still written.
        element.Element("BusinessProcessType")!.Value.Should().Be("0");
        element.Elements().Select(e => e.Name.LocalName).Should().EndWith(["PrimaryEntity", "LocalizedNames", "Descriptions", "labels"]);
    }

    [Fact]
    public async Task Managed_uninstall_removes_processes()
    {
        var realtime = Process("Managed workflow", "realtime-create-update", mode: 1, createStage: 40);
        var rule = Process("Managed rule", "business-rule-entity", category: 2, onCreate: false, triggers: [new("change", "account", 2, "name")]);
        var solution = Solution("managed");
        await ImportAsync(WorkflowSolutionBuilder.Build(solution, managed: true, realtime, rule));

        var activation = (await ActivationsOfAsync(realtime.Id)).Should().ContainSingle().Subject.Id;
        (await TriggersOfAsync(rule.Id)).Should().ContainSingle();
        (await ScalarAsync("SELECT ismanaged FROM data.workflow WHERE workflowid = @id", ("@id", realtime.Id))).Should().Be(true);
        var job = Guid.NewGuid();
        await ScalarAsync($"""
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, {S.WorkflowActivationId}, statecode, statuscode, createdon, modifiedon)
            VALUES (@id, 'Workflow job', 10, @activation, 3, 30, SYSUTCDATETIME(), SYSUTCDATETIME())
            """, ("@id", job), ("@activation", activation));
        // A job still waiting (1/10) on the activation, with its wait (W4, W10).
        var waiting = Guid.NewGuid();
        await ScalarAsync($"""
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, {S.WorkflowActivationId}, statecode, statuscode, createdon, modifiedon)
            VALUES (@id, 'Waiting workflow job', 10, @activation, 1, 10, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO meta.workflow_wait (asyncoperationid, entity, objectid, wake_at) VALUES (@id, 'account', NEWID(), '9999-12-31');
            """, ("@id", waiting), ("@activation", activation));
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
                        await fx.App.Services.GetRequiredService<Xrmd.Workflows.Background.WorkflowWaits>().RefreshAsync(new DataSession(conn, null), default);
        }

        var solutionId = (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @name", ("@name", solution)))!;
        await Admin.DeleteAsync("solution", solutionId);

        (await ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE workflowid IN (@a, @b, @c)",
            ("@a", realtime.Id), ("@b", rule.Id), ("@c", activation))).Should().Be(0, "the definitions and their activation are removed");
        (await ScalarAsync("SELECT COUNT(*) FROM data.processtrigger WHERE processid = @id", ("@id", rule.Id))).Should().Be(0);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND {S.WorkflowActivationId} IS NULL", ("@id", job)))
            .Should().Be(1, "the job stays, without its activation (Workflow_AsyncOperations, RemoveLink)");
        (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid IN (@a, @b)", ("@a", realtime.Id), ("@b", rule.Id)))
            .Should().Be(0);
        // The waiting job of the removed process is canceled, and its wait goes.
        (await ScalarAsync("SELECT CONCAT(statecode, '/', statuscode, ' ', message) FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", waiting)))
            .Should().Be("3/32 " + Xrmd.Workflows.Background.WorkflowJobs.WorkflowDeletedMessage);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.workflow_wait WHERE asyncoperationid = @id", ("@id", waiting))).Should().Be(0);
    }

    [Fact]
    public async Task Category_5_process_is_not_a_row()
    {
        var flow = Process("Modern flow", null, category: 5, primaryEntity: "none", onCreate: false);
        var classic = Process("Classic next to flow", "realtime-create-update", mode: 1, createStage: 40);
        var solution = Solution("flow");

        var file = WorkflowSolutionBuilder.Build(solution, flow, classic);
        var jobId = await ImportAsync(file);

        (await ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE workflowid = @id", ("@id", flow.Id))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE workflowid = @id", ("@id", classic.Id))).Should().Be(1);
        var log = (await LogAsync(jobId)).ToString();
        log.Should().Contain($"Process '{flow.Name}'").And.Contain("category 5 is not run by xrmverse");

        // The export still holds the flow as it was, and its file.
        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = solution, Managed = false }))
            .ExportSolutionFile;
        var (_, customizations, files) = SolutionZip.Read(exported);
        var workflows = customizations.Root!.Element("Workflows")!.Elements("Workflow").ToList();
        workflows.Select(w => Guid.Parse((string)w.Attribute("WorkflowId")!)).Should().BeEquivalentTo([flow.Id, classic.Id]);
        var (_, original, _) = SolutionZip.Read(file);
        XNode.DeepEquals(workflows.Single(w => Guid.Parse((string)w.Attribute("WorkflowId")!) == flow.Id),
            original.Root!.Element("Workflows")!.Elements("Workflow").Single(w => Guid.Parse((string)w.Attribute("WorkflowId")!) == flow.Id))
            .Should().BeTrue("the kept process is exported as it was imported");
        files.Should().ContainKey(flow.FilePath);
    }
}
