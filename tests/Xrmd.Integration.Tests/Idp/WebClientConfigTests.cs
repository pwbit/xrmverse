using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static Xrmd.Integration.Tests.Idp.LocalIdpTestHost;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>Plan 2.8, Task 6: what /ui/config.json tells the web admin in Local mode.</summary>
[Collection("xrmd")]
public sealed class WebClientConfigTests(XrmdFixture fx)
{
    [Fact]
    public async Task Ui_config_in_local_mode_names_the_local_idp_and_the_web_client()
    {
        await using var app = Create(fx);
        var body = await app.CreateClient().GetStringAsync("/ui/config.json");
        var json = JsonDocument.Parse(body).RootElement;

        json.GetProperty("authMode").GetString().Should().Be("Local");
        json.GetProperty("audience").GetString().Should().Be(Audience);
        json.GetProperty("authority").GetString().Should().Be(Audience + "adfs");
        json.GetProperty("clientId").GetString().Should().Be("xrmd-web");
        json.GetProperty("scope").GetString().Should().Be(Audience + ".default");
        json.GetProperty("apiVersion").GetString().Should().Be("v9.2");
    }

    [Fact]
    public async Task Audience_configured_without_a_trailing_slash_gives_slash_terminated_scope_and_redirect()
    {
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Auth:Mode", "Local");
            b.UseSetting("Auth:Audience", "https://" + LocalIdpTestHost.Host);
        });
        var json = JsonDocument.Parse(await app.CreateClient().GetStringAsync("/ui/config.json")).RootElement;
        json.GetProperty("audience").GetString().Should().Be(Audience);
        json.GetProperty("authority").GetString().Should().Be(Audience + "adfs");
        json.GetProperty("scope").GetString().Should().Be(Audience + ".default");

        await using var scope = app.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
        var web = await manager.FindByClientIdAsync("xrmd-web");
        (await manager.GetRedirectUrisAsync(web!)).Should().Equal(Audience);
    }
}
