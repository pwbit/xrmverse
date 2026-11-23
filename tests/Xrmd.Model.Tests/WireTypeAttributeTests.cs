using FluentAssertions;
using Xrmd.Model;

namespace Xrmd.Model.Tests;

public sealed class WireTypeAttributeTests
{
    [WireType("Sample")]
    private sealed class Annotated;

    [Fact]
    public void The_attribute_carries_the_wire_name()
    {
        typeof(Annotated).GetCustomAttributes(typeof(WireTypeAttribute), false)
            .Cast<WireTypeAttribute>().Single().Name.Should().Be("Sample");
    }
}
