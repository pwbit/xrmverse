namespace Xrmd.Model.Metadata;

/// <summary>Base of the column definitions; concrete like the SDK type (Web API <c>AttributeMetadata</c>).</summary>
[WireType("AttributeMetadata")]
public class ColumnDefinition
{
    public string? AttributeOf { get; set; }
    public ColumnTypeCode? AttributeType { get; set; }
    public ColumnTypeName? AttributeTypeName { get; set; }
    public int? ColumnNumber { get; set; }
    public LabelSet? Description { get; set; }
    public LabelSet? DisplayName { get; set; }
    public string? DeprecatedVersion { get; set; }
    public string? IntroducedVersion { get; set; }
    public string? EntityLogicalName { get; set; }
    public ManagedFlag? IsAuditEnabled { get; set; }
    public bool? IsCustomAttribute { get; set; }
    public bool? IsPrimaryId { get; set; }
    public bool IsValidODataAttribute { get; set; }
    public bool? IsPrimaryName { get; set; }
    public bool? IsValidForCreate { get; set; }
    public bool? IsValidForRead { get; set; }
    public bool? IsValidForUpdate { get; set; }
    public bool? CanBeSecuredForRead { get; set; }
    public bool? CanBeSecuredForCreate { get; set; }
    public bool? CanBeSecuredForUpdate { get; set; }
    public bool? IsSecured { get; set; }
    public bool? IsRetrievable { get; set; }
    public bool? IsFilterable { get; set; }
    public bool? IsSearchable { get; set; }
    public bool? IsManaged { get; set; }
    public ManagedFlag? IsGlobalFilterEnabled { get; set; }
    public ManagedFlag? IsSortableEnabled { get; set; }
    public Guid? LinkedAttributeId { get; set; }
    public string? LogicalName { get; set; }
    public ManagedFlag? IsCustomizable { get; set; }
    public ManagedFlag? IsRenameable { get; set; }
    public ManagedFlag? IsValidForAdvancedFind { get; set; }
    public bool? IsValidForForm { get; set; }
    public bool? IsRequiredForForm { get; set; }
    public bool? IsValidForGrid { get; set; }
    public RequiredLevelSetting? RequiredLevel { get; set; }
    public ManagedFlag? CanModifyAdditionalSettings { get; set; }
    public string? SchemaName { get; set; }
    public string? ExternalName { get; set; }
    public bool? IsLogical { get; set; }
    public bool? IsDataSourceSecret { get; set; }
    public string? InheritsFrom { get; set; }
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public int? SourceType { get; set; }
    public string? AutoNumberFormat { get; set; }
    public List<TableSetting>? Settings { get; set; }
    public Guid? MetadataId { get; set; }
    public bool? HasChanged { get; set; }
}
