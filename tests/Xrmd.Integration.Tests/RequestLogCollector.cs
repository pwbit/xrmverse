using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Xrmd.Integration.Tests;

/// <summary>Collects the host's log lines in memory so tests can print the requests a client made.</summary>
public sealed class RequestLogCollector : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Drain()
    {
        var lines = new List<string>();
        while (_lines.TryDequeue(out var line)) lines.Add(line);
        return lines;
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _lines.Enqueue($"[xrmd {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception.GetType().Name + ": " + exception.Message)}");

    public void Dispose()
    {
    }
}
