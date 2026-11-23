namespace Xrmd.Model.Metadata;

/// <summary>The MultiSelectPicklist column type (Web API <c>MultiSelectPicklistAttributeMetadata</c>).</summary>
[WireType("MultiSelectPicklistAttributeMetadata")]
public class MultiChoiceColumn : ChoiceColumnBase
{
    public MultiChoiceColumn()
    {
        AttributeType = ColumnTypeCode.Virtual;
        AttributeTypeName = new ColumnTypeName { Value = "MultiSelectPicklistType" };
    }

    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
    public string? ParentPicklistLogicalName { get; set; }
    public List<string>? ChildPicklistLogicalNames { get; set; }
}
