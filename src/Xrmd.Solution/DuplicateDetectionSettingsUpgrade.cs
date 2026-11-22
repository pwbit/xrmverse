using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization that imported tables before <c>IsDuplicateDetectionEnabled</c> was modelled (plan 2.15,
/// ruling D18): the element <c>IsDuplicateCheckSupported</c> is turned into a marker in the kept XML of each table (the export
/// writes it from the metadata now), and a table whose kept value was <c>1</c> gets the flag in its metadata, so that a read and
/// an export show the same value as before.
/// </summary>
/// <remarks>
/// A kept <c>1</c> on a table that cannot have the flag (<see cref="DuplicateDetectionSchema.CanBeEnabled"/>) is reported and
/// not applied. Idempotent: only a kept element that is not a marker is touched, so a second run changes nothing.
/// </remarks>
public static class DuplicateDetectionSettingsUpgrade
{
    private const string Element = "IsDuplicateCheckSupported";

    /// <summary>Runs the upgrade; does nothing when no kept XML carries the element.</summary>
    /// <param name="store">The organization's metadata store (loaded, seeded).</param>
    /// <param name="logger">Receives a warning for each kept <c>1</c> that is not applied.</param>
    /// <exception cref="NotSupportedException"><paramref name="store"/> is not the server's <see cref="MetadataStore"/>.</exception>
    /// <exception cref="InvalidOperationException">A kept value is not a flag: it is not dropped silently.</exception>
    public static async Task EnsureAsync(IMetadataStore store, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store is not MetadataStore concrete)
            throw new NotSupportedException("The duplicate detection settings upgrade needs the metadata store of the server.");

        var kept = new List<(int Type, string Key, string Xml)>();
        var enable = new List<string>();
        foreach (var entity in store.GetAllEntities())
        {
            var (type, key) = UnmodelledXml.EntityKey(entity.LogicalName);
            if (SolutionExporter.Kept(store.GetUnmodelledXml(type, key), "entity", $"entity '{key}'") is not { } element) continue;
            var taken = UnmodelledXml.TakeModelled(element, Element);
            if (taken.Count == 0) continue;
            kept.Add((type, key, element.ToString(SaveOptions.DisableFormatting)));
            if (!Flag(taken[0].Value, entity.LogicalName) || entity.IsDuplicateDetectionEnabled?.Value == true) continue;
            if (DuplicateDetectionSchema.CanBeEnabled(entity)) enable.Add(entity.LogicalName);
            else
                logger?.LogWarning("{Element} of '{Entity}' is 1 in its kept XML, but the table cannot have duplicate detection: not applied.",
                    Element, entity.LogicalName);
        }
        if (kept.Count == 0) return;

        await concrete.InUpgradeSessionAsync(async session =>
        {
            foreach (var name in enable)
                await store.UpdateEntityAsync(session,
                    new EntityMetadata { LogicalName = name, IsDuplicateDetectionEnabled = new BooleanManagedProperty(true) }, ct);
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
