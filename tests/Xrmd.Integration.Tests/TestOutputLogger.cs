using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>An <see cref="ILogger"/> that writes to the xunit test output (for clients under test, e.g. ServiceClient).</summary>
public sealed class TestOutputLogger(ITestOutputHelper output, LogLevel minimum = LogLevel.Information) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        try
        {
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
        }
        catch (InvalidOperationException)
        {
            // The test has already finished; late client log lines are dropped.
        }
    }
}
