using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Pipeline;

/// <summary>
/// Plan 4.1c: the shared variables only the server sets (<see cref="MailSchema.MailboxDeliveryVariable"/>). When the server builds a
/// context (a pipeline execution, or an asynchronous job rebuilt from its queued row), it takes a snapshot of their values in each
/// <c>SharedVariables</c> collection of the context and its parents. A plug-in or workflow that adds, changes or removes one fails the
/// message with 400. A collection with no snapshot expects none of them.
/// </summary>
internal static class ReservedSharedVariables
{
    /// <summary>The reserved keys.</summary>
    public static readonly string[] Keys = [MailSchema.MailboxDeliveryVariable];

    private static readonly IReadOnlyDictionary<string, object> None = new Dictionary<string, object>();

    // Keyed by the collection itself (by reference): the clones of a context share it, and a plug-in cannot reach the table.
    private static readonly ConditionalWeakTable<ParameterCollection, IReadOnlyDictionary<string, object>> Snapshots = new();

    /// <summary>
    /// Records the reserved values <paramref name="context"/>'s own <c>SharedVariables</c> hold now (a new execution: its parents
    /// already have theirs).
    /// </summary>
    public static void Snapshot(IExecutionContext context) => Take(context.SharedVariables);

    /// <summary>
    /// Records the reserved values of <paramref name="context"/> and of every <c>ParentContext</c> above it (a context rebuilt from its
    /// queued row, whose parents are rebuilt with it).
    /// </summary>
    public static void SnapshotChain(IExecutionContext context)
    {
        for (IExecutionContext? level = context; level is not null; level = (level as IPluginExecutionContext)?.ParentContext)
            Take(level.SharedVariables);
    }

    private static void Take(ParameterCollection? shared)
    {
        if (shared is null) return;
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in Keys)
            if (shared.TryGetValue(key, out var value)) values[key] = value;
        Snapshots.AddOrUpdate(shared, values);
    }

    /// <summary>
    /// Each reserved variable of <paramref name="context"/> and of every <c>ParentContext</c> above it still holds its snapshot (or is
    /// still absent).
    /// </summary>
    /// <param name="who">What may have changed it, for the message ("The plug-in step 'x'").</param>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> when one was added, changed or removed.</exception>
    public static void DemandKept(IExecutionContext context, string who)
    {
        for (IExecutionContext? level = context; level is not null; level = (level as IPluginExecutionContext)?.ParentContext)
        {
            if (level.SharedVariables is not { } shared) continue;
            var expected = Snapshots.TryGetValue(shared, out var snapshot) ? snapshot : None;
            foreach (var key in Keys)
            {
                var has = shared.TryGetValue(key, out var actual);
                var set = expected.TryGetValue(key, out var value);
                if (has != set || (has && !Equals(actual, value)))
                    throw XrmdFaultException.InvalidArgument(
                        $"{who} added, changed or removed a shared variable only the server sets: the shared variable '{key}' is reserved.");
            }
        }
    }
}
