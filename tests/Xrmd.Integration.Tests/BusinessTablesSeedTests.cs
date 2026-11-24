using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The seeded business tables <c>account</c> and <c>contact</c> (plan 11, task 2): their columns, the seed upgrade of an
/// existing database, the startup failure for a custom table of the same name, the computed <c>contact.fullname</c> and the
/// Customer column <c>contact.parentcustomerid</c>.
/// </summary>
[Collection("xrmd")]
public sealed class BusinessTablesSeedTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private IDbConnectionFactory _factory = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_biz_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        _factory = TestProvider.Factory(
            TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public Task DisposeAsync() => fx.DropDatabaseAsync(_database); // the test's own database (CI runners have little free disk)

    private IMetadataStore HostStore => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static readonly (string Entity, string Column, int Length, StringFormat Format)[] Strings =
    [
        ("account", "name", 160, StringFormat.Text), ("account", "accountnumber", 20, StringFormat.Text),
        ("account", "telephone1", 50, StringFormat.Text), ("account", "emailaddress1", 100, StringFormat.Email),
        ("account", "websiteurl", 200, StringFormat.Url), ("account", "address1_line1", 250, StringFormat.Text),
        ("account", "address1_city", 80, StringFormat.Text), ("account", "address1_postalcode", 20, StringFormat.Text),
        ("account", "address1_country", 80, StringFormat.Text),
        ("contact", "firstname", 50, StringFormat.Text), ("contact", "middlename", 50, StringFormat.Text),
        ("contact", "lastname", 50, StringFormat.Text), ("contact", "fullname", 160, StringFormat.Text),
        ("contact", "emailaddress1", 100, StringFormat.Email), ("contact", "telephone1", 50, StringFormat.Text),
        ("contact", "mobilephone", 50, StringFormat.Text), ("contact", "jobtitle", 100, StringFormat.Text),
        ("contact", "address1_line1", 250, StringFormat.Text), ("contact", "address1_city", 80, StringFormat.Text),
        ("contact", "address1_postalcode", 20, StringFormat.Text), ("contact", "address1_country", 80, StringFormat.Text),
    ];

    private static void AssertSeeded(IMetadataStore store)
    {
        var account = store.GetEntity("account")!;
        var contact = store.GetEntity("contact")!;
        account.ObjectTypeCode.Should().Be(1);
        contact.ObjectTypeCode.Should().Be(2);
        account.SchemaName.Should().Be("Account");
        contact.SchemaName.Should().Be("Contact");
        account.EntitySetName.Should().Be("accounts");
        contact.EntitySetName.Should().Be("contacts");
        account.PrimaryIdAttribute.Should().Be("accountid");
        contact.PrimaryIdAttribute.Should().Be("contactid");
        account.PrimaryNameAttribute.Should().Be("name");
        contact.PrimaryNameAttribute.Should().Be("fullname");
        account.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        contact.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        foreach (var e in new[] { account, contact })
        {
            e.HasActivities.Should().BeTrue(e.LogicalName);
            e.HasNotes.Should().BeTrue(e.LogicalName);
            store.GetEntityBySetName(e.EntitySetName).Should().BeSameAs(e);
        }

        foreach (var (entity, column, length, format) in Strings)
        {
            var attribute = store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == column)
                .Should().BeOfType<StringAttributeMetadata>($"{entity}.{column}").Subject;
            attribute.MaxLength.Should().Be(length, $"{entity}.{column}");
            attribute.Format.Should().Be(format, $"{entity}.{column}");
        }
        var fullname = contact.Attributes.Single(a => a.LogicalName == "fullname");
        fullname.IsValidForCreate.Should().BeFalse();
        fullname.IsValidForUpdate.Should().BeFalse();

        LookupAttributeMetadata Lookup(EntityMetadata e, string name) =>
            e.Attributes.Single(a => a.LogicalName == name).Should().BeOfType<LookupAttributeMetadata>(name).Subject;
        Lookup(account, "parentaccountid").Targets.Should().Equal("account");
        Lookup(account, "primarycontactid").Targets.Should().Equal("contact");
        var customer = Lookup(contact, "parentcustomerid");
        customer.AttributeType.Should().Be(AttributeTypeCode.Customer);
        customer.Targets.Should().Equal("account", "contact");

        // The relationships and cascades of the Learn table references (contact, account).
        const CascadeType C = CascadeType.Cascade, N = CascadeType.NoCascade, L = CascadeType.RemoveLink;
        foreach (var (name, referencing, lookup, target, navigation, cascade) in new[]
                 {
                     ("contact_customer_accounts", "contact", "parentcustomerid", "account", "parentcustomerid_account", (C, C, C, C, C, C)),
                     ("contact_customer_contacts", "contact", "parentcustomerid", "contact", "parentcustomerid_contact", (C, L, C, C, C, C)),
                     ("account_parent_account", "account", "parentaccountid", "account", "parentaccountid", (C, L, C, C, C, C)),
                     ("account_primary_contact", "account", "primarycontactid", "contact", "primarycontactid", (N, L, C, N, N, N)),
                 })
        {
            var r = store.GetRelationship(name).Should().BeOfType<OneToManyRelationshipMetadata>(name).Subject;
            r.SchemaName.Should().Be(name);
            r.ReferencingEntity.Should().Be(referencing);
            r.ReferencingAttribute.Should().Be(lookup);
            r.ReferencedEntity.Should().Be(target);
            r.ReferencedAttribute.Should().Be(target + "id");
            r.ReferencingEntityNavigationPropertyName.Should().Be(navigation);
            r.ReferencedEntityNavigationPropertyName.Should().Be(name);
            var c = r.CascadeConfiguration;
            (c.Assign, c.Delete, c.Merge, c.Reparent, c.Share, c.Unshare).Should().Be(cascade, name);
            (c.Archive, c.RollupView).Should().Be((N, N), name);
        }
        store.GetOneToManyRelationships().Count(r => r.ReferencingEntity == "contact" && r.ReferencingAttribute == "parentcustomerid")
            .Should().Be(2);
        // The generated names of the account lookups are gone: the Dataverse names replace them.
        store.GetRelationship("account_parentaccountid_account").Should().BeNull();
        store.GetRelationship("account_primarycontactid_contact").Should().BeNull();
        store.GetEntity("organization")!.Attributes.Single(a => a.LogicalName == "fullnameconventioncode")
            .Should().BeOfType<PicklistAttributeMetadata>().Which.OptionSet.Options.Select(o => o.Value).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public async Task Account_and_contact_are_seeded_with_their_columns()
    {
        AssertSeeded(HostStore);

        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        var reloaded = await NewStoreAsync();
        AssertSeeded(reloaded);
        (await ScalarAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'data' AND table_name = 'contact' AND column_name IN ('parentcustomerid', 'parentcustomeridtype', 'fullname', 'ownerid')"))
            .Should().Be(4);
        (await ScalarAsync("SELECT fullnameconventioncode FROM data.organization")).Should().Be(0);

        // Seeding again changes nothing (no second relationship, no second column).
        await new SystemSeed(reloaded, _factory).EnsureAsync(Options);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship WHERE schema_name LIKE 'contact_customer_%'")).Should().Be(2);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship WHERE schema_name IN ('account_parent_account', 'account_primary_contact')")).Should().Be(2);
    }

    [Fact]
    public async Task Seed_upgrade_sets_the_dataverse_cascades_of_a_database_seeded_by_the_first_plan_11_seed()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        var before = await NewStoreAsync();
        var ids = new[] { "contact_customer_accounts", "contact_customer_contacts" }.ToDictionary(n => n, n => before.GetRelationship(n)!.MetadataId);
        // The first seed: Delete RemoveLink only on both Customer relationships, no saved relationship for the account lookups.
        await ExecuteAsync("""
            UPDATE meta.relationship SET definition = JSON_MODIFY(definition, '$.CascadeConfiguration', JSON_QUERY('{"Delete":"RemoveLink"}'))
                WHERE schema_name LIKE 'contact_customer_%';
            DELETE FROM meta.relationship WHERE schema_name IN ('account_parent_account', 'account_primary_contact');
            """);
        var stale = await NewStoreAsync();
        ((OneToManyRelationshipMetadata)stale.GetRelationship("contact_customer_accounts")!).CascadeConfiguration.Delete.Should().Be(CascadeType.RemoveLink);
        stale.GetRelationship("account_parent_account").Should().BeNull();
        stale.GetRelationship("account_parentaccountid_account").Should().NotBeNull(); // the generated name of the lookup's relationship

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        AssertSeeded(stale);
        var upgraded = await NewStoreAsync();
        AssertSeeded(upgraded);
        // The rows are kept (same MetadataId), only their cascades change; a custom relationship would never be touched.
        foreach (var (name, id) in ids) upgraded.GetRelationship(name)!.MetadataId.Should().Be(id);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship WHERE schema_name LIKE 'contact_customer_%'")).Should().Be(2);
    }

    [Fact]
    public async Task Entity_definitions_report_HasActivities_and_HasNotes()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/EntityDefinitions(LogicalName='account')?$select=HasActivities,HasNotes,ObjectTypeCode");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!.AsObject();
        json["HasActivities"]!.GetValue<bool>().Should().BeTrue();
        json["HasNotes"]!.GetValue<bool>().Should().BeTrue();
        json["ObjectTypeCode"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Seed_upgrade_adds_the_tables_to_an_existing_database()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        // An organization seeded before account and contact existed: no tables, no metadata, no fullnameconventioncode.
        await ExecuteAsync("""
            DELETE FROM meta.relationship WHERE schema_name LIKE 'contact_customer_%' OR schema_name IN ('account_parent_account', 'account_primary_contact');
            DELETE FROM meta.attribute WHERE entity_logical_name IN ('account', 'contact');
            DELETE FROM meta.entity WHERE logical_name IN ('account', 'contact');
            DROP TABLE data.contact; DROP TABLE data.account;
            DELETE FROM meta.attribute WHERE entity_logical_name = 'organization' AND logical_name = 'fullnameconventioncode';
            ALTER TABLE data.organization DROP COLUMN fullnameconventioncode;
            """);
        var stale = await NewStoreAsync();
        stale.GetEntity("account").Should().BeNull();
        stale.GetEntity("organization")!.Attributes.Should().NotContain(a => a.LogicalName == "fullnameconventioncode");

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        AssertSeeded(stale);
        AssertSeeded(await NewStoreAsync());
        (await ScalarAsync("SELECT fullnameconventioncode FROM data.organization")).Should().Be(0);
    }

    [Fact]
    public async Task Startup_fails_when_a_custom_table_named_account_exists()
    {
        foreach (var name in new[] { "account", "contact" })
        {
            await using var _ = await FreshDatabaseAsync();
            var store = await NewStoreAsync();
            var custom = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.OrganizationOwned };
            custom.SetAttributes([]);
            await store.CreateEntityAsync(custom, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_name", MaxLength = 100 });
            store.GetEntity(name)!.ObjectTypeCode.Should().BeGreaterThanOrEqualTo(10000);

            var seed = () => new SystemSeed(store, _factory).EnsureAsync(Options);

            var failure = (await seed.Should().ThrowAsync<InvalidOperationException>()).Which;
            failure.Message.Should().Contain($"'{name}'").And.Contain(XrmdFixture.OrgName).And.Contain("Rename or delete");
            // Nothing was touched: the custom table is still the only one of its name, and no system table was seeded yet.
            store.GetEntity(name)!.ObjectTypeCode.Should().BeGreaterThanOrEqualTo(10000);
            (await ScalarAsync("SELECT COUNT(*) FROM meta.entity")).Should().Be(1);
        }
    }

    private sealed class Drop(Func<Task> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(action());
    }

    /// <summary>Points the factory at a new empty database; disposing drops it and restores the previous factory.</summary>
    private async Task<IAsyncDisposable> FreshDatabaseAsync()
    {
        var (previous, previousDatabase) = (_factory, _database);
        await InitializeAsync();
        var database = _database;
        return new Drop(async () =>
        {
            (_factory, _database) = (previous, previousDatabase);
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        });
    }

    // ---- fullname -------------------------------------------------------------------------------------------------

    public static TheoryData<int, string?, string?, string?, string?> Names => new()
    {
        { 0, "Ada", "Marie", "Lovelace", "Lovelace, Ada" },
        { 1, "Ada", "Marie", "Lovelace", "Ada Lovelace" },
        { 2, "Ada", "Marie", "Lovelace", "Lovelace, Ada M." },
        { 3, "Ada", "Marie", "Lovelace", "Ada M. Lovelace" },
        { 4, "Ada", "Marie", "Lovelace", "Lovelace, Ada Marie" },
        { 5, "Ada", "Marie", "Lovelace", "Ada Marie Lovelace" },
        { 6, "Ada", "Marie", "Lovelace", "Lovelace Ada" },
        { 7, "Ada", "Marie", "Lovelace", "LovelaceAda" },
        // Review focus 4: missing parts leave no stray separator.
        { 0, null, null, "Lovelace", "Lovelace" },
        { 0, "Ada", null, null, "Ada" },
        { 0, null, "Marie", null, null },
        { 0, null, null, null, null },
        { 0, "", "  ", "", null },
        { 2, "Ada", null, "Lovelace", "Lovelace, Ada" },
        { 2, null, "Marie", "Lovelace", "Lovelace, M." },
        { 3, null, null, "Lovelace", "Lovelace" },
        { 4, "Ada", null, "Lovelace", "Lovelace, Ada" },
        { 5, null, "Marie", null, "Marie" },
        { 6, null, null, "Lovelace", "Lovelace" },
        { 7, "Ada", null, null, "Ada" },
        { 1, null, null, "Lovelace", "Lovelace" },
        { 1, "Ada", null, null, "Ada" },
        { 0, "  Ada ", null, " Lovelace  ", "Lovelace, Ada" },
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Contact_fullname_follows_the_convention(int convention, string? first, string? middle, string? last, string? expected) =>
        ContactFullName.Compose(first, middle, last, convention).Should().Be(expected);

    [Fact]
    public void Contact_fullname_refuses_an_unknown_convention() =>
        ((Action)(() => ContactFullName.Compose("a", null, "b", 8))).Should().Throw<ArgumentOutOfRangeException>();

    private async Task SetConventionAsync(int code)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "UPDATE data.organization SET fullnameconventioncode = @code");
        cmd.AddParameter("@code", code);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Contact_fullname_is_computed_on_create_and_update()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        try
        {
            await SetConventionAsync(0);
            var id = await admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "Lovelace" });
            (await admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname").Should().Be("Lovelace, Ada");

            // Only a part is sent: the others come from the stored record.
            await admin.UpdateAsync(new Entity("contact", id) { ["middlename"] = "Marie" });
            (await admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname").Should().Be("Lovelace, Ada");

            await SetConventionAsync(5);
            await admin.UpdateAsync(new Entity("contact", id) { ["lastname"] = "King" });
            (await admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname").Should().Be("Ada Marie King");

            // A change that touches no name part leaves it alone.
            await admin.UpdateAsync(new Entity("contact", id) { ["jobtitle"] = "Count" });
            (await admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname").Should().Be("Ada Marie King");

            // Parts cleared: no full name.
            await admin.UpdateAsync(new Entity("contact", id) { ["firstname"] = null, ["middlename"] = null, ["lastname"] = null });
            (await admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname").Should().BeNull();
        }
        finally
        {
            await SetConventionAsync(0);
        }
    }

    [Fact]
    public async Task Unchanged_fullname_on_update_is_a_no_op_and_a_different_one_is_refused()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var id = await admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "Lovelace" });

        // Retrieve, change another column, send the whole record back.
        var retrieved = await admin.RetrieveAsync("contact", id, new ColumnSet(true));
        retrieved["jobtitle"] = "Countess";
        await admin.UpdateAsync(retrieved);
        var after = await admin.RetrieveAsync("contact", id, new ColumnSet("fullname", "jobtitle"));
        after.GetAttributeValue<string>("fullname").Should().Be("Lovelace, Ada");
        after.GetAttributeValue<string>("jobtitle").Should().Be("Countess");

        var different = async () => await admin.UpdateAsync(new Entity("contact", id) { ["fullname"] = "Other" });
        (await different.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        var create = async () => await admin.CreateAsync(new Entity("contact") { ["lastname"] = "X", ["fullname"] = "X" });
        (await create.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Convention_code_outside_0_to_7_is_refused_and_a_stored_one_falls_back_to_0()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var org = (await admin.RetrieveMultipleAsync(new QueryExpression("organization") { ColumnSet = new ColumnSet("organizationid") })).Entities.Single().Id;
        var bad = async () => await admin.UpdateAsync(new Entity("organization", org) { ["fullnameconventioncode"] = new OptionSetValue(8) });
        (await bad.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);

        try
        {
            await SetConventionAsync(9); // stored behind the API's back
            var id = await admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "Lovelace" });
            (await admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname").Should().Be("Lovelace, Ada");
        }
        finally
        {
            await SetConventionAsync(0);
        }
    }

    [Fact]
    public async Task Fullname_is_not_writable()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var id = await admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "Lovelace" });

        var create = async () => await admin.CreateAsync(new Entity("contact") { ["lastname"] = "X", ["fullname"] = "Mine" });
        var update = async () => await admin.UpdateAsync(new Entity("contact", id) { ["fullname"] = "Mine" });

        (await create.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        var failure = (await update.Should().ThrowAsync<XrmdFaultException>()).Which;
        failure.HttpStatus.Should().Be(400);
        failure.Message.Should().Contain("fullname").And.Contain("computed");
        (await admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname").Should().Be("Lovelace, Ada");
    }

    // ---- Customer column ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Contact_with_parentcustomerid_account_round_trips()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var account = await admin.CreateAsync(new Entity("account") { ["name"] = "Fennwick Tiles" });
        var parentContact = await admin.CreateAsync(new Entity("contact") { ["lastname"] = "Parent" });

        var onAccount = await admin.CreateAsync(new Entity("contact")
            { ["lastname"] = "Doe", ["parentcustomerid"] = new EntityReference("account", account) });
        var onContact = await admin.CreateAsync(new Entity("contact")
            { ["lastname"] = "Roe", ["parentcustomerid"] = new EntityReference("contact", parentContact) });

        var first = await admin.RetrieveAsync("contact", onAccount, new ColumnSet("parentcustomerid", "fullname"));
        first.GetAttributeValue<EntityReference>("parentcustomerid").Should().Match<EntityReference>(r => r.LogicalName == "account" && r.Id == account);
        first.GetAttributeValue<string>("fullname").Should().Be("Doe");
        (await admin.RetrieveAsync("contact", onContact, new ColumnSet("parentcustomerid")))
            .GetAttributeValue<EntityReference>("parentcustomerid").Should().Match<EntityReference>(r => r.LogicalName == "contact" && r.Id == parentContact);

        // The lookup of an account to a contact and to a parent account.
        var primary = await admin.CreateAsync(new Entity("account")
            { ["name"] = "Child", ["parentaccountid"] = new EntityReference("account", account), ["primarycontactid"] = new EntityReference("contact", onAccount) });
        var child = await admin.RetrieveAsync("account", primary, new ColumnSet("parentaccountid", "primarycontactid"));
        child.GetAttributeValue<EntityReference>("parentaccountid").Id.Should().Be(account);
        child.GetAttributeValue<EntityReference>("primarycontactid").LogicalName.Should().Be("contact");

        // Queried by the Customer column.
        var query = new QueryExpression("contact") { ColumnSet = new ColumnSet("contactid") };
        query.Criteria.AddCondition("parentcustomerid", ConditionOperator.Equal, account);
        (await admin.RetrieveMultipleAsync(query)).Entities.Select(e => e.Id).Should().Equal(onAccount);

        // Only an account or a contact can be the customer.
        var team = new EntityReference("systemuser", fx.AdminObjectId);
        var wrong = async () => await admin.CreateAsync(new Entity("contact") { ["lastname"] = "Bad", ["parentcustomerid"] = team });
        (await wrong.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
    }
}
