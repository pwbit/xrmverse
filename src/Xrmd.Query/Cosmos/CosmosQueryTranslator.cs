using System.Globalization;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Cosmos;

/// <summary>One result property: <paramref name="JsonSteps"/> is empty for a whole column, else the steps inside the JSON column.</summary>
public sealed record CosmosProjection(string Name, string Attribute, IReadOnlyList<CosmosPathStep> JsonSteps);

/// <summary>The query model to run and how to shape each row of its result.</summary>
public sealed record CosmosQueryPlan(QueryModel Model, IReadOnlyList<CosmosProjection> Projection);

/// <summary>
/// Binds a parsed Cosmos SQL query to an elastic table and its parameters (plan #78, rulings J1 to J8) and produces an
/// ordinary <see cref="QueryModel"/>: top-level columns become <see cref="ConditionNode"/>s, paths inside a JSON column become
/// <see cref="JsonPathConditionNode"/>s. <c>NOT</c> is pushed down to the comparisons first.
/// </summary>
public static class CosmosQueryTranslator
{
    
    private static XrmdFaultException Unsupported(string construct) =>
        XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: {construct} is not supported by xrmverse.");

    /// <param name="parameters">Normalized: keys with the leading <c>@</c>, values of a ruling J6 type.</param>
    /// <exception cref="XrmdFaultException">400 for every construct, value or cookie that cannot be translated.</exception>
    public static CosmosQueryPlan Translate(CosmosQuery query, EntityMetadata table, IReadOnlyDictionary<string, object?> parameters,
        int? pageSize, string? pagingCookie)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(parameters);
        if (pageSize is < 1 or > SqlQueryCompiler.MaxPageSize)
            throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: PageSize must be between 1 and {SqlQueryCompiler.MaxPageSize}.");

        var binder = new Binder(table, parameters);
        var primaryId = SystemAttributes.PrimaryIdName(table);
        var root = new EntityNode { EntityName = table.LogicalName };
        root.Columns.Add(new ColumnNode(primaryId));

        var projection = new List<CosmosProjection>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var unnamed = 0;
        foreach (var item in query.Select)
        {
            var (column, rest) = binder.Resolve(item.Path);
            var name = item.Alias ?? item.Path.Steps[^1] switch
            {
                PropertyStep p => p.Name,
                _ => "$" + (++unnamed).ToString(CultureInfo.InvariantCulture),
            };
            if (name.StartsWith('@'))
                throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: the result name '{name}' is reserved.");
            if (!names.Add(name)) throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: duplicate result name '{name}'.");
            if (root.Columns.All(c => c.Attribute != column.LogicalName)) root.Columns.Add(new ColumnNode(column.LogicalName));
            projection.Add(new CosmosProjection(name, column.LogicalName, rest));
        }

        if (query.Where is not null)
        {
            var ir = Build(query.Where, false, binder);
            if (IrDepth(ir) > QueryLimits.MaxFilterDepth)
                throw new XrmdValidationException($"Filters are nested more than {QueryLimits.MaxFilterDepth} levels deep.");
            Fill(root.Filter, ir);
        }

        if (query.OrderBy is { } order)
        {
            var (column, rest) = binder.Resolve(order.Path);
            if (rest.Count > 0 || JsonPaths.IsJson(column)) throw Unsupported("ORDER BY on a JSON path");
            if (column.LogicalName != primaryId) root.Orders.Add(new OrderNode(column.LogicalName, order.Descending));
            else if (order.Descending) root.Orders.Add(new OrderNode(primaryId, true));
        }
        if (root.Orders.All(o => o.Attribute != primaryId)) root.Orders.Add(new OrderNode(primaryId, false));

        var model = new QueryModel { Root = root, PageSize = pageSize };
        if (!string.IsNullOrEmpty(pagingCookie))
        {
            var cookie = CosmosPagingCookie.Decode(pagingCookie);
            model.PageNumber = cookie.Page + 1;
            model.PagingCookie = cookie.Format();
        }
        return new CosmosQueryPlan(model, projection);
    }

    // ---- filter intermediate form -----------------------------------------------------------------------------------------

    private abstract record Ir;

    private sealed record Leaf(ConditionNode? Condition, JsonPathConditionNode? Json) : Ir;

    private sealed record Group(LogicalOperator Operator, List<Ir> Items) : Ir;

    /// <summary>Pushes <paramref name="negated"/> down (De Morgan, operator negation) and flattens same-operator chains.</summary>
    private static Ir Build(CosmosExpr expr, bool negated, Binder binder)
    {
        switch (expr)
        {
            case Not n:
                return Build(n.Inner, !negated, binder);
            case And or Or:
            {
                var op = (expr is And) != negated ? LogicalOperator.And : LogicalOperator.Or;
                var items = new List<Ir>();
                Collect(expr, negated, op, items, binder);
                return new Group(op, items);
            }
            case Comparison c:
                return binder.Compare(c, negated);
            case IsDefined d:
                return binder.Defined(d.Path, negated);
            default:
                throw new InvalidOperationException($"Unknown expression {expr.GetType().Name}.");
        }
    }

    private static void Collect(CosmosExpr expr, bool negated, LogicalOperator op, List<Ir> items, Binder binder)
    {
        // Iterative along the left spine: a chain of 1000 connectives is left-nested.
        var stack = new Stack<(CosmosExpr Expr, bool Negated)>();
        stack.Push((expr, negated));
        while (stack.Count > 0)
        {
            var (e, neg) = stack.Pop();
            var inner = e;
            var n = neg;
            while (inner is Not not) { inner = not.Inner; n = !n; }
            if (inner is And or Or && ((inner is And) != n ? LogicalOperator.And : LogicalOperator.Or) == op)
            {
                var (left, right) = inner is And a ? (a.Left, a.Right) : (((Or)inner).Left, ((Or)inner).Right);
                stack.Push((right, n));
                stack.Push((left, n));
            }
            else items.Add(Build(inner, n, binder));
        }
    }

    private static int IrDepth(Ir ir) => ir is Group g ? 1 + g.Items.Max(IrDepth) : 1;

    private static void Fill(FilterNode filter, Ir ir)
    {
        switch (ir)
        {
            case Leaf leaf:
                Add(filter, leaf);
                break;
            case Group g:
                filter.Operator = g.Operator;
                foreach (var item in g.Items) AddChild(filter, item);
                break;
        }
    }

    private static void AddChild(FilterNode filter, Ir item)
    {
        if (item is Leaf leaf) Add(filter, leaf);
        else
        {
            var child = new FilterNode();
            Fill(child, item);
            filter.Filters.Add(child);
        }
    }

    private static void Add(FilterNode filter, Leaf leaf)
    {
        if (leaf.Condition is not null) filter.Conditions.Add(leaf.Condition);
        if (leaf.Json is not null) filter.JsonPaths.Add(leaf.Json);
    }

    // ---- binding to the table ---------------------------------------------------------------------------------------------

    private sealed class Binder(EntityMetadata table, IReadOnlyDictionary<string, object?> parameters)
    {
        /// <summary>The column a path starts at and the steps that continue into it (only for a JSON column).</summary>
        public (AttributeMetadata Column, IReadOnlyList<CosmosPathStep> Steps) Resolve(CosmosPath path)
        {
            var steps = path.Steps;
            if (steps.Count < 2 || steps[0] is not PropertyStep { Name: "props" } || steps[1] is not PropertyStep columnStep)
                throw Unsupported("a property outside c.props");
            var column = table.Attributes.FirstOrDefault(a =>
                    string.Equals(a.LogicalName, columnStep.Name, StringComparison.OrdinalIgnoreCase) && TypeMapping.IsReadable(a))
                ?? throw new XrmdValidationException(
                    $"'{table.LogicalName}' entity doesn't contain attribute with Name = '{columnStep.Name}'.");
            var rest = steps.Skip(2).ToList();
            if (rest.Count > 0 && !JsonPaths.IsJson(column))
                throw Unsupported($"a property path into the non-JSON column '{column.LogicalName}'");
            return (column, rest);
        }

        public Ir Defined(CosmosPath path, bool negated)
        {
            var (column, rest) = Resolve(path);
            if (rest.Count == 0)
                return new Leaf(new ConditionNode(null, column.LogicalName, negated ? ConditionOperator.Null : ConditionOperator.NotNull, []), null);
            return new Leaf(null, Json(column, rest, negated ? JsonPathTest.NotDefined : JsonPathTest.Defined, null));
        }

        public Ir Compare(Comparison c, bool negated)
        {
            CosmosPath path;
            CosmosOperand value;
            var op = c.Op;
            switch (c.Left, c.Right)
            {
                case (PathOperand l, PathOperand):
                    throw Unsupported("a comparison between two properties");
                case (PathOperand l, var r):
                    (path, value) = (l.Path, r);
                    break;
                case (var l, PathOperand r):
                    (path, value) = (r.Path, l);
                    op = Flip(op);
                    break;
                default:
                    throw new InvalidOperationException("The parser accepts no comparison without a property.");
            }
            if (negated) op = Negate(op);
            var literal = ValueOf(value);
            var (column, rest) = Resolve(path);
            return rest.Count > 0 ? JsonCompare(column, rest, op, literal) : ColumnCompare(column, op, literal, negated);
        }

        private Ir ColumnCompare(AttributeMetadata column, CosmosComparisonOp op, object? literal, bool negated)
        {
            if (JsonPaths.IsJson(column)) throw Unsupported($"a comparison of the whole JSON column '{column.LogicalName}'");
            var binary = column is StringAttributeMetadata or MemoAttributeMetadata;
            if (literal is null)
            {
                return op switch
                {
                    CosmosComparisonOp.NotEqual => new Leaf(new ConditionNode(null, column.LogicalName, ConditionOperator.NotNull, [], binary), null),
                    CosmosComparisonOp.Equal when negated => throw Unsupported("comparing a column with null (use IS_DEFINED or NOT IS_DEFINED)"),
                    CosmosComparisonOp.Equal => throw Unsupported("a comparison of a column with null (use NOT IS_DEFINED)"),
                    _ => throw Unsupported("a range comparison with null"),
                };
            }
            RequireMatchingType(column, literal);
            var cop = op switch
            {
                CosmosComparisonOp.Equal => ConditionOperator.Equal,
                CosmosComparisonOp.NotEqual => ConditionOperator.NotEqual,
                CosmosComparisonOp.Less => ConditionOperator.LessThan,
                CosmosComparisonOp.LessEqual => ConditionOperator.LessEqual,
                CosmosComparisonOp.Greater => ConditionOperator.GreaterThan,
                _ => ConditionOperator.GreaterEqual,
            };
            return new Leaf(new ConditionNode(null, column.LogicalName, cop, [literal], binary), null);
        }

        /// <summary>Type-strict literals on text and number columns; other column types are left to the value coercion.</summary>
        private static void RequireMatchingType(AttributeMetadata column, object literal)
        {
            if (column is StringAttributeMetadata or MemoAttributeMetadata)
            {
                if (literal is not string)
                    throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: the value for '{column.LogicalName}' must be a string.");
            }
            else if (column is IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata or DoubleAttributeMetadata
                     or MoneyAttributeMetadata && literal is string or bool)
            {
                throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: the value for '{column.LogicalName}' must be a number.");
            }
        }

        private Ir JsonCompare(AttributeMetadata column, IReadOnlyList<CosmosPathStep> rest, CosmosComparisonOp op, object? literal)
        {
            var test = op switch
            {
                CosmosComparisonOp.Equal => JsonPathTest.Equal,
                CosmosComparisonOp.NotEqual => JsonPathTest.NotEqual,
                CosmosComparisonOp.Less => JsonPathTest.Less,
                CosmosComparisonOp.LessEqual => JsonPathTest.LessEqual,
                CosmosComparisonOp.Greater => JsonPathTest.Greater,
                _ => JsonPathTest.GreaterEqual,
            };
            object? value = literal switch
            {
                null => null,
                string s => s,
                bool b => b,
                _ => Convert.ToDouble(literal, CultureInfo.InvariantCulture),
            };
            if (test is not (JsonPathTest.Equal or JsonPathTest.NotEqual) && value is null or bool)
                throw Unsupported($"a range comparison with {(value is null ? "null" : "bool")}");
            return new Leaf(null, Json(column, rest, test, value));
        }

        private static JsonPathConditionNode Json(AttributeMetadata column, IReadOnlyList<CosmosPathStep> rest, JsonPathTest test, object? value)
        {
            var parent = JsonPaths.Format(rest.Take(rest.Count - 1));
            return rest[^1] switch
            {
                IndexStep i => new JsonPathConditionNode(column.LogicalName, parent, i.Index.ToString(CultureInfo.InvariantCulture), test, value, true),
                PropertyStep p => new JsonPathConditionNode(column.LogicalName, parent, p.Name, test, value),
                _ => throw new InvalidOperationException("Unknown path step."),
            };
        }

        private object? ValueOf(CosmosOperand operand) => operand switch
        {
            LiteralOperand l => l.Value,
            ParameterOperand p => parameters.TryGetValue(p.Name, out var v)
                ? v ?? throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: the parameter '{p.Name}' has no value.")
                : throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: the parameter '{p.Name}' has no value."),
            _ => throw new InvalidOperationException("Unknown operand."),
        };

        private static CosmosComparisonOp Flip(CosmosComparisonOp op) => op switch
        {
            CosmosComparisonOp.Less => CosmosComparisonOp.Greater,
            CosmosComparisonOp.LessEqual => CosmosComparisonOp.GreaterEqual,
            CosmosComparisonOp.Greater => CosmosComparisonOp.Less,
            CosmosComparisonOp.GreaterEqual => CosmosComparisonOp.LessEqual,
            _ => op,
        };

        private static CosmosComparisonOp Negate(CosmosComparisonOp op) => op switch
        {
            CosmosComparisonOp.Equal => CosmosComparisonOp.NotEqual,
            CosmosComparisonOp.NotEqual => CosmosComparisonOp.Equal,
            CosmosComparisonOp.Less => CosmosComparisonOp.GreaterEqual,
            CosmosComparisonOp.LessEqual => CosmosComparisonOp.Greater,
            CosmosComparisonOp.Greater => CosmosComparisonOp.LessEqual,
            _ => CosmosComparisonOp.Less,
        };
    }
}
