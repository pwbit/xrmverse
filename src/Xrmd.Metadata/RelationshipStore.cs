using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata;

/// <summary>
/// Relationships of <see cref="MetadataStore"/>. A many-to-many relationship is one <c>meta.relationship</c> row (the
/// <see cref="ManyToManyRelationshipMetadata"/> JSON) plus its intersect entity, created and deleted with it in the same
/// session: an organization-owned entity with <c>IsIntersect = true</c>, no primary name, the columns
/// <c>&lt;entity1&gt;id</c>/<c>&lt;entity2&gt;id</c> (<c>&lt;entity&gt;idone</c>/<c>&lt;entity&gt;idtwo</c> when both sides
/// are the same entity) and a unique index on the pair. One-to-many relationships are the ones implied by lookups
/// (<see cref="EntityMetadataExtensions.OneToManyRelationships"/>), a polymorphic lookup giving one per target, named
/// <c>{referencing}_{lookup}_{target}</c>; one created by <see cref="CreateOneToManyAsync"/> is also a
/// <c>meta.relationship</c> row (the <see cref="OneToManyRelationshipMetadata"/> JSON) that keeps its SchemaName and
/// MetadataId, deleted with its lookup or either entity. Schema names are unique ignoring case among all kinds.
/// </summary>
public sealed partial class MetadataStore
{
    private const string IdOne = "idone", IdTwo = "idtwo";

    public RelationshipMetadataBase? GetRelationship(string schemaName)
    {
        if (string.IsNullOrEmpty(schemaName)) return null;
        var cache = _cache;
        return cache.ManyToMany.TryGetValue(schemaName, out var manyToMany)
            ? manyToMany
            : cache.AllOneToMany.FirstOrDefault(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ManyToManyRelationshipMetadata> GetManyToManyRelationships() => _cache.AllManyToMany;

    public string? GetRelationshipMenuXml(string schemaName) =>
        !string.IsNullOrEmpty(schemaName) && _cache.RelationshipMenus.TryGetValue(schemaName, out var xml) ? xml : null;

    public async Task SetRelationshipMenuXmlAsync(DataSession session, string schemaName, string? xml, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaName);
        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        Guid? id = cache.ManyToMany.TryGetValue(schemaName, out var manyToMany) ? manyToMany.MetadataId
            : cache.SavedOneToMany.TryGetValue(schemaName, out var oneToMany) ? oneToMany.MetadataId
            : null;
        if (id is null)
            throw new XrmdValidationException(
                $"Relationship '{schemaName}' has no saved definition: its associated menu configuration cannot be kept.");

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session, "UPDATE meta.relationship SET associated_menu_xml = @xml, version_stamp = @stamp WHERE metadata_id = @id", ct,
            ("@xml", (object?)xml ?? DBNull.Value), ("@stamp", stamp), ("@id", id.Value));
        var name = manyToMany?.SchemaName ?? cache.SavedOneToMany[schemaName].SchemaName;
        if (xml is null) cache.RelationshipMenus.Remove(name);
        else cache.RelationshipMenus[name] = xml;
    }

    public IReadOnlyList<OneToManyRelationshipMetadata> GetOneToManyRelationships() => _cache.AllOneToMany;

    public async Task<bool> SetRelationshipIsManagedAsync(DataSession session, string schemaName, bool? isManaged, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaName);
        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        RelationshipMetadataBase? current = cache.ManyToMany.TryGetValue(schemaName, out var manyToMany) ? manyToMany
            : cache.SavedOneToMany.TryGetValue(schemaName, out var oneToMany) ? oneToMany
            : null;
        if (current is null) return false;
        if (current.IsManaged == isManaged) return true;
        var updated = MetadataSerializer.Clone(current);
        SdkProperty.Set(updated, nameof(RelationshipMetadataBase.IsManaged), isManaged);
        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session, "UPDATE meta.relationship SET definition = @definition, version_stamp = @stamp WHERE metadata_id = @id", ct,
            ("@definition", MetadataSerializer.SerializeRelationship(updated)), ("@stamp", stamp), ("@id", updated.MetadataId!.Value));
        switch (updated)
        {
            case ManyToManyRelationshipMetadata m:
                cache.ManyToMany[m.SchemaName] = m;
                break;
            case OneToManyRelationshipMetadata o:
                cache.SavedOneToMany[o.SchemaName] = o;
                break;
        }
        return true;
    }

    public OneToManyRelationshipMetadata? GetHierarchicalRelationship(string entityLogicalName) =>
        entityLogicalName is not null && _cache.Hierarchical.TryGetValue(entityLogicalName, out var r) ? r : null;

    /// <summary>A self-referencing one-to-many relationship marked <c>IsHierarchical</c>.</summary>
    private static bool IsHierarchicalRelationship(OneToManyRelationshipMetadata r) =>
        r.IsHierarchical == true && r.ReferencingEntity is not null && r.ReferencingEntity == r.ReferencedEntity;

    public async Task CreateOneToManyAsync(DataSession session, OneToManyRelationshipMetadata relationship, LookupAttributeMetadata lookup,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentNullException.ThrowIfNull(lookup);
        var r = MetadataSerializer.Clone(relationship);
        var l = MetadataSerializer.Clone(lookup);
        if (r.SchemaName is null || !RelationshipNamePattern().IsMatch(r.SchemaName))
            throw new XrmdValidationException(
                $"Invalid relationship name '{r.SchemaName}': it must match ^[A-Za-z][A-Za-z0-9_]{{0,99}}$.");
        LogicalNameValidator.EnsureValid(r.ReferencedEntity, "entity");
        LogicalNameValidator.EnsureValid(r.ReferencingEntity, "entity");
        LogicalNameValidator.EnsureValid(l.LogicalName, "attribute");
        if (r.ReferencingEntity == FileColumns.AttachmentEntity && l.LogicalName == FileColumns.AttachmentLookup)
            throw AttachmentLookupIsManaged($"Lookup '{r.ReferencingEntity}.{l.LogicalName}'");
        EnsureNotCustomer(r.ReferencingEntity, l);
        l.Targets = [r.ReferencedEntity];
        l.MetadataId ??= Guid.NewGuid();
        r.ReferencingAttribute = l.LogicalName;
        r.MetadataId ??= Guid.NewGuid();
        r.IsCustomRelationship ??= true;

        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        foreach (var side in (string[])[r.ReferencedEntity, r.ReferencingEntity])
        {
            var entity = cache.ByLogicalName.GetValueOrDefault(side)
                         ?? throw new XrmdValidationException($"Entity '{side}' does not exist.");
            if (entity.IsIntersect == true)
                throw new XrmdValidationException($"Entity '{side}' is an intersect entity and cannot take part in a relationship.");
        }
        var referencedEntity = cache.ByLogicalName[r.ReferencedEntity];
        ElasticTables.DemandValidRelationship(r, referencedEntity, cache.ByLogicalName[r.ReferencingEntity]);
        if (cache.ManyToMany.ContainsKey(r.SchemaName) || cache.SavedOneToMany.ContainsKey(r.SchemaName)
            || EntityMetadataExtensions.FindLookupRelationship(cache.ByLogicalName.Values, r.SchemaName, cache.SavedOneToMany.Values) is not null)
            throw new MetadataComponentExistsException($"Relationship '{r.SchemaName}' already exists.");
        if (r.IsHierarchical == true) DemandHierarchicalAllowed(cache, r);

        if (!string.IsNullOrEmpty(r.ReferencingEntityNavigationPropertyName))
        {
            var navigation = r.ReferencingEntityNavigationPropertyName;
            if (!NavigationNamePattern().IsMatch(navigation))
                throw new XrmdValidationException(
                    $"Invalid ReferencingEntityNavigationPropertyName '{navigation}': it must match ^[A-Za-z_][A-Za-z0-9_]{{0,99}}$.");
            // $metadata declares it next to the table's columns and the other lookups' navigation properties.
            var referencingEntity = cache.ByLogicalName[r.ReferencingEntity];
            var taken = (referencingEntity.Attributes ?? []).SelectMany(a => new[] { a.LogicalName, $"_{a.LogicalName}_value" })
                .Concat(EntityMetadataExtensions.OneToManyRelationships(cache.ByLogicalName.Values, cache.SavedOneToMany.Values)
                    .Where(x => x.ReferencingEntity == r.ReferencingEntity).Select(x => x.ReferencingEntityNavigationPropertyName!));
            if (taken.Contains(navigation, StringComparer.Ordinal))
                throw new XrmdValidationException(
                    $"ReferencingEntityNavigationPropertyName '{navigation}' is already a property of entity '{r.ReferencingEntity}'.");
        }

        await AddAttributeAsync(session, r.ReferencingEntity, l, ct);
        // A lookup to an elastic table also holds the partition of the row it points to, in the same transaction (plan 3.4).
        if (ElasticTables.IsElastic(referencedEntity))
            await AddAttributeAsync(session, r.ReferencingEntity, new StringAttributeMetadata
            {
                LogicalName = ElasticTables.PartitionColumnOf(l.LogicalName),
                SchemaName = (l.SchemaName ?? l.LogicalName) + "Pid",
                DisplayName = l.DisplayName?.UserLocalizedLabel is { } label
                    ? SystemAttributes.CreateLabel(label.Label + " Partition Id", label.LanguageCode)
                    : null,
                MaxLength = ElasticTables.PartitionIdMaxLength,
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                MetadataId = Guid.NewGuid(),
            }, ct);
        await SaveOneToManyAsync(session, pending, r, ct);
    }

    /// <summary>
    /// Saves the one-to-many relationship <paramref name="r"/> (its SchemaName and MetadataId set; its lookup exists in
    /// <paramref name="pending"/>'s view): the one place that writes a saved one-to-many row, for a plain lookup, a seeded
    /// table and a Customer column alike.
    /// </summary>
    /// <param name="stamp">The version stamp the session already took for the same write (the currency relationship is written with its
    /// columns); null bumps a new one.</param>
    private async Task SaveOneToManyAsync(DataSession session, PendingChanges pending, OneToManyRelationshipMetadata r, CancellationToken ct,
        long? stamp = null)
    {
        stamp ??= await BumpVersionStampAsync(session, pending, ct);
        if (await ScalarAsync<int>(session, "SELECT COUNT(*) FROM meta.relationship WHERE LOWER(schema_name) = LOWER(@name)", ct,
                ("@name", r.SchemaName)) > 0)
            throw new MetadataComponentExistsException($"Relationship '{r.SchemaName}' already exists.");
        await ExecuteAsync(session,
            "INSERT INTO meta.relationship (metadata_id, schema_name, definition, version_stamp) VALUES (@id, @name, @definition, @stamp)", ct,
            ("@id", r.MetadataId!.Value), ("@name", r.SchemaName), ("@definition", MetadataSerializer.SerializeRelationship(r)),
            ("@stamp", stamp.Value));
        pending.Working.SavedOneToMany[r.SchemaName] = r;
    }

    /// <summary>
    /// Saves the relationship from <c>transactioncurrency</c> to the currency lookup of <paramref name="entityLogicalName"/> (ruling
    /// X4): <see cref="CurrencySchema.RelationshipName"/>, Delete Restrict and every other cascade NoCascade, referencing navigation
    /// property <c>transactioncurrencyid</c> (#183: what Dataverse answers for <c>op365_project</c> and what Learn lists for its
    /// tables), not custom. Called in the session that gives the table its currency pair. Idempotent: a relationship already saved
    /// for the lookup is kept as it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The table has no currency pair.</exception>
    /// <exception cref="MetadataComponentExistsException">Another relationship already has the name.</exception>
    internal async Task EnsureCurrencyRelationshipAsync(DataSession session, string entityLogicalName, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        var entity = cache.ByLogicalName.GetValueOrDefault(entityLogicalName)
                     ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
        if (!CurrencySchema.HasCurrencyColumns(entity))
            throw new InvalidOperationException($"Entity '{entityLogicalName}' has no currency pair: its currency relationship cannot be saved.");
        if (cache.SavedOneToMany.Values.Any(r => r.ReferencingEntity == entityLogicalName && r.ReferencingAttribute == CurrencySchema.CurrencyId
                                                 && r.ReferencedEntity == CurrencySchema.Table))
            return;
        var name = CurrencySchema.RelationshipName(entityLogicalName);
        EnsureCurrencyRelationshipNameFree(cache, entityLogicalName);
        var r = new OneToManyRelationshipMetadata
        {
            SchemaName = name,
            MetadataId = Guid.NewGuid(),
            ReferencedEntity = CurrencySchema.Table,
            ReferencedAttribute = CurrencySchema.CurrencyId,
            ReferencingEntity = entityLogicalName,
            ReferencingAttribute = CurrencySchema.CurrencyId,
            ReferencingEntityNavigationPropertyName = CurrencySchema.CurrencyId,
            ReferencedEntityNavigationPropertyName = name,
            IsCustomRelationship = false,
            IsHierarchical = false,
            CascadeConfiguration = new CascadeConfiguration
            {
                Assign = CascadeType.NoCascade, Delete = CascadeType.Restrict, Merge = CascadeType.NoCascade, Reparent = CascadeType.NoCascade,
                Share = CascadeType.NoCascade, Unshare = CascadeType.NoCascade, Archive = CascadeType.NoCascade, RollupView = CascadeType.NoCascade,
            },
        };
        await SaveOneToManyAsync(session, pending, r, ct, pending.Stamp);
    }

    /// <summary>
    /// Saves <paramref name="relationship"/>, a relationship of a lookup the store maintains (the activity model of plan 2.11), in the
    /// session that gives the lookup its target: its lookup exists in the session's view and targets its referenced entity. Not
    /// custom; a new MetadataId; the referenced entity's key as <c>ReferencedAttribute</c>. Idempotent: a relationship already saved
    /// for the same lookup and target is kept as it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The lookup does not exist or does not target the referenced entity.</exception>
    /// <exception cref="MetadataComponentExistsException">Another relationship already has the name.</exception>
    internal async Task EnsureNamedRelationshipAsync(DataSession session, OneToManyRelationshipMetadata relationship, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        var r = MetadataSerializer.Clone(relationship);
        var referencing = cache.ByLogicalName.GetValueOrDefault(r.ReferencingEntity ?? "");
        var lookup = (referencing?.Attributes ?? []).OfType<LookupAttributeMetadata>().FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute);
        if (lookup is null || !(lookup.Targets ?? []).Contains(r.ReferencedEntity, StringComparer.Ordinal)
                           || cache.ByLogicalName.GetValueOrDefault(r.ReferencedEntity ?? "") is not { } referenced)
            throw new InvalidOperationException(
                $"Relationship '{r.SchemaName}': lookup '{r.ReferencingEntity}.{r.ReferencingAttribute}' does not target '{r.ReferencedEntity}'.");
        if (cache.SavedOneToMany.Values.Any(x => x.ReferencingEntity == r.ReferencingEntity && x.ReferencingAttribute == r.ReferencingAttribute
                                                 && x.ReferencedEntity == r.ReferencedEntity))
            return;
        if (r.SchemaName is null || cache.ManyToMany.ContainsKey(r.SchemaName) || cache.SavedOneToMany.ContainsKey(r.SchemaName)
            || EntityMetadataExtensions.FindLookupRelationship(cache.ByLogicalName.Values, r.SchemaName, cache.SavedOneToMany.Values) is not null)
            throw new MetadataComponentExistsException(
                $"Relationship '{r.SchemaName}' already exists: it is the name of the relationship of '{r.ReferencingEntity}.{r.ReferencingAttribute}' to '{r.ReferencedEntity}'.");
        r.MetadataId = Guid.NewGuid();
        r.ReferencedAttribute = SystemAttributes.PrimaryIdName(referenced);
        r.IsCustomRelationship = false;
        await SaveOneToManyAsync(session, pending, r, ct, pending.Stamp);
    }

    /// <summary>
    /// Upgrade (#183): the currency relationships saved before the fix name their referencing navigation property
    /// <see cref="LegacyCurrencyNavigation"/>; Dataverse names it <c>transactioncurrencyid</c>. Renames every such system
    /// relationship. Idempotent: a second run finds none.
    /// </summary>
    /// <returns>The schema names of the renamed relationships.</returns>
    internal async Task<IReadOnlyList<string>> RenameLegacyCurrencyNavigationAsync(DataSession session, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        var legacy = cache.SavedOneToMany.Values
            .Where(r => r.IsCustomRelationship != true && r.ReferencedEntity == CurrencySchema.Table
                        && r.ReferencingAttribute == CurrencySchema.CurrencyId
                        && r.ReferencingEntityNavigationPropertyName == LegacyCurrencyNavigation)
            .OrderBy(r => r.SchemaName, StringComparer.Ordinal)
            .ToList();
        if (legacy.Count == 0) return [];
        var stamp = await BumpVersionStampAsync(session, pending, ct);
        foreach (var saved in legacy)
        {
            var upgraded = MetadataSerializer.Clone(saved);
            upgraded.ReferencingEntityNavigationPropertyName = CurrencySchema.CurrencyId;
            await ExecuteAsync(session, "UPDATE meta.relationship SET definition = @definition, version_stamp = @stamp WHERE metadata_id = @id", ct,
                ("@definition", MetadataSerializer.SerializeRelationship(upgraded)), ("@stamp", stamp), ("@id", saved.MetadataId!.Value));
            cache.SavedOneToMany[saved.SchemaName] = upgraded;
        }
        return legacy.Select(r => r.SchemaName).ToList();
    }

    /// <summary>The referencing navigation property the currency relationships of custom tables had before #183.</summary>
    internal const string LegacyCurrencyNavigation = "TransactionCurrencyId";

    /// <summary>
    /// The table <c>transactioncurrency</c> exists and no relationship other than the currency lookup's own already has the name
    /// <see cref="CurrencySchema.RelationshipName"/> of <paramref name="entityLogicalName"/>.
    /// </summary>
    private static void EnsureCurrencyLinkable(CacheState cache, string entityLogicalName)
    {
        if (!cache.ByLogicalName.ContainsKey(CurrencySchema.Table))
            throw new XrmdValidationException(
                $"Money columns need the system table '{CurrencySchema.Table}', which this organization does not have yet.");
        EnsureCurrencyRelationshipNameFree(cache, entityLogicalName);
    }

    private static void EnsureCurrencyRelationshipNameFree(CacheState cache, string entityLogicalName)
    {
        var name = CurrencySchema.RelationshipName(entityLogicalName);
        var holder = cache.ManyToMany.ContainsKey(name) ? null
            : cache.SavedOneToMany.TryGetValue(name, out var saved) ? saved
            : EntityMetadataExtensions.FindLookupRelationship(cache.ByLogicalName.Values, name, cache.SavedOneToMany.Values);
        var free = !cache.ManyToMany.ContainsKey(name)
                   && (holder is null || (holder.ReferencingEntity == entityLogicalName && holder.ReferencingAttribute == CurrencySchema.CurrencyId
                                          && holder.ReferencedEntity == CurrencySchema.Table));
        if (!free)
            throw new MetadataComponentExistsException(
                $"Relationship '{name}' already exists: it is the name the currency columns of '{entityLogicalName}' give their relationship to '{CurrencySchema.Table}'.");
    }

    /// <summary>
    /// The rules of a hierarchical relationship (<c>IsHierarchical</c> true): it links a table to itself, and a table has at
    /// most one (the walk of the hierarchical operators and of hierarchy security follows it).
    /// </summary>
    /// <exception cref="XrmdValidationException">Either rule is broken.</exception>
    private static void DemandHierarchicalAllowed(CacheState cache, OneToManyRelationshipMetadata r)
    {
        if (r.ReferencedEntity != r.ReferencingEntity)
            throw new XrmdValidationException(
                $"Relationship '{r.SchemaName}' cannot be hierarchical: a hierarchical relationship must reference its own table, " +
                $"but it goes from '{r.ReferencingEntity}' to '{r.ReferencedEntity}'.");
        if (cache.OneToMany().FirstOrDefault(x => IsHierarchicalRelationship(x) && x.ReferencingEntity == r.ReferencingEntity
                                                  && !string.Equals(x.SchemaName, r.SchemaName, StringComparison.OrdinalIgnoreCase)) is { } existing)
            throw new XrmdValidationException(
                $"Relationship '{r.SchemaName}' cannot be hierarchical: table '{r.ReferencingEntity}' already has the hierarchical " +
                $"relationship '{existing.SchemaName}', and a table can have only one.");
    }

    /// <summary>
    /// Seed-only path: saves the one-to-many relationships a seeded table declares (<c>ManyToOneRelationships</c> of its seed
    /// definition) under their own names, each for a lookup of the table that already exists and one of its targets. A
    /// Customer lookup (<c>contact.parentcustomerid</c>) has one relationship per target, so one attribute can carry several.
    /// Idempotent: a relationship already saved under the name keeps its row and MetadataId; when it is a seeded one (not
    /// custom) whose CascadeConfiguration or IsHierarchical differs from the seed's (an earlier seed stored Delete RemoveLink
    /// only on the relationships of <c>contact.parentcustomerid</c>, and <c>account_parent_account</c> was not hierarchical
    /// before plan 2.5), the seed's values replace them. The public
    /// <see cref="CreateOneToManyAsync"/> keeps creating a lookup together with its single relationship.
    /// </summary>
    internal Task EnsureSeedRelationshipsAsync(IReadOnlyList<OneToManyRelationshipMetadata> relationships, CancellationToken ct = default) =>
        InOwnSessionAsync(async session =>
        {
            var pending = await EnterAsync(session, ct);
            var cache = pending.Working;
            foreach (var relationship in relationships)
            {
                var r = MetadataSerializer.Clone(relationship);
                if (r.SchemaName is null) continue;
                if (cache.SavedOneToMany.TryGetValue(r.SchemaName, out var saved))
                {
                    if (saved.IsCustomRelationship == true) continue;
                    var cascadeChanged = r.CascadeConfiguration is { } seeded
                        && JsonSerializer.Serialize(seeded, MetadataSerializer.Options) != JsonSerializer.Serialize(saved.CascadeConfiguration ?? new CascadeConfiguration(), MetadataSerializer.Options);
                    var hierarchyChanged = (r.IsHierarchical ?? false) != (saved.IsHierarchical ?? false);
                    if (cascadeChanged || hierarchyChanged)
                    {
                        var upgraded = MetadataSerializer.Clone(saved);
                        if (cascadeChanged) upgraded.CascadeConfiguration = MetadataSerializer.Clone(r.CascadeConfiguration!);
                        upgraded.IsHierarchical = r.IsHierarchical ?? false;
                        var stamp = await BumpVersionStampAsync(session, pending, ct);
                        await ExecuteAsync(session,
                            "UPDATE meta.relationship SET definition = @definition, version_stamp = @stamp WHERE metadata_id = @id", ct,
                            ("@definition", MetadataSerializer.SerializeRelationship(upgraded)), ("@stamp", stamp), ("@id", saved.MetadataId!.Value));
                        cache.SavedOneToMany[saved.SchemaName] = upgraded;
                    }
                    continue;
                }
                var referencing = cache.ByLogicalName.GetValueOrDefault(r.ReferencingEntity ?? "")
                                  ?? throw new InvalidOperationException(
                                      $"Seed relationship '{r.SchemaName}': entity '{r.ReferencingEntity}' does not exist.");
                var lookup = (referencing.Attributes ?? []).OfType<LookupAttributeMetadata>().SingleOrDefault(a => a.LogicalName == r.ReferencingAttribute)
                             ?? throw new InvalidOperationException(
                                 $"Seed relationship '{r.SchemaName}': '{r.ReferencingEntity}.{r.ReferencingAttribute}' is not a lookup.");
                if (!(lookup.Targets ?? []).Contains(r.ReferencedEntity))
                    throw new InvalidOperationException(
                        $"Seed relationship '{r.SchemaName}': lookup '{r.ReferencingEntity}.{r.ReferencingAttribute}' does not target '{r.ReferencedEntity}'.");
                r.MetadataId ??= Guid.NewGuid();
                r.IsCustomRelationship ??= false;
                await SaveOneToManyAsync(session, pending, r, ct);
            }
        }, ct);

    /// <summary>
    /// Saves the many-to-many relationship <paramref name="relationship"/> of a seeded intersect table (plan 4.1a:
    /// <c>queuemembership_association</c>) once; an existing one is left as it is. Unlike <see cref="CreateManyToManyAsync"/> the
    /// intersect entity already exists (it is seeded), so only the relationship row is written.
    /// </summary>
    internal Task EnsureSeedManyToManyAsync(ManyToManyRelationshipMetadata relationship, CancellationToken ct = default) =>
        InOwnSessionAsync(async session =>
        {
            var pending = await EnterAsync(session, ct);
            var cache = pending.Working;
            var r = MetadataSerializer.Clone(relationship);
            if (r.SchemaName is null || cache.ManyToMany.ContainsKey(r.SchemaName)) return;
            foreach (var name in (string[])[r.Entity1LogicalName, r.Entity2LogicalName, r.IntersectEntityName])
                if (!cache.ByLogicalName.ContainsKey(name))
                    throw new InvalidOperationException($"Seed relationship '{r.SchemaName}': entity '{name}' does not exist.");
            r.MetadataId ??= Guid.NewGuid();
            r.IsCustomRelationship ??= false;
            r.Entity1NavigationPropertyName ??= r.SchemaName;
            r.Entity2NavigationPropertyName ??= r.SchemaName;
            SdkProperty.Set(r, nameof(RelationshipMetadataBase.RelationshipType), RelationshipType.ManyToManyRelationship);
            var stamp = await BumpVersionStampAsync(session, pending, ct);
            await ExecuteAsync(session,
                "INSERT INTO meta.relationship (metadata_id, schema_name, definition, version_stamp) VALUES (@id, @name, @definition, @stamp)", ct,
                ("@id", r.MetadataId.Value), ("@name", r.SchemaName), ("@definition", MetadataSerializer.SerializeRelationship(r)), ("@stamp", stamp));
            cache.ManyToMany[r.SchemaName] = r;
        }, ct);

    /// <summary>
    /// Creates a Customer column with its two relationships (the <c>CreateCustomerRelationships</c> message): the lookup
    /// <paramref name="lookup"/> on the referencing entity (<c>AttributeType</c> Customer, <c>Targets</c> account and contact, with
    /// its <c>&lt;name&gt;type</c> column) and one saved one-to-many relationship per target, each keeping its SchemaName and
    /// MetadataId (a new one when null). The two relationships must name the same referencing entity and, as referenced
    /// entities, exactly <c>account</c> and <c>contact</c>. A missing <c>ReferencingEntityNavigationPropertyName</c> becomes
    /// <c>{lookup}_{target}</c>. The cascade is checked by <see cref="SupportedCascade"/> (Delete RemoveLink and Merge Cascade
    /// by default). The rows go with the lookup (DeleteAttribute) and with either entity.
    /// </summary>
    /// <exception cref="XrmdValidationException">Invalid names or targets, an unknown or intersect entity, an unsupported
    /// cascade; nothing is written.</exception>
    /// <exception cref="MetadataComponentExistsException">A relationship with the same SchemaName (ignoring case) or the
    /// attribute exists.</exception>
    public async Task CreateCustomerRelationshipsAsync(DataSession session, LookupAttributeMetadata lookup,
        IReadOnlyList<OneToManyRelationshipMetadata> relationships, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(relationships);
        var l = MetadataSerializer.Clone(lookup);
        var rs = relationships.Select(MetadataSerializer.Clone).ToList();
        LogicalNameValidator.EnsureValid(l.LogicalName, "attribute");
        if (l.AttributeType is not (null or AttributeTypeCode.Lookup or AttributeTypeCode.Customer))
            throw new XrmdValidationException(
                $"Lookup '{l.LogicalName}' has the type {l.AttributeType}: {CustomerColumns.CreateMessage} creates a Customer column.");
        var where = $"{CustomerColumns.CreateMessage} for column '{l.LogicalName}'";
        if (rs.Count != CustomerColumns.Targets.Count)
            throw new XrmdValidationException(CustomerColumns.TargetsMessage($"{where} takes {CustomerColumns.Targets.Count} relationships, not {rs.Count}"));
        if (!CustomerColumns.AreCustomerTargets(rs.Select(r => r.ReferencedEntity)))
            throw new XrmdValidationException(CustomerColumns.TargetsMessage(
                $"{where} (relationships to {string.Join(", ", rs.Select(r => $"'{r.ReferencedEntity}'"))})"));
        if (l.Targets is { Length: > 0 } && !CustomerColumns.AreCustomerTargets(l.Targets))
            throw new XrmdValidationException(CustomerColumns.TargetsMessage($"{where} (lookup targets {string.Join(", ", l.Targets.Select(t => $"'{t}'"))})"));
        var referencingEntity = rs[0].ReferencingEntity;
        if (rs.Any(r => r.ReferencingEntity != referencingEntity))
            throw new XrmdValidationException($"{where}: both relationships must have the same ReferencingEntity.");
        LogicalNameValidator.EnsureValid(referencingEntity, "entity");
        if (referencingEntity == FileColumns.AttachmentEntity)
            throw AttachmentLookupIsManaged($"Table '{referencingEntity}'");

        foreach (var r in rs)
        {
            if (r.SchemaName is null || !RelationshipNamePattern().IsMatch(r.SchemaName))
                throw new XrmdValidationException(
                    $"Invalid relationship name '{r.SchemaName}': it must match ^[A-Za-z][A-Za-z0-9_]{{0,99}}$.");
            if (r.IsHierarchical == true)
                throw new XrmdValidationException(
                    $"Relationship '{r.SchemaName}' cannot be hierarchical: a Customer column has two targets, and a hierarchical relationship needs a lookup to its own table only.");
            r.ReferencingAttribute = l.LogicalName;
            r.MetadataId ??= Guid.NewGuid();
            r.IsCustomRelationship = true;
            if (string.IsNullOrEmpty(r.ReferencingEntityNavigationPropertyName))
                r.ReferencingEntityNavigationPropertyName = CustomerColumns.DefaultNavigation(l.LogicalName, r.ReferencedEntity);
            else if (!NavigationNamePattern().IsMatch(r.ReferencingEntityNavigationPropertyName))
                throw new XrmdValidationException(
                    $"Invalid ReferencingEntityNavigationPropertyName '{r.ReferencingEntityNavigationPropertyName}': it must match ^[A-Za-z_][A-Za-z0-9_]{{0,99}}$.");
            r.CascadeConfiguration = SupportedCascade(r);
        }
        if (rs[0].SchemaName!.Equals(rs[1].SchemaName, StringComparison.OrdinalIgnoreCase))
            throw new XrmdValidationException($"{where}: the two relationships need different SchemaNames.");
        if (rs[0].ReferencingEntityNavigationPropertyName == rs[1].ReferencingEntityNavigationPropertyName)
            throw new XrmdValidationException($"{where}: the two relationships need different ReferencingEntityNavigationPropertyNames.");
        l.Targets = [.. CustomerColumns.Targets];
        SdkProperty.Set(l, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.Customer);
        l.MetadataId ??= Guid.NewGuid();

        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        var referencing = cache.ByLogicalName.GetValueOrDefault(referencingEntity)
                          ?? throw new XrmdValidationException($"Entity '{referencingEntity}' does not exist.");
        if (referencing.IsIntersect == true)
            throw new XrmdValidationException($"Entity '{referencingEntity}' is an intersect entity and cannot take part in a relationship.");
        ElasticTables.DemandNotElastic(referencing, "Customer lookups");
        foreach (var r in rs)
            if (cache.ManyToMany.ContainsKey(r.SchemaName) || cache.SavedOneToMany.ContainsKey(r.SchemaName)
                || EntityMetadataExtensions.FindLookupRelationship(cache.ByLogicalName.Values, r.SchemaName, cache.SavedOneToMany.Values) is not null)
                throw new MetadataComponentExistsException($"Relationship '{r.SchemaName}' already exists.");

        // $metadata declares each navigation property next to the table's columns and the other lookups' navigation properties.
        var taken = (referencing.Attributes ?? []).SelectMany(a => new[] { a.LogicalName, $"_{a.LogicalName}_value" })
            .Concat(EntityMetadataExtensions.OneToManyRelationships(cache.ByLogicalName.Values, cache.SavedOneToMany.Values)
                .Where(x => x.ReferencingEntity == referencingEntity).Select(x => x.ReferencingEntityNavigationPropertyName!))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var r in rs)
            if (taken.Contains(r.ReferencingEntityNavigationPropertyName!))
                throw new XrmdValidationException(
                    $"ReferencingEntityNavigationPropertyName '{r.ReferencingEntityNavigationPropertyName}' is already a property of entity '{referencingEntity}'.");

        await AddAttributeCoreAsync(session, referencingEntity, l, customerAllowed: true, ct);
        foreach (var r in rs) await SaveOneToManyAsync(session, pending, r, ct);
    }

    /// <summary>
    /// The cascade of a Customer relationship: Delete is Cascade, RemoveLink (the default) or Restrict, the three options of
    /// Dataverse, all enforced by the Delete message. Merge is Cascade (the default: the only valid option on Dataverse for a
    /// mergeable referenced table such as account and contact) or NoCascade; the Merge message itself is refused by xrmverse.
    /// Assign, Share, Unshare, Reparent, Archive and RollupView are stored as given (Dataverse's seeded Customer relationships
    /// cascade Assign, Share, Unshare and Reparent); xrmverse does not enforce them (a documented gap). Any other Delete or Merge
    /// value is refused rather than stored.
    /// </summary>
    private static CascadeConfiguration SupportedCascade(OneToManyRelationshipMetadata r)
    {
        var cascade = r.CascadeConfiguration is { } given ? MetadataSerializer.Clone(given) : new CascadeConfiguration();
        if (cascade.Delete is not (null or CascadeType.Cascade or CascadeType.RemoveLink or CascadeType.Restrict))
            throw new XrmdValidationException(
                $"Relationship '{r.SchemaName}': CascadeConfiguration.Delete {cascade.Delete} is not valid on a Customer relationship; " +
                "use Cascade, RemoveLink or Restrict.");
        if (cascade.Merge is not (null or CascadeType.Cascade or CascadeType.NoCascade))
            throw new XrmdValidationException(
                $"Relationship '{r.SchemaName}': CascadeConfiguration.Merge {cascade.Merge} is not valid on a Customer relationship; use Cascade.");
        cascade.Delete ??= CascadeType.RemoveLink;
        cascade.Merge ??= CascadeType.Cascade;
        return cascade;
    }

    public async Task CreateManyToManyAsync(DataSession session, ManyToManyRelationshipMetadata relationship, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        var r = MetadataSerializer.Clone(relationship);
        if (r.SchemaName is null || !RelationshipNamePattern().IsMatch(r.SchemaName))
            throw new XrmdValidationException(
                $"Invalid relationship name '{r.SchemaName}': it must match ^[A-Za-z][A-Za-z0-9_]{{0,99}}$.");
        LogicalNameValidator.EnsureValid(r.Entity1LogicalName, "entity");
        LogicalNameValidator.EnsureValid(r.Entity2LogicalName, "entity");
        r.IntersectEntityName ??= r.SchemaName.ToLowerInvariant();
        if (!LogicalNameValidator.IsValid(r.IntersectEntityName))
            throw new XrmdValidationException(
                $"Invalid IntersectEntityName '{r.IntersectEntityName}': it must match ^[a-z][a-z0-9_]{{0,63}}$.");

        // The intersect columns are the server's, whatever the caller sent (as Dataverse).
        var selfReferential = r.Entity1LogicalName == r.Entity2LogicalName;
        r.Entity1IntersectAttribute = selfReferential ? r.Entity1LogicalName + IdOne : SystemAttributes.PrimaryIdName(r.Entity1LogicalName);
        r.Entity2IntersectAttribute = selfReferential ? r.Entity2LogicalName + IdTwo : SystemAttributes.PrimaryIdName(r.Entity2LogicalName);
        r.Entity1NavigationPropertyName ??= r.SchemaName;
        r.Entity2NavigationPropertyName ??= r.SchemaName;
        r.MetadataId ??= Guid.NewGuid();
        r.IsCustomRelationship ??= true;
        SdkProperty.Set(r, nameof(RelationshipMetadataBase.RelationshipType), RelationshipType.ManyToManyRelationship);

        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        foreach (var side in (string[])[r.Entity1LogicalName, r.Entity2LogicalName])
        {
            var entity = cache.ByLogicalName.GetValueOrDefault(side)
                         ?? throw new XrmdValidationException($"Entity '{side}' does not exist.");
            if (entity.IsIntersect == true)
                throw new XrmdValidationException($"Entity '{side}' is an intersect entity and cannot take part in a relationship.");
            ElasticTables.DemandNotElastic(entity, "Many-to-many relationships");
        }
        if (cache.ManyToMany.ContainsKey(r.SchemaName)
            || cache.SavedOneToMany.ContainsKey(r.SchemaName)
            || EntityMetadataExtensions.FindLookupRelationship(cache.ByLogicalName.Values, r.SchemaName, cache.SavedOneToMany.Values) is not null)
            throw new MetadataComponentExistsException($"Relationship '{r.SchemaName}' already exists.");
        if (cache.ByLogicalName.ContainsKey(r.IntersectEntityName))
            throw new MetadataComponentExistsException($"Entity '{r.IntersectEntityName}' already exists.");

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        // LOWER on both sides: unique ignoring case whatever the collation (the version-stamp lock serializes writers).
        if (await ScalarAsync<int>(session, "SELECT COUNT(*) FROM meta.relationship WHERE LOWER(schema_name) = LOWER(@name)", ct,
                ("@name", r.SchemaName)) > 0)
            throw new MetadataComponentExistsException($"Relationship '{r.SchemaName}' already exists.");
        await ExecuteAsync(session,
            "INSERT INTO meta.relationship (metadata_id, schema_name, definition, version_stamp) VALUES (@id, @name, @definition, @stamp)", ct,
            ("@id", r.MetadataId.Value), ("@name", r.SchemaName), ("@definition", MetadataSerializer.SerializeRelationship(r)),
            ("@stamp", stamp));

        await CreateEntityCoreAsync(session, IntersectEntity(r), primaryName: null, seed: false, ct,
            (r.Entity1IntersectAttribute, r.Entity2IntersectAttribute));
        cache.ManyToMany[r.SchemaName] = r;
    }

    public async Task DeleteRelationshipAsync(DataSession session, string schemaName, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        if (schemaName is not null && cache.ManyToMany.TryGetValue(schemaName, out var manyToMany))
        {
            await BumpVersionStampAsync(session, pending, ct);
            await ExecuteAsync(session, "DELETE FROM meta.relationship WHERE metadata_id = @id", ct, ("@id", manyToMany.MetadataId!.Value));
            cache.ManyToMany.Remove(manyToMany.SchemaName);
            cache.RelationshipMenus.Remove(manyToMany.SchemaName);
            ForgetRelationship(cache, manyToMany.SchemaName);
            if (cache.ByLogicalName.GetValueOrDefault(manyToMany.IntersectEntityName) is { } intersect)
                await DeleteEntityCoreAsync(session, pending, intersect, ct);
            return;
        }

        var oneToMany = EntityMetadataExtensions.FindLookupRelationship(cache.ByLogicalName.Values, schemaName, cache.SavedOneToMany.Values)
                        ?? throw new XrmdValidationException($"Relationship '{schemaName}' does not exist.");
        var lookup = cache.ByLogicalName[oneToMany.ReferencingEntity].Attributes!
            .OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == oneToMany.ReferencingAttribute);
        if (oneToMany.ReferencingEntity == FileColumns.AttachmentEntity && lookup.LogicalName == FileColumns.AttachmentLookup)
            throw AttachmentLookupIsManaged($"Relationship '{oneToMany.SchemaName}'");
        if (ActivitySchema.IsManagedLookup(oneToMany.ReferencingEntity, lookup.LogicalName))
            throw ActivityLookupIsManaged($"Relationship '{oneToMany.SchemaName}'");
        if (lookup.Targets is { Length: > 1 })
            throw new XrmdValidationException(
                $"Relationship '{oneToMany.SchemaName}' is one target of the polymorphic lookup '{oneToMany.ReferencingEntity}.{lookup.LogicalName}': " +
                "delete the lookup attribute instead.");
        // A relationship a rollup column aggregates over, or names as its hierarchy, stays (plan 2.12b, ruling R13).
        DemandNoRollupDependentsOfRelationship(cache, oneToMany.SchemaName);
        // A one-to-many relationship is its lookup: deleting it deletes the lookup (403 for a system one) and its saved row.
        await DeleteAttributeAsync(session, oneToMany.ReferencingEntity, lookup.LogicalName, ct);
    }

    /// <summary>Deletes the saved one-to-many relationships of <paramref name="cache"/> that <paramref name="match"/> (their
    /// lookup or entity is being deleted); the caller has bumped the version stamp.</summary>
    private static async Task DeleteSavedOneToManyAsync(DataSession session, CacheState cache,
        Func<OneToManyRelationshipMetadata, bool> match, CancellationToken ct)
    {
        foreach (var r in cache.SavedOneToMany.Values.Where(match).ToList())
        {
            await ExecuteAsync(session, "DELETE FROM meta.relationship WHERE metadata_id = @id", ct, ("@id", r.MetadataId!.Value));
            cache.SavedOneToMany.Remove(r.SchemaName);
            cache.RelationshipMenus.Remove(r.SchemaName);
            ForgetRelationship(cache, r.SchemaName);
        }
    }

    /// <summary>The many-to-many relationship of <paramref name="cache"/> that <paramref name="entityLogicalName"/> is a side
    /// or the intersect of; null when there is none.</summary>
    private static ManyToManyRelationshipMetadata? ManyToManyOf(CacheState cache, string entityLogicalName) =>
        cache.ManyToMany.Values
            .Where(r => r.Entity1LogicalName == entityLogicalName || r.Entity2LogicalName == entityLogicalName
                        || r.IntersectEntityName == entityLogicalName)
            .OrderBy(r => r.SchemaName, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The intersect entity of <paramref name="r"/>: organization owned, its two columns, no primary name.</summary>
    private static EntityMetadata IntersectEntity(ManyToManyRelationshipMetadata r)
    {
        var entity = new EntityMetadata
        {
            LogicalName = r.IntersectEntityName,
            SchemaName = r.IntersectEntityName,
            OwnershipType = OwnershipTypes.OrganizationOwned,
        };
        SdkProperty.Set(entity, nameof(EntityMetadata.IsIntersect), true);
        entity.SetAttributes(
        [
            IntersectAttribute(r.Entity1IntersectAttribute),
            IntersectAttribute(r.Entity2IntersectAttribute),
        ]);
        return entity;
    }

    private static UniqueIdentifierAttributeMetadata IntersectAttribute(string logicalName) => new()
    {
        LogicalName = logicalName,
        SchemaName = logicalName,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
        IsValidForCreate = false,
        IsValidForUpdate = false,
    };

    /// <summary>Reads <c>meta.relationship</c> (many-to-many and saved one-to-many relationships) into <paramref name="state"/>.</summary>
    private static async Task LoadRelationshipsAsync(DbConnection conn, CacheState state, CancellationToken ct)
    {
        await using var cmd = conn.Command("SELECT definition, associated_menu_xml, unmodelled_xml FROM meta.relationship");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var relationship = MetadataSerializer.DeserializeRelationship(rd.GetString(0));
            switch (relationship)
            {
                case ManyToManyRelationshipMetadata { SchemaName: not null } manyToMany:
                    state.ManyToMany[manyToMany.SchemaName] = manyToMany;
                    break;
                case OneToManyRelationshipMetadata { SchemaName: not null } oneToMany:
                    state.SavedOneToMany[oneToMany.SchemaName] = oneToMany;
                    break;
            }
            if (relationship.SchemaName is { } name && !rd.IsDBNull(1)) state.RelationshipMenus[name] = rd.GetString(1);
            if (relationship.SchemaName is { } schemaName && !rd.IsDBNull(2))
                state.Unmodelled[UnmodelledKey(RelationshipComponent, schemaName)] = rd.GetString(2);
        }
    }

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RelationshipNamePattern();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NavigationNamePattern();
}
