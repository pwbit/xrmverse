using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Workflows;

namespace Xrmd.WebApi;

/// <summary>
/// Builds the OData <see cref="IEdmModel"/> (served as <c>$metadata</c>) from the metadata cache: one entity type
/// <c>Microsoft.Dynamics.CRM.{LogicalName}</c> per entity, keyed by its primary id, and one entity set
/// <c>{EntitySetName}</c> in the container <c>System</c>. Lookups (also Owner/Customer) become a
/// <c>_{name}_value</c> <c>Edm.Guid</c> property plus a single-valued navigation property (<see cref="NavigationName"/>) to the first
/// target that exists; a Customer column has one navigation property per target (<see cref="NavigationNames"/>). The metadata entity sets (<c>EntityDefinitions</c>, <c>GlobalOptionSetDefinitions</c>,
/// <c>RelationshipDefinitions</c>) and the solution actions and function of <see cref="ActionController"/> are declared too.
/// The custom APIs of <see cref="CustomApiRegistry.Current"/> are declared by <see cref="CustomApiOperations"/>, then the activated
/// XAML actions of <see cref="IActivatedActions.Current"/> the same way (issue #343).
/// The entity sets of the tracked tables (<see cref="EntityMetadata.ChangeTrackingEnabled"/>, plan 2.13) carry the
/// <c>Org.OData.Capabilities.V1.ChangeTracking</c> annotation with <c>Supported</c> true.
/// The model is cached and rebuilt when <see cref="IMetadataStore.VersionStamp"/>, the generation of the custom APIs or the list of
/// the activated actions changes.
/// </summary>
public sealed class ODataEdmModelBuilder(IMetadataStore store, CustomApiRegistry customApis, ILogger<ODataEdmModelBuilder>? logger = null,
    IActivatedActions? actions = null)
{
    public const string Namespace = "Microsoft.Dynamics.CRM";
    public const string ContainerName = "System";

    private sealed record Snapshot(long VersionStamp, int ApiGeneration, IReadOnlyList<ActionDefinition> Actions, IEdmModel Model);

    private volatile Snapshot? _snapshot;

    public IEdmModel Build()
    {
        // Read the stamp before the entities: the store publishes entities before bumping the stamp, so a model built
        // here is never older than the stamp it is cached under.
        // The custom APIs are read once, with the generation of the snapshot they come from; the pair is the cache key, with the
        // list of the activated actions (a new instance at each load of the activated processes).
        var stamp = store.VersionStamp;
        var (generation, apis) = customApis.Current;
        var xamlActions = actions?.Current ?? [];
        var snapshot = _snapshot;
        if (snapshot is not null && snapshot.VersionStamp == stamp && snapshot.ApiGeneration == generation
            && ReferenceEquals(snapshot.Actions, xamlActions))
            return snapshot.Model;

        var model = Create(store.GetAllEntities(), store.GetOneToManyRelationships(), apis, xamlActions,
            message => logger?.LogWarning("{Message}", message));
        _snapshot = new Snapshot(stamp, generation, xamlActions, model);
        return model;
    }

    /// <summary>Name of the structural property holding a lookup's id: <c>_{name}_value</c>.</summary>
    public static string LookupValueProperty(string lookupLogicalName) => $"_{lookupLogicalName}_value";

    /// <summary>
    /// Name of the single-valued navigation property of a lookup: the <c>ReferencingEntityNavigationPropertyName</c> of its
    /// relationship (<paramref name="relationships"/>: <see cref="IMetadataStore.GetOneToManyRelationships"/>), which defaults
    /// to the lookup's <c>SchemaName</c> for a custom lookup (<c>new_ParentId</c>) as in Dataverse; a system lookup keeps the
    /// logical name. Used by <c>$metadata</c>, <c>$expand</c>, <c>@odata.bind</c> and the <c>associatednavigationproperty</c> annotation.
    /// </summary>
    public static string NavigationName(EntityMetadata entity, LookupAttributeMetadata lookup,
        IEnumerable<OneToManyRelationshipMetadata>? relationships) =>
        EntityMetadataExtensions.ReferencingNavigationName(entity, lookup, relationships);

    /// <summary>
    /// Every single-valued navigation property of <paramref name="lookup"/>: one with no target for a lookup (also
    /// Owner), one per target for a Customer column (<c>parentcustomerid_account</c>, <c>parentcustomerid_contact</c>) and for a
    /// lookup of the activity model (<c>regardingobjectid_account_email</c>, <c>objectid_account</c>, <c>partyid_contact</c>, plan
    /// 2.11), named by the target's relationship; none for a party list (it has no column, plan 2.11 A8). Any other lookup with
    /// several targets whose relationships name a navigation property of their own for each target
    /// (<c>sdkmessageprocessingstep.eventhandler</c>: <c>eventhandler_plugintype</c>, <c>eventhandler_serviceendpoint</c>, plan 3.2)
    /// has one per target too.
    /// </summary>
    public static IReadOnlyList<(string Name, string? Target)> NavigationNames(EntityMetadata entity, LookupAttributeMetadata lookup,
        IEnumerable<OneToManyRelationshipMetadata>? relationships)
    {
        if (ActivitySchema.IsPartyList(lookup)) return [];
        if (CustomerColumns.IsCustomer(lookup) || ActivitySchema.IsManagedLookup(entity.LogicalName, lookup.LogicalName))
            return EntityMetadataExtensions.CustomerNavigations(entity, lookup, relationships).Select(n => (n.Name, (string?)n.Target)).ToList();
        if (lookup.Targets is { Length: > 1 } && relationships is not null)
        {
            var perTarget = EntityMetadataExtensions.CustomerNavigations(entity, lookup, relationships).ToList();
            var own = relationships.Where(r => r.ReferencingEntity == entity.LogicalName && r.ReferencingAttribute == lookup.LogicalName)
                .Select(r => r.ReferencingEntityNavigationPropertyName).ToHashSet(StringComparer.Ordinal);
            if (perTarget.All(n => own.Contains(n.Name)) && perTarget.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count() == perTarget.Count)
                return perTarget.Select(n => (n.Name, (string?)n.Target)).ToList();
        }
        return [(NavigationName(entity, lookup, relationships), null)];
    }

    /// <summary>
    /// The lookup of <paramref name="entity"/> that has the single-valued navigation property <paramref name="navigation"/>, with
    /// the target the navigation property stands for (a Customer column's; null for any other lookup); null when there is none.
    /// </summary>
    public static (LookupAttributeMetadata Lookup, string? Target)? FindNavigation(EntityMetadata entity, string navigation,
        IEnumerable<OneToManyRelationshipMetadata>? relationships)
    {
        var all = relationships?.ToList();
        foreach (var lookup in (entity.Attributes ?? []).OfType<LookupAttributeMetadata>())
            foreach (var (name, target) in NavigationNames(entity, lookup, all))
                if (name == navigation) return (lookup, target);
        return null;
    }

    private static EdmModel Create(IReadOnlyList<EntityMetadata> entities, IReadOnlyList<OneToManyRelationshipMetadata> relationships,
        IReadOnlyList<CustomApiDefinition> customApiDefinitions, IReadOnlyList<ActionDefinition> xamlActions, Action<string> warn)
    {
        var model = new EdmModel();
        var container = new EdmEntityContainer(Namespace, ContainerName);
        model.AddElement(container);

        var types = new Dictionary<string, EdmEntityType>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var type = new EdmEntityType(Namespace, entity.LogicalName);
            AddStructuralProperties(type, entity);
            model.AddElement(type);
            types[entity.LogicalName] = type;
        }

        var sets = new Dictionary<string, EdmEntitySet>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (entity.EntitySetName is not { } setName) continue;
            var set = sets[entity.LogicalName] = container.AddEntitySet(setName, types[entity.LogicalName]);
            // Plan 2.13, ruling C12: a tracked table's entity set accepts odata.track-changes.
            if (entity.ChangeTrackingEnabled == true) model.SetChangeTrackingAnnotation(set, true, [], []);
        }

        foreach (var entity in entities)
        {
            var type = types[entity.LogicalName];
            foreach (var lookup in (entity.Attributes ?? []).OfType<LookupAttributeMetadata>())
            {
                if (!IsExposed(lookup) || ActivitySchema.IsPartyList(lookup)) continue;
                // A lookup has one navigation property, to its first target that exists; a Customer column has one per target.
                var navigations = NavigationNames(entity, lookup, relationships)
                    .Select(n => (n.Name, Target: n.Target ?? (lookup.Targets ?? []).FirstOrDefault(types.ContainsKey)))
                    .Where(n => n.Target is not null && types.ContainsKey(n.Target));
                foreach (var (name, target) in navigations)
                {
                    var targetType = types[target!];
                    var nav = type.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo
                    {
                        Name = name,
                        Target = targetType,
                        TargetMultiplicity = EdmMultiplicity.ZeroOrOne,
                        DependentProperties = [(IEdmStructuralProperty)type.FindProperty(LookupValueProperty(lookup.LogicalName))],
                        PrincipalProperties = targetType.Key(),
                    });
                    if (sets.TryGetValue(entity.LogicalName, out var set) && sets.TryGetValue(target!, out var targetSet))
                        set.AddNavigationTarget(nav, targetSet);
                }
            }
        }

        // The party rows of an activity (plan 2.11): a collection-valued navigation property {activity}_activity_parties. Task 4 of
        // the plan reads and writes it ($expand, deep insert of the party lists); the other one-to-many relationships have none yet.
        foreach (var r in relationships.Where(r => r.ReferencingEntity == ActivitySchema.Party && r.ReferencingAttribute == ActivitySchema.ActivityId))
        {
            if (r.ReferencedEntity is not { } activity || !types.TryGetValue(activity, out var activityType)
                || !types.TryGetValue(ActivitySchema.Party, out var partyType) || r.ReferencedEntityNavigationPropertyName is not { Length: > 0 } name
                || activityType.FindProperty(name) is not null)
                continue;
            var parties = activityType.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo
            {
                Name = name, Target = partyType, TargetMultiplicity = EdmMultiplicity.Many,
            });
            if (sets.TryGetValue(activity, out var activitySet) && sets.TryGetValue(ActivitySchema.Party, out var partySet))
                activitySet.AddNavigationTarget(parties, partySet);
        }

        AddMetadataDefinitions(model, container);
        ActionController.AddOperations(model, container, types);
        RolePrivilegeActions.AddOperations(model, types);
        RecordTeamActions.AddOperations(model, types);
        ExecuteWorkflowAction.AddOperations(model, types);
        EmailActions.AddOperations(model, types);
        AuditEndpoints.AddOperations(model, container, types); // after both: it uses crmbaseentity and RolePrivilege
        DuplicateDetectionActions.AddOperations(model, container, types); // after the audit ones: it uses PagingInfo
        AccessActions.AddOperations(model, container, types); // after the audit ones: it uses AccessRights
        RollupActions.AddOperations(model, container); // after ActionController's: it uses crmbaseentity
        QueueActions.AddOperations(model, container, types); // after ActionController's: it uses crmbaseentity
        FunctionController.AddFormOperations(model, container, types); // after ActionController's: it uses crmbaseentity
        CustomApiOperations.AddOperations(model, container, types, customApiDefinitions, warn); // last: it uses crmbaseentity
        // The activated XAML actions after the custom APIs: a custom API keeps its name (they cannot share one anyway).
        CustomApiOperations.AddOperations(model, container, types, xamlActions.Select(CustomApiEndpoints.AsCustomApi).ToList(), warn, "Action");
        return model;
    }

    /// <summary>
    /// The metadata entity sets served by <see cref="MetadataController"/>, as Dataverse declares them:
    /// <c>EntityDefinitions</c> of <c>EntityMetadata</c>, <c>GlobalOptionSetDefinitions</c> of the abstract
    /// <c>OptionSetMetadataBase</c> and <c>RelationshipDefinitions</c> of the abstract <c>RelationshipMetadataBase</c>, all
    /// derived from the abstract <c>MetadataBase</c> keyed by <c>MetadataId</c>. The types are open: only the key and the
    /// names the endpoints are addressed by are declared; the other properties are those of the JSON the endpoints write.
    /// </summary>
    private static void AddMetadataDefinitions(EdmModel model, EdmEntityContainer container)
    {
        var metadataBase = new EdmEntityType(Namespace, "MetadataBase", null, isAbstract: true, isOpen: true);
        metadataBase.AddKeys(metadataBase.AddStructuralProperty("MetadataId", EdmPrimitiveTypeKind.Guid, isNullable: false));
        model.AddElement(metadataBase);

        EdmEntityType Derived(string name, bool isAbstract, params string[] properties)
        {
            var type = new EdmEntityType(Namespace, name, metadataBase, isAbstract, isOpen: true);
            foreach (var property in properties) type.AddStructuralProperty(property, EdmPrimitiveTypeKind.String);
            model.AddElement(type);
            return type;
        }
        container.AddEntitySet("EntityDefinitions", Derived("EntityMetadata", false, "LogicalName", "SchemaName", "EntitySetName"));
        container.AddEntitySet("GlobalOptionSetDefinitions", Derived("OptionSetMetadataBase", true, "Name"));
        container.AddEntitySet("RelationshipDefinitions", Derived("RelationshipMetadataBase", true, "SchemaName"));
    }

    private static void AddStructuralProperties(EdmEntityType type, EntityMetadata entity)
    {
        var primaryId = SystemAttributes.PrimaryIdName(entity);
        var attributes = entity.Attributes ?? [];

        var key = type.AddStructuralProperty(primaryId, EdmPrimitiveTypeKind.Guid, isNullable: false);
        type.AddKeys(key);

        foreach (var a in attributes)
        {
            if (a.LogicalName == primaryId || !IsExposed(a) || WebApiHiddenAttributes.IsHidden(entity.LogicalName, a.LogicalName)) continue;
            // A party list has no column: its rows are reached through {activity}_activity_parties (plan 2.11 A8, pre-flight C9).
            if (ActivitySchema.IsPartyList(a)) continue;
            if (a is LookupAttributeMetadata)
            {
                type.AddStructuralProperty(LookupValueProperty(a.LogicalName), EdmPrimitiveTypeKind.Guid, isNullable: true);
                continue;
            }
            if (EdmType(a) is { } edmType) type.AddStructuralProperty(a.LogicalName, edmType);
        }
    }

    /// <summary>
    /// Attributes stored in another attribute's column (<c>AttributeOf</c>, e.g. lookup names) are not properties, except the
    /// companions of file and image columns, which Dataverse declares (read-only): those that have a column of their own (e.g.
    /// <c>&lt;file&gt;_name</c>) and the computed url of an image column (<c>&lt;image&gt;_url</c>, e.g. <c>entityimage_url</c>), and
    /// <see cref="IsExposedTypeCompanion"/>.
    /// </summary>
    internal static bool IsExposed(AttributeMetadata a) =>
        a.LogicalName is not null
        && (a.AttributeOf is null || FileColumns.IsStoredCompanion(a) || FileColumns.IsUrlCompanion(a) || IsExposedTypeCompanion(a));

    /// <summary>
    /// Plan 4.1b, ruling P8: <c>queueitem.objectidtypecode</c>, the EntityName companion of <c>objectid</c>, is a Web API property
    /// as in Dataverse (the table's logical name; it reads the type column of <c>objectid</c>, and a written value must name
    /// <c>objectid</c>'s table, plan 2.11 A7). The other EntityName companions (<c>regardingobjecttypecode</c>, …) stay out.
    /// </summary>
    internal static bool IsExposedTypeCompanion(AttributeMetadata a) =>
        a is EntityNameAttributeMetadata { LogicalName: QueueObjectTypeCode, AttributeOf: "objectid" };

    /// <summary>The EntityName companion of <c>queueitem.objectid</c> (<see cref="IsExposedTypeCompanion"/>).</summary>
    internal const string QueueObjectTypeCode = "objectidtypecode";

    /// <summary>EDM type of a non-lookup attribute; null for types without a Web API property mapping.</summary>
    private static IEdmPrimitiveTypeReference? EdmType(AttributeMetadata a)
    {
        var core = EdmCoreModel.Instance;
        return a switch
        {
            StringAttributeMetadata or MemoAttributeMetadata or MultiSelectPicklistAttributeMetadata => core.GetString(true),
            IntegerAttributeMetadata => core.GetInt32(true),
            BigIntAttributeMetadata => core.GetInt64(true),
            // Scale null = "Variable", as Dataverse declares decimal and money properties.
            DecimalAttributeMetadata or MoneyAttributeMetadata => core.GetDecimal(precision: null, scale: null, isNullable: true),
            DoubleAttributeMetadata => core.GetDouble(true),
            BooleanAttributeMetadata => core.GetBoolean(true),
            DateTimeAttributeMetadata => core.GetDateTimeOffset(true),
            UniqueIdentifierAttributeMetadata or FileAttributeMetadata => core.GetGuid(true), // a file column reads as its file id
            ImageAttributeMetadata => core.GetBinary(true),
            PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata => core.GetInt32(true),
            // Plan 2.11 A7: an EntityName column (activitytypecode) holds a table's logical name.
            EntityNameAttributeMetadata => core.GetString(true),
            _ => null,
        };
    }
}
