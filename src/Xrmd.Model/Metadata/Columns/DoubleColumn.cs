namespace Xrmd.Model.Metadata;

/// <summary>The Double column type (Web API <c>DoubleAttributeMetadata</c>).</summary>
[WireType("DoubleAttributeMetadata")]
public class DoubleColumn : ColumnDefinition
{
    public DoubleColumn()
    {
        AttributeType = ColumnTypeCode.Double;
        AttributeTypeName = new ColumnTypeName { Value = "DoubleType" };
    }

    public InputMethodMode? ImeMode { get; set; }
    public double? MaxValue { get; set; }
    public double? MinValue { get; set; }
    public int? Precision { get; set; }
    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
}
