using System.Diagnostics;
using FluentAssertions;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Tests;

/// <summary>The generated <c>Caddyfile</c> of the <c>caddy</c> proxy mode (H3, H5, H8, H10).</summary>
public class CaddyfileRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-caddyfile-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string EnvPath => Path.Combine(_dir, ".env");
    private string CaddyfilePath => Path.Combine(_dir, "Caddyfile");

    private async Task<Installation> InitAsync(params string[] extra)
    {
        using var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--dir", _dir, .. TestCli.Eula(extra)])).Should().Be(0, cli.Err.ToString());
        return Installation.Load(_dir);
    }

    /// <summary>An installation in <c>files</c> mode without going through a certificate pair: init in internal mode, then edit .env.</summary>
    private async Task<Installation> FilesModeAsync()
    {
        await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        File.WriteAllText(EnvPath, File.ReadAllText(EnvPath).Replace("TLS_MODE=internal", "TLS_MODE=files"));
        return Installation.Load(_dir);
    }

    /// <summary>The block of one site, from its address line to its closing brace.</summary>
    private static string Site(string caddyfile, string host)
    {
        var start = caddyfile.IndexOf("\n" + host + " {\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"the Caddyfile has a site for {host}:\n{caddyfile}");
        var end = caddyfile.IndexOf("\n}\n", start, StringComparison.Ordinal);
        return caddyfile[(start + 1)..(end + 3)];
    }

    [Fact]
    public async Task Renders_one_site_per_host_proxying_to_the_org_service()
    {
        var installation = await InitAsync("--org", "acme", "--org", "zenith", "--org", "orbit",
            "--public-host", "acme=acme.example.com", "--public-host", "zenith=crm.example.com", "--tls", "internal");

        var caddyfile = CaddyfileRenderer.Render(installation);
        Site(caddyfile, "acme.example.com").Should().Contain("\treverse_proxy xrmd-acme:5000\n");
        Site(caddyfile, "crm.example.com").Should().Contain("\treverse_proxy xrmd-zenith:5000\n");
        caddyfile.Should().NotContain("orbit", "an organization without a host is not behind the proxy");
        caddyfile.Split('\n').Count(l => l.EndsWith(" {", StringComparison.Ordinal) && !l.StartsWith('\t')).Should().Be(2);
        File.ReadAllText(CaddyfilePath).Should().Be(caddyfile, "init writes the rendered Caddyfile beside the compose file");
    }

    [Fact]
    public async Task Upstream_can_be_replaced()
    {
        var installation = await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal");

        CaddyfileRenderer.Render(installation, org => $"host.docker.internal:{org.Port}")
            .Should().Contain("\treverse_proxy host.docker.internal:5001\n").And.NotContain("xrmd-acme");
    }

    [Fact]
    public async Task Files_mode_points_at_the_cert_pair()
    {
        var caddyfile = CaddyfileRenderer.Render(await FilesModeAsync());

        Site(caddyfile, "acme.xrm.test").Should().Contain("\ttls /certs/server.crt /certs/server.key\n");
        caddyfile.Should().NotContain("email").And.NotContain("tls internal");
    }

    [Fact]
    public async Task Acme_mode_sets_the_global_email()
    {
        var caddyfile = CaddyfileRenderer.Render(
            await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "acme", "--acme-email", "ops@example.com"));

        var global = caddyfile.IndexOf("{\n\temail ops@example.com\n}\n", StringComparison.Ordinal);
        global.Should().BeGreaterThan(0, caddyfile);
        global.Should().BeLessThan(caddyfile.IndexOf("acme.xrm.test {", StringComparison.Ordinal), "the global options block comes first");
        Site(caddyfile, "acme.xrm.test").Should().NotContain("\ttls ", "the certificate comes from the ACME CA");
    }

    [Fact]
    public async Task Internal_mode_uses_tls_internal()
    {
        var caddyfile = CaddyfileRenderer.Render(await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal"));

        Site(caddyfile, "acme.xrm.test").Should().Contain("\ttls internal\n");
        Site(caddyfile, "zenith.xrm.test").Should().Contain("\ttls internal\n");
        caddyfile.Should().NotContain("email");
    }

    [Fact]
    public async Task Body_limit_matches_solution_import()
    {
        // Xrmd.WebApi.SolutionFileSizeLimit.MaxBytes (128 MB); Xrmd.Integration.Tests compares the two constants, since this
        // project does not reference the server.
        CaddyfileRenderer.RequestBodyMaxBytes.Should().Be(128L * 1024 * 1024);
        var caddyfile = CaddyfileRenderer.Render(await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal"));

        Site(caddyfile, "acme.xrm.test").Should().Contain("\trequest_body {\n\t\tmax_size 134217728\n\t}\n",
            "in bytes: Caddy reads 128MB as 128,000,000");
    }

    [Fact]
    public async Task Output_starts_with_the_generated_header()
    {
        var caddyfile = CaddyfileRenderer.Render(await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal"));

        caddyfile.Should().StartWith(ComposeRenderer.Header + " ");
        ComposeRenderer.IsGenerated(caddyfile).Should().BeTrue();
        caddyfile.Should().NotContain("{{").And.NotContain("#{{").And.EndWith("}\n");
    }

    [Fact]
    public async Task Render_refuses_an_installation_without_the_caddy_proxy()
    {
        var installation = await InitAsync("--org", "acme");

        installation.Invoking(i => CaddyfileRenderer.Render(i)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task MissingCertificates_lists_absent_files_only()
    {
        var installation = await FilesModeAsync();
        CaddyfileRenderer.MissingCertificates(installation).Should().Equal("certs/server.crt", "certs/server.key");

        Directory.CreateDirectory(installation.CertsPath);
        File.WriteAllText(Path.Combine(installation.CertsPath, "server.crt"), "cert");
        CaddyfileRenderer.MissingCertificates(installation).Should().Equal("certs/server.key");

        File.WriteAllText(Path.Combine(installation.CertsPath, "server.key"), "key");
        CaddyfileRenderer.MissingCertificates(installation).Should().BeEmpty();

        Directory.Delete(_dir, recursive: true);
        CaddyfileRenderer.MissingCertificates(await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal"))
            .Should().BeEmpty("only TLS_MODE=files reads a certificate pair");
    }

    /// <summary>H10: <c>caddy validate</c> of the pinned image accepts the rendered Caddyfile in every TLS mode.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task Rendered_caddyfile_is_valid_for_the_pinned_caddy()
    {
        foreach (var mode in new[] { "acme", "files", "internal" })
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            var installation = mode switch
            {
                "acme" => await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "acme", "--acme-email", "ops@example.com"),
                "files" => await FilesModeAsync(),
                _ => await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--public-host", "zenith=crm.example.com", "--tls", "internal"),
            };
            installation.Save();
            if (mode == "files") WriteSelfSignedPair(installation.CertsPath);

            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[]
                     {
                         "run", "--rm", "-v", $"{CaddyfilePath}:/etc/caddy/Caddyfile:ro", "-v", $"{installation.CertsPath}:/certs:ro",
                         ComposeRenderer.CaddyImage, "caddy", "validate", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile",
                     })
                start.ArgumentList.Add(arg);
            Directory.CreateDirectory(installation.CertsPath);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, $"{mode}: {await stderr}{await stdout}");
            (await stdout).Should().Contain("Valid configuration", mode);
            (await stderr).Should().NotContain("\"level\":\"warn\"", $"{mode}: no warning (an unformatted Caddyfile, for instance)")
                .And.Contain("enabling automatic HTTP->HTTPS redirects", "H5: port 80 only redirects to 443");
        }
    }

    private static void WriteSelfSignedPair(string dir)
    {
        Directory.CreateDirectory(dir);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=*.xrm.test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        File.WriteAllText(Path.Combine(dir, "server.crt"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(dir, "server.key"), rsa.ExportPkcs8PrivateKeyPem());
    }
}
