namespace Xrmd.Model.Metadata;

/// <summary>A boolean setting plus whether a solution layer may change it (Web API <c>BooleanManagedProperty</c>).</summary>
[WireType("BooleanManagedProperty")]
public class ManagedFlag
{
    public bool Value { get; set; }
    public bool CanBeChanged { get; set; } = true;
    public string? ManagedPropertyLogicalName { get; set; }
}

/// <summary>The requirement level of a column plus whether it may be changed (Web API <c>AttributeRequiredLevelManagedProperty</c>).</summary>
[WireType("AttributeRequiredLevelManagedProperty")]
public class RequiredLevelSetting
{
    public bool IsValueModified { get; set; } = true;
    public bool IsCanBeChangedPropertyModified { get; set; } = true;
    public RequirementLevel Value { get; set; }
    public bool CanBeChanged { get; set; } = true;
    public string? ManagedPropertyLogicalName { get; set; }
}
