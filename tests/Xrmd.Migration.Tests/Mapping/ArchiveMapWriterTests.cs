using FluentAssertions;
using Xrmd.Migration.Mapping;

namespace Xrmd.Migration.Tests.Mapping;

/// <summary>The archive tables chosen after a dry run, added to the map file as text (migration table mapping spec §8.7).</summary>
public sealed class ArchiveMapWriterTests
{
    private static readonly string LongColumn = "new_" + new string('x', 70);

    private static SourceCatalog Catalog() => new(
    [
        new SourceCatalogTable("quote", "quoteid", 1084, "user", false,
        [
            new SourceCatalogColumn("quoteid", "Uniqueidentifier", [], false), new SourceCatalogColumn("name", "String", [], false),
            new SourceCatalogColumn("opportunityid", "Lookup", ["opportunity"], false),
        ]),
        new SourceCatalogTable("invoice", "invoiceid", 1090, "user", false,
        [
            new SourceCatalogColumn("invoiceid", "Uniqueidentifier", [], false), new SourceCatalogColumn(LongColumn, "String", [], true),
        ]),
        new SourceCatalogTable("account", "accountid", 1, "user", false, [new SourceCatalogColumn("accountid", "Uniqueidentifier", [], false)]),
    ], []);

    private static readonly Dictionary<string, long> Candidates = new(StringComparer.Ordinal) { ["quote"] = 2, ["invoice"] = 7 };

    private static string ReferenceMap() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mapping", "sales-like.yaml"));

    private static MapSet Valid(string yaml) => MapSet.Combine([MapReader.Parse(yaml, "map")]);

    [Fact]
    public void Long_column_names_are_shortened_deterministically()
    {
        var yaml = ArchiveMapWriter.Append(null, ["invoice"], Catalog(), Candidates);

        var shortened = ArchiveNames.Column(LongColumn);
        shortened.Should().NotBe("xrmva_" + LongColumn, "the name would not fit");
        yaml.Should().Contain($"# shortened names: {LongColumn} -> {shortened}", "the owner sees the shortened name and can rename it");
        ArchiveMapWriter.Append(null, ["invoice"], Catalog(), Candidates).Should().Be(yaml, "the same names every time");
        ArchiveMapWriter.Append(null, ["quote"], Catalog(), Candidates).Should().NotContain("shortened", "short names are kept whole");
    }

    [Fact]
    public void Append_keeps_the_existing_text_and_comments()
    {
        // The reference map ends with its relationships: the entry goes at the end of its tables list, every original byte kept around it.
        var original = ReferenceMap();
        var yaml = ArchiveMapWriter.Append(original, ["quote"], Catalog(), Candidates);

        var entry = "  # 2 row(s); columns become xrmva_<column>\n  - { source: quote, target: xrmva_quote, archive: true }\n";
        var at = original.IndexOf("\n\nrelationships:", StringComparison.Ordinal) + 1;
        yaml.Should().Be(original[..at] + entry + original[at..]);
        Valid(yaml).BySource("quote").Should().BeEquivalentTo(new { Target = "xrmva_quote", Archive = true });
        Valid(yaml).BySource("lead")!.Target.Should().Be("xrmv_lead");

        // A map whose tables list ends the file: the original text is a prefix of the result.
        var tablesLast = "# Our map\nversion: 1\nrelationships: []\ntables:\n- source: lead   # leads\n  target: xrmv_lead\n# end of the map";
        var appended = ArchiveMapWriter.Append(tablesLast, ["quote", "invoice"], Catalog(), Candidates);
        appended.Should().StartWith(tablesLast + "\n");
        appended[(tablesLast.Length + 1)..].Should().Be(
            "# 2 row(s); columns become xrmva_<column>\n- { source: quote, target: xrmva_quote, archive: true }\n"
            + $"# 7 row(s); columns become xrmva_<column>\n# shortened names: {LongColumn} -> {ArchiveNames.Column(LongColumn)}\n"
            + "- { source: invoice, target: xrmva_invoice, archive: true }\n");
        Valid(appended).Tables.Should().HaveCount(3);
    }

    [Fact]
    public void Append_keeps_windows_line_ends()
    {
        var original = "version: 1\r\ntables:\r\n  - { source: lead, target: xrmv_lead }\r\n";
        ArchiveMapWriter.Append(original, ["quote"], Catalog(), Candidates).Should().Be(
            original + "  # 2 row(s); columns become xrmva_<column>\r\n  - { source: quote, target: xrmva_quote, archive: true }\r\n");
    }

    [Fact]
    public void Append_creates_a_map_when_none()
    {
        var expected = "version: 1\ntables:\n  # 2 row(s); columns become xrmva_<column>\n  - { source: quote, target: xrmva_quote, archive: true }\n";
        ArchiveMapWriter.Append(null, ["quote"], Catalog(), Candidates).Should().Be(expected);
        ArchiveMapWriter.Append("", ["quote"], Catalog(), Candidates).Should().Be(expected);
        ArchiveMapWriter.Append("# archive tables only\n", ["quote"], Catalog(), Candidates).Should().Be("# archive tables only\n" + expected);
        // A map without a tables list gets one at its end.
        ArchiveMapWriter.Append("version: 1\nrequires: { solution: XvMapSample, minVersion: 0.1.0.0 }", ["quote"], Catalog(), Candidates)
            .Should().Be("version: 1\nrequires: { solution: XvMapSample, minVersion: 0.1.0.0 }\n" + expected["version: 1\n".Length..]);
    }

    [Fact]
    public void Append_skips_tables_already_in_the_file()
    {
        var once = ArchiveMapWriter.Append(null, ["quote"], Catalog(), Candidates);
        ArchiveMapWriter.Append(once, ["quote"], Catalog(), Candidates).Should().Be(once);
        var twice = ArchiveMapWriter.Append(once, ["quote", "invoice", "invoice"], Catalog(), Candidates);
        Valid(twice).Tables.Select(t => t.Source).Should().Equal("quote", "invoice");

        // A table the map already maps elsewhere is not archived too.
        var mapped = "version: 1\ntables:\n  - { source: quote, target: xrmv_offer }\n";
        ArchiveMapWriter.Append(mapped, ["quote"], Catalog(), Candidates).Should().Be(mapped);
    }

    [Fact]
    public void Append_refuses_a_table_that_is_not_a_candidate()
    {
        var act = () => ArchiveMapWriter.Append(null, ["quote", "account"], Catalog(), Candidates);
        act.Should().Throw<MapValidationException>().Which.Problems.Should().ContainSingle()
            .Which.Should().Contain("'account' is not an archive candidate");
    }

    [Fact]
    public void Inline_tables_list_is_refused_and_a_bad_map_is_reported()
    {
        ((Action)(() => ArchiveMapWriter.Append("version: 1\ntables: []\n", ["quote"], Catalog(), Candidates)))
            .Should().Throw<MapFormatException>().WithMessage("line 2: 'tables' is written inline*");
        ((Action)(() => ArchiveMapWriter.Append("version: 2\n", ["quote"], Catalog(), Candidates))).Should().Throw<MapFormatException>();
    }
}
