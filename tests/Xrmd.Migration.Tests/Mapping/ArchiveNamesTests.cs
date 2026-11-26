using FluentAssertions;
using Xrmd.Migration.Mapping;

namespace Xrmd.Migration.Tests.Mapping;

public sealed class ArchiveNamesTests
{
    [Fact]
    public void Short_names_get_the_prefix()
    {
        ArchiveNames.Table("quote").Should().Be("xrmva_quote");
        ArchiveNames.Column("totalamount").Should().Be("xrmva_totalamount");
        ArchiveNames.LookupText("opportunityid").Should().Be("xrmva_opportunityidname");
        ArchiveNames.Relationship("quote", "customerid").Should().Be("xrmva_quote_customerid");
    }

    [Fact]
    public void Long_names_are_shortened_with_a_stable_hash_and_leave_room_for_server_suffixes()
    {
        var source = "a" + new string('b', 70);
        var table = ArchiveNames.Table(source);
        var column = ArchiveNames.Column(source);

        (table.Length + "id".Length).Should().BeLessThanOrEqualTo(ArchiveNames.MaxLength, "the primary key is the table name and 'id'");
        (column.Length + "_base".Length).Should().BeLessThanOrEqualTo(ArchiveNames.MaxLength, "a money column has a '_base' companion");
        table.Should().StartWith("xrmva_ab").And.MatchRegex("^[a-z][a-z0-9_]*_[0-9a-f]{8}$");
        ArchiveNames.Table(source).Should().Be(table, "the same name every time");
        ArchiveNames.Table(source + "c").Should().NotBe(table, "names that share their start stay apart");
        ArchiveNames.LookupText(source).Should().NotBe(column);
    }

    [Fact]
    public void Name_at_the_limit_is_kept_whole()
    {
        var source = new string('q', ArchiveNames.MaxLength - "xrmva_".Length - "id".Length);
        ArchiveNames.Table(source).Should().Be("xrmva_" + source);
        ArchiveNames.Table(source + "q").Should().NotBe("xrmva_" + source + "q");
    }
}
