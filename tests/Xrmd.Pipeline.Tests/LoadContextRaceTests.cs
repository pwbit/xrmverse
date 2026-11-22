using System.Diagnostics;
using FluentAssertions;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// The SDK's lazy known-types initialization racing plug-in loads (normal and early-bound) must never deadlock. The SDK
/// initializes once per process, so every attempt runs <c>Xrmd.Pipeline.RaceProbe</c> in a fresh process, which forces
/// the interleaving that used to hang (see its Program.cs).
/// </summary>
public class LoadContextRaceTests
{
    private const int Attempts = 20;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Known_types_initialization_racing_plugin_loads_never_hangs()
    {
        var probe = ProbePath();
        File.Exists(probe).Should().BeTrue($"the race probe should be built at {probe}");

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var (exitCode, output) = await RunAsync(probe);
            exitCode.Should().Be(0, $"attempt {attempt} of the race probe should end (2 = deadlock, -1 = killed): {output}");
        }
    }

    /// <summary>The probe in its own output directory (same configuration and framework as this test assembly).</summary>
    private static string ProbePath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = output.Name;
        var configuration = output.Parent!.Name;
        var tests = output.Parent.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(tests, "Xrmd.Pipeline.RaceProbe", "bin", configuration, framework, "Xrmd.Pipeline.RaceProbe.dll");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string probe)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var info = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(probe);
        info.ArgumentList.Add(AppContext.BaseDirectory);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return (-1, "timed out: " + await stderr);
        }

        return (process.ExitCode, await stdout + await stderr);
    }
}
