namespace Xrmd.Model.Metadata;

/// <summary>The Boolean column type (Web API <c>BooleanAttributeMetadata</c>).</summary>
[WireType("BooleanAttributeMetadata")]
public class BooleanColumn : ColumnDefinition
{
    public BooleanColumn()
    {
        AttributeType = ColumnTypeCode.Boolean;
        AttributeTypeName = new ColumnTypeName { Value = "BooleanType" };
    }

    public bool? DefaultValue { get; set; }
    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
    public BooleanChoiceSet? OptionSet { get; set; }
}
