using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Abstractions;

/// <summary>
/// The audit action of a request that runs as another message (plan 13, ruling A6): SetState runs as an Update for plug-ins, yet
/// its audit row has the Set State action. The mark is kept beside the request, not in its parameters, so plug-ins see the same
/// <c>InputParameters</c> as without auditing.
/// </summary>
public static class AuditActionHint
{
    private static readonly ConditionalWeakTable<OrganizationRequest, StrongBox<int>> Hints = new();

    /// <summary>Marks <paramref name="request"/> with the audit action <paramref name="action"/>.</summary>
    public static void Mark(OrganizationRequest request, int action)
    {
        ArgumentNullException.ThrowIfNull(request);
        Hints.AddOrUpdate(request, new StrongBox<int>(action));
    }

    /// <summary>The audit action <paramref name="request"/> was marked with, or null.</summary>
    public static int? Of(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Hints.TryGetValue(request, out var box) ? box.Value : null;
    }
}
