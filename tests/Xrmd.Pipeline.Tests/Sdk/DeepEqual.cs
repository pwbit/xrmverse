using System.Collections;
using System.Reflection;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// Structural equality for SDK objects: public readable instance properties recursively, collections element-wise,
/// dictionaries by key. Returns the path of the first difference, or null when equal.
/// </summary>
internal static class DeepEqual
{
    public static string? Diff(object? a, object? b) => Diff(a, b, "$", 0);

    public static void Assert(object? expected, object? actual)
    {
        var diff = Diff(expected, actual);
        Xunit.Assert.True(diff is null, diff);
    }

    private static string? Diff(object? a, object? b, string path, int depth)
    {
        if (depth > 40) return $"{path}: too deep";
        if (a is null || b is null) return a is null && b is null ? null : $"{path}: {a ?? "null"} vs {b ?? "null"}";
        var type = a.GetType();
        if (type != b.GetType()) return $"{path}: type {type.Name} vs {b.GetType().Name}";
        if (type.IsPrimitive || type.IsEnum || a is string or decimal or Guid or DateTime or DateTimeOffset or TimeSpan)
            return a.Equals(b) ? null : $"{path}: {a} vs {b}";

        if (a is IDictionary da)
        {
            var db = (IDictionary)b;
            if (da.Count != db.Count) return $"{path}: dictionary count {da.Count} vs {db.Count}";
            foreach (DictionaryEntry e in da)
            {
                if (!db.Contains(e.Key)) return $"{path}[{e.Key}]: missing";
                var d = Diff(e.Value, db[e.Key], $"{path}[{e.Key}]", depth + 1);
                if (d is not null) return d;
            }
            return null;
        }
        if (a is IEnumerable ea)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = ((IEnumerable)b).Cast<object?>().ToList();
            if (la.Count != lb.Count) return $"{path}: count {la.Count} vs {lb.Count}";
            for (var i = 0; i < la.Count; i++)
            {
                var d = Diff(la[i], lb[i], $"{path}[{i}]", depth + 1);
                if (d is not null) return d;
            }
            return null;
        }
        // Dictionary entries of generic dictionaries (KeyValuePair) and other structs/classes: public properties.
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || p.Name == "ExtensionData") continue;
            var d = Diff(p.GetValue(a), p.GetValue(b), $"{path}.{p.Name}", depth + 1);
            if (d is not null) return d;
        }
        return null;
    }
}
