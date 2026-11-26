using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// The files of an unpacked solution folder held in memory: relative paths with <c>/</c> separators (relative to the folder
/// that holds <c>Other/Solution.xml</c>) mapped to their bytes. Lookup is case-sensitive; adding a key that differs from an
/// existing one only by case is refused, so a set can always be written to a case-insensitive file system.
/// </summary>
public sealed class SourceFiles : IReadOnlyDictionary<string, byte[]>
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of files.</summary>
    public int Count => _files.Count;

    /// <summary>The paths, in no particular order.</summary>
    public IEnumerable<string> Keys => _files.Keys;

    /// <summary>The contents, in no particular order.</summary>
    public IEnumerable<byte[]> Values => _files.Values;

    /// <summary>The bytes of the file at <paramref name="key"/>.</summary>
    public byte[] this[string key] => _files[key];

    /// <summary>Adds a file.</summary>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument when the path is empty, absolute, has a drive letter, a backslash or a <c>.</c>, <c>..</c> or empty
    /// segment, or when the same path (ignoring case) or a different-case variant of it is already present.
    /// </exception>
    public void Add(string key, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateKey(key);
        if (_folded.TryGetValue(key, out var existing))
        {
            throw XrmdFaultException.InvalidArgument(
                existing == key
                    ? $"The source file '{key}' appears more than once."
                    : $"The source files '{existing}' and '{key}' differ only by case, which a case-insensitive file system cannot hold.");
        }
        _files.Add(key, content);
        _folded.Add(key, key);
    }

    /// <summary>Whether a file with exactly this path exists.</summary>
    public bool ContainsKey(string key) => _files.ContainsKey(key);

    /// <summary>Looks a file up by its exact path.</summary>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out byte[] value) => _files.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator() => _files.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Checks that <paramref name="key"/> is a safe relative path with <c>/</c> separators.</summary>
    internal static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bad = key.Length == 0
            || key[0] == '/'
            || key.Contains('\\')
            || key.Contains('\0')
            || (key.Length >= 2 && key[1] == ':')
            || key.Split('/').Any(s => s.Length == 0 || s == "." || s == "..");
        if (bad) throw XrmdFaultException.InvalidArgument($"The source file path '{key}' is not a valid relative path.");
    }
}
