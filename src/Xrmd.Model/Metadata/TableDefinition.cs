namespace Xrmd.Model.Metadata;

/// <summary>A table definition (Web API <c>EntityMetadata</c>).</summary>
[WireType("EntityMetadata")]
public sealed class TableDefinition
{
    public int? ActivityTypeMask { get; set; }
    public List<ColumnDefinition>? Attributes { get; set; }
    public bool? AutoRouteToOwnerQueue { get; set; }
    public bool? CanTriggerWorkflow { get; set; }
    public LabelSet? Description { get; set; }
    public LabelSet? DisplayCollectionName { get; set; }
    public LabelSet? DisplayName { get; set; }
    public bool? EntityHelpUrlEnabled { get; set; }
    public string? EntityHelpUrl { get; set; }
    public bool? IsDocumentManagementEnabled { get; set; }
    public bool? IsOneNoteIntegrationEnabled { get; set; }
    public bool? IsInteractionCentricEnabled { get; set; }
    public bool? IsKnowledgeManagementEnabled { get; set; }
    public bool? IsSLAEnabled { get; set; }
    public bool? IsBPFEntity { get; set; }
    public bool? IsDocumentRecommendationsEnabled { get; set; }
    public bool? IsMSTeamsIntegrationEnabled { get; set; }
    public string? SettingOf { get; set; }
    public Guid? DataProviderId { get; set; }
    public Guid? DataSourceId { get; set; }
    public bool? AutoCreateAccessTeams { get; set; }
    public bool? IsActivity { get; set; }
    public bool? IsActivityParty { get; set; }
    public ManagedFlag? IsAuditEnabled { get; set; }
    public bool? IsRetrieveAuditEnabled { get; set; }
    public bool? IsRetrieveMultipleAuditEnabled { get; set; }
    public bool? IsArchivalEnabled { get; set; }
    public bool? IsRetentionEnabled { get; set; }
    public TableClusterMode? ClusterMode { get; set; }
    public bool? CanChangeClusterMode { get; set; }
    public bool? AutoReplicateClusterRecords { get; set; }
    public bool? IsAvailableOffline { get; set; }
    public bool? IsChildEntity { get; set; }
    public bool? IsAIRUpdated { get; set; }
    public ManagedFlag? IsValidForQueue { get; set; }
    public ManagedFlag? IsConnectionsEnabled { get; set; }
    public string? IconLargeName { get; set; }
    public string? IconMediumName { get; set; }
    public string? IconSmallName { get; set; }
    public string? IconVectorName { get; set; }
    public bool? IsCustomEntity { get; set; }
    public bool? IsBusinessProcessEnabled { get; set; }
    public ManagedFlag? IsCustomizable { get; set; }
    public ManagedFlag? IsRenameable { get; set; }
    public ManagedFlag? IsMappable { get; set; }
    public ManagedFlag? IsDuplicateDetectionEnabled { get; set; }
    public ManagedFlag? CanCreateAttributes { get; set; }
    public ManagedFlag? CanCreateForms { get; set; }
    public ManagedFlag? CanCreateViews { get; set; }
    public ManagedFlag? CanCreateCharts { get; set; }
    public ManagedFlag? CanBeRelatedEntityInRelationship { get; set; }
    public ManagedFlag? CanBePrimaryEntityInRelationship { get; set; }
    public ManagedFlag? CanBeInManyToMany { get; set; }
    public ManagedFlag? CanBeInCustomEntityAssociation { get; set; }
    public ManagedFlag? CanEnableSyncToExternalSearchIndex { get; set; }
    public bool? SyncToExternalSearchIndex { get; set; }
    public ManagedFlag? CanModifyAdditionalSettings { get; set; }
    public ManagedFlag? CanChangeHierarchicalRelationship { get; set; }
    public bool? IsOptimisticConcurrencyEnabled { get; set; }
    public bool? ChangeTrackingEnabled { get; set; }
    public ManagedFlag? CanChangeTrackingBeEnabled { get; set; }
    public bool? IsImportable { get; set; }
    public bool? IsIntersect { get; set; }
    public ManagedFlag? IsMailMergeEnabled { get; set; }
    public bool? IsManaged { get; set; }
    public bool? IsEnabledForCharts { get; set; }
    public bool? IsEnabledForTrace { get; set; }
    public bool? IsValidForAdvancedFind { get; set; }
    public ManagedFlag? IsVisibleInMobile { get; set; }
    public ManagedFlag? IsVisibleInMobileClient { get; set; }
    public ManagedFlag? IsReadOnlyInMobileClient { get; set; }
    public ManagedFlag? IsOfflineInMobileClient { get; set; }
    public int? DaysSinceRecordLastModified { get; set; }
    public string? MobileOfflineFilters { get; set; }
    public bool? IsReadingPaneEnabled { get; set; }
    public bool? IsQuickCreateEnabled { get; set; }
    public string? LogicalName { get; set; }
    public List<ManyToManyRelationship>? ManyToManyRelationships { get; set; }
    public List<OneToManyRelationship>? ManyToOneRelationships { get; set; }
    public List<OneToManyRelationship>? OneToManyRelationships { get; set; }
    public int? ObjectTypeCode { get; set; }
    public TableOwnership? OwnershipType { get; set; }
    public string? PrimaryNameAttribute { get; set; }
    public string? PrimaryImageAttribute { get; set; }
    public string? PrimaryIdAttribute { get; set; }
    public List<TablePrivilege>? Privileges { get; set; }
    public string? RecurrenceBaseEntityLogicalName { get; set; }
    public string? ReportViewName { get; set; }
    public string? SchemaName { get; set; }
    public string? IntroducedVersion { get; set; }
    public bool? IsStateModelAware { get; set; }
    public bool? EnforceStateTransitions { get; set; }
    public string? ExternalName { get; set; }
    public string? EntityColor { get; set; }
    public List<string>? PrimaryKey { get; set; }
    public List<TableKey>? Keys { get; set; }
    public string? LogicalCollectionName { get; set; }
    public string? ExternalCollectionName { get; set; }
    public string? CollectionSchemaName { get; set; }
    public string? EntitySetName { get; set; }
    public bool? IsEnabledForExternalChannels { get; set; }
    public bool? IsPrivate { get; set; }
    public bool? UsesBusinessDataLabelTable { get; set; }
    public bool? IsLogicalEntity { get; set; }
    public bool? HasNotes { get; set; }
    public bool? HasActivities { get; set; }
    public bool? HasFeedback { get; set; }
    public bool? IsSolutionAware { get; set; }
    public List<TableSetting>? Settings { get; set; }
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public bool? HasEmailAddresses { get; set; }
    public Guid? OwnerId { get; set; }
    public int? OwnerIdType { get; set; }
    public Guid? OwningBusinessUnit { get; set; }
    public string? TableType { get; set; }
    public Guid? MetadataId { get; set; }
    public bool? HasChanged { get; set; }
}

/// <summary>An alternate key of a table (Web API <c>EntityKeyMetadata</c>).</summary>
[WireType("EntityKeyMetadata")]
public class TableKey
{
    public LabelSet? DisplayName { get; set; }
    public string? LogicalName { get; set; }
    public string? SchemaName { get; set; }
    public string? EntityLogicalName { get; set; }
    public List<string>? KeyAttributes { get; set; }
    public ManagedFlag? IsCustomizable { get; set; }
    public bool? IsManaged { get; set; }
    public string? IntroducedVersion { get; set; }
    public KeyIndexState EntityKeyIndexStatus { get; set; }
    public RecordPointer? AsyncJob { get; set; }
    public bool? IsSynchronous { get; set; }
    public bool? IsExportKey { get; set; }
    public bool? IsSecondaryKey { get; set; }
    public Guid? MetadataId { get; set; }
    public bool? HasChanged { get; set; }
}

/// <summary>A security privilege of a table (Web API <c>SecurityPrivilegeMetadata</c>).</summary>
[WireType("SecurityPrivilegeMetadata")]
public class TablePrivilege
{
    public bool CanBeBasic { get; set; }
    public bool CanBeDeep { get; set; }
    public bool CanBeGlobal { get; set; }
    public bool CanBeLocal { get; set; }
    public bool CanBeEntityReference { get; set; }
    public bool CanBeParentEntityReference { get; set; }
    public bool CanBeRecordFilter { get; set; }
    public string? Name { get; set; }
    public Guid PrivilegeId { get; set; }
    public PrivilegeKind PrivilegeType { get; set; }
}

/// <summary>A name/value setting of a table or column; the value is held as raw JSON (Web API <c>EntitySetting</c>).</summary>
[WireType("EntitySetting")]
public class TableSetting
{
    public string? Name { get; set; }
    public System.Text.Json.JsonElement? Value { get; set; }
    public List<TableSetting>? ChildSettings { get; set; }
}

/// <summary>A reference to a row of a table (Web API <c>EntityReference</c>).</summary>
[WireType("EntityReference")]
public class RecordPointer
{
    public Guid Id { get; set; }
    public string? LogicalName { get; set; }
    public string? Name { get; set; }
    /// <summary>The alternate key values; written as an array of <c>{"Key","Value"}</c> pairs like the Web API.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(KeyValueArrayConverter))]
    public Dictionary<string, System.Text.Json.JsonElement>? KeyAttributes { get; set; } = new(StringComparer.Ordinal);
    public string? RowVersion { get; set; }
}
