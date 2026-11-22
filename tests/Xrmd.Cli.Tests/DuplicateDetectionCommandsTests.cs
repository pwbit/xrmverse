using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary>
/// <c>xrmd duplicate-detection</c> (plan 2.15, task 6): each subcommand's Web API calls and output over a fake server; the server's
/// refusals keep the exit codes of the other Web API commands (403 is 3, 404 is 4, 400 is 5).
/// </summary>
public class DuplicateDetectionCommandsTests
{
    private const string OrgId = "00000000-0000-0000-0000-0000000000a1";
    private const string RuleId = "11111111-0000-0000-0000-000000000001";
    private const string JobId = "22222222-0000-0000-0000-000000000002";
    private const string RowId = "9a8b7c6d-1111-4222-8333-944455556666";
    private const string DupId = "9a8b7c6d-1111-4222-8333-944455557777";

    private static HttpResponseMessage Server(HttpRequestMessage request)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var method = request.Method.Method;
        if (method == "GET" && url.Contains("/organizations?"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"organizationid":"{{OrgId}}","isduplicatedetectionenabled":true,"isduplicatedetectionenabledforonlinecreateupdate":false,"isduplicatedetectionenabledforimport":false,"isduplicatedetectionenabledforofflinesync":false}]}""");
        if (method == "PATCH" && url.Contains($"/organizations({OrgId})"))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "GET" && url.Contains("/EntityDefinitions?"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[{"LogicalName":"contact","IsDuplicateDetectionEnabled":{"Value":true}},{"LogicalName":"account","IsDuplicateDetectionEnabled":{"Value":false}}]}""");
        if (method == "GET" && url.Contains("EntityDefinitions(LogicalName='contact')"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"@odata.context":"x","LogicalName":"contact","PrimaryIdAttribute":"contactid","PrimaryNameAttribute":"fullname","IsDuplicateDetectionEnabled":{"Value":false,"CanBeChanged":true}}""");
        if (method == "GET" && url.Contains("EntityDefinitions(LogicalName='account')"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"LogicalName":"account","PrimaryIdAttribute":"accountid","PrimaryNameAttribute":"name"}""");
        if (method == "PUT" && url.Contains("EntityDefinitions(LogicalName="))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "GET" && url.Contains("/duplicaterules?") && url.Contains("uniquename eq 'by_mail'"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"duplicateruleid":"{{RuleId}}"}]}""");
        if (method == "GET" && url.Contains("/duplicaterules?") && url.Contains("uniquename eq"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (method == "GET" && url.Contains("/duplicaterules?"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""
                {"value":[{"duplicateruleid":"{{RuleId}}","name":"Same email","uniquename":"by_mail","baseentityname":"contact","matchingentityname":"contact","statecode":1,"statuscode":2}]}
                """);
        if (method == "POST" && url.EndsWith("/duplicaterules", StringComparison.Ordinal))
        {
            var created = new HttpResponseMessage(HttpStatusCode.NoContent);
            created.Headers.Add("OData-EntityId", $"http://xrmd.test:5000/api/data/v9.2/duplicaterules({RuleId})");
            return created;
        }
        if (method == "POST" && url.EndsWith("/duplicateruleconditions", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "DELETE" && url.Contains("/duplicaterules("))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "POST" && url.Contains($"duplicaterules({RuleId})/Microsoft.Dynamics.CRM.PublishDuplicateRule"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"asyncoperationid":"{{JobId}}","operationtype":7}""");
        if (method == "POST" && url.EndsWith("/UnpublishDuplicateRule", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "POST" && url.EndsWith("/BulkDetectDuplicates", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"JobId":"{{JobId}}"}""");
        if (method == "GET" && url.Contains("/RetrieveDuplicates("))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"contactid":"{{DupId}}","fullname":"Anna Copy"}]}""");
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

    private static List<JsonElement> BodiesOf(TestCli cli, string method, string pathEnd) => cli.Handler.Requests
        .Select((r, i) => (r, i)).Where(x => x.r.Method.Method == method && Uri.UnescapeDataString(x.r.RequestUri!.AbsolutePath).EndsWith(pathEnd, StringComparison.Ordinal))
        .Select(x => JsonDocument.Parse(cli.Handler.Bodies[x.i]!).RootElement).ToList();

    [Fact]
    public async Task Status_shows_switches_and_rules()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "status")).Should().Be(0, cli.Err.ToString());

        var text = cli.Out.ToString();
        text.Should().Contain("Duplicate detection: on");
        text.Should().Contain("when a row is created or updated: off");
        text.Should().Contain("stored only");
        text.Should().Contain("Tables with duplicate detection: contact");
        text.Should().Contain("Same email").And.Contain("Published");
    }

    [Fact]
    public async Task Json_output()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "status", "-o", "json")).Should().Be(0, cli.Err.ToString());

        var root = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        root.GetProperty("switches").GetProperty("enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("switches").GetProperty("enabledForCreateUpdate").GetBoolean().Should().BeFalse();
        root.GetProperty("tables").EnumerateArray().Select(t => t.GetString()).Should().Equal("contact");
        var rule = root.GetProperty("rules")[0];
        rule.GetProperty("duplicateruleid").GetString().Should().Be(RuleId);
        rule.GetProperty("status").GetString().Should().Be("Published");

        cli.Out.GetStringBuilder().Clear();
        (await cli.RunAsync("duplicate-detection", "rule", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        JsonDocument.Parse(cli.Out.ToString()).RootElement[0].GetProperty("uniquename").GetString().Should().Be("by_mail");
    }

    [Fact]
    public async Task Enable_and_disable_patch_the_switches_and_put_the_table()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "disable")).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("duplicate-detection", "enable", "--create-update")).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("duplicate-detection", "enable", "--table", "contact", "-o", "json")).Should().Be(0, cli.Err.ToString());

        var patches = BodiesOf(cli, "PATCH", $"organizations({OrgId})");
        patches[0].GetProperty("isduplicatedetectionenabled").GetBoolean().Should().BeFalse();
        patches[1].GetProperty("isduplicatedetectionenabledforonlinecreateupdate").GetBoolean().Should().BeTrue();
        var put = BodiesOf(cli, "PUT", "EntityDefinitions(LogicalName='contact')").Single();
        put.GetProperty("IsDuplicateDetectionEnabled").GetProperty("Value").GetBoolean().Should().BeTrue();
        put.GetProperty("LogicalName").GetString().Should().Be("contact");
        put.TryGetProperty("@odata.context", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Rule_create_parses_conditions()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "rule", "create", "--base", "contact", "--matching", "contact", "--name", "Same email",
            "--condition", "emailaddress1:exact:ignore-blank",
            "--condition", "lastname:surname:first:3",
            "--condition", "birthdate:date",
            "--condition", "a:b:last:2",
            "--condition", "gendercode:value",
            "--case-sensitive", "--exclude-inactive", "-o", "json")).Should().Be(0, cli.Err.ToString());

        var rule = BodiesOf(cli, "POST", "/duplicaterules").Single();
        rule.GetProperty("name").GetString().Should().Be("Same email");
        rule.GetProperty("baseentityname").GetString().Should().Be("contact");
        rule.GetProperty("matchingentityname").GetString().Should().Be("contact");
        rule.GetProperty("iscasesensitive").GetBoolean().Should().BeTrue();
        rule.GetProperty("excludeinactiverecords").GetBoolean().Should().BeTrue();
        var c = BodiesOf(cli, "POST", "/duplicateruleconditions");
        c.Should().HaveCount(5);
        c[0].GetProperty("baseattributename").GetString().Should().Be("emailaddress1");
        c[0].GetProperty("matchingattributename").GetString().Should().Be("emailaddress1");
        c[0].GetProperty("operatorcode").GetInt32().Should().Be(0);
        c[0].GetProperty("ignoreblankvalues").GetBoolean().Should().BeTrue();
        c[0].GetProperty("regardingobjectid@odata.bind").GetString().Should().Be($"duplicaterules({RuleId})");
        c[1].GetProperty("baseattributename").GetString().Should().Be("lastname");
        c[1].GetProperty("matchingattributename").GetString().Should().Be("surname");
        c[1].GetProperty("operatorcode").GetInt32().Should().Be(1);
        c[1].GetProperty("operatorparam").GetInt32().Should().Be(3);
        c[1].GetProperty("ignoreblankvalues").GetBoolean().Should().BeFalse();
        c[2].GetProperty("operatorcode").GetInt32().Should().Be(3);
        c[2].TryGetProperty("operatorparam", out _).Should().BeFalse();
        c[3].GetProperty("operatorcode").GetInt32().Should().Be(2);
        c[3].GetProperty("operatorparam").GetInt32().Should().Be(2);
        c[4].GetProperty("operatorcode").GetInt32().Should().Be(6);
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("duplicateruleid").GetString().Should().Be(RuleId);
    }

    [Theory]
    [InlineData("emailaddress1")]
    [InlineData("emailaddress1:fuzzy")]
    [InlineData("emailaddress1:first")]
    [InlineData("emailaddress1:first:0")]
    [InlineData("emailaddress1:first:x")]
    [InlineData("a:b:c:exact")]
    [InlineData("emailaddress1:label")]
    [InlineData(":exact")]
    [InlineData("first:5:ignore-blank")]
    public async Task Bad_condition_syntax_exits_2(string condition)
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "rule", "create", "--base", "contact", "--matching", "contact", "--name", "X", "--condition", condition))
            .Should().Be(2);

        cli.Err.ToString().Should().Contain(condition);
        Calls(cli).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_condition_takes_the_rule_back()
    {
        using var cli = await LoggedInAsync(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/duplicateruleconditions", StringComparison.Ordinal)
            ? FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"0x80040203","message":"Column 'nope' does not exist."}}""")
            : Server(r));

        (await cli.RunAsync("duplicate-detection", "rule", "create", "--base", "contact", "--matching", "contact", "--name", "X", "--condition", "nope:exact"))
            .Should().Be(5);

        cli.Err.ToString().Trim().Should().Be("Column 'nope' does not exist.");
        Calls(cli).Should().Contain($"DELETE duplicaterules({RuleId})");
    }

    [Fact]
    public async Task Publish_wait_polls_job()
    {
        var polls = 0;
        using var cli = await LoggedInAsync(r =>
        {
            if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains($"asyncoperations({JobId})"))
                return FakeHandler.Json(HttpStatusCode.OK, ++polls < 3 ? """{"statecode":2,"statuscode":20,"message":null}""" : """{"statecode":3,"statuscode":30,"message":null}""");
            return Server(r);
        });
        cli.Clock = new SteppingClock();

        (await cli.RunAsync("duplicate-detection", "rule", "publish", "by_mail", "--wait", "-o", "json")).Should().Be(0, cli.Err.ToString());

        polls.Should().Be(3);
        Calls(cli).Should().Contain($"POST duplicaterules({RuleId})/Microsoft.Dynamics.CRM.PublishDuplicateRule");
        Calls(cli).Should().Contain(c => c.StartsWith("GET duplicaterules?") && c.Contains("uniquename eq 'by_mail'"));
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("status").GetString().Should().Be("published");
    }

    [Fact]
    public async Task A_failed_publish_job_exits_5_with_its_message()
    {
        using var cli = await LoggedInAsync(r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains($"asyncoperations({JobId})")
                ? FakeHandler.Json(HttpStatusCode.OK, """{"statecode":3,"statuscode":31,"message":"Publishing failed: out of space."}""")
                : Server(r));
        cli.Clock = new SteppingClock();

        (await cli.RunAsync("duplicate-detection", "rule", "publish", RuleId, "--wait")).Should().Be(5);

        cli.Err.ToString().Should().Contain("out of space");
    }

    [Fact]
    public async Task A_job_that_outlasts_the_timeout_exits_6_and_a_bad_timeout_exits_2()
    {
        using var cli = await LoggedInAsync(r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains($"asyncoperations({JobId})")
                ? FakeHandler.Json(HttpStatusCode.OK, """{"statecode":2,"statuscode":20,"message":null}""")
                : Server(r));
        cli.Clock = new SteppingClock();

        (await cli.RunAsync("duplicate-detection", "rule", "publish", RuleId, "--wait", "--timeout", "1")).Should().Be(6);
        cli.Err.ToString().Should().Contain(JobId);
        (await cli.RunAsync("duplicate-detection", "bulk", "--table", "contact", "--wait", "--timeout", "0")).Should().Be(2);
    }

    [Fact]
    public async Task Publish_without_wait_names_the_job()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "rule", "publish", RuleId)).Should().Be(0, cli.Err.ToString());

        cli.Out.ToString().Should().Contain(JobId);
        Calls(cli).Should().NotContain(c => c.Contains("asyncoperations("));
    }

    [Fact]
    public async Task Unpublish_posts_the_rule_id()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "rule", "unpublish", "by_mail")).Should().Be(0, cli.Err.ToString());

        BodiesOf(cli, "POST", "/UnpublishDuplicateRule").Single().GetProperty("DuplicateRuleId").GetString().Should().Be(RuleId);
    }

    [Fact]
    public async Task Delete_needs_confirmation_without_a_terminal_and_deletes_with_yes()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "rule", "delete", RuleId)).Should().Be(2);
        Calls(cli).Should().NotContain(c => c.StartsWith("DELETE"));

        (await cli.RunAsync("duplicate-detection", "rule", "delete", RuleId, "--yes")).Should().Be(0, cli.Err.ToString());
        Calls(cli).Should().Contain($"DELETE duplicaterules({RuleId})");
    }

    [Fact]
    public async Task Find_prints_duplicates()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "find", "--table", "contact", "--id", RowId, "-o", "json")).Should().Be(0, cli.Err.ToString());

        var call = Calls(cli).Single(c => c.StartsWith("GET RetrieveDuplicates("));
        call.Should().Contain("@p1={\"@odata.type\":\"Microsoft.Dynamics.CRM.contact\",\"contactid\":\"" + RowId + "\"}");
        call.Should().Contain("@p2='contact'").And.Contain("\"PageNumber\":1");
        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("id").GetString().Should().Be(DupId);
        rows[0].GetProperty("name").GetString().Should().Be("Anna Copy");
    }

    [Fact]
    public async Task Find_follows_more_records()
    {
        var pages = 0;
        using var cli = await LoggedInAsync(r =>
        {
            if (r.Method != HttpMethod.Get || !r.RequestUri!.AbsolutePath.Contains("RetrieveDuplicates(")) return Server(r);
            return FakeHandler.Json(HttpStatusCode.OK, ++pages == 1
                ? $$"""{"value":[{"contactid":"{{DupId}}","fullname":"One"}],"@Microsoft.Dynamics.CRM.morerecords":true}"""
                : $$"""{"value":[{"contactid":"{{RowId}}","fullname":"Two"}]}""");
        });

        (await cli.RunAsync("duplicate-detection", "find", "--table", "contact", "--id", RowId, "--matching", "contact", "-o", "json")).Should().Be(0, cli.Err.ToString());

        pages.Should().Be(2);
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Bulk_starts_the_job_and_waits()
    {
        using var cli = await LoggedInAsync(r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains($"asyncoperations({JobId})")
                ? FakeHandler.Json(HttpStatusCode.OK, """{"statecode":3,"statuscode":30,"message":"Stopped at 5000 rows."}""")
                : Server(r));
        cli.Clock = new SteppingClock();

        (await cli.RunAsync("duplicate-detection", "bulk", "--table", "contact", "--name", "Nightly", "--wait")).Should().Be(0, cli.Err.ToString());

        var body = BodiesOf(cli, "POST", "/BulkDetectDuplicates").Single();
        body.GetProperty("Query").GetProperty("EntityName").GetString().Should().Be("contact");
        body.GetProperty("JobName").GetString().Should().Be("Nightly");
        body.GetProperty("SendEmailNotification").GetBoolean().Should().BeFalse();
        cli.Out.ToString().Should().Contain("Stopped at 5000 rows.");
    }

    [Fact]
    public async Task Unknown_rule_exits_4()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "rule", "publish", "nobody")).Should().Be(4);
        (await cli.RunAsync("duplicate-detection", "rule", "delete", "nobody", "--yes")).Should().Be(4);
        cli.Err.ToString().Should().Contain("nobody");
        Calls(cli).Should().NotContain(c => c.StartsWith("POST") || c.StartsWith("DELETE"));
    }

    [Fact]
    public async Task Unknown_table_exits_4()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("duplicate-detection", "enable", "--table", "nothing")).Should().Be(4);
        (await cli.RunAsync("duplicate-detection", "rule", "create", "--base", "nothing", "--matching", "nothing", "--name", "X", "--condition", "a:exact")).Should().Be(4);
        (await cli.RunAsync("duplicate-detection", "find", "--table", "nothing", "--id", RowId)).Should().Be(4);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 3)]
    [InlineData(HttpStatusCode.BadRequest, 5)]
    public async Task Server_refusals_map_to_exit_codes_and_print_the_message(HttpStatusCode status, int exit)
    {
        using var cli = await LoggedInAsync(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith(".PublishDuplicateRule", StringComparison.Ordinal)
            ? FakeHandler.Json(status, """{"error":{"code":"0x80040203","message":"The rule is already being published."}}""")
            : Server(r));

        (await cli.RunAsync("duplicate-detection", "rule", "publish", RuleId)).Should().Be(exit);
        cli.Err.ToString().Trim().Should().Be("The rule is already being published.");
    }

    /// <summary>Every timer fires at once and moves the clock forward by its due time.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) Interlocked.Add(ref _ticks, dueTime.Ticks);
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
}
