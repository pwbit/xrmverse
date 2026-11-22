using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xrmd.Host.Auth;

namespace Xrmd.Integration.Tests;

public class HealthTests
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        await using var app = new WebApplicationFactory<Program>();
        var res = await app.CreateClient().GetAsync("/healthz");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_server_path_requires_a_token()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        // Anything else outside the server paths belongs to the web shell and is anonymous (see UiHostingTests).
        foreach (var path in new[] { "/api/data/v9.2/accounts", "/admin/doctor" })
            (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("30.00:00:00")] // more than int.MaxValue milliseconds
    public void Webhook_timeout_out_of_range_fails_at_startup(string timeout)
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Pipeline:WebhookTimeout", timeout));

        var ex = FluentActions.Invoking(() => app.CreateClient()).Should().Throw<Exception>().Which;
        ex.ToString().Should().Contain("Pipeline:WebhookTimeout must be a positive time span of at most 24.20:31:23.647");
    }

    [Fact]
    public void Invalid_auth_configuration_fails_at_startup()
    {
        // The options the host validates at startup (ValidateOnStart), built from a service collection of the test's own:
        // no host, no server, nothing shared with the other tests.
        static JwtBearerOptions Options(string key)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Mode"] = "Development",
                ["Auth:DevSigningKey"] = key,
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddXrmdAuth();
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        }

        Options(new string('k', 32)).TokenValidationParameters.ValidIssuer.Should().Be(JwtSetup.DevIssuer, "a valid key builds the options");
        var ex = FluentActions.Invoking(() => Options("too-short")).Should().Throw<Exception>().Which;
        ex.ToString().Should().Contain("Auth:DevSigningKey", "the failure names the setting: {0}", ex.ToString());
    }
}
