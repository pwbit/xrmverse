namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// The order the records phase writes the tables in (spec §7.6 step 4): a table after the tables its lookups reference. A self
/// reference is ignored (the row is written or deferred within the table); a cycle is broken by taking, among the tables left, the
/// one with the fewest unwritten dependencies (then by name): its lookups to the tables after it are deferred.
/// </summary>
public static class TableOrder
{
    public static IReadOnlyList<string> Sort(IEnumerable<string> tables, Func<string, IEnumerable<(string Column, string Target)>> lookups)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(lookups);
        var set = new SortedSet<string>(tables, StringComparer.Ordinal);
        var deps = set.ToDictionary(t => t, t => lookups(t).Select(l => l.Target).Where(x => x != t && set.Contains(x)).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var order = new List<string>();
        var left = new SortedSet<string>(set, StringComparer.Ordinal);
        while (left.Count > 0)
        {
            var next = left.Select(t => (Table: t, Unmet: deps[t].Count(left.Contains))).OrderBy(x => x.Unmet).ThenBy(x => x.Table, StringComparer.Ordinal).First();
            order.Add(next.Table);
            left.Remove(next.Table);
        }
        return order;
    }
}
