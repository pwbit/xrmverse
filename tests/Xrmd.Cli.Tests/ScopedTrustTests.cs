using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Xrmd.Cli.Config;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Tests;

/// <summary>
/// Review Focus 7: the root of Caddy's internal CA is trusted only for the profile's host, never for another host, and
/// never instead of the name and validity checks.
/// </summary>
public class ScopedTrustTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>A client over <paramref name="handler"/> whose every connection goes to the loopback <paramref name="port"/>, whatever the URL's host.</summary>
    private static HttpClient Client(System.Net.Http.SocketsHttpHandler handler, int port)
    {
        handler.UseProxy = false; // straight to the test server, whatever HTTPS_PROXY says
        handler.ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static async Task<bool> SucceedsAsync(HttpClient client, string url)
    {
        try
        {
            using var response = await client.GetAsync(url);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Scoped_trust_accepts_the_root_only_for_its_host()
    {
        using var root = TestCertificates.CreateRoot(Now);
        // One certificate that names both hosts: only the trust scope tells them apart.
        using var leaf = TestCertificates.CreateLeaf(root, Now.AddDays(-1), Now.AddDays(30), "acme.xrm.test", "zenith.xrm.test");
        await using var server = new TlsTestServer(leaf);
        using var client = Client(ScopedTrust.CreateHandler("acme.xrm.test", root), server.Port);

        (await SucceedsAsync(client, $"https://acme.xrm.test:{server.Port}/")).Should().BeTrue("the root is trusted for the profile's host");
        (await SucceedsAsync(client, $"https://ACME.xrm.test:{server.Port}/")).Should().BeTrue("host names compare without case");
        (await SucceedsAsync(client, $"https://zenith.xrm.test:{server.Port}/")).Should().BeFalse("another host keeps the default validation");
    }

    [Fact]
    public async Task Scoped_trust_rejects_the_root_for_another_host()
    {
        using var root = TestCertificates.CreateRoot(Now);
        using var leaf = TestCertificates.CreateLeaf(root, Now.AddDays(-1), Now.AddDays(30), "zenith.xrm.test");
        await using var server = new TlsTestServer(leaf);

        using (var scoped = Client(ScopedTrust.CreateHandler("acme.xrm.test", root), server.Port))
        {
            (await SucceedsAsync(scoped, $"https://zenith.xrm.test:{server.Port}/")).Should().BeFalse("the root is not trusted for zenith");
            (await SucceedsAsync(scoped, $"https://acme.xrm.test:{server.Port}/")).Should().BeFalse("a leaf that does not name acme is refused for acme");
        }
        using (var plain = Client(new System.Net.Http.SocketsHttpHandler(), server.Port))
            (await SucceedsAsync(plain, $"https://zenith.xrm.test:{server.Port}/")).Should().BeFalse("nothing is trusted globally");
        using (var zenith = Client(ScopedTrust.CreateHandler("zenith.xrm.test", root), server.Port))
            (await SucceedsAsync(zenith, $"https://zenith.xrm.test:{server.Port}/")).Should().BeTrue("the same leaf passes for its own scope");
    }

    [Fact]
    public async Task Scoped_trust_accepts_a_chain_through_the_served_intermediate_for_its_host_only()
    {
        // Caddy's local CA: root -> intermediate -> leaf; the server sends the leaf and the intermediate, never the root.
        using var root = TestCertificates.CreateRoot(Now);
        using var intermediate = TestCertificates.CreateIntermediate(root, Now.AddDays(-7), Now.AddDays(7));
        using var leaf = TestCertificates.CreateLeaf(intermediate, Now.AddHours(-1), Now.AddHours(12), "acme.xrm.test", "zenith.xrm.test");
        await using var server = new TlsTestServer(leaf, "{}", intermediate);
        using var client = Client(ScopedTrust.CreateHandler("acme.xrm.test", root), server.Port);

        (await SucceedsAsync(client, $"https://acme.xrm.test:{server.Port}/")).Should().BeTrue();
        (await SucceedsAsync(client, $"https://zenith.xrm.test:{server.Port}/")).Should().BeFalse("another host keeps the default validation");
    }

    [Fact]
    public async Task Scoped_trust_rejects_an_expired_intermediate()
    {
        using var root = TestCertificates.CreateRoot(Now);
        using var intermediate = TestCertificates.CreateIntermediate(root, Now.AddDays(-30), Now.AddDays(-1));
        using var leaf = TestCertificates.CreateLeaf(intermediate, Now.AddDays(-1), Now.AddDays(30), "acme.xrm.test"); // in date itself
        await using var server = new TlsTestServer(leaf, "{}", intermediate);
        using var client = Client(ScopedTrust.CreateHandler("acme.xrm.test", root), server.Port);

        (await SucceedsAsync(client, $"https://acme.xrm.test:{server.Port}/")).Should().BeFalse();
    }

    [Fact]
    public void Scoped_trust_requires_the_server_authentication_usage()
    {
        using var root = TestCertificates.CreateRoot(Now);
        using var leaf = TestCertificates.CreateLeaf(root, Now.AddDays(-1), Now.AddDays(30), new[] { "1.3.6.1.5.5.7.3.2" }, "acme.xrm.test");
        ScopedTrust.Validate("acme.xrm.test", root, "acme.xrm.test", leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeFalse("a client-authentication certificate is not a server certificate");
        using var server = TestCertificates.CreateLeaf(root, Now.AddDays(-1), Now.AddDays(30), "acme.xrm.test");
        ScopedTrust.Validate("acme.xrm.test", root, "acme.xrm.test", server, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeTrue();
    }

    [Fact]
    public void Scoped_trust_validates_through_the_served_intermediate_and_leaves_the_handshake_chain_usable()
    {
        // Validate disposes its own copies of the served certificates, never those of the handshake's chain.
        using var root = TestCertificates.CreateRoot(Now);
        using var intermediate = TestCertificates.CreateIntermediate(root, Now.AddDays(-7), Now.AddDays(180));
        using var leaf = TestCertificates.CreateLeaf(intermediate, Now.AddDays(-1), Now.AddDays(30), "acme.xrm.test");
        using var handshake = new X509Chain();
        handshake.ChainPolicy.ExtraStore.Add(intermediate);

        ScopedTrust.Validate("acme.xrm.test", root, "acme.xrm.test", leaf, handshake, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeTrue();
        ScopedTrust.Validate("acme.xrm.test", root, "acme.xrm.test", leaf, handshake, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeTrue("the chain's certificates are still usable after a first validation");
        handshake.ChainPolicy.ExtraStore[0].Thumbprint.Should().Be(intermediate.Thumbprint);
        intermediate.Thumbprint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Scoped_trust_still_checks_the_validity_period()
    {
        using var root = TestCertificates.CreateRoot(Now);
        using var expired = TestCertificates.CreateLeaf(root, Now.AddDays(-60), Now.AddDays(-1), "acme.xrm.test");
        await using var server = new TlsTestServer(expired);
        using var client = Client(ScopedTrust.CreateHandler("acme.xrm.test", root), server.Port);

        (await SucceedsAsync(client, $"https://acme.xrm.test:{server.Port}/")).Should().BeFalse();
    }

    [Fact]
    public async Task Scoped_trust_does_not_accept_another_root()
    {
        using var root = TestCertificates.CreateRoot(Now);
        using var other = TestCertificates.CreateRoot(Now, "another root");
        using var leaf = TestCertificates.CreateLeaf(other, Now.AddDays(-1), Now.AddDays(30), "acme.xrm.test");
        await using var server = new TlsTestServer(leaf);
        using var client = Client(ScopedTrust.CreateHandler("acme.xrm.test", root), server.Port);

        (await SucceedsAsync(client, $"https://acme.xrm.test:{server.Port}/")).Should().BeFalse();
    }

    [Fact]
    public async Task A_profile_with_a_ca_file_trusts_it_for_its_url_only()
    {
        using var root = TestCertificates.CreateRoot(Now);
        using var leaf = TestCertificates.CreateLeaf(root, Now.AddDays(-1), Now.AddDays(30), "127.0.0.1");
        await using var server = new TlsTestServer(leaf, """{"UserId":"11111111-1111-1111-1111-111111111111"}""");
        using var cli = new TestCli();
        cli.Handler = null!; // the real network: the profile's own handler is what is under test
        var caFile = Path.Combine(cli.ConfigDir, "caddy-root.crt");
        TestCertificates.WritePem(root, caFile);
        var url = $"https://127.0.0.1:{server.Port}/";
        (await cli.LoginDevAsync(url)).Should().Be(0, cli.Err.ToString());

        (await cli.RunAsync("auth", "status")).Should().Be(ExitCodes.Unreachable, "without CaFile the private root is not trusted");

        var store = new ProfileStore(cli.ConfigDir);
        var config = store.Load();
        var name = config.CurrentProfile!;
        config.Profiles[name] = config.Profiles[name] with { CaFile = caFile };
        store.Save(config);
        (await cli.RunAsync("auth", "status")).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("11111111-1111-1111-1111-111111111111");

        // The same profile pointed (--url) at another host name of the same server: default validation again.
        (await cli.RunAsync("auth", "status", "--url", $"https://localhost:{server.Port}/")).Should().NotBe(0);
    }

    [Fact]
    public async Task A_missing_ca_file_is_an_explicit_error()
    {
        using var cli = new TestCli();
        cli.Handler = null!;
        (await cli.LoginDevAsync("https://acme.xrm.test/")).Should().Be(0, cli.Err.ToString());
        var store = new ProfileStore(cli.ConfigDir);
        var config = store.Load();
        var name = config.CurrentProfile!;
        var caFile = Path.Combine(cli.ConfigDir, "missing-root.crt");
        config.Profiles[name] = config.Profiles[name] with { CaFile = caFile };
        store.Save(config);

        (await cli.RunAsync("auth", "status")).Should().Be(ExitCodes.Unexpected);
        cli.Err.ToString().Should().Contain(caFile).And.Contain("xrmd server up");
    }
}
