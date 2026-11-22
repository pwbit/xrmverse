using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Xrmd.Host.Auth;

namespace Xrmd.Integration.Tests;

public class JwtSetupTests
{
    [Fact]
    public void Entra_mode_configures_authority_issuer_and_audiences()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mode"] = "Entra",
            ["TenantId"] = "tenant-1",
            ["Audience"] = "https://xrm.example.com/",
        }).Build();

        var o = new JwtBearerOptions();
        JwtSetup.Configure(o, cfg);

        const string authority = "https://login.microsoftonline.com/tenant-1/v2.0";
        o.Authority.Should().Be(authority);
        o.TokenValidationParameters.ValidIssuer.Should().Be(authority);
        o.TokenValidationParameters.ValidAudiences.Should()
            .BeEquivalentTo("https://xrm.example.com", "https://xrm.example.com/");
        o.MapInboundClaims.Should().BeFalse();
        o.IncludeErrorDetails.Should().BeFalse();
    }

    [Fact]
    public void Local_issuer_and_authorize_uri_derive_from_the_audience()
    {
        JwtSetup.LocalIssuer("https://xrm.example.com/").Should().Be("https://xrm.example.com/adfs");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mode"] = "Local",
            ["Audience"] = "https://xrm.example.com/",
        }).Build();
        JwtSetup.AuthorizeUri(cfg).Should().Be("https://xrm.example.com/adfs/oauth2/authorize");
    }

    [Fact]
    public void Local_audience_without_a_trailing_slash_is_normalized_to_exactly_one()
    {
        foreach (var configured in new[] { "https://h", "https://h/", "https://h//" })
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mode"] = "Local",
                ["Audience"] = configured,
            }).Build();
            JwtSetup.LocalAudience(cfg).Should().Be("https://h/", configured);
            JwtSetup.LocalIssuer(JwtSetup.LocalAudience(cfg)).Should().Be("https://h/adfs");
        }
    }

    [Fact]
    public void Entra_mode_with_a_tenant_but_no_audience_still_configures_and_challenges_without_a_resource()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mode"] = "Entra",
            ["TenantId"] = "tenant-1",
        }).Build();

        var o = new JwtBearerOptions();
        var act = () => JwtSetup.Configure(o, cfg);
        act.Should().NotThrow();
        o.Events.OnChallenge.Should().NotBeNull();
    }
}
