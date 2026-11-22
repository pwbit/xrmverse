using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meziantou.Framework.Tds;
using Meziantou.Framework.Tds.Handler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Tds;

/// <summary>
/// The TDS listener of the organization (plan 3.3): starts the TDS server when <see cref="TdsOptions.Port"/> is set and hands
/// every login to <see cref="ITdsAuthenticator"/> and every request to <see cref="ITdsQueryHandler"/>, each resolved in a new
/// DI scope. A login with no user name and no SSPI token, which is how an access token login (FEDAUTH) arrives, is refused
/// before the authenticator with its own message (ruling T5).
/// <para>
/// TLS is required (ruling T3). The only exception is Development without a certificate, where the listener binds the loopback
/// address only and accepts unencrypted connections. The certificate files are polled; when their content changes (a renewal),
/// the server restarts on the same port with the new certificate, within <see cref="CertificatePollInterval"/>. A pair that
/// cannot be loaded (e.g. half written) keeps the running server until the next poll. A pair that cannot be loaded at start
/// (not copied yet, plan 3.3 Task 5) does not fail the host: the endpoint stays closed, with a warning, and starts at the first
/// poll that loads it.
/// </para>
/// </summary>
public sealed class TdsListener : BackgroundService
{
    /// <summary>How often the certificate files are checked (ruling T3: a renewal is picked up within 60 s).</summary>
    public static readonly TimeSpan CertificatePollInterval = TimeSpan.FromSeconds(10);

    private readonly TdsOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TdsListener> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TdsServer? _server;
    private volatile bool _started;
    private volatile bool _disposed;
    private string? _certificatePath;
    private string? _privateKeyPath;
    private byte[]? _certificateHash;

    // The port a restart binds again: the one the first start got (Tds:Port = 0 picks a free one).
    private int _port;

    public TdsListener(IOptions<TdsOptions> options, IServiceScopeFactory scopes, IHostEnvironment environment, ILogger<TdsListener> logger)
    {
        _options = options.Value;
        _scopes = scopes;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>The port the endpoint listens on; null while it does not listen (no <see cref="TdsOptions.Port"/>, or stopped).</summary>
    public int? BoundPort { get; private set; }

    private bool UsesTls => _certificatePath is not null;

    /// <summary>Starts the server before the host completes its start, so a bad configuration or a taken port fails the start.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Port is not { } port) return;
        if (port is < 0 or > 65535) throw new InvalidOperationException($"Tds:Port {port} is not a valid TCP port.");
        if (string.IsNullOrWhiteSpace(_options.CertificatePath) != string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
            throw new InvalidOperationException("Tds:CertificatePath and Tds:PrivateKeyPath must be set together.");
        if (string.IsNullOrWhiteSpace(_options.CertificatePath))
        {
            if (!_environment.IsDevelopment())
                throw new InvalidOperationException(
                    "The SQL endpoint requires TLS: set Tds:CertificatePath and Tds:PrivateKeyPath (PEM files) with Tds:Port.");
            _logger.LogWarning("The SQL endpoint has no certificate: Development only, it listens on the loopback address without TLS.");
        }
        else
        {
            _certificatePath = _options.CertificatePath;
            _privateKeyPath = _options.PrivateKeyPath;
            _certificateHash = ReadCertificate();
            if (_certificateHash is null)
            {
                // The rest of the host (HTTP, web admin, identity provider) must not go down with the SQL endpoint: it opens once
                // the pair can be loaded (the poll of ExecuteAsync starts a server while there is none).
                _logger.LogWarning(
                    "The SQL endpoint stays closed until its certificate (Tds:CertificatePath, Tds:PrivateKeyPath) can be loaded; the files are checked every {Interval} s.",
                    CertificatePollInterval.TotalSeconds);
                _port = port;
                _started = true;
                await base.StartAsync(cancellationToken);
                return;
            }
        }

        await StartServerAsync(port, cancellationToken);
        _port = BoundPort!.Value;
        _started = true;
        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the server. Idempotent and safe in any order with <see cref="Dispose"/> (a host can dispose its services and then
    /// stop them again, e.g. a host that stopped itself and is then disposed); a no-op when the endpoint never started.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started || _disposed) return;
        await base.StopAsync(cancellationToken);
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            DisposeServer();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the server. <see cref="_gate"/> is deliberately not disposed: <see cref="StopAsync"/> can still run after this, and a
    /// <see cref="SemaphoreSlim"/> whose wait handle is never used holds no unmanaged resource.
    /// </summary>
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        base.Dispose();
        DisposeServer();
    }

    private void DisposeServer()
    {
        _server?.Dispose();
        _server = null;
        BoundPort = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!UsesTls) return;
        using var timer = new PeriodicTimer(CertificatePollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // While the endpoint waits for its first pair (logged once at start), a missing pair is not worth a warning per poll.
                var hash = ReadCertificate(_server is null && _certificateHash is null ? LogLevel.Debug : LogLevel.Warning);
                if (hash is null) continue;
                // No server: the last restart failed, so try again even if the files are back to the running pair.
                if (_server is not null && hash.AsSpan().SequenceEqual(_certificateHash)) continue;
                await RestartAsync(hash, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RestartAsync(byte[] hash, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return;
            if (_server is null && _certificateHash is null)
                _logger.LogInformation("The SQL endpoint certificate can be loaded; starting the listener on port {Port}.", _port);
            else if (_server is null)
                _logger.LogInformation("Starting the SQL endpoint listener again on port {Port} after a failed restart.", _port);
            else
                _logger.LogInformation("The SQL endpoint certificate changed; restarting the listener on port {Port}.", _port);
            _server?.Dispose();
            _server = null;
            BoundPort = null;
            await StartServerCoreAsync(_port, cancellationToken);
            // Dispose does not take the gate: if it ran while this restart was binding, the new server was assigned after
            // Dispose disposed the old one, so it is disposed here instead of leaking a bound port.
            if (_disposed)
            {
                DisposeServer();
                return;
            }

            if (_port == 0) _port = BoundPort!.Value; // a deferred first start on Tds:Port = 0: later restarts keep the port it got
            _certificateHash = hash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The next poll tries again (the hash is left unchanged), on the same port.
            _logger.LogError(ex, "The SQL endpoint could not restart with the new certificate.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartServerAsync(int port, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StartServerCoreAsync(port, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartServerCoreAsync(int port, CancellationToken cancellationToken)
    {
        var options = new TdsServerOptions();
        if (UsesTls)
        {
            options.AddTcpListener(port, IPAddress.Any);
            options.RequireEncryption = true;
            options.TlsPemCertificatePath = _certificatePath;
            options.TlsPemPrivateKeyPath = _privateKeyPath;
        }
        else
        {
            options.AddTcpListener(port, IPAddress.Loopback);
        }

        var server = new TdsServer(options, AuthenticateAsync, HandleAsync, _logger);
        try
        {
            await server.StartAsync(cancellationToken);
        }
        catch
        {
            server.Dispose();
            throw;
        }

        _server = server;
        BoundPort = server.Ports.Count > 0 ? server.Ports[0] : port;
        _logger.LogInformation("The SQL endpoint listens on port {Port} ({Tls}).", BoundPort, UsesTls ? "TLS required" : "no TLS");
    }

    private async ValueTask<TdsAuthenticationResult> AuthenticateAsync(TdsAuthenticationContext context, CancellationToken cancellationToken)
    {
        // Ruling T5: the TDS server does not read the FEDAUTH feature extension of LOGIN7, so an access token login
        // (SqlConnection.AccessToken, Microsoft Entra) reaches us with no user name and no token. It is refused here, explicitly.
        if (string.IsNullOrEmpty(context.UserName) && string.IsNullOrEmpty(context.AuthenticationToken))
            return TdsErrors.AccessTokenNotSupportedResult();

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ITdsAuthenticator>().AuthenticateAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never the credentials, nor the exception's message or stack trace, which could echo them: only the client's
            // address and the exception type.
            _logger.LogError("A SQL endpoint login from {RemoteEndPoint} failed with {ExceptionType}.", context.RemoteEndPoint,
                ex.GetType().FullName);
            return TdsErrors.LoginFailedResult();
        }
    }

    private async ValueTask<TdsQueryResult> HandleAsync(TdsQueryContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ITdsQueryHandler>().HandleAsync(context, timeout.Token);
            // A request that ran past the limit fails even when no step observed the cancellation (a short query can finish
            // between two checks): the limit does not depend on where the time was spent.
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                return TdsErrors.Error(TdsErrors.Unsupported, TdsErrors.TimeoutMessage(_options.Timeout));
            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return TdsErrors.Error(TdsErrors.Unsupported, TdsErrors.TimeoutMessage(_options.Timeout));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never the SQL text, nor the exception's message or stack trace, which can echo literal values (ruling T10): only
            // the exception type and, for a database error, its class (ruling P9).
            _logger.LogError("A SQL endpoint request from {RemoteEndPoint} failed with {ExceptionType} (database error {DbErrorKind}).",
                context.RemoteEndPoint, ex.GetType().FullName, ex is System.Data.Common.DbException db ? SqlDialects.Classify(db) : null);
            return TdsErrors.Error(TdsErrors.Internal, "The SQL endpoint could not answer the request.");
        }
    }

    /// <summary>The SHA-256 of the certificate and key files, or null when the pair cannot be read or loaded.</summary>
    private byte[]? ReadCertificate(LogLevel failureLevel = LogLevel.Warning)
    {
        try
        {
            var certificate = File.ReadAllBytes(_certificatePath!);
            var key = File.ReadAllBytes(_privateKeyPath!);
            // Throws when a file is not PEM or the key does not match the certificate (e.g. one file of a renewal written).
            using (X509Certificate2.CreateFromPemFile(_certificatePath!, _privateKeyPath)) { }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(certificate);
            hash.AppendData(key);
            return hash.GetHashAndReset();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            _logger.Log(failureLevel, "The SQL endpoint certificate files cannot be loaded: {Reason}", ex.Message);
            return null;
        }
    }
}
