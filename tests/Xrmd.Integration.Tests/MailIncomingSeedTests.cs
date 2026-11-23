using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 1: the incoming email seed columns, the message key and mailbox sync tables (migration 0020), the key of every
/// sent email (the send job, and the startup upgrade for emails sent before the plan) and the key's insert-if-absent.
/// </summary>
[Collection("xrmd")]
public sealed class MailIncomingSeedTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private IDbConnectionFactory Factory => fx.App.Services.GetRequiredService<IDbConnectionFactory>();

    private T Column<T>(string table, string column) where T : AttributeMetadata =>
        Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == column).Should().BeOfType<T>().Subject;

    private static byte[] NewKey() => MailMessageKeys.KeyOf(Guid.NewGuid().ToString("N") + "@example.com");

    [Fact]
    public void Seed_AddsCorrelationSwitch_AndUserFiltering()
    {
        var correlation = Column<BooleanAttributeMetadata>("organization", MailSchema.CorrelationEnabledColumn);
        correlation.LogicalName.Should().Be("emailcorrelationenabled");
        correlation.DefaultValue.Should().BeTrue();

        var filtering = Column<PicklistAttributeMetadata>("usersettings", "incomingemailfilteringmethod");
        filtering.OptionSet.Options.Select(o => o.Value!.Value).Should().Equal(
            MailSchema.FilterAll, MailSchema.FilterResponses, MailSchema.FilterContactsAccounts, MailSchema.FilterEmailEnabled, MailSchema.FilterNone);
        filtering.OptionSet.Options.Select(o => o.Value!.Value).Should().Equal(0, 1, 2, 3, 4);
        filtering.OptionSet.Options.Select(o => o.Label.UserLocalizedLabel.Label).Should().Equal(
            "All", "Responses", "From contacts and accounts", "From email-enabled records", "None");
        filtering.DefaultFormValue.Should().Be(MailSchema.UserFilterDefault).And.Be(2);
        MailSchema.QueueFilterDefault.Should().Be(0);
        MailSchema.MailboxDeliveryVariable.Should().Be("xrmverse_MailboxDeliveryId");

        // Smart matching and the multiple-match setting stay unseeded (decisions 2 and 6).
        var organization = Store.GetEntity("organization")!.Attributes.Select(a => a.LogicalName).ToList();
        organization.Should().NotContain(["enablesmartmatching", "hashmaxcount", "unresolveemailaddressifmultiplematch"]);
        organization.Should().NotContain(n => n.StartsWith("hash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seed_WritesTheDefaults_OnTheOrganizationAndNewSettingsRows()
    {
        Convert.ToBoolean(await TestSql.ScalarAsync(fx.ConnectionString, $"SELECT {MailSchema.CorrelationEnabledColumn} FROM data.organization"))
            .Should().BeTrue();
        (await TestSql.CountAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.usersettings WHERE {MailSchema.FilteringMethodColumn} IS NULL"))
            .Should().Be(0, "the seeded users have the default");

        // A user created now gets the default in its new settings row.
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var root = await TestSql.ScalarAsync(fx.ConnectionString, "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL");
        var user = await admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Filter " + tag, ["domainname"] = $"filter.{tag}@example.com", ["azureactivedirectoryobjectid"] = Guid.NewGuid(),
            ["businessunitid"] = new EntityReference("businessunit", (Guid)root!),
        });
        Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString,
                $"SELECT {MailSchema.FilteringMethodColumn} FROM data.usersettings WHERE systemuserid = @id", ("@id", user)))
            .Should().Be(MailSchema.UserFilterDefault);
    }

    [Fact]
    public async Task Migration_CreatesKeyAndSyncTables()
    {
        (await TestSql.ColumnsAsync(fx.ConnectionString, "meta", "mail_message"))
            .Should().BeEquivalentTo("messagekey", "emailid", "incoming", "createdon");
        (await TestSql.ColumnsAsync(fx.ConnectionString, "meta", "mailbox_sync"))
            .Should().BeEquivalentTo("mailboxid", "cursor", "failkey", "failcount", "skipped", "deniedcount", "generation", "updatedon");
        (await TestSql.IndexExistsAsync(fx.ConnectionString, "meta", "mail_message", "IX_meta_mail_message_emailid")).Should().BeTrue();

        // The sync row's fail count starts at 0; the cursor and the failing key are free text and 32 bytes.
        var mailbox = Guid.NewGuid();
        var q = TestSql.Dialect.Quote("cursor");
        try
        {
            await TestSql.ExecAsync(fx.ConnectionString, $"INSERT INTO meta.mailbox_sync (mailboxid, {q}, failkey) VALUES (@id, @cursor, @key)",
                ("@id", mailbox), ("@cursor", new string('c', 5000)), ("@key", NewKey()));
            (await TestSql.ScalarAsync(fx.ConnectionString, "SELECT failcount FROM meta.mailbox_sync WHERE mailboxid = @id", ("@id", mailbox)))
                .Should().BeOneOf(0, 0L);
            // Final review M2 and M4: the generation and the count of refusals for a privilege start at 0 too.
            (await TestSql.ScalarAsync(fx.ConnectionString, "SELECT generation + deniedcount FROM meta.mailbox_sync WHERE mailboxid = @id", ("@id", mailbox)))
                .Should().BeOneOf(0, 0L);
            var duplicate = () => TestSql.ExecAsync(fx.ConnectionString, "INSERT INTO meta.mailbox_sync (mailboxid) VALUES (@id)", ("@id", mailbox));
            await duplicate.Should().ThrowAsync<Exception>("the mailbox id is the primary key");
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.mailbox_sync WHERE mailboxid = @id", ("@id", mailbox));
        }
    }

    [Fact]
    public async Task TryRegister_And_Find()
    {
        var key = NewKey();
        var email = Guid.NewGuid();
        await using var conn = await Factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var s = new DataSession(conn, tx);

        (await MailMessageKeys.FindAsync(s, key, default)).Should().BeNull();
        (await MailMessageKeys.TryRegisterAsync(s, key, email, incoming: true, default)).Should().BeNull("the key was absent");
        (await MailMessageKeys.TryRegisterAsync(s, key, Guid.NewGuid(), incoming: false, default)).Should().Be(email, "the owner is read back");
        (await MailMessageKeys.FindAsync(s, key, default)).Should().Be((email, true));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task TryRegister_ParallelSameKey_OneInserted_OtherReadsOwner()
    {
        var key = NewKey();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await using var connA = await Factory.OpenAsync();
            await using var txA = await connA.BeginTransactionAsync();
            var a = new DataSession(connA, txA);
            (await MailMessageKeys.TryRegisterAsync(a, key, first, incoming: true, default)).Should().BeNull();

            // The second session registers the same key while the first one's insert is not committed yet: it waits for it.
            var other = Task.Run(async () =>
            {
                await using var connB = await Factory.OpenAsync();
                await using var txB = await connB.BeginTransactionAsync();
                var b = new DataSession(connB, txB);
                var owner = await MailMessageKeys.TryRegisterAsync(b, key, second, incoming: true, default);
                // The transaction is not aborted: it reads and commits.
                var found = await MailMessageKeys.FindAsync(b, key, default);
                await txB.CommitAsync();
                return (owner, found);
            });
            await Task.Delay(500);
            other.IsCompleted.Should().BeFalse("the second session waits for the first one's outcome");
            (await MailMessageKeys.FindAsync(a, key, default)).Should().Be((first, true), "the first transaction still runs");
            await txA.CommitAsync();

            var (owner, found) = await other.WaitAsync(TimeSpan.FromSeconds(30));
            owner.Should().Be(first);
            found.Should().Be((first, true));
            (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_message WHERE messagekey = @k", ("@k", key))).Should().Be(1);
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.mail_message WHERE messagekey = @k", ("@k", key));
        }
    }

    [Fact]
    public async Task TryRegister_ParallelSameKey_BothAtOnce_OneInserted()
    {
        var key = NewKey();
        try
        {
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var results = await Task.WhenAll(ids.Select(id => Task.Run(async () =>
            {
                await using var conn = await Factory.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();
                var s = new DataSession(conn, tx);
                var owner = await MailMessageKeys.TryRegisterAsync(s, key, id, incoming: false, default);
                await MailMessageKeys.FindAsync(s, key, default);
                await tx.CommitAsync();
                return (id, owner);
            })));

            results.Count(r => r.owner is null).Should().Be(1);
            var winner = results.Single(r => r.owner is null).id;
            results.Single(r => r.owner is not null).owner.Should().Be(winner);
            (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_message WHERE messagekey = @k", ("@k", key))).Should().Be(1);
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.mail_message WHERE messagekey = @k", ("@k", key));
        }
    }

    [Fact]
    public async Task EmailDelete_RemovesKeyRow()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var email = await admin.CreateAsync(new Entity("email") { ["subject"] = "Keyed " + Guid.NewGuid().ToString("N")[..8] });
        var kept = Guid.NewGuid();
        var (key, otherKey) = (NewKey(), NewKey());
        await using (var conn = await Factory.OpenAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var s = new DataSession(conn, tx);
            await MailMessageKeys.TryRegisterAsync(s, key, email, incoming: true, default);
            await MailMessageKeys.TryRegisterAsync(s, otherKey, kept, incoming: true, default);
            await tx.CommitAsync();
        }

        await admin.DeleteAsync("email", email);

        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_message WHERE emailid = @id", ("@id", email))).Should().Be(0);
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_message WHERE emailid = @id", ("@id", kept))).Should().Be(1);
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.mail_message WHERE emailid = @id", ("@id", kept));
    }

    // ---- the startup upgrade ----

    private static Task StartAsync(string connectionString, SeedOptions seed) => Task.Run(async () =>
    {
        await using var services = OrganizationDatabase.BuildServices(connectionString, TestProvider.Current);
        await OrganizationDatabase.PopulateAsync(services, seed);
    });

    [Fact]
    public async Task Startup_OfAnOrganizationWithoutTheColumns_WritesTheDefaults()
    {
        var database = "xrmd_old_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        var seed = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        try
        {
            await StartAsync(connectionString, seed);
            // Rows made before the columns: both empty, the step not recorded.
            await TestSql.ExecAsync(connectionString, $"UPDATE data.organization SET {MailSchema.CorrelationEnabledColumn} = NULL");
            await TestSql.ExecAsync(connectionString, $"UPDATE data.usersettings SET {MailSchema.FilteringMethodColumn} = NULL");
            await TestSql.ExecAsync(connectionString, "DELETE FROM meta.upgrade_step WHERE name = @n", ("@n", SystemSeed.IncomingEmailFilteringDefaultStep));

            await StartAsync(connectionString, seed);

            Convert.ToBoolean(await TestSql.ScalarAsync(connectionString, $"SELECT {MailSchema.CorrelationEnabledColumn} FROM data.organization")).Should().BeTrue();
            (await TestSql.CountAsync(connectionString, "SELECT COUNT(*) FROM data.usersettings")).Should().BeGreaterThan(0);
            (await TestSql.CountAsync(connectionString,
                    $"SELECT COUNT(*) FROM data.usersettings WHERE {MailSchema.FilteringMethodColumn} IS NULL OR {MailSchema.FilteringMethodColumn} <> {MailSchema.UserFilterDefault}"))
                .Should().Be(0);

            // The filtering default is written once: a value cleared afterwards stays cleared; the correlation switch is set at every start.
            await TestSql.ExecAsync(connectionString, $"UPDATE data.organization SET {MailSchema.CorrelationEnabledColumn} = NULL");
            await TestSql.ExecAsync(connectionString, $"UPDATE data.usersettings SET {MailSchema.FilteringMethodColumn} = NULL");
            await StartAsync(connectionString, seed);
            Convert.ToBoolean(await TestSql.ScalarAsync(connectionString, $"SELECT {MailSchema.CorrelationEnabledColumn} FROM data.organization")).Should().BeTrue();
            (await TestSql.CountAsync(connectionString, $"SELECT COUNT(*) FROM data.usersettings WHERE {MailSchema.FilteringMethodColumn} IS NOT NULL"))
                .Should().Be(0);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }

    [Fact]
    public async Task Upgrade_RegistersKeysOfSentEmails_Once()
    {
        var database = "xrmd_old_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        var seed = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        try
        {
            await StartAsync(connectionString, seed);
            (await TestSql.CountAsync(connectionString, "SELECT COUNT(*) FROM meta.upgrade_step WHERE name = @n", ("@n", IncomingMailSettingsUpgrade.Step)))
                .Should().Be(1, "a new organization records the step at its first start");

            // An organization from before the plan: sent emails with and without a message id, a draft with one; no key, no step.
            var (sent, blank, draft) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            foreach (var (id, state, status, messageId) in new[]
                     { (sent, 1, MailSchema.EmailSent, (string?)" <sent.1@example.com> "), (blank, 1, MailSchema.EmailSent, "  "), (draft, 0, MailSchema.EmailDraft, "<draft.1@example.com>") })
                await TestSql.ExecAsync(connectionString,
                    $"INSERT INTO data.email (activityid, subject, statecode, statuscode, directioncode, messageid) VALUES (@id, @id2, @state, @status, {TestSql.True}, @m)",
                    ("@id", id), ("@id2", id.ToString()), ("@state", state), ("@status", status), ("@m", messageId));
            await TestSql.ExecAsync(connectionString, "DELETE FROM meta.upgrade_step WHERE name = @n", ("@n", IncomingMailSettingsUpgrade.Step));

            await StartAsync(connectionString, seed);

            var rows = await TestSql.RowsAsync(connectionString, "SELECT messagekey, emailid, incoming, createdon FROM meta.mail_message");
            var row = rows.Should().ContainSingle("only the sent email with a message id gets a key").Subject;
            ((byte[])row[0]!).Should().Equal(MailMessageKeys.KeyOf("sent.1@example.com"));
            row[1].Should().Be(sent);
            Convert.ToBoolean(row[2]).Should().BeFalse("an email sent from here is outgoing");
            row[3].Should().NotBeNull();

            // The second start writes nothing: the step is recorded, so even a key removed since is not written again.
            await TestSql.ExecAsync(connectionString, "DELETE FROM meta.mail_message");
            await StartAsync(connectionString, seed);
            (await TestSql.CountAsync(connectionString, "SELECT COUNT(*) FROM meta.mail_message")).Should().Be(0);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }
}
