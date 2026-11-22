using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.PowerPlatform.Dataverse.Client.Extensions;
using OpenIddict.Abstractions;
using System.Net;
using System.Text.Json;
using Xrmd.Host.Idp;
using Xrmd.Integration.Tests.Idp;
using Xrmd.Security;
using Xunit.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.8 acceptance: the real Dataverse <see cref="ServiceClient"/> against xrmd in Local mode, with no Entra involved.
/// </summary>
/// <remarks>Binds Kestrel to 127.0.0.1:443 and adds a test certificate to the trust store; skip with <c>--filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient-local")]
[Trait("Category", "Acceptance")]
public sealed class LocalIdpServiceClientTests(LocalIdpServiceClientFixture fx, ITestOutputHelper output)
{
    private static readonly string[] EntraHosts = ["login.microsoftonline.com", "login.microsoft.com", "login.windows.net"];

    [Fact]
    public async Task ServiceClient_client_secret_works_without_entra()
    {
        var (userId, _) = await CreateUserAsync("svc-acceptance");
        var (clientId, secret) = await CreateClientAsync(userId);
        using var calls = new HttpCallRecorder();

        using var client = new ServiceClient(
            $"AuthType=ClientSecret;Url={LocalIdpServiceClientFixture.BaseUrl};ClientId={clientId};ClientSecret={secret};RequireNewInstance=true",
            new TestOutputLogger(output));

        client.IsReady.Should().BeTrue(client.LastError);
        client.GetMyUserId().Should().Be(userId, "WhoAmI returns the systemuser bound to the client");
        calls.Hosts.Should().NotBeEmpty().And.Contain(LocalIdpServiceClientFixture.Host);
        calls.Hosts.Should().NotIntersectWith(EntraHosts, "no connection to Entra was attempted");
    }

    [Fact]
    public async Task ServiceClient_token_provider_works()
    {
        const string password = "correct horse battery";
        var (userId, _) = await CreateUserAsync("kim-acceptance", password);
        var msal = PublicClientApplicationBuilder.Create(IdpClients.Cli)
            .WithAuthority($"https://{LocalIdpServiceClientFixture.Host}/adfs/", validateAuthority: false)
            .Build();
        var scope = LocalIdpServiceClientFixture.BaseUrl + ".default";
        var result = await msal.AcquireTokenWithDeviceCode([scope], device => IdpBrowser.ApproveDeviceAsync(
            () => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = new Uri(LocalIdpServiceClientFixture.BaseUrl) },
            device, "kim-acceptance", password)).ExecuteAsync();

        using var client = new ServiceClient(new Uri(LocalIdpServiceClientFixture.BaseUrl), _ => Task.FromResult(result.AccessToken),
            useUniqueInstance: true, new TestOutputLogger(output));

        client.IsReady.Should().BeTrue(client.LastError);
        client.GetMyUserId().Should().Be(userId);
    }

    private async Task<(Guid UserId, Guid ObjectId)> CreateUserAsync(string userName, string? password = null)
    {
        var oid = Guid.NewGuid();
        await using var scope = fx.App.Services.CreateAsyncScope();
        var userId = await scope.ServiceProvider.GetRequiredService<SecurityBootstrap>().EnsureSystemAdministratorAsync(oid, userName, default);
        if (password is not null) await scope.ServiceProvider.GetRequiredService<CredentialService>().SetPasswordAsync(userId, password, default);
        return (userId, oid);
    }

    private async Task<(string ClientId, string Secret)> CreateClientAsync(Guid userId)
    {
        var clientId = "svc-" + Guid.NewGuid().ToString("N")[..8];
        var secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using var scope = fx.App.Services.CreateAsyncScope();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId, ClientSecret = secret, ClientType = ClientTypes.Confidential,
            Permissions = { Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials },
        };
        descriptor.Properties[IdpClients.SystemUserIdProperty] = JsonSerializer.SerializeToElement(userId.ToString());
        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(descriptor);
        return (clientId, secret);
    }

    /// <summary>The hosts of every outgoing HttpClient request of the process (the <c>HttpHandlerDiagnosticListener</c>).</summary>
    private sealed class HttpCallRecorder : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly List<string> _hosts = [];
        private readonly List<IDisposable> _subscriptions = [];

        public HttpCallRecorder() => _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

        public IReadOnlySet<string> Hosts { get { lock (_hosts) return _hosts.ToHashSet(StringComparer.OrdinalIgnoreCase); } }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener") _subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (e.Key != "System.Net.Http.HttpRequestOut.Start" || e.Value is null) return;
            if (e.Value.GetType().GetProperty("Request")?.GetValue(e.Value) is HttpRequestMessage { RequestUri: { } uri })
                lock (_hosts) _hosts.Add(uri.Host);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { foreach (var s in _subscriptions) s.Dispose(); }
    }
}
