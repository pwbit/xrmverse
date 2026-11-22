using System.Data;
using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;

namespace Xrmd.Host;

/// <summary>Startup refused: another xrmd process holds the instance lock of the organization database.</summary>
public sealed class InstanceLockException(string message) : Exception(message);

/// <summary>
/// One xrmd process per organization database: the session applock <see cref="Resource"/> (Exclusive, no wait), taken in the
/// organization database on a connection of its own (<c>Pooling=false</c>, so closing it really ends the session) and held for
/// the process lifetime. The asynchronous job recovery (<c>AsyncOperationWorker.RecoverAsync</c>) relies on it. On PostgreSQL it
/// is a session advisory lock in that database (plan 2.7a, Task 5), through the dialect's application locks.
/// </summary>
public sealed class InstanceLock : IAsyncDisposable
{
    public const string Resource = OrganizationDatabase.InstanceLockResource;

    private readonly string _connectionString;
    private readonly StorageProvider _provider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DbConnection? _connection;
    private bool _disposed;

    private InstanceLock(string connectionString, StorageProvider provider, string database, ILogger logger, DbConnection connection)
    {
        _connectionString = connectionString;
        _provider = provider;
        Database = database;
        _logger = logger;
        _connection = connection;
    }

    /// <summary>The organization database the lock is held in.</summary>
    public string Database { get; }

    /// <summary>The message of the <see cref="InstanceLockException"/> for <paramref name="database"/>.</summary>
    public static string HeldMessage(string database) =>
        $"Another xrmd process already serves the database '{database}' (applock '{Resource}' is held). Run one xrmd process per organization database.";

    /// <summary>Takes the lock in the database of <paramref name="orgConnectionString"/>, without waiting.</summary>
    /// <param name="provider">The engine of <paramref name="orgConnectionString"/> (<c>Storage:Provider</c>).</param>
    /// <exception cref="InstanceLockException">Another session holds it.</exception>
    public static async Task<InstanceLock> AcquireAsync(string orgConnectionString, ILogger logger, CancellationToken ct,
        StorageProvider provider = StorageProvider.SqlServer)
    {
        var database = StorageConnectionStrings.DatabaseName(orgConnectionString);
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("ConnectionStrings:Org must specify a Database (Initial Catalog).");
        var connection = await TryAcquireAsync(orgConnectionString, provider, ct) ?? throw new InstanceLockException(HeldMessage(database));
        logger.LogInformation("Holding the instance lock '{Resource}' of the database {Database}.", Resource, database);
        return new InstanceLock(orgConnectionString, provider, database, logger, connection);
    }

    /// <summary>True when another session holds the lock in the database of <paramref name="orgConnectionString"/> (the database must exist).</summary>
    public static Task<bool> IsHeldByAnotherAsync(string orgConnectionString, StorageProvider provider, CancellationToken ct) =>
        OrganizationDatabase.IsInstanceLockHeldByAnotherAsync(orgConnectionString, provider, ct);

    /// <summary>
    /// Checks that the lock session is alive and still holds the lock; when it is not (SQL Server restart, failover, killed
    /// session), opens a new connection and takes the lock again. False when another session holds it by then. Throws when
    /// SQL Server cannot be reached (the caller retries).
    /// </summary>
    public async Task<bool> EnsureHeldAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null)
            {
                if (await IsHeldOnAsync(_connection, ct)) return true;
                _logger.LogWarning("The instance lock '{Resource}' of the database {Database} was lost; taking it again.", Resource, Database);
                await DisposeQuietlyAsync(_connection);
                _connection = null;
            }

            var connection = await TryAcquireAsync(_connectionString, _provider, ct);
            if (connection is null) return false;
            _connection = connection;
            _logger.LogWarning("The instance lock '{Resource}' of the database {Database} is held again.", Resource, Database);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the lock session (the connection is not pooled: closing it releases the lock).</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_connection is not null) await DisposeQuietlyAsync(_connection);
            _connection = null;
            _logger.LogInformation("Released the instance lock '{Resource}' of the database {Database}.", Resource, Database);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A connection holding the lock, or null when another session holds it.</summary>
    private static Task<DbConnection?> TryAcquireAsync(string connectionString, StorageProvider provider, CancellationToken ct) =>
        OrganizationDatabase.TryTakeInstanceLockAsync(connectionString, provider, ct);

    private async Task<bool> IsHeldOnAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open) return false;
        try
        {
            // A short timeout: a session that does not answer is treated as lost (the lock is then taken again or the process stops).
            // A live but slow session that times out here is closed below, which releases the lock, and EnsureHeldAsync then races to
            // take it again. That is acceptable: if another process wins the race, this one stops (the watchdog), so the database is
            // never served by two processes.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var session = new DataSession(connection, null);
            return await session.Dialect.HoldsAppLockAsync(session, Resource, deadline.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("The instance lock session of the database {Database} is gone: {Message}", Database, ex.Message);
            return false;
        }
    }

    private static async Task DisposeQuietlyAsync(DbConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception)
        {
            // A broken session is already gone; nothing to release.
        }
    }
}

/// <summary>
/// Keeps the <see cref="InstanceLock"/> held (ruling M7): every <see cref="CheckInterval"/> it calls
/// <see cref="InstanceLock.EnsureHeldAsync"/>. When SQL Server cannot be reached, it retries every <c>Startup:SqlRetryDelaySeconds</c>
/// up to <c>Startup:SqlRetries</c> times, all within one deadline (<see cref="DeadlineOf"/>: retries x delay, at least 5 s; 60 s with
/// the defaults). When another process holds the lock, the retries run out or the deadline passes, it logs Critical and stops the
/// application with exit code 1. It stops within about <see cref="CheckInterval"/> once another process holds the lock, and at most
/// about 70 s (with the defaults) when SQL Server cannot be reached; it is not an absolute guarantee. A lost lock is therefore noticed within <see cref="CheckInterval"/>
/// and the stop requested at most the deadline later: with the defaults, the process serves at most about 70 s without the lock
/// (plus the time the host takes to stop). Registered first among the xrmd hosted services, so it stops last
/// and releases the lock only after the others (the asynchronous worker among them) have stopped. Idle on a bare host (no lock).
/// </summary>
public sealed class InstanceLockWatchdog(IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<InstanceLockWatchdog> logger)
    : BackgroundService, IAsyncDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private InstanceLock? _lock;

    /// <summary>Hands the lock taken at startup to the watchdog, which keeps it and releases it when the host stops.</summary>
    public void Attach(InstanceLock instanceLock)
    {
        if (_lock is not null) throw new InvalidOperationException("The instance lock is already attached.");
        _lock = instanceLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_lock is not { } held) return;
        var retries = Math.Max(1, configuration.GetValue("Startup:SqlRetries", 30));
        var delay = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("Startup:SqlRetryDelaySeconds", 2.0)));
        try
        {
            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (await KeepAsync(held, retries, delay, stoppingToken)) continue;
                Environment.ExitCode = 1; // a failure for a supervisor (systemd Restart=on-failure, a script), not a graceful stop
                lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The instance lock watchdog of the database {Database} failed; stopping.", held.Database);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// True when the lock is held (again). All attempts share one deadline, <see cref="DeadlineOf"/>: an attempt blocked on a server
    /// that does not answer (a connect can take its whole connect timeout) cannot stretch the retries beyond it.
    /// </summary>
    private async Task<bool> KeepAsync(InstanceLock held, int retries, TimeSpan delay, CancellationToken stoppingToken)
    {
        var deadline = DeadlineOf(retries, delay);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        bounded.CancelAfter(deadline);
        var ct = bounded.Token;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // WaitAsync: the deadline holds even where SqlClient does not observe the token at once.
                    if (await held.EnsureHeldAsync(ct).WaitAsync(ct)) return true;
                    logger.LogCritical("{Message} This process lost the lock and stops.", InstanceLock.HeldMessage(held.Database));
                    return false;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (attempt >= retries)
                    {
                        logger.LogCritical(ex, "Could not take the instance lock '{Resource}' of the database {Database} again after {Retries} attempts; stopping.",
                            InstanceLock.Resource, held.Database, retries);
                        return false;
                    }
                    logger.LogWarning("Could not take the instance lock of the database {Database} again (attempt {Attempt}/{Retries}): {Message}",
                        held.Database, attempt, retries, ex.Message);
                    await Task.Delay(delay, ct);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical("Could not confirm or take again the instance lock '{Resource}' of the database {Database} within {Deadline}; stopping.",
                InstanceLock.Resource, held.Database, deadline);
            return false;
        }
    }

    /// <summary>The time one check (and its retries) may take: <c>Startup:SqlRetries</c> x <c>Startup:SqlRetryDelaySeconds</c>, at least 5 s.</summary>
    public static TimeSpan DeadlineOf(int retries, TimeSpan delay) =>
        TimeSpan.FromTicks(Math.Max(MinimumDeadline.Ticks, delay.Ticks * Math.Max(1, retries)));

    private static readonly TimeSpan MinimumDeadline = TimeSpan.FromSeconds(5);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_lock is { } held) await held.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_lock is { } held) await held.DisposeAsync();
    }
}
