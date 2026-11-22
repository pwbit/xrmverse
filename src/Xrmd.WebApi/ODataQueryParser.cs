using System.Globalization;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using Microsoft.OData.UriParser.Aggregation;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.WebApi;

/// <summary>
/// Translates the OData query options of <c>GET {entityset}</c> into a <see cref="QueryModel"/>, parsing them with
/// <see cref="ODataUriParser"/> over the EDM model:
/// <list type="bullet">
/// <item><c>$select</c> → columns (a lookup as <c>_{name}_value</c>); absent → all columns.</item>
/// <item><c>$filter</c> → filter nodes: <c>eq ne gt ge lt le</c> between a property and a literal (<c>null</c> →
/// Null/NotNull), <c>and</c>/<c>or</c> (chains of the same operator flattened), <c>contains</c> (Like <c>%v%</c>),
/// <c>startswith</c> (BeginsWith), <c>endswith</c> (EndsWith), and <c>not</c> only in front of these three functions.
/// A comparison on <c>_{name}_value</c> is a condition on the lookup.</item>
/// <item><c>$orderby</c>, <c>$top</c>, <c>$count=true</c> (ReturnTotalRecordCount).</item>
/// <item><c>$expand={lookup}[($select=a,b)]</c> of a single-valued navigation property → a LeftOuter <see cref="LinkNode"/>
/// aliased by the lookup name (target primary key always selected; all readable columns without a nested
/// <c>$select</c>); a per-target navigation property of a lookup of the activity model (<c>regardingobjectid_account</c>,
/// <c>partyid_contact</c>, <c>activityid_email</c>, plan 2.11 A14) joins its table and the lookup's type column. Other polymorphic
/// lookups, collection-valued navigation, nested options other than <c>$select</c> → 400, except the
/// party rows of an activity (<c>{activity}_activity_parties</c>, plan 2.11), which the caller reads with
/// <see cref="ReadPartyExpand"/> when it passes <c>partyExpand</c>.</item>
/// <item><c>$apply=[filter(…)/]aggregate(…)</c> or <c>[filter(…)/]groupby((p1,…)[,aggregate(…)])</c> → an aggregate query:
/// <c>sum average min max countdistinct</c> on a property and <c>$count</c>, all with <c>as alias</c>; groupby properties
/// are aliased by their own name (<c>_{lookup}_value</c> groups on the lookup). Only <c>$orderby</c> on these aliases and
/// <c>$top</c> may accompany <c>$apply</c>.</item>
/// <item><c>fetchXml</c>: see <see cref="ParseFetchXml"/>.</item>
/// <item>Paging: the page size is <c>odata.maxpagesize</c>; the <c>$skiptoken</c> (see <see cref="FormatSkipToken"/>)
/// carries the page number and the paging cookie.</item>
/// </list>
/// </summary>
public sealed class ODataQueryParser(ODataEdmModelBuilder edm, IMetadataStore store)
{
    private const string Select = "$select";
    private const string Filter = "$filter";
    private const string OrderBy = "$orderby";
    private const string Top = "$top";
    private const string Count = "$count";
    private const string SkipToken = "$skiptoken";
    private const string Expand = "$expand";
    private const string Apply = "$apply";
    private const string FetchXmlOption = "fetchXml";

    private static readonly string[] Parsed = [Select, Filter, OrderBy, Top, Count, Expand, Apply];

    /// <summary>Options with a dedicated Dataverse meaning that this server does not implement yet.</summary>
    private static readonly string[] NotSupportedYet = ["savedQuery", "userQuery"];

    /// <summary>The largest page number a <c>$skiptoken</c> may carry (keeps <c>PageNumber + 1</c> far from overflow).</summary>
    public const int MaxPageNumber = 1_000_000;

    private static readonly Uri ServiceRoot = new("http://localhost" + MetadataController.ServiceRootPath);

    /// <param name="m">The entity of the entity set.</param>
    /// <param name="query">The request's query options (<c>$skiptoken</c> is read from <paramref name="skipToken"/>).</param>
    /// <param name="maxPageSize">The <c>odata.maxpagesize</c> preference (null: the default, 5000).</param>
    /// <param name="skipToken">The <c>$skiptoken</c> of a <c>@odata.nextLink</c>.</param>
    /// <param name="partyExpand">True when the caller reads the <c>$expand</c> of the activity's party rows itself
    /// (<see cref="ReadPartyExpand"/>): the item is left out of the model; false: it is refused as any collection-valued
    /// <c>$expand</c>.</param>
    /// <exception cref="XrmdValidationException">An invalid, unknown or unsupported option, property, expression or token.</exception>
    public QueryModel Parse(EntityMetadata m, IQueryCollection query, int? maxPageSize, string? skipToken, bool partyExpand = false)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(query);
        m = store.GetEntity(m.LogicalName) ?? m;
        var options = ReadOptions(query);
        if (maxPageSize is { } size && size is < 1 or > SqlQueryCompiler.MaxPageSize)
            throw new XrmdValidationException(
                $"The odata.maxpagesize preference must be between 1 and {SqlQueryCompiler.MaxPageSize}, got {size}.");

        var root = new EntityNode { EntityName = m.LogicalName };
        // Learn ("Order rows by using OData"): $orderby sorts a choice by its integer value, unlike FetchXML and
        // QueryExpression, and a lookup by the related primary name.
        var model = new QueryModel { Root = root, PageSize = maxPageSize, UseRawOrderBy = true };
        var lookups = Lookups(m);
        if (options.ContainsKey(Apply))
        {
            ParseApply(m, options, model, lookups);
        }
        else
        {
            var parser = CreateParser(m, options);
            try
            {
                ReadSelect(parser.ParseSelectAndExpand(), m, root, lookups, partyExpand);
                if (ParseFilter(parser, options) is { } filter)
                    root.Filter = new FilterTranslator(options[Filter], lookups).Translate(filter.Expression);
                for (var order = parser.ParseOrderBy(); order is not null; order = order.ThenBy)
                    root.Orders.Add(new OrderNode(PropertyOf(order.Expression, lookups, OrderBy, options[OrderBy]), order.Direction == OrderByDirection.Descending));
                model.Top = ReadTop(parser);
                model.ReturnTotalRecordCount = parser.ParseCount() == true;
            }
            catch (Exception ex) when (IsParserError(ex))
            {
                throw new XrmdValidationException(ex.Message, ex);
            }
        }

        if (!string.IsNullOrEmpty(skipToken))
            (model.PageNumber, model.PagingCookie) = ReadSkipToken(skipToken);
        return model;
    }

    /// <summary>
    /// <c>GET {entityset}({id})</c> with <c>$expand</c>: the <c>$select</c> and <c>$expand</c> options (others are
    /// ignored, as on a plain retrieve) as a query of the record with that primary key (<c>top 1</c>).
    /// </summary>
    /// <param name="partyExpand">As for <see cref="Parse"/>.</param>
    /// <exception cref="XrmdValidationException">An invalid or unsupported <c>$select</c> or <c>$expand</c>.</exception>
    public QueryModel ParseSingle(EntityMetadata m, IQueryCollection query, Guid id, bool partyExpand = false)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(query);
        m = store.GetEntity(m.LogicalName) ?? m;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            var name = new[] { Select, Expand }.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (name is null) continue;
            if (values.Count != 1 || options.ContainsKey(name))
                throw new XrmdValidationException($"The query parameter {name} is specified more than once.");
            options[name] = values[0] ?? "";
        }

        var root = new EntityNode { EntityName = m.LogicalName };
        var parser = CreateParser(m, options);
        try
        {
            ReadSelect(parser.ParseSelectAndExpand(), m, root, Lookups(m), partyExpand);
        }
        catch (Exception ex) when (IsParserError(ex))
        {
            throw new XrmdValidationException(ex.Message, ex);
        }
        root.Filter.Conditions.Add(new ConditionNode(null, SystemAttributes.PrimaryIdName(m), ConditionOperator.Equal, [id]));
        return new QueryModel { Root = root, Top = 1 };
    }

    /// <summary>
    /// The <c>$expand</c> of the party rows of an activity (plan 2.11, A8, A14): the collection-valued navigation property
    /// <c>{activity}_activity_parties</c> of <paramref name="m"/>, with the <c>activityparty</c> columns of its nested <c>$select</c>
    /// (null: all of them); null when the request does not expand it. Only <c>$select</c> is supported inside it.
    /// </summary>
    /// <exception cref="XrmdValidationException">An invalid <c>$expand</c>, or an option other than <c>$select</c> inside the item.</exception>
    public PartyExpand? ReadPartyExpand(EntityMetadata m, IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(query);
        m = store.GetEntity(m.LogicalName) ?? m;
        if (PartyNavigation(m) is not { } navigation) return null;
        var text = query.FirstOrDefault(q => string.Equals(q.Key, Expand, StringComparison.OrdinalIgnoreCase)).Value.ToString();
        if (string.IsNullOrEmpty(text)) return null;
        var party = store.GetEntity(ActivitySchema.Party)
                    ?? throw new XrmdValidationException($"The table '{ActivitySchema.Party}' does not exist.");
        try
        {
            foreach (var item in CreateParser(m, new Dictionary<string, string> { [Expand] = text }).ParseSelectAndExpand()?.SelectedItems ?? [])
            {
                if (item is not ExpandedNavigationSelectItem { PathToNavigationProperty.FirstSegment: NavigationPropertySegment segment } expand
                    || segment.NavigationProperty.Name != navigation)
                    continue;
                DemandOnlySelect(expand, navigation);
                var node = new EntityNode { EntityName = party.LogicalName };
                ReadSelect(expand.SelectAndExpand, party, node, Lookups(party), partyExpand: false);
                if (node.Links.Count > 0)
                    throw new XrmdValidationException($"$expand inside '{navigation}' is not supported.");
                return new PartyExpand(navigation, node.AllColumns ? null : [.. node.Columns.Select(c => c.Attribute)]);
            }
        }
        catch (Exception ex) when (IsParserError(ex))
        {
            throw new XrmdValidationException(ex.Message, ex);
        }
        return null;
    }

    /// <summary>The collection-valued navigation property of the party rows of <paramref name="m"/>; null for a table that has none.</summary>
    private string? PartyNavigation(EntityMetadata m) =>
        store.GetOneToManyRelationships().FirstOrDefault(r => r.ReferencingEntity == ActivitySchema.Party
                                                               && r.ReferencingAttribute == ActivitySchema.ActivityId
                                                               && r.ReferencedEntity == m.LogicalName)?.ReferencedEntityNavigationPropertyName;

    /// <summary>True when the request carries a <c>fetchXml</c> query parameter.</summary>
    public static bool IsFetchXml(IQueryCollection query) =>
        query.Keys.Any(k => string.Equals(k, FetchXmlOption, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>GET {entityset}?fetchXml=…</c>: the FetchXML (see <see cref="FetchXmlParser.Parse"/>); its entity must be the
    /// entity of the set and no OData system query option (<c>$…</c>) may accompany it.
    /// </summary>
    /// <exception cref="XrmdValidationException">Invalid FetchXML, another entity, or another query option.</exception>
    public QueryModel ParseFetchXml(EntityMetadata m, IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(query);
        string? fetchXml = null;
        foreach (var (key, values) in query)
        {
            if (string.Equals(key, FetchXmlOption, StringComparison.OrdinalIgnoreCase))
            {
                if (values.Count != 1 || fetchXml is not null)
                    throw new XrmdValidationException($"The query parameter {FetchXmlOption} is specified more than once.");
                fetchXml = values[0] ?? "";
            }
            else if (key.StartsWith('$') || NotSupportedYet.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                throw new XrmdValidationException($"The query parameter {key} cannot be combined with {FetchXmlOption}.");
        }
        if (fetchXml is null) throw new XrmdValidationException($"The query parameter {FetchXmlOption} is missing.");

        var model = FetchXmlParser.Parse(fetchXml);
        if (!string.Equals(model.Root.EntityName, m.LogicalName, StringComparison.Ordinal))
            throw new XrmdValidationException(
                $"The entity '{model.Root.EntityName}' of the FetchXML does not match the entity set '{m.EntitySetName}' (entity '{m.LogicalName}').");
        WebApiHiddenAttributes.DemandNotNamed(model, store);
        return model;
    }

    private static HashSet<string> Lookups(EntityMetadata m) =>
        (m.Attributes ?? []).OfType<LookupAttributeMetadata>().Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);

    private static int? ReadTop(ODataUriParser parser) => parser.ParseTop() switch
    {
        null => null,
        >= 1 and <= SqlQueryCompiler.MaxPageSize and var top => (int)top,
        var top => throw new XrmdValidationException($"$top must be between 1 and {SqlQueryCompiler.MaxPageSize}, got {top}."),
    };

    // ---- $apply -------------------------------------------------------------------------------------------------

    /// <summary>Fills <paramref name="model"/> from <c>$apply</c> (with <c>$orderby</c> on its aliases and <c>$top</c>).</summary>
    private void ParseApply(EntityMetadata m, Dictionary<string, string> options, QueryModel model, HashSet<string> lookups)
    {
        foreach (var other in new[] { Select, Expand, Filter, Count })
            if (options.ContainsKey(other))
                throw new XrmdValidationException($"The query parameter {other} cannot be combined with $apply.");

        var text = options[Apply];
        var root = model.Root;
        model.Aggregate = true;
        var parser = CreateParser(m, options.Where(o => o.Key is Apply or Top).ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal));
        try
        {
            var transformations = parser.ParseApply()?.Transformations.ToList() ?? [];
            if (transformations.FirstOrDefault() is FilterTransformationNode filter)
            {
                root.Filter = new FilterTranslator(text, lookups).Translate(filter.FilterClause.Expression);
                transformations.RemoveAt(0);
            }
            if (transformations.Count != 1)
                throw UnsupportedApply(text, "expected one aggregate or groupby transformation, optionally preceded by filter");
            switch (transformations[0])
            {
                case AggregateTransformationNode aggregate:
                    ReadAggregates(aggregate, m, root, lookups, text);
                    break;
                case GroupByTransformationNode groupBy:
                    foreach (var property in groupBy.GroupingProperties)
                    {
                        if (property.ChildTransformations is { Count: > 0 } || TryProperty(property.Expression, lookups) is not { } attribute)
                            throw UnsupportedApply(text, $"groupby supports properties of the entity only ('{property.Name}')");
                        root.Columns.Add(new ColumnNode(attribute, property.Name, GroupBy: true));
                    }
                    switch (groupBy.ChildTransformations)
                    {
                        case null:
                            break;
                        case AggregateTransformationNode aggregate:
                            ReadAggregates(aggregate, m, root, lookups, text);
                            break;
                        default:
                            throw UnsupportedApply(text, "groupby supports an aggregate transformation only");
                    }
                    break;
                default:
                    throw UnsupportedApply(text, $"the transformation '{transformations[0].Kind}' is not supported");
            }
            model.Top = ReadTop(parser);
        }
        catch (Exception ex) when (IsParserError(ex))
        {
            throw new XrmdValidationException(ex.Message, ex);
        }

        if (options.TryGetValue(OrderBy, out var orderBy))
            foreach (var item in orderBy.Split(',', StringSplitOptions.TrimEntries))
            {
                var parts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var descending = parts.Length == 2 && string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);
                if (parts.Length is < 1 or > 2 || parts.Length == 2 && !descending && !string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase)
                    || root.Columns.All(c => c.Alias != parts[0]))
                    throw new XrmdValidationException(
                        $"The $orderby clause '{orderBy}' is not supported with $apply: only the groupby properties and aggregate aliases can be ordered.");
                root.Orders.Add(new OrderNode(parts[0], descending, parts[0]));
            }
    }

    private static void ReadAggregates(AggregateTransformationNode node, EntityMetadata m, EntityNode root, HashSet<string> lookups, string text)
    {
        foreach (var expression in node.AggregateExpressions)
        {
            if (expression is not AggregateExpression aggregate)
                throw UnsupportedApply(text, "only property aggregations and $count are supported");
            if (aggregate.Method == AggregationMethod.VirtualPropertyCount)
            {
                root.Columns.Add(new ColumnNode(SystemAttributes.PrimaryIdName(m), aggregate.Alias, XrmAggregate.Count));
                continue;
            }
            var attribute = aggregate.Expression is null ? null : TryProperty(aggregate.Expression, lookups);
            if (attribute is null) throw UnsupportedApply(text, $"the aggregation '{aggregate.Alias}' must be on a property of the entity");
            root.Columns.Add(aggregate.Method switch
            {
                AggregationMethod.Sum => new ColumnNode(attribute, aggregate.Alias, XrmAggregate.Sum),
                AggregationMethod.Average => new ColumnNode(attribute, aggregate.Alias, XrmAggregate.Avg),
                AggregationMethod.Min => new ColumnNode(attribute, aggregate.Alias, XrmAggregate.Min),
                AggregationMethod.Max => new ColumnNode(attribute, aggregate.Alias, XrmAggregate.Max),
                AggregationMethod.CountDistinct => new ColumnNode(attribute, aggregate.Alias, XrmAggregate.CountColumn, Distinct: true),
                _ => throw UnsupportedApply(text, $"the aggregation method of '{aggregate.Alias}' is not supported"),
            });
        }
    }

    private static XrmdValidationException UnsupportedApply(string text, string reason) =>
        new($"The $apply clause '{text}' is not supported: {reason}.");

    /// <summary>
    /// The <c>$skiptoken</c> of the page <paramref name="pageNumber"/>:
    /// <c>&lt;cookie pagenumber="N" pagingcookie="{XML-escaped cookie}" istracking="False" /&gt;</c> (not URL-encoded).
    /// </summary>
    public static string FormatSkipToken(int pageNumber, string? pagingCookie) => string.Create(CultureInfo.InvariantCulture,
        $"<cookie pagenumber=\"{pageNumber}\" pagingcookie=\"{SecurityElement.Escape(pagingCookie ?? "")}\" istracking=\"False\" />");

    /// <summary>
    /// <c>$filter</c>, where a Dataverse query function (<c>Microsoft.Dynamics.CRM.Under(...)</c> and the others, which the EDM
    /// model does not declare) fails with an explicit message instead of the parser's "unknown function" (plan 2.5, ruling H9).
    /// </summary>
    /// <exception cref="XrmdValidationException">The clause names a query function.</exception>
    private static FilterClause? ParseFilter(ODataUriParser parser, Dictionary<string, string> options)
    {
        try
        {
            return parser.ParseFilter();
        }
        catch (Exception ex) when (IsParserError(ex) && options.TryGetValue(Filter, out var text)
                                   && QueryFunction.Match(text) is { Success: true } function)
        {
            throw new XrmdValidationException(
                $"The $filter clause '{text}' is not supported: Dataverse query functions are not supported in $filter " +
                $"(found {function.Groups[1].Value}); use FetchXML (?fetchXml=) or QueryExpression.", ex);
        }
    }

    /// <summary>A call of a Dataverse query function in a <c>$filter</c> clause.</summary>
    private static readonly System.Text.RegularExpressions.Regex QueryFunction = new(
        @"(Microsoft\.Dynamics\.CRM\.[A-Za-z]+)\s*\(", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Exceptions ODataUriParser throws for malformed input: <see cref="ODataException"/>, and also overflow (<c>1e400</c>,
    /// <c>INF</c> or <c>NaN</c> against a decimal), invalid casts (nested options in <c>$select</c>), format and argument
    /// errors. Our own <see cref="XrmdValidationException"/> passes through unchanged.
    /// </summary>
    private static bool IsParserError(Exception ex) =>
        ex is ODataException or OverflowException or InvalidCastException or FormatException
        || ex is ArgumentException and not XrmdValidationException;

    /// <summary>The supported options by name; rejects <c>$skip</c>, options not supported yet and other system options.</summary>
    private static Dictionary<string, string> ReadOptions(IQueryCollection query)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            if (string.Equals(key, "$skip", StringComparison.OrdinalIgnoreCase))
                throw new XrmdValidationException("The query parameter $skip is not supported");
            if (NotSupportedYet.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                throw new XrmdValidationException($"The query parameter {key} is not supported yet.");
            if (string.Equals(key, FetchXmlOption, StringComparison.OrdinalIgnoreCase))
                throw new XrmdValidationException($"The query parameter {FetchXmlOption} cannot be combined with OData query options.");
            if (string.Equals(key, SkipToken, StringComparison.OrdinalIgnoreCase)) continue;

            var name = Parsed.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                if (key.StartsWith('$')) throw new XrmdValidationException($"The query parameter {key} is not supported.");
                continue; // not a system query option
            }
            if (values.Count != 1 || options.ContainsKey(name))
                throw new XrmdValidationException($"The query parameter {name} is specified more than once.");
            options[name] = values[0] ?? "";
        }
        return options;
    }

    private ODataUriParser CreateParser(EntityMetadata m, Dictionary<string, string> options)
    {
        var query = string.Join('&', options.Select(o => $"{o.Key}={Uri.EscapeDataString(o.Value)}"));
        var relative = Uri.EscapeDataString(m.EntitySetName ?? m.LogicalName) + (query.Length > 0 ? "?" + query : "");
        try
        {
            return new ODataUriParser(edm.Build(), ServiceRoot, new Uri(ServiceRoot, relative));
        }
        catch (Exception ex) when (IsParserError(ex))
        {
            throw new XrmdValidationException(ex.Message, ex);
        }
    }

    /// <summary>Reads <c>$select</c> into the root's columns and each <c>$expand</c> item into a link.</summary>
    private void ReadSelect(SelectExpandClause? clause, EntityMetadata m, EntityNode root, HashSet<string> lookups, bool partyExpand = false,
        ExpandState? expands = null, string? parentAlias = null)
    {
        expands ??= new ExpandState();
        var all = clause is null || clause.AllSelected;
        foreach (var item in clause?.SelectedItems ?? [])
        {
            switch (item)
            {
                case ExpandedNavigationSelectItem { PathToNavigationProperty.FirstSegment: NavigationPropertySegment segment }
                    when partyExpand && segment.NavigationProperty.Name == PartyNavigation(m):
                    break; // read by the caller (ReadPartyExpand)
                case ExpandedNavigationSelectItem expand:
                    root.Links.Add(ReadExpand(expand, m, expands, parentAlias));
                    break;
                case WildcardSelectItem:
                    all = true;
                    break;
                case PathSelectItem when all:
                    break;
                case PathSelectItem { SelectedPath.FirstSegment: PropertySegment property } path when path.SelectedPath.Count == 1:
                    var attribute = AttributeOf(property.Property.Name, lookups);
                    if (root.Columns.All(c => c.Attribute != attribute)) root.Columns.Add(new ColumnNode(attribute));
                    break;
                case PathSelectItem { SelectedPath.FirstSegment: NavigationPropertySegment navigation }:
                    var selected = navigation.NavigationProperty.Name;
                    var selectedLookup = LookupOfNavigation(m, selected)?.LogicalName ?? selected;
                    throw new XrmdValidationException(
                        $"$select of the navigation property '{selected}' is not supported: " +
                        $"select '{ODataEdmModelBuilder.LookupValueProperty(selectedLookup)}' instead.");
                default:
                    throw new XrmdValidationException("This $select item is not supported.");
            }
        }
        if (all || root.Columns.Count == 0)
        {
            root.Columns.Clear();
            if (WebApiHiddenAttributes.VisibleColumns(m) is { } visible)
                foreach (var name in visible) root.Columns.Add(new ColumnNode(name));
            else root.AllColumns = true;
        }
    }

    /// <summary>
    /// <c>$expand={lookup}[($select=…)]</c> → LeftOuter link to the lookup's single target, aliased by the navigation property name,
    /// with the target's primary key and the nested <c>$select</c> (all readable columns without it).
    /// </summary>
    private LinkNode ReadExpand(ExpandedNavigationSelectItem expand, EntityMetadata m, ExpandState expands, string? parentAlias)
    {
        if (expand.PathToNavigationProperty.Count != 1 || expand.PathToNavigationProperty.FirstSegment is not NavigationPropertySegment segment)
            throw new XrmdValidationException("This $expand item is not supported.");
        var navigation = segment.NavigationProperty;
        var name = navigation.Name;
        if (navigation.TargetMultiplicity() == EdmMultiplicity.Many)
            throw new XrmdValidationException($"$expand of the collection-valued navigation property '{name}' is not supported yet.");
        var (lookup, perTarget) = ODataEdmModelBuilder.FindNavigation(m, name, store.GetOneToManyRelationships())
                     ?? throw new XrmdValidationException($"'{name}' is not a single-valued navigation property of '{m.LogicalName}'.");
        // Plan 2.11 A14: a per-target navigation property of a lookup of the activity model joins its table and checks the lookup's
        // type column, never the id alone. Other polymorphic lookups (Customer, Owner, …) stay refused.
        var activityTarget = ActivitySchema.IsManagedLookup(m.LogicalName, lookup.LogicalName) ? perTarget
            // fileattachment.objectid (2.2): its navigation property, objectid_{table}, names its target; joined by id and type
            // the same way (plan 2.11, review I2: the note of a body's row, with that body).
            : FileColumns.IsAttachmentObjectId(lookup) ? (lookup.Targets ?? []).FirstOrDefault(t => name == $"{FileColumns.AttachmentLookup}_{t}")
            : null;
        // Plan 3.2: a lookup whose relationships name a navigation property of their own per target
        // (sdkmessageprocessingstep.eventhandler_serviceendpoint) expands that target, joined by id. A Customer column does not yet.
        var ownTarget = activityTarget is null && perTarget is not null && !CustomerColumns.IsCustomer(lookup) ? perTarget : null;
        if (activityTarget is null && ownTarget is null && (TypeMapping.IsPolymorphicLookup(lookup) || lookup.Targets is not { Length: 1 }))
            throw new XrmdValidationException($"$expand of the polymorphic lookup '{name}' is not supported yet.");
        DemandSupportedExpandOptions(expand, name);
        if (++expands.Count > MaxExpands)
            throw new XrmdValidationException($"A query can expand at most {MaxExpands} navigation properties, all levels counted.");

        var targetName = activityTarget ?? ownTarget ?? lookup.Targets![0];
        var target = store.GetEntity(targetName)
                     ?? throw new XrmdValidationException($"The target '{targetName}' of '{name}' does not exist.");
        var pk = SystemAttributes.PrimaryIdName(target);
        var link = new LinkNode
        {
            EntityName = target.LogicalName, From = pk, To = lookup.LogicalName, JoinType = JoinOperator.LeftOuter,
            Alias = expands.Take(parentAlias is null ? name : $"{parentAlias}_{name}"), NavigationProperty = name,
        };
        if (activityTarget is not null)
        {
            // activityid_activitypointer: the party row's type column holds the activity's own table, which the pointer row names.
            if (target.LogicalName == ActivitySchema.Pointer && lookup.LogicalName == ActivitySchema.ActivityId) link.ToTypeAttribute = ActivitySchema.ActivityTypeCode;
            else link.ToTypeCode = target.ObjectTypeCode
                                   ?? throw new XrmdValidationException($"The target '{targetName}' of '{name}' has no object type code.");
        }
        ReadSelect(expand.SelectAndExpand, target, link, Lookups(target), expands: expands, parentAlias: link.Alias);
        if (!link.AllColumns && link.Columns.All(c => c.Attribute != pk)) link.Columns.Insert(0, new ColumnNode(pk));
        return link;
    }

    /// <summary>Learn ("Join tables using OData"): a query carries at most 15 <c>$expand</c> options.</summary>
    public const int MaxExpands = 15;

    /// <summary>The <c>$expand</c> links of one query: their count over all levels and the aliases taken (issue #190, N3, N4).</summary>
    private sealed class ExpandState
    {
        private readonly HashSet<string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public int Count { get; set; }

        /// <summary><paramref name="alias"/>, or with the first numeric suffix that is still free.</summary>
        public string Take(string alias)
        {
            var taken = alias;
            for (var n = 2; !_aliases.Add(taken); n++) taken = alias + n.ToString(CultureInfo.InvariantCulture);
            return taken;
        }
    }

    /// <summary>
    /// Inside a single-valued <c>$expand</c>: <c>$select</c> and a nested <c>$expand</c> of single-valued navigation properties
    /// (issue #190, N1, N5). Learn documents <c>$filter</c>, <c>$orderby</c> and <c>$top</c> for collection-valued navigation
    /// properties only; they, the other options and <c>/$ref</c> are refused.
    /// </summary>
    private static void DemandSupportedExpandOptions(ExpandedNavigationSelectItem expand, string name)
    {
        if (expand.FilterOption is not null || expand.OrderByOption is not null || expand.TopOption is not null
            || expand.SkipOption is not null || expand.CountOption is not null || expand.SearchOption is not null
            || expand.LevelsOption is not null || expand.ComputeOption is not null || expand.ApplyOption is not null
            || expand.SelectAndExpand?.SelectedItems.Any(i => i is ExpandedReferenceSelectItem and not ExpandedNavigationSelectItem) == true)
            throw new XrmdValidationException(
                $"Only $select and a nested $expand are supported inside the $expand of the single-valued navigation property '{name}'.");
    }

    /// <exception cref="XrmdValidationException">An option other than <c>$select</c> inside the <c>$expand</c> item.</exception>
    private static void DemandOnlySelect(ExpandedNavigationSelectItem expand, string name)
    {
        if (expand.FilterOption is not null || expand.OrderByOption is not null || expand.TopOption is not null
            || expand.SkipOption is not null || expand.CountOption is not null || expand.SearchOption is not null
            || expand.LevelsOption is not null || expand.ComputeOption is not null || expand.ApplyOption is not null
            || expand.SelectAndExpand?.SelectedItems.Any(i => i is ExpandedReferenceSelectItem) == true)
            throw new XrmdValidationException($"Only $select is supported inside $expand ('{name}').");
    }

    /// <summary>The lookup of <paramref name="m"/> whose single-valued navigation property is <paramref name="navigation"/> (<see cref="ODataEdmModelBuilder.NavigationName"/>).</summary>
    private LookupAttributeMetadata? LookupOfNavigation(EntityMetadata m, string navigation)
    {
        return ODataEdmModelBuilder.FindNavigation(m, navigation, store.GetOneToManyRelationships())?.Lookup;
    }

    /// <summary>The attribute of a property: <c>_{lookup}_value</c> → the lookup, otherwise the property name.</summary>
    private static string AttributeOf(string property, HashSet<string> lookups)
    {
        if (property.Length > "__value".Length && property.StartsWith('_') && property.EndsWith("_value", StringComparison.Ordinal))
        {
            var lookup = property[1..^"_value".Length];
            if (lookups.Contains(lookup)) return lookup;
        }
        return property;
    }

    private static QueryNode Unwrap(QueryNode node)
    {
        while (node is ConvertNode convert) node = convert.Source;
        return node;
    }

    /// <summary>The attribute of a property of the queried entity, or null when the node is something else.</summary>
    private static string? TryProperty(QueryNode node, HashSet<string> lookups) =>
        Unwrap(node) is SingleValuePropertyAccessNode { Source: ResourceRangeVariableReferenceNode } access
            ? AttributeOf(access.Property.Name, lookups)
            : null;

    private static string PropertyOf(QueryNode node, HashSet<string> lookups, string option, string text) =>
        TryProperty(node, lookups) ?? throw new XrmdValidationException(
            $"The {option} clause '{text}' is not supported: only properties of the entity are supported.");

    /// <summary>Reads <c>pagenumber</c> and <c>pagingcookie</c>; DTDs are prohibited.</summary>
    /// <exception cref="XrmdValidationException">A malformed token or a page number out of range.</exception>
    public static (int PageNumber, string? PagingCookie) ReadSkipToken(string token)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StringReader(token);
            using var xr = XmlReader.Create(sr, settings);
            var root = XDocument.Load(xr).Root;
            if (root is not null && root.Name.LocalName == "cookie"
                && int.TryParse((string?)root.Attribute("pagenumber"), NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                && page is >= 1 and <= MaxPageNumber)
            {
                var cookie = (string?)root.Attribute("pagingcookie");
                return (page, string.IsNullOrEmpty(cookie) ? null : cookie);
            }
        }
        catch (XmlException)
        {
            // malformed: reported below
        }
        throw new XrmdValidationException("The $skiptoken is not valid.");
    }

    /// <summary>Visits the <c>$filter</c> expression; any node outside the supported subset is a validation error.</summary>
    private sealed class FilterTranslator(string text, HashSet<string> lookups)
    {
        public FilterNode Translate(SingleValueNode expression)
        {
            var node = Unwrap(expression);
            if (node is BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And or BinaryOperatorKind.Or } logical)
                return Group(logical);
            var filter = new FilterNode();
            filter.Conditions.Add(Condition(node));
            return filter;
        }

        private FilterNode Group(BinaryOperatorNode logical)
        {
            var filter = new FilterNode
            {
                Operator = logical.OperatorKind == BinaryOperatorKind.And ? LogicalOperator.And : LogicalOperator.Or,
            };
            Add(filter, logical.OperatorKind, logical.Left);
            Add(filter, logical.OperatorKind, logical.Right);
            return filter;
        }

        private void Add(FilterNode filter, BinaryOperatorKind kind, QueryNode operand)
        {
            var node = Unwrap(operand);
            if (node is BinaryOperatorNode binary && binary.OperatorKind == kind)
            {
                Add(filter, kind, binary.Left);
                Add(filter, kind, binary.Right);
            }
            else if (node is BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And or BinaryOperatorKind.Or } other)
                filter.Filters.Add(Group(other));
            else
                filter.Conditions.Add(Condition(node));
        }

        private ConditionNode Condition(QueryNode node) => node switch
        {
            BinaryOperatorNode binary => Comparison(binary),
            SingleValueFunctionCallNode call => StringFunction(call, negated: false),
            UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not when Unwrap(not.Operand) is SingleValueFunctionCallNode call =>
                StringFunction(call, negated: true),
            UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } => throw Unsupported("'not' is only supported in front of contains, startswith or endswith"),
            _ => throw Unsupported($"{node.Kind} expressions are not supported"),
        };

        private ConditionNode Comparison(BinaryOperatorNode binary)
        {
            var op = binary.OperatorKind switch
            {
                BinaryOperatorKind.Equal => ConditionOperator.Equal,
                BinaryOperatorKind.NotEqual => ConditionOperator.NotEqual,
                BinaryOperatorKind.GreaterThan => ConditionOperator.GreaterThan,
                BinaryOperatorKind.GreaterThanOrEqual => ConditionOperator.GreaterEqual,
                BinaryOperatorKind.LessThan => ConditionOperator.LessThan,
                BinaryOperatorKind.LessThanOrEqual => ConditionOperator.LessEqual,
                _ => throw Unsupported($"the operator '{binary.OperatorKind}' is not supported"),
            };
            var attribute = TryProperty(binary.Left, lookups);
            var literal = Unwrap(binary.Right);
            if (attribute is null)
            {
                attribute = TryProperty(binary.Right, lookups);
                literal = Unwrap(binary.Left);
                op = Flip(op);
            }
            if (attribute is null || literal is not ConstantNode constant)
                throw Unsupported("a comparison must be between a property of the entity and a literal");

            if (constant.Value is null)
                return op switch
                {
                    ConditionOperator.Equal => new ConditionNode(null, attribute, ConditionOperator.Null, []),
                    ConditionOperator.NotEqual => new ConditionNode(null, attribute, ConditionOperator.NotNull, []),
                    _ => throw Unsupported("null can only be compared with eq or ne"),
                };
            return new ConditionNode(null, attribute, op, [Value(constant.Value)]);
        }

        private ConditionNode StringFunction(SingleValueFunctionCallNode call, bool negated)
        {
            var parameters = call.Parameters.ToList();
            var op = call.Name switch
            {
                "contains" => negated ? ConditionOperator.NotLike : ConditionOperator.Like,
                "startswith" => negated ? ConditionOperator.DoesNotBeginWith : ConditionOperator.BeginsWith,
                "endswith" => negated ? ConditionOperator.DoesNotEndWith : ConditionOperator.EndsWith,
                _ => throw Unsupported($"the function '{call.Name}' is not supported"),
            };
            var attribute = parameters.Count == 2 ? TryProperty(parameters[0], lookups) : null;
            if (attribute is null || Unwrap(parameters[1]) is not ConstantNode { Value: string value })
                throw Unsupported($"'{call.Name}' requires a property of the entity and a string literal");
            return new ConditionNode(null, attribute, op, [call.Name == "contains" ? "%" + EscapeLike(value) + "%" : value]);
        }

        private static ConditionOperator Flip(ConditionOperator op) => op switch
        {
            ConditionOperator.GreaterThan => ConditionOperator.LessThan,
            ConditionOperator.GreaterEqual => ConditionOperator.LessEqual,
            ConditionOperator.LessThan => ConditionOperator.GreaterThan,
            ConditionOperator.LessEqual => ConditionOperator.GreaterEqual,
            _ => op,
        };

        /// <summary>A date literal is midnight UTC; other literals are passed as parsed (coerced by the compiler).</summary>
        private static object Value(object value) => value switch
        {
            DateOnly date => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
#pragma warning disable CS0618 // Edm.Date literals are Microsoft.OData.Edm.Date until ODL 9
            Date date => new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc),
#pragma warning restore CS0618
            _ => value,
        };

        /// <summary>Escapes the LIKE wildcards <c>[</c>, <c>%</c>, <c>_</c> so that <c>contains</c> matches literally.</summary>
        private static string EscapeLike(string value) =>
            value.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);

        private XrmdValidationException Unsupported(string reason) =>
            new($"The $filter clause '{text}' is not supported: {reason}.");
    }
}

/// <summary>
/// The <c>$expand</c> of an activity's party rows (<see cref="ODataQueryParser.ReadPartyExpand"/>): the navigation property and the
/// <c>activityparty</c> columns to read (null: all of them).
/// </summary>
public sealed record PartyExpand(string Navigation, IReadOnlyList<string>? Columns);
