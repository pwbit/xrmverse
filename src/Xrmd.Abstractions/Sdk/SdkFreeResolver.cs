using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Xrmd.Abstractions.Sdk;

/// <summary>An SDK assembly an SDK-free server refused to load, and the xrmd method that asked for it (null when none is on the stack).</summary>
public sealed record SdkFreeRefusal(string Assembly, string? RequestedBy);

/// <summary>
/// The default context's <see cref="AssemblyLoadContext.Resolving"/> handler of an SDK-free server (<c>Sdk:Mode</c> <c>none</c>,
/// plan S.2a): it never loads an SDK assembly (<see cref="SdkModes.AssemblyNames"/>), records who asked for one and says so on
/// stderr, once for each assembly and requester. Names no SDK type.
/// </summary>
public static class SdkFreeResolver
{
    private const string OwnNamespace = "Xrmd.Abstractions.Sdk";

    private static readonly object Gate = new();
    private static readonly List<SdkFreeRefusal> RefusalList = [];
    private static int _installed;

    /// <summary>The refusals so far, in order, each (assembly, requester) pair once.</summary>
    public static IReadOnlyList<SdkFreeRefusal> Refusals
    {
        get { lock (Gate) return RefusalList.ToArray(); }
    }

    /// <summary>Installs the handler on the default context; later calls do nothing.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 0)
            AssemblyLoadContext.Default.Resolving += OnResolving;
    }

    /// <summary>Forgets the refusals (tests); the handler stays installed.</summary>
    public static void Reset()
    {
        lock (Gate) RefusalList.Clear();
    }

    /// <summary>
    /// The message of a host that stops because <paramref name="requestedBy"/> still needs the SDK.
    /// </summary>
    public static string NotYetMessage(string? requestedBy) =>
        $"This build cannot run as an SDK-free server yet: {requestedBy ?? "a component of the host"} still needs the Microsoft SDK " +
        $"(SDK-free servers are experimental, see {SdkFreeMessages.DocsLink}).";

    /// <summary>
    /// The SDK assembly (one of <see cref="SdkModes.AssemblyNames"/>) whose absence caused <paramref name="exception"/>: a
    /// <see cref="FileNotFoundException"/>, <see cref="FileLoadException"/> or <see cref="TypeLoadException"/> naming it, at any
    /// depth of inner exceptions. Null for any other failure.
    /// </summary>
    public static string? RefusedAssembly(Exception? exception)
    {
        var pending = new Stack<Exception>();
        if (exception is not null) pending.Push(exception);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 64)
        {
            var ex = pending.Pop();
            var name = ex switch
            {
                FileNotFoundException f => SdkName(SimpleName(f.FileName)),
                FileLoadException f => SdkName(SimpleName(f.FileName)),
                TypeLoadException t => SdkModes.AssemblyNames.FirstOrDefault(a =>
                    t.Message.Contains($"assembly '{a},", StringComparison.OrdinalIgnoreCase)
                    || t.Message.Contains($"assembly '{a}'", StringComparison.OrdinalIgnoreCase)),
                _ => null,
            };
            if (name is not null) return name;
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

    /// <summary>The handler: null for every name; an SDK name is recorded and written to stderr (once for each pair).</summary>
    internal static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        if (SdkName(name.Name) is not { } assembly) return null;
        return Refuse(assembly, RequestedBy());
    }

    /// <summary>Records and reports one refusal (once for each pair); the line says <c>(unknown)</c> when nobody is named.</summary>
    internal static Assembly? Refuse(string assembly, string? requestedBy)
    {
        var refusal = new SdkFreeRefusal(assembly, requestedBy);
        lock (Gate)
        {
            if (RefusalList.Contains(refusal)) return null;
            RefusalList.Add(refusal);
        }

        Console.Error.WriteLine($"sdk-free: refused {refusal.Assembly} requested by {refusal.RequestedBy ?? "(unknown)"}");
        return null;
    }

    /// <summary>The first xrmd method on the stack outside this namespace, as <c>Namespace.Type.Method</c>.</summary>
    private static string? RequestedBy()
    {
        foreach (var frame in new StackTrace(1, false).GetFrames())
        {
            if (frame.GetMethod() is not { DeclaringType: { } type } method) continue;
            var ns = type.Namespace;
            if (ns is null || !ns.StartsWith("Xrmd.", StringComparison.Ordinal) || ns == OwnNamespace) continue;
            return $"{type.FullName}.{method.Name}";
        }

        return null;
    }

    private static string? SdkName(string? simpleName) =>
        simpleName is null ? null : SdkModes.AssemblyNames.FirstOrDefault(a => string.Equals(a, simpleName, StringComparison.OrdinalIgnoreCase));

    private static string? SimpleName(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
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
}
