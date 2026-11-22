using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Async;

namespace Xrmd.Pipeline.Sandbox;

/// <summary>
/// Loads plug-in assemblies into collectible <see cref="AssemblyLoadContext"/>s: one copy per assembly content in the whole
/// process for the executions, which the registration check uses but never releases (see <see cref="IsPluginType"/>).
/// </summary>
/// <remarks>
/// <para>The copies are process-wide (static), whatever the number of loader instances, and keyed by content: a new
/// registration version with the same content reuses the loaded copy. Issue #51: the registration check used to leave a
/// copy of its own, and an early-bound assembly (<c>[assembly: ProxyTypesAssembly]</c>) is kept alive by the SDK for the
/// life of the process, so the process listed two copies of it; a <c>ServiceClient</c> in the same process whose first
/// proxy-type scan came after that failed with "A proxy type with the name … has been defined by another assembly".
/// A new content with the same identity first releases the old copy and waits (bounded, outside the lock) for its
/// collection (issue #70: only for the copies that release freed, not for older ones still alive); an early-bound copy is
/// never collected, so nobody waits for it and after such an update both copies stay loaded. A released copy that is
/// still alive is revived when its content is asked for again, never loaded twice (a revived context, on which
/// <see cref="AssemblyLoadContext.Unload"/> was already called, still resolves the dependencies it had not loaded yet: they
/// come from the default context). The server itself (synchronous
/// and asynchronous steps) works with both; only an in-process client scanning proxy types afterwards would not, and xrmd
/// hosts none.</para>
/// <para>
/// The SDK's <c>KnownTypesProvider</c> hooks <see cref="AppDomain.AssemblyLoad"/> and, on first use, scans the attributes
/// of every loaded assembly under a lock of its own; the attributes of a plug-in assembly resolve through
/// <c>PluginLoadContext.Load</c>. Were <c>Load</c> compiled lazily, compiling it would load an SDK assembly whose
/// AssemblyLoad handler waits for that lock while the scanning thread waits for the compilation: a deadlock. So the
/// static constructor (explicit: the type is not <c>beforefieldinit</c>, it runs before any plug-in context can exist)
/// finishes the SDK's known-types initialization, keeps the SDK assemblies in static fields and compiles <c>Load</c>,
/// which references no SDK type and takes no lock of ours.</para>
/// </remarks>
public sealed class PluginAssemblyLoader
{
    /// <summary>The host's <c>Microsoft.Xrm.Sdk</c>, handed to every plug-in context.</summary>
    private static readonly Assembly SdkAssembly;

    /// <summary>The host's <c>Microsoft.Crm.Sdk.Proxy</c>, handed to every plug-in context.</summary>
    private static readonly Assembly ProxyAssembly;

    static PluginAssemblyLoader()
    {
        SdkAssembly = typeof(IPlugin).Assembly;
        ProxyAssembly = typeof(Microsoft.Crm.Sdk.Messages.WhoAmIRequest).Assembly;
        WarmUpKnownTypes();
        RuntimeHelpers.PrepareMethod(typeof(PluginLoadContext)
            .GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(AssemblyName)])!
            .MethodHandle);
    }

    /// <summary>
    /// Runs the static initialization (see the remarks) if it has not run yet; the host calls it at startup so the first
    /// request does not pay for it.
    /// </summary>
    public static void Initialize()
    {
    }

    /// <summary>
    /// Completes the SDK's lazy known-types initialization (the scan of the loaded assemblies and the known value and
    /// request types) with a serialization round trip of an execution context, as the async queue does.
    /// </summary>
    private static void WarmUpKnownTypes()
    {
        var context = new XrmdPluginExecutionContext { MessageName = "Create" };
        context.InputParameters["Target"] = new Entity("none") { ["value"] = new OptionSetValue(0) };
        AsyncContextSerializer.Deserialize(AsyncContextSerializer.Serialize(AsyncContextSerializer.ToRemote(context)));
    }

    /// <summary>Guards <see cref="Entries"/>, <see cref="Copies"/>, <see cref="Released"/> and <see cref="Releasing"/>.</summary>
    private static readonly object Gate = new();

    /// <summary>The copy each assembly id runs, at the registration version it was last asked for.</summary>
    private static readonly Dictionary<Guid, Entry> Entries = new();

    /// <summary>The live copies, by SHA-256 of their content.</summary>
    private static readonly Dictionary<string, Copy> Copies = new(StringComparer.Ordinal);

    /// <summary>
    /// Copies being unloaded, held weakly: a copy that is still alive when its content is asked for again is revived instead
    /// of loading the same content once more.
    /// </summary>
    private static readonly List<ReleasedCopy> Released = [];

    /// <summary>The identities whose released copies a thread is waiting for (outside <see cref="Gate"/>).</summary>
    private static readonly Dictionary<string, Task> Releasing = new(StringComparer.Ordinal);

    /// <summary>How many full collections a release waits for before giving up.</summary>
    private const int ReleaseCollections = 10;

    private readonly ILogger _logger;

    /// <param name="logger">Receives how long a release waited; none when null.</param>
    public PluginAssemblyLoader(ILogger<PluginAssemblyLoader>? logger = null) =>
        _logger = (ILogger?)logger ?? NullLogger.Instance;

    private sealed record Entry(long Version, Copy Copy);

    /// <summary>One loaded copy of an assembly content.</summary>
    /// <param name="Pinned">The assembly carries <c>[assembly: ProxyTypesAssembly]</c>: the SDK keeps it alive for the life of
    /// the process (observed), so its unload never completes and nobody waits for it.</param>
    private sealed record Copy(string Hash, string Identity, bool Pinned, PluginLoadContext Context, Assembly Assembly)
    {
        /// <summary>True once <see cref="AssemblyLoadContext.Unload"/> was called (it cannot be called twice).</summary>
        public bool Unloading { get; set; }
    }

    private sealed record ReleasedCopy(string Hash, string Identity, bool Pinned, WeakReference Context, WeakReference Assembly);

    /// <summary>What <see cref="Inspect"/> reads from a content, outside the lock.</summary>
    private sealed record Content(byte[] Bytes, string Hash, string Identity, bool Pinned);

    /// <summary>True when <paramref name="assemblyId"/> is loaded at exactly <paramref name="version"/> (no content needed).</summary>
    public bool IsLoaded(Guid assemblyId, long version)
    {
        lock (Gate) return Entries.TryGetValue(assemblyId, out var e) && e.Version == version;
    }

    /// <summary>
    /// Returns the assembly for <paramref name="assemblyId"/> at <paramref name="version"/> (the registration's
    /// modifiedon/versionnumber). A different version with the same content reuses the loaded copy; a different content
    /// releases the copy it replaces.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c>: the content is not an assembly.</exception>
    public Assembly Load(Guid assemblyId, long version, Func<byte[]> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return LoadCore(assemblyId, version, content)!;
    }

    /// <summary>
    /// <see cref="Load(Guid, long, Func{byte[]})"/> with the content already read: null (nothing changes) when
    /// <paramref name="content"/> is null and that version is not loaded. Nothing slow (no database read, no hashing, no
    /// wait for a collection) runs under the loader's lock.
    /// </summary>
    public Assembly? TryLoad(Guid assemblyId, long version, byte[]? content) =>
        LoadCore(assemblyId, version, content is null ? null : () => content);

    private Assembly? LoadCore(Guid assemblyId, long version, Func<byte[]>? content)
    {
        if (Current(assemblyId, version) is { } current) return current;
        if (content is null) return null;
        var read = Inspect(content()) ?? throw NotAnAssembly();
        return Acquire(read, assemblyId, version) ?? throw NotAnAssembly();
    }

    // No local of a method that may wait for a collection holds a copy: in a Debug build a local lives to the end of its
    // method and would keep the released context alive. Hence the small NoInlining helpers.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly? Current(Guid assemblyId, long version)
    {
        lock (Gate) return Entries.TryGetValue(assemblyId, out var e) && e.Version == version ? e.Copy.Assembly : null;
    }

    /// <summary>The hash, identity and pinning of <paramref name="bytes"/>, from its metadata; null when it is not an assembly.</summary>
    private static Content? Inspect(byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata) return null;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly) return null;
            var definition = reader.GetAssemblyDefinition();
            return new Content(bytes, Convert.ToHexString(SHA256.HashData(bytes)), definition.GetAssemblyName().FullName,
                HasProxyTypesAttribute(reader, definition));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True when the assembly declares <c>Microsoft.Xrm.Sdk.Client.ProxyTypesAssemblyAttribute</c> (read by name).</summary>
    private static bool HasProxyTypesAttribute(MetadataReader reader, AssemblyDefinition definition)
    {
        foreach (var handle in definition.GetCustomAttributes())
        {
            var constructor = reader.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind != HandleKind.MemberReference) continue;
            var parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)parent);
            if (reader.StringComparer.Equals(type.Name, "ProxyTypesAssemblyAttribute")
                && reader.StringComparer.Equals(type.Namespace, "Microsoft.Xrm.Sdk.Client"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The copy of <paramref name="content"/>: the live one, a released one still alive (revived), or a new one. Before a new
    /// copy is loaded, the live copies with the same identity are released; when one of them can be collected (not pinned),
    /// this thread waits for it outside the lock, a bounded number of collections, while other loads of that identity wait
    /// for this one and loads of other assemblies go on. When <paramref name="assemblyId"/> is set, the entry is updated
    /// under the same lock that hands out the copy. Null when the content cannot be loaded.
    /// </summary>
    private Assembly? Acquire(Content content, Guid? assemblyId, long version)
    {
        while (true)
        {
            Task? other;
            TaskCompletionSource mine;
            List<WeakReference> released = [];
            lock (Gate)
            {
                if (!Releasing.TryGetValue(content.Identity, out other))
                {
                    if (!Copies.ContainsKey(content.Hash) && !TryReviveLocked(content.Hash)
                        && (released = DetachLocked(content.Identity)).Count > 0)
                    {
                        mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Releasing[content.Identity] = mine.Task;
                    }
                    else
                    {
                        return AssignLocked(content, assemblyId, version);
                    }
                }
                else
                {
                    mine = null!;
                }
            }

            if (other is not null)
            {
                other.Wait();
                continue;
            }

            try
            {
                WaitForCollection(content.Identity, released);
            }
            finally
            {
                lock (Gate) Releasing.Remove(content.Identity);
                mine.SetResult();
            }
        }
    }

    /// <summary>
    /// Under the lock, once nothing of this identity is being waited for: the copy of <paramref name="content"/> (loaded when
    /// there is none, after releasing what is left of its identity without waiting), recorded for <paramref name="assemblyId"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly? AssignLocked(Content content, Guid? assemblyId, long version)
    {
        if (!Copies.TryGetValue(content.Hash, out var copy))
        {
            DetachLocked(content.Identity);
            var context = new PluginLoadContext($"plugin-{content.Identity.Split(',')[0]}-{content.Hash[..12]}");
            try
            {
                using var stream = new MemoryStream(content.Bytes, writable: false);
                copy = new Copy(content.Hash, content.Identity, content.Pinned, context, context.LoadFromStream(stream));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ArgumentException)
            {
                context.Unload();
                return null;
            }

            Copies[content.Hash] = copy;
        }

        if (assemblyId is { } id)
        {
            var previous = Entries.TryGetValue(id, out var e) ? e.Copy : null;
            Entries[id] = new Entry(version, copy);
            if (previous is not null && previous != copy && Copies.TryGetValue(previous.Hash, out var live) && live == previous
                && Entries.Values.All(x => x.Copy != previous))
                ReleaseLocked(previous);
        }

        return copy.Assembly;
    }

    /// <summary>Puts back in <see cref="Copies"/> a released copy of <paramref name="hash"/> that is still alive.</summary>
    private static bool TryReviveLocked(string hash)
    {
        var index = Released.FindIndex(r => r.Hash == hash && r.Context.IsAlive && r.Assembly.IsAlive);
        if (index < 0) return false;
        var r = Released[index];
        if (r.Context.Target is not PluginLoadContext context || r.Assembly.Target is not Assembly assembly) return false;
        Released.RemoveAt(index);
        Copies[hash] = new Copy(hash, r.Identity, r.Pinned, context, assembly) { Unloading = true };
        return true;
    }

    /// <summary>
    /// Releases every live copy of <paramref name="identity"/> (with the entries that run it); the contexts (held weakly) of
    /// those that are not pinned, whose collection is worth waiting for.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> DetachLocked(string identity)
    {
        var collectible = new List<WeakReference>();
        foreach (var copy in Copies.Values.Where(c => c.Identity == identity).ToList())
        {
            foreach (var id in Entries.Where(e => e.Value.Copy == copy).Select(e => e.Key).ToList()) Entries.Remove(id);
            var released = ReleaseLocked(copy);
            if (!copy.Pinned) collectible.Add(released.Context);
        }

        return collectible;
    }

    /// <summary>Unloads <paramref name="copy"/> (once) and moves it from the live copies to the released ones.</summary>
    private static ReleasedCopy ReleaseLocked(Copy copy)
    {
        Copies.Remove(copy.Hash);
        if (!copy.Unloading)
        {
            copy.Unloading = true;
            copy.Context.Unload();
        }

        Released.RemoveAll(r => !r.Context.IsAlive);
        var released = new ReleasedCopy(copy.Hash, copy.Identity, copy.Pinned, new WeakReference(copy.Context), new WeakReference(copy.Assembly));
        Released.Add(released);
        return released;
    }

    /// <summary>
    /// Outside the lock: up to <see cref="ReleaseCollections"/> full collections, until none of the contexts this call
    /// released (<paramref name="released"/>) is alive (one still alive is accepted: the new content is loaded beside it).
    /// A copy that an earlier release left alive is not waited for again (issue #70).
    /// </summary>
    private void WaitForCollection(string identity, List<WeakReference> released)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rounds = 0;
        bool alive;
        while (true)
        {
            alive = released.Any(r => r.IsAlive);
            if (!alive || rounds == ReleaseCollections) break;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            rounds++;
        }

        _logger.LogInformation(
            "Released the previous copy of plug-in assembly {Identity}: {Outcome} after {Rounds} collections ({ElapsedMs} ms).",
            identity, alive ? "still alive, the new content is loaded beside it" : "collected", rounds, watch.ElapsedMilliseconds);
    }

    private static XrmdFaultException NotAnAssembly() =>
        new(ErrorCodes.InvalidArgument, "The assembly is not a valid plug-in assembly.", 400);

    /// <summary>Finds <paramref name="typeName"/> in the assembly and checks that it is an <see cref="IPlugin"/>.</summary>
    /// <exception cref="XrmdFaultException">400 when the type is absent or not a plug-in; 500 when a dependency cannot be loaded.</exception>
    public Type ResolvePluginType(Guid assemblyId, long version, Func<byte[]> content, string typeName) =>
        PluginType(Load(assemblyId, version, content), typeName);

    /// <summary>
    /// <see cref="ResolvePluginType"/> with the content already read: null when <paramref name="content"/> is null and that
    /// version is not loaded (the caller reads the content, outside any lock, and calls again with it).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when the type is absent or not a plug-in; 500 when a dependency cannot be loaded.</exception>
    public Type? TryResolvePluginType(Guid assemblyId, long version, byte[]? content, string typeName) =>
        TryLoad(assemblyId, version, content) is { } assembly ? PluginType(assembly, typeName) : null;

    /// <summary>
    /// The custom workflow activity <paramref name="typeName"/> of the assembly (plan 2.14, W17), loaded in the same process-wide
    /// copy as the plug-ins: a class deriving, at any depth, from <see cref="CodeActivityTypeName"/> (compared by full name, so this
    /// names no type of the workflow runtime). Null when <paramref name="content"/> is null and that version is not loaded (the caller
    /// reads the content, outside any lock, and calls again with it).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when the type is absent or not a workflow activity; 500 when a dependency cannot be
    /// loaded.</exception>
    public Type? TryResolveWorkflowActivityType(Guid assemblyId, long version, byte[]? content, string typeName)
    {
        if (TryLoad(assemblyId, version, content) is not { } assembly) return null;
        Type? type;
        try
        {
            // throwOnError: a type whose base type cannot be loaded (no System.Activities) is then the missing dependency, not "not found".
            type = assembly.GetType(typeName, throwOnError: true);
            var derives = false;
            for (var t = type?.BaseType; t is not null && !derives; t = t.BaseType) derives = t.FullName == CodeActivityTypeName;
            if (type is not null && (!derives || type.IsAbstract))
                throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                    $"Type '{typeName}' is not a custom workflow activity: it does not derive from {CodeActivityTypeName}.", 400);
        }
        catch (TypeLoadException ex) when (ex.TypeName == typeName)
        {
            type = null;
        }
        catch (FileNotFoundException ex)
        {
            throw MissingDependency(ex.FileName ?? ex.Message);
        }
        catch (FileLoadException ex)
        {
            throw MissingDependency(ex.FileName ?? ex.Message);
        }

        return type ?? throw new XrmdFaultException(ErrorCodes.InvalidArgument,
            $"Type '{typeName}' was not found in the plug-in assembly.", 400);
    }

    private static Type PluginType(Assembly assembly, string typeName)
    {
        Type? type;
        try
        {
            type = assembly.GetType(typeName, throwOnError: false);
            if (type is not null && !typeof(IPlugin).IsAssignableFrom(type))
                throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                    $"Type '{typeName}' does not implement IPlugin.", 400);
        }
        catch (FileNotFoundException ex)
        {
            throw MissingDependency(ex.FileName ?? ex.Message);
        }
        catch (FileLoadException ex)
        {
            throw MissingDependency(ex.FileName ?? ex.Message);
        }

        return type ?? throw new XrmdFaultException(ErrorCodes.InvalidArgument,
            $"Type '{typeName}' was not found in the plug-in assembly.", 400);
    }

    /// <summary>
    /// True when <paramref name="content"/> loads as an assembly that has a type <paramref name="typeName"/> implementing
    /// <see cref="IPlugin"/>. No code of the assembly runs (no instance is created, no static constructor is triggered).
    /// False also when it cannot be loaded or its types need a dependency that cannot be resolved.
    /// </summary>
    /// <remarks>
    /// The registration check runs inside the registration's transaction, which may still roll back, so it never releases or
    /// replaces the copy that executions run (issue #70): the live copy of that content when there is one; else, for an
    /// ordinary assembly, a throwaway copy unloaded right after the check; else (early-bound, which the SDK keeps alive for
    /// the life of the process anyway) a copy added beside the live ones of its identity, which the next execution of that
    /// content takes over instead of loading a third copy.
    /// </remarks>
    public bool IsPluginType(byte[] content, string typeName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(typeName);
        if (Inspect(content) is not { } read) return false;
        if (Shared(read) is { } shared) return HasPluginType(shared, typeName);

        var context = new PluginLoadContext($"plugin-check-{read.Identity.Split(',')[0]}-{read.Hash[..12]}");
        try
        {
            Assembly assembly;
            try
            {
                using var stream = new MemoryStream(read.Bytes, writable: false);
                assembly = context.LoadFromStream(stream);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ArgumentException)
            {
                return false;
            }

            return HasPluginType(assembly, typeName);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// The copy of <paramref name="content"/> the check may use: the live one; for an early-bound content, also a released one
    /// still alive (revived) or a new one added to the live copies without releasing any. Null for an ordinary content that
    /// has no live copy, and for an early-bound one that cannot be loaded.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly? Shared(Content content)
    {
        lock (Gate)
        {
            if (Copies.TryGetValue(content.Hash, out var live)) return live.Assembly;
            if (!content.Pinned) return null;
            if (TryReviveLocked(content.Hash)) return Copies[content.Hash].Assembly;
            var context = new PluginLoadContext($"plugin-{content.Identity.Split(',')[0]}-{content.Hash[..12]}");
            try
            {
                using var stream = new MemoryStream(content.Bytes, writable: false);
                var copy = new Copy(content.Hash, content.Identity, content.Pinned, context, context.LoadFromStream(stream));
                Copies[content.Hash] = copy;
                return copy.Assembly;
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ArgumentException)
            {
                context.Unload();
                return null;
            }
        }
    }

    private static bool HasPluginType(Assembly assembly, string typeName)
    {
        try
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            return type is not null && typeof(IPlugin).IsAssignableFrom(type);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException
                                       or ArgumentException or TypeLoadException)
        {
            return false;
        }
    }

    /// <summary>The base type of a custom workflow activity (plan 2.14, W17), matched by full name.</summary>
    public const string CodeActivityTypeName = "System.Activities.CodeActivity";

    /// <summary>
    /// True when <paramref name="content"/> has a concrete class <paramref name="typeName"/> one of whose base types is
    /// <see cref="CodeActivityTypeName"/> (plan 2.14, W17): a custom workflow activity. Read through a
    /// <see cref="MetadataLoadContext"/> (references bound by simple name from the runtime, the application folder and the SDK
    /// folder), so the assembly is never loaded and none of its code runs; when a base type's assembly cannot be found there,
    /// the base types defined in the assembly itself are followed in its metadata. False when the content is not an assembly.
    /// </summary>
    public static bool IsWorkflowActivityType(byte[] content, string typeName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(typeName);
        string core;
        try
        {
            using var pe = new PEReader(new MemoryStream(content, writable: false));
            if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly) return false;
            core = CoreAssemblyName(pe.GetMetadataReader());
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        try
        {
            using var context = new MetadataLoadContext(new SimpleNameResolver(ReferencePaths()), core);
            var type = context.LoadFromByteArray(content).GetType(typeName, throwOnError: false);
            if (type is null || !type.IsClass || type.IsAbstract) return false;
            for (var t = type.BaseType; t is not null; t = t.BaseType)
                if (t.FullName == CodeActivityTypeName) return true;
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return DerivesFromCodeActivityByMetadata(content, typeName);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The core assembly the content references (<c>mscorlib</c> for .NET Framework builds).</summary>
    private static string CoreAssemblyName(MetadataReader reader)
    {
        foreach (var handle in reader.AssemblyReferences)
        {
            var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
            if (name is "mscorlib" or "System.Runtime" or "netstandard" or "System.Private.CoreLib") return name;
        }

        return "System.Private.CoreLib";
    }

    /// <summary>
    /// The assemblies a <see cref="MetadataLoadContext"/> may bind to, by simple name, first found wins: the runtime folder, the
    /// application folder (CoreWF's <c>System.Activities</c> in the host), then the SDK folder (<c>Microsoft.Xrm.Sdk</c> and
    /// <c>Microsoft.Xrm.Sdk.Workflow</c>). Scanned once per SDK folder: the runtime and application folders never change, and the
    /// SDK folder is set at startup (a different folder, as between test hosts, scans again).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReferencePaths()
    {
        var sdk = Xrmd.Abstractions.Sdk.SdkAssemblyResolver.Folder;
        if (_referencePaths is { } cached && string.Equals(cached.SdkFolder, sdk, StringComparison.Ordinal)) return cached.Paths;

        var paths = ScanReferencePaths(sdk);
        _referencePaths = new ReferencePathCache(sdk, paths);
        return paths;
    }

    private static volatile ReferencePathCache? _referencePaths;

    private sealed record ReferencePathCache(string? SdkFolder, IReadOnlyDictionary<string, string> Paths);

    private static Dictionary<string, string> ScanReferencePaths(string? sdkFolder)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
#pragma warning disable IL3000 // Location is empty only in a single-file app; the host is never one.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
#pragma warning restore IL3000
        foreach (var folder in new[] { runtime, AppContext.BaseDirectory, sdkFolder })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.dll"))
                paths.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }

        return paths;
    }

    /// <summary>
    /// Binds a reference to the file of the same simple name, whatever its version and public key token: a .NET Framework
    /// activity references <c>System.Activities</c> 4.0, which CoreWF provides under another version and key.
    /// </summary>
    private sealed class SimpleNameResolver(IReadOnlyDictionary<string, string> paths) : MetadataAssemblyResolver
    {
        public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName) =>
            assemblyName.Name is { } name && paths.TryGetValue(name, out var path) ? context.LoadFromAssemblyPath(path) : null;
    }

    /// <summary>
    /// The fallback of <see cref="IsWorkflowActivityType"/>: follows the base types defined in the assembly itself until one is a
    /// reference to <see cref="CodeActivityTypeName"/>. A base class in another assembly cannot be followed without it.
    /// </summary>
    private static bool DerivesFromCodeActivityByMetadata(byte[] content, string typeName)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(content, writable: false));
            var reader = pe.GetMetadataReader();
            TypeDefinitionHandle? start = null;
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                if (definition.GetDeclaringType().IsNil && FullName(reader, definition.Namespace, definition.Name) == typeName)
                    start = handle;
            }

            if (start is not { } current) return false;
            var first = reader.GetTypeDefinition(current);
            if ((first.Attributes & (System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Interface)) != 0) return false;
            var seen = new HashSet<TypeDefinitionHandle>();
            while (seen.Add(current))
            {
                var baseType = reader.GetTypeDefinition(current).BaseType;
                switch (baseType.Kind)
                {
                    case HandleKind.TypeReference:
                        var reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
                        return FullName(reader, reference.Namespace, reference.Name) == CodeActivityTypeName;
                    case HandleKind.TypeDefinition:
                        current = (TypeDefinitionHandle)baseType;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static string FullName(MetadataReader reader, StringHandle ns, StringHandle name) =>
        ns.IsNil || reader.GetString(ns).Length == 0 ? reader.GetString(name) : reader.GetString(ns) + "." + reader.GetString(name);

    /// <summary>
    /// The names of the assemblies <paramref name="content"/> references that a throwaway collectible context (the loader's
    /// own rules: the host's SDK copies, then the default context) cannot resolve; empty when all resolve. The references are
    /// read from the metadata: the assembly itself is not loaded (no copy of it is left in the process) and none of its code
    /// runs.
    /// </summary>
    /// <exception cref="BadImageFormatException">The content is not a managed assembly.</exception>
    public static IReadOnlyList<string> MissingReferences(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var references = new List<AssemblyName>();
        using (var pe = new PEReader(new MemoryStream(content, writable: false)))
        {
            if (!pe.HasMetadata) throw new BadImageFormatException("The content has no .NET metadata.");
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences) references.Add(reader.GetAssemblyReference(handle).GetAssemblyName());
        }

        var context = new PluginLoadContext("plugin-doctor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var missing = new List<string>();
            foreach (var reference in references)
            {
                try
                {
                    context.LoadFromAssemblyName(reference);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    missing.Add(reference.Name ?? reference.FullName);
                }
            }

            return missing;
        }
        finally
        {
            context.Unload();
        }
    }

    private static XrmdFaultException MissingDependency(string name) =>
        new(ErrorCodes.UnexpectedPluginException,
            $"Could not load the plug-in assembly dependency '{name}'.", 500);

    /// <summary>
    /// Collectible context: the Dataverse SDK assemblies bind to the host's copies (type identity), <c>System.Activities</c> and
    /// <c>Microsoft.Xrm.Sdk.Workflow</c> to the default context's by simple name (plan 2.14, W17), everything else is resolved
    /// from the default context (framework) or fails.
    /// </summary>
    /// <remarks><see cref="Load"/> is compiled by the static constructor of <see cref="PluginAssemblyLoader"/> and must
    /// reference no SDK type (only the cached <see cref="Assembly"/> fields) nor take any lock: see its remarks.</remarks>
    private sealed class PluginLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            switch (assemblyName.Name)
            {
                case "Microsoft.Xrm.Sdk": return SdkAssembly;
                case "Microsoft.Crm.Sdk.Proxy": return ProxyAssembly;
                // Plan 2.14, W17: a custom workflow activity binds to the host's CoreWF and workflow SDK, whatever version it was
                // built against (System.Activities 4.0 of .NET Framework). Both are in the default context: CoreWF in the
                // application folder, the workflow SDK loaded by SdkBootstrap (else resolved from the SDK folder, lock-free).
                case "System.Activities":
                case "Microsoft.Xrm.Sdk.Workflow":
                    try { return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(assemblyName.Name)); }
                    catch (FileNotFoundException) { return null; }
            }

            try { return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName); }
            catch (FileNotFoundException) { return null; }
        }
    }
}
