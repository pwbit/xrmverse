using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Xrmd.TestSupport;

/// <summary>
/// The target solution of the migration table mapping tests (GAP-27): the unmanaged solution <see cref="Name"/>
/// <see cref="Version"/>, built in code with invented names. It holds the user-owned table <see cref="Lead"/> (choice
/// <c>xv_source</c>, a status model of its own, lookup <c>xv_parentaccountid</c> to <c>account</c>), the organization-owned table
/// <see cref="Rival"/>, the custom activity <see cref="DealClose"/> (whole number <c>xv_reason</c>, choice <c>xv_outcome</c>) and the
/// N:N relationships <see cref="LeadRival"/> and <see cref="LeadLead"/> (a lead with other leads). Every table has notes.
/// </summary>
/// <remarks>Hand-written in the shape the solution reader accepts, not a real export.</remarks>
public static class MappingTargetSolution
{
    public const string Name = "xvmaptarget";
    public const string Version = "1.0.0.0";
    public const string Lead = "xv_lead";
    public const string Rival = "xv_rival";
    public const string DealClose = "xv_dealclose";
    public const string LeadRival = "xv_lead_xv_rival";
    public const string LeadLead = "xv_lead_xv_lead";

    private const string PublisherName = "xrmversemaptests";
    private const string Prefix = "xv";

    /// <summary>The options of <c>xv_lead.xv_source</c>.</summary>
    public static IReadOnlyList<(int Value, string Label)> SourceOptions { get; } = [(100000000, "Web"), (100000001, "Referral"), (100000006, "Other")];

    /// <summary>The status reasons of <c>xv_lead</c> with their state.</summary>
    public static IReadOnlyList<(int State, int Status, string Label)> LeadStatuses { get; } =
        [(0, 1, "New"), (0, 100000000, "Contacted"), (1, 2, "Qualified"), (1, 100000001, "Lost"), (1, 100000004, "Canceled")];

    /// <summary>The options of <c>xv_dealclose.xv_outcome</c>.</summary>
    public static IReadOnlyList<(int Value, string Label)> OutcomeOptions { get; } = [(100000000, "Won"), (100000001, "Lost")];

    /// <summary>The solution file.</summary>
    public static byte[] Build()
    {
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["solution.xml"] = Bytes(Solution()),
            ["customizations.xml"] = Bytes(Customizations()),
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes(
                """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /></Types>"""),
        };
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static XDocument Solution() => new(new XElement("ImportExportXml",
        new XAttribute("version", "9.2.24093.200"), new XAttribute("SolutionPackageVersion", "9.2"), new XAttribute("languagecode", 1033),
        new XAttribute("generatedBy", "CrmLive"),
        new XElement("SolutionManifest",
            new XElement("UniqueName", Name),
            new XElement("LocalizedNames", Label("LocalizedName", "xrmverse map target")),
            new XElement("Descriptions"),
            new XElement("Version", Version),
            new XElement("Managed", 0),
            new XElement("Publisher",
                new XElement("UniqueName", PublisherName),
                new XElement("LocalizedNames", Label("LocalizedName", "xrmverse map tests")),
                new XElement("Descriptions"),
                new XElement("CustomizationPrefix", Prefix),
                new XElement("CustomizationOptionValuePrefix", 10000)),
            new XElement("RootComponents", new[] { Lead, Rival, DealClose }.Select(t =>
                new XElement("RootComponent", new XAttribute("type", 1), new XAttribute("schemaName", t), new XAttribute("behavior", 0)))),
            new XElement("MissingDependencies"))));

    private static XDocument Customizations() => new(new XElement("ImportExportXml",
        new XElement("Entities",
            Entity(Lead, "Lead (map test)", "UserOwned", activity: false,
                PrimaryKey(Lead + "id", "Lead"),
                Text("xv_name", "Name", primaryName: true),
                Picklist(Lead, "xv_source", "Source", SourceOptions),
                Lookup("xv_parentaccountid", "Parent account"),
                State(Lead, [(0, 1, "Active"), (1, 2, "Inactive")]),
                Status(Lead, LeadStatuses)),
            Entity(Rival, "Rival", "OrgOwned", activity: false,
                PrimaryKey(Rival + "id", "Rival"),
                Text("xv_name", "Name", primaryName: true),
                State(Rival, [(0, 1, "Active"), (1, 2, "Inactive")]),
                Status(Rival, [(0, 1, "Active"), (1, 2, "Inactive")])),
            Entity(DealClose, "Deal close", "UserOwned", activity: true,
                PrimaryKey("activityid", "Deal close"),
                Text("subject", "Subject", primaryName: true, custom: false, maxLength: 400),
                Integer("xv_reason", "Reason"),
                Picklist(DealClose, "xv_outcome", "Outcome", OutcomeOptions),
                State(DealClose, [(0, 1, "Open"), (1, 2, "Completed"), (2, 3, "Canceled")]),
                Status(DealClose, [(0, 1, "Open"), (1, 2, "Completed"), (2, 3, "Canceled")]))),
        new XElement("Roles"), new XElement("Workflows"), new XElement("FieldSecurityProfiles"), new XElement("Templates"), new XElement("EntityMaps"),
        new XElement("EntityRelationships",
            new XElement("EntityRelationship", new XAttribute("Name", "xv_account_xv_lead_parentaccountid"),
                new XElement("EntityRelationshipType", "OneToMany"),
                new XElement("IsCustomRelationship", 1),
                new XElement("IsCustomizable", 1),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("IsHierarchical", 0),
                new XElement("ReferencingEntityName", Lead),
                new XElement("ReferencedEntityName", "account"),
                new XElement("CascadeAssign", "NoCascade"), new XElement("CascadeDelete", "RemoveLink"), new XElement("CascadeArchive", "NoCascade"),
                new XElement("CascadeReparent", "NoCascade"), new XElement("CascadeShare", "NoCascade"), new XElement("CascadeUnshare", "NoCascade"),
                new XElement("CascadeRollupView", "NoCascade"),
                new XElement("IsValidForAdvancedFind", 1),
                new XElement("ReferencingAttributeName", "xv_parentaccountid")),
            new XElement("EntityRelationship", new XAttribute("Name", LeadRival),
                new XElement("EntityRelationshipType", "ManyToMany"),
                new XElement("IsCustomizable", 1),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("SecurityTypes", "Append"),
                new XElement("IsHierarchical", 0),
                new XElement("FirstEntityName", Lead),
                new XElement("SecondEntityName", Rival),
                new XElement("IntersectEntityName", LeadRival),
                new XElement("IsValidForAdvancedFind", 1)),
            new XElement("EntityRelationship", new XAttribute("Name", LeadLead),
                new XElement("EntityRelationshipType", "ManyToMany"),
                new XElement("IsCustomizable", 1),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("SecurityTypes", "Append"),
                new XElement("IsHierarchical", 0),
                new XElement("FirstEntityName", Lead),
                new XElement("SecondEntityName", Lead),
                new XElement("IntersectEntityName", LeadLead),
                new XElement("IsValidForAdvancedFind", 1))),
        new XElement("OrganizationSettings"),
        new XElement("optionsets"),
        new XElement("CustomControls"),
        new XElement("SolutionPluginAssemblies"),
        new XElement("SdkMessageProcessingSteps"),
        new XElement("WebResources"),
        new XElement("Languages", new XElement("Language", 1033))));

    private static XElement Entity(string name, string label, string ownership, bool activity, params XElement[] attributes) => new("Entity",
        new XElement("Name", new XAttribute("LocalizedName", label), new XAttribute("OriginalName", label), name),
        new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", name),
            new XElement("LocalizedNames", Label("LocalizedName", label)),
            new XElement("LocalizedCollectionNames", Label("LocalizedCollectionName", label + "s")),
            new XElement("Descriptions", Label("Description", $"The map test table {name}.")),
            new XElement("attributes", attributes),
            new XElement("EntitySetName", name + "s"),
            new XElement("IsDuplicateCheckSupported", 0),
            new XElement("IsBusinessProcessEnabled", 0),
            new XElement("IsRequiredOffline", 0),
            new XElement("IsInteractionCentricEnabled", 0),
            new XElement("IsCollaboration", 0),
            new XElement("AutoRouteToOwnerQueue", 0),
            new XElement("IsConnectionsEnabled", 0),
            new XElement("OwnershipTypeMask", ownership),
            new XElement("IsAuditEnabled", 0),
            new XElement("IsActivity", activity ? 1 : 0),
            activity ? new XElement("ActivityTypeMask", 1) : null,
            new XElement("HasRelatedNotes", "True"),
            new XElement("HasRelatedActivities", activity ? "False" : "True"),
            new XElement("IntroducedVersion", "1.0.0.0"),
            new XElement("IsCustomizable", 1),
            new XElement("IsRenameable", 1),
            new XElement("CanModifyAuditSettings", 1),
            new XElement("CanCreateAttributes", 1),
            new XElement("CanCreateForms", 1),
            new XElement("CanCreateCharts", 1),
            new XElement("CanCreateViews", 1),
            new XElement("CanModifyAdditionalSettings", 1))));

    /// <summary>An attribute; its schema name (<c>PhysicalName</c>) is the logical name, except the platform's own columns of an activity.</summary>
    private static XElement Attribute(string logicalName, string type, string label, bool custom, string requiredLevel, string displayMask,
        params object?[] typeSpecific) => new("attribute",
        new XAttribute("PhysicalName", logicalName switch { "activityid" => "ActivityId", "subject" => "Subject", _ => logicalName }),
        new XElement("Type", type),
        new XElement("Name", logicalName),
        new XElement("LogicalName", logicalName),
        new XElement("RequiredLevel", requiredLevel),
        new XElement("DisplayMask", displayMask),
        new XElement("ValidForUpdateApi", 1),
        new XElement("ValidForReadApi", 1),
        new XElement("ValidForCreateApi", 1),
        new XElement("IsCustomField", custom ? 1 : 0),
        new XElement("IsAuditEnabled", 1),
        new XElement("IsSecured", 0),
        new XElement("IntroducedVersion", "1.0.0.0"),
        new XElement("IsCustomizable", 1),
        new XElement("IsRenameable", 1),
        new XElement("CanModifySearchSettings", 1),
        new XElement("CanModifyRequirementLevelSettings", 1),
        new XElement("CanModifyAdditionalSettings", 1),
        new XElement("SourceType", 0),
        typeSpecific,
        new XElement("displaynames", Label("displayname", label)),
        new XElement("Descriptions", Label("Description", "")));

    private const string Grid = "ValidForAdvancedFind|ValidForForm|ValidForGrid";

    private static XElement PrimaryKey(string logicalName, string label) =>
        Attribute(logicalName, "primarykey", label, custom: false, "systemrequired", "ValidForAdvancedFind|RequiredForGrid");

    private static XElement Text(string logicalName, string label, bool primaryName = false, bool custom = true, int maxLength = 100) =>
        Attribute(logicalName, "nvarchar", label, custom, primaryName ? "required" : "none",
            primaryName ? "PrimaryName|" + Grid + "|RequiredForForm" : Grid,
            new XElement("Format", "text"), new XElement("MaxLength", maxLength), new XElement("Length", maxLength * 2));

    private static XElement Integer(string logicalName, string label) =>
        Attribute(logicalName, "int", label, custom: true, "none", Grid,
            new XElement("Format", "none"), new XElement("MinValue", int.MinValue), new XElement("MaxValue", int.MaxValue));

    private static XElement Lookup(string logicalName, string label) => Attribute(logicalName, "lookup", label, custom: true, "none", Grid);

    private static XElement Picklist(string table, string logicalName, string label, IReadOnlyList<(int Value, string Label)> options) =>
        Attribute(logicalName, "picklist", label, custom: true, "none", Grid,
            new XElement("optionset", new XAttribute("Name", $"{table}_{logicalName}"),
                new XElement("OptionSetType", "picklist"),
                new XElement("IsGlobal", 0),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("IsCustomizable", 1),
                new XElement("displaynames", Label("displayname", label)),
                new XElement("Descriptions", Label("Description", "")),
                new XElement("options", options.Select(o => new XElement("option",
                    new XAttribute("value", o.Value), new XAttribute("ExternalValue", ""), new XAttribute("IsHidden", 0),
                    new XElement("labels", Label("label", o.Label)))))));

    private static XElement State(string table, IReadOnlyList<(int Value, int DefaultStatus, string Label)> states) =>
        Attribute("statecode", "state", "Status", custom: false, "systemrequired", Grid,
            new XElement("optionset", new XAttribute("Name", table + "_statecode"),
                new XElement("OptionSetType", "state"),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("IsCustomizable", 1),
                new XElement("displaynames", Label("displayname", "Status")),
                new XElement("Descriptions", Label("Description", "")),
                new XElement("states", states.Select(s => new XElement("state",
                    new XAttribute("value", s.Value), new XAttribute("defaultstatus", s.DefaultStatus), new XAttribute("invariantname", s.Label),
                    new XElement("labels", Label("label", s.Label)))))));

    private static XElement Status(string table, IReadOnlyList<(int State, int Status, string Label)> statuses) =>
        Attribute("statuscode", "status", "Status Reason", custom: false, "none", Grid,
            new XElement("optionset", new XAttribute("Name", table + "_statuscode"),
                new XElement("OptionSetType", "status"),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("IsCustomizable", 1),
                new XElement("displaynames", Label("displayname", "Status Reason")),
                new XElement("Descriptions", Label("Description", "")),
                new XElement("statuses", statuses.Select(s => new XElement("status",
                    new XAttribute("value", s.Status), new XAttribute("state", s.State),
                    new XElement("labels", Label("label", s.Label)))))));

    private static XElement Label(string element, string text) =>
        new(element, new XAttribute("description", text), new XAttribute("languagecode", 1033));

    private static byte[] Bytes(XDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
