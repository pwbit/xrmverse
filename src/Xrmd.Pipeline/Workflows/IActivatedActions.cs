namespace Xrmd.Pipeline.Workflows;

/// <summary>
/// The activated XAML actions (plan 2.14, ruling W14), process-wide, as the Web API reads them (issue #343): the operations it
/// calls when no custom API has the name, and the actions <c>$metadata</c> declares. Implemented by <c>Xrmd.Workflows</c>' registry
/// of the activated processes, which the pipeline invalidates after the commit of a transaction that wrote <c>workflow</c>.
/// </summary>
public interface IActivatedActions
{
    /// <summary>The activated actions, by message name; loaded on first use and again after an invalidation.</summary>
    Task<IReadOnlyList<ActionDefinition>> GetAsync(CancellationToken ct);

    /// <summary>
    /// The activated actions of the last load (empty before the first), never loading and never waiting, so the synchronous
    /// <c>$metadata</c> model builder can read them. Each load gives a new list: the builder caches its model on the instance.
    /// </summary>
    IReadOnlyList<ActionDefinition> Current { get; }
}
