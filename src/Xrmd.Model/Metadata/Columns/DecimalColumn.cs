namespace Xrmd.Model.Metadata;

/// <summary>The Decimal column type (Web API <c>DecimalAttributeMetadata</c>).</summary>
[WireType("DecimalAttributeMetadata")]
public class DecimalColumn : ColumnDefinition
{
    public DecimalColumn()
    {
        AttributeType = ColumnTypeCode.Decimal;
        AttributeTypeName = new ColumnTypeName { Value = "DecimalType" };
    }

    public decimal? MaxValue { get; set; }
    public decimal? MinValue { get; set; }
    public int? Precision { get; set; }
    public InputMethodMode? ImeMode { get; set; }
    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
}
