using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// Resolves the Microsoft SDK assemblies (<see cref="SdkPackage.Assemblies"/>, and the optional workflow SDK assembly of
/// <see cref="SdkPackage.Workflow"/>) from the SDK folder for the default <see cref="AssemblyLoadContext"/> (Plan 12, B4/B5). Names no SDK type, so it can run before the SDK is loaded.
/// </summary>
/// <remarks>
/// The <see cref="AssemblyLoadContext.Resolving"/> handler is lock-free: it reads a volatile field, checks the file and
/// loads it. It only runs when the default context cannot bind a name by itself, which never happens for an SDK assembly
/// the host has already loaded (<c>SdkBootstrap</c> loads them eagerly, before any plug-in context exists).
/// </remarks>
public static class SdkAssemblyResolver
{
    private static volatile string? _folder;
    private static int _installed;

    /// <summary>The SDK folder the handler resolves from; null before <see cref="Install"/>.</summary>
    public static string? Folder => _folder;

    /// <summary>Points the handler at <paramref name="folder"/>, installing it on the default context on the first call.</summary>
    public static void Install(string folder)
    {
        _folder = Path.GetFullPath(folder);
        if (Interlocked.Exchange(ref _installed, 1) == 0)
            AssemblyLoadContext.Default.Resolving += Resolve;
    }

    /// <summary>True when <paramref name="simpleName"/> is one of the three SDK assemblies (no extension, any case).</summary>
    public static bool IsSdkAssembly(string? simpleName) =>
        simpleName is not null && SdkPackage.Assemblies.Any(a =>
            string.Equals(Path.GetFileNameWithoutExtension(a), simpleName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="simpleName"/> is the optional workflow SDK assembly (<see cref="SdkPackage.Workflow"/>, plan 2.14
    /// W18), which the handler resolves from the SDK folder too. Not one of <see cref="IsSdkAssembly"/>'s: its absence is no
    /// missing SDK.
    /// </summary>
    public static bool IsWorkflowSdkAssembly(string? simpleName) =>
        string.Equals(simpleName, SdkPackage.Workflow.AssemblyName, StringComparison.OrdinalIgnoreCase);

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        var folder = _folder;
        if (folder is null || !(IsSdkAssembly(name.Name) || IsWorkflowSdkAssembly(name.Name))) return null;
        var path = Path.Combine(folder, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    /// <summary>
    /// The simple name of the SDK assembly whose absence caused <paramref name="exception"/>: a
    /// <see cref="FileNotFoundException"/> or <see cref="FileLoadException"/> for one of the three assemblies, at any depth
    /// of inner exceptions (a <see cref="TypeInitializationException"/>, a <see cref="TargetInvocationException"/>, an
    /// <see cref="AggregateException"/>, a wrapping exception of ours). Null for any other failure.
    /// </summary>
    public static string? MissingAssembly(Exception? exception)
    {
        var pending = new Stack<Exception>();
        if (exception is not null) pending.Push(exception);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 64)
        {
            var ex = pending.Pop();
            var file = ex switch
            {
                FileNotFoundException f => f.FileName,
                FileLoadException f => f.FileName,
                _ => null,
            };
            if (file is not null && SimpleName(file) is { } simple && IsSdkAssembly(simple)) return simple;
            switch (ex)
            {
                case AggregateException aggregate:
                    foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
                    break;
                case ReflectionTypeLoadException typeLoad:
                    foreach (var inner in typeLoad.LoaderExceptions.OfType<Exception>()) pending.Push(inner);
                    break;
                default:
                    if (ex.InnerException is not null) pending.Push(ex.InnerException);
                    break;
            }
        }

        return null;
    }

    /// <summary>The simple name in an assembly display name ("Microsoft.Xrm.Sdk, Version=...") or a file name.</summary>
    private static string? SimpleName(string file)
    {
        if (file.Contains('/') || file.Contains('\\')) return Path.GetFileNameWithoutExtension(file);
        try
        {
            var name = new AssemblyName(file).Name;
            if (name is not null && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
            return name;
        }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException)
        {
            return Path.GetFileNameWithoutExtension(file);
        }
    }

    /// <summary>The file version of <paramref name="assembly"/> (informational version, else assembly version, when it has none).</summary>
    public static string VersionOf(Assembly assembly)
    {
#pragma warning disable IL3000 // Location is empty only for an assembly inside a single-file bundle; the SDK never is.
        var location = assembly.Location;
#pragma warning restore IL3000
        if (!string.IsNullOrEmpty(location) && FileVersionInfo.GetVersionInfo(location).FileVersion is { Length: > 0 } fileVersion)
            return fileVersion;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
