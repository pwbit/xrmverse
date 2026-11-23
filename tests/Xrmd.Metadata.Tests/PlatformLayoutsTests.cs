using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>The shape of the seeded forms (plan 4.1d, task 1).</summary>
public class PlatformLayoutsTests
{
    private static XDocument Doc(Guid id) => XDocument.Parse(PlatformLayouts.Forms.Single(f => f.Id == id).FormXml);

    private static string[] Fields(XElement column) => [.. column.Descendants("control").Select(c => (string?)c.Attribute("datafieldname") ?? "").Where(s => s != "")];

    [Fact]
    public void Email_form_places_party_lists_then_subject_regarding_and_body()
    {
        var columns = Doc(PlatformLayouts.EmailFormId).Descendants("column").ToArray();
        columns.Select(c => (string?)c.Attribute("width")).Should().Equal("66%", "34%");
        Fields(columns[0]).Should().Equal("from", "to", "cc", "bcc", "subject", "regardingobjectid", "description");
        Fields(columns[1]).Should().Equal("ownerid", "prioritycode", "scheduledend");
        var body = columns[0].Descendants("cell").Single(c => (string?)c.Element("control")!.Attribute("datafieldname") == "description");
        ((string?)body.Attribute("rowspan")).Should().Be("12");
        ((string?)body.Attribute("showlabel")).Should().Be("false");
    }

    [Fact]
    public void Every_cell_id_is_unique_and_stable()
    {
        foreach (var form in PlatformLayouts.Forms)
        {
            var ids = XDocument.Parse(form.FormXml).Descendants().Where(e => e.Name != "control").Select(e => (string?)e.Attribute("id")).Where(i => i != null).ToArray();
            ids.Should().OnlyHaveUniqueItems(form.Name);
            ids.Should().NotBeEmpty();
        }
        PlatformLayouts.BuildForms().Select(f => f.FormXml).Should().Equal(PlatformLayouts.Forms.Select(f => f.FormXml));
    }

    [Fact]
    public void Queue_form_is_one_full_width_column_of_general_email_settings_and_items()
    {
        var columns = Doc(PlatformLayouts.QueueFormId).Descendants("column").ToArray();
        columns.Select(c => (string?)c.Attribute("width")).Should().Equal("100%");
        columns[0].Descendants("section").Select(s => (string?)s.Attribute("name")).Should().Equal("general", "email", "items");
    }

    [Fact]
    public void Queue_form_subgrid_uses_queue_entries_and_the_active_items_view()
    {
        var control = Doc(PlatformLayouts.QueueFormId).Descendants("control").Single(c => (string?)c.Attribute("classid") == "{E7A81278-8635-4d9e-8D4D-59480B391C5B}");
        string P(string n) => control.Element("parameters")!.Element(n)!.Value;
        P("RelationshipName").Should().Be("queue_entries");
        P("TargetEntityType").Should().Be("queueitem");
        P("ViewId").Should().Be("{5a1c0001-4a2b-4c3d-8e4f-0a1b2c3d4e01}");
        P("RecordsPerPage").Should().Be("10");
    }

    [Fact]
    public void Views_have_the_listed_ids_and_one_default_per_table()
    {
        PlatformLayouts.Views.Where(v => v.Table is "email" or "queue").Select(v => v.Name).Should().Equal("Open Emails", "All Emails", "Sent Emails", "Received Emails", "All Queues");
        PlatformLayouts.Views.Where(v => v.Table == "email" && v.IsDefault).Should().ContainSingle().Which.Name.Should().Be("Open Emails");
    }

    [Fact]
    public void Activity_views_key_their_rows_on_activityid()
    {
        foreach (var view in PlatformLayouts.Views.Where(v => v.Table is "email" or "task" or "phonecall" or "appointment"))
        {
            view.FetchXml.Should().Contain("<attribute name=\"activityid\" />", view.Name).And.NotContain($"{view.Table}id", view.Name);
            view.LayoutXml(1).Should().Contain("id=\"activityid\"", view.Name);
        }
        PlatformLayouts.Views.Single(v => v.Name == "All Queues").FetchXml.Should().Contain("<attribute name=\"queueid\" />");
    }


    [Theory]
    [InlineData("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e04", "subject,regardingobjectid,description", "ownerid,prioritycode,scheduledend,scheduleddurationminutes,percentcomplete")]
    [InlineData("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e05", "from,to,phonenumber,directioncode,subject,regardingobjectid,description", "ownerid,prioritycode,scheduledend,actualdurationminutes,leftvoicemail")]
    [InlineData("5a1c0002-4a2b-4c3d-8e4f-0a1b2c3d4e06", "subject,requiredattendees,optionalattendees,location,scheduledstart,scheduledend,isalldayevent,regardingobjectid,description", "ownerid,prioritycode,scheduleddurationminutes")]
    public void Activity_forms_place_fields_in_two_columns(string id, string left, string right)
    {
        var columns = Doc(new Guid(id)).Descendants("column").ToArray();
        columns.Select(c => (string?)c.Attribute("width")).Should().Equal("66%", "34%");
        Fields(columns[0]).Should().Equal(left.Split(','));
        Fields(columns[1]).Should().Equal(right.Split(','));
        var memo = columns[0].Descendants("cell").Single(c => (string?)c.Element("control")!.Attribute("datafieldname") == "description");
        ((string?)memo.Attribute("rowspan")).Should().Be("6");
        ((string?)memo.Attribute("showlabel")).Should().Be("false");
    }

    [Fact]
    public void Activity_forms_use_the_known_control_classes()
    {
        string Class(Guid form, string field) => (string)Doc(form).Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == field).Attribute("classid")!;
        Class(PlatformLayouts.PhoneCallFormId, "leftvoicemail").Should().Be("{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}");
        Class(PlatformLayouts.AppointmentFormId, "isalldayevent").Should().Be("{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}");
        Class(PlatformLayouts.TaskFormId, "percentcomplete").Should().Be("{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}");
        Class(PlatformLayouts.TaskFormId, "scheduleddurationminutes").Should().Be("{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}");
        Class(PlatformLayouts.PhoneCallFormId, "from").Should().Be("{CBFB742C-14E7-4a17-96BB-1A13F7F64AA2}");
    }

    [Fact]
    public void Activity_views_are_listed_with_one_default_per_table()
    {
        var v = PlatformLayouts.Views.Where(x => x.Table is "task" or "phonecall" or "appointment").ToArray();
        v.Select(x => x.Name).Should().Equal("Open Tasks", "All Tasks", "Open Phone Calls", "All Phone Calls", "Open Appointments", "All Appointments");
        v.Where(x => x.IsDefault).Select(x => x.Name).Should().Equal("Open Tasks", "Open Phone Calls", "Open Appointments");
        v.Select(x => x.Id.ToString()[^2..]).Should().Equal("09", "0a", "0b", "0c", "0d", "0e");
    }

    [Fact]
    public void Open_appointments_filters_with_an_in_condition_on_open_and_scheduled()
    {
        var view = PlatformLayouts.Views.Single(x => x.Name == "Open Appointments");
        var cond = XDocument.Parse(view.FetchXml).Descendants("condition").Single();
        ((string?)cond.Attribute("operator")).Should().Be("in");
        ((string?)cond.Attribute("attribute")).Should().Be("statecode");
        cond.Elements("value").Select(e => e.Value).Should().Equal("0", "3");
    }

    [Fact]
    public void Sent_and_received_email_views_filter_on_the_status_reason()
    {
        string Value(string name) => (string)XDocument.Parse(PlatformLayouts.Views.Single(x => x.Name == name).FetchXml).Descendants("condition").Single().Attribute("value")!;
        Value("Sent Emails").Should().Be("3");
        Value("Received Emails").Should().Be("4");
    }

    [Fact]
    public void Single_equality_views_keep_their_fetchxml()
    {
        QueueSchema.SystemViews[0].FetchXml.Should().Contain("<filter type=\"and\"><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter>");
    }
}
