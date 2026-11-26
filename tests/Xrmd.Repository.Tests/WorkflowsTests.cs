using System.Xml.Linq;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Repository.Tests;

/// <summary>
/// Plan 10, Task 3: the GitHub workflows (pull request CI and release) run the license check and the vulnerability gate,
/// the release is blocked by them, attaches the SBOMs and ships the third-party notices with every artifact.
/// </summary>
public sealed class WorkflowsTests
{
    private const string Notices = "THIRD_PARTY_NOTICES.md";
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WorkflowsDir = Path.Combine(RepoRoot, ".github", "workflows");

    [Fact]
    public void Workflows_use_read_only_permissions_except_the_release_job()
    {
        var files = Directory.GetFiles(WorkflowsDir, "*.yml").Concat(Directory.GetFiles(WorkflowsDir, "*.yaml"))
            .Select(Path.GetFileName).ToArray();
        files.Should().Contain(new[] { "ci.yml", "release.yml" });

        var violations = files.SelectMany(f => PermissionViolations(f!, Load(f!))).ToArray();

        violations.Should().BeEmpty("workflows get contents: read; only the release job may write contents (to attach its files) and only the image job packages and id-token (push and sign)");
        var release = (YamlMappingNode)Job(Load("release.yml"), "release")["permissions"];
        release.Children.Select(c => $"{c.Key}: {c.Value}").Should().Equal("contents: write");
        var image = (YamlMappingNode)Job(Load("release.yml"), "image")["permissions"];
        image.Children.Select(c => $"{c.Key}: {c.Value}").Should().Equal("contents: read", "packages: write", "id-token: write");
        // The Marketplace image workflow stays in the private repository (eng/public-export/manifest.txt).
        if (files.Contains("marketplace-image.yml"))
        {
            var marketplace = (YamlMappingNode)Job(Load("marketplace-image.yml"), "build")["permissions"];
            marketplace.Children.Select(c => $"{c.Key}: {c.Value}").Should().Equal("contents: write", "packages: read", "id-token: write");
        }
        var pages = (YamlMappingNode)Job(Load("pages.yml"), "deploy")["permissions"];
        pages.Children.Select(c => $"{c.Key}: {c.Value}").Should().Equal("pages: write", "id-token: write");
    }

    [Fact]
    public void Permission_rule_reports_write_access_outside_the_release_job()
    {
        var workflow = Map("""
            permissions: write-all
            jobs:
              build:
                runs-on: ubuntu-latest
                permissions:
                  contents: write
                  pull-requests: read
              release:
                runs-on: ubuntu-latest
                permissions:
                  contents: write
              quiet:
                runs-on: ubuntu-latest
                permissions:
                  contents: read
                  id-token: none
            """);

        PermissionViolations("ci.yml", workflow).Should().Equal(
            "ci.yml: top-level permissions must be exactly 'contents: read'",
            "ci.yml: job 'build' has 'contents: write'",
            "ci.yml: job 'release' has 'contents: write'");
        PermissionViolations("release.yml", workflow).Should().Equal(
            "release.yml: top-level permissions must be exactly 'contents: read'",
            "release.yml: job 'build' has 'contents: write'");
        PermissionViolations("ci.yml", Map("jobs: {}")).Should().Equal(
            "ci.yml: top-level permissions must be exactly 'contents: read'");
    }

    [Fact]
    public void Release_archives_contain_the_notices()
    {
        var archive = Step(Job(Load("release.yml"), "publish"), "Archive");
        var commands = Run(archive).Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains("zip ", StringComparison.Ordinal) || l.Contains("tar ", StringComparison.Ordinal))
            .ToArray();

        commands.Should().Contain(l => l.Contains("zip ", StringComparison.Ordinal), "the Windows archive is a zip");
        commands.Should().Contain(l => l.Contains("tar ", StringComparison.Ordinal), "the macOS and Linux archives are tar.gz");
        commands.Should().OnlyContain(l => l.Contains(Notices, StringComparison.Ordinal),
            "every archive carries the notices beside the executable");
        commands.Should().OnlyContain(l => l.Contains("dotnet-runtime-LICENSE.txt", StringComparison.Ordinal)
                                           && l.Contains("dotnet-runtime-THIRD-PARTY-NOTICES.txt", StringComparison.Ordinal),
            "the self-contained executable embeds the .NET runtime, so its license and notices ship too");
        var script = Run(archive);
        script.Should().Contain("LICENSE.TXT").And.Contain("THIRD-PARTY-NOTICES.TXT").And.Contain("Microsoft.NETCore.App.Runtime");
    }

    [Fact]
    public void Tool_package_and_server_image_contain_the_notices()
    {
        var cli = XDocument.Load(Path.Combine(RepoRoot, "src", "Xrmd.Cli", "Xrmd.Cli.csproj"));
        cli.Descendants("None").Should().Contain(
            n => (string?)n.Attribute("Include") == @"..\..\" + Notices && (string?)n.Attribute("Pack") == "true",
            "the dotnet tool package carries the notices");

        var dockerfile = File.ReadAllLines(Path.Combine(RepoRoot, "docker", "Dockerfile"));
        var runtime = dockerfile.SkipWhile(l => !l.StartsWith("FROM ", StringComparison.Ordinal) || !l.Contains(" AS runtime", StringComparison.Ordinal));
        runtime.Should().Contain($"COPY {Notices} ./", "the server image has the notices in /app");

        var dockerignore = File.ReadAllLines(Path.Combine(RepoRoot, ".dockerignore")).Select(l => l.Trim()).ToList();
        dockerignore.Should().Contain("!" + Notices, "*.md is ignored, so the notices need an exception to reach the build");
        dockerignore.IndexOf("!" + Notices).Should().BeGreaterThan(dockerignore.IndexOf("*.md"), "the last matching rule wins");
    }

    [Fact]
    public void Release_is_blocked_by_the_scan_and_attaches_the_sboms()
    {
        var release = Load("release.yml");
        var scan = Runs(Job(release, "scan"));
        scan.Should().Contain("eng/licenses.sh vulnerabilities");
        scan.Should().Contain($"eng/licenses.sh check --notices {Notices}");

        foreach (var job in new[] { "publish", "pack", "sbom", "release" })
        {
            Needs(release, job).Should().Contain("scan", $"a High/Critical advisory or a license violation blocks '{job}'");
        }

        Needs(release, "release").Should().Contain(new[] { "publish", "pack", "sbom" });
        var sbom = Runs(Job(release, "sbom"));
        sbom.Should().Contain("--scope cli").And.Contain("xrmd-cli-${VERSION}.cdx.json");
        sbom.Should().Contain("--scope server").And.Contain("xrmd-server-${VERSION}.cdx.json");
        Uses(Job(release, "sbom")).Should().Contain("actions/upload-artifact@v4");
        sbom.Should().Contain($"cp {Notices} sbom/", "the notices are attached to the release beside the SBOMs (legal checklist item 3)");
        var upload = Steps(Job(release, "sbom")).Single(s => s.Children.TryGetValue(new YamlScalarNode("uses"), out var u)
                                                              && u.ToString().StartsWith("actions/upload-artifact", StringComparison.Ordinal));
        ((YamlMappingNode)upload["with"])["path"].ToString().Should().Be("sbom/*",
            "one path, so the artifact is flat and 'assets/*' of the release job finds the files");
    }

    [Fact]
    public void Pull_request_ci_builds_tests_and_runs_both_gates()
    {
        var ci = Load("ci.yml");
        var on = (YamlMappingNode)ci["on"];
        on.Children.Keys.Select(k => k.ToString()).Should().Contain("pull_request");
        ((YamlSequenceNode)((YamlMappingNode)on["push"])["branches"]).Select(b => b.ToString()).Should().Equal("main");

        var build = Runs(Job(ci, "build-test"));
        build.Should().Contain("dotnet build").And.Contain("dotnet test \"$project\" --no-build --filter \"Category!=Acceptance&Category!=Timing\"");
        var ui = Runs(Job(ci, "ui"));
        foreach (var command in new[]
                 {
                     "pnpm -C ui install --frozen-lockfile", "pnpm -C ui test", "pnpm -C ui typecheck", "pnpm -C ui build",
                 })
        {
            ui.Should().Contain(command);
        }

        var integration = Runs(Job(ci, "integration"));
        integration.Should().Contain("eng/test-shard.sh tests/Xrmd.Integration.Tests")
            .And.Contain("dotnet test tests/Xrmd.Integration.Tests --no-build --filter \"Category!=Acceptance&Category!=Timing&($shard)\"");
        Needs(ci, "integration").Should().Contain("ui", "the integration tests need the web admin build (HostStaticWebAssetsTests)");

        Runs(Job(ci, "licenses")).Should().Contain($"eng/licenses.sh check --notices {Notices}");
        Runs(Job(ci, "vulnerabilities")).Should().Contain("eng/licenses.sh vulnerabilities");
    }

    /// <summary>
    /// Plan 2.7a, Task 8: the integration tests on PostgreSQL run every night and on demand only (never on a pull request or a push),
    /// in the shards of the SQL Server job, with the same filter (plan 2.7b: PostgreSQL runs every feature).
    /// </summary>
    [Fact]
    public void Postgres_integration_tests_run_nightly_and_on_demand_only()
    {
        var ci = Load("ci.yml");
        var job = Job(ci, "postgres");
        job["if"].ToString().Should().StartWith("(github.event_name == 'schedule' || github.event_name == 'workflow_dispatch')");
        Needs(ci, "postgres").Should().Contain("ui", "the integration tests need the web admin build (HostStaticWebAssetsTests)");
        ((YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)job["strategy"])["matrix"])["shard"]).Select(n => n.ToString())
            .Should().Equal("1", "2", "3", "4");
        var test = Steps(job).Single(s => Run(s).Contains("dotnet test", StringComparison.Ordinal));
        ((YamlMappingNode)test["env"])["XRMD_TEST_PROVIDER"].ToString().Should().Be("postgres");
        Run(test).Should().Contain("eng/test-shard.sh tests/Xrmd.Integration.Tests")
            .And.Contain("dotnet test tests/Xrmd.Integration.Tests --no-build --filter \"Category!=Acceptance&Category!=Timing&($shard)\"");
        Job(ci, "integration").Children.Should().NotContainKey(new YamlScalarNode("env"), "the SQL Server job keeps the default provider");
    }

    [Fact]
    public void Every_setup_dotnet_step_uses_dotnet_10()
    {
        var versions = new[] { "ci.yml", "release.yml" }
            .SelectMany(f => Load(f).Children.Where(c => c.Key.ToString() == "jobs").SelectMany(c => ((YamlMappingNode)c.Value).Children)
                .SelectMany(j => Steps((YamlMappingNode)j.Value)))
            .Where(s => s.Children.TryGetValue(new YamlScalarNode("uses"), out var u) && u.ToString().StartsWith("actions/setup-dotnet", StringComparison.Ordinal))
            .Select(s => ((YamlMappingNode)s["with"])["dotnet-version"].ToString())
            .ToArray();

        versions.Should().HaveCount(9, "five in ci.yml and four in release.yml");
        versions.Should().OnlyContain(v => v == "10.0.x");
    }

    [Fact]
    public void Ci_and_release_run_the_no_sdk_check()
    {
        Runs(Job(Load("ci.yml"), "build-test")).Should().Contain("eng/check-no-sdk.sh");
        Runs(Job(Load("release.yml"), "scan")).Should().Contain("eng/check-no-sdk.sh");
        File.ReadAllText(Path.Combine(RepoRoot, "eng", "check-no-sdk.sh"))
            .Should().Contain("Microsoft.Xrm.Sdk.dll").And.Contain("Microsoft.Crm.Sdk.Proxy.dll")
            .And.Contain("Microsoft.PowerPlatform.Dataverse.Client.dll");
    }

    [Fact]
    public void Release_job_scans_the_downloaded_assets_before_attaching_them()
    {
        var steps = Steps(Job(Load("release.yml"), "release")).ToList();
        int Index(Func<YamlMappingNode, bool> match) => steps.FindIndex(s => match(s));
        bool UsesAction(YamlMappingNode s, string action) =>
            s.Children.TryGetValue(new YamlScalarNode("uses"), out var u) && u.ToString().StartsWith(action, StringComparison.Ordinal);

        var checkout = Index(s => UsesAction(s, "actions/checkout"));
        var download = Index(s => UsesAction(s, "actions/download-artifact"));
        var scan = Index(s => Run(s).Contains("eng/check-no-sdk.sh --artifacts assets", StringComparison.Ordinal));
        var attach = Index(s => UsesAction(s, "softprops/action-gh-release"));

        checkout.Should().BeGreaterThanOrEqualTo(0, "the scan script comes from the repository");
        scan.Should().BeGreaterThan(download, "the files handed to customers are the ones scanned");
        scan.Should().BeGreaterThan(checkout);
        attach.Should().BeGreaterThan(scan, "a release with an SDK assembly is never created");

        File.ReadAllText(Path.Combine(WorkflowsDir, "release.yml")).Should().Contain("image-publish job",
            "a future image-publish job is told to scan the image (byo-sdk checklist step 4)");
    }

    [Fact]
    public void Devcontainer_workflow_runs_the_tests_inside_the_container()
    {
        var workflow = Load("devcontainer.yml");
        var on = (YamlMappingNode)workflow["on"];
        on.Children.Keys.Select(k => k.ToString()).Should().Contain("workflow_dispatch");
        ((YamlSequenceNode)((YamlMappingNode)on["pull_request"])["paths"]).Select(p => p.ToString())
            .Should().Equal(".devcontainer/**", ".github/workflows/devcontainer.yml");
        var permissions = (YamlMappingNode)workflow["permissions"];
        permissions.Children.Select(c => $"{c.Key}: {c.Value}").Should().Equal("contents: read");

        var jobs = ((YamlMappingNode)workflow["jobs"]).Children.Select(c => c.Key.ToString()).ToArray();
        jobs.Should().Equal("test");
        var job = Job(workflow, "test");
        job["runs-on"].ToString().Should().Be("ubuntu-latest");

        var steps = Steps(job).ToList();
        var ci = steps.FindIndex(s => s.Children.TryGetValue(new YamlScalarNode("uses"), out var u) && u.ToString() == "devcontainers/ci@v0.3");
        ci.Should().BeGreaterThanOrEqualTo(0);
        var with = (YamlMappingNode)steps[ci]["with"];
        with["runCmd"].ToString().Should().Be("dotnet test --filter \"Category!=Acceptance&Category!=Timing\"", "the same tests as ci.yml; the acceptance tests accept the SQL Server terms and the timing tests (S.2e3b Decision 9) are run by hand (issue #60)");
        with["push"].ToString().Should().Be("never");

        var free = steps.FindIndex(s => Run(s).Contains("sudo rm -rf /usr/local/lib/android /opt/ghc /usr/local/.ghcup /opt/hostedtoolcache/CodeQL", StringComparison.Ordinal));
        free.Should().BeGreaterThanOrEqualTo(0);
        free.Should().BeLessThan(ci, "the disk is freed before the container is built");
    }

    // ---- rule ----

    internal static IEnumerable<string> PermissionViolations(string file, YamlMappingNode workflow)
    {
        if (!workflow.Children.TryGetValue(new YamlScalarNode("permissions"), out var top)
            || top is not YamlMappingNode topMap || topMap.Children.Count != 1
            || topMap.Children.Single() is not { Key: YamlScalarNode { Value: "contents" }, Value: YamlScalarNode { Value: "read" } })
        {
            yield return $"{file}: top-level permissions must be exactly 'contents: read'";
        }

        if (!workflow.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs) || jobs is not YamlMappingNode jobMap)
        {
            yield break;
        }

        foreach (var (name, job) in jobMap.Children)
        {
            if (job is not YamlMappingNode jobNode || !jobNode.Children.TryGetValue(new YamlScalarNode("permissions"), out var permissions))
            {
                continue;
            }

            var releaseJob = file == "release.yml" && name.ToString() == "release";
            var imageJob = file == "release.yml" && name.ToString() == "image";
            var marketplaceJob = file == "marketplace-image.yml" && name.ToString() == "build";
            var pagesJob = file == "pages.yml" && name.ToString() == "deploy";
            if (permissions is not YamlMappingNode permissionMap)
            {
                yield return $"{file}: job '{name}' has '{permissions}'";
                continue;
            }

            foreach (var (scope, level) in permissionMap.Children)
            {
                var value = level.ToString();
                var allowed = value is "read" or "none"
                              || (releaseJob && scope.ToString() == "contents" && value == "write" && permissionMap.Children.Count == 1)
                              // The image job pushes to GHCR and signs keylessly: exactly these two extra scopes.
                              || (imageJob && scope.ToString() is "packages" or "id-token" && value == "write"
                                  && permissionMap.Children.Count == 3)
                              // The Marketplace image job signs in to Azure by OIDC (id-token) and reads draft releases,
                              // which GITHUB_TOKEN sees only with contents: write. It never writes to the repository.
                              || (marketplaceJob && scope.ToString() is "id-token" or "contents" && value == "write")
                              // The website deploy job publishes to GitHub Pages: exactly pages and id-token (OIDC), nothing else.
                              || (pagesJob && scope.ToString() is "pages" or "id-token" && value == "write"
                                  && permissionMap.Children.Count == 2);
                if (!allowed)
                {
                    yield return $"{file}: job '{name}' has '{scope}: {value}'";
                }
            }
        }
    }

    // ---- helpers ----

    private static YamlMappingNode Load(string file) => Map(File.ReadAllText(Path.Combine(WorkflowsDir, file)));

    private static YamlMappingNode Map(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Job(YamlMappingNode workflow, string name) =>
        (YamlMappingNode)((YamlMappingNode)workflow["jobs"])[name];

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job) =>
        ((YamlSequenceNode)job["steps"]).Cast<YamlMappingNode>();

    private static YamlMappingNode Step(YamlMappingNode job, string name) =>
        Steps(job).Single(s => s.Children.TryGetValue(new YamlScalarNode("name"), out var n) && n.ToString() == name);

    private static string Run(YamlMappingNode step) =>
        step.Children.TryGetValue(new YamlScalarNode("run"), out var run) ? run.ToString() : "";

    private static string Runs(YamlMappingNode job) => string.Join('\n', Steps(job).Select(Run));

    private static string[] Uses(YamlMappingNode job) =>
        Steps(job).Where(s => s.Children.ContainsKey(new YamlScalarNode("uses"))).Select(s => s["uses"].ToString()).ToArray();

    private static string[] Needs(YamlMappingNode workflow, string job) =>
        Job(workflow, job).Children.TryGetValue(new YamlScalarNode("needs"), out var needs)
            ? needs is YamlSequenceNode seq ? seq.Select(n => n.ToString()).ToArray() : new[] { needs.ToString() }
            : Array.Empty<string>();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "xrmverse.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }
}
