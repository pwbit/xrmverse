using Microsoft.Extensions.Logging;

namespace Xrmd.Admin;

/// <summary>One captured log line.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps the most recent log lines in memory (a ring: the oldest is dropped when
/// <see cref="Capacity"/> is reached) for <c>GET /admin/logs</c>. The host's logging configuration decides what reaches it.
/// </summary>
public sealed class LogRingBuffer(int capacity = LogRingBuffer.DefaultCapacity) : ILoggerProvider
{
    public const int DefaultCapacity = 5000;

    private readonly object _gate = new();
    private readonly LogEntry?[] _entries = new LogEntry?[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private int _next;
    private int _count;

    public int Capacity => _entries.Length;

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>
    /// The entries at or above <paramref name="minimumLevel"/> and not before <paramref name="since"/>, oldest first, limited
    /// to the most recent <paramref name="take"/>.
    /// </summary>
    public IReadOnlyList<LogEntry> Query(LogLevel minimumLevel, DateTimeOffset? since, int take)
    {
        var matches = new List<LogEntry>();
        lock (_gate)
        {
            // Newest first, stopping once enough have matched.
            for (var i = 1; i <= _count && matches.Count < take; i++)
            {
                var entry = _entries[(_next - i + _entries.Length) % _entries.Length]!;
                if (since is { } s && entry.Timestamp < s) break; // entries are in time order
                if (entry.Level >= minimumLevel) matches.Add(entry);
            }
        }

        matches.Reverse();
        return matches;
    }

    private void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
            if (_count < _entries.Length) _count++;
        }
    }

    private sealed class BufferLogger(LogRingBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
