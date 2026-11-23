using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd mailbox</c> (plan 4.1b, task 7) against a fake server.</summary>
public sealed class MailboxCommandsTests
{
    private const string MailboxId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string UserId = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string ProfileId = "cccccccc-0000-0000-0000-000000000001";
    private const string Secret = "s3cret-relay-password";

    private static HttpResponseMessage Server(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        if (request.Method == HttpMethod.Post && path.EndsWith("/emailserverprofiles"))
        {
            var created = new HttpResponseMessage(HttpStatusCode.NoContent);
            created.Headers.Add("OData-EntityId", $"https://xrmd.test/api/data/v9.2/emailserverprofiles({ProfileId})");
            return created;
        }
        if (request.Method == HttpMethod.Get && path.EndsWith("/emailserverprofiles"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""
                {"value":[{"emailserverprofileid":"{{ProfileId}}","name":"Relay","servertype":4,"outgoingserverlocation":"smtp.example.com",
                  "outgoingportnumber":587,"outgoingusessl":false,"outgoingcredentialretrieval":1,"isoutgoingpasswordset":true,
                  "isoauthclientsecretset":false,"statecode":0}]}
                """);
        if (request.Method == HttpMethod.Get && path.EndsWith("/mailboxes"))
            return FakeHandler.Json(HttpStatusCode.OK, query.Contains("nobody")
                ? """{"value":[]}"""
                : $$"""
                  {"value":[{"mailboxid":"{{MailboxId}}","name":"Ada","emailaddress":"ada@example.com","_emailserverprofile_value":"{{ProfileId}}",
                    "outgoingemaildeliverymethod":2,"enabledforoutgoingemail":true,"outgoingemailstatus":0,"emailrouteraccessapproval":2,
                    "ispasswordset":false,"statecode":0}]}
                  """);
        if (request.Method == HttpMethod.Get && path.EndsWith("/systemusers"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"systemuserid":"{{UserId}}"}]}""");
        if (request.Method == HttpMethod.Get && path.EndsWith("/queues"))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static async Task<TestCli> LoggedInAsync(Func<HttpRequestMessage, HttpResponseMessage>? server = null)
    {
        var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(server ?? Server);
        return cli;
    }

    private static (HttpRequestMessage Request, string? Body) Last(TestCli cli, HttpMethod method) =>
        cli.Handler.Requests.Select((r, i) => (r, cli.Handler.Bodies[i])).Last(x => x.r.Method == method);

    [Fact]
    public async Task ProfileCreate_ReadsSecretFromStdin_NotFromArguments()
    {
        using var cli = await LoggedInAsync();
        cli.Input = Secret + "\nignored\n";

        var code = await cli.RunAsync("mailbox", "profile", "create", "--name", "Relay", "--type", "smtp", "--server", "smtp.example.com",
            "--port", "587", "--username", "relay@example.com", "--secret-stdin");

        code.Should().Be(0, cli.Err.ToString());
        var (post, body) = Last(cli, HttpMethod.Post);
        post.RequestUri!.AbsolutePath.Should().EndWith("/emailserverprofiles");
        var json = JsonDocument.Parse(body!).RootElement;
        json.GetProperty("outgoingpassword").GetString().Should().Be(Secret);
        json.GetProperty("servertype").GetInt32().Should().Be(4);
        json.GetProperty("outgoingauthenticationprotocol").GetInt32().Should().Be(3);
        json.GetProperty("outgoingcredentialretrieval").GetInt32().Should().Be(1);
        json.GetProperty("outgoingusername").GetString().Should().Be("relay@example.com");
        cli.Out.ToString().Should().Contain(ProfileId).And.NotContain(Secret);
        cli.Err.ToString().Should().NotContain(Secret);

        // A secret is never an argument: there is no option that takes one.
        using var other = await LoggedInAsync();
        (await other.RunAsync("mailbox", "profile", "create", "--name", "Relay", "--type", "smtp", "--server", "smtp.example.com",
            "--username", "relay@example.com", "--password", Secret)).Should().Be(2);
        other.Handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("create", "--name", "Relay", "--type", "smtp", "--server", "smtp.example.com", "--password")]
    [InlineData("create", "--name", "Exchange", "--type", "graph", "--tenant", "contoso.example.com", "--client-secret")]
    [InlineData("update", "Relay", "--secret")]
    [InlineData("update", "Relay", "--pasword")]
    [InlineData("update", "Relay", "--port", "587")]
    public async Task ProfileCommands_ASecretAsAnArgument_IsRefused_WithoutEchoingIt(params string[] args)
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync(["mailbox", "profile", .. args, Secret])).Should().Be(2);

        cli.Handler.Requests.Should().BeEmpty();
        cli.Out.ToString().Should().NotContain(Secret);
        cli.Err.ToString().Should().NotContain(Secret).And.Contain("--secret-stdin");
    }

    [Fact]
    public async Task ProfileCreate_WithoutASecretSource_OffATerminal_IsAUsageError()
    {
        using var cli = await LoggedInAsync();
        cli.Input = Secret + "\n";

        (await cli.RunAsync("mailbox", "profile", "create", "--name", "Relay", "--type", "smtp", "--server", "smtp.example.com",
            "--username", "relay@example.com")).Should().Be(2);

        cli.Handler.Requests.Should().BeEmpty();
        cli.Err.ToString().Should().Contain("--secret-stdin");
    }

    [Fact]
    public async Task ProfileCreate_OnATerminal_PromptsForTheSecret()
    {
        using var cli = await LoggedInAsync();
        cli.IsInputTty = true;
        cli.Input = Secret + "\n";

        (await cli.RunAsync("mailbox", "profile", "create", "--name", "Graph", "--type", "graph", "--tenant", "contoso.example.com",
            "--client-id", "11111111-2222-3333-4444-555555555555")).Should().Be(0, cli.Err.ToString());

        var json = JsonDocument.Parse(Last(cli, HttpMethod.Post).Body!).RootElement;
        json.GetProperty("oauthclientsecret").GetString().Should().Be(Secret);
        json.GetProperty("servertype").GetInt32().Should().Be(0);
        json.GetProperty("outgoingauthenticationprotocol").GetInt32().Should().Be(4);
        json.GetProperty("outgoingcredentialretrieval").GetInt32().Should().Be(7);
        json.GetProperty("exchangeonlinetenantid").GetString().Should().Be("contoso.example.com");
        cli.Err.ToString().Should().Contain("secret").And.NotContain(Secret);
    }

    [Fact]
    public async Task ProfileList_ShowsSecretsAsSetOrNotSet()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("mailbox", "profile", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());

        var row = JsonDocument.Parse(cli.Out.ToString()).RootElement[0];
        row.GetProperty("name").GetString().Should().Be("Relay");
        row.GetProperty("type").GetString().Should().Be("smtp");
        row.GetProperty("secret").GetString().Should().Be("set");
    }

    [Fact]
    public async Task List_And_Show()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("mailbox", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var row = JsonDocument.Parse(cli.Out.ToString()).RootElement[0];
        row.GetProperty("emailaddress").GetString().Should().Be("ada@example.com");
        row.GetProperty("approval").GetString().Should().Be("Pending Approval");

        (await cli.RunAsync("mailbox", "show", "ada@example.com")).Should().Be(0, cli.Err.ToString());
        Uri.UnescapeDataString(cli.Handler.Requests.Last().RequestUri!.Query).Should().Contain("emailaddress eq 'ada@example.com'");
        (await cli.RunAsync("mailbox", "show", "nobody@example.com")).Should().Be(4);
    }

    [Fact]
    public async Task Approve_ApprovesTheOwner()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("mailbox", "approve", MailboxId)).Should().Be(0, cli.Err.ToString());

        var (patch, body) = Last(cli, HttpMethod.Patch);
        patch.RequestUri!.AbsolutePath.Should().EndWith($"/systemusers({UserId})");
        JsonDocument.Parse(body!).RootElement.GetProperty("emailrouteraccessapproval").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Link_SetsTheProfile_ServerSideSync_AndEnablesOutgoing()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("mailbox", "link", "ada@example.com", "--profile", "Relay")).Should().Be(0, cli.Err.ToString());

        var (patch, body) = Last(cli, HttpMethod.Patch);
        patch.RequestUri!.AbsolutePath.Should().EndWith($"/mailboxes({MailboxId})");
        var json = JsonDocument.Parse(body!).RootElement;
        json.GetProperty("emailserverprofile@odata.bind").GetString().Should().Be($"/emailserverprofiles({ProfileId})");
        json.GetProperty("outgoingemaildeliverymethod").GetInt32().Should().Be(2);
        json.GetProperty("enabledforoutgoingemail").GetBoolean().Should().BeTrue();
        cli.Out.ToString().Should().Contain("Relay");
    }

    [Fact]
    public async Task Link_UnknownMailbox_Exits4_WithoutWriting()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("mailbox", "link", "nobody@example.com", "--profile", "Relay")).Should().Be(4);

        cli.Handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Link_WithoutProfile_IsAUsageError()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("mailbox", "link", MailboxId)).Should().Be(2);

        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_WithoutWait_SchedulesTheTest()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("mailbox", "test", MailboxId)).Should().Be(0, cli.Err.ToString());

        var (patch, body) = Last(cli, HttpMethod.Patch);
        patch.RequestUri!.AbsolutePath.Should().EndWith($"/mailboxes({MailboxId})");
        JsonDocument.Parse(body!).RootElement.GetProperty("testemailconfigurationscheduled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Test_Wait_PrintsResult()
    {
        var polls = 0;
        using var cli = await LoggedInAsync(r =>
        {
            if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith($"/mailboxes({MailboxId})"))
                return FakeHandler.Json(HttpStatusCode.OK, ++polls < 3
                    ? """{"testemailconfigurationscheduled":true,"outgoingemailstatus":0}"""
                    : """{"testemailconfigurationscheduled":false,"outgoingemailstatus":2,"testmailboxaccesscompletedon":"2026-11-23T10:00:00Z","lastsyncerror":"The SMTP server refused the user name or password (535)."}""");
            return Server(r);
        });
        cli.Clock = new SteppingClock();

        (await cli.RunAsync("mailbox", "test", MailboxId, "--wait", "-o", "json")).Should().Be(5, "a failed test is exit 5");

        polls.Should().Be(3);
        var result = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        result.GetProperty("outgoingemailstatus").GetString().Should().Be("Failure");
        result.GetProperty("lastsyncerror").GetString().Should().Contain("refused");
        result.GetProperty("testmailboxaccesscompletedon").GetString().Should().StartWith("2026-11-23");
    }

    [Fact]
    public async Task Test_Wait_Success_IsExit0()
    {
        using var cli = await LoggedInAsync(r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith($"/mailboxes({MailboxId})")
                ? FakeHandler.Json(HttpStatusCode.OK, """{"testemailconfigurationscheduled":false,"outgoingemailstatus":1,"testmailboxaccesscompletedon":"2026-11-23T10:00:00Z"}""")
                : Server(r));
        cli.Clock = new SteppingClock();

        (await cli.RunAsync("mailbox", "test", MailboxId, "--wait")).Should().Be(0, cli.Err.ToString());

        cli.Out.ToString().Should().Contain("Success");
    }

    /// <summary>A clock whose timers fire at once, moving the time on by their delay.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) Interlocked.Add(ref _ticks, dueTime.Ticks);
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
}
