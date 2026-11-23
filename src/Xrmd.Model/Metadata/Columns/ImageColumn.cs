namespace Xrmd.Model.Metadata;

/// <summary>The Image column type (Web API <c>ImageAttributeMetadata</c>).</summary>
[WireType("ImageAttributeMetadata")]
public class ImageColumn : ColumnDefinition
{
    public ImageColumn()
    {
        AttributeType = ColumnTypeCode.Virtual;
        AttributeTypeName = new ColumnTypeName { Value = "ImageType" };
    }

    public bool? IsPrimaryImage { get; set; }
    public short? MaxHeight { get; set; }
    public short? MaxWidth { get; set; }
    public int? MaxSizeInKB { get; set; }
    public bool? CanStoreFullImage { get; set; }
}
