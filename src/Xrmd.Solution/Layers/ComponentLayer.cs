namespace Xrmd.Solution.Layers;

/// <summary>What a layer of a component is (plan 2.6, ruling M1).</summary>
public enum LayerKind
{
    /// <summary>The base state of a seeded component, owned by <see cref="ComponentLayerStore.SystemLayerSolutionId"/>.</summary>
    System,

    /// <summary>The component as a managed solution (base or holding) delivered it.</summary>
    Managed,

    /// <summary>The unmanaged marker, owned by the <c>Active</c> solution: its content is the effective state itself.</summary>
    Active,
}

/// <summary>
/// One row of <c>meta.component_layer</c>: the layer that <paramref name="SolutionId"/> holds on a component.
/// </summary>
/// <param name="Type">The component type (type 3 is recorded as 10, <see cref="ComponentType.Normalize"/>).</param>
/// <param name="ObjectId">The component's <c>MetadataId</c> or row id.</param>
/// <param name="SolutionId">The owner: a managed solution, the System layer id or the <c>Active</c> solution.</param>
/// <param name="Order">Its place in the stack, higher on top: 0 for System, <c>segment * 1024 + sub</c> for a managed
/// solution (ruling M2), <see cref="long.MaxValue"/> for Active.</param>
/// <param name="Kind">Follows from <paramref name="SolutionId"/>.</param>
/// <param name="Payload">The component as its solution delivered it (ruling M5); null for the Active marker.</param>
public sealed record ComponentLayer(int Type, Guid ObjectId, Guid SolutionId, long Order, LayerKind Kind, string? Payload);
