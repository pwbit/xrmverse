using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Security;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.MigrationTestPackage;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The <c>migrate_*</c> MCP tools (plan M5 task 4; spec §9): System Administrator only, the same JSON as the Admin API routes, an
/// import started and followed to <c>finalized</c>, <c>migrate_activate</c> a dry run unless told otherwise, the passphrase never answered.
/// </summary>
[Collection("xrmd")]
public sealed class McpMigrationToolsTests(XrmdFixture fx) : IDisposable
{
    private const string AdminRequired = "The Admin API requires the System Administrator role.";

    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m5m-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly string[] Destructive = ["migrate_start_import", "migrate_cancel", "migrate_finalize", "migrate_activate"];
    private static readonly string[] ReadOnly = ["migrate_inspect_package", "migrate_get_status", "migrate_get_report"];

    private Task<McpClient> ConnectAsync(WebApplicationFactory<Program> app, Guid? oid = null) => McpTests.ConnectAsync(app, fx.BaseUrl, oid ?? fx.AdminObjectId);

    /// <summary>Calls the tool; the text of its answer, and the answer as JSON.</summary>
    private static async Task<(string Text, JsonObject Json)> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        var text = McpTests.Text(result);
        result.IsError.Should().NotBe(true, text);
        return (text, JsonNode.Parse(text)!.AsObject());
    }

    private WebApplicationFactory<Program> Host() => fx.WithOwnDatabase(b =>
    {
        b.UseSetting("Pipeline:AsyncWorkerEnabled", "true");
        b.UseSetting(MigrationPackages.ImportRootSetting, _root);
    });

    /// <summary>Polls <c>migrate_get_status</c> until the run is no longer queued or running, at most 60 s.</summary>
    private static async Task<JsonObject> WaitAsync(McpClient client, string runId, List<string> answers)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var (text, status) = await CallAsync(client, "migrate_get_status", new() { ["runId"] = runId });
            answers.Add(text);
            if (status["state"]!.GetValue<string>() is not (MigrationRunStates.Queued or MigrationRunStates.Running or MigrationRunStates.Completed)
                || DateTime.UtcNow > until)
                return status;
            await Task.Delay(250);
        }
    }

    [Fact]
    public async Task Tools_are_listed_with_their_flags()
    {
        await using var client = await ConnectAsync(fx.App);
        var tools = (await client.ListToolsAsync()).Where(t => t.Name.StartsWith("migrate_", StringComparison.Ordinal)).ToList();
        tools.Select(t => t.Name).Should().BeEquivalentTo([.. Destructive, .. ReadOnly]);
        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            annotations.Should().NotBeNull(tool.Name);
            if (Destructive.Contains(tool.Name)) annotations!.DestructiveHint.Should().BeTrue(tool.Name);
            else annotations!.ReadOnlyHint.Should().BeTrue(tool.Name);
        }

        var readerOid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), readerOid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "m5m-" + Guid.NewGuid().ToString("N")[..8], ("prvReadSolution", DepthMask.Global)));
        await using var reader = await ConnectAsync(fx.App, readerOid);
        foreach (var name in new[] { "migrate_get_status", "migrate_activate" })
        {
            var denied = await reader.CallToolAsync(name, new Dictionary<string, object?> { ["runId"] = Guid.NewGuid().ToString() });
            denied.IsError.Should().BeTrue(name);
            McpTests.Text(denied).Should().Contain(AdminRequired);
        }
    }

    [Fact]
    public async Task Start_import_from_server_path_runs_to_finalized()
    {
        var file = Build(_root);
        await using var app = Host();
        await using var client = await ConnectAsync(app);

        var (_, started) = await CallAsync(client, "migrate_start_import", new() { ["serverPath"] = file, ["passphrase"] = Passphrase });
        var runId = started["runId"]!.GetValue<string>();
        var status = await WaitAsync(client, runId, []);
        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized, status["message"]?.ToString());

        var (_, report) = await CallAsync(client, "migrate_get_report", new() { ["runId"] = runId });
        report.ToJsonString().Should().Contain("account");

        var wrong = await client.CallToolAsync("migrate_get_status", new Dictionary<string, object?> { ["runId"] = Guid.NewGuid().ToString() });
        wrong.IsError.Should().BeTrue();
        McpTests.Text(wrong).Should().Contain("No migration run");
    }

    [Fact]
    public async Task Activate_tool_defaults_to_dry_run()
    {
        var process = new TestProcess("MigWf" + Guid.NewGuid().ToString("N")[..8], "background-wait", StateCode: 1);
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p
            .Solution(SolutionFixtureZip.FixtureName, SolutionFixtureZip.Build(), order: 1)
            .Solution("xvmigwf", WorkflowSolutionBuilder.Build("xvmigwf", process), order: 2)), null, new SchemaPhase());
        await h.ScalarAsync("UPDATE meta.migration_run SET state = 'finalized' WHERE runid = @run", ("@run", h.RunId));
        await using var client = await ConnectAsync(h.App);

        var (_, dry) = await CallAsync(client, "migrate_activate", new() { ["runId"] = h.RunId.ToString(), ["all"] = true });
        dry["items"]!.AsArray().Select(i => i!["action"]!.GetValue<string>()).Should().OnlyContain(a => a == BusinessLogicActivation.WouldActivate).And.HaveCount(3);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE statecode = 1 AND sdkmessageprocessingstepid IN "
            + "(SELECT id FROM meta.migration_deactivated WHERE runid = @run)", ("@run", h.RunId))).Should().Be(2);

        var (_, done) = await CallAsync(client, "migrate_activate", new() { ["runId"] = h.RunId.ToString(), ["all"] = true, ["dryRun"] = false });
        done["items"]!.AsArray().Select(i => i!["action"]!.GetValue<string>()).Should().OnlyContain(a => a == BusinessLogicActivation.Activated);
    }

    [Fact]
    public async Task Passphrase_is_never_echoed()
    {
        await using var app = Host();
        var token = DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl);
        var packageId = await UploadAsync(app.CreateClient(), token, Build(_root));
        await using var client = await ConnectAsync(app);
        var answers = new List<string>();

        var (inspectText, inspected) = await CallAsync(client, "migrate_inspect_package", new() { ["packageId"] = packageId.ToString(), ["passphrase"] = Passphrase });
        answers.Add(inspectText);
        inspected["tables"]!.AsArray().Select(t => t!["logicalName"]!.GetValue<string>()).Should().Contain(["account", "contact"]);
        inspected["tables"]!.AsArray().Single(t => (string)t!["logicalName"]! == "account")!["records"]!.GetValue<long>().Should().Be(3);

        var wrong = await client.CallToolAsync("migrate_inspect_package",
            new Dictionary<string, object?> { ["packageId"] = packageId.ToString(), ["passphrase"] = "not the right passphrase" });
        wrong.IsError.Should().BeTrue();
        answers.Add(McpTests.Text(wrong));

        var (startText, started) = await CallAsync(client, "migrate_start_import", new() { ["packageId"] = packageId.ToString(), ["passphrase"] = Passphrase });
        answers.Add(startText);
        var runId = started["runId"]!.GetValue<string>();
        await WaitAsync(client, runId, answers);
        answers.Add((await CallAsync(client, "migrate_get_report", new() { ["runId"] = runId })).Text);

        answers.Should().NotContain(a => a.Contains(Passphrase, StringComparison.Ordinal) || a.Contains("not the right passphrase", StringComparison.Ordinal));
    }
}
