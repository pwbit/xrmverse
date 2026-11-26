using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Solution.SourceLayout;

namespace Xrmd.Solution.Tests.SourceLayout;

public class UnpackTests
{
    public static TheoryData<string> Solutions => new() { "XrmSharedTest", "OpenProject365" };

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
        // Global option sets have no place in the reference folders: unpack must refuse them, never drop them.
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<optionsets />",
                "<optionsets><optionset Name=\"new_colour\" localizedName=\"Colour\"><OptionSetType>picklist</OptionSetType></optionset></optionsets>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("optionset").And.Contain("new_colour");
    }

    [SourceLayoutFact]
    public void Unknown_entity_part_fails_with_its_name()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<RibbonDiffXml>", "<SomethingNew><Part /></SomethingNew><RibbonDiffXml>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("SomethingNew").And.Contain("op365_Project");
    }

    [SourceLayoutFact]
    public void Classic_workflow_is_refused_with_its_name()
    {
        var zip = Rewrite(SourceLayoutFixtures.Zip("XrmSharedTest"), "customizations.xml",
            bytes => Replace(bytes, "<Workflows></Workflows>",
                "<Workflows><Workflow WorkflowId=\"{11111111-2222-3333-4444-555555555555}\" Name=\"Notify owner\">"
                + "<XamlFileName>/Workflows/NotifyOwner-11111111-2222-3333-4444-555555555555.xaml</XamlFileName></Workflow></Workflows>"));

        var act = () => SolutionUnpacker.Unpack(zip);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("Notify owner");
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
