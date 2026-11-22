using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization made before <c>IsAuditEnabled</c> was modelled: gives every table and column that has no
/// setting its value in the metadata, and turns the elements of the kept XML that carried it into markers, so that a read and an
/// export show the same values as before.
/// </summary>
/// <remarks>
/// <para>A table or column whose kept XML has <c>IsAuditEnabled</c> (and, for a table, <c>CanModifyAuditSettings</c>) takes that
/// value; every other one takes the default of its kind (<see cref="AuditDefaults"/>). A many-to-many's intersect table is off and
/// fixed whatever its kept XML says, as a new one is. Whether a column can change is the
/// rule's (<see cref="AuditDefaults.ForAttribute"/>), not the file's: a file has no element for it.</para>
/// <para>Idempotent: only a component without a setting is touched, so a second run changes nothing.</para>
/// </remarks>
public static class AuditSettingsUpgrade
{
    private static readonly string[] EntityElements = ["IsAuditEnabled", "CanModifyAuditSettings"];
    private static readonly string[] AttributeElements = ["IsAuditEnabled"];

    /// <summary>Runs the upgrade; does nothing when every table and column has its setting.</summary>
    /// <param name="store">The organization's metadata store (loaded).</param>
    /// <param name="factory">The organization's connection factory (kept for the signature; the store opens its own session).</param>
    /// <param name="logger">Receives a warning for each fixed column whose kept value differs from the fixed one.</param>
    /// <exception cref="NotSupportedException"><paramref name="store"/> is not the server's <see cref="MetadataStore"/>.</exception>
    public static async Task EnsureAsync(IMetadataStore store, IDbConnectionFactory factory, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        if (store is not MetadataStore concrete)
            throw new NotSupportedException("The audit settings upgrade needs the metadata store of the server.");

        var settings = new List<(string Entity, string? Attribute, BooleanManagedProperty Setting)>();
        var kept = new List<(int Type, string Key, string? Xml)>();
        foreach (var entity in store.GetAllEntities())
        {
            if (entity.IsAuditEnabled is null)
            {
                var (type, key) = UnmodelledXml.EntityKey(entity.LogicalName);
                var taken = Take(store, type, key, "entity", EntityElements, kept);
                var fallback = AuditDefaults.ForEntity(entity.LogicalName, MetadataStore.IsSystemEntity(entity));
                // A many-to-many's intersect table is off and fixed, as the metadata store makes a new one.
                settings.Add((entity.LogicalName, null, entity.IsIntersect == true
                    ? AuditDefaults.Managed(false, canBeChanged: false)
                    : AuditDefaults.Managed(
                        Flag(taken, "IsAuditEnabled", entity.LogicalName) ?? fallback.Value,
                        Flag(taken, "CanModifyAuditSettings", entity.LogicalName) ?? fallback.CanBeChanged)));
            }
            foreach (var attribute in (entity.Attributes ?? []).Where(a => a.IsAuditEnabled is null))
            {
                var (type, key) = UnmodelledXml.AttributeKey(entity.LogicalName, attribute.LogicalName);
                var taken = Take(store, type, key, "attribute", AttributeElements, kept);
                var rule = AuditDefaults.ForAttribute(attribute, entity.PrimaryIdAttribute);
                var keptValue = Flag(taken, "IsAuditEnabled", $"{entity.LogicalName}.{attribute.LogicalName}");
                // Like an import: a column that cannot change has the fixed value; the kept one is reported, not applied.
                if (keptValue is { } value && !rule.CanBeChanged && value != rule.Value)
                    logger?.LogWarning("IsAuditEnabled of '{Entity}.{Attribute}' is {Fixed} in xrmverse and cannot change: the kept {Kept} is not applied.",
                        entity.LogicalName, attribute.LogicalName, rule.Value, value);
                settings.Add((entity.LogicalName, attribute.LogicalName,
                    AuditDefaults.Managed(rule.CanBeChanged ? keptValue ?? rule.Value : rule.Value, rule.CanBeChanged)));
            }
        }
        if (settings.Count == 0) return;

        await concrete.InUpgradeSessionAsync(async session =>
        {
            await concrete.SetAuditSettingsAsync(session, settings, ct);
            foreach (var (type, key, xml) in kept) await store.SetUnmodelledXmlAsync(session, type, key, xml, ct);
        }, ct);
    }

    /// <summary>
    /// The values the kept XML of a component holds for <paramref name="names"/>; the kept XML without them (each a marker now) is
    /// added to <paramref name="changed"/> when it changed.
    /// </summary>
    private static List<(string Name, string Value)> Take(IMetadataStore store, int type, string key, string name, string[] names,
        List<(int, string, string?)> changed)
    {
        if (SolutionExporter.Kept(store.GetUnmodelledXml(type, key), name, $"{name} '{key}'") is not { } element) return [];
        var taken = UnmodelledXml.TakeModelled(element, names);
        if (taken.Count > 0) changed.Add((type, key, element.ToString(SaveOptions.DisableFormatting)));
        return taken;
    }

    /// <exception cref="InvalidOperationException">The kept value is not a flag: it is not dropped silently.</exception>
    private static bool? Flag(List<(string Name, string Value)> taken, string name, string component)
    {
        if (taken.FirstOrDefault(t => t.Name == name) is not { Name: not null } found) return null;
        return found.Value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new InvalidOperationException($"The kept XML of '{component}' gives {name} the value '{found.Value}', which is not a flag (1 or 0)."),
        };
    }
}
