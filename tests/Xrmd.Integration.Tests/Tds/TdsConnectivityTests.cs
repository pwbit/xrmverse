using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Tds;

namespace Xrmd.Integration.Tests.Tds;

/// <summary>
/// The SQL endpoint's listener (plan 3.3, Task 1): TLS required (ruling T3), access-token login (FEDAUTH, ruling T5) and
/// certificate reload (ruling T3), with real SQL logins against a host in Local mode (<see cref="TdsQueryFixture"/>).
/// </summary>
[Collection("tds-query")]
public sealed class TdsConnectivityTests(TdsQueryFixture fx)
{
    private const byte EncryptOff = 0x00;
    private const byte EncryptOn = 0x01;
    private const byte EncryptNotSupported = 0x02;
    private const byte EncryptRequired = 0x03;

    [Fact(Skip = "FEDAUTH not supported by TdsServer 2.0.19 (ruling T5)")]
    public async Task Select_1_over_tls_with_access_token()
    {
        var host = fx.Host;
        await using var connection = new SqlConnection(host.ConnectionString().ConnectionString) { AccessToken = TdsTestHost.TestToken };
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT 1", connection);

        (await command.ExecuteScalarAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Access_token_login_is_refused_explicitly()
    {
        var host = fx.Host;
        await using var connection = new SqlConnection(host.ConnectionString().ConnectionString) { AccessToken = TdsTestHost.TestToken };

        var error = (await FluentActions.Awaiting(() => connection.OpenAsync()).Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be((int)TdsErrors.LoginFailed);
        error.Message.Should().Be(TdsErrors.AccessTokenNotSupportedMessage);
    }

    [Fact]
    public async Task Select_over_tls_with_sql_login()
    {
        await using var connection = new SqlConnection(fx.Host.SqlLoginConnectionString().ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT SERVERPROPERTY('ProductVersion')", connection);

        (await command.ExecuteScalarAsync()).Should().Be("12.0.2000.8");
    }

    /// <summary>
    /// SqlClient never sends a login in clear to this server: with <c>Encrypt=False</c> it announces ENCRYPT_OFF, the server
    /// answers ENCRYPT_REQ, and SqlClient then encrypts the whole session (so such a connection succeeds, over TLS). What must be
    /// refused is a client that does not do TLS: one that cannot (ENCRYPT_NOT_SUP), or one that sends a valid LOGIN7 in clear.
    /// </summary>
    [Fact]
    public async Task Plain_connection_is_refused_when_tls_is_required()
    {
        var host = fx.Host;

        (await host.ProbePreLoginAsync(EncryptNotSupported)).Should().Be((EncryptRequired, true, false), "a client without TLS is refused");
        (await host.ProbePreLoginAsync(EncryptOff)).Should().Be((EncryptRequired, false, false), "a client that can do TLS is told to");
        (await host.ProbePreLoginAsync(EncryptOn)).Should().Be((EncryptRequired, false, false));

        // A well-formed LOGIN7 with the System Administrator's valid SQL login, in clear: the connection ends and no LOGINACK comes back.
        (await host.ProbePreLoginAsync(EncryptOff, cleartextLogin: true))
            .Should().Be((EncryptRequired, true, false), "a valid login sent in clear is refused");
    }

    /// <summary>A host that is not in Local mode (here Development, no organization database) refuses every SQL login.</summary>
    [Fact]
    public async Task Production_defaults_refuse_every_login()
    {
        await using var host = await TdsTestHost.StartAsync();
        await using var connection = new SqlConnection(host.SqlLoginConnectionString().ConnectionString);

        var error = (await FluentActions.Awaiting(() => connection.OpenAsync()).Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be((int)TdsErrors.LoginFailed);
        error.Message.Should().Be("Login failed for the SQL endpoint.");
    }

    /// <summary>
    /// Plan 3.3, Task 5: the certificate is copied by tds-certs after the organization started. Without it the host still runs
    /// (HTTP, web admin, identity provider) with the endpoint closed, and the endpoint opens within a poll of the pair appearing.
    /// </summary>
    [Fact]
    public async Task A_certificate_written_after_the_start_opens_the_endpoint()
    {
        await using var host = await TdsTestHost.StartAsync(new TdsTestOrg(await fx.NewDatabaseAsync(), fx.AdminObjectId), withCertificate: false);
        host.App.Services.GetRequiredService<TdsListener>().BoundPort.Should().BeNull("the pair is not there yet");
        using (var http = new HttpClient { BaseAddress = new Uri(host.App.Urls.First()) })
            (await http.GetAsync("/healthz")).IsSuccessStatusCode.Should().BeTrue("the rest of the host runs");
        host.Logs.Lines.Should().Contain(m => m.Contains("stays closed until its certificate", StringComparison.Ordinal));

        host.WriteCertificatePair();

        (await host.WaitForListenerAsync(TdsListener.CertificatePollInterval * 3)).Should().NotBeNull("the endpoint opens at the next poll");
        await using var connection = new SqlConnection(host.SqlLoginConnectionString().ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT SERVERPROPERTY('ProductVersion')", connection);
        (await command.ExecuteScalarAsync()).Should().Be("12.0.2000.8");
    }

    [Fact]
    public async Task Certificate_change_restarts_the_listener()
    {
        await using var host = await TdsTestHost.StartAsync(new TdsTestOrg(await fx.NewDatabaseAsync(), fx.AdminObjectId));
        var before = await host.ProbeThumbprintAsync();

        var after = host.ReplaceCertificate();
        after.Should().NotBe(before);

        var watch = Stopwatch.StartNew();
        string seen;
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                seen = await host.ProbeThumbprintAsync();
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                seen = "";  // Between the stop and the start of the restart.
            }
        } while (seen != after && watch.Elapsed < TimeSpan.FromSeconds(60));

        seen.Should().Be(after, "the listener restarts with the new certificate within 60 s");

        // The restarted listener serves the same port.
        await using var connection = new SqlConnection(host.SqlLoginConnectionString().ConnectionString);
        await connection.OpenAsync();
    }
}
