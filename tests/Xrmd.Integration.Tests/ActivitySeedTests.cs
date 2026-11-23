using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The seeded activity tables (plan 2.11, task 1): <c>activitypointer</c>, <c>activityparty</c>, <c>annotation</c> and the four
/// standard activities, their names, columns, states and party lists, the EntityName columns that read a lookup's type column,
/// and the two new settings columns (<c>systemuser.internalemailaddress</c>, <c>organization.maxuploadfilesize</c>).
/// </summary>
[Collection("xrmd")]
public sealed class ActivitySeedTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<List<string>> ColumnsAsync(string table)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT column_name FROM information_schema.columns WHERE CONCAT(table_schema, '.', table_name) = CAST(@table AS varchar(300))");
        cmd.Parameters.AddWithValue("@table", "data." + table);
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    [Fact]
    public void Seed_tables_have_dataverse_names()
    {
        foreach (var (name, set, otc, primaryId, primaryName, ownership) in new[]
                 {
                     ("annotation", "annotations", 5, "annotationid", "subject", OwnershipTypes.UserOwned),
                     ("activityparty", "activityparties", 135, "activitypartyid", "partyidname", OwnershipTypes.None),
                     ("activitypointer", "activitypointers", 4200, "activityid", "subject", OwnershipTypes.UserOwned),
                     ("appointment", "appointments", 4201, "activityid", "subject", OwnershipTypes.UserOwned),
                     ("email", "emails", 4202, "activityid", "subject", OwnershipTypes.UserOwned),
                     ("phonecall", "phonecalls", 4210, "activityid", "subject", OwnershipTypes.UserOwned),
                     ("task", "tasks", 4212, "activityid", "subject", OwnershipTypes.UserOwned),
                 })
        {
            var e = Store.GetEntity(name);
            e.Should().NotBeNull(name);
            e!.EntitySetName.Should().Be(set, name);
            e.ObjectTypeCode.Should().Be(otc, name);
            e.PrimaryIdAttribute.Should().Be(primaryId, name);
            e.PrimaryNameAttribute.Should().Be(primaryName, name);
            e.OwnershipType.Should().Be(ownership, name);
            Store.GetEntityByObjectTypeCode(otc).Should().BeSameAs(e, name);
            Store.GetEntityBySetName(set).Should().BeSameAs(e, name);
        }
        foreach (var name in new[] { "email", "task", "phonecall", "appointment" })
        {
            var e = Store.GetEntity(name)!;
            e.IsActivity.Should().BeTrue(name);
            e.HasNotes.Should().BeTrue(name);
            e.HasActivities.Should().BeFalse(name);
            ActivitySchema.IsActivityTable(e).Should().BeTrue(name);
        }
        Store.GetEntity("activitypointer")!.IsActivity.Should().BeFalse();
        ActivitySchema.IsActivityTable(Store.GetEntity("activitypointer")!).Should().BeFalse();
        // The polymorphic lookups of the model keep their type column.
        Store.GetEntity("activityparty")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "partyid")
            .Targets.Should().BeEquivalentTo("account", "contact", "queue", "systemuser");
        // Seeded without targets; the startup upgrade (task 2) gives it the tables with HasNotes.
        Store.GetEntity("annotation")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "objectid")
            .Targets.Should().Contain(["account", "contact", "email", "task", "phonecall", "appointment"]);
    }

    [Fact]
    public async Task Activity_tables_carry_common_columns_and_party_lists()
    {
        var email = Store.GetEntity("email")!;
        foreach (var common in ActivitySchema.CommonAttributes())
        {
            var a = email.Attributes.SingleOrDefault(x => x.LogicalName == common.LogicalName);
            a.Should().NotBeNull(common.LogicalName);
            a!.GetType().Should().Be(common.GetType(), common.LogicalName);
        }
        foreach (var party in new[] { "from", "to", "cc", "bcc", "related" })
        {
            var a = email.Attributes.Single(x => x.LogicalName == party).Should().BeOfType<LookupAttributeMetadata>(party).Subject;
            a.AttributeType.Should().Be(AttributeTypeCode.PartyList, party);
        }
        email.Attributes.Single(a => a.LogicalName == "regardingobjecttypecode").AttributeType.Should().Be(AttributeTypeCode.EntityName);
        email.Attributes.Single(a => a.LogicalName == "activitytypecode").AttributeType.Should().Be(AttributeTypeCode.EntityName);
        CurrencySchema.HasCurrencyColumns(email).Should().BeTrue();
        Store.GetRelationship("TransactionCurrency_Email").Should().NotBeNull();
        Store.GetRelationship("TransactionCurrency_ActivityPointer").Should().NotBeNull();
        // The party lists' own one-to-many relationships do not exist (no column).
        Store.GetOneToManyRelationships().Should().NotContain(r => r.ReferencingEntity == "email" && r.ReferencingAttribute == "to");

        var columns = await ColumnsAsync("email");
        columns.Should().Contain(["activityid", "subject", "regardingobjectid", "regardingobjectidtype", "activitytypecode",
            "transactioncurrencyid", "exchangerate", "ownerid", "statecode", "statuscode", "directioncode"]);
        columns.Should().NotContain(["from", "to", "cc", "bcc", "related", "regardingobjecttypecode", "emailid"]);
        (await ColumnsAsync("annotation")).Should().Contain(["objectid", "objectidtype", "notetext", "filename"])
            .And.NotContain(["objecttypecode", "documentbody"]);
        (await ColumnsAsync("activityparty")).Should().Contain(["activityid", "activityidtype", "partyid", "partyidtype", "participationtypemask"])
            .And.NotContain("partyobjecttypecode");
        (await TestSql.IndexExistsAsync(fx.ConnectionString, "data", "activitypointer", "IX_activitypointer_regarding")).Should().BeTrue();
        (await TestSql.IndexExistsAsync(fx.ConnectionString, "data", "activityparty", "IX_activityparty_activityid")).Should().BeTrue();
        (await TestSql.IndexExistsAsync(fx.ConnectionString, "data", "activityparty", "IX_activityparty_partyid")).Should().BeTrue();
    }

    [Fact]
    public void State_and_status_options_match_learn()
    {
        (int State, int Default)[] States(string table) => Store.GetEntity(table)!.Attributes.OfType<StateAttributeMetadata>().Single()
            .OptionSet.Options.Cast<StateOptionMetadata>().Select(o => (o.Value!.Value, o.DefaultStatus!.Value)).ToArray();
        int[] Statuses(string table, int state) => Store.GetEntity(table)!.Attributes.OfType<StatusAttributeMetadata>().Single()
            .OptionSet.Options.Cast<StatusOptionMetadata>().Where(o => o.State == state).Select(o => o.Value!.Value).ToArray();

        States("email").Should().Equal((0, 1), (1, 2), (2, 5));
        Statuses("email", 1).Should().Contain(6);
        States("appointment").Should().Contain((3, 5));
        States("task")[0].Default.Should().Be(2);
        States("activitypointer").Should().Equal((0, 1), (1, 2), (2, 3), (3, 4));
    }

    [Fact]
    public void Participation_type_choice_has_1_to_13()
    {
        var mask = Store.GetEntity("activityparty")!.Attributes.Single(a => a.LogicalName == "participationtypemask")
            .Should().BeOfType<PicklistAttributeMetadata>().Subject;
        mask.OptionSet.Options.Select(o => o.Value!.Value).Should().Equal(Enumerable.Range(1, 13));
        mask.OptionSet.Name.Should().Be("activityparty_participationtypemask");
        mask.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
    }

    [Fact]
    public async Task Entityname_companions_read_the_type_column()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var account = await admin.CreateAsync(new Entity("account") { ["name"] = "Seed note target" });
        var note = Guid.NewGuid();
        var owner = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;
        var bu = (Guid)(await ScalarAsync("SELECT businessunitid FROM data.systemuser WHERE systemuserid = @u", ("@u", owner)))!;
        await ScalarAsync("""
            INSERT INTO data.annotation (annotationid, subject, objectid, objectidtype, isdocument, ownerid, owneridtype, owninguser,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES (@id, N'Seed note', @account, 1, 0, @u, 8, @u, @bu, SYSUTCDATETIME(), @u, SYSUTCDATETIME(), @u, 0, 1)
            """, ("@id", note), ("@account", account), ("@u", owner), ("@bu", bu));

        var read = await admin.RetrieveAsync("annotation", note, new ColumnSet("objectid", "objecttypecode"));
        read.GetAttributeValue<string>("objecttypecode").Should().Be("account");
        read.FormattedValues["objecttypecode"].Should().Be("Account"); // the table's display name (else SchemaName)
        read.GetAttributeValue<EntityReference>("objectid").LogicalName.Should().Be("account");
        read.GetAttributeValue<EntityReference>("objectid").Id.Should().Be(account);

        foreach (var value in new[] { "account", "1" })
        {
            var found = await admin.RetrieveMultipleAsync(new FetchExpression($"""
                <fetch><entity name="annotation"><attribute name="annotationid" /><attribute name="objecttypecode" />
                  <filter><condition attribute="objecttypecode" operator="eq" value="{value}" />
                    <condition attribute="annotationid" operator="eq" value="{note}" /></filter>
                </entity></fetch>
                """));
            found.Entities.Should().ContainSingle(value).Which.GetAttributeValue<string>("objecttypecode").Should().Be("account");
        }
        var none = await admin.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch><entity name="annotation"><attribute name="annotationid" />
              <filter><condition attribute="objecttypecode" operator="eq" value="contact" />
                <condition attribute="annotationid" operator="eq" value="{note}" /></filter>
            </entity></fetch>
            """));
        none.Entities.Should().BeEmpty();
        var unknown = await TestHelpers.BadRequestAsync(() => admin.RetrieveMultipleAsync(new FetchExpression("""
            <fetch><entity name="annotation"><attribute name="annotationid" />
              <filter><condition attribute="objecttypecode" operator="eq" value="nosuchtable" /></filter>
            </entity></fetch>
            """)));
        unknown.Should().Contain("nosuchtable");
    }

    [Fact]
    public async Task Systemuser_internalemailaddress_and_maxuploadfilesize_seeded()
    {
        var email = Store.GetEntity("systemuser")!.Attributes.Single(a => a.LogicalName == ActivitySchema.InternalEmailAddress)
            .Should().BeOfType<StringAttributeMetadata>().Subject;
        email.MaxLength.Should().Be(100);
        email.Format.Should().Be(StringFormat.Email);
        var size = Store.GetEntity("organization")!.Attributes.Single(a => a.LogicalName == ActivitySchema.MaxUploadFileSize)
            .Should().BeOfType<IntegerAttributeMetadata>().Subject;
        (size.MinValue, size.MaxValue).Should().Be((0, ActivitySchema.MaxMaxUploadFileSize));
        (await ScalarAsync("SELECT maxuploadfilesize FROM data.organization")).Should().Be(ActivitySchema.DefaultMaxUploadFileSize);
        (await ColumnsAsync("systemuser")).Should().Contain(ActivitySchema.InternalEmailAddress);

        // An organization made before the column gets the default from the next seed (pre-flight C3); a set value is kept.
        await ScalarAsync("UPDATE data.organization SET maxuploadfilesize = NULL");
        var seed = new SystemSeed(Store, TestProvider.Factory(fx.ConnectionString));
        await seed.EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName));
        (await ScalarAsync("SELECT maxuploadfilesize FROM data.organization")).Should().Be(ActivitySchema.DefaultMaxUploadFileSize);
        await ScalarAsync("UPDATE data.organization SET maxuploadfilesize = 1000");
        await seed.EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName));
        (await ScalarAsync("SELECT maxuploadfilesize FROM data.organization")).Should().Be(1000);
        await ScalarAsync($"UPDATE data.organization SET maxuploadfilesize = {ActivitySchema.DefaultMaxUploadFileSize}");
    }

    private static void SetType(AttributeMetadata a, AttributeTypeCode type) =>
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.GetSetMethod(true)!.Invoke(a, [type]);

    /// <summary>A7: EntityName and PartyList columns are seeded only; a caller cannot create one (as Dataverse).</summary>
    [Fact]
    public async Task Entityname_and_party_list_columns_cannot_be_created()
    {
        await TestEntities.EnsureProjectAsync(Store);
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var message = await TestHelpers.BadRequestAsync(() => admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateAttributeRequest
        {
            EntityName = TestEntities.Project,
            Attribute = new EntityNameAttributeMetadata("pb_Kind") { LogicalName = "pb_kind" },
        }));
        message.Should().Contain("pb_kind").And.Contain("EntityName");
        Store.GetEntity(TestEntities.Project)!.Attributes.Should().NotContain(a => a.LogicalName == "pb_kind");

        var parties = new LookupAttributeMetadata { LogicalName = "pb_people", SchemaName = "pb_People", Targets = ["account", "contact"] };
        SetType(parties, AttributeTypeCode.PartyList);
        (await TestHelpers.BadRequestAsync(() => Store.AddAttributeAsync(TestEntities.Project, parties))).Should().Contain("PartyList");

        foreach (var attribute in new AttributeMetadata[] { new EntityNameAttributeMetadata("pb_Kind") { LogicalName = "pb_kind" }, parties })
        {
            var name = "pb_entname" + Guid.NewGuid().ToString("N")[..8];
            var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes([attribute]);
            await TestHelpers.BadRequestAsync(() => Store.CreateEntityAsync(entity,
                new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 }));
            Store.GetEntity(name).Should().BeNull();
        }
    }

    /// <summary>The pointer and the party table have no write filters (Learn lists their messages); an activity has all of them.</summary>
    [Fact]
    public async Task Message_filters_of_pointer_and_party_are_the_allowed_messages()
    {
        async Task<List<string>> FiltersAsync(string table)
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, """
                SELECT m.name FROM data.sdkmessagefilter f JOIN data.sdkmessage m ON m.sdkmessageid = f.sdkmessageid
                WHERE f.primaryobjecttypecode = @t
                """);
            cmd.Parameters.AddWithValue("@t", table);
            var names = new List<string>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) names.Add(rd.GetString(0));
            return names;
        }
        (await FiltersAsync("activitypointer")).Should().BeEquivalentTo("Retrieve", "RetrieveMultiple", "Associate", "Disassociate");
        (await FiltersAsync("activityparty")).Should().BeEquivalentTo("RetrieveMultiple", "Associate", "Disassociate");
        // Plan 2.11, task 6: email also has the SendEmail filter (A17); on a standard table the entity messages drop Upsert and
        // gain CreateMultiple and UpdateMultiple (#76, ruling B9).
        (await FiltersAsync("email")).Should().BeEquivalentTo([.. SdkMessageCatalog.EntityMessages.Where(m => m != "Upsert"), "CreateMultiple", "UpdateMultiple", "SendEmail"]).And.HaveCount(16);
        (await FiltersAsync("annotation")).Should().BeEquivalentTo([.. SdkMessageCatalog.EntityMessages.Where(m => m != "Upsert"), "CreateMultiple", "UpdateMultiple"]);
    }

    /// <summary>
    /// An organization from before plan 2.11 (standard roles, no Activity or Note privileges, no top-up recorded): the catalog
    /// creates the privileges and the top-up gives Basic User its grants, once.
    /// </summary>
    [Fact]
    public async Task Activity_top_up_reaches_an_existing_organization()
    {
        var names = new[] { "Activity", "Note" }
            .SelectMany(stem => new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" }.Select(v => $"prv{v}{stem}"))
            .ToList();
        var ids = string.Join(", ", names.Select(n => $"'{PrivilegeNames.IdOf(n)}'"));
        await ScalarAsync($"""
            DELETE FROM data.roleprivileges WHERE privilegeid IN ({ids});
            DELETE FROM data.privilegeobjecttypecodes WHERE privilegeid IN ({ids});
            DELETE FROM data.privilege WHERE privilegeid IN ({ids});
            DELETE FROM meta.upgrade_step WHERE name = N'activity-role-privileges';
            """);
        try
        {
            (await ScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE privilegeid IN ({ids})")).Should().Be(0);
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE privilegeid IN ({ids})")).Should().Be(16);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegedepthmask = 1 AND privilegeid IN ({ids})"))
                .Should().Be(16);
            (await ScalarAsync("SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'activity-role-privileges'")).Should().Be(1);

            // Once: a grant removed afterwards stays removed.
            await ScalarAsync($"DELETE FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{PrivilegeNames.IdOf("prvShareNote")}'");
            await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ({ids})"))
                .Should().Be(15);
        }
        finally
        {
            await ScalarAsync($"""
                IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{PrivilegeNames.IdOf("prvShareNote")}')
                    INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                    VALUES (NEWID(), '{StandardRoles.BasicUser}', '{PrivilegeNames.IdOf("prvShareNote")}', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """);
            Cache(fx).Invalidate();
        }
    }
}
