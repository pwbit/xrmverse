using System.Text;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Solution.SourceLayout;

namespace Xrmd.Solution.Tests.SourceLayout;

public class SourceComparerTests
{
    public static TheoryData<string> Solutions => new() { "XrmSharedTest", "OpenProject365", "xrmCalcTest" };

    private const string ProjectTable = "Entities/op365_Project";
    private const string MainForm = ProjectTable + "/FormXml/main/{a07738a0-2593-486c-a9d7-3ef1ec96515f}.xml";
    private const string InactiveView = ProjectTable + "/SavedQueries/{223de2ca-0f06-49f8-9c73-50c92461fe9c}.xml";
    private const string ScriptResource = "WebResources/op365_/scripts/purchasing.js";

    [SourceLayoutTheory]
    [MemberData(nameof(Solutions))]
    public void Two_exports_of_an_unchanged_solution_are_equal(string name)
    {
        var organization = SolutionUnpacker.Unpack(SourceLayoutFixtures.Zip(name));
        var folder = SourceFolder.Read(SourceLayoutFixtures.Folder(name, 'b'));

        var diff = SourceComparer.Compare(organization, folder);

        diff.Items.Should().BeEmpty();
        diff.Any.Should().BeFalse();

        // A later export of the same solution carries a new version number; that alone is not a difference.
        var bumped = With(organization, files => files["Other/Solution.xml"] = EditXml(files["Other/Solution.xml"],
            doc => doc.Root!.Element("SolutionManifest")!.Element("Version")!.Value = "9.8.7.6"));
        SourceComparer.Compare(bumped, folder).Items.Should().BeEmpty();
    }

    /// <summary>
    /// The folder comes from Dataverse's tools and the organization is xrmverse: the stamps an export puts on the root of the
    /// solution and customizations files name the exporting server, not the solution, and are no difference.
    /// </summary>
    [SourceLayoutTheory]
    [InlineData("Other/Solution.xml")]
    [InlineData("Other/Customizations.xml")]
    public void Export_stamps_on_the_root_are_ignored_but_the_language_is_not(string file)
    {
        var folder = Read("XrmSharedTest");
        string[] stamps = ["version", "SolutionPackageVersion", "generatedBy", "OrganizationVersion", "OrganizationSchemaType", "CRMServerServiceabilityVersion"];
        var restamped = With(folder, files => files[file] = EditXml(files[file], doc =>
        {
            foreach (var stamp in stamps) doc.Root!.SetAttributeValue(stamp, "xrmverse-" + stamp);
        }));
        var unstamped = With(folder, files => files[file] = EditXml(files[file], doc =>
        {
            foreach (var stamp in stamps) doc.Root!.Attribute(stamp)?.Remove();
        }));

        SourceComparer.Compare(restamped, folder).Items.Should().BeEmpty();
        SourceComparer.Compare(unstamped, folder).Items.Should().BeEmpty();

        var otherLanguage = With(folder, files => files[file] = EditXml(files[file], doc => doc.Root!.SetAttributeValue("languagecode", "1031")));
        SourceComparer.Compare(otherLanguage, folder).Items.Should().ContainSingle().Which.Files.Should().Equal(file);
    }

    [SourceLayoutFact]
    public void Export_stamps_count_on_other_elements()
    {
        var folder = Read("XrmSharedTest");
        var organization = With(folder, files => files[MainForm] = EditXml(files[MainForm],
            doc => doc.Root!.Elements().First().SetAttributeValue("OrganizationVersion", "1.2.3.4")));
        SourceComparer.Compare(organization, folder).Items.Should().ContainSingle().Which.Path.Should().Be(MainForm);
    }

    [SourceLayoutFact]
    public void Attribute_order_and_whitespace_are_ignored()
    {
        var folder = Read("XrmSharedTest");
        var reformatted = With(folder, files =>
        {
            foreach (var path in new[] { MainForm, InactiveView, "Other/Solution.xml" })
                files[path] = Reformat(files[path]);
        });
        reformatted[MainForm].Should().NotEqual(folder[MainForm]);

        SourceComparer.Compare(reformatted, folder).Items.Should().BeEmpty();
    }

    [SourceLayoutFact]
    public void Changed_form_reports_its_file()
    {
        var folder = Read("XrmSharedTest");
        var organization = With(folder, files => files[MainForm] = EditXml(files[MainForm], doc =>
        {
            var label = doc.Descendants("tab").Descendants("label").First(l => l.Attribute("description") is not null);
            label.SetAttributeValue("description", label.Attribute("description")!.Value + " (edited)");
        }));

        var diff = SourceComparer.Compare(organization, folder);

        diff.Any.Should().BeTrue();
        diff.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ComponentDifference("changed", "form", "Project", MainForm, [MainForm]));
    }

    [SourceLayoutFact]
    public void Changed_text_content_is_a_difference()
    {
        var folder = Read("XrmSharedTest");
        var organization = With(folder, files => files[InactiveView] = EditXml(files[InactiveView],
            doc => doc.Descendants("isdefault").First().Value += " "));

        SourceComparer.Compare(organization, folder).Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ComponentDifference("changed", "view", "Inactive Projects", InactiveView, [InactiveView]));
    }

    [SourceLayoutFact]
    public void Missing_and_new_components()
    {
        var folder = Read("OpenProject365");
        var control = folder.Keys.Where(k => k.StartsWith("Controls/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).First();
        var controlFolder = string.Join('/', control.Split('/').Take(2));
        var bundle = controlFolder + "/bundle.js";
        var view = folder.Keys.Where(k => k.Contains("/SavedQueries/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).First();
        var addedView = view[..(view.LastIndexOf('/') + 1)] + "{00000000-0000-0000-0000-000000000001}.xml";
        var role = folder.Keys.First(k => k.StartsWith("Roles/", StringComparison.Ordinal));

        var organization = With(folder, files =>
        {
            files.Remove(view);
            files.Remove(role);
            files[addedView] = folder[view];
            files.Remove(bundle);
        });

        var diff = SourceComparer.Compare(organization, folder);

        diff.Items.Select(i => (i.Status, i.Type, i.Path, string.Join(",", i.Files))).Should().Equal(
            new[]
            {
                ("changed", "control", controlFolder, bundle),
                ("missing", "view", view, view),
                ("new", "view", addedView, addedView),
                ("missing", "role", role, role),
            }.OrderBy(i => i.Item3, StringComparer.Ordinal));
        diff.Items.Single(i => i.Path == role).Name.Should().Be(Path.GetFileNameWithoutExtension(role));
        diff.Items.Single(i => i.Path == controlFolder).Name.Should().Be(controlFolder["Controls/".Length..]);
    }

    private const string Choice = "OptionSets/new_buttamiscelte.xml";
    private const string WorkflowXaml = "Workflows/Buttami-BF5DBD53-E0E9-F111-84EA-7CED8D2D53E4.xaml";

    [SourceLayoutFact]
    public void Changed_global_choice_is_one_component()
    {
        var folder = Read("XrmSharedTest");
        var organization = With(folder, files => files[Choice] = EditXml(files[Choice],
            doc => doc.Descendants("label").First().SetAttributeValue("description", "Uno")));

        SourceComparer.Compare(organization, folder).Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ComponentDifference("changed", "choice", "new_buttamiscelte", Choice, [Choice]));
    }

    [SourceLayoutFact]
    public void Classic_workflow_is_its_xaml_and_data_file()
    {
        var folder = Read("XrmSharedTest");
        var changed = With(folder, files => files[WorkflowXaml] = EditXml(files[WorkflowXaml],
            doc => doc.Root!.SetAttributeValue("{http://schemas.microsoft.com/winfx/2006/xaml}Class", "XrmWorkflowEdited")));
        SourceComparer.Compare(changed, folder).Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ComponentDifference("changed", "workflow", "Buttami", WorkflowXaml, [WorkflowXaml]));

        var removed = With(folder, files =>
        {
            files.Remove(WorkflowXaml);
            files.Remove(WorkflowXaml + ".data.xml");
            files.Remove(Choice);
        });
        SourceComparer.Compare(removed, folder).Items.Select(i => (i.Status, i.Type, i.Name, i.Path, string.Join(",", i.Files))).Should().Equal(
            ("missing", "choice", "new_buttamiscelte", Choice, Choice),
            ("missing", "workflow", "Buttami", WorkflowXaml, WorkflowXaml + "," + WorkflowXaml + ".data.xml"));
    }

    [SourceLayoutFact]
    public void Formula_files_are_formula_components()
    {
        const string xaml = "Entities/xrmv_Calcorder/Formulas/xrmv_calcorder-xrmv_age.xaml";
        const string yaml = "Entities/xrmv_Calcorder/Formulas/xrmv_calcorder-FormulaDefinitions.yaml";
        var folder = Read("xrmCalcTest");

        var reindented = With(folder, files => files[xaml] = Encoding.UTF8.GetBytes(XDocument.Parse(Encoding.UTF8.GetString(files[xaml])).ToString(SaveOptions.DisableFormatting)));
        SourceComparer.Compare(reindented, folder).Any.Should().BeFalse();

        var changed = With(folder, files => files[yaml] = [.. files[yaml], .. "xrmv_more: =1\n"u8.ToArray()]);
        SourceComparer.Compare(changed, folder).Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ComponentDifference("changed", "formula", "xrmv_calcorder-FormulaDefinitions", yaml, [yaml]));
    }

    private const string CalcEntity = "Entities/xrmv_Calcorder/Entity.xml";

    private static void SwapFirstTwoColumns(XDocument doc)
    {
        var columns = doc.Descendants("attributes").First().Elements("attribute").ToList();
        columns[1].Remove();
        columns[0].AddBeforeSelf(columns[1]);
    }

    [SourceLayoutFact]
    public void Column_order_in_a_table_is_not_a_difference()
    {
        var folder = Read("xrmCalcTest");
        var reordered = With(folder, files => files[CalcEntity] = EditXml(files[CalcEntity], SwapFirstTwoColumns));
        reordered[CalcEntity].Should().NotEqual(folder[CalcEntity]);

        SourceComparer.Compare(reordered, folder).Any.Should().BeFalse();
    }

    [SourceLayoutFact]
    public void A_changed_column_still_shows_when_columns_are_reordered()
    {
        var folder = Read("xrmCalcTest");
        var changed = With(folder, files => files[CalcEntity] = EditXml(files[CalcEntity], doc =>
        {
            SwapFirstTwoColumns(doc);
            doc.Descendants("attribute").First().Element("RequiredLevel")!.Value = "changed";
        }));

        SourceComparer.Compare(changed, folder).Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ComponentDifference("changed", "table", "xrmv_Calcorder", CalcEntity, [CalcEntity]),
                o => o.Excluding(d => d.Name));
    }

    [SourceLayoutFact]
    public void Binary_compares_by_content()
    {
        var folder = Read("OpenProject365");
        var asset = folder.Keys.First(k => k.StartsWith("Assets/", StringComparison.Ordinal));

        // Whitespace that would not count in XML counts in a script; a file that does not parse as XML compares by bytes.
        var organization = With(folder, files =>
        {
            files[ScriptResource] = [.. files[ScriptResource], (byte)'\n'];
            files[asset] = "<not xml"u8.ToArray();
        });
        var diff = SourceComparer.Compare(organization, folder);
        diff.Items.Select(i => (i.Status, i.Type, i.Name, i.Path, string.Join(",", i.Files))).Should().Equal(
            ("changed", "asset", asset["Assets/".Length..], asset, asset),
            ("changed", "webresource", "op365_/scripts/purchasing.js", ScriptResource, ScriptResource));

        // The same unparsable bytes on both sides are equal.
        var both = With(folder, files => files[asset] = "<not xml"u8.ToArray());
        SourceComparer.Compare(both, both).Items.Should().BeEmpty();
    }

    private static SourceFiles Read(string solution) => SourceFolder.Read(SourceLayoutFixtures.Folder(solution, 'a'));

    private static SourceFiles With(SourceFiles source, Action<Dictionary<string, byte[]>> edit)
    {
        var files = source.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        edit(files);
        var result = new SourceFiles();
        foreach (var (path, bytes) in files) result.Add(path, bytes);
        return result;
    }

    private static byte[] EditXml(byte[] bytes, Action<XDocument> edit)
    {
        var doc = XDocument.Load(new MemoryStream(bytes));
        edit(doc);
        using var stream = new MemoryStream();
        doc.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The same XML written differently: attributes in reverse order, tab indentation, CRLF line ends, no declaration and
    /// no byte order mark, and empty elements written with a closing tag.
    /// </summary>
    private static byte[] Reformat(byte[] bytes)
    {
        var doc = XDocument.Load(new MemoryStream(bytes));
        foreach (var element in doc.Descendants())
        {
            var attributes = element.Attributes().Reverse().ToList();
            element.ReplaceAttributes(attributes);
            if (element.IsEmpty) element.Value = "";
        }
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\r\n",
        };
        using (var writer = XmlWriter.Create(stream, settings)) doc.Root!.WriteTo(writer);
        return stream.ToArray();
    }
}
