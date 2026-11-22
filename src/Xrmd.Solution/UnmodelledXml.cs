using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution;

/// <summary>
/// What a solution file gives a component that xrmverse does not model (its other child elements and XML attributes:
/// auditing, search and customization flags, mobile settings, dependencies, any element xrmverse does not know), kept
/// opaque so that an export writes it again in its original place.
/// </summary>
/// <remarks>
/// <para><see cref="Capture"/> keeps the component's element with each child it models replaced by an empty marker of the
/// same name (its position) and each child it does not model as it is; the XML attributes it models are dropped. A modelled
/// child with a nested shape (the option set of an attribute, its options, the images of a step) is captured in its marker,
/// matched later by a key (an option's value, an image's id).</para>
/// <para><see cref="Merge"/> writes the kept children in their order, each marker replaced by what xrmverse writes now;
/// what xrmverse writes that the kept XML has no marker for goes after every element written before it. A component with
/// nothing kept (created in xrmverse) is merged with its shape's defaults: the values Dataverse gives a component created
/// through the SDK, from memory of real exports and not verified.</para>
/// </remarks>
public static class UnmodelledXml
{
    /// <summary>The namespace of the marker attributes.</summary>
    public static readonly XNamespace Namespace = "urn:xrmverse:unmodelled";

    private static readonly XName Marker = Namespace + "m";
    private static readonly XName KeyName = Namespace + "key";

    /// <summary>The key of an entity's kept XML (<c>EntityInfo/entity</c>).</summary>
    public static (int, string) EntityKey(string logicalName) => (ComponentType.Entity, logicalName);

    /// <summary>The key of an attribute's kept XML.</summary>
    public static (int, string) AttributeKey(string entity, string attribute) => (ComponentType.Attribute, $"{entity}.{attribute}");

    /// <summary>The key of a global option set's kept XML (names ignore case).</summary>
    public static (int, string) OptionSetKey(string name) => (ComponentType.OptionSet, name.ToLowerInvariant());

    /// <summary>The key of a relationship's kept XML (names ignore case).</summary>
    public static (int, string) RelationshipKey(string schemaName) => (ComponentType.EntityRelationship, schemaName.ToLowerInvariant());

    /// <summary>The key of a step's kept XML (with its images).</summary>
    public static (int, string) StepKey(Guid id) => (ComponentType.SdkMessageProcessingStep, id.ToString("D"));

    /// <summary>The key of a web resource's kept XML.</summary>
    public static (int, string) WebResourceKey(Guid id) => (ComponentType.WebResource, id.ToString("D"));

    /// <summary>The key of a plug-in assembly's kept XML (with its plug-in types).</summary>
    public static (int, string) AssemblyKey(Guid id) => (ComponentType.PluginAssembly, id.ToString("D"));

    /// <summary>The value of the marker of a child captured from a file (a template's marker is <c>1</c>).</summary>
    private const string Captured = "c";

    /// <summary>
    /// Replaces each child of <paramref name="kept"/> named in <paramref name="names"/> by the marker of a modelled child, in
    /// place (what <see cref="Capture"/> does for a child xrmverse models), and returns the values it held (name, text) of the
    /// ones that were not markers already. Used to move a child that became modelled out of the kept XML of an existing component.
    /// </summary>
    public static List<(string Name, string Value)> TakeModelled(XElement kept, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(kept);
        var taken = new List<(string, string)>();
        foreach (var child in kept.Elements().Where(e => names.Contains(e.Name.LocalName) && !IsMarker(e)).ToList())
        {
            taken.Add((child.Name.LocalName, child.Value));
            var marker = new XElement(child.Name);
            marker.SetAttributeValue(Marker, Captured);
            child.ReplaceWith(marker);
        }
        return taken;
    }

    /// <summary>
    /// A copy of <paramref name="kept"/> with a marker of <paramref name="name"/> as its last child: the place of a modelled child
    /// the file did not have and the export now writes (plan 2.11: <c>HasRelatedNotes</c> and <c>HasRelatedActivities</c> come
    /// last in a real export, written only when True; plan 4.1a: <c>IsValidForQueue</c> after them, from memory).
    /// </summary>
    public static XElement WithMarkerAtEnd(XElement kept, string name)
    {
        ArgumentNullException.ThrowIfNull(kept);
        var copy = new XElement(kept);
        var marker = new XElement(name);
        marker.SetAttributeValue(Marker, Captured);
        copy.Add(marker);
        return copy;
    }

    /// <summary>True for a marker (the position of a modelled child).</summary>
    public static bool IsMarker(XElement element) => element.Attribute(Marker) is not null;

    // ---- shapes -----------------------------------------------------------------------------------------------------

    private static readonly UnmodelledShape OptionShape = new(["labels", "Descriptions"], ["value", "ExternalValue", "Color"],
        Key: e => (string?)e.Attribute("value"), Defaults: _ => new XElement("option", new XAttribute("IsHidden", "0")));

    private static readonly UnmodelledShape StateShape = new(["labels", "Descriptions"], ["value", "defaultstatus", "invariantname"],
        Key: e => (string?)e.Attribute("value"));

    private static readonly UnmodelledShape StatusShape = new(["labels", "Descriptions"], ["value", "state"],
        Key: e => (string?)e.Attribute("value"));

    /// <summary>A global or local option set (<c>optionset</c>) and its options.</summary>
    public static UnmodelledShape OptionSetShape { get; } = new(
        ["OptionSetType", "IsGlobal", "displaynames", "Descriptions", "options", "states", "statuses"],
        ["Name", "localizedName", "description"],
        new Dictionary<string, UnmodelledShape>(StringComparer.Ordinal)
        {
            ["options"] = new([ "option" ], [], new Dictionary<string, UnmodelledShape> { ["option"] = OptionShape }),
            ["states"] = new([ "state" ], [], new Dictionary<string, UnmodelledShape> { ["state"] = StateShape }),
            ["statuses"] = new([ "status" ], [], new Dictionary<string, UnmodelledShape> { ["status"] = StatusShape }),
        },
        Defaults: written => Template("optionset",
            ("OptionSetType", null), ("IsGlobal", null), ("IntroducedVersion", "1.0.0.0"), ("IsCustomizable", "1"), ("displaynames", null),
            ("Descriptions", null), ("options", null), ("states", null), ("statuses", null)));

    /// <summary>The elements xrmverse writes for an attribute, as type-specific ones in the order of an export.</summary>
    private static readonly string[] TypeSpecificElements =
    [
        "Format", "MaxLength", "Length", "MinValue", "MaxValue", "Accuracy", "AccuracySource", "AppDefaultValue", "Behavior",
        "OptionSetName", "optionset", "LookupTypes", "MaxSizeInKB", "CanStoreFullImage", "IsPrimaryImage", "MaxHeight", "MaxWidth",
    ];

    /// <summary>
    /// An attribute (<c>attributes/attribute</c>); <c>DisplayMask</c> is kept as written (xrmverse does not model it). The
    /// <c>FormulaDefinitionFileName</c> of a calculated, rollup or formula column is written by xrmverse (plans 2.12a, 2.12b, 2.12c;
    /// after <c>SourceType</c> for a column created in xrmverse, at its kept place, the end of the element in the parity export, for an
    /// imported one). The
    /// <c>CalculationOf</c> of a computed column's <c>_base</c> is kept as the file has it; xrmverse writes it, after the type's
    /// elements, only for a <c>_base</c> with nothing kept (plan 2.12b, ruling R14).
    /// </summary>
    public static UnmodelledShape AttributeShape { get; } = new(
        ["Type", "Name", "LogicalName", "RequiredLevel", "ValidForUpdateApi", "ValidForCreateApi", "IsCustomField", "IsSecured",
            "IsAuditEnabled", "SourceType", "FormulaDefinitionFileName", "AutoNumberFormat", .. TypeSpecificElements, "displaynames", "Descriptions"],
        ["PhysicalName"],
        new Dictionary<string, UnmodelledShape>(StringComparer.Ordinal) { ["optionset"] = OptionSetShape },
        Defaults: AttributeDefaults);

    /// <summary>An entity (<c>EntityInfo/entity</c>).</summary>
    public static UnmodelledShape EntityShape { get; } = new(
        ["LocalizedNames", "LocalizedCollectionNames", "Descriptions", "attributes", "EntitySetName", "OwnershipTypeMask", "IsActivity",
            "ActivityTypeMask", "IsAuditEnabled", "CanModifyAuditSettings", "AutoCreateAccessTeams", "ChangeTrackingEnabled",
            "CanChangeTrackingBeEnabled", "HasRelatedNotes", "HasRelatedActivities", "IsDuplicateCheckSupported", "IsValidForQueue"],
        ["Name"],
        Defaults: EntityDefaults);

    /// <summary>A relationship (<c>EntityRelationship</c>).</summary>
    public static UnmodelledShape RelationshipShape { get; } = new(
        ["EntityRelationshipType", "IsCustomRelationship", "IsHierarchical", "ReferencingEntityName", "ReferencedEntityName",
            "FirstEntityName", "SecondEntityName", "IntersectEntityName", "CascadeAssign", "CascadeDelete", "CascadeMerge", "CascadeArchive",
            "CascadeReparent", "CascadeShare", "CascadeUnshare", "CascadeRollupView", "ReferencingAttributeName", "RelationshipDescription",
            "EntityRelationshipRoles"],
        ["Name"],
        Defaults: written => (string?)written.Element("EntityRelationshipType") == "ManyToMany"
            ? Template("EntityRelationship", ("EntityRelationshipType", null), ("IsCustomRelationship", null), ("IsCustomizable", "1"),
                ("IntroducedVersion", "1.0.0.0"), ("SecurityTypes", "None"), ("IsHierarchical", null), ("FirstEntityName", null),
                ("SecondEntityName", null), ("IntersectEntityName", null), ("CascadeAssign", null), ("CascadeDelete", null),
                ("CascadeMerge", null), ("CascadeArchive", null), ("CascadeReparent", null), ("CascadeShare", null), ("CascadeUnshare", null),
                ("CascadeRollupView", null), ("IsValidForAdvancedFind", "1"), ("RelationshipDescription", null),
                ("EntityRelationshipRoles", null))
            : Template("EntityRelationship", ("EntityRelationshipType", null), ("IsCustomRelationship", null), ("IsCustomizable", "1"),
                ("IntroducedVersion", "1.0.0.0"), ("IsHierarchical", null), ("ReferencingEntityName", null), ("ReferencedEntityName", null),
                ("CascadeAssign", null), ("CascadeDelete", null), ("CascadeMerge", null), ("CascadeArchive", null), ("CascadeReparent", null),
                ("CascadeShare", null), ("CascadeUnshare", null), ("CascadeRollupView", null), ("IsValidForAdvancedFind", "1"),
                ("ReferencingAttributeName", null), ("RelationshipDescription", null), ("EntityRelationshipRoles", null)));

    private static readonly UnmodelledShape ImageShape = new(
        ["SdkMessageProcessingStepImageId", "Attributes", "EntityAlias", "ImageType", "MessagePropertyName"],
        ["Name"],
        Key: e => Guid.TryParse((string?)e.Element("SdkMessageProcessingStepImageId"), out var id) ? id.ToString("D") : null,
        Defaults: _ => Template("SdkMessageProcessingStepImage", ("SdkMessageProcessingStepImageId", null), ("Attributes", null),
            ("EntityAlias", null), ("ImageType", null), ("MessagePropertyName", null), ("IsCustomizable", "1")));

    /// <summary>A step (<c>SdkMessageProcessingStep</c>) with its images.</summary>
    public static UnmodelledShape StepShape { get; } = new(
        ["SdkMessageId", "PluginTypeName", "PluginTypeId", "PrimaryEntity", "AsyncAutoDelete", "Description", "FilteringAttributes",
            "ImpersonatingUserId", "InvocationSource", "Mode", "Rank", "SdkMessageFilterId", "Stage", "SupportedDeployment",
            "CustomConfiguration", "StateCode", "StatusCode", "SdkMessageProcessingStepImages"],
        ["Name", "SdkMessageProcessingStepId"],
        new Dictionary<string, UnmodelledShape>(StringComparer.Ordinal)
        {
            ["SdkMessageProcessingStepImages"] = new(["SdkMessageProcessingStepImage"], [],
                new Dictionary<string, UnmodelledShape> { ["SdkMessageProcessingStepImage"] = ImageShape }),
        },
        Defaults: _ => Template("SdkMessageProcessingStep", ("SdkMessageId", null), ("PluginTypeName", null), ("PluginTypeId", null),
            ("PrimaryEntity", null), ("AsyncAutoDelete", null), ("Description", null), ("FilteringAttributes", null),
            ("ImpersonatingUserId", null), ("InvocationSource", null), ("Mode", null), ("Rank", null), ("SdkMessageFilterId", null),
            ("EventHandlerTypeCode", "4602"), ("Stage", null), ("IsCustomizable", "1"), ("IsHidden", "0"), ("SupportedDeployment", null),
            ("IntroducedVersion", "1.0"), ("CustomConfiguration", null), ("StateCode", null), ("StatusCode", null),
            ("SdkMessageProcessingStepImages", null)));

    private static readonly UnmodelledShape PluginTypeShape = new(["FriendlyName", "WorkflowActivityGroupName"], ["AssemblyQualifiedName", "PluginTypeId", "Name"],
        Key: e => Guid.TryParse((string?)e.Attribute("PluginTypeId"), out var id) ? id.ToString("D") : null);

    /// <summary>A plug-in assembly (<c>PluginAssembly</c>) with its plug-in types.</summary>
    public static UnmodelledShape AssemblyShape { get; } = new(
        ["IsolationMode", "SourceType", "FileName", "PluginTypes"],
        ["FullName", "PluginAssemblyId"],
        new Dictionary<string, UnmodelledShape>(StringComparer.Ordinal)
        {
            ["PluginTypes"] = new(["PluginType"], [], new Dictionary<string, UnmodelledShape> { ["PluginType"] = PluginTypeShape }),
        },
        Defaults: _ =>
        {
            var template = Template("PluginAssembly", ("IsolationMode", null), ("SourceType", null), ("IntroducedVersion", "1.0"),
                ("FileName", null), ("PluginTypes", null));
            template.SetAttributeValue("CustomizationLevel", "1");
            return template;
        });

    /// <summary>A web resource (<c>WebResource</c>).</summary>
    public static UnmodelledShape WebResourceShape { get; } = new(
        ["WebResourceId", "Name", "DisplayName", "Description", "WebResourceType", "FileName"],
        [],
        Defaults: _ => Template("WebResource", ("WebResourceId", null), ("Name", null), ("DisplayName", null), ("Description", null),
            ("WebResourceType", null), ("IntroducedVersion", "1.0.0.0"), ("IsEnabledForMobileClient", "0"),
            ("IsAvailableForMobileOffline", "0"), ("DependencyXml", ""), ("IsCustomizable", "1"), ("CanBeDeleted", "1"), ("IsHidden", "0"),
            ("FileName", null)));

    /// <summary>
    /// The value the export writes for the table setting <paramref name="element"/> of a table created in xrmverse (the
    /// default of <see cref="EntityShape"/>); null when the export writes no such setting, or writes it from the metadata.
    /// </summary>
    public static string? EntityDefault(string element) =>
        EntityDefaults(new XElement("entity")).Element(element) is { } setting && !IsMarker(setting) ? setting.Value : null;

    /// <summary>
    /// The settings of a table created through the SDK, in the order of an export (from memory of real exports, not
    /// verified); null values are the elements xrmverse writes.
    /// </summary>
    private static XElement EntityDefaults(XElement _) => Template("entity",
        ("LocalizedNames", null), ("LocalizedCollectionNames", null), ("Descriptions", null), ("attributes", null), ("EntitySetName", null),
        ("IsDuplicateCheckSupported", null), ("IsBusinessProcessEnabled", "0"), ("IsRequiredOffline", "0"), ("IsInteractionCentricEnabled", "0"),
        ("IsCollaboration", "0"), ("AutoRouteToOwnerQueue", "0"), ("IsConnectionsEnabled", "0"), ("EntityColor", ""),
        ("IsDocumentManagementEnabled", "0"), ("AutoCreateAccessTeams", null), ("IsOneNoteIntegrationEnabled", "0"),
        ("IsKnowledgeManagementEnabled", "0"), ("IsSLAEnabled", "0"), ("IsDocumentRecommendationsEnabled", "0"), ("IsBPFEntity", "0"),
        ("OwnershipTypeMask", null), ("IsAuditEnabled", null), ("IsRetrieveAuditEnabled", "0"), ("IsRetrieveMultipleAuditEnabled", "0"),
        ("IsActivity", null), ("ActivityTypeMask", null), ("IsActivityParty", "0"), ("IsReplicated", "0"), ("IsReplicationUserFiltered", "0"),
        ("IsMailMergeEnabled", "0"), ("IsVisibleInMobile", "0"), ("IsVisibleInMobileClient", "0"), ("IsReadOnlyInMobileClient", "0"),
        ("IsOfflineInMobileClient", "0"), ("DaysSinceRecordLastModified", "0"), ("MobileOfflineFilters", ""), ("IsMapiGridEnabled", "1"),
        ("IsReadingPaneEnabled", "1"), ("IsQuickCreateEnabled", "0"), ("SyncToExternalSearchIndex", "0"), ("IntroducedVersion", "1.0.0.0"),
        ("IsCustomizable", "1"), ("IsRenameable", "1"), ("IsMappable", "1"), ("CanModifyAuditSettings", null), ("CanModifyMobileVisibility", "1"),
        ("CanModifyMobileClientVisibility", "1"), ("CanModifyMobileClientReadOnly", "1"), ("CanModifyMobileClientOffline", "1"),
        ("CanModifyConnectionSettings", "1"), ("CanModifyDuplicateDetectionSettings", "1"), ("CanModifyMailMergeSettings", "1"),
        ("CanModifyQueueSettings", "1"), ("CanCreateAttributes", "1"), ("CanCreateForms", "1"), ("CanCreateCharts", "1"), ("CanCreateViews", "1"),
        ("CanModifyAdditionalSettings", "1"), ("CanEnableSyncToExternalSearchIndex", "1"), ("EnforceStateTransitions", "0"),
        ("CanChangeHierarchicalRelationship", "1"), ("EntityHelpUrlEnabled", "0"), ("ChangeTrackingEnabled", null),
        ("CanChangeTrackingBeEnabled", null), ("IsEnabledForExternalChannels", "0"), ("IsMSTeamsIntegrationEnabled", "0"),
        ("IsSolutionAware", "0"), ("HasRelatedNotes", null), ("HasRelatedActivities", null), ("IsValidForQueue", null));

    /// <summary>
    /// The defaults of an attribute created with its table or through the SDK (from memory of real exports, not verified):
    /// its <c>DisplayMask</c> by kind (primary key, primary name, the system columns without one) and the flags of a column,
    /// in the order of an export. The writer marks the primary key and name with <c>xrmverse:kind</c>.
    /// </summary>
    private static XElement AttributeDefaults(XElement written)
    {
        var type = (string?)written.Element("Type");
        var name = (string?)written.Element("Name");
        var mask = (string?)written.Attribute(Namespace + "kind") switch
        {
            "primarykey" => "ValidForAdvancedFind|RequiredForGrid",
            "primaryname" => "PrimaryName|ValidForAdvancedFind|ValidForForm|ValidForGrid|RequiredForForm",
            _ => name switch
            {
                "versionnumber" or "timezoneruleversionnumber" or "utcconversiontimezonecode" => "",
                "importsequencenumber" => "ValidForAdvancedFind",
                "overriddencreatedon" => "ValidForAdvancedFind|ValidForGrid",
                _ => "ValidForAdvancedFind|ValidForForm|ValidForGrid",
            },
        };
        var typeSpecific = new List<(string, string?)>();
        foreach (var element in TypeSpecificElements)
        {
            if (element == "Behavior" && type == "datetime") typeSpecific.Add(("CanChangeDateTimeBehavior", "1"));
            if (element == "LookupTypes" && type is "lookup" or "customer" or "owner") typeSpecific.Add(("LookupStyle", "single"));
            typeSpecific.Add((element, null));
        }
        return Template("attribute",
        [
            ("Type", null), ("Name", null), ("LogicalName", null), ("RequiredLevel", null), ("DisplayMask", mask), ("ImeMode", "auto"),
            ("ValidForUpdateApi", null), ("ValidForReadApi", "1"), ("ValidForCreateApi", null), ("IsCustomField", null),
            ("IsAuditEnabled", null), ("IsSecured", null), ("IntroducedVersion", "1.0.0.0"), ("IsCustomizable", "1"), ("IsRenameable", "1"),
            ("CanModifySearchSettings", "1"), ("CanModifyRequirementLevelSettings", "1"), ("CanModifyAdditionalSettings", "1"),
            ("SourceType", null), ("FormulaDefinitionFileName", null), ("IsGlobalFilterEnabled", "0"), ("IsSortableEnabled", "0"), ("CanModifyGlobalFilterSettings", "1"),
            ("CanModifyIsSortableSettings", "1"), ("IsDataSourceSecret", "0"), ("AutoNumberFormat", null), ("IsSearchable", "0"),
            ("IsFilterable", "0"), ("IsRetrievable", "0"), ("IsLocalizable", "0"), .. typeSpecific, ("displaynames", null),
            ("Descriptions", null),
        ]);
    }

    /// <summary>A kept element: a marker for each null value, the element with its value otherwise.</summary>
    internal static XElement Template(string name, params (string Name, string? Value)[] children) =>
        new(name, children.Select(c => c.Value is null ? new XElement(c.Name, new XAttribute(Marker, "1")) : new XElement(c.Name, c.Value)));

    // ---- capture and merge ------------------------------------------------------------------------------------------

    /// <summary>The kept XML of <paramref name="file"/> (see the class remarks).</summary>
    public static XElement Capture(XElement file, UnmodelledShape shape)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(shape);
        var kept = new XElement(file.Name);
        foreach (var attribute in file.Attributes().Where(a => !a.IsNamespaceDeclaration && !shape.Attributes.Contains(a.Name.LocalName)))
            kept.Add(new XAttribute(attribute));
        foreach (var child in file.Elements())
        {
            var name = child.Name.LocalName;
            if (!shape.Children.Contains(name))
            {
                kept.Add(new XElement(child));
                continue;
            }
            var nested = shape.Nested?.GetValueOrDefault(name);
            var marker = nested is null ? new XElement(child.Name) : Capture(child, nested);
            marker.SetAttributeValue(Marker, Captured);
            if (nested?.Key?.Invoke(child) is { } key) marker.SetAttributeValue(KeyName, key);
            kept.Add(marker);
        }
        return kept;
    }

    /// <summary>
    /// <paramref name="written"/> with what <paramref name="kept"/> keeps put back in place (see the class remarks); with
    /// nothing kept, the shape's defaults.
    /// </summary>
    /// <param name="written">What xrmverse writes of the component.</param>
    /// <param name="kept">The kept XML (<see cref="Capture"/>); null: nothing kept.</param>
    /// <param name="shape">What xrmverse models of the element.</param>
    /// <param name="component">The component, for the error.</param>
    /// <exception cref="XrmdFaultException">
    /// 500 Unexpected naming the component and the element when the kept XML carries, as kept content, a child element or an
    /// XML attribute that xrmverse models: it was not captured from a file and would be written twice (never silently).
    /// </exception>
    public static XElement Merge(XElement written, XElement? kept, UnmodelledShape shape, string? component = null)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(shape);
        component ??= $"the {written.Name.LocalName} element";
        kept ??= shape.Defaults?.Invoke(written);
        if (kept is not null) DemandNothingModelled(kept, shape, component);

        var result = new XElement(written.Name);
        foreach (var attribute in written.Attributes().Where(a => a.Name.Namespace != Namespace)) result.Add(new XAttribute(attribute));
        foreach (var attribute in kept?.Attributes().Where(a => a.Name.Namespace != Namespace && !a.IsNamespaceDeclaration) ?? [])
            if (result.Attribute(attribute.Name) is null) result.Add(new XAttribute(attribute));

        var children = written.Elements().ToList();
        if (children.Count == 0 && kept is null)
        {
            result.Add(written.Nodes().Select(Copy));
            return result;
        }
        if (children.Count == 0) result.Add(written.Nodes().OfType<XText>().Select(t => new XText(t.Value)));

        var output = new List<XElement>();
        var placed = new int?[children.Count];
        foreach (var keptChild in kept?.Elements() ?? [])
        {
            if (!IsMarker(keptChild))
            {
                output.Add(new XElement(keptChild));
                continue;
            }
            var nested = shape.Nested?.GetValueOrDefault(keptChild.Name.LocalName);
            var key = (string?)keptChild.Attribute(KeyName);
            var index = children.FindIndex(c => c.Name == keptChild.Name && placed[children.IndexOf(c)] is null
                                                 && (key is null || nested?.Key?.Invoke(c) == key));
            if (index < 0) continue; // xrmverse no longer writes it
            // A template's marker keeps nothing (the nested shape's defaults apply); a captured one, even empty, keeps what the file had.
            output.Add(nested is null ? Strip(children[index])
                : Merge(children[index], IsTemplateMarker(keptChild) ? null : keptChild, nested, component));
            placed[index] = output.Count - 1;
        }
        for (var i = 0; i < children.Count; i++)
        {
            if (placed[i] is not null) continue;
            var nested = shape.Nested?.GetValueOrDefault(children[i].Name.LocalName);
            var element = nested is null ? Strip(children[i]) : Merge(children[i], null, nested, component);
            // After every element written before it; else before every one written after it; else last.
            var before = Enumerable.Range(0, i).Select(j => placed[j]).Max();
            var after = Enumerable.Range(i + 1, children.Count - i - 1).Select(j => placed[j]).Min();
            var at = before is { } b ? b + 1 : after ?? output.Count;
            output.Insert(at, element);
            for (var j = 0; j < placed.Length; j++)
                if (placed[j] is { } p && p >= at) placed[j] = p + 1;
            placed[i] = at;
        }
        result.Add(output);
        return result;
    }

    /// <summary>An empty marker of a template (not captured from a file): it keeps nothing.</summary>
    private static bool IsTemplateMarker(XElement marker) =>
        (string?)marker.Attribute(Marker) != Captured && !marker.HasElements
        && !marker.Attributes().Any(a => a.Name.Namespace != Namespace && !a.IsNamespaceDeclaration);

    /// <exception cref="XrmdFaultException">500 for kept content xrmverse models (see <see cref="Merge"/>).</exception>
    private static void DemandNothingModelled(XElement kept, UnmodelledShape shape, string component)
    {
        if (kept.Attributes().FirstOrDefault(a => a.Name.Namespace != Namespace && !a.IsNamespaceDeclaration
                                                  && shape.Attributes.Contains(a.Name.LocalName)) is { } attribute)
            throw Corrupted(component, $"the XML attribute '{attribute.Name.LocalName}' of '{kept.Name.LocalName}'");
        if (kept.Elements().FirstOrDefault(e => !IsMarker(e) && shape.Children.Contains(e.Name.LocalName)) is { } element)
            throw Corrupted(component, $"the element '{element.Name.LocalName}' in '{kept.Name.LocalName}'");
    }

    private static XrmdFaultException Corrupted(string component, string what) =>
        new(ErrorCodes.Unexpected,
            $"The kept XML of {component} carries {what}, which xrmverse writes itself: it was not kept from a solution file and is not exported.",
            500);

    private static XNode Copy(XNode node) => node is XElement element ? Strip(element) : node is XText text ? new XText(text.Value) : node;

    /// <summary>A copy of <paramref name="element"/> without the writer's annotations (<see cref="Namespace"/> attributes).</summary>
    private static XElement Strip(XElement element)
    {
        var copy = new XElement(element);
        foreach (var e in copy.DescendantsAndSelf()) e.Attributes().Where(a => a.Name.Namespace == Namespace).Remove();
        return copy;
    }
}

/// <summary>
/// What xrmverse models of a component's element: the child elements and XML attributes it writes (the others are kept),
/// the shapes of the modelled children that keep content of their own, the key that matches a repeated child to its kept
/// XML, and the defaults of a component with nothing kept.
/// </summary>
/// <param name="Children">The child elements xrmverse writes.</param>
/// <param name="Attributes">The XML attributes xrmverse writes.</param>
/// <param name="Nested">The shapes of modelled children that keep content of their own.</param>
/// <param name="Key">The key matching a repeated element to its kept XML.</param>
/// <param name="Defaults">The kept XML of an element with nothing kept, from what xrmverse writes.</param>
public sealed record UnmodelledShape(
    IReadOnlyCollection<string> Children,
    IReadOnlyCollection<string> Attributes,
    IReadOnlyDictionary<string, UnmodelledShape>? Nested = null,
    Func<XElement, string?>? Key = null,
    Func<XElement, XElement?>? Defaults = null);
