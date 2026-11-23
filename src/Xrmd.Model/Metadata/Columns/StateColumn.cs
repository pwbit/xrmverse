namespace Xrmd.Model.Metadata;

/// <summary>The State column type (Web API <c>StateAttributeMetadata</c>).</summary>
[WireType("StateAttributeMetadata")]
public class StateColumn : ChoiceColumnBase
{
    public StateColumn()
    {
        AttributeType = ColumnTypeCode.State;
        AttributeTypeName = new ColumnTypeName { Value = "StateType" };
    }
}
