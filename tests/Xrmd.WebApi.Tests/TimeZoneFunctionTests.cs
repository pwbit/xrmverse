using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi.Tests;

/// <summary>#131, ruling Z5: the parameters of <c>GET LocalTimeFromUtcTime(TimeZoneCode=…,UtcTime=…)</c>, inline or as aliases.</summary>
public class TimeZoneFunctionTests
{
    private static HttpRequest Request(string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context.Request;
    }

    [Fact]
    public void Inline_parameters()
    {
        var (code, utc) = FunctionController.ParseLocalTimeParameters(Request(), "TimeZoneCode=110,UtcTime=2026-03-29T01:30:00Z");
        code.Should().Be(110);
        utc.Should().Be(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc));
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Aliases_in_any_order_and_offsets()
    {
        var (code, utc) = FunctionController.ParseLocalTimeParameters(Request("?@t=2026-03-29T03:30:00%2B02:00&@c=110"), " UtcTime=@t , TimeZoneCode=@c ");
        (code, utc).Should().Be((110, new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("TimeZoneCode=110")]
    [InlineData("UtcTime=2026-03-29T01:30:00Z")]
    [InlineData("TimeZoneCode=x,UtcTime=2026-03-29T01:30:00Z")]
    [InlineData("TimeZoneCode=110,UtcTime=yesterday")]
    [InlineData("TimeZoneCode=110,UtcTime=2026-03-29T01:30:00Z,Other=1")]
    [InlineData("TimeZoneCode=@c,UtcTime=2026-03-29T01:30:00Z")]
    public void Missing_or_invalid_parameters_are_400(string parameters)
    {
        FluentActions.Invoking(() => FunctionController.ParseLocalTimeParameters(Request(), parameters))
            .Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void Local_time_is_written_with_a_z_suffix()
    {
        // Observed on a trial environment (export 3d): the local time carries "Z" although it is not UTC.
        FunctionController.LocalTimeText(new DateTime(2026, 3, 29, 3, 30, 0)).Should().Be("2026-03-29T03:30:00Z");
    }
}
