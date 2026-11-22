using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using OpenIddict.Abstractions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Idp;
using Xrmd.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>
/// Plan 2.8, Task 3: the OpenIddict server of <c>Auth:Mode=Local</c> with the ADFS-compatible layout (L1), WebFinger, the built-in
/// clients (L10) and the client-credentials grant, end to end with MSAL.NET as ServiceClient uses it.
/// </summary>
[Collection("xrmd")]
public sealed class ClientCredentialsTests(XrmdFixture fx)
{
    private const string Host = "xrm.local.test";
    private const string Audience = "https://" + Host + "/";
    private const string TrustedRealm = "http://schemas.microsoft.com/rel/trusted-realm";

    private WebApplicationFactory<Program> LocalHost() => fx.WithOwnDatabase(b =>
    {
        b.UseSetting("Auth:Mode", "Local");
        b.UseSetting("Auth:Audience", Audience);
    });

    private static HttpClient Client(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(Audience), AllowAutoRedirect = false });

    [Fact]
    public async Task Discovery_document_has_the_adfs_layout()
    {
        await using var app = LocalHost();
        var client = Client(app);

        var res = await client.GetAsync("/adfs/.well-known/openid-configuration");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("issuer").GetString().Should().Be("https://xrm.local.test/adfs");
        doc.GetProperty("authorization_endpoint").GetString().Should().Be("https://xrm.local.test/adfs/oauth2/authorize");
        doc.GetProperty("token_endpoint").GetString().Should().Be("https://xrm.local.test/adfs/oauth2/token");
        doc.GetProperty("device_authorization_endpoint").GetString().Should().Be("https://xrm.local.test/adfs/oauth2/devicecode");
        doc.GetProperty("end_session_endpoint").GetString().Should().Be("https://xrm.local.test/adfs/oauth2/logout");
        doc.GetProperty("jwks_uri").GetString().Should().Be("https://xrm.local.test/adfs/discovery/keys");
        Strings(doc, "grant_types_supported").Should().BeEquivalentTo(
            "authorization_code", "urn:ietf:params:oauth:grant-type:device_code", "client_credentials", "refresh_token");
        Strings(doc, "code_challenge_methods_supported").Should().Equal("S256");

        var keys = await client.GetAsync("/adfs/discovery/keys");
        keys.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebFinger_returns_the_trusted_realm()
    {
        await using var app = LocalHost();
        var client = Client(app);

        var res = await client.GetAsync($"/.well-known/webfinger?resource=https://{Host}&rel={TrustedRealm}");
        var other = await client.GetAsync($"/.well-known/webfinger?resource=https://other.test&rel={TrustedRealm}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var links = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("links");
        links.EnumerateArray().Should().ContainSingle(l =>
            l.GetProperty("rel").GetString() == TrustedRealm && l.GetProperty("href").GetString() == "https://" + Host);
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Msal_client_credentials_gets_a_token_and_calls_WhoAmI()
    {
        await using var app = LocalHost();
        var client = Client(app);
        var (userId, _) = await ApplicationUserAsync(app);
        var (clientId, secret) = await ConfidentialClientAsync(app, userId);
        var recorder = new RecordingHandler(app.Server.CreateHandler());

        var msal = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(secret)
            .WithAuthority($"https://{Host}/adfs/", validateAuthority: true)
            .WithHttpClientFactory(new TestHttpClientFactory(new HttpClient(recorder)))
            .Build();
        var result = await msal.AcquireTokenForClient([$"https://{Host}/.default"]).ExecuteAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/WhoAmI");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        var res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("UserId").GetGuid().Should().Be(userId);
        recorder.Seen.Should().NotBeEmpty().And.OnlyContain(u => u.Host == Host, "no Entra connectivity");
        recorder.Seen.Should().Contain(u => u.AbsolutePath == "/.well-known/webfinger", "MSAL validated the ADFS authority");
    }

    [Fact]
    public async Task Token_has_the_org_issuer_audience_and_user_claims()
    {
        await using var app = LocalHost();
        var client = Client(app);
        var (userId, oid) = await ApplicationUserAsync(app);
        var (clientId, secret) = await ConfidentialClientAsync(app, userId);

        var res = await RequestTokenAsync(client, clientId, secret, "https://xrm.local.test//.default");

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Issuer.Should().Be("https://xrm.local.test/adfs");
        jwt.Audiences.Should().Equal(Audience);
        jwt.Subject.Should().Be(userId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "oid" && c.Value == oid.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "name" && c.Value == "svc-app");
    }

    [Fact]
    public async Task Unknown_scope_gets_invalid_scope()
    {
        await using var app = LocalHost();
        var client = Client(app);
        var (userId, _) = await ApplicationUserAsync(app);
        var (clientId, secret) = await ConfidentialClientAsync(app, userId);

        var res = await RequestTokenAsync(client, clientId, secret, "https://other.test/.default");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be(Errors.InvalidScope);
    }

    [Fact]
    public async Task Client_bound_to_a_disabled_user_gets_invalid_grant()
    {
        await using var app = LocalHost();
        var client = Client(app);
        var (userId, _) = await ApplicationUserAsync(app);
        var (clientId, secret) = await ConfidentialClientAsync(app, userId);
        await using (var conn = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync())
        await using (var cmd = TestSql.Command(conn, "UPDATE data.systemuser SET isdisabled = 1 WHERE systemuserid = @id"))
        {
            cmd.AddParameter("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        var res = await RequestTokenAsync(client, clientId, secret, Audience + ".default");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be(Errors.InvalidGrant);
    }

    [Fact]
    public async Task Client_without_an_application_user_gets_unauthorized_client()
    {
        await using var app = LocalHost();
        var (clientId, secret) = await ConfidentialClientAsync(app, null);

        var res = await RequestTokenAsync(Client(app), clientId, secret, Audience + ".default");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be(Errors.UnauthorizedClient);
    }

    [Fact]
    public async Task Client_bound_to_a_missing_user_or_a_user_without_object_id_gets_invalid_grant()
    {
        await using var app = LocalHost();
        var (missing, missingSecret) = await ConfidentialClientAsync(app, Guid.NewGuid());
        var (userId, _) = await ApplicationUserAsync(app);
        var (noOid, noOidSecret) = await ConfidentialClientAsync(app, userId);
        await using (var conn = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync())
        await using (var cmd = TestSql.Command(conn, "UPDATE data.systemuser SET azureactivedirectoryobjectid = NULL WHERE systemuserid = @id"))
        {
            cmd.AddParameter("@id", userId);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var (clientId, secret) in new[] { (missing, missingSecret), (noOid, noOidSecret) })
        {
            var res = await RequestTokenAsync(Client(app), clientId, secret, Audience + ".default");
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be(Errors.InvalidGrant);
        }
    }

    [Fact]
    public async Task Built_in_clients_are_seeded_once()
    {
        await using var db = await IdpDatabase.CreateAsync(fx.ConnectionString);
        for (var start = 0; start < 2; start++)
        {
            await using var app = fx.App.WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:Org", db.ConnectionString);
                b.UseSetting("Auth:Mode", "Local");
                b.UseSetting("Auth:Audience", Audience);
            });
            app.CreateClient().Dispose();
        }

        await using var conn = await db.Factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT [ClientId], COUNT(*) FROM auth.application WHERE [ClientId] IN ('xrmd-cli', 'xrmd-web') GROUP BY [ClientId] ORDER BY [ClientId]");
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, int)>();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), Convert.ToInt32(rd.GetValue(1))));
        rows.Should().Equal((IdpClients.Cli, 1), (IdpClients.Web, 1));
    }

    private static Task<HttpResponseMessage> RequestTokenAsync(HttpClient client, string clientId, string secret, string scope) =>
        client.PostAsync("/adfs/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.ClientCredentials,
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope,
        }));

    /// <summary>An application user with the System Administrator role (Task 5 adds the Dataverse <c>applicationid</c> marker).</summary>
    private static async Task<(Guid UserId, Guid ObjectId)> ApplicationUserAsync(WebApplicationFactory<Program> app)
    {
        var oid = Guid.NewGuid();
        var userId = await app.Services.GetRequiredService<SecurityBootstrap>().EnsureSystemAdministratorAsync(oid, "svc-app", default);
        return (userId, oid);
    }

    private static async Task<(string ClientId, string Secret)> ConfidentialClientAsync(WebApplicationFactory<Program> app, Guid? userId)
    {
        var clientId = "svc-" + Guid.NewGuid().ToString("N")[..8];
        var secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using var scope = app.Services.CreateAsyncScope();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            Permissions = { Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials },
        };
        if (userId is { } bound)
            descriptor.Properties[IdpClients.SystemUserIdProperty] = JsonSerializer.SerializeToElement(bound.ToString());
        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(descriptor);
        return (clientId, secret);
    }

    private static IEnumerable<string?> Strings(JsonElement doc, string name) =>
        doc.GetProperty(name).EnumerateArray().Select(e => e.GetString());

    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly List<Uri> _seen = [];

        public IReadOnlyList<Uri> Seen { get { lock (_seen) return [.. _seen]; } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_seen) _seen.Add(request.RequestUri!);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => client;
    }
}
