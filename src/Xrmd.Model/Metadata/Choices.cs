namespace Xrmd.Model.Metadata;

/// <summary>Base of the choice (option set) definitions (Web API <c>OptionSetMetadataBase</c>); abstract like the SDK type.</summary>
[WireType("OptionSetMetadataBase")]
public abstract class ChoiceSetBase
{
    public LabelSet? Description { get; set; }
    public LabelSet? DisplayName { get; set; }
    public bool? IsCustomOptionSet { get; set; }
    public bool? IsGlobal { get; set; }
    public bool? IsManaged { get; set; }
    public ManagedFlag? IsCustomizable { get; set; }
    public string? Name { get; set; }
    public string? ExternalTypeName { get; set; }
    public ChoiceSetKind? OptionSetType { get; set; }
    public string? IntroducedVersion { get; set; }
    public Guid? MetadataId { get; set; }
    public bool? HasChanged { get; set; }
}

/// <summary>A choice with a list of options (Web API <c>OptionSetMetadata</c>).</summary>
[WireType("OptionSetMetadata")]
public class ChoiceSet : ChoiceSetBase
{
    /// <summary>Never null, like the SDK: a missing key or an explicit <c>null</c> reads as an empty list.</summary>
    public List<ChoiceOption>? Options { get; set => field = value ?? []; } = [];
    public string? ParentOptionSetName { get; set; }
}

/// <summary>A two-valued choice (Web API <c>BooleanOptionSetMetadata</c>).</summary>
[WireType("BooleanOptionSetMetadata")]
public class BooleanChoiceSet : ChoiceSetBase
{
    public ChoiceOption? TrueOption { get; set; }
    public ChoiceOption? FalseOption { get; set; }
}

/// <summary>One option of a choice (Web API <c>OptionMetadata</c>); concrete like the SDK type.</summary>
[WireType("OptionMetadata")]
public class ChoiceOption
{
    public int? Value { get; set; }
    public LabelSet? Label { get; set; }
    public LabelSet? Description { get; set; }
    public string? Color { get; set; }
    public bool? IsManaged { get; set; }
    public string? ExternalValue { get; set; } = "";
    public List<int>? ParentValues { get; set; }
    public string? Tag { get; set; }
    public bool IsHidden { get; set; }
    public Guid? MetadataId { get; set; }
    public bool? HasChanged { get; set; }
}

/// <summary>An option of the state column (Web API <c>StateOptionMetadata</c>).</summary>
[WireType("StateOptionMetadata")]
public class StateOption : ChoiceOption
{
    public int? DefaultStatus { get; set; }
    public string? InvariantName { get; set; }
}

/// <summary>An option of the status column (Web API <c>StatusOptionMetadata</c>).</summary>
[WireType("StatusOptionMetadata")]
public class StatusOption : ChoiceOption
{
    public int? State { get; set; }
    public string? TransitionData { get; set; }
}
