using FluentAssertions;
using Xrmd.Cli.Commands;

namespace Xrmd.Cli.Tests;

public sealed class CsvTests
{
    private static List<string[]> Fields(string text) => Csv.Parse(text).Select(r => r.Fields.ToArray()).ToList();

    [Fact]
    public void Leading_BOM_is_skipped()
    {
        Fields("\uFEFFa,b\r\n1,2\r\n").Should().BeEquivalentTo(new[] { new[] { "a", "b" }, new[] { "1", "2" } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Line_breaks_are_CRLF_LF_or_a_bare_CR()
    {
        Csv.Parse("a\rb\nc\r\nd").Select(r => (r.Line, r.Fields[0])).Should().Equal((1, "a"), (2, "b"), (3, "c"), (4, "d"));
    }

    [Fact]
    public void Quoted_fields_keep_commas_quotes_and_line_breaks()
    {
        var records = Csv.Parse("x,y\r\n\"a\r\nb\",\"say \"\"hi\"\", ok\"\r\nlast,1");
        records[1].Fields.Should().Equal("a\r\nb", "say \"hi\", ok");
        records[1].Line.Should().Be(2);
        records[2].Line.Should().Be(4); // the quoted line break counts as a physical line
    }

    [Fact]
    public void Unterminated_quote_is_a_format_error()
    {
        var act = () => Csv.Parse("a,b\r\n\"open,1\r\n");
        act.Should().Throw<FormatException>().WithMessage("*unterminated*");
    }

    [Theory]
    [InlineData("\"a\"x,b")]
    [InlineData("ab\"c,d")]
    public void Junk_around_quotes_is_a_format_error(string text)
    {
        var act = () => Csv.Parse(text);
        act.Should().Throw<FormatException>().WithMessage("line 1:*");
    }

    [Fact]
    public void Blank_lines_are_marked_with_their_physical_line_and_trailing_ones_dropped()
    {
        var records = Csv.Parse("a,b\r\n\r\n1,2\r\n\r\n\r\n");
        records.Select(r => (r.Line, r.IsBlankLine)).Should().Equal((1, false), (2, true), (3, false));
    }

    [Fact]
    public void A_single_empty_cell_is_a_value_and_a_blank_line_is_told_apart()
    {
        var records = Csv.Parse("a\r\n\"\"\r\n\r\nz");
        records.Select(r => (r.Line, r.IsBlankLine, r.Fields[0])).Should().Equal((1, false, "a"), (2, false, ""), (3, true, ""), (4, false, "z"));
    }
}
