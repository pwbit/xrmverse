using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1b, task 7: writing <c>mailbox.testemailconfigurationscheduled</c> true (the owner of the mailbox or a System Administrator)
/// queues one <see cref="MailboxTestJob"/>, which tests the mailbox's profile and credentials (<see cref="IMailProvider.TestAsync"/>),
/// records the outcome on the mailbox and sets the flag back to false; and <c>GetTrackingTokenEmail</c> hands out the next tracking
/// token. The provider is a recording fake here; the jobs run directly, as the worker runs them.
/// </summary>
[Collection("xrmd")]
public sealed class MailboxTestJobTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Secret = "s3cret-relay-password";
    private const string Api = "/api/data/v9.2/";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => ResetAsync();

    public Task DisposeAsync() => ResetAsync();

    private async Task ResetAsync()
    {
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.asyncoperation WHERE operationtype = {AsyncOperationQueue.MailboxTest}");
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.organization SET trackingprefix = NULL, trackingtokeniddigits = NULL, nexttrackingnumber = NULL, maximumtrackingnumber = NULL");
    }

    // ---- the recording provider ----

    private sealed record TestCall(string Address, MailProfile Profile, MailCredentials Credentials);

    private sealed class FakeProvider : IMailProvider
    {
        public List<TestCall> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Task SendAsync(MimeMessage message, MailProfile profile, MailCredentials credentials, CancellationToken ct) =>
            throw new InvalidOperationException("A mailbox test sends nothing.");

        public Task TestAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct)
        {
            Calls.Add(new TestCall(address, profile, credentials));
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    // ---- set-up helpers ----

    private sealed record User(Guid Id, string Address, OrganizationService Service);

    /// <summary>A Basic User with an address (and so a mailbox); <paramref name="mailboxDepth"/> widens its mailbox privileges.</summary>
    private async Task<User> UserAsync(string name, DepthMask mailboxDepth = DepthMask.Basic)
    {
        var oid = Guid.NewGuid();
        var address = $"{name.ToLowerInvariant()}.{_tag}@example.com";
        var id = await Admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = $"{name} {_tag}", ["domainname"] = $"{_tag}.{Guid.NewGuid():N}@example.com", ["azureactivedirectoryobjectid"] = oid,
            ["internalemailaddress"] = address, ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await AssignRoleAsync(fx, id, StandardRoles.BasicUser);
        if (mailboxDepth != DepthMask.Basic)
            await GrantAsync(fx, id, team: false, ("prvReadMailbox", mailboxDepth), ("prvWriteMailbox", mailboxDepth));
        return new User(id, address, ServiceFor(fx, oid));
    }

    /// <summary>An SMTP profile with the credentials in the profile.</summary>
    private Task<Guid> ProfileAsync() => Admin.CreateAsync(new Entity("emailserverprofile")
    {
        ["name"] = "Relay " + _tag, ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
        ["outgoingportnumber"] = 587, ["outgoingusessl"] = false, ["outgoingauthenticationprotocol"] = new OptionSetValue(3),
        ["outgoingcredentialretrieval"] = new OptionSetValue(1), ["outgoingusername"] = "relay@example.com", ["outgoingpassword"] = Secret,
    });

    /// <summary>The user's mailbox, approved, on a profile and set up to send (not tested yet).</summary>
    private async Task<Guid> SetUpAsync(User user)
    {
        await Admin.UpdateAsync(new Entity("systemuser", user.Id) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        var mailbox = (await Admin.RetrieveAsync("systemuser", user.Id, new ColumnSet("defaultmailbox"))).GetAttributeValue<EntityReference>("defaultmailbox").Id;
        await Admin.UpdateAsync(new Entity("mailbox", mailbox)
        {
            ["emailserverprofile"] = new EntityReference("emailserverprofile", await ProfileAsync()),
            ["outgoingemaildeliverymethod"] = new OptionSetValue(2), ["enabledforoutgoingemail"] = true,
        });
        return mailbox;
    }

    private static Entity Schedule(Guid mailbox) => new("mailbox", mailbox) { ["testemailconfigurationscheduled"] = true };

    private sealed record JobRow(Guid Id, int State, string? Data, Guid Owner, Guid BusinessUnit, string? Message);

    private async Task<List<JobRow>> JobsAsync(Guid mailbox)
    {
        var rows = await TestSql.RowsAsync(fx.ConnectionString,
            "SELECT asyncoperationid, statecode, data, ownerid, owningbusinessunit, message FROM data.asyncoperation " +
            $"WHERE regardingobjectid = @id AND operationtype = {AsyncOperationQueue.MailboxTest} ORDER BY createdon",
            ("@id", mailbox));
        return [.. rows.Select(r => new JobRow((Guid)r[0]!, Convert.ToInt32(r[1]), r[2] as string, (Guid)r[3]!, (Guid)r[4]!, r[5] as string))];
    }

    private async Task<object?> MailboxColumnAsync(Guid mailbox, string column) =>
        await TestSql.ScalarAsync(fx.ConnectionString, $"SELECT {column} FROM data.mailbox WHERE mailboxid = @id", ("@id", mailbox));

    /// <summary>Runs the (only) test job of <paramref name="mailbox"/> as the worker does: as its owner.</summary>
    private async Task<AsyncOperationRun> RunAsync(Guid mailbox, FakeProvider fake)
    {
        var job = (await JobsAsync(mailbox)).Should().ContainSingle().Subject;
        using var scope = fx.App.Services.CreateScope();
        var run = new AsyncOperationRun(job.Id, new Caller(job.Owner, job.BusinessUnit), job.Owner, job.Data);
        await new MailboxTestJob(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), scope.ServiceProvider.GetRequiredService<CoreOperations>(),
            new MailProviders(fake, fake)).RunAsync(run, CancellationToken.None);
        return run;
    }

    // ---- the mailbox test ----

    [Fact]
    public async Task Test_Success_SetsStatus_AndCompletedOn()
    {
        var user = await UserAsync("Ada");
        var mailbox = await SetUpAsync(user);
        var before = DateTime.UtcNow.AddSeconds(-5);

        await user.Service.UpdateAsync(Schedule(mailbox));

        var job = (await JobsAsync(mailbox)).Should().ContainSingle().Subject;
        job.Owner.Should().Be(user.Id, "the test runs as the user who asked for it");
        job.Data.Should().NotContain(Secret);
        JsonNode.Parse(job.Data!)!["mailboxId"]!.GetValue<Guid>().Should().Be(mailbox);
        Convert.ToBoolean(await MailboxColumnAsync(mailbox, "testemailconfigurationscheduled")).Should().BeTrue();
        using (var scope = fx.App.Services.CreateScope())
            scope.ServiceProvider.GetServices<IAsyncOperationHandler>().Should().ContainSingle(h => h.OperationType == AsyncOperationQueue.MailboxTest)
                .Which.Should().BeOfType<MailboxTestJob>();

        var fake = new FakeProvider();
        var run = await RunAsync(mailbox, fake);

        var call = fake.Calls.Should().ContainSingle().Subject;
        call.Address.Should().Be(user.Address);
        call.Profile.ServerLocation.Should().Be("smtp.example.com");
        call.Credentials.UserName.Should().Be("relay@example.com");
        call.Credentials.Password.Should().Be(Secret);
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "outgoingemailstatus")).Should().Be(1);
        ((DateTime)(await MailboxColumnAsync(mailbox, "testmailboxaccesscompletedon"))!).Should().BeAfter(before);
        (await MailboxColumnAsync(mailbox, "lastsyncerror")).Should().BeNull();
        Convert.ToBoolean(await MailboxColumnAsync(mailbox, "testemailconfigurationscheduled")).Should().BeFalse();
        run.CompletionMessage.Should().NotContain(Secret);
        // The column reads back through the API, and only the server writes it (ruling P7).
        (await Admin.RetrieveAsync("mailbox", mailbox, new ColumnSet("testmailboxaccesscompletedon")))
            .GetAttributeValue<DateTime?>("testmailboxaccesscompletedon").Should().NotBeNull();
        var fault = await FaultAsync(() => Admin.UpdateAsync(new Entity("mailbox", mailbox) { ["testmailboxaccesscompletedon"] = DateTime.UtcNow.AddDays(-1) }));
        fault.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Test_WrongPassword_SetsFailure_AndReason()
    {
        var user = await UserAsync("Grace");
        var mailbox = await SetUpAsync(user);
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET outgoingemailstatus = 1 WHERE mailboxid = @id", ("@id", mailbox));
        await user.Service.UpdateAsync(Schedule(mailbox));

        var fake = new FakeProvider { Failure = new MailSendException("The SMTP server refused the user name or password (535 5.7.8).", transient: false) };
        var run = await RunAsync(mailbox, fake);

        fake.Calls.Should().ContainSingle();
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "outgoingemailstatus")).Should().Be(2, "a failed test blocks the sends");
        (await MailboxColumnAsync(mailbox, "testmailboxaccesscompletedon")).Should().NotBeNull();
        var error = (string)(await MailboxColumnAsync(mailbox, "lastsyncerror"))!;
        error.Should().Contain("refused the user name or password").And.NotContain(Secret);
        Convert.ToBoolean(await MailboxColumnAsync(mailbox, "testemailconfigurationscheduled")).Should().BeFalse();
        run.CompletionMessage.Should().Contain("refused the user name or password").And.NotContain(Secret);
    }

    [Fact]
    public async Task Test_NotApproved_Fails_WithoutCallingTheProvider()
    {
        var user = await UserAsync("Linus");
        var mailbox = await SetUpAsync(user);
        await Admin.UpdateAsync(new Entity("systemuser", user.Id) { ["emailrouteraccessapproval"] = new OptionSetValue(3) });
        await user.Service.UpdateAsync(Schedule(mailbox));

        var fake = new FakeProvider();
        await RunAsync(mailbox, fake);

        fake.Calls.Should().BeEmpty();
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "outgoingemailstatus")).Should().Be(2);
        ((string)(await MailboxColumnAsync(mailbox, "lastsyncerror"))!).Should().Contain("approved");
        Convert.ToBoolean(await MailboxColumnAsync(mailbox, "testemailconfigurationscheduled")).Should().BeFalse();
    }

    [Fact]
    public async Task Test_ByOtherUser_Is403()
    {
        var owner = await UserAsync("Owner");
        var mailbox = await SetUpAsync(owner);
        // Write on every mailbox, but not its owner and not a System Administrator.
        var other = await UserAsync("Other", DepthMask.Global);

        var fault = await FaultAsync(() => other.Service.UpdateAsync(Schedule(mailbox)));

        fault.HttpStatus.Should().Be(403);
        (await JobsAsync(mailbox)).Should().BeEmpty();
        Convert.ToBoolean(await MailboxColumnAsync(mailbox, "testemailconfigurationscheduled")).Should().BeFalse();
        // The other user may still write the columns of the allowlist (ruling P2).
        await other.Service.UpdateAsync(new Entity("mailbox", mailbox) { ["enabledforoutgoingemail"] = true });
        // A System Administrator may schedule the test of anyone's mailbox; a second request while one waits queues nothing more.
        await Admin.UpdateAsync(Schedule(mailbox));
        await Admin.UpdateAsync(Schedule(mailbox));
        (await JobsAsync(mailbox)).Should().ContainSingle().Which.Owner.Should().Be(await AdminUserIdAsync());
    }

    [Fact]
    public async Task Test_ClearedByOtherUser_Is403()
    {
        var owner = await UserAsync("Owner");
        var mailbox = await SetUpAsync(owner);
        var other = await UserAsync("Other", DepthMask.Global);
        Entity Clear() => new("mailbox", mailbox) { ["testemailconfigurationscheduled"] = false };

        // Sending the stored value back (false while nothing waits) is not a write of the flag.
        await other.Service.UpdateAsync(Clear());
        await Admin.UpdateAsync(Schedule(mailbox));

        var fault = await FaultAsync(() => other.Service.UpdateAsync(Clear()));

        fault.HttpStatus.Should().Be(403);
        Convert.ToBoolean(await MailboxColumnAsync(mailbox, "testemailconfigurationscheduled")).Should().BeTrue();
        // The owner may withdraw its own pending request.
        await owner.Service.UpdateAsync(Clear());
        Convert.ToBoolean(await MailboxColumnAsync(mailbox, "testemailconfigurationscheduled")).Should().BeFalse();
    }

    private async Task<Guid> AdminUserIdAsync() => (Guid)(await TestSql.ScalarAsync(fx.ConnectionString,
        "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;

    // ---- GetTrackingTokenEmail ----

    [Fact]
    public async Task GetTrackingTokenEmail_TrackingOff_Is400()
    {
        var user = await UserAsync("Tok");

        var fault = await FaultAsync(() => user.Service.ExecuteAsync(new GetTrackingTokenEmailRequest { Subject = "Hello" }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("trackingprefix");
    }

    [Fact]
    public async Task GetTrackingTokenEmail_TrackingOn_ReturnsPrefixedNumber_AndIncrements()
    {
        var user = await UserAsync("Ken");
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.organization SET trackingprefix = 'CRM:', trackingtokeniddigits = 4, nexttrackingnumber = 7");

        var first = (GetTrackingTokenEmailResponse)await user.Service.ExecuteAsync(new GetTrackingTokenEmailRequest { Subject = "Hello" });
        var second = (GetTrackingTokenEmailResponse)await Admin.ExecuteAsync(new GetTrackingTokenEmailRequest());

        first.TrackingToken.Should().Be("CRM:0007");
        second.TrackingToken.Should().Be("CRM:0008");
        Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT nexttrackingnumber FROM data.organization")).Should().Be(9);

        // The Web API action (Learn: an unbound action with Subject).
        var res = await HttpAsync(HttpMethod.Post, "GetTrackingTokenEmail", """{"Subject":"Hello"}""");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!;
        json["TrackingToken"]!.GetValue<string>().Should().Be("CRM:0009");
        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.GetTrackingTokenEmailResponse");
        var metadata = await (await HttpAsync(HttpMethod.Get, "$metadata")).Content.ReadAsStringAsync();
        metadata.Should().Contain("Name=\"GetTrackingTokenEmail\"");
    }

    private async Task<HttpResponseMessage> HttpAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }
}
