using FluentAssertions;
using Xrmd.Licenses;

namespace Xrmd.Repository.Tests;

/// <summary>
/// Plan 10, Task 3 (ruling P8): the vulnerability gate of <c>tools/Xrmd.Licenses</c>. The fixtures under
/// Data/vulnerabilities/ have the shapes of <c>dotnet list package --vulnerable --include-transitive --format json</c>
/// and <c>pnpm audit --prod --json</c> as observed on this repository (the moderate pnpm file is a trimmed real output).
/// </summary>
public sealed class VulnerabilitiesTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data", "vulnerabilities");

    [Fact]
    public void Vulnerability_gate_fails_on_high_and_critical_only()
    {
        var nuget = Gate("dotnet-high.json", "pnpm-audit-moderate.json");
        nuget.Exit.Should().Be(1, "NuGet has a High and a Critical advisory");
        nuget.Errors.Should().BeEquivalentTo(new[]
        {
            "error: nuget Acme.Critical 1.0.0 Critical https://github.com/advisories/GHSA-0000-0000-0001",
            "error: nuget System.Security.Cryptography.Xml 8.0.2 High https://github.com/advisories/GHSA-37gx-xxp4-5rgx",
        }, o => o.WithStrictOrdering(), "one line per advisory, most severe first, a package in two projects once");
        nuget.Output.Should().Contain(new[]
        {
            "warning: nuget System.Security.Cryptography.Pkcs 8.0.1 Moderate https://github.com/advisories/GHSA-2fmv-rv2c-59x7",
            "warning: nuget Acme.Critical 1.0.0 Low https://github.com/advisories/GHSA-0000-0000-0002",
            "warning: npm react-router 6.30.6 Moderate https://github.com/advisories/GHSA-wrjc-x8rr-h8h6",
            "warning: npm react-router 6.30.6 Moderate https://github.com/advisories/GHSA-337j-9hxr-rhxg",
        });

        var npm = Gate("dotnet-none.json", "pnpm-audit-critical.json");
        npm.Exit.Should().Be(1, "npm has a High and a Critical advisory");
        npm.Errors.Should().BeEquivalentTo(new[]
        {
            "error: npm acme-critical 1.0.0 Critical https://github.com/advisories/GHSA-0000-0000-0003",
            "error: npm acme-critical 1.1.0 Critical https://github.com/advisories/GHSA-0000-0000-0003",
            "error: npm acme-high 2.0.0 High https://github.com/advisories/GHSA-0000-0000-0004",
        }, o => o.WithStrictOrdering(), "one line per installed version of the package");
        npm.Output.Should().Contain("warning: npm acme-low 3.0.0 Low https://github.com/advisories/GHSA-0000-0000-0005");

        var moderate = Gate("dotnet-moderate.json", "pnpm-audit-moderate.json");
        moderate.Exit.Should().Be(0, "Moderate and Low are warnings (P8)");
        moderate.Errors.Should().BeEmpty();
        moderate.Output.Should().HaveCount(4, "three warnings and the summary")
            .And.EndWith("3 advisories: 0 high or critical, 3 moderate or low");

        var none = Gate("dotnet-none.json", "pnpm-audit-none.json");
        none.Exit.Should().Be(0);
        none.Output.Should().Equal("0 advisories: 0 high or critical, 0 moderate or low");
    }

    [Theory]
    [InlineData("dotnet-problem.json", "pnpm-audit-none.json", "No assets file was found")]
    [InlineData("dotnet-none.json", "pnpm-audit-error.json", "ERR_PNPM_AUDIT_BAD_RESPONSE")]
    [InlineData("missing.json", "pnpm-audit-none.json", "missing.json")]
    [InlineData("dotnet-nu1900.json", "pnpm-audit-none.json", "NU1900")]
    public void Vulnerability_gate_fails_closed_on_input_it_cannot_use(string dotnet, string pnpm, string named)
    {
        var result = Gate(dotnet, pnpm);

        result.Exit.Should().Be(2, "a scan that did not run must not pass the gate");
        string.Join('\n', result.Errors).Should().Contain(named);
    }

    [Fact]
    public void Vulnerability_gate_needs_both_inputs()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Program.Run(new[] { "vulnerabilities", "--dotnet", Path.Combine(DataDir, "dotnet-none.json") }, stdout, stderr)
            .Should().Be(2);
        stderr.ToString().Should().Contain("missing --pnpm-audit");
    }

    private static (int Exit, string[] Output, string[] Errors) Gate(string dotnet, string pnpm)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Program.Run(
            new[] { "vulnerabilities", "--dotnet", Path.Combine(DataDir, dotnet), "--pnpm-audit", Path.Combine(DataDir, pnpm) },
            stdout,
            stderr);
        return (exit, Lines(stdout), Lines(stderr));
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
}
