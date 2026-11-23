namespace Xrmd.Model.Metadata;

/// <summary>The Picklist column type (Web API <c>PicklistAttributeMetadata</c>).</summary>
[WireType("PicklistAttributeMetadata")]
public class ChoiceColumn : ChoiceColumnBase
{
    public ChoiceColumn()
    {
        AttributeType = ColumnTypeCode.Picklist;
        AttributeTypeName = new ColumnTypeName { Value = "PicklistType" };
    }

    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
    public string? ParentPicklistLogicalName { get; set; }
    public List<string>? ChildPicklistLogicalNames { get; set; }
}
