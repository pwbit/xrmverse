using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization made before plan 2.11 (ruling A20): the elements <c>HasRelatedActivities</c> and
/// <c>HasRelatedNotes</c> (and <c>ActivityTypeMask</c>) of the kept XML of each table become markers (the metadata holds the settings now), and a table whose kept
/// value was <c>1</c> gets <c>HasActivities</c> or <c>HasNotes</c> in its metadata, so that a read shows the same value as before.
/// Then every table with the settings (the seeded <c>account</c> and <c>contact</c>, the activity tables' notes, the tables just
/// upgraded) gets its targets on <c>regardingobjectid</c> and <c>annotation.objectid</c> with their relationships, and every
/// activity table its target on <c>activityparty.activityid</c>. Runs after <see cref="SystemSeed"/>.
/// </summary>
/// <remarks>
/// A kept <c>1</c> that the table cannot have (the rule of the update, <c>MetadataStore.ActivitySettingRefusal</c>: for example
/// <c>HasActivities</c> on an activity, an intersect table or <c>annotation</c>) is reported and not applied; a kept <c>0</c> on a table that has the setting keeps the setting (it is one-way).
/// Idempotent: only a kept element that is not a marker is touched and the targets are written only when missing, so a second run
/// writes nothing (the version stamp does not move).
/// </remarks>
public static class ActivitySettingsUpgrade
{
    private const string Activities = "HasRelatedActivities";
    private const string Notes = "HasRelatedNotes";
    private const string TypeMask = "ActivityTypeMask";

    /// <summary>Runs the upgrade.</summary>
    /// <param name="store">The organization's metadata store (loaded, seeded).</param>
    /// <param name="factory">The organization's database (the signature of the other startup upgrades; the store writes through its own sessions).</param>
    /// <param name="logger">Receives a warning for each kept value that is not applied.</param>
    /// <exception cref="NotSupportedException"><paramref name="store"/> is not the server's <see cref="MetadataStore"/>.</exception>
    /// <exception cref="InvalidOperationException">A kept value is not a flag: it is not dropped silently.</exception>
    public static async Task EnsureAsync(IMetadataStore store, IDbConnectionFactory factory, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        if (store is not MetadataStore concrete)
            throw new NotSupportedException("The activity settings upgrade needs the metadata store of the server.");

        var kept = new List<(int Type, string Key, string Xml)>();
        var changes = new List<EntityMetadata>();
        foreach (var entity in store.GetAllEntities())
        {
            var (type, key) = UnmodelledXml.EntityKey(entity.LogicalName);
            if (SolutionExporter.Kept(store.GetUnmodelledXml(type, key), "entity", $"entity '{key}'") is not { } element) continue;
            // ActivityTypeMask is written from the metadata too (plan 2.11 task 7): its kept value is dropped. A custom table imported
            // before could not be an activity (its kept value is empty); a standard activity (email, ...) a file carried keeps the
            // value of its metadata, and a kept value that differs from it is reported.
            var taken = UnmodelledXml.TakeModelled(element, Activities, Notes, TypeMask);
            if (taken.FirstOrDefault(t => t.Name == TypeMask) is { Name: not null, Value: var keptMask } && keptMask.Trim().Length > 0
                && keptMask.Trim() != (entity.IsActivity == true ? entity.ActivityTypeMask?.ToString(System.Globalization.CultureInfo.InvariantCulture) : null))
                logger?.LogWarning("{Element} of '{Entity}' is '{Kept}' in its kept XML; it is not kept: the export writes the table's own value ('{Current}').",
                    TypeMask, entity.LogicalName, keptMask.Trim(), entity.IsActivity == true ? entity.ActivityTypeMask?.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            if (taken.Count == 0) continue;
            kept.Add((type, key, element.ToString(SaveOptions.DisableFormatting)));
            var change = new EntityMetadata { LogicalName = entity.LogicalName };
            foreach (var (name, value) in taken.Where(t => t.Name != TypeMask))
            {
                var on = Flag(value, name, entity.LogicalName);
                var current = name == Activities ? entity.HasActivities == true : entity.HasNotes == true;
                if (on == current) continue;
                if (!on)
                {
                    logger?.LogWarning("{Element} of '{Entity}' is 0 in its kept XML, but the table has the setting, which stays on.", name, entity.LogicalName);
                    continue;
                }
                // The rule the update applies (A5), so a kept flag the table cannot have is reported, never a failed start.
                if (concrete.ActivitySettingRefusal(entity, name == Activities, name == Notes) is { } refusal)
                    logger?.LogWarning("{Element} of '{Entity}' is 1 in its kept XML, but it is not applied: {Refusal}", name, entity.LogicalName, refusal);
                else if (name == Activities) change.HasActivities = true;
                else change.HasNotes = true;
            }
            if (change.HasActivities is not null || change.HasNotes is not null) changes.Add(change);
        }

        await concrete.InUpgradeSessionAsync(async session =>
        {
            foreach (var change in changes) await store.UpdateEntityAsync(session, change, ct);
            foreach (var (type, key, xml) in kept) await store.SetUnmodelledXmlAsync(session, type, key, xml, ct);
            await concrete.EnsureActivityLinksAsync(session, ct);
        }, ct);
    }

    private static bool Flag(string value, string element, string entity) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" or "" => false,
        _ => throw new InvalidOperationException($"The kept XML of '{entity}' gives {element} the value '{value}', which is not a flag (1 or 0)."),
    };
}
