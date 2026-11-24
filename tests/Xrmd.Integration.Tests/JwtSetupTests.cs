using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
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

    private const string DevKey = DevTokenFactory.SigningKey; // allow-listed test vector (.gitleaks.toml)

    private static JwtBearerOptions DevOptions(string audience)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mode"] = "Development",
            ["DevSigningKey"] = DevKey,
            ["Audience"] = audience,
        }).Build();
        var o = new JwtBearerOptions();
        JwtSetup.Configure(o, cfg);
        return o;
    }

    private static bool Validates(JwtBearerOptions o, string audience)
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            JwtSetup.DevIssuer, audience, [new Claim("sub", "u")], expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DevKey)), SecurityAlgorithms.HmacSha256)));
        var handler = new JsonWebTokenHandler();
        return handler.ValidateTokenAsync(token, o.TokenValidationParameters).GetAwaiter().GetResult().IsValid;
    }

    [Theory]
    [InlineData("https://xrm.example.com/; http://xrmd:5000/")]
    [InlineData("https://xrm.example.com , http://xrmd:5000")]
    public void Several_audiences_each_validate_and_a_third_does_not(string configured)
    {
        var o = DevOptions(configured);
        Validates(o, "https://xrm.example.com/").Should().BeTrue();
        Validates(o, "https://xrm.example.com").Should().BeTrue();
        Validates(o, "http://xrmd:5000/").Should().BeTrue();
        Validates(o, "http://xrmd:5000").Should().BeTrue();
        Validates(o, "https://other.example.com/").Should().BeFalse();
        Validates(o, "http://xrmd").Should().BeFalse();
        Validates(o, "*").Should().BeFalse();
    }

    [Fact]
    public void One_audience_and_empty_entries_behave_as_a_single_value()
    {
        var o = DevOptions(";https://xrm.example.com/;, ;");
        o.TokenValidationParameters.ValidAudiences.Should().BeEquivalentTo("https://xrm.example.com", "https://xrm.example.com/");
    }

    [Fact]
    public void Local_mode_uses_the_first_audience_and_refuses_an_http_value_in_the_list()
    {
        IConfiguration Cfg(string a) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mode"] = "Local",
            ["Audience"] = a,
        }).Build();

        JwtSetup.LocalAudience(Cfg("https://a.example.com; https://b/")).Should().Be("https://a.example.com/");
        var act = () => JwtSetup.LocalAudience(Cfg("https://a.example.com;http://xrmd:5000/"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:Audience must be an https URL when Auth:Mode is Local.*");
        JwtSetup.AuthorizeUri(Cfg("https://a.example.com;https://b/")).Should().Be("https://a.example.com/adfs/oauth2/authorize");
    }
}
