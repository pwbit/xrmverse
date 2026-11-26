using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Abstractions;

/// <summary>The SDK overloads of <see cref="CascadeHint"/>: the same mark, keyed on an <see cref="OrganizationRequest"/>.</summary>
public static partial class CascadeHint
{
    private static readonly ConditionalWeakTable<OrganizationRequest, object> Marks = new();

    /// <summary>Marks <paramref name="request"/> as a cascade the core issues.</summary>
    public static void Mark(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Marks.AddOrUpdate(request, Marks);
    }

    /// <summary>True when <paramref name="request"/> was marked by <see cref="Mark(OrganizationRequest)"/>.</summary>
    public static bool IsMarked(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Marks.TryGetValue(request, out _);
    }
}

/// <summary>The SDK overloads of <see cref="AuditActionHint"/>: the same mark, keyed on an <see cref="OrganizationRequest"/>.</summary>
public static partial class AuditActionHint
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
