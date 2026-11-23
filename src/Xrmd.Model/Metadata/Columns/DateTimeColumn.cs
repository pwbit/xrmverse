namespace Xrmd.Model.Metadata;

/// <summary>The DateTime column type (Web API <c>DateTimeAttributeMetadata</c>).</summary>
[WireType("DateTimeAttributeMetadata")]
public class DateTimeColumn : ColumnDefinition
{
    public DateTimeColumn()
    {
        AttributeType = ColumnTypeCode.DateTime;
        AttributeTypeName = new ColumnTypeName { Value = "DateTimeType" };
    }

    public DateTimeDisplayFormat? Format { get; set; }
    public InputMethodMode? ImeMode { get; set; }
    public int? SourceTypeMask { get; set; }
    public string? FormulaDefinition { get; set; }
    public DateTimeBehaviorName? DateTimeBehavior { get; set; }
    public ManagedFlag? CanChangeDateTimeBehavior { get; set; }
}
