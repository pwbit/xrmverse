using FluentAssertions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Text assertions on .github/workflows/release.yml (no YAML dependency): the server image is pushed by version tag only,
/// signed, scanned for SDK assemblies, and handed to customers as an offline archive on a draft release.
/// </summary>
public class ReleaseWorkflowTests
{
    private const string Image = "ghcr.io/pwbit/xrmd";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }

    private static string[] Lines() =>
        File.ReadAllLines(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));

    /// <summary>The lines of a top-level job (two-space-indented key under jobs:), comments removed.</summary>
    private static string Job(string name)
    {
        var lines = Lines();
        var start = Array.FindIndex(lines, l => l == $"  {name}:");
        start.Should().BeGreaterThanOrEqualTo(0, $"job '{name}' exists");
        var body = lines.Skip(start + 1).TakeWhile(l => l.Length == 0 || l.StartsWith("    ") || l.TrimStart().StartsWith('#'));
        return string.Join('\n', body.Where(l => !l.TrimStart().StartsWith('#')));
    }

    /// <summary>The lines of the step of a job whose body contains <paramref name="marker"/>: from its "- " line to the next one.</summary>
    private static string Step(string job, string marker)
    {
        var lines = job.Split('\n');
        var at = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
        at.Should().BeGreaterThanOrEqualTo(0, $"'{marker}' is in the job");
        var start = at;
        while (start > 0 && !lines[start].TrimStart().StartsWith("- ")) start--;
        var end = Array.FindIndex(lines, at + 1, l => l.TrimStart().StartsWith("- ") && l.Length - l.TrimStart().Length == lines[start].Length - lines[start].TrimStart().Length);
        return string.Join('\n', lines.Skip(start).Take((end < 0 ? lines.Length : end) - start));
    }

    private static string WithoutComments() =>
        string.Join('\n', Lines().Where(l => !l.TrimStart().StartsWith('#')));

    [Fact]
    public void Image_job_pushes_only_the_version_tag()
    {
        var image = Job("image");
        image.Should().Contain($"IMAGE: {Image}");
        WithoutComments().Should().NotContain(":latest");
        image.Should().Contain("docker push").And.Contain("refs/tags/v");
        image.Should().NotContain("--all-tags");
        image.Should().Contain("load: true", "the image is built locally, scanned, and only then pushed");
        image.Should().Contain("platforms: linux/amd64");
        image.Should().Contain("tags: ${{ env.IMAGE }}:");
        image.Should().Contain("labels: org.opencontainers.image.source=${{ github.server_url }}/${{ github.repository }}",
            "GHCR links the package to the repository through this label; without it the workflow token may be refused");
        image.Should().Contain("file: docker/Dockerfile");
    }

    [Fact]
    public void Image_job_has_only_the_permissions_it_needs()
    {
        var image = Job("image");
        image.Should().Contain("permissions:").And.Contain("packages: write").And.Contain("id-token: write").And.Contain("contents: read");
        image.Should().NotContain("contents: write");

        var top = string.Join('\n', Lines().TakeWhile(l => l != "jobs:"));
        top.Should().Contain("permissions:\n  contents: read");
        top.Should().NotContain("packages").And.NotContain("id-token");
        Job("release").Should().Contain("contents: write").And.NotContain("packages").And.NotContain("id-token");
        foreach (var other in new[] { "scan", "sbom", "publish", "pack" })
            Job(other).Should().NotContain("packages: write").And.NotContain("id-token");
    }

    [Fact]
    public void Release_is_a_draft()
    {
        var release = Job("release");
        release.Should().Contain("draft: true");
        release.Should().Contain("prerelease: ${{ contains(github.ref_name, '-') }}");
        release.Should().MatchRegex(@"needs: \[[^\]]*\bimage\b");
        release.Should().Contain("image-assets/*", "the offline archive and its checksum are attached");
    }

    [Fact]
    public void Offline_archive_holds_only_the_xrmd_image()
    {
        var image = Job("image");
        image.Should().Contain("docker save").And.Contain("| gzip");
        image.Should().Contain("xrmd-server-${VERSION}-linux-amd64.tar.gz").And.Contain("sha256sum");
        var save = image.Split('\n').Single(l => l.Contains("docker save"));
        save.Should().Contain("${IMAGE}:");
        image.Should().Contain($"IMAGE: {Image}");
        save.Should().NotContain("mssql").And.NotContain("caddy");
        WithoutComments().Should().NotContain("mssql/server").And.NotContain("caddy");
    }

    [Fact]
    public void Image_is_signed_and_carries_the_sbom()
    {
        var image = Job("image");
        image.Should().Contain("sigstore/cosign-installer");
        image.Should().Contain("cosign sign").And.Contain("cosign attest --type cyclonedx");
        image.Should().Contain("@${DIGEST}", "signing is by digest, never by tag");
        image.Should().Contain("xrmd-server-${VERSION}.cdx.json");
        image.Should().MatchRegex(@"needs: \[scan, sbom\]");
    }

    [Fact]
    public void Image_is_scanned_for_sdk_assemblies()
    {
        var image = Job("image");
        var create = image.IndexOf("docker create", StringComparison.Ordinal);
        var export = image.IndexOf("docker export", StringComparison.Ordinal);
        var scan = image.IndexOf("eng/check-no-sdk.sh --tree", StringComparison.Ordinal);
        var push = image.IndexOf("docker push", StringComparison.Ordinal);
        create.Should().BeGreaterThanOrEqualTo(0);
        export.Should().BeGreaterThan(create);
        scan.Should().BeGreaterThan(export);
        push.Should().BeGreaterThan(scan, "nothing is pushed before the scan passes");
    }

    [Fact]
    public void Manual_runs_build_everything_and_push_nothing()
    {
        string.Join('\n', Lines().TakeWhile(l => l != "jobs:")).Should().Contain("workflow_dispatch");
        var image = Job("image");
        // A manual run on a v* tag ref has that ref too: only the tag push event publishes.
        const string OnTag = "if: github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')";
        Step(image, "docker push").Should().Contain(OnTag, "the push step itself runs on tags only");
        Step(image, "cosign sign ").Should().Contain(OnTag);
        Step(image, "docker/login-action").Should().Contain(OnTag);
        Step(image, "sigstore/cosign-installer").Should().Contain(OnTag).And.Contain("cosign-release: v2.");
        Step(image, "docker save").Should().NotContain("if:", "the archive is built on every run");
        Job("release").Should().Contain(OnTag);
        Lines().Where(l => l.Contains("startsWith(github.ref, 'refs/tags/v')", StringComparison.Ordinal))
            .Should().OnlyContain(l => l.Contains("github.event_name == 'push' && ", StringComparison.Ordinal),
                "every publishing condition also requires the push event");
    }

    [Fact]
    public void Every_setup_dotnet_step_uses_dotnet_10()
    {
        var versions = Lines().Where(l => l.TrimStart().StartsWith("dotnet-version:", StringComparison.Ordinal))
            .Select(l => l.Split(':', 2)[1].Trim()).ToArray();
        versions.Should().HaveCount(4, "the four jobs that set up .NET");
        versions.Should().OnlyContain(v => v == "10.0.x");
    }
}
