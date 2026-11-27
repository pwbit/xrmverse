using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Solution.SourceLayout;

namespace Xrmd.Solution.Tests.SourceLayout;

public class PackTests
{
    public static TheoryData<string> Solutions => new() { "XrmSharedTest", "OpenProject365", "xrmCalcTest" };

    /// <summary>
    /// The collections whose order a source folder does not keep (one file per member, read back in file-name order) or
    /// that unpack sorts; their order means nothing to an import, so the comparison sorts their members.
    /// </summary>
    private static readonly HashSet<string> UnorderedCollections =
    [
        "Entities", "Roles", "Workflows", "FieldSecurityProfiles", "Dashboards", "WebResources", "CustomControls",
        "SolutionPluginAssemblies", "SdkMessageProcessingSteps", "AppModules", "AppModuleSiteMaps", "EntityRelationships",
        "FormXml", "forms", "savedqueries", "Visualizations", "AppModuleComponents", "MissingDependencies", "RootComponents", "optionsets",
    ];

    [SourceLayoutTheory]
    [MemberData(nameof(Solutions))]
    public void Pack_then_unpack_is_identity(string name)
    {
        var files = SourceFolder.Read(SourceLayoutFixtures.Folder(name, 'a'));

        var again = SolutionUnpacker.Unpack(SolutionPacker.Pack(files));

        again.Keys.Should().BeEquivalentTo(files.Keys);
        foreach (var (key, bytes) in files)
            again[key].AsSpan().SequenceEqual(bytes).Should().BeTrue($"{key} must come back byte for byte");
    }

    [SourceLayoutTheory]
    [MemberData(nameof(Solutions))]
    public void Unpack_then_pack_imports_the_same(string name)
    {
        var zip = SourceLayoutFixtures.Zip(name);

        var packed = SolutionPacker.Pack(SolutionUnpacker.Unpack(zip));

        var original = Entries(zip);
        var result = Entries(packed);
        result.Keys.Should().BeEquivalentTo(original.Keys);
        foreach (var xml in new[] { SolutionZip.SolutionEntry, SolutionZip.CustomizationsEntry })
            Canonical(result[xml]).Should().Be(Canonical(original[xml]), $"{xml} must carry the same content");
        foreach (var (path, bytes) in original)
        {
            if (path is SolutionZip.SolutionEntry or SolutionZip.CustomizationsEntry or SolutionZip.ContentTypesEntry) continue;
            result[path].AsSpan().SequenceEqual(bytes).Should().BeTrue($"the payload {path} must be byte-identical");
        }
        // The packed zip reads back with the reader the import uses.
        var read = SolutionZip.Read(packed);
        read.Files.Keys.Should().Contain(SolutionZip.ContentTypesEntry);
    }

    [SourceLayoutFact]
    public void Missing_listed_path_fails()
    {
        var files = Without(SourceFolder.Read(SourceLayoutFixtures.Folder("OpenProject365", 'a')), "WebResources/op365_/scripts/purchasing.js");

        var act = () => SolutionPacker.Pack(files);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("WebResources/op365_/scripts/purchasing.js");
    }

    private const string CalcYaml = "Entities/xrmv_Calcorder/Formulas/xrmv_calcorder-FormulaDefinitions.yaml";

    [SourceLayoutFact]
    public void Formula_files_go_back_to_the_Formulas_folder_of_the_zip()
    {
        var files = SourceFolder.Read(SourceLayoutFixtures.Folder("xrmCalcTest", 'a'));

        var entries = SolutionZip.ReadRaw(SolutionPacker.Pack(files));

        entries["Formulas/xrmv_calcorder-FormulaDefinitions.yaml"].Should().Equal(files[CalcYaml]);
        entries.Keys.Should().Contain("Formulas/account-xrmv_ordercount.xaml");
    }

    [SourceLayoutFact]
    public void Pack_fails_when_a_formula_file_is_missing()
    {
        var files = Without(SourceFolder.Read(SourceLayoutFixtures.Folder("xrmCalcTest", 'a')), CalcYaml);

        var act = () => SolutionPacker.Pack(files);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(CalcYaml);
    }

    [SourceLayoutFact]
    public void Pack_refuses_an_orphan_formula_file()
    {
        const string orphan = "Entities/xrmv_Calcorder/Formulas/xrmv_calcorder-nobody.xaml";
        var files = With(SourceFolder.Read(SourceLayoutFixtures.Folder("xrmCalcTest", 'a')), orphan, "<Activity />"u8.ToArray());

        var act = () => SolutionPacker.Pack(files);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain(orphan);
    }

    [SourceLayoutFact]
    public void Entity_maps_go_back_into_the_customizations()
    {
        var zip = UnpackTests.ZipWithEntityMap();

        var packed = SolutionPacker.Pack(SolutionUnpacker.Unpack(zip));

        Canonical(Entries(packed)[SolutionZip.CustomizationsEntry]).Should().Be(Canonical(Entries(zip)[SolutionZip.CustomizationsEntry]));
    }

    [SourceLayoutFact]
    public void Entity_maps_in_both_places_fail()
    {
        var files = SolutionUnpacker.Unpack(UnpackTests.ZipWithEntityMap());
        var customizations = Encoding.UTF8.GetString(files["Other/Customizations.xml"]).Replace(
            "<EntityMaps />", "<EntityMaps><EntityMap><EntitySource>account</EntitySource><EntityTarget>xrmv_test</EntityTarget></EntityMap></EntityMaps>");
        files = With(Without(files, "Other/Customizations.xml"), "Other/Customizations.xml", Encoding.UTF8.GetBytes(customizations));

        var act = () => SolutionPacker.Pack(files);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("Other/EntityMaps.xml");
    }

    [SourceLayoutFact]
    public void Relationship_listed_but_not_defined_fails()
    {
        var files = Without(SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')), "Other/Relationships/Team.xml");

        var act = () => SolutionPacker.Pack(files);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("Other/Relationships.xml").And.Contain("team_op365_project");
    }

    [SourceLayoutFact]
    public void Unexpected_file_fails_with_its_path()
    {
        var files = With(SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')), "Notes/todo.txt", "remember"u8.ToArray());

        var act = () => SolutionPacker.Pack(files);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("Notes/todo.txt");
    }

    [SourceLayoutFact]
    public void Entity_part_without_its_entity_file_fails()
    {
        var files = Without(SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')), "Entities/op365_Project/Entity.xml");

        var act = () => SolutionPacker.Pack(files);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("Entities/op365_Project/Entity.xml");
    }

    [SourceLayoutFact]
    public void A_document_type_definition_is_refused()
    {
        var files = SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'));
        var text = Encoding.UTF8.GetString(files["Other/Solution.xml"]);
        var withDtd = text.Replace("<ImportExportXml", "<!DOCTYPE ImportExportXml [<!ENTITY x \"y\">]><ImportExportXml");
        files = With(Without(files, "Other/Solution.xml"), "Other/Solution.xml", Encoding.UTF8.GetBytes(withDtd));

        var act = () => SolutionPacker.Pack(files);

        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    private const string ButtamiXaml = "Workflows/Buttami-BF5DBD53-E0E9-F111-84EA-7CED8D2D53E4.xaml";

    [SourceLayoutFact]
    public void Option_set_and_classic_workflow_are_packed_into_the_customizations()
    {
        var packed = SolutionPacker.Pack(SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')));

        var entries = Entries(packed);
        entries.Keys.Should().Contain(ButtamiXaml).And.NotContain(ButtamiXaml + ".data.xml").And.NotContain(k => k.StartsWith("OptionSets/", StringComparison.Ordinal));
        var customizations = XDocument.Load(new MemoryStream(entries[SolutionZip.CustomizationsEntry])).Root!;
        customizations.Element("optionsets")!.Elements("optionset").Select(o => o.Attribute("Name")!.Value).Should().Equal("new_buttamiscelte");
        customizations.Element("Workflows")!.Elements("Workflow").Single().Element("XamlFileName")!.Value.Should().Be("/" + ButtamiXaml);
    }

    [SourceLayoutTheory]
    [InlineData("<Category>2</Category>")]
    [InlineData("<Category>4</Category>")]
    [InlineData("")]
    public void Xaml_process_of_another_category_is_refused_by_pack(string category)
    {
        var files = SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'));
        var data = Encoding.UTF8.GetString(files[ButtamiXaml + ".data.xml"]).Replace("<Category>0</Category>", category);
        files = With(Without(files, ButtamiXaml + ".data.xml"), ButtamiXaml + ".data.xml", Encoding.UTF8.GetBytes(data));

        var act = () => SolutionPacker.Pack(files);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.UnsupportedComponent);
        fault.Message.Should().Contain("Buttami");
    }

    [SourceLayoutFact]
    public void Workflow_definition_without_its_xaml_fails_with_the_path()
    {
        var files = Without(SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')), ButtamiXaml);

        var act = () => SolutionPacker.Pack(files);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain(ButtamiXaml);
    }

    [SourceLayoutFact]
    public void Option_set_file_with_another_root_fails_with_its_path()
    {
        const string path = "OptionSets/new_buttamiscelte.xml";
        var files = With(Without(SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')), path), path, "<optionsets />"u8.ToArray());

        var act = () => SolutionPacker.Pack(files);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain(path);
    }

    private const string PurchasingData = "WebResources/op365_/scripts/purchasing.js.data.xml";
    private const string PurchasingFile = "/WebResources/op365_scriptspurchasingjs80644930-00D0-F111-A05B-7CED8D2D53E4";

    /// <summary>OpenProject365 plus a copy of the purchasing script named copy.js, whose definition names <paramref name="fileName"/>.</summary>
    private static SourceFiles WithCopiedWebResource(string fileName)
    {
        var files = SourceFolder.Read(SourceLayoutFixtures.Folder("OpenProject365", 'a'));
        var data = Encoding.UTF8.GetString(files[PurchasingData])
            .Replace("<Name>op365_/scripts/purchasing.js</Name>", "<Name>op365_/scripts/copy.js</Name>")
            .Replace(PurchasingFile, fileName);
        files = With(files, "WebResources/op365_/scripts/copy.js", "var copy = 1;"u8.ToArray());
        return With(files, "WebResources/op365_/scripts/copy.js.data.xml", Encoding.UTF8.GetBytes(data));
    }

    [SourceLayoutFact]
    public void Two_web_resources_with_one_file_name_fail_naming_both()
    {
        var act = () => SolutionPacker.Pack(WithCopiedWebResource(PurchasingFile));

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(PurchasingData).And.Contain("WebResources/op365_/scripts/copy.js.data.xml");
    }

    [SourceLayoutFact]
    public void Zip_entries_differing_only_by_case_fail()
    {
        var act = () => SolutionPacker.Pack(WithCopiedWebResource(PurchasingFile.ToLowerInvariant()));

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(PurchasingData).And.Contain("WebResources/op365_/scripts/copy.js.data.xml");
    }

    [SourceLayoutTheory]
    [InlineData("/customizations.xml")]
    [InlineData("/Solution.xml")]
    [InlineData("/[content_types].xml")]
    public void Web_resource_file_name_of_a_reserved_entry_fails(string fileName)
    {
        var act = () => SolutionPacker.Pack(WithCopiedWebResource(fileName));

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(fileName.TrimStart('/')).And.Contain("WebResources/op365_/scripts/copy.js.data.xml");
    }

    [SourceLayoutFact]
    public void Stray_file_directly_in_Entities_fails_with_its_path()
    {
        var files = With(SourceFolder.Read(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')), "Entities/notes.txt", "x"u8.ToArray());

        var act = () => SolutionPacker.Pack(files);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("Entities/notes.txt");
    }

    [SourceLayoutFact]
    public void Pack_of_a_folder_ignores_files_outside_the_component_folders_but_refuses_strays_inside()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xrmd-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = SourceLayoutFixtures.Folder("XrmSharedTest", 'a');
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(dir, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to);
            }
            // A repository root: what diff and sync leave alone, pack leaves out too.
            foreach (var extra in new[] { "README.md", ".git/HEAD", ".git/objects/ab/cdef", ".github/workflows/ci.yml" })
            {
                var path = Path.Combine(dir, extra);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "mine");
            }

            var packed = SolutionPacker.Pack(SourceFolder.Read(dir));
            Entries(packed).Keys.Should().NotContain(k => k.Contains("README") || k.Contains(".git"));

            File.WriteAllText(Path.Combine(dir, "Entities", "notes.txt"), "x");
            var act = () => SolutionPacker.Pack(SourceFolder.Read(dir));
            act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("Entities/notes.txt");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static SourceFiles Without(SourceFiles files, string key)
    {
        files.ContainsKey(key).Should().BeTrue();
        var result = new SourceFiles();
        foreach (var (k, v) in files) if (k != key) result.Add(k, v);
        return result;
    }

    private static SourceFiles With(SourceFiles files, string key, byte[] content)
    {
        var result = new SourceFiles();
        foreach (var (k, v) in files) result.Add(k, v);
        result.Add(key, content);
        return result;
    }

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var s = entry.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            result[entry.FullName] = m.ToArray();
        }
        return result;
    }

    /// <summary>
    /// Canonical XML: attributes sorted, whitespace between elements and namespace declarations dropped, an element with no
    /// content written empty, and the members of <see cref="UnorderedCollections"/> sorted by their canonical text.
    /// It ignores comments, and the text of an element that also has child elements (mixed content).
    /// </summary>
    private static string Canonical(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var document = XDocument.Load(stream);
        return Canonical(document.Root!).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Canonical(XElement source)
    {
        var result = new XElement(source.Name,
            source.Attributes().Where(a => !a.IsNamespaceDeclaration)
                .OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)
                .Select(a => new XAttribute(a.Name, a.Value)));
        if (source.HasElements)
        {
            var children = source.Elements().Select(Canonical);
            if (UnorderedCollections.Contains(source.Name.LocalName))
                children = children.OrderBy(c => c.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal);
            result.Add(children);
        }
        else if (source.Value.Length > 0)
        {
            result.Add(source.Value);
        }
        return result;
    }
}
