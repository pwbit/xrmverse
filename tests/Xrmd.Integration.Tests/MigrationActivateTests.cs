using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>POST /admin/migrate/runs/{id}/activate</c> (plan M5 task 2; spec §7.9): only what the import left off is switched back on, only
/// once the run is finalized, with a dry run; deleted or already active items are skipped; the report lists the items.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationActivateTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m5a-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The two plug-in steps of the fixture solution, both enabled in its file.</summary>
    private static readonly Guid[] Steps = [Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21"), Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e22")];

    /// <summary>A harness whose run imported the fixture solution and an activated process, then was finalized.</summary>
    private async Task<(MigrationPhaseHarness H, TestProcess Process)> ImportedAsync(bool finalized = true)
    {
        var process = new TestProcess("MigWf" + Guid.NewGuid().ToString("N")[..8], "background-wait", StateCode: 1);
        var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p
            .Solution(SolutionFixtureZip.FixtureName, SolutionFixtureZip.Build(), order: 1)
            .Solution("xvmigwf", WorkflowSolutionBuilder.Build("xvmigwf", process), order: 2)), null, new SchemaPhase());
        if (finalized) await h.ScalarAsync("UPDATE meta.migration_run SET state = 'finalized' WHERE runid = @run", ("@run", h.RunId));
        return (h, process);
    }

    private static async Task<JsonObject> ActivateAsync(MigrationPhaseHarness h, string body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await h.SendAsync(HttpMethod.Post, $"admin/migrate/runs/{h.RunId}/activate", body);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static Dictionary<string, string> Actions(JsonObject result) =>
        result["items"]!.AsArray().ToDictionary(i => (string)i!["id"]!, i => (string)i!["action"]!);

    private Task<object?> StepStateAsync(MigrationPhaseHarness h, Guid id) =>
        h.ScalarAsync("SELECT statecode FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", id));

    [Fact]
    public async Task Activate_needs_a_finalized_run()
    {
        var (h, _) = await ImportedAsync(finalized: false);
        await using var _h = h;
        (await ActivateAsync(h, """{"all":true}""", HttpStatusCode.Conflict)).ToJsonString().Should().Contain("finalize");
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var (h, _) = await ImportedAsync();
        await using var _h = h;
        var result = await ActivateAsync(h, """{"all":true,"dryRun":true}""");
        Actions(result).Values.Should().OnlyContain(a => a == BusinessLogicActivation.WouldActivate).And.HaveCount(3);
        foreach (var step in Steps) (await StepStateAsync(h, step)).Should().Be(1);
    }

    [Fact]
    public async Task Activate_by_id_switches_on_only_that_item()
    {
        var (h, _) = await ImportedAsync();
        await using var _h = h;
        var result = await ActivateAsync(h, $$"""{"ids":["{{Steps[0]}}"]}""");
        Actions(result).Should().Equal(new Dictionary<string, string> { [Steps[0].ToString()] = BusinessLogicActivation.Activated });
        (await StepStateAsync(h, Steps[0])).Should().Be(0);
        (await h.ScalarAsync("SELECT statuscode FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", Steps[0]))).Should().Be(1);
        (await StepStateAsync(h, Steps[1])).Should().Be(1);
    }

    [Fact]
    public async Task Activate_all_restores_the_source_states()
    {
        var (h, process) = await ImportedAsync();
        await using var _h = h;
        var result = await ActivateAsync(h, """{"all":true}""");
        Actions(result).Values.Should().OnlyContain(a => a == BusinessLogicActivation.Activated).And.HaveCount(3);
        foreach (var step in Steps) (await StepStateAsync(h, step)).Should().Be(0);
        (await h.ScalarAsync("SELECT statecode FROM data.workflow WHERE workflowid = @id", ("@id", process.Id))).Should().Be(1);
        (await h.ScalarAsync("SELECT COUNT(*) FROM meta.migration_deactivated WHERE runid = @run AND activatedon IS NOT NULL", ("@run", h.RunId))).Should().Be(3);
    }

    [Fact]
    public async Task Activate_skips_deleted_and_already_on()
    {
        var (h, process) = await ImportedAsync();
        await using var _h = h;
        await h.ScalarAsync("UPDATE data.sdkmessageprocessingstep SET statecode = 0, statuscode = 1 WHERE sdkmessageprocessingstepid = @id", ("@id", Steps[0]));
        await h.ScalarAsync("DELETE FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", Steps[1]));

        var first = Actions(await ActivateAsync(h, """{"all":true}"""));
        first[Steps[0].ToString()].Should().Be(BusinessLogicActivation.Skipped);
        first[Steps[1].ToString()].Should().Be(BusinessLogicActivation.Skipped);
        first[process.Id.ToString()].Should().Be(BusinessLogicActivation.Activated);

        Actions(await ActivateAsync(h, """{"all":true}""")).Values.Should().OnlyContain(a => a == BusinessLogicActivation.Skipped);
    }

    [Fact]
    public async Task Unknown_id_is_rejected()
    {
        var (h, _) = await ImportedAsync();
        await using var _h = h;
        var unknown = Guid.NewGuid();
        (await ActivateAsync(h, $$"""{"ids":["{{unknown}}"]}""", HttpStatusCode.BadRequest)).ToJsonString().Should().Contain(unknown.ToString());
        await ActivateAsync(h, """{}""", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Report_lists_left_off_items()
    {
        var (h, process) = await ImportedAsync();
        await using var _h = h;
        var res = await h.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{h.RunId}/report");
        var report = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        var items = report["deactivated"]!.AsArray();
        items.Should().HaveCount(3);
        items.Single(i => (string)i!["id"]! == process.Id.ToString())!["sourceState"]!.GetValue<int>().Should().Be(1);
        items.Single(i => (string)i!["id"]! == Steps[0].ToString())!["sourceState"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Unexpected_failure_of_one_item_does_not_stop_the_others()
    {
        var (h, process) = await ImportedAsync();
        await using var _h = h;
        // A database error (not an xrmverse fault) on the first step only.
        await h.ScalarAsync(TestProvider.IsPostgres
            ? $"""
              CREATE FUNCTION data.tr_m5_fail() RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN IF NEW.sdkmessageprocessingstepid = '{Steps[0]}' THEN RAISE EXCEPTION 'The database refused the update.'; END IF; RETURN NEW; END $$;
              CREATE TRIGGER tr_m5_fail AFTER UPDATE ON data.sdkmessageprocessingstep FOR EACH ROW EXECUTE FUNCTION data.tr_m5_fail();
              """
            : $"""
              CREATE TRIGGER data.tr_m5_fail ON data.sdkmessageprocessingstep AFTER UPDATE AS
              IF EXISTS (SELECT 1 FROM inserted WHERE sdkmessageprocessingstepid = '{Steps[0]}') THROW 50001, 'The database refused the update.', 1;
              """);

        var result = Actions(await ActivateAsync(h, """{"all":true}"""));
        result[Steps[0].ToString()].Should().Be(BusinessLogicActivation.Failed);
        result[Steps[1].ToString()].Should().Be(BusinessLogicActivation.Activated);
        result[process.Id.ToString()].Should().Be(BusinessLogicActivation.Activated);
        (await StepStateAsync(h, Steps[0])).Should().Be(1);
        (await StepStateAsync(h, Steps[1])).Should().Be(0);
    }
}
