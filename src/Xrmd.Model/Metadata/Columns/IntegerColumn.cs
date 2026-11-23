namespace Xrmd.Model.Metadata;

/// <summary>The Integer column type (Web API <c>IntegerAttributeMetadata</c>).</summary>
[WireType("IntegerAttributeMetadata")]
public class IntegerColumn : ColumnDefinition
{
    public IntegerColumn()
    {
        AttributeType = ColumnTypeCode.Integer;
        AttributeTypeName = new ColumnTypeName { Value = "IntegerType" };
    }

    public IntegerDisplayFormat? Format { get; set; }
    public int? MaxValue { get; set; }
    public int? MinValue { get; set; }
    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
}
