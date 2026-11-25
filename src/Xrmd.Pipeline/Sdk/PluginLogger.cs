using System.Globalization;
using HostLogger = Microsoft.Extensions.Logging.ILogger;
using HostLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Xrm.Sdk.PluginTelemetry;

namespace Xrmd.Pipeline;

/// <summary>
/// Plugin telemetry logger: forwards every call to the execution's <see cref="TracingService"/> (prefixed with the
/// level) and to the host logger. Scopes are no-ops.
/// </summary>
public sealed class PluginLogger(TracingService tracing, HostLogger host) : ILogger
{
    private sealed class Noop : IDisposable { public void Dispose() { } }

    private static HostLevel Map(LogLevel l) => l switch
    {
        LogLevel.Trace => HostLevel.Trace,
        LogLevel.Debug => HostLevel.Debug,
        LogLevel.Information => HostLevel.Information,
        LogLevel.Warning => HostLevel.Warning,
        LogLevel.Error => HostLevel.Error,
        LogLevel.Critical => HostLevel.Critical,
        _ => HostLevel.None,
    };

    private void Write(LogLevel level, Exception? ex, string? message, object[]? args)
    {
        string text;
        try { text = args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, message ?? string.Empty, args) : message ?? string.Empty; }
        catch (FormatException) { text = message ?? string.Empty; }
        if (ex is not null) text += " " + ex.Message;
        tracing.Trace("{0}: {1}", level, text);
        if (host.IsEnabled(Map(level))) host.Log(Map(level), default(Microsoft.Extensions.Logging.EventId), text, ex, static (s, _) => s);
    }

    /// <inheritdoc />
    public void AddCustomProperty(string propertyName, string propertyValue) { }
    /// <inheritdoc />
    public IDisposable BeginScope(string messageFormat, params object[] args) => new Noop();
    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) => new Noop();
    /// <inheritdoc />
    public void Execute(string activityName, Action execute, IEnumerable<KeyValuePair<string, string>>? additionalProperties = null) => execute();
    /// <inheritdoc />
    public Task ExecuteAsync(string activityName, Func<Task> execute, IEnumerable<KeyValuePair<string, string>>? additionalProperties = null) => execute();
    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    /// <inheritdoc />
    public void Log(LogLevel logLevel, EventId eventId, Exception exception, string message, params object[] args) => Write(logLevel, exception, message, args);
    /// <inheritdoc />
    public void Log(LogLevel logLevel, EventId eventId, string message, params object[] args) => Write(logLevel, null, message, args);
    /// <inheritdoc />
    public void Log(LogLevel logLevel, Exception exception, string message, params object[] args) => Write(logLevel, exception, message, args);
    /// <inheritdoc />
    public void Log(LogLevel logLevel, string message, params object[] args) => Write(logLevel, null, message, args);
    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        => Write(logLevel, exception, formatter(state, exception), null);

    /// <inheritdoc />
    public void LogCritical(EventId eventId, Exception exception, string message, params object[] args) => Write(LogLevel.Critical, exception, message, args);
    /// <inheritdoc />
    public void LogCritical(EventId eventId, string message, params object[] args) => Write(LogLevel.Critical, null, message, args);
    /// <inheritdoc />
    public void LogCritical(Exception exception, string message, params object[] args) => Write(LogLevel.Critical, exception, message, args);
    /// <inheritdoc />
    public void LogCritical(string message, params object[] args) => Write(LogLevel.Critical, null, message, args);
    /// <inheritdoc />
    public void LogDebug(EventId eventId, Exception exception, string message, params object[] args) => Write(LogLevel.Debug, exception, message, args);
    /// <inheritdoc />
    public void LogDebug(EventId eventId, string message, params object[] args) => Write(LogLevel.Debug, null, message, args);
    /// <inheritdoc />
    public void LogDebug(Exception exception, string message, params object[] args) => Write(LogLevel.Debug, exception, message, args);
    /// <inheritdoc />
    public void LogDebug(string message, params object[] args) => Write(LogLevel.Debug, null, message, args);
    /// <inheritdoc />
    public void LogError(EventId eventId, Exception exception, string message, params object[] args) => Write(LogLevel.Error, exception, message, args);
    /// <inheritdoc />
    public void LogError(EventId eventId, string message, params object[] args) => Write(LogLevel.Error, null, message, args);
    /// <inheritdoc />
    public void LogError(Exception exception, string message, params object[] args) => Write(LogLevel.Error, exception, message, args);
    /// <inheritdoc />
    public void LogError(string message, params object[] args) => Write(LogLevel.Error, null, message, args);
    /// <inheritdoc />
    public void LogInformation(EventId eventId, Exception exception, string message, params object[] args) => Write(LogLevel.Information, exception, message, args);
    /// <inheritdoc />
    public void LogInformation(EventId eventId, string message, params object[] args) => Write(LogLevel.Information, null, message, args);
    /// <inheritdoc />
    public void LogInformation(Exception exception, string message, params object[] args) => Write(LogLevel.Information, exception, message, args);
    /// <inheritdoc />
    public void LogInformation(string message, params object[] args) => Write(LogLevel.Information, null, message, args);
    /// <inheritdoc />
    public void LogMetric(string name, IDictionary<string, string> properties, long value) { }
    /// <inheritdoc />
    public void LogMetric(string name, long value) { }
    /// <inheritdoc />
    public void LogTrace(EventId eventId, Exception exception, string message, params object[] args) => Write(LogLevel.Trace, exception, message, args);
    /// <inheritdoc />
    public void LogTrace(EventId eventId, string message, params object[] args) => Write(LogLevel.Trace, null, message, args);
    /// <inheritdoc />
    public void LogTrace(Exception exception, string message, params object[] args) => Write(LogLevel.Trace, exception, message, args);
    /// <inheritdoc />
    public void LogTrace(string message, params object[] args) => Write(LogLevel.Trace, null, message, args);
    /// <inheritdoc />
    public void LogWarning(EventId eventId, Exception exception, string message, params object[] args) => Write(LogLevel.Warning, exception, message, args);
    /// <inheritdoc />
    public void LogWarning(EventId eventId, string message, params object[] args) => Write(LogLevel.Warning, null, message, args);
    /// <inheritdoc />
    public void LogWarning(Exception exception, string message, params object[] args) => Write(LogLevel.Warning, exception, message, args);
    /// <inheritdoc />
    public void LogWarning(string message, params object[] args) => Write(LogLevel.Warning, null, message, args);
}
