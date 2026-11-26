using System.Text;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3a, Task 1: which core messages change the request they are given. For every scenario of the core response goldens
/// (<see cref="CoreScenarios"/>), the request is run through the core's interface directly (as (b) there) and dumped before and after,
/// with object identities as tokens; <c>tests/fixtures/core-response-golden/mutation.txt</c> holds one line per message:
/// <c>unchanged</c>, or the paths that changed (<c>+</c> added, <c>-</c> removed, <c>~</c> changed or replaced by another instance),
/// and <c>(fault)</c> when the call failed. Task 3 checks that the extension path reproduces it. Captured on the code before
/// <c>IPipelineCore</c> moved onto the model; write-if-missing as the goldens.
/// </summary>
[Collection("xrmd")]
public sealed class CoreRequestMutationProbeTests(XrmdFixture fx)
{
    [Fact]
    public async Task Core_changes_its_requests_as_the_probe_file_says()
    {
        var file = new StringBuilder();
        foreach (var scenario in CoreScenarios.All)
        {
            await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
            var request = await scenario.Arrange(kit);
            var tokens = new CoreDump.Tokens();
            var before = CoreDump.Request(request, tokens);
            var (_, fault) = await CoreScenarios.ViaCoreAsync(kit, scenario, request);
            var after = CoreDump.Request(request, tokens);
            file.Append(scenario.Message).Append(": ").Append(Changes(before, after)).Append(fault is null ? "" : " (fault)").Append('\n');
        }

        CoreDump.AssertGolden("mutation.txt", file.ToString());
    }

    /// <summary>The probe itself sees an added attribute, a changed value, a replaced instance and a removed parameter.</summary>
    [Fact]
    public void Probe_sees_each_kind_of_change()
    {
        var target = new Entity("account") { ["name"] = "a" };
        var request = new CreateRequest { Target = target, ["SolutionUniqueName"] = "s" };
        var tokens = new CoreDump.Tokens();
        var before = CoreDump.Request(request, tokens);
        target["name"] = "b";
        target["description"] = "added";
        request.Parameters.Remove("SolutionUniqueName");
        var middle = CoreDump.Request(request, tokens);
        request.Target = new Entity("account") { ["name"] = "b", ["description"] = "added" };

        Changes(before, middle).Should().Be(
            "~Request/Parameters, -Request/Parameters/SolutionUniqueName, ~Request/Parameters/Target/Attributes, " +
            "+Request/Parameters/Target/Attributes/description, ~Request/Parameters/Target/Attributes/name");
        // A replaced Target is a new instance, and so is each collection it owns; the values are the same.
        Changes(middle, CoreDump.Request(request, tokens)).Should().Be(
            "~Request/Parameters/Target, ~Request/Parameters/Target/Attributes, ~Request/Parameters/Target/FormattedValues, " +
            "~Request/Parameters/Target/KeyAttributes, ~Request/Parameters/Target/RelatedEntities");
        Changes(middle, middle).Should().Be("unchanged");
    }

    /// <summary>The changed paths, topmost first (a path under an added or removed one is not repeated), or <c>unchanged</c>.</summary>
    internal static string Changes(CoreDump before, CoreDump after)
    {
        var was = before.Entries.ToDictionary(e => e.Path, e => e.Line);
        var now = after.Entries.ToDictionary(e => e.Path, e => e.Line);
        var changes = new List<(string Path, char Mark)>();
        foreach (var (path, line) in now)
            if (!was.TryGetValue(path, out var old)) changes.Add((path, '+'));
            else if (old != line) changes.Add((path, '~'));
        foreach (var path in was.Keys)
            if (!now.ContainsKey(path)) changes.Add((path, '-'));

        var reported = changes
            .Where(c => c.Mark == '~' || !changes.Any(o => o.Mark == c.Mark && c.Path.StartsWith(o.Path + "/", StringComparison.Ordinal)))
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => c.Mark + c.Path)
            .ToList();
        return reported.Count == 0 ? "unchanged" : string.Join(", ", reported);
    }
}
