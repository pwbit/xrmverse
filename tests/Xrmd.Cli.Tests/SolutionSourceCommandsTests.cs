using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Migration.Tests.Source;
using PackedZip = Xrmd.Solution.SolutionZip;
using Xrmd.TestSupport;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd solution unpack</c> and <c>pack</c> (offline), <c>diff</c> and <c>sync</c> (against a scripted server).</summary>
public sealed class SolutionSourceCommandsTests : IDisposable
{
    private readonly TestCli _cli = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xrmd-source-tests", Guid.NewGuid().ToString("N"));

    public SolutionSourceCommandsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _cli.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string Zip(string solution)
    {
        var path = Path.Combine(_root, solution + ".zip");
        File.WriteAllBytes(path, SourceLayoutFixtures.Zip(solution));
        return path;
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_managed.xml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => Convert.ToHexString(File.ReadAllBytes(f)));

    [SourceLayoutTheory]
    [InlineData("XrmSharedTest")]
    [InlineData("OpenProject365")]
    public async Task Unpack_writes_the_layout(string solution)
    {
        var target = Path.Combine(_root, "out");
        (await _cli.RunAsync("solution", "unpack", Zip(solution), "--folder", target)).Should().Be(0, _cli.Err.ToString());
        Snapshot(target).Should().BeEquivalentTo(Snapshot(SourceLayoutFixtures.Folder(solution, 'a')));
    }

    [SourceLayoutFact]
    public async Task Unpack_again_updates_and_removes_files()
    {
        var target = Path.Combine(_root, "out");
        (await _cli.RunAsync("solution", "unpack", Zip("XrmSharedTest"), "--folder", target)).Should().Be(0);
        File.WriteAllText(Path.Combine(target, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(target, "Entities", "stale.txt"), "x");
        File.WriteAllText(Path.Combine(target, "Other", "Solution_managed.xml"), "keep");
        (await _cli.RunAsync("solution", "unpack", Zip("XrmSharedTest"), "--folder", target)).Should().Be(0);
        _cli.Out.ToString().Should().Contain("0 written, 1 removed");
        File.Exists(Path.Combine(target, "Entities", "stale.txt")).Should().BeFalse("files of the component folders follow the zip");
        File.ReadAllText(Path.Combine(target, "notes.txt")).Should().Be("x", "files outside the component folders are kept");
        File.Exists(Path.Combine(target, "Other", "Solution_managed.xml")).Should().BeTrue();
    }

    [SourceLayoutFact]
    public async Task Unpack_refuses_an_unrelated_non_empty_folder()
    {
        var target = Path.Combine(_root, "other");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "notes.txt"), "mine");
        (await _cli.RunAsync("solution", "unpack", Zip("XrmSharedTest"), "--folder", target)).Should().Be(ExitCodes.Usage);
        File.Exists(Path.Combine(target, "Other", "Solution.xml")).Should().BeFalse();
    }

    [Fact]
    public async Task Unpack_managed_exits_2()
    {
        var zip = Path.Combine(_root, "m.zip");
        File.WriteAllBytes(zip, SolutionZip.Build("Managed1", "1.0.0.0", managed: true));
        (await _cli.RunAsync("solution", "unpack", zip, "--folder", Path.Combine(_root, "out"))).Should().Be(ExitCodes.Usage);
        _cli.Err.ToString().Should().Contain("anaged");
    }

    [Fact]
    public async Task Unpack_missing_zip_exits_2_with_the_path()
    {
        var zip = Path.Combine(_root, "nope.zip");
        (await _cli.RunAsync("solution", "unpack", zip, "--folder", Path.Combine(_root, "out"))).Should().Be(ExitCodes.Usage);
        _cli.Err.ToString().Should().Contain(zip);
    }

    [SourceLayoutFact]
    public async Task Pack_writes_a_zip_readable_by_SolutionZip()
    {
        var file = Path.Combine(_root, "packed.zip");
        (await _cli.RunAsync("solution", "pack", SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "--out", file)).Should().Be(0, _cli.Err.ToString());
        var (solution, _, _) = PackedZip.Read(File.ReadAllBytes(file));
        solution.Descendants("UniqueName").First().Value.Should().Be("XrmSharedTest");
    }

    [Fact]
    public async Task Pack_folder_without_solution_xml_exits_2()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        (await _cli.RunAsync("solution", "pack", empty, "--out", Path.Combine(_root, "x.zip"))).Should().Be(ExitCodes.Usage);
        File.Exists(Path.Combine(_root, "x.zip")).Should().BeFalse();
    }

    [SourceLayoutFact]
    public async Task Json_output_lists_files()
    {
        var target = Path.Combine(_root, "out");
        (await _cli.RunAsync("solution", "unpack", Zip("XrmSharedTest"), "--folder", target, "--output", "json")).Should().Be(0, _cli.Err.ToString());
        var unpack = JsonDocument.Parse(_cli.Out.ToString()).RootElement;
        unpack.GetProperty("solution").GetString().Should().Be("XrmSharedTest");
        unpack.GetProperty("written").EnumerateArray().Select(e => e.GetString()).Should().Contain("Other/Solution.xml");
        unpack.GetProperty("removed").GetArrayLength().Should().Be(0);

        var file = Path.Combine(_root, "p.zip");
        (await _cli.RunAsync("solution", "pack", target, "--out", file, "--output", "json")).Should().Be(0, _cli.Err.ToString());
        var pack = JsonDocument.Parse(_cli.Out.ToString()).RootElement;
        pack.GetProperty("bytes").GetInt64().Should().Be(new FileInfo(file).Length);
        pack.GetProperty("file").GetString().Should().Be(file);
    }

    // ---- diff / sync ------------------------------------------------------------------------------------------------

    private const string Url = "http://xrmd.test:5000";
    private const string MainForm = "Entities/op365_Project/FormXml/main/{a07738a0-2593-486c-a9d7-3ef1ec96515f}.xml";

    /// <summary>A CLI signed in to a server whose <c>ExportSolution</c> returns the fixture zip of XrmSharedTest.</summary>
    private static async Task<TestCli> ServerCliAsync(Func<HttpRequestMessage, HttpResponseMessage>? export = null)
    {
        var zip = Convert.ToBase64String(SourceLayoutFixtures.Zip("XrmSharedTest"));
        var cli = new TestCli(request => request.RequestUri!.AbsolutePath.EndsWith("/ExportSolution", StringComparison.Ordinal)
            ? export?.Invoke(request) ?? FakeHandler.Json(HttpStatusCode.OK, $$"""{"ExportSolutionFile":"{{zip}}"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        (await cli.LoginDevAsync(Url)).Should().Be(0, cli.Err.ToString());
        return cli;
    }

    private static List<string?> ExportBodies(TestCli cli) =>
        cli.Handler.Requests.Zip(cli.Handler.Bodies)
            .Where(c => c.First.RequestUri!.AbsolutePath.EndsWith("/ExportSolution", StringComparison.Ordinal))
            .Select(c => c.Second).ToList();

    private string Copy(string source, string name)
    {
        var target = Path.Combine(_root, name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return target;
    }

    private static void EditForm(string dir)
    {
        var path = Path.Combine(dir, MainForm.Replace('/', Path.DirectorySeparatorChar));
        var text = File.ReadAllText(path);
        var label = Regex.Match(text, "<label description=\"([^\"]+)\"");
        label.Success.Should().BeTrue();
        File.WriteAllText(path, text.Remove(label.Groups[1].Index, label.Groups[1].Length).Insert(label.Groups[1].Index, label.Groups[1].Value + " (edited)"));
    }

    [SourceLayoutFact]
    public async Task Diff_equal_exits_0()
    {
        using var cli = await ServerCliAsync();
        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", SourceLayoutFixtures.Folder("XrmSharedTest", 'b'))).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("No differences");
        var body = ExportBodies(cli).Should().ContainSingle().Subject;
        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("SolutionName").GetString().Should().Be("XrmSharedTest");
        json.RootElement.GetProperty("Managed").GetBoolean().Should().BeFalse();
    }

    [SourceLayoutFact]
    public async Task Diff_changed_exits_9_and_prints_status_type_name_path()
    {
        var folder = Copy(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "repo");
        EditForm(folder);
        using var cli = await ServerCliAsync();

        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", folder)).Should().Be(ExitCodes.Differences, cli.Err.ToString());

        var lines = cli.Out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain(l => Regex.IsMatch(l, "^changed +form +Project +" + Regex.Escape(MainForm) + "$"));
        lines.Should().Contain(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Trim() == MainForm);
    }

    [SourceLayoutFact]
    public async Task Diff_json_lists_the_items()
    {
        var folder = Copy(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "repo");
        EditForm(folder);
        using var cli = await ServerCliAsync();

        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", folder, "-o", "json")).Should().Be(ExitCodes.Differences, cli.Err.ToString());

        var item = JsonDocument.Parse(cli.Out.ToString()).RootElement.EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("status").GetString().Should().Be("changed");
        item.GetProperty("type").GetString().Should().Be("form");
        item.GetProperty("name").GetString().Should().Be("Project");
        item.GetProperty("path").GetString().Should().Be(MainForm);
        item.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Should().Equal(MainForm);
    }

    [SourceLayoutFact]
    public async Task Diff_with_zip_does_not_call_the_server()
    {
        var folder = Copy(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "repo");
        using var cli = new TestCli();
        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", folder, "--zip", Zip("XrmSharedTest"))).Should().Be(0, cli.Err.ToString());
        EditForm(folder);
        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", folder, "--zip", Zip("XrmSharedTest"))).Should().Be(ExitCodes.Differences, cli.Err.ToString());
        cli.Handler.Requests.Should().BeEmpty();
    }

    [SourceLayoutFact]
    public async Task Diff_unknown_solution_exits_4()
    {
        using var cli = await ServerCliAsync(_ => FakeHandler.Json(HttpStatusCode.NotFound,
            """{"error":{"code":"0x80040217","message":"The solution with unique name 'Nope' does not exist."}}"""));
        (await cli.RunAsync("solution", "diff", "Nope", "--folder", SourceLayoutFixtures.Folder("XrmSharedTest", 'a'))).Should().Be(ExitCodes.NotFound);
        cli.Err.ToString().Should().Contain("Nope");
        (await cli.RunAsync("solution", "sync", "Nope", "--folder", Path.Combine(_root, "new"))).Should().Be(ExitCodes.NotFound);
        Directory.Exists(Path.Combine(_root, "new")).Should().BeFalse();
    }

    [SourceLayoutFact]
    public async Task Diff_folder_without_solution_exits_2()
    {
        using var cli = await ServerCliAsync();
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", empty)).Should().Be(ExitCodes.Usage);
    }

    [SourceLayoutFact]
    public async Task Sync_dry_run_writes_nothing()
    {
        var target = Path.Combine(_root, "new");
        using var cli = await ServerCliAsync();
        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", target, "--dry-run")).Should().Be(0, cli.Err.ToString());
        Directory.Exists(target).Should().BeFalse();
        cli.Out.ToString().Should().Contain("Other/Solution.xml").And.Contain(MainForm);

        var folder = Copy(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "repo");
        EditForm(folder);
        File.WriteAllText(Path.Combine(folder, "Entities", "op365_Project", "SavedQueries", "{00000000-0000-0000-0000-000000000001}.xml"), "<savedqueries />");
        var before = Snapshot(folder);
        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", folder, "--dry-run", "-o", "json")).Should().Be(0, cli.Err.ToString());
        Snapshot(folder).Should().BeEquivalentTo(before);
        var plan = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        plan.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        plan.GetProperty("written").EnumerateArray().Select(e => e.GetString()).Should().Equal(MainForm);
        plan.GetProperty("removed").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Entities/op365_Project/SavedQueries/{00000000-0000-0000-0000-000000000001}.xml");
    }

    [SourceLayoutFact]
    public async Task Sync_leaves_managed_files_and_cdsproj()
    {
        var project = Copy(SourceLayoutFixtures.CdsprojSample, "project");
        var src = Path.Combine(project, "src");
        File.WriteAllText(Path.Combine(src, "Other", "Solution_managed.xml"), "managed");
        File.WriteAllText(Path.Combine(src, "notes.txt"), "mine");
        var stale = Path.Combine(src, "Entities", "op365_Project", "SavedQueries", "{00000000-0000-0000-0000-000000000001}.xml");
        File.WriteAllText(stale, "<savedqueries />");
        EditForm(src);
        var cdsproj = Directory.EnumerateFiles(project, "*.cdsproj").Single();
        var cdsprojBytes = File.ReadAllBytes(cdsproj);
        var outside = Directory.EnumerateFiles(project).Where(f => !f.EndsWith(".cdsproj", StringComparison.Ordinal)).ToDictionary(f => f, File.ReadAllBytes);

        using var cli = await ServerCliAsync();
        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", project)).Should().Be(0, cli.Err.ToString());

        File.ReadAllBytes(cdsproj).Should().Equal(cdsprojBytes);
        foreach (var (file, bytes) in outside) File.ReadAllBytes(file).Should().Equal(bytes);
        File.ReadAllText(Path.Combine(src, "Other", "Solution_managed.xml")).Should().Be("managed");
        File.ReadAllText(Path.Combine(src, "notes.txt")).Should().Be("mine");
        File.Exists(stale).Should().BeFalse();
        cli.Out.ToString().Should().Contain("1 written").And.Contain("1 removed");
        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", project)).Should().Be(0, cli.Out.ToString());
    }

    [SourceLayoutFact]
    public async Task Sync_into_empty_folder_then_diff_is_clean()
    {
        var target = Path.Combine(_root, "new");
        using var cli = await ServerCliAsync();
        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", target)).Should().Be(0, cli.Err.ToString());
        Snapshot(target).Should().BeEquivalentTo(Snapshot(SourceLayoutFixtures.Folder("XrmSharedTest", 'a')));
        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", target)).Should().Be(0, cli.Out.ToString());

        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", target, "-o", "json")).Should().Be(0, cli.Err.ToString());
        var again = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        again.GetProperty("written").GetArrayLength().Should().Be(0);
        again.GetProperty("removed").GetArrayLength().Should().Be(0);
    }

    [SourceLayoutFact]
    public async Task Sync_refuses_an_unrelated_non_empty_folder()
    {
        var target = Path.Combine(_root, "other");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "notes.txt"), "mine");
        using var cli = await ServerCliAsync();
        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", target)).Should().Be(ExitCodes.Usage);
        Directory.EnumerateFileSystemEntries(target).Should().ContainSingle();
    }

    // ---- the folder may be a repository: nothing outside the component folders is touched ------------------------------

    private static readonly string[] RepositoryFiles =
        ["README.md", ".git/HEAD", ".git/config", ".git/objects/ab/cdef0123", ".github/workflows/ci.yml", "docs/notes.md"];

    private static void AddRepositoryFiles(string dir)
    {
        foreach (var file in RepositoryFiles)
        {
            var path = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "mine: " + file);
        }
    }

    private static void RepositoryFilesAreIntact(string dir)
    {
        foreach (var file in RepositoryFiles) File.ReadAllText(Path.Combine(dir, file)).Should().Be("mine: " + file);
    }

    [SourceLayoutFact]
    public async Task Unpack_again_keeps_a_repository_around_the_folder()
    {
        var target = Path.Combine(_root, "repo");
        (await _cli.RunAsync("solution", "unpack", Zip("XrmSharedTest"), "--folder", target)).Should().Be(0, _cli.Err.ToString());
        AddRepositoryFiles(target);

        (await _cli.RunAsync("solution", "unpack", Zip("XrmSharedTest"), "--folder", target)).Should().Be(0, _cli.Err.ToString());

        _cli.Out.ToString().Should().Contain("0 written, 0 removed");
        RepositoryFilesAreIntact(target);
    }

    [SourceLayoutFact]
    public async Task Sync_diff_and_pack_keep_and_ignore_a_repository_around_the_folder()
    {
        var folder = Copy(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "repo");
        AddRepositoryFiles(folder);
        EditForm(folder);
        using var cli = await ServerCliAsync();

        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", folder)).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("1 written, 0 removed");
        RepositoryFilesAreIntact(folder);

        (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", folder)).Should().Be(0, cli.Out.ToString());
        var file = Path.Combine(_root, "packed.zip");
        (await cli.RunAsync("solution", "pack", folder, "--out", file)).Should().Be(0, cli.Err.ToString());
        PackedZip.Read(File.ReadAllBytes(file)).Files.Keys.Should().NotContain(k => k.Contains("README") || k.Contains(".git"));
        RepositoryFilesAreIntact(folder);
    }

    [SourceLayoutFact]
    public async Task Pack_still_refuses_a_stray_file_inside_a_component_folder()
    {
        var folder = Copy(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "repo");
        AddRepositoryFiles(folder);
        File.WriteAllText(Path.Combine(folder, "Entities", "notes.txt"), "x");
        (await _cli.RunAsync("solution", "pack", folder, "--out", Path.Combine(_root, "x.zip"))).Should().Be(ExitCodes.Usage);
        _cli.Err.ToString().Should().Contain("Entities/notes.txt");
    }

    // ---- export faults, solution names -------------------------------------------------------------------------------

    /// <summary>The fixture zip of XrmSharedTest with one entry changed.</summary>
    private static byte[] FixtureZipWith(string entry, Func<string, string> change)
    {
        var buffer = new MemoryStream();
        buffer.Write(SourceLayoutFixtures.Zip("XrmSharedTest"));
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var old = archive.GetEntry(entry)!;
            string text;
            using (var reader = new StreamReader(old.Open())) text = reader.ReadToEnd();
            old.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(change(text));
        }
        return buffer.ToArray();
    }

    private static HttpResponseMessage Export(byte[] zip) =>
        FakeHandler.Json(HttpStatusCode.OK, $$"""{"ExportSolutionFile":"{{Convert.ToBase64String(zip)}}"}""");

    [SourceLayoutFact]
    public async Task An_export_that_cannot_be_read_exits_1_and_an_unsupported_component_exits_5()
    {
        var broken = FixtureZipWith("customizations.xml", _ => "<ImportExportXml><Entities>");
        using (var cli = await ServerCliAsync(_ => Export(broken)))
        {
            (await cli.RunAsync("solution", "diff", "XrmSharedTest", "--folder", SourceLayoutFixtures.Folder("XrmSharedTest", 'a'))).Should().Be(ExitCodes.Unexpected);
            (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", Path.Combine(_root, "new"))).Should().Be(ExitCodes.Unexpected);
            Directory.Exists(Path.Combine(_root, "new")).Should().BeFalse();
        }

        // A business rule (a XAML process of category 2) has no reference folder: refused, never guessed.
        var unsupported = FixtureZipWith("customizations.xml", t => t.Replace("<Category>0</Category>", "<Category>2</Category>", StringComparison.Ordinal));
        using (var cli = await ServerCliAsync(_ => Export(unsupported)))
        {
            (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", Path.Combine(_root, "new"))).Should().Be(ExitCodes.Rejected);
            cli.Err.ToString().Should().Contain("Buttami");
        }
    }

    [SourceLayoutFact]
    public async Task Diff_with_the_zip_of_another_solution_exits_2()
    {
        var folder = SourceLayoutFixtures.Folder("XrmSharedTest", 'a');
        (await _cli.RunAsync("solution", "diff", "xrmsharedtest", "--folder", folder, "--zip", Zip("XrmSharedTest"))).Should().Be(0, _cli.Err.ToString());
        (await _cli.RunAsync("solution", "diff", "AnotherSolution", "--folder", folder, "--zip", Zip("XrmSharedTest"))).Should().Be(ExitCodes.Usage);
        _cli.Err.ToString().Should().Contain("XrmSharedTest").And.Contain("AnotherSolution");
    }

    [SourceLayoutFact]
    public async Task Unpack_and_sync_refuse_the_folder_of_another_solution()
    {
        var folder = Copy(SourceLayoutFixtures.Folder("XrmSharedTest", 'a'), "repo");
        var solutionXml = Path.Combine(folder, "Other", "Solution.xml");
        File.WriteAllText(solutionXml, File.ReadAllText(solutionXml).Replace("<UniqueName>XrmSharedTest</UniqueName>", "<UniqueName>AnotherSolution</UniqueName>", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "Entities", "stale.txt"), "x");
        var before = Snapshot(folder);

        (await _cli.RunAsync("solution", "unpack", Zip("XrmSharedTest"), "--folder", folder)).Should().Be(ExitCodes.Usage);
        _cli.Err.ToString().Should().Contain("AnotherSolution");
        using var cli = await ServerCliAsync();
        (await cli.RunAsync("solution", "sync", "XrmSharedTest", "--folder", folder)).Should().Be(ExitCodes.Usage);
        cli.Err.ToString().Should().Contain("AnotherSolution");
        Snapshot(folder).Should().BeEquivalentTo(before);
    }
}
