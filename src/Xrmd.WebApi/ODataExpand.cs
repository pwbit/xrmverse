using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Model;

namespace Xrmd.WebApi;

/// <summary>
/// One <c>$expand</c> of a single-valued navigation property as the JSON writer reads it: the link's alias (its columns come back
/// as <c>{alias}.{attribute}</c>), the target table and the nested <c>$expand</c>s of the target, by navigation property
/// (issue #190).
/// </summary>
public sealed record ODataExpand(string Alias, EntityMetadata Target, IReadOnlyDictionary<string, ODataExpand> Children)
{
    /// <summary>The expands of <paramref name="model"/> by navigation property; null for an aggregate or a query without links.</summary>
    public static IReadOnlyDictionary<string, ODataExpand>? From(IMetadataStore store, QueryModel model)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Aggregate || model.Root.Links.Count == 0) return null;
        return Of(store, model.Root);
    }

    private static Dictionary<string, ODataExpand> Of(IMetadataStore store, EntityNode node) =>
        node.Links.ToDictionary(l => l.NavigationProperty ?? l.Alias!,
            l => new ODataExpand(l.Alias!, store.GetEntity(l.EntityName)!, Of(store, l)), StringComparer.Ordinal);

    /// <summary>The aliases of <paramref name="expands"/> and of all their nested expands.</summary>
    public static IEnumerable<string> Aliases(IReadOnlyDictionary<string, ODataExpand> expands) =>
        expands.Values.SelectMany(e => Aliases(e.Children).Prepend(e.Alias));
}
