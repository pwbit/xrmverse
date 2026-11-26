using System.Runtime.CompilerServices;
using Xrmd.Model.Messages;

namespace Xrmd.Abstractions;

/// <summary>
/// Marks a request the core issues as the cascade of another write (plan 2.14, ruling W5: a child Delete of a Cascade delete
/// relationship). The pipeline runs it as any nested request (its plug-in steps fire) but flags its invocation as a core cascade, and
/// a core cascade triggers no workflow. The mark is kept beside the request, not in its parameters, so plug-ins see the same
/// <c>InputParameters</c>.
/// </summary>
/// <remarks>
/// A model request and an SDK request (<c>Sdk/RequestHints.Sdk.cs</c>) have separate tables; only the pipeline copies a mark from one to
/// the other where a request crosses (S.2e3b, Decision 6).
/// </remarks>
public static partial class CascadeHint
{
    private static readonly ConditionalWeakTable<Request, object> ModelMarks = new();

    /// <summary>Marks the model <paramref name="request"/> as a cascade the core issues.</summary>
    public static void Mark(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelMarks.AddOrUpdate(request, ModelMarks);
    }

    /// <summary>True when the model <paramref name="request"/> was marked by <see cref="Mark(Request)"/>.</summary>
    public static bool IsMarked(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ModelMarks.TryGetValue(request, out _);
    }
}
