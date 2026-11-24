using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>Helpers over tables and their relationships (the implied one-to-many relationships of their lookups, navigation names).</summary>
public static partial class EntityMetadataExtensions
{
    /// <summary>Sets the columns of <paramref name="entity"/> (a new list: the table never shares the caller's).</summary>
    public static void SetAttributes(this TableDefinition entity, IEnumerable<ColumnDefinition> attributes)
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity.Attributes = attributes.ToList();
    }

    /// <summary>
    /// Sets the relationships of <paramref name="entity"/>: copies of the one-to-many relationships of
    /// <paramref name="oneToMany"/> (every one, as <see cref="OneToManyRelationships"/> gives them) it is the referencing
    /// entity of (<c>ManyToOneRelationships</c>) or the referenced entity of (<c>OneToManyRelationships</c>), and of the
    /// saved many-to-many relationships of <paramref name="manyToMany"/> it is a side of.
    /// </summary>
    public static void SetRelationships(this TableDefinition entity, IEnumerable<OneToManyRelationship> oneToMany,
        IEnumerable<ManyToManyRelationship> manyToMany)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var all = oneToMany.ToList();
        entity.ManyToOneRelationships = all.Where(r => r.ReferencingEntity == entity.LogicalName).Select(MetadataJson.Clone).ToList();
        entity.OneToManyRelationships = all.Where(r => r.ReferencedEntity == entity.LogicalName).Select(MetadataJson.Clone).ToList();
        entity.ManyToManyRelationships = manyToMany
            .Where(r => r.Entity1LogicalName == entity.LogicalName || r.Entity2LogicalName == entity.LogicalName)
            .Select(MetadataJson.Clone)
            .ToList();
    }

    /// <summary>
    /// The referencing navigation property of the relationship of <paramref name="lookup"/>: the
    /// <c>ReferencingEntityNavigationPropertyName</c> of its relationship in <paramref name="relationships"/> (as
    /// <see cref="OneToManyRelationships"/> gives them: a saved relationship's own name, else the default), and without a
    /// relationship the default, which Dataverse sets to the lookup's <c>SchemaName</c> for a custom lookup
    /// (<c>new_ParentId</c>); a system lookup (<c>ownerid</c>, <c>createdby</c>, ...) and a lookup without a SchemaName keep the
    /// logical name.
    /// </summary>
    public static string ReferencingNavigationName(TableDefinition entity, LookupColumn lookup,
        IEnumerable<OneToManyRelationship>? relationships = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(lookup);
        var named = relationships?.FirstOrDefault(r => r.ReferencingEntity == entity.LogicalName && r.ReferencingAttribute == lookup.LogicalName
                                                       && !string.IsNullOrEmpty(r.ReferencingEntityNavigationPropertyName));
        return named?.ReferencingEntityNavigationPropertyName ?? DefaultNavigationName(entity, lookup);
    }

    private static string DefaultNavigationName(TableDefinition entity, LookupColumn lookup) =>
        lookup.SchemaName is { Length: > 0 } schemaName && MetadataStore.IsCustomAttribute(entity, lookup) ? schemaName : lookup.LogicalName!;

    /// <summary>
    /// The referencing navigation properties of a Customer lookup (and of a lookup of the activity model, plan 2.11), one per target: the
    /// <c>ReferencingEntityNavigationPropertyName</c> of the target's relationship in <paramref name="relationships"/>, else
    /// <see cref="CustomerColumns.DefaultNavigation"/> (<c>parentcustomerid_account</c>).
    /// </summary>
    public static IEnumerable<(string Name, string Target)> CustomerNavigations(TableDefinition entity, LookupColumn lookup,
        IEnumerable<OneToManyRelationship>? relationships = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(lookup);
        var all = relationships?.ToList() ?? [];
        foreach (var target in lookup.Targets ?? [])
        {
            var named = all.FirstOrDefault(r => r.ReferencingEntity == entity.LogicalName && r.ReferencingAttribute == lookup.LogicalName
                                                && r.ReferencedEntity == target && !string.IsNullOrEmpty(r.ReferencingEntityNavigationPropertyName));
            yield return (named?.ReferencingEntityNavigationPropertyName ?? CustomerColumns.DefaultNavigation(lookup.LogicalName!, target), target);
        }
    }

    /// <summary>
    /// The one-to-many relationship named <paramref name="schemaName"/> (case-insensitive) among those of
    /// <see cref="OneToManyRelationships"/>; null when there is none.
    /// </summary>
    public static OneToManyRelationship? FindLookupRelationship(IEnumerable<TableDefinition> all, string? schemaName,
        IEnumerable<OneToManyRelationship>? saved = null)
    {
        if (string.IsNullOrEmpty(schemaName)) return null;
        return OneToManyRelationships(all, saved)
            .FirstOrDefault(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The one-to-many relationships implied by the lookups of <paramref name="all"/>, one per lookup target (a polymorphic
    /// lookup gives one per target), named <c>{referencing}_{lookup}_{target}</c> unless <paramref name="saved"/> holds a
    /// relationship for the same referencing entity, attribute and referenced entity (created by CreateOneToMany): then
    /// that one's SchemaName, MetadataId and other properties (its own navigation property name too) are kept. The referencing
    /// navigation property defaults to the lookup's <c>SchemaName</c> for a custom lookup (its logical name for a system one),
    /// as in the Web API's $metadata (<see cref="ReferencingNavigationName"/>); the referenced one defaults to the SchemaName. The lookups
    /// of intersect entities and the party lists (plan 2.11) imply no relationship.
    /// </summary>
    public static IEnumerable<OneToManyRelationship> OneToManyRelationships(IEnumerable<TableDefinition> all,
        IEnumerable<OneToManyRelationship>? saved = null)
    {
        var entities = all.ToDictionary(e => e.LogicalName!, StringComparer.Ordinal);
        var savedByLookup = (saved ?? [])
            .Where(r => r.ReferencingEntity is not null && r.ReferencingAttribute is not null && r.ReferencedEntity is not null)
            .ToDictionary(r => (r.ReferencingEntity, r.ReferencingAttribute, r.ReferencedEntity));
        foreach (var referencing in entities.Values)
        {
            if (referencing.IsIntersect == true) continue;
            foreach (var lookup in (referencing.Attributes ?? []).OfType<LookupColumn>())
            {
                // A party list has no column: its rows are activityparty rows, whose relationships carry it (plan 2.11 A8).
                if (ActivitySchema.IsPartyList(lookup)) continue;
                foreach (var target in lookup.Targets ?? [])
                {
                    if (!entities.TryGetValue(target, out var referenced)) continue;
                    OneToManyRelationship r;
                    if (savedByLookup.TryGetValue((referencing.LogicalName, lookup.LogicalName, target), out var savedOne))
                    {
                        r = MetadataJson.Clone(savedOne);
                    }
                    else
                    {
                        r = new OneToManyRelationship { SchemaName = $"{referencing.LogicalName}_{lookup.LogicalName}_{target}" };
                    }
                    r.ReferencingEntity = referencing.LogicalName;
                    r.ReferencingAttribute = lookup.LogicalName;
                    r.ReferencedEntity = target;
                    r.ReferencedAttribute = referenced.PrimaryIdAttribute;
                    if (string.IsNullOrEmpty(r.ReferencingEntityNavigationPropertyName))
                        // A lookup with one navigation property per target and no saved relationship (post.regardingobjectid,
                        // plan 3.5 UI-1g) is named per target too, as a Customer column.
                        r.ReferencingEntityNavigationPropertyName = CustomerColumns.IsCustomer(lookup)
                                                                    || ActivitySchema.IsManagedLookup(referencing.LogicalName, lookup.LogicalName)
                            ? CustomerColumns.DefaultNavigation(lookup.LogicalName!, target)
                            : DefaultNavigationName(referencing, lookup);
                    r.ReferencedEntityNavigationPropertyName ??= r.SchemaName;
                    yield return r;
                }
            }
        }
    }
}
