namespace Xrmd.Model.Metadata;

/// <summary>The Memo column type (Web API <c>MemoAttributeMetadata</c>).</summary>
[WireType("MemoAttributeMetadata")]
public class MemoColumn : ColumnDefinition
{
    public MemoColumn()
    {
        AttributeType = ColumnTypeCode.Memo;
        AttributeTypeName = new ColumnTypeName { Value = "MemoType" };
    }

    public TextDisplayFormat? Format { get; set; }
    public MemoFormatId? FormatName { get; set; }
    public InputMethodMode? ImeMode { get; set; }
    public int? MaxLength { get; set; }
    public bool? IsLocalizable { get; set; }
}
