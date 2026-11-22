namespace Xrmd.Abstractions;

/// <summary>
/// The flag of an asynchronous flow that is a solution layer operation (plan 2.6, ruling R2): a managed import writing its
/// components and its <c>solution</c> row as the importing user, which the managed properties and the managed-component rules
/// do not apply to. A flag of the flow only: nothing a request carries can set it. No plug-in step may run inside it (the
/// layer operations call the core operations directly, outside the pipeline), so a plug-in's own requests can never inherit
/// the bypass; the pipeline asserts it.
/// </summary>
public static class LayerOperationScope
{
    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>True inside <see cref="Enter"/> on the current asynchronous flow.</summary>
    public static bool IsActive => Active.Value;

    /// <summary>Marks the current asynchronous flow as a layer operation until the result is disposed.</summary>
    public static IDisposable Enter()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
