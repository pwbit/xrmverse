using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Xrmd.Core.Errors;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>#131, ruling Z5 (needs a database): LocalTimeFromUtcTime on the Web API and both time zone messages on the SDK.</summary>
[Collection("xrmd")]
public sealed class TimeZoneFunctionTests(XrmdFixture fx)
{
    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Web_api_returns_the_local_time_with_z()
    {
        // Observed on a trial environment (export 3d).
        var (status, body) = await GetAsync("LocalTimeFromUtcTime(TimeZoneCode=110,UtcTime=2026-03-29T01:30:00Z)");
        status.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!;
        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.LocalTimeFromUtcTimeResponse");
        json["LocalTime"]!.GetValue<string>().Should().Be("2026-03-29T03:30:00Z");

        (status, body) = await GetAsync("LocalTimeFromUtcTime(TimeZoneCode=@c,UtcTime=@t)?@c=92&@t=2026-03-29T01:30:00Z");
        status.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!["LocalTime"]!.GetValue<string>().Should().Be("2026-03-29T01:30:00Z");
    }

    [Fact]
    public async Task Web_api_refuses_an_unknown_code()
    {
        var (status, body) = await GetAsync("LocalTimeFromUtcTime(TimeZoneCode=9999,UtcTime=2026-03-29T01:30:00Z)");
        status.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Contain("9999");
    }

    [Fact]
    public async Task Sdk_converts_both_ways()
    {
        var service = ServiceFor(fx, fx.AdminObjectId);
        var local = (LocalTimeFromUtcTimeResponse)await service.ExecuteAsync(new LocalTimeFromUtcTimeRequest
        {
            TimeZoneCode = 110, UtcTime = new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc),
        });
        local.LocalTime.Should().Be(new DateTime(2026, 3, 29, 3, 30, 0));

        var utc = (UtcTimeFromLocalTimeResponse)await service.ExecuteAsync(new UtcTimeFromLocalTimeRequest
        {
            TimeZoneCode = 110, LocalTime = new DateTime(2026, 7, 1, 12, 0, 0),
        });
        utc.UtcTime.Should().Be(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        utc.UtcTime.Kind.Should().Be(DateTimeKind.Utc);

        await FluentActions.Awaiting(() => service.ExecuteAsync(new UtcTimeFromLocalTimeRequest { TimeZoneCode = 9999, LocalTime = DateTime.Now }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.HttpStatus == 400);
    }
}
