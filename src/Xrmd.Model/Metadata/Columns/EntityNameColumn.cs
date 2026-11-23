namespace Xrmd.Model.Metadata;

/// <summary>The EntityName column type (Web API <c>EntityNameAttributeMetadata</c>).</summary>
[WireType("EntityNameAttributeMetadata")]
public class EntityNameColumn : ChoiceColumnBase
{
    public EntityNameColumn()
    {
        AttributeType = ColumnTypeCode.EntityName;
        AttributeTypeName = new ColumnTypeName { Value = "EntityNameType" };
    }

    public bool IsEntityReferenceStored { get; set; }
}
