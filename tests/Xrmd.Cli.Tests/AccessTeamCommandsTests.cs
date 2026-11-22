using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary>
/// <c>xrmd access-team</c> (plan 2.16, task 5): each subcommand's Web API calls and output over a fake server; the server's
/// refusals keep the exit codes of the other Web API commands (403 is 3, 400 is 5).
/// </summary>
public class AccessTeamCommandsTests
{
    private const string SalesId = "11111111-0000-0000-0000-000000000001";
    private const string OtherId = "11111111-0000-0000-0000-000000000002";
    private const string UserId = "5b1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a2";
    private const string RecordId = "9a8b7c6d-1111-4222-8333-944455556666";
    private const string TeamId = "7c1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a3";

    /// <summary>A fake server: <c>account</c> (code 1, access teams on), <c>contact</c> (code 2, off), two templates on account.</summary>
    private static HttpResponseMessage Server(HttpRequestMessage request)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var method = request.Method.Method;
        if (method == "GET" && url.Contains("EntityDefinitions(LogicalName='account')"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"LogicalName":"account","ObjectTypeCode":1,"PrimaryIdAttribute":"accountid","AutoCreateAccessTeams":true,"IsAuditEnabled":{"Value":false}}""");
        if (method == "GET" && url.Contains("EntityDefinitions(LogicalName='contact')"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"LogicalName":"contact","ObjectTypeCode":2,"PrimaryIdAttribute":"contactid","AutoCreateAccessTeams":false}""");
        if (method == "PUT" && url.Contains("EntityDefinitions(LogicalName="))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "GET" && url.Contains("/EntityDefinitions?"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[{"LogicalName":"account","ObjectTypeCode":1},{"LogicalName":"contact","ObjectTypeCode":2}]}""");
        if (method == "GET" && url.Contains("/teamtemplates?") && url.Contains("teamtemplatename eq 'Dup'"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"teamtemplateid":"{{SalesId}}"},{"teamtemplateid":"{{OtherId}}"}]}""");
        if (method == "GET" && url.Contains("/teamtemplates?") && url.Contains("teamtemplatename eq 'Sales team'"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"teamtemplateid":"{{SalesId}}"}]}""");
        if (method == "GET" && url.Contains("/teamtemplates?") && url.Contains("teamtemplatename eq"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (method == "GET" && url.Contains("/teamtemplates?"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""
                {"value":[{"teamtemplateid":"{{SalesId}}","teamtemplatename":"Sales team","objecttypecode":1,"defaultaccessrightsmask":3,"description":null},
                          {"teamtemplateid":"{{OtherId}}","teamtemplatename":"Reviewers","objecttypecode":1,"defaultaccessrightsmask":65536,"description":"Delete only"}]}
                """);
        if (method == "GET" && url.Contains($"/teamtemplates({SalesId})"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"teamtemplateid":"{{SalesId}}","teamtemplatename":"Sales team","objecttypecode":1,"defaultaccessrightsmask":3}""");
        if (method == "POST" && url.EndsWith("/teamtemplates", StringComparison.Ordinal))
        {
            var created = new HttpResponseMessage(HttpStatusCode.NoContent);
            created.Headers.Add("OData-EntityId", $"http://xrmd.test:5000/api/data/v9.2/teamtemplates({SalesId})");
            return created;
        }
        if (method == "DELETE" && url.Contains("/teamtemplates("))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "GET" && url.Contains("/systemusers?"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"systemuserid":"{{UserId}}"}]}""");
        if (method == "POST" && url.Contains("/Microsoft.Dynamics.CRM.AddUserToRecordTeam"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"AccessTeamId":"{{TeamId}}"}""");
        if (method == "POST" && url.Contains("/Microsoft.Dynamics.CRM.RemoveUserFromRecordTeam"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"AccessTeamId":"{{TeamId}}"}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static async Task<TestCli> LoggedInAsync(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var cli = new TestCli(respond ?? Server);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Clear();
        cli.Handler.Bodies.Clear();
        return cli;
    }

    private static List<string> Calls(TestCli cli) => cli.Handler.Requests
        .Select(r => $"{r.Method.Method} {Uri.UnescapeDataString(r.RequestUri!.PathAndQuery).Replace("/api/data/v9.2/", "")}").ToList();

    private static JsonElement Body(TestCli cli, string method) =>
        JsonDocument.Parse(cli.Handler.Bodies[cli.Handler.Requests.FindIndex(r => r.Method.Method == method)]!).RootElement;

    [Fact]
    public async Task Template_list_names_the_table_and_the_rights_of_each_template()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "template", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());

        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("teamtemplateid").GetString().Should().Be(SalesId);
        rows[0].GetProperty("table").GetString().Should().Be("account");
        rows[0].GetProperty("name").GetString().Should().Be("Sales team");
        rows[0].GetProperty("rights").GetString().Should().Be("read,write");
        rows[0].GetProperty("mask").GetInt32().Should().Be(3);
        rows[1].GetProperty("rights").GetString().Should().Be("delete");
    }

    [Fact]
    public async Task Template_list_with_a_table_filters_on_its_object_type_code()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "template", "list", "--table", "contact")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Contain(c => c.StartsWith("GET teamtemplates?") && c.Contains("objecttypecode eq 2"));
    }

    [Fact]
    public async Task Template_create_posts_the_code_of_the_table_and_the_mask_of_the_rights()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "template", "create", "--table", "account", "--name", "Sales team",
            "--rights", "read,write,AppendTo,share", "-o", "json")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Last().Should().Be("POST teamtemplates");
        var body = Body(cli, "POST");
        body.GetProperty("teamtemplatename").GetString().Should().Be("Sales team");
        body.GetProperty("objecttypecode").GetInt32().Should().Be(1);
        body.GetProperty("defaultaccessrightsmask").GetInt32().Should().Be(1 + 2 + 16 + 262144);
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("teamtemplateid").GetString().Should().Be(SalesId);
    }

    [Fact]
    public async Task Template_create_refuses_an_unknown_right_before_calling_the_server()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "template", "create", "--table", "account", "--name", "X", "--rights", "read,fly"))
            .Should().Be(2);

        cli.Err.ToString().Should().Contain("fly");
        Calls(cli).Should().BeEmpty();
    }

    [Fact]
    public async Task Template_delete_deletes_by_id()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "template", "delete", SalesId, "--yes")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Equal($"DELETE teamtemplates({SalesId})");
    }

    [Theory]
    [InlineData("enable", true)]
    [InlineData("disable", false)]
    public async Task Enable_and_disable_put_the_whole_definition_with_the_flag_changed(string verb, bool value)
    {
        using var cli = await LoggedInAsync();
        var table = value ? "contact" : "account";

        (await cli.RunAsync("access-team", verb, table, "-o", "json")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Equal($"GET EntityDefinitions(LogicalName='{table}')", $"PUT EntityDefinitions(LogicalName='{table}')");
        var body = Body(cli, "PUT");
        body.GetProperty("AutoCreateAccessTeams").GetBoolean().Should().Be(value);
        body.GetProperty("LogicalName").GetString().Should().Be(table);
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("autoCreateAccessTeams").GetBoolean().Should().Be(value);
    }

    [Fact]
    public async Task Add_resolves_the_user_by_domain_name_and_the_template_by_name_and_posts_the_action()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "add", "--table", "account", "--record", RecordId,
            "--user", "jane@contoso.test", "--template", "Sales team", "-o", "json")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Contain(c => c.StartsWith("GET systemusers?") && c.Contains("domainname eq 'jane@contoso.test'"));
        Calls(cli).Should().Contain(c => c.StartsWith("GET teamtemplates?") && c.Contains("teamtemplatename eq 'Sales team'") && c.Contains("objecttypecode eq 1"));
        Calls(cli).Last().Should().Be($"POST systemusers({UserId})/Microsoft.Dynamics.CRM.AddUserToRecordTeam");
        var body = Body(cli, "POST");
        body.GetProperty("Record").GetProperty("@odata.type").GetString().Should().Be("Microsoft.Dynamics.CRM.account");
        body.GetProperty("Record").GetProperty("accountid").GetString().Should().Be(RecordId);
        body.GetProperty("TeamTemplate").GetProperty("teamtemplateid").GetString().Should().Be(SalesId);
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("accessTeamId").GetString().Should().Be(TeamId);
    }

    [Fact]
    public async Task Remove_takes_ids_as_they_are_and_posts_the_remove_action()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "remove", "--table", "account", "--record", RecordId, "--user", UserId, "--template", SalesId))
            .Should().Be(0, cli.Err.ToString());

        Calls(cli).Last().Should().Be($"POST systemusers({UserId})/Microsoft.Dynamics.CRM.RemoveUserFromRecordTeam");
        Body(cli, "POST").GetProperty("TeamTemplate").GetProperty("teamtemplateid").GetString().Should().Be(SalesId);
        cli.Out.ToString().Should().Contain(TeamId);
    }

    [Fact]
    public async Task A_template_name_that_is_not_unique_for_the_table_is_an_error()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "add", "--table", "account", "--record", RecordId, "--user", UserId, "--template", "Dup")).Should().Be(5);

        cli.Err.ToString().Should().Contain("Dup");
        Calls(cli).Should().NotContain(c => c.StartsWith("POST"));
    }

    [Fact]
    public async Task An_unknown_template_name_is_exit_4()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("access-team", "add", "--table", "account", "--record", RecordId, "--user", UserId, "--template", "Nobody")).Should().Be(4);
        Calls(cli).Should().NotContain(c => c.StartsWith("POST"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 3)]
    [InlineData(HttpStatusCode.BadRequest, 5)]
    public async Task Server_refusals_map_to_exit_codes_and_print_the_message(HttpStatusCode status, int exit)
    {
        using var cli = await LoggedInAsync(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/teamtemplates", StringComparison.Ordinal)
            ? FakeHandler.Json(status, """{"error":{"code":"0x80040203","message":"A table can have at most 4 access team templates."}}""")
            : Server(r));

        (await cli.RunAsync("access-team", "template", "create", "--table", "account", "--name", "Fifth", "--rights", "read")).Should().Be(exit);
        cli.Err.ToString().Trim().Should().Be("A table can have at most 4 access team templates.");
    }
}
