using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xrmd.Abstractions.Sdk;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests.SdkFree;

/// <summary>Plan S.2a, Task 3: health and doctor report the SDK mode, and touch no SDK type in mode <c>none</c>.</summary>
[Collection("xrmd")]
public sealed class SdkFreeAdminTests(XrmdFixture fx)
{
    [Fact]
    public async Task Health_in_mode_none_reports_the_mode_and_no_sdk_version()
    {
        await using var app = NewSdkFreeApp();
        var health = await GetAsync(app, "/admin/health");

        health["sdkMode"]!.GetValue<string>().Should().Be("none");
        health.ContainsKey("sdkVersion").Should().BeTrue("the field stays, null");
        health["sdkVersion"].Should().BeNull();
    }

    [Fact]
    public async Task Doctor_in_mode_none_reports_the_sdk_checks_as_not_used()
    {
        await using var app = NewSdkFreeApp();
        var doctor = await GetAsync(app, "/admin/doctor");

        foreach (var name in new[] { "sdk", "sdk-workflow" })
        {
            var check = doctor["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;
            check["status"]!.GetValue<string>().Should().Be("ok");
            check["detail"]!.GetValue<string>().Should().Be(SdkFreeMessages.NotUsed);
        }
    }

    [Fact]
    public async Task Health_in_the_default_mode_reports_standard_and_the_sdk_version()
    {
        var res = await fx.Client.SendAsync(Authorized(fx.Client.BaseAddress!, "/admin/health"));
        var health = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();

        health["sdkMode"]!.GetValue<string>().Should().Be("standard");
        health["sdkVersion"]!.GetValue<string>().Should().Be(SdkPackage.XrmSdkFileVersion);
    }

    private WebApplicationFactory<Program> NewSdkFreeApp() => fx.WithOwnDatabase(b => b.UseSetting("Sdk:Mode", "none"));

    private async Task<JsonObject> GetAsync(WebApplicationFactory<Program> app, string path)
    {
        using var client = app.CreateClient();
        var res = await client.SendAsync(Authorized(client.BaseAddress!, path));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
    }

    private HttpRequestMessage Authorized(Uri baseUrl, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, baseUrl.ToString()));
        return req;
    }
}
