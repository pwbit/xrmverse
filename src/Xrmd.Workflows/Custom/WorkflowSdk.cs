using System.Reflection;

namespace Xrmd.Workflows.Custom;

/// <summary>
/// Whether the customer installed the Microsoft workflow SDK (<c>Microsoft.Xrm.Sdk.Workflow.dll</c> in the SDK folder, plan 2.14
/// rulings W17 and W18). Set once by the host at startup (<c>SdkBootstrap</c>), after <c>Microsoft.Xrm.Sdk</c> is loaded; read by
/// the XAML parser, the health report and the doctor. Names no SDK type: the assembly is only ever used by full type names.
/// </summary>
public static class WorkflowSdk
{
    private static volatile State _state = new(null, null, null);

    private sealed record State(Assembly? Assembly, string? Version, string? Mismatch);

    /// <summary>True when <see cref="Assembly"/> is loaded: custom workflow activity steps can run.</summary>
    public static bool IsAvailable => _state.Assembly is not null;

    /// <summary>The loaded <c>Microsoft.Xrm.Sdk.Workflow</c> (default context); null when the SDK folder has none.</summary>
    public static Assembly? Assembly => _state.Assembly;

    /// <summary>The file version of <see cref="Assembly"/>; null when it is not loaded.</summary>
    public static string? Version => _state.Version;

    /// <summary>
    /// Why the loaded assembly is not the pinned package (<c>SdkPackage.Workflow</c>): another file version, or <c>sdk.json</c>
    /// recording another package version or hash. Null when it is the pinned one, or when none is loaded.
    /// </summary>
    public static string? Mismatch => _state.Mismatch;

    /// <summary>
    /// Records what the host found at startup, in one write: the loaded assembly, its file version and why it is not the pinned
    /// one, or null for none.
    /// </summary>
    public static void Set(Assembly? assembly, string? version, string? mismatch = null) =>
        _state = assembly is null ? new State(null, null, null) : new State(assembly, version, mismatch);
}
