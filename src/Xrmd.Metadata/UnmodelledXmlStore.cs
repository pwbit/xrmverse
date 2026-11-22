using Xrmd.Abstractions;

namespace Xrmd.Metadata;

/// <summary>
/// The unmodelled XML of <see cref="MetadataStore"/>: what a solution file gives a table, a column, a relationship and a
/// global option set that xrmverse does not model, kept opaque in the <c>unmodelled_xml</c> column of its <c>meta.*</c> row
/// for the export; it goes when the component is deleted.
/// </summary>
/// <remarks>
/// The keys (<see cref="IMetadataStore.GetUnmodelledXml"/>): 1 an entity by logical name, 2 an attribute as
/// <c>entity.attribute</c>, 9 a global option set by name, 10 a saved relationship by SchemaName (names ignoring case).
/// </remarks>
public sealed partial class MetadataStore
{
    private const int EntityComponent = 1;
    private const int AttributeComponent = 2;
    private const int OptionSetComponent = 9;
    private const int RelationshipComponent = 10;

    public string? GetUnmodelledXml(int componentType, string key) =>
        _cache.Unmodelled.GetValueOrDefault(UnmodelledKey(componentType, key));

    public string? GetUnmodelledXml(DataSession session, int componentType, string key)
    {
        ArgumentNullException.ThrowIfNull(session);
        // The session's working state when it has written metadata (it holds the write lock), the published cache otherwise:
        // a read takes no lock.
        var cache = _pending.TryGetValue(session, out var pending) ? pending.Working : _cache;
        return cache.Unmodelled.GetValueOrDefault(UnmodelledKey(componentType, key));
    }

    public async Task<bool> SetUnmodelledXmlAsync(DataSession session, int componentType, string key, string? xml, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var cacheKey = UnmodelledKey(componentType, key);
        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;

        (string Sql, (string, object)[] Parameters)? update = null;
        switch (componentType)
        {
            case EntityComponent when cache.ByLogicalName.ContainsKey(key):
                update = ("UPDATE meta.entity SET unmodelled_xml = @xml, version_stamp = @stamp WHERE logical_name = @name", [("@name", key)]);
                break;
            case AttributeComponent:
                var dot = key.IndexOf('.', StringComparison.Ordinal);
                if (dot > 0 && cache.ByLogicalName.GetValueOrDefault(key[..dot]) is { } entity
                    && (entity.Attributes ?? []).Any(a => a.LogicalName == key[(dot + 1)..]))
                    update = ("UPDATE meta.attribute SET unmodelled_xml = @xml, version_stamp = @stamp " +
                              "WHERE entity_logical_name = @entity AND logical_name = @name",
                        [("@entity", key[..dot]), ("@name", key[(dot + 1)..])]);
                break;
            case OptionSetComponent when cache.OptionSets.GetValueOrDefault(key) is { MetadataId: { } optionSetId }:
                update = ("UPDATE meta.optionset SET unmodelled_xml = @xml, version_stamp = @stamp WHERE metadata_id = @id", [("@id", optionSetId)]);
                break;
            case RelationshipComponent:
                Guid? relationshipId = cache.ManyToMany.TryGetValue(key, out var manyToMany) ? manyToMany.MetadataId
                    : cache.SavedOneToMany.TryGetValue(key, out var oneToMany) ? oneToMany.MetadataId
                    : null;
                if (relationshipId is { } id)
                    update = ("UPDATE meta.relationship SET unmodelled_xml = @xml, version_stamp = @stamp WHERE metadata_id = @id", [("@id", id)]);
                break;
            case EntityComponent or OptionSetComponent:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(componentType), componentType, "No unmodelled XML is kept for this component type.");
        }
        if (update is not { } command) return false;
        if (cache.Unmodelled.GetValueOrDefault(cacheKey) == xml) return true;

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session, command.Sql, ct, [("@xml", (object?)xml ?? DBNull.Value), ("@stamp", stamp), .. command.Parameters]);
        if (xml is null) cache.Unmodelled.Remove(cacheKey);
        else cache.Unmodelled[cacheKey] = xml;
        return true;
    }

    /// <summary>The cache key: option set and relationship names ignore case.</summary>
    private static (int, string) UnmodelledKey(int componentType, string key) =>
        componentType is OptionSetComponent or RelationshipComponent ? (componentType, key.ToLowerInvariant()) : (componentType, key);

    /// <summary>Forgets the unmodelled XML of the attributes <paramref name="names"/> of <paramref name="entity"/>.</summary>
    private static void ForgetAttributes(CacheState cache, string entity, IEnumerable<string> names)
    {
        foreach (var name in names) cache.Unmodelled.Remove((AttributeComponent, $"{entity}.{name}"));
    }

    /// <summary>Forgets the unmodelled XML of the entity <paramref name="entity"/> and of its attributes.</summary>
    private static void ForgetEntity(CacheState cache, string entity)
    {
        cache.Unmodelled.Remove((EntityComponent, entity));
        foreach (var key in cache.Unmodelled.Keys.Where(k => k.Item1 == AttributeComponent && k.Item2.StartsWith(entity + ".", StringComparison.Ordinal)).ToList())
            cache.Unmodelled.Remove(key);
    }

    private static void ForgetRelationship(CacheState cache, string schemaName) =>
        cache.Unmodelled.Remove(UnmodelledKey(RelationshipComponent, schemaName));

    private static void ForgetOptionSet(CacheState cache, string name) =>
        cache.Unmodelled.Remove(UnmodelledKey(OptionSetComponent, name));
}
