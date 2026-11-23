namespace Xrmd.Model.Metadata;

/// <summary>The File column type (Web API <c>FileAttributeMetadata</c>).</summary>
[WireType("FileAttributeMetadata")]
public class FileColumn : ColumnDefinition
{
    public FileColumn()
    {
        AttributeType = ColumnTypeCode.Virtual;
        AttributeTypeName = new ColumnTypeName { Value = "FileType" };
        IsValidForCreate = false;
        IsValidForUpdate = false;
        RequiredLevel = new RequiredLevelSetting { IsValueModified = true, IsCanBeChangedPropertyModified = true, Value = RequirementLevel.None, CanBeChanged = true };
    }

    public int? MaxSizeInKB { get; set; }
}
