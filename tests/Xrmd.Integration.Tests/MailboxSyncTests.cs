using System.Text;
using FluentAssertions;
using MailKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 9: <see cref="MailboxSyncJob"/> runs one pass over a mailbox's inbox (GreenMail's IMAP, one container for the class
/// and a fresh user per test; a provider-level fake where a failure must be forced), imports each message, keeps its cursor and the count
/// of a message's failures, and suspends itself until the next interval; <see cref="MailboxSyncJob.EnsureScheduledAsync"/> and the host
/// start queue one job per mailbox. The jobs run directly, as the worker runs them.
/// </summary>
[Collection("xrmd")]
public sealed class MailboxSyncTests(XrmdFixture fx, GreenMailFixture mail) : IClassFixture<GreenMailFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IncomingKit _kit = new(fx, Guid.NewGuid().ToString("N")[..8]);
    private GreenMail Server => mail.Server;
    private OrganizationService Admin => _kit.Admin;

    public async Task InitializeAsync()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.asyncoperation WHERE operationtype = {AsyncOperationQueue.MailboxSync}");
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.asyncoperation WHERE operationtype = {AsyncOperationQueue.MailboxSync}");
    }

    // ---- set-up ----

    private sealed record Box(Guid User, Guid Mailbox, MailUser Inbox);

    /// <summary>
    /// A Basic User's mailbox that can receive: approved, on a profile that reads <paramref name="inbox"/> (a new GreenMail user) with the
    /// credentials in the profile, incoming method Server-Side Synchronization, enabled, the incoming test a Success, the filter All, and
    /// <c>processemailreceivedafter</c> an hour ago (unless <paramref name="receivedAfter"/> says otherwise; null leaves it empty).
    /// </summary>
    private async Task<Box> ReceivingAsync(string name = "Ada", MailUser? inbox = null, string? password = null, bool basicUser = true,
        DateTime? receivedAfter = default, bool keepReceivedAfterNull = false)
    {
        inbox ??= await Server.NewUserAsync();
        Guid user, mailbox;
        if (basicUser)
        {
            (user, mailbox) = await _kit.UserMailboxAsync(name);
        }
        else
        {
            // A user without any role: the pipeline refuses its email Create (403).
            user = await Admin.CreateAsync(new Entity("systemuser")
            {
                ["fullname"] = $"{name} {_kit.Tag}", ["domainname"] = $"{_kit.Tag}.{Guid.NewGuid():N}@example.com",
                ["azureactivedirectoryobjectid"] = Guid.NewGuid(), ["internalemailaddress"] = _kit.Address(name),
                ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
            });
            mailbox = await _kit.MailboxOfAsync("systemuser", user);
        }
        await Admin.UpdateAsync(new Entity("systemuser", user) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        var profile = await Admin.CreateAsync(new Entity("emailserverprofile")
        {
            ["name"] = $"Inbox {_kit.Tag} {Guid.NewGuid():N}", ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
            ["outgoingauthenticationprotocol"] = new OptionSetValue(3), ["outgoingcredentialretrieval"] = new OptionSetValue(0),
            ["incomingserverlocation"] = Server.Hostname, ["incomingportnumber"] = Server.ImapPort, ["incomingusessl"] = false,
            ["incomingcredentialretrieval"] = new OptionSetValue(1), ["incomingusername"] = inbox.Login,
            ["incomingpassword"] = password ?? inbox.Password,
        });
        await Admin.UpdateAsync(new Entity("mailbox", mailbox)
        {
            ["emailserverprofile"] = new EntityReference("emailserverprofile", profile), ["incomingemaildeliverymethod"] = new OptionSetValue(2),
            ["enabledforincomingemail"] = true,
        });
        var after = keepReceivedAfterNull ? (DateTime?)null : receivedAfter ?? DateTime.UtcNow.AddHours(-1);
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.mailbox SET incomingemailstatus = 1, processemailreceivedafter = @after WHERE mailboxid = @id",
            ("@after", (object?)after ?? DBNull.Value), ("@id", mailbox));
        await TestSql.ExecAsync(fx.ConnectionString,
            $"UPDATE data.usersettings SET {MailSchema.FilteringMethodColumn} = {MailSchema.FilterAll} WHERE systemuserid = @id", ("@id", user));
        return new Box(user, mailbox, inbox);
    }

    /// <summary>A message to the box from sender@example.com, with subject <paramref name="subject"/> plus the tag; <paramref name="messageId"/> "" has none.</summary>
    private MimeMessage Message(string subject, string? messageId = null) => _kit.Message(_kit.Address("Ada"), messageId, subject);

    private Task AppendAsync(Box box, MimeMessage m, DateTimeOffset? receivedOn = null) =>
        Server.AppendAsync(box.Inbox, m, receivedOn ?? DateTimeOffset.UtcNow.AddMinutes(-5));

    private static MailProviders Providers(IIncomingMailProvider? imap = null) =>
        new(new NoSend(), new NoSend(), imap ?? new ImapMailProvider(allowInsecure: true));

    /// <summary>Runs one pass of the sync job of <paramref name="box"/> as the worker does: as the delivery user.</summary>
    private async Task<AsyncOperationRun> PassAsync(Box box, IIncomingMailProvider? imap = null, int batchSize = 50)
    {
        var (_, user) = await _kit.ReceiverAsync(box.Mailbox);
        using var scope = fx.App.Services.CreateScope();
        var job = new MailboxSyncJob(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), scope.ServiceProvider.GetRequiredService<CoreOperations>(),
            Providers(imap), _kit.Importer(scope), new MailboxSyncOptions { IncomingSyncInterval = Interval, IncomingBatchSize = batchSize });
        var run = new AsyncOperationRun(Guid.NewGuid(), user, user.UserId, MailboxSyncJob.DataOf(box.Mailbox));
        await job.RunAsync(run, CancellationToken.None);
        return run;
    }

    private async Task<object?> MailboxColumnAsync(Guid mailbox, string column) =>
        await TestSql.ScalarAsync(fx.ConnectionString, $"SELECT {column} FROM data.mailbox WHERE mailboxid = @id", ("@id", mailbox));

    private async Task<MailboxSyncState> StateAsync(Guid mailbox)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await MailboxSyncState.ReadAsync(new DataSession(conn, null), mailbox, default);
    }

    private async Task SetStateAsync(Guid mailbox, MailboxSyncState state)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await MailboxSyncState.WriteAsync(new DataSession(conn, null), mailbox, state, default);
    }

    private static void ShouldSuspendUntilNextInterval(AsyncOperationRun run, Guid mailbox)
    {
        run.Suspension.Should().NotBeNull("the job waits for its next pass");
        run.Suspension!.Value.Until.Should().Be(run.Now + Interval);
        run.Suspension.Value.Data.Should().Be(MailboxSyncJob.DataOf(mailbox));
    }

    private sealed record JobRow(Guid Id, int State, string? Data, Guid Owner, string? Table);

    private async Task<List<JobRow>> JobsAsync(Guid mailbox) =>
        [.. (await TestSql.RowsAsync(fx.ConnectionString,
                "SELECT asyncoperationid, statecode, data, ownerid, primaryentitytype FROM data.asyncoperation " +
                $"WHERE regardingobjectid = @id AND operationtype = {AsyncOperationQueue.MailboxSync} ORDER BY createdon", ("@id", mailbox)))
            .Select(r => new JobRow((Guid)r[0]!, Convert.ToInt32(r[1]), r[2] as string, (Guid)r[3]!, r[4] as string))];

    private async Task EnsureScheduledAsync(Guid mailbox)
    {
        await MailConnections.InTransactionAsync(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), async s =>
        {
            await MailboxSyncJob.EnsureScheduledAsync(s, fx.App.Services.GetRequiredService<AsyncOperationQueue>(), mailbox, default);
            return true;
        }, default);
    }

    // ---- fakes ----

    private sealed class NoSend : IMailProvider
    {
        public Task SendAsync(MimeMessage message, MailProfile profile, MailCredentials credentials, CancellationToken ct) =>
            throw new InvalidOperationException("A sync sends nothing.");

        public Task TestAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct) =>
            throw new InvalidOperationException("A sync tests nothing.");
    }

    /// <summary>The real IMAP provider with hooks: a failure at Open, before a fetch, and listings without resume points.</summary>
    private sealed class HookedImap : IIncomingMailProvider
    {
        private readonly ImapMailProvider _inner = new(allowInsecure: true);

        public Exception? OpenFailure { get; set; }

        /// <summary>Called before each fetch with the fetch's number in the session (1 for the first).</summary>
        public Func<int, IncomingRef, Exception?>? BeforeFetch { get; set; }

        /// <summary>True: no item is a resume point (as a Graph page), so the cursor moves only at the end of the listing.</summary>
        public bool NoResumePoints { get; set; }

        public async Task<IIncomingSession> OpenAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct)
        {
            if (OpenFailure is not null) throw OpenFailure;
            return new Session(await _inner.OpenAsync(address, profile, credentials, ct), this);
        }

        private sealed class Session(IIncomingSession inner, HookedImap hooks) : IIncomingSession
        {
            private int _fetches;

            public async Task<IncomingBatch> ListAsync(string? cursor, DateTime since, int max, CancellationToken ct)
            {
                var batch = await inner.ListAsync(cursor, since, max, ct);
                return hooks.NoResumePoints ? batch with { Items = [.. batch.Items.Select(i => i with { CursorAfter = null })] } : batch;
            }

            public Task<MimeMessage?> FetchAsync(IncomingRef item, CancellationToken ct) =>
                hooks.BeforeFetch?.Invoke(++_fetches, item) is { } failure ? Task.FromException<MimeMessage?>(failure) : inner.FetchAsync(item, ct);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    // ---- one pass ----

    [Fact]
    public async Task Pass_ImportsNewMessages_AndSuspendsUntilNextInterval()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("First"));
        await AppendAsync(box, Message("Second"));
        var before = DateTime.UtcNow.AddSeconds(-5);
        using (var scope = fx.App.Services.CreateScope())
            scope.ServiceProvider.GetServices<IAsyncOperationHandler>().Should().ContainSingle(h => h.OperationType == AsyncOperationQueue.MailboxSync)
                .Which.Should().BeOfType<MailboxSyncJob>().Which.RestartsWhenInterrupted.Should().BeTrue();

        var run = await PassAsync(box);

        ShouldSuspendUntilNextInterval(run, box.Mailbox);
        var first = (await _kit.EmailsAsync("First")).Should().ContainSingle().Subject;
        (await _kit.EmailsAsync("Second")).Should().ContainSingle();
        var email = await Admin.RetrieveAsync("email", first, new Microsoft.Xrm.Sdk.Query.ColumnSet("ownerid", "directioncode"));
        email.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(box.User);
        email.GetAttributeValue<bool>("directioncode").Should().BeFalse();
        ((DateTime)(await MailboxColumnAsync(box.Mailbox, "lastsuccessfulsynccompletedon"))!).Should().BeAfter(before);
        var state = await StateAsync(box.Mailbox);
        state.Cursor.Should().StartWith("imap:");
        state.FailCount.Should().Be(0);

        // The next pass resumes after the cursor: nothing again, and a new message only.
        await AppendAsync(box, Message("Third"));
        ShouldSuspendUntilNextInterval(await PassAsync(box), box.Mailbox);
        (await _kit.EmailsAsync("First")).Should().ContainSingle();
        (await _kit.EmailsAsync("Third")).Should().ContainSingle();

        // A batch smaller than the inbox: the job runs again at once.
        await AppendAsync(box, Message("Fourth"));
        await AppendAsync(box, Message("Fifth"));
        var partial = await PassAsync(box, batchSize: 1);
        partial.Suspension!.Value.Until.Should().Be(partial.Now, "the server has more");
        (await _kit.EmailsAsync("Fourth")).Should().ContainSingle();
        (await _kit.EmailsAsync("Fifth")).Should().BeEmpty();
    }

    [Fact]
    public async Task Pass_InterruptedAfterCommit_DoesNotReimport()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("First"));
        await AppendAsync(box, Message("Second"));
        // The first message is imported, then the pass stops before any cursor is stored (no item is a resume point).
        var imap = new HookedImap { NoResumePoints = true, BeforeFetch = (n, _) => n == 2 ? new InvalidOperationException("The pass was interrupted.") : null };

        var interrupted = await PassAsync(box, imap);

        ShouldSuspendUntilNextInterval(interrupted, box.Mailbox);
        (await _kit.EmailsAsync("First")).Should().ContainSingle();
        (await _kit.EmailsAsync("Second")).Should().BeEmpty();
        (await StateAsync(box.Mailbox)).Cursor.Should().BeNull("the cursor never moved");

        imap.BeforeFetch = null;
        await PassAsync(box, imap);

        (await _kit.EmailsAsync("First")).Should().ContainSingle("its key makes the rerun a duplicate");
        (await _kit.EmailsAsync("Second")).Should().ContainSingle();
        (await StateAsync(box.Mailbox)).FailCount.Should().Be(0);
    }

    [Fact]
    public async Task Pass_PoisonMessage_SkippedAfterThreeAttempts()
    {
        var box = await ReceivingAsync();
        var poison = Message("Poison");
        var builder = new BodyBuilder { HtmlBody = "<p>Files</p>" };
        builder.Attachments.Add("report.txt", Encoding.UTF8.GetBytes("the report"), ContentType.Parse("text/plain"));
        poison.Body = builder.ToMessageBody();
        await AppendAsync(box, poison);
        await AppendAsync(box, Message("After"));
        // A plug-in refuses every attachment: only the poison message has one.
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        await PluginTestHelper.RegisterStepAsync(Admin, await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin"),
            "Create", MailSchema.Attachment, 20, config: "invalid:attachments are refused here");

        for (var pass = 1; pass <= 2; pass++)
        {
            ShouldSuspendUntilNextInterval(await PassAsync(box), box.Mailbox);
            var state = await StateAsync(box.Mailbox);
            state.FailCount.Should().Be(pass);
            state.FailKey.Should().Equal(MailMessageKeys.KeyOf(poison.MessageId!));
            state.Cursor.Should().BeNull("the pass stops at the failing message");
            (await _kit.EmailsAsync("After")).Should().BeEmpty();
            ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain("attachments are refused here");
        }

        ShouldSuspendUntilNextInterval(await PassAsync(box), box.Mailbox);

        (await _kit.EmailsAsync("Poison")).Should().BeEmpty();
        (await _kit.EmailsAsync("After")).Should().ContainSingle("the third pass skips the poison message and goes on");
        var final = await StateAsync(box.Mailbox);
        final.FailCount.Should().Be(0);
        final.FailKey.Should().BeNull();
        final.Cursor.Should().NotBeNull();
        var error = (string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!;
        error.Should().Contain($"<{poison.MessageId}>").And.Contain("attachments are refused here");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "lastsyncerrorcount")).Should().Be(0, "a successful pass resets the count, not the text");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(1, "a message's failure is not the mailbox's");
    }

    /// <summary>Adds a <c>ThrowPlugin</c> on the Create of an attachment: only the messages with an attachment fail.</summary>
    private async Task RefuseAttachmentsAsync()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        await PluginTestHelper.RegisterStepAsync(Admin, await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin"),
            "Create", MailSchema.Attachment, 20, config: "invalid:attachments are refused here");
    }

    private MimeMessage WithAttachment(string subject)
    {
        var m = Message(subject);
        var builder = new BodyBuilder { HtmlBody = "<p>Files</p>" };
        builder.Attachments.Add("report.txt", Encoding.UTF8.GetBytes("the report"), ContentType.Parse("text/plain"));
        m.Body = builder.ToMessageBody();
        return m;
    }

    [Fact]
    public async Task Pass_TwoPoisonMessages_WithoutResumePoints_DoNotStall()
    {
        // Ruling T9-a: a listing without resume points (as a Graph page) lists a skipped message again until the cursor moves past the
        // whole page; the skipped keys keep two poison messages from resetting each other's count forever.
        var box = await ReceivingAsync();
        await AppendAsync(box, WithAttachment("PoisonA"));
        await AppendAsync(box, WithAttachment("PoisonB"));
        await AppendAsync(box, Message("Good"));
        await RefuseAttachmentsAsync();
        var imap = new HookedImap { NoResumePoints = true };

        var passes = 0;
        while (passes < 6 && (await _kit.EmailsAsync("Good")).Count == 0)
        {
            passes++;
            await PassAsync(box, imap);
            if (passes < 6 && (await _kit.EmailsAsync("Good")).Count == 0)
                (await StateAsync(box.Mailbox)).Cursor.Should().BeNull("the cursor moves only at the end of the listing");
        }

        (await _kit.EmailsAsync("Good")).Should().ContainSingle("the good message is imported within 6 passes");
        passes.Should().Be(5);
        (await _kit.EmailsAsync("PoisonA")).Should().BeEmpty();
        (await _kit.EmailsAsync("PoisonB")).Should().BeEmpty();
        var state = await StateAsync(box.Mailbox);
        state.Cursor.Should().NotBeNull("the cursor moved past the page");
        state.Skipped.Should().BeNull("a cursor that moves forgets the skipped keys");
        state.FailKey.Should().BeNull();
    }

    [Fact]
    public async Task Pass_MalformedSkipped_TreatedAsNone()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("Garbage"));
        await SetStateAsync(box.Mailbox, MailboxSyncState.Empty);
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE meta.mailbox_sync SET skipped = 'not json [' WHERE mailboxid = @id", ("@id", box.Mailbox));

        (await StateAsync(box.Mailbox)).Skipped.Should().BeNull();
        ShouldSuspendUntilNextInterval(await PassAsync(box), box.Mailbox);

        (await _kit.EmailsAsync("Garbage")).Should().ContainSingle();
        (await StateAsync(box.Mailbox)).Cursor.Should().NotBeNull();
    }

    [Fact]
    public async Task Pass_SettingsResetDuringPass_DoesNotWriteStaleCursor()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("Racing"));
        // The incoming settings change while the pass fetches: the status goes back to Not Run and the sync state is forgotten.
        var imap = new HookedImap
        {
            BeforeFetch = (_, _) =>
            {
                TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET incomingemailstatus = 0 WHERE mailboxid = @id", ("@id", box.Mailbox))
                    .GetAwaiter().GetResult();
                return null;
            },
        };

        var run = await PassAsync(box, imap);

        run.Suspension.Should().BeNull("the job ends");
        run.CompletionMessage.Should().Contain("incoming settings changed");
        // The profile's set-up reset the state once (generation 1); the pass wrote nothing over it.
        (await StateAsync(box.Mailbox)).Should().BeEquivalentTo(MailboxSyncState.Empty with { Generation = 1 }, "no stale cursor is written back");
    }

    /// <summary>
    /// Final review M2: the settings are reset and a test succeeds while a pass still runs (the status is Success again): the pass sees the
    /// new generation of the state, writes nothing and ends.
    /// </summary>
    [Fact]
    public async Task Pass_ResetAndRetestDuringPass_DoesNotWriteStaleCursor()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("Overtaken"));
        await SetStateAsync(box.Mailbox, new MailboxSyncState("imap:1:999999", null, 0));
        var imap = new HookedImap
        {
            BeforeFetch = (_, _) =>
            {
                ResetAsync(box.Mailbox).GetAwaiter().GetResult();
                return null;
            },
        };

        var run = await PassAsync(box, imap);

        run.Suspension.Should().BeNull("the job ends");
        run.CompletionMessage.Should().Contain("incoming settings changed");
        var state = await StateAsync(box.Mailbox);
        state.Cursor.Should().BeNull("no stale cursor is written back");
        state.Generation.Should().Be(1);
    }

    /// <summary>The settings reset (as the mailbox Update does it) followed by a successful test, in one go.</summary>
    private async Task ResetAsync(Guid mailbox)
    {
        await MailConnections.InTransactionAsync(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), async s =>
        {
            await MailboxSyncState.ResetAsync(s, mailbox, default);
            return true;
        }, default);
    }

    /// <summary>
    /// Final review I3: every path that sends the incoming status back to Not Run resets the sync state: the address of the user or of the
    /// queue, and the incoming delivery method set on the owner.
    /// </summary>
    [Theory]
    [InlineData("user-address")]
    [InlineData("user-method")]
    [InlineData("queue-address")]
    public async Task OwnerChange_BackToNotRun_ResetsSyncState(string change)
    {
        Guid mailbox;
        Entity update;
        if (change.StartsWith("queue", StringComparison.Ordinal))
        {
            var (queue, queueMailbox, _) = await _kit.QueueMailboxAsync();
            mailbox = queueMailbox;
            update = new Entity("queue", queue) { ["emailaddress"] = $"moved.{Guid.NewGuid():N}@example.com" };
        }
        else
        {
            var box = await ReceivingAsync();
            mailbox = box.Mailbox;
            update = change == "user-address"
                ? new Entity("systemuser", box.User) { ["internalemailaddress"] = $"moved.{Guid.NewGuid():N}@example.com" }
                : new Entity("systemuser", box.User) { ["incomingemaildeliverymethod"] = new OptionSetValue(2) };
        }
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET incomingemailstatus = 1 WHERE mailboxid = @id", ("@id", mailbox));
        await SetStateAsync(mailbox, new MailboxSyncState("imap:7:42", MailMessageKeys.KeyOf("x@example.com"), 2, ["ab"]));

        await Admin.UpdateAsync(update);

        Convert.ToInt32(await MailboxColumnAsync(mailbox, "incomingemailstatus")).Should().Be(0);
        var state = await StateAsync(mailbox);
        state.Cursor.Should().BeNull("the old address's cursor must not be resumed");
        state.FailKey.Should().BeNull();
        state.FailCount.Should().Be(0);
        state.Skipped.Should().BeNull();
        state.Generation.Should().Be(1, "a pass still running sees the reset");
    }

    [Fact]
    public async Task Startup_RefusesTooShortInterval_AndBatchSizeZero()
    {
        foreach (var (key, value, refusal) in new[]
                 {
                     ("Mail:IncomingSyncInterval", "00:00:05", Xrmd.Host.XrmdHost.IncomingSyncIntervalTooShort),
                     ("Mail:IncomingBatchSize", "0", Xrmd.Host.XrmdHost.IncomingBatchSizeOutOfRange),
                 })
        {
            await using var app = fx.WithOwnDatabase(b => b.UseSetting(key, value));
            var act = () => app.Services;
            var thrown = act.Should().Throw<Exception>().Which;
            Messages(thrown).Should().Contain(m => m.Contains(refusal, StringComparison.Ordinal), $"{key} = {value} is refused");
        }

        static IEnumerable<string> Messages(Exception? e)
        {
            for (; e is not null; e = e.InnerException) yield return e.Message;
        }
    }

    [Fact]
    public async Task Pass_TransientInfrastructureError_NotCountedAsPoison()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("Blip"));
        var imap = new HookedImap { BeforeFetch = (_, _) => new BlinkingDbException("The database blinked.") };

        for (var pass = 1; pass <= 3; pass++)
        {
            ShouldSuspendUntilNextInterval(await PassAsync(box, imap), box.Mailbox);
            var state = await StateAsync(box.Mailbox);
            state.FailCount.Should().Be(0, "ruling PF11: only the pipeline's own failures count");
            state.FailKey.Should().BeNull();
            (await _kit.EmailsAsync("Blip")).Should().BeEmpty();
        }
        ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain("The database blinked.");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "lastsyncerrorcount")).Should().Be(3);
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(1);

        imap.BeforeFetch = null;
        await PassAsync(box, imap);

        (await _kit.EmailsAsync("Blip")).Should().ContainSingle("the message is imported once the error clears");
    }

    /// <summary>A database failure, as a driver throws it.</summary>
    private sealed class BlinkingDbException(string message) : System.Data.Common.DbException(message);

    /// <summary>
    /// Final review I2: a failure that belongs to one message (the server refuses its fetch, it cannot be parsed, a non-database
    /// exception for it) counts against that message: the message is skipped at the third pass and the next one comes in.
    /// </summary>
    [Theory]
    [InlineData("imap-no")]
    [InlineData("graph-4xx")]
    [InlineData("unparsable")]
    [InlineData("unexpected")]
    public async Task Pass_MessageOnlyFailure_CountsAsPoison_SkippedAtThirdPass(string kind)
    {
        var box = await ReceivingAsync();
        var broken = Message("Broken");
        await AppendAsync(box, broken);
        await AppendAsync(box, Message("Behind"));
        Exception Failure() => kind switch
        {
            "imap-no" => ImapMailProvider.MapFetch(new MailKit.Net.Imap.ImapCommandException(MailKit.Net.Imap.ImapCommandResponse.No, "The message is damaged.")),
            "graph-4xx" => new MailSendException("Microsoft Graph answered 422: ErrorCorruptData.", false) { Scope = MailFailureScope.Message },
            "unparsable" => new FormatException("Not MIME."),
            _ => new ArgumentException("Something about this message."),
        };
        var imap = new HookedImap
        {
            BeforeFetch = (_, item) => MailMessageKeys.Normalize(item.MessageId) == broken.MessageId ? Failure() : null,
        };

        for (var pass = 1; pass <= 2; pass++)
        {
            ShouldSuspendUntilNextInterval(await PassAsync(box, imap), box.Mailbox);
            var state = await StateAsync(box.Mailbox);
            state.FailCount.Should().Be(pass);
            state.FailKey.Should().Equal(MailMessageKeys.KeyOf(broken.MessageId!));
            (await _kit.EmailsAsync("Behind")).Should().BeEmpty("the pass stops at the failing message");
            Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(1, "a message's failure is not the mailbox's");
        }

        ShouldSuspendUntilNextInterval(await PassAsync(box, imap), box.Mailbox);

        (await _kit.EmailsAsync("Behind")).Should().ContainSingle("the third pass skips the broken message and goes on");
        (await _kit.EmailsAsync("Broken")).Should().BeEmpty();
        ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain($"<{broken.MessageId}>").And.Contain("was skipped");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(1);
    }

    /// <summary>Final review I2: database, network and timeout failures and the provider's connection stay the mailbox's, never counted.</summary>
    [Fact]
    public async Task Pass_InfrastructureFailures_NeverCounted()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("Steady"));
        Exception[] failures =
        [
            new TimeoutException("The command timed out."),
            new InvalidOperationException("Wrapped.", new BlinkingDbException("The database blinked.")),
            new MailSendException("The IMAP server answered unexpectedly.", transient: true),
            new IOException("Connection reset."),
        ];
        foreach (var failure in failures)
        {
            var imap = new HookedImap { BeforeFetch = (_, _) => failure };
            ShouldSuspendUntilNextInterval(await PassAsync(box, imap), box.Mailbox);
            var state = await StateAsync(box.Mailbox);
            state.FailCount.Should().Be(0, $"{failure.GetType().Name} is not the message's");
            state.FailKey.Should().BeNull();
        }
        (await _kit.EmailsAsync("Steady")).Should().BeEmpty();
        await PassAsync(box);
        (await _kit.EmailsAsync("Steady")).Should().ContainSingle();
    }

    [Fact]
    public async Task Pass_PrivilegeFault_NotPoison_FailsMailboxAtThirdPass()
    {
        // Ruling T8-c: the delivery user may not create an email: the setup's fault, never counted as a strike against the message.
        // Final review M4: the same refusal three passes in a row sets the mailbox's incoming status Failure, so the admin sees it.
        var box = await ReceivingAsync("Norole", basicUser: false);
        await AppendAsync(box, Message("Denied"));

        for (var pass = 1; pass <= 2; pass++)
        {
            ShouldSuspendUntilNextInterval(await PassAsync(box), box.Mailbox);
            var state = await StateAsync(box.Mailbox);
            state.FailCount.Should().Be(0);
            state.DeniedCount.Should().Be(pass);
            state.Cursor.Should().BeNull("the cursor stays before the message");
            ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain("cannot be imported");
            Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(1);
        }

        var third = await PassAsync(box);

        third.Suspension.Should().BeNull("a failed mailbox ends its job");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(2);
        ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain("missing privilege").And.Contain("3 passes in a row");
        var after = await StateAsync(box.Mailbox);
        after.DeniedCount.Should().Be(0);
        after.FailCount.Should().Be(0);
        after.Cursor.Should().BeNull("the message was never skipped");

        // The admin fixes the role and tests the mailbox again: the message comes in.
        await AssignRoleAsync(fx, box.User, StandardRoles.BasicUser);
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET incomingemailstatus = 1 WHERE mailboxid = @id", ("@id", box.Mailbox));
        await PassAsync(box);

        (await _kit.EmailsAsync("Denied")).Should().ContainSingle("the message was never skipped");
    }

    [Fact]
    public async Task Pass_AuthFailure_SetsIncomingFailure_AndEndsJob()
    {
        var box = await ReceivingAsync(password: "not-the-password");
        var before = DateTime.UtcNow.AddSeconds(-5);

        var run = await PassAsync(box);

        run.Suspension.Should().BeNull("a permanent failure ends the job");
        run.CompletionMessage.Should().Contain("IMAP authentication failed").And.NotContain("not-the-password");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(2);
        ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain("IMAP authentication failed");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "lastsyncerrorcount")).Should().Be(1);
        ((DateTime)(await MailboxColumnAsync(box.Mailbox, "lastsyncerroroccurredon"))!).Should().BeAfter(before);
    }

    [Fact]
    public async Task Pass_DeliveryUserDisabled_SetsIncomingFailure_AndEndsJob()
    {
        var box = await ReceivingAsync();
        var (_, user) = await _kit.ReceiverAsync(box.Mailbox);
        await TestSql.ExecAsync(fx.ConnectionString, $"UPDATE data.systemuser SET isdisabled = {(TestProvider.IsPostgres ? "true" : "1")} WHERE systemuserid = @id", ("@id", box.User));

        var run = new AsyncOperationRun(Guid.NewGuid(), user, user.UserId, MailboxSyncJob.DataOf(box.Mailbox));
        using (var scope = fx.App.Services.CreateScope())
            await new MailboxSyncJob(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), scope.ServiceProvider.GetRequiredService<CoreOperations>(),
                Providers(), _kit.Importer(scope), new MailboxSyncOptions { IncomingSyncInterval = Interval }).RunAsync(run, CancellationToken.None);

        run.Suspension.Should().BeNull();
        run.CompletionMessage.Should().Contain("disabled");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(2);
        ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain("disabled");
    }

    [Fact]
    public async Task Pass_Transient_RecordsError_KeepsStatus()
    {
        var box = await ReceivingAsync();
        var imap = new HookedImap { OpenFailure = new MailSendException("The IMAP server could not be reached: connection refused.", transient: true) };

        var run = await PassAsync(box, imap);

        ShouldSuspendUntilNextInterval(run, box.Mailbox);
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "incomingemailstatus")).Should().Be(1);
        ((string)(await MailboxColumnAsync(box.Mailbox, "lastsyncerror"))!).Should().Contain("could not be reached");
        Convert.ToInt32(await MailboxColumnAsync(box.Mailbox, "lastsyncerrorcount")).Should().Be(1);
        (await MailboxColumnAsync(box.Mailbox, "lastsyncerroroccurredon")).Should().NotBeNull();
    }

    [Fact]
    public async Task Pass_MailboxDisabledMeanwhile_EndsJob()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("Late"));
        await Admin.UpdateAsync(new Entity("mailbox", box.Mailbox) { ["enabledforincomingemail"] = false });

        var run = await PassAsync(box);

        run.Suspension.Should().BeNull();
        run.CompletionMessage.Should().Contain("cannot receive").And.Contain("enabledforincomingemail");
        (await _kit.EmailsAsync("Late")).Should().BeEmpty();

        // A deleted mailbox ends its job too, and its sync state goes with it.
        await SetStateAsync(box.Mailbox, new MailboxSyncState("imap:1:1", null, 0));
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.systemuser SET defaultmailbox = NULL WHERE systemuserid = @id", ("@id", box.User));
        await Admin.DeleteAsync("mailbox", box.Mailbox);
        (await StateAsync(box.Mailbox)).Should().BeEquivalentTo(MailboxSyncState.Empty);
        (await PassAsyncForDeleted(box)).CompletionMessage.Should().Contain("no longer exists");
    }

    private async Task<AsyncOperationRun> PassAsyncForDeleted(Box box)
    {
        using var scope = fx.App.Services.CreateScope();
        var job = new MailboxSyncJob(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), scope.ServiceProvider.GetRequiredService<CoreOperations>(),
            Providers(), _kit.Importer(scope), new MailboxSyncOptions { IncomingSyncInterval = Interval });
        var run = new AsyncOperationRun(Guid.NewGuid(), Caller.Internal, Guid.Empty, MailboxSyncJob.DataOf(box.Mailbox));
        await job.RunAsync(run, CancellationToken.None);
        run.Suspension.Should().BeNull();
        return run;
    }

    [Fact]
    public async Task Pass_MessagesBeforeReceivedAfter_Skipped()
    {
        var box = await ReceivingAsync(keepReceivedAfterNull: true);
        await AppendAsync(box, Message("Old"), DateTimeOffset.UtcNow.AddDays(-2));
        await AppendAsync(box, Message("Earlier"), DateTimeOffset.UtcNow.AddMinutes(-10));
        var before = DateTime.UtcNow.AddSeconds(-1);

        await PassAsync(box);

        var after = (DateTime)(await MailboxColumnAsync(box.Mailbox, "processemailreceivedafter"))!;
        after.Should().BeAfter(before, "an empty start is set to the first pass's time");
        (await _kit.EmailsAsync("Old")).Should().BeEmpty();
        (await _kit.EmailsAsync("Earlier")).Should().BeEmpty();

        // After the cursor: a new message comes in, one whose server date is before the start does not.
        await AppendAsync(box, Message("New"), DateTimeOffset.UtcNow.AddSeconds(5));
        await AppendAsync(box, Message("Backdated"), DateTimeOffset.UtcNow.AddDays(-1));
        await PassAsync(box);

        (await _kit.EmailsAsync("New")).Should().ContainSingle();
        (await _kit.EmailsAsync("Backdated")).Should().BeEmpty();
    }

    [Fact]
    public async Task Pass_UidValidityReset_NoDuplicates()
    {
        var box = await ReceivingAsync();
        var withId = Message("WithId");
        var withoutId = Message("WithoutId", messageId: "");
        withoutId.Headers.Contains(HeaderId.MessageId).Should().BeFalse();
        await AppendAsync(box, withId);
        await AppendAsync(box, withoutId);
        await PassAsync(box);
        (await _kit.EmailsAsync("WithId")).Should().ContainSingle();
        (await _kit.EmailsAsync("WithoutId")).Should().ContainSingle();

        // The server renumbered its folder: the cursor's UIDVALIDITY is no longer the folder's, so the listing starts again from the date.
        var cursor = (await StateAsync(box.Mailbox)).Cursor!;
        await SetStateAsync(box.Mailbox, new MailboxSyncState("imap:1:999999", null, 0));
        await PassAsync(box);

        (await _kit.EmailsAsync("WithId")).Should().ContainSingle();
        (await _kit.EmailsAsync("WithoutId")).Should().ContainSingle("the content key survives the reset");
        (await StateAsync(box.Mailbox)).Cursor.Should().Be(cursor);
    }

    [Fact]
    public async Task Pass_DoesNotChangeServerMailbox()
    {
        var box = await ReceivingAsync();
        await AppendAsync(box, Message("Keep one"));
        await AppendAsync(box, Message("Keep two"));

        await PassAsync(box);
        await PassAsync(box);

        (await _kit.EmailsAsync("Keep one")).Should().ContainSingle();
        var flags = await Server.InboxFlagsAsync(box.Inbox);
        flags.Should().HaveCount(2, "nothing is moved or deleted");
        flags.Should().OnlyContain(f => (f & (MessageFlags.Seen | MessageFlags.Deleted | MessageFlags.Answered | MessageFlags.Flagged)) == 0,
            "no flag is set");
    }

    // ---- scheduling ----

    [Fact]
    public async Task Schedule_TwiceWhileRunning_OneJob()
    {
        var box = await ReceivingAsync();

        await Task.WhenAll(EnsureScheduledAsync(box.Mailbox), EnsureScheduledAsync(box.Mailbox));
        await EnsureScheduledAsync(box.Mailbox);

        var job = (await JobsAsync(box.Mailbox)).Should().ContainSingle().Subject;
        job.Owner.Should().Be(box.User, "the job runs as the delivery user");
        job.Table.Should().Be("mailbox");
        job.Data.Should().Be(MailboxSyncJob.DataOf(box.Mailbox));

        // Running (Locked) or waiting for its next pass (Suspended): still one.
        foreach (var state in new[] { 2, 1 })
        {
            await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.asyncoperation SET statecode = @s WHERE asyncoperationid = @id", ("@s", state), ("@id", job.Id));
            await EnsureScheduledAsync(box.Mailbox);
            (await JobsAsync(box.Mailbox)).Should().ContainSingle();
        }

        // Ended: a new one.
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.asyncoperation SET statecode = 3, statuscode = 31 WHERE asyncoperationid = @id", ("@id", job.Id));
        await EnsureScheduledAsync(box.Mailbox);
        (await JobsAsync(box.Mailbox)).Should().HaveCount(2).And.ContainSingle(j => j.State == 0);
    }

    [Fact]
    public async Task Startup_SchedulesReceivableMailboxes_Once()
    {
        var first = await ReceivingAsync("Ada");
        var second = await ReceivingAsync("Bea");
        var untested = await ReceivingAsync("Cid");
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET incomingemailstatus = 0 WHERE mailboxid = @id", ("@id", untested.Mailbox));
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var queue = fx.App.Services.GetRequiredService<AsyncOperationQueue>();

        // The host's own periodic sweep (MailboxSyncStartup) runs in this fixture and may queue these mailboxes first, so the first
        // count is not asserted: the jobs below prove the scheduling, and the second call proves it is done once.
        await MailboxSyncJob.ScheduleReceivableAsync(factory, queue, null, default);
        (await MailboxSyncJob.ScheduleReceivableAsync(factory, queue, null, default)).Should().Be(0, "every receivable mailbox has its job");

        (await JobsAsync(first.Mailbox)).Should().ContainSingle().Which.Owner.Should().Be(first.User);
        (await JobsAsync(second.Mailbox)).Should().ContainSingle();
        (await JobsAsync(untested.Mailbox)).Should().BeEmpty();
    }
}
