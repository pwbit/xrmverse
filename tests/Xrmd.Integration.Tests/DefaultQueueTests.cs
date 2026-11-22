using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1a, task 3: every user and every owner team has a private default queue, created in the transaction of the principal's
/// Create (name <c>&lt;fullname&gt;</c> or <c>&lt;team name&gt;</c>, from memory: checklist 4) and, for an organization made before,
/// by the startup upgrade. An access team has none. A team goes with its default queue unless it holds active queue items.
/// </summary>
[Collection("xrmd")]
public sealed class DefaultQueueTests(XrmdFixture fx)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result is long n ? (int)n : result;
    }

    private async Task<Guid> NewUserAsync(string fullname) => await Admin.CreateAsync(new Entity("systemuser")
    {
        ["fullname"] = fullname, ["domainname"] = $"{_tag}.{Guid.NewGuid():N}@example.invalid", ["azureactivedirectoryobjectid"] = Guid.NewGuid(),
        ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
    });

    private async Task<Guid> NewTeamAsync(string name, int teamType = 0) => await Admin.CreateAsync(new Entity("team")
    {
        ["name"] = name, ["teamtype"] = new OptionSetValue(teamType),
        ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
    });

    [Fact]
    public async Task CreateUser_CreatesPrivateDefaultQueue()
    {
        var user = await NewUserAsync("Ada " + _tag);

        var queueRef = (await Admin.RetrieveAsync("systemuser", user, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid");
        queueRef.Should().NotBeNull();
        queueRef.LogicalName.Should().Be("queue");
        var queue = await Admin.RetrieveAsync("queue", queueRef.Id, new ColumnSet("name", "queueviewtype", "queuetypecode", "ownerid", "statecode"));
        queue.GetAttributeValue<string>("name").Should().Be($"<Ada {_tag}>");
        queue.GetAttributeValue<OptionSetValue>("queueviewtype").Value.Should().Be(1);
        queue.GetAttributeValue<OptionSetValue>("queuetypecode").Value.Should().Be(1);
        queue.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        queue.GetAttributeValue<EntityReference>("ownerid").Should().Match<EntityReference>(o => o.LogicalName == "systemuser" && o.Id == user);
        // The user is a member of it, through the queuemembership intersect.
        (await ScalarAsync($"SELECT COUNT(*) FROM data.queuemembership WHERE queueid = '{queueRef.Id}' AND systemuserid = '{user}'")).Should().Be(1);
    }

    [Fact]
    public async Task CreateTeam_CreatesPrivateDefaultQueue_AndAccessTeamsGetNone()
    {
        var team = await NewTeamAsync("Support " + _tag);
        var access = await NewTeamAsync("Access " + _tag, teamType: 1);

        var queueRef = (await Admin.RetrieveAsync("team", team, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid");
        queueRef.Should().NotBeNull();
        var queue = await Admin.RetrieveAsync("queue", queueRef.Id, new ColumnSet("name", "queueviewtype", "ownerid"));
        queue.GetAttributeValue<string>("name").Should().Be($"<Support {_tag}>");
        queue.GetAttributeValue<OptionSetValue>("queueviewtype").Value.Should().Be(1);
        queue.GetAttributeValue<EntityReference>("ownerid").Should().Match<EntityReference>(o => o.LogicalName == "team" && o.Id == team);
        // A team's members are members of its queue at read time (task 4): no membership rows.
        (await ScalarAsync($"SELECT COUNT(*) FROM data.queuemembership WHERE queueid = '{queueRef.Id}'")).Should().Be(0);

        (await Admin.RetrieveAsync("team", access, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Should().BeNull();
        (await ScalarAsync($"SELECT COUNT(*) FROM data.queue WHERE ownerid = '{access}'")).Should().Be(0);
    }

    [Fact]
    public async Task DeleteTeam_WithoutItems_DeletesItsDefaultQueue()
    {
        var team = await NewTeamAsync("Gone " + _tag);
        var queue = (await Admin.RetrieveAsync("team", team, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;

        await Admin.DeleteAsync("team", team);

        (await ScalarAsync($"SELECT COUNT(*) FROM data.queue WHERE queueid = '{queue}'")).Should().Be(0);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.team WHERE teamid = '{team}'")).Should().Be(0);
    }

    [Fact]
    public async Task DeleteTeam_WithActiveItemsInDefaultQueue_Is400_AndDeletesNothing()
    {
        var team = await NewTeamAsync("Busy " + _tag);
        var queue = (await Admin.RetrieveAsync("team", team, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = "Call " + _tag });
        var item = await Admin.CreateAsync(new Entity("queueitem")
        {
            ["queueid"] = new EntityReference("queue", queue), ["objectid"] = new EntityReference("task", task), ["title"] = "Call " + _tag,
        });

        var message = await BadRequestAsync(() => Admin.DeleteAsync("team", team));

        message.Should().Contain("default queue").And.Contain("active queue items");
        (await ScalarAsync($"SELECT COUNT(*) FROM data.team WHERE teamid = '{team}'")).Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.queue WHERE queueid = '{queue}'")).Should().Be(1);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.queueitem WHERE queueitemid = '{item}'")).Should().Be(1);
    }

    [Fact]
    public async Task DeleteUser_StaysRefused_AndKeepsItsDefaultQueue()
    {
        var user = await NewUserAsync("Keep " + _tag);
        var queue = (await Admin.RetrieveAsync("systemuser", user, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;

        // Users are never deleted (CheckDeleteRulesAsync), so there is no default queue to go with one.
        (await BadRequestAsync(() => Admin.DeleteAsync("systemuser", user))).Should().Contain("cannot be deleted");
        (await ScalarAsync($"SELECT COUNT(*) FROM data.queue WHERE queueid = '{queue}'")).Should().Be(1);
    }

    /// <summary>
    /// Final review minor 3 (task 3 deferred #6): backfills that run at once (two processes starting together) give each principal
    /// one default queue, with no orphan queue or membership left behind.
    /// </summary>
    [Fact]
    public async Task Startup_ConcurrentBackfills_CreateOneDefaultQueuePerPrincipal()
    {
        var (factory, database) = await OldOrganizationAsync();
        try
        {
            var bu = (Guid)(await ScalarAsync(factory, "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL"))!;
            var now = TestSql.Dialect.UtcNow;
            for (var i = 0; i < 20; i++)
                await ScalarAsync(factory, $"""
                    INSERT INTO data.systemuser (systemuserid, fullname, domainname, businessunitid, isdisabled, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                    VALUES ('{Guid.NewGuid()}', 'Old user {i}', 'old{i}@example.invalid', '{bu}', {TestSql.Dialect.BooleanLiteral(false)}, '{bu}', {now}, {now}, 0, 1)
                    """);
            var store = await StoreAsync(factory);

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => QueueSettingsUpgrade.BackfillDefaultQueuesAsync(store, factory, default))));

            var principals = (int)(await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser"))!
                             + (int)(await ScalarAsync(factory, "SELECT COUNT(*) FROM data.team WHERE teamtype IS NULL OR teamtype = 0"))!;
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser WHERE queueid IS NULL")).Should().Be(0);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queue")).Should().Be(principals, "one default queue per principal, no orphan");
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queuemembership")).Should()
                .Be(await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser"), "one membership per user, in its own queue");
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queuemembership m JOIN data.systemuser u ON u.queueid = m.queueid " +
                "AND u.systemuserid = m.systemuserid")).Should().Be(await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser"));
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }

    /// <summary>A database of its own, seeded as before plan 4.1a: users and teams without a queue.</summary>
    private async Task<(IDbConnectionFactory Factory, string Database)> OldOrganizationAsync()
    {
        var database = "xrmd_dq_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(factory).MigrateAsync();
        var store = new MetadataStore(factory);
        await store.LoadAsync();
        await new SystemSeed(store, factory).EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada"));
        return (factory, database);
    }

    private static async Task<object?> ScalarAsync(IDbConnectionFactory factory, string sql)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result is long n ? (int)n : result;
    }

    private static async Task<MetadataStore> StoreAsync(IDbConnectionFactory factory)
    {
        var store = new MetadataStore(factory);
        await store.LoadAsync();
        return store;
    }

    [Fact]
    public async Task Startup_BackfillsDefaultQueues_ForExistingUsersAndTeams()
    {
        var (factory, database) = await OldOrganizationAsync();
        try
        {
            var bu = (Guid)(await ScalarAsync(factory, "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL"))!;
            var (owner, access) = (Guid.NewGuid(), Guid.NewGuid());
            var now = TestSql.Dialect.UtcNow;
            await ScalarAsync(factory, $"""
                INSERT INTO data.team (teamid, name, businessunitid, teamtype, isdefault, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                VALUES ('{owner}', 'Old owner team', '{bu}', 0, 0, '{bu}', {now}, {now}, 0, 1);
                INSERT INTO data.team (teamid, name, businessunitid, teamtype, isdefault, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                VALUES ('{access}', 'Old access team', '{bu}', 1, 0, '{bu}', {now}, {now}, 0, 1)
                """);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queue")).Should().Be(0);
            var users = (int)(await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser"))!;
            users.Should().BeGreaterThan(0);

            await QueueSettingsUpgrade.EnsureAsync(await StoreAsync(factory), factory);

            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser WHERE queueid IS NULL")).Should().Be(0);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.team WHERE queueid IS NULL AND teamtype = 1")).Should().Be(1);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.team WHERE queueid IS NULL AND (teamtype IS NULL OR teamtype <> 1)")).Should().Be(0);
            (await ScalarAsync(factory, $"SELECT q.name FROM data.queue q JOIN data.team t ON t.queueid = q.queueid WHERE t.teamid = '{owner}'"))
                .Should().Be("<Old owner team>");
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queue q JOIN data.systemuser u ON u.queueid = q.queueid " +
                "WHERE q.queueviewtype = 1 AND q.ownerid = u.systemuserid AND q.name = CONCAT('<', u.fullname, '>')")).Should().Be(users);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queuemembership m JOIN data.systemuser u ON u.queueid = m.queueid " +
                "AND u.systemuserid = m.systemuserid")).Should().Be(users);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }

    [Fact]
    public async Task Startup_Twice_WritesNothingTheSecondTime()
    {
        var (factory, database) = await OldOrganizationAsync();
        try
        {
            await QueueSettingsUpgrade.EnsureAsync(await StoreAsync(factory), factory);
            var queues = await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queue");
            var members = await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queuemembership");
            var stamp = await ScalarAsync(factory, "SELECT [current] FROM meta.version_stamp");
            var latest = await ScalarAsync(factory, "SELECT MAX(modifiedon) FROM data.queue");
            queues.Should().NotBe(0);

            await QueueSettingsUpgrade.EnsureAsync(await StoreAsync(factory), factory);

            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queue")).Should().Be(queues);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.queuemembership")).Should().Be(members);
            (await ScalarAsync(factory, "SELECT [current] FROM meta.version_stamp")).Should().Be(stamp);
            (await ScalarAsync(factory, "SELECT MAX(modifiedon) FROM data.queue")).Should().Be(latest);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }
}
