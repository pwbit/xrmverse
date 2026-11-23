using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>
/// An organization made by an image older than plan 4.1a has no queue, queueitem, queuemembership, mailbox, emailserverprofile or
/// activitymimeattachment table and none of the lookups that point to them. The startup (seed and upgrades) must add them, and a
/// second start must write nothing. Field bug: "Lookup attribute 'systemuser.queueid' targets entity 'queue', which does not exist".
/// </summary>
[Collection("xrmd")]
public sealed class OlderOrganizationUpgradeTests(XrmdFixture fx)
{
    private static readonly string[] OldTables = ["queueitem", "queuemembership", "queue", "mailbox", "emailserverprofile", "activitymimeattachment"];

    private static readonly (string Table, string[] Columns)[] NewColumns =
    [
        ("systemuser", ["queueid", "emailrouteraccessapproval", "defaultmailbox", "incomingemaildeliverymethod", "outgoingemaildeliverymethod"]),
        ("team", ["queueid"]),
        ("email", ["inreplyto", "conversationindex", "correlationmethod", "parentactivityid", "attachmentcount", "emailsender",
            "sender", "torecipients", "deliveryattempts"]),
        ("organization", ["blockedattachments", "trackingprefix", "trackingtokenidbase", "trackingtokeniddigits", "maximumtrackingnumber",
            "nexttrackingnumber", "defaultemailserverprofileid", "allowunresolvedpartiesonemailsend"]),
    ];

    private static async Task<object?> ScalarAsync(IDbConnectionFactory factory, string sql)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task ExecuteAsync(IDbConnectionFactory factory, string sql) => await ScalarAsync(factory, sql);

    private static Task StartAsync(string connectionString, SeedOptions seed) => Task.Run(async () =>
    {
        await using var services = OrganizationDatabase.BuildServices(connectionString, TestProvider.Current);
        await OrganizationDatabase.PopulateAsync(services, seed);
    });

    /// <summary>Takes the queue and mail tables, their lookups and their metadata out of an organization seeded now.</summary>
    private static async Task MakeOlderAsync(IDbConnectionFactory factory)
    {
        foreach (var (table, columns) in NewColumns)
            foreach (var column in columns)
            {
                await ExecuteAsync(factory, $"ALTER TABLE data.{table} DROP COLUMN {column}");
                await ExecuteAsync(factory, $"DELETE FROM meta.attribute WHERE entity_logical_name = '{table}' AND logical_name = '{column}'");
            }
        // The type column of the polymorphic lookup email.emailsender is a data column only.
        await ExecuteAsync(factory, "ALTER TABLE data.email DROP COLUMN emailsendertype");
        foreach (var table in OldTables)
        {
            await ExecuteAsync(factory, $"DROP TABLE data.{table}");
            await ExecuteAsync(factory, $"DELETE FROM meta.attribute WHERE entity_logical_name = '{table}'");
            await ExecuteAsync(factory, $"DELETE FROM meta.entity WHERE logical_name = '{table}'");
            await ExecuteAsync(factory, $"DELETE FROM meta.relationship WHERE definition LIKE '%\"{table}\"%'");
        }
    }

    [Fact]
    public async Task Startup_OfAnOrganizationOlderThanQueuesAndMail_AddsTablesAndColumns_AndTheSecondStartWritesNothing()
    {
        var database = "xrmd_old_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        var factory = TestProvider.Factory(connectionString);
        var seed = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        try
        {
            await StartAsync(connectionString, seed);
            await MakeOlderAsync(factory);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM meta.entity WHERE logical_name IN ('queue', 'mailbox')")).Should().Be(0);

            await StartAsync(connectionString, seed);

            foreach (var table in OldTables)
                (await ScalarAsync(factory, $"SELECT COUNT(*) FROM meta.entity WHERE logical_name = '{table}'")).Should().Be(1, table);
            foreach (var (table, columns) in NewColumns)
                foreach (var column in columns)
                    (await ScalarAsync(factory, $"SELECT COUNT(*) FROM meta.attribute WHERE entity_logical_name = '{table}' AND logical_name = '{column}'"))
                        .Should().Be(1, $"{table}.{column}");
            // The default queue of every user, from the queue upgrade.
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser WHERE queueid IS NULL")).Should().Be(0);

            var stamp = await ScalarAsync(factory, "SELECT MAX(version_stamp) FROM meta.attribute");
            var queues = await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queue");
            var privileges = await ScalarAsync(factory, "SELECT COUNT(*) FROM data.privilege");
            var grants = await ScalarAsync(factory, "SELECT COUNT(*) FROM data.roleprivileges");
            var counter = await ScalarAsync(factory, "SELECT COUNT(*) FROM meta.attribute");

            await StartAsync(connectionString, seed);

            (await ScalarAsync(factory, "SELECT MAX(version_stamp) FROM meta.attribute")).Should().Be(stamp, "no metadata is written");
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM meta.attribute")).Should().Be(counter);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queue")).Should().Be(queues);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.privilege")).Should().Be(privileges);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.roleprivileges")).Should().Be(grants);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }

    [Fact]
    public async Task Startup_OfAnOrganizationWithoutTheMailboxTestColumn_AddsIt()
    {
        // Plan 4.1b, task 7: mailbox.testmailboxaccesscompletedon came after the mailbox table itself.
        var database = "xrmd_old_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        var factory = TestProvider.Factory(connectionString);
        var seed = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        try
        {
            await StartAsync(connectionString, seed);
            await ExecuteAsync(factory, "ALTER TABLE data.mailbox DROP COLUMN testmailboxaccesscompletedon");
            await ExecuteAsync(factory, "DELETE FROM meta.attribute WHERE entity_logical_name = 'mailbox' AND logical_name = 'testmailboxaccesscompletedon'");

            await StartAsync(connectionString, seed);

            (await ScalarAsync(factory, "SELECT COUNT(*) FROM meta.attribute WHERE entity_logical_name = 'mailbox' AND logical_name = 'testmailboxaccesscompletedon'"))
                .Should().Be(1);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.mailbox WHERE testmailboxaccesscompletedon IS NOT NULL")).Should().Be(0);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }

    // ---- organization.blockedattachments (final review I2) ----

    private async Task BlockedAttachmentsCaseAsync(Func<IDbConnectionFactory, string, SeedOptions, Task> body)
    {
        var database = "xrmd_old_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        try
        {
            await body(TestProvider.Factory(connectionString), connectionString, new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada"));
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }

    private static string StepFilter => $"name = '{SystemSeed.BlockedAttachmentsDefaultStep}'";

    [Fact]
    public Task NewOrganization_GetsTheDefaultBlockedAttachments() => BlockedAttachmentsCaseAsync(async (factory, connectionString, seed) =>
    {
        await StartAsync(connectionString, seed);

        (await ScalarAsync(factory, "SELECT blockedattachments FROM data.organization")).Should().Be(MailSchema.DefaultBlockedAttachments);
        (await ScalarAsync(factory, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE {StepFilter}")).Should().BeOneOf(1, 1L);
    });

    [Fact]
    public Task Upgrade_FromAnEmptyBlockedAttachments_SetsTheDefaultOnce() => BlockedAttachmentsCaseAsync(async (factory, connectionString, seed) =>
    {
        await StartAsync(connectionString, seed);
        // An organization from before the step: the value is empty and the step is not recorded.
        await ExecuteAsync(factory, "UPDATE data.organization SET blockedattachments = NULL");
        await ExecuteAsync(factory, $"DELETE FROM meta.upgrade_step WHERE {StepFilter}");

        await StartAsync(connectionString, seed);

        (await ScalarAsync(factory, "SELECT blockedattachments FROM data.organization")).Should().Be(MailSchema.DefaultBlockedAttachments);

        // Once only: a list an administrator clears afterwards stays cleared at the next start.
        await ExecuteAsync(factory, "UPDATE data.organization SET blockedattachments = NULL");
        await StartAsync(connectionString, seed);
        (await ScalarAsync(factory, "SELECT blockedattachments FROM data.organization")).Should().BeNull();
    });

    [Fact]
    public Task Upgrade_KeepsABlockedAttachmentsListAnAdministratorSet() => BlockedAttachmentsCaseAsync(async (factory, connectionString, seed) =>
    {
        await StartAsync(connectionString, seed);
        await ExecuteAsync(factory, "UPDATE data.organization SET blockedattachments = 'exe;msi'");
        await ExecuteAsync(factory, $"DELETE FROM meta.upgrade_step WHERE {StepFilter}");

        await StartAsync(connectionString, seed);

        (await ScalarAsync(factory, "SELECT blockedattachments FROM data.organization")).Should().Be("exe;msi");
        (await ScalarAsync(factory, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE {StepFilter}")).Should().BeOneOf(1, 1L);
    });
}
