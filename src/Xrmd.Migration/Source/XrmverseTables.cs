namespace Xrmd.Migration.Source;

/// <summary>
/// The standard tables xrmverse has out of the box: the names of its seed files (<c>fixtures/seed/*.json</c>), embedded at build time.
/// A standard Dataverse table outside this list does not exist in the target (spec §6.3: only tables that exist in the target schema
/// after the schema phase are migrated), so its rows are counted in the report, never read.
/// </summary>
public static class XrmverseTables
{
    private static readonly Lazy<HashSet<string>> Names = new(Load);

    public static bool Contains(string logicalName) => Names.Value.Contains(logicalName);

    private static HashSet<string> Load()
    {
        using var stream = typeof(XrmverseTables).Assembly.GetManifestResourceStream("Xrmd.Migration.XrmverseTables.txt")
            ?? throw new InvalidOperationException("The list of xrmverse tables is not embedded.");
        using var reader = new StreamReader(stream);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (line.Trim() is { Length: > 0 } name) names.Add(name);
        return names;
    }
}
