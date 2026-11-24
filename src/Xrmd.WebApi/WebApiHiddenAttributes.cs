using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Model;

namespace Xrmd.WebApi;

/// <summary>
/// Columns that exist in the metadata and the SDK path but are not part of the Web API entity type: absent from
/// <c>$metadata</c> and from every response, and refused (400) in <c>$select</c>, <c>$filter</c> and <c>$orderby</c> as any
/// unknown property; a FetchXML query (<c>?fetchXml=</c>) that names one is refused the same way, and one that reads one through
/// <c>all-attributes</c> does not get it back, under an alias or not. They are <c>audit.changedata</c>, which holds xrmverse's own
/// change format (clients read the audit messages instead), and the party list columns of the activities (plan 2.11, A8: as in
/// Dataverse, the Web API reads and writes them through the collection-valued navigation property <c>{activity}_activity_parties</c>).
/// </summary>
public static class WebApiHiddenAttributes
{
    /// <summary>True for <c>audit.changedata</c> (the name-only rule: the party lists need their metadata, see the overloads).</summary>
    public static bool IsHidden(string? entity, string? attribute) =>
        string.Equals(entity, AuditTable.Name, StringComparison.Ordinal)
        && string.Equals(attribute, AuditTable.ChangeData, StringComparison.Ordinal);

    /// <summary>True when <paramref name="attribute"/> of <paramref name="entity"/> is hidden (a party list is found in <paramref name="store"/>).</summary>
    public static bool IsHidden(string? entity, string? attribute, IMetadataStore? store) =>
        IsHidden(entity, attribute)
        || (entity is not null && store?.GetEntity(entity)?.Attributes?.FirstOrDefault(a => a.LogicalName == attribute) is { } a
            && ActivitySchema.IsPartyList(a));

    /// <summary>
    /// Refuses a FetchXML query of the Web API that names a hidden column: as a column (plain, aliased or aggregated), in a
    /// condition, in an order or in a link's <c>from</c>/<c>to</c>, of the root or of any link-entity.
    /// </summary>
    /// <exception cref="XrmdValidationException">Naming the column and its table.</exception>
    internal static void DemandNotNamed(QueryModel model, IMetadataStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var byAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(model.Root, byAlias);
        Check(model.Root, parent: null, byAlias, store);
    }

    private static void Collect(EntityNode node, Dictionary<string, string> byAlias)
    {
        foreach (var link in node.Links)
        {
            byAlias.TryAdd(link.Alias ?? link.EntityName, link.EntityName);
            Collect(link, byAlias);
        }
    }

    private static void Check(EntityNode node, EntityNode? parent, Dictionary<string, string> byAlias, IMetadataStore? store)
    {
        foreach (var column in node.Columns) Demand(node.EntityName, column.Attribute, store);
        foreach (var order in node.Orders.Where(o => o.Alias is null)) Demand(node.EntityName, order.Attribute, store);
        CheckFilter(node.Filter, node.EntityName, byAlias, store);
        if (node is LinkNode link)
        {
            Demand(link.EntityName, link.From, store);
            if (parent is not null) Demand(parent.EntityName, link.To, store);
        }
        foreach (var child in node.Links) Check(child, node, byAlias, store);
    }

    private static void CheckFilter(FilterNode filter, string entity, Dictionary<string, string> byAlias, IMetadataStore? store)
    {
        foreach (var condition in filter.Conditions)
            Demand(condition.EntityAlias is { } alias ? byAlias.GetValueOrDefault(alias, alias) : entity, condition.Attribute, store);
        foreach (var child in filter.Filters) CheckFilter(child, entity, byAlias, store);
    }

    private static void Demand(string entity, string? attribute, IMetadataStore? store)
    {
        if (IsHidden(entity, attribute)) throw new XrmdValidationException(AuditColumnMessage(entity, attribute));
        if (IsHidden(entity, attribute, store))
            throw new XrmdValidationException(
                $"The party list column '{attribute}' of table '{entity}' is not part of the Web API: read the parties with " +
                $"$expand={entity}_activity_parties, or query '{ActivitySchema.Party}'.");
    }

    /// <summary>The refusal of a name-hidden column (<c>audit.changedata</c>) in a query.</summary>
    internal static string AuditColumnMessage(string entity, string? attribute) =>
        $"The column '{attribute}' of table '{entity}' is not part of the Web API: it cannot be selected, filtered on, ordered by or joined on in a query. Read it with the audit messages.";

    /// <summary>
    /// The columns of an "all columns" read of <paramref name="entity"/> that leaves the hidden ones out (so that
    /// <c>changedata</c>, an <c>nvarchar(max)</c>, is not loaded to be dropped, and the party lists are not read); null when the table
    /// has none hidden.
    /// </summary>
    internal static IReadOnlyList<string>? VisibleColumns(Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity) =>
        (entity.Attributes ?? []).Any(a => Hidden(entity, a))
            ? (entity.Attributes ?? []).Where(a => ODataEdmModelBuilder.IsExposed(a) && !Hidden(entity, a)).Select(a => a.LogicalName).ToList()
            : null;

    private static bool Hidden(Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity, Microsoft.Xrm.Sdk.Metadata.AttributeMetadata a) =>
        ActivitySchema.IsPartyList(a) || IsHidden(entity.LogicalName, a.LogicalName);
}
