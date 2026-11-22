using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Auth;
using Xrmd.Host.Idp;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>Plan 2.8, Task 2: <c>Auth:Mode=Local</c> token validation and the Dataverse-shaped 401 challenge (L13).</summary>
[Collection("xrmd")]
public sealed class ChallengeTests(XrmdFixture fx)
{
    private const string Audience = "https://xrm.local.test/";
    private const string WhoAmI = "/api/data/v9.2/WhoAmI";

    private WebApplicationFactory<Program> LocalHost() => fx.WithOwnDatabase(b =>
    {
        b.UseSetting("Auth:Mode", "Local");
        b.UseSetting("Auth:Audience", Audience);
    });

    private static string Token(X509Certificate2 cert, Guid oid, string audience = Audience)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(JwtSetup.LocalIssuer(audience), audience,
            [new Claim("oid", oid.ToString()), new Claim("name", "Test Admin")], now, now.AddHours(1),
            new SigningCredentials(new X509SecurityKey(cert), SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(req);
    }

    [Fact]
    public async Task Local_mode_requires_an_https_audience()
    {
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Auth:Mode", "Local");
            b.UseSetting("Auth:Audience", "http://x/");
        });

        var act = () => app.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:Audience must be an https URL when Auth:Mode is Local.*");
    }

    [Fact]
    public async Task Local_mode_without_an_org_connection_string_fails_at_start()
    {
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Auth:Mode", "Local");
            b.UseSetting("Auth:Audience", Audience);
            b.UseSetting("ConnectionStrings:Org", "");
        });

        var act = () => app.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings:Org is required when Auth:Mode is Local*");
    }

    [Fact]
    public async Task Local_mode_fails_at_start_when_the_signing_key_cannot_be_loaded()
    {
        // Migrated (so the host's bootstrap passes and does not recreate it) but without the key table.
        await using var db = await IdpDatabase.CreateAsync(fx.ConnectionString);
        await using (var conn = await db.Factory.OpenAsync())
        await using (var drop = TestSql.Command(conn, "DROP TABLE auth.signing_key"))
            await drop.ExecuteNonQueryAsync();
        await using var app = fx.App.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Org", db.ConnectionString);
            b.UseSetting("Auth:Mode", "Local");
            b.UseSetting("Auth:Audience", Audience);
        });

        var act = () => app.CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("signing_key");
    }

    [Fact]
    public async Task Local_mode_accepts_a_token_signed_by_the_org_key()
    {
        await using var app = LocalHost();
        var client = app.CreateClient();
        var (signing, _) = await app.Services.GetRequiredService<SigningKeyStore>().GetOrCreateAsync(default);

        var res = await GetAsync(client, WhoAmI, Token(signing, fx.AdminObjectId));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_of_another_org_is_refused()
    {
        await using var app = LocalHost();
        var client = app.CreateClient();
        await using var other = await IdpDatabase.CreateAsync(fx.ConnectionString);
        var (foreign, _) = await new SigningKeyStore(other.Factory).GetOrCreateAsync(default);

        var res = await GetAsync(client, WhoAmI, Token(foreign, fx.AdminObjectId));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(WhoAmI)]
    [InlineData("/XRMServices/2011/Organization.svc/web")]
    public async Task Challenge_names_the_local_authorize_endpoint(string path)
    {
        await using var app = LocalHost();
        var client = app.CreateClient();

        var res = await GetAsync(client, path);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        res.Headers.WwwAuthenticate.ToString().Should()
            .Be("Bearer authorization_uri=https://xrm.local.test/adfs/oauth2/authorize, resource_id=https://xrm.local.test/");
        (await res.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Challenge_in_entra_mode_names_login_microsoftonline()
    {
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Auth:Mode", "Entra");
            b.UseSetting("Auth:TenantId", "t1");
            b.UseSetting("Auth:Audience", Audience);
        });
        var client = app.CreateClient();

        var res = await GetAsync(client, WhoAmI);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        res.Headers.WwwAuthenticate.ToString().Should()
            .Be("Bearer authorization_uri=https://login.microsoftonline.com/t1/oauth2/authorize, resource_id=https://xrm.local.test/");
    }

    [Fact]
    public async Task Other_paths_keep_the_plain_challenge()
    {
        await using var app = LocalHost();
        var client = app.CreateClient();

        var res = await GetAsync(client, "/mcp");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        res.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
    }
}
