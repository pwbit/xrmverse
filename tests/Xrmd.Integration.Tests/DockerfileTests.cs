using System.Text.RegularExpressions;
using FluentAssertions;

namespace Xrmd.Integration.Tests;

/// <summary>Static checks of docker/Dockerfile: the image must restore every project the host needs and ship the web shell.</summary>
public class DockerfileTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }

    private static string[] DockerfileLines() =>
        File.ReadAllLines(Path.Combine(RepoRoot(), "docker", "Dockerfile")).Select(l => l.Trim()).Where(l => !l.StartsWith('#')).ToArray();

    /// <summary>Project names (e.g. "Xrmd.Core") reachable from Xrmd.Host through ProjectReference, host included.</summary>
    private static HashSet<string> HostClosure()
    {
        var root = RepoRoot();
        var seen = new HashSet<string>();
        var pending = new Stack<string>();
        pending.Push("Xrmd.Host");
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!seen.Add(name)) continue;
            var csproj = File.ReadAllText(Path.Combine(root, "src", name, name + ".csproj"));
            foreach (Match m in Regex.Matches(csproj, @"ProjectReference Include=""\.\.\\([^\\]+)\\"))
                pending.Push(m.Groups[1].Value);
        }
        return seen;
    }

    [Fact]
    public void Restore_stage_copies_the_project_file_of_every_project_the_host_references()
    {
        var copied = DockerfileLines()
            .Select(l => Regex.Match(l, @"^COPY\s+src/(Xrmd(?:\.[A-Za-z]+)+)/\*\.csproj\s+src/\1/$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();

        HostClosure().Should().NotBeEmpty();
        copied.Should().BeEquivalentTo(HostClosure(), "restore must see every project in the host's reference graph, and nothing else");
    }

    [Fact]
    public void Image_builds_the_ui_with_a_pinned_pnpm_and_a_frozen_lockfile()
    {
        var lines = DockerfileLines();
        lines.Should().Contain(l => Regex.IsMatch(l, @"^FROM node:22[\w.\-]* AS ui$"));
        lines.Should().Contain(l => l.Contains("corepack") && l.Contains("pnpm") && !l.Contains("latest"));
        lines.Should().Contain(l => l.Contains("pnpm install --frozen-lockfile"));
        lines.Should().Contain(l => l.Contains("pnpm build"));

        var pkg = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "package.json"));
        pkg.Should().MatchRegex("\"packageManager\":\\s*\"pnpm@10\\.\\d+\\.\\d+\"", "corepack pins pnpm from this field");
    }

    [Fact]
    public void Runtime_image_serves_the_shell_from_the_hosts_default_ui_root_as_a_non_root_user()
    {
        var lines = DockerfileLines();
        lines.Should().Contain("COPY --from=ui /repo/ui/dist/ ./wwwroot/", "the host reads Ui:RootPath, default 'wwwroot' under the content root (/app)");
        lines.Should().Contain("USER $APP_UID");
        lines.Select((l, i) => (l, i)).Last(t => t.l.StartsWith("COPY --from=ui")).i
            .Should().BeLessThan(lines.ToList().IndexOf("USER $APP_UID"));
    }

    [Fact]
    public void Image_sets_the_sdk_dir_and_copies_no_sdk()
    {
        var lines = DockerfileLines();
        lines.Should().Contain("ENV XRMD_SDK_DIR=/var/lib/xrmd/sdk");
        lines.Should().Contain(l => l.StartsWith("RUN mkdir -p") && l.Contains("/var/lib/xrmd/sdk"));
        // The directory stays root-owned (the container user only reads it); only files and backups are chowned to the app user.
        lines.Where(l => l.Contains("chown")).Should().OnlyContain(l => !l.Contains("/var/lib/xrmd/sdk"));
        // The SDK is the customer's own copy, mounted at run time: nothing in the Dockerfile may bring in a Microsoft SDK assembly.
        var text = string.Join('\n', File.ReadAllLines(Path.Combine(RepoRoot(), "docker", "Dockerfile")));
        foreach (var dll in new[] { "Microsoft.Xrm.Sdk", "Microsoft.Crm.Sdk.Proxy", "Microsoft.PowerPlatform.Dataverse.Client" })
            text.Should().NotContain(dll);
        lines.Should().NotContain(l => l.StartsWith("COPY") && l.Contains("sdk", StringComparison.OrdinalIgnoreCase) && !l.Contains("Xrmd."));
    }

    [Fact]
    public void Dockerignore_keeps_ui_build_output_and_dependencies_out_of_the_context()
    {
        var ignored = File.ReadAllLines(Path.Combine(RepoRoot(), ".dockerignore")).Select(l => l.Trim()).ToArray();
        ignored.Should().Contain("ui/node_modules");
        ignored.Should().Contain("ui/dist");
    }
}
