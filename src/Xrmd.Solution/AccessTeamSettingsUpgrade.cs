using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization that imported tables before <c>AutoCreateAccessTeams</c> was modelled (plan 2.16): the
/// element is turned into a marker in the kept XML of each table (the export writes it from the metadata now, and refuses kept XML
/// that carries it), and a table whose kept value was <c>1</c> gets the setting in its metadata, so that a read and an export
/// show the same value as before.
/// </summary>
/// <remarks>
/// A kept <c>1</c> that the table cannot have (a table not owned by a user or a team, or one beyond
/// <see cref="AccessTeamSchema.MaxTables"/>) is reported and not applied. Idempotent: only a kept element that is not a marker
/// is touched, so a second run changes nothing.
/// </remarks>
public static class AccessTeamSettingsUpgrade
{
    private const string Element = "AutoCreateAccessTeams";

    /// <summary>Runs the upgrade; does nothing when no kept XML carries the element.</summary>
    /// <param name="store">The organization's metadata store (loaded, seeded).</param>
    /// <param name="logger">Receives a warning for each kept <c>1</c> that is not applied.</param>
    /// <exception cref="NotSupportedException"><paramref name="store"/> is not the server's <see cref="MetadataStore"/>.</exception>
    /// <exception cref="InvalidOperationException">A kept value is not a flag: it is not dropped silently.</exception>
    public static async Task EnsureAsync(IMetadataStore store, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store is not MetadataStore concrete)
            throw new NotSupportedException("The access team settings upgrade needs the metadata store of the server.");

        var kept = new List<(int Type, string Key, string Xml)>();
        var enable = new List<string>();
        var enabled = store.AccessTeamTables.Count;
        foreach (var entity in store.GetAllEntities())
        {
            var (type, key) = UnmodelledXml.EntityKey(entity.LogicalName);
            if (SolutionExporter.Kept(store.GetUnmodelledXml(type, key), "entity", $"entity '{key}'") is not { } element) continue;
            var taken = UnmodelledXml.TakeModelled(element, Element);
            if (taken.Count == 0) continue;
            kept.Add((type, key, element.ToString(SaveOptions.DisableFormatting)));
            if (!Flag(taken[0].Value, entity.LogicalName) || entity.AutoCreateAccessTeams == true) continue;
            if (!AccessTeamSchema.AllowsAccessTeams(entity.OwnershipType) || entity.IsIntersect == true)
                logger?.LogWarning("{Element} of '{Entity}' is 1 in its kept XML, but only a table owned by a user or a team has access teams: not applied.",
                    Element, entity.LogicalName);
            else if (enabled >= AccessTeamSchema.MaxTables)
                logger?.LogWarning("{Element} of '{Entity}' is 1 in its kept XML, but {Max} tables already have it: not applied.",
                    Element, entity.LogicalName, AccessTeamSchema.MaxTables);
            else
            {
                enable.Add(entity.LogicalName);
                enabled++;
            }
        }
        if (kept.Count == 0) return;

        await concrete.InUpgradeSessionAsync(async session =>
        {
            foreach (var name in enable)
                await store.UpdateEntityAsync(session, new EntityMetadata { LogicalName = name, AutoCreateAccessTeams = true }, ct);
            foreach (var (type, key, xml) in kept) await store.SetUnmodelledXmlAsync(session, type, key, xml, ct);
        }, ct);
    }

    private static bool Flag(string value, string entity) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" or "" => false,
        _ => throw new InvalidOperationException($"The kept XML of '{entity}' gives {Element} the value '{value}', which is not a flag (1 or 0)."),
    };
}
