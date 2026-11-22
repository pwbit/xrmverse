using FluentAssertions;
using Xrmd.Core.Query;

namespace Xrmd.Core.Tests;

/// <summary>Learn ("Count rows using FetchXml"): the total stops at 5,000 for standard tables and 500 for elastic tables.</summary>
public sealed class TotalRecordCountTests
{
    [Theory]
    [InlineData(12, false, 12, false)]
    [InlineData(5000, false, 5000, false)]
    [InlineData(5001, false, 5000, true)]
    [InlineData(500, true, 500, false)]
    [InlineData(501, true, 500, true)]
    public void Total_is_capped_and_flagged(int total, bool elastic, int expected, bool exceeded)
    {
        TotalRecordCount.Cap(total, elastic).Should().Be((expected, exceeded));
    }
}
