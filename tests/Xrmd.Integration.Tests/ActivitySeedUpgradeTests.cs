using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Solution;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The activity seed (plan 2.11, task 1) on a database of its own: an organization seeded before the activity tables gets them,
/// with their columns, indexes and the two settings columns; a custom table with one of their names stops the startup.
/// </summary>
[Collection("xrmd")]
public sealed class ActivitySeedUpgradeTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private static readonly string[] Tables =
        ["activitypointer", "activityparty", "email", "task", "phonecall", "appointment", "annotation"];
    private IDbConnectionFactory _factory = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        _database = "xrmd_act_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, _database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await TestProvider.DropDatabaseAsync(fx.ConnectionString, _database);
    }

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        // A COUNT(*) is bigint on PostgreSQL: counts compare as int.
        return result is DBNull ? null : result is long count ? (int)count : result;
    }

    private static void AssertSeeded(IMetadataStore store)
    {
        foreach (var table in Tables) store.GetEntity(table).Should().NotBeNull(table);
        store.GetEntity("email")!.Attributes.Should().Contain(a => a.LogicalName == "to" && a.AttributeType == AttributeTypeCode.PartyList);
        store.GetEntity("annotation")!.Attributes.Should().Contain(a => a.LogicalName == "objecttypecode" && a.AttributeOf == "objectid");
        store.GetEntity("systemuser")!.Attributes.Should().Contain(a => a.LogicalName == ActivitySchema.InternalEmailAddress);
        store.GetEntity("organization")!.Attributes.Should().Contain(a => a.LogicalName == ActivitySchema.MaxUploadFileSize);
        store.GetRelationship("TransactionCurrency_Email").Should().NotBeNull();
        store.GetRelationship("activitypointer_activity_parties").Should().NotBeNull();
        store.GetRelationship("account_activity_parties").Should().NotBeNull();
    }

    [Fact]
    public async Task Seed_upgrade_adds_the_activity_tables_to_an_existing_database()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        AssertSeeded(await NewStoreAsync());

        // An organization seeded before plan 2.11: none of the seven tables, no internalemailaddress, no maxuploadfilesize.
        var names = string.Join(", ", Tables.Select(t => $"'{t}'"));
        await ScalarAsync($"""
            DELETE FROM meta.relationship WHERE {TestSql.Dialect.JsonValue("definition", "$.ReferencingEntity")} IN ({names});
            DELETE FROM meta.attribute WHERE entity_logical_name IN ({names});
            DELETE FROM meta.entity WHERE logical_name IN ({names});
            DROP TABLE data.annotation; DROP TABLE data.appointment; DROP TABLE data.phonecall; DROP TABLE data.task; DROP TABLE data.email;
            DROP TABLE data.activityparty; DROP TABLE data.activitypointer;
            DELETE FROM meta.attribute WHERE entity_logical_name = 'systemuser' AND logical_name = 'internalemailaddress';
            ALTER TABLE data.systemuser DROP COLUMN internalemailaddress;
            DELETE FROM meta.attribute WHERE entity_logical_name = 'organization' AND logical_name = 'maxuploadfilesize';
            ALTER TABLE data.organization DROP COLUMN maxuploadfilesize;
            """);
        var stale = await NewStoreAsync();
        foreach (var table in Tables) stale.GetEntity(table).Should().BeNull(table);
        stale.GetEntity("organization")!.Attributes.Should().NotContain(a => a.LogicalName == ActivitySchema.MaxUploadFileSize);

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        AssertSeeded(stale);
        AssertSeeded(await NewStoreAsync());
        (await ScalarAsync("SELECT maxuploadfilesize FROM data.organization")).Should().Be(ActivitySchema.DefaultMaxUploadFileSize);
        var cs = TestProvider.WithDatabase(fx.ConnectionString, _database);
        (await TestSql.ColumnExistsAsync(cs, "data", "systemuser", "internalemailaddress")).Should().BeTrue();
        (await TestSql.IndexExistsAsync(cs, "data", "activitypointer", "IX_activitypointer_regarding")).Should().BeTrue();
        (await TestSql.IndexExistsAsync(cs, "data", "activityparty", "IX_activityparty_activityid")).Should().BeTrue();
        (await TestSql.IndexExistsAsync(cs, "data", "activityparty", "IX_activityparty_partyid")).Should().BeTrue();

        // A second start changes nothing.
        var relationships = await ScalarAsync("SELECT COUNT(*) FROM meta.relationship");
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship")).Should().Be(relationships);
    }

    /// <summary>The email columns the parity export's SendEmail step sets (plan 2.14, Task 9, R3), with their Learn types.</summary>
    private static readonly (string Name, AttributeTypeCode Type)[] SendEmailColumns =
    [
        ("correlatedsubjectchanged", AttributeTypeCode.Boolean), ("deliveryprioritycode", AttributeTypeCode.Picklist),
        ("followemailuserpreference", AttributeTypeCode.Boolean), ("isduplicatesenderunresolved", AttributeTypeCode.Boolean),
        ("notifications", AttributeTypeCode.Picklist),
    ];

    /// <summary>
    /// Plan 2.14, Task 9 (R3): an organization seeded with the email table of plan 2.11, which lacks the five columns of the parity
    /// export's SendEmail step, gets them at the next start, with their table columns; a second start changes nothing.
    /// </summary>
    [Fact]
    public async Task Seed_upgrade_adds_the_send_email_columns_of_the_parity_export()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        var names = string.Join(", ", SendEmailColumns.Select(c => $"'{c.Name}'"));
        await ScalarAsync($"""
            DELETE FROM meta.attribute WHERE entity_logical_name = 'email' AND logical_name IN ({names});
            {TestSql.Dialect.DropColumns("data.email", SendEmailColumns.Select(c => c.Name).ToList())};
            """);
        var stale = await NewStoreAsync();
        stale.GetEntity("email")!.Attributes.Should().NotContain(a => SendEmailColumns.Any(c => c.Name == a.LogicalName));

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        var email = (await NewStoreAsync()).GetEntity("email")!;
        foreach (var (name, type) in SendEmailColumns)
            email.Attributes.Should().ContainSingle(a => a.LogicalName == name).Which.AttributeType.Should().Be(type, name);
        email.Attributes.OfType<PicklistAttributeMetadata>().Single(a => a.LogicalName == "deliveryprioritycode").OptionSet.Options
            .Select(o => o.Value).Should().Equal(0, 1, 2);
        email.Attributes.OfType<PicklistAttributeMetadata>().Single(a => a.LogicalName == "notifications").OptionSet.Options
            .Select(o => o.Value).Should().Equal(0, 1, 2);
        (await TestSql.ColumnsAsync(TestProvider.WithDatabase(fx.ConnectionString, _database), "data", "email"))
            .Should().Contain(SendEmailColumns.Select(c => c.Name));

        var attributes = await ScalarAsync("SELECT COUNT(*) FROM meta.attribute");
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.attribute")).Should().Be(attributes);
    }

    /// <summary>
    /// Plan 2.11, task 2 (A20, RF5): the seed leaves the managed lookups without targets; the startup upgrade gives them the
    /// seeded tables with HasActivities and HasNotes (account, contact, the four activities' notes) and the activity tables of
    /// activityparty, each with its relationship, once.
    /// </summary>
    [Fact]
    public async Task Upgrade_links_the_seeded_tables_once()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        var seeded = await NewStoreAsync();
        static string[] Targets(IMetadataStore store, string entity, string attribute) =>
            store.GetEntity(entity)!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == attribute).Targets;
        Targets(seeded, "email", "regardingobjectid").Should().BeEmpty();
        Targets(seeded, "annotation", "objectid").Should().BeEmpty();
        seeded.GetRelationship("Account_Emails").Should().BeNull();

        await ActivitySettingsUpgrade.EnsureAsync(seeded, _factory);

        foreach (var store in new[] { seeded, await NewStoreAsync() })
        {
            foreach (var holder in new[] { "activitypointer", "email", "task", "phonecall", "appointment" })
                Targets(store, holder, "regardingobjectid").Should().Equal(["account", "contact"], holder);
            Targets(store, "annotation", "objectid").Should().Equal("account", "appointment", "contact", "email", "phonecall", "task");
            Targets(store, "activityparty", "activityid").Should().Equal("activitypointer", "appointment", "email", "phonecall", "task");
            foreach (var name in new[] { "Account_Emails", "Contact_ActivityPointers", "Account_Annotation", "Task_Annotation", "email_activity_parties",
                         "activitypointer_activity_parties" })
                store.GetRelationship(name).Should().NotBeNull(name);
        }

        // A second start writes nothing.
        var stamp = await ScalarAsync("SELECT [current] FROM meta.version_stamp");
        var relationships = await ScalarAsync("SELECT COUNT(*) FROM meta.relationship");
        await ActivitySettingsUpgrade.EnsureAsync(await NewStoreAsync(), _factory);
        (await ScalarAsync("SELECT [current] FROM meta.version_stamp")).Should().Be(stamp);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship")).Should().Be(relationships);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("annotation")]
    public async Task Startup_fails_when_a_custom_table_named_like_an_activity_table_exists(string name)
    {
        var store = await NewStoreAsync();
        var custom = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.OrganizationOwned };
        custom.SetAttributes([]);
        await store.CreateEntityAsync(custom, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_name", MaxLength = 100 });
        store.GetEntity(name)!.ObjectTypeCode.Should().BeGreaterThanOrEqualTo(10000);

        var seed = () => new SystemSeed(store, _factory).EnsureAsync(Options);

        var failure = (await seed.Should().ThrowAsync<InvalidOperationException>()).Which;
        failure.Message.Should().Contain($"'{name}'").And.Contain(XrmdFixture.OrgName).And.Contain("Rename or delete");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(1);
    }
}
