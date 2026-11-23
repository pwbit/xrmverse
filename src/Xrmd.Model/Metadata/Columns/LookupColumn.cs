namespace Xrmd.Model.Metadata;

/// <summary>The Lookup column type (Web API <c>LookupAttributeMetadata</c>).</summary>
[WireType("LookupAttributeMetadata")]
public class LookupColumn : ColumnDefinition
{
    public LookupColumn()
    {
        AttributeType = ColumnTypeCode.Lookup;
        AttributeTypeName = new ColumnTypeName { Value = "LookupType" };
    }

    public List<string>? Targets { get; set; }
    public LookupDisplayFormat? Format { get; set; }
}
