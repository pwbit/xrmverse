using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

public class HierarchySecurityCommandsTests
{
    private const string State = """{"enabled":true,"depth":4,"excludedTables":["account","pb_project"]}""";

    private static async Task<TestCli> LoggedInAsync(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(respond ?? (_ => FakeHandler.Json(HttpStatusCode.OK, State)));
        return cli;
    }

    [Fact]
    public async Task Show_prints_a_sentence_in_table_and_the_object_in_json()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("hierarchy-security", "show")).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Trim().Should().Be("Hierarchy security is on, depth 4, excluded tables: account, pb_project.");
        cli.Handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/admin/hierarchy-security");

        (await cli.RunAsync("hierarchy-security", "show", "--output", "json")).Should().Be(0);
        var json = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        json.GetProperty("enabled").GetBoolean().Should().BeTrue();
        json.GetProperty("depth").GetInt32().Should().Be(4);
        json.GetProperty("excludedTables").EnumerateArray().Select(e => e.GetString()).Should().Equal("account", "pb_project");
    }

    [Theory]
    [InlineData("enable", """{"enabled":true}""")]
    [InlineData("disable", """{"enabled":false}""")]
    public async Task Enable_and_disable_put_the_switch(string verb, string expectedBody)
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("hierarchy-security", verb)).Should().Be(0, cli.Err.ToString());

        var put = cli.Handler.Requests.Select((r, i) => (r, i)).Single(x => x.r.Method == HttpMethod.Put);
        put.r.RequestUri!.AbsolutePath.Should().Be("/admin/hierarchy-security");
        cli.Handler.Bodies[put.i].Should().Be(expectedBody);
        cli.Out.ToString().Should().StartWith(verb == "enable" ? "Hierarchy security enabled." : "Hierarchy security disabled.");
    }

    [Fact]
    public async Task Depth_puts_the_number_and_json_prints_the_saved_settings()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("hierarchy-security", "depth", "6", "--output", "json")).Should().Be(0, cli.Err.ToString());

        var i = cli.Handler.Requests.FindIndex(r => r.Method == HttpMethod.Put);
        cli.Handler.Bodies[i].Should().Be("""{"depth":6}""");
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("depth").GetInt32().Should().Be(4); // the server's answer
    }

    [Fact]
    public async Task Exclude_adds_to_the_current_list_and_include_removes_from_it()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("hierarchy-security", "exclude", "contact")).Should().Be(0, cli.Err.ToString());
        var i = cli.Handler.Requests.FindIndex(r => r.Method == HttpMethod.Put);
        cli.Handler.Bodies[i].Should().Be("""{"excludedTables":["account","pb_project","contact"]}""");
        cli.Out.ToString().Should().StartWith("Table 'contact' excluded from hierarchy security.");

        (await cli.RunAsync("hierarchy-security", "include", "ACCOUNT")).Should().Be(0, cli.Err.ToString());
        var j = cli.Handler.Requests.FindLastIndex(r => r.Method == HttpMethod.Put);
        cli.Handler.Bodies[j].Should().Be("""{"excludedTables":["pb_project"]}""");
        cli.Out.ToString().Should().StartWith("Table 'ACCOUNT' included in hierarchy security.");
    }

    [Fact]
    public async Task Exclude_twice_does_not_duplicate_the_table()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("hierarchy-security", "exclude", "account")).Should().Be(0, cli.Err.ToString());

        var i = cli.Handler.Requests.FindIndex(r => r.Method == HttpMethod.Put);
        cli.Handler.Bodies[i].Should().Be("""{"excludedTables":["pb_project","account"]}""");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 3)]
    [InlineData(HttpStatusCode.BadRequest, 5)]
    public async Task Server_refusals_map_to_exit_codes_and_print_the_message(HttpStatusCode status, int exit)
    {
        using var cli = await LoggedInAsync(r => r.Method == HttpMethod.Get
            ? FakeHandler.Json(HttpStatusCode.OK, State)
            : FakeHandler.Json(status, """{"error":{"code":"0x80040220","message":"Refused by the server."}}"""));

        (await cli.RunAsync("hierarchy-security", "depth", "500")).Should().Be(exit);
        cli.Err.ToString().Trim().Should().Be("Refused by the server.");
    }

    [Fact]
    public async Task Depth_needs_a_number()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("hierarchy-security", "depth", "many")).Should().NotBe(0);
        cli.Handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }
}
