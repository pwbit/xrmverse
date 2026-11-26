using FluentAssertions;
using Xrmd.Migration.Mapping;

namespace Xrmd.Migration.Tests.Mapping;

public sealed class MapReaderTests
{
    private static MapFile Reference() =>
        MapReader.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mapping", "sales-like.yaml")), "sales-like.yaml");

    [Fact]
    public void Reads_the_reference_map()
    {
        var map = Reference();

        map.Version.Should().Be(1);
        map.Tables.Should().HaveCount(6);
        map.Relationships.Should().HaveCount(2);
        map.Requires[0].Should().Be(new MapRequirement("XvMapSample", new Version(0, 1, 0, 0)));

        var lead = map.Tables.Single(t => t.Source == "lead");
        lead.Target.Should().Be("xrmv_lead");
        lead.IsRenamed.Should().BeTrue();
        lead.CarryCustomColumns.Should().BeTrue();
        var source = lead.Columns["leadsourcecode"];
        source.Target.Should().Be("xrmv_source");
        source.Values![8].Should().Be(100000000);
        source.ByLabel.Should().BeTrue();
        source.Fallback.Should().Be(new MapFallback(true, 100000006));
        lead.Columns["subject"].Target.Should().Be("xrmv_topic");
        lead.Columns["subject"].Values.Should().BeNull();
        lead.Columns["subject"].Fallback.IsSet.Should().BeFalse();
        lead.Derived["xrmv_qualifiedon"].LookupColumn.Should().Be("qualifyingopportunityid");
        lead.Derived["xrmv_qualifiedon"].Column.Should().Be("createdon");
        lead.StateFallback[2].Should().Be((1, 100000004));
        lead.State.Should().Contain(new MapStatePair((2, 4), (1, 100000001)));

        map.Tables.Single(t => t.Source == "account").IsRenamed.Should().BeFalse();
        map.Relationships[0].Should().Be(new MapRelationship("leadcompetitors", "xrmv_lead_xrmv_competitor"));
    }

    [Fact]
    public void Fallback_null_is_set_and_empty()
    {
        Reference().Tables.Single(t => t.Source == "lead").Columns["leadqualitycode"].Fallback.Should().Be(new MapFallback(true, null));
    }

    [Fact]
    public void Requires_accepts_a_list()
    {
        var map = MapReader.Parse("version: 1\nrequires: [{solution: A, minVersion: 1.0}, {solution: B, minVersion: 2.0.0.0}]\ntables: []", "m");
        map.Requires.Should().Equal(new MapRequirement("A", new Version(1, 0)), new MapRequirement("B", new Version(2, 0, 0, 0)));
    }

    [Fact]
    public void Unknown_key_is_an_error_with_its_line()
    {
        var act = () => MapReader.Parse("tables: [{source: a, target: b, colums: {}}]", "m");
        act.Should().Throw<MapFormatException>().Which.Message.Should().StartWith("line 1:").And.Contain("colums");
    }

    [Fact]
    public void Version_other_than_1_is_an_error()
    {
        var act = () => MapReader.Parse("version: 2\ntables: []", "m");
        act.Should().Throw<MapFormatException>().Which.Message.Should().StartWith("line 1:").And.Contain("version");
    }

    [Fact]
    public void Derived_from_needs_one_dot()
    {
        var act = () => MapReader.Parse("version: 1\ntables:\n  - source: a\n    target: b\n    derived:\n      x: { from: createdon }\n", "m");
        act.Should().Throw<MapFormatException>().Which.Message.Should().StartWith("line 6:").And.Contain("<lookup>.<column>");
    }

    [Theory]
    [InlineData("carry", true)]
    [InlineData("drop", false)]
    public void CustomColumns_accepts_carry_and_drop_only(string value, bool carry)
    {
        var map = MapReader.Parse($"version: 1\ntables: [{{source: a, target: b, customColumns: {value}}}]", "m");
        map.Tables[0].CarryCustomColumns.Should().Be(carry);

        var act = () => MapReader.Parse("version: 1\ntables: [{source: a, target: b, customColumns: keep}]", "m");
        act.Should().Throw<MapFormatException>().Which.Message.Should().Contain("customColumns");
    }

    [Fact]
    public void Duplicate_key_is_an_error_with_its_line()
    {
        var act = () => MapReader.Parse("version: 1\ntables:\n  - source: a\n    target: b\n    target: c\n", "m");
        act.Should().Throw<MapFormatException>().Which.Message.Should().StartWith("line 5:").And.Contain("target");
    }

    [Fact]
    public void Derived_values_and_null_fallback_are_read()
    {
        var outcome = Reference().Tables.Single(t => t.Source == "opportunityclose").Derived["xrmv_outcome"];
        outcome.LookupColumn.Should().Be("opportunityid");
        outcome.Column.Should().Be("statecode");
        outcome.Values.Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 100000000, [2] = 100000001 });
        outcome.ByLabel.Should().BeFalse();
        outcome.Fallback.Should().Be(new MapFallback(true, null));
    }

    [Fact]
    public void Archive_key_is_read()
    {
        var map = MapReader.Parse("version: 1\ntables:\n  - source: quote\n    target: xrmva_quote\n    archive: true\n    columns:\n      name: xrmva_title\n      description: null\n      note: { target: null }\n  - source: invoice\n    archive: true\n  - source: lead\n    target: xrmv_lead\n", "m.yaml");

        var quote = map.Tables[0];
        quote.Archive.Should().BeTrue();
        quote.Columns["name"].Target.Should().Be("xrmva_title");
        quote.Columns["description"].Drop.Should().BeTrue();
        quote.Columns["note"].Drop.Should().BeTrue();
        map.Tables[1].Target.Should().Be("xrmva_invoice", "an archive entry may leave the target out");
        map.Tables[2].Archive.Should().BeFalse();
    }

    [Fact]
    public void Dropping_a_column_needs_an_archive_entry()
    {
        var act = () => MapReader.Parse("version: 1\ntables:\n  - source: lead\n    target: xrmv_lead\n    columns:\n      subject: null\n", "m.yaml");
        act.Should().Throw<MapFormatException>().Which.Message.Should().StartWith("line 3:").And.Contain("archive");
    }

    [Fact]
    public void Archive_must_be_true_or_false()
    {
        var act = () => MapReader.Parse("version: 1\ntables:\n  - source: quote\n    archive: yes\n", "m.yaml");
        act.Should().Throw<MapFormatException>().Which.Message.Should().StartWith("line 4:").And.Contain("true or false");
    }
}
