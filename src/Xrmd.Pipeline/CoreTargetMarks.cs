using System.Runtime.CompilerServices;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline;

/// <summary>
/// S.2e3c3b1 (Decision 4): what the core knows about the model Target of a write that the SDK boundary needs to keep the caller's
/// <c>Entity</c> in step (<see cref="CoreSdkBoundary"/>), kept beside the record as <c>CascadeHint</c> keeps its mark beside a request.
/// The core marks the Target of a Create or Update on an elastic table before the body runs; the boundary reads the mark after the call
/// instead of the metadata store, which only the core holds. Internal: only the core (<c>InternalsVisibleTo</c>) sets it.
/// </summary>
internal static class CoreTargetMarks
{
    private static readonly ConditionalWeakTable<Record, object> Elastic = new();

    /// <summary>Marks <paramref name="record"/> as the Target of a write on an elastic table.</summary>
    public static void MarkElastic(Record record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Elastic.AddOrUpdate(record, Elastic);
    }

    /// <summary>True when <paramref name="record"/> was marked by <see cref="MarkElastic"/>.</summary>
    public static bool IsElastic(Record record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Elastic.TryGetValue(record, out _);
    }
}
