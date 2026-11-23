namespace Xrmd.Model.Metadata;

/// <summary>The String column type (Web API <c>StringAttributeMetadata</c>).</summary>
[WireType("StringAttributeMetadata")]
public class TextColumn : ColumnDefinition
{
    public TextColumn()
    {
        AttributeType = ColumnTypeCode.String;
        AttributeTypeName = new ColumnTypeName { Value = "StringType" };
    }

    public TextDisplayFormat? Format { get; set; }
    public TextFormatId? FormatName { get; set; }
    public InputMethodMode? ImeMode { get; set; }
    public int? MaxLength { get; set; }
    public string? YomiOf { get; set; }
    public bool? IsLocalizable { get; set; }
    public int? DatabaseLength { get; set; }
    public string? FormulaDefinition { get; set; }
    public int? SourceTypeMask { get; set; }
}
