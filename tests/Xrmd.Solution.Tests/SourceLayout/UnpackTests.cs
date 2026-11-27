using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Solution.SourceLayout;

namespace Xrmd.Solution.Tests.SourceLayout;

public class UnpackTests
{
    public static TheoryData<string> Solutions => new() { "XrmSharedTest", "OpenProject365", "xrmCalcTest" };

    [SourceLayoutTheory]
    [MemberData(nameof(Solutions))]
    public void Unpack_matches_reference(string name)
    {
        var expected = SourceFolder.Read(SourceLayoutFixtures.Folder(name, 'a'));

        var actual = SolutionUnpacker.Unpack(SourceLayoutFixtures.Zip(name));

        Differences(expected, actual).Should().BeEmpty();
    }

    [SourceLayoutFact]
    public void Webresource_payload_is_byte_identical()
    {
        // A script with a byte order mark, CRLF line ends and non-ASCII text: none of it may be touched.
        var payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("// città\r\nvar a = 1;\r\n\r\n")).ToArray();
        var zip = Rewrite(SourceLayoutFixtures.Zip("OpenProject365"),
            "WebResources/op365_scriptspurchasingjs80644930-00D0-F111-A05B-7CED8D2D53E4", _ => payload);

        var files = SolutionUnpacker.Unpack(zip);

        files["WebResources/op365_/scripts/purchasing.js"].Should().Equal(payload);
    }

    [SourceLayoutFact]
    public void Managed_zip_is_refused()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "solution.xml",
            bytes => Replace(bytes, "<Managed>0</Managed>", "<Managed>1</Managed>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.ManagedNotSupported);
        fault.Message.Should().Contain("managed");
    }

    [SourceLayoutFact]
    public void Unknown_component_fails_with_its_name()
    {
        // A part the reference folders hold nothing for has no place in the layout: unpack must refuse it, never drop it.
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<EntityDataProviders />",
                "<EntityDataProviders><EntityDataProvider Name=\"new_provider\"><DataSourceLogicalName>new_source</DataSourceLogicalName></EntityDataProvider></EntityDataProviders>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("EntityDataProvider").And.Contain("new_provider");
    }

    [SourceLayoutFact]
    public void Global_option_set_is_written_to_its_own_file()
    {
        var files = SolutionUnpacker.Unpack(SourceLayoutFixtures.Zip("XrmSharedTest"));

        files.Keys.Should().Contain("OptionSets/new_buttamiscelte.xml");
        var optionSet = XDocument.Load(new MemoryStream(files["OptionSets/new_buttamiscelte.xml"])).Root!;
        optionSet.Name.LocalName.Should().Be("optionset");
        optionSet.Attribute("Name")!.Value.Should().Be("new_buttamiscelte");
        Encoding.UTF8.GetString(files["Other/Customizations.xml"]).Should().Contain("<optionsets />");
    }

    [SourceLayoutFact]
    public void Option_set_with_a_name_that_cannot_be_a_file_is_refused()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<optionset Name=\"new_buttamiscelte\"", "<optionset Name=\"CON\""));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("CON");
    }

    [SourceLayoutFact]
    public void Unknown_element_among_the_option_sets_is_refused()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "</optionsets>", "<booleanoptionset Name=\"new_flag\" /></optionsets>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("new_flag");
    }

    [SourceLayoutFact]
    public void Classic_workflow_is_written_as_its_xaml_and_data_file()
    {
        const string xaml = "Workflows/Buttami-BF5DBD53-E0E9-F111-84EA-7CED8D2D53E4.xaml";
        var zip = SourceLayoutFixtures.Zip("XrmSharedTest");

        var files = SolutionUnpacker.Unpack(zip);

        using var archive = new ZipArchive(new MemoryStream(zip));
        using var entry = archive.GetEntry(xaml)!.Open();
        using var original = new MemoryStream();
        entry.CopyTo(original);
        files[xaml].Should().Equal(original.ToArray());
        var data = XDocument.Load(new MemoryStream(files[xaml + ".data.xml"])).Root!;
        data.Name.LocalName.Should().Be("Workflow");
        data.Element("XamlFileName")!.Value.Should().Be("/" + xaml);
    }

    [SourceLayoutFact]
    public void Formula_files_are_copied_byte_for_byte_into_their_table_folder()
    {
        var zip = SourceLayoutFixtures.Zip("xrmCalcTest");
        var entries = SolutionZip.ReadRaw(zip);

        var files = SolutionUnpacker.Unpack(zip);

        files["Entities/xrmv_Calcorder/Formulas/xrmv_calcorder-FormulaDefinitions.yaml"]
            .Should().Equal(entries["Formulas/xrmv_calcorder-FormulaDefinitions.yaml"]);
        files["Entities/Account/Formulas/account-xrmv_ordercount.xaml"]
            .Should().Equal(entries["Formulas/account-xrmv_ordercount.xaml"]);
        files.Keys.Should().NotContain(k => k.StartsWith("Formulas/", StringComparison.Ordinal));
    }

    [SourceLayoutFact]
    public void Formula_file_outside_the_Formulas_folder_is_refused_with_the_table()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("xrmCalcTest"), "customizations.xml",
            bytes => Replace(bytes, "/Formulas/xrmv_calcorder-xrmv_age.xaml", "/Other/xrmv_calcorder-xrmv_age.xaml"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("xrmv_Calcorder");
    }

    [SourceLayoutFact]
    public void Formula_file_the_zip_lacks_fails_with_its_name()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("xrmCalcTest"), "customizations.xml",
            bytes => Replace(bytes, "/Formulas/xrmv_calcorder-xrmv_age.xaml", "/Formulas/xrmv_calcorder-xrmv_gone.xaml"));

        var act = () => SolutionUnpacker.Unpack(zip);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("xrmv_calcorder-xrmv_gone.xaml");
    }

    /// <summary>The xrmCalcTest zip with the map of the reference <c>Other/EntityMaps.xml</c> in its customizations.</summary>
    internal static byte[] ZipWithEntityMap()
    {
        var maps = XDocument.Parse(Encoding.UTF8.GetString(SourceLayoutFixtures.EntityMapsReference).TrimStart('\uFEFF')).Root!;
        var inner = string.Concat(maps.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
        return Rewrite(SourceLayoutFixtures.Zip("xrmCalcTest"), "customizations.xml",
            bytes => Replace(bytes, "<EntityMaps />", "<EntityMaps>" + inner + "</EntityMaps>"));
    }

    [SourceLayoutFact]
    public void Entity_maps_are_written_to_their_own_file_as_the_reference_shows()
    {
        var files = SolutionUnpacker.Unpack(ZipWithEntityMap());

        files["Other/EntityMaps.xml"].Should().Equal(SourceLayoutFixtures.EntityMapsReference);
        XDocument.Parse(Encoding.UTF8.GetString(files["Other/Customizations.xml"]).TrimStart('\uFEFF'))
            .Root!.Element("EntityMaps")!.HasElements.Should().BeFalse();
    }

    [SourceLayoutFact]
    public void Solution_without_entity_maps_has_no_maps_file()
    {
        SolutionUnpacker.Unpack(SourceLayoutFixtures.Zip("xrmCalcTest")).Keys.Should().NotContain("Other/EntityMaps.xml");
    }

    [SourceLayoutFact]
    public void Unknown_element_among_the_entity_maps_is_refused()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("xrmCalcTest"), "customizations.xml",
            bytes => Replace(bytes, "<EntityMaps />", "<EntityMaps><SomethingElse Name=\"x\" /></EntityMaps>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("SomethingElse");
    }

    [SourceLayoutFact]
    public void Roles_sharing_a_name_are_refused_with_every_id()
    {
        var zip = SourceLayoutFixtures.Zip("OpenProject365");
        var customizations = XDocument.Parse(Encoding.UTF8.GetString(SolutionZip.ReadRaw(zip)[SolutionZip.CustomizationsEntry]).TrimStart('\uFEFF'));
        var role = customizations.Root!.Element("Roles")!.Elements("Role").First();
        var copy = new XElement(role);
        copy.SetAttributeValue("id", "{00000000-0000-0000-0000-000000000042}");
        copy.SetAttributeValue("name", ((string)role.Attribute("name")!).ToUpperInvariant());
        role.AddAfterSelf(copy);
        var changed = Rewrite(zip, "customizations.xml", _ => Encoding.UTF8.GetBytes(customizations.ToString()));

        var act = () => SolutionUnpacker.Unpack(changed);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain((string)role.Attribute("name")!).And.Contain((string)role.Attribute("id")!)
            .And.Contain("{00000000-0000-0000-0000-000000000042}");
    }

    [SourceLayoutFact]
    public void Unknown_entity_part_fails_with_its_name()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<RibbonDiffXml>", "<SomethingNew><Part /></SomethingNew><RibbonDiffXml>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("SomethingNew").And.Contain("op365_Project");
    }

    [SourceLayoutTheory]
    [InlineData("<Category>1</Category>")] // dialog
    [InlineData("<Category>2</Category>")] // business rule
    [InlineData("<Category>3</Category>")] // action
    [InlineData("<Category>4</Category>")] // business process flow
    [InlineData("<Category>6</Category>")] // desktop flow
    [InlineData("<Category></Category>")]
    [InlineData("")]
    public void Process_with_xaml_of_another_category_is_refused_with_its_name(string category)
    {
        // Only classic workflows (category 0) have a reference folder; every other kind of XAML process is refused.
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<Category>0</Category>", category));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("Buttami");
    }

    [SourceLayoutFact]
    public void Process_with_both_xaml_and_json_is_refused_with_its_name()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<Type>1</Type>", "<JsonFileName>/Workflows/Buttami.json</JsonFileName><Type>1</Type>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        act.Should().Throw<XrmdFaultException>().Which.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
    }

    [SourceLayoutTheory]
    [InlineData("Mystery/x.bin")]
    [InlineData("Controls/.hidden/x.bin")]
    public void Entry_in_an_unknown_folder_is_refused_with_its_path(string path)
    {
        var zip = AddEntry(SourceLayoutFixtures.Zip("XrmSharedTest"), path, [1, 2, 3]);

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain(path);
    }

    [SourceLayoutTheory]
    [MemberData(nameof(Solutions))]
    public void Every_unpacked_file_lies_in_the_component_scope(string name)
    {
        // What unpack writes is all that sync and a second unpack may delete: it must stay inside the component folders.
        var files = SolutionUnpacker.Unpack(SourceLayoutFixtures.Zip(name));
        files.Keys.Should().NotBeEmpty().And.OnlyContain(k => SourceFolder.InComponentFolder(k));
    }

    [SourceLayoutFact]
    public void Attribute_on_a_forms_group_is_refused_with_the_table()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<forms type=\"main\">", "<forms type=\"main\" extra=\"1\">"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("extra").And.Contain("op365_Project");
    }

    [SourceLayoutFact]
    public void Attribute_on_a_savedqueries_group_is_refused_with_the_table()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<savedqueries>", "<savedqueries extra=\"1\">"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("extra").And.Contain("op365_Project");
    }

    [Theory]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("CON")]
    [InlineData("prn")]
    [InlineData("Aux")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9.txt")]
    [InlineData("nul.tar.gz")]
    public void Names_that_cannot_be_written_everywhere_are_refused(string name)
    {
        var act = () => UnpackRules.SafeSegment(name, "table");
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    [Theory]
    [InlineData("op365_Project")]
    [InlineData("{a07738a0-2593-486c-a9d7-3ef1ec96515f}")]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("a.b")]
    public void Ordinary_names_are_accepted(string name) => UnpackRules.SafeSegment(name, "table").Should().Be(name);

    [SourceLayoutFact]
    public void Table_with_a_reserved_name_is_refused_before_anything_is_written()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "OriginalName=\"Project\">op365_Project</Name>", "OriginalName=\"Project\">CON</Name>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("CON");
    }

    private static byte[] AddEntry(byte[] zip, string path, byte[] content)
    {
        var buffer = new MemoryStream();
        buffer.Write(zip);
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        using (var w = archive.CreateEntry(path).Open())
            w.Write(content);
        return buffer.ToArray();
    }

    /// <summary>The paths missing, extra or different, each with a short hint (empty when the sets are equal).</summary>
    private static List<string> Differences(SourceFiles expected, SourceFiles actual)
    {
        var result = new List<string>();
        foreach (var key in expected.Keys.Order(StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(key, out var bytes)) result.Add("missing " + key);
            else if (!bytes.AsSpan().SequenceEqual(expected[key])) result.Add($"differs {key}: {FirstDifference(expected[key], bytes)}");
        }
        result.AddRange(actual.Keys.Where(k => !expected.ContainsKey(k)).Order(StringComparer.Ordinal).Select(k => "extra " + k));
        return result;
    }

    private static string FirstDifference(byte[] expected, byte[] actual)
    {
        var i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i]) i++;
        string Around(byte[] b) => Encoding.UTF8.GetString(b, Math.Max(0, i - 40), Math.Min(b.Length, i + 60) - Math.Max(0, i - 40));
        return $"at byte {i}: expected «{Around(expected)}» got «{Around(actual)}»";
    }

    private static byte[] Replace(byte[] bytes, string from, string to)
    {
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().Contain(from);
        return Encoding.UTF8.GetBytes(text.Replace(from, to));
    }

    /// <summary>A copy of <paramref name="zip"/> with the entry <paramref name="path"/> rewritten.</summary>
    private static byte[] Rewrite(byte[] zip, string path, Func<byte[], byte[]> change)
    {
        var buffer = new MemoryStream();
        buffer.Write(zip);
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"No entry {path}.");
            byte[] old;
            using (var s = entry.Open())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                old = m.ToArray();
            }
            entry.Delete();
            using var w = archive.CreateEntry(path).Open();
            w.Write(change(old));
        }
        return buffer.ToArray();
    }
}
