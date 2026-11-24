using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Mail;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1b, task 6: SendEmail with <c>IssueSend</c> true sets the email Pending Send and queues one <see cref="EmailSendJob"/>,
/// which sends it through the mailbox of its sender (a user or a queue) with the provider of the mailbox's profile. The provider is a
/// recording fake here (<see cref="MailProviders"/> built from it); the jobs run directly, as the worker runs them.
/// </summary>
[Collection("xrmd")]
public sealed class EmailSendTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Secret = "s3cret-relay-password";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => ResetAsync();

    public Task DisposeAsync() => ResetAsync();

    /// <summary>The shared organization back to its defaults: no send job left, policy and tracking tokens off, unresolved parties refused.</summary>
    private async Task ResetAsync()
    {
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.asyncoperation WHERE operationtype = {AsyncOperationQueue.EmailSend}");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.organizationsetting WHERE settingdefinitionid = @id", ("@id", AppModuleSchema.EmailSendFromId));
        await TestSql.ExecAsync(fx.ConnectionString,
            $"UPDATE data.organization SET trackingprefix = NULL, trackingtokeniddigits = NULL, nexttrackingnumber = NULL, maximumtrackingnumber = NULL, allowunresolvedpartiesonemailsend = {TestSql.False}");
    }

    // ---- the recording provider ----

    private sealed record SentMail(string? From, IReadOnlyList<string> To, string Subject, string MessageId,
        IReadOnlyList<(string Name, string Content)> Attachments, MailProfile Profile, MailCredentials Credentials,
        string? InReplyTo = null, IReadOnlyList<string>? References = null);

    private sealed class FakeProvider : IMailProvider
    {
        public List<SentMail> Calls { get; } = [];

        /// <summary>Run after the call is recorded: may throw to fail it.</summary>
        public Func<int, Task>? After { get; set; }

        public async Task SendAsync(MimeMessage message, MailProfile profile, MailCredentials credentials, CancellationToken ct)
        {
            var attachments = new List<(string, string)>();
            foreach (var part in message.Attachments.OfType<MimePart>())
            {
                using var ms = new MemoryStream();
                await part.Content!.DecodeToAsync(ms, ct);
                attachments.Add((part.FileName ?? "", Encoding.UTF8.GetString(ms.ToArray())));
            }
            Calls.Add(new SentMail(message.From.Mailboxes.FirstOrDefault()?.Address, [.. message.To.Mailboxes.Select(m => m.Address)],
                message.Subject ?? "", message.MessageId ?? "", attachments, profile, credentials, message.InReplyTo, [.. message.References]));
            if (After is { } after) await after(Calls.Count);
        }

        public Task TestAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct) => Task.CompletedTask;
    }

    // ---- set-up helpers ----

    private sealed record Sender(Guid Id, Guid Oid, string Address, OrganizationService Service);

    /// <summary>A Basic User with an address (and so a mailbox), who may write activities and read contacts.</summary>
    private async Task<Sender> SenderAsync(string name = "Ada")
    {
        var oid = Guid.NewGuid();
        var address = $"{name.ToLowerInvariant()}.{_tag}@example.com";
        var id = await Admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = $"{name} {_tag}", ["domainname"] = $"{_tag}.{Guid.NewGuid():N}@example.com", ["azureactivedirectoryobjectid"] = oid,
            ["internalemailaddress"] = address, ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await AssignRoleAsync(fx, id, StandardRoles.BasicUser);
        await GrantAsync(fx, id, team: false, ("prvCreateActivity", DepthMask.Basic), ("prvReadActivity", DepthMask.Basic),
            ("prvWriteActivity", DepthMask.Basic), ("prvReadContact", DepthMask.Global));
        return new Sender(id, oid, address, ServiceFor(fx, oid));
    }

    private Task<Guid> QueueAsync(string name, int viewType = 0) => Admin.CreateAsync(new Entity("queue")
    {
        ["name"] = $"{name} {_tag}", ["emailaddress"] = $"{name.ToLowerInvariant()}.{_tag}@example.com",
        ["queueviewtype"] = new OptionSetValue(viewType), ["queuetypecode"] = new OptionSetValue(1),
    });

    /// <summary>An SMTP profile with the credentials in the profile.</summary>
    private Task<Guid> ProfileAsync() => Admin.CreateAsync(new Entity("emailserverprofile")
    {
        ["name"] = "Relay " + _tag, ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
        ["outgoingportnumber"] = 587, ["outgoingusessl"] = false, ["outgoingauthenticationprotocol"] = new OptionSetValue(3),
        ["outgoingcredentialretrieval"] = new OptionSetValue(1), ["outgoingusername"] = "relay@example.com", ["outgoingpassword"] = Secret,
    });

    private async Task<Guid> MailboxIdAsync(string table, Guid owner) =>
        (await Admin.RetrieveAsync(table, owner, new ColumnSet("defaultmailbox"))).GetAttributeValue<EntityReference>("defaultmailbox").Id;

    /// <summary>The mailbox of <paramref name="table"/> <paramref name="owner"/> approved, on <paramref name="profile"/>, enabled and tested.</summary>
    private async Task<Guid> ReadyAsync(string table, Guid owner, Guid profile)
    {
        await Admin.UpdateAsync(new Entity(table, owner) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        var mailbox = await MailboxIdAsync(table, owner);
        await Admin.UpdateAsync(new Entity("mailbox", mailbox)
        {
            ["emailserverprofile"] = new EntityReference("emailserverprofile", profile), ["outgoingemaildeliverymethod"] = new OptionSetValue(2),
            ["enabledforoutgoingemail"] = true,
        });
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET outgoingemailstatus = 1 WHERE mailboxid = @id", ("@id", mailbox));
        return mailbox;
    }

    private async Task<EntityReference> ContactAsync(string first) =>
        new("contact", await Admin.CreateAsync(new Entity("contact")
        {
            ["firstname"] = first, ["lastname"] = "Example", ["emailaddress1"] = $"{first.ToLowerInvariant()}.{_tag}@example.com",
        }));

    private static EntityCollection Parties(params Entity[] parties) => new([.. parties]);

    private static Entity Party(EntityReference record) => new("activityparty") { ["partyid"] = record };

    private async Task<Guid> EmailAsync(OrganizationService service, EntityReference? from = null, params Entity[] to)
    {
        var email = new Entity("email")
        {
            ["subject"] = "Hello " + _tag, ["description"] = "<p>Hi</p>",
            ["to"] = Parties(to.Length > 0 ? to : [Party(await ContactAsync("Grace"))]),
        };
        if (from is not null) email["from"] = Parties(Party(from));
        return await service.CreateAsync(email);
    }

    private static SendEmailRequest Send(Guid id, bool issueSend) => new() { EmailId = id, IssueSend = issueSend };

    private async Task<(int State, int Status)> StateAsync(Guid email)
    {
        var row = (await TestSql.RowsAsync(fx.ConnectionString, "SELECT statecode, statuscode FROM data.email WHERE activityid = @id", ("@id", email))).Single();
        return (Convert.ToInt32(row[0]), Convert.ToInt32(row[1]));
    }

    private async Task<object?> EmailColumnAsync(Guid email, string column) =>
        await TestSql.ScalarAsync(fx.ConnectionString, $"SELECT {column} FROM data.email WHERE activityid = @id", ("@id", email));

    private async Task<object?> MailboxColumnAsync(Guid mailbox, string column) =>
        await TestSql.ScalarAsync(fx.ConnectionString, $"SELECT {column} FROM data.mailbox WHERE mailboxid = @id", ("@id", mailbox));

    private sealed record JobRow(Guid Id, int State, int Status, string? Data, Guid Owner, Guid BusinessUnit, string? Name, string? Message);

    private async Task<List<JobRow>> JobsAsync(Guid email)
    {
        var rows = await TestSql.RowsAsync(fx.ConnectionString,
            "SELECT asyncoperationid, statecode, statuscode, data, ownerid, owningbusinessunit, name, message FROM data.asyncoperation " +
            $"WHERE regardingobjectid = @id AND operationtype = {AsyncOperationQueue.EmailSend} ORDER BY createdon",
            ("@id", email));
        return [.. rows.Select(r => new JobRow((Guid)r[0]!, Convert.ToInt32(r[1]), Convert.ToInt32(r[2]), r[3] as string, (Guid)r[4]!, (Guid)r[5]!,
            r[6] as string, r[7] as string))];
    }

    private EmailSendJob Job(IServiceScope scope, FakeProvider fake) =>
        new(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), scope.ServiceProvider.GetRequiredService<CoreOperations>(),
            new MailProviders(fake, fake), fx.App.Services.GetRequiredService<FileColumnService>(), fx.App.Services.GetRequiredService<SecurityGuard>());

    /// <summary>Runs the (last) send job of <paramref name="email"/> as the worker does: as its owner, from <paramref name="data"/> (its row's by default).</summary>
    private async Task<AsyncOperationRun> RunAsync(Guid email, FakeProvider fake, string? data = null, CancellationToken ct = default)
    {
        var job = (await JobsAsync(email)).Last();
        using var scope = fx.App.Services.CreateScope();
        var run = new AsyncOperationRun(job.Id, new Caller(job.Owner, job.BusinessUnit), job.Owner, data ?? job.Data);
        await Job(scope, fake).RunAsync(run, ct);
        return run;
    }

    private Task SetPolicyAsync(string value) => Admin.CreateAsync(new Entity(AppModuleSchema.OrganizationSetting)
    {
        ["uniquename"] = AppModuleSchema.EmailSendFrom, ["displayname"] = AppModuleSchema.EmailSendFrom, ["value"] = value,
        ["settingdefinitionid"] = new EntityReference(AppModuleSchema.SettingDefinition, AppModuleSchema.EmailSendFromId),
    });

    /// <summary>A user whose own mailbox is ready, and an email of theirs to a contact.</summary>
    private async Task<(Sender Sender, Guid Mailbox, Guid Email)> ReadyUserEmailAsync()
    {
        var sender = await SenderAsync();
        var mailbox = await ReadyAsync("systemuser", sender.Id, await ProfileAsync());
        return (sender, mailbox, await EmailAsync(sender.Service));
    }

    // ---- SendEmail ----

    [Fact]
    public async Task IssueSendTrue_SetsPendingSend_AndEnqueuesOneJob()
    {
        var (sender, _, email) = await ReadyUserEmailAsync();

        var response = (SendEmailResponse)await sender.Service.ExecuteAsync(Send(email, issueSend: true));

        response.Subject.Should().Be("Hello " + _tag);
        (await StateAsync(email)).Should().Be((1, 6));
        var job = (await JobsAsync(email)).Should().ContainSingle().Subject;
        job.State.Should().Be(0);
        job.Owner.Should().Be(sender.Id, "the job sends as the caller");
        JsonNode.Parse(job.Data!)!["emailId"]!.GetValue<Guid>().Should().Be(email);
        job.Data.Should().NotContain(Secret);
        using var scope = fx.App.Services.CreateScope();
        scope.ServiceProvider.GetServices<IAsyncOperationHandler>().Should().ContainSingle(h => h.OperationType == AsyncOperationQueue.EmailSend)
            .Which.Should().BeOfType<EmailSendJob>().Which.RestartsWhenInterrupted.Should().BeFalse();
    }

    [Fact]
    public async Task IssueSendFalse_KeepsTodaysBehaviour_NoJob()
    {
        var (sender, _, email) = await ReadyUserEmailAsync();

        await sender.Service.ExecuteAsync(Send(email, issueSend: false));

        (await StateAsync(email)).Should().Be((1, 3));
        (await EmailColumnAsync(email, "senton")).Should().NotBeNull();
        (await JobsAsync(email)).Should().BeEmpty();
    }

    [Fact]
    public async Task Send_FromQueue_NeedsReadOnQueue()
    {
        var sender = await SenderAsync();
        var queue = await QueueAsync("Private", viewType: 1);
        await ReadyAsync("queue", queue, await ProfileAsync());
        var email = await EmailAsync(sender.Service);
        // The sender cannot read the private queue: an administrator puts it as the sender.
        await Admin.UpdateAsync(new Entity("email", email) { ["from"] = Parties(Party(new EntityReference("queue", queue))) });

        var fault = await FaultAsync(() => sender.Service.ExecuteAsync(Send(email, issueSend: true)));
        fault.HttpStatus.Should().Be(403);
        (await StateAsync(email)).Should().Be((0, 1));
        (await JobsAsync(email)).Should().BeEmpty();

        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("queue", queue), Relationship = new Relationship(QueueSchema.MembershipRelationshipName),
            RelatedEntities = [new EntityReference("systemuser", sender.Id)],
        });
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        (await StateAsync(email)).Should().Be((1, 6));
        var fake = new FakeProvider();
        await RunAsync(email, fake);
        fake.Calls.Should().ContainSingle().Which.From.Should().Be($"private.{_tag}@example.com");
        (await StateAsync(email)).Should().Be((1, 3));
    }

    [Fact]
    public async Task UnresolvedParty_SettingOff_Is400()
    {
        var (sender, _, _) = await ReadyUserEmailAsync();
        var email = await EmailAsync(sender.Service, null, new Entity("activityparty") { ["addressused"] = $"nobody.{_tag}@example.com" });

        var fault = await FaultAsync(() => sender.Service.ExecuteAsync(Send(email, issueSend: true)));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain($"nobody.{_tag}@example.com").And.Contain("allowunresolvedpartiesonemailsend");
        (await StateAsync(email)).Should().Be((0, 1));
        (await JobsAsync(email)).Should().BeEmpty();
    }

    [Fact]
    public async Task UnresolvedParty_SettingOn_Sends()
    {
        var (sender, _, _) = await ReadyUserEmailAsync();
        await TestSql.ExecAsync(fx.ConnectionString, $"UPDATE data.organization SET allowunresolvedpartiesonemailsend = {TestSql.True}");
        var email = await EmailAsync(sender.Service, null, new Entity("activityparty") { ["addressused"] = $"nobody.{_tag}@example.com" });

        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider();
        await RunAsync(email, fake);

        fake.Calls.Should().ContainSingle().Which.To.Should().Equal($"nobody.{_tag}@example.com");
        (await StateAsync(email)).Should().Be((1, 3));
    }

    // ---- the sender policy (xrmverse_EmailSendFrom) ----

    [Fact]
    public async Task PolicyQueueOnly_UserOrEmptyFrom_Is400()
    {
        var sender = await SenderAsync();
        await ReadyAsync("systemuser", sender.Id, await ProfileAsync());
        await SetPolicyAsync(AppModuleSchema.EmailSendFromQueueOnly);
        var empty = await EmailAsync(sender.Service);
        var fromUser = await EmailAsync(sender.Service, new EntityReference("systemuser", sender.Id));

        foreach (var email in new[] { empty, fromUser })
        {
            var fault = await FaultAsync(() => sender.Service.ExecuteAsync(Send(email, issueSend: true)));
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Contain(AppModuleSchema.EmailSendFrom).And.Contain("queue");
            (await StateAsync(email)).Should().Be((0, 1));
            (await JobsAsync(email)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task PolicyQueueOnly_FromQueue_Sends()
    {
        var sender = await SenderAsync();
        var queue = await QueueAsync("Support");
        await ReadyAsync("queue", queue, await ProfileAsync());
        await SetPolicyAsync(AppModuleSchema.EmailSendFromQueueOnly);
        var email = await EmailAsync(sender.Service, new EntityReference("queue", queue));

        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider();
        await RunAsync(email, fake);

        fake.Calls.Should().ContainSingle().Which.From.Should().Be($"support.{_tag}@example.com");
        (await StateAsync(email)).Should().Be((1, 3));
    }

    [Fact]
    public async Task PolicyQueueOnly_IssueSendFalse_Allowed()
    {
        var sender = await SenderAsync();
        await SetPolicyAsync(AppModuleSchema.EmailSendFromQueueOnly);
        var email = await EmailAsync(sender.Service);

        await sender.Service.ExecuteAsync(Send(email, issueSend: false));

        (await StateAsync(email)).Should().Be((1, 3));
        (await JobsAsync(email)).Should().BeEmpty();
    }

    [Fact]
    public async Task Job_PolicyQueueOnly_FailsUserSender()
    {
        var (sender, _, email) = await ReadyUserEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        // The policy changes while the job waits.
        await SetPolicyAsync(AppModuleSchema.EmailSendFromQueueOnly);

        var fake = new FakeProvider();
        var run = await RunAsync(email, fake);

        fake.Calls.Should().BeEmpty();
        (await StateAsync(email)).Should().Be((0, 8));
        run.CompletionMessage.Should().Contain(AppModuleSchema.EmailSendFrom);
    }

    [Fact]
    public async Task PolicyDefault_UserSends()
    {
        var (sender, _, email) = await ReadyUserEmailAsync();

        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider();
        await RunAsync(email, fake);

        fake.Calls.Should().ContainSingle().Which.From.Should().Be(sender.Address);
        (await StateAsync(email)).Should().Be((1, 3));
    }

    // ---- the job ----

    [Fact]
    public async Task SendJob_RegistersOutgoingKey()
    {
        // Plan 4.1c, task 1: the message id the job stores is the email's key, outgoing, so that a copy coming back is not imported.
        var (sender, _, email) = await ReadyUserEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));

        var fake = new FakeProvider();
        await RunAsync(email, fake);

        var sent = fake.Calls.Should().ContainSingle().Subject;
        var rows = await TestSql.RowsAsync(fx.ConnectionString, "SELECT messagekey, incoming FROM meta.mail_message WHERE emailid = @id", ("@id", email));
        var row = rows.Should().ContainSingle().Subject;
        ((byte[])row[0]!).Should().Equal(MailMessageKeys.KeyOf(sent.MessageId));
        Convert.ToBoolean(row[1]).Should().BeFalse();
    }

    [Fact]
    public async Task Job_Success_SetsSent_Senton_MessageId_AndMailboxSuccess()
    {
        var (sender, mailbox, email) = await ReadyUserEmailAsync();
        await sender.Service.CreateAsync(new Entity("activitymimeattachment")
        {
            ["objectid"] = new EntityReference("email", email), ["filename"] = "note.txt", ["mimetype"] = "text/plain",
            ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("attached " + _tag)),
        });
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var before = DateTime.UtcNow.AddSeconds(-5);

        var fake = new FakeProvider();
        var run = await RunAsync(email, fake);

        run.Suspension.Should().BeNull();
        var sent = fake.Calls.Should().ContainSingle().Subject;
        sent.From.Should().Be(sender.Address);
        sent.To.Should().Equal($"grace.{_tag}@example.com");
        sent.Subject.Should().Be("Hello " + _tag);
        sent.Attachments.Should().Equal(("note.txt", "attached " + _tag));
        sent.Profile.ServerLocation.Should().Be("smtp.example.com");
        sent.Profile.Port.Should().Be(587);
        sent.Credentials.UserName.Should().Be("relay@example.com");
        sent.Credentials.Password.Should().Be(Secret);
        (await StateAsync(email)).Should().Be((1, 3));
        ((DateTime)(await EmailColumnAsync(email, "senton"))!).Should().BeOnOrAfter(before);
        (await EmailColumnAsync(email, "messageid")).Should().Be($"<{sent.MessageId}>");
        Convert.ToInt32(await EmailColumnAsync(email, "deliveryattempts")).Should().Be(1);
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "outgoingemailstatus")).Should().Be(1);
        (await StateAsync(email)).Should().Be((1, 3));
        var pointer = await TestSql.ScalarAsync(fx.ConnectionString, "SELECT statuscode FROM data.activitypointer WHERE activityid = @id", ("@id", email));
        Convert.ToInt32(pointer).Should().Be(3);
        run.CompletionMessage.Should().BeNull();
    }

    /// <summary>A received email (with <paramref name="messageId"/>, none when null) answering <paramref name="parent"/>, as the importer stores it.</summary>
    private Sender? _replier;

    /// <summary>The user who answers: ready to send, and the owner of the received emails, so that they can be appended to.</summary>
    private async Task<Sender> ReplierAsync()
    {
        if (_replier is not null) return _replier;
        var sender = await SenderAsync();
        await ReadyAsync("systemuser", sender.Id, await ProfileAsync());
        return _replier = sender;
    }

    private async Task<Guid> ReceivedAsync(string? messageId, Guid? parent = null)
    {
        var owner = await ReplierAsync();
        var received = new Entity("email") { ["subject"] = "Received " + _tag };
        if (messageId is not null) received["messageid"] = messageId;
        if (parent is { } p) received["parentactivityid"] = new EntityReference("email", p);
        return await owner.Service.CreateAsync(received);
    }

    private async Task<(FakeProvider Fake, Guid Email)> SendReplyAsync(Guid parent)
    {
        var sender = await ReplierAsync();
        var reply = await sender.Service.CreateAsync(new Entity("email")
        {
            ["subject"] = "Re: " + _tag, ["description"] = "<p>Hi</p>", ["to"] = Parties(Party(await ContactAsync("Grace"))),
            ["parentactivityid"] = new EntityReference("email", parent),
        });
        await sender.Service.ExecuteAsync(Send(reply, issueSend: true));
        var fake = new FakeProvider();
        await RunAsync(reply, fake);
        return (fake, reply);
    }

    [Fact]
    public async Task Job_Reply_CarriesInReplyTo_AndReferences_AndStoresInReplyTo()
    {
        var parent = await ReceivedAsync($"<parent.{_tag}@customer.example>");

        var (fake, reply) = await SendReplyAsync(parent);

        var sent = fake.Calls.Should().ContainSingle().Subject;
        sent.InReplyTo.Should().Be($"parent.{_tag}@customer.example");
        sent.References.Should().Equal($"parent.{_tag}@customer.example");
        (await EmailColumnAsync(reply, "inreplyto")).Should().Be($"<parent.{_tag}@customer.example>");
    }

    [Fact]
    public async Task Job_NoParent_KeepsAHandSetInReplyTo()
    {
        var sender = await ReplierAsync();
        var email = await sender.Service.CreateAsync(new Entity("email")
        {
            ["subject"] = "Hand " + _tag, ["description"] = "<p>Hi</p>", ["to"] = Parties(Party(await ContactAsync("Grace"))),
            ["inreplyto"] = $"<hand.{_tag}@customer.example>",
        });
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider();
        await RunAsync(email, fake);

        fake.Calls.Should().ContainSingle().Subject.InReplyTo.Should().BeNull();
        (await EmailColumnAsync(email, "inreplyto")).Should().Be($"<hand.{_tag}@customer.example>");
    }

    [Fact]
    public async Task Job_Reply_ParentWithoutMessageId_SendsWithoutHeaders()
    {
        var parent = await ReceivedAsync(null);

        var (fake, reply) = await SendReplyAsync(parent);

        var sent = fake.Calls.Should().ContainSingle().Subject;
        sent.InReplyTo.Should().BeNull();
        sent.References.Should().BeEmpty();
        (await EmailColumnAsync(reply, "inreplyto")).Should().BeOneOf(null, DBNull.Value);
    }

    [Fact]
    public async Task Job_Reply_ChainOfThree_ReferencesOldestFirst()
    {
        var first = await ReceivedAsync($"<m1.{_tag}@customer.example>");
        var second = await ReceivedAsync($"<m2.{_tag}@customer.example>", first);
        var third = await ReceivedAsync($"<m3.{_tag}@customer.example>", second);

        var (fake, _) = await SendReplyAsync(third);

        var sent = fake.Calls.Should().ContainSingle().Subject;
        sent.InReplyTo.Should().Be($"m3.{_tag}@customer.example");
        sent.References.Should().Equal($"m1.{_tag}@customer.example", $"m2.{_tag}@customer.example", $"m3.{_tag}@customer.example");
    }

    [Fact]
    public async Task Job_Reply_ChainLoop_StopsAtTheRepeat()
    {
        var a = await ReceivedAsync($"<a.{_tag}@customer.example>");
        var b = await ReceivedAsync($"<b.{_tag}@customer.example>", a);
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.email SET parentactivityid = @b WHERE activityid = @a", ("@a", a), ("@b", b));

        var (fake, _) = await SendReplyAsync(b);

        fake.Calls.Should().ContainSingle().Which.References.Should().Equal($"a.{_tag}@customer.example", $"b.{_tag}@customer.example");
    }

    [Fact]
    public async Task Job_Transient_RetriesThreeTimes_ThenFailed()
    {
        var (sender, mailbox, email) = await ReadyUserEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider { After = n => throw new MailSendException($"Server busy ({n}).", transient: true) };

        string? data = null;
        foreach (var (minutes, attempt) in new[] { (1, 1), (5, 2), (15, 3) })
        {
            var run = await RunAsync(email, fake, data);
            run.Suspension.Should().NotBeNull($"attempt {attempt} is retried");
            run.Suspension!.Value.Until.Should().Be(run.Now.AddMinutes(minutes));
            data = run.Suspension.Value.Data;
            data.Should().NotContain(Secret);
            Convert.ToInt32(await EmailColumnAsync(email, "deliveryattempts")).Should().Be(attempt);
            (await StateAsync(email)).Should().Be((1, 6), "the email waits for the retry");
            Convert.ToInt32(await MailboxColumnAsync(mailbox, "lastsyncerrorcount")).Should().Be(attempt);
        }

        var last = await RunAsync(email, fake, data);

        last.Suspension.Should().BeNull();
        fake.Calls.Should().HaveCount(4);
        fake.Calls.Select(c => c.MessageId).Distinct().Should().ContainSingle("every attempt sends the same message id");
        (await StateAsync(email)).Should().Be((0, 8));
        Convert.ToInt32(await EmailColumnAsync(email, "deliveryattempts")).Should().Be(4);
        last.CompletionMessage.Should().Contain("Server busy (4).").And.NotContain(Secret);
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "outgoingemailstatus")).Should().Be(2);
        (await MailboxColumnAsync(mailbox, "lastsyncerror") as string).Should().Contain("Server busy (4).").And.NotContain(Secret);
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "lastsyncerrorcount")).Should().Be(4);
        (await MailboxColumnAsync(mailbox, "lastsyncerroroccurredon")).Should().NotBeNull();
    }

    [Fact]
    public async Task Job_Permanent_FailsAtOnce()
    {
        var (sender, mailbox, email) = await ReadyUserEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider
        {
            After = _ => throw new MailSendException("SMTP authentication failed.", transient: false) { Scope = MailFailureScope.Mailbox },
        };

        var run = await RunAsync(email, fake);

        run.Suspension.Should().BeNull();
        fake.Calls.Should().ContainSingle();
        (await StateAsync(email)).Should().Be((0, 8));
        run.CompletionMessage.Should().Contain("SMTP authentication failed.");
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "outgoingemailstatus")).Should().Be(2);
        (await MailboxColumnAsync(mailbox, "lastsyncerror")).Should().Be("SMTP authentication failed.");
    }

    [Fact]
    public async Task Job_RecipientRejected_FailsTheEmailOnly_MailboxStillSends()
    {
        var (sender, mailbox, email) = await ReadyUserEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider
        {
            After = _ => throw new MailSendException("SMTP server answered 550: 5.1.1 no such user", transient: false)
            {
                Scope = MailFailureScope.Message,
            },
        };

        var run = await RunAsync(email, fake);

        run.Suspension.Should().BeNull();
        (await StateAsync(email)).Should().Be((0, 8));
        run.CompletionMessage.Should().Contain("no such user");
        Convert.ToInt32(await MailboxColumnAsync(mailbox, "outgoingemailstatus")).Should().Be(1, "one refused recipient does not stop the mailbox");
        (await MailboxColumnAsync(mailbox, "lastsyncerror")).Should().BeOneOf(null, DBNull.Value);

        // The mailbox still sends the next email.
        var next = await EmailAsync(sender.Service);
        await sender.Service.ExecuteAsync(Send(next, issueSend: true));
        var ok = new FakeProvider();
        await RunAsync(next, ok);
        ok.Calls.Should().ContainSingle();
        (await StateAsync(next)).Should().Be((1, 3));
    }

    [Fact]
    public async Task Job_MailboxCannotSend_FailsWithReason()
    {
        var sender = await SenderAsync();
        var mailbox = await ReadyAsync("systemuser", sender.Id, await ProfileAsync());
        var cases = new (string Sql, string Reason)[]
        {
            ("UPDATE data.systemuser SET emailrouteraccessapproval = 2 WHERE systemuserid = @owner", "approved"),
            ("UPDATE data.mailbox SET emailserverprofile = NULL WHERE mailboxid = @mailbox", "email server profile"),
            ("UPDATE data.mailbox SET outgoingemailstatus = 0 WHERE mailboxid = @mailbox", "test"),
        };
        foreach (var (sql, reason) in cases)
        {
            var email = await EmailAsync(sender.Service);
            await sender.Service.ExecuteAsync(Send(email, issueSend: true));
            var stored = await TestSql.RowsAsync(fx.ConnectionString,
                "SELECT emailserverprofile, outgoingemailstatus FROM data.mailbox WHERE mailboxid = @id", ("@id", mailbox));
            await TestSql.ExecAsync(fx.ConnectionString, sql, ("@owner", sender.Id), ("@mailbox", mailbox));

            var fake = new FakeProvider();
            var run = await RunAsync(email, fake);

            fake.Calls.Should().BeEmpty(reason);
            (await StateAsync(email)).Should().Be((0, 8), reason);
            run.CompletionMessage.Should().Contain(reason);
            Convert.ToInt32(await EmailColumnAsync(email, "deliveryattempts") ?? 0).Should().Be(0, "nothing was attempted");
            // Back to ready for the next case.
            await TestSql.ExecAsync(fx.ConnectionString,
                "UPDATE data.systemuser SET emailrouteraccessapproval = 1 WHERE systemuserid = @owner; " +
                "UPDATE data.mailbox SET emailserverprofile = @profile, outgoingemailstatus = 1 WHERE mailboxid = @mailbox",
                ("@owner", sender.Id), ("@mailbox", mailbox), ("@profile", stored[0][0]));
        }
    }

    [Fact]
    public async Task Job_EmailNoLongerPendingSend_EndsWithoutSending()
    {
        var (sender, _, reopened) = await ReadyUserEmailAsync();
        var deleted = await EmailAsync(sender.Service);
        var alreadySent = await EmailAsync(sender.Service);
        var sentAgain = await EmailAsync(sender.Service);
        foreach (var email in new[] { reopened, deleted, alreadySent, sentAgain })
            await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var jobs = new Dictionary<Guid, JobRow>();
        foreach (var email in new[] { reopened, deleted, alreadySent, sentAgain }) jobs[email] = (await JobsAsync(email)).Single();
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.email SET statecode = 0, statuscode = 1 WHERE activityid = @id", ("@id", reopened));
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.email SET statuscode = 3 WHERE activityid = @id", ("@id", alreadySent));
        await Admin.DeleteAsync("email", deleted);
        // Reopened and sent again: a newer job sends it; the older one ends.
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.email SET statecode = 0, statuscode = 1 WHERE activityid = @id", ("@id", sentAgain));
        await sender.Service.ExecuteAsync(Send(sentAgain, issueSend: true));

        var fake = new FakeProvider();
        foreach (var (email, job) in jobs)
        {
            using var scope = fx.App.Services.CreateScope();
            var run = new AsyncOperationRun(job.Id, new Caller(job.Owner, job.BusinessUnit), job.Owner, job.Data);
            await Job(scope, fake).RunAsync(run, default);
            run.CompletionMessage.Should().NotBeNullOrEmpty();
            run.Suspension.Should().BeNull();
        }

        fake.Calls.Should().BeEmpty();
        (await StateAsync(reopened)).Should().Be((0, 1));
        (await StateAsync(alreadySent)).Should().Be((1, 3));
        (await StateAsync(sentAgain)).Should().Be((1, 6), "the newer job sends it");
        Convert.ToInt32(await EmailColumnAsync(reopened, "deliveryattempts") ?? 0).Should().Be(0);
    }

    [Fact]
    public async Task Job_WithMessageIdAndInterrupted_DoesNotResend()
    {
        var (sender, _, email) = await ReadyUserEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        using var stopping = new CancellationTokenSource();
        // The provider accepts the message, then the host stops before the job records the outcome.
        var fake = new FakeProvider
        {
            After = _ =>
            {
                stopping.Cancel();
                throw new OperationCanceledException(stopping.Token);
            },
        };

        var interrupted = () => RunAsync(email, fake, ct: stopping.Token);
        await interrupted.Should().ThrowAsync<OperationCanceledException>();

        var sent = fake.Calls.Should().ContainSingle().Subject;
        (await EmailColumnAsync(email, "messageid")).Should().Be($"<{sent.MessageId}>", "the message id is committed before the provider call");
        (await StateAsync(email)).Should().Be((1, 6));

        // A run of the same job again (a crashed process's job put back to Ready) does not send it a second time.
        var again = new FakeProvider();
        var rerun = () => RunAsync(email, again);
        (await rerun.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("not sent again");
        again.Calls.Should().BeEmpty();
        (await StateAsync(email)).Should().Be((1, 6), "it stays Pending Send (Known limits)");
    }

    // ---- queue items after a failed send (#333) ----

    /// <summary>An email of a ready sender that an administrator put in a new queue; returns it with its item and the queue.</summary>
    private async Task<(Sender Sender, Guid Mailbox, Guid Email, Guid Item, Guid Queue)> QueuedEmailAsync(string queueName = "Outbox")
    {
        var (sender, mailbox, email) = await ReadyUserEmailAsync();
        var queue = await QueueAsync(queueName);
        var item = ((AddToQueueResponse)await Admin.ExecuteAsync(new AddToQueueRequest
        {
            Target = new EntityReference("email", email), DestinationQueueId = queue,
        })).QueueItemId;
        return (sender, mailbox, email, item, queue);
    }

    /// <summary>The item's state, status, queue and worker, read directly.</summary>
    private async Task<(int State, int Status, Guid? Queue, Guid? Worker)> ItemAsync(Guid item)
    {
        var row = (await TestSql.RowsAsync(fx.ConnectionString,
            "SELECT statecode, statuscode, queueid, workerid FROM data.queueitem WHERE queueitemid = @id", ("@id", item))).Single();
        return (Convert.ToInt32(row[0]), Convert.ToInt32(row[1]), row[2] as Guid?, row[3] as Guid?);
    }

    private static FakeProvider Permanent() => new()
    {
        After = _ => throw new MailSendException("SMTP server answered 550: 5.1.1 no such user", transient: false) { Scope = MailFailureScope.Message },
    };

    [Fact]
    public async Task Failed_ReactivatesTheQueueItemsSendEmailDeactivated()
    {
        var (sender, _, email, item, queue) = await QueuedEmailAsync();
        await Admin.ExecuteAsync(new PickFromQueueRequest { QueueItemId = item, WorkerId = sender.Id });

        await sender.Service.ExecuteAsync(Send(email, issueSend: true));

        (await ItemAsync(item)).Should().Be((1, 2, queue, sender.Id), "SendEmail deactivates the email's item");
        var data = JsonNode.Parse((await JobsAsync(email)).Single().Data!)!.AsObject();
        data["queueItems"]!.AsArray().Select(n => n!.GetValue<Guid>()).Should().Equal(item);

        var run = await RunAsync(email, Permanent());

        (await StateAsync(email)).Should().Be((0, 8));
        run.CompletionMessage.Should().Contain("no such user");
        (await ItemAsync(item)).Should().Be((0, 1, queue, sender.Id), "the failed email is back in its queue, with its worker");
        (await TestSql.ScalarAsync(fx.ConnectionString, "SELECT modifiedby FROM data.queueitem WHERE queueitemid = @id", ("@id", item)))
            .Should().Be(sender.Id, "the sender reactivates the item, as it deactivated it");
    }

    [Fact]
    public async Task Failed_BeforeSending_AlsoReactivatesItems()
    {
        var (sender, mailbox, email, item, queue) = await QueuedEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        // The mailbox's test no longer counts: the job fails the email before calling the provider.
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET outgoingemailstatus = 0 WHERE mailboxid = @id", ("@id", mailbox));

        var fake = new FakeProvider();
        await RunAsync(email, fake);

        fake.Calls.Should().BeEmpty();
        (await StateAsync(email)).Should().Be((0, 8));
        (await ItemAsync(item)).Should().Be((0, 1, queue, (Guid?)null));
    }

    [Fact]
    public async Task Failed_ItemNotReactivated_WhenRecordHasAnotherActiveItem_OrQueueInactive()
    {
        // Another active item of the email (created while it was Pending Send): one active item per record (ruling R15).
        var (sender, _, email, item, queue) = await QueuedEmailAsync("First");
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var other = await QueueAsync("Second");
        var otherItem = await Admin.CreateAsync(new Entity("queueitem")
        {
            ["queueid"] = new EntityReference("queue", other), ["objectid"] = new EntityReference("email", email),
        });

        await RunAsync(email, Permanent());

        (await StateAsync(email)).Should().Be((0, 8));
        (await ItemAsync(item)).Should().Be((1, 2, queue, (Guid?)null), "the email already has an active item");
        (await ItemAsync(otherItem)).State.Should().Be(0);

        // The item's queue deactivated in the meantime: the item stays inactive.
        var (sender2, _, email2, item2, queue2) = await QueuedEmailAsync("Closed");
        await sender2.Service.ExecuteAsync(Send(email2, issueSend: true));
        await Admin.UpdateAsync(new Entity("queue", queue2) { ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2) });

        await RunAsync(email2, Permanent());

        (await StateAsync(email2)).Should().Be((0, 8));
        (await ItemAsync(item2)).Should().Be((1, 2, queue2, (Guid?)null), "its queue is inactive");
    }

    [Fact]
    public async Task Failed_AfterTransientRetry_StillReactivatesItems()
    {
        var (sender, _, email, item, queue) = await QueuedEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        var fake = new FakeProvider
        {
            After = n => throw (n == 1
                ? new MailSendException("Server busy.", transient: true)
                : new MailSendException("SMTP server answered 550: 5.1.1 no such user", transient: false) { Scope = MailFailureScope.Message }),
        };

        var first = await RunAsync(email, fake);

        first.Suspension.Should().NotBeNull("the transient failure is retried");
        var data = JsonNode.Parse(first.Suspension!.Value.Data!)!.AsObject();
        data["queueItems"]!.AsArray().Select(n => n!.GetValue<Guid>()).Should().Equal(item);
        (await ItemAsync(item)).State.Should().Be(1, "the email waits for its retry, still Pending Send");

        await RunAsync(email, fake, first.Suspension.Value.Data);

        fake.Calls.Should().HaveCount(2);
        (await StateAsync(email)).Should().Be((0, 8));
        (await ItemAsync(item)).Should().Be((0, 1, queue, (Guid?)null));
    }

    [Fact]
    public async Task Sent_LeavesItemsInactive()
    {
        var (sender, _, email, item, queue) = await QueuedEmailAsync();
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));

        await RunAsync(email, new FakeProvider());

        (await StateAsync(email)).Should().Be((1, 3));
        (await ItemAsync(item)).Should().Be((1, 2, queue, (Guid?)null));
    }

    [Fact]
    public async Task QueueItemDeactivationAndReactivation_FireNoQueueItemStep()
    {
        // Contract for the contact center: the server's own moves of queue items are core writes; no queueitem step sees them.
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var (sender, _, email, item, queue) = await QueuedEmailAsync();
            var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var thrower = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, thrower, "Update", "queueitem", 20, config: "invalid:queue item step fired");
            // The step is live: an Update of the item through the pipeline runs it.
            (await FaultAsync(() => Admin.UpdateAsync(new Entity("queueitem", item) { ["title"] = "Touched" })))
                .Message.Should().Contain("queue item step fired");

            await sender.Service.ExecuteAsync(Send(email, issueSend: true));
            (await ItemAsync(item)).State.Should().Be(1, "SendEmail deactivated the item without the step");

            var run = await RunAsync(email, Permanent());

            run.CompletionMessage.Should().NotContain("queue item step fired");
            (await StateAsync(email)).Should().Be((0, 8));
            (await ItemAsync(item)).Should().Be((0, 1, queue, (Guid?)null), "the Failed path reactivated the item without the step");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    // ---- tracking tokens ----

    [Fact]
    public async Task TrackingOn_NoTokenGiven_GeneratesToken_AndAppendsItToSubject()
    {
        var (sender, _, email) = await ReadyUserEmailAsync();
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.organization SET trackingprefix = 'CRM:', trackingtokeniddigits = 4, nexttrackingnumber = 7");

        var response = (SendEmailResponse)await sender.Service.ExecuteAsync(Send(email, issueSend: true));

        response.Subject.Should().Be($"Hello {_tag} CRM:0007");
        (await EmailColumnAsync(email, "trackingtoken")).Should().Be("CRM:0007");
        (await EmailColumnAsync(email, "subject")).Should().Be($"Hello {_tag} CRM:0007");
        Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT nexttrackingnumber FROM data.organization")).Should().Be(8);
        var fake = new FakeProvider();
        await RunAsync(email, fake);
        fake.Calls.Should().ContainSingle().Which.Subject.Should().Be($"Hello {_tag} CRM:0007");
    }

    [Fact]
    public async Task TrackingOn_FailedEmailSentAgain_KeepsItsToken_AndTakesNoNewNumber()
    {
        // Final review M2: the subject already carries a token in the current format, so no second one is appended.
        var (sender, _, email) = await ReadyUserEmailAsync();
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.organization SET trackingprefix = 'CRM:', trackingtokeniddigits = 4, nexttrackingnumber = 7");
        await sender.Service.ExecuteAsync(Send(email, issueSend: true));
        await RunAsync(email, new FakeProvider
        {
            After = _ => throw new MailSendException("Recipient rejected.", transient: false) { Scope = MailFailureScope.Message },
        });
        (await StateAsync(email)).Should().Be((0, 8));

        var again = (SendEmailResponse)await sender.Service.ExecuteAsync(Send(email, issueSend: true));

        again.Subject.Should().Be($"Hello {_tag} CRM:0007");
        (await EmailColumnAsync(email, "subject")).Should().Be($"Hello {_tag} CRM:0007");
        (await EmailColumnAsync(email, "trackingtoken")).Should().Be("CRM:0007");
        Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT nexttrackingnumber FROM data.organization")).Should().Be(8);

        // A token of another prefix (the format changed since) is not the current format: a new one is taken.
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.organization SET trackingprefix = 'TKN-'");
        var other = await EmailAsync(sender.Service);
        await sender.Service.UpdateAsync(new Entity("email", other) { ["subject"] = $"Re: old CRM:0003 {_tag}" });
        ((SendEmailResponse)await sender.Service.ExecuteAsync(Send(other, issueSend: true))).Subject.Should().Be($"Re: old CRM:0003 {_tag} TKN-0008");
    }

    [Fact]
    public async Task TrackingTokenIdBase_NonDefaultWrite_Is400_NamingTheColumn()
    {
        // Final review I3: xrmverse does not use the tracking token base, so a value other than empty or 0 is refused.
        var organization = (Guid)(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT organizationid FROM data.organization"))!;
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("organization", organization) { ["trackingtokenidbase"] = 5 })))
            .Should().Contain("trackingtokenidbase");
        await Admin.UpdateAsync(new Entity("organization", organization) { ["trackingtokenidbase"] = 0 });
        await Admin.UpdateAsync(new Entity("organization", organization) { ["trackingtokenidbase"] = null });
    }

    [Fact]
    public async Task TrackingOff_NoToken()
    {
        var (sender, _, email) = await ReadyUserEmailAsync();

        var response = (SendEmailResponse)await sender.Service.ExecuteAsync(Send(email, issueSend: true));

        response.Subject.Should().Be("Hello " + _tag);
        (await EmailColumnAsync(email, "trackingtoken")).Should().BeNull();
        var fake = new FakeProvider();
        await RunAsync(email, fake);
        fake.Calls.Should().ContainSingle().Which.Subject.Should().Be("Hello " + _tag);
    }
}
