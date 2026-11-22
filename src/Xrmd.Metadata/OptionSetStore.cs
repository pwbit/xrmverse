using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Computed;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata;

/// <summary>
/// Global option sets of <see cref="MetadataStore"/>: one <c>meta.optionset</c> row each (the <see cref="OptionSetMetadata"/>
/// JSON), written in a session like the other metadata. Names are unique ignoring case, enforced here because the
/// <c>UNIQUE</c> constraint follows the database collation. A Picklist or MultiSelect attribute bound to a global set
/// (<c>OptionSet.IsGlobal = true</c> and its <c>Name</c>) is stored in <c>meta.attribute</c> with the name only; the cache
/// puts a copy of the set in the attribute, and every change of the set replaces that copy in every bound attribute.
/// </summary>
public sealed partial class MetadataStore
{
    public OptionSetMetadataBase? GetOptionSet(string name) =>
        name is not null && _cache.OptionSets.TryGetValue(name, out var optionSet) ? optionSet : null;

    public IReadOnlyList<OptionSetMetadataBase> GetAllOptionSets() => _cache.AllOptionSets;

    /// <summary>Stable id of a global option set: MD5 of <c>xrmd:optionset:</c> + the lower-cased name.</summary>
    public static Guid OptionSetId(string name) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("xrmd:optionset:" + name.ToLowerInvariant())));

    public async Task CreateOptionSetAsync(DataSession session, OptionSetMetadataBase optionSet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(optionSet);
        var os = MetadataSerializer.Clone(AsPicklistOptionSet(optionSet));
        if (os.Name is null || !OptionSetNamePattern().IsMatch(os.Name))
            throw new XrmdValidationException(
                $"Invalid global option set name '{os.Name}': it must match ^[A-Za-z][A-Za-z0-9_]{{0,127}}$.");
        os.IsGlobal = true;
        os.OptionSetType ??= OptionSetType.Picklist;
        os.MetadataId = OptionSetId(os.Name);
        var options = os.Options ?? [];
        if (options.FirstOrDefault(o => o.Value is null) is not null)
            throw new XrmdValidationException($"Global option set '{os.Name}': every option needs a value.");
        foreach (var option in options) OptionColor.Demand($"Global option set '{os.Name}'", option);
        if (options.GroupBy(o => o.Value).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new XrmdValidationException($"Global option set '{os.Name}': value {duplicate.Key} appears more than once.");

        var pending = await EnterAsync(session, ct);
        if (pending.Working.OptionSets.ContainsKey(os.Name))
            throw new MetadataComponentExistsException($"Global option set '{os.Name}' already exists.");

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        // LOWER on both sides: unique ignoring case whatever the collation (the version-stamp lock serializes writers).
        if (await ScalarAsync<int>(session, "SELECT COUNT(*) FROM meta.optionset WHERE LOWER(name) = LOWER(@name)", ct, ("@name", os.Name)) > 0)
            throw new MetadataComponentExistsException($"Global option set '{os.Name}' already exists.");
        await ExecuteAsync(session,
            "INSERT INTO meta.optionset (metadata_id, name, definition, version_stamp) VALUES (@id, @name, @definition, @stamp)", ct,
            ("@id", os.MetadataId.Value), ("@name", os.Name), ("@definition", MetadataSerializer.SerializeOptionSet(os)), ("@stamp", stamp));

        pending.Working.OptionSets[os.Name] = os;
    }

    /// <summary>
    /// Seed-only path: creates each global option set that does not exist yet, in one session. An existing one is left alone.
    /// </summary>
    internal Task EnsureSeedOptionSetsAsync(IReadOnlyList<OptionSetMetadata> optionSets, CancellationToken ct = default) =>
        InOwnSessionAsync(async session =>
        {
            foreach (var optionSet in optionSets)
            {
                // Ignoring case, as the uniqueness of names: a custom choice named Audit_Action must not stop the startup.
                if (_cache.OptionSets.Keys.Any(n => string.Equals(n, optionSet.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var seeded = MetadataSerializer.Clone(optionSet);
                seeded.IsGlobal = true;
                await CreateOptionSetAsync(session, seeded, ct);
            }
        }, ct);

    public Task UpdateOptionSetAsync(DataSession session, OptionSetMetadataBase changes, CancellationToken ct) =>
        UpdateOptionSetAsync(session, changes, replaceLabels: false, ct);

    public async Task UpdateOptionSetAsync(DataSession session, OptionSetMetadataBase changes, bool replaceLabels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var given = AsPicklistOptionSet(changes);

        var pending = await EnterAsync(session, ct);
        var current = FindOptionSet(pending.Working, given.Name);
        var updated = MetadataSerializer.Clone(current);
        if (Applies(given.DisplayName, replaceLabels)) updated.DisplayName = UpdatedLabel(given.DisplayName!);
        if (Applies(given.Description, replaceLabels)) updated.Description = UpdatedLabel(given.Description!);
        MergeOptions($"Global option set '{current.Name}'", updated, given.Options ?? [], replaceLabels);

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session, "UPDATE meta.optionset SET definition = @definition, version_stamp = @stamp WHERE metadata_id = @id", ct,
            ("@definition", MetadataSerializer.SerializeOptionSet(updated)), ("@stamp", stamp), ("@id", updated.MetadataId!.Value));

        var cache = pending.Working;
        cache.OptionSets[updated.Name] = updated;
        // Copy-on-write of every entity with an attribute bound to the set: the published cache keeps its instances.
        foreach (var entity in cache.ByLogicalName.Values.Where(e => BoundAttributes(e, updated.Name).Any()).ToList())
        {
            var copy = MetadataSerializer.Clone(entity);
            foreach (var attribute in BoundAttributes(copy, updated.Name)) attribute.OptionSet = MetadataSerializer.Clone(updated);
            cache.Put(copy, previous: entity);
        }
    }

    public async Task SetOptionSetIsManagedAsync(DataSession session, string name, bool? isManaged, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        var current = FindOptionSet(pending.Working, name);
        if (current.IsManaged == isManaged) return;
        var updated = MetadataSerializer.Clone(current);
        SdkProperty.Set(updated, nameof(OptionSetMetadataBase.IsManaged), isManaged);
        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session, "UPDATE meta.optionset SET definition = @definition, version_stamp = @stamp WHERE metadata_id = @id", ct,
            ("@definition", MetadataSerializer.SerializeOptionSet(updated)), ("@stamp", stamp), ("@id", updated.MetadataId!.Value));

        var cache = pending.Working;
        cache.OptionSets[updated.Name] = updated;
        // Copy-on-write of every entity with an attribute bound to the set, as an update of the set does.
        foreach (var entity in cache.ByLogicalName.Values.Where(e => BoundAttributes(e, updated.Name).Any()).ToList())
        {
            var copy = MetadataSerializer.Clone(entity);
            foreach (var attribute in BoundAttributes(copy, updated.Name)) attribute.OptionSet = MetadataSerializer.Clone(updated);
            cache.Put(copy, previous: entity);
        }
    }

    public async Task DeleteOptionSetAsync(DataSession session, string name, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        var current = FindOptionSet(pending.Working, name);
        var referencing = pending.Working.ByLogicalName.Values
            .SelectMany(e => BoundAttributes(e, current.Name).Select(a => $"{e.LogicalName}.{a.LogicalName}"))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (referencing is not null)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The component cannot be deleted because it is referenced by attribute '{referencing}'.", 400);

        await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session, "DELETE FROM meta.optionset WHERE metadata_id = @id", ct, ("@id", current.MetadataId!.Value));
        pending.Working.OptionSets.Remove(current.Name);
        ForgetOptionSet(pending.Working, current.Name);
    }

    /// <summary>Reads <c>meta.optionset</c> into <paramref name="state"/>.</summary>
    private static async Task LoadOptionSetsAsync(DbConnection conn, CacheState state, CancellationToken ct)
    {
        await using var cmd = conn.Command("SELECT definition, unmodelled_xml FROM meta.optionset");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            if (MetadataSerializer.DeserializeOptionSet(rd.GetString(0)) is not OptionSetMetadata { Name: not null } os) continue;
            state.OptionSets[os.Name] = os;
            if (!rd.IsDBNull(1)) state.Unmodelled[UnmodelledKey(OptionSetComponent, os.Name)] = rd.GetString(1);
        }
    }

    /// <summary>
    /// When <paramref name="attribute"/> is bound to a global option set, puts a copy of the set of <paramref name="cache"/>
    /// in it (the options the caller may have given with the binding are ignored).
    /// </summary>
    /// <param name="required">Throw when the set does not exist (writes); loading leaves such a binding as it is.</param>
    /// <exception cref="XrmdValidationException">A binding without name, or to an unknown set when required.</exception>
    private static void ResolveGlobalOptionSet(CacheState cache, AttributeMetadata attribute, bool required)
    {
        if (attribute is not (PicklistAttributeMetadata or MultiSelectPicklistAttributeMetadata)
            || attribute is not EnumAttributeMetadata { OptionSet: { IsGlobal: true } binding } bound)
            return;
        if (string.IsNullOrEmpty(binding.Name))
        {
            if (!required) return;
            throw new XrmdValidationException(
                $"Attribute '{attribute.LogicalName}': a global option set (IsGlobal = true) is named by OptionSet.Name.");
        }
        if (cache.OptionSets.TryGetValue(binding.Name, out var global))
            bound.OptionSet = MetadataSerializer.Clone(global);
        else if (required)
            throw new XrmdValidationException(
                $"Attribute '{attribute.LogicalName}' uses global option set '{binding.Name}', which does not exist.");
    }

    /// <summary>The <c>meta.attribute</c> JSON: an attribute bound to a global option set keeps only the set's name.</summary>
    private static string StoredAttributeJson(AttributeMetadata attribute, int? imageOrdinal = null)
    {
        attribute = ComputedColumnRules.WithoutMask(attribute); // SourceTypeMask is computed on read (ruling K12), never stored
        var json = BoundOptionSetName(attribute) is not { } name
            ? MetadataSerializer.SerializeAttribute(attribute)
            : MetadataSerializer.SerializeAttribute(WithGlobalBinding((EnumAttributeMetadata)MetadataSerializer.Clone(attribute), name));
        if (imageOrdinal is not { } ordinal) return json;
        // xrmverse-only key: the creation order of image columns, read back on load; never part of the SDK/Web API metadata.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node[ImageOrdinalKey] = ordinal;
        return node.ToJsonString();
    }

    private static EnumAttributeMetadata WithGlobalBinding(EnumAttributeMetadata stored, string name)
    {
        stored.OptionSet = new OptionSetMetadata { IsGlobal = true, Name = name };
        return stored;
    }

    private const string ImageOrdinalKey = "xrmverse_imageordinal";

    /// <summary>The image creation ordinal in a stored attribute definition; null when there is none.</summary>
    private static int? StoredImageOrdinal(string json) =>
        json.Contains(ImageOrdinalKey, StringComparison.Ordinal)
        && System.Text.Json.Nodes.JsonNode.Parse(json)![ImageOrdinalKey] is { } value ? (int)value : null;

    /// <summary>The name of the global option set a Picklist or MultiSelect attribute is bound to; null otherwise.</summary>
    private static string? BoundOptionSetName(AttributeMetadata attribute) =>
        attribute is PicklistAttributeMetadata or MultiSelectPicklistAttributeMetadata
        && attribute is EnumAttributeMetadata { OptionSet: { IsGlobal: true, Name: { Length: > 0 } name } }
            ? name
            : null;

    private static IEnumerable<EnumAttributeMetadata> BoundAttributes(EntityMetadata entity, string optionSetName) =>
        (entity.Attributes ?? []).OfType<EnumAttributeMetadata>()
        .Where(a => string.Equals(BoundOptionSetName(a), optionSetName, StringComparison.OrdinalIgnoreCase));

    /// <exception cref="XrmdValidationException">No global option set has this name.</exception>
    private static OptionSetMetadata FindOptionSet(CacheState cache, string? name) =>
        name is not null && cache.OptionSets.TryGetValue(name, out var optionSet)
            ? optionSet
            : throw new XrmdValidationException($"Global option set '{name}' does not exist.");

    /// <exception cref="XrmdValidationException">Another kind of option set (only Picklist global option sets are kept).</exception>
    private static OptionSetMetadata AsPicklistOptionSet(OptionSetMetadataBase optionSet) =>
        optionSet as OptionSetMetadata
        ?? throw new XrmdValidationException(
            $"Global option set '{optionSet.Name}' is a {optionSet.GetType().Name}: only OptionSetMetadata global option sets are supported.");

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionSetNamePattern();
}
