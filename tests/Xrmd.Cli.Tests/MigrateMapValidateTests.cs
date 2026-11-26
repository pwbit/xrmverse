using FluentAssertions;

namespace Xrmd.Cli.Tests;

public sealed class MigrateMapValidateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-map-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, string yaml)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task Valid_map_prints_ok_and_the_counts()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Mapping", "sales-like.yaml");
        using var cli = new TestCli();
        (await cli.RunAsync("migrate", "map", "validate", fixture)).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("OK: 6 table(s), 2 relationship(s)");
    }

    [Fact]
    public async Task Broken_rule_2_prints_the_problem_and_exits_1()
    {
        var a = Write("a.yaml", "version: 1\ntables: [{source: lead, target: xrmv_lead}]");
        var b = Write("b.yaml", "version: 1\ntables: [{source: lead, target: xrmv_other}]");
        using var cli = new TestCli();
        (await cli.RunAsync("migrate", "map", "validate", a, b)).Should().Be(1);
        cli.Err.ToString().Should().Contain("'lead'").And.Contain("a.yaml");
        cli.Out.ToString().Should().NotContain("OK:");
    }

    [Fact]
    public async Task Unreadable_file_prints_its_name_and_exits_1()
    {
        var bad = Write("bad.yaml", "version: 1\ntables: [{source: a, target: b, colums: {}}]");
        using var cli = new TestCli();
        (await cli.RunAsync("migrate", "map", "validate", bad)).Should().Be(1);
        cli.Err.ToString().Should().Contain("bad.yaml: line 2:").And.Contain("colums");
    }

    [Fact]
    public async Task Missing_file_prints_its_name_and_exits_1()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("migrate", "map", "validate", Path.Combine(_root, "nope.yaml"))).Should().Be(1);
        cli.Err.ToString().Should().Contain("nope.yaml");
    }
}
