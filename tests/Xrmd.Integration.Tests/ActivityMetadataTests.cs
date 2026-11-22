using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.11, task 2: custom activities (<c>IsActivity</c>, ruling A4), <c>HasActivities</c> and <c>HasNotes</c> (A5), the targets
/// and relationships of <c>regardingobjectid</c> (A6), <c>annotation.objectid</c> (A15) and <c>activityparty.activityid</c> (A10),
/// kept by the metadata store, and the startup upgrade (A20, RF5).
/// </summary>
[Collection("xrmd")]
public sealed class ActivityMetadataTests(XrmdFixture fx)
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Name(string label) => $"new_{label}{_run}";

    private static readonly string[] Holders = ["activitypointer", "email", "task", "phonecall", "appointment"];

    private static StringAttributeMetadata Subject(string schemaName = "Subject", string logicalName = "subject") =>
        new() { LogicalName = logicalName, SchemaName = schemaName, MaxLength = 200, DisplayName = Label("Subject") };

    private static CreateEntityRequest CreateActivity(string logicalName, Action<EntityMetadata>? configure = null, StringAttributeMetadata? primary = null)
    {
        var entity = TestHelpers.Entity(logicalName, logicalName, "Activity " + logicalName);
        entity.IsActivity = true;
        configure?.Invoke(entity);
        return new CreateEntityRequest { Entity = entity, PrimaryAttribute = primary ?? Subject() };
    }

    private static CreateEntityRequest CreateTable(string logicalName, Action<EntityMetadata>? configure = null)
    {
        var entity = TestHelpers.Entity(logicalName, logicalName, "Table " + logicalName);
        configure?.Invoke(entity);
        return new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        };
    }

    private Task UpdateEntityAsync(EntityMetadata changes) => Admin.ExecuteAsync(new UpdateEntityRequest { Entity = changes });

    private Task DeleteEntityAsync(string name) => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });

    private string[] Targets(string entity, string attribute) =>
        Store.GetEntity(entity)!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == attribute).Targets;

    private OneToManyRelationshipMetadata Relationship(string schemaName) =>
        Store.GetRelationship(schemaName).Should().BeOfType<OneToManyRelationshipMetadata>(schemaName).Subject;

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    // ---- the seeded tables -------------------------------------------------------------------------------------------

    [Fact]
    public void Account_and_contact_are_regarding_and_notes_targets()
    {
        foreach (var holder in Holders)
            Targets(holder, "regardingobjectid").Should().Contain(["account", "contact"], holder);
        Targets("annotation", "objectid").Should().Contain(["account", "contact", "email", "task", "phonecall", "appointment"]);
        Targets("activityparty", "activityid").Should().Contain(["activitypointer", "email", "task", "phonecall", "appointment"]);

        var emails = Relationship("Account_Emails");
        (emails.ReferencedEntity, emails.ReferencingEntity, emails.ReferencingAttribute, emails.ReferencingEntityNavigationPropertyName)
            .Should().Be(("account", "email", "regardingobjectid", "regardingobjectid_account_email"));
        emails.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
        emails.CascadeConfiguration.Assign.Should().Be(CascadeType.Cascade);
        var pointers = Relationship("Account_ActivityPointers");
        (pointers.ReferencingEntity, pointers.ReferencingEntityNavigationPropertyName).Should().Be(("activitypointer", "regardingobjectid_account"));
        pointers.CascadeConfiguration.Delete.Should().Be(CascadeType.RemoveLink);
        Relationship("Contact_Phonecalls").ReferencingEntityNavigationPropertyName.Should().Be("regardingobjectid_contact_phonecall");
        var notes = Relationship("Account_Annotation");
        (notes.ReferencingEntity, notes.ReferencingAttribute, notes.ReferencingEntityNavigationPropertyName)
            .Should().Be(("annotation", "objectid", "objectid_account"));
        notes.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
        Relationship("Email_Annotation").ReferencingEntityNavigationPropertyName.Should().Be("objectid_email");
        var parties = Relationship("email_activity_parties");
        (parties.ReferencedEntity, parties.ReferencingEntity, parties.ReferencingAttribute, parties.ReferencingEntityNavigationPropertyName)
            .Should().Be(("email", "activityparty", "activityid", "activityid_email"));
        Relationship("account_activity_parties").ReferencingEntityNavigationPropertyName.Should().Be("partyid_account");
        Store.GetEntity("account")!.HasActivities.Should().BeTrue();
        Store.RegardingTables.Should().Contain(["account", "contact"]);
        Store.NotesTables.Should().Contain(["account", "contact", "email"]);
        Store.ActivityTables.Should().Contain(["email", "task", "phonecall", "appointment"]).And.NotContain("activitypointer");
    }

    // ---- custom activities (A4) --------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_custom_activity()
    {
        var name = Name("sms");
        await Admin.ExecuteAsync(CreateActivity(name));

        var sms = Store.GetEntity(name)!;
        (sms.PrimaryIdAttribute, sms.PrimaryNameAttribute, sms.IsActivity, sms.HasNotes, sms.HasActivities, sms.ActivityTypeMask)
            .Should().Be(("activityid", "subject", true, true, false, 1));
        sms.Attributes.Single(a => a.LogicalName == "subject").SchemaName.Should().Be("Subject");
        sms.Attributes.Select(a => a.LogicalName).Should().Contain(ActivitySchema.CommonAttributes().Select(a => a.LogicalName));
        sms.Attributes.Where(ActivitySchema.IsPartyList).Select(a => a.LogicalName).Should().BeEquivalentTo(
            "from", "to", "cc", "bcc", "requiredattendees", "optionalattendees", "organizer", "customers", "resources");
        sms.Attributes.OfType<StateAttributeMetadata>().Single().OptionSet.Options.Select(o => o.Value).Should().Equal(0, 1, 2);
        ActivitySchema.IsActivityTable(sms).Should().BeTrue();
        Store.ActivityTables.Should().Contain(name);
        var read = ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.Entity })).EntityMetadata;
        (read.IsActivity, read.HasNotes, read.HasActivities, read.ActivityTypeMask).Should().Be((true, true, false, 1));

        // The columns: activityid is the key, regardingobjectid keeps its type column, party lists have none.
        var columns = new List<string>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "SELECT column_name FROM information_schema.columns WHERE CONCAT(table_schema, '.', table_name) = CAST(@t AS varchar(300))");
            cmd.Parameters.AddWithValue("@t", $"data.{name}");
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) columns.Add(rd.GetString(0));
        }
        columns.Should().Contain(["activityid", "subject", "regardingobjectid", "regardingobjectidtype", "activitytypecode", "transactioncurrencyid"]);
        columns.Should().NotContain(["to", "from", "regardingobjecttypecode", name + "id"]);

        // A regarding holder towards account and contact, a notes target, an activity of activityparty.
        Targets(name, "regardingobjectid").Should().Contain(["account", "contact"]);
        var regarding = Relationship("Account_" + ActivitySchema.CollectionSchemaName(sms));
        (regarding.ReferencingEntity, regarding.ReferencingEntityNavigationPropertyName).Should().Be((name, $"regardingobjectid_account_{name}"));
        Targets("annotation", "objectid").Should().Contain(name);
        Relationship($"{name}_Annotations").ReferencingEntityNavigationPropertyName.Should().Be($"objectid_{name}");
        Targets("activityparty", "activityid").Should().Contain(name);
        Relationship($"{name}_activity_parties").ReferencingEntityNavigationPropertyName.Should().Be($"activityid_{name}_activityparty");
        Store.GetRelationship(CurrencySchema.RelationshipName(name)).Should().NotBeNull();

        await DeleteEntityAsync(name);
    }

    [Fact]
    public async Task Custom_activity_rules()
    {
        foreach (var (request, expected) in new (CreateEntityRequest, string)[]
                 {
                     (CreateActivity(Name("r1"), primary: Subject("new_Title", "new_title")), "Subject"),
                     (CreateActivity(Name("r2"), e => e.HasActivities = true), "HasActivities"),
                     (CreateActivity(Name("r3"), e => e.HasNotes = false), "HasNotes"),
                     (CreateActivity(Name("r4"), e => e.OwnershipType = OwnershipTypes.TeamOwned), "TeamOwned"),
                     (CreateActivity(Name("r5"), e => e.OwnershipType = OwnershipTypes.OrganizationOwned), "OrganizationOwned"),
                     (CreateActivity(Name("r6"), e => e.IsMailMergeEnabled = new BooleanManagedProperty(true)), "IsMailMergeEnabled"),
                     (CreateActivity(Name("r7"), e => e.ActivityTypeMask = 2), "ActivityTypeMask"),
                     (CreateActivity(Name("r8"), e => e.SetAttributes([new MemoAttributeMetadata { LogicalName = "description", SchemaName = "Description" }])),
                         "description"),
                 })
        {
            var message = await BadRequestAsync(() => Admin.ExecuteAsync(request));
            message.Should().Contain(expected);
            Store.GetEntity(request.Entity.LogicalName).Should().BeNull();
        }
        // The request's HasActivities is refused too; its HasNotes false is the request's default and is not a refusal.
        var viaRequest = CreateActivity(Name("r9"));
        viaRequest.HasActivities = true;
        (await BadRequestAsync(() => Admin.ExecuteAsync(viaRequest))).Should().Contain("HasActivities");

        // The primary column 'subject' carries no publisher prefix (pre-flight C7), through the SDK and the Web API.
        var sdk = Name("ok");
        var accepted = CreateActivity(sdk, e => e.ActivityTypeMask = 0);
        accepted.HasNotes = false;
        await Admin.ExecuteAsync(accepted);
        (Store.GetEntity(sdk)!.ActivityTypeMask, Store.GetEntity(sdk)!.HasNotes).Should().Be((0, true));

        var web = Name("wa");
        var res = await fx.Client.SendAsync(WebApi(HttpMethod.Post, "EntityDefinitions", $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "{{web}}", "LogicalName": "{{web}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "Web activity", "LanguageCode": 1033 } ] },
              "IsActivity": true, "OwnershipType": "UserOwned",
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "subject",
                                "SchemaName": "Subject", "MaxLength": 200, "IsPrimaryName": true } ]
            }
            """));
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        Store.GetEntity(web)!.PrimaryIdAttribute.Should().Be("activityid");

        await DeleteEntityAsync(sdk);
        await DeleteEntityAsync(web);
    }

    private HttpRequestMessage WebApi(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, "/api/data/v9.2/" + path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    [Fact]
    public async Task IsActivity_and_ActivityTypeMask_cannot_change()
    {
        var sms = Name("fx");
        var plain = Name("pl");
        await Admin.ExecuteAsync(CreateActivity(sms));
        await Admin.ExecuteAsync(CreateTable(plain));

        (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = sms, IsActivity = false }))).Should().Contain("IsActivity");
        (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = sms, ActivityTypeMask = 0 }))).Should().Contain("ActivityTypeMask");
        (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = plain, IsActivity = true }))).Should().Contain("IsActivity");
        // The same values sent back are no change.
        await UpdateEntityAsync(new EntityMetadata { LogicalName = sms, IsActivity = true, ActivityTypeMask = 1 });
        Store.GetEntity(sms)!.ActivityTypeMask.Should().Be(1);

        await DeleteEntityAsync(sms);
        await DeleteEntityAsync(plain);
    }

    // ---- HasActivities and HasNotes (A5, A6, A15) ----------------------------------------------------------------------

    [Fact]
    public async Task HasActivities_on_custom_table_adds_targets_to_every_activity_table()
    {
        var sms = Name("ha");
        await Admin.ExecuteAsync(CreateActivity(sms));
        var project = Name("pr");
        var request = CreateTable(project);
        request.HasActivities = true; // the request's parameter
        await Admin.ExecuteAsync(request);

        Store.GetEntity(project)!.HasActivities.Should().BeTrue();
        Store.RegardingTables.Should().Contain(project);
        foreach (var holder in Holders.Append(sms))
            Targets(holder, "regardingobjectid").Should().Contain(project, holder);
        Relationship($"{project}_ActivityPointers").ReferencingEntityNavigationPropertyName.Should().Be($"regardingobjectid_{project}");
        Relationship($"{project}_Emails").ReferencingEntityNavigationPropertyName.Should().Be($"regardingobjectid_{project}_email");
        Relationship($"{project}_Phonecalls").ReferencedEntity.Should().Be(project);
        Relationship($"{project}_{ActivitySchema.CollectionSchemaName(Store.GetEntity(sms)!)}").ReferencingEntity.Should().Be(sms);
        Targets("annotation", "objectid").Should().NotContain(project);

        await DeleteEntityAsync(sms);
        await DeleteEntityAsync(project);
    }

    [Fact]
    public async Task HasNotes_adds_objectid_target()
    {
        var name = Name("hn");
        await Admin.ExecuteAsync(CreateTable(name));
        Targets("annotation", "objectid").Should().NotContain(name);

        await UpdateEntityAsync(new EntityMetadata { LogicalName = name, HasNotes = true });
        Store.GetEntity(name)!.HasNotes.Should().BeTrue();
        Targets("annotation", "objectid").Should().Contain(name);
        var notes = Relationship($"{name}_Annotations");
        (notes.ReferencedEntity, notes.ReferencingEntityNavigationPropertyName).Should().Be((name, $"objectid_{name}"));
        Store.NotesTables.Should().Contain(name);
        foreach (var holder in Holders) Targets(holder, "regardingobjectid").Should().NotContain(name);
        // Sent again, true is no change.
        await UpdateEntityAsync(new EntityMetadata { LogicalName = name, HasNotes = true });

        await DeleteEntityAsync(name);
    }

    [Fact]
    public async Task Switching_flags_off_is_refused()
    {
        var name = Name("of");
        await Admin.ExecuteAsync(CreateTable(name, e => { e.HasNotes = true; e.HasActivities = true; }));

        (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = name, HasActivities = false })))
            .Should().Contain("HasActivities");
        (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = name, HasNotes = false }))).Should().Contain("HasNotes");
        (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = "account", HasActivities = false })))
            .Should().Contain("HasActivities");
        Store.GetEntity(name)!.HasActivities.Should().BeTrue();
        Targets("email", "regardingobjectid").Should().Contain(name);

        await DeleteEntityAsync(name);
    }

    [Fact]
    public async Task HasActivities_on_activity_or_intersect_table_is_refused()
    {
        var sms = Name("hx");
        await Admin.ExecuteAsync(CreateActivity(sms));
        foreach (var table in new[] { "email", "activitypointer", sms })
            (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = table, HasActivities = true })))
                .Should().Contain("HasActivities", table);

        var intersect = Store.GetAllEntities().First(e => e.IsIntersect == true).LogicalName;
        foreach (var changes in new[]
                 {
                     new EntityMetadata { LogicalName = intersect, HasActivities = true }, new EntityMetadata { LogicalName = intersect, HasNotes = true },
                 })
            (await BadRequestAsync(() => TestEntities.InSessionAsync(fx.ConnectionString, s => Store.UpdateEntityAsync(s, changes, default))))
                .Should().Contain("intersect");

        await DeleteEntityAsync(sms);
    }

    [Fact]
    public async Task Creating_an_activity_after_a_regarding_table_links_both()
    {
        var project = Name("rb");
        await Admin.ExecuteAsync(CreateTable(project, e => e.HasActivities = true));
        var sms = Name("ab");
        await Admin.ExecuteAsync(CreateActivity(sms));

        Targets(sms, "regardingobjectid").Should().Contain(["account", "contact", project]);
        var r = Relationship($"{project}_{ActivitySchema.CollectionSchemaName(Store.GetEntity(sms)!)}");
        (r.ReferencedEntity, r.ReferencingEntity, r.ReferencingEntityNavigationPropertyName).Should().Be((project, sms, $"regardingobjectid_{project}_{sms}"));

        await DeleteEntityAsync(sms);
        await DeleteEntityAsync(project);
    }

    // ---- deletes ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_regarding_table_with_activities_is_refused()
    {
        var project = Name("dr");
        await Admin.ExecuteAsync(CreateTable(project, e => { e.HasActivities = true; e.HasNotes = true; }));
        var record = await Admin.CreateAsync(new Entity(project) { ["new_name"] = "P" });
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = "About P", ["regardingobjectid"] = new EntityReference(project, record) });

        (await BadRequestAsync(() => DeleteEntityAsync(project))).Should().Contain("activit");
        Store.GetEntity(project).Should().NotBeNull();
        Targets("email", "regardingobjectid").Should().Contain(project);

        await Admin.DeleteAsync("email", email);
        var code = Store.GetEntity(project)!.ObjectTypeCode!.Value;
        var note = Guid.NewGuid();
        await ScalarAsync("INSERT INTO data.annotation (annotationid, subject, objectid, objectidtype) VALUES (@id, 'n', @record, @code)",
            ("@id", note), ("@record", record), ("@code", code));
        (await BadRequestAsync(() => DeleteEntityAsync(project))).Should().Contain("note");

        await ScalarAsync("DELETE FROM data.annotation WHERE annotationid = @id", ("@id", note));
        await DeleteEntityAsync(project);
        Store.GetEntity(project).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_empty_regarding_table_removes_targets_and_relationships()
    {
        var project = Name("de");
        await Admin.ExecuteAsync(CreateTable(project, e => { e.HasActivities = true; e.HasNotes = true; }));
        Store.GetRelationship($"{project}_Emails").Should().NotBeNull();

        await DeleteEntityAsync(project);

        foreach (var holder in Holders) Targets(holder, "regardingobjectid").Should().NotContain(project, holder);
        Targets("annotation", "objectid").Should().NotContain(project);
        foreach (var suffix in new[] { "ActivityPointers", "Emails", "Tasks", "Phonecalls", "Appointments", "Annotations" })
            Store.GetRelationship($"{project}_{suffix}").Should().BeNull(suffix);
        Store.RegardingTables.Should().NotContain(project);
        Store.NotesTables.Should().NotContain(project);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship WHERE schema_name LIKE @p", ("@p", project + "[_]%"))).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_custom_activity_removes_its_relationships_and_targets()
    {
        var sms = Name("dc");
        await Admin.ExecuteAsync(CreateActivity(sms));
        var entity = Store.GetEntity(sms)!;
        var code = entity.ObjectTypeCode!.Value;
        var collection = ActivitySchema.CollectionSchemaName(entity);
        // A pointer row and a party row of one of its activities (written directly: their writes are task 3's).
        var activity = Guid.NewGuid();
        await ScalarAsync("INSERT INTO data.activitypointer (activityid, activitytypecode, subject) VALUES (@id, @code, 'x')", ("@id", activity), ("@code", code));
        await ScalarAsync("INSERT INTO data.activityparty (activitypartyid, activityid, activityidtype, participationtypemask) VALUES (NEWID(), @id, @code, 9)",
            ("@id", activity), ("@code", code));

        await DeleteEntityAsync(sms);

        Store.GetEntity(sms).Should().BeNull();
        Targets("annotation", "objectid").Should().NotContain(sms);
        Targets("activityparty", "activityid").Should().NotContain(sms);
        Store.ActivityTables.Should().NotContain(sms);
        foreach (var name in new[] { "Account_" + collection, "Contact_" + collection, $"{sms}_Annotations", $"{sms}_activity_parties" })
            Store.GetRelationship(name).Should().BeNull(name);
        (await ScalarAsync("SELECT COUNT(*) FROM data.activitypointer WHERE activityid = @id", ("@id", activity))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id", ("@id", activity))).Should().Be(0);
    }

    /// <summary>A13: the pointer and the party table cannot be change-tracked (their rows go with a deleted custom activity).</summary>
    [Fact]
    public async Task Pointer_and_party_cannot_be_change_tracked()
    {
        foreach (var table in new[] { "activitypointer", "activityparty" })
        {
            (await BadRequestAsync(() => UpdateEntityAsync(new EntityMetadata { LogicalName = table, ChangeTrackingEnabled = true })))
                .Should().Contain("ChangeTrackingEnabled cannot be enabled").And.Contain(table);
            Store.GetEntity(table)!.ChangeTrackingEnabled.Should().NotBe(true, table);
        }
    }

    /// <summary>A custom activity whose records have notes is not deleted (the notes would be left pointing at nothing).</summary>
    [Fact]
    public async Task Deleting_custom_activity_with_notes_is_refused()
    {
        var sms = Name("dn");
        await Admin.ExecuteAsync(CreateActivity(sms));
        var code = Store.GetEntity(sms)!.ObjectTypeCode!.Value;
        var note = Guid.NewGuid();
        await ScalarAsync("INSERT INTO data.annotation (annotationid, subject, objectid, objectidtype, isdocument) VALUES (@id, 'n', NEWID(), @code, 0)",
            ("@id", note), ("@code", code));

        (await BadRequestAsync(() => DeleteEntityAsync(sms))).Should().Contain($"Entity '{sms}' cannot be deleted: 1 notes are attached to its records");
        Store.GetEntity(sms).Should().NotBeNull();

        await ScalarAsync("DELETE FROM data.annotation WHERE annotationid = @id", ("@id", note));
        await DeleteEntityAsync(sms);
    }

    /// <summary>Pre-flight C5: the shared Activity privileges belong to activitypointer, so deleting a custom activity keeps them.</summary>
    [Fact]
    public async Task Deleting_custom_activity_keeps_activity_privileges()
    {
        const string count = """
            SELECT COUNT(*) FROM data.privilege p WHERE p.name LIKE 'prv%Activity'
            """;
        const string grants = """
            SELECT COUNT(*) FROM data.roleprivileges rp JOIN data.privilege p ON p.privilegeid = rp.privilegeid WHERE p.name LIKE 'prv%Activity'
            """;
        var privileges = Convert.ToInt32(await ScalarAsync(count));
        var granted = Convert.ToInt32(await ScalarAsync(grants));
        privileges.Should().Be(8);

        var sms = Name("pk");
        await Admin.ExecuteAsync(CreateActivity(sms));
        (await ScalarAsync(count)).Should().Be(privileges, "a custom activity has no privileges of its own");
        await DeleteEntityAsync(sms);

        (await ScalarAsync(count)).Should().Be(privileges);
        (await ScalarAsync(grants)).Should().Be(granted);
    }

    // ---- the managed lookups (A6) ------------------------------------------------------------------------------------

    [Fact]
    public async Task Managed_lookups_and_relationships_cannot_be_changed_publicly()
    {
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = "email",
            Attribute = new LookupAttributeMetadata { LogicalName = "regardingobjectid", DisplayName = Label("About") },
        }))).Should().Contain("regardingobjectid").And.Contain("activity model");
        foreach (var relationship in new[] { "Account_Emails", "Account_Annotation", "email_activity_parties" })
            (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = relationship })))
                .Should().Contain(relationship).And.Contain("activity model");
        Store.GetRelationship("Account_Emails").Should().NotBeNull();
        Targets("email", "regardingobjectid").Should().Contain("account");
    }

    // ---- the startup upgrade (A20, RF5) ------------------------------------------------------------------------------

    [Fact]
    public async Task Upgrade_turns_a_kept_ActivityTypeMask_into_a_marker()
    {
        // A table imported before plan 2.11 task 7: ActivityTypeMask kept as the file had it; the export writes it from the
        // metadata now, so a kept value would be written twice (500).
        var name = Name("mask");
        await Admin.ExecuteAsync(CreateTable(name));
        var (type, key) = UnmodelledXml.EntityKey(name);
        var kept = new XElement("entity", new XAttribute("Name", name), new XElement("IsActivity"), new XElement("ActivityTypeMask", ""),
            new XElement("IsMapiGridEnabled", "1"));
        UnmodelledXml.TakeModelled(kept, "IsActivity");
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, kept.ToString(SaveOptions.DisableFormatting), default));

        await ActivitySettingsUpgrade.EnsureAsync(Store, fx.App.Services.GetRequiredService<IDbConnectionFactory>());

        var after = XElement.Parse(Store.GetUnmodelledXml(type, key)!);
        UnmodelledXml.IsMarker(after.Element("ActivityTypeMask")!).Should().BeTrue();
        after.Element("IsMapiGridEnabled")!.Value.Should().Be("1");
        var read = ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.Entity }))
            .EntityMetadata;
        read.ActivityTypeMask.Should().BeNull();
        read.HasNotes.Should().BeFalse();
    }

    [Fact]
    public async Task Upgrade_turns_kept_flags_into_targets_once()
    {
        // A table imported before plan 2.11: the flags only in its kept XML, not in its metadata.
        var name = Name("up");
        await Admin.ExecuteAsync(CreateTable(name));
        var (type, key) = UnmodelledXml.EntityKey(name);
        var kept = new XElement("entity", new XAttribute("Name", name), new XElement("IsMapiGridEnabled", "1"),
            new XElement("HasRelatedNotes", "1"), new XElement("HasRelatedActivities", "1"));
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, kept.ToString(SaveOptions.DisableFormatting), default));
        (Store.GetEntity(name)!.HasActivities ?? false).Should().BeFalse();
        Targets("email", "regardingobjectid").Should().NotContain(name);

        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await ActivitySettingsUpgrade.EnsureAsync(Store, factory);

        var upgraded = Store.GetEntity(name)!;
        (upgraded.HasActivities, upgraded.HasNotes).Should().Be((true, true));
        foreach (var holder in Holders) Targets(holder, "regardingobjectid").Should().Contain(name, holder);
        Targets("annotation", "objectid").Should().Contain(name);
        Store.GetRelationship($"{name}_Emails").Should().NotBeNull();
        Store.GetRelationship($"{name}_Annotations").Should().NotBeNull();
        // The kept elements became markers: the metadata is the value now; what the table had besides stays.
        var after = XElement.Parse(Store.GetUnmodelledXml(type, key)!);
        UnmodelledXml.IsMarker(after.Element("HasRelatedNotes")!).Should().BeTrue();
        UnmodelledXml.IsMarker(after.Element("HasRelatedActivities")!).Should().BeTrue();
        after.Element("IsMapiGridEnabled")!.Value.Should().Be("1");

        // A second start changes nothing.
        var stamp = (long)(await ScalarAsync("SELECT [current] FROM meta.version_stamp"))!;
        await ActivitySettingsUpgrade.EnsureAsync(Store, factory);
        (await ScalarAsync("SELECT [current] FROM meta.version_stamp")).Should().Be(stamp);
        Store.VersionStamp.Should().Be(stamp);

        await DeleteEntityAsync(name);
    }

    /// <summary>A kept flag the table cannot have (A5) is reported at startup and not applied, on the tables of the model too.</summary>
    [Fact]
    public async Task Upgrade_reports_kept_flags_the_table_cannot_have()
    {
        var originals = new List<(int Type, string Key, string? Xml)>();
        foreach (var table in new[] { "annotation", "activityparty" })
        {
            var (type, key) = UnmodelledXml.EntityKey(table);
            originals.Add((type, key, Store.GetUnmodelledXml(type, key)));
            var kept = new XElement("entity", new XAttribute("Name", table),
                new XElement("HasRelatedNotes", "1"), new XElement("HasRelatedActivities", "1"));
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, kept.ToString(SaveOptions.DisableFormatting), default));
        }
        try
        {
            var logger = new ListLogger();
            await ActivitySettingsUpgrade.EnsureAsync(Store, fx.App.Services.GetRequiredService<IDbConnectionFactory>(), default, logger);

            foreach (var table in new[] { "annotation", "activityparty" })
            {
                var entity = Store.GetEntity(table)!;
                (entity.HasActivities == true, entity.HasNotes == true).Should().Be((false, false), table);
                logger.Messages.Should().Contain(m => m.Contains($"HasRelatedActivities of '{table}'") && m.Contains("not applied"), table);
                logger.Messages.Should().Contain(m => m.Contains($"HasRelatedNotes of '{table}'") && m.Contains("not applied"), table);
            }
        }
        finally
        {
            foreach (var (type, key, xml) in originals)
                await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, xml, default));
        }
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
