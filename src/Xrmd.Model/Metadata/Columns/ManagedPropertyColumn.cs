namespace Xrmd.Model.Metadata;

/// <summary>The ManagedProperty column type (Web API <c>ManagedPropertyAttributeMetadata</c>).</summary>
[WireType("ManagedPropertyAttributeMetadata")]
public class ManagedPropertyColumn : ColumnDefinition
{
    public ManagedPropertyColumn()
    {
        AttributeType = ColumnTypeCode.ManagedProperty;
        AttributeTypeName = new ColumnTypeName { Value = "ManagedPropertyType" };
        ValueAttributeTypeCode = ColumnTypeCode.Boolean;
    }

    public string? ManagedPropertyLogicalName { get; set; }
    public int? ParentComponentType { get; set; }
    public string? ParentAttributeName { get; set; }
    public ColumnTypeCode ValueAttributeTypeCode { get; set; }
}
