using FluentAssertions;
using Xrmd.Migration.Mapping;

namespace Xrmd.Migration.Tests.Mapping;

public sealed class MapSetTests
{
    private static MapFile Parse(string yaml, string name = "m.yaml") => MapReader.Parse("version: 1\n" + yaml, name);

    private static IReadOnlyList<string> Problems(params MapFile[] files)
    {
        var act = () => MapSet.Combine(files);
        return act.Should().Throw<MapValidationException>().Which.Problems;
    }

    [Fact]
    public void Reference_map_is_valid()
    {
        var file = MapReader.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mapping", "sales-like.yaml")), "sales-like.yaml");
        var set = MapSet.Combine([file]);

        set.IsEmpty.Should().BeFalse();
        set.Tables.Should().HaveCount(6);
        set.TargetOf("lead").Should().Be("xrmv_lead");
        set.TargetOf("account").Should().BeNull();
        set.BySource("account")!.Target.Should().Be("account");
        set.BySource("nothing").Should().BeNull();
        set.RelationshipTargetOf("leadcompetitors").Should().Be("xrmv_lead_xrmv_competitor");
        set.Requires.Should().ContainSingle().Which.Solution.Should().Be("XvMapSample");
    }

    [Fact]
    public void No_tables_and_no_relationships_is_empty()
    {
        MapSet.Combine([Parse("tables: []")]).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Rule2_renamed_source_twice_is_an_error()
    {
        var problems = Problems(Parse("tables: [{source: lead, target: xrmv_lead}]", "a.yaml"), Parse("tables: [{source: lead, target: xrmv_lead2}]", "b.yaml"));
        problems.Should().ContainSingle().Which.Should().Contain("lead").And.Contain("a.yaml").And.Contain("b.yaml");
    }

    [Fact]
    public void Rule2_two_sources_to_one_target_is_an_error()
    {
        var problems = Problems(Parse("tables: [{source: lead, target: xrmv_x}, {source: opportunity, target: xrmv_x}]"));
        problems.Should().ContainSingle().Which.Should().Contain("xrmv_x").And.Contain("lead").And.Contain("opportunity");
    }

    [Fact]
    public void Rule2_same_table_entries_merge_by_column()
    {
        var set = MapSet.Combine([
            Parse("tables: [{source: account, target: account, columns: {x: xrmv_a}, derived: {xrmv_d: {from: l.c}}}]", "a.yaml"),
            Parse("tables: [{source: account, target: account, columns: {y: xrmv_b}}]", "b.yaml"),
        ]);

        set.Tables.Should().ContainSingle();
        var account = set.BySource("account")!;
        account.Columns.Keys.Should().BeEquivalentTo("x", "y");
        account.Derived.Keys.Should().BeEquivalentTo("xrmv_d");
        set.Files.Should().HaveCount(2);
    }

    [Fact]
    public void Rule2_same_column_in_two_maps_is_an_error()
    {
        var problems = Problems(
            Parse("tables: [{source: account, target: account, columns: {x: xrmv_a}}]", "a.yaml"),
            Parse("tables: [{source: account, target: account, columns: {x: xrmv_b}}]", "b.yaml"));
        problems.Should().ContainSingle().Which.Should().Contain("account").And.Contain("'x'");
    }

    [Fact]
    public void State_on_a_same_table_entry_is_an_error()
    {
        Problems(Parse("tables: [{source: account, target: account, state: [{from: [0, 1], to: [0, 1]}]}]", "a.yaml"))
            .Should().ContainSingle().Which.Should().Contain("account").And.Contain("needs a renamed table").And.Contain("a.yaml");

        Problems(
                Parse("tables: [{source: account, target: account, stateFallback: {0: [0, 1]}}]", "a.yaml"),
                Parse("tables: [{source: account, target: account, stateFallback: {1: [1, 2]}}]", "b.yaml"))
            .Should().ContainSingle().Which.Should().Contain("needs a renamed table").And.Contain("a.yaml").And.Contain("b.yaml");

        MapSet.Combine([Parse("tables: [{source: account, target: account, columns: {creditlimit: xrmv_creditlimit}}]")])
            .BySource("account")!.State.Should().BeEmpty();
    }

    [Fact]
    public void Rule3_first_party_prefix_target_is_an_error()
    {
        var problems = Problems(Parse("tables: [{source: lead, target: msdyn_x}]\nrelationships: [{source: r, target: adx_y}]"));
        problems.Should().HaveCount(2);
        problems[0].Should().Contain("msdyn_x");
        problems[1].Should().Contain("adx_y");
    }

    [Fact]
    public void Rule4_duplicate_state_source_pair_is_an_error()
    {
        var problems = Problems(Parse("tables: [{source: lead, target: xrmv_lead, state: [{from: [0, 1], to: [0, 1]}, {from: [0, 1], to: [0, 2]}]}]"));
        problems.Should().ContainSingle().Which.Should().Contain("lead").And.Contain("[0, 1]");
    }

    [Fact]
    public void Rule4_one_source_state_to_two_target_states_is_an_error()
    {
        var problems = Problems(Parse("tables: [{source: lead, target: xrmv_lead, state: [{from: [0, 1], to: [0, 1]}, {from: [0, 2], to: [1, 2]}]}]"));
        problems.Should().ContainSingle().Which.Should().Contain("lead").And.Contain("statecode 0");
    }

    [Fact]
    public void Rule9_two_columns_to_one_target_column_is_an_error()
    {
        var problems = Problems(Parse("tables: [{source: lead, target: xrmv_lead, columns: {a: xrmv_t, b: xrmv_t}}]"));
        problems.Should().ContainSingle().Which.Should().Contain("xrmv_t").And.Contain("'a'").And.Contain("'b'");
    }

    [Fact]
    public void Relationship_source_twice_is_an_error()
    {
        var problems = Problems(Parse("relationships: [{source: r, target: t1}]", "a.yaml"), Parse("relationships: [{source: r, target: t2}]", "b.yaml"));
        problems.Should().ContainSingle().Which.Should().Contain("'r'");
    }

    [Fact]
    public void All_problems_are_collected_and_joined_in_the_message()
    {
        var act = () => MapSet.Combine([Parse("tables: [{source: a, target: msdyn_x}, {source: b, target: msdyn_x}]")]);
        var ex = act.Should().Throw<MapValidationException>().Which;
        ex.Problems.Should().HaveCountGreaterThan(1);
        ex.Message.Should().Be(string.Join("\n", ex.Problems));
    }

    [Fact]
    public void Requires_add_up_keeping_the_highest_version_per_solution()
    {
        var set = MapSet.Combine([
            Parse("requires: {solution: A, minVersion: 1.0}\ntables: []"),
            Parse("requires: [{solution: A, minVersion: 2.0}, {solution: B, minVersion: 1.0}]\ntables: []"),
        ]);
        set.Requires.Should().BeEquivalentTo([new MapRequirement("A", new Version(2, 0)), new MapRequirement("B", new Version(1, 0))]);
    }

    [Fact]
    public void Archive_target_needs_the_archive_prefix()
    {
        Problems(Parse("tables:\n  - { source: quote, target: xrmv_quote, archive: true }")).Should().ContainSingle()
            .Which.Should().Contain("must start with 'xrmva_'");
    }

    [Fact]
    public void Archive_entry_defaults_to_the_source_name_and_may_rename_or_drop_columns()
    {
        var set = MapSet.Combine([Parse("tables:\n  - source: quote\n    archive: true\n    columns:\n      name: xrmva_title\n      a: null\n      b: null\n")]);
        var quote = set.BySource("quote")!;
        quote.Archive.Should().BeTrue();
        quote.Target.Should().Be("xrmva_quote");
        set.TargetOf("quote").Should().Be("xrmva_quote");
    }

    [Fact]
    public void Archive_columns_rename_only_to_archive_columns_and_keep_their_values()
    {
        var problems = Problems(Parse("tables:\n  - source: quote\n    archive: true\n    columns:\n      name: xrmv_title\n      kind: { target: xrmva_kind, values: { 1: 2 } }\n"));
        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Contains("quote.name") && p.Contains("xrmva_"));
        problems.Should().Contain(p => p.Contains("quote.kind") && p.Contains("value translation"));
    }

    [Fact]
    public void Archive_entry_takes_no_derived_or_state()
    {
        var problems = Problems(Parse("tables:\n  - source: quote\n    archive: true\n    derived:\n      xrmva_x: { from: a.b }\n    state:\n      - { from: [0, 1], to: [0, 1] }\n"));
        problems.Should().Contain(p => p.Contains("derived")).And.Contain(p => p.Contains("state"));
    }
}
