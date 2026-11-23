namespace Xrmd.Model;

/// <summary>Names the Web API (wire) type a model class or enum maps to, when it differs from the CLR name.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum)]
public sealed class WireTypeAttribute(string name) : Attribute
{
    /// <summary>The Web API type name.</summary>
    public string Name { get; } = name;
}
