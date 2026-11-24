using System.Data;
using System.Data.Common;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Solution.Layers;

namespace Xrmd.Core;

/// <summary>
/// The SDK metadata messages, answered outside the plug-in pipeline (as RetrieveEntity): no step runs, so the metadata
/// writes of a session always come before its <c>data.*</c> writes.
/// <list type="bullet">
/// <item>Writes: CreateEntity, CreateAttribute, CreateOneToMany, CreateManyToMany, CreateCustomerRelationships, CreateOptionSet, UpdateEntity,
/// UpdateAttribute, UpdateOptionSet, InsertOptionValue, UpdateOptionValue, DeleteEntity, DeleteAttribute, DeleteRelationship,
/// DeleteOptionSet. Each requires its privilege (<see cref="PrivilegeNames.Metadata"/>; 403 PrivilegeDenied) and runs in
/// a transaction of its own, unless the caller hands it a <see cref="DataSession"/>. A created component is recorded in the
/// <c>Default</c> solution and, with <c>SolutionUniqueName</c>, in that solution too (404 when it does not exist); an
/// updated one (Update*, InsertOptionValue) is added to the <c>SolutionUniqueName</c> solution when one is given; a deleted
/// one leaves every solution (<see cref="SolutionComponentTracker"/>, after the metadata writes); DeleteEntity also deletes
/// the entity's views (<c>savedquery</c>) and forms (<c>systemform</c>), as Dataverse does. Of a system component
/// (an attribute of a seeded entity, or a system attribute of any entity) a caller that is not unrestricted (SYSTEM,
/// System Administrator) may change only the labels (and option colours, UpdateOptionValue included): another change
/// (RequiredLevel, MaxLength, a new option, InsertOptionValue) is 403 PrivilegeDenied. UpdateEntity and UpdateAttribute refuse (400 naming it) every changed
/// property the store does not apply, and CreateEntity what xrmverse does not model (<see cref="MetadataRequestRules"/>); a system entity or attribute is never deleted
/// (403, the store's rule). A many-to-many or one-to-many relationship cannot take the name of a security relationship
/// (400), nor an entity a reserved EntitySetName (400, <see cref="LogicalNameValidator.ReservedEntitySetNames"/>).</item>
/// <item>AddSolutionComponent and RemoveSolutionComponent (<see cref="SolutionComponentMessages"/>), which require
/// <c>prvWriteSolution</c>. Like the other writes, they cannot be executed from a plug-in (400).</item>
/// <item>PublishXml and PublishAllXml: no-ops (metadata is live once committed) that reload the metadata cache; they
/// require <c>prvPublishCustomization</c>.</item>
/// <item>Reads, for every organization member: RetrieveRelationship, RetrieveOptionSet, RetrieveAllOptionSets,
/// RetrieveAllEntities (see <see cref="Retrieve"/>).</item>
/// </list>
/// </summary>
public sealed class MetadataOperations(IMetadataStore store, IDbConnectionFactory factory, SecurityGuard guard,
    SolutionComponentTracker tracker, PublisherPrefixRule prefixes)
{
    /// <summary>First value of an option added without one (the default publisher's option value prefix, 10000).</summary>
    private const int FirstOptionValue = 100_000_000;

    private const string EntityDefinitions = "EntityDefinitions";
    private const string Attributes = "Attributes";
    private const string RelationshipDefinitions = "RelationshipDefinitions";
    private const string GlobalOptionSetDefinitions = "GlobalOptionSetDefinitions";

    /// <summary>The write messages with the privilege each requires and the component kind named in a PrivilegeDenied fault
    /// (InsertOptionValue, UpdateOptionValue: the global option set's; on a local option set they require <c>prvWriteAttribute</c>).</summary>
    private static readonly Dictionary<string, (string Privilege, string Kind)> WriteMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CreateEntity"] = (PrivilegeNames.CreateEntity, EntityDefinitions),
        ["CreateAttribute"] = (PrivilegeNames.CreateAttribute, Attributes),
        ["CreateOneToMany"] = (PrivilegeNames.CreateRelationship, RelationshipDefinitions),
        ["CreateManyToMany"] = (PrivilegeNames.CreateRelationship, RelationshipDefinitions),
        ["CreateCustomerRelationships"] = (PrivilegeNames.CreateRelationship, RelationshipDefinitions),
        ["CreateOptionSet"] = (PrivilegeNames.CreateOptionSet, GlobalOptionSetDefinitions),
        ["UpdateEntity"] = (PrivilegeNames.WriteEntity, EntityDefinitions),
        ["UpdateAttribute"] = (PrivilegeNames.WriteAttribute, Attributes),
        ["UpdateOptionSet"] = (PrivilegeNames.WriteOptionSet, GlobalOptionSetDefinitions),
        ["InsertOptionValue"] = (PrivilegeNames.WriteOptionSet, GlobalOptionSetDefinitions),
        ["UpdateOptionValue"] = (PrivilegeNames.WriteOptionSet, GlobalOptionSetDefinitions),
        ["DeleteEntity"] = (PrivilegeNames.DeleteEntity, EntityDefinitions),
        ["DeleteAttribute"] = (PrivilegeNames.DeleteAttribute, Attributes),
        ["DeleteRelationship"] = (PrivilegeNames.DeleteRelationship, RelationshipDefinitions),
        ["DeleteOptionSet"] = (PrivilegeNames.DeleteOptionSet, GlobalOptionSetDefinitions),
        ["PublishXml"] = (PrivilegeNames.PublishCustomization, "Customization"),
        ["PublishAllXml"] = (PrivilegeNames.PublishCustomization, "Customization"),
        ["AddSolutionComponent"] = (SolutionComponentMessages.Privilege, SolutionComponentMessages.Entity),
        ["RemoveSolutionComponent"] = (SolutionComponentMessages.Privilege, SolutionComponentMessages.Entity),
    };

    private IMetadataStore Store => store;

    private SolutionComponentTracker Tracker => tracker;

    private PublisherPrefixRule Prefixes => prefixes;

    private static readonly HashSet<string> ReadMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "RetrieveRelationship", "RetrieveOptionSet", "RetrieveAllOptionSets", "RetrieveAllEntities",
    };

    /// <summary>The metadata messages (writes, publish and reads).</summary>
    public static IEnumerable<string> Messages => WriteMessages.Keys.Concat(ReadMessages);

    /// <summary>True for a message of <see cref="Messages"/> (ignoring case).</summary>
    public static bool IsMetadataMessage(string? messageName) =>
        messageName is not null && (WriteMessages.ContainsKey(messageName) || ReadMessages.Contains(messageName));

    /// <summary>True for a metadata message that is not a read (writes, PublishXml, PublishAllXml).</summary>
    public static bool IsWriteMessage(string? messageName) => messageName is not null && WriteMessages.ContainsKey(messageName);

    /// <summary>
    /// Runs the metadata message <paramref name="request"/> for <paramref name="caller"/>. A write runs in
    /// <paramref name="session"/> when given (its owner commits and reports the outcome; publishing is then a no-op) and in
    /// a transaction of its own otherwise. Without a session, unknown components are 404 ObjectDoesNotExist and existing
    /// ones (on create) 412 DuplicateRecord, checked against the published metadata; in a session the store checks them
    /// against the session's own view (unknown components are 400).
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied; 400 for a missing or invalid parameter or a component that
    /// cannot be deleted; 404; 412.</exception>
    /// <exception cref="NotSupportedException">Not a metadata message.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, DataSession? session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        var name = request.RequestName ?? "";
        if (ReadMessages.Contains(name)) return Retrieve(store, request);
        if (!WriteMessages.TryGetValue(name, out var demand))
            throw new NotSupportedException($"Request '{request.RequestName}' is not a metadata message.");
        if (session is { Transaction: null })
            throw new InvalidOperationException("Metadata writes need a transaction: the session has none.");

        var message = Canonical(name);
        if (message is "InsertOptionValue" or "UpdateOptionValue" && !IsGlobalTarget(request))
            demand = (PrivilegeNames.WriteAttribute, Attributes);
        await guard.DemandPrivilegeAsync(caller, demand.Privilege, demand.Kind, ct);
        // A Customer column is an attribute as well as two relationships.
        if (message == CustomerColumns.CreateMessage) await guard.DemandPrivilegeAsync(caller, PrivilegeNames.CreateAttribute, Attributes, ct);

        if (message is "PublishXml" or "PublishAllXml")
        {
            // Committed metadata is already live: publishing only reloads the cache (not from inside a session).
            if (session is null) await store.LoadAsync(ct);
            return message == "PublishXml"
                ? new PublishXmlResponse { ResponseName = message }
                : new PublishAllXmlResponse { ResponseName = message };
        }

        // Turning field security off on a column needs read, create and update on it (final review I1), checked before any write.
        if (message == "UpdateAttribute" && request.Parameters.TryGetValue("Attribute", out var changed)
            && changed is AttributeMetadata { IsSecured: false, LogicalName: { } changedName }
            && request.Parameters.TryGetValue("EntityName", out var owner) && owner is string ownerName
            && store.GetEntity(ownerName)?.Attributes.FirstOrDefault(a => a.LogicalName == changedName) is { IsSecured: true })
            await guard.DemandUnsecureAsync(caller, ownerName, changedName, ct);

        var write = new Write(this, request, message, Checked: session is null, Unrestricted: await guard.IsAdministratorAsync(caller, ct),
            Internal: caller == Caller.Internal, Bypass: ManagedProperties.Bypassed(caller));
        if (session is not null) return await write.RunAsync(session, ct);
        return await InOwnSessionAsync(write.RunAsync, ct);
    }

    /// <summary>InsertOptionValue or UpdateOptionValue on a global option set (a non-empty <c>OptionSetName</c>), else on an
    /// attribute's local one.</summary>
    private static bool IsGlobalTarget(OrganizationRequest request) =>
        request.Parameters.TryGetValue("OptionSetName", out var name) && name is string { Length: > 0 };

    /// <summary>
    /// A system component: an attribute of a system (seeded) entity, or a system attribute of any entity (primary key and
    /// name, generated attributes). Only an unrestricted caller (SYSTEM, System Administrator) may change more than its labels.
    /// </summary>
    private static bool IsSystemComponent(EntityMetadata entity, AttributeMetadata attribute) =>
        MetadataStore.IsSystemEntity(entity) || MetadataStore.IsSystemAttribute(entity, attribute);

    private static XrmdFaultException OnlyLabels(EntityMetadata entity, AttributeMetadata attribute, string change) =>
        new(ErrorCodes.PrivilegeDenied,
            $"Attribute '{attribute.LogicalName}' of entity '{entity.LogicalName}' is a system component: only its labels can be changed ({change} was requested).",
            403);

    private static string Canonical(string name) => WriteMessages.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs a metadata create of a front end that writes through the store itself (the Web API's <c>POST EntityDefinitions</c>,
    /// whose body carries attributes the SDK message does not take) in a transaction of its own, and records the created
    /// component in <c>Default</c> and in <paramref name="solutionUniqueName"/> (404 when it does not exist) in the same
    /// transaction, after the metadata writes. The caller has checked the privileges.
    /// </summary>
    /// <param name="write">The store write.</param>
    /// <param name="componentType">The created component's type.</param>
    /// <param name="objectId">The created component's id.</param>
    /// <param name="solutionUniqueName">The solution to record it in too; null for <c>Default</c> only.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="addedTo">For a new column: the table it is added to and how a refusal names the column. Refused (400)
    /// when a managed solution delivers the table with <c>IsCustomizable</c> false (plan 2.6, ruling M13).</param>
    public Task CreateTrackedAsync(Func<DataSession, CancellationToken, Task> write, int componentType, Guid objectId,
        string? solutionUniqueName, CancellationToken ct, (string Table, string Component)? addedTo = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        return InOwnSessionAsync(async (session, token) =>
        {
            // A front end's create: never a layer operation.
            if (addedTo is { } added && store.GetEntity(added.Table) is { MetadataId: { } tableId }
                && await ManagedProperties.StateAsync(session, ComponentType.Entity, tableId, token) is { Flags.IsCustomizable: false } state)
                throw ManagedProperties.TableNotCustomizable(added.Component, added.Table, state);
            await write(session, token);
            await tracker.TrackAsync(session, componentType, objectId, solutionUniqueName, token);
            return new OrganizationResponse();
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="write"/> in a session on a connection and transaction of its own, committed when it succeeds and
    /// rolled back otherwise; the outcome is reported to the session (publishing or discarding the metadata changes).
    /// </summary>
    private async Task<OrganizationResponse> InOwnSessionAsync(Func<DataSession, CancellationToken, Task<OrganizationResponse>> write,
        CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        OrganizationResponse response;
        try
        {
            response = await write(session, ct);
            // CancellationToken.None: once everything is written, a cancelled caller must not leave the commit half done.
            await tx.CommitAsync(CancellationToken.None);
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The transaction is already gone (doomed and rolled back by the server): nothing is committed.
            }
            session.RolledBack();
            throw;
        }
        session.Committed();
        return response;
    }

    /// <summary>One write message on a session.</summary>
    /// <param name="Checked">Check existence against the published metadata first (404/412); false in a caller's session.</param>
    /// <param name="Unrestricted">The caller may change system components beyond their labels.</param>
    /// <param name="Internal">The caller is <see cref="Caller.Internal"/> (a layer operation): no Active marker (ruling M4).</param>
    /// <param name="Bypass">The managed properties and the managed-component rules do not apply (plan 2.6, rulings M13, M14 and
    /// R2: <see cref="ManagedProperties.Bypassed"/>).</param>
    private sealed record Write(MetadataOperations Ops, OrganizationRequest Request, string Message, bool Checked, bool Unrestricted,
        bool Internal, bool Bypass)
    {
        private IMetadataStore Store => Ops.Store;

        public async Task<OrganizationResponse> RunAsync(DataSession session, CancellationToken ct)
        {
            switch (Message)
            {
                case "AddSolutionComponent" or "RemoveSolutionComponent":
                    return await new SolutionComponentMessages(Store, Ops.Tracker).ExecuteAsync(Request, session, ct);
                case "CreateEntity": return await CreateEntityAsync(session, ct);
                case "CreateAttribute": return await CreateAttributeAsync(session, ct);
                case "CreateOneToMany": return await CreateOneToManyAsync(session, ct);
                case "CreateManyToMany": return await CreateManyToManyAsync(session, ct);
                case "CreateCustomerRelationships": return await CreateCustomerRelationshipsAsync(session, ct);
                case "CreateOptionSet": return await CreateOptionSetAsync(session, ct);
                case "UpdateEntity":
                    var entity = MetadataSerializer.Clone(Parameter<EntityMetadata>("Entity"));
                    EnsureEntity(entity.LogicalName);
                    var currentEntity = Store.GetEntity(entity.LogicalName ?? string.Empty) ?? throw NotPublished("Entity", entity.LogicalName);
                    // The type is fixed at creation (plan 3.4): any case of the same value passes; the store refuses a change.
                    if (entity.TableType is not null && ElasticTables.NormalizeTableType(entity.TableType) == ElasticTables.NormalizeTableType(currentEntity.TableType))
                        entity.TableType = ElasticTables.NormalizeTableType(currentEntity.TableType);
                    // Compared with the definition as a read shows it (with the settings the export writes).
                    if (MetadataRequestRules.UnappliedEntityChanges(ExportedSettings.Entity(Store, currentEntity, attributes: false), entity) is { Count: > 0 } entityChanges)
                        throw Unapplied("UpdateEntity", $"entity '{entity.LogicalName}'", entityChanges);
                    if (await ManagedAsync(session, ComponentType.Entity, currentEntity.MetadataId, ct) is { } managedEntity)
                    {
                        var what = $"table '{entity.LogicalName}'";
                        if (!managedEntity.Flags.IsCustomizable) throw ManagedProperties.NotCustomizable(what, managedEntity);
                        if (!managedEntity.Flags.IsRenameable
                            && (Renamed(entity.DisplayName, currentEntity.DisplayName) || Renamed(entity.DisplayCollectionName, currentEntity.DisplayCollectionName)))
                            throw ManagedProperties.NotRenameable(what, managedEntity);
                    }
                    await Store.UpdateEntityAsync(session, entity, ct);
                    await TrackChangedAsync(session, ComponentType.Entity, currentEntity.MetadataId, ct);
                    return new UpdateEntityResponse { ResponseName = Message };
                case "UpdateAttribute":
                    var entityName = String("EntityName");
                    var attribute = MetadataSerializer.Clone(Parameter<AttributeMetadata>("Attribute"));
                    EnsureAttribute(entityName, attribute.LogicalName);
                    if (Published(entityName, attribute.LogicalName) is null && IsPrimaryImageClaim(attribute))
                    {
                        // A create ignores IsPrimaryImage = true when the table already has an image column, so making
                        // an image created in this transaction primary takes an update; the store applies it to its
                        // pending definition. Any other change still needs the published definition to compare with.
                        await Store.UpdateAttributeAsync(session, entityName, attribute, ct);
                        return new UpdateAttributeResponse { ResponseName = Message };
                    }
                    var (owner, published) = Published(entityName, attribute.LogicalName) ?? throw NotPublished("Attribute", $"{entityName}.{attribute.LogicalName}");
                    var current = ExportedSettings.Attribute(Store, entityName, published); // as a read shows it
                    if (attribute.GetType() == current.GetType())
                    {
                        if (MetadataRequestRules.UnappliedAttributeChanges(current, attribute, labelsOnly: false) is { Count: > 0 } attributeChanges)
                            throw Unapplied("UpdateAttribute", $"attribute '{entityName}.{attribute.LogicalName}'", attributeChanges);
                        if (!Unrestricted && IsSystemComponent(owner, current)
                            && MetadataRequestRules.UnappliedAttributeChanges(current, attribute, labelsOnly: true) is [var change, ..])
                            throw OnlyLabels(owner, current, change);
                        MetadataRequestRules.RemoveUnchangedOptions(current, attribute);
                    }
                    if (await ManagedAsync(session, ComponentType.Attribute, published.MetadataId, ct) is { } managedAttribute)
                    {
                        var what = $"attribute '{entityName}.{attribute.LogicalName}'";
                        if (!managedAttribute.Flags.IsCustomizable) throw ManagedProperties.NotCustomizable(what, managedAttribute);
                        if (!managedAttribute.Flags.IsRenameable && Renamed(attribute.DisplayName, published.DisplayName))
                            throw ManagedProperties.NotRenameable(what, managedAttribute);
                        if (!managedAttribute.Flags.CanModifyRequirementLevel && attribute.RequiredLevel?.Value is { } level
                            && level != (published.RequiredLevel?.Value ?? AttributeRequiredLevel.None))
                            throw ManagedProperties.RequirementLevelFixed(what, managedAttribute);
                    }
                    await Store.UpdateAttributeAsync(session, entityName, attribute, ct);
                    await TrackChangedAsync(session, ComponentType.Attribute, Published(entityName, attribute.LogicalName)?.Attribute.MetadataId, ct);
                    return new UpdateAttributeResponse { ResponseName = Message };
                case "UpdateOptionSet":
                    var optionSet = MetadataSerializer.Clone(Parameter<OptionSetMetadataBase>("OptionSet"));
                    EnsureOptionSet(optionSet.Name);
                    if (optionSet is OptionSetMetadata givenSet)
                    {
                        var publishedSet = Store.GetOptionSet(givenSet.Name ?? "") as OptionSetMetadata ?? throw NotPublished("Global option set", givenSet.Name);
                        if (MetadataRequestRules.UnappliedOptionSetChanges(publishedSet, givenSet) is { Count: > 0 } optionSetChanges)
                            throw Unapplied("UpdateOptionSet", $"global option set '{givenSet.Name}'", optionSetChanges);
                    }
                    await DemandCustomizableAsync(session, ComponentType.OptionSet, OptionSetIdOf(optionSet.Name), $"global option set '{optionSet.Name}'", ct);
                    await Store.UpdateOptionSetAsync(session, optionSet, ct);
                    await TrackChangedAsync(session, ComponentType.OptionSet, OptionSetIdOf(optionSet.Name), ct);
                    return new UpdateOptionSetResponse { ResponseName = Message };
                case "InsertOptionValue": return await InsertOptionValueAsync(session, ct);
                case "UpdateOptionValue": return await UpdateOptionValueAsync(session, ct);
                case "DeleteEntity": return await DeleteEntityAsync(session, ct);
                case "DeleteAttribute":
                    var deletedFrom = String("EntityLogicalName");
                    var deleted = String("LogicalName");
                    EnsureAttribute(deletedFrom, deleted);
                    await DemandDeletableAsync(session, ComponentType.Attribute,
                        Store.GetEntity(deletedFrom)?.Attributes?.FirstOrDefault(a => a.LogicalName == deleted)?.MetadataId,
                        $"attribute '{deletedFrom}.{deleted}'", ct);
                    var attributeComponents = AttributeComponents(deletedFrom, deleted);
                    await Store.DeleteAttributeAsync(session, deletedFrom, deleted, ct);
                    await Ops.Tracker.RemoveDeletedAsync(session, attributeComponents, ct);
                    return new DeleteAttributeResponse { ResponseName = Message };
                case "DeleteRelationship":
                    var relationship = String("Name");
                    if (Checked && Store.GetRelationship(relationship) is null)
                        throw NotFound($"Could not find relationship with SchemaName '{relationship}'.");
                    await DemandDeletableAsync(session, ComponentType.EntityRelationship, Store.GetRelationship(relationship)?.MetadataId,
                        $"relationship '{relationship}'", ct);
                    var relationshipComponents = RelationshipComponents(relationship);
                    await Store.DeleteRelationshipAsync(session, relationship, ct);
                    await Ops.Tracker.RemoveDeletedAsync(session, relationshipComponents, ct);
                    return new DeleteRelationshipResponse { ResponseName = Message };
                case "DeleteOptionSet":
                    var optionSetName = String("Name");
                    EnsureOptionSet(optionSetName);
                    await DemandDeletableAsync(session, ComponentType.OptionSet, OptionSetIdOf(optionSetName), $"global option set '{optionSetName}'", ct);
                    await Store.DeleteOptionSetAsync(session, optionSetName, ct);
                    await Ops.Tracker.RemoveDeletedAsync(session, [MetadataStore.OptionSetId(optionSetName)], ct);
                    return new DeleteOptionSetResponse { ResponseName = Message };
                default:
                    throw new NotSupportedException($"Request '{Message}' is not supported yet.");
            }
        }

        private async Task<OrganizationResponse> CreateEntityAsync(DataSession session, CancellationToken ct)
        {
            var entity = MetadataSerializer.Clone(Parameter<EntityMetadata>("Entity"));
            if (Parameter<AttributeMetadata>("PrimaryAttribute") is not StringAttributeMetadata primaryAttribute)
                throw XrmdFaultException.InvalidArgument("PrimaryAttribute must be a StringAttributeMetadata.");
            var primary = MetadataSerializer.Clone(primaryAttribute);
            ServerOwnedProperties.Clear(entity);
            ServerOwnedProperties.Clear(primary);
            // The request's HasActivities and HasNotes (plan 2.11 A5): the SDK sends both, false unless the caller set them, so only
            // true asks for something; it turns the setting on as the entity's own would.
            if (Request.Parameters.TryGetValue("HasActivities", out var hasActivities) && hasActivities is true) entity.HasActivities = true;
            if (Request.Parameters.TryGetValue("HasNotes", out var hasNotes) && hasNotes is true) entity.HasNotes = true;
            LogicalNameValidator.EnsureValid(entity.LogicalName, "entity");
            Ops.Prefixes.DemandEntity(entity, primary);
            MetadataRequestRules.DemandSupportedEntity(entity, isNew: true);
            MetadataRequestRules.DemandOnlyModelledSettings(entity, primary.LogicalName);
            foreach (var given in (entity.Attributes ?? []).Append(primary))
                MetadataRequestRules.DemandOnlyModelledAttributeSettings(given, entity.LogicalName);
            entity.EntitySetName ??= entity.LogicalName + "s";
            if (!LogicalNameValidator.IsValidEntitySetName(entity.EntitySetName))
                throw XrmdFaultException.InvalidArgument(LogicalNameValidator.InvalidEntitySetNameMessage(entity.EntitySetName));
            if (Checked)
            {
                if (Store.GetEntity(entity.LogicalName) is not null
                    || (entity.EntitySetName is { } set && Store.GetAllEntities().Any(e => string.Equals(e.EntitySetName, set, StringComparison.OrdinalIgnoreCase))))
                    throw Duplicate($"An entity with LogicalName '{entity.LogicalName}' or EntitySetName '{entity.EntitySetName}' already exists.");
                // Privilege names are built from the SchemaName (compared ignoring case): two entities must not share one.
                var schemaName = entity.SchemaName ?? entity.LogicalName;
                if (Store.GetAllEntities().FirstOrDefault(e =>
                        string.Equals(e.SchemaName ?? e.LogicalName, schemaName, StringComparison.OrdinalIgnoreCase)) is { } clash)
                    throw XrmdFaultException.InvalidArgument(
                        $"SchemaName '{schemaName}' is already used by entity '{clash.LogicalName}' (SchemaName is unique, ignoring case).");
            }

            entity.MetadataId = Guid.NewGuid();
            primary.MetadataId = Guid.NewGuid();
            await CreatingAsync(() => Store.CreateEntityAsync(session, entity, primary, ct));
            await TrackCreatedAsync(session, ComponentType.Entity, entity.MetadataId.Value, ct);
            return new CreateEntityResponse
            {
                ResponseName = Message, Results = { ["EntityId"] = entity.MetadataId.Value, ["AttributeId"] = primary.MetadataId.Value },
            };
        }

        private async Task<OrganizationResponse> CreateAttributeAsync(DataSession session, CancellationToken ct)
        {
            var entityName = String("EntityName");
            var attribute = MetadataSerializer.Clone(Parameter<AttributeMetadata>("Attribute"));
            ServerOwnedProperties.Clear(attribute);
            if (CustomerColumns.IsCustomer(attribute))
                throw XrmdFaultException.InvalidArgument(CustomerColumns.OnlyViaMessage($"Attribute '{entityName}.{attribute.LogicalName}'"));
            LogicalNameValidator.EnsureValid(attribute.LogicalName, "attribute"); // a missing or malformed name before the prefix rule
            Ops.Prefixes.Demand(attribute.LogicalName, "column");
            MetadataRequestRules.DemandOnlyModelledAttributeSettings(attribute, entityName);
            if (Checked)
            {
                var entity = EnsureEntity(entityName)!;
                if ((entity.Attributes ?? []).Any(a => a.LogicalName == attribute.LogicalName))
                    throw Duplicate($"Attribute '{attribute.LogicalName}' already exists on entity '{entityName}'.");
            }

            await DemandTableCustomizableAsync(session, entityName, $"attribute '{attribute.LogicalName}'", ct);
            attribute.MetadataId = Guid.NewGuid();
            await CreatingAsync(() => Store.AddAttributeAsync(session, entityName, attribute, ct));
            await TrackCreatedAsync(session, ComponentType.Attribute, attribute.MetadataId.Value, ct);
            return new CreateAttributeResponse { ResponseName = Message, Results = { ["AttributeId"] = attribute.MetadataId.Value } };
        }

        private async Task<OrganizationResponse> CreateOneToManyAsync(DataSession session, CancellationToken ct)
        {
            var relationship = MetadataSerializer.Clone(Parameter<OneToManyRelationshipMetadata>("OneToManyRelationship"));
            if (Parameter<AttributeMetadata>("Lookup") is not LookupAttributeMetadata lookupAttribute)
                throw XrmdFaultException.InvalidArgument("Lookup must be a LookupAttributeMetadata.");
            var lookup = MetadataSerializer.Clone(lookupAttribute);
            ServerOwnedProperties.Clear(lookup);
            if (CustomerColumns.IsCustomer(lookup))
                throw XrmdFaultException.InvalidArgument(CustomerColumns.OnlyViaMessage($"Lookup '{relationship.ReferencingEntity}.{lookup.LogicalName}'"));
            MetadataRequestRules.DemandOnlyModelledAttributeSettings(lookup, relationship.ReferencingEntity);
            EnsureNotSecurityRelationship(relationship.SchemaName);
            if (relationship.ReferencingEntity == FileColumns.AttachmentEntity && lookup.LogicalName == FileColumns.AttachmentLookup)
                throw XrmdFaultException.InvalidArgument(FileColumns.ManagedMessage($"Lookup '{relationship.ReferencingEntity}.{lookup.LogicalName}'"));
            LogicalNameValidator.EnsureValid(lookup.LogicalName, "attribute"); // a missing or malformed name before the prefix rule
            Ops.Prefixes.Demand(lookup.LogicalName, "column");
            // The relationship's name is the caller's only when it gave one; otherwise the store derives it from the lookup.
            if (!string.IsNullOrEmpty(relationship.SchemaName)) Ops.Prefixes.Demand(relationship.SchemaName, "relationship");
            if (Checked)
            {
                EnsureNewRelationship(relationship.SchemaName);
                var referencing = EnsureEntity(relationship.ReferencingEntity)!;
                EnsureEntity(relationship.ReferencedEntity);
                if ((referencing.Attributes ?? []).Any(a => a.LogicalName == lookup.LogicalName))
                    throw Duplicate($"Attribute '{lookup.LogicalName}' already exists on entity '{referencing.LogicalName}'.");
            }

            var oneToMany = $"relationship '{(string.IsNullOrEmpty(relationship.SchemaName) ? lookup.LogicalName : relationship.SchemaName)}'";
            await DemandTableCustomizableAsync(session, relationship.ReferencingEntity, oneToMany, ct);
            await DemandTableCustomizableAsync(session, relationship.ReferencedEntity, oneToMany, ct);
            relationship.MetadataId = Guid.NewGuid();
            lookup.MetadataId = Guid.NewGuid();
            await CreatingAsync(() => Store.CreateOneToManyAsync(session, relationship, lookup, ct));
            await TrackCreatedAsync(session, ComponentType.EntityRelationship, relationship.MetadataId.Value, ct);
            await TrackCreatedAsync(session, ComponentType.Attribute, lookup.MetadataId.Value, ct);
            return new CreateOneToManyResponse
            {
                ResponseName = Message,
                Results = { ["RelationshipId"] = relationship.MetadataId.Value, ["AttributeId"] = lookup.MetadataId.Value },
            };
        }

        private async Task<OrganizationResponse> CreateCustomerRelationshipsAsync(DataSession session, CancellationToken ct)
        {
            if (Parameter<AttributeMetadata>("Lookup") is not LookupAttributeMetadata lookupAttribute)
                throw XrmdFaultException.InvalidArgument("Lookup must be a LookupAttributeMetadata.");
            var lookup = MetadataSerializer.Clone(lookupAttribute);
            ServerOwnedProperties.Clear(lookup);
            if (!Request.Parameters.TryGetValue("OneToManyRelationships", out var given) || given is not OneToManyRelationshipMetadata[] sent)
                throw XrmdFaultException.InvalidArgument(
                    "OneToManyRelationships must be an array of the two OneToManyRelationshipMetadata, one to account and one to contact.");
            if (sent.Length != 2 || sent.Any(r => r is null))
                throw XrmdFaultException.InvalidArgument(
                    "OneToManyRelationships must hold exactly two OneToManyRelationshipMetadata, one to account and one to contact.");
            var relationships = sent.Select(MetadataSerializer.Clone).ToList();
            var referencing = relationships.FirstOrDefault()?.ReferencingEntity;
            // The relationships' SchemaNames are the caller's (the rule applies to each), the navigation names are not names of a component.
            LogicalNameValidator.EnsureValid(lookup.LogicalName, "attribute"); // a missing or malformed name before the prefix rule
            Ops.Prefixes.Demand(lookup.LogicalName, "column");
            foreach (var relationship in relationships.Where(r => !string.IsNullOrEmpty(r.SchemaName)))
                Ops.Prefixes.Demand(relationship.SchemaName, "relationship");
            MetadataRequestRules.DemandOnlyModelledAttributeSettings(lookup, referencing);
            foreach (var relationship in relationships) EnsureNotSecurityRelationship(relationship.SchemaName);
            if (Checked)
            {
                foreach (var relationship in relationships) EnsureNewRelationship(relationship.SchemaName);
                var entity = EnsureEntity(referencing)!;
                foreach (var target in CustomerColumns.Targets) EnsureEntity(target);
                if ((entity.Attributes ?? []).Any(a => a.LogicalName == lookup.LogicalName))
                    throw Duplicate($"Attribute '{lookup.LogicalName}' already exists on entity '{entity.LogicalName}'.");
            }

            await DemandTableCustomizableAsync(session, referencing, $"attribute '{lookup.LogicalName}'", ct);
            foreach (var target in CustomerColumns.Targets)
                await DemandTableCustomizableAsync(session, target, $"attribute '{lookup.LogicalName}'", ct);
            lookup.MetadataId = Guid.NewGuid();
            foreach (var relationship in relationships) relationship.MetadataId = Guid.NewGuid();
            await CreatingAsync(() => Store.CreateCustomerRelationshipsAsync(session, lookup, relationships, ct));
            await TrackCreatedAsync(session, ComponentType.Attribute, lookup.MetadataId.Value, ct);
            foreach (var relationship in relationships)
                await TrackCreatedAsync(session, ComponentType.EntityRelationship, relationship.MetadataId!.Value, ct);
            return new CreateCustomerRelationshipsResponse
            {
                ResponseName = Message,
                Results = { ["AttributeId"] = lookup.MetadataId.Value, ["RelationshipIds"] = relationships.Select(r => r.MetadataId!.Value).ToArray() },
            };
        }

        private async Task<OrganizationResponse> CreateManyToManyAsync(DataSession session, CancellationToken ct)
        {
            var relationship = MetadataSerializer.Clone(Parameter<ManyToManyRelationshipMetadata>("ManyToManyRelationship"));
            EnsureNotSecurityRelationship(relationship.SchemaName);
            if (Request.Parameters.TryGetValue("IntersectEntitySchemaName", out var value) && value is string { Length: > 0 } intersect)
                relationship.IntersectEntityName ??= intersect.ToLowerInvariant();
            // The intersect table named by the caller is a custom table; one derived from the relationship name is generated.
            if (!string.IsNullOrEmpty(relationship.SchemaName)) Ops.Prefixes.Demand(relationship.SchemaName, "relationship");
            if (!string.IsNullOrEmpty(relationship.IntersectEntityName)) Ops.Prefixes.Demand(relationship.IntersectEntityName, "table");
            if (Checked)
            {
                EnsureNewRelationship(relationship.SchemaName);
                EnsureEntity(relationship.Entity1LogicalName);
                EnsureEntity(relationship.Entity2LogicalName);
                var intersectName = relationship.IntersectEntityName ?? relationship.SchemaName?.ToLowerInvariant();
                if (intersectName is not null && Store.GetEntity(intersectName) is not null)
                    throw Duplicate($"An entity with LogicalName '{intersectName}' already exists.");
            }

            var manyToMany = $"relationship '{relationship.SchemaName}'";
            await DemandTableCustomizableAsync(session, relationship.Entity1LogicalName, manyToMany, ct);
            await DemandTableCustomizableAsync(session, relationship.Entity2LogicalName, manyToMany, ct);
            relationship.MetadataId = Guid.NewGuid();
            await CreatingAsync(() => Store.CreateManyToManyAsync(session, relationship, ct));
            await TrackCreatedAsync(session, ComponentType.EntityRelationship, relationship.MetadataId.Value, ct);
            return new CreateManyToManyResponse
            {
                ResponseName = Message, Results = { ["ManyToManyRelationshipId"] = relationship.MetadataId.Value },
            };
        }

        private async Task<OrganizationResponse> CreateOptionSetAsync(DataSession session, CancellationToken ct)
        {
            var optionSet = MetadataSerializer.Clone(Parameter<OptionSetMetadataBase>("OptionSet"));
            Ops.Prefixes.Demand(optionSet.Name, "choice");
            if (Checked && optionSet.Name is not null && Store.GetOptionSet(optionSet.Name) is not null)
                throw Duplicate($"A global option set named '{optionSet.Name}' already exists.");

            await CreatingAsync(() => Store.CreateOptionSetAsync(session, optionSet, ct));
            await TrackCreatedAsync(session, ComponentType.OptionSet, MetadataStore.OptionSetId(optionSet.Name!), ct);
            return new CreateOptionSetResponse
            {
                // The store derives the id from the name (the same in every org).
                ResponseName = Message, Results = { ["OptionSetId"] = MetadataStore.OptionSetId(optionSet.Name!) },
            };
        }

        /// <summary>
        /// Adds one option to a global option set (<c>OptionSetName</c>) or to the local option set of a Picklist or
        /// MultiSelect attribute (<c>EntityLogicalName</c>, <c>AttributeLogicalName</c>); without <c>Value</c>, the next
        /// value from <see cref="FirstOptionValue"/>. A value already in the set is 412 DuplicateRecord. A non-empty
        /// <c>Color</c> parameter (the SDK request has no property for it) is the option's colour.
        /// </summary>
        private async Task<OrganizationResponse> InsertOptionValueAsync(DataSession session, CancellationToken ct)
        {
            var label = Parameter<Label>("Label");
            var description = OptionalParameter<Label>("Description");
            int? given = Request.Parameters.TryGetValue("Value", out var v) && v is int value ? value : null;
            var color = ColorParameter();
            DemandNoUnsupportedOptionParameters();

            var target = OptionTargetOf(update: false);
            var values = (target.OptionSet?.Options ?? []).Select(x => x.Value).OfType<int>().ToList();
            if (given is { } taken && values.Contains(taken))
                throw Duplicate($"The value {taken} is already used by an option of the option set.");
            var newValue = given ?? Math.Max(FirstOptionValue, values.Count == 0 ? FirstOptionValue : values.Max() + 1);
            var option = new OptionMetadata(WithUserLabel(MetadataSerializer.Clone(label)), newValue);
            if (description is not null) option.Description = WithUserLabel(MetadataSerializer.Clone(description));
            if (color is { Length: > 0 }) option.Color = color;
            OptionColor.Demand(target.Owner, option);

            await WriteOptionAsync(session, target, option, replaceLabels: false, ct);
            return new InsertOptionValueResponse { ResponseName = Message, Results = { ["NewOptionValue"] = newValue } };
        }

        /// <summary>
        /// Changes the label, description and colour of one option (<c>Value</c>, required) of a global option set
        /// (<c>OptionSetName</c>) or of the local option set of a Picklist, MultiSelect or Status attribute
        /// (<c>EntityLogicalName</c>, <c>AttributeLogicalName</c>). With <c>MergeLabels</c> the localized labels given replace
        /// those of their languages and the others stay; without it the label given is the whole label. A parameter not given
        /// leaves its property; <c>Color</c> given empty (or null) clears the colour. As only labels and the colour change, a
        /// caller that is not unrestricted may update the option of a system component.
        /// </summary>
        /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist for a value the option set does not have (400 in a
        /// caller's session); 400 for a missing <c>Value</c>, a <c>Label</c> without localized labels, a State or Boolean
        /// attribute, a malformed colour, or <c>ParentValues</c>, <c>ExternalValue</c>, <c>IsHidden</c> true.</exception>
        private async Task<OrganizationResponse> UpdateOptionValueAsync(DataSession session, CancellationToken ct)
        {
            var value = Request.Parameters.TryGetValue("Value", out var v) && v is int given
                ? given
                : throw XrmdFaultException.InvalidArgument($"Required parameter 'Value' of request '{Request.RequestName}' is missing or not an Int32.");
            var label = OptionalParameter<Label>("Label");
            if (label is { LocalizedLabels.Count: 0 })
                throw XrmdFaultException.InvalidArgument(
                    $"Parameter 'Label' of request '{Request.RequestName}' has no localized label: an option keeps a label (leave Label out to keep it).");
            var description = OptionalParameter<Label>("Description");
            var merge = OptionalParameter<object>("MergeLabels") switch
            {
                null => false,
                bool flag => flag,
                var other => throw XrmdFaultException.InvalidArgument(
                    $"Parameter 'MergeLabels' of request '{Request.RequestName}' must be a Boolean, not a {other.GetType().Name}."),
            };
            var color = ColorParameter();
            DemandNoUnsupportedOptionParameters();

            var target = OptionTargetOf(update: true);
            var existing = target.OptionSet?.Options.FirstOrDefault(o => o.Value == value);
            if (existing is null)
            {
                var message = $"Option value {value} does not exist in {target.What}.";
                throw Checked ? NotFound(message) : XrmdFaultException.InvalidArgument(message);
            }
            var option = MetadataSerializer.Clone(existing);
            if (label is not null) option.Label = Relabelled(option.Label, label, merge);
            if (description is not null) option.Description = Relabelled(option.Description, description, merge);
            if (color is not null) option.Color = color.Length == 0 ? null : color;
            OptionColor.Demand(target.Owner, option);

            // The option is complete: written as the whole option (its labels and colour replace the stored ones).
            await WriteOptionAsync(session, target, option, replaceLabels: true, ct);
            return new UpdateOptionValueResponse { ResponseName = Message };
        }

        /// <summary>The option set an option value message changes: a global one, or the local one of an attribute.</summary>
        /// <param name="OptionSet">Its current definition (null for an attribute not published).</param>
        /// <param name="EntityName">The attribute's entity; null for a global option set.</param>
        /// <param name="Attribute">The attribute; null for a global option set.</param>
        private sealed record OptionTarget(OptionSetMetadata? OptionSet, string? EntityName, AttributeMetadata? Attribute)
        {
            /// <summary>Names the option set at the start of a validation message.</summary>
            public string Owner => Attribute is null ? $"Global option set '{OptionSet?.Name}'" : $"Attribute '{EntityName}.{Attribute.LogicalName}'";

            /// <summary>Names the option set inside a message.</summary>
            public string What => Attribute is null ? $"global option set '{OptionSet?.Name}'" : $"attribute '{EntityName}.{Attribute.LogicalName}'";
        }

        /// <summary>
        /// The target of InsertOptionValue (<paramref name="update"/> false) or UpdateOptionValue: a global option set
        /// (<c>OptionSetName</c>, 404 when unknown) or the local option set of the attribute <c>EntityLogicalName</c>,
        /// <c>AttributeLogicalName</c>: a Picklist or MultiSelectPicklist, and for an update a Status attribute too (400
        /// otherwise). A new option on a system component is refused for a caller that is not unrestricted (403).
        /// </summary>
        private OptionTarget OptionTargetOf(bool update)
        {
            if (IsGlobalTarget(Request))
            {
                var optionSetName = (string)Request.Parameters["OptionSetName"];
                var current = Store.GetOptionSet(optionSetName) as OptionSetMetadata;
                if (Checked && current is null) throw NotFound($"Could not find optionset with Name '{optionSetName}'.");
                return new OptionTarget(current ?? new OptionSetMetadata { Name = optionSetName }, null, null);
            }

            var entityName = String("EntityLogicalName");
            var attributeName = String("AttributeLogicalName");
            var attribute = Checked
                ? EnsureAttribute(entityName, attributeName)
                : Store.GetEntity(entityName)?.Attributes?.FirstOrDefault(a => a.LogicalName == attributeName);
            // A new option is never only a label.
            if (!update && !Unrestricted && Published(entityName, attributeName) is var (owner, published) && IsSystemComponent(owner, published))
                throw OnlyLabels(owner, published, "a new option");
            if (update)
            {
                if (attribute is not (PicklistAttributeMetadata or MultiSelectPicklistAttributeMetadata or StatusAttributeMetadata))
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{attributeName}' of entity '{entityName}' is {(attribute?.AttributeType is { } type ? $"a {type}" : "not a published")} attribute: " +
                        "UpdateOptionValue changes the options of a Picklist, MultiSelectPicklist or Status attribute (the options of a State or Boolean attribute are not supported here).");
            }
            else if (attribute is not (PicklistAttributeMetadata or MultiSelectPicklistAttributeMetadata))
                throw XrmdFaultException.InvalidArgument(
                    $"Attribute '{attributeName}' of entity '{entityName}' is not a Picklist or MultiSelectPicklist attribute.");
            return new OptionTarget(((EnumAttributeMetadata)attribute).OptionSet, entityName, attribute);
        }

        /// <summary>
        /// Writes <paramref name="option"/> to <paramref name="target"/> (the store adds a new value and merges an existing
        /// one; <paramref name="replaceLabels"/>: its labels and colour replace the stored ones) after the customizable check,
        /// and adds the changed component to the <c>SolutionUniqueName</c> solution.
        /// </summary>
        private async Task WriteOptionAsync(DataSession session, OptionTarget target, OptionMetadata option, bool replaceLabels, CancellationToken ct)
        {
            if (target.Attribute is not { } attribute)
            {
                var name = target.OptionSet!.Name;
                await DemandCustomizableAsync(session, ComponentType.OptionSet, OptionSetIdOf(name), target.What, ct);
                await Store.UpdateOptionSetAsync(session, new OptionSetMetadata([option]) { Name = name }, replaceLabels, ct);
                await TrackChangedAsync(session, ComponentType.OptionSet, OptionSetIdOf(name), ct);
                return;
            }
            await DemandCustomizableAsync(session, ComponentType.Attribute, attribute.MetadataId, target.What, ct);
            var changes = (EnumAttributeMetadata)Activator.CreateInstance(attribute.GetType())!;
            changes.LogicalName = attribute.LogicalName;
            changes.OptionSet = new OptionSetMetadata([option]) { IsGlobal = false };
            await Store.UpdateAttributeAsync(session, target.EntityName!, changes, replaceLabels, ct);
            await TrackChangedAsync(session, ComponentType.Attribute, attribute.MetadataId, ct);
        }

        /// <summary>
        /// The <c>Color</c> parameter of InsertOptionValue and UpdateOptionValue (the SDK requests have no property for it):
        /// null when absent; empty when given empty or null (UpdateOptionValue: a clear).
        /// </summary>
        /// <exception cref="XrmdFaultException">400 naming <c>Color</c> when it is not a string.</exception>
        private string? ColorParameter()
        {
            if (!Request.Parameters.TryGetValue("Color", out var value)) return null;
            return value switch
            {
                null => "",
                string text => text,
                _ => throw XrmdFaultException.InvalidArgument(
                    $"Parameter 'Color' of request '{Request.RequestName}' must be a string (#RRGGBB), not a {value.GetType().Name}."),
            };
        }

        /// <summary>
        /// 400 naming <c>ParentValues</c> (non-empty), <c>ExternalValue</c> (non-null) or <c>IsHidden</c> (true) of an option
        /// value message: xrmverse does not model dependent options, external values or hidden options, and never drops them
        /// silently. <c>IsHidden</c> false is Learn's default (a visible option, what every option is here) and is accepted.
        /// </summary>
        private void DemandNoUnsupportedOptionParameters()
        {
            if (Request.Parameters.TryGetValue("ParentValues", out var parents) && parents is not (null or System.Collections.ICollection { Count: 0 }))
                throw NotSupportedParameter("ParentValues");
            if (Request.Parameters.TryGetValue("ExternalValue", out var external) && external is not null)
                throw NotSupportedParameter("ExternalValue");
            if (Request.Parameters.TryGetValue("IsHidden", out var hidden) && hidden is not (null or false))
                throw NotSupportedParameter("IsHidden");
        }

        private XrmdFaultException NotSupportedParameter(string name) =>
            XrmdFaultException.InvalidArgument($"Parameter '{name}' of request '{Request.RequestName}' is not supported by xrmverse.");

        /// <summary>
        /// <paramref name="current"/> with the localized labels of <paramref name="given"/>: each replaces the one of its
        /// language (<paramref name="merge"/>) or the given label is the whole label; the user label is the 1033 one, else the first.
        /// </summary>
        private static Label Relabelled(Label? current, Label given, bool merge)
        {
            var result = merge && current is not null ? MetadataSerializer.Clone(current) : new Label();
            foreach (var localized in given.LocalizedLabels)
            {
                var copy = new LocalizedLabel(localized.Label, localized.LanguageCode);
                var index = result.LocalizedLabels.ToList().FindIndex(l => l.LanguageCode == localized.LanguageCode);
                if (index >= 0) result.LocalizedLabels[index] = copy;
                else result.LocalizedLabels.Add(copy);
            }
            result.UserLocalizedLabel = null;
            return WithUserLabel(result);
        }

        /// <summary><paramref name="label"/> with its user label set (the 1033 one, else the first) when it has none.</summary>
        private static Label WithUserLabel(Label label)
        {
            if (label.UserLocalizedLabel is null && label.LocalizedLabels.Count > 0)
                label.UserLocalizedLabel = label.LocalizedLabels.FirstOrDefault(l => l.LanguageCode == 1033) ?? label.LocalizedLabels[0];
            return label;
        }

        /// <summary>The parameter <paramref name="name"/>: null when absent or null; 400 naming it when not a <typeparamref name="T"/>.</summary>
        private T? OptionalParameter<T>(string name) where T : class =>
            !Request.Parameters.TryGetValue(name, out var value) || value is null
                ? null
                : value as T ?? throw XrmdFaultException.InvalidArgument(
                    $"Parameter '{name}' of request '{Request.RequestName}' must be a {typeof(T).Name}, not a {value.GetType().Name}.");

        /// <summary>
        /// Deletes the entity unless a plug-in step is registered on it (400, the Ruling's message): the store would delete
        /// such steps with the entity.
        /// </summary>
        private async Task<OrganizationResponse> DeleteEntityAsync(DataSession session, CancellationToken ct)
        {
            var name = String("LogicalName");
            var entity = EnsureEntity(name);
            // A managed table goes with its solution's uninstall (ruling M14), before any other refusal.
            await DemandDeletableAsync(session, ComponentType.Entity, Store.GetEntity(name)?.MetadataId, $"table '{name}'", ct);
            // A system entity is the store's 403, whatever is registered on it.
            if (!(entity is not null && MetadataStore.IsSystemEntity(entity)) && await StepOnAsync(session, name, ct) is { } step)
                throw XrmdFaultException.InvalidArgument($"The component cannot be deleted because it is referenced by step '{step}'.");
            var components = EntityComponents(name);
            await Store.DeleteEntityAsync(session, name, ct);
            var viewsAndForms = await DeleteViewsAndFormsAsync(session, name, ct);
            await Ops.Tracker.RemoveDeletedAsync(session, [.. components, .. viewsAndForms], ct);
            return new DeleteEntityResponse { ResponseName = Message };
        }

        /// <summary>
        /// Deletes the views (<c>savedquery.returnedtypecode</c>), forms (<c>systemform.objecttypecode</c>) and system charts
        /// (<c>savedqueryvisualization.primaryentitytypecode</c>, plan 3.5 UI-1f) of a deleted entity, as Dataverse deletes a
        /// table's views, forms and charts with it; after the metadata writes. Returns their ids (to leave every solution).
        /// </summary>
        private static async Task<Guid[]> DeleteViewsAndFormsAsync(DataSession session, string entity, CancellationToken ct)
        {
            var d = session.Dialect;
            string[] query = ["savedqueryid"], form = ["systemformid"], chart = ["savedqueryvisualizationid"];
            await using var cmd = session.Command($"""
                DELETE FROM data.savedquery{d.ReturningInline(query, RowImage.Deleted)} WHERE returnedtypecode = @entity{d.ReturningTrailing(query, RowImage.Deleted)};
                DELETE FROM data.systemform{d.ReturningInline(form, RowImage.Deleted)} WHERE objecttypecode = @entity{d.ReturningTrailing(form, RowImage.Deleted)};
                DELETE FROM data.savedqueryvisualization{d.ReturningInline(chart, RowImage.Deleted)} WHERE primaryentitytypecode = @entity{d.ReturningTrailing(chart, RowImage.Deleted)};
                """);
            cmd.AddParameter("@entity", DbType.String, entity);
            var ids = new List<Guid>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            do
            {
                while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
            }
            while (await rd.NextResultAsync(ct));
            return [.. ids];
        }

        /// <summary>The name of a step registered on <paramref name="entity"/> (the first by name); null when there is none.</summary>
        private async Task<string?> StepOnAsync(DataSession session, string entity, CancellationToken ct)
        {
            if (Store.GetEntity("sdkmessageprocessingstep") is null || Store.GetEntity("sdkmessagefilter") is null) return null;
            // A plain read (no lock kept): the metadata write lock is taken after it, never while data locks are held.
            await using var cmd = session.Command(session.Dialect.Limit("""
                SELECT s.name FROM data.sdkmessageprocessingstep AS s
                JOIN data.sdkmessagefilter AS f ON f.sdkmessagefilterid = s.sdkmessagefilterid
                WHERE f.primaryobjecttypecode = @entity
                ORDER BY s.name
                """, "1"));
            cmd.AddParameter("@entity", DbType.String, entity);
            return await cmd.ExecuteScalarAsync(ct) is string step ? step : null;
        }

        /// <summary>The <c>SolutionUniqueName</c> parameter; null when absent or empty.</summary>
        private string? SolutionUniqueName =>
            Request.Parameters.TryGetValue("SolutionUniqueName", out var value) && value is string { Length: > 0 } name ? name : null;

        /// <summary>A created component: in <c>Default</c> and in the <c>SolutionUniqueName</c> solution (after the metadata writes).</summary>
        private Task TrackCreatedAsync(DataSession session, int type, Guid objectId, CancellationToken ct) =>
            Ops.Tracker.TrackAsync(session, type, objectId, SolutionUniqueName, ct, markActive: !Internal);

        /// <summary>
        /// A changed component whose id is known (published; a component created earlier in the caller's session has none
        /// here): added to the <c>SolutionUniqueName</c> solution when one is given, and given its Active marker when it is
        /// layered (plan 2.6, ruling M4), unless the caller is <see cref="Caller.Internal"/>.
        /// </summary>
        private async Task TrackChangedAsync(DataSession session, int type, Guid? objectId, CancellationToken ct)
        {
            if (objectId is not { } id) return;
            if (SolutionUniqueName is not null) await Ops.Tracker.TrackAsync(session, type, id, SolutionUniqueName, ct, markActive: !Internal);
            else if (!Internal) await Ops.Tracker.MarkChangedAsync(session, type, id, ct);
        }

        private static Guid? OptionSetIdOf(string? name) => name is null ? null : MetadataStore.OptionSetId(name);

        /// <summary>
        /// The managed state of a published component (plan 2.6, rulings M13 and M14); null when the rules do not apply
        /// (<see cref="Bypass"/>), the id is unknown or the component has no Managed layer.
        /// </summary>
        private async Task<ManagedState?> ManagedAsync(DataSession session, int type, Guid? id, CancellationToken ct) =>
            Bypass || id is not { } value ? null : await ManagedProperties.StateAsync(session, type, value, ct);

        /// <summary>400 when a managed solution delivers the component with <c>IsCustomizable</c> false (ruling M13).</summary>
        private async Task DemandCustomizableAsync(DataSession session, int type, Guid? id, string what, CancellationToken ct)
        {
            if (await ManagedAsync(session, type, id, ct) is { Flags.IsCustomizable: false } state)
                throw ManagedProperties.NotCustomizable(what, state);
        }

        /// <summary>
        /// 400 when <paramref name="table"/> is delivered with <c>IsCustomizable</c> or <c>CanCreateAttributes</c> false: no new
        /// column or relationship on it (M13).
        /// </summary>
        private async Task DemandTableCustomizableAsync(DataSession session, string? table, string what, CancellationToken ct)
        {
            if (table is null || Store.GetEntity(table) is not { } entity
                || await ManagedAsync(session, ComponentType.Entity, entity.MetadataId, ct) is not { } state) return;
            if (!state.Flags.IsCustomizable) throw ManagedProperties.TableNotCustomizable(what, table, state);
            if (!state.Flags.CanCreateAttributes) throw ManagedProperties.TableFlagFalse(what, table, "CanCreateAttributes", state);
        }

        /// <summary>400 for a managed component: it goes with the uninstall of its solution (rulings M13 <c>CanBeDeleted</c>, M14).</summary>
        private async Task DemandDeletableAsync(DataSession session, int type, Guid? id, string what, CancellationToken ct)
        {
            if (await ManagedAsync(session, type, id, ct) is { } state) throw ManagedProperties.NotDeletable(what, state);
        }

        private static bool Renamed(Label? given, Label? current) => ManagedProperties.Renamed(given, current);

        /// <summary>The published ids a deleted entity takes with it: its own, its attributes' and its relationships'.</summary>
        private Guid[] EntityComponents(string entity)
        {
            var e = Store.GetEntity(entity);
            if (e is null) return [];
            var relationships = Store.GetOneToManyRelationships()
                .Where(r => r.ReferencingEntity == entity || r.ReferencedEntity == entity).Select(r => r.MetadataId)
                .Concat(Store.GetManyToManyRelationships()
                    .Where(r => r.Entity1LogicalName == entity || r.Entity2LogicalName == entity).Select(r => r.MetadataId));
            return (e.Attributes ?? []).Select(a => a.MetadataId).Prepend(e.MetadataId).Concat(relationships)
                .OfType<Guid>().Distinct().ToArray();
        }

        /// <summary>The published ids a deleted attribute takes with it: its own and those of the relationships of a lookup.</summary>
        private Guid[] AttributeComponents(string entity, string attribute) =>
            Store.GetOneToManyRelationships()
                .Where(r => r.ReferencingEntity == entity && r.ReferencingAttribute == attribute).Select(r => r.MetadataId)
                .Prepend(Published(entity, attribute)?.Attribute.MetadataId)
                .OfType<Guid>().Distinct().ToArray();

        /// <summary>The published ids a deleted relationship takes with it: its own and, for a one-to-many one, its lookup's.</summary>
        private Guid[] RelationshipComponents(string schemaName) => Store.GetRelationship(schemaName) switch
        {
            OneToManyRelationshipMetadata r when r.ReferencingEntity is { } entity && r.ReferencingAttribute is { } lookup =>
                AttributeComponents(entity, lookup).Append(r.MetadataId ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToArray(),
            { MetadataId: { } id } => [id],
            _ => [],
        };

        /// <summary>
        /// Runs a store create: its <see cref="MetadataComponentExistsException"/> (the component exists) is 412
        /// DuplicateRecord; any other exception stays what it is.
        /// </summary>
        private static async Task CreatingAsync(Func<Task> create)
        {
            try
            {
                await create();
            }
            catch (MetadataComponentExistsException ex)
            {
                throw Duplicate(ex.Message);
            }
        }

        /// <summary>400 for the SchemaName of a security relationship (<c>systemuserroles_association</c>, ...).</summary>
        private static void EnsureNotSecurityRelationship(string? schemaName)
        {
            if (SecurityAdministration.FindRelationship(schemaName) is not null)
                throw XrmdFaultException.InvalidArgument($"SchemaName '{schemaName}' is the name of a system relationship.");
        }

        /// <summary>An update that only sets IsPrimaryImage = true on an image column, and changes nothing else.</summary>
        private static bool IsPrimaryImageClaim(AttributeMetadata attribute) =>
            attribute is ImageAttributeMetadata { IsPrimaryImage: true }
            && MetadataSerializer.SerializeAttribute(attribute) == MetadataSerializer.SerializeAttribute(
                new ImageAttributeMetadata { LogicalName = attribute.LogicalName, IsPrimaryImage = true });

        /// <summary>The published entity and attribute; null when either is not published (created in the caller's session).</summary>
        private (EntityMetadata Entity, AttributeMetadata Attribute)? Published(string entityLogicalName, string? attributeLogicalName) =>
            Store.GetEntity(entityLogicalName) is { } entity
            && (entity.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attributeLogicalName) is { } attribute
                ? (entity, attribute)
                : null;

        private void EnsureNewRelationship(string? schemaName)
        {
            if (schemaName is not null && Store.GetRelationship(schemaName) is not null)
                throw Duplicate($"A relationship with SchemaName '{schemaName}' already exists.");
        }

        /// <summary>The published entity; 404 when it does not exist (only when <see cref="Checked"/>; null otherwise).</summary>
        private EntityMetadata? EnsureEntity(string? logicalName)
        {
            if (!Checked) return null;
            return Store.GetEntity(logicalName ?? "") ?? throw NotFound($"Could not find an entity with name {logicalName}.");
        }

        private AttributeMetadata? EnsureAttribute(string entityLogicalName, string? attributeLogicalName)
        {
            var entity = EnsureEntity(entityLogicalName);
            if (entity is null) return null;
            return (entity.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attributeLogicalName)
                   ?? throw NotFound($"Could not find attribute {attributeLogicalName} of entity {entityLogicalName}.");
        }

        private void EnsureOptionSet(string? name)
        {
            if (Checked && Store.GetOptionSet(name ?? "") is null) throw NotFound($"Could not find optionset with Name '{name}'.");
        }

        private T Parameter<T>(string name) where T : class => CoreOperations.Parameter<T>(Request, name);

        private string String(string name) =>
            Request.Parameters.TryGetValue(name, out var value) && value is string { Length: > 0 } text
                ? text
                : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{Request.RequestName}' is missing.");
    }

    private static XrmdFaultException NotFound(string message) => new(ErrorCodes.ObjectDoesNotExist, message, 404);

    /// <summary>400 naming the properties a request changes that the store does not apply (never dropped silently).</summary>
    private static XrmdFaultException Unapplied(string message, string component, IReadOnlyList<string> properties) =>
        XrmdFaultException.InvalidArgument(
            $"{message} cannot change {string.Join(", ", properties)} of {component}: xrmverse does not apply {(properties.Count == 1 ? "this property" : "these properties")}.");

    /// <summary>
    /// 400 for an update in a caller's session of a component created earlier in that session: its definition is not
    /// published, so what the request changes cannot be compared.
    /// </summary>
    private static XrmdFaultException NotPublished(string kind, string? name) =>
        XrmdFaultException.InvalidArgument(
            $"{kind} '{name}' was created in this transaction and cannot be updated before it commits; send the final definition in the create request.");

    private static XrmdFaultException Duplicate(string message) => new(ErrorCodes.DuplicateRecord, message, 412);

    /// <summary>
    /// Answers the metadata reads from the published cache, with copies: RetrieveRelationship (<c>Name</c> or
    /// <c>MetadataId</c>; a one-to-many relationship without a saved row has no MetadataId), RetrieveOptionSet (<c>Name</c>
    /// or <c>MetadataId</c>), RetrieveAllOptionSets, RetrieveAllEntities (<c>EntityFilters</c>: the attributes with
    /// <see cref="EntityFilters.Attributes"/>, the relationships with <see cref="EntityFilters.Relationships"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter; 404 for an unknown component.</exception>
    /// <exception cref="NotSupportedException">Not a metadata read.</exception>
    public static OrganizationResponse Retrieve(IMetadataStore store, OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        var name = ReadMessages.TryGetValue(request.RequestName ?? "", out var canonical)
            ? canonical
            : throw new NotSupportedException($"Request '{request.RequestName}' is not a metadata read.");
        switch (name)
        {
            case "RetrieveRelationship":
            {
                var relationship = ByNameOrId(request, store.GetRelationship,
                    id => store.GetManyToManyRelationships().Cast<RelationshipMetadataBase>()
                        .Concat(store.GetOneToManyRelationships()).FirstOrDefault(r => r.MetadataId == id), "relationship");
                return new RetrieveRelationshipResponse
                {
                    ResponseName = name, Results = { ["RelationshipMetadata"] = MetadataSerializer.Clone(relationship) },
                };
            }
            case "RetrieveOptionSet":
            {
                var optionSet = ByNameOrId(request, store.GetOptionSet,
                    id => store.GetAllOptionSets().FirstOrDefault(o => o.MetadataId == id), "optionset");
                return new RetrieveOptionSetResponse { ResponseName = name, Results = { ["OptionSetMetadata"] = MetadataSerializer.Clone(optionSet) } };
            }
            case "RetrieveAllOptionSets":
                return new RetrieveAllOptionSetsResponse
                {
                    ResponseName = name,
                    Results = { ["OptionSetMetadata"] = store.GetAllOptionSets().Select(MetadataSerializer.Clone).ToArray() },
                };
            default: // RetrieveAllEntities
                var filters = request.Parameters.TryGetValue("EntityFilters", out var f) && f is EntityFilters given ? given : EntityFilters.Entity;
                var entities = store.GetAllEntities().Select(e =>
                {
                    var copy = ExportedSettings.Entity(store, e, attributes: filters.HasFlag(EntityFilters.Attributes));
                    if (filters.HasFlag(EntityFilters.Relationships))
                        copy.SetRelationships(store.GetOneToManyRelationships(), store.GetManyToManyRelationships());
                    return copy;
                }).ToArray();
                return new RetrieveAllEntitiesResponse { ResponseName = name, Results = { ["EntityMetadata"] = entities } };
        }
    }

    /// <summary>The component named by the request's <c>Name</c>, else by its <c>MetadataId</c>.</summary>
    private static T ByNameOrId<T>(OrganizationRequest request, Func<string, T?> byName, Func<Guid, T?> byId, string kind) where T : class
    {
        if (request.Parameters.TryGetValue("Name", out var n) && n is string { Length: > 0 } name)
            return byName(name) ?? throw NotFound($"Could not find {kind} with Name '{name}'.");
        if (request.Parameters.TryGetValue("MetadataId", out var i) && i is Guid id && id != Guid.Empty)
            return byId(id) ?? throw NotFound($"Could not find {kind} with MetadataId '{id}'.");
        throw XrmdFaultException.InvalidArgument($"Request '{request.RequestName}' needs a Name or a MetadataId.");
    }
}
