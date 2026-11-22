using FluentAssertions;
using Xrmd.Pipeline;

namespace Xrmd.Pipeline.Tests;

public class TracingServiceTests
{
    [Fact]
    public void Trace_formats_invariant()
    {
        var t = new TracingService();
        t.Trace("{0:N2}", 1234.5);
        t.Text.Should().Be("1,234.50");
    }

    [Fact]
    public void Trace_without_args_keeps_braces()
    {
        var t = new TracingService();
        t.Trace("{x}");
        t.Text.Should().Be("{x}");
    }

    [Fact]
    public void Trace_with_bad_format_writes_literal()
    {
        var t = new TracingService();
        t.Trace("{0", 1);
        t.Text.Should().Be("{0");
    }

    [Fact]
    public void Text_is_truncated_at_10240()
    {
        var t = new TracingService();
        for (var i = 0; i < 2000; i++) t.Trace("line number {0}", i);
        t.Text.Length.Should().Be(TracingService.MaxLength);
    }

    [Fact]
    public void Lines_are_separated_by_newline()
    {
        var t = new TracingService();
        t.Trace("a"); t.Trace("b");
        t.Text.Should().Be("a\nb");
    }
}
