using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The participation types of an <c>activityparty</c> row (global choice <c>activityparty_participationtypemask</c>, Learn).
/// </summary>
public enum ParticipationType
{
    Sender = 1,
    ToRecipient,
    CcRecipient,
    BccRecipient,
    RequiredAttendee,
    OptionalAttendee,
    Organizer,
    Regarding,
    Owner,
    Resource,
    Customer,
    ChatParticipant,
    Related,
}

/// <summary>
/// The activity model (plan 2.11): the seeded tables <c>activitypointer</c>, <c>activityparty</c>, <c>annotation</c> and the four
/// standard activities, their columns, party lists and states, the lookups whose targets the metadata store maintains, and the
/// two settings columns of <c>systemuser</c> and <c>organization</c>.
/// </summary>
/// <remarks>
/// The object type codes, the party list columns of task and of custom activities, the states of custom activities and of the
/// pointer and the privilege depths are from memory, not from Learn: the plan 2.11 checklist verifies them on a real environment.
/// </remarks>
public static partial class ActivitySchema
{
    /// <summary>The tables.</summary>
    public const string Pointer = "activitypointer", Party = "activityparty", Annotation = "annotation",
        Email = "email", Task = "task", PhoneCall = "phonecall", Appointment = "appointment";

    /// <summary>The object type codes of the tables (from memory: checklist 1a).</summary>
    public const int AnnotationObjectTypeCode = 5, PartyObjectTypeCode = 135, PointerObjectTypeCode = 4200,
        AppointmentObjectTypeCode = 4201, EmailObjectTypeCode = 4202, PhoneCallObjectTypeCode = 4210, TaskObjectTypeCode = 4212;

    /// <summary>The object type codes by table.</summary>
    public static readonly IReadOnlyDictionary<string, int> ObjectTypeCodes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Annotation] = AnnotationObjectTypeCode, [Party] = PartyObjectTypeCode, [Pointer] = PointerObjectTypeCode,
        [Appointment] = AppointmentObjectTypeCode, [Email] = EmailObjectTypeCode, [PhoneCall] = PhoneCallObjectTypeCode,
        [Task] = TaskObjectTypeCode,
    };

    /// <summary>The four standard activity tables.</summary>
    public static readonly IReadOnlyList<string> StandardActivities = [Email, Task, PhoneCall, Appointment];

    /// <summary>Columns of the activities (and the pointer).</summary>
    public const string ActivityId = "activityid", ActivityTypeCode = "activitytypecode", Subject = "subject",
        RegardingObjectId = "regardingobjectid", RegardingTypeCode = "regardingobjecttypecode";

    /// <summary>Columns of <see cref="Party"/>.</summary>
    public const string ActivityPartyId = "activitypartyid", PartyId = "partyid", PartyTypeCode = "partyobjecttypecode",
        PartyIdName = "partyidname", ParticipationTypeMask = "participationtypemask", AddressUsed = "addressused",
        IsPartyDeleted = "ispartydeleted";

    /// <summary>Columns of <see cref="Annotation"/>.</summary>
    public const string ObjectId = "objectid", ObjectTypeCode = "objecttypecode", DocumentBody = "documentbody", FileName = "filename",
        FileSize = "filesize", MimeType = "mimetype", IsDocument = "isdocument", NoteText = "notetext";

    /// <summary>The organization's limit on a note body, in bytes.</summary>
    public const string MaxUploadFileSize = "maxuploadfilesize";

    /// <summary>The primary email address of a user (the <c>addressused</c> of a user party).</summary>
    public const string InternalEmailAddress = "internalemailaddress";

    /// <summary>The default of <see cref="MaxUploadFileSize"/> (5 MB) and its highest value (range from memory: checklist 1e).</summary>
    public const int DefaultMaxUploadFileSize = 5242880, MaxMaxUploadFileSize = 131072000;

    /// <summary>The longest <see cref="AddressUsed"/> (Learn).</summary>
    public const int AddressUsedMaxLength = 320;

    /// <summary>The shared privilege stems (A12).</summary>
    public const string ActivityStem = "Activity", NoteStem = "Note";

    /// <summary>The activity message xrmverse implements (A17): it changes the email's state, no email is sent before 4.1.</summary>
    public const string SendEmailMessage = "SendEmail";

    /// <summary>The message that hands out the next tracking token of the organization (plan 4.1b, task 7).</summary>
    public const string GetTrackingTokenEmailMessage = "GetTrackingTokenEmail";

    private const string SchedulingReason =
        "appointment scheduling and recurrence are not replicated; create or update the appointment instead";
    private const string EmailReason =
        "email templates, background sending and incoming email processing are not replicated yet; send an email with SendEmail";

    /// <summary>
    /// The activity and note messages a caller could reach that xrmverse refuses explicitly (A18), with the reason given in the
    /// refusal. They are answered "not supported" before any custom message of the organization is looked up, and no custom API
    /// can take their names (pre-flight C15). The queue messages are implemented (plan 4.1a, <c>QueueSchema.Messages</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RefusedMessageReasons =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Book"] = SchedulingReason,
            ["Reschedule"] = SchedulingReason,
            ["AddRecurrence"] = SchedulingReason,
            ["DeliverPromoteEmail"] = EmailReason,
            ["DeliverIncomingEmail"] = EmailReason,
            ["CheckIncomingEmail"] = EmailReason,
            ["CheckPromoteEmail"] = EmailReason,
            ["BackgroundSendEmail"] = EmailReason,
            ["SendEmailFromTemplate"] = EmailReason,
            ["SendTemplate"] = EmailReason,
            ["InstantiateTemplate"] = EmailReason,
            ["Rollup"] = "activity rollups are not replicated; query activitypointer by regardingobjectid instead",
            ["Restore"] = "deleted records cannot be restored in xrmverse",
            ["AppendRelatedParty"] = "party rows are written only through the party list columns of their activity",
            ["InitializeAnnotationBlocksUpload"] =
                "chunked note attachments are not supported; write annotation.documentbody (base64) with Create or Update",
            ["InitializeAnnotationBlocksDownload"] =
                "chunked note attachments are not supported; read annotation.documentbody (base64) with Retrieve or RetrieveMultiple",
            ["InitializeAttachmentBlocksUpload"] =
                "chunked email attachments are not supported; write activitymimeattachment.body (base64) with Create or Update",
            ["InitializeAttachmentBlocksDownload"] =
                "chunked email attachments are not supported; read activitymimeattachment.body (base64) with Retrieve or RetrieveMultiple",
        };

    /// <summary>The names of <see cref="RefusedMessageReasons"/>, in a stable order.</summary>
    public static readonly IReadOnlyList<string> RefusedMessages = [.. RefusedMessageReasons.Keys];

    /// <summary>True for an activity or note message xrmverse refuses (case-insensitive).</summary>
    public static bool IsRefusedMessage(string? name) => name is not null && RefusedMessageReasons.ContainsKey(name);

    /// <summary>The refusal text of the refused message <paramref name="name"/>.</summary>
    public static string RefusedMessageText(string name) =>
        $"The message '{name}' is not supported by xrmverse: {RefusedMessageReasons[name]}.";

    /// <summary>A table made as an activity (<c>IsActivity</c> true): the four standard ones and the custom ones, not the pointer.</summary>
    public static bool IsActivityTable(TableDefinition e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.IsActivity == true && e.LogicalName != Pointer;
    }

    /// <summary>
    /// The privilege stem a table shares (A12): <see cref="ActivityStem"/> for the pointer and every activity table,
    /// <see cref="NoteStem"/> for <see cref="Annotation"/>, <see cref="PostSchema.PrivilegeStem"/> for <c>post</c>,
    /// <c>postcomment</c> and <c>postregarding</c> (plan 3.5 UI-1g), null for any other table (its own SchemaName).
    /// </summary>
    public static string? PrivilegeStem(TableDefinition e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.LogicalName == Pointer || IsActivityTable(e)) return ActivityStem;
        if (PostSchema.IsPostTable(e.LogicalName)) return PostSchema.PrivilegeStem;
        return e.LogicalName == Annotation ? NoteStem : null;
    }

    /// <summary>
    /// The lookups whose targets the metadata store maintains (pre-flight C1): <c>regardingobjectid</c> of an activity table, of
    /// the pointer, of <c>post</c> and of <c>postregarding</c> (plan 3.5 UI-1g), <c>annotation.objectid</c>, <c>queueitem.objectid</c> (plan 4.1a),
    /// <c>activityparty.partyid</c> and <c>activityparty.activityid</c>. They keep a type column whatever the number of their targets.
    /// A <c>regardingobjectid</c> outside the seeded tables that have their own
    /// (<c>team</c> for access teams, <c>audit</c>, <c>asyncoperation</c>, whose column is a UniqueIdentifier, and
    /// <c>duplicaterulecondition</c>, a lookup to its rule; <c>mailbox</c>, a lookup to its user or queue) can only be an activity's or a post's: the name has no publisher prefix, so no
    /// custom column takes it. A guard test (<c>ActivitySchemaTests</c>) checks every seeded table with the column.
    /// </summary>
    public static bool IsManagedLookup(string? entityLogicalName, string? attributeLogicalName) =>
        (entityLogicalName, attributeLogicalName) switch
        {
            (null, _) or (_, null) => false,
            (AccessTeamSchema.Team or AuditTable.Name or "asyncoperation" or DuplicateDetectionSchema.Condition or MailSchema.Mailbox, RegardingObjectId) => false,
            (_, RegardingObjectId) => true,
            (Annotation or QueueSchema.QueueItem, ObjectId) => true,
            (Party, PartyId or ActivityId) => true,
            _ => false,
        };

    /// <summary><see cref="IsManagedLookup(string?, string?)"/> of a lookup attribute (its <c>EntityLogicalName</c> set).</summary>
    public static bool IsManagedLookup(ColumnDefinition a) =>
        a is LookupColumn && IsManagedLookup(a.EntityLogicalName, a.LogicalName);

    /// <summary>True for <c>annotation.documentbody</c>, which has no SQL column: the body lives in the file store (A16).</summary>
    public static bool IsDocumentBody(ColumnDefinition a) => a.EntityLogicalName == Annotation && a.LogicalName == DocumentBody;

    /// <summary>
    /// True for <c>activitymimeattachment.body</c> (plan 4.1b), which has no SQL column either: the body lives in the file store, as
    /// a note's does.
    /// </summary>
    public static bool IsAttachmentBody(ColumnDefinition a) => a.EntityLogicalName == MailSchema.Attachment && a.LogicalName == MailSchema.AttachmentBody;

    /// <summary>True for a party list column (AttributeType PartyList): it has no SQL column, its rows live in <see cref="Party"/>.</summary>
    public static bool IsPartyList(ColumnDefinition? a) => a is LookupColumn { AttributeType: ColumnTypeCode.PartyList };

    /// <summary>
    /// The party list columns of <paramref name="activity"/> with their participation type and whether they take one party only
    /// (Global Constraints): the four standard tables by name, a custom activity the nine custom ones, any other table none.
    /// </summary>
    public static IReadOnlyList<(string Attribute, ParticipationType Type, bool Single)> PartyLists(TableDefinition activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return activity.LogicalName switch
        {
            Email => [("from", ParticipationType.Sender, true), ("to", ParticipationType.ToRecipient, false),
                ("cc", ParticipationType.CcRecipient, false), ("bcc", ParticipationType.BccRecipient, false),
                ("related", ParticipationType.Related, false)],
            PhoneCall => [("from", ParticipationType.Sender, true), ("to", ParticipationType.ToRecipient, false),
                ("related", ParticipationType.Related, false)],
            Appointment => [("requiredattendees", ParticipationType.RequiredAttendee, false),
                ("optionalattendees", ParticipationType.OptionalAttendee, false), ("organizer", ParticipationType.Organizer, true)],
            Task or Pointer => [],
            _ when IsActivityTable(activity) => [("from", ParticipationType.Sender, true), ("to", ParticipationType.ToRecipient, false),
                ("cc", ParticipationType.CcRecipient, false), ("bcc", ParticipationType.BccRecipient, false),
                ("requiredattendees", ParticipationType.RequiredAttendee, false),
                ("optionalattendees", ParticipationType.OptionalAttendee, false), ("organizer", ParticipationType.Organizer, true),
                ("customers", ParticipationType.Customer, false), ("resources", ParticipationType.Resource, false)],
            _ => [],
        };
    }

    /// <summary>
    /// The states of an activity table with their default status and statuses (Global Constraints): the four standard tables by
    /// name, the pointer its four states, any other table (a custom activity) Open, Completed and Canceled.
    /// </summary>
    public static IReadOnlyList<(int State, int DefaultStatus, int[] Statuses)> States(string table) => table switch
    {
        Email => [(0, 1, [1, 8]), (1, 2, [2, 3, 4, 6, 7]), (2, 5, [5])],
        Task => [(0, 2, [2, 3, 4, 7]), (1, 5, [5]), (2, 6, [6])],
        PhoneCall => [(0, 1, [1]), (1, 2, [2, 4]), (2, 3, [3])],
        Appointment => [(0, 1, [1, 2]), (1, 3, [3]), (2, 4, [4]), (3, 5, [5, 6])],
        Pointer => [(0, 1, [1]), (1, 2, [2]), (2, 3, [3]), (3, 4, [4])],
        _ => [(0, 1, [1]), (1, 2, [2]), (2, 3, [3])],
    };

    /// <summary>
    /// The common activity columns of ruling A2 other than the primary key, the primary name (<c>subject</c>), state, status and
    /// the owner and system columns, as new instances: the seed files of the pointer and of the four standard tables carry the
    /// same definitions, and a custom activity gets them at creation.
    /// </summary>
    public static IReadOnlyList<ColumnDefinition> CommonColumns()
    {
        var typeCode = new EntityNameColumn { SchemaName = "ActivityTypeCode", LogicalName = ActivityTypeCode, DisplayName = Label("Activity Type"), IsValidForCreate = false, IsValidForUpdate = false,
            RequiredLevel = ModelFactory.Required(RequirementLevel.SystemRequired),
        };
        var regarding = new LookupColumn
        {
            LogicalName = RegardingObjectId, SchemaName = "RegardingObjectId", DisplayName = Label("Regarding"), Targets = [],
        };
        var regardingType = new EntityNameColumn { SchemaName = "RegardingObjectTypeCode", LogicalName = RegardingTypeCode };
        regardingType.AttributeOf = RegardingObjectId;
        var currency = SystemAttributes.CurrencyLookupColumn();
        var rate = SystemAttributes.ExchangeRateColumn();
        return
        [
            typeCode,
            new MemoColumn { SchemaName = "Description", LogicalName = "description", DisplayName = Label("Description"), MaxLength = 2000 },
            regarding,
            regardingType,
            Date("scheduledstart", "ScheduledStart", "Start Date"),
            Date("scheduledend", "ScheduledEnd", "Due Date"),
            Date("actualstart", "ActualStart", "Actual Start"),
            Date("actualend", "ActualEnd", "Actual End"),
            new IntegerColumn { SchemaName = "ScheduledDurationMinutes", LogicalName = "scheduleddurationminutes", DisplayName = Label("Scheduled Duration"), MinValue = 0, MaxValue = int.MaxValue,
            },
            new IntegerColumn { SchemaName = "ActualDurationMinutes", LogicalName = "actualdurationminutes", DisplayName = Label("Actual Duration"), MinValue = 0, MaxValue = int.MaxValue,
            },
            new ChoiceColumn { SchemaName = "PriorityCode", LogicalName = "prioritycode", DisplayName = Label("Priority"), DefaultFormValue = 1,
                OptionSet = new ChoiceSet
                {
                    Options = [ModelFactory.Option(Label("Low"), 0), ModelFactory.Option(Label("Normal"), 1), ModelFactory.Option(Label("High"), 2)],
                    IsGlobal = false, OptionSetType = ChoiceSetKind.Picklist,
                },
            },
            Flag("isbilled", "IsBilled", "Billed", validForWrite: true),
            Flag("isworkflowcreated", "IsWorkflowCreated", "Created by Workflow", validForWrite: true),
            Flag("isregularactivity", "IsRegularActivity", "Regular Activity", validForWrite: false, defaultValue: true),
            currency,
            rate,
        ];
    }

    /// <summary>The SchemaNames of the party list columns a custom activity gets (from memory: checklist 1b).</summary>
    private static readonly IReadOnlyDictionary<string, (string SchemaName, string Label)> CustomPartyLists =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["from"] = ("From", "From"), ["to"] = ("To", "To"), ["cc"] = ("Cc", "Cc"), ["bcc"] = ("Bcc", "Bcc"),
            ["requiredattendees"] = ("RequiredAttendees", "Required Attendees"), ["optionalattendees"] = ("OptionalAttendees", "Optional Attendees"),
            ["organizer"] = ("Organizer", "Organizer"), ["customers"] = ("Customers", "Customers"), ["resources"] = ("Resources", "Resources"),
        };

    /// <summary>
    /// The tables a party list names by default, and those of a custom activity's party lists (A10: <c>knowledgearticle</c> is not
    /// modelled). Plan 4.1b adds <c>queue</c> to the party lists of <c>email</c> (<see cref="PartyRecordTables"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> PartyTargets = ["account", "contact", "systemuser"];

    /// <summary>
    /// The tables <c>activityparty.partyid</c> names: <see cref="PartyTargets"/> and, since plan 4.1b, <c>queue</c> (the sender or a
    /// recipient of an email; the email party lists target it, the others do not).
    /// </summary>
    public static readonly IReadOnlyList<string> PartyRecordTables = ["account", "contact", "queue", "systemuser"];

    /// <summary>
    /// What a custom activity gets besides the caller's columns (A4): the common columns (<see cref="CommonAttributes"/>), the
    /// nine party lists of a custom activity and its own <c>statecode</c>/<c>statuscode</c> (Open, Completed, Canceled), as new
    /// instances. The metadata store adds them on its own path: callers cannot create EntityName or PartyList columns.
    /// </summary>
    public static IReadOnlyList<ColumnDefinition> CustomActivityColumns()
    {
        var list = new List<ColumnDefinition>(CommonColumns());
        foreach (var (logicalName, (schemaName, label)) in CustomPartyLists)
        {
            var party = new LookupColumn { LogicalName = logicalName, SchemaName = schemaName, DisplayName = Label(label), Targets = [.. PartyTargets] };
            party.AttributeType = ColumnTypeCode.PartyList;
            list.Add(party);
        }
        var stateLabels = new[] { "Open", "Completed", "Canceled" };
        var states = new List<ChoiceOption>();
        var statuses = new List<ChoiceOption>();
        foreach (var (state, defaultStatus, codes) in States(""))
        {
            states.Add(new StateOption { Value = state, Label = Label(stateLabels[state]), DefaultStatus = defaultStatus });
            foreach (var code in codes) statuses.Add(new StatusOption { Value = code, State = state, Label = Label(stateLabels[state]) });
        }
        list.Add(new StateColumn
        {
            LogicalName = "statecode", SchemaName = "StateCode", DisplayName = Label("Status"),
            RequiredLevel = ModelFactory.Required(RequirementLevel.SystemRequired),
            OptionSet = new ChoiceSet { Options = states, IsGlobal = false, OptionSetType = ChoiceSetKind.State },
        });
        list.Add(new StatusColumn
        {
            LogicalName = "statuscode", SchemaName = "StatusCode", DisplayName = Label("Status Reason"),
            OptionSet = new ChoiceSet { Options = statuses, IsGlobal = false, OptionSetType = ChoiceSetKind.Status },
        });
        return list;
    }

    private static readonly HashSet<string> GeneratedActivityNames = new(
        CommonColumns().Select(a => a.LogicalName!).Concat(CustomPartyLists.Keys).Append(ActivityId), StringComparer.Ordinal);

    /// <summary>
    /// True for a column name a custom activity gets from the server (A2 columns, party lists, <c>activityid</c>): a caller cannot
    /// define one, and the publisher prefix rule does not apply to it.
    /// </summary>
    public static bool IsGeneratedActivityAttribute(string? logicalName) => logicalName is not null && GeneratedActivityNames.Contains(logicalName);

    /// <summary>
    /// The collection SchemaName an activity table gives its regarding relationships (<c>Account_Emails</c>): the Dataverse names of
    /// the pointer and of the four standard tables (Learn), else the table's <c>CollectionSchemaName</c>, else its SchemaName and
    /// <c>s</c> (from memory: checklist 2a).
    /// </summary>
    public static string CollectionSchemaName(TableDefinition activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return activity.LogicalName switch
        {
            Pointer => "ActivityPointers",
            Email => "Emails",
            Task => "Tasks",
            PhoneCall => "Phonecalls",
            Appointment => "Appointments",
            _ => activity.CollectionSchemaName is { Length: > 0 } collection ? collection : (activity.SchemaName ?? activity.LogicalName) + "s",
        };
    }

    /// <summary>A seeded table is named by its SchemaName in a relationship name (<c>Account_</c>), a custom one by its logical name.</summary>
    internal static string RelationshipStem(TableDefinition table) =>
        MetadataStore.IsSystemEntity(table) ? (table.SchemaName ?? table.LogicalName)! : table.LogicalName!;

    /// <summary>
    /// The relationship of <paramref name="activity"/>'s <c>regardingobjectid</c> to <paramref name="target"/> (Global Constraints):
    /// <c>{target}_{collection}</c> (<c>Account_Emails</c>), navigation <c>regardingobjectid_{target}</c> on the pointer and
    /// <c>regardingobjectid_{target}_{activity}</c> elsewhere; Assign, Delete, Merge, Reparent, Share and Unshare Cascade to an
    /// activity table, everything NoCascade but Delete RemoveLink to the pointer. The stem pattern (SchemaName of a seeded table,
    /// as <c>Contact_{collection}</c>) and <c>{logicalname}_{collection}</c> for a custom table are from memory: checklist 2a.
    /// </summary>
    public static OneToManyRelationship RegardingRelationship(TableDefinition target, TableDefinition activity)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(activity);
        var pointer = activity.LogicalName == Pointer;
        return RelationshipDefinition($"{RelationshipStem(target)}_{CollectionSchemaName(activity)}", target.LogicalName!, activity.LogicalName!, RegardingObjectId,
            pointer ? $"{RegardingObjectId}_{target.LogicalName}" : $"{RegardingObjectId}_{target.LogicalName}_{activity.LogicalName}",
            pointer ? CascadeSettings(delete: CascadeAction.RemoveLink) : CascadeSettings(CascadeAction.Cascade, CascadeAction.Cascade, CascadeAction.Cascade, CascadeAction.Cascade,
                CascadeAction.Cascade, CascadeAction.Cascade));
    }

    /// <summary>
    /// The relationship of <c>annotation.objectid</c> to <paramref name="target"/>: <c>{SchemaName}_Annotation</c> for a seeded
    /// table (<c>Account_Annotation</c>, <c>Email_Annotation</c>), <c>{logicalname}_Annotations</c> for a custom one (from memory:
    /// checklist 2a); navigation <c>objectid_{target}</c>; Assign, Delete, Reparent, Share and Unshare Cascade.
    /// </summary>
    public static OneToManyRelationship NotesRelationship(TableDefinition target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = MetadataStore.IsSystemEntity(target) ? $"{RelationshipStem(target)}_Annotation" : $"{target.LogicalName}_Annotations";
        return RelationshipDefinition(name, target.LogicalName!, Annotation, ObjectId, $"{ObjectId}_{target.LogicalName}",
            CascadeSettings(assign: CascadeAction.Cascade, delete: CascadeAction.Cascade, reparent: CascadeAction.Cascade, share: CascadeAction.Cascade,
                unshare: CascadeAction.Cascade));
    }

    /// <summary>
    /// The relationship of <c>activitymimeattachment.objectid</c> to <paramref name="target"/> (the pointer or <c>email</c>, plan 4.1b,
    /// ruling P4): <c>{target}_activity_mime_attachments</c>, navigation <c>objectid_{target}</c>; Delete Cascade (an email's
    /// attachments go with it), everything else NoCascade. The name is from memory: checklist 2a.
    /// </summary>
    public static OneToManyRelationship AttachmentRelationship(TableDefinition target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return RelationshipDefinition($"{target.LogicalName}_activity_mime_attachments", target.LogicalName!, MailSchema.Attachment, ObjectId,
            $"{ObjectId}_{target.LogicalName}", CascadeSettings(delete: CascadeAction.Cascade));
    }

    /// <summary>
    /// The relationship of <c>activityparty.activityid</c> to <paramref name="activity"/>: <c>{activity}_activity_parties</c>,
    /// navigation <c>activityid_{activity}</c> for the pointer and the four standard tables, <c>activityid_{activity}_activityparty</c>
    /// for a custom one; NoCascade, but Delete Cascade from the pointer (Learn). The custom navigation
    /// <c>activityid_{activity}_activityparty</c> is from memory: checklist 2a.
    /// </summary>
    public static OneToManyRelationship PartiesRelationship(TableDefinition activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var name = activity.LogicalName;
        var standard = name == Pointer || StandardActivities.Contains(name);
        return RelationshipDefinition($"{name}_activity_parties", name!, Party, ActivityId,
            standard ? $"{ActivityId}_{name}" : $"{ActivityId}_{name}_{Party}",
            CascadeSettings(delete: name == Pointer ? CascadeAction.Cascade : CascadeAction.NoCascade));
    }

    /// <summary>
    /// The relationship of <c>activityparty.partyid</c> to <paramref name="target"/> (one of <see cref="PartyTargets"/>):
    /// <c>account_activity_parties</c>, <c>contact_activity_parties</c>, <c>system_user_activity_parties</c> (Learn), navigation
    /// <c>partyid_{target}</c>, NoCascade (deleting a party record keeps its rows, A10).
    /// </summary>
    public static OneToManyRelationship PartyTargetRelationshipDefinition(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var name = target == "systemuser" ? "system_user_activity_parties" : $"{target}_activity_parties";
        var r = RelationshipDefinition(name, target, Party, PartyId, $"{PartyId}_{target}", CascadeSettings());
        r.ReferencedAttribute = target + "id";
        return r;
    }

    internal static OneToManyRelationship RelationshipDefinition(string schemaName, string referenced, string referencing, string attribute,
        string navigation, CascadeSettings cascade) => new()
    {
        SchemaName = schemaName,
        ReferencedEntity = referenced,
        ReferencingEntity = referencing,
        ReferencingAttribute = attribute,
        ReferencingEntityNavigationPropertyName = navigation,
        ReferencedEntityNavigationPropertyName = schemaName,
        IsCustomRelationship = false,
        IsHierarchical = false,
        CascadeConfiguration = cascade,
    };

    internal static CascadeSettings CascadeSettings(CascadeAction assign = CascadeAction.NoCascade, CascadeAction delete = CascadeAction.NoCascade,
        CascadeAction merge = CascadeAction.NoCascade, CascadeAction reparent = CascadeAction.NoCascade, CascadeAction share = CascadeAction.NoCascade,
        CascadeAction unshare = CascadeAction.NoCascade) => new()
    {
        Assign = assign, Delete = delete, Merge = merge, Reparent = reparent, Share = share, Unshare = unshare,
        Archive = CascadeAction.NoCascade, RollupView = CascadeAction.NoCascade,
    };

    private static DateTimeColumn Date(string logicalName, string schemaName, string label) =>
        new() { Format = DateTimeDisplayFormat.DateAndTime, SchemaName = schemaName, LogicalName = logicalName, DisplayName = Label(label) };

    private static BooleanColumn Flag(string logicalName, string schemaName, string label, bool validForWrite, bool defaultValue = false)
    {
        var a = new BooleanColumn { SchemaName = schemaName, LogicalName = logicalName, DisplayName = Label(label), DefaultValue = defaultValue };
        if (!validForWrite)
        {
            a.IsValidForCreate = false;
            a.IsValidForUpdate = false;
        }
        return a;
    }

    private static LabelSet Label(string text) => SystemAttributes.CreateLabelSet(text, 1033);
}
