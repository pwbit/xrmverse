using System.Xml.Linq;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The fixture solution (<see cref="SolutionFixtureZip"/>) with one more table, the custom activity <c>xv_sms</c> (plan 2.11,
/// Task 7, pre-flight ruling C16: built here, the fixture folder is not changed).
/// </summary>
/// <remarks>
/// Hand-written, not a real export: the table is the fixture's <c>xv_task</c> reshaped in the test (invented names). It says
/// <c>IsActivity</c> 1, <c>ActivityTypeMask</c> 1 and <c>HasRelatedNotes</c> True; its primary key is <c>activityid</c> and its
/// primary name the system column <c>subject</c>; it carries some of the columns an activity gets from the platform (the
/// <c>regardingobjectid</c> lookup, the <c>activitytypecode</c> and <c>regardingobjecttypecode</c> EntityName columns, the
/// <c>from</c>, <c>to</c> and <c>customers</c> party lists, <c>description</c>, <c>scheduledstart</c>), one custom column
/// <c>xv_phonenumber</c>, and three relationships of the activity model (regarding, notes, parties), marked custom on purpose:
/// the import derives them, whatever the file says. The shape of a real export of a custom activity (which system columns and
/// relationships it lists, their flags) is not verified: plan 2.11 checklist.
/// </remarks>
public static class CustomActivitySolution
{
    /// <summary>The logical name of the custom activity.</summary>
    public const string Table = "xv_sms";

    /// <summary>The relationships of the activity model the file carries (the import skips them).</summary>
    public static readonly string[] ModelRelationships = ["xv_sms_regarding_account", "xv_sms_activity_parties", "xv_sms_Annotations"];

    /// <summary>
    /// The fixture with <c>xv_sms</c>; <paramref name="info"/> may alter its <c>EntityInfo/entity</c> element,
    /// <paramref name="managed"/> marks the solution managed and <paramref name="relationships"/> may alter the
    /// <c>EntityRelationships</c> element (the three of the activity model added).
    /// </summary>
    public static byte[] Build(Action<XElement>? info = null, bool managed = false, Action<XElement>? relationships = null) => SolutionFixtureZip.Build(
        customizations: d =>
        {
            var entities = d.Root!.Element("Entities")!;
            var entity = new XElement(entities.Elements("Entity").Single(e => e.Element("Name")!.Value == "xv_Task"));
            Reshape(entity);
            info?.Invoke(entity.Element("EntityInfo")!.Element("entity")!);
            entities.Add(entity);
            var relationships_ = d.Root!.Element("EntityRelationships")!;
            var template = relationships_.Elements("EntityRelationship").Single(r => (string)r.Attribute("Name")! == "business_unit_xv_project");
            relationships_.Add(
                Relationship(template, "xv_sms_regarding_account", referencing: "xv_Sms", referenced: "Account", attribute: "RegardingObjectId"),
                Relationship(template, "xv_sms_activity_parties", referencing: "ActivityParty", referenced: "xv_Sms", attribute: "ActivityId"),
                Relationship(template, "xv_sms_Annotations", referencing: "Annotation", referenced: "xv_Sms", attribute: "ObjectId"));
            relationships?.Invoke(relationships_);
        },
        solution: d =>
        {
            d.Descendants("RootComponents").Single().Add(new XElement("RootComponent",
                new XAttribute("type", "1"), new XAttribute("schemaName", Table), new XAttribute("behavior", "0")));
            if (managed) d.Descendants("Managed").Single().Value = "1";
        });

    private static void Reshape(XElement entity)
    {
        entity.Element("Name")!.Value = "xv_Sms";
        entity.Element("Name")!.SetAttributeValue("LocalizedName", "SMS");
        entity.Element("Name")!.SetAttributeValue("OriginalName", "SMS");
        entity.Elements().Where(e => e.Name.LocalName is "FormXml" or "SavedQueries").Remove();
        var info = entity.Element("EntityInfo")!.Element("entity")!;
        info.SetAttributeValue("Name", "xv_Sms");
        foreach (var label in info.Element("LocalizedNames")!.Elements()) label.SetAttributeValue("description", "SMS");
        foreach (var label in info.Element("LocalizedCollectionNames")!.Elements()) label.SetAttributeValue("description", "SMS messages");
        info.Element("EntitySetName")!.Value = "xv_smses";
        info.Element("IsActivity")!.Value = "1";
        info.Element("ActivityTypeMask")!.Value = "1";
        info.Element("HasRelatedNotes")!.Value = "True";
        info.Element("HasRelatedActivities")!.Value = "False";

        var attributes = info.Element("attributes")!;
        XElement Column(string physical) => attributes.Elements("attribute").Single(a => (string)a.Attribute("PhysicalName")! == physical);
        Rename(Column("xv_TaskId"), "ActivityId", "activityid");
        Column("xv_ProjectId").Remove();
        var name = Column("xv_Name");
        var phone = new XElement(name);
        Rename(name, "Subject", "subject");
        name.Element("IsCustomField")!.Value = "0";
        name.Element("MaxLength")!.Value = "400";
        name.Element("Length")!.Value = "800";
        foreach (var label in name.Element("displaynames")!.Elements()) label.SetAttributeValue("description", "Subject");
        Rename(phone, "xv_PhoneNumber", "xv_phonenumber");
        phone.Element("RequiredLevel")!.Value = "none";
        phone.Element("DisplayMask")!.Value = "ValidForAdvancedFind|ValidForForm|ValidForGrid";
        phone.Element("MaxLength")!.Value = "20";
        phone.Element("Length")!.Value = "40";
        foreach (var label in phone.Element("displaynames")!.Elements()) label.SetAttributeValue("description", "Phone number");
        attributes.Add(phone);

        // The states of a custom activity (Open, Completed, Canceled) with one status reason each (from memory: checklist).
        string[] states = ["Open", "Completed", "Canceled"];
        var state = Column("statecode").Element("optionset")!.Element("states")!;
        state.ReplaceNodes(states.Select((label, value) => new XElement("state", new XAttribute("value", value),
            new XAttribute("defaultstatus", value + 1), new XAttribute("invariantname", label), Labels(label))));
        var status = Column("statuscode").Element("optionset")!.Element("statuses")!;
        status.ReplaceNodes(states.Select((label, value) => new XElement("status", new XAttribute("value", value + 1),
            new XAttribute("state", value), Labels(label))));

        // Some of the columns the platform gives an activity, as system columns (IsCustomField 0).
        var template = Column("TimeZoneRuleVersionNumber");
        attributes.Add(
            System(template, "RegardingObjectId", "lookup", "Regarding"),
            System(template, "ActivityTypeCode", "entityname", "Activity Type"),
            System(template, "RegardingObjectTypeCode", "entityname", "Regarding Type"),
            System(template, "From", "partylist", "From"),
            System(template, "To", "partylist", "To"),
            System(template, "Customers", "partylist", "Customers"),
            System(template, "Description", "ntext", "Description", new XElement("MaxLength", "2000")),
            System(template, "ScheduledStart", "datetime", "Start Date", new XElement("Format", "datetime"), new XElement("Behavior", "1")));
    }

    private static void Rename(XElement attribute, string physical, string logical)
    {
        attribute.SetAttributeValue("PhysicalName", physical);
        attribute.Element("Name")!.Value = logical;
        attribute.Element("LogicalName")!.Value = logical;
    }

    private static XElement System(XElement template, string physical, string type, string label, params XElement[] typeSpecific)
    {
        var attribute = new XElement(template);
        Rename(attribute, physical, physical.ToLowerInvariant());
        attribute.Element("Type")!.Value = type;
        attribute.Element("DisplayMask")!.Value = "ValidForAdvancedFind|ValidForForm|ValidForGrid";
        attribute.Elements().Where(e => e.Name.LocalName is "Format" or "MinValue" or "MaxValue").Remove();
        attribute.Element("IsLocalizable")!.AddAfterSelf(typeSpecific);
        foreach (var name in attribute.Element("displaynames")!.Elements()) name.SetAttributeValue("description", label);
        return attribute;
    }

    private static XElement Labels(string label) => new("labels",
        new XElement("label", new XAttribute("description", label), new XAttribute("languagecode", "1033")));

    private static XElement Relationship(XElement template, string name, string referencing, string referenced, string attribute)
    {
        var relationship = new XElement(template);
        relationship.SetAttributeValue("Name", name);
        relationship.Element("EntityRelationshipType")!.AddAfterSelf(new XElement("IsCustomRelationship", "1"));
        relationship.Element("ReferencingEntityName")!.Value = referencing;
        relationship.Element("ReferencedEntityName")!.Value = referenced;
        relationship.Element("ReferencingAttributeName")!.Value = attribute;
        relationship.Element("EntityRelationshipRoles")!.Remove();
        return relationship;
    }
}
