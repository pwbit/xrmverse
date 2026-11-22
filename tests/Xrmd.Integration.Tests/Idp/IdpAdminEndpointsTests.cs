using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Xrmd.Host.Idp;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static Xrmd.Integration.Tests.Idp.LocalIdpTestHost;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>Plan 2.8, Task 5: <c>/admin/idp</c>, the Admin API of the local identity provider.</summary>
[Collection("xrmd")]
public sealed class IdpAdminEndpointsTests(XrmdFixture fx)
{
    /// <summary>A Local-mode host, a client for it, and an administrator and a plain user (no role) with a bearer client each.</summary>
    private sealed record Setup(WebApplicationFactory<Program> App, HttpClient Admin, HttpClient Plain, Guid AdminId, Guid PlainId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private async Task<Setup> StartAsync()
    {
        var app = Create(fx);
        var (adminId, _) = await SystemUserAsync(app, "idp-admin");
        var plainId = Guid.NewGuid();
        await ExecAsync(app, """
            INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid, isdisabled,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES (@id, 'plain', 'plain', NEWID(), (SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL), 0,
                (SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL), SYSUTCDATETIME(), @id, SYSUTCDATETIME(), @id, 0, 1)
            """, ("@id", plainId));
        return new Setup(app, await BearerAsync(app, adminId), await BearerAsync(app, plainId), adminId, plainId);
    }

    private static async Task<HttpClient> BearerAsync(WebApplicationFactory<Program> app, Guid userId)
    {
        var secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var clientId = "t-" + Guid.NewGuid().ToString("N")[..8];
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientSecret = secret,
                ClientType = ClientTypes.Confidential,
                Permissions = { Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials },
                Properties = { [IdpClients.SystemUserIdProperty] = JsonSerializer.SerializeToElement(userId.ToString()) },
            });
        var client = Plain(app);
        var res = await client.PostAsync("/adfs/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.ClientCredentials, ["client_id"] = clientId, ["client_secret"] = secret, ["scope"] = Audience + ".default",
        }));
        var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpClient Plain(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(Audience), AllowAutoRedirect = false });

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_user_without_the_administrator_role_gets_403_and_nothing_changes()
    {
        await using var s = await StartAsync();

        var res = await s.Plain.PutAsync($"/admin/idp/users/{s.PlainId}/password", Json(new { password = "a long enough password" }));
        var create = await s.Plain.PostAsync("/admin/idp/clients", Json(new { displayName = "x", type = "public", redirectUris = new[] { "http://localhost" } }));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Convert.ToInt32(await ScalarAsync(s.App, "SELECT COUNT(*) FROM auth.credential WHERE [SystemUserId] = @id", ("@id", s.PlainId))).Should().Be(0);
        (await Plain(s.App).PutAsync($"/admin/idp/users/{s.PlainId}/password", Json(new { password = "x" }))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Password_is_set_and_an_11_character_password_is_400_with_the_service_message()
    {
        await using var s = await StartAsync();

        var short11 = await s.Admin.PutAsync($"/admin/idp/users/{s.PlainId}/password", Json(new { password = "elevenchars" }));
        short11.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await short11.Content.ReadAsStringAsync()).Should().Contain("The password must have at least 12 characters.");

        var ok = await s.Admin.PutAsync($"/admin/idp/users/{s.PlainId}/password", Json(new { password = "twelve chars" }));
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var result = await WithCredentialsAsync(s.App, c => c.SignInAsync("plain", "twelve chars", null, default));
        result.Should().BeOfType<SignInResult.Succeeded>();
    }

    [Fact]
    public async Task Unknown_user_is_404_and_a_bad_id_is_400()
    {
        await using var s = await StartAsync();

        (await s.Admin.PutAsync($"/admin/idp/users/{Guid.NewGuid()}/password", Json(new { password = "twelve chars" }))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await s.Admin.PutAsync("/admin/idp/users/nope/password", Json(new { password = "twelve chars" }))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Totp_is_enabled_disabled_and_the_credential_removed()
    {
        await using var s = await StartAsync();
        await s.Admin.PutAsync($"/admin/idp/users/{s.PlainId}/password", Json(new { password = "twelve chars" }));

        var enable = await s.Admin.PostAsync($"/admin/idp/users/{s.PlainId}/totp", null);
        enable.StatusCode.Should().Be(HttpStatusCode.OK);
        (await enable.Content.ReadFromJsonAsync<JsonObject>())!["otpauthUri"]!.GetValue<string>().Should().StartWith("otpauth://totp/");
        Convert.ToInt32(await ScalarAsync(s.App, "SELECT COUNT(*) FROM auth.credential WHERE [SystemUserId] = @id AND [TotpKey] IS NOT NULL", ("@id", s.PlainId)))
            .Should().Be(1);

        (await s.Admin.DeleteAsync($"/admin/idp/users/{s.PlainId}/totp")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Convert.ToInt32(await ScalarAsync(s.App, "SELECT COUNT(*) FROM auth.credential WHERE [SystemUserId] = @id AND [TotpKey] IS NOT NULL", ("@id", s.PlainId)))
            .Should().Be(0);

        (await s.Admin.DeleteAsync($"/admin/idp/users/{s.PlainId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Convert.ToInt32(await ScalarAsync(s.App, "SELECT COUNT(*) FROM auth.credential WHERE [SystemUserId] = @id", ("@id", s.PlainId))).Should().Be(0);
    }

    [Fact]
    public async Task Totp_for_a_user_without_credential_is_409()
    {
        await using var s = await StartAsync();

        (await s.Admin.PostAsync($"/admin/idp/users/{s.PlainId}/totp", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Confidential_client_needs_a_user_that_exists()
    {
        await using var s = await StartAsync();

        var none = await s.Admin.PostAsync("/admin/idp/clients", Json(new { displayName = "svc", type = "confidential" }));
        var missing = await s.Admin.PostAsync("/admin/idp/clients", Json(new { displayName = "svc", type = "confidential", systemuserid = Guid.NewGuid() }));

        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await none.Content.ReadAsStringAsync()).Should().Contain("systemuserid");
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("not found");
    }

    [Fact]
    public async Task Confidential_client_secret_is_returned_once_and_gets_client_credentials_only()
    {
        await using var s = await StartAsync();

        var res = await s.Admin.PostAsync("/admin/idp/clients", Json(new { displayName = "svc", type = "confidential", systemuserid = s.PlainId }));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var created = (await res.Content.ReadFromJsonAsync<JsonObject>())!;
        var clientId = created["clientId"]!.GetValue<string>();
        var secret = created["clientSecret"]!.GetValue<string>();
        secret.Length.Should().BeGreaterThanOrEqualTo(32);

        var list = await s.Admin.GetStringAsync("/admin/idp/clients");
        list.Should().Contain(clientId).And.NotContain(secret).And.NotContain("clientSecret");
        var row = JsonNode.Parse(list)!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == clientId)!;
        row["type"]!.GetValue<string>().Should().Be("confidential");
        row["systemuserid"]!.GetValue<string>().Should().Be(s.PlainId.ToString());

        await using var scope = s.App.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync(clientId))!;
        var permissions = await manager.GetPermissionsAsync(app);
        permissions.Should().Contain(Permissions.GrantTypes.ClientCredentials).And.NotContain(Permissions.GrantTypes.RefreshToken)
            .And.NotContain(Permissions.GrantTypes.AuthorizationCode);

        // and it works: a token for the bound user
        var token = await Plain(s.App).PostAsync("/adfs/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.ClientCredentials, ["client_id"] = clientId, ["client_secret"] = secret, ["scope"] = Audience + ".default",
        }));
        token.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Public_client_gets_the_code_flow_with_pkce_for_its_redirect_uris()
    {
        await using var s = await StartAsync();

        var none = await s.Admin.PostAsync("/admin/idp/clients", Json(new { displayName = "spa", type = "public" }));
        var res = await s.Admin.PostAsync("/admin/idp/clients",
            Json(new { displayName = "spa", type = "public", redirectUris = new[] { "https://app.example/cb" } }));

        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = (await res.Content.ReadFromJsonAsync<JsonObject>())!;
        body.ContainsKey("clientSecret").Should().BeFalse();
        var clientId = body["clientId"]!.GetValue<string>();
        await using var scope = s.App.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync(clientId))!;
        (await manager.GetPermissionsAsync(app)).Should().Contain(Permissions.GrantTypes.AuthorizationCode)
            .And.Contain(Permissions.GrantTypes.RefreshToken).And.NotContain(Permissions.GrantTypes.ClientCredentials);
        (await manager.GetRequirementsAsync(app)).Should().Contain(Requirements.Features.ProofKeyForCodeExchange);
        (await manager.GetRedirectUrisAsync(app)).Should().Equal("https://app.example/cb");
    }

    [Fact]
    public async Task Clients_are_deleted_and_the_built_in_ones_are_400()
    {
        await using var s = await StartAsync();
        var created = (await (await s.Admin.PostAsync("/admin/idp/clients",
            Json(new { displayName = "spa", type = "public", redirectUris = new[] { "http://localhost" } }))).Content.ReadFromJsonAsync<JsonObject>())!;

        (await s.Admin.DeleteAsync("/admin/idp/clients/" + IdpClients.Cli)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await s.Admin.DeleteAsync("/admin/idp/clients/" + IdpClients.Web)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await s.Admin.DeleteAsync("/admin/idp/clients/" + created["clientId"]!.GetValue<string>())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await s.Admin.DeleteAsync("/admin/idp/clients/" + created["clientId"]!.GetValue<string>())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await s.Admin.GetStringAsync("/admin/idp/clients")).Should().Contain(IdpClients.Cli).And.NotContain(created["clientId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Outside_Local_mode_every_route_is_409()
    {
        var client = fx.Client;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        try
        {
            foreach (var res in new[]
            {
                await client.GetAsync("/admin/idp/clients"),
                await client.PutAsync($"/admin/idp/users/{Guid.NewGuid()}/password", Json(new { password = "twelve chars" })),
                await client.DeleteAsync("/admin/idp/clients/x"),
            })
            {
                res.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await res.Content.ReadAsStringAsync()).Should().Contain("The local identity provider is not enabled (Auth:Mode is not Local).");
            }
        }
        finally
        {
            client.DefaultRequestHeaders.Authorization = null;
        }
    }
}
