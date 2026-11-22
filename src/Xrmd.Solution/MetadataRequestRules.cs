using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// What a metadata request may ask of xrmverse, checked the same way by every front end (SDK messages, Web API) and, for
/// what an import shares with them, by the solution import: what the store does not apply is refused (400 naming it),
/// never dropped silently.
/// </summary>
/// <remarks>
/// <para>Create: the table type is Standard or Elastic (<see cref="ElasticTables.DemandValidEntity"/>); no ownership but user or
/// organization (both paths, <see cref="DemandSupportedEntity"/>; the store adds the rules of a custom activity, plan 2.11 A4);
/// through the SDK and the Web API, no table setting xrmverse does not model unless it is the value the export writes for a
/// table created in xrmverse (<see cref="DemandOnlyModelledSettings"/>), so that RetrieveEntity and the export never differ.</para>
/// <para>Update: the store applies labels to an entity, and labels, the required level, <c>IsSecured</c>, a larger
/// <c>MaxLength</c> and the options of a picklist to an attribute (a whitelist). Any other property of the request whose value differs from the
/// current definition is refused (<see cref="UnappliedEntityChanges"/>, <see cref="UnappliedAttributeChanges"/>). A property
/// whose value is the one a new SDK object has (<c>AttributeTypeName</c>, an option's <c>IsHidden</c>, ...) counts as not
/// given. The child collections of an entity (attributes, relationships, privileges, keys) are not part of UpdateEntity
/// and are ignored, as Dataverse does.</para>
/// </remarks>
public static class MetadataRequestRules
{
    private const string ODataType = "@odata.type";

    /// <summary>The entity properties a create takes (the others are table settings).</summary>
    private static readonly HashSet<string> CreatedEntityProperties = new(StringComparer.Ordinal)
    {
        ODataType, nameof(EntityMetadata.LogicalName), nameof(EntityMetadata.SchemaName), nameof(EntityMetadata.DisplayName),
        nameof(EntityMetadata.DisplayCollectionName), nameof(EntityMetadata.Description), nameof(EntityMetadata.EntitySetName),
        nameof(EntityMetadata.OwnershipType), nameof(EntityMetadata.IsActivity), nameof(EntityMetadata.Attributes),
        nameof(EntityMetadata.HasChanged), nameof(EntityMetadata.PrimaryIdAttribute), nameof(EntityMetadata.PrimaryNameAttribute),
        nameof(EntityMetadata.IsAuditEnabled), nameof(EntityMetadata.AutoCreateAccessTeams), nameof(EntityMetadata.ChangeTrackingEnabled),
        // Plan 2.11 (A4, A5): the store applies them and their rules.
        nameof(EntityMetadata.HasNotes), nameof(EntityMetadata.HasActivities), nameof(EntityMetadata.ActivityTypeMask),
        nameof(EntityMetadata.IsDuplicateDetectionEnabled), nameof(EntityMetadata.TableType),
    };

    /// <summary>The SDK properties whose setting the export writes under another name.</summary>
    private static readonly Dictionary<string, string> ExportNames = new(StringComparer.Ordinal)
    {
        [nameof(EntityMetadata.HasNotes)] = "HasRelatedNotes",
        [nameof(EntityMetadata.HasActivities)] = "HasRelatedActivities",
    };

    /// <summary>The entity properties UpdateEntity does not take (changed through their own messages).</summary>
    private static readonly HashSet<string> EntityChildren = new(StringComparer.Ordinal)
    {
        nameof(EntityMetadata.Attributes), nameof(EntityMetadata.OneToManyRelationships), nameof(EntityMetadata.ManyToOneRelationships),
        nameof(EntityMetadata.ManyToManyRelationships), nameof(EntityMetadata.Privileges), nameof(EntityMetadata.Keys),
    };

    /// <summary>Never compared: the name (it identifies the component), the client's change flag, the JSON type.</summary>
    private static readonly HashSet<string> Identity = new(StringComparer.Ordinal)
    {
        ODataType, nameof(MetadataBase.HasChanged), nameof(EntityMetadata.LogicalName),
    };

    /// <summary>The labels the store applies.</summary>
    private static readonly HashSet<string> EntityLabels = new(StringComparer.Ordinal)
    {
        nameof(EntityMetadata.DisplayName), nameof(EntityMetadata.DisplayCollectionName), nameof(EntityMetadata.Description),
    };

    private static readonly HashSet<string> AttributeLabels = new(StringComparer.Ordinal)
    {
        nameof(AttributeMetadata.DisplayName), nameof(AttributeMetadata.Description),
    };

    /// <summary>The properties of an existing option the store applies: its labels and its background colour.</summary>
    private static readonly HashSet<string> AppliedOptionProperties = new(StringComparer.Ordinal)
    {
        nameof(OptionMetadata.Label), nameof(OptionMetadata.Description), nameof(OptionMetadata.Color),
    };

    /// <summary>The properties UpdateOptionSet applies to a global option set: the labels and the options.</summary>
    private static readonly HashSet<string> OptionSetApplied = new(StringComparer.Ordinal)
    {
        nameof(OptionSetMetadata.DisplayName), nameof(OptionSetMetadata.Description), nameof(OptionSetMetadata.Options),
    };

    /// <summary>The <c>FormulaDefinition</c> of the column types that can be calculated (plan 2.12a).</summary>
    private const string CalculatedFormula = "FormulaDefinition";

    /// <summary>
    /// The column properties that neither <see cref="ExportedSettings.AttributeDefaults"/> nor the store model, with the only
    /// value the export writes for them (null: none; the export has no element for it): the <c>DisplayMask</c> bits the
    /// export writes as <c>ValidForAdvancedFind|ValidForForm|ValidForGrid</c> and the read flag are true; the deprecation and
    /// inheritance markers, the column number and the linked attribute are not written at all. (The <c>CanBeSecuredFor*</c>
    /// flags are server-owned: <see cref="ServerOwnedProperties"/> clears them, and reads compute them.)
    /// </summary>
    private static readonly Dictionary<string, string?> UnwrittenAttributeSettings = new(StringComparer.Ordinal)
    {
        [nameof(AttributeMetadata.IsValidForAdvancedFind)] = "1",
        [nameof(AttributeMetadata.IsValidForForm)] = "1",
        [nameof(AttributeMetadata.IsValidForGrid)] = "1",
        [nameof(AttributeMetadata.IsValidForRead)] = "1",
        [nameof(AttributeMetadata.DeprecatedVersion)] = null,
        [nameof(AttributeMetadata.InheritsFrom)] = null,
        [nameof(AttributeMetadata.ColumnNumber)] = null,
        [nameof(AttributeMetadata.LinkedAttributeId)] = null,
    };

    // ---- create -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A table xrmverse can hold: when <paramref name="isNew"/>, owned by a user or the organization (null: the default, user,
    /// unless <paramref name="ownershipRequired"/>: a solution file always states it). A custom activity (<c>IsActivity</c>, plan
    /// 2.11 A4) is accepted here; the metadata store checks its own rules (user owned, primary column <c>Subject</c>, ...).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the entity and what is not supported.</exception>
    public static void DemandSupportedEntity(EntityMetadata entity, bool isNew, bool ownershipRequired = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var ownership = entity.OwnershipType ?? (ownershipRequired ? null : OwnershipTypes.UserOwned);
        if (isNew && ownership is not (OwnershipTypes.UserOwned or OwnershipTypes.OrganizationOwned))
            throw XrmdFaultException.InvalidArgument(
                $"Entity '{entity.LogicalName}' has the ownership '{ownership}', which xrmverse does not support (only user and organization owned).");
    }

    /// <summary>
    /// A created table carries no setting xrmverse does not model (notes, colors, quick create, ...), except
    /// with the value the export writes for a table created in xrmverse; its primary name, when given, is the primary name
    /// attribute's. The server-owned properties are cleared first (<see cref="ServerOwnedProperties"/>); the primary key is the
    /// server's (a given <c>PrimaryIdAttribute</c> is ignored: the store names it, an existing ruling).
    /// </summary>
    /// <param name="primaryName">The logical name of the primary name attribute.</param>
    /// <exception cref="XrmdFaultException">400 naming the property.</exception>
    public static void DemandOnlyModelledSettings(EntityMetadata entity, string? primaryName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var name = entity.LogicalName;
        if (entity.PrimaryNameAttribute is { } given && given != primaryName)
            throw XrmdFaultException.InvalidArgument(
                $"Entity '{name}': PrimaryNameAttribute '{given}' is not the primary name attribute '{primaryName}'.");

        ElasticTables.DemandValidEntity(entity); // the table type and what an elastic table cannot be (plan 3.4, ruling E9)
        DemandCreatableAuditSetting(entity.IsAuditEnabled, $"Entity '{name}'", fixedValue: null);
        foreach (var (property, value) in Json(entity, typeof(EntityMetadata)))
        {
            if (CreatedEntityProperties.Contains(property) || value is null || value is JsonArray { Count: 0 }) continue;
            // A custom activity is available offline (plan 2.11 A4): true is the one value it takes.
            if (property == nameof(EntityMetadata.IsAvailableOffline) && entity.IsActivity == true)
            {
                if (Text(value) == "1") continue;
                throw XrmdFaultException.InvalidArgument($"Entity '{name}': IsAvailableOffline of a custom activity can only be true.");
            }
            var expected = UnmodelledXml.EntityDefault(property);
            if (expected is not null && Text(value) is { } text && text == Normalize(expected)) continue;
            throw XrmdFaultException.InvalidArgument(expected is null
                ? $"Entity '{name}': the property {property} is not modelled by xrmverse and cannot be set."
                : $"Entity '{name}': {property} = {value.ToJsonString()} is not supported by xrmverse (only the default, {expected}).");
        }
    }

    /// <summary>
    /// A created column carries no setting xrmverse does not model (auditing, sorting, search flags, IME mode, ...) except
    /// with the value the export writes for a column created in xrmverse (<see cref="ExportedSettings"/>), so that
    /// RetrieveAttribute and the export never differ. Checked for CreateAttribute, the attributes of CreateEntity (SDK
    /// primary attribute, Web API <c>Attributes</c>) and the lookup of CreateOneToMany; the server-owned properties are
    /// cleared first.
    /// </summary>
    /// <param name="attribute">The attribute to create.</param>
    /// <param name="entity">The logical name of its entity, for the error.</param>
    /// <exception cref="XrmdFaultException">400 naming the property.</exception>
    public static void DemandOnlyModelledAttributeSettings(AttributeMetadata attribute, string? entity)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var defaults = ExportedSettings.AttributeDefaults(attribute);
        DemandCreatableAuditSetting(attribute.IsAuditEnabled, $"Attribute '{entity}.{attribute.LogicalName}'", AuditDefaults.ForAttribute(attribute));
        foreach (var (property, value) in Json(attribute, typeof(AttributeMetadata)))
        {
            if (value is null || property == nameof(AttributeMetadata.IsAuditEnabled)) continue;
            if (!defaults.TryGetValue(property, out var expected))
            {
                if (!UnwrittenAttributeSettings.TryGetValue(property, out expected)) continue;
                if (expected is null)
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{entity}.{attribute.LogicalName}': the property {property} is not modelled by xrmverse and cannot be set.");
            }
            if (Text(value) is { } text && string.Equals(text, Normalize(expected), StringComparison.OrdinalIgnoreCase)) continue;
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{entity}.{attribute.LogicalName}': {property} = {value.ToJsonString()} is not supported by xrmverse (only the default, {expected}).");
        }
    }

    /// <summary>
    /// A created table or column takes either value of <c>IsAuditEnabled</c>, but not a managed property that says it cannot
    /// change, nor another managed property name. A column that cannot change (<paramref name="fixedValue"/>: the primary key,
    /// the file columns, ...) takes only its fixed value.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming <c>IsAuditEnabled</c>.</exception>
    private static void DemandCreatableAuditSetting(BooleanManagedProperty? given, string what, BooleanManagedProperty? fixedValue)
    {
        if (given is null) return;
        if (!given.CanBeChanged)
            throw XrmdFaultException.InvalidArgument($"{what}: IsAuditEnabled cannot be created with CanBeChanged false.");
        if (given.ManagedPropertyLogicalName is { } managed
            && !string.Equals(managed, AuditDefaults.ManagedPropertyName, StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument(
                $"{what}: the ManagedPropertyLogicalName of IsAuditEnabled is '{AuditDefaults.ManagedPropertyName}', not '{managed}'.");
        if (fixedValue is { CanBeChanged: false } && given.Value != fixedValue.Value)
            throw XrmdFaultException.InvalidArgument(
                $"{what}: IsAuditEnabled cannot be {(given.Value ? "enabled" : "disabled")}: its value is fixed to {(fixedValue.Value ? "true" : "false")}.");
    }

    /// <summary>
    /// What an update asks of <c>IsAuditEnabled</c> that xrmverse does not apply: a changed value while the current setting
    /// cannot be changed, a managed property that says it cannot change, or another managed property name. A <c>CanBeChanged</c>
    /// of true on a setting that cannot change is not a request (the SDK object has it by default): only a changed value is.
    /// Known limit: a Web API body that sends <c>CanBeChanged</c> true explicitly is read the same way, because the request
    /// reaches this check as an SDK object that no longer tells a given value from the default.
    /// </summary>
    private static IEnumerable<string> UnappliedAuditChanges(BooleanManagedProperty? current, BooleanManagedProperty? changes)
    {
        if (changes is null || current is null) yield break;
        if (changes.Value != current.Value && !current.CanBeChanged) yield return nameof(EntityMetadata.IsAuditEnabled);
        if (!changes.CanBeChanged && current.CanBeChanged) yield return nameof(EntityMetadata.IsAuditEnabled) + ".CanBeChanged";
        if (changes.ManagedPropertyLogicalName is { } managed
            && !string.Equals(managed, AuditDefaults.ManagedPropertyName, StringComparison.OrdinalIgnoreCase))
            yield return nameof(EntityMetadata.IsAuditEnabled) + ".ManagedPropertyLogicalName";
    }

    // ---- update -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The properties of <paramref name="changes"/> that differ from <paramref name="current"/> and that UpdateEntity does
    /// not apply: anything but the labels, <c>IsAuditEnabled</c>, <c>AutoCreateAccessTeams</c>, <c>ChangeTrackingEnabled</c>,
    /// <c>HasActivities</c>, <c>HasNotes</c> and <c>IsDuplicateDetectionEnabled</c> (the store applies their rules; the child collections
    /// are not part of UpdateEntity and are not compared). <c>IsActivity</c> and <c>ActivityTypeMask</c> are compared: they cannot change.
    /// </summary>
    public static IReadOnlyList<string> UnappliedEntityChanges(EntityMetadata current, EntityMetadata changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        var differences = Differences(Json(current, typeof(EntityMetadata)), Json(changes, typeof(EntityMetadata)), Json(new EntityMetadata(), typeof(EntityMetadata)),
            property => Identity.Contains(property) || EntityChildren.Contains(property) || EntityLabels.Contains(property)
                        || property is nameof(EntityMetadata.IsAuditEnabled) or nameof(EntityMetadata.AutoCreateAccessTeams)
                            or nameof(EntityMetadata.ChangeTrackingEnabled) or nameof(EntityMetadata.HasActivities) or nameof(EntityMetadata.HasNotes)
                            or nameof(EntityMetadata.IsDuplicateDetectionEnabled), "");
        differences.AddRange(UnappliedAuditChanges(current.IsAuditEnabled, changes.IsAuditEnabled));
        return differences;
    }

    /// <summary>
    /// The properties of <paramref name="changes"/> that differ from <paramref name="current"/> and that UpdateAttribute does
    /// not apply: anything but the labels, the required level, <c>IsSecured</c> (the store applies the securable rule), <c>AutoNumberFormat</c> and <c>MaxLength</c> (string), <c>MaxLength</c> (memo), <c>MaxSizeInKB</c> (file, image), <c>CanStoreFullImage</c> and <c>IsPrimaryImage</c> (image) and the options of a
    /// picklist, a multi-select picklist, a <c>statecode</c> or a <c>statuscode</c> (new options, the labels of existing
    /// ones), and the <c>FormulaDefinition</c> and <c>SourceType</c> of a calculated column (the store re-validates the formula
    /// and refuses a <c>SourceType</c> change, plan 2.12a ruling K4); <c>SourceTypeMask</c> is the server's and never compared.
    /// With <paramref name="labelsOnly"/> (a system component for a restricted caller), anything but the labels.
    /// The type is not compared (the store refuses a type change).
    /// </summary>
    public static IReadOnlyList<string> UnappliedAttributeChanges(AttributeMetadata current, AttributeMetadata changes, bool labelsOnly)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        var enumOptions = changes is EnumAttributeMetadata && current is EnumAttributeMetadata;
        var maxLength = changes is StringAttributeMetadata or MemoAttributeMetadata;
        var differences = Differences(Json(current, typeof(AttributeMetadata)), Json(changes, typeof(AttributeMetadata)),
            Json(New(changes), typeof(AttributeMetadata)),
            property => Identity.Contains(property) || AttributeLabels.Contains(property) || property == nameof(AttributeMetadata.IsAuditEnabled)
                        || property == ServerOwnedProperties.SourceTypeMask // computed by every read (plan 2.12a, ruling K12)
                        || (enumOptions && property == nameof(EnumAttributeMetadata.OptionSet))
                        // A calculated column's definition (ruling K4): the store re-validates the formula and refuses a SourceType change.
                        || (!labelsOnly && property is nameof(AttributeMetadata.SourceType) or CalculatedFormula)
                        || (!labelsOnly && (property == nameof(AttributeMetadata.RequiredLevel) || property == nameof(AttributeMetadata.IsSecured)
                                            || (changes is StringAttributeMetadata && property == nameof(AttributeMetadata.AutoNumberFormat))
                                            || (maxLength && property == nameof(StringAttributeMetadata.MaxLength))
                                            || (changes is FileAttributeMetadata && property == nameof(FileAttributeMetadata.MaxSizeInKB))
                                            || (changes is ImageAttributeMetadata && property is nameof(ImageAttributeMetadata.MaxSizeInKB)
                                                or nameof(ImageAttributeMetadata.CanStoreFullImage) or nameof(ImageAttributeMetadata.IsPrimaryImage)))),
            "");
        differences.AddRange(UnappliedAuditChanges(current.IsAuditEnabled, changes.IsAuditEnabled));
        if (labelsOnly && changes.IsAuditEnabled is { } audit && current.IsAuditEnabled is { } now && audit.Value != now.Value
            && !differences.Contains(nameof(AttributeMetadata.IsAuditEnabled)))
            differences.Add(nameof(AttributeMetadata.IsAuditEnabled));
        if (enumOptions && ((EnumAttributeMetadata)changes).OptionSet is { } set)
            differences.AddRange(OptionSetDifferences(((EnumAttributeMetadata)current).OptionSet, set, labelsOnly));
        return differences;
    }

    /// <summary>
    /// The properties of <paramref name="changes"/> that differ from <paramref name="current"/> and that UpdateOptionSet does
    /// not apply to a global option set: anything but the labels and the options (new ones are added, the labels of existing
    /// ones changed; the other properties of an existing option are compared like <see cref="UnappliedAttributeChanges"/>).
    /// </summary>
    public static IReadOnlyList<string> UnappliedOptionSetChanges(OptionSetMetadata current, OptionSetMetadata changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        return OptionSetDifferences(current, changes, labelsOnly: false, prefix: "",
            ignored: property => Identity.Contains(property) || OptionSetApplied.Contains(property));
    }

    /// <summary>
    /// Removes from the options of <paramref name="changes"/> those equal to the current ones (a retrieved definition sent
    /// back): only new and relabelled options reach the store (a global option set's are changed on the option set).
    /// </summary>
    public static void RemoveUnchangedOptions(AttributeMetadata current, AttributeMetadata changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes is not EnumAttributeMetadata { OptionSet.Options: { Count: > 0 } options } || current is not EnumAttributeMetadata currentEnum) return;
        foreach (var option in options.ToList())
        {
            var existing = currentEnum.OptionSet?.Options.FirstOrDefault(o => o.Value == option.Value);
            if (existing is not null && JsonNode.DeepEquals(Node(option, option.GetType()), Node(existing, existing.GetType()))) options.Remove(option);
        }
    }

    private static List<string> OptionSetDifferences(OptionSetMetadata? current, OptionSetMetadata changes, bool labelsOnly,
        string prefix = "OptionSet.", Func<string, bool>? ignored = null)
    {
        var differences = Differences(Json(current, typeof(OptionSetMetadata)), Json(changes, typeof(OptionSetMetadata)),
            Json(new OptionSetMetadata(), typeof(OptionSetMetadata)),
            property => property == nameof(OptionSetMetadata.Options) || (ignored?.Invoke(property) ?? false), prefix);
        foreach (var option in changes.Options)
        {
            var existing = current?.Options.FirstOrDefault(o => o.Value == option.Value);
            var at = $"{prefix}Options[{option.Value?.ToString(CultureInfo.InvariantCulture)}]";
            if (existing is null)
            {
                if (labelsOnly) differences.Add($"{at} (a new option)");
                continue;
            }
            differences.AddRange(Differences(Json(existing, existing.GetType()), Json(option, option.GetType()),
                Json(Activator.CreateInstance(option.GetType()), option.GetType()), AppliedOptionProperties.Contains, at + "."));
        }
        return differences;
    }

    /// <summary>
    /// The properties of <paramref name="changes"/> (not <paramref name="ignored"/>, not null, not the value a new SDK object
    /// has) whose value differs from <paramref name="current"/>'s.
    /// </summary>
    private static List<string> Differences(JsonObject current, JsonObject changes, JsonObject defaults, Func<string, bool> ignored, string prefix)
    {
        var differences = new List<string>();
        foreach (var (property, value) in changes)
        {
            if (ignored(property) || value is null) continue;
            if (defaults.TryGetPropertyValue(property, out var initial) && JsonNode.DeepEquals(value, initial)) continue;
            if (!JsonNode.DeepEquals(value, current[property])) differences.Add(prefix + property);
        }
        return differences;
    }

    private static object New(AttributeMetadata attribute) => Activator.CreateInstance(attribute.GetType())!;

    private static JsonObject Json(object? value, Type type) => value is null ? new JsonObject() : Node(value, type);

    private static JsonObject Node(object value, Type type) => JsonSerializer.SerializeToNode(value, type, MetadataSerializer.Options)!.AsObject();

    /// <summary>A setting's value as the export writes it: <c>1</c>/<c>0</c> for a flag (a managed property's value), else its text.</summary>
    private static string? Text(JsonNode value) => value switch
    {
        JsonObject managed when managed["Value"] is { } inner => Text(inner),
        JsonValue v when v.TryGetValue<bool>(out var flag) => flag ? "1" : "0",
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    private static string Normalize(string exported) => exported switch
    {
        "True" or "true" => "1",
        "False" or "false" => "0",
        _ => exported,
    };
}
