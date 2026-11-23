namespace Xrmd.Model.Metadata;

/// <summary>The UniqueIdentifier column type (Web API <c>UniqueIdentifierAttributeMetadata</c>).</summary>
[WireType("UniqueIdentifierAttributeMetadata")]
public class UniqueIdentifierColumn : ColumnDefinition
{
    public UniqueIdentifierColumn()
    {
        AttributeType = ColumnTypeCode.Uniqueidentifier;
        AttributeTypeName = new ColumnTypeName { Value = "UniqueidentifierType" };
    }
}
