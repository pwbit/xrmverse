using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The queue model (plan 4.1a): the seeded tables <c>queue</c>, <c>queueitem</c> and the intersect <c>queuemembership</c>, and the
/// relationship of <c>queueitem.objectid</c> to a queue-enabled table.
/// </summary>
/// <remarks>
/// The object type codes 2020, 2029 and 2027 are from memory, not from Learn: checklist 1. The type column of <c>objectid</c> is
/// <c>objectidtypecode</c> (an EntityName, as on Learn, which also lists a read-only choice <c>objecttypecode</c> that xrmverse
/// does not seed).
/// </remarks>
public static partial class QueueSchema
{
    /// <summary>The tables.</summary>
    public const string Queue = "queue", QueueItem = "queueitem", QueueMembership = "queuemembership";

    /// <summary>Columns of <see cref="QueueItem"/> (Learn: the type column of <c>objectid</c> is <c>objectidtypecode</c>).</summary>
    public const string ObjectId = "objectid", ObjectIdTypeCode = "objectidtypecode", WorkerId = "workerid",
        WorkerIdModifiedOn = "workeridmodifiedon";

    /// <summary>The many-to-many relationship between <see cref="Queue"/> and <c>systemuser</c> (Learn).</summary>
    public const string MembershipRelationshipName = "queuemembership_association";

    /// <summary>The seeded many-to-many relationship <see cref="MembershipRelationshipName"/> through <see cref="QueueMembership"/>.</summary>
    public static ManyToManyRelationship MembershipRelationship() => new()
    {
        SchemaName = MembershipRelationshipName,
        IntersectEntityName = QueueMembership,
        Entity1LogicalName = Queue,
        Entity1IntersectAttribute = QueueIdColumn,
        Entity1NavigationPropertyName = MembershipRelationshipName,
        Entity2LogicalName = "systemuser",
        Entity2IntersectAttribute = "systemuserid",
        Entity2NavigationPropertyName = MembershipRelationshipName,
        IsCustomRelationship = false,
    };

    /// <summary>The queue messages of plan 4.1a tasks 5 and 6 (names from the SDK and the Learn Web API reference).</summary>
    public const string AddToQueue = "AddToQueue", RemoveFromQueue = "RemoveFromQueue", RouteTo = "RouteTo",
        AddPrincipalToQueue = "AddPrincipalToQueue", PickFromQueue = "PickFromQueue", ReleaseToQueue = "ReleaseToQueue",
        RetrieveUserQueues = "RetrieveUserQueues";

    /// <summary>The queue messages xrmverse implements: core messages with message rows of their own (steps fire for every table).</summary>
    public static readonly IReadOnlyList<string> Messages =
        [AddToQueue, RemoveFromQueue, RouteTo, AddPrincipalToQueue, PickFromQueue, ReleaseToQueue, RetrieveUserQueues];

    /// <summary>The <c>queueid</c> column of <see cref="QueueItem"/>, <c>systemuser</c> and <c>team</c>.</summary>
    public const string QueueIdColumn = "queueid";

    /// <summary>The managed property name of the table setting <c>IsValidForQueue</c> (the export's <c>CanModifyQueueSettings</c>).</summary>
    public const string SettingManagedProperty = "canmodifyqueuesettings";

    /// <summary>The stored shape of the table setting <c>IsValidForQueue</c>: the value, changeable, with its managed property name.</summary>
    public static ManagedFlag SettingManagedFlag(bool value) =>
        new() { Value = value, CanBeChanged = true, ManagedPropertyLogicalName = SettingManagedProperty };

    /// <summary>The values of a default queue: Private (<c>queueviewtype</c> 1) and the one <c>queuetypecode</c> (1).</summary>
    public const int PrivateViewType = 1, TypeCode = 1;

    /// <summary>The value of <c>queueviewtype</c> of a public queue.</summary>
    public const int PublicViewType = 0;

    /// <summary>The <c>statuscode</c> of an inactive <see cref="QueueItem"/> (<c>statecode</c> 1).</summary>
    public const int InactiveItemStatus = 2;

    /// <summary>The longest <c>queue.name</c>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>
    /// The name of the default queue of a user (its full name) or of a team (its name): the name between angle brackets, cut so that
    /// the whole fits the column (from memory, plan checklist 4). A principal without a name gets <c>&lt;&gt;</c>.
    /// </summary>
    public static string DefaultQueueName(string? principalName)
    {
        var name = principalName ?? "";
        return "<" + (name.Length > NameMaxLength - 2 ? name[..(NameMaxLength - 2)] : name) + ">";
    }

    /// <summary>The status of a received email, which may be in several queues at once (Learn, queue tables).</summary>
    public const int ReceivedEmailStatus = 4;

    /// <summary>
    /// The relationship of <c>queueitem.objectid</c> to <paramref name="target"/> (Learn): <c>{SchemaName}_QueueItem</c> for a seeded
    /// activity table (<c>Email_QueueItem</c>, <c>ActivityPointer_QueueItem</c>), <c>{logicalname}_QueueItems</c> for any other
    /// (<c>knowledgearticle_QueueItems</c>, a custom activity: from memory, checklist 2); navigation <c>objectid_{target}</c>;
    /// Delete Cascade, everything else NoCascade.
    /// </summary>
    public static OneToManyRelationship ItemRelationship(TableDefinition target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var seededActivity = MetadataStore.IsSystemEntity(target)
            && (target.LogicalName == ActivitySchema.Pointer || ActivitySchema.StandardActivities.Contains(target.LogicalName));
        var name = seededActivity ? $"{ActivitySchema.RelationshipStem(target)}_QueueItem" : $"{target.LogicalName}_QueueItems";
        return ActivitySchema.RelationshipDefinition(name, target.LogicalName!, QueueItem, ObjectId, $"{ObjectId}_{target.LogicalName}",
            ActivitySchema.CascadeSettings(delete: CascadeAction.Cascade));
    }

    /// <summary>
    /// The system views the seed writes for the queue tables (plan 4.1a, task 7, ruling R20): well-known ids, our own names, the
    /// default one first of each table. A view an administrator changed or removed is not written again over the change (a row is
    /// inserted only when its id is missing).
    /// </summary>
    public static IReadOnlyList<SystemView> SystemViews { get; } =
    [
        new(new Guid("5a1c0001-4a2b-4c3d-8e4f-0a1b2c3d4e01"), "Active Queue Items", QueueItem, true, "statecode", 0,
            [("title", 260), (ObjectId, 180), (QueueIdColumn, 180), (WorkerId, 160), ("enteredon", 160)], "enteredon", true),
        new(new Guid("5a1c0001-4a2b-4c3d-8e4f-0a1b2c3d4e02"), "All Queue Items", QueueItem, false, null, null,
            [("title", 260), (ObjectId, 180), (QueueIdColumn, 180), (WorkerId, 160), ("enteredon", 160), ("statecode", 120)], "enteredon", true),
        new(new Guid("5a1c0001-4a2b-4c3d-8e4f-0a1b2c3d4e03"), "Active Queues", Queue, true, "statecode", 0,
            [("name", 260), ("queueviewtype", 160), ("ownerid", 200)], "name", false),
    ];

    /// <summary>A seeded system view: the columns with their widths, an optional equality filter (or an `in` filter with FilterValues) and the sort.</summary>
    public sealed record SystemView(Guid Id, string Name, string Table, bool IsDefault, string? FilterColumn, int? FilterValue,
        (string Column, int Width)[] Columns, string SortColumn, bool SortDescending, int[]? FilterValues = null)
    {
        /// <summary>The primary key: <c>activityid</c> for the four standard activity tables, <c>{table}id</c> for any other.</summary>
        public string IdColumn => Table is ActivitySchema.Email or ActivitySchema.Task or ActivitySchema.PhoneCall or ActivitySchema.Appointment
            ? "activityid" : $"{Table}id";

        public string FetchXml =>
            $"<fetch version=\"1.0\" mapping=\"logical\"><entity name=\"{Table}\">" +
            string.Concat(Columns.Select(c => $"<attribute name=\"{c.Column}\" />")) + $"<attribute name=\"{IdColumn}\" />" +
            $"<order attribute=\"{SortColumn}\" descending=\"{(SortDescending ? "true" : "false")}\" />" +
            (FilterColumn is null ? "" : FilterValues is { Length: > 0 }
                ? $"<filter type=\"and\"><condition attribute=\"{FilterColumn}\" operator=\"in\">" + string.Concat(FilterValues.Select(v => $"<value>{v}</value>")) + "</condition></filter>"
                : $"<filter type=\"and\"><condition attribute=\"{FilterColumn}\" operator=\"eq\" value=\"{FilterValue}\" /></filter>") +
            "</entity></fetch>";

        public string LayoutXml(int objectTypeCode) =>
            $"<grid name=\"resultset\" object=\"{objectTypeCode}\" jump=\"{Columns[0].Column}\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"{IdColumn}\">" +
            string.Concat(Columns.Select(c => $"<cell name=\"{c.Column}\" width=\"{c.Width}\" />")) + "</row></grid>";
    }
}
