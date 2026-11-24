using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization whose tables were made before <c>overriddencreatedon</c> (#343.5): each table with
/// <c>createdon</c> (the audit table excepted) and without the column gets it (<see cref="SystemAttributes.OverriddenCreatedOn"/>,
/// a nullable column). The rows are not touched: their column stays null, as for a record created without it.
/// </summary>
/// <remarks>
/// One metadata session per table, so a table is either upgraded or not at all. Idempotent: an upgraded table has the column and is
/// skipped, so a second run changes nothing.
/// </remarks>
public static class OverriddenCreatedOnUpgrade
{
    /// <summary>Runs the upgrade.</summary>
    /// <param name="store">The organization's metadata store (loaded, seeded).</param>
    /// <param name="ct">Cancels the upgrade between tables.</param>
    /// <param name="logger">Receives one line per upgraded table.</param>
    /// <exception cref="NotSupportedException"><paramref name="store"/> is not the server's <see cref="MetadataStore"/>.</exception>
    /// <exception cref="InvalidOperationException">A table holds an attribute named <c>overriddencreatedon</c> that is not a DateTime.</exception>
    public static async Task EnsureAsync(IMetadataStore store, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var tables = store.GetAllTables().Where(SystemAttributes.NeedsOverriddenCreatedOn)
            .Select(e => e.LogicalName!).Order(StringComparer.Ordinal).ToList();
        if (tables.Count == 0) return;
        if (store is not MetadataStore concrete)
            throw new NotSupportedException("The overriddencreatedon upgrade needs the metadata store of the server.");

        foreach (var table in tables)
        {
            var added = false;
            await concrete.InUpgradeSessionAsync(async session => added = await concrete.EnsureOverriddenCreatedOnAsync(session, table, ct), ct);
            if (added) logger?.LogInformation("Table '{Table}': column 'overriddencreatedon' (Record Created On) added.", table);
        }
    }
}
