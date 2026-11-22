using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.Layers;

/// <summary>
/// Restores components from their layers (plan 2.6, ruling M5): the payloads are merged into one package
/// (<see cref="LayerPayload.ToPackage"/>) that <see cref="SolutionImporter"/> applies in <see cref="ApplyMode.Restore"/>, so a
/// restore writes components through the same path as an import. It writes no publisher or solution row, no
/// <c>solutioncomponent</c>, no <c>importjob</c> and no Active marker, and runs as <see cref="Caller.Internal"/> (rulings R2, R5).
/// </summary>
/// <remarks>
/// Only the requested components (each layer's type and id) are written; what a payload carries besides (the table of a column,
/// a lookup's relationships or a relationship's lookup) is there for the reader only. So a table carried only for its columns is
/// not itself restored: the caller passes the table's own layer when the table is to be restored too. Labels are replaced
/// (a label the layer lacks is cleared); option values are never removed and a relationship's cascade configuration is never
/// reverted (each a warning).
/// </remarks>
/// <param name="importer">The importer of the caller's scope.</param>
public sealed class LayerRestorer(SolutionImporter importer)
{
    /// <summary>
    /// Restores each layer's component to the content of that layer, on <paramref name="s"/> (the caller's transaction: a
    /// failure leaves the caller to roll back everything). Usually the new top layer of each component after an uninstall or
    /// a promote.
    /// </summary>
    /// <param name="s">The caller's session, in a transaction.</param>
    /// <param name="layers">The layers to restore, each with its payload (System or Managed; an Active marker has none).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The warnings of the restore: those the import's write path logs, each option value kept (a restore does not
    /// remove one) and each cascade configuration kept (a restore does not revert one).</returns>
    /// <exception cref="ArgumentException">For an Active marker or a layer without a payload.</exception>
    /// <exception cref="XrmdFaultException">400 naming the component and the property for a difference the store cannot apply
    /// (a shorter <c>MaxLength</c>, another attribute type, ...), for layers of different languages or a requested component
    /// xrmverse does not have; the faults of the import's write path.</exception>
    public async Task<IReadOnlyList<string>> RestoreAsync(DataSession s, IReadOnlyList<ComponentLayer> layers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0) return [];
        foreach (var layer in layers.Where(l => l.Kind == LayerKind.Active || l.Payload is null))
            throw new ArgumentException(
                $"The layer of solution {layer.SolutionId} on component {layer.ObjectId} of type {layer.Type} has no payload to restore.", nameof(layers));
        var package = LayerPayload.ToPackage(layers.Select(l => (l.Type, l.ObjectId, l.Payload!)).ToList());
        var requested = layers.Select(l => (ComponentType.Normalize(l.Type), l.ObjectId)).ToHashSet();
        return await importer.RestoreAsync(s, package, requested, ct);
    }
}
