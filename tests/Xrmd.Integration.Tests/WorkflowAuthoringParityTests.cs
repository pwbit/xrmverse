using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Cli;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Solution;
using Xrmd.TestSupport;
using Xunit.Abstractions;
using P = Xrmd.Integration.Tests.WorkflowParityAssertions;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>xrmd</c> run in process (<see cref="CliApp.RunAsync"/>) against a host: <paramref name="handler"/> is a TestServer's handler, or
/// null for a real socket (the ServiceClient host on port 80). It signs in as <paramref name="objectId"/> with a development token.
/// </summary>
internal sealed class InProcessCli(HttpMessageHandler? handler) : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-wf-parity", Guid.NewGuid().ToString("N"));

    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();

    public static async Task<InProcessCli> LoginAsync(HttpMessageHandler? handler, string url, Guid objectId)
    {
        var cli = new InProcessCli(handler);
        (await cli.RunAsync("auth", "login", "--url", url, "--mode", "dev", "--object-id", objectId.ToString())).Should().Be(0, cli.Err.ToString());
        return cli;
    }

    public Task<int> RunAsync(params string[] args)
    {
        Out.GetStringBuilder().Clear();
        Err.GetStringBuilder().Clear();
        return CliApp.RunAsync(args, new CliEnvironment(Out, Err, new StringReader(""), false, handler, _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        });
    }

    /// <summary>
    /// <c>xrmd workflow apply &lt;fixture&gt; --solution xrmdWfTest [--activate] -o json</c> of the YAML twin <paramref name="fixture"/>
    /// (<c>tests/fixtures/workflows/yaml</c>, as copied next to the test assembly); the process must be created, not updated.
    /// </summary>
    public async Task ApplyTwinAsync(string fixture, bool activate)
    {
        string[] args = ["workflow", "apply", Path.Combine(System.AppContext.BaseDirectory, "fixtures", "workflows", "yaml", fixture),
            "--solution", "xrmdWfTest", "-o", "json"];
        (await RunAsync(activate ? [.. args, "--activate"] : args)).Should().Be(0, "{0} applies: {1}", fixture, Err.ToString());
        var result = JsonDocument.Parse(Out.ToString()).RootElement;
        result.GetProperty("action").GetString().Should().Be("created", "{0} is authored, not an update of an imported process", fixture);
        result.GetProperty("state").GetString().Should().Be(activate ? "activated" : "draft");
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}

/// <summary>
/// Plan 2.21, Task 8 (A20, first half): the 2.14 parity scenario authored as code. Its five processes are the YAML twins of the export
/// (<c>tests/fixtures/workflows/yaml</c>), applied with <c>xrmd workflow apply &lt;file&gt; --solution xrmdWfTest --activate</c> into an
/// organization that holds the export's <c>xrmv_order</c> table but none of its processes (the export imported without them,
/// <see cref="WorkflowParityAssertions.ZipWithoutProcesses"/>). The real ServiceClient then sees the effects the imported processes give
/// (ServiceClientAcceptanceTests' <c>ServiceClient_parity_*</c>): the same <see cref="WorkflowParityAssertions"/>.
/// </summary>
/// <remarks>Binds Kestrel to 127.0.0.1:80; skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class WorkflowAuthoringParityTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        fx.DrainRequestLog();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var line in fx.DrainRequestLog()) output.WriteLine(line);
        return Task.CompletedTask;
    }

    private ServiceClient Connect()
    {
        var client = new ServiceClient(new Uri(ServiceClientFixture.BaseUrl),
            _ => Task.FromResult(DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl)), useUniqueInstance: true, new TestOutputLogger(output));
        client.IsReady.Should().BeTrue(client.LastError);
        return client;
    }

    /// <summary>
    /// The setup of the authored path: no process of the export is left in the organization (<see cref="WorkflowParityAssertions.DeleteProcesses"/>:
    /// the imported ones of the <c>ServiceClient_parity_*</c> tests share their ids with the twins, so each twin is created from its
    /// YAML), the export without its processes is imported (the table, the publisher and the solution), then each of
    /// <paramref name="fixtures"/> is applied with the CLI and activated, in order.
    /// </summary>
    private Func<Task> Authored(ServiceClient client, params string[] fixtures) => async () =>
    {
        P.DeleteProcesses(client);
        client.Execute(new ImportSolutionRequest { CustomizationFile = P.ZipWithoutProcesses(), ImportJobId = Guid.NewGuid() });
        using var cli = await InProcessCli.LoginAsync(null, ServiceClientFixture.BaseUrl, fx.AdminObjectId);
        foreach (var fixture in fixtures) await cli.ApplyTwinAsync(fixture, activate: true);
    };

    [Fact]
    public async Task Authored_realtime_creates_task()
    {
        using var client = Connect();
        await P.RealtimeCreatesTaskAsync(client, Authored(client, "child.yaml", "oncreate.yaml"));
    }

    [Fact]
    public async Task Authored_background_waits_then_sends_email()
    {
        using var client = Connect();
        await P.BackgroundWaitsThenSendsEmailAsync(client, Authored(client, "onupdate.yaml"));
    }

    [Fact]
    public async Task Authored_business_rule_sets_and_refuses()
    {
        using var client = Connect();
        await P.BusinessRuleSetsAndRefusesAsync(client, Authored(client, "business-rule.yaml"));
    }

    [Fact]
    public async Task Authored_action_returns_total()
    {
        using var client = Connect();
        await P.ActionReturnsTotalAsync(client, Authored(client, "recalculate.yaml"));
    }
}

/// <summary>
/// Plan 2.21, Task 8 (A20): the solution that holds the five processes authored as YAML exports in the real export's shapes, and
/// imports into a second organization whose <c>xrmd workflow export</c> gives the YAML twins back. Each organization is a database of
/// its own on the collection's SQL Server (<see cref="XrmdFixture.WithOwnDatabase"/>), dropped at the end.
/// </summary>
[Collection("real-export")]
public sealed class WorkflowAuthoringParityExportTests(XrmdFixture fx)
{
    /// <summary>The twin of each process of the export, by process id.</summary>
    private static readonly (Guid Id, string Name, string Fixture)[] Twins =
    [
        (P.Child, "Child", "child.yaml"),
        (P.OnCreate, "OnCreate", "oncreate.yaml"),
        (P.OnUpdate, "OnUpdate", "onupdate.yaml"),
        (P.Rule, "New business rule", "business-rule.yaml"),
        (P.Recalculate, "Recalculate", "recalculate.yaml"),
    ];

    /// <summary>The columns of a <c>Workflow</c> element left out of the comparison: the state, the version and the message id differ by design.</summary>
    private static readonly HashSet<string> NotCompared = ["StateCode", "StatusCode", "IntroducedVersion", "SdkMessageId"];

    private OrganizationService Admin(WebApplicationFactory<Program> app) => TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);

    /// <summary>
    /// Imports <paramref name="solution"/> (the parity export without its processes, or the authored export) and creates the currency
    /// OnUpdate's email names, with its id in the exporting organization (invented test data): compiling the twin, or verifying its
    /// decompiled form, needs that record to exist (RF4), as running the imported process does (WorkflowParityTests).
    /// </summary>
    private async Task PrepareAsync(WebApplicationFactory<Program> app, byte[] solution)
    {
        await app.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var admin = Admin(app);
        await admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = solution, ImportJobId = Guid.NewGuid() });
        await admin.CreateAsync(new Entity("transactioncurrency", P.Currency)
        {
            ["currencyname"] = "Euro of the exporting org", ["isocurrencycode"] = "XTS", ["currencysymbol"] = "X", ["currencyprecision"] = 2,
            ["exchangerate"] = 1m, ["currencytype"] = new OptionSetValue(1),
        });
    }

    /// <summary>
    /// The unmanaged export of <c>xrmdWfTest</c> from an organization where the five twins were applied with the CLI, left Draft as the
    /// real export's processes are (StateCode 0).
    /// </summary>
    private async Task<byte[]> AuthoredExportAsync()
    {
        await using var app = fx.WithOwnDatabase(_ => { });
        await PrepareAsync(app, P.ZipWithoutProcesses());
        using (var cli = await InProcessCli.LoginAsync(app.Server.CreateHandler(), fx.BaseUrl, fx.AdminObjectId))
            foreach (var (_, _, fixture) in Twins) await cli.ApplyTwinAsync(fixture, activate: false);
        return await ExportAsync(app);
    }

    private async Task<byte[]> ExportAsync(WebApplicationFactory<Program> app) =>
        ((ExportSolutionResponse)await Admin(app).ExecuteAsync(new ExportSolutionRequest { SolutionName = "xrmdWfTest", Managed = false }))
        .ExportSolutionFile;

    /// <summary>The <c>sdkmessage</c> named <paramref name="name"/>: its id, or null.</summary>
    private static async Task<Guid?> MessageIdAsync(OrganizationService admin, string name) =>
        (await admin.RetrieveMultipleAsync(new QueryExpression("sdkmessage")
        {
            ColumnSet = new ColumnSet("sdkmessageid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, name) } },
        })).Entities.SingleOrDefault()?.Id;

    private static Dictionary<Guid, XElement> Workflows(XDocument customizations) =>
        customizations.Root!.Element("Workflows")!.Elements("Workflow").ToDictionary(w => Guid.Parse((string)w.Attribute("WorkflowId")!));

    private static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The <c>id</c> of each <c>steplabels</c> is the generated <c>LabelId</c> of the XAML's step label (A9): compared as such.</summary>
    private static XElement Normalized(XElement column, string xaml)
    {
        var copy = new XElement(column);
        foreach (var labels in copy.DescendantsAndSelf("steplabels"))
        {
            var id = Guid.Parse((string)labels.Attribute("id")!);
            xaml.Should().Contain($"LabelId=\"{id:D}\"", "the step label {0} of <labels> is the XAML's", id);
            labels.SetAttributeValue("id", "LABEL");
        }
        return copy;
    }

    [Fact]
    public async Task Exported_solution_xaml_matches_real_export_shapes()
    {
        var (_, realCustomizations, realFiles) = SolutionZip.Read(P.Zip());
        var (_, customizations, files) = SolutionZip.Read(await AuthoredExportAsync());

        var real = Workflows(realCustomizations);
        var exported = Workflows(customizations);
        exported.Keys.Should().BeEquivalentTo(real.Keys, "the solution holds the five authored processes");
        using var scope = new AssertionScope(); // every difference, not only the first
        foreach (var (id, realWorkflow) in real)
        {
            var workflow = exported[id];
            var path = realWorkflow.Element("XamlFileName")!.Value.TrimStart('/');
            files.Should().ContainKey(path, "the XAML of {0} is exported where the real export has it", id);
            if (!files.TryGetValue(path, out var bytes)) continue;
            var xaml = Text(bytes);
            GoldenXaml.Canonical(xaml).Should().Be(GoldenXaml.Canonical(Text(realFiles[path])), "the XAML of {0} is the real one", path);

            foreach (var attribute in realWorkflow.Attributes())
                ((string?)workflow.Attribute(attribute.Name)).Should().Be(attribute.Value, "{0}/@{1}", id, attribute.Name);
            foreach (var column in realWorkflow.Elements().Where(c => !NotCompared.Contains(c.Name.LocalName)))
            {
                var mine = workflow.Element(column.Name);
                mine.Should().NotBeNull("the <Workflow> of {0} holds <{1}> as the real export does", id, column.Name);
                if (mine is null) continue;
                Normalized(mine, xaml).ToString().Should().Be(Normalized(column, Text(realFiles[path])).ToString(), "<{0}> of {1}", column.Name, id);
            }
            foreach (var name in NotCompared.Where(n => realWorkflow.Element(n) is not null))
                workflow.Element(name).Should().NotBeNull("the <Workflow> of {0} holds <{1}> (its value is not compared)", id, name);
            var realOrder = realWorkflow.Elements().Select(e => e.Name.LocalName).ToList();
            workflow.Elements().Select(e => e.Name.LocalName).Should().BeSubsetOf(realOrder.Concat(NotCompared),
                "the <Workflow> of {0} holds no element the real one does not", id);
            workflow.Elements().Select(e => e.Name.LocalName).Where(realOrder.Contains).Should().Equal(realOrder, "the elements of {0} in the real export's order", id);
        }
    }

    [Fact]
    public async Task Exported_solution_reimports_and_exports_same_yaml()
    {
        var solution = await AuthoredExportAsync();

        await using var second = fx.WithOwnDatabase(_ => { });
        await PrepareAsync(second, solution);
        var admin = Admin(second);
        foreach (var (id, name, _) in Twins)
            (await admin.RetrieveAsync(S.Table, id, new ColumnSet(S.Name))).GetAttributeValue<string>(S.Name).Should().Be(name, "the import created {0}", id);

        using var cli = await InProcessCli.LoginAsync(second.Server.CreateHandler(), fx.BaseUrl, fx.AdminObjectId);
        foreach (var (_, name, fixture) in Twins)
        {
            (await cli.RunAsync("workflow", "export", name)).Should().Be(0, "{0} exports: {1}", name, cli.Err.ToString());
            cli.Out.ToString().Should().Be(AuthoringTestHelper.Fixture(fixture), "the export of {0} is its YAML twin", name);
        }
    }

    [Fact]
    public async Task Exported_draft_action_has_its_message_and_reimports_and_activates()
    {
        // A Draft authored action has its message, as an imported Draft action has (2.14 W14: the import writes it whatever the state);
        // a call to it is the 400 of a message whose action is not activated.
        byte[] solution;
        Guid messageId;
        await using (var app = fx.WithOwnDatabase(_ => { }))
        {
            await PrepareAsync(app, P.ZipWithoutProcesses());
            using (var cli = await InProcessCli.LoginAsync(app.Server.CreateHandler(), fx.BaseUrl, fx.AdminObjectId))
                await cli.ApplyTwinAsync("recalculate.yaml", activate: false);
            var admin = Admin(app);
            messageId = (await MessageIdAsync(admin, "xrmv_Recalculate")).Should().NotBeNull("the Draft action has its message").And.Subject!.Value;
            (await admin.RetrieveAsync(S.Table, P.Recalculate, new ColumnSet(S.SdkMessageId))).GetAttributeValue<EntityReference>(S.SdkMessageId)
                .Id.Should().Be(messageId);
            var order = await admin.CreateAsync(new Entity(TestEntities.Order) { ["xrmv_name"] = "Draft action" });
            var call = () => admin.ExecuteAsync(new OrganizationRequest("xrmv_Recalculate")
            {
                ["Target"] = new EntityReference(TestEntities.Order, order), ["Increment"] = 1m,
            });
            var fault = (await call.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which;
            (fault.HttpStatus, fault.Message.Contains("is not activated", StringComparison.Ordinal)).Should().Be((400, true), fault.Message);
            solution = await ExportAsync(app);
        }

        var (_, customizations, _) = SolutionZip.Read(solution);
        Workflows(customizations)[P.Recalculate].Element("SdkMessageId")!.Value.Should().Be(messageId.ToString("B"));

        await using var second = fx.WithOwnDatabase(_ => { });
        await PrepareAsync(second, solution);
        var secondAdmin = Admin(second);
        (await MessageIdAsync(secondAdmin, "xrmv_Recalculate")).Should().Be(messageId, "the import keeps the exported SdkMessageId");
        await WorkflowTestHelper.SetStateAsync(secondAdmin, P.Recalculate, S.ActivatedState, S.ActivatedStatus);
        var target = await secondAdmin.CreateAsync(new Entity(TestEntities.Order) { ["xrmv_name"] = "Reimported action", ["xrmv_amount"] = 10m });
        var response = await secondAdmin.ExecuteAsync(new OrganizationRequest("xrmv_Recalculate")
        {
            ["Target"] = new EntityReference(TestEntities.Order, target), ["Increment"] = 2.5m,
        });
        response.Results["Total"].Should().Be(12.5m);
    }
}
