using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xrmd.Abstractions.Sdk;
using Xrmd.Workflows.Custom;

namespace Xrmd.Host;

/// <summary>
/// Makes the Microsoft SDK loadable before anything of the host that uses it is compiled (Plan 12, B4). The SDK is not
/// shipped with the host: the customer installs it into the SDK folder with <c>xrmd sdk install</c>.
/// </summary>
/// <remarks>
/// This class names no SDK type, and <c>Program</c> calls it before <c>AddXrmd</c>: a method that names an SDK type
/// cannot even be compiled while the SDK is missing, and would fail with a <see cref="TypeLoadException"/> trace instead
/// of the message below. <see cref="Ensure"/> installs the default context's <c>Resolving</c> handler
/// (<see cref="SdkAssemblyResolver"/>) and loads <c>Microsoft.Xrm.Sdk</c> and <c>Microsoft.Crm.Sdk.Proxy</c> eagerly, before
/// <c>PluginAssemblyLoader</c>'s static constructor runs, so that no SDK assembly is ever loaded lazily from inside a
/// plug-in context (see the deadlock note on <c>PluginAssemblyLoader</c>). The optional workflow SDK
/// (<c>Microsoft.Xrm.Sdk.Workflow</c>, plan 2.14 W18) is loaded the same way when the SDK folder has it, after
/// <c>Microsoft.Xrm.Sdk</c>; without it the host starts all the same and <see cref="WorkflowSdk"/> says so.
/// </remarks>
public static class SdkBootstrap
{
    /// <summary>The exit code of a host that refuses to start because the SDK is missing or is not the pinned one.</summary>
    public const int ExitCode = 3;

    /// <summary>The configuration key of the SDK folder; <c>XRMD_SDK_DIR</c> and <c>~/.xrmd/sdk</c> follow.</summary>
    public const string PathKey = "Sdk:Path";

    /// <summary>
    /// Loads the SDK; null when it is loaded, else the reason the host must not start (the caller writes it and exits with
    /// <see cref="ExitCode"/>). An SDK that resolves from the application folder (tests, <c>dotnet run</c> with the test
    /// SDK) is accepted as is.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string? Ensure(IConfiguration? configuration)
    {
        var folder = SdkFolder.Resolve(configuration?[PathKey]);
        SdkAssemblyResolver.Install(folder);

        // WorkflowSdk is written once, with the final result: a host starting in parallel (tests) never sees a reset in between.
        var refusal = LoadSdk(folder);
        var (workflow, version, mismatch) = refusal is null ? LoadWorkflowSdk(folder) : default;
        WorkflowSdk.Set(workflow, version, mismatch);
        return refusal;
    }

    /// <summary>Loads the Dataverse SDK; null when it is loaded, else the reason the host must not start.</summary>
    private static string? LoadSdk(string folder)
    {
        if (Load("Microsoft.Xrm.Sdk") is not { } sdk) return SdkPackage.MissingMessage(folder);
        if (!IsUnder(sdk, folder)) return Load("Microsoft.Crm.Sdk.Proxy") is null ? SdkPackage.MissingMessage(folder) : null;

        var record = SdkFolder.TryRead(folder);
        if (record is not null && (record.Version != SdkPackage.Version || record.Sha512 != SdkPackage.Sha512Base64))
        {
            var found = record.Version == SdkPackage.Version ? $"{record.Version} with another package hash ({record.Sha512})" : record.Version;
            return Mismatch(folder, $"{record.Package} {found}");
        }

        var version = SdkAssemblyResolver.VersionOf(sdk);
        if (version != SdkPackage.XrmSdkFileVersion)
            return Mismatch(folder, $"Microsoft.Xrm.Sdk {version} (expected {SdkPackage.XrmSdkFileVersion})");

        // Microsoft.PowerPlatform.Dataverse.Client is deliberately not loaded up front: the host does not use it, and a
        // loaded copy would join the SDK's known-types scan (PluginAssemblyLoader's warm-up) with its own dependencies. A
        // plug-in that references it resolves it lazily through the default context's Resolving handler
        // (SdkAssemblyResolver), which takes no lock (see the deadlock note on PluginAssemblyLoader).
        return Load("Microsoft.Crm.Sdk.Proxy") is null ? SdkPackage.MissingMessage(folder) : null;
    }

    /// <summary>
    /// Loads the optional workflow SDK when it can be found (the SDK folder, or the application folder as for the SDK itself).
    /// Never a reason to refuse the start: one that is not the pinned package is loaded all the same, with a warning here and in
    /// the doctor (<c>sdk-workflow</c>).
    /// </summary>
    private static (Assembly? Assembly, string? Version, string? Mismatch) LoadWorkflowSdk(string folder)
    {
        var inFolder = File.Exists(Path.Combine(folder, SdkPackage.Workflow.AssemblyFileName));
        var assembly = Load(SdkPackage.Workflow.AssemblyName);
        if (assembly is null)
        {
            if (inFolder)
                Console.Error.WriteLine($"warning: {SdkPackage.Workflow.AssemblyFileName} in {folder} cannot be loaded: custom workflow activities will not run. Run 'xrmd sdk install' to reinstall it.");
            return default;
        }

        var version = SdkAssemblyResolver.VersionOf(assembly);
        // As for the SDK itself, a copy from the application folder (tests) is accepted as is.
        var mismatch = IsUnder(assembly, folder) ? WorkflowSdkMismatch(SdkFolder.TryRead(folder), version) : null;
        if (mismatch is not null)
            Console.Error.WriteLine($"warning: the workflow SDK in {folder} is {mismatch}, not {SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version}. Run 'xrmd sdk install' to reinstall it.");
        return (assembly, version, mismatch);
    }

    /// <summary>
    /// Why a loaded workflow SDK is not the pinned package: <c>sdk.json</c> records another package version or hash, or the file
    /// version differs from <see cref="SdkPackage.Workflow.FileVersion"/>. Null when it is the pinned one. A record without the
    /// workflow fields (a dll put there by hand) is judged by the file version alone.
    /// </summary>
    public static string? WorkflowSdkMismatch(SdkRecord? record, string? fileVersion)
    {
        if (record?.WorkflowVersion is { } recorded && recorded != SdkPackage.Workflow.Version)
            return $"{record.WorkflowPackage ?? SdkPackage.Workflow.Id} {recorded}";
        if (record?.WorkflowSha512 is { } sha && sha != SdkPackage.Workflow.Sha512Base64)
            return $"{record.WorkflowPackage ?? SdkPackage.Workflow.Id} {record.WorkflowVersion} with another package hash ({sha})";
        if (fileVersion != SdkPackage.Workflow.FileVersion)
            return $"{SdkPackage.Workflow.AssemblyName} {fileVersion} (expected {SdkPackage.Workflow.FileVersion})";
        return null;
    }

    private static string Mismatch(string folder, string found) =>
        $"The Microsoft SDK in {folder} is {found}, but this server needs {SdkPackage.Id} {SdkPackage.Version}. " +
        "Run 'xrmd sdk install' to install the pinned version after accepting Microsoft's license terms.";

    private static Assembly? Load(string name)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static bool IsUnder(Assembly assembly, string folder)
    {
        var location = assembly.Location;
        if (string.IsNullOrEmpty(location)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(location));
        return string.Equals(directory?.TrimEnd(Path.DirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
