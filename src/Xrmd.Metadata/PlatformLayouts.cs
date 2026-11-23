using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Xrmd.Metadata;

/// <summary>A seeded main form: well-known id, name, table and the FormXml.</summary>
public sealed record SystemFormSeed(Guid Id, string Name, string Table, string FormXml);

/// <summary>
/// The default forms and the extra system views the seed writes for email, queue and queue item (plan 4.1d, task 1). Like
/// <see cref="QueueSchema.SystemViews"/>, each row is inserted only when its id is missing. Labels are our own short English.
/// </summary>
public static class PlatformLayouts
{
    private const string PartyList = "{CBFB742C-14E7-4a17-96BB-1A13F7F64AA2}";
    private const string Text = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}";
    private const string Memo = "{E0DECE4B-6FC8-4a8f-A065-082708572369}";
    private const string Lookup = "{270BD3DB-D9AF-4782-9025-509E298DEC0A}";
    private const string Choice = "{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}";
    private const string Date = "{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}";
    private const string TwoOptions = "{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}";
    private const string WholeNumber = "{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}";
    private const string Subgrid = "{E7A81278-8635-4d9e-8D4D-59480B391C5B}";

    public static readonly Guid EmailFormId = new("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e01");
    public static readonly Guid QueueFormId = new("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e02");
    public static readonly Guid QueueItemFormId = new("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e03");

    public static readonly Guid TaskFormId = new("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e04");
    public static readonly Guid PhoneCallFormId = new("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e05");
    public static readonly Guid AppointmentFormId = new("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e06");

    private static Guid ViewId(int n) => new($"5a1c0001-4a2b-4c3d-8e4f-0a1b2c3d4e{n:x2}");

    private static readonly Guid ActiveQueueItemsView = new("5a1c0001-4a2b-4c3d-8e4f-0a1b2c3d4e01");

    /// <summary>The seeded main forms: email, queue, queue item.</summary>
    public static IReadOnlyList<SystemFormSeed> Forms { get; } = BuildForms();

    /// <summary>Builds the forms again; the result is identical on every call (the ids are derived, not random).</summary>
    public static IReadOnlyList<SystemFormSeed> BuildForms() =>
    [
        new(EmailFormId, "Email", "email", EmailForm()),
        new(QueueFormId, "Queue", "queue", QueueForm()),
        new(QueueItemFormId, "Queue Item", "queueitem", QueueItemForm()),
        new(TaskFormId, "Task", "task", TaskForm()),
        new(PhoneCallFormId, "Phone Call", "phonecall", PhoneCallForm()),
        new(AppointmentFormId, "Appointment", "appointment", AppointmentForm()),
    ];

    /// <summary>The email views and "All Queues" (the 4.1a ids end in e01..e03).</summary>
    public static IReadOnlyList<QueueSchema.SystemView> Views { get; } =
    [
        new(ViewId(4), "Open Emails", "email", true, "statecode", 0,
            [("subject", 260), ("regardingobjectid", 180), ("prioritycode", 100), ("statuscode", 120), ("ownerid", 160), ("modifiedon", 150)],
            "modifiedon", true),
        new(ViewId(5), "All Emails", "email", false, null, null,
            [("subject", 260), ("regardingobjectid", 180), ("prioritycode", 100), ("statuscode", 120), ("ownerid", 160), ("modifiedon", 150), ("statecode", 100)],
            "modifiedon", true),
        new(ViewId(6), "Sent Emails", "email", false, "statuscode", 3,
            [("subject", 260), ("regardingobjectid", 180), ("ownerid", 160), ("senton", 150)], "senton", true),
        new(ViewId(7), "Received Emails", "email", false, "statuscode", 4,
            [("subject", 260), ("regardingobjectid", 180), ("ownerid", 160), ("actualend", 150)], "actualend", true),
        new(ViewId(8), "All Queues", "queue", false, null, null,
            [("name", 260), ("queueviewtype", 160), ("emailaddress", 200), ("ownerid", 200)], "name", false),
        new(ViewId(9), "Open Tasks", "task", true, "statecode", 0, TaskColumns, "scheduledend", false),
        new(ViewId(10), "All Tasks", "task", false, null, null, [.. TaskColumns, ("statecode", 100)], "modifiedon", true),
        new(ViewId(11), "Open Phone Calls", "phonecall", true, "statecode", 0, CallColumns, "scheduledend", false),
        new(ViewId(12), "All Phone Calls", "phonecall", false, null, null, [.. CallColumns, ("statecode", 100)], "modifiedon", true),
        new(ViewId(13), "Open Appointments", "appointment", true, "statecode", 0, AppointmentColumns, "scheduledstart", false, [0, 3]),
        new(ViewId(14), "All Appointments", "appointment", false, null, null, [.. AppointmentColumns, ("statecode", 100)], "scheduledstart", true),
    ];

    private static (string, int)[] TaskColumns =>
        [("subject", 260), ("regardingobjectid", 180), ("prioritycode", 100), ("scheduledend", 150), ("ownerid", 160)];
    private static (string, int)[] CallColumns =>
        [("subject", 260), ("phonenumber", 140), ("directioncode", 100), ("regardingobjectid", 180), ("scheduledend", 150), ("ownerid", 160)];
    private static (string, int)[] AppointmentColumns =>
        [("subject", 260), ("location", 160), ("scheduledstart", 150), ("scheduledend", 150), ("regardingobjectid", 180), ("ownerid", 160)];

    private static string EmailForm()
    {
        var f = EmailFormId;
        return Form(Tab(f, "general", "Email", false,
            Column(66,
                Section(f, "message", "Message", false,
                    Field(f, "from", "From", PartyList), Field(f, "to", "To", PartyList), Field(f, "cc", "Cc", PartyList),
                    Field(f, "bcc", "Bcc", PartyList), Field(f, "subject", "Subject", Text),
                    Field(f, "regardingobjectid", "Regarding", Lookup)),
                Section(f, "body", "Body", false, Field(f, "description", "Description", Memo, showLabel: false, rowSpan: 12))),
            Column(34,
                Section(f, "details", "Details", true,
                    Field(f, "ownerid", "Owner", Lookup), Field(f, "prioritycode", "Priority", Choice), Field(f, "scheduledend", "Due", Date)))));
    }

    private static string TaskForm()
    {
        var f = TaskFormId;
        return Form(Tab(f, "general", "General", false,
            Column(66,
                Section(f, "task", "Task", false,
                    Field(f, "subject", "Subject", Text), Field(f, "regardingobjectid", "Regarding", Lookup),
                    Field(f, "description", "Description", Memo, showLabel: false, rowSpan: 6))),
            Column(34,
                Section(f, "details", "Details", true,
                    Field(f, "ownerid", "Owner", Lookup), Field(f, "prioritycode", "Priority", Choice), Field(f, "scheduledend", "Due", Date),
                    Field(f, "scheduleddurationminutes", "Duration", WholeNumber), Field(f, "percentcomplete", "Percent complete", WholeNumber)))));
    }

    private static string PhoneCallForm()
    {
        var f = PhoneCallFormId;
        return Form(Tab(f, "general", "General", false,
            Column(66,
                Section(f, "call", "Phone call", false,
                    Field(f, "from", "Call from", PartyList), Field(f, "to", "Call to", PartyList), Field(f, "phonenumber", "Phone number", Text),
                    Field(f, "directioncode", "Direction", TwoOptions), Field(f, "subject", "Subject", Text),
                    Field(f, "regardingobjectid", "Regarding", Lookup),
                    Field(f, "description", "Description", Memo, showLabel: false, rowSpan: 6))),
            Column(34,
                Section(f, "details", "Details", true,
                    Field(f, "ownerid", "Owner", Lookup), Field(f, "prioritycode", "Priority", Choice), Field(f, "scheduledend", "Due", Date),
                    Field(f, "actualdurationminutes", "Duration", WholeNumber), Field(f, "leftvoicemail", "Left voice mail", TwoOptions)))));
    }

    private static string AppointmentForm()
    {
        var f = AppointmentFormId;
        return Form(Tab(f, "general", "General", false,
            Column(66,
                Section(f, "appointment", "Appointment", false,
                    Field(f, "subject", "Subject", Text), Field(f, "requiredattendees", "Required", PartyList),
                    Field(f, "optionalattendees", "Optional", PartyList), Field(f, "location", "Location", Text),
                    Field(f, "scheduledstart", "Start", Date), Field(f, "scheduledend", "End", Date),
                    Field(f, "isalldayevent", "All day event", TwoOptions), Field(f, "regardingobjectid", "Regarding", Lookup),
                    Field(f, "description", "Description", Memo, showLabel: false, rowSpan: 6))),
            Column(34,
                Section(f, "details", "Details", true,
                    Field(f, "ownerid", "Owner", Lookup), Field(f, "prioritycode", "Priority", Choice),
                    Field(f, "scheduleddurationminutes", "Duration", WholeNumber)))));
    }

    private static string QueueForm()
    {
        var f = QueueFormId;
        // One full-width column: the Items subgrid needs the width for its columns.
        return Form(Tab(f, "general", "Summary", true,
            Column(100,
                Section(f, "general", "General", true,
                    Field(f, "name", "Name", Text), Field(f, "queueviewtype", "Type", Choice), Field(f, "ownerid", "Owner", Lookup),
                    Field(f, "emailaddress", "Incoming email", Text), Field(f, "description", "Description", Memo)),
                Section(f, "email", "Email settings", true,
                    Field(f, "incomingemailfilteringmethod", "Convert incoming email", Choice), Field(f, "defaultmailbox", "Mailbox", Lookup)),
                Section(f, "items", "Items", true,
                    SubgridCell(f, "queue_entries", "Items", "queue_entries", "queueitem", ActiveQueueItemsView, 10)))));
    }

    private static string QueueItemForm()
    {
        var f = QueueItemFormId;
        return Form(Tab(f, "general", "Queue item", true,
            Column(100,
                Section(f, "general", "General", true,
                    Field(f, "title", "Title", Text), Field(f, "objectid", "Item", Lookup), Field(f, "queueid", "Queue", Lookup),
                    Field(f, "workerid", "Worked by", Lookup), Field(f, "workeridmodifiedon", "Worked by since", Date),
                    Field(f, "enteredon", "Entered queue", Date), Field(f, "statuscode", "Status reason", Choice)))));
    }

    // The form XML builder: every tab, section and cell id is derived from the form id and its own key, so it is stable across starts.

    private static string Id(Guid form, string key) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{form:D}/{key}"))).ToString("B").ToUpperInvariant();

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    private static string Label(string text) => $"<labels><label description=\"{Esc(text)}\" languagecode=\"1033\"/></labels>";

    private static string Form(params string[] tabs) => "<form><tabs>" + string.Concat(tabs) + "</tabs></form>";

    private static string Tab(Guid form, string name, string label, bool showLabel, params string[] columns) =>
        $"<tab name=\"{name}\" id=\"{Id(form, "tab:" + name)}\" expanded=\"true\" showlabel=\"{Bool(showLabel)}\" verticallayout=\"true\">" +
        Label(label) + "<columns>" + string.Concat(columns) + "</columns></tab>";

    private static string Column(int widthPercent, params string[] sections) =>
        $"<column width=\"{widthPercent}%\"><sections>{string.Concat(sections)}</sections></column>";

    private static string Section(Guid form, string name, string label, bool showLabel, params string[] cells) =>
        $"<section name=\"{name}\" id=\"{Id(form, "section:" + name)}\" showlabel=\"{Bool(showLabel)}\" showbar=\"false\" columns=\"1\">" +
        Label(label) + "<rows>" + string.Concat(cells.Select(c => $"<row>{c}</row>")) + "</rows></section>";

    private static string Field(Guid form, string column, string label, string classId, bool showLabel = true, int rowSpan = 1) =>
        $"<cell id=\"{Id(form, "cell:" + column)}\" showlabel=\"{Bool(showLabel)}\"{(rowSpan > 1 ? $" rowspan=\"{rowSpan}\"" : "")}>" +
        Label(label) + $"<control id=\"{column}\" classid=\"{classId}\" datafieldname=\"{column}\"/></cell>";

    private static string SubgridCell(Guid form, string id, string label, string relationship, string targetTable, Guid viewId, int recordsPerPage) =>
        $"<cell id=\"{Id(form, "cell:" + id)}\" showlabel=\"false\">" + Label(label) +
        $"<control id=\"{id}\" classid=\"{Subgrid}\"><parameters><RelationshipName>{relationship}</RelationshipName>" +
        $"<TargetEntityType>{targetTable}</TargetEntityType><ViewId>{viewId:B}</ViewId><RecordsPerPage>{recordsPerPage}</RecordsPerPage></parameters></control></cell>";

    private static string Bool(bool b) => b ? "true" : "false";
}
