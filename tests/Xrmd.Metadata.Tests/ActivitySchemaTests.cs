using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>The names, party lists, states and common columns of the activity model (plan 2.11, task 1).</summary>
public class ActivitySchemaTests
{
    private static EntityMetadata Table(string name, bool? isActivity = null)
    {
        var e = new EntityMetadata { LogicalName = name, SchemaName = name };
        if (isActivity is { } flag) typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.IsActivity))!.GetSetMethod(true)!.Invoke(e, [flag]);
        return e;
    }

    private static EntityMetadata SeedDefinition(string name)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream("Xrmd.Metadata.Seed." + name + ".json")!;
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    [Fact]
    public void Party_lists_per_table()
    {
        ActivitySchema.PartyLists(Table("email", true)).Should().Equal(
            ("from", ParticipationType.Sender, true), ("to", ParticipationType.ToRecipient, false),
            ("cc", ParticipationType.CcRecipient, false), ("bcc", ParticipationType.BccRecipient, false),
            ("related", ParticipationType.Related, false));
        ActivitySchema.PartyLists(Table("phonecall", true)).Should().Equal(
            ("from", ParticipationType.Sender, true), ("to", ParticipationType.ToRecipient, false),
            ("related", ParticipationType.Related, false));
        ActivitySchema.PartyLists(Table("appointment", true)).Should().Equal(
            ("requiredattendees", ParticipationType.RequiredAttendee, false), ("optionalattendees", ParticipationType.OptionalAttendee, false),
            ("organizer", ParticipationType.Organizer, true));
        ActivitySchema.PartyLists(Table("task", true)).Should().BeEmpty();
        var custom = ActivitySchema.PartyLists(Table("xv_sms", true));
        custom.Should().HaveCount(9);
        custom.Select(p => p.Attribute).Should().Equal(
            "from", "to", "cc", "bcc", "requiredattendees", "optionalattendees", "organizer", "customers", "resources");
        custom.Where(p => p.Single).Select(p => p.Attribute).Should().Equal("from", "organizer");
        custom.Single(p => p.Attribute == "customers").Type.Should().Be(ParticipationType.Customer);
        custom.Single(p => p.Attribute == "resources").Type.Should().Be(ParticipationType.Resource);
        ActivitySchema.PartyLists(Table("account")).Should().BeEmpty();
        ActivitySchema.PartyLists(Table("activitypointer", false)).Should().BeEmpty();
    }

    [Fact]
    public void Participation_types_are_1_to_13()
    {
        Enum.GetValues<ParticipationType>().Select(t => (int)t).Should().Equal(Enumerable.Range(1, 13));
        ((int)ParticipationType.Regarding).Should().Be(8);
        ((int)ParticipationType.Owner).Should().Be(9);
    }

    [Fact]
    public void Activity_tables_are_flagged_activities_except_the_pointer()
    {
        ActivitySchema.IsActivityTable(Table("email", true)).Should().BeTrue();
        ActivitySchema.IsActivityTable(Table("xv_sms", true)).Should().BeTrue();
        ActivitySchema.IsActivityTable(Table("activitypointer", true)).Should().BeFalse();
        ActivitySchema.IsActivityTable(Table("account", false)).Should().BeFalse();
        ActivitySchema.IsActivityTable(Table("account")).Should().BeFalse();
        ActivitySchema.ObjectTypeCodes.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["annotation"] = 5, ["activityparty"] = 135, ["activitypointer"] = 4200, ["appointment"] = 4201,
            ["email"] = 4202, ["phonecall"] = 4210, ["task"] = 4212,
        });
        ActivitySchema.DefaultMaxUploadFileSize.Should().Be(5242880);
        ActivitySchema.MaxMaxUploadFileSize.Should().Be(131072000);
    }

    [Fact]
    public void States_match_the_global_constraints()
    {
        ActivitySchema.States("email").Select(s => (s.State, s.DefaultStatus)).Should().Equal((0, 1), (1, 2), (2, 5));
        ActivitySchema.States("email").Single(s => s.State == 1).Statuses.Should().Equal(2, 3, 4, 6, 7);
        ActivitySchema.States("email").Single(s => s.State == 0).Statuses.Should().Equal(1, 8);
        ActivitySchema.States("task").Select(s => (s.State, s.DefaultStatus)).Should().Equal((0, 2), (1, 5), (2, 6));
        ActivitySchema.States("task").Single(s => s.State == 0).Statuses.Should().Equal(2, 3, 4, 7);
        ActivitySchema.States("phonecall").Select(s => (s.State, s.DefaultStatus)).Should().Equal((0, 1), (1, 2), (2, 3));
        ActivitySchema.States("phonecall").Single(s => s.State == 1).Statuses.Should().Equal(2, 4);
        ActivitySchema.States("appointment").Select(s => (s.State, s.DefaultStatus)).Should().Equal((0, 1), (1, 3), (2, 4), (3, 5));
        ActivitySchema.States("appointment").Single(s => s.State == 3).Statuses.Should().Equal(5, 6);
        ActivitySchema.States("xv_sms").Select(s => (s.State, s.DefaultStatus)).Should().Equal((0, 1), (1, 2), (2, 3));
        ActivitySchema.States("activitypointer").Select(s => (s.State, s.DefaultStatus)).Should().Equal((0, 1), (1, 2), (2, 3), (3, 4));
    }

    /// <summary>The seed files carry the state and status options of <see cref="ActivitySchema.States"/>.</summary>
    [Theory]
    [InlineData("email")]
    [InlineData("task")]
    [InlineData("phonecall")]
    [InlineData("appointment")]
    [InlineData("activitypointer")]
    public void Seed_states_match_the_schema(string table)
    {
        var definition = SeedDefinition(table);
        var state = definition.Attributes.OfType<StateAttributeMetadata>().Single();
        var status = definition.Attributes.OfType<StatusAttributeMetadata>().Single();
        var expected = ActivitySchema.States(table);
        state.OptionSet.Options.Cast<StateOptionMetadata>().Select(o => (o.Value!.Value, o.DefaultStatus!.Value))
            .Should().Equal(expected.Select(s => (s.State, s.DefaultStatus)));
        foreach (var s in expected)
            status.OptionSet.Options.Cast<StatusOptionMetadata>().Where(o => o.State == s.State).Select(o => o.Value!.Value)
                .Should().Equal(s.Statuses, $"{table} state {s.State}");
    }

    /// <summary>
    /// The seed files of the pointer and of the four standard activities carry every common column (A2) with the type
    /// <see cref="ActivitySchema.CommonAttributes"/> gives it, and the party lists of their table.
    /// </summary>
    [Theory]
    [InlineData("email")]
    [InlineData("task")]
    [InlineData("phonecall")]
    [InlineData("appointment")]
    [InlineData("activitypointer")]
    public void Seed_tables_carry_the_common_columns(string table)
    {
        var definition = SeedDefinition(table);
        definition.PrimaryIdAttribute.Should().Be("activityid");
        definition.PrimaryNameAttribute.Should().Be("subject");
        foreach (var common in ActivitySchema.CommonAttributes())
        {
            var seeded = definition.Attributes.SingleOrDefault(a => a.LogicalName == common.LogicalName);
            seeded.Should().NotBeNull($"{table}.{common.LogicalName}");
            seeded!.GetType().Should().Be(common.GetType(), $"{table}.{common.LogicalName}");
            seeded.AttributeOf.Should().Be(common.AttributeOf, $"{table}.{common.LogicalName}");
            SameDefinition(seeded, common, $"{table}.{common.LogicalName}");
        }
        var parties = definition.Attributes.OfType<LookupAttributeMetadata>().Where(a => a.AttributeType == AttributeTypeCode.PartyList)
            .Select(a => a.LogicalName);
        parties.Should().BeEquivalentTo(ActivitySchema.PartyLists(definition).Select(p => p.Attribute), table);
    }

    /// <summary>
    /// Plan 2.14, Task 9 (R3): the email columns the parity export's SendEmail step sets, with the schema names, types and defaults of
    /// Learn's email table reference (<c>correlatedsubjectchanged</c> is all lower case there).
    /// </summary>
    [Fact]
    public void Email_seed_has_the_send_email_columns_of_the_parity_export()
    {
        var email = SeedDefinition("email");
        email.Attributes.Select(a => (a.LogicalName, a.SchemaName))
            .Should().Contain(new[]
            {
                ("correlatedsubjectchanged", "correlatedsubjectchanged"), ("deliveryprioritycode", "DeliveryPriorityCode"),
                ("followemailuserpreference", "FollowEmailUserPreference"), ("isduplicatesenderunresolved", "IsDuplicateSenderUnresolved"),
                ("notifications", "Notifications"),
            });
        foreach (var flag in new[] { "correlatedsubjectchanged", "followemailuserpreference", "isduplicatesenderunresolved" })
            email.Attributes.OfType<BooleanAttributeMetadata>().Single(a => a.LogicalName == flag).DefaultValue.Should().BeFalse(flag);
        email.Attributes.OfType<PicklistAttributeMetadata>().Single(a => a.LogicalName == "deliveryprioritycode").DefaultFormValue.Should().Be(1);
        email.Attributes.OfType<PicklistAttributeMetadata>().Single(a => a.LogicalName == "notifications").DefaultFormValue.Should().Be(0);
    }

    [Fact]
    public void Common_attributes_have_the_a2_names()
    {
        ActivitySchema.CommonAttributes().Select(a => a.LogicalName).Should().BeEquivalentTo(
            "activitytypecode", "description", "regardingobjectid", "regardingobjecttypecode", "scheduledstart", "scheduledend",
            "actualstart", "actualend", "scheduleddurationminutes", "actualdurationminutes", "prioritycode", "isbilled",
            "isworkflowcreated", "isregularactivity", "transactioncurrencyid", "exchangerate");
        var regarding = ActivitySchema.CommonAttributes().OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "regardingobjectid");
        regarding.AttributeType.Should().Be(AttributeTypeCode.Lookup);
        ActivitySchema.CommonAttributes().OfType<EntityNameAttributeMetadata>().Single(a => a.LogicalName == "regardingobjecttypecode")
            .AttributeOf.Should().Be("regardingobjectid");
        ActivitySchema.CommonAttributes().OfType<EntityNameAttributeMetadata>().Single(a => a.LogicalName == "activitytypecode")
            .AttributeOf.Should().BeNull();
        // Each call builds new instances (callers change them).
        ActivitySchema.CommonAttributes()[0].Should().NotBeSameAs(ActivitySchema.CommonAttributes()[0]);
    }

    [Fact]
    public void Managed_lookups()
    {
        ActivitySchema.IsManagedLookup("email", "regardingobjectid").Should().BeTrue();
        ActivitySchema.IsManagedLookup("xv_sms", "regardingobjectid").Should().BeTrue();
        ActivitySchema.IsManagedLookup("activitypointer", "regardingobjectid").Should().BeTrue();
        ActivitySchema.IsManagedLookup("annotation", "objectid").Should().BeTrue();
        ActivitySchema.IsManagedLookup("activityparty", "partyid").Should().BeTrue();
        ActivitySchema.IsManagedLookup("activityparty", "activityid").Should().BeTrue();
        // team.regardingobjectid belongs to the access teams; audit's lookups name any record.
        ActivitySchema.IsManagedLookup("team", "regardingobjectid").Should().BeFalse();
        ActivitySchema.IsManagedLookup("audit", "regardingobjectid").Should().BeFalse();
        ActivitySchema.IsManagedLookup("fileattachment", "objectid").Should().BeFalse();
        ActivitySchema.IsManagedLookup("account", "parentaccountid").Should().BeFalse();
    }

    /// <summary>
    /// Final review M5: every seeded table with a <c>regardingobjectid</c>. Both overloads agree, and only the activity tables and
    /// the pointer, <c>post</c> and <c>postregarding</c> have a managed one (<c>asyncoperation</c>'s is a UniqueIdentifier, <c>duplicaterulecondition</c>'s a lookup
    /// to its rule, <c>team</c>'s and <c>audit</c>'s are their own).
    /// </summary>
    [Fact]
    public void Managed_regarding_lookup_on_every_seeded_table()
    {
        var withRegarding = typeof(SystemSeed).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Xrmd.Metadata.Seed.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n["Xrmd.Metadata.Seed.".Length..^".json".Length])
            .Select(name => (Name: name, Definition: TryDefinition(name)))
            .Where(t => t.Definition?.Attributes?.Any(a => a.LogicalName == "regardingobjectid") == true)
            .ToList();
        withRegarding.Select(t => t.Name).Should().Contain(["activitypointer", "email", "task", "asyncoperation", "team", "audit", "duplicaterulecondition"]);
        foreach (var (name, definition) in withRegarding)
        {
            // Plan 3.5 UI-1g: post and postregarding have a managed regardingobjectid too.
            var expected = name is "activitypointer" or "post" or "postregarding" || definition!.IsActivity == true;
            ActivitySchema.IsManagedLookup(name, "regardingobjectid").Should().Be(expected, name);
            var attribute = definition!.Attributes.Single(a => a.LogicalName == "regardingobjectid");
            typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.EntityLogicalName))!.GetSetMethod(true)!.Invoke(attribute, [name]);
            ActivitySchema.IsManagedLookup(attribute).Should().Be(expected && attribute is LookupAttributeMetadata, name);
        }
    }

    private static EntityMetadata? TryDefinition(string name)
    {
        try
        {
            return SeedDefinition(name);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            return null; // not a table definition
        }
    }

    /// <summary>A12: every activity table and the pointer share the stem Activity, annotation has Note, any other table none.</summary>
    [Fact]
    public void Stem_is_activity_for_activity_tables()
    {
        ActivitySchema.PrivilegeStem(Table("activitypointer", false)).Should().Be("Activity");
        ActivitySchema.PrivilegeStem(Table("email", true)).Should().Be("Activity");
        ActivitySchema.PrivilegeStem(Table("xv_sms", true)).Should().Be("Activity");
        ActivitySchema.PrivilegeStem(Table("annotation")).Should().Be("Note");
        ActivitySchema.PrivilegeStem(Table("account", false)).Should().BeNull();
        ActivitySchema.PrivilegeStem(Table("activityparty")).Should().BeNull();
    }

    private static EntityMetadata Typed(string name, string schemaName, int objectTypeCode, bool? isActivity = null)
    {
        var e = Table(name, isActivity);
        e.SchemaName = schemaName;
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.GetSetMethod(true)!.Invoke(e, [objectTypeCode]);
        return e;
    }

    private static void Cascades(OneToManyRelationshipMetadata r, CascadeType assign, CascadeType delete, CascadeType merge, CascadeType reparent,
        CascadeType share, CascadeType unshare)
    {
        var c = r.CascadeConfiguration;
        (c.Assign, c.Delete, c.Merge, c.Reparent, c.Share, c.Unshare, c.Archive, c.RollupView).Should().Be(
            (assign, delete, merge, reparent, share, unshare, CascadeType.NoCascade, CascadeType.NoCascade), r.SchemaName);
    }

    /// <summary>Global Constraints: the regarding relationships, their navigation properties and cascades (task 2).</summary>
    [Fact]
    public void Regarding_relationships_have_the_dataverse_names()
    {
        var account = Typed("account", "Account", 1);
        var contact = Typed("contact", "Contact", 2);
        var project = Typed("xv_project", "xv_Project", 10001);
        var email = Typed("email", "Email", 4202, true);
        var pointer = Typed("activitypointer", "ActivityPointer", 4200, false);
        var sms = Typed("xv_sms", "xv_sms", 10002, true);

        var r = ActivitySchema.RegardingRelationship(account, email);
        (r.SchemaName, r.ReferencedEntity, r.ReferencingEntity, r.ReferencingAttribute, r.ReferencingEntityNavigationPropertyName,
            r.ReferencedEntityNavigationPropertyName, r.IsCustomRelationship).Should().Be(
            ("Account_Emails", "account", "email", "regardingobjectid", "regardingobjectid_account_email", "Account_Emails", false));
        Cascades(r, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade);

        var p = ActivitySchema.RegardingRelationship(account, pointer);
        (p.SchemaName, p.ReferencingEntityNavigationPropertyName).Should().Be(("Account_ActivityPointers", "regardingobjectid_account"));
        Cascades(p, CascadeType.NoCascade, CascadeType.RemoveLink, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade);

        ActivitySchema.RegardingRelationship(contact, Typed("task", "Task", 4212, true)).SchemaName.Should().Be("Contact_Tasks");
        ActivitySchema.RegardingRelationship(contact, Typed("phonecall", "PhoneCall", 4210, true)).SchemaName.Should().Be("Contact_Phonecalls");
        ActivitySchema.RegardingRelationship(account, Typed("appointment", "Appointment", 4201, true)).SchemaName.Should().Be("Account_Appointments");
        // A custom target is named by its logical name, a custom activity by its collection name.
        ActivitySchema.RegardingRelationship(project, email).SchemaName.Should().Be("xv_project_Emails");
        ActivitySchema.CollectionSchemaName(sms).Should().Be("xv_smss");
        var custom = ActivitySchema.RegardingRelationship(account, sms);
        (custom.SchemaName, custom.ReferencingEntityNavigationPropertyName).Should().Be(("Account_xv_smss", "regardingobjectid_account_xv_sms"));
    }

    [Fact]
    public void Notes_and_party_relationships_have_the_dataverse_names()
    {
        var n = ActivitySchema.NotesRelationship(Typed("account", "Account", 1));
        (n.SchemaName, n.ReferencedEntity, n.ReferencingEntity, n.ReferencingAttribute, n.ReferencingEntityNavigationPropertyName).Should().Be(
            ("Account_Annotation", "account", "annotation", "objectid", "objectid_account"));
        Cascades(n, CascadeType.Cascade, CascadeType.Cascade, CascadeType.NoCascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade);
        ActivitySchema.NotesRelationship(Typed("email", "Email", 4202, true)).SchemaName.Should().Be("Email_Annotation");
        ActivitySchema.NotesRelationship(Typed("xv_project", "xv_Project", 10001)).SchemaName.Should().Be("xv_project_Annotations");

        var e = ActivitySchema.PartiesRelationship(Typed("email", "Email", 4202, true));
        (e.SchemaName, e.ReferencedEntity, e.ReferencingEntity, e.ReferencingAttribute, e.ReferencingEntityNavigationPropertyName).Should().Be(
            ("email_activity_parties", "email", "activityparty", "activityid", "activityid_email"));
        Cascades(e, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade);
        ActivitySchema.PartiesRelationship(Typed("xv_sms", "xv_sms", 10002, true)).ReferencingEntityNavigationPropertyName
            .Should().Be("activityid_xv_sms_activityparty");

        // The seeded relationships of activityparty are the ones these rules give.
        var seeded = SeedDefinition("activityparty").ManyToOneRelationships;
        foreach (var expected in new[]
                 {
                     ActivitySchema.PartiesRelationship(Typed("activitypointer", "ActivityPointer", 4200, false)),
                     ActivitySchema.PartyTargetRelationship("account"), ActivitySchema.PartyTargetRelationship("contact"),
                     ActivitySchema.PartyTargetRelationship("systemuser"),
                 })
        {
            var s = seeded.Single(x => x.SchemaName == expected.SchemaName);
            (s.ReferencedEntity, s.ReferencingAttribute, s.ReferencingEntityNavigationPropertyName).Should().Be(
                (expected.ReferencedEntity, expected.ReferencingAttribute, expected.ReferencingEntityNavigationPropertyName), expected.SchemaName);
            var (sc, ec) = (s.CascadeConfiguration, expected.CascadeConfiguration);
            (sc.Assign, sc.Delete, sc.Merge, sc.Reparent, sc.Share, sc.Unshare).Should().Be(
                (ec.Assign, ec.Delete, ec.Merge, ec.Reparent, ec.Share, ec.Unshare), expected.SchemaName);
        }
        ActivitySchema.PartyTargetRelationship("systemuser").SchemaName.Should().Be("system_user_activity_parties");
    }

    /// <summary>A4: what a custom activity gets besides the caller's columns.</summary>
    [Fact]
    public void Custom_activity_attributes()
    {
        var attributes = ActivitySchema.CustomActivityAttributes();
        attributes.Select(a => a.LogicalName).Should().Contain(ActivitySchema.CommonAttributes().Select(a => a.LogicalName));
        attributes.Where(ActivitySchema.IsPartyList).Select(a => a.LogicalName).Should().Equal(
            "from", "to", "cc", "bcc", "requiredattendees", "optionalattendees", "organizer", "customers", "resources");
        attributes.OfType<LookupAttributeMetadata>().Where(ActivitySchema.IsPartyList)
            .Should().OnlyContain(l => l.Targets.SequenceEqual(new[] { "account", "contact", "systemuser" }));
        var state = attributes.OfType<StateAttributeMetadata>().Single();
        state.OptionSet.Options.Cast<StateOptionMetadata>().Select(o => (o.Value, o.DefaultStatus)).Should().Equal((0, 1), (1, 2), (2, 3));
        var status = attributes.OfType<StatusAttributeMetadata>().Single();
        status.OptionSet.Options.Cast<StatusOptionMetadata>().Select(o => (o.Value, o.State)).Should().Equal((1, 0), (2, 1), (3, 2));
        ActivitySchema.IsGeneratedActivityAttribute("description").Should().BeTrue();
        ActivitySchema.IsGeneratedActivityAttribute("to").Should().BeTrue();
        ActivitySchema.IsGeneratedActivityAttribute("xv_note").Should().BeFalse();
    }

    /// <summary>The functional properties a seed column shares with its <see cref="ActivitySchema.CommonAttributes"/> definition.</summary>
    private static void SameDefinition(AttributeMetadata seeded, AttributeMetadata common, string because)
    {
        seeded.SchemaName.Should().Be(common.SchemaName, because);
        (seeded.RequiredLevel?.Value ?? AttributeRequiredLevel.None).Should().Be(common.RequiredLevel?.Value ?? AttributeRequiredLevel.None, because);
        (seeded.IsValidForCreate ?? true).Should().Be(common.IsValidForCreate ?? true, because);
        (seeded.IsValidForUpdate ?? true).Should().Be(common.IsValidForUpdate ?? true, because);
        switch (seeded, common)
        {
            case (BooleanAttributeMetadata b, BooleanAttributeMetadata c):
                (b.DefaultValue ?? false).Should().Be(c.DefaultValue ?? false, because);
                break;
            case (IntegerAttributeMetadata i, IntegerAttributeMetadata c):
                (i.MinValue, i.MaxValue).Should().Be((c.MinValue, c.MaxValue), because);
                break;
            case (DecimalAttributeMetadata d, DecimalAttributeMetadata c):
                (d.Precision, d.MinValue, d.MaxValue).Should().Be((c.Precision, c.MinValue, c.MaxValue), because);
                break;
            case (PicklistAttributeMetadata p, PicklistAttributeMetadata c):
                p.DefaultFormValue.Should().Be(c.DefaultFormValue, because);
                p.OptionSet.Options.Select(o => o.Value).Should().Equal(c.OptionSet.Options.Select(o => o.Value), because);
                break;
            case (LookupAttributeMetadata l, LookupAttributeMetadata c):
                l.Targets.Should().Equal(c.Targets, because);
                l.AttributeType.Should().Be(c.AttributeType, because);
                break;
            case (DateTimeAttributeMetadata d, DateTimeAttributeMetadata c):
                d.Format.Should().Be(c.Format, because);
                break;
        }
    }
}
