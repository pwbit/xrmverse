using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Migration.Tests;

namespace Xrmd.Cli.Tests;

/// <summary>
/// <c>xrmd migrate import --dry-run [--archive a,b] [--save-map file]</c> (migration table mapping spec §8.7): after the dry run's report
/// the owner chooses archive tables among the candidates it lists, and the map file gets them as text.
/// </summary>
[Collection("migrate-import")]
public sealed class MigrateDryRunArchiveTests : IDisposable
{
    private const string Url = "http://xrmd.test:5000";
    private static readonly Guid PackageId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RunId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string Prompt = "Create which as archive tables? (e.g. 1,3-5; empty = none): ";

    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-archive-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// The dry run's report: two candidates, and as <c>maps</c> the request's maps, else <paramref name="packageMaps"/> (as the server
    /// reports the maps a run used).
    /// </summary>
    private static string Report(JsonNode? start, string[] packageMaps, string state) => new JsonObject
    {
        ["runId"] = RunId.ToString(), ["state"] = state, ["dryRun"] = true, ["message"] = null, ["hasErrors"] = false,
        ["tables"] = new JsonArray(), ["issues"] = new JsonArray(), ["issuesTruncated"] = false, ["lookupsNulled"] = new JsonArray(),
        ["users"] = new JsonObject(), ["mappedTables"] = new JsonArray(),
        ["archiveCandidates"] = new JsonArray(new JsonObject { ["table"] = "quote", ["rows"] = 2 }, new JsonObject { ["table"] = "invoice", ["rows"] = 7 }),
        ["maps"] = start?["maps"]?.DeepClone() ?? new JsonArray([.. packageMaps.Select(m => (JsonNode)m)]),
        ["phases"] = new JsonArray(),
    }.ToJsonString();

    private static Func<HttpRequestMessage, HttpResponseMessage> Server(string[]? packageMaps = null, string state = "completed")
    {
        JsonNode? start = null;
        return req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.Method;
            if (path == "/admin/migrate/packages" && method == "POST") return FakeHandler.Json(HttpStatusCode.Created, $$"""{"packageId":"{{PackageId}}"}""");
            if (path.StartsWith($"/admin/migrate/packages/{PackageId}/chunks/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path == $"/admin/migrate/packages/{PackageId}/complete") return FakeHandler.Json(HttpStatusCode.OK, $$"""{"packageId":"{{PackageId}}","state":"complete"}""");
            if (path == "/admin/migrate/runs" && method == "POST")
            {
                start = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result);
                return FakeHandler.Json(HttpStatusCode.Accepted, $$"""{"runId":"{{RunId}}","asyncOperationId":"{{Guid.NewGuid()}}","dryRun":true}""");
            }
            if (path == $"/admin/migrate/runs/{RunId}" && method == "GET")
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"runId":"{{RunId}}","state":"{{state}}","phase":1,"phases":["verify","dry-run"]}""");
            if (path == $"/admin/migrate/runs/{RunId}/report") return FakeHandler.Json(HttpStatusCode.OK, Report(start, packageMaps ?? [], state));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static async Task<TestCli> CliAsync(bool tty, string input = "", string[]? packageMaps = null, string state = "completed")
    {
        var cli = new TestCli(Server(packageMaps, state)) { IsInputTty = tty, Input = input };
        cli.Vars["XRMD_MIGRATE_PASSPHRASE"] = SyntheticPackage.Passphrase;
        (await cli.LoginDevAsync(Url)).Should().Be(0, cli.Err.ToString());
        return cli;
    }

    private string Map(string name, string yaml)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task Dry_run_prompt_updates_the_map_file()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var original = "# our map\n" + SyntheticPackage.SalesLikeYaml;
        var map = Map("migration.yaml", original);
        using var cli = await CliAsync(tty: true, "1\n");

        (await cli.RunAsync("migrate", "import", file, "--map", map, "--dry-run")).Should().Be(0, cli.Err.ToString());

        cli.Out.ToString().Should().Contain("## Archive candidates", "the report comes first");
        var err = cli.Err.ToString();
        err.Should().Contain("1. invoice (7 rows)\n2. quote (2 rows)\n" + Prompt, "largest first");
        err.Should().Contain($"Updated {map}: 1 archive table(s) added. Import with: xrmd migrate import {file} --map {map}");
        var yaml = await File.ReadAllTextAsync(map);
        yaml.Should().Be(original + "  # 7 row(s); columns become xrmva_<column>\n  - { source: invoice, target: xrmva_invoice, archive: true }\n");

        // A second dry run shows the choice as made.
        cli.Input = "\n";
        (await cli.RunAsync("migrate", "import", file, "--map", map, "--dry-run")).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().Contain($"Already archive tables in {map}: invoice");
    }

    [Fact]
    public async Task Empty_answer_leaves_the_file_untouched()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var map = Map("migration.yaml", SyntheticPackage.SalesLikeYaml);
        var written = File.GetLastWriteTimeUtc(map);
        using var cli = await CliAsync(tty: true, "\n");

        (await cli.RunAsync("migrate", "import", file, "--map", map, "--dry-run")).Should().Be(0, cli.Err.ToString());

        cli.Err.ToString().Should().Contain(Prompt).And.NotContain("Updated");
        (await File.ReadAllTextAsync(map)).Should().Be(SyntheticPackage.SalesLikeYaml);
        File.GetLastWriteTimeUtc(map).Should().Be(written);
    }

    [Fact]
    public async Task Bad_answer_is_asked_again()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var map = Map("migration.yaml", SyntheticPackage.SalesLikeYaml);
        using var cli = await CliAsync(tty: true, "3\n1-2\n");

        (await cli.RunAsync("migrate", "import", file, "--map", map, "--dry-run")).Should().Be(0, cli.Err.ToString());

        cli.Err.ToString().Should().Contain("'3' is not a number from 1 to 2.").And.Contain("2 archive table(s) added");
    }

    [Fact]
    public async Task Archive_flag_without_terminal_updates_the_file()
    {
        // The map comes from the package: --save-map names the file, which starts as the package's map.
        var file = SyntheticPackage.BuildFile(_root, withMaps: true);
        var saved = Path.Combine(_root, "saved.yaml");
        using var cli = await CliAsync(tty: false, packageMaps: [SyntheticPackage.SalesLikeYaml]);

        (await cli.RunAsync("migrate", "import", file, "--dry-run", "--archive", "quote, invoice", "--save-map", saved)).Should().Be(0, cli.Err.ToString());

        cli.Err.ToString().Should().NotContain(Prompt)
            .And.Contain($"Updated {saved}: 2 archive table(s) added. Import with: xrmd migrate import {file} --map {saved}");
        var yaml = await File.ReadAllTextAsync(saved);
        yaml.Should().StartWith(SyntheticPackage.SalesLikeYaml)
            .And.Contain("- { source: quote, target: xrmva_quote, archive: true }").And.Contain("- { source: invoice, target: xrmva_invoice, archive: true }");

        // A table the dry run did not list is refused, and the file stays as it is.
        (await cli.RunAsync("migrate", "import", file, "--dry-run", "--archive", "salesorder", "--save-map", saved)).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain("'salesorder' is not an archive candidate");
        (await File.ReadAllTextAsync(saved)).Should().Be(yaml);
    }

    [Fact]
    public async Task Archive_without_dry_run_is_a_usage_error()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var map = Map("migration.yaml", SyntheticPackage.SalesLikeYaml);
        using var cli = await CliAsync(tty: false);

        (await cli.RunAsync("migrate", "import", file, "--map", map, "--archive", "quote")).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain("--archive and --save-map need --dry-run");
        (await cli.RunAsync("migrate", "import", file, "--save-map", map)).Should().Be(2, cli.Err.ToString());
        // --archive with no file to write: refused before anything is sent.
        (await cli.RunAsync("migrate", "import", file, "--dry-run", "--archive", "quote")).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain("Give --save-map <file> to save the choices.");
        cli.Handler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.StartsWith("/admin/migrate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_maps_need_save_map()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var first = Map("accounts.yaml", SyntheticPackage.SalesLikeYaml);
        var second = Map("contacts.yaml", "version: 1\ntables:\n  - source: contact\n    target: xv_person\n");
        using var cli = await CliAsync(tty: true, "1\n");

        (await cli.RunAsync("migrate", "import", file, "--map", first, "--map", second, "--dry-run")).Should().Be(0, cli.Err.ToString());

        cli.Err.ToString().Should().Contain("Give --save-map <file> to save the choices.").And.NotContain(Prompt);
        (await File.ReadAllTextAsync(first)).Should().Be(SyntheticPackage.SalesLikeYaml);

        // A new --save-map file cannot start as two maps: they must be merged into one first.
        var saved = Path.Combine(_root, "archive.yaml");
        cli.Input = "2\n";
        (await cli.RunAsync("migrate", "import", file, "--map", first, "--map", second, "--dry-run", "--save-map", saved)).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain("The dry run used 2 maps").And.Contain("merge them into one file");
        File.Exists(saved).Should().BeFalse();

        // A file that holds both maps takes the choices, and is imported alone.
        await File.WriteAllTextAsync(saved, "version: 1\ntables:\n  - source: account\n    target: xv_company\n  - source: contact\n    target: xv_person\n");
        cli.Input = "2\n";
        (await cli.RunAsync("migrate", "import", file, "--map", first, "--map", second, "--dry-run", "--save-map", saved)).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().Contain($"Import with: xrmd migrate import {file} --map {saved}\n");
        (await File.ReadAllTextAsync(saved)).Should().Contain("- { source: quote, target: xrmva_quote, archive: true }");
    }

    [Fact]
    public async Task Server_path_without_map_starts_the_file_from_the_package_maps()
    {
        var saved = Path.Combine(_root, "saved.yaml");
        using var cli = await CliAsync(tty: false, packageMaps: [SyntheticPackage.SalesLikeYaml]);

        (await cli.RunAsync("migrate", "import", "--server-path", "/srv/import/pkg.xrmpkg", "--dry-run", "--archive", "quote", "--save-map", saved))
            .Should().Be(0, cli.Err.ToString());

        cli.Err.ToString().Should().Contain($"Import with: xrmd migrate import --server-path /srv/import/pkg.xrmpkg --map {saved}");
        (await File.ReadAllTextAsync(saved)).Should().Be(SyntheticPackage.SalesLikeYaml
            + "  # 2 row(s); columns become xrmva_<column>\n  - { source: quote, target: xrmva_quote, archive: true }\n");
    }

    [Fact]
    public async Task Existing_save_map_must_hold_the_package_maps()
    {
        var file = SyntheticPackage.BuildFile(_root, withMaps: true);
        var saved = Map("saved.yaml", "# archive tables\nversion: 1\n");
        using var cli = await CliAsync(tty: false, packageMaps: [SyntheticPackage.SalesLikeYaml]);

        (await cli.RunAsync("migrate", "import", file, "--dry-run", "--archive", "quote", "--save-map", saved)).Should().Be(2, cli.Err.ToString());

        cli.Err.ToString().Should().Contain($"{saved} does not hold table 'account' of the map(s) the dry run used");
        (await File.ReadAllTextAsync(saved)).Should().Be("# archive tables\nversion: 1\n");
    }

    [Fact]
    public async Task Same_dry_run_twice_with_save_map()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var map = Map("migration.yaml", SyntheticPackage.SalesLikeYaml);
        var saved = Path.Combine(_root, "saved.yaml");
        using var cli = await CliAsync(tty: false);

        string[] args = ["migrate", "import", file, "--map", map, "--dry-run", "--archive", "quote", "--save-map", saved];
        (await cli.RunAsync(args)).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().Contain($"Import with: xrmd migrate import {file} --map {saved}\n");
        var yaml = await File.ReadAllTextAsync(saved);

        (await cli.RunAsync(args)).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().Contain($"{saved} already has these tables.");
        (await File.ReadAllTextAsync(saved)).Should().Be(yaml);

        args[^3] = "quote,invoice";
        (await cli.RunAsync(args)).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().Contain($"Updated {saved}: 1 archive table(s) added. Import with: xrmd migrate import {file} --map {saved}\n");
        (await File.ReadAllTextAsync(map)).Should().Be(SyntheticPackage.SalesLikeYaml, "the --map file is left as it is");
    }

    [Fact]
    public async Task Failed_dry_run_saves_no_choice()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var map = Map("migration.yaml", SyntheticPackage.SalesLikeYaml);
        using var cli = await CliAsync(tty: false, state: "failed");

        (await cli.RunAsync("migrate", "import", file, "--map", map, "--dry-run", "--archive", "quote")).Should().Be(5, cli.Err.ToString());

        cli.Err.ToString().Should().Contain("The choices were not saved: the dry run failed.");
        (await File.ReadAllTextAsync(map)).Should().Be(SyntheticPackage.SalesLikeYaml);
    }
}
