using System.Collections.Concurrent;
using Microsoft.PowerFx;

namespace Xrmd.Workflows.Formula;

/// <summary>
/// What <see cref="FormulaTranslator"/> keeps between calls (plan 2.12c): the Power Fx parse of each formula text, and the outcome of
/// <c>Engine.Check</c> (the first error, or none) by formula text and the names it reads with the columns and types they resolved to.
/// A query compilation translates every computed column it reads, so neither runs again for a definition seen before; a change of a
/// column the formula reads changes the key. Each map starts over above <see cref="MaxEntries"/> (texts replaced by updates are never
/// read again).
/// </summary>
internal sealed class FormulaCache
{
    private const int MaxEntries = 4096;

    private readonly ConcurrentDictionary<string, ParseResult> _parses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Text, string Reads), string?> _checks = new();
    private int _parseCount, _checkCount;

    /// <summary>How many times a formula was parsed (a cache miss); for tests.</summary>
    public int ParseCount => Volatile.Read(ref _parseCount);

    /// <summary>How many times a formula was checked (a cache miss); for tests.</summary>
    public int CheckCount => Volatile.Read(ref _checkCount);

    /// <summary>The parse of <paramref name="text"/>, from the cache or by <paramref name="parse"/>.</summary>
    public ParseResult Parse(string text, Func<string, ParseResult> parse)
    {
        if (_parses.TryGetValue(text, out var cached)) return cached;
        Interlocked.Increment(ref _parseCount);
        var result = parse(text);
        if (_parses.Count >= MaxEntries) _parses.Clear();
        return _parses.GetOrAdd(text, result);
    }

    /// <summary>The first error of the check of <paramref name="text"/> reading <paramref name="reads"/> (null: none).</summary>
    public string? Check(string text, string reads, Func<string?> check)
    {
        if (_checks.TryGetValue((text, reads), out var cached)) return cached;
        Interlocked.Increment(ref _checkCount);
        var result = check();
        if (_checks.Count >= MaxEntries) _checks.Clear();
        _checks[(text, reads)] = result;
        return result;
    }
}
