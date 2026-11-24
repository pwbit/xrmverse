using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Xrmd.Abstractions;
using Xrmd.Model.Query;
using Xrmd.Query.Model;

namespace Xrmd.Query.FetchXml;

public static class FetchXmlParser
{
    private const int MaxPageSize = 5000;

    private static readonly Dictionary<string, FilterOperator> Operators = new(StringComparer.Ordinal)
    {
        ["eq"] = FilterOperator.Equal,
        ["ne"] = FilterOperator.NotEqual,
        ["gt"] = FilterOperator.GreaterThan,
        ["ge"] = FilterOperator.GreaterEqual,
        ["lt"] = FilterOperator.LessThan,
        ["le"] = FilterOperator.LessEqual,
        ["like"] = FilterOperator.Like,
        ["not-like"] = FilterOperator.NotLike,
        ["in"] = FilterOperator.In,
        ["not-in"] = FilterOperator.NotIn,
        ["null"] = FilterOperator.Null,
        ["not-null"] = FilterOperator.NotNull,
        ["begins-with"] = FilterOperator.BeginsWith,
        ["not-begin-with"] = FilterOperator.DoesNotBeginWith,
        ["ends-with"] = FilterOperator.EndsWith,
        ["not-end-with"] = FilterOperator.DoesNotEndWith,
        ["between"] = FilterOperator.Between,
        ["not-between"] = FilterOperator.NotBetween,
        ["on"] = FilterOperator.On,
        ["on-or-after"] = FilterOperator.OnOrAfter,
        ["on-or-before"] = FilterOperator.OnOrBefore,
        ["last-x-days"] = FilterOperator.LastXDays,
        ["next-x-days"] = FilterOperator.NextXDays,
        ["last-x-months"] = FilterOperator.LastXMonths,
        ["this-month"] = FilterOperator.ThisMonth,
        ["this-year"] = FilterOperator.ThisYear,
        ["eq-userid"] = FilterOperator.EqualUserId,
        ["ne-userid"] = FilterOperator.NotEqualUserId,
        ["eq-businessid"] = FilterOperator.EqualBusinessId,
        ["eq-userteams"] = FilterOperator.EqualUserTeams,
        ["eq-useroruserteams"] = FilterOperator.EqualUserOrUserTeams,
        ["eq-useroruserhierarchy"] = FilterOperator.EqualUserOrUserHierarchy,
        ["eq-useroruserhierarchyandteams"] = FilterOperator.EqualUserOrUserHierarchyAndTeams,
        ["under"] = FilterOperator.Under,
        ["eq-or-under"] = FilterOperator.UnderOrEqual,
        ["above"] = FilterOperator.Above,
        ["eq-or-above"] = FilterOperator.AboveOrEqual,
        ["not-under"] = FilterOperator.NotUnder,
    };

    private static readonly Dictionary<string, XrmAggregate> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["count"] = XrmAggregate.Count,
        ["countcolumn"] = XrmAggregate.CountColumn,
        ["sum"] = XrmAggregate.Sum,
        ["avg"] = XrmAggregate.Avg,
        ["min"] = XrmAggregate.Min,
        ["max"] = XrmAggregate.Max,
    };

    private static readonly Dictionary<string, DateGrouping> DateGroupings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["day"] = DateGrouping.Day,
        ["week"] = DateGrouping.Week,
        ["month"] = DateGrouping.Month,
        ["quarter"] = DateGrouping.Quarter,
        ["year"] = DateGrouping.Year,
    };

    public static QueryModel Parse(string fetchXml)
    {
        if (string.IsNullOrWhiteSpace(fetchXml)) throw new XrmdValidationException("FetchXML is empty.");

        XElement fetch;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StringReader(fetchXml);
            using var xr = XmlReader.Create(sr, settings);
            fetch = XDocument.Load(xr).Root ?? throw new XrmdValidationException("FetchXML has no root element.");
        }
        catch (XmlException ex)
        {
            throw new XrmdValidationException($"Invalid FetchXML: {ex.Message}", ex);
        }

        if (fetch.Name.LocalName != "fetch")
            throw new XrmdValidationException($"FetchXML root must be 'fetch', found '{fetch.Name.LocalName}'.");

        var entities = fetch.Elements().ToList();
        foreach (var e in entities)
            if (e.Name.LocalName != "entity") throw Unknown(e, "fetch");
        if (entities.Count != 1) throw new XrmdValidationException("FetchXML must contain exactly one 'entity' element.");

        var top = OptInt(fetch, "top");
        var count = OptInt(fetch, "count");
        if (top is not null && count is not null)
            throw new XrmdValidationException("FetchXML cannot specify both 'top' and 'count'.");
        if (top is <= 0 or > MaxPageSize) throw new XrmdValidationException($"'top' must be between 1 and {MaxPageSize}.");
        if (count is <= 0 or > MaxPageSize) throw new XrmdValidationException($"'count' must be between 1 and {MaxPageSize}.");
        var page = OptInt(fetch, "page") ?? 1;
        if (page < 1) throw new XrmdValidationException("'page' must be 1 or greater.");

        var root = new EntityNode { EntityName = Req(entities[0], "name") };
        ParseEntityContent(entities[0], root, linkDepth: 0);

        return new QueryModel
        {
            Root = root,
            Top = top,
            PageSize = count,
            PageNumber = page,
            PagingCookie = (string?)fetch.Attribute("paging-cookie"),
            Distinct = Bool(fetch, "distinct"),
            Aggregate = Bool(fetch, "aggregate"),
            ReturnTotalRecordCount = Bool(fetch, "returntotalrecordcount"),
            UseRawOrderBy = Bool(fetch, "useraworderby"),
        };
    }

    /// <param name="linkDepth">0 for the root entity, n for a link-entity nested n levels deep.</param>
    private static void ParseEntityContent(XElement element, EntityNode node, int linkDepth)
    {
        var filters = new List<FilterNode>();
        // Sibling filters are wrapped in one AND filter: they start one level deeper, as the compiler sees them.
        var filterDepth = element.Elements().Count(c => c.Name.LocalName == "filter") > 1 ? 2 : 1;
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "attribute":
                    node.Columns.Add(ParseColumn(child));
                    break;
                case "all-attributes":
                    node.AllColumns = true;
                    break;
                case "filter":
                    filters.Add(ParseFilter(child, filterDepth));
                    break;
                case "link-entity":
                    node.Links.Add(ParseLink(child, linkDepth + 1));
                    break;
                case "order":
                    node.Orders.Add(ParseOrder(child));
                    break;
                default:
                    throw Unknown(child, element.Name.LocalName);
            }
        }

        // Multiple sibling filters are AND-combined into one flat filter.
        if (filters.Count == 1) node.Filter = filters[0];
        else if (filters.Count > 1)
        {
            var and = new FilterNode { Operator = FilterLogic.And };
            and.Filters.AddRange(filters);
            node.Filter = and;
        }
    }

    private static ColumnNode ParseColumn(XElement e)
    {
        if (e.Attribute("rowaggregate") is not null)
            throw new XrmdValidationException("The attribute 'rowaggregate' (CountChildren) is not supported.");
        XrmAggregate? agg = null;
        var aggText = (string?)e.Attribute("aggregate");
        if (aggText is not null)
        {
            if (!Aggregates.TryGetValue(aggText, out var a))
                throw new XrmdValidationException($"Unknown aggregate '{aggText}'.");
            agg = a;
        }

        DateGrouping? dg = null;
        var dgText = (string?)e.Attribute("dategrouping");
        if (dgText is not null)
        {
            if (!DateGroupings.TryGetValue(dgText, out var d))
                throw new XrmdValidationException($"Unknown dategrouping '{dgText}'.");
            dg = d;
        }

        return new ColumnNode(Req(e, "name"), (string?)e.Attribute("alias"), agg, Bool(e, "groupby"), Bool(e, "distinct"), dg);
    }

    private static FilterNode ParseFilter(XElement e, int depth)
    {
        if (depth > QueryLimits.MaxFilterDepth)
            throw new XrmdValidationException($"Filters are nested more than {QueryLimits.MaxFilterDepth} levels deep.");
        var node = new FilterNode();
        var type = (string?)e.Attribute("type");
        if (type is not null)
        {
            node.Operator = type.ToLowerInvariant() switch
            {
                "and" => FilterLogic.And,
                "or" => FilterLogic.Or,
                _ => throw new XrmdValidationException($"Unknown filter type '{type}'."),
            };
        }

        foreach (var child in e.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "condition": node.Conditions.Add(ParseCondition(child)); break;
                case "filter": node.Filters.Add(ParseFilter(child, depth + 1)); break;
                default: throw Unknown(child, "filter");
            }
        }
        return node;
    }

    private static ConditionNode ParseCondition(XElement e)
    {
        var opText = Req(e, "operator");
        if (!Operators.TryGetValue(opText, out var op))
            throw new XrmdValidationException($"Unknown condition operator '{opText}'.");

        var values = new List<object?>();
        var single = (string?)e.Attribute("value");
        if (single is not null) values.Add(single);
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName != "value") throw Unknown(child, "condition");
            values.Add(child.Value);
        }

        return new ConditionNode((string?)e.Attribute("entityname"), Req(e, "attribute"), op, values);
    }

    private static LinkNode ParseLink(XElement e, int depth)
    {
        if (depth > QueryLimits.MaxLinkDepth)
            throw new XrmdValidationException($"Link-entities are nested more than {QueryLimits.MaxLinkDepth} levels deep.");
        var linkType = (string?)e.Attribute("link-type") ?? "inner";
        var join = linkType.ToLowerInvariant() switch
        {
            "inner" => JoinKind.Inner,
            "outer" => JoinKind.LeftOuter,
            _ => throw new XrmdValidationException($"Unsupported link-type '{linkType}'."),
        };
        var link = new LinkNode
        {
            EntityName = Req(e, "name"),
            From = Req(e, "from"),
            To = Req(e, "to"),
            JoinType = join,
            Alias = (string?)e.Attribute("alias"),
        };
        ParseEntityContent(e, link, depth);
        return link;
    }

    private static OrderNode ParseOrder(XElement e)
    {
        var desc = Bool(e, "descending");
        var alias = (string?)e.Attribute("alias");
        if (alias is not null) return new OrderNode(alias, desc, alias);
        return new OrderNode(Req(e, "attribute"), desc);
    }

    private static string Req(XElement e, string name)
    {
        var v = (string?)e.Attribute(name);
        if (string.IsNullOrEmpty(v))
            throw new XrmdValidationException($"Element '{e.Name.LocalName}' requires attribute '{name}'.");
        return v;
    }

    private static int? OptInt(XElement e, string name)
    {
        var v = (string?)e.Attribute(name);
        if (v is null) return null;
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new XrmdValidationException($"Attribute '{name}' must be an integer, found '{v}'.");
        return n;
    }

    private static bool Bool(XElement e, string name)
    {
        var v = (string?)e.Attribute(name);
        if (v is null) return false;
        return v.ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw new XrmdValidationException($"Attribute '{name}' must be true/false/1/0, found '{v}'."),
        };
    }

    private static XrmdValidationException Unknown(XElement e, string parent) =>
        new($"Unsupported FetchXML element '{e.Name.LocalName}' inside '{parent}'.");
}
