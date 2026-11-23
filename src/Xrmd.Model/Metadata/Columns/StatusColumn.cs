namespace Xrmd.Model.Metadata;

/// <summary>The Status column type (Web API <c>StatusAttributeMetadata</c>).</summary>
[WireType("StatusAttributeMetadata")]
public class StatusColumn : ChoiceColumnBase
{
    public StatusColumn()
    {
        AttributeType = ColumnTypeCode.Status;
        AttributeTypeName = new ColumnTypeName { Value = "StatusType" };
    }
}
