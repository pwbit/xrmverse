using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Pipeline.Merge;

/// <summary>Who started an inner execution (#76).</summary>
internal enum InnerKind
{
    /// <summary>The per-target run of a <c>CreateMultiple</c>, <c>UpdateMultiple</c> or <c>UpsertMultiple</c> (<c>BulkStage</c>, ruling B4).</summary>
    Bulk,

    /// <summary>The <c>Create</c> or <c>Update</c> that the stage 30 of an <c>Upsert</c> on a standard table runs (ruling B10).</summary>
    Upsert,
}

/// <summary>
/// Marks a request the pipeline runs as an <em>inner</em> execution of another message (#76, ruling B4): the single-record
/// request that the stage 30 of an <c>Upsert</c> (or of a bulk message) on a standard table issues, at the same depth, in the
/// same transaction, with the outer context as <c>ParentContext</c>. The mark records its <see cref="InnerKind"/>: only a
/// <see cref="InnerKind.Bulk"/> inner skips the single-to-bulk merge (its bulk steps already fired at the bulk level). The mark
/// lives beside the request (as <c>AuditActionHint</c>), so plug-ins see the same <c>InputParameters</c> and cannot set or clear it.
/// </summary>
internal static class InnerExecution
{
    private static readonly ConditionalWeakTable<OrganizationRequest, StrongBox<InnerKind>> Marks = new();

    /// <summary>Marks <paramref name="request"/> as an inner execution of <paramref name="kind"/>.</summary>
    public static void Mark(OrganizationRequest request, InnerKind kind)
    {
        ArgumentNullException.ThrowIfNull(request);
        Marks.AddOrUpdate(request, new StrongBox<InnerKind>(kind));
    }

    /// <summary>The kind <paramref name="request"/> was marked with; null when it is not an inner execution.</summary>
    public static InnerKind? KindOf(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Marks.TryGetValue(request, out var box) ? box.Value : null;
    }
}
