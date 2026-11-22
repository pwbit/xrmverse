using FluentAssertions;
using Xrmd.Query.FetchXml;

namespace Xrmd.Query.Tests;

public class PagingCookieTests
{
    [Fact]
    public void Format_then_TryParse_roundtrips()
    {
        var c = new PagingCookie(3, "pb_projectid", Guid.NewGuid(), Guid.NewGuid());
        var s = c.Format();
        s.Should().StartWith("<cookie page=\"3\"><pb_projectid last=\"{").And.Contain("first=\"{");
        PagingCookie.TryParse(s).Should().Be(c);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<cookie page=\"x\"/>")]
    [InlineData("<cookie page=\"1\"><accountid last=\"zz\" first=\"zz\"/></cookie>")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_returns_null_for(string? s) => PagingCookie.TryParse(s).Should().BeNull();

    [Fact]
    public void TryParse_accepts_guids_with_and_without_braces()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var c = PagingCookie.TryParse($"<cookie page=\"2\"><accountid last=\"{a}\" first=\"{{{b}}}\"/></cookie>");
        c.Should().Be(new PagingCookie(2, "accountid", b, a));
    }
}
