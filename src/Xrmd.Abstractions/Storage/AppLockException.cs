namespace Xrmd.Abstractions.Storage;

/// <summary>
/// The engine did not grant an application lock (<see cref="ISqlDialect.AcquireAppLockAsync(DataSession, string, bool, CancellationToken)"/>,
/// plan 2.7a, ruling P7): it timed out, was chosen as a deadlock victim or was refused. Callers catch it to report the lock in
/// their own terms, with <see cref="Detail"/>.
/// </summary>
public sealed class AppLockException(string resource, string detail)
    : InvalidOperationException($"Could not acquire the application lock '{resource}' ({detail}).")
{
    /// <summary>The name of the lock that was not granted.</summary>
    public string Resource { get; } = resource;

    /// <summary>The engine's own account of the refusal (<c>sp_getapplock returned -1</c> on SQL Server).</summary>
    public string Detail { get; } = detail;
}
