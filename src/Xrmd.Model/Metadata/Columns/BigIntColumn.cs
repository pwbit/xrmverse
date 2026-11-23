namespace Xrmd.Model.Metadata;

/// <summary>The BigInt column type (Web API <c>BigIntAttributeMetadata</c>).</summary>
[WireType("BigIntAttributeMetadata")]
public class BigIntColumn : ColumnDefinition
{
    public BigIntColumn()
    {
        AttributeType = ColumnTypeCode.BigInt;
        AttributeTypeName = new ColumnTypeName { Value = "BigIntType" };
    }

    public long? MaxValue { get; set; }
    public long? MinValue { get; set; }
}
