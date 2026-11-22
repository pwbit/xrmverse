using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Metadata;
using Xrmd.Schema;
using Xrmd.Security;
using Xrmd.Workflows.Authoring;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.21, Task 7 (A18): <c>xrmd workflow</c> and the MCP tools <c>apply_workflow</c>, <c>export_workflow</c>, <c>list_workflows</c>
/// and <c>set_workflow_state</c> against the in-memory host and the parity export's database. Both run as the caller.
/// </summary>
[Collection("real-export")]
public sealed class CliWorkflowMcpTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = "xrmv_order";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-wf", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public async Task InitializeAsync()
    {
        await AuthoringTestHelper.EnsureParityAsync(fx);
        await WorkflowTestHelper.ClearAsync(fx);
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    public async Task DisposeAsync()
    {
        await WorkflowTestHelper.ClearAsync(fx);
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string Name(string what) => $"{what} {_run}";

    private Guid IdOf(string name) => WorkflowIdentity.IdOf(WorkflowKind.Workflow, Order, name);

    private async Task<int?> StateAsync(string name) =>
        await AuthoringTestHelper.ScalarAsync(fx, "SELECT statecode FROM data.workflow WHERE workflowid = @id", ("@id", IdOf(name))) is int state ? state : null;

    private static string Realtime(string name, string subject = "Hi") => $$"""
        kind: workflow
        name: {{name}}
        entity: {{Order}}
        mode: realtime
        trigger:
          create: true
        steps:
          - create: { entity: task, set: { subject: {{subject}}, regardingobjectid: { record: primary } } }

        """;

    private CliEnvironment Env() =>
        new(_out, _err, new StringReader(""), false, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        return CliApp.RunAsync(args, Env());
    }

    private string WriteFile(string yaml)
    {
        Directory.CreateDirectory(_configDir);
        var path = Path.Combine(_configDir, Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }

    // ---- the CLI -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cli_apply_export_round_trip_end_to_end()
    {
        var yaml = Realtime(Name("Round trip"));
        var file = WriteFile(yaml);

        (await RunAsync("workflow", "apply", file, "--dry-run")).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("would be created");
        (await StateAsync(Name("Round trip"))).Should().BeNull("a dry run writes nothing");

        (await RunAsync("workflow", "apply", file, "-o", "json")).Should().Be(0, _err.ToString());
        var applied = JsonDocument.Parse(_out.ToString()).RootElement;
        applied.GetProperty("action").GetString().Should().Be("created");
        applied.GetProperty("id").GetGuid().Should().Be(IdOf(Name("Round trip")));
        (await StateAsync(Name("Round trip"))).Should().Be(S.DraftState);

        (await RunAsync("workflow", "list", "--kind", "workflow", "--entity", Order, "-o", "json")).Should().Be(0, _err.ToString());
        JsonDocument.Parse(_out.ToString()).RootElement.EnumerateArray()
            .Should().Contain(e => e.GetProperty("name").GetString() == Name("Round trip") && e.GetProperty("state").GetString() == "draft");

        (await RunAsync("workflow", "export", Name("Round trip"))).Should().Be(0, _err.ToString());
        var exported = _out.ToString();
        exported.Should().Contain($"name: {Name("Round trip")}").And.Contain($"id: {IdOf(Name("Round trip")):D}");
        WorkflowYaml.Read(exported).Should().NotBeNull("the export reads back");

        // The exported file applies again as it is: nothing to change.
        var again = WriteFile(exported);
        (await RunAsync("workflow", "apply", again, "-o", "json")).Should().Be(0, _err.ToString());
        JsonDocument.Parse(_out.ToString()).RootElement.GetProperty("action").GetString().Should().Be("unchanged");

        (await RunAsync("workflow", "export", IdOf(Name("Round trip")).ToString(), "--format", "xaml")).Should().Be(0, _err.ToString());
        _out.ToString().Should().StartWith("<Activity");

        (await RunAsync("workflow", "export", Name("Nobody"))).Should().Be(4, _err.ToString());
        (await RunAsync("workflow", "apply", WriteFile(yaml.Replace("subject: Hi", "subjet: Hi", StringComparison.Ordinal)))).Should().Be(5);
        _err.ToString().Should().Contain("subjet").And.Contain(":8:");
    }

    [Fact]
    public async Task Cli_apply_then_activate_runs_the_process()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var file = WriteFile(Realtime(Name("Runs"), subject: "From the CLI " + _run));

        (await RunAsync("workflow", "apply", file, "--activate")).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("activated");
        (await StateAsync(Name("Runs"))).Should().Be(S.ActivatedState);

        var order = await admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Order") });
        (await admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            ColumnSet = new ColumnSet("subject"),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
        })).Entities.Should().ContainSingle().Which.GetAttributeValue<string>("subject").Should().Be("From the CLI " + _run);

        (await RunAsync("workflow", "deactivate", Name("Runs"))).Should().Be(0, _err.ToString());
        (await StateAsync(Name("Runs"))).Should().Be(S.DraftState);
        (await RunAsync("workflow", "activate", IdOf(Name("Runs")).ToString())).Should().Be(0, _err.ToString());
        (await StateAsync(Name("Runs"))).Should().Be(S.ActivatedState);
    }

    // ---- the MCP tools -----------------------------------------------------------------------------------------------------------

    private async Task<McpClient> ConnectAsync(Guid oid)
    {
        var http = fx.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(fx.BaseUrl + "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static async Task<JsonObject> CallJsonAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        result.IsError.Should().NotBe(true, Text(result));
        return JsonNode.Parse(Text(result))!.AsObject();
    }

    [Fact]
    public async Task Mcp_apply_workflow_defaults_to_dry_run()
    {
        await using var client = await ConnectAsync(fx.AdminObjectId);
        var yaml = Realtime(Name("Mcp dry"));

        var dry = await CallJsonAsync(client, "apply_workflow", new() { ["yaml"] = yaml });
        dry["action"]!.GetValue<string>().Should().Be("created");
        dry["state"]!.GetValue<string>().Should().Be("draft");
        dry["id"]!.GetValue<Guid>().Should().Be(IdOf(Name("Mcp dry")));
        dry["errors"]!.AsArray().Should().BeEmpty();
        (await StateAsync(Name("Mcp dry"))).Should().BeNull("dryRun defaults to true");

        var applied = await CallJsonAsync(client, "apply_workflow", new() { ["yaml"] = yaml, ["dryRun"] = false, ["activate"] = true });
        applied["state"]!.GetValue<string>().Should().Be("activated");
        (await StateAsync(Name("Mcp dry"))).Should().Be(S.ActivatedState);
    }

    [Fact]
    public async Task Mcp_refused_apply_is_error_with_result()
    {
        await using var client = await ConnectAsync(fx.AdminObjectId);

        var result = await client.CallToolAsync("apply_workflow", new Dictionary<string, object?>
        {
            ["yaml"] = Realtime(Name("Mcp refused")).Replace("subject: Hi", "subjet: Hi", StringComparison.Ordinal),
            ["dryRun"] = false,
        });

        result.IsError.Should().BeTrue();
        var text = Text(result);
        text.Should().Contain("0x80040203").And.Contain("subjet").And.Contain("\"errors\"").And.Contain("\"line\":8");
        (await StateAsync(Name("Mcp refused"))).Should().BeNull();
    }

    [Fact]
    public async Task Mcp_tools_run_as_caller_without_admin_role()
    {
        // A custom role with the workflow privileges and no administrator role can author; a Basic User cannot.
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvCreateWorkflow", DepthMask.Global), ("prvReadWorkflow", DepthMask.Global),
            ("prvWriteWorkflow", DepthMask.Global), ("prvActivateSynchronousWorkflow", DepthMask.Global));
        await using (var author = await ConnectAsync(oid))
        {
            var applied = await CallJsonAsync(author, "apply_workflow", new() { ["yaml"] = Realtime(Name("Mcp author")), ["dryRun"] = false, ["activate"] = true });
            applied["state"]!.GetValue<string>().Should().Be("activated");
            (await CallJsonAsync(author, "list_workflows", new() { ["entity"] = Order }))["workflows"]!.AsArray()
                .Should().Contain(n => n!["name"]!.GetValue<string>() == Name("Mcp author"));
        }

        var (_, basic) = await AuthoringTestHelper.UserWithRoleAndObjectIdAsync(fx, StandardRoles.BasicUser);
        await using var denied = await ConnectAsync(basic);
        var result = await denied.CallToolAsync("apply_workflow", new Dictionary<string, object?> { ["yaml"] = Realtime(Name("Mcp basic")), ["dryRun"] = false });
        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("0x80040220");
        (await StateAsync(Name("Mcp basic"))).Should().BeNull();
        (await denied.CallToolAsync("list_workflows", new Dictionary<string, object?>())).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Mcp_export_and_set_state()
    {
        await using var client = await ConnectAsync(fx.AdminObjectId);
        await CallJsonAsync(client, "apply_workflow", new() { ["yaml"] = Realtime(Name("Mcp state")), ["dryRun"] = false });

        var yaml = await CallJsonAsync(client, "export_workflow", new() { ["nameOrId"] = Name("Mcp state") });
        yaml["format"]!.GetValue<string>().Should().Be("yaml");
        yaml["text"]!.GetValue<string>().Should().Contain($"name: {Name("Mcp state")}");
        var xaml = await CallJsonAsync(client, "export_workflow", new() { ["nameOrId"] = IdOf(Name("Mcp state")).ToString(), ["format"] = "xaml" });
        xaml["text"]!.GetValue<string>().Should().StartWith("<Activity");

        var on = await CallJsonAsync(client, "set_workflow_state", new() { ["nameOrId"] = Name("Mcp state"), ["activated"] = true });
        on["state"]!.GetValue<string>().Should().Be("activated");
        on["kind"]!.GetValue<string>().Should().Be("workflow");
        (await StateAsync(Name("Mcp state"))).Should().Be(S.ActivatedState);
        var off = await CallJsonAsync(client, "set_workflow_state", new() { ["nameOrId"] = IdOf(Name("Mcp state")).ToString(), ["activated"] = false });
        off["state"]!.GetValue<string>().Should().Be("draft");

        var missing = await client.CallToolAsync("export_workflow", new Dictionary<string, object?> { ["nameOrId"] = Name("Nobody") });
        missing.IsError.Should().BeTrue();
        Text(missing).Should().Contain("0x80040217");
    }
}
