using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Errors;
using Xrmd.TestSupport;
using Xrmd.Workflows.Xaml;
using Xunit.Abstractions;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 8b (ruling W17): the custom workflow activities of the net462 <c>Xrmd.TestWorkflowActivities</c> run through
/// CoreWF in the published host, whose SDK folder holds the workflow SDK copied from the test package (<c>xrmd sdk install</c> from the
/// cached nupkg). The host is a process of its own: the workflow SDK it loads, and the parser's view of it, stay out of the in-process
/// hosts of the other tests (8a review M). The registration and import of activity types are covered by 8a
/// (<c>RegistrationValidationTests.Registering_codeactivity_type_is_accepted</c>,
/// <c>WorkflowSolutionTests.Imported_assembly_with_workflow_activity_group_marks_type</c> and
/// <c>Custom_activity_step_without_the_workflow_sdk_is_flagged_on_import</c>).
/// </summary>
[Collection("xrmd")]
public sealed class CustomWorkflowActivityTests(XrmdFixture fx, CustomActivityHosts hosts, ITestOutputHelper output)
    : IClassFixture<CustomActivityHosts>
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Custom_activity_step_runs_and_maps_outputs()
    {
        // The first run of a net462 activity: System.Activities 4.0 binds to CoreWF through the default context (8a review).
        // Real time, runas the caller: the activity's service acts as the user who created the account, as the built-in step after it.
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Create task", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("task"), (process, CreateTaskThenNote(process)));
        var (userId, objectId) = await host.CreateUserAsync();
        var user = host.Client(objectId);

        var accountId = await user.CreateAsync(new Entity("account") { ["name"] = "Activity " + _run });

        var task = (await host.Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            ColumnSet = new ColumnSet("subject", "regardingobjectid", "createdby"),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, accountId) } },
        })).Entities.Should().ContainSingle("the activity created one task").Subject;
        task.GetAttributeValue<string>("subject").Should().Be("Follow up " + _run);
        task.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(accountId);

        // The later built-in step read the activity's output TaskId.
        var note = (await host.Admin.RetrieveMultipleAsync(new QueryExpression("annotation")
        {
            ColumnSet = new ColumnSet("subject", "objectid", "createdby"),
            Criteria = { Conditions = { new ConditionExpression("objectid", ConditionOperator.Equal, task.Id) } },
        })).Entities.Should().ContainSingle("the step after the activity created a note on its task").Subject;
        note.GetAttributeValue<string>("subject").Should().Be("Note " + _run);

        task.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(userId, "the activity runs as the run's user (runas: the caller)");
        note.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(userId, "as the built-in step of the same run");
    }

    [Fact]
    public async Task Workflow_context_exposes_depth_stage_and_primary_entity()
    {
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Describe", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("describe"), (process, DescribeThenNote()));
        var (userId, objectId) = await host.CreateUserAsync();

        var accountId = await host.Client(objectId).CreateAsync(new Entity("account") { ["name"] = "Describe " + _run });

        var description = await host.NoteTextAsync(accountId);
        Fields(description).Should().Equal(new Dictionary<string, string>
        {
            ["depth"] = "1",
            ["stage"] = "PostOperation",
            ["entity"] = "account",
            ["id"] = accountId.ToString(),
            ["message"] = "Create",
            ["mode"] = "0",
            ["workflowmode"] = "1",
            ["category"] = "0",
            ["user"] = userId.ToString(),
            ["service"] = userId.ToString(),
            ["parent"] = "null",
        });
    }

    [Fact]
    public async Task Background_run_runs_the_activity_as_the_job_owner()
    {
        // A background run is the job owner's (the process's owner), whoever triggered it.
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Describe later", mode: 0);
        await using var activated = await host.ImportAsync(Solution("background"), (process, DescribeThenNote()));
        var owner = (await host.Admin.RetrieveAsync(S.Table, process.Id, new ColumnSet("ownerid"))).GetAttributeValue<EntityReference>("ownerid").Id;
        var (userId, objectId) = await host.CreateUserAsync();

        var accountId = await host.Client(objectId).CreateAsync(new Entity("account") { ["name"] = "Background " + _run });

        var description = await host.WaitForAsync(() => host.NoteTextAsync(accountId));
        var fields = Fields(description);
        owner.Should().NotBe(userId);
        fields["user"].Should().Be(owner.ToString());
        fields["service"].Should().Be(owner.ToString());
        fields["mode"].Should().Be("1");
        fields["workflowmode"].Should().Be("0");
        fields["stage"].Should().Be("PostOperation");
        fields["depth"].Should().Be("1");
        fields["message"].Should().Be("Create");
        fields["id"].Should().Be(accountId.ToString());
    }

    [Fact]
    public async Task Missing_required_argument_fails_step_with_400()
    {
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("No subject", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("nosubject"), (process, CreateTaskThenNote(process, withSubject: false)));
        var name = "No subject " + _run;

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = name }))
            .Should().ThrowAsync<SoapFaultException>();

        fault.Which.Code.Should().Be(ErrorCodes.InvalidArgument);
        fault.Which.Message.Should().Contain("'Subject'").And.Contain("required input argument")
            .And.Contain("Xrmd.TestWorkflowActivities.CreateTaskActivity");
        (await host.Admin.RetrieveMultipleAsync(new QueryExpression("account")
        {
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, name) } },
        })).Entities.Should().BeEmpty("the real-time run failed, so the create rolled back");
    }

    [Fact]
    public async Task Custom_activity_without_workflow_sdk_fails_explicitly()
    {
        // RF5: an SDK folder without Microsoft.Xrm.Sdk.Workflow.dll. The import marks the type and activates the process with the
        // step listed as unsupported; a write that triggers it fails naming the command that installs the SDK.
        var host = await hosts.WithoutSdkAsync(fx, output);
        var process = Process("No SDK", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("nosdk"), (process, CreateTaskThenNote(process)));

        (await host.Admin.RetrieveAsync("plugintype", CustomActivityHost.CreateTaskTypeId, new ColumnSet("isworkflowactivity")))
            .GetAttributeValue<bool?>("isworkflowactivity").Should().BeTrue();
        (await host.Admin.RetrieveAsync(S.Table, process.Id, new ColumnSet(S.UnsupportedStepsColumn)))
            .GetAttributeValue<string>(S.UnsupportedStepsColumn).Should().Contain(XamlWorkflowParser.WorkflowSdkMissingReason);

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = "No SDK " + _run }))
            .Should().ThrowAsync<SoapFaultException>();
        fault.Which.Code.Should().Be(ErrorCodes.InvalidArgument);
        fault.Which.Message.Should().Contain("xrmd sdk install").And.Contain(process.Name);
    }

    [Fact]
    public async Task Activity_abort_fails_the_real_time_write_with_its_message()
    {
        // InvalidPluginExecutionException: the plug-in abort, 400 with its message; the triggering create rolls back.
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Abort", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("abort"), (process, ThrowThenNote("abort", "Refused " + _run)));
        var name = "Abort " + _run;

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = name })).Should().ThrowAsync<SoapFaultException>();

        fault.Which.Code.Should().Be(ErrorCodes.ISVAborted);
        fault.Which.Message.Should().Be("Refused " + _run);
        (await AccountsNamedAsync(host, name)).Should().BeEmpty();
    }

    [Fact]
    public async Task Activity_abort_fails_the_background_job()
    {
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Abort later", mode: 0);
        await using var activated = await host.ImportAsync(Solution("abortlater"), (process, ThrowThenNote("abort", "Refused later " + _run)));

        var accountId = await host.Admin.CreateAsync(new Entity("account") { ["name"] = "Abort later " + _run });

        var job = await host.WaitForAsync(() => host.EndedJobAsync(accountId));
        (job.State, job.Status).Should().Be((3, 31), "a failed workflow job is Completed/Failed");
        job.Message.Should().Contain("Refused later " + _run);
        (await host.NoteTextAsync(accountId)).Should().BeNull("the step after the activity did not run");
    }

    [Fact]
    public async Task Activity_crash_is_an_unexpected_500()
    {
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Crash", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("crash"), (process, ThrowThenNote("crash", "Broken " + _run)));
        var name = "Crash " + _run;

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = name })).Should().ThrowAsync<SoapFaultException>();

        fault.Which.Code.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Which.Message.Should().Contain("Xrmd.TestWorkflowActivities.ThrowActivity").And.Contain("System.InvalidOperationException")
            .And.Contain("Broken " + _run);
        (await AccountsNamedAsync(host, name)).Should().BeEmpty();
        var trace = await host.TraceAsync("Xrmd.TestWorkflowActivities.ThrowActivity");
        trace.Should().NotBeNull("an unexpected failure is traced, as a plug-in's is");
        trace!.Value.Details.Should().Contain("System.InvalidOperationException: Broken " + _run);
        trace.Value.OperationType.Should().Be(2, "operationtype 2 is a workflow activity");
    }

    [Fact]
    public async Task Swallowed_service_failure_dooms_the_transaction()
    {
        // The activity catches the fault of its failed call and returns normally: Dataverse refuses to commit what is left.
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Swallow", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("swallow"), (process, ThrowThenNote("swallow", "")));
        var name = "Swallow " + _run;

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = name })).Should().ThrowAsync<SoapFaultException>();

        fault.Which.Code.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Which.Message.Should().Be(Xrmd.Pipeline.PipelineExecutor.NoActiveTransactionMessage);
        (await AccountsNamedAsync(host, name)).Should().BeEmpty();
    }

    [Fact]
    public async Task Slow_activity_times_out_as_a_plugin()
    {
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Sleep", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("sleep"), (process, SleepThenNote(60)));
        var name = "Sleep " + _run;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = name })).Should().ThrowAsync<SoapFaultException>();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(40), "the run ends at the plug-in timeout, not when the activity returns");
        fault.Which.Code.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Which.Message.Should().Contain("timed-out at the Sandbox Host");
        (await AccountsNamedAsync(host, name)).Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_succeeded_ends_the_run_as_succeeded()
    {
        // StopWorkflowException(Succeeded): the workflow ends as a stop step that succeeds; the steps after it do not run.
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Stop ok", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("stopok"), (process, ThrowThenNote("stop-succeeded", "Done " + _run)));

        var accountId = await host.Admin.CreateAsync(new Entity("account") { ["name"] = "Stop ok " + _run });

        (await host.NoteTextAsync(accountId)).Should().BeNull("the step after the stop did not run");
    }

    [Fact]
    public async Task Stop_canceled_cancels_the_real_time_write()
    {
        // StopWorkflowException(Canceled): the W11 canceled stop; in real time the plug-in abort with the exception's message.
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Stop canceled", mode: 1, createStage: 40);
        await using var activated = await host.ImportAsync(Solution("stopcancel"), (process, ThrowThenNote("stop-canceled", "Halt " + _run)));
        var name = "Stop canceled " + _run;

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = name })).Should().ThrowAsync<SoapFaultException>();

        fault.Which.Code.Should().Be(ErrorCodes.ISVAborted);
        fault.Which.Message.Should().Be("Halt " + _run);
        (await AccountsNamedAsync(host, name)).Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_canceled_cancels_the_background_job()
    {
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Stop canceled later", mode: 0);
        await using var activated = await host.ImportAsync(Solution("stopcancellater"), (process, ThrowThenNote("stop-canceled", "Halt later " + _run)));

        var accountId = await host.Admin.CreateAsync(new Entity("account") { ["name"] = "Stop canceled later " + _run });

        var job = await host.WaitForAsync(() => host.EndedJobAsync(accountId));
        (job.State, job.Status).Should().Be((3, 32), "a canceled workflow job is Completed/Canceled (W11)");
        job.Message.Should().Contain("Halt later " + _run);
    }

    [Fact]
    public async Task Unregistered_activity_type_fails_explicitly()
    {
        // NotAnActivity is in the assembly, but no plugintype marked isworkflowactivity names it.
        var host = await hosts.WithSdkAsync(fx, output);
        var process = Process("Unregistered", mode: 1, createStage: 40);
        var xaml = ThrowThenNote("abort", "x").Replace("xta:ThrowActivity", "xta:NotAnActivity", StringComparison.Ordinal);
        await using var activated = await host.ImportAsync(Solution("unregistered"), (process, xaml));

        var fault = await host.Admin.Invoking(s => s.CreateAsync(new Entity("account") { ["name"] = "Unregistered " + _run }))
            .Should().ThrowAsync<SoapFaultException>();

        fault.Which.Code.Should().Be(ErrorCodes.InvalidArgument);
        fault.Which.Message.Should().Contain("'Xrmd.TestWorkflowActivities.NotAnActivity'").And.Contain("is not registered");
    }

    private static async Task<IReadOnlyList<Entity>> AccountsNamedAsync(CustomActivityHost host, string name) =>
        (await host.Admin.RetrieveMultipleAsync(new QueryExpression("account")
        {
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, name) } },
        })).Entities;

    // ---- processes ----------------------------------------------------------------------------------------------------------

    private string Solution(string name) => $"xw_act{name}_{_run}";

    private TestProcess Process(string name, int mode, int? createStage = null) =>
        new($"{name} {_run}", "custom-activity", Mode: mode, CreateStage: createStage);

    private static Dictionary<string, string> Fields(string? description)
    {
        description.Should().NotBeNullOrEmpty("the step after the activity wrote its output in a note");
        return description!.Split(';').Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1]);
    }

    private const string Header = """
        <Activity x:Class="XrmWorkflowTest" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" xmlns:mxs="clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:s="clr-namespace:System;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" xmlns:xta="clr-namespace:Xrmd.TestWorkflowActivities;assembly=Xrmd.TestWorkflowActivities, Version=1.0.0.0, Culture=neutral, PublicKeyToken=a15f04ad73430aaf" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <mxswa:Workflow>
        """;

    private const string Footer = """
          </mxswa:Workflow>
        </Activity>
        """;

    private static string Text(string variable, string text) => $"""
              <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.EvaluateExpression, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" DisplayName="EvaluateExpression">
                <mxswa:ActivityReference.Arguments>
                  <InArgument x:TypeArguments="x:String" x:Key="ExpressionOperator">CreateCrmType</InArgument>
                  <InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() {"{"} Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.String, "{text}", "String" {"}"}]</InArgument>
                  <InArgument x:TypeArguments="s:Type" x:Key="TargetType">
                    <mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:String" />
                  </InArgument>
                  <OutArgument x:TypeArguments="x:Object" x:Key="Result">[{variable}]</OutArgument>
                </mxswa:ActivityReference.Arguments>
              </mxswa:ActivityReference>
        """;

    private static string TargetType(string element, string type) => $"""
                <mxswa:{element}.TargetType>
                  <InArgument x:TypeArguments="s:Type">
                    <mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="{type}" />
                  </InArgument>
                </mxswa:{element}.TargetType>
        """;

    /// <summary>
    /// The activity <c>CreateTaskActivity</c> (Subject "Follow up {run}", Account the primary record), then a create step of a note
    /// whose <c>objectid</c> is the activity's output <c>TaskId</c>.
    /// </summary>
    private string CreateTaskThenNote(TestProcess process, bool withSubject = true) => Header + $"""
            <Sequence DisplayName="CustomActivityStep1: create a task">
              <Sequence.Variables>
                <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_1" />
                <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_2" />
              </Sequence.Variables>
        {Text("CustomActivityStep1_1", "Follow up " + _run)}
              <mxswa:GetEntityProperty Attribute="accountid" Entity="[InputEntities(&quot;primaryEntity&quot;)]" EntityName="account" Value="[CustomActivityStep1_2]">
        {TargetType("GetEntityProperty", "mxs:EntityReference")}
              </mxswa:GetEntityProperty>
              <xta:CreateTaskActivity DisplayName="CustomActivityStep1" {(withSubject ? "Subject=\"[CustomActivityStep1_1]\" " : "")}Account="[CustomActivityStep1_2]">
                <xta:CreateTaskActivity.TaskId>
                  <OutArgument x:TypeArguments="mxs:EntityReference">[CustomActivityStep1_TaskId]</OutArgument>
                </xta:CreateTaskActivity.TaskId>
              </xta:CreateTaskActivity>
            </Sequence>
            <Sequence DisplayName="CreateStep2: note on the task">
              <Sequence.Variables>
                <Variable x:TypeArguments="x:Object" Name="CreateStep2_1" />
              </Sequence.Variables>
              <Assign x:TypeArguments="mxs:Entity" To="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" Value="[New Entity(&quot;annotation&quot;)]" />
        {Text("CreateStep2_1", "Note " + _run)}
              <mxswa:SetEntityProperty Attribute="subject" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" Value="[CreateStep2_1]">
        {TargetType("SetEntityProperty", "x:String")}
              </mxswa:SetEntityProperty>
              <mxswa:SetEntityProperty Attribute="objectid" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" Value="[CustomActivityStep1_TaskId]">
        {TargetType("SetEntityProperty", "mxs:EntityReference")}
              </mxswa:SetEntityProperty>
              <mxswa:CreateEntity EntityId="{"{x:Null}"}" DisplayName="CreateStep2" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" />
              <Assign x:TypeArguments="mxs:Entity" To="[CreatedEntities(&quot;CreateStep2_localParameter&quot;)]" Value="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" />
            </Sequence>
        """ + Footer;

    /// <summary><c>ThrowActivity</c> with <paramref name="kind"/> and <paramref name="message"/>, then a note on the primary record.</summary>
    private string ThrowThenNote(string kind, string message) => ActivityThenNote($"""
              <xta:ThrowActivity DisplayName="CustomActivityStep1" Kind="[CustomActivityStep1_1]" Message="[CustomActivityStep1_2]" />
        """, Text("CustomActivityStep1_1", kind) + Text("CustomActivityStep1_2", message));

    /// <summary><c>SleepActivity</c> for <paramref name="seconds"/>, then a note on the primary record.</summary>
    private string SleepThenNote(int seconds) => ActivityThenNote($"""
              <xta:SleepActivity DisplayName="CustomActivityStep1" Seconds="[CustomActivityStep1_1]" />
        """, $"""
              <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.EvaluateExpression, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" DisplayName="EvaluateExpression">
                <mxswa:ActivityReference.Arguments>
                  <InArgument x:TypeArguments="x:String" x:Key="ExpressionOperator">CreateCrmType</InArgument>
                  <InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() {"{"} Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.Integer, "{seconds}", "Integer" {"}"}]</InArgument>
                  <InArgument x:TypeArguments="s:Type" x:Key="TargetType">
                    <mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:Int32" />
                  </InArgument>
                  <OutArgument x:TypeArguments="x:Object" x:Key="Result">[CustomActivityStep1_1]</OutArgument>
                </mxswa:ActivityReference.Arguments>
              </mxswa:ActivityReference>
        """);

    /// <summary><paramref name="activity"/> after <paramref name="values"/>, then a create step of a note "After" on the primary record.</summary>
    private string ActivityThenNote(string activity, string values) => Header + $"""
            <Sequence DisplayName="CustomActivityStep1: the activity">
              <Sequence.Variables>
                <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_1" />
                <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_2" />
              </Sequence.Variables>
        {values}
        {activity}
            </Sequence>
            <Sequence DisplayName="CreateStep2: note after the activity">
              <Sequence.Variables>
                <Variable x:TypeArguments="x:Object" Name="CreateStep2_1" />
                <Variable x:TypeArguments="x:Object" Name="CreateStep2_2" />
              </Sequence.Variables>
              <Assign x:TypeArguments="mxs:Entity" To="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" Value="[New Entity(&quot;annotation&quot;)]" />
        {Text("CreateStep2_1", "After " + _run)}
              <mxswa:SetEntityProperty Attribute="notetext" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" Value="[CreateStep2_1]">
        {TargetType("SetEntityProperty", "x:String")}
              </mxswa:SetEntityProperty>
              <mxswa:GetEntityProperty Attribute="accountid" Entity="[InputEntities(&quot;primaryEntity&quot;)]" EntityName="account" Value="[CreateStep2_2]">
        {TargetType("GetEntityProperty", "mxs:EntityReference")}
              </mxswa:GetEntityProperty>
              <mxswa:SetEntityProperty Attribute="objectid" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" Value="[CreateStep2_2]">
        {TargetType("SetEntityProperty", "mxs:EntityReference")}
              </mxswa:SetEntityProperty>
              <mxswa:CreateEntity EntityId="{"{x:Null}"}" DisplayName="CreateStep2" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" />
              <Assign x:TypeArguments="mxs:Entity" To="[CreatedEntities(&quot;CreateStep2_localParameter&quot;)]" Value="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" />
            </Sequence>
        """ + Footer;

    /// <summary>The activity <c>DescribeContextActivity</c>, then a create step of a note on the primary record holding its output.</summary>
    private static string DescribeThenNote() => Header + $"""
            <Sequence DisplayName="CustomActivityStep1: describe the context">
              <xta:DescribeContextActivity DisplayName="CustomActivityStep1">
                <xta:DescribeContextActivity.Description>
                  <OutArgument x:TypeArguments="x:String">[CustomActivityStep1_Description]</OutArgument>
                </xta:DescribeContextActivity.Description>
              </xta:DescribeContextActivity>
            </Sequence>
            <Sequence DisplayName="CreateStep2: note the description">
              <Sequence.Variables>
                <Variable x:TypeArguments="x:Object" Name="CreateStep2_1" />
              </Sequence.Variables>
              <Assign x:TypeArguments="mxs:Entity" To="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" Value="[New Entity(&quot;annotation&quot;)]" />
              <mxswa:SetEntityProperty Attribute="notetext" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" Value="[CustomActivityStep1_Description]">
        {TargetType("SetEntityProperty", "x:String")}
              </mxswa:SetEntityProperty>
              <mxswa:GetEntityProperty Attribute="accountid" Entity="[InputEntities(&quot;primaryEntity&quot;)]" EntityName="account" Value="[CreateStep2_1]">
        {TargetType("GetEntityProperty", "mxs:EntityReference")}
              </mxswa:GetEntityProperty>
              <mxswa:SetEntityProperty Attribute="objectid" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" Value="[CreateStep2_1]">
        {TargetType("SetEntityProperty", "mxs:EntityReference")}
              </mxswa:SetEntityProperty>
              <mxswa:CreateEntity EntityId="{"{x:Null}"}" DisplayName="CreateStep2" Entity="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" EntityName="annotation" />
              <Assign x:TypeArguments="mxs:Entity" To="[CreatedEntities(&quot;CreateStep2_localParameter&quot;)]" Value="[CreatedEntities(&quot;CreateStep2_localParameter#Temp&quot;)]" />
            </Sequence>
        """ + Footer;
}

/// <summary>A fault of the SOAP organization endpoint: its <c>OrganizationServiceFault</c> code and message.</summary>
public sealed class SoapFaultException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>The SDK requests of one user against a published host, over its SOAP organization endpoint.</summary>
public sealed class SoapClient(HttpClient http, Func<string> url, string audience, Guid objectId)
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Contracts = "http://schemas.microsoft.com/xrm/2011/Contracts";

    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url() + "XRMServices/2011/Organization.svc/web")
        {
            Content = new StringContent(OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(request)), Encoding.UTF8, "text/xml"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, audience));
        var response = await http.SendAsync(message);
        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            var text = await response.Content.ReadAsStringAsync();
            var detail = XDocument.Parse(text).Descendants(Soap + "Fault").Single().Element("detail")?.Element(Contracts + "OrganizationServiceFault");
            if (detail is null) throw new InvalidOperationException(text);
            throw new SoapFaultException((int)detail.Element(Contracts + "ErrorCode")!, (string)detail.Element(Contracts + "Message")!);
        }
        return await OrganizationSoapEndpointTests.ExecuteResultAsync(response);
    }

    public async Task<Guid> CreateAsync(Entity entity) => (Guid)(await ExecuteAsync(new CreateRequest { Target = entity })).Results["id"];

    public async Task<Entity> RetrieveAsync(string entity, Guid id, ColumnSet columns) =>
        (Entity)(await ExecuteAsync(new RetrieveRequest { Target = new EntityReference(entity, id), ColumnSet = columns })).Results["Entity"];

    public async Task<EntityCollection> RetrieveMultipleAsync(QueryBase query) =>
        (EntityCollection)(await ExecuteAsync(new RetrieveMultipleRequest { Query = query })).Results["EntityCollection"];
}

/// <summary>
/// The published hosts of <see cref="CustomWorkflowActivityTests"/>, started on first use and shared by its tests: one whose SDK
/// folder has the workflow SDK (with the async worker on, polling every second, and a plug-in timeout of
/// <see cref="CustomActivityHost.PluginTimeout"/>), one whose folder has not. A host whose clean-up failed
/// (<see cref="CustomActivityHost.Broken"/>) is replaced by a new one, so that a process it left activated reaches no later test.
/// </summary>
public sealed class CustomActivityHosts : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<CustomActivityHost> _started = [];
    private readonly List<string> _sdks = [];
    private CustomActivityHost? _withSdk, _withoutSdk;

    public async Task<CustomActivityHost> WithSdkAsync(XrmdFixture fx, ITestOutputHelper output)
    {
        await _gate.WaitAsync();
        try
        {
            return _withSdk = _withSdk is { Broken: false } ? _withSdk : await StartAsync(fx, output, withWorkflowSdk: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CustomActivityHost> WithoutSdkAsync(XrmdFixture fx, ITestOutputHelper output)
    {
        await _gate.WaitAsync();
        try
        {
            return _withoutSdk = _withoutSdk is { Broken: false } ? _withoutSdk : await StartAsync(fx, output, withWorkflowSdk: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CustomActivityHost> StartAsync(XrmdFixture fx, ITestOutputHelper output, bool withWorkflowSdk)
    {
        var sdk = await PublishedApps.InstalledSdkAsync(withWorkflow: withWorkflowSdk);
        _sdks.Add(sdk);
        File.Exists(Path.Combine(sdk, SdkPackage.Workflow.AssemblyFileName)).Should().Be(withWorkflowSdk);
        var host = await CustomActivityHost.StartAsync(fx, sdk, output);
        _started.Add(host);
        return host;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _started) await host.DisposeAsync();
        foreach (var sdk in _sdks) PublishedApps.DeleteFolder(sdk);
    }
}

/// <summary>One published host on a database of its own, with the test activity assembly registered by solution import.</summary>
public sealed class CustomActivityHost : IAsyncDisposable
{
    public static readonly Guid AssemblyId = Guid.Parse("6b0c2f8e-1d3a-4f5b-9c7e-2a4d6f8b0c11");
    public static readonly Guid CreateTaskTypeId = Guid.Parse("6b0c2f8e-1d3a-4f5b-9c7e-2a4d6f8b0c12");
    public static readonly Guid DescribeTypeId = Guid.Parse("6b0c2f8e-1d3a-4f5b-9c7e-2a4d6f8b0c13");
    public static readonly Guid ThrowTypeId = Guid.Parse("6b0c2f8e-1d3a-4f5b-9c7e-2a4d6f8b0c14");
    public static readonly Guid SleepTypeId = Guid.Parse("6b0c2f8e-1d3a-4f5b-9c7e-2a4d6f8b0c15");

    /// <summary>
    /// The plug-in timeout of the hosts (<c>Pipeline:PluginTimeout</c>): short, for the timeout test (whose activity sleeps 60 s and
    /// which expects the run to end within 40 s), yet long enough for a normal activity's first run (JIT, a collectible load) on a
    /// slow runner.
    /// </summary>
    public static readonly TimeSpan PluginTimeout = TimeSpan.FromSeconds(12);

    private const string ActivityAssembly = "Xrmd.TestWorkflowActivities, Version=1.0.0.0, Culture=neutral, PublicKeyToken=a15f04ad73430aaf";

    private readonly XrmdFixture _fx;
    private readonly RunningProcess _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    private CustomActivityHost(XrmdFixture fx, RunningProcess process, string database)
    {
        _fx = fx;
        _process = process;
        Database = database;
        Admin = Client(fx.AdminObjectId);
    }

    /// <summary>The address the host listens on (read from its output once it bound port 0: no port is reserved in advance).</summary>
    public string Url { get; private set; } = "";

    /// <summary>True once a clean-up of this host failed: a process may still be activated, so no later test may use it.</summary>
    public bool Broken { get; private set; }

    public string Database { get; }

    public string ConnectionString => TestProvider.WithDatabase(_fx.ConnectionString, Database);

    public SoapClient Admin { get; }

    public string Output => _process.Output;

    public SoapClient Client(Guid objectId) => new(_http, () => Url, _fx.BaseUrl, objectId);

    public static async Task<CustomActivityHost> StartAsync(XrmdFixture fx, string sdk, ITestOutputHelper output)
    {
        var database = "wfact_" + Guid.NewGuid().ToString("N")[..10];
        var psi = await PublishedApps.HostStartInfoAsync(sdk);
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        psi.Environment["ConnectionStrings__Org"] = TestProvider.WithDatabase(fx.ConnectionString, database);
        psi.Environment["Storage__Provider"] = TestProvider.Setting(fx.Provider);
        psi.Environment["Auth__Mode"] = "Development";
        psi.Environment["Auth__Audience"] = fx.BaseUrl;
        psi.Environment["Auth__DevSigningKey"] = DevTokenFactory.SigningKey;
        psi.Environment["Seed__OrgName"] = XrmdFixture.OrgName;
        psi.Environment["Seed__AdminObjectId"] = fx.AdminObjectId.ToString();
        psi.Environment["Seed__AdminName"] = XrmdFixture.AdminName;
        psi.Environment["Pipeline__AsyncWorkerEnabled"] = "true";
        psi.Environment["Pipeline__AsyncPollInterval"] = "00:00:01";
        psi.Environment["Pipeline__PluginTimeout"] = PluginTimeout.ToString("c");
        psi.Environment["Files__FileSystem__Root"] = PublishedApps.NewFolder("files");

        var process = new RunningProcess(psi, line =>
        {
            try { output.WriteLine(line); } catch (InvalidOperationException) { }
        });
        var host = new CustomActivityHost(fx, process, database);
        try
        {
            await host.WaitUntilHealthyAsync();
            await TestPublishers.EnsureAsync(host.ConnectionString);
            await host.ImportAsync("xw_activities_" + database);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private async Task WaitUntilHealthyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.Process.HasExited) throw new Xunit.Sdk.XunitException($"The host exited with {_process.Process.ExitCode}:\n{Output}");
            if (Url.Length == 0)
            {
                if (System.Text.RegularExpressions.Regex.Match(Output, @"Now listening on: (http://127\.0\.0\.1:\d+)") is { Success: true } bound)
                    Url = bound.Groups[1].Value + "/";
                else
                {
                    await Task.Delay(250);
                    continue;
                }
            }
            try
            {
                if ((await _http.GetAsync(Url + "healthz")).StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }

        throw new Xunit.Sdk.XunitException("The host did not answer /healthz:\n" + Output);
    }

    /// <summary>
    /// Imports a solution with the test activity assembly (its two activity types carry a <c>WorkflowActivityGroupName</c>, as an export
    /// of custom workflow activities has) and <paramref name="processes"/>, each with its XAML.
    /// </summary>
    /// <returns>Deactivates the processes when disposed: an activated process of a test would run on the writes of the next.</returns>
    public async Task<IAsyncDisposable> ImportAsync(string solution, params (TestProcess Process, string Xaml)[] processes)
    {
        var zip = WorkflowSolutionBuilder.Build(solution, false, entries =>
        {
            foreach (var (process, xaml) in processes) entries[process.FilePath] = Encoding.UTF8.GetBytes(xaml);
            var path = $"PluginAssemblies/XrmdTestWorkflowActivities-{AssemblyId.ToString("D").ToUpperInvariant()}/XrmdTestWorkflowActivities.dll";
            entries[path] = PluginTestHelper.TestWorkflowActivityBytes;
            var customizations = XDocument.Load(new MemoryStream(entries["customizations.xml"]));
            customizations.Root!.Element("SolutionPluginAssemblies")!.Add(new XElement("PluginAssembly",
                new XAttribute("FullName", ActivityAssembly), new XAttribute("PluginAssemblyId", AssemblyId.ToString("D")),
                new XAttribute("CustomizationLevel", 1),
                new XElement("IsolationMode", 2),
                new XElement("SourceType", 0),
                new XElement("IntroducedVersion", "1.0"),
                new XElement("FileName", "/" + path),
                new XElement("PluginTypes",
                    Type(CreateTaskTypeId, "Xrmd.TestWorkflowActivities.CreateTaskActivity", "Create a task"),
                    Type(DescribeTypeId, "Xrmd.TestWorkflowActivities.DescribeContextActivity", "Describe the context"),
                    Type(ThrowTypeId, "Xrmd.TestWorkflowActivities.ThrowActivity", "Fail"),
                    Type(SleepTypeId, "Xrmd.TestWorkflowActivities.SleepActivity", "Sleep"))));
            entries["customizations.xml"] = Save(customizations);
            var manifest = XDocument.Load(new MemoryStream(entries["solution.xml"]));
            manifest.Descendants("RootComponents").Single().Add(new XElement("RootComponent", new XAttribute("type", 91),
                new XAttribute("id", AssemblyId.ToString("B")), new XAttribute("schemaName", ActivityAssembly), new XAttribute("behavior", 0)));
            entries["solution.xml"] = Save(manifest);
        }, processes.Select(p => p.Process).ToArray());

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        return new Deactivation(this, processes.Select(p => p.Process.Id).ToList());
    }

    /// <summary>
    /// Deactivates the processes of a test. It never throws (the test's own failure stands); when a deactivation fails the host is
    /// marked <see cref="Broken"/> and replaced before the next test.
    /// </summary>
    private sealed class Deactivation(CustomActivityHost host, List<Guid> ids) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var id in ids)
            {
                try
                {
                    await host.Admin.ExecuteAsync(new SetStateRequest
                    {
                        EntityMoniker = new EntityReference(S.Table, id), State = new OptionSetValue(S.DraftState), Status = new OptionSetValue(S.DraftStatus),
                    });
                }
                catch (Exception)
                {
                    host.Broken = true;
                }
            }
        }
    }

    private static XElement Type(Guid id, string typeName, string friendlyName) => new("PluginType",
        new XAttribute("AssemblyQualifiedName", $"{typeName}, {ActivityAssembly}"),
        new XAttribute("PluginTypeId", id.ToString("D")),
        new XAttribute("Name", typeName),
        new XElement("FriendlyName", friendlyName),
        new XElement("WorkflowActivityGroupName", "Xrmd.TestWorkflowActivities (1.0.0.0)"));

    private static byte[] Save(XDocument document)
    {
        var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A new enabled user of the root business unit with the System Administrator role: its id and its object id.</summary>
    public async Task<(Guid UserId, Guid ObjectId)> CreateUserAsync()
    {
        var id = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        await using var conn = await TestProvider.OpenAsync(ConnectionString);
        await using var cmd = TestSql.Command("""
            INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid, isdisabled,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES (@id, @name, @name, @oid, @bu, @disabled, @bu, SYSUTCDATETIME(), @id, SYSUTCDATETIME(), @id, 0, 1);
            INSERT INTO data.systemuserroles (systemuserrolesid, systemuserid, roleid, createdon, modifiedon, statecode, statuscode)
            SELECT NEWID(), @id, roleid, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
            FROM data.role WHERE name = 'System Administrator' AND businessunitid = @bu;
            """, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@oid", objectId);
        cmd.Parameters.AddWithValue("@name", "activity-user-" + id.ToString("N")[..8]);
        cmd.Parameters.AddWithValue("@disabled", false);
        cmd.Parameters.AddWithValue("@bu", (await TestSql.ScalarAsync(ConnectionString,
            "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL"))!);
        await cmd.ExecuteNonQueryAsync();
        return (id, objectId);
    }

    /// <summary>The <c>notetext</c> of the note on <paramref name="accountId"/>; null when it has none.</summary>
    public async Task<string?> NoteTextAsync(Guid accountId) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("annotation")
        {
            ColumnSet = new ColumnSet("notetext"),
            Criteria = { Conditions = { new ConditionExpression("objectid", ConditionOperator.Equal, accountId) } },
        })).Entities.SingleOrDefault()?.GetAttributeValue<string>("notetext");

    /// <summary>The workflow job of <paramref name="accountId"/> once it has ended (statecode 3): its state, status and message.</summary>
    public async Task<(int State, int Status, string Message)?> EndedJobAsync(Guid accountId)
    {
        await using var conn = await TestProvider.OpenAsync(ConnectionString);
        await using var cmd = TestSql.Command(
            "SELECT statecode, statuscode, message FROM data.asyncoperation WHERE regardingobjectid = @id AND operationtype = 10 AND statecode = 3", conn);
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = accountId;
        await using var rd = await cmd.ExecuteReaderAsync();
        return await rd.ReadAsync() ? (rd.GetInt32(0), rd.GetInt32(1), rd.IsDBNull(2) ? "" : rd.GetString(2)) : null;
    }

    /// <summary>The last <c>plugintracelog</c> row of <paramref name="typeName"/>: its exception details and operation type; null when none.</summary>
    public async Task<(string Details, int OperationType)?> TraceAsync(string typeName)
    {
        await using var conn = await TestProvider.OpenAsync(ConnectionString);
        await using var cmd = TestSql.Command(
            "SELECT TOP 1 exceptiondetails, operationtype FROM data.plugintracelog WHERE typename = @type ORDER BY createdon DESC", conn);
        cmd.Parameters.AddWithValue("@type", typeName);
        await using var rd = await cmd.ExecuteReaderAsync();
        return await rd.ReadAsync() ? (rd.IsDBNull(0) ? "" : rd.GetString(0), rd.GetInt32(1)) : null;
    }

    /// <summary>Polls <paramref name="read"/> until it is not null (60 s), else fails with the host's jobs and output.</summary>
    public async Task<T> WaitForAsync<T>(Func<Task<T?>> read) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await read() is { } value) return value;
            await Task.Delay(500);
        }

        throw await NothingAsync();
    }

    /// <summary><see cref="WaitForAsync{T}(Func{Task{T}})"/> for a value type.</summary>
    public async Task<T> WaitForAsync<T>(Func<Task<T?>> read, bool _ = false) where T : struct
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await read() is { } value) return value;
            await Task.Delay(500);
        }

        throw await NothingAsync();
    }

    private async Task<Exception> NothingAsync()
    {
        var jobs = new StringBuilder();
        await using var conn = await TestProvider.OpenAsync(ConnectionString);
        await using var cmd = TestSql.Command("SELECT * FROM data.asyncoperation", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            for (var i = 0; i < rd.FieldCount; i++)
                if (!rd.IsDBNull(i)) jobs.Append(rd.GetName(i)).Append('=').Append(rd.GetValue(i)).Append(i == rd.FieldCount - 1 ? "\n" : "; ");
        return new Xunit.Sdk.XunitException($"Nothing after 60 s. Jobs:\n{jobs}\nHost:\n{Output}");
    }

    public async ValueTask DisposeAsync()
    {
        _process.Dispose();
        _http.Dispose();
        await _fx.DropDatabaseAsync(Database);
    }
}
