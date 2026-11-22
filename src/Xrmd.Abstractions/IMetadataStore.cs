using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Abstractions;

/// <summary>
/// Entity/attribute definitions (schema <c>meta</c>) with an in-memory cache, plus the physical tables they
/// describe (schema <c>data</c>). Returned metadata objects are shared cache instances: treat them as read-only.
/// </summary>
public interface IMetadataStore
{
    /// <summary>From cache; null if absent.</summary>
    /// <remarks>
    /// The cached attributes carry no <c>CanBeSecuredForCreate</c>, <c>CanBeSecuredForRead</c> or
    /// <c>CanBeSecuredForUpdate</c> (null): only the metadata reads compute them. Code that needs to know whether a column can
    /// be secured calls <c>Xrmd.Metadata.FieldSecurability.IsSecurable</c>. <c>IsSecured</c> is true only on a column the rule
    /// allows (a legacy flag on another column is read as false).
    /// </remarks>
    EntityMetadata? GetEntity(string logicalName);

    /// <summary>
    /// An entity as <paramref name="session"/> sees it: its own metadata changes included when it has made some (a table it
    /// created, with the ids of its columns), the published state otherwise. Takes no lock.
    /// </summary>
    EntityMetadata? GetEntity(DataSession session, string logicalName) => GetEntity(logicalName);

    EntityMetadata? GetEntityBySetName(string entitySetName);

    /// <summary>From cache; null if no entity has this ObjectTypeCode.</summary>
    EntityMetadata? GetEntityByObjectTypeCode(int objectTypeCode);

    /// <summary>All entities ordered by LogicalName (a shared snapshot, rebuilt only when metadata changes).</summary>
    IReadOnlyList<EntityMetadata> GetAllEntities();

    /// <summary>The global option set named <paramref name="name"/> (ignoring case), from cache; null if absent.</summary>
    OptionSetMetadataBase? GetOptionSet(string name);

    /// <summary>All global option sets ordered by Name (a shared snapshot, rebuilt only when metadata changes).</summary>
    IReadOnlyList<OptionSetMetadataBase> GetAllOptionSets();

    /// <summary>
    /// The relationship named <paramref name="schemaName"/> (ignoring case), from cache: a saved
    /// <see cref="ManyToManyRelationshipMetadata"/>, or one of <see cref="GetOneToManyRelationships"/>; null if absent.
    /// </summary>
    RelationshipMetadataBase? GetRelationship(string schemaName);

    /// <summary>The saved many-to-many relationships ordered by SchemaName (a shared snapshot, rebuilt only when metadata changes).</summary>
    IReadOnlyList<ManyToManyRelationshipMetadata> GetManyToManyRelationships();

    /// <summary>
    /// Every one-to-many relationship, ordered by SchemaName (a shared snapshot, rebuilt only when metadata changes): one
    /// per lookup target (a polymorphic lookup gives one per target), named <c>{referencing}_{lookup}_{target}</c> unless
    /// <see cref="CreateOneToManyAsync"/> gave it a SchemaName of its own.
    /// </summary>
    IReadOnlyList<OneToManyRelationshipMetadata> GetOneToManyRelationships();

    /// <summary>
    /// The hierarchical relationship of <paramref name="entityLogicalName"/>: the self-referencing one-to-many relationship with
    /// <c>IsHierarchical</c> true (a table has at most one); null when the table has none or does not exist.
    /// </summary>
    OneToManyRelationshipMetadata? GetHierarchicalRelationship(string entityLogicalName) =>
        GetOneToManyRelationships().FirstOrDefault(r => r.IsHierarchical == true
                                                        && r.ReferencingEntity == entityLogicalName && r.ReferencedEntity == entityLogicalName);

    /// <summary>
    /// The logical names of the tables with <c>AutoCreateAccessTeams</c> true (plan 2.16), from cache. The targets of
    /// <c>team.regardingobjectid</c> hold every one of them (a target is added when its table is enabled and stays when it is
    /// disabled, see the metadata store).
    /// </summary>
    IReadOnlySet<string> AccessTeamTables =>
        GetAllEntities().Where(e => e.AutoCreateAccessTeams == true).Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The logical names of the tables with <c>ChangeTrackingEnabled</c> true (plan 2.13), from cache. A table enters it once
    /// and stays until it is deleted (tracking cannot be switched off).
    /// </summary>
    IReadOnlySet<string> ChangeTrackingTables =>
        GetAllEntities().Where(e => e.ChangeTrackingEnabled == true).Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The logical names of the activity tables (plan 2.11): the tables with <c>IsActivity</c> true, the four standard ones and
    /// the custom activities; not <c>activitypointer</c>, which reads them. From cache.
    /// </summary>
    IReadOnlySet<string> ActivityTables =>
        GetAllEntities().Where(e => e.IsActivity == true && e.LogicalName != "activitypointer").Select(e => e.LogicalName)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The logical names of the tables with <c>HasActivities</c> true (plan 2.11, ruling A6): the targets of the
    /// <c>regardingobjectid</c> of <c>activitypointer</c> and of every activity table. From cache.
    /// </summary>
    IReadOnlySet<string> RegardingTables =>
        GetAllEntities().Where(e => e.HasActivities == true).Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The logical names of the tables with <c>HasNotes</c> true (plan 2.11, ruling A15): the targets of <c>annotation.objectid</c>.
    /// From cache.
    /// </summary>
    IReadOnlySet<string> NotesTables =>
        GetAllEntities().Where(e => e.HasNotes == true).Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Why deleting the table <paramref name="logicalName"/> would be refused by the activity model now (plan 2.11: activities
    /// regarding its records, notes attached to them), without locks; null when nothing blocks it. Activities of the tables in
    /// <paramref name="dropped"/> are not counted. A store without the activity model blocks nothing.
    /// </summary>
    Task<string?> ActivityDeleteRefusalAsync(DataSession session, string logicalName, IReadOnlySet<string>? dropped, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// The logical names of the tables with <c>IsDuplicateDetectionEnabled</c> true (plan 2.15), from cache. The targets of
    /// <c>duplicaterecord.baserecordid</c> and <c>duplicaterecordid</c> hold every one of them.
    /// </summary>
    IReadOnlySet<string> DuplicateDetectionTables =>
        GetAllEntities().Where(e => e.IsDuplicateDetectionEnabled?.Value == true).Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The creation ordinal of the image column <paramref name="attributeLogicalName"/> of <paramref name="entityLogicalName"/>
    /// (the oldest remaining image column becomes primary when the primary one is deleted); null for any other attribute.
    /// xrmverse-private: never part of the metadata a caller reads. A solution export writes the image columns in this order,
    /// so that an import creates them in it again.
    /// </summary>
    int? GetImageOrdinal(string entityLogicalName, string attributeLogicalName) => null;

    /// <summary>Metadata version, incremented by every committed metadata change (ClientVersionStamp).</summary>
    long VersionStamp { get; }

    /// <summary>Loads <c>meta.*</c> into the cache, replacing its content.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the entity, its attributes (<paramref name="entity"/>.Attributes, <paramref name="primaryName"/>, primary key
    /// and system attributes) and creates <c>data.[logicalname]</c>, in a transaction of its own; then updates the cache.
    /// Same as the <see cref="DataSession"/> overload run in a session that is committed right away.
    /// </summary>
    /// <exception cref="XrmdValidationException">Invalid logical names or attribute definitions; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">An entity with the same logical name already exists.</exception>
    Task CreateEntityAsync(EntityMetadata entity, AttributeMetadata primaryName, CancellationToken ct = default);

    /// <summary>Persists the attribute and adds its column(s) in a transaction of its own; then updates the cache.</summary>
    /// <exception cref="XrmdValidationException">Unknown entity or invalid attribute; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">The attribute already exists.</exception>
    Task AddAttributeAsync(string entityLogicalName, AttributeMetadata attribute, CancellationToken ct = default);

    // Writes in a session. Every overload below writes meta.* and runs the DDL in session.Transaction (required). The
    // first write of a session takes the store's write lock and works on a private copy of the cache; the session's owner
    // must report the outcome: DataSession.Committed() publishes the copy in one step (readers see all the session's
    // changes or none) and DataSession.RolledBack() discards it. Both release the lock. Lookups by these overloads (does
    // the entity exist?) see the session's own uncommitted changes; the read members above see only published metadata.

    /// <summary>
    /// Like <see cref="CreateEntityAsync(EntityMetadata, AttributeMetadata, CancellationToken)"/> in <paramref name="session"/>;
    /// the <see cref="IEntityCreatedHandler"/>s run in the session too.
    /// </summary>
    /// <exception cref="XrmdValidationException">Invalid logical names or attribute definitions; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">An entity with the same logical name already exists, or the session has no transaction.</exception>
    Task CreateEntityAsync(DataSession session, EntityMetadata entity, AttributeMetadata primaryName, CancellationToken ct);

    /// <summary>Like <see cref="AddAttributeAsync(string, AttributeMetadata, CancellationToken)"/> in <paramref name="session"/>.</summary>
    /// <exception cref="XrmdValidationException">Unknown entity or invalid attribute; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">The attribute already exists, or the session has no transaction.</exception>
    Task AddAttributeAsync(DataSession session, string entityLogicalName, AttributeMetadata attribute, CancellationToken ct);

    /// <summary>
    /// Updates the entity named by <paramref name="changes"/>.LogicalName: <c>DisplayName</c>, <c>DisplayCollectionName</c>
    /// and <c>Description</c> when set (null leaves them as they are). <c>EntitySetName</c> cannot change.
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown entity or a different <c>EntitySetName</c>; nothing is written.</exception>
    Task UpdateEntityAsync(DataSession session, EntityMetadata changes, CancellationToken ct);

    /// <summary>
    /// <see cref="UpdateEntityAsync(DataSession, EntityMetadata, CancellationToken)"/>; with <paramref name="replaceLabels"/>,
    /// every label given (not null) replaces the current one, an empty one clearing it (the restore of a layer, plan 2.6
    /// ruling R6). Stores that do not support replacing labels refuse it.
    /// </summary>
    /// <exception cref="NotSupportedException">With <paramref name="replaceLabels"/>, on a store that cannot replace labels.</exception>
    Task UpdateEntityAsync(DataSession session, EntityMetadata changes, bool replaceLabels, CancellationToken ct) =>
        replaceLabels ? throw new NotSupportedException("This metadata store cannot replace labels.") : UpdateEntityAsync(session, changes, ct);

    /// <summary>
    /// Updates the attribute named by <paramref name="changes"/>.LogicalName: <c>DisplayName</c>, <c>Description</c> and
    /// <c>RequiredLevel</c> when set; a larger <c>MaxLength</c> (String, Memo; the column grows with it); the options of a
    /// local option set (Picklist, MultiSelect), each added when its value is new and relabelled otherwise (none is removed).
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown entity/attribute, another attribute type, a smaller <c>MaxLength</c>,
    /// an option without value or options on a global option set; nothing is written.</exception>
    Task UpdateAttributeAsync(DataSession session, string entityLogicalName, AttributeMetadata changes, CancellationToken ct);

    /// <summary>
    /// <see cref="UpdateAttributeAsync(DataSession, string, AttributeMetadata, CancellationToken)"/>; with
    /// <paramref name="replaceLabels"/>, every label given (the column's and those of the options given) replaces the current
    /// one, an empty one clearing it, and so does a missing label of an option given (plan 2.6 ruling R6). No option is removed
    /// either way.
    /// </summary>
    /// <exception cref="NotSupportedException">With <paramref name="replaceLabels"/>, on a store that cannot replace labels.</exception>
    Task UpdateAttributeAsync(DataSession session, string entityLogicalName, AttributeMetadata changes, bool replaceLabels, CancellationToken ct) =>
        replaceLabels ? throw new NotSupportedException("This metadata store cannot replace labels.")
            : UpdateAttributeAsync(session, entityLogicalName, changes, ct);

    /// <summary>
    /// Takes the metadata write lock for <paramref name="session"/> (and the version-stamp row lock) ahead of its first
    /// metadata write, so that a unit of work that must write <c>data.*</c> rows before its metadata writes (the cascade
    /// delete of a solution deletes plug-in steps before the tables they are registered on) still takes the metadata locks
    /// before any data lock. The session publishes (a new version stamp) or discards like any other metadata write.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session has no transaction, or another session holds the write lock
    /// on this call path.</exception>
    Task BeginWriteAsync(DataSession session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Once <paramref name="session"/> commits, makes the Mass job of the rollup <paramref name="entityLogicalName"/>.<paramref
    /// name="attributeLogicalName"/> due at once: a solution import wrote its definition, or an uninstall restored it from a lower
    /// layer (plan 2.12b, rulings R8 and R14). Queued after the store's own notification of the write, so that it is the last word.
    /// </summary>
    /// <exception cref="NotSupportedException">A store that cannot schedule rollup jobs (never silently skipped).</exception>
    void NotifyRollupImported(DataSession session, string entityLogicalName, string attributeLogicalName) =>
        throw new NotSupportedException(
            $"This metadata store cannot schedule the jobs of the rollup column '{entityLogicalName}.{attributeLogicalName}'.");

    /// <summary>
    /// Deletes the attribute, the attributes stored in its columns (money <c>_base</c>, <c>AttributeOf</c>) and drops its
    /// column(s).
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown entity or attribute; nothing is written.</exception>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">403 <c>PrivilegeDenied</c>: a system attribute (primary key or
    /// name, generated attributes, attributes of a system entity's definition).</exception>
    Task DeleteAttributeAsync(DataSession session, string entityLogicalName, string attributeLogicalName, CancellationToken ct);

    /// <summary>
    /// Deletes the entity's <c>meta.*</c> rows and drops <c>data.[logicalname]</c>; the <see cref="IEntityDeletedHandler"/>s
    /// remove in the session what describes it elsewhere (privileges, message filters, steps registered on them).
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown entity; nothing is written.</exception>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">403 <c>PrivilegeDenied</c>: a system entity; 400 when a lookup of
    /// another entity targets it or it is a side or the intersect of a many-to-many relationship.</exception>
    Task DeleteEntityAsync(DataSession session, string entityLogicalName, CancellationToken ct);

    // Global option sets (meta.optionset). A Picklist or MultiSelect attribute whose OptionSet has IsGlobal = true and a
    // Name is bound to the global set of that name: meta.attribute keeps only the name, the cache shows the set's options
    // in the attribute (and every change of the set reaches every bound attribute).

    /// <summary>
    /// Creates the global option set <paramref name="optionSet"/> (an <see cref="OptionSetMetadata"/>; <c>IsGlobal</c> is
    /// set). Its <c>MetadataId</c> is derived from the name (a caller value is ignored), so it is the same in every org.
    /// </summary>
    /// <exception cref="XrmdValidationException">Not an <see cref="OptionSetMetadata"/>, an invalid name, an option without
    /// value or two options with the same value; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">A global option set with the same name (ignoring case) exists, or the
    /// session has no transaction.</exception>
    Task CreateOptionSetAsync(DataSession session, OptionSetMetadataBase optionSet, CancellationToken ct);

    /// <summary>
    /// Updates the global option set named by <paramref name="changes"/>.Name: <c>DisplayName</c> and <c>Description</c>
    /// when set; each option is added when its value is new and relabelled otherwise (none is removed).
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown option set or an option without value; nothing is written.</exception>
    Task UpdateOptionSetAsync(DataSession session, OptionSetMetadataBase changes, CancellationToken ct);

    /// <summary>
    /// <see cref="UpdateOptionSetAsync(DataSession, OptionSetMetadataBase, CancellationToken)"/>; with
    /// <paramref name="replaceLabels"/>, every label given (the set's and its options') replaces the current one, an empty one
    /// clearing it, and so does a missing label of an option given (plan 2.6 ruling R6). No option is removed either way.
    /// </summary>
    /// <exception cref="NotSupportedException">With <paramref name="replaceLabels"/>, on a store that cannot replace labels.</exception>
    Task UpdateOptionSetAsync(DataSession session, OptionSetMetadataBase changes, bool replaceLabels, CancellationToken ct) =>
        replaceLabels ? throw new NotSupportedException("This metadata store cannot replace labels.") : UpdateOptionSetAsync(session, changes, ct);

    /// <summary>Deletes the global option set named <paramref name="name"/>.</summary>
    /// <exception cref="XrmdValidationException">Unknown option set; nothing is written.</exception>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 when an attribute is bound to it.</exception>
    Task DeleteOptionSetAsync(DataSession session, string name, CancellationToken ct);

    /// <summary>
    /// Sets <c>IsManaged</c> of the table <paramref name="entityLogicalName"/>, or of its column
    /// <paramref name="attributeLogicalName"/> when one is given (plan 2.6, ruling M7: a component with a managed layer is
    /// managed); null clears it. The columns a custom table is generated with (primary key, system columns) follow the table.
    /// Nothing is written when the flags already have that value. Stores that do not keep the flag refuse it.
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown entity or attribute; nothing is written.</exception>
    /// <exception cref="NotSupportedException">On a store that does not keep <c>IsManaged</c>.</exception>
    Task SetIsManagedAsync(DataSession session, string entityLogicalName, string? attributeLogicalName, bool? isManaged, CancellationToken ct) =>
        throw new NotSupportedException("This metadata store does not keep IsManaged.");

    /// <summary>
    /// Sets <c>IsManaged</c> of the global option set <paramref name="name"/> (plan 2.6, ruling M7); null clears it. Nothing
    /// is written when the flag already has that value. Stores that do not keep the flag refuse it.
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown option set; nothing is written.</exception>
    /// <exception cref="NotSupportedException">On a store that does not keep <c>IsManaged</c>.</exception>
    Task SetOptionSetIsManagedAsync(DataSession session, string name, bool? isManaged, CancellationToken ct) =>
        throw new NotSupportedException("This metadata store does not keep IsManaged.");

    /// <summary>
    /// Sets <c>IsManaged</c> of the saved relationship <paramref name="schemaName"/> (a many-to-many one, or a one-to-many one
    /// created with its lookup; plan 2.6, ruling M14); null clears it. A one-to-many relationship that is only implied by a
    /// lookup has no definition to keep the flag: nothing is written and false is returned. Stores that do not keep the flag
    /// refuse it.
    /// </summary>
    /// <returns>True when the relationship has a saved definition (written, or already with that value).</returns>
    /// <exception cref="NotSupportedException">On a store that does not keep <c>IsManaged</c>.</exception>
    Task<bool> SetRelationshipIsManagedAsync(DataSession session, string schemaName, bool? isManaged, CancellationToken ct) =>
        throw new NotSupportedException("This metadata store does not keep IsManaged.");

    // Relationships (meta.relationship). A lookup attribute with more than one target is polymorphic: AddAttributeAsync and
    // CreateEntityAsync give it a <name>type column (the target's object type code), as ownerid.

    /// <summary>
    /// Creates the many-to-many relationship <paramref name="relationship"/> between <c>Entity1LogicalName</c> and
    /// <c>Entity2LogicalName</c> (the same entity for a self-referential one) and, in the session, its intersect entity
    /// <c>IntersectEntityName</c> (default: the lower-cased SchemaName): <c>IsIntersect = true</c>, organization owned, the
    /// columns <c>&lt;entity1&gt;id</c>/<c>&lt;entity2&gt;id</c> (self-referential: <c>&lt;entity&gt;idone</c>/<c>&lt;entity&gt;idtwo</c>,
    /// set in <c>Entity1IntersectAttribute</c>/<c>Entity2IntersectAttribute</c> whatever the caller sent) with a unique index on
    /// the pair. Navigation property names default to the SchemaName.
    /// </summary>
    /// <exception cref="XrmdValidationException">Invalid names, an unknown entity or an intersect entity as a side; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">A relationship with the same SchemaName (ignoring case) or an entity named
    /// like the intersect exists, or the session has no transaction.</exception>
    Task CreateManyToManyAsync(DataSession session, ManyToManyRelationshipMetadata relationship, CancellationToken ct);

    /// <summary>
    /// Creates the one-to-many relationship <paramref name="relationship"/> (<c>SchemaName</c>, <c>ReferencedEntity</c>,
    /// <c>ReferencingEntity</c>) with its lookup attribute <paramref name="lookup"/> on the referencing entity (its
    /// <c>Targets</c> set to the referenced entity, <c>ReferencingAttribute</c> set to its name): the attribute as
    /// <see cref="AddAttributeAsync(DataSession, string, AttributeMetadata, CancellationToken)"/> adds it, and a
    /// <c>meta.relationship</c> row that keeps the relationship's SchemaName and MetadataId (a new one when null). The row
    /// goes with the lookup (DeleteAttribute, DeleteRelationship) and with either entity.
    /// </summary>
    /// <exception cref="XrmdValidationException">Invalid names, an unknown or intersect entity, an invalid lookup; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">A relationship with the same SchemaName (ignoring case) or the attribute
    /// exists, or the session has no transaction.</exception>
    Task CreateOneToManyAsync(DataSession session, OneToManyRelationshipMetadata relationship, LookupAttributeMetadata lookup,
        CancellationToken ct);

    /// <summary>
    /// Creates a Customer column with its two relationships: the lookup <paramref name="lookup"/> (made a Customer column that
    /// targets <c>account</c> and <c>contact</c>, with its <c>&lt;name&gt;type</c> column) and one saved one-to-many relationship per
    /// target (<paramref name="relationships"/>: two, with the same <c>ReferencingEntity</c> and, as <c>ReferencedEntity</c>,
    /// <c>account</c> and <c>contact</c>), each keeping its SchemaName and MetadataId (a new one when null). Delete cascades
    /// RemoveLink. This is the only way to create a Customer column; <see cref="AddAttributeAsync(DataSession, string, AttributeMetadata, CancellationToken)"/>,
    /// <see cref="CreateEntityAsync(DataSession, EntityMetadata, AttributeMetadata, CancellationToken)"/> and
    /// <see cref="CreateOneToManyAsync"/> refuse one.
    /// </summary>
    /// <exception cref="XrmdValidationException">Invalid names, targets other than exactly account and contact, an unknown or
    /// intersect entity, a cascade xrmverse does not enforce; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">A relationship with the same SchemaName (ignoring case) or the attribute
    /// exists, or the session has no transaction.</exception>
    Task CreateCustomerRelationshipsAsync(DataSession session, LookupAttributeMetadata lookup,
        IReadOnlyList<OneToManyRelationshipMetadata> relationships, CancellationToken ct);

    /// <summary>
    /// Deletes the relationship named <paramref name="schemaName"/> (ignoring case): a many-to-many one with its intersect
    /// entity (table dropped); a one-to-many one of a single-target lookup with that lookup attribute.
    /// </summary>
    /// <exception cref="XrmdValidationException">Unknown relationship, or one target of a polymorphic lookup; nothing is written.</exception>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">403 <c>PrivilegeDenied</c> for the lookup of a system attribute.</exception>
    Task DeleteRelationshipAsync(DataSession session, string schemaName, CancellationToken ct);

    /// <summary>
    /// The associated menu configuration of the saved relationship named <paramref name="schemaName"/> (ignoring case), as
    /// the opaque XML a solution file gave it (<c>EntityRelationshipRoles</c>: the navigation pane settings, kept for the
    /// export); null when it has none or the relationship is not saved.
    /// </summary>
    string? GetRelationshipMenuXml(string schemaName) => null;

    /// <summary>
    /// Sets (null: removes) the associated menu configuration of the saved relationship named <paramref name="schemaName"/>
    /// (a many-to-many one or a one-to-many one created with its own SchemaName); it goes with the relationship.
    /// </summary>
    /// <exception cref="XrmdValidationException">No saved relationship has that name; nothing is written.</exception>
    Task SetRelationshipMenuXmlAsync(DataSession session, string schemaName, string? xml, CancellationToken ct) =>
        throw new NotSupportedException("This metadata store does not keep relationship menus.");

    /// <summary>
    /// What a solution file gave a component that xrmverse does not model, kept opaque for the export (the XML of
    /// <c>Xrmd.Solution.UnmodelledXml</c>); null when it has none. The keys: 1 an entity by logical name, 2 an attribute as
    /// <c>entity.attribute</c>, 9 a global option set by name, 10 a saved relationship by SchemaName (names ignoring case).
    /// </summary>
    string? GetUnmodelledXml(int componentType, string key) => null;

    /// <summary>
    /// The unmodelled XML of a component as <paramref name="session"/> sees it: its own metadata changes included when it has
    /// made some, the published state otherwise. Takes no lock.
    /// </summary>
    string? GetUnmodelledXml(DataSession session, int componentType, string key) => GetUnmodelledXml(componentType, key);

    /// <summary>
    /// Sets (null: removes) the unmodelled XML of a component, as <paramref name="session"/> sees it; it goes with the
    /// component. False, and nothing written, when the component does not exist (or has no row of its own: a relationship
    /// xrmverse derives from a lookup).
    /// </summary>
    Task<bool> SetUnmodelledXmlAsync(DataSession session, int componentType, string key, string? xml, CancellationToken ct) =>
        throw new NotSupportedException("This metadata store does not keep unmodelled XML.");
}
