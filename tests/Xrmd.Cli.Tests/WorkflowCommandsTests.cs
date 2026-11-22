using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd workflow</c> (plan 2.21, A18) against a fake authoring API.</summary>
public class WorkflowCommandsTests
{
    private const string Id = "0fe1b782-e5de-f111-84e8-002248872b66";

    private const string Yaml = """
        kind: workflow
        name: On order create
        entity: xrmv_order
        mode: realtime
        trigger:
          create: true
        steps:
          - create: { entity: task, set: { subject: Hi, regardingobjectid: { record: primary } } }

        """;

    private static string Result(string action = "created", string state = "draft", string assumed = "[]", string errors = "[]") =>
        "{\"id\":\"" + Id + "\",\"name\":\"On order create\",\"action\":\"" + action + "\",\"state\":\"" + state
        + "\",\"stateChanged\":false,\"assumedShapes\":" + assumed + ",\"errors\":" + errors + "}";

    private static string Summary(string state = "draft") =>
        $$"""{"id":"{{Id}}","name":"On order create","kind":"workflow","entity":"xrmv_order","mode":"realtime","state":"{{state}}","unsupportedSteps":0}""";

    private static HttpResponseMessage Envelope(HttpStatusCode status, string code, string message) =>
        FakeHandler.Json(status, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}");

    private static string WriteFile(TestCli cli, string text, string name = "wf.yaml")
    {
        Directory.CreateDirectory(cli.ConfigDir);
        var path = Path.Combine(cli.ConfigDir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Query(TestCli cli, int index = 0) => cli.Handler.Requests[index].RequestUri!.PathAndQuery;

    [Fact]
    public async Task Invalid_yaml_exits_2_with_path_line_column_and_sends_nothing()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, Result()));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        var file = WriteFile(cli, "kind: workflow\nname: [oops\n");
        cli.Handler.Requests.Clear();

        (await cli.RunAsync("workflow", "apply", file)).Should().Be(2);

        cli.Err.ToString().Should().MatchRegex(@"wf\.yaml:\d+:\d+: ");
        cli.Handler.Requests.Should().BeEmpty();
        cli.Out.ToString().Should().BeEmpty();

        (await cli.RunAsync("workflow", "apply", Path.Combine(cli.ConfigDir, "missing.yaml"))).Should().Be(2);
        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_sends_dryRun_false_explicitly()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, Result(state: "activated")));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        var file = WriteFile(cli, Yaml);
        cli.Handler.Requests.Clear();
        cli.Handler.Bodies.Clear();

        (await cli.RunAsync("workflow", "apply", file, "--solution", "My Sol", "--activate")).Should().Be(0, cli.Err.ToString());

        var request = cli.Handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/authoring/workflows/apply");
        request.RequestUri.Query.Should().Contain("dryRun=false").And.Contain("activate=true").And.Contain("solution=My%20Sol");
        request.Headers.Authorization.Should().NotBeNull();
        cli.Handler.Bodies.Single().Should().Be(Yaml);
        cli.Out.ToString().Should().Contain("On order create").And.Contain(Id).And.Contain("created").And.Contain("activated");

        (await cli.RunAsync("workflow", "apply", file)).Should().Be(0, cli.Err.ToString());
        Query(cli, 1).Should().Contain("dryRun=false").And.Contain("activate=false").And.NotContain("solution");
    }

    [Fact]
    public async Task Dry_run_prints_action_state_and_assumed_warnings()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, Result("updated", assumed: """["wait: a condition shape no export confirmed"]""")));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        var file = WriteFile(cli, Yaml);
        cli.Handler.Requests.Clear();

        (await cli.RunAsync("workflow", "apply", file, "--dry-run")).Should().Be(0, cli.Err.ToString());

        Query(cli).Should().Contain("dryRun=true");
        cli.Out.ToString().Should().Contain("Dry run").And.Contain("updated").And.Contain("draft");
        cli.Err.ToString().Should().Contain("warning").And.Contain("a condition shape no export confirmed");

        (await cli.RunAsync("workflow", "apply", file, "--dry-run", "-o", "json")).Should().Be(0, cli.Err.ToString());
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("action").GetString().Should().Be("updated");
    }

    [Fact]
    public async Task Rejected_apply_prints_every_error_and_exits_5()
    {
        var errors = """[{"at":{"line":8,"column":13},"message":"Unknown key 'subjet'."},{"at":null,"message":"The workflow has no steps."}]""";
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.BadRequest, Result(errors: errors)));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        var file = WriteFile(cli, Yaml);

        (await cli.RunAsync("workflow", "apply", file)).Should().Be(5);

        var err = cli.Err.ToString();
        err.Should().Contain("wf.yaml:8:13: Unknown key 'subjet'.").And.Contain("The workflow has no steps.").And.Contain("nothing was written");
        cli.Out.ToString().Should().NotContain("created");

        // A refusal that is an error envelope (e.g. several definitions share the name) is exit 5 with the server's text.
        using var other = new TestCli(_ => Envelope(HttpStatusCode.BadRequest, "0x80040203", "Several workflows are named X: 1, 2."));
        (await other.LoginDevAsync()).Should().Be(0, other.Err.ToString());
        (await other.RunAsync("workflow", "apply", WriteFile(other, Yaml))).Should().Be(5);
        other.Err.ToString().Should().Contain("Several workflows are named X");

        // Forbidden is exit 3.
        using var denied = new TestCli(_ => Envelope(HttpStatusCode.Forbidden, "0x80040220", "Missing prvReadWorkflow."));
        (await denied.LoginDevAsync()).Should().Be(0, denied.Err.ToString());
        (await denied.RunAsync("workflow", "apply", WriteFile(denied, Yaml))).Should().Be(3);

        using var expired = new TestCli(_ => Envelope(HttpStatusCode.Unauthorized, "0x80040220", "The token is not valid."));
        (await expired.LoginDevAsync()).Should().Be(0, expired.Err.ToString());
        (await expired.RunAsync("workflow", "apply", WriteFile(expired, Yaml))).Should().Be(3);
    }

    [Fact]
    public async Task Export_writes_yaml_to_stdout_or_file()
    {
        using var cli = new TestCli(r => r.RequestUri!.Query.Contains("format=xaml")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<Activity />", System.Text.Encoding.UTF8, "text/xml") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Yaml, System.Text.Encoding.UTF8, "text/yaml") });
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Clear();

        (await cli.RunAsync("workflow", "export", "On order create")).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Be(Yaml);
        var request = cli.Handler.Requests.Single().RequestUri!;
        request.AbsolutePath.Should().Be("/authoring/workflows/On%20order%20create");
        request.Query.Should().Contain("format=yaml");

        var target = Path.Combine(cli.ConfigDir, "out.yaml");
        (await cli.RunAsync("workflow", "export", Id, "--file", target)).Should().Be(0, cli.Err.ToString());
        File.ReadAllText(target).Should().Be(Yaml);
        File.ReadAllBytes(target).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no BOM");

        (await cli.RunAsync("workflow", "export", Id, "--format", "xaml")).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Be("<Activity />");
        cli.Handler.Requests.Last().RequestUri!.Query.Should().Contain("format=xaml");

        (await cli.RunAsync("workflow", "export", Id, "--format", "json")).Should().Be(2);
    }

    [Fact]
    public async Task Export_not_representable_exits_5_with_reasons()
    {
        using var cli = new TestCli(_ => Envelope(HttpStatusCode.BadRequest, "0x80040203",
            "The workflow is not representable as YAML: step 3 uses an activity xrmverse does not know. Export it as XAML instead."));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());

        (await cli.RunAsync("workflow", "export", Id)).Should().Be(5);

        cli.Err.ToString().Should().Contain("not representable").And.Contain("step 3");
        cli.Out.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task List_prints_table_and_json()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, "[" + Summary() + "]"));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Clear();

        (await cli.RunAsync("workflow", "list", "--kind", "workflow", "--entity", "xrmv_order")).Should().Be(0, cli.Err.ToString());

        var request = cli.Handler.Requests.Single().RequestUri!;
        request.AbsolutePath.Should().Be("/authoring/workflows");
        request.Query.Should().Contain("kind=workflow").And.Contain("entity=xrmv_order");
        var header = cli.Out.ToString().Split('\n')[0].Split("  ", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToArray();
        header.Should().Equal("id", "name", "kind", "entity", "mode", "state", "unsupportedSteps");
        cli.Out.ToString().Should().Contain("On order create").And.Contain("realtime");

        (await cli.RunAsync("workflow", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Last().RequestUri!.Query.Should().BeEmpty();
        JsonDocument.Parse(cli.Out.ToString()).RootElement[0].GetProperty("name").GetString().Should().Be("On order create");
    }

    [Fact]
    public async Task Unknown_name_exits_4()
    {
        using var cli = new TestCli(_ => Envelope(HttpStatusCode.NotFound, "0x80040217", "Workflow 'Nope' does not exist."));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());

        (await cli.RunAsync("workflow", "export", "Nope")).Should().Be(4);
        cli.Err.ToString().Should().Contain("does not exist");
        (await cli.RunAsync("workflow", "activate", "Nope")).Should().Be(4);
    }

    [Fact]
    public async Task Name_with_a_slash_is_refused_locally_with_exit_2()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, Summary()));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Clear();

        foreach (var verb in new[] { "export", "activate", "deactivate" })
        {
            (await cli.RunAsync("workflow", verb, "a/b")).Should().Be(2);
            cli.Err.ToString().Should().Contain("by its id");
        }
        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Activate_and_deactivate_call_their_routes()
    {
        using var cli = new TestCli(r => FakeHandler.Json(HttpStatusCode.OK, Summary(r.RequestUri!.AbsolutePath.EndsWith("/activate", StringComparison.Ordinal) ? "activated" : "draft")));
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Clear();

        (await cli.RunAsync("workflow", "activate", "On order create")).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        cli.Handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/authoring/workflows/On%20order%20create/activate");
        cli.Out.ToString().Should().Contain("activated");

        (await cli.RunAsync("workflow", "deactivate", Id, "-o", "json")).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests[1].RequestUri!.AbsolutePath.Should().Be($"/authoring/workflows/{Id}/deactivate");
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("state").GetString().Should().Be("draft");
    }
}
