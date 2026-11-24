namespace Xrmd.Model.Messages;

/// <summary>Ordinal, case-sensitive parameter bag, like the SDK's parameter collection.</summary>
public sealed class ParameterBag : Dictionary<string, object?>
{
    public ParameterBag() : base(StringComparer.Ordinal) { }
}

/// <summary>Our counterpart of the SDK's public <c>OrganizationRequest</c>.</summary>
public class Request
{
    public string? Name { get; set; }

    public ParameterBag Parameters { get; set; } = new();

    public Guid? RequestId { get; set; }

    public object? this[string parameterName]
    {
        get => Parameters[parameterName];
        set => Parameters[parameterName] = value;
    }
}

/// <summary>Our counterpart of the SDK's public <c>OrganizationResponse</c>.</summary>
public class Response
{
    public string? Name { get; set; }

    public ParameterBag Results { get; set; } = new();

    public object? this[string resultName]
    {
        get => Results[resultName];
        set => Results[resultName] = value;
    }
}
