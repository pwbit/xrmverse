using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary>
/// <c>xrmd fieldsecurity</c> (plan 14, task 7): each subcommand's Web API calls and output, over a fake server; a 403 of the
/// server is exit 3.
/// </summary>
public class FieldSecurityCommandsTests
{
    private const string ProfileId = "2d3c1b4a-5e6f-4a1b-9c2d-3e4f5a6b7c80";
    private const string PermissionId = "9a8b7c6d-1111-4222-8333-944455556666";
    private const string UserId = "5b1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a2";
    private const string TeamId = "7c1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a3";

    /// <summary>A fake server: the profile <c>Finance</c>, one permission on <c>account.creditlimit</c>, a user and a team.</summary>
    private static HttpResponseMessage Server(HttpRequestMessage request)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var method = request.Method.Method;
        if (method == "GET" && url.Contains("/fieldsecurityprofiles?") && url.Contains("name eq 'Finance'"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"fieldsecurityprofileid":"{{ProfileId}}","name":"Finance"}]}""");
        if (method == "GET" && url.Contains("/fieldsecurityprofiles?") && url.Contains("name eq"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (method == "GET" && url.Contains("/fieldsecurityprofiles?"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""
                {"value":[{"fieldsecurityprofileid":"572329c1-a042-4e22-be47-367c6374ea45","name":"System Administrator","description":"Full access"},
                          {"fieldsecurityprofileid":"{{ProfileId}}","name":"Finance","description":null}]}
                """);
        if (method == "POST" && url.EndsWith("/fieldsecurityprofiles", StringComparison.Ordinal))
            return Created($"fieldsecurityprofiles({ProfileId})");
        if (method == "GET" && url.Contains("/fieldpermissions?") && url.Contains("attributelogicalname eq 'creditlimit'"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"fieldpermissionid":"{{PermissionId}}"}]}""");
        if (method == "GET" && url.Contains("/fieldpermissions?"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (method == "POST" && url.EndsWith("/fieldpermissions", StringComparison.Ordinal))
            return Created($"fieldpermissions({PermissionId})");
        if (method == "GET" && url.Contains("/systemusers?"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"systemuserid":"{{UserId}}"}]}""");
        if (method == "GET" && url.Contains("/teams?"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"teamid":"{{TeamId}}"}]}""");
        if (method == "GET" && url.Contains("/Attributes(LogicalName='creditlimit')"))
            return FakeHandler.Json(HttpStatusCode.OK, """
                {"@odata.type":"#Microsoft.Dynamics.CRM.MoneyAttributeMetadata","LogicalName":"creditlimit","IsSecured":false,"CanBeSecuredForRead":true}
                """);
        if (method is "PATCH" or "PUT" or "DELETE" || (method == "POST" && url.EndsWith("/$ref", StringComparison.Ordinal)))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Created(string path)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        response.Headers.Add("OData-EntityId", "http://xrmd.test:5000/api/data/v9.2/" + path);
        return response;
    }

    private static async Task<TestCli> LoggedInAsync(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var cli = new TestCli(respond ?? Server);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Clear();
        cli.Handler.Bodies.Clear();
        return cli;
    }

    /// <summary>The method and the unescaped path and query of every request, in order.</summary>
    private static List<string> Calls(TestCli cli) => cli.Handler.Requests
        .Select(r => $"{r.Method.Method} {Uri.UnescapeDataString(r.RequestUri!.PathAndQuery).Replace("/api/data/v9.2/", "")}").ToList();

    private static JsonElement Body(TestCli cli, string method) =>
        JsonDocument.Parse(cli.Handler.Bodies[cli.Handler.Requests.FindIndex(r => r.Method.Method == method)]!).RootElement;

    [Fact]
    public async Task Profiles_lists_the_profiles()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "profiles", "-o", "json")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().ContainSingle().Which.Should().StartWith("GET fieldsecurityprofiles?$select=fieldsecurityprofileid,name,description");
        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        rows.GetArrayLength().Should().Be(2);
        rows[1].GetProperty("name").GetString().Should().Be("Finance");
        rows[0].GetProperty("readOnly").GetBoolean().Should().BeTrue("the System Administrator profile cannot be changed");
    }

    [Fact]
    public async Task Profile_create_posts_the_name_and_description()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "profile", "create", "Auditors", "--description", "Read the budget", "-o", "json"))
            .Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Equal("POST fieldsecurityprofiles");
        var body = Body(cli, "POST");
        body.GetProperty("name").GetString().Should().Be("Auditors");
        body.GetProperty("description").GetString().Should().Be("Read the budget");
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("fieldsecurityprofileid").GetString().Should().Be(ProfileId);
    }

    [Fact]
    public async Task Profile_delete_finds_the_profile_by_name_and_deletes_it()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "profile", "delete", "Finance", "--yes")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().HaveCount(2);
        Calls(cli)[0].Should().Contain("fieldsecurityprofiles?").And.Contain("name eq 'Finance'");
        Calls(cli)[1].Should().Be($"DELETE fieldsecurityprofiles({ProfileId})");
        cli.Out.ToString().Should().Contain("Finance");
    }

    [Fact]
    public async Task An_unknown_profile_is_exit_4()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "profile", "delete", "Nobody", "--yes")).Should().Be(4);
        cli.Err.ToString().Should().Contain("Nobody");
    }

    [Fact]
    public async Task Permission_set_creates_a_missing_permission()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "permission", "set", "Finance", "account", "revenue", "--read", "--update"))
            .Should().Be(0, cli.Err.ToString());

        Calls(cli).Last().Should().Be("POST fieldpermissions");
        Calls(cli).Should().Contain(c => c.StartsWith("GET fieldpermissions?") && c.Contains($"_fieldsecurityprofileid_value eq {ProfileId}")
                                         && c.Contains("entityname eq 'account'") && c.Contains("attributelogicalname eq 'revenue'"));
        var body = Body(cli, "POST");
        body.GetProperty("fieldsecurityprofileid@odata.bind").GetString().Should().Be($"fieldsecurityprofiles({ProfileId})");
        body.GetProperty("entityname").GetString().Should().Be("account");
        body.GetProperty("attributelogicalname").GetString().Should().Be("revenue");
        body.GetProperty("canread").GetInt32().Should().Be(4);
        body.GetProperty("cancreate").GetInt32().Should().Be(0);
        body.GetProperty("canupdate").GetInt32().Should().Be(4);
        cli.Out.ToString().Should().Contain("account.revenue");
    }

    [Fact]
    public async Task Permission_set_updates_an_existing_permission()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "permission", "set", "Finance", "account", "creditlimit", "--create"))
            .Should().Be(0, cli.Err.ToString());

        Calls(cli).Last().Should().Be($"PATCH fieldpermissions({PermissionId})");
        var body = Body(cli, "PATCH");
        body.GetProperty("canread").GetInt32().Should().Be(0);
        body.GetProperty("cancreate").GetInt32().Should().Be(4);
        body.GetProperty("canupdate").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Permission_remove_deletes_the_permission()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "permission", "remove", "Finance", "account", "creditlimit")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Last().Should().Be($"DELETE fieldpermissions({PermissionId})");
    }

    [Fact]
    public async Task Permission_remove_of_a_missing_permission_is_exit_4()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "permission", "remove", "Finance", "account", "revenue")).Should().Be(4);
        Calls(cli).Should().NotContain(c => c.StartsWith("DELETE"));
    }

    [Fact]
    public async Task Assign_to_a_user_associates_the_profile()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "assign", "Finance", "--user", "jane@contoso.test")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Contain(c => c.StartsWith("GET systemusers?") && c.Contains("domainname eq 'jane@contoso.test'"));
        Calls(cli).Last().Should().Be($"POST systemusers({UserId})/systemuserprofiles_association/$ref");
        Body(cli, "POST").GetProperty("@odata.id").GetString().Should().EndWith($"/api/data/v9.2/fieldsecurityprofiles({ProfileId})");
    }

    [Fact]
    public async Task Assign_a_user_by_id_matches_the_systemuserid_or_the_object_id()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "assign", "Finance", "--user", UserId)).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Contain(c => c.StartsWith("GET systemusers?") && c.Contains($"systemuserid eq {UserId}")
                                         && c.Contains($"azureactivedirectoryobjectid eq {UserId}"));
    }

    [Fact]
    public async Task Assign_to_a_team_associates_the_profile()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "assign", "Finance", "--team", "Sales")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Contain(c => c.StartsWith("GET teams?") && c.Contains("name eq 'Sales'"));
        Calls(cli).Last().Should().Be($"POST teams({TeamId})/teamprofiles_association/$ref");
    }

    [Fact]
    public async Task Assign_needs_exactly_one_of_user_and_team()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "assign", "Finance")).Should().Be(2);
        (await cli.RunAsync("fieldsecurity", "assign", "Finance", "--user", "a", "--team", "b")).Should().Be(2);
        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Unassign_removes_the_association()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", "unassign", "Finance", "--user", "jane@contoso.test")).Should().Be(0, cli.Err.ToString());
        Calls(cli).Last().Should().Be($"DELETE systemusers({UserId})/systemuserprofiles_association({ProfileId})/$ref");

        (await cli.RunAsync("fieldsecurity", "unassign", "Finance", "--team", "Sales")).Should().Be(0, cli.Err.ToString());
        Calls(cli).Last().Should().Be($"DELETE teams({TeamId})/teamprofiles_association({ProfileId})/$ref");
    }

    [Theory]
    [InlineData("secure", true)]
    [InlineData("unsecure", false)]
    public async Task Secure_and_unsecure_put_the_attribute_with_IsSecured(string verb, bool secured)
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("fieldsecurity", verb, "account", "creditlimit")).Should().Be(0, cli.Err.ToString());

        Calls(cli).Should().Equal(
            "GET EntityDefinitions(LogicalName='account')/Attributes(LogicalName='creditlimit')",
            "PUT EntityDefinitions(LogicalName='account')/Attributes(LogicalName='creditlimit')");
        var body = Body(cli, "PUT");
        body.GetProperty("IsSecured").GetBoolean().Should().Be(secured);
        body.GetProperty("LogicalName").GetString().Should().Be("creditlimit");
        body.TryGetProperty("CanBeSecuredForRead", out _).Should().BeFalse("server-owned properties are not sent back");
        cli.Out.ToString().Should().Contain("account.creditlimit");
    }

    [Fact]
    public async Task A_user_without_privileges_gets_exit_3()
    {
        using var cli = await LoggedInAsync(request => request.Method == HttpMethod.Get && !request.RequestUri!.AbsolutePath.EndsWith("/fieldpermissions", StringComparison.Ordinal)
            ? Server(request)
            : FakeHandler.Json(HttpStatusCode.Forbidden,
                """{"error":{"code":"0x80040220","message":"Principal user is missing prvCreateFieldSecurityProfile privilege."}}"""));

        (await cli.RunAsync("fieldsecurity", "profile", "create", "Auditors")).Should().Be(3);
        cli.Err.ToString().Should().Contain("prvCreateFieldSecurityProfile");
    }
}
