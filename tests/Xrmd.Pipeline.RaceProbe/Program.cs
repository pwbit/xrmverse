using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Sandbox;

// Child process of LoadContextRaceTests (the SDK initializes its known types once per process, so every attempt needs a
// fresh one). Usage: Xrmd.Pipeline.RaceProbe <directory holding Xrmd.TestPlugins.dll and Xrmd.TestPlugins.EarlyBound.dll>.
// Exit code 0: every thread finished; 2: a thread was still running after 20 s (deadlock); 1: a thread failed.
//
// The race (seen as a hang of the test process): the SDK's KnownTypesProvider hooks AppDomain.AssemblyLoad and, on first
// use, scans the attributes of every loaded assembly while holding its lock; the attributes of a plug-in assembly are
// resolved through PluginLoadContext.Load. If Load is not compiled yet, a thread compiling it loads an SDK assembly whose
// AssemblyLoad handler waits for that lock, while the scanning thread waits for the compilation of Load: neither ends.
// The probe forces that interleaving: the scan is held on a "gate" assembly (loaded before the plug-ins, so scanned
// before them) until the other thread has started compiling Load (it is loading Microsoft.Crm.Sdk.Proxy).
var directory = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
var plugins = File.ReadAllBytes(Path.Combine(directory, "Xrmd.TestPlugins.dll"));
var earlyBound = File.ReadAllBytes(Path.Combine(directory, "Xrmd.TestPlugins.EarlyBound.dll"));
const string PluginType = "Xrmd.TestPlugins.SetAttributePlugin";
const string EarlyBoundType = "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin";

using var proxyLoading = new ManualResetEventSlim();
AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
{
    if (e.LoadedAssembly.GetName().Name == "Microsoft.Crm.Sdk.Proxy") proxyLoading.Set();
};

// 1. The gate assembly, loaded before anything else of the test (its attributes are resolved through GateContext.Load).
var gate = new GateContext(proxyLoading);
gate.LoadFromStream(new MemoryStream(plugins));

// 2. The plug-in assemblies (no type resolved yet).
var loader = new PluginAssemblyLoader();
Guid pluginId = Guid.NewGuid(), earlyBoundId = Guid.NewGuid();
loader.Load(pluginId, 1, () => plugins);
loader.Load(earlyBoundId, 1, () => earlyBound);

// 3. As in the host, the SDK is in use before plug-ins run: KnownTypesProvider's static constructor hooks AssemblyLoad
//    (without starting its lazy scan).
typeof(Entity).Assembly.GetType("Microsoft.Xrm.Sdk.KnownTypesProvider", throwOnError: true)!
    .GetMethod("RetrieveKnownConditionValueTypes")!.Invoke(null, null);

gate.Armed = true;
var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
using var knownTypesDone = new ManualResetEventSlim();

Thread Run(string name, Action work, ManualResetEventSlim? done = null)
{
    var thread = new Thread(() =>
    {
        try { work(); }
        catch (Exception ex) { failures.Enqueue(ex); }
        finally { done?.Set(); }
    }) { IsBackground = true, Name = name };
    thread.Start();
    return thread;
}

var threads = new[]
{
    // The SDK's known-types initialization, not through the plug-in loader.
    Run("known-types", () =>
    {
        var context = new XrmdPluginExecutionContext { MessageName = "Create" };
        context.InputParameters["Target"] = new Entity("account") { ["industrycode"] = new OptionSetValue(1) };
        AsyncContextSerializer.Deserialize(AsyncContextSerializer.Serialize(AsyncContextSerializer.ToRemote(context)));
    }, knownTypesDone),
    // Plug-in type resolution (PluginLoadContext.Load), once the scan holds the SDK's lock (or is over).
    Run("load", () =>
    {
        WaitHandle.WaitAny([gate.Entered.WaitHandle, knownTypesDone.WaitHandle], TimeSpan.FromSeconds(5));
        loader.ResolvePluginType(pluginId, 1, () => plugins, PluginType);
        loader.ResolvePluginType(earlyBoundId, 1, () => earlyBound, EarlyBoundType);
    }),
    Run("inspect-early-bound", () =>
    {
        WaitHandle.WaitAny([gate.Entered.WaitHandle, knownTypesDone.WaitHandle], TimeSpan.FromSeconds(5));
        if (!loader.IsPluginType(earlyBound, EarlyBoundType)) throw new InvalidOperationException("not a plug-in");
    }),
};

var deadline = DateTime.UtcNow.AddSeconds(20);
foreach (var thread in threads)
{
    var left = deadline - DateTime.UtcNow;
    if (left < TimeSpan.Zero || !thread.Join(left))
    {
        Console.Error.WriteLine($"HANG: thread '{thread.Name}' still running after 20 s");
        return 2;
    }
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.IsEmpty ? 0 : 1;

/// <summary>
/// Once <see cref="Armed"/>, the first resolution of a dependency (an attribute type of its assembly) signals
/// <see cref="Entered"/> and waits (at most 5 s) for Microsoft.Crm.Sdk.Proxy to start loading.
/// </summary>
internal sealed class GateContext(ManualResetEventSlim proxyLoading) : AssemblyLoadContext("race-gate", isCollectible: false)
{
    public volatile bool Armed;
    public ManualResetEventSlim Entered { get; } = new();

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (Armed && !Entered.IsSet)
        {
            Entered.Set();
            proxyLoading.Wait(TimeSpan.FromSeconds(5));
        }

        return null; // the default context resolves it
    }
}
