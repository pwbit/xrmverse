using System.Runtime.CompilerServices;
using Xrmd.Model.Messages;

namespace Xrmd.Abstractions;

/// <summary>
/// The audit action of a request that runs as another message (plan 13, ruling A6): SetState runs as an Update for plug-ins, yet
/// its audit row has the Set State action. The mark is kept beside the request, not in its parameters, so plug-ins see the same
/// <c>InputParameters</c> as without auditing.
/// </summary>
/// <remarks>
/// A model request and an SDK request (<c>Sdk/RequestHints.Sdk.cs</c>) have separate tables; only the pipeline copies a mark from one to
/// the other where a request crosses (S.2e3b, Decision 6).
/// </remarks>
public static partial class AuditActionHint
{
    private static readonly ConditionalWeakTable<Request, StrongBox<int>> ModelHints = new();

    /// <summary>Marks the model <paramref name="request"/> with the audit action <paramref name="action"/>.</summary>
    public static void Mark(Request request, int action)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelHints.AddOrUpdate(request, new StrongBox<int>(action));
    }

    /// <summary>The audit action the model <paramref name="request"/> was marked with, or null.</summary>
    public static int? Of(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ModelHints.TryGetValue(request, out var box) ? box.Value : null;
    }
}
