namespace Xrmd.Model.Metadata;

/// <summary>The Money column type (Web API <c>MoneyAttributeMetadata</c>).</summary>
[WireType("MoneyAttributeMetadata")]
public class MoneyColumn : ColumnDefinition
{
    public MoneyColumn()
    {
        AttributeType = ColumnTypeCode.Money;
        AttributeTypeName = new ColumnTypeName { Value = "MoneyType" };
    }

    public InputMethodMode? ImeMode { get; set; }
    public double? MaxValue { get; set; }
    public double? MinValue { get; set; }
    public int? Precision { get; set; }
    public int? PrecisionSource { get; set; }
    public string? CalculationOf { get; set; }
    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
    public bool? IsBaseCurrency { get; set; }
}
