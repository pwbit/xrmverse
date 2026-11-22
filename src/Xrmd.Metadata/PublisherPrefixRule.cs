using System.Data;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// The publisher prefix rule: the name of a custom component supplied by a caller (table, column, relationship, global option
/// set) is <c>{prefix}_{rest}</c>, where <c>{prefix}</c> is the <c>customizationprefix</c> of an existing publisher. A single
/// instance per organization (registered as a singleton), called by every path that creates a custom component: the metadata
/// messages (SDK and Web API), the Web API's table and column creation, the solution import and <c>xrmd apply</c>.
/// <list type="bullet">
/// <item>Only the prefix is checked, not that it belongs to the publisher of the solution the component is created in:
/// any existing publisher's prefix is accepted.</item>
/// <item>The prefix is compared exactly (case-sensitive), and a name equal to the prefix with no <c>_rest</c> is refused.</item>
/// <item>Seeded names (the system tables and columns) and the names the server generates (the <c>_base</c> column of a money
/// column, the file and image companions, the implied relationship of a lookup, an intersect table) never reach the rule:
/// callers pass only the names a user supplied.</item>
/// </list>
/// The prefixes are read from <c>data.publisher</c> and cached; <see cref="Invalidate"/> drops the cache (after a write to a
/// <c>publisher</c> row), and a name refused against the cache is checked once more against the table before it is refused, so
/// a publisher created by a path that did not invalidate is usable at once all the same.
/// </summary>
public sealed class PublisherPrefixRule(IDbConnectionFactory factory)
{
    private const string Table = "publisher";

    /// <summary>What was loaded under one version; it is never used once the version has moved on.</summary>
    private sealed record Loaded(int Version, IReadOnlySet<string> Prefixes);

    private readonly object _loadLock = new();
    private int _version;
    private Loaded? _loaded;

    /// <summary>The customization prefixes of the organization's publishers (a cached snapshot).</summary>
    public IReadOnlySet<string> Prefixes => Current();

    /// <summary>Drops the cached prefixes: the next use reads them again. Called after a write to a <c>publisher</c> row.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>True when <paramref name="name"/> is <c>{prefix}_{rest}</c> for one of <paramref name="prefixes"/> (exact match, a non-empty rest).</summary>
    public static bool HasPrefix(string? name, IEnumerable<string> prefixes)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var underscore = name.IndexOf('_');
        if (underscore <= 0 || underscore == name.Length - 1) return false;
        var prefix = name.AsSpan(0, underscore);
        foreach (var candidate in prefixes)
            if (prefix.SequenceEqual(candidate)) return true;
        return false;
    }

    /// <summary>
    /// Demands that <paramref name="name"/> carries the prefix of an existing publisher (or one of
    /// <paramref name="extraPrefixes"/>, the prefixes of publishers a caller is about to create, e.g. the publisher of a solution
    /// being imported).
    /// </summary>
    /// <param name="name">The name a caller supplied for a custom component.</param>
    /// <param name="componentKind">What the name is of, for the message: <c>table</c>, <c>column</c>, <c>relationship</c>, <c>choice</c>.</param>
    /// <exception cref="XrmdValidationException">The name has no prefix of an existing publisher (400).</exception>
    public void Demand(string? name, string componentKind, IEnumerable<string>? extraPrefixes = null)
    {
        var extra = extraPrefixes?.ToList() ?? [];
        var prefixes = Current();
        if (HasPrefix(name, prefixes) || HasPrefix(name, extra)) return;
        // Not found in the cache: look at the table once more before refusing (a publisher may have just been created).
        prefixes = Load(force: true);
        if (HasPrefix(name, prefixes) || HasPrefix(name, extra)) return;
        throw new XrmdValidationException(Refusal(name, componentKind, prefixes.Concat(extra)));
    }

    /// <summary>
    /// Demands the prefix of the names a caller supplied for a table: its logical name, its primary name column and every
    /// column of <paramref name="entity"/>.Attributes except the ones the server generates (primary key, system columns, the
    /// stored-by-other-column companions such as a money column's <c>_base</c>) and, for a custom activity, <c>subject</c> and the
    /// names of the columns every activity has.
    /// </summary>
    public void DemandEntity(EntityMetadata entity, AttributeMetadata? primaryName, IEnumerable<string>? extraPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var extra = extraPrefixes?.ToList();
        // A custom activity's primary column is 'subject' and its other columns are the server's (plan 2.11 A4, pre-flight C7): the
        // metadata store refuses the names it generates itself, with an error that says so.
        var activity = entity.IsActivity == true;
        bool Exempt(string? name) => activity && (name == ActivitySchema.Subject || ActivitySchema.IsGeneratedActivityAttribute(name));
        Demand(entity.LogicalName, "table", extra);
        if (primaryName is not null && !Exempt(primaryName.LogicalName)) Demand(primaryName.LogicalName, "column", extra);
        foreach (var attribute in entity.Attributes ?? [])
            if (IsSupplied(entity.LogicalName, attribute) && !Exempt(attribute.LogicalName)) Demand(attribute.LogicalName, "column", extra);
    }

    /// <summary>True for a column a caller defines (not the primary key, a system column or a generated companion).</summary>
    public static bool IsSupplied(string entityLogicalName, AttributeMetadata attribute) =>
        !SystemAttributes.IsReservedName(entityLogicalName, attribute.LogicalName) && !TypeMapping.IsStoredByOtherAttribute(attribute);

    private static string Refusal(string? name, string componentKind, IEnumerable<string> prefixes)
    {
        var known = string.Join(", ", prefixes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(p => $"'{p}'"));
        return $"The {componentKind} name '{name}' does not start with the prefix of an existing publisher: a custom name is "
               + $"'<prefix>_<name>' (lowercase prefix, then '_' and a name). The existing prefixes are {known}. "
               + "Create a publisher with 'xrmd publisher create' first.";
    }

    private IReadOnlySet<string> Current()
    {
        var loaded = Volatile.Read(ref _loaded);
        return loaded is not null && loaded.Version == Volatile.Read(ref _version) ? loaded.Prefixes : Load(force: false);
    }

    /// <summary>Reads the prefixes from the table. Blocking: it runs only on the first use and after an invalidation.</summary>
    private IReadOnlySet<string> Load(bool force)
    {
        lock (_loadLock)
        {
            var version = Volatile.Read(ref _version);
            if (!force && _loaded is { } current && current.Version == version) return current.Prefixes;
            var prefixes = ReadPrefixesAsync().GetAwaiter().GetResult();
            // An invalidation that came in while reading leaves the result with an old version: it is used by this call only.
            _loaded = new Loaded(version, prefixes);
            return prefixes;
        }
    }

    private async Task<IReadOnlySet<string>> ReadPrefixesAsync()
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT customizationprefix FROM {SqlDialects.For(conn).DataTable(Table)} WHERE customizationprefix IS NOT NULL";
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        while (await reader.ReadAsync()) prefixes.Add(reader.GetString(0));
        return prefixes;
    }
}
