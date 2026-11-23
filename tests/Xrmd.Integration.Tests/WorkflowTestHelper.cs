using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.ServiceModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Solution;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Activated processes for the tests of the "xrmd" collection (plan 2.14, Task 5). From Task 5 on, an activated real-time process
/// runs on every matching write: every test class that activates one must call <see cref="ClearAsync"/> at start and in
/// DisposeAsync, so that none fires in a later class.
/// </summary>
public static class WorkflowTestHelper
{
    /// <summary>
    /// Deactivates every activated definition through the real path (a public SetState as the administrator, so the activation
    /// deletes its activation row and clears <c>activeworkflowid</c>, ruling W4), then invalidates the registry.
    /// </summary>
    public static async Task ClearAsync(XrmdFixture fx)
    {
        var activated = new List<Guid>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"SELECT [{S.WorkflowId}] FROM data.[{S.Table}] WHERE [{S.TypeColumn}] = {(int)S.Type.Definition} AND statecode = {S.ActivatedState}");
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) activated.Add(rd.GetGuid(0));
        }
        var admin = SecurityTestHelper.ServiceFor(fx, fx.AdminObjectId);
        foreach (var id in activated) await DeactivateAsync(admin, id);
        fx.App.Services.GetRequiredService<WorkflowRegistry>().Invalidate();
    }

    /// <summary>Saves <paramref name="row"/> (and its triggers) through the <see cref="WorkflowStore"/>, as the solution import does.</summary>
    public static async Task<WorkflowRow> SaveAsync(XrmdFixture fx, WorkflowRow row, params Entity[] triggers)
    {
        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await scope.ServiceProvider.GetRequiredService<WorkflowStore>().SaveAsync(new DataSession(conn, null), row, triggers, default);
        return row;
    }

    /// <summary>Saves <paramref name="row"/> and activates it with a public SetState as <paramref name="service"/>.</summary>
    public static async Task<WorkflowRow> ActivateAsync(XrmdFixture fx, OrganizationService service, WorkflowRow row, params Entity[] triggers)
    {
        await SaveAsync(fx, row, triggers);
        await SetStateAsync(service, row.Id, S.ActivatedState, S.ActivatedStatus);
        return row;
    }

    /// <summary>Deactivates the definition with a public SetState.</summary>
    public static Task DeactivateAsync(OrganizationService service, Guid id) => SetStateAsync(service, id, S.DraftState, S.DraftStatus);

    public static Task SetStateAsync(OrganizationService service, Guid id, int state, int status) =>
        service.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(S.Table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
        });

    /// <summary>A Draft definition of <paramref name="xaml"/>: category 0, real-time, organization scope, with an unique name.</summary>
    public static WorkflowRow Realtime(string name, string xaml, string primaryEntity, int category = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = category,
        Mode = 1,
        Type = 1,
        Scope = 4,
        PrimaryEntity = primaryEntity,
        Xaml = xaml,
    };

    /// <summary>The XAML of the parity export file <c>real/{file}.xaml</c> (byte for byte).</summary>
    public static string Real(string file) => WorkflowFixtures.Xaml("real/" + file);

    /// <summary>
    /// <paramref name="xaml"/> without the top-level step (a child of <c>mxswa:Workflow</c>) whose <c>DisplayName</c> starts with
    /// <paramref name="displayName"/>: a real file with one of its steps taken out (the others are unchanged).
    /// </summary>
    public static string Without(string xaml, string displayName)
    {
        var doc = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        var workflow = doc.Descendants().First(e => e.Name.LocalName == "Workflow");
        var step = workflow.Elements().Single(e => ((string?)e.Attribute("DisplayName"))?.StartsWith(displayName, StringComparison.Ordinal) == true);
        step.Remove();
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary><paramref name="xaml"/> with <paramref name="original"/>, which must occur, replaced.</summary>
    public static string Replace(string xaml, string original, string replacement) =>
        xaml.Contains(original, StringComparison.Ordinal)
            ? xaml.Replace(original, replacement, StringComparison.Ordinal)
            : throw new ArgumentException($"The XAML does not contain '{original}'.", nameof(original));

    /// <summary><paramref name="xaml"/> with every match of <paramref name="pattern"/> removed (the pattern must match).</summary>
    public static string Remove(string xaml, string pattern) =>
        Regex.IsMatch(xaml, pattern) ? Regex.Replace(xaml, pattern, "") : throw new ArgumentException($"No match for '{pattern}'.", nameof(pattern));
}

/// <summary>
/// The effects of the 2.14 parity scenario through the real <see cref="ServiceClient"/> (plan 2.14, Task 9; plan 2.21, Task 8, A20), for
/// both ways its five processes get into the organization: the export <c>xrmdWfTest_1_0_0_2.zip</c> imported unmodified, or the YAML
/// twins (<c>tests/fixtures/workflows/yaml</c>) applied with <c>xrmd workflow apply</c>. Each assertion runs <c>setup</c>, which must
/// leave the processes it shows activated, then the scenario, and deactivates those processes at the end. The twins keep the export's
/// process ids, so both paths are checked against the same rows. The expected effects are read from the exported XAML and written as
/// literals; they were not recorded on Dataverse (2.14 checklist step 2 stays open).
/// </summary>
public static class WorkflowParityAssertions
{
    public static readonly Guid Child = Guid.Parse("0fe1b782-e5de-f111-84e8-002248872b66");
    public static readonly Guid OnCreate = Guid.Parse("3e1f0307-d8de-f111-84e8-002248872b66");
    public static readonly Guid OnUpdate = Guid.Parse("bc9a625b-e4de-f111-84e8-002248872b66");
    public static readonly Guid Rule = Guid.Parse("216fa344-e5de-f111-84e8-002248872b66");
    public static readonly Guid Recalculate = Guid.Parse("9df696a1-e6de-f111-84e8-002248872b66");

    /// <summary>The five processes of the export, in the order they can be created (Child before OnCreate, which starts it).</summary>
    public static readonly Guid[] All = [Child, OnCreate, OnUpdate, Rule, Recalculate];

    /// <summary>The currency of the exporting organization that OnUpdate's email names (lookup literal "Euro").</summary>
    public static readonly Guid Currency = Guid.Parse("692363f0-a3da-f111-84e8-3833c5c48caf");

    /// <summary>The parity export's bytes.</summary>
    public static byte[] Zip() =>
        File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmdWfTest_1_0_0_2.zip"));

    /// <summary>
    /// The parity export without its processes: no <c>Workflow</c> element, no XAML file and no root component of type 29. Importing it
    /// gives the organization the export's <c>xrmv_order</c> table, its publisher <c>xrmv</c> and the solution <c>xrmdWfTest</c>.
    /// </summary>
    public static byte[] ZipWithoutProcesses()
    {
        var (solution, customizations, files) = SolutionZip.Read(Zip());
        customizations.Root!.Element("Workflows")!.RemoveNodes();
        solution.Descendants("RootComponent").Where(c => (string?)c.Attribute("type") == "29").ToList().ForEach(c => c.Remove());
        var kept = files.Where(f => !f.Key.StartsWith("Workflows/", StringComparison.OrdinalIgnoreCase)
                                    && !f.Key.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Key, f => f.Value);
        return SolutionZip.Write(solution, customizations, kept);
    }

    /// <summary>
    /// Deletes every process of the export left in the organization (deactivated first): the imported processes and the authored
    /// twins share their ids, so each setup starts without them and the order of the tests does not matter.
    /// </summary>
    public static void DeleteProcesses(ServiceClient client)
    {
        foreach (var id in Enumerable.Reverse(All))
        {
            var row = client.RetrieveMultiple(new QueryExpression(S.Table)
            {
                ColumnSet = new ColumnSet("statecode"),
                Criteria = { Conditions = { new ConditionExpression(S.WorkflowId, ConditionOperator.Equal, id) } },
            }).Entities.SingleOrDefault();
            if (row is null) continue;
            if (row.GetAttributeValue<OptionSetValue>("statecode").Value == S.ActivatedState) SetState(client, id, S.DraftState, S.DraftStatus);
            client.Delete(S.Table, id);
        }
    }

    /// <summary>Deletes the export's processes (<see cref="DeleteProcesses"/>), imports the parity export through the client, then activates <paramref name="processes"/> in order.</summary>
    public static Func<Task> Imported(ServiceClient client, params Guid[] processes) => () =>
    {
        DeleteProcesses(client);
        client.Execute(new ImportSolutionRequest { CustomizationFile = Zip(), ImportJobId = Guid.NewGuid() });
        foreach (var id in processes) SetState(client, id, S.ActivatedState, S.ActivatedStatus);
        return Task.CompletedTask;
    };

    public static void SetState(ServiceClient client, Guid id, int state, int status) => client.Execute(new SetStateRequest
    {
        EntityMoniker = new EntityReference(S.Table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
    });

    /// <summary>Runs <paramref name="setup"/> and <paramref name="body"/>, then deactivates those of <paramref name="processes"/> left activated (in reverse order).</summary>
    private static async Task WithAsync(ServiceClient client, Func<Task> setup, Guid[] processes, Func<Task> body)
    {
        try
        {
            await setup();
            foreach (var id in processes)
                client.Retrieve(S.Table, id, new ColumnSet("statecode")).GetAttributeValue<OptionSetValue>("statecode").Value
                    .Should().Be(S.ActivatedState, "the setup activates process {0}", id);
            await body();
        }
        finally
        {
            foreach (var id in Enumerable.Reverse(processes))
                if (client.RetrieveMultiple(new QueryExpression(S.Table)
                    {
                        ColumnSet = new ColumnSet("statecode"),
                        Criteria =
                        {
                            Conditions =
                            {
                                new ConditionExpression(S.WorkflowId, ConditionOperator.Equal, id),
                                new ConditionExpression("statecode", ConditionOperator.Equal, S.ActivatedState),
                            },
                        },
                    }).Entities.Count != 0)
                    SetState(client, id, S.DraftState, S.DraftStatus);
        }
    }

    /// <summary>Polls through the client (30 s at most) until <paramref name="read"/> returns a value.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<T?> read, string what) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (read() is { } value) return value;
            DateTime.UtcNow.Should().BeBefore(deadline, what);
            await Task.Delay(200);
        }
    }

    /// <summary>OnCreate (real-time) creates its task in the Create and starts Child, whose only step stops it as Canceled.</summary>
    public static Task RealtimeCreatesTaskAsync(ServiceClient client, Func<Task> setup)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        // Child first: OnCreate's child workflow step needs it activated (W12).
        return WithAsync(client, setup, [Child, OnCreate], async () =>
        {
            var order = client.Create(new Entity(TestEntities.Order) { ["xrmv_name"] = "Parity order " + run });

            // OnCreate's CreateStep1 ran in the Create (real-time, stage 40): subject "TEST", regarding the order, priority 1.
            var task = client.RetrieveMultiple(new QueryExpression("task")
            {
                ColumnSet = new ColumnSet("subject", "regardingobjectid", "prioritycode", "isworkflowcreated"),
                Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
            }).Entities.Should().ContainSingle("the real-time workflow created the task in the request").Subject;
            task.GetAttributeValue<string>("subject").Should().Be("TEST");
            task.GetAttributeValue<EntityReference>("regardingobjectid").Should().Be(new EntityReference(TestEntities.Order, order));
            task.GetAttributeValue<OptionSetValue>("prioritycode").Value.Should().Be(1);
            task.GetAttributeValue<bool>("isworkflowcreated").Should().BeTrue();

            // ChildWorkflowStep2 queued Child (children run in the background, W12), whose only step stops it as Canceled: 3/32.
            var job = await EventuallyAsync(() => client.RetrieveMultiple(new QueryExpression("asyncoperation")
            {
                ColumnSet = new ColumnSet("operationtype", "statecode", "statuscode", "workflowactivationid"),
                Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
            }).Entities.SingleOrDefault(j => j.GetAttributeValue<OptionSetValue>("statecode").Value == 3), "the child workflow's job should end");
            (job.GetAttributeValue<OptionSetValue>("operationtype").Value, job.GetAttributeValue<OptionSetValue>("statuscode").Value).Should().Be((10, 32));
        });
    }

    /// <summary>
    /// OnUpdate (background) waits until the amount is above 100, then sends its email. The email names a currency of the exporting
    /// organization: a currency of that id is made to exist before <paramref name="setup"/> runs, as an administrator would
    /// (WorkflowParityTests shows the explicit failure without it); it and the emails naming it are deleted at the end (M3).
    /// </summary>
    public static async Task BackgroundWaitsThenSendsEmailAsync(ServiceClient client, Func<Task> setup)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        DeleteCurrency(client); // left by an interrupted earlier run
        client.Create(new Entity("transactioncurrency", Currency)
        {
            ["currencyname"] = "Euro of the exporting org", ["isocurrencycode"] = "XTS", ["currencysymbol"] = "X", ["currencyprecision"] = 2,
            ["exchangerate"] = 1m, ["currencytype"] = new OptionSetValue(1),
        });

        try
        {
            await WithAsync(client, setup, [OnUpdate], async () =>
            {
                var contact = client.Create(new Entity("contact") { ["lastname"] = "Parity " + run, ["emailaddress1"] = $"parity.{run}@example.com" });
                var account = client.Create(new Entity("account") { ["name"] = "Parity " + run, ["primarycontactid"] = new EntityReference("contact", contact) });
                var order = client.Create(new Entity(TestEntities.Order)
                {
                    ["xrmv_name"] = "Parity order " + run, ["xrmv_amount"] = 50m, ["xrmv_accountid"] = new EntityReference("account", account),
                });

                // OnUpdate triggers on xrmv_status; WaitStep1 waits until xrmv_amount > 100 (no timeout): 1/10.
                client.Update(new Entity(TestEntities.Order, order) { ["xrmv_status"] = new OptionSetValue(128830001) });
                var waiting = await EventuallyAsync(() => client.RetrieveMultiple(new QueryExpression("asyncoperation")
                {
                    ColumnSet = new ColumnSet("statecode", "statuscode"),
                    Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
                }).Entities.SingleOrDefault(j => j.GetAttributeValue<OptionSetValue>("statecode").Value == 1), "the job should wait");
                waiting.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(10);
                QueryExpression Emails() => new("email")
                {
                    ColumnSet = new ColumnSet("to", "regardingobjectid", "correlatedsubjectchanged", "deliveryprioritycode", "directioncode",
                        "followemailuserpreference", "isduplicatesenderunresolved", "notifications", "prioritycode", "transactioncurrencyid",
                        "statecode", "statuscode"),
                    Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
                };
                client.RetrieveMultiple(Emails()).Entities.Should().BeEmpty("the job waits");

                client.Update(new Entity(TestEntities.Order, order) { ["xrmv_amount"] = 150m });

                // SendEmailStep3, with the values its XAML sets. A background step commits each request on its own, so the email
                // can be read as a Draft between its Create and its SendEmail: wait until it has left Draft (status 1). The EmailSend
                // job SendEmail queues (plan 4.1b) may already have run and failed it (the sender's mailbox is not approved): 0/8.
                var email = await EventuallyAsync(() => client.RetrieveMultiple(Emails()).Entities
                    .SingleOrDefault(e => e.GetAttributeValue<OptionSetValue>("statuscode").Value != 1), "the email should be sent");
                email.GetAttributeValue<EntityCollection>("to").Entities.Should().ContainSingle()
                    .Which.GetAttributeValue<EntityReference>("partyid").Should().Be(new EntityReference("contact", contact));
                email.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(order);
                email.GetAttributeValue<bool>("correlatedsubjectchanged").Should().BeFalse();
                email.GetAttributeValue<OptionSetValue>("deliveryprioritycode").Value.Should().Be(1);
                email.GetAttributeValue<bool>("directioncode").Should().BeTrue();
                email.GetAttributeValue<bool>("followemailuserpreference").Should().BeFalse();
                email.GetAttributeValue<bool>("isduplicatesenderunresolved").Should().BeFalse();
                email.GetAttributeValue<OptionSetValue>("notifications").Value.Should().Be(0);
                email.GetAttributeValue<OptionSetValue>("prioritycode").Value.Should().Be(1);
                email.GetAttributeValue<EntityReference>("transactioncurrencyid").Id.Should().Be(Currency);
                (email.GetAttributeValue<OptionSetValue>("statecode").Value, email.GetAttributeValue<OptionSetValue>("statuscode").Value)
                    .Should().BeOneOf([(1, 6), (0, 8)], "sent with IssueSend: Pending Send, then Failed once the EmailSend job ran (plan 4.1b)");
            });
        }
        finally
        {
            DeleteCurrency(client);
        }
        client.RetrieveMultiple(new QueryExpression("transactioncurrency")
        {
            Criteria = { Conditions = { new ConditionExpression("isocurrencycode", ConditionOperator.Equal, "XTS") } },
        }).Entities.Should().BeEmpty("the test leaves no currency behind");
    }

    /// <summary>Deletes the emails naming the exporting organization's currency, then that currency, if it exists.</summary>
    private static void DeleteCurrency(ServiceClient client)
    {
        foreach (var email in client.RetrieveMultiple(new QueryExpression("email")
                 {
                     Criteria = { Conditions = { new ConditionExpression("transactioncurrencyid", ConditionOperator.Equal, Currency) } },
                 }).Entities)
            client.Delete("email", email.Id);
        if (client.RetrieveMultiple(new QueryExpression("transactioncurrency")
            {
                Criteria = { Conditions = { new ConditionExpression("transactioncurrencyid", ConditionOperator.Equal, Currency) } },
            }).Entities.Count != 0)
            client.Delete("transactioncurrency", Currency);
    }

    /// <summary>The business rule sets the status of an order without amount and refuses one with an amount, on Create and on Update.</summary>
    public static Task BusinessRuleSetsAndRefusesAsync(ServiceClient client, Func<Task> setup)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        return WithAsync(client, setup, [Rule], () =>
        {
            // ConditionStep1: xrmv_amount is empty → SetAttributeValue xrmv_status = 128830001.
            var order = client.Create(new Entity(TestEntities.Order) { ["xrmv_name"] = "Parity rule " + run });
            client.Retrieve(TestEntities.Order, order, new ColumnSet("xrmv_status")).GetAttributeValue<OptionSetValue>("xrmv_status").Value
                .Should().Be(128830001);

            // Otherwise (ConditionBranchStep3, condition True): SetMessage "fdsfds" on xrmv_status, on Create and on Update.
            var create = () => client.Create(new Entity(TestEntities.Order) { ["xrmv_name"] = "Parity refused " + run, ["xrmv_amount"] = 5m });
            var created = create.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail;
            (created.ErrorCode, created.Message).Should().Be((ErrorCodes.ISVAborted, "fdsfds"));
            var update = () => client.Update(new Entity(TestEntities.Order, order) { ["xrmv_amount"] = 5m });
            var updated = update.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail;
            (updated.ErrorCode, updated.Message).Should().Be((ErrorCodes.ISVAborted, "fdsfds"));
            client.Retrieve(TestEntities.Order, order, new ColumnSet("xrmv_amount")).GetAttributeValue<decimal?>("xrmv_amount")
                .Should().BeNull("the refused update was rolled back");
            return Task.CompletedTask;
        });
    }

    /// <summary>The action <c>xrmv_Recalculate</c> adds its input to the amount and returns the total; a negative input cancels it.</summary>
    public static Task ActionReturnsTotalAsync(ServiceClient client, Func<Task> setup)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        return WithAsync(client, setup, [Recalculate], () =>
        {
            var order = client.Create(new Entity(TestEntities.Order) { ["xrmv_name"] = "Parity action " + run, ["xrmv_amount"] = 10m });
            var target = new EntityReference(TestEntities.Order, order);

            // UpdateStep1: xrmv_amount = xrmv_amount + Increment; AssignOutputArgumentStep2: Total = xrmv_amount (after the update).
            var response = client.Execute(new OrganizationRequest("xrmv_Recalculate") { ["Target"] = target, ["Increment"] = 2.5m });
            response.Results["Total"].Should().Be(12.5m);
            client.Retrieve(TestEntities.Order, order, new ColumnSet("xrmv_amount")).GetAttributeValue<decimal>("xrmv_amount").Should().Be(12.5m);

            // ConditionStep3: Increment < 0 → StopWorkflowStep5, Canceled: the call fails and its update is rolled back (W11, W19).
            var negative = () => client.Execute(new OrganizationRequest("xrmv_Recalculate") { ["Target"] = target, ["Increment"] = -1m });
            negative.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
            client.Retrieve(TestEntities.Order, order, new ColumnSet("xrmv_amount")).GetAttributeValue<decimal>("xrmv_amount").Should().Be(12.5m);

            // Increment is a required input of type Decimal: missing or of another type → 0x80040203 (W14).
            var missing = () => client.Execute(new OrganizationRequest("xrmv_Recalculate") { ["Target"] = target });
            missing.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            return Task.CompletedTask;
        });
    }
}
