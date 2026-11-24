using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Sdk;

/// <summary>
/// The SDK view of <see cref="IMetadataStore"/> (S.2b2): the SDK-typed reads and writes of earlier versions, with the same
/// names, types and semantics, over the store's model members. Each read result is the cached SDK view of the model
/// instance (<see cref="SdkViewCache"/>): two reads of the same definition give the same SDK instance until metadata changes, and a
/// change gives a new one. A view is shared by every caller: treat it as read-only, as the store's own instances were.
/// The writes convert their arguments to the model (see below). Transitional: each module moves to the model members in its
/// own step (S.2c to S.2h).
/// </summary>
public static class SdkMetadataView
{
    /// <summary>The view of <see cref="IMetadataStore.GetTable(string)"/>; null if absent.</summary>
    public static EntityMetadata? GetEntity(this IMetadataStore store, string logicalName) =>
        View(store.GetTable(logicalName));

    /// <summary>The view of <see cref="IMetadataStore.GetTable(DataSession, string)"/>; null if absent.</summary>
    public static EntityMetadata? GetEntity(this IMetadataStore store, DataSession session, string logicalName) =>
        View(store.GetTable(session, logicalName));

    /// <summary>The view of <see cref="IMetadataStore.GetTableBySetName"/>; null if absent.</summary>
    public static EntityMetadata? GetEntityBySetName(this IMetadataStore store, string entitySetName) =>
        View(store.GetTableBySetName(entitySetName));

    /// <summary>The view of <see cref="IMetadataStore.GetTableByObjectTypeCode"/>; null if absent.</summary>
    public static EntityMetadata? GetEntityByObjectTypeCode(this IMetadataStore store, int objectTypeCode) =>
        View(store.GetTableByObjectTypeCode(objectTypeCode));

    /// <summary>The view of <see cref="IMetadataStore.GetAllTables"/> (a shared read-only snapshot, ordered by LogicalName).</summary>
    public static IReadOnlyList<EntityMetadata> GetAllEntities(this IMetadataStore store) =>
        SdkViewCache.Of<TableDefinition, EntityMetadata>(store.GetAllTables());

    /// <summary>The view of <see cref="IMetadataStore.GetChoiceSet"/>; null if absent.</summary>
    public static OptionSetMetadataBase? GetOptionSet(this IMetadataStore store, string name) =>
        store.GetChoiceSet(name) is { } choiceSet ? SdkViewCache.Of(choiceSet) : null;

    /// <summary>The view of <see cref="IMetadataStore.GetAllChoiceSets"/> (a shared read-only snapshot, ordered by Name).</summary>
    public static IReadOnlyList<OptionSetMetadataBase> GetAllOptionSets(this IMetadataStore store) =>
        SdkViewCache.Of<ChoiceSetBase, OptionSetMetadataBase>(store.GetAllChoiceSets());

    /// <summary>The view of <see cref="IMetadataStore.GetRelationshipDefinition"/>; null if absent.</summary>
    public static RelationshipMetadataBase? GetRelationship(this IMetadataStore store, string schemaName) =>
        store.GetRelationshipDefinition(schemaName) is { } relationship ? SdkViewCache.Of(relationship) : null;

    /// <summary>The view of <see cref="IMetadataStore.GetManyToManyDefinitions"/> (a shared read-only snapshot).</summary>
    public static IReadOnlyList<ManyToManyRelationshipMetadata> GetManyToManyRelationships(this IMetadataStore store) =>
        SdkViewCache.Of<ManyToManyRelationship, ManyToManyRelationshipMetadata>(store.GetManyToManyDefinitions());

    /// <summary>The view of <see cref="IMetadataStore.GetOneToManyDefinitions"/> (a shared read-only snapshot).</summary>
    public static IReadOnlyList<OneToManyRelationshipMetadata> GetOneToManyRelationships(this IMetadataStore store) =>
        SdkViewCache.Of<OneToManyRelationship, OneToManyRelationshipMetadata>(store.GetOneToManyDefinitions());

    /// <summary>The view of <see cref="IMetadataStore.GetHierarchicalDefinition"/>; null when the table has none.</summary>
    public static OneToManyRelationshipMetadata? GetHierarchicalRelationship(this IMetadataStore store, string entityLogicalName) =>
        store.GetHierarchicalDefinition(entityLogicalName) is { } relationship
            ? (OneToManyRelationshipMetadata)SdkViewCache.Of(relationship)
            : null;

    // Between a model definition and its SDK view, for the SDK edges of the modules on the model (S.2c: Xrmd.Query's Sdk/ twins and
    // callbacks). Transitional: removed with the SDK view (S.2i).

    /// <summary>
    /// The model of <paramref name="view"/>: the model instance a view of this cache was built from (the one the store hands out),
    /// else a fresh conversion, never cached (an SDK object that is not a view belongs to its caller, who may change it).
    /// </summary>
    public static TableDefinition ToModel(this EntityMetadata view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return SdkTwin.Model(view);
    }

    /// <summary>
    /// The model of <paramref name="view"/>: for a column of a view of this cache, the matching column of the model table, else a
    /// fresh conversion, never cached (see <see cref="ToModel(EntityMetadata)"/>).
    /// </summary>
    public static ColumnDefinition ToModel(this AttributeMetadata view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return SdkTwin.Model(view);
    }

    /// <summary>The cached SDK view of <paramref name="table"/>: the instance <see cref="GetEntity(IMetadataStore, string)"/> returns for it.</summary>
    /// <remarks>
    /// Only for a definition the store hands out, which never changes. The view is built once per instance and cached: a table the
    /// caller owns and changes afterwards gets a view that never follows those changes.
    /// </remarks>
    public static EntityMetadata SdkView(this TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return SdkViewCache.Of(table);
    }

    /// <summary>
    /// The SDK view of <paramref name="column"/> of <paramref name="table"/>: the matching column of <see cref="SdkView(TableDefinition)"/>
    /// (so a reader sees one consistent table), else the column's own cached view.
    /// </summary>
    /// <remarks>As <see cref="SdkView(TableDefinition)"/>: only for a definition the store hands out, which never changes.</remarks>
    public static AttributeMetadata SdkView(this TableDefinition table, ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);
        return SdkViewCache.Of(table, column);
    }

    // Writes: the SDK overloads of the store's model writes, with the same names, parameter names and semantics. Each converts its
    // arguments with SdkMetadataBridge.ToModel (a fresh conversion, never cached: a caller may change and send the same instance
    // again) and calls the model member; a null stays null, so the store reports it as before. The caller's objects are not changed.

    /// <summary>The SDK overload of <see cref="IMetadataStore.CreateEntityAsync(TableDefinition, ColumnDefinition, CancellationToken)"/>.</summary>
    public static Task CreateEntityAsync(this IMetadataStore store, EntityMetadata entity, AttributeMetadata primaryName,
        CancellationToken ct = default) =>
        store.CreateEntityAsync(ModelOfArgument(entity), ModelOfArgument(primaryName), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.AddAttributeAsync(string, ColumnDefinition, CancellationToken)"/>.</summary>
    public static Task AddAttributeAsync(this IMetadataStore store, string entityLogicalName, AttributeMetadata attribute,
        CancellationToken ct = default) =>
        store.AddAttributeAsync(entityLogicalName, ModelOfArgument(attribute), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.CreateEntityAsync(DataSession, TableDefinition, ColumnDefinition, CancellationToken)"/>.</summary>
    public static Task CreateEntityAsync(this IMetadataStore store, DataSession session, EntityMetadata entity, AttributeMetadata primaryName,
        CancellationToken ct) =>
        store.CreateEntityAsync(session, ModelOfArgument(entity), ModelOfArgument(primaryName), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.AddAttributeAsync(DataSession, string, ColumnDefinition, CancellationToken)"/>.</summary>
    public static Task AddAttributeAsync(this IMetadataStore store, DataSession session, string entityLogicalName, AttributeMetadata attribute,
        CancellationToken ct) =>
        store.AddAttributeAsync(session, entityLogicalName, ModelOfArgument(attribute), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.UpdateEntityAsync(DataSession, TableDefinition, CancellationToken)"/>.</summary>
    public static Task UpdateEntityAsync(this IMetadataStore store, DataSession session, EntityMetadata changes, CancellationToken ct) =>
        store.UpdateEntityAsync(session, ModelOfArgument(changes), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.UpdateEntityAsync(DataSession, TableDefinition, bool, CancellationToken)"/>.</summary>
    public static Task UpdateEntityAsync(this IMetadataStore store, DataSession session, EntityMetadata changes, bool replaceLabels,
        CancellationToken ct) =>
        store.UpdateEntityAsync(session, ModelOfArgument(changes), replaceLabels, ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.UpdateAttributeAsync(DataSession, string, ColumnDefinition, CancellationToken)"/>.</summary>
    public static Task UpdateAttributeAsync(this IMetadataStore store, DataSession session, string entityLogicalName, AttributeMetadata changes,
        CancellationToken ct) =>
        store.UpdateAttributeAsync(session, entityLogicalName, ModelOfArgument(changes), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.UpdateAttributeAsync(DataSession, string, ColumnDefinition, bool, CancellationToken)"/>.</summary>
    public static Task UpdateAttributeAsync(this IMetadataStore store, DataSession session, string entityLogicalName, AttributeMetadata changes,
        bool replaceLabels, CancellationToken ct) =>
        store.UpdateAttributeAsync(session, entityLogicalName, ModelOfArgument(changes), replaceLabels, ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.CreateOptionSetAsync"/>.</summary>
    public static Task CreateOptionSetAsync(this IMetadataStore store, DataSession session, OptionSetMetadataBase optionSet, CancellationToken ct) =>
        store.CreateOptionSetAsync(session, ModelOfArgument(optionSet), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.UpdateOptionSetAsync(DataSession, ChoiceSetBase, CancellationToken)"/>.</summary>
    public static Task UpdateOptionSetAsync(this IMetadataStore store, DataSession session, OptionSetMetadataBase changes, CancellationToken ct) =>
        store.UpdateOptionSetAsync(session, ModelOfArgument(changes), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.UpdateOptionSetAsync(DataSession, ChoiceSetBase, bool, CancellationToken)"/>.</summary>
    public static Task UpdateOptionSetAsync(this IMetadataStore store, DataSession session, OptionSetMetadataBase changes, bool replaceLabels,
        CancellationToken ct) =>
        store.UpdateOptionSetAsync(session, ModelOfArgument(changes), replaceLabels, ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.CreateManyToManyAsync"/>.</summary>
    public static Task CreateManyToManyAsync(this IMetadataStore store, DataSession session, ManyToManyRelationshipMetadata relationship,
        CancellationToken ct) =>
        store.CreateManyToManyAsync(session, (ManyToManyRelationship)ModelOfArgument(relationship), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.CreateOneToManyAsync"/>.</summary>
    public static Task CreateOneToManyAsync(this IMetadataStore store, DataSession session, OneToManyRelationshipMetadata relationship,
        LookupAttributeMetadata lookup, CancellationToken ct) =>
        store.CreateOneToManyAsync(session, (OneToManyRelationship)ModelOfArgument(relationship), (LookupColumn)ModelOfArgument(lookup), ct);

    /// <summary>The SDK overload of <see cref="IMetadataStore.CreateCustomerRelationshipsAsync"/>.</summary>
    public static Task CreateCustomerRelationshipsAsync(this IMetadataStore store, DataSession session, LookupAttributeMetadata lookup,
        IReadOnlyList<OneToManyRelationshipMetadata> relationships, CancellationToken ct) =>
        store.CreateCustomerRelationshipsAsync(session, (LookupColumn)ModelOfArgument(lookup),
            relationships?.Select(r => (OneToManyRelationship)ModelOfArgument(r)).ToList()!, ct);

    private static TableDefinition ModelOfArgument(EntityMetadata? entity) => entity is null ? null! : SdkMetadataBridge.ToModel(entity);

    private static ColumnDefinition ModelOfArgument(AttributeMetadata? attribute) =>
        attribute is null ? null! : SdkMetadataBridge.ToModel(attribute);

    private static ChoiceSetBase ModelOfArgument(OptionSetMetadataBase? optionSet) =>
        optionSet is null ? null! : SdkMetadataBridge.ToModel(optionSet);

    private static RelationshipDefinition ModelOfArgument(RelationshipMetadataBase? relationship) =>
        relationship is null ? null! : SdkMetadataBridge.ToModel(relationship);

    // For the test fakes of IMetadataStore that keep SDK fixtures (the store keeps the model since S.2b2 Task 4). The model of an
    // SDK fixture is converted once, on the first call, and its view is that same SDK instance: the SDK reads return the fake's
    // own instances. A fixture changed after its first read is not seen.

    /// <summary>The model of <paramref name="entity"/> (cached per instance); its view is <paramref name="entity"/> itself.</summary>
    internal static TableDefinition ModelOf(EntityMetadata entity) => SdkViewCache.ModelOf(entity);

    /// <summary>The model of <paramref name="optionSet"/> (cached per instance); its view is <paramref name="optionSet"/> itself.</summary>
    internal static ChoiceSetBase ModelOf(OptionSetMetadataBase optionSet) => SdkViewCache.ModelOf(optionSet);

    /// <summary>The model of <paramref name="relationship"/> (cached per instance); its view is <paramref name="relationship"/> itself.</summary>
    internal static RelationshipDefinition ModelOf(RelationshipMetadataBase relationship) => SdkViewCache.ModelOf(relationship);

    private static EntityMetadata? View(TableDefinition? table) => table is null ? null : SdkViewCache.Of(table);
}
