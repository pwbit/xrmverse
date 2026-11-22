using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Solution;
using Xrmd.Workflows.Xaml;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 9 (W25, rulings R1–R4): the parity solution is the user's own export <c>xrmdWfTest_1_0_0_2.zip</c> (publisher
/// xrmv, invented data; tests/fixtures/solutions/RealExports). Its five processes import with no unsupported step, every catalog
/// entry its XAML relies on is confirmed by it (<c>export</c>), its XAML is kept byte for byte, and its background OnUpdate, run
/// unmodified, sends its email with the values its XAML sets once the currency it names exists in the organization (and fails,
/// naming that currency, while it does not). The expected values are read from the exported XAML and written here as literals.
/// </summary>
[Collection("real-export")]
public sealed class WorkflowParityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = "xrmv_order";
    private static readonly Guid OnUpdateId = Guid.Parse("bc9a625b-e4de-f111-84e8-002248872b66");

    /// <summary>The currency of the exporting organization that OnUpdate's email names (a lookup literal "Euro").</summary>
    private static readonly Guid ExportCurrencyId = Guid.Parse("692363f0-a3da-f111-84e8-3833c5c48caf");

    /// <summary>The five processes of the export: id, name and category.</summary>
    private static readonly (Guid Id, string Name, int Category)[] Processes =
    [
        (Guid.Parse("0fe1b782-e5de-f111-84e8-002248872b66"), "Child", 0),
        (Guid.Parse("3e1f0307-d8de-f111-84e8-002248872b66"), "OnCreate", 0),
        (OnUpdateId, "OnUpdate", 0),
        (Guid.Parse("216fa344-e5de-f111-84e8-002248872b66"), "New business rule", 2),
        (Guid.Parse("9df696a1-e6de-f111-84e8-002248872b66"), "Recalculate", 3),
    ];

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await WorkflowTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM meta.workflow_wait; DELETE FROM data.asyncoperation;");
    }

    public async Task DisposeAsync()
    {
        await WorkflowTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM meta.workflow_wait; DELETE FROM data.asyncoperation;");
    }

    private static byte[] Zip() =>
        File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmdWfTest_1_0_0_2.zip"));

    /// <summary>The XAML files of the zip by process id, from each Workflow element's XamlFileName.</summary>
    private static Dictionary<Guid, (string Path, byte[] Bytes, int Category)> XamlFiles()
    {
        var (_, customizations, files) = SolutionZip.Read(Zip());
        return customizations.Root!.Element("Workflows")!.Elements("Workflow").ToDictionary(
            w => Guid.Parse((string)w.Attribute("WorkflowId")!),
            w =>
            {
                var path = w.Element("XamlFileName")!.Value.TrimStart('/');
                return (path, files[path], int.Parse(w.Element("Category")!.Value, System.Globalization.CultureInfo.InvariantCulture));
            });
    }

    private static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private async Task<Guid> ImportAsync(bool publishWorkflows)
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = Zip(), ImportJobId = jobId, PublishWorkflows = publishWorkflows });
        return jobId;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Parity_solution_imports_without_unsupported_steps()
    {
        var jobId = await ImportAsync(publishWorkflows: true);

        var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        log.Should().NotContain("steps xrmverse cannot run").And.NotContain("is imported as Draft");
        foreach (var (id, name, category) in Processes)
        {
            var row = await Admin.RetrieveAsync(S.Table, id, new ColumnSet(true));
            (row.GetAttributeValue<string>(S.Name), row.GetAttributeValue<OptionSetValue>(S.CategoryColumn).Value).Should().Be((name, category));
            row.GetAttributeValue<string>(S.UnsupportedStepsColumn).Should().BeNull("{0} has no unsupported step", name);
            row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(S.ActivatedState, "{0} is published; the log:\n{1}", name, log);
        }
        // Each XAML of the zip, parsed in its process's category, has no unsupported step and no unsupported member.
        foreach (var (id, (path, bytes, category)) in XamlFiles())
        {
            var definition = XamlWorkflowParser.Parse(Text(bytes), category, workflowSdkAvailable: false);
            definition.Unsupported.Should().BeEmpty(path);
            definition.UnsupportedMembers.Should().BeEmpty(path);
        }
    }

    [Fact]
    public void Parity_catalog_has_no_assumed_entry_used_by_export()
    {
        var files = XamlFiles();
        files.Should().HaveCount(5);
        var used = files.Values.SelectMany(f => XamlWorkflowParser.CatalogEntriesUsed(Text(f.Bytes), f.Category, workflowSdkAvailable: false))
            .ToHashSet();

        used.Should().NotBeEmpty();
        var entries = ActivityCatalog.Entries.Where(e => used.Contains(e.Element)).ToList();
        entries.Select(e => e.Element).Should().BeEquivalentTo(used, "every entry the parse relies on is in the catalog");
        string.Join(", ", entries.Where(e => e.Source == "assumed").Select(e => e.Element)).Should().BeEmpty();
        string.Join(", ", entries.Where(e => e.Source != "export").Select(e => $"{e.Element} ({e.Source})"))
            .Should().BeEmpty("R4: every entry the export uses is marked export");
    }

    [Fact]
    public async Task Parity_xaml_round_trips_byte_for_byte()
    {
        await ImportAsync(publishWorkflows: false);

        var files = XamlFiles();
        foreach (var (id, (_, bytes, _)) in files)
            (await Admin.RetrieveAsync(S.Table, id, new ColumnSet(S.Xaml))).GetAttributeValue<string>(S.Xaml)
                .Should().Be(Text(bytes), "the row of {0} keeps the XAML as the file has it", id);

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "xrmdWfTest", Managed = false }))
            .ExportSolutionFile;
        var (_, _, written) = SolutionZip.Read(exported);
        foreach (var (path, bytes, _) in files.Values)
            written.Should().ContainKey(path).WhoseValue.Should().Equal(bytes, "the XAML {0} is exported byte for byte", path);
    }

    // ---- OnUpdate run unmodified (R3) ------------------------------------------------------------------------------------------

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }), null, TimeProvider.System);

    private async Task<int> RunJobsAsync()
    {
        var worker = NewWorker();
        var ran = 0;
        while (ran < 50 && await worker.RunOnceAsync(default)) ran++;
        return ran;
    }

    private async Task<(int State, int Status, string? Message)> JobOfAsync(Guid order)
    {
        var job = (await Admin.RetrieveMultipleAsync(new QueryExpression("asyncoperation")
        {
            ColumnSet = new ColumnSet("statecode", "statuscode", "message"),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
        })).Entities.Should().ContainSingle().Subject;
        return (job.GetAttributeValue<OptionSetValue>("statecode").Value, job.GetAttributeValue<OptionSetValue>("statuscode").Value,
            job.GetAttributeValue<string>("message"));
    }

    /// <summary>An order of an account whose primary contact has an address, with an amount of 50 and no status.</summary>
    private async Task<(Guid Order, Guid Contact)> OrderAsync()
    {
        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Parity " + _run, ["emailaddress1"] = $"parity.{_run}@example.com" });
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Parity " + _run, ["primarycontactid"] = new EntityReference("contact", contact) });
        var order = await Admin.CreateAsync(new Entity(Order)
        {
            ["xrmv_name"] = "Parity order " + _run, ["xrmv_amount"] = 50m, ["xrmv_accountid"] = new EntityReference("account", account),
        });
        return (order, contact);
    }

    /// <summary>Changes the status (OnUpdate's trigger), runs the job to its wait, raises the amount above 100, runs it again.</summary>
    private async Task TriggerAndWakeAsync(Guid order)
    {
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_status"] = new OptionSetValue(128830001) });
        (await RunJobsAsync()).Should().Be(1);
        (await JobOfAsync(order)).Should().Match<(int State, int Status, string? Message)>(j => j.State == 1 && j.Status == 10,
            "the job waits: the amount is not above 100");
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_amount"] = 150m });
        (await RunJobsAsync()).Should().Be(1);
    }

    private async Task<List<Entity>> EmailsOfAsync(Guid order) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("email")
        {
            ColumnSet = new ColumnSet("regardingobjectid", "to", "correlatedsubjectchanged", "deliveryprioritycode", "directioncode",
                "followemailuserpreference", "isduplicatesenderunresolved", "notifications", "prioritycode", "transactioncurrencyid",
                "isworkflowcreated", "statecode", "statuscode"),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
        })).Entities.ToList();

    [Fact]
    public async Task Parity_background_email_needs_the_currency_of_the_exporting_organization()
    {
        await ImportAsync(publishWorkflows: false);
        await WorkflowTestHelper.SetStateAsync(Admin, OnUpdateId, S.ActivatedState, S.ActivatedStatus);
        var created = new List<Guid>();
        var orders = new List<Guid>();
        try
        {
            // The organization has no currency of that id: the email step fails explicitly, naming it (checklist: Dataverse's own
            // behaviour on a lookup literal to a record missing in the target is unconfirmed; we never substitute another currency).
            (await ScalarAsync($"SELECT COUNT(*) FROM data.transactioncurrency WHERE transactioncurrencyid = '{ExportCurrencyId}'")).Should().Be(0);
            var (missing, _) = await OrderAsync();
            orders.Add(missing);
            await TriggerAndWakeAsync(missing);
            var failed = await JobOfAsync(missing);
            (failed.State, failed.Status).Should().Be((3, 31));
            failed.Message.Should().Contain("transactioncurrency").And.Contain(ExportCurrencyId.ToString());
            (await EmailsOfAsync(missing)).Should().BeEmpty("the failed step left no email");

            // The same currency (by id) made to exist in the target, as an administrator would: the email is created and sent.
            var currency = await Admin.CreateAsync(new Entity("transactioncurrency", ExportCurrencyId)
            {
                ["currencyname"] = "Euro of the exporting org " + _run, ["isocurrencycode"] = "XTS", ["currencysymbol"] = "X",
                ["currencyprecision"] = 2, ["exchangerate"] = 1m, ["currencytype"] = new OptionSetValue(1),
            });
            created.Add(currency);
            var (order, contact) = await OrderAsync();
            orders.Add(order);
            await TriggerAndWakeAsync(order);
            // Succeeded: OnUpdate's AsyncAutoDelete (1 in the export) deletes the job.
            (await ScalarAsync($"SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = '{order}'")).Should().Be(0);

            var email = (await EmailsOfAsync(order)).Should().ContainSingle("the step after the wait sent the email").Subject;
            // The values OnUpdate's SendEmailStep3 sets (OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66.xaml), copied as literals.
            email.GetAttributeValue<EntityReference>("regardingobjectid").Should().Be(new EntityReference(Order, order));
            email.GetAttributeValue<EntityCollection>("to").Entities.Should().ContainSingle()
                .Which.GetAttributeValue<EntityReference>("partyid").Id.Should().Be(contact);
            email.GetAttributeValue<bool>("correlatedsubjectchanged").Should().BeFalse();
            email.GetAttributeValue<OptionSetValue>("deliveryprioritycode").Value.Should().Be(1);
            email.GetAttributeValue<bool>("directioncode").Should().BeTrue();
            email.GetAttributeValue<bool>("followemailuserpreference").Should().BeFalse();
            email.GetAttributeValue<bool>("isduplicatesenderunresolved").Should().BeFalse();
            email.GetAttributeValue<OptionSetValue>("notifications").Value.Should().Be(0);
            email.GetAttributeValue<OptionSetValue>("prioritycode").Value.Should().Be(1);
            email.GetAttributeValue<EntityReference>("transactioncurrencyid").Id.Should().Be(ExportCurrencyId);
            email.GetAttributeValue<bool>("isworkflowcreated").Should().BeTrue();
            // SendEmail with IssueSend: Pending Send, as xrmverse has no email provider (plan 2.11, A17).
            (email.GetAttributeValue<OptionSetValue>("statecode").Value, email.GetAttributeValue<OptionSetValue>("statuscode").Value)
                .Should().Be((1, 6));
        }
        finally
        {
            await WorkflowTestHelper.SetStateAsync(Admin, OnUpdateId, S.DraftState, S.DraftStatus);
            // The emails name the currency: they go first.
            foreach (var order in orders)
                foreach (var email in await EmailsOfAsync(order))
                    await Admin.DeleteAsync("email", email.Id);
            foreach (var id in created) await Admin.DeleteAsync("transactioncurrency", id);
        }
    }
}
