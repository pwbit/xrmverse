using System.Globalization;
using System.Text;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Pipeline;

/// <summary>Collects plugin trace output, one line per call, capped at <see cref="MaxLength"/> characters.</summary>
public sealed class TracingService : ITracingService
{
    /// <summary>Maximum length of <see cref="Text"/>.</summary>
    public const int MaxLength = 10_240;

    private readonly StringBuilder _text = new();
    private readonly object _gate = new();

    /// <summary>The trace lines separated by <c>\n</c>, truncated to <see cref="MaxLength"/>.</summary>
    public string Text
    {
        get { lock (_gate) return _text.ToString(); }
    }

    /// <inheritdoc />
    public void Trace(string format, params object[] args)
    {
        string line;
        if (args is null || args.Length == 0) line = format ?? string.Empty;
        else
        {
            try { line = string.Format(CultureInfo.InvariantCulture, format, args); }
            catch (FormatException) { line = format; }
        }

        lock (_gate)
        {
            if (_text.Length >= MaxLength) return;
            if (_text.Length > 0) _text.Append('\n');
            _text.Append(line);
            if (_text.Length > MaxLength) _text.Length = MaxLength;
        }
    }
}
