namespace Xrmd.Files;

/// <summary>Key and prefix validation shared by all stores.</summary>
public static class FileKeys
{
    /// <summary>Throws <see cref="ArgumentException"/> unless the key is non-empty <c>/</c>-separated segments of <c>[A-Za-z0-9._-]</c>, none of them <c>.</c> or <c>..</c>.</summary>
    public static void ValidateKey(string key, string paramName = "key")
    {
        ArgumentNullException.ThrowIfNull(key, paramName);
        if (key.Length == 0) throw new ArgumentException("A file key must not be empty.", paramName);
        ValidateSegments(key.Split('/'), key, paramName);
    }

    /// <summary>Like <see cref="ValidateKey"/> but a prefix may be empty and may end with <c>/</c>.</summary>
    public static void ValidatePrefix(string prefix, string paramName = "prefix")
    {
        ArgumentNullException.ThrowIfNull(prefix, paramName);
        if (prefix.Length == 0) return;
        var segments = prefix.Split('/');
        if (segments.Length > 1 && segments[^1].Length == 0) segments = segments[..^1];
        ValidateSegments(segments, prefix, paramName);
    }

    private static void ValidateSegments(string[] segments, string value, string paramName)
    {
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new ArgumentException($"'{value}' is not a valid file key: empty segment (absolute path or '//').", paramName);
            if (segment is "." or "..")
                throw new ArgumentException($"'{value}' is not a valid file key: '{segment}' segments are not allowed.", paramName);
            foreach (var c in segment)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
                    throw new ArgumentException($"'{value}' is not a valid file key: character '{c}' is not allowed (use [A-Za-z0-9._-] segments separated by '/').", paramName);
        }
    }
}
