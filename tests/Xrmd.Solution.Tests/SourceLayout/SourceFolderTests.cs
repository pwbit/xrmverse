using System.Text;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Solution.SourceLayout;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests.SourceLayout;

public class SourceFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xrmd-src-" + Guid.NewGuid().ToString("N"));

    public SourceFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private string Make(params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        foreach (var (p, c) in files)
        {
            var full = Path.Combine(dir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, c);
        }
        return dir;
    }

    [Fact]
    public void Case_collision_is_refused()
    {
        var files = new SourceFiles { { "Entities/Account/Entity.xml", B("x") } };
        var act = () => files.Add("entities/account/entity.xml", B("y"));
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        files.Should().HaveCount(1);
        files["Entities/Account/Entity.xml"].Should().Equal(B("x"));
        files.ContainsKey("entities/account/entity.xml").Should().BeFalse();
    }

    [Theory]
    [InlineData("../x.xml")]
    [InlineData("a/../../x.xml")]
    [InlineData("/x.xml")]
    [InlineData("C:/x.xml")]
    [InlineData("a\\b.xml")]
    [InlineData("")]
    public void Path_or_symlink_out_of_the_folder_is_refused(string key)
    {
        var files = new SourceFiles();
        var act = () => files.Add(key, B("x"));
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void Symlink_out_of_the_folder_is_refused_on_read_and_write()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
        var dir = Make(("Other/Solution.xml", "<s/>"));
        Directory.CreateSymbolicLink(Path.Combine(dir, "Other", "Link"), outside);

        var read = () => SourceFolder.Read(dir);
        read.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);

        var plan = new SyncPlan(["Other/Link/new.txt"], []);
        var apply = () => SourceFolder.Apply(dir, plan, new SourceFiles { { "Other/Link/new.txt", B("n") } });
        apply.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        File.Exists(Path.Combine(outside, "new.txt")).Should().BeFalse();
    }

    [Fact]
    public void File_symlink_out_of_the_folder_is_refused_on_read()
    {
        var outside = Path.Combine(_root, "secret.txt");
        File.WriteAllText(outside, "s");
        var dir = Make(("Other/Solution.xml", "<s/>"));
        File.CreateSymbolicLink(Path.Combine(dir, "Other", "l.xml"), outside);
        var read = () => SourceFolder.Read(dir);
        read.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    [SourceLayoutFact]
    public void Cdsproj_folder_resolves_to_src()
    {
        var sample = SourceLayoutFixtures.CdsprojSample;
        SourceFolder.Resolve(sample).Should().Be(Path.Combine(sample, "src"));
        SourceFolder.Read(sample).Keys.Should().Contain("Other/Solution.xml");
        SourceFolder.Resolve(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')).Should().Be(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'));
    }

    [Fact]
    public void Resolve_refuses_a_folder_without_a_solution()
    {
        var dir = Make(("readme.txt", "x"));
        var act = () => SourceFolder.Resolve(dir);
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        var missing = () => SourceFolder.Resolve(Path.Combine(_root, "nope"));
        missing.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void Managed_files_are_not_read_and_not_removed()
    {
        var dir = Make(("Other/Solution.xml", "<s/>"), ("Other/Customizations.xml", "<c/>"),
            ("Entities/A/Entity_managed.xml", "m"), ("Other/X_MANAGED.XML", "m"));
        var files = SourceFolder.Read(dir);
        files.Keys.Should().BeEquivalentTo("Other/Solution.xml", "Other/Customizations.xml");

        var wanted = new SourceFiles { { "Other/Solution.xml", B("<s/>") } };
        var plan = SourceFolder.Plan(dir, wanted);
        plan.Write.Should().BeEmpty();
        plan.Remove.Should().BeEquivalentTo("Other/Customizations.xml");
        SourceFolder.Apply(dir, plan, wanted);
        File.Exists(Path.Combine(dir, "Entities/A/Entity_managed.xml")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "Other/X_MANAGED.XML")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "Other/Customizations.xml")).Should().BeFalse();
    }

    [Fact]
    public void Plan_on_empty_folder_writes_everything()
    {
        var dir = Path.Combine(_root, "fresh");
        var wanted = new SourceFiles { { "Other/Solution.xml", B("<s/>") }, { "Entities/A/Entity.xml", B("<e/>") } };
        var plan = SourceFolder.Plan(dir, wanted);
        plan.Write.Should().BeEquivalentTo("Other/Solution.xml", "Entities/A/Entity.xml");
        plan.Remove.Should().BeEmpty();
        SourceFolder.Apply(dir, plan, wanted);
        File.ReadAllBytes(Path.Combine(dir, "Entities", "A", "Entity.xml")).Should().Equal(B("<e/>"));
        Directory.Exists(Path.Combine(_root, "fresh")).Should().BeTrue();
    }

    private const string TwoColumns =
        "<Entity><Name>t</Name><EntityInfo><entity Name=\"t\"><attributes>" +
        "<attribute PhysicalName=\"a\"><Type>int</Type></attribute><attribute PhysicalName=\"b\"><Type>int</Type></attribute>" +
        "</attributes></entity></EntityInfo></Entity>";

    [Fact]
    public void Plan_leaves_a_table_whose_columns_only_changed_order()
    {
        var swapped = TwoColumns.Replace("<attribute PhysicalName=\"a\"><Type>int</Type></attribute><attribute PhysicalName=\"b\"><Type>int</Type></attribute>",
            "<attribute PhysicalName=\"b\"><Type>int</Type></attribute>\n  <attribute PhysicalName=\"a\"><Type>int</Type></attribute>");
        var dir = Make(("Other/Solution.xml", "<s/>"), ("Entities/t/Entity.xml", swapped));
        var wanted = new SourceFiles { { "Other/Solution.xml", B("<s/>") }, { "Entities/t/Entity.xml", B(TwoColumns) } };

        SourceFolder.Plan(dir, wanted).Write.Should().BeEmpty();
    }

    [Fact]
    public void Plan_writes_a_changed_column_and_a_changed_solution_version()
    {
        var dir = Make(
            ("Other/Solution.xml", "<ImportExportXml><SolutionManifest><Version>1.0.0.0</Version></SolutionManifest></ImportExportXml>"),
            ("Entities/t/Entity.xml", TwoColumns));
        var wanted = new SourceFiles
        {
            { "Other/Solution.xml", B("<ImportExportXml><SolutionManifest><Version>1.0.0.1</Version></SolutionManifest></ImportExportXml>") },
            { "Entities/t/Entity.xml", B(TwoColumns.Replace("<Type>int</Type></attribute></attributes>", "<Type>bigint</Type></attribute></attributes>")) },
        };

        SourceFolder.Plan(dir, wanted).Write.Should().BeEquivalentTo("Other/Solution.xml", "Entities/t/Entity.xml");
    }

    [Fact]
    public void Plan_on_empty_existing_folder_and_cdsproj_folder_use_expected_roots()
    {
        var empty = Make();
        Directory.CreateDirectory(empty);
        SourceFolder.ResolveForWrite(empty).Should().Be(empty);
        var proj = Make(("A.cdsproj", "<Project/>"));
        SourceFolder.ResolveForWrite(proj).Should().Be(Path.Combine(proj, "src"));
        SourceFolder.ResolveForWrite(Path.Combine(_root, "missing")).Should().Be(Path.Combine(_root, "missing"));
    }

    [Fact]
    public void Apply_writes_only_under_the_folder()
    {
        var dir = Make(("Other/Solution.xml", "old"), ("Other/Stale.xml", "s"), ("Entities/Gone/Entity.xml", "g"));
        var wanted = new SourceFiles { { "Other/Solution.xml", B("new") }, { "Other/New.xml", B("n") } };
        var plan = SourceFolder.Plan(dir, wanted);
        plan.Write.Should().BeEquivalentTo("Other/Solution.xml", "Other/New.xml");
        plan.Remove.Should().BeEquivalentTo("Other/Stale.xml", "Entities/Gone/Entity.xml");

        SourceFolder.Apply(dir, plan, wanted);
        File.ReadAllText(Path.Combine(dir, "Other/Solution.xml")).Should().Be("new");
        File.Exists(Path.Combine(dir, "Other/New.xml")).Should().BeTrue();
        Directory.Exists(Path.Combine(dir, "Entities")).Should().BeFalse("emptied directories are removed");
        Directory.Exists(dir).Should().BeTrue();

        var escape = new SyncPlan(["../escape.xml"], []);
        var act = () => SourceFolder.Apply(dir, escape, new SourceFiles());
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        File.Exists(Path.Combine(_root, "escape.xml")).Should().BeFalse();
    }

    [Fact]
    public void Apply_with_a_trailing_slash_dir_keeps_the_resolved_folder()
    {
        var dir = Make(("Other/Stale.xml", "s"));
        var withSlash = dir + Path.DirectorySeparatorChar;
        var wanted = new SourceFiles();
        var plan = SourceFolder.Plan(withSlash, wanted);
        plan.Remove.Should().BeEquivalentTo("Other/Stale.xml");
        SourceFolder.Apply(withSlash, plan, wanted);
        Directory.Exists(dir).Should().BeTrue();
        Directory.Exists(Path.Combine(dir, "Other")).Should().BeFalse();
    }

    [Fact]
    public void Case_variant_on_disk_is_a_write_not_a_remove_and_ends_with_the_wanted_name()
    {
        var dir = Make(("Other/solution.xml", "old"));
        var wanted = new SourceFiles { { "Other/Solution.xml", B("new") } };
        var plan = SourceFolder.Plan(dir, wanted);
        plan.Write.Should().BeEquivalentTo("Other/Solution.xml");
        plan.Remove.Should().BeEmpty();
        SourceFolder.Apply(dir, plan, wanted);
        Directory.GetFiles(Path.Combine(dir, "Other")).Select(Path.GetFileName).Should().Equal("Solution.xml");
        File.ReadAllText(Path.Combine(dir, "Other", "Solution.xml")).Should().Be("new");
    }

    [Fact]
    public void Plan_skips_managed_keys_of_wanted()
    {
        var dir = Make(("Other/Solution.xml", "<s/>"));
        var wanted = new SourceFiles { { "Other/Solution.xml", B("<s/>") }, { "Entities/A/Entity_managed.xml", B("m") } };
        SourceFolder.Plan(dir, wanted).Write.Should().BeEmpty();
    }

    [Fact]
    public void Dangling_symlink_out_of_the_folder_is_refused_on_write()
    {
        var dir = Make(("Other/Solution.xml", "<s/>"));
        File.CreateSymbolicLink(Path.Combine(dir, "Other", "dangling.xml"), Path.Combine(_root, "nowhere", "x.xml"));
        var plan = new SyncPlan(["Other/dangling.xml"], []);
        var act = () => SourceFolder.Apply(dir, plan, new SourceFiles { { "Other/dangling.xml", B("n") } });
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        Directory.Exists(Path.Combine(_root, "nowhere")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Other/Solution.xml", true)]
    [InlineData("Entities/A/Entity.xml", true)]
    [InlineData("Controls/x/bundle.js", true)]
    [InlineData("README.md", false)]
    [InlineData(".git/HEAD", false)]
    [InlineData(".github/workflows/ci.yml", false)]
    [InlineData("notes/Other/Solution.xml", false)]
    [InlineData("other/Solution.xml", false)]
    [InlineData("Entities/.cache/x.xml", false)]
    [InlineData("Entities", false)]
    public void Component_scope_is_the_layout_folders_without_dot_folders(string key, bool inScope) =>
        SourceFolder.InComponentFolder(key).Should().Be(inScope);

    [Fact]
    public void Read_plan_and_apply_never_touch_files_outside_the_component_folders()
    {
        var outside = new[] { "README.md", ".git/HEAD", ".git/objects/ab/cdef", ".github/workflows/ci.yml", "docs/notes.txt", "Entities/.cache/x.xml" };
        var dir = Make([("Other/Solution.xml", "<s/>"), ("Other/Stale.xml", "s"), .. outside.Select(p => (p, "mine"))]);
        // A link that leads out of the folder is refused only where the layout reads; elsewhere it is not looked at.
        File.CreateSymbolicLink(Path.Combine(dir, "elsewhere.txt"), Path.Combine(_root, "nowhere.txt"));

        SourceFolder.Read(dir).Keys.Should().BeEquivalentTo("Other/Solution.xml", "Other/Stale.xml");

        var wanted = new SourceFiles { { "Other/Solution.xml", B("<s2/>") } };
        var plan = SourceFolder.Plan(dir, wanted);
        plan.Write.Should().Equal("Other/Solution.xml");
        plan.Remove.Should().Equal("Other/Stale.xml");
        SourceFolder.Apply(dir, plan, wanted);

        foreach (var p in outside) File.ReadAllText(Path.Combine(dir, p)).Should().Be("mine", p);
        File.Exists(Path.Combine(dir, "Other", "Stale.xml")).Should().BeFalse();
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData(".git/HEAD")]
    [InlineData("Entities/.cache/x.xml")]
    public void Apply_refuses_paths_outside_the_component_folders(string key)
    {
        var dir = Make(("Other/Solution.xml", "<s/>"), (key, "mine"));
        var remove = () => SourceFolder.Apply(dir, new SyncPlan([], [key]), new SourceFiles());
        remove.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        var write = () => SourceFolder.Apply(dir, new SyncPlan(["Other/New.xml", key], []),
            new SourceFiles { { "Other/New.xml", B("n") }, { key, B("x") } });
        write.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        File.ReadAllText(Path.Combine(dir, key)).Should().Be("mine");
        File.Exists(Path.Combine(dir, "Other", "New.xml")).Should().BeFalse("every path is checked before the first change");
    }

    [Theory]
    [InlineData("Other/a*b.xml")]
    [InlineData("Other/a?.xml")]
    [InlineData("Other/a\"b.xml")]
    [InlineData("Other/a<b.xml")]
    [InlineData("Other/a>b.xml")]
    [InlineData("Other/a|b.xml")]
    [InlineData("Other/a\u0001b.xml")]
    [InlineData("Other/name.")]
    [InlineData("Other/name ")]
    [InlineData("Entities/CON/Entity.xml")]
    [InlineData("Entities/con.xml")]
    [InlineData("Other/AUX")]
    [InlineData("Other/nul.txt")]
    [InlineData("Other/PRN")]
    [InlineData("Other/COM1.xml")]
    [InlineData("Other/lpt9")]
    public void Apply_refuses_names_that_cannot_be_written_everywhere_before_the_first_write(string key)
    {
        var dir = Make(("Other/Solution.xml", "<s/>"));
        var wanted = new SourceFiles { { "Other/A.xml", B("a") }, { key, B("x") } };
        var act = () => SourceFolder.Apply(dir, new SyncPlan(["Other/A.xml", key], []), wanted);
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        File.Exists(Path.Combine(dir, "Other", "A.xml")).Should().BeFalse();
    }
}
