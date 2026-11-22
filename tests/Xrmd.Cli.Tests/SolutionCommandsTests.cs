using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary>
/// <c>xrmd solution import --mode</c>, <c>uninstall</c>, <c>promote</c> and <c>layers</c> (plan 2.6, Task 8, ruling M16): the HTTP
/// call each makes and its output, against a scripted server.
/// </summary>
public sealed class SolutionCommandsTests : IDisposable
{
    private const string Url = "http://xrmd.test:5000";

    private const string PlanJson = """
        {"deleted":[{"type":1,"name":"xm_item"}],"kept":[{"type":26,"name":"Active Items","reason":"in another solution"}],
         "restored":["account"],"dataLoss":[{"table":"xm_item","column":null,"rows":3}]}
        """;

    private const string EmptyPlanJson = """{"deleted":[],"kept":[],"restored":[],"dataLoss":[]}""";

    private readonly string _zip = Path.Combine(Path.GetTempPath(), "xrmd-solcmd-" + Guid.NewGuid().ToString("N") + ".zip");

    public SolutionCommandsTests() => File.WriteAllBytes(_zip, [0x50, 0x4b, 3, 4]);

    public void Dispose() => File.Delete(_zip);

    private static async Task<TestCli> CliAsync(Func<HttpRequestMessage, HttpResponseMessage> respond, bool tty = false, string input = "")
    {
        var cli = new TestCli(respond) { IsInputTty = tty, Input = input };
        (await cli.LoginDevAsync(Url)).Should().Be(0, cli.Err.ToString());
        return cli;
    }

    private static HttpResponseMessage Ok(string json = "{}") => FakeHandler.Json(HttpStatusCode.OK, json);

    private static IEnumerable<(string Path, string? Body)> Calls(TestCli cli) =>
        cli.Handler.Requests.Zip(cli.Handler.Bodies, (r, b) => (r.Method.Method + " " + r.RequestUri!.AbsolutePath, b))
            .Where(c => c.Item1.Contains("/solutions") || c.Item1.Contains("Solution") || c.Item1.Contains("StageAndUpgrade") || c.Item1.Contains("/layers"));

    // ---- import --mode ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("update", "ImportSolution", false)]
    [InlineData("upgrade", "StageAndUpgrade", false)]
    [InlineData("stage", "ImportSolution", true)]
    public async Task Import_mode_picks_the_message(string mode, string message, bool holding)
    {
        using var cli = await CliAsync(_ => Ok());
        (await cli.RunAsync("solution", "import", _zip, "--mode", mode, "--overwrite")).Should().Be(0, cli.Err.ToString());
        var call = Calls(cli).Single();
        call.Path.Should().Be("POST /api/data/v9.2/" + message);
        using var body = JsonDocument.Parse(call.Body!);
        body.RootElement.GetProperty("OverwriteUnmanagedCustomizations").GetBoolean().Should().BeTrue();
        body.RootElement.TryGetProperty("HoldingSolution", out var flag).Should().Be(holding);
        if (holding) flag.GetBoolean().Should().BeTrue();
    }

    /// <summary>The stage outcome names the holding solution and the promote command from the file's unique name.</summary>
    [Fact]
    public async Task Import_stage_names_the_holding_solution_and_the_promote()
    {
        var zip = Path.Combine(Path.GetTempPath(), "xrmd-solcmd-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("solution.xml").Open());
                writer.Write("<ImportExportXml><SolutionManifest><UniqueName>xm_base</UniqueName></SolutionManifest></ImportExportXml>");
            }
            using var cli = await CliAsync(_ => Ok());
            (await cli.RunAsync("solution", "import", zip, "--mode", "stage")).Should().Be(0, cli.Err.ToString());
            cli.Out.ToString().Should().Contain("holding solution xm_base_Upgrade").And.Contain("xrmd solution promote xm_base'")
                .And.NotContain("{name}").And.NotContain("<name>");

            // A file without a readable solution.xml: no placeholder either.
            using var other = await CliAsync(_ => Ok());
            (await other.RunAsync("solution", "import", _zip, "--mode", "stage")).Should().Be(0, other.Err.ToString());
            other.Out.ToString().Should().Contain("_Upgrade").And.NotContain("{name}");
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_defaults_to_update_and_rejects_an_unknown_mode()
    {
        using var cli = await CliAsync(_ => Ok());
        (await cli.RunAsync("solution", "import", _zip)).Should().Be(0, cli.Err.ToString());
        Calls(cli).Single().Path.Should().EndWith("/ImportSolution");
        (await cli.RunAsync("solution", "import", _zip, "--mode", "replace")).Should().Be(ExitCodes.Usage);
    }

    [Fact]
    public async Task Import_upgrade_async_queues_StageAndUpgradeAsync()
    {
        var op = Guid.NewGuid();
        using var cli = await CliAsync(r => r.RequestUri!.AbsolutePath.EndsWith("StageAndUpgradeAsync")
            ? Ok($$$"""{"AsyncOperationId":"{{{op}}}","ImportJobKey":"{{{Guid.NewGuid()}}}"}""")
            : Ok("""{"statecode":3,"statuscode":30,"message":null}"""));
        (await cli.RunAsync("solution", "import", _zip, "--mode", "upgrade", "--async")).Should().Be(0, cli.Err.ToString());
        Calls(cli).First().Path.Should().EndWith("/StageAndUpgradeAsync");
        cli.Out.ToString().Should().Contain("Imported and upgraded");
    }

    [Fact]
    public async Task Import_upgrade_data_loss_refusal_exits_5_and_names_stage_and_promote()
    {
        const string message = "The solution 'xm_base' was not upgraded: it would drop data: table xm_item (1 row). Stage the file instead (xrmd solution import --mode stage), then confirm the loss: xrmd solution promote xm_base --drop-data. Nothing was changed.";
        using var cli = await CliAsync(r => r.RequestUri!.AbsolutePath.EndsWith("StageAndUpgrade")
            ? FakeHandler.Json(HttpStatusCode.BadRequest, $$$"""{"error":{"code":"0x80040203","message":"{{{message}}}"}}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        (await cli.RunAsync("solution", "import", _zip, "--mode", "upgrade")).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().Contain("--mode stage").And.Contain("promote xm_base --drop-data");
    }

    // ---- uninstall ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Uninstall_yes_posts_once_and_prints_the_plan_in_table_mode()
    {
        using var cli = await CliAsync(_ => Ok(PlanJson));
        (await cli.RunAsync("solution", "uninstall", "xm_base", "--drop-data", "--yes")).Should().Be(0, cli.Err.ToString());
        var call = Calls(cli).Single();
        call.Path.Should().Be("POST /admin/solutions/xm_base/uninstall");
        JsonDocument.Parse(call.Body!).RootElement.GetProperty("dropData").GetBoolean().Should().BeTrue();
        JsonDocument.Parse(call.Body!).RootElement.GetProperty("dryRun").GetBoolean().Should().BeFalse();
        cli.Out.ToString().Should().Contain("Uninstalled 'xm_base'").And.Contain("xm_item").And.Contain("Data loss").And.Contain("account");
    }

    [Fact]
    public async Task Uninstall_json_mode_prints_the_server_answer()
    {
        using var cli = await CliAsync(_ => Ok(PlanJson));
        (await cli.RunAsync("solution", "uninstall", "xm_base", "--yes", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var json = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        json.GetProperty("restored")[0].GetString().Should().Be("account");
        json.GetProperty("dataLoss")[0].GetProperty("rows").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task Uninstall_dry_run_changes_nothing_and_asks_nothing()
    {
        using var cli = await CliAsync(_ => Ok(PlanJson));
        (await cli.RunAsync("solution", "uninstall", "xm_base", "--dry-run")).Should().Be(0, cli.Err.ToString());
        var call = Calls(cli).Single();
        JsonDocument.Parse(call.Body!).RootElement.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        cli.Out.ToString().Should().Contain("Dry run").And.Contain("xm_item");
    }

    [Fact]
    public async Task Uninstall_without_yes_prints_the_dry_run_plan_and_asks()
    {
        using var cli = await CliAsync(_ => Ok(EmptyPlanJson), tty: true, input: "n\n");
        (await cli.RunAsync("solution", "uninstall", "xm_base")).Should().Be(ExitCodes.Unexpected);
        cli.Err.ToString().Should().Contain("would be uninstalled").And.Contain("[y/N]").And.Contain("Aborted");
        var calls = Calls(cli).ToList();
        calls.Should().ContainSingle();
        JsonDocument.Parse(calls[0].Body!).RootElement.GetProperty("dryRun").GetBoolean().Should().BeTrue();

        using var yes = await CliAsync(_ => Ok(EmptyPlanJson), tty: true, input: "y\n");
        (await yes.RunAsync("solution", "uninstall", "xm_base")).Should().Be(0, yes.Err.ToString());
        var both = Calls(yes).ToList();
        both.Should().HaveCount(2);
        JsonDocument.Parse(both[1].Body!).RootElement.GetProperty("dryRun").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Uninstall_without_yes_and_without_a_terminal_is_a_usage_error()
    {
        using var cli = await CliAsync(_ => Ok(EmptyPlanJson));
        (await cli.RunAsync("solution", "uninstall", "xm_base")).Should().Be(ExitCodes.Usage);
        Calls(cli).Should().ContainSingle();
    }

    [Fact]
    public async Task Uninstall_that_drops_data_without_drop_data_is_refused_before_the_question()
    {
        using var cli = await CliAsync(_ => Ok(PlanJson), tty: true, input: "y\n");
        (await cli.RunAsync("solution", "uninstall", "xm_base")).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().Contain("--drop-data").And.NotContain("[y/N]");
        Calls(cli).Should().ContainSingle();
    }

    [Fact]
    public async Task Uninstall_of_an_unknown_solution_exits_4()
    {
        using var cli = await CliAsync(_ => FakeHandler.Json(HttpStatusCode.NotFound,
            """{"error":{"code":"0x80040217","message":"The solution with unique name 'x' does not exist."}}"""));
        (await cli.RunAsync("solution", "uninstall", "x", "--yes")).Should().Be(ExitCodes.NotFound);
    }

    // ---- promote ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Promote_yes_posts_to_the_promote_route()
    {
        using var cli = await CliAsync(_ => Ok(EmptyPlanJson));
        (await cli.RunAsync("solution", "promote", "xm_base", "--yes")).Should().Be(0, cli.Err.ToString());
        var call = Calls(cli).Single();
        call.Path.Should().Be("POST /admin/solutions/xm_base/promote");
        JsonDocument.Parse(call.Body!).RootElement.GetProperty("dropData").GetBoolean().Should().BeFalse();
        cli.Out.ToString().Should().Contain("Promoted 'xm_base'");
    }

    [Fact]
    public async Task Promote_without_yes_asks_after_the_plan_and_drop_data_is_sent()
    {
        using var cli = await CliAsync(_ => Ok(PlanJson), tty: true, input: "y\n");
        (await cli.RunAsync("solution", "promote", "xm_base", "--drop-data")).Should().Be(0, cli.Err.ToString());
        var calls = Calls(cli).ToList();
        calls.Should().HaveCount(2);
        calls.Should().OnlyContain(c => c.Path == "POST /admin/solutions/xm_base/promote");
        cli.Err.ToString().Should().Contain("Data loss").And.Contain("[y/N]");
    }

    // ---- layers -------------------------------------------------------------------------------------------------------

    private const string LayersJson = """
        [{"componentType":1,"objectId":"11111111-1111-1111-1111-111111111111","solution":"xm_base","kind":"managed","order":1024,"version":"1.0.0.0"},
         {"componentType":1,"objectId":"11111111-1111-1111-1111-111111111111","solution":"System","kind":"system","order":0,"version":null}]
        """;

    [Fact]
    public async Task Layers_of_a_solution_uses_the_solution_route()
    {
        using var cli = await CliAsync(_ => Ok(LayersJson));
        (await cli.RunAsync("solution", "layers", "xm_base")).Should().Be(0, cli.Err.ToString());
        Calls(cli).Single().Path.Should().Be("GET /admin/solutions/xm_base/layers");
        cli.Out.ToString().Should().Contain("entity (1)").And.Contain("xm_base").And.Contain("managed").And.Contain("1.0.0.0");
    }

    [Fact]
    public async Task Layers_of_a_component_uses_the_component_route_and_json_mode()
    {
        using var cli = await CliAsync(_ => Ok(LayersJson));
        (await cli.RunAsync("solution", "layers", "--type", "entity", "--id", "11111111-1111-1111-1111-111111111111", "-o", "json"))
            .Should().Be(0, cli.Err.ToString());
        Calls(cli).Single().Path.Should().Be("GET /admin/layers/1/11111111-1111-1111-1111-111111111111");
        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        rows.GetArrayLength().Should().Be(2);
        rows[1].GetProperty("kind").GetString().Should().Be("system");
        rows[0].GetProperty("order").GetInt64().Should().Be(1024);
    }

    [Fact]
    public async Task Layers_needs_a_name_or_type_with_id()
    {
        using var cli = await CliAsync(_ => Ok(LayersJson));
        (await cli.RunAsync("solution", "layers")).Should().Be(ExitCodes.Usage);
        (await cli.RunAsync("solution", "layers", "--type", "entity")).Should().Be(ExitCodes.Usage);
        (await cli.RunAsync("solution", "layers", "xm_base", "--type", "entity", "--id", Guid.NewGuid().ToString())).Should().Be(ExitCodes.Usage);
        (await cli.RunAsync("solution", "layers", "--type", "entity", "--id", "nope")).Should().Be(ExitCodes.Usage);
        Calls(cli).Should().BeEmpty();
    }
}
