using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Repository.Tests;

/// <summary>
/// Plan 2.9, Task 1: the dev container definition (.NET 10, Node 22, docker-in-docker, amd64, open-licensed extensions,
/// CI-ordered one-time setup, no secret and no license acceptance).
/// </summary>
public sealed class DevContainerTests
{
    private const string DockerInDocker = "ghcr.io/devcontainers/features/docker-in-docker:2";
    private const string Node = "ghcr.io/devcontainers/features/node:1";
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string DevContainerDir = Path.Combine(RepoRoot, ".devcontainer");

    [Fact]
    public void Image_is_the_dotnet_10_dev_container()
    {
        Root().GetProperty("build").GetProperty("dockerfile").GetString().Should().Be("Dockerfile");
        File.ReadAllLines(Path.Combine(DevContainerDir, "Dockerfile")).Where(l => l.StartsWith("FROM", StringComparison.Ordinal))
            .Should().Equal("FROM --platform=linux/amd64 mcr.microsoft.com/devcontainers/dotnet:2-10.0");
    }

    [Fact]
    public void Features_are_docker_in_docker_and_node_22()
    {
        var features = Root().GetProperty("features");

        features.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { DockerInDocker, Node });
        features.GetProperty(Node).GetProperty("version").GetString().Should().Be("22");
        features.GetProperty(Node).GetProperty("pnpmVersion").GetString().Should().Be("none", "pnpm comes from corepack and the packageManager pin");
    }

    [Fact]
    public void Runs_as_amd64_with_unprivileged_port_80()
    {
        var runArgs = Root().GetProperty("runArgs").EnumerateArray().Select(e => e.GetString()).ToList();

        runArgs.Should().Contain("--platform=linux/amd64");
        var sysctl = runArgs.IndexOf("--sysctl");
        sysctl.Should().BeGreaterThanOrEqualTo(0);
        runArgs.Should().HaveCountGreaterThan(sysctl + 1);
        runArgs[sysctl + 1].Should().Be("net.ipv4.ip_unprivileged_port_start=80");
    }

    [Fact]
    public void Extensions_are_open_licensed()
    {
        var extensions = Root().GetProperty("customizations").GetProperty("vscode").GetProperty("extensions")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        extensions.Should().Equal("ms-dotnettools.csharp", "dbaeumer.vscode-eslint", "ms-azuretools.vscode-docker", "editorconfig.editorconfig");
        extensions.Should().NotContain("ms-dotnettools.csdevkit");
    }

    [Fact]
    public void Post_create_builds_in_ci_order()
    {
        Root().GetProperty("postCreateCommand").GetString().Should().Be(".devcontainer/post-create.sh");

        var path = Path.Combine(DevContainerDir, "post-create.sh");
        File.Exists(path).Should().BeTrue();
        var lines = File.ReadAllLines(path);
        lines.Take(2).Should().Equal("#!/usr/bin/env bash", "set -euo pipefail");

        lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l != "set -euo pipefail")
            .Should().Equal("corepack enable", "cd ui", "pnpm install --frozen-lockfile", "pnpm build", "cd ..", "dotnet restore", "dotnet build --no-restore");

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().HaveFlag(UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Nothing_accepts_or_holds_a_secret()
    {
        var forbidden = new[] { "ACCEPT_EULA", "accept-microsoft-license", "accept-sql-server-eula", "SA_PASSWORD", "Password=" };
        var files = (Directory.Exists(DevContainerDir) ? Directory.GetFiles(DevContainerDir, "*", SearchOption.AllDirectories) : Array.Empty<string>())
            .Append(Path.Combine(RepoRoot, ".github", "workflows", "devcontainer.yml")).ToArray();

        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
            {
                text.Contains(word, StringComparison.OrdinalIgnoreCase).Should().BeFalse($"{file} must not contain '{word}'");
            }
        }
    }

    private static JsonElement Root()
    {
        var text = File.ReadAllText(Path.Combine(DevContainerDir, "devcontainer.json"));
        return JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement;
    }

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
