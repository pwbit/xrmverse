using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 8: what incoming email tests need: mailboxes of basic users, queues and teams, messages, and the import of one
/// message as the sync job runs it (the receiver and the delivery user resolved first). Shared by <see cref="IncomingImportTests"/>
/// and <see cref="WebhookDeliveryTests"/>.
/// </summary>
internal sealed class IncomingKit(XrmdFixture fx, string tag)
{
    public OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public string Tag => tag;

    public string Address(string name) => $"{name.ToLowerInvariant()}.{tag}@example.com";

    /// <summary>A Basic User with an address (and so a mailbox), who may also write the plug-in log of the tests.</summary>
    public async Task<(Guid User, Guid Mailbox)> UserMailboxAsync(string name = "Ada", params (string Privilege, DepthMask Depth)[] privileges)
    {
        var user = await Admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = $"{name} {tag}", ["domainname"] = $"{tag}.{Guid.NewGuid():N}@example.com", ["azureactivedirectoryobjectid"] = Guid.NewGuid(),
            ["internalemailaddress"] = Address(name), ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await AssignRoleAsync(fx, user, StandardRoles.BasicUser);
        await GrantAsync(fx, user, team: false, [("prvCreatepb_PipeLog", DepthMask.Basic), ("prvReadpb_PipeLog", DepthMask.Basic), .. privileges]);
        return (user, await MailboxOfAsync("systemuser", user));
    }

    /// <summary>A queue with an address owned by a new Basic User (its mailbox's owner); returns the queue, its mailbox and that user.</summary>
    public async Task<(Guid Queue, Guid Mailbox, Guid Owner)> QueueMailboxAsync(string name = "Desk")
    {
        var (owner, _) = await UserMailboxAsync(name + "Owner");
        var queue = await Admin.CreateAsync(new Entity("queue")
        {
            ["name"] = $"{name} {tag}", ["emailaddress"] = Address(name), ["queueviewtype"] = new OptionSetValue(0),
            ["queuetypecode"] = new OptionSetValue(1), ["ownerid"] = new EntityReference("systemuser", owner),
        });
        return (queue, await MailboxOfAsync("queue", queue), owner);
    }

    public async Task<Guid> MailboxOfAsync(string table, Guid owner) =>
        (await Admin.RetrieveAsync(table, owner, new ColumnSet("defaultmailbox"))).GetAttributeValue<EntityReference>("defaultmailbox").Id;

    /// <summary>A message from <paramref name="from"/> to <paramref name="to"/>; <paramref name="messageId"/> "" leaves the header out.</summary>
    public MimeMessage Message(string to, string? messageId = null, string subject = "Hello", string? from = null, string html = "<p>Hi</p>")
    {
        var m = new MimeMessage { Subject = $"{subject} {tag}" };
        m.From.Add(MailboxAddress.Parse(from ?? $"sender.{tag}@example.com"));
        m.To.Add(MailboxAddress.Parse(to));
        m.Date = new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.Zero);
        m.Body = new TextPart("html") { Text = html };
        if (messageId == "") m.Headers.Remove(HeaderId.MessageId);
        else m.MessageId = messageId ?? NewMessageId();
        return m;
    }

    public string NewMessageId() => $"{Guid.NewGuid():N}.{tag}@example.com";

    public static IncomingRef Ref(MimeMessage m, string? serverId = null, string? messageId = null) =>
        new(serverId ?? Guid.NewGuid().ToString("N"), messageId ?? (m.MessageId is null ? null : $"<{m.MessageId}>"), null,
            new DateTime(2026, 11, 20, 8, 31, 0, DateTimeKind.Utc), null);

    public IncomingEmailImporter Importer(IServiceScope scope) =>
        new(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), scope.ServiceProvider.GetRequiredService<PipelineExecutor>(),
            fx.App.Services.GetRequiredService<IMetadataStore>(), fx.App.Services.GetRequiredService<SecurityGuard>());

    public async Task<(MailboxReceiver Receiver, Caller DeliveryUser)> ReceiverAsync(Guid mailbox)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var s = new DataSession(conn, null);
        var receiver = (await Mailboxes.ResolveReceiverAsync(s, mailbox, default))!;
        return (receiver, await Mailboxes.DeliveryUserAsync(s, receiver, default));
    }

    /// <summary>Imports <paramref name="m"/> into <paramref name="mailbox"/> as the sync job does, with the filter <paramref name="filter"/> (default All).</summary>
    public async Task<ImportOutcome> ImportAsync(Guid mailbox, MimeMessage m, IncomingRef? item = null, int? filter = MailSchema.FilterAll,
        CancellationToken ct = default)
    {
        var (receiver, user) = await ReceiverAsync(mailbox);
        if (filter is { } f) receiver = receiver with { Filter = f };
        using var scope = fx.App.Services.CreateScope();
        return await Importer(scope).ImportAsync(receiver, user, item ?? Ref(m), m, ct);
    }

    /// <summary>The emails whose subject is <paramref name="subject"/> plus the tag.</summary>
    public async Task<List<Guid>> EmailsAsync(string subject = "Hello") =>
        [.. (await TestSql.RowsAsync(fx.ConnectionString, "SELECT activityid FROM data.email WHERE subject = @s", ("@s", $"{subject} {tag}")))
            .Select(r => (Guid)r[0]!)];

    public async Task<List<Guid>> QueueItemsAsync(Guid email) =>
        [.. (await TestSql.RowsAsync(fx.ConnectionString, "SELECT queueitemid FROM data.queueitem WHERE objectid = @id", ("@id", email)))
            .Select(r => (Guid)r[0]!)];
}

/// <summary>
/// Plan 4.1c, task 8: <see cref="IncomingEmailImporter"/> imports one incoming message in one transaction, through the pipeline, as the
/// delivery user, with <see cref="MailSchema.MailboxDeliveryVariable"/> in every context: the received email, its attachments (a
/// blocked or oversized one becomes a note), and, for a queue mailbox, AddToQueue and its nested <c>queueitem</c> Create.
/// </summary>
[Collection("xrmd")]
public sealed class IncomingImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Key = MailSchema.MailboxDeliveryVariable;

    private readonly IncomingKit _kit = new(fx, Guid.NewGuid().ToString("N")[..8]);
    private OrganizationService Admin => _kit.Admin;
    private Guid _assembly;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{TestEntities.PipeLog}");
        _assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{TestEntities.PipeLog}");
        await TestSql.ExecAsync(fx.ConnectionString,
            $"UPDATE data.organization SET maxuploadfilesize = 5242880, blockedattachments = '{MailSchema.DefaultBlockedAttachments}'");
    }

    // ---- helpers ----

    private static readonly (string, DepthMask)[] AccountPrivileges = [("prvReadAccount", DepthMask.Basic), ("prvAppendToAccount", DepthMask.Basic)];

    private async Task<Guid> StepAsync(string type, string message, string? entity, int stage, string config, int rank = 1) =>
        await PluginTestHelper.RegisterStepAsync(Admin, await PluginTestHelper.RegisterTypeAsync(Admin, _assembly, type), message, entity, stage,
            rank: rank, config: config);

    private Task<Guid> RecordStepAsync(string message, string? entity, int stage) =>
        StepAsync("Xrmd.TestPlugins.SharedVariablePlugin", message, entity, stage, $"record|{TestEntities.PipeLog}|{Key}");

    private async Task<List<string>> LogAsync() =>
        [.. (await TestSql.RowsAsync(fx.ConnectionString, $"SELECT pb_name FROM data.{TestEntities.PipeLog}")).Select(r => (string)r[0]!)];

    private async Task<Entity> EmailAsync(Guid id) => await Admin.RetrieveAsync("email", id, new ColumnSet(true));

    private async Task<EntityReference> ContactAsync(string first, string? address = null) =>
        new("contact", await Admin.CreateAsync(new Entity("contact")
        {
            ["firstname"] = first, ["lastname"] = "Example", ["emailaddress1"] = address ?? _kit.Address(first),
        }));

    /// <summary>An email owned by <paramref name="owner"/> whose Message-ID is registered (sent from here), regarding <paramref name="regarding"/>.</summary>
    private async Task<Guid> SentEmailAsync(string messageId, Guid owner, EntityReference? regarding = null)
    {
        var email = new Entity("email") { ["subject"] = "Sent " + _kit.Tag, ["ownerid"] = new EntityReference("systemuser", owner) };
        if (regarding is not null) email["regardingobjectid"] = regarding;
        var id = await Admin.CreateAsync(email);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        (await MailMessageKeys.RegisterOutgoingAsync(new DataSession(conn, null), id, messageId, default)).Should().BeTrue();
        return id;
    }

    private async Task<bool> KeyRegisteredAsync(string? messageId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await MailMessageKeys.FindAsync(new DataSession(conn, null), MailMessageKeys.KeyOf(MailMessageKeys.Normalize(messageId)!), default) is not null;
    }

    // ---- tests ----

    [Fact]
    public async Task Import_UserMailbox_CreatesReceivedEmail_OwnedByUser()
    {
        var (user, mailbox) = await _kit.UserMailboxAsync("Ada", ("prvReadContact", DepthMask.Global));
        var sender = await ContactAsync("Grace");
        var m = _kit.Message(_kit.Address("Ada"), from: _kit.Address("Grace"), html: "<p>Hello there</p>");
        m.To.Add(MailboxAddress.Parse(_kit.Address("Other")));
        m.InReplyTo = _kit.NewMessageId();
        m.Headers.Add("Thread-Index", "AdkAAAAAAAAAAAAAAAAAAAAAAAAAAA==");

        (await _kit.ImportAsync(mailbox, m, filter: null)).Should().Be(ImportOutcome.Created, "the user's default filter admits a contact");

        var email = await EmailAsync((await _kit.EmailsAsync()).Should().ContainSingle().Subject);
        email.GetAttributeValue<bool>("directioncode").Should().BeFalse();
        email.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        email.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(QueueSchema.ReceivedEmailStatus);
        email.GetAttributeValue<string>("description").Should().Be("<p>Hello there</p>");
        email.GetAttributeValue<string>("messageid").Should().Contain(m.MessageId);
        email.GetAttributeValue<string>("inreplyto").Should().Contain(m.InReplyTo);
        email.GetAttributeValue<string>("conversationindex").Should().Be("AdkAAAAAAAAAAAAAAAAAAAAAAAAAAA==");
        email.GetAttributeValue<string>("sender").Should().Be(_kit.Address("Grace"));
        email.GetAttributeValue<string>("torecipients").Should().Be($"{_kit.Address("Ada")};{_kit.Address("Other")}");
        email.GetAttributeValue<bool>("isduplicatesenderunresolved").Should().BeFalse();
        email.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeNull();
        email.GetAttributeValue<EntityReference>("parentactivityid").Should().BeNull();
        email.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);
        email.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(user);
        email.GetAttributeValue<DateTime>("senton").Should().Be(new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc));
        email.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(MailSchema.CorrelationNone);
        email.GetAttributeValue<EntityReference>("emailsender").Should().Match<EntityReference>(r => r.LogicalName == "contact" && r.Id == sender.Id);
        email.GetAttributeValue<EntityCollection>("from").Entities.Should().ContainSingle()
            .Which.GetAttributeValue<EntityReference>("partyid").Id.Should().Be(sender.Id);
        var to = email.GetAttributeValue<EntityCollection>("to").Entities;
        to.Should().HaveCount(2);
        to.Should().ContainSingle(p => p.GetAttributeValue<EntityReference>("partyid") != null)
            .Which.GetAttributeValue<EntityReference>("partyid").Id.Should().Be(user);
        (await _kit.QueueItemsAsync(email.Id)).Should().BeEmpty("a user mailbox adds to no queue");
        (await KeyRegisteredAsync(m.MessageId)).Should().BeTrue();
    }

    [Fact]
    public async Task Import_NoDate_SentOnIsReceivedTime()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var m = _kit.Message(_kit.Address("Ada"));
        m.Headers.Remove(HeaderId.Date);

        await _kit.ImportAsync(mailbox, m);

        (await EmailAsync((await _kit.EmailsAsync()).Single())).GetAttributeValue<DateTime>("senton")
            .Should().Be(new DateTime(2026, 11, 20, 8, 31, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Import_Reply_CopiesRegardingAndParent()
    {
        var (user, mailbox) = await _kit.UserMailboxAsync("Ada", AccountPrivileges);
        var account = new EntityReference("account", await Admin.CreateAsync(new Entity("account")
        {
            ["name"] = "Acme " + _kit.Tag, ["ownerid"] = new EntityReference("systemuser", user),
        }));
        var parentId = _kit.NewMessageId();
        var parent = await SentEmailAsync(parentId, user, account);
        var m = _kit.Message(_kit.Address("Ada"), subject: "Re");
        m.InReplyTo = parentId;

        (await _kit.ImportAsync(mailbox, m, filter: MailSchema.FilterResponses)).Should().Be(ImportOutcome.Created, "a response is admitted");

        var email = await EmailAsync((await _kit.EmailsAsync("Re")).Single());
        email.GetAttributeValue<EntityReference>("regardingobjectid").Should().Match<EntityReference>(r => r.LogicalName == "account" && r.Id == account.Id);
        email.GetAttributeValue<EntityReference>("parentactivityid").Id.Should().Be(parent);
        email.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(MailSchema.CorrelationInReplyTo);
    }

    [Fact]
    public async Task Import_RegardingNotAppendable_CreatedWithoutRegarding()
    {
        var (user, mailbox) = await _kit.UserMailboxAsync("Ada", AccountPrivileges);
        // The admin's account: the Basic User may not append to it.
        var account = new EntityReference("account", await Admin.CreateAsync(new Entity("account") { ["name"] = "Foreign " + _kit.Tag }));
        var parentId = _kit.NewMessageId();
        var parent = await SentEmailAsync(parentId, user, account);
        var m = _kit.Message(_kit.Address("Ada"), subject: "Re");
        m.InReplyTo = parentId;

        (await _kit.ImportAsync(mailbox, m)).Should().Be(ImportOutcome.Created);

        var email = await EmailAsync((await _kit.EmailsAsync("Re")).Single());
        email.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeNull();
        email.GetAttributeValue<EntityReference>("parentactivityid").Id.Should().Be(parent);
        email.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(MailSchema.CorrelationInReplyTo);
    }

    /// <summary>Final review M5: a correlated reply whose parent and regarding record are both left out stores correlation method 0 (None).</summary>
    [Fact]
    public async Task Import_ParentNotAppendable_NoRegarding_CorrelationNone()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync("Ada");
        // Bea's email: Ada (a Basic User) may not append to it.
        var (bea, _) = await _kit.UserMailboxAsync("Bea");
        var parentId = _kit.NewMessageId();
        await SentEmailAsync(parentId, bea);
        var m = _kit.Message(_kit.Address("Ada"), subject: "Re");
        m.InReplyTo = parentId;

        (await _kit.ImportAsync(mailbox, m)).Should().Be(ImportOutcome.Created);

        var email = await EmailAsync((await _kit.EmailsAsync("Re")).Single());
        email.GetAttributeValue<EntityReference>("parentactivityid").Should().BeNull();
        email.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeNull();
        email.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(MailSchema.CorrelationNone);
    }

    /// <summary>
    /// Final review M1: <c>messageid</c> holds the id that made the key, with its angle brackets, only when it fits the column (320);
    /// with no MIME <c>Message-ID</c> it is the one the server reports.
    /// </summary>
    [Fact]
    public async Task Import_MessageId_StoredOnlyWhenItMadeTheKeyAndFits()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var longId = new string('a', 330) + "." + _kit.NewMessageId();
        var tooLong = new string('b', 1000) + "." + _kit.NewMessageId();
        var fitting = new string('c', 318 - 1 - _kit.NewMessageId().Length) + "." + _kit.NewMessageId();
        (fitting.Length + 2).Should().Be(320, "exactly the column length with its angle brackets");
        var reported = _kit.NewMessageId();

        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), longId, subject: "Long"))).Should().Be(ImportOutcome.Created);
        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), tooLong, subject: "TooLong"))).Should().Be(ImportOutcome.Created);
        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), fitting, subject: "Fitting"))).Should().Be(ImportOutcome.Created);
        var bare = _kit.Message(_kit.Address("Ada"), messageId: "", subject: "Reported");
        (await _kit.ImportAsync(mailbox, bare, IncomingKit.Ref(bare, messageId: reported))).Should().Be(ImportOutcome.Created);

        async Task<string?> StoredAsync(string subject) => (await EmailAsync((await _kit.EmailsAsync(subject)).Single())).GetAttributeValue<string>("messageid");
        (await StoredAsync("Long")).Should().BeNull("a 320+ character id is keyed in full, never stored cut");
        (await KeyRegisteredAsync(longId)).Should().BeTrue();
        (await StoredAsync("TooLong")).Should().BeNull("an id over 998 characters is no key: the content is");
        (await StoredAsync("Fitting")).Should().Be($"<{fitting}>");
        (await StoredAsync("Reported")).Should().Be($"<{reported}>", "the server's id made the key");
    }

    [Fact]
    public async Task Import_UnreadableSender_KeepsItsAddressOnly()
    {
        // Basic User reads no contact: the sender stays unresolved, and the delivery is not refused for it.
        var (_, mailbox) = await _kit.UserMailboxAsync();
        await ContactAsync("Grace");

        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), from: _kit.Address("Grace")), filter: null))
            .Should().Be(ImportOutcome.Created, "the filter counts the contact all the same");

        var email = await EmailAsync((await _kit.EmailsAsync()).Single());
        var from = email.GetAttributeValue<EntityCollection>("from").Entities.Should().ContainSingle().Subject;
        from.GetAttributeValue<EntityReference>("partyid").Should().BeNull();
        from.GetAttributeValue<string>("addressused").Should().Be(_kit.Address("Grace"));
        email.GetAttributeValue<EntityReference>("emailsender").Should().BeNull();
    }

    [Fact]
    public async Task Import_Filtered_NoRowNoKey()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var m = _kit.Message(_kit.Address("Ada"), from: $"stranger.{_kit.Tag}@example.com");

        (await _kit.ImportAsync(mailbox, m, filter: null)).Should().Be(ImportOutcome.Filtered, "the user's default filter wants a contact or account");

        (await _kit.EmailsAsync()).Should().BeEmpty();
        (await KeyRegisteredAsync(m.MessageId)).Should().BeFalse();
        (await _kit.ImportAsync(mailbox, m, filter: MailSchema.FilterAll)).Should().Be(ImportOutcome.Created, "a filtered message holds no key");
    }

    [Fact]
    public async Task Import_QueueMailbox_CreatesEmail_AndQueueItemThroughPipeline()
    {
        var (queue, mailbox, owner) = await _kit.QueueMailboxAsync();
        await RecordStepAsync("Create", "queueitem", 40);
        var options = fx.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PipelineOptions>>().Value;
        options.WebhookAllowAnyPort = true;
        using var receiver = new WebhookDeliveryTests.Receiver();
        try
        {
            var endpoint = await Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
            {
                ["name"] = "delivery " + _kit.Tag, ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
                ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = receiver.Url, [ServiceEndpointSchema.AuthValue] = "k3y",
            });
            await WebhookDeliveryTests.CreateWebhookStepAsync(Admin, endpoint, "AddToQueue", null, 40);

            (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Desk")))).Should().Be(ImportOutcome.Created);

            var email = await EmailAsync((await _kit.EmailsAsync()).Single());
            email.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(owner);
            var item = await Admin.RetrieveAsync("queueitem", (await _kit.QueueItemsAsync(email.Id)).Should().ContainSingle().Subject,
                new ColumnSet("queueid", "createdby", "title", "statecode"));
            item.GetAttributeValue<EntityReference>("queueid").Id.Should().Be(queue);
            item.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(owner, "the delivery user creates the item");
            item.GetAttributeValue<string>("title").Should().Be($"Hello {_kit.Tag}");
            (await LogAsync()).Should().Equal($"Create|40|2|{mailbox:D}");
            var hook = receiver.Requests.Should().ContainSingle().Subject;
            hook.Body.GetProperty("MessageName").GetString().Should().Be("AddToQueue");
            hook.Body.GetProperty("InitiatingUserId").GetGuid().Should().Be(owner);
        }
        finally
        {
            options.WebhookAllowAnyPort = false;
            await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;");
        }
    }

    [Fact]
    public async Task Import_GuardContext()
    {
        var (queue, mailbox, owner) = await _kit.QueueMailboxAsync();
        await StepAsync("Xrmd.TestPlugins.ContextRecorderPlugin", "AddToQueue", null, 20, TestEntities.PipeLog);
        await RecordStepAsync("AddToQueue", null, 20);
        await StepAsync("Xrmd.TestPlugins.ContextRecorderPlugin", "Create", "queueitem", 20, TestEntities.PipeLog);
        await RecordStepAsync("Create", "queueitem", 20);

        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Desk")))).Should().Be(ImportOutcome.Created);

        var rows = await TestSql.RowsAsync(fx.ConnectionString, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}");
        var names = rows.Select(r => (string)r[0]!).ToList();
        names.Should().Contain($"AddToQueue|20|1|{mailbox:D}").And.Contain($"Create|20|2|{mailbox:D}");
        var addToQueue = rows.Single(r => (string)r[0]! == "AddToQueue|20|0|1")[1]!.ToString()!.Split('\n');
        addToQueue.Should().Contain($"user={owner}").And.Contain($"initiating={owner}").And.Contain("parent=");
        addToQueue.Single(l => l.StartsWith("shared=", StringComparison.Ordinal)).Should().Contain(Key);
        var nested = rows.Single(r => (string)r[0]! == "Create|20|0|2")[1]!.ToString()!.Split('\n');
        nested.Should().Contain("parent=AddToQueue").And.Contain($"user={owner}").And.Contain($"initiating={owner}");
        nested.Single(l => l.StartsWith("shared=", StringComparison.Ordinal)).Should().Contain(Key);
        (await _kit.QueueItemsAsync((await _kit.EmailsAsync()).Single())).Should().ContainSingle();
        queue.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Import_SameMessageTwice_CreatesOneEmail()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var m = _kit.Message(_kit.Address("Ada"));

        (await _kit.ImportAsync(mailbox, m)).Should().Be(ImportOutcome.Created);
        (await _kit.ImportAsync(mailbox, m, IncomingKit.Ref(m))).Should().Be(ImportOutcome.Duplicate);

        (await _kit.EmailsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Import_SameMessageTwice_InParallel_OneCreatedOneDuplicate()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var id = _kit.NewMessageId();

        var outcomes = await Task.WhenAll(
            Task.Run(() => _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), id))),
            Task.Run(() => _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), id))));

        outcomes.Should().BeEquivalentTo([ImportOutcome.Created, ImportOutcome.Duplicate]);
        (await _kit.EmailsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Import_SameMessageToSecondQueueMailbox_NoSecondEmail_NoSecondQueueItem()
    {
        var (first, firstMailbox, _) = await _kit.QueueMailboxAsync("Desk");
        var (_, secondMailbox, _) = await _kit.QueueMailboxAsync("Help");
        var m = _kit.Message(_kit.Address("Desk"));
        m.To.Add(MailboxAddress.Parse(_kit.Address("Help")));

        (await _kit.ImportAsync(firstMailbox, m)).Should().Be(ImportOutcome.Created);
        (await _kit.ImportAsync(secondMailbox, m)).Should().Be(ImportOutcome.Duplicate);

        var email = (await _kit.EmailsAsync()).Should().ContainSingle().Subject;
        var item = (await _kit.QueueItemsAsync(email)).Should().ContainSingle().Subject;
        (await Admin.RetrieveAsync("queueitem", item, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id.Should().Be(first);
    }

    [Fact]
    public async Task Import_MessageAlreadySentByOrg_NoNewEmail_NoQueueItem()
    {
        var (_, mailbox, owner) = await _kit.QueueMailboxAsync();
        var id = _kit.NewMessageId();
        var sent = await SentEmailAsync(id, owner);

        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Desk"), id))).Should().Be(ImportOutcome.Duplicate);

        (await _kit.EmailsAsync()).Should().BeEmpty();
        (await _kit.QueueItemsAsync(sent)).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_NoMessageId_ImportedOnce()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var m = _kit.Message(_kit.Address("Ada"), messageId: "");
        m.MessageId.Should().BeNull();

        (await _kit.ImportAsync(mailbox, m, IncomingKit.Ref(m, "1:10", messageId: null))).Should().Be(ImportOutcome.Created);
        // The same content under another server id (a UIDVALIDITY reset): the key does not depend on it.
        var again = _kit.Message(_kit.Address("Ada"), messageId: "");
        (await _kit.ImportAsync(mailbox, again, IncomingKit.Ref(again, "2:1", messageId: null))).Should().Be(ImportOutcome.Duplicate);
        // Another body length is another message.
        var other = _kit.Message(_kit.Address("Ada"), messageId: "", html: "<p>Hi again</p>");
        (await _kit.ImportAsync(mailbox, other, IncomingKit.Ref(other, "2:2", messageId: null))).Should().Be(ImportOutcome.Created);

        (await _kit.EmailsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Import_KeyFromMimeMessageId_FallsBackToIncomingRef()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var mime = _kit.NewMessageId();
        var reported = _kit.NewMessageId();

        // The MIME Message-ID is the key, not the one the server reports.
        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), mime, subject: "One"),
            IncomingKit.Ref(new MimeMessage(), messageId: $"<{reported}>"))).Should().Be(ImportOutcome.Created);
        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Ada"), reported, subject: "Two"))).Should().Be(ImportOutcome.Created);
        // Without a MIME Message-ID, the server's (brackets or not) is the key.
        var bare = _kit.Message(_kit.Address("Ada"), messageId: "", subject: "Three");
        (await _kit.ImportAsync(mailbox, bare, IncomingKit.Ref(bare, messageId: mime))).Should().Be(ImportOutcome.Duplicate);
        (await _kit.ImportAsync(mailbox, bare, IncomingKit.Ref(bare, messageId: $" <{reported}> "))).Should().Be(ImportOutcome.Duplicate);

        (await _kit.EmailsAsync("One")).Should().ContainSingle();
        (await _kit.EmailsAsync("Two")).Should().ContainSingle();
        (await _kit.EmailsAsync("Three")).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_PipelineFailure_ThrowsIncomingImportException_OtherErrorsPropagateUnchanged()
    {
        var (user, mailbox) = await _kit.UserMailboxAsync();

        // A plug-in's failure: the pipeline's own.
        var step = await StepAsync("Xrmd.TestPlugins.ThrowPlugin", "Create", "email", 20, "invalid:refused by a plug-in");
        var m = _kit.Message(_kit.Address("Ada"));
        var failure = (await FluentActions.Awaiting(() => _kit.ImportAsync(mailbox, m)).Should().ThrowAsync<IncomingImportException>()).Which;
        failure.InnerException.Should().BeOfType<XrmdFaultException>().Which.Message.Should().Contain("refused by a plug-in");
        await Admin.DeleteAsync("sdkmessageprocessingstep", step);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();

        // Ruling T8-a: an earlier email whose regarding names no table is caused by the data, so it is permanent too.
        var parentId = _kit.NewMessageId();
        var parent = await SentEmailAsync(parentId, user);
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.email SET regardingobjectid = @r, regardingobjectidtype = 2147000000 WHERE activityid = @id", ("@r", Guid.NewGuid()), ("@id", parent));
        var reply = _kit.Message(_kit.Address("Ada"), subject: "Re");
        reply.InReplyTo = parentId;
        (await FluentActions.Awaiting(() => _kit.ImportAsync(mailbox, reply)).Should().ThrowAsync<IncomingImportException>())
            .Which.InnerException.Should().BeAssignableTo<InvalidOperationException>().Which.Message.Should().Contain("2147000000");

        // Not the pipeline's: a setup error and a cancellation propagate unchanged.
        await FluentActions.Awaiting(() => _kit.ImportAsync(mailbox, m, filter: 9)).Should().ThrowAsync<MailSetupException>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await FluentActions.Awaiting(() => _kit.ImportAsync(mailbox, m, ct: cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();

        (await _kit.EmailsAsync()).Should().BeEmpty();
        (await _kit.EmailsAsync("Re")).Should().BeEmpty();
        (await _kit.ImportAsync(mailbox, m)).Should().Be(ImportOutcome.Created, "nothing of the failed attempts was kept");
    }

    [Fact]
    public async Task Import_PluginThrowsOnEmailCreate_NothingCommitted_KeyRowRolledBack()
    {
        var (_, mailbox, _) = await _kit.QueueMailboxAsync();
        var step = await StepAsync("Xrmd.TestPlugins.ThrowPlugin", "Create", "email", 40, "invalid:after the insert");
        var m = _kit.Message(_kit.Address("Desk"));

        await FluentActions.Awaiting(() => _kit.ImportAsync(mailbox, m)).Should().ThrowAsync<IncomingImportException>();

        (await _kit.EmailsAsync()).Should().BeEmpty();
        (await KeyRegisteredAsync(m.MessageId)).Should().BeFalse("the key row is rolled back with the email");

        // A failure of the nested queueitem Create, at the end of the delivery, rolls back the email too.
        await Admin.DeleteAsync("sdkmessageprocessingstep", step);
        await StepAsync("Xrmd.TestPlugins.ThrowPlugin", "Create", "queueitem", 40, "invalid:no item");
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
        await FluentActions.Awaiting(() => _kit.ImportAsync(mailbox, m)).Should().ThrowAsync<IncomingImportException>();
        (await _kit.EmailsAsync()).Should().BeEmpty();
        (await KeyRegisteredAsync(m.MessageId)).Should().BeFalse();

        await PluginTestHelper.ClearAsync(fx);
        (await _kit.ImportAsync(mailbox, m)).Should().Be(ImportOutcome.Created);
    }

    private static MimeMessage WithAttachments(MimeMessage m, params (string Name, byte[] Body)[] files)
    {
        var builder = new BodyBuilder { HtmlBody = "<p>Files</p>" };
        foreach (var (name, body) in files) builder.Attachments.Add(name, body, ContentType.Parse("application/octet-stream"));
        m.Body = builder.ToMessageBody();
        return m;
    }

    [Fact]
    public async Task Import_Attachments_StoredOnReceivedEmail()
    {
        var (_, mailbox) = await _kit.UserMailboxAsync();
        var m = WithAttachments(_kit.Message(_kit.Address("Ada")), ("report.txt", Encoding.UTF8.GetBytes("the report")), ("data.csv", "a,b"u8.ToArray()));

        (await _kit.ImportAsync(mailbox, m)).Should().Be(ImportOutcome.Created);

        var email = (await _kit.EmailsAsync()).Single();
        var rows = await TestSql.RowsAsync(fx.ConnectionString,
            "SELECT filename, filesize, createdby FROM data.activitymimeattachment WHERE objectid = @id ORDER BY attachmentnumber", ("@id", email));
        rows.Select(r => (string)r[0]!).Should().Equal("report.txt", "data.csv");
        Convert.ToInt32(rows[0][1]).Should().Be(10);
        (await EmailAsync(email)).GetAttributeValue<int>("attachmentcount").Should().Be(2);
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.annotation WHERE objectid = @id", ("@id", email))).Should().Be(0);

        // Rule P4 holds again once the import is over: a received email takes no new attachment.
        var fault = await FaultAsync(() => Admin.CreateAsync(new Entity("activitymimeattachment")
        {
            ["objectid"] = new EntityReference("email", email), ["filename"] = "late.txt", ["body"] = Convert.ToBase64String("late"u8.ToArray()),
        }));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("not open");
    }

    [Fact]
    public async Task Import_BlockedAndOversizedAttachments_SkippedWithNote()
    {
        var (user, mailbox) = await _kit.UserMailboxAsync();
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.organization SET maxuploadfilesize = 100");
        var m = WithAttachments(_kit.Message(_kit.Address("Ada")),
            ("fine.txt", "fine"u8.ToArray()), ("run.exe", [1, 2, 3]), ("big.bin", new byte[101]));

        (await _kit.ImportAsync(mailbox, m)).Should().Be(ImportOutcome.Created);

        var email = (await _kit.EmailsAsync()).Single();
        var names = await TestSql.RowsAsync(fx.ConnectionString, "SELECT filename FROM data.activitymimeattachment WHERE objectid = @id", ("@id", email));
        names.Select(r => (string)r[0]!).Should().Equal("fine.txt");
        var note = (await TestSql.RowsAsync(fx.ConnectionString, "SELECT notetext, createdby FROM data.annotation WHERE objectid = @id", ("@id", email)))
            .Should().ContainSingle().Subject;
        var text = (string)note[0]!;
        text.Should().Contain("run.exe").And.Contain("blocked").And.Contain("big.bin").And.Contain("101").And.NotContain("fine.txt");
        ((Guid)note[1]!).Should().Be(user);
    }

    [Fact]
    public async Task Import_TeamOwnedQueue_RunsAsTeamAdministrator_EmailOwnedByTeam()
    {
        var (admin, _) = await _kit.UserMailboxAsync("Lead");
        var team = await Admin.CreateAsync(new Entity("team")
        {
            ["name"] = "Desk team " + _kit.Tag, ["teamtype"] = new OptionSetValue(0), ["administratorid"] = new EntityReference("systemuser", admin),
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("team", team), Relationship = new Relationship("teammembership_association"),
            RelatedEntities = [new EntityReference("systemuser", admin)],
        });
        var queue = (await Admin.RetrieveAsync("team", team, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;
        await Admin.UpdateAsync(new Entity("queue", queue) { ["emailaddress"] = _kit.Address("Team") });
        var mailbox = await _kit.MailboxOfAsync("queue", queue);

        (await _kit.ImportAsync(mailbox, _kit.Message(_kit.Address("Team")))).Should().Be(ImportOutcome.Created);

        var email = await EmailAsync((await _kit.EmailsAsync()).Single());
        email.GetAttributeValue<EntityReference>("ownerid").Should().Match<EntityReference>(o => o.LogicalName == "team" && o.Id == team);
        email.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(admin);
        var item = await Admin.RetrieveAsync("queueitem", (await _kit.QueueItemsAsync(email.Id)).Single(), new ColumnSet("queueid", "createdby"));
        item.GetAttributeValue<EntityReference>("queueid").Id.Should().Be(queue);
        item.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(admin);
    }
}
