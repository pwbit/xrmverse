using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Abstractions;

/// <summary>
/// Marks a request the core issues as the cascade of another write (plan 2.14, ruling W5: a child Delete of a Cascade delete
/// relationship). The pipeline runs it as any nested request (its plug-in steps fire) but flags its invocation as a core cascade, and
/// a core cascade triggers no workflow. The mark is kept beside the request, not in its parameters, so plug-ins see the same
/// <c>InputParameters</c>.
/// </summary>
public static class CascadeHint
{
    private static readonly ConditionalWeakTable<OrganizationRequest, object> Marks = new();

    /// <summary>Marks <paramref name="request"/> as a cascade the core issues.</summary>
    public static void Mark(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Marks.AddOrUpdate(request, Marks);
    }

    /// <summary>True when <paramref name="request"/> was marked by <see cref="Mark"/>.</summary>
    public static bool IsMarked(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Marks.TryGetValue(request, out _);
    }
}
