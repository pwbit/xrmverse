namespace Xrmd.Model.Metadata;

/// <summary>Base of the choice-backed column types (Web API <c>EnumAttributeMetadata</c>).</summary>
[WireType("EnumAttributeMetadata")]
public abstract class ChoiceColumnBase : ColumnDefinition
{
    public int? DefaultFormValue { get; set; }
    public ChoiceSet? OptionSet { get; set; }
}
