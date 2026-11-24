using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Query;
using Xrmd.Query.Model;

namespace Xrmd.Tds.Translation;

/// <summary>
/// Translates the SQL of a batch into a <see cref="QueryModel"/> (plan 3.3, rulings T1, T6, T7 and T9). The SQL text is parsed
/// by ScriptDom and only ever read: nothing of it reaches a database.
/// <para>
/// The walk is a whitelist. Each statement, clause, expression and predicate the subset supports is handled explicitly; any
/// other node type is refused with error 40000 naming the construct. After the walk, every node of the syntax tree must have
/// been read by the translator: a node that was not (a clause of a supported node that the translator does not know, for
/// example) is refused the same way, so a construct the translator overlooks can never be silently dropped.
/// </para>
/// </summary>
public sealed class SqlTranslator(IMetadataStore store)
{
    /// <summary>The <c>SERVERPROPERTY</c> names answered (ruling T9), in their canonical case.</summary>
    public static readonly IReadOnlyList<string> ServerProperties = ["ProductVersion", "Edition", "EngineEdition", "ProductLevel"];

    /// <summary>The columns of <c>INFORMATION_SCHEMA.TABLES</c>, in SQL Server's order.</summary>
    public static readonly IReadOnlyList<string> InformationSchemaTablesColumns = ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE"];

    /// <summary>The columns of <c>INFORMATION_SCHEMA.COLUMNS</c>, in SQL Server's order.</summary>
    public static readonly IReadOnlyList<string> InformationSchemaColumnsColumns =
    [
        "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "COLUMN_DEFAULT", "IS_NULLABLE",
        "DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "CHARACTER_OCTET_LENGTH", "NUMERIC_PRECISION", "NUMERIC_PRECISION_RADIX",
        "NUMERIC_SCALE", "DATETIME_PRECISION", "CHARACTER_SET_CATALOG", "CHARACTER_SET_SCHEMA", "CHARACTER_SET_NAME",
        "COLLATION_CATALOG", "COLLATION_SCHEMA", "COLLATION_NAME", "DOMAIN_CATALOG", "DOMAIN_SCHEMA", "DOMAIN_NAME",
    ];

    /// <summary>The suffix of the virtual columns that hold a lookup's name or a choice's label (ruling T7).</summary>
    public const string NameSuffix = "name";

    private const string DefaultSchema = "dbo";
    private const string InformationSchema = "INFORMATION_SCHEMA";

    /// <summary>Translates one batch.</summary>
    /// <param name="sql">The batch text.</param>
    /// <param name="parameters">The values of the batch's <c>@parameters</c> (<c>sp_executesql</c>), by name with or without
    /// the leading <c>@</c>; names ignore case, as SQL Server variable names do.</param>
    /// <exception cref="TdsSqlException">The batch cannot be parsed, names an unknown table or column, or uses a construct
    /// the endpoint does not support.</exception>
    public TranslatedBatch Translate(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
        {
            var error = errors[0];
            throw new TdsSqlException(TdsSqlErrors.Syntax,
                $"The SQL text has a syntax error at line {error.Line}, column {error.Column}.");
        }
        if (fragment is not TSqlScript script) throw TdsSqlErrors.NotSupported(Describe(fragment));

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.FirstOrDefault(IsFmtOnly) is { } fmtOnly) throw TdsSqlErrors.NotSupported(Describe(fmtOnly));
        // SET options are accepted and ignored (ruling T6); every other statement counts, and a batch may hold only one.
        var others = statements.Where(s => !IsIgnoredSet(s)).ToList();
        if (others.Count > 1) throw TdsSqlErrors.NotSupported("multiple statements in one batch");
        if (others.Count == 0) return NoOp.Instance;
        return new Walker(store, parameters).Statement(others[0]);
    }

    /// <summary>The SET statements that change only session options the endpoint does not have; they are ignored.</summary>
    private static bool IsIgnoredSet(TSqlStatement statement) => statement switch
    {
        // SET FMTONLY ON asks for the result shape without running the query: ignoring it would run the query instead.
        PredicateSetStatement p when IsFmtOnly(p) => false,
        PredicateSetStatement or SetTransactionIsolationLevelStatement or SetTextSizeStatement or SetStatisticsStatement => true,
        // SET ROWCOUNT n would cut the results: only 0 (no limit) is ignored.
        SetRowCountStatement { NumberRows: IntegerLiteral { Value: "0" } } => true,
        // DATEFORMAT, DATEFIRST, LANGUAGE, DEADLOCK_PRIORITY, LOCK_TIMEOUT and the like.
        SetCommandStatement s => s.Commands.All(c => c is GeneralSetCommand),
        _ => false,
    };

    private static bool IsFmtOnly(TSqlStatement statement) =>
        // SET FMTONLY OFF is the session default: it changes nothing and is ignored like the other SET options.
        statement is PredicateSetStatement p && p.Options.HasFlag(SetOptions.FmtOnly) && p.IsOn;

    /// <summary>The construct a node stands for, as the message of error 40000 names it.</summary>
    internal static string Describe(TSqlFragment fragment) => fragment switch
    {
        ExecuteStatement or ExecuteSpecification or ExecutableProcedureReference => "EXEC statements",
        SetVariableStatement => "SET with a variable",
        PredicateSetStatement p when IsFmtOnly(p) => "SET FMTONLY ON",
        SetRowCountStatement => "SET ROWCOUNT other than 0",
        ScalarSubquery or QueryDerivedTable or ExistsPredicate or SubqueryComparisonPredicate => "subqueries",
        WithCtesAndXmlNamespaces or CommonTableExpression => "common table expressions (WITH)",
        BinaryQueryExpression b => b.BinaryQueryExpressionType.ToString().ToUpperInvariant(),
        QueryParenthesisExpression => "queries in parentheses",
        HavingClause => "HAVING",
        OffsetClause => "OFFSET and FETCH",
        ForClause => "FOR XML, FOR JSON and FOR BROWSE",
        UnqualifiedJoin { UnqualifiedJoinType: UnqualifiedJoinType.CrossJoin } => "CROSS JOIN",
        UnqualifiedJoin => "CROSS APPLY and OUTER APPLY",
        JoinParenthesisTableReference => "joins in parentheses",
        PivotedTableReference => "PIVOT",
        UnpivotedTableReference => "UNPIVOT",
        TableHint => "table hints",
        OptimizerHint => "query hints (OPTION)",
        OverClause => "window functions (OVER)",
        FunctionCall f => $"the function {f.FunctionName.Value.ToUpperInvariant()}",
        CaseExpression => "CASE expressions",
        CastCall or ConvertCall or TryCastCall or TryConvertCall => "CAST and CONVERT",
        GlobalVariableExpression g => g.Name.ToUpperInvariant(),
        TSqlStatement s => Words(s.GetType().Name, "Statement").ToUpperInvariant() + " statements",
        _ => Words(fragment.GetType().Name, null).ToLowerInvariant(),
    };

    /// <summary><c>DropTableStatement</c> → <c>Drop Table</c> (without <paramref name="suffix"/>).</summary>
    private static string Words(string typeName, string? suffix)
    {
        if (suffix is not null && typeName.EndsWith(suffix, StringComparison.Ordinal) && typeName.Length > suffix.Length)
            typeName = typeName[..^suffix.Length];
        var sb = new StringBuilder();
        for (var i = 0; i < typeName.Length; i++)
        {
            if (i > 0 && char.IsUpper(typeName[i]) && !char.IsUpper(typeName[i - 1])) sb.Append(' ');
            sb.Append(typeName[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The attributes the SQL endpoint exposes (ruling T7): readable columns of the types Dataverse's SQL endpoint returns.
    /// File, image, multiselect, party list, virtual and other types are not (they give "Invalid column name").
    /// </summary>
    internal static bool IsExposed(AttributeMetadata a) =>
        a.IsValidForRead != false
        // The companions of file and image columns (file name, image id, timestamp and url) go with their column, which is
        // not exposed either.
        && a.AttributeOf is null
        && a.AttributeType != AttributeTypeCode.Virtual
        && TypeMapping.IsReadable(a)
        && a switch
        {
            StringAttributeMetadata or MemoAttributeMetadata or IntegerAttributeMetadata or BigIntAttributeMetadata
                or DecimalAttributeMetadata or DoubleAttributeMetadata or MoneyAttributeMetadata or BooleanAttributeMetadata
                or DateTimeAttributeMetadata or UniqueIdentifierAttributeMetadata
                or PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata => true,
            LookupAttributeMetadata => a.AttributeType != AttributeTypeCode.PartyList,
            _ => false,
        };

    /// <summary>A table of the <c>FROM</c> clause.</summary>
    /// <param name="Exposed">The alias, or the table's logical name when it has none.</param>
    private sealed record Table(string Exposed, EntityMetadata Entity, EntityNode Node)
    {
        public bool IsRoot => Node is not LinkNode;
    }

    /// <summary>A resolved column: an attribute of a table and what is read of it.</summary>
    private sealed record Column(Table Table, AttributeMetadata Attribute, OutputValue Value, string Written)
    {
        /// <summary>The name of the result column: the logical name, plus <c>name</c> for a name or label.</summary>
        public string LogicalName => Value == OutputValue.Raw ? Attribute.LogicalName : Attribute.LogicalName + NameSuffix;
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        public List<TSqlFragment> Nodes { get; } = [];
        public override void Visit(TSqlFragment node) => Nodes.Add(node);
    }

    /// <summary>The translation of one statement.</summary>
    private sealed class Walker
    {
        private readonly IMetadataStore _store;
        private readonly Dictionary<string, object?> _parameters = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<TSqlFragment> _read = new(ReferenceEqualityComparer.Instance);
        private readonly List<Table> _tables = [];
        private readonly HashSet<string> _modelAliases = new(StringComparer.Ordinal);

        public Walker(IMetadataStore store, IReadOnlyDictionary<string, object?> parameters)
        {
            _store = store;
            foreach (var (name, value) in parameters)
            {
                var key = name.StartsWith('@') ? name[1..] : name;
                if (_parameters.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) is { } existing)
                    throw TdsSqlErrors.NotSupported($"two parameters with the same name (\"@{existing}\" and \"@{key}\")");
                _parameters.Add(key, value is DBNull ? null : value);
            }
        }

        public TranslatedBatch Statement(TSqlStatement statement)
        {
            if (statement is not SelectStatement select) throw TdsSqlErrors.NotSupported(Describe(statement));
            Read(select);
            if (select.WithCtesAndXmlNamespaces is not null) throw TdsSqlErrors.NotSupported(Describe(select.WithCtesAndXmlNamespaces));
            if (select.Into is not null || select.On is not null) throw TdsSqlErrors.NotSupported("SELECT INTO");
            if (select.ComputeClauses.Count > 0) throw TdsSqlErrors.NotSupported("COMPUTE");
            if (select.OptimizerHints.Count > 0) throw TdsSqlErrors.NotSupported(Describe(select.OptimizerHints[0]));
            if (select.QueryExpression is not QuerySpecification spec) throw TdsSqlErrors.NotSupported(Describe(select.QueryExpression));
            Read(spec);
            if (spec.ForClause is not null) throw TdsSqlErrors.NotSupported(Describe(spec.ForClause));
            if (spec.OffsetClause is not null) throw TdsSqlErrors.NotSupported(Describe(spec.OffsetClause));
            if (spec.HavingClause is not null) throw TdsSqlErrors.NotSupported(Describe(spec.HavingClause));
            if (spec.WindowClause is not null) throw TdsSqlErrors.NotSupported("WINDOW");

            TranslatedBatch result;
            if (spec.FromClause is null) result = SystemScalar(spec);
            else if (spec.FromClause.TableReferences is [NamedTableReference { SchemaObject.SchemaIdentifier: { } schema } view]
                     && string.Equals(schema.Value, InformationSchema, StringComparison.OrdinalIgnoreCase))
                result = InformationSchemaQuery(spec, view);
            else result = Select(spec);

            CheckEverythingRead(select);
            return result;
        }

        // ---- System queries (ruling T9) ----

        private SystemQuery SystemScalar(QuerySpecification spec)
        {
            if (spec.WhereClause is not null || spec.GroupByClause is not null || spec.OrderByClause is not null
                || spec.TopRowFilter is not null || spec.UniqueRowFilter == UniqueRowFilter.Distinct)
                throw TdsSqlErrors.NotSupported("SELECT without FROM, other than the system queries");
            if (spec.SelectElements is not [SelectScalarExpression element])
                throw TdsSqlErrors.NotSupported("SELECT without FROM, other than the system queries");
            Read(element);
            var name = ColumnAlias(element) ?? "";
            var expression = element.Expression;
            switch (expression)
            {
                case GlobalVariableExpression g:
                    Read(g);
                    return g.Name.ToUpperInvariant() switch
                    {
                        "@@VERSION" => new SystemQuery(SystemQueryKind.Version, null, [name]),
                        "@@SPID" => new SystemQuery(SystemQueryKind.Spid, null, [name]),
                        _ => throw TdsSqlErrors.NotSupported(Describe(g)),
                    };
                case FunctionCall f when PlainFunction(f, "SERVERPROPERTY") && f.Parameters is [StringLiteral property]:
                    ReadFunction(f);
                    Read(property);
                    var canonical = ServerProperties.FirstOrDefault(p => string.Equals(p, property.Value, StringComparison.OrdinalIgnoreCase))
                                    ?? throw TdsSqlErrors.NotSupported($"SERVERPROPERTY('{property.Value}')");
                    return new SystemQuery(SystemQueryKind.ServerProperty, canonical, [name]);
                case FunctionCall f when PlainFunction(f, "DB_NAME") && f.Parameters.Count == 0:
                    ReadFunction(f);
                    return new SystemQuery(SystemQueryKind.DatabaseName, null, [name]);
                case FunctionCall f:
                    throw TdsSqlErrors.NotSupported(Describe(f));
                default:
                    throw TdsSqlErrors.NotSupported("SELECT without FROM, other than the system queries");
            }
        }

        private SystemQuery InformationSchemaQuery(QuerySpecification spec, NamedTableReference view)
        {
            Read(spec.FromClause);
            Read(view);
            var objectName = view.SchemaObject;
            if (objectName.ServerIdentifier is not null || objectName.DatabaseIdentifier is not null)
                throw TdsSqlErrors.NotSupported("names with a server or database");
            if (view.TableHints.Count > 0) throw TdsSqlErrors.NotSupported(Describe(view.TableHints[0]));
            ReadAll(objectName);
            if (view.Alias is not null) Read(view.Alias);
            var (kind, columns) = objectName.BaseIdentifier.Value.ToUpperInvariant() switch
            {
                "TABLES" => (SystemQueryKind.Tables, InformationSchemaTablesColumns),
                "COLUMNS" => (SystemQueryKind.Columns, InformationSchemaColumnsColumns),
                _ => throw TdsSqlErrors.NotSupported($"the view {InformationSchema}.{objectName.BaseIdentifier.Value}"),
            };
            if (spec.GroupByClause is not null || spec.OrderByClause is not null || spec.TopRowFilter is not null
                || spec.UniqueRowFilter == UniqueRowFilter.Distinct)
                throw TdsSqlErrors.NotSupported($"GROUP BY, ORDER BY, TOP and DISTINCT on {InformationSchema} views");

            string ViewColumn(ColumnReferenceExpression c)
            {
                var ids = c.MultiPartIdentifier?.Identifiers;
                if (c.ColumnType != ColumnType.Regular || c.Collation is not null || ids is null || ids.Count is < 1 or > 2
                    || ids.Count == 2 && !string.Equals(ids[0].Value, view.Alias?.Value ?? objectName.BaseIdentifier.Value,
                        StringComparison.OrdinalIgnoreCase))
                    throw TdsSqlErrors.NotSupported($"this column reference on {InformationSchema} views");
                ReadAll(c);
                var written = ids[^1].Value;
                return columns.FirstOrDefault(n => string.Equals(n, written, StringComparison.OrdinalIgnoreCase))
                       ?? throw TdsSqlErrors.InvalidColumnName(written);
            }

            List<string>? selected = null;
            if (spec.SelectElements is [SelectStarExpression { Qualifier: null } star]) Read(star);
            else
            {
                selected = [];
                foreach (var element in spec.SelectElements)
                {
                    if (element is not SelectScalarExpression { Expression: ColumnReferenceExpression c, ColumnName: null } scalar)
                        throw TdsSqlErrors.NotSupported($"expressions, aliases and * among columns on {InformationSchema} views");
                    Read(scalar);
                    selected.Add(ViewColumn(c));
                }
            }

            string? tableName = null;
            if (spec.WhereClause is { } where)
            {
                Read(where);
                var condition = Unparenthesize(where.SearchCondition);
                if (where.Cursor is not null || condition is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } eq)
                    throw TdsSqlErrors.NotSupported($"filters on {InformationSchema} views other than TABLE_NAME = <value>");
                Read(eq);
                var (column, value) = eq.FirstExpression is ColumnReferenceExpression c1 ? (c1, eq.SecondExpression)
                    : eq.SecondExpression is ColumnReferenceExpression c2 ? (c2, eq.FirstExpression)
                    : throw TdsSqlErrors.NotSupported($"filters on {InformationSchema} views other than TABLE_NAME = <value>");
                if (ViewColumn(column) != "TABLE_NAME")
                    throw TdsSqlErrors.NotSupported($"filters on {InformationSchema} views other than TABLE_NAME = <value>");
                tableName = Value(value) as string
                            ?? throw TdsSqlErrors.NotSupported($"a TABLE_NAME filter that is not text");
            }
            return new SystemQuery(kind, tableName, selected);
        }

        // ---- SELECT over tables ----

        private TranslatedQuery Select(QuerySpecification spec)
        {
            Read(spec.FromClause);
            if (spec.FromClause.TableReferences.Count != 1)
                throw TdsSqlErrors.NotSupported("several tables separated by commas in FROM (a cross join)");
            AddTables(spec.FromClause.TableReferences[0]);
            var root = _tables[0];

            var aggregate = spec.GroupByClause is not null
                            || spec.SelectElements.Any(e => e is SelectScalarExpression { Expression: FunctionCall f } && IsAggregate(f));
            var model = new QueryModel
            {
                Root = root.Node, Aggregate = aggregate, Distinct = spec.UniqueRowFilter == UniqueRowFilter.Distinct,
                OrderByStoredValues = true, // SQL ORDER BY: a lookup by its id, a choice by its value
            };
            if (spec.TopRowFilter is { } top) model.Top = Top(top);

            if (spec.WhereClause is { } where)
            {
                Read(where);
                if (where.Cursor is not null) throw TdsSqlErrors.NotSupported("WHERE CURRENT OF");
                AddPredicate(root.Node.Filter, where.SearchCondition, negate: false);
            }

            var outputs = new List<OutputColumn>();
            var groupKeys = new List<(Column Column, ColumnNode Node)>();
            if (aggregate) AggregateProjection(spec, outputs, groupKeys);
            else Projection(spec, outputs);

            if (spec.OrderByClause is { } orderBy) AddOrders(orderBy, outputs, groupKeys, aggregate);
            return new TranslatedQuery(model, outputs);
        }

        private int Top(TopRowFilter top)
        {
            Read(top);
            if (top.Percent) throw TdsSqlErrors.NotSupported("TOP … PERCENT");
            if (top.WithTies) throw TdsSqlErrors.NotSupported("TOP … WITH TIES");
            var expression = UnparenthesizeScalar(top.Expression);
            object? value;
            if (expression is IntegerLiteral literal) value = Value(literal);
            else if (expression is VariableReference variable)
            {
                value = Parameter(variable)
                        ?? throw TdsSqlErrors.NotSupported($"a NULL value for the TOP parameter \"{variable.Name}\"");
            }
            else throw TdsSqlErrors.NotSupported("TOP with an expression");
            // Integral CLR types only: TOP never rounds a decimal or parses a string.
            long? n = value switch
            {
                int i => i,
                long l => l,
                short sh => sh,
                byte b => b,
                _ => null,
            };
            return n is >= 0 and <= int.MaxValue ? (int)n.Value : throw TdsSqlErrors.NotSupported("this TOP value");
        }

        private void AddTables(TableReference reference)
        {
            switch (reference)
            {
                case NamedTableReference named when _tables.Count == 0:
                {
                    var (entity, exposed) = ResolveTable(named);
                    _tables.Add(new Table(exposed, entity, new EntityNode { EntityName = entity.LogicalName }));
                    break;
                }
                case QualifiedJoin join:
                {
                    Read(join);
                    if (join.JoinHint != JoinHint.None) throw TdsSqlErrors.NotSupported("join hints");
                    var joinType = join.QualifiedJoinType switch
                    {
                        QualifiedJoinType.Inner => JoinKind.Inner,
                        QualifiedJoinType.LeftOuter => JoinKind.LeftOuter,
                        _ => throw TdsSqlErrors.NotSupported("RIGHT and FULL joins"),
                    };
                    AddTables(join.FirstTableReference);
                    if (join.SecondTableReference is not NamedTableReference named)
                        throw TdsSqlErrors.NotSupported(join.SecondTableReference is QualifiedJoin
                            ? "joins in parentheses" : Describe(join.SecondTableReference));
                    AddJoin(named, joinType, join.SearchCondition);
                    break;
                }
                default:
                    throw TdsSqlErrors.NotSupported(Describe(reference));
            }
        }

        private (EntityMetadata Entity, string Exposed) ResolveTable(NamedTableReference named)
        {
            Read(named);
            var name = named.SchemaObject;
            var written = string.Join('.', name.Identifiers.Select(i => i.Value));
            if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
                throw TdsSqlErrors.NotSupported($"names with a server or database ('{written}'); use dbo.<table>");
            if (named.TableHints.Count > 0) throw TdsSqlErrors.NotSupported(Describe(named.TableHints[0]));
            if (named.TableSampleClause is not null) throw TdsSqlErrors.NotSupported("TABLESAMPLE");
            if (named.TemporalClause is not null) throw TdsSqlErrors.NotSupported("FOR SYSTEM_TIME");
            if (name.SchemaIdentifier is { } schema && !string.Equals(schema.Value, DefaultSchema, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(schema.Value, "sys", StringComparison.OrdinalIgnoreCase))
                    throw TdsSqlErrors.NotSupported($"the system catalog view {written}");
                if (string.Equals(schema.Value, InformationSchema, StringComparison.OrdinalIgnoreCase))
                    throw TdsSqlErrors.NotSupported($"{InformationSchema} views in a join");
                throw TdsSqlErrors.InvalidObjectName(written);
            }
            ReadAll(name);
            var entity = _store.GetEntity(name.BaseIdentifier.Value.ToLowerInvariant())
                         ?? throw TdsSqlErrors.InvalidObjectName(written);
            string exposed;
            if (named.Alias is { } alias)
            {
                Read(alias);
                exposed = alias.Value;
            }
            else exposed = entity.LogicalName;
            if (_tables.Any(t => string.Equals(t.Exposed, exposed, StringComparison.OrdinalIgnoreCase)))
                throw new TdsSqlException(TdsSqlErrors.DuplicateExposedName,
                    $"The query names two tables '{exposed}': give them different aliases.");
            return (entity, exposed);
        }

        /// <summary><c>JOIN t ON a.lookup = t.primarykey</c> or <c>ON t.lookup = a.primarykey</c>, in either order.</summary>
        private void AddJoin(NamedTableReference named, JoinKind joinType, BooleanExpression on)
        {
            var (entity, exposed) = ResolveTable(named);
            var link = new LinkNode { EntityName = entity.LogicalName, From = "", To = "", JoinType = joinType, Alias = exposed };
            var table = new Table(exposed, entity, link);
            _tables.Add(table);

            const string onlyLookups = "join conditions other than <lookup column> = <primary key column>";
            if (Unparenthesize(on) is not BooleanComparisonExpression
                {
                    ComparisonType: BooleanComparisonType.Equals,
                    FirstExpression: ColumnReferenceExpression left,
                    SecondExpression: ColumnReferenceExpression right,
                } eq)
                throw TdsSqlErrors.NotSupported(onlyLookups);
            Read(eq);
            var first = ResolveColumn(left);
            var second = ResolveColumn(right);
            var (inner, outer) = first.Table == table && second.Table != table ? (first, second)
                : second.Table == table && first.Table != table ? (second, first)
                : throw TdsSqlErrors.NotSupported(onlyLookups);
            if (inner.Value != OutputValue.Raw || outer.Value != OutputValue.Raw
                || !(IsKeyOf(inner, outer) || IsKeyOf(outer, inner)))
                throw TdsSqlErrors.NotSupported(onlyLookups);
            link.From = inner.Attribute.LogicalName;
            link.To = outer.Attribute.LogicalName;
            outer.Table.Node.Links.Add(link);
        }

        /// <summary>True when <paramref name="key"/> is the primary key of its table and <paramref name="lookup"/> a lookup to it.</summary>
        private static bool IsKeyOf(Column key, Column lookup) =>
            key.Attribute.LogicalName == SystemAttributes.PrimaryIdName(key.Table.Entity)
            && lookup.Attribute is LookupAttributeMetadata { Targets: { } targets }
            && targets.Contains(key.Table.Entity.LogicalName, StringComparer.Ordinal);

        private void Projection(QuerySpecification spec, List<OutputColumn> outputs)
        {
            foreach (var element in spec.SelectElements)
            {
                switch (element)
                {
                    case SelectStarExpression star:
                        Read(star);
                        foreach (var table in StarTables(star))
                            foreach (var a in (table.Entity.Attributes ?? []).Where(IsExposed))
                                AddOutput(new Column(table, a, OutputValue.Raw, a.LogicalName), null, outputs, fromStar: true);
                        break;
                    case SelectScalarExpression scalar:
                        Read(scalar);
                        var alias = ColumnAlias(scalar);
                        if (UnparenthesizeScalar(scalar.Expression) is not ColumnReferenceExpression reference)
                            throw TdsSqlErrors.NotSupported(DescribeExpression(scalar.Expression, "the select list"));
                        AddOutput(ResolveColumn(reference), alias, outputs);
                        break;
                    default:
                        throw TdsSqlErrors.NotSupported(element is SelectSetVariable ? "assigning variables in SELECT" : Describe(element));
                }
            }
        }

        private static void AddOutput(Column column, string? alias, List<OutputColumn> outputs, bool fromStar = false)
        {
            var node = column.Table.Node;
            if (!node.Columns.Any(c => c.Attribute == column.Attribute.LogicalName)) node.Columns.Add(new ColumnNode(column.Attribute.LogicalName));
            outputs.Add(new OutputColumn(alias ?? column.LogicalName, column.Table.Exposed, column.Attribute.LogicalName, column.Value, fromStar));
        }

        private IEnumerable<Table> StarTables(SelectStarExpression star)
        {
            if (star.Qualifier is null) return _tables;
            var ids = star.Qualifier.Identifiers;
            ReadAll(star.Qualifier);
            var qualifier = ids.Count switch
            {
                1 => ids[0].Value,
                2 when string.Equals(ids[0].Value, DefaultSchema, StringComparison.OrdinalIgnoreCase) => ids[1].Value,
                _ => throw TdsSqlErrors.NotSupported("this qualified *"),
            };
            var table = _tables.FirstOrDefault(t => string.Equals(t.Exposed, qualifier, StringComparison.OrdinalIgnoreCase))
                        ?? throw TdsSqlErrors.InvalidObjectName(qualifier);
            return [table];
        }

        private void AggregateProjection(QuerySpecification spec, List<OutputColumn> outputs, List<(Column Column, ColumnNode Node)> groupKeys)
        {
            var keys = new List<Column>();
            if (spec.GroupByClause is { } groupBy)
            {
                Read(groupBy);
                if (groupBy.All) throw TdsSqlErrors.NotSupported("GROUP BY ALL");
                if (groupBy.GroupByOption != GroupByOption.None) throw TdsSqlErrors.NotSupported("ROLLUP and CUBE");
                foreach (var grouping in groupBy.GroupingSpecifications)
                {
                    if (grouping is not ExpressionGroupingSpecification { DistributedAggregation: false } byExpression)
                        throw TdsSqlErrors.NotSupported("GROUPING SETS");
                    Read(byExpression);
                    if (UnparenthesizeScalar(byExpression.Expression) is not ColumnReferenceExpression reference)
                        throw TdsSqlErrors.NotSupported(DescribeExpression(byExpression.Expression, "GROUP BY"));
                    var column = ResolveColumn(reference);
                    if (column.Value != OutputValue.Raw) throw TdsSqlErrors.NotSupported("grouping by name columns");
                    keys.Add(column);
                }
            }

            foreach (var element in spec.SelectElements)
            {
                if (element is not SelectScalarExpression scalar)
                    throw TdsSqlErrors.NotSupported(element is SelectStarExpression ? "* in an aggregate query" : Describe(element));
                Read(scalar);
                var alias = ColumnAlias(scalar);
                switch (UnparenthesizeScalar(scalar.Expression))
                {
                    case ColumnReferenceExpression reference:
                    {
                        var column = ResolveColumn(reference);
                        if (column.Value != OutputValue.Raw) throw TdsSqlErrors.NotSupported("name columns in an aggregate query");
                        if (!keys.Any(k => SameColumn(k, column)))
                            throw TdsSqlErrors.NotSupported(
                                $"a column that is neither grouped nor aggregated ('{column.Written}') in an aggregate query");
                        var node = new ColumnNode(column.Attribute.LogicalName, ModelAlias(alias ?? column.LogicalName), GroupBy: true);
                        column.Table.Node.Columns.Add(node);
                        groupKeys.Add((column, node));
                        outputs.Add(new OutputColumn(alias ?? column.LogicalName, column.Table.Exposed, node.Alias!, OutputValue.Aggregate));
                        break;
                    }
                    case FunctionCall f when IsAggregate(f):
                    {
                        var (table, node) = AggregateColumn(f, alias);
                        table.Node.Columns.Add(node);
                        outputs.Add(new OutputColumn(alias ?? "", table.Exposed, node.Alias!, OutputValue.Aggregate));
                        break;
                    }
                    default:
                        throw TdsSqlErrors.NotSupported(DescribeExpression(scalar.Expression, "the select list"));
                }
            }

            // Grouped columns that are not selected still group the rows.
            foreach (var key in keys.Where(k => !groupKeys.Any(g => SameColumn(g.Column, k))))
            {
                var node = new ColumnNode(key.Attribute.LogicalName, ModelAlias(key.LogicalName), GroupBy: true);
                key.Table.Node.Columns.Add(node);
                groupKeys.Add((key, node));
            }
        }

        private static bool SameColumn(Column a, Column b) => a.Table == b.Table && a.Attribute == b.Attribute && a.Value == b.Value;

        private static bool IsAggregate(FunctionCall f) =>
            f.FunctionName.Value.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";

        /// <summary>A function call with no target, OVER, WITHIN GROUP, collation or other option.</summary>
        private static bool PlainFunction(FunctionCall f, string name) =>
            string.Equals(f.FunctionName.Value, name, StringComparison.OrdinalIgnoreCase)
            && f.CallTarget is null && f.OverClause is null && f.WithinGroupClause is null && f.Collation is null
            && f.IgnoreRespectNulls is not { Count: > 0 };

        private void ReadFunction(FunctionCall f)
        {
            Read(f);
            Read(f.FunctionName);
        }

        private (Table Table, ColumnNode Node) AggregateColumn(FunctionCall f, string? alias)
        {
            var name = f.FunctionName.Value.ToUpperInvariant();
            if (f.OverClause is not null) throw TdsSqlErrors.NotSupported(Describe(f.OverClause));
            if (!PlainFunction(f, name)) throw TdsSqlErrors.NotSupported($"{name} with these options");
            if (f.Parameters.Count != 1) throw TdsSqlErrors.NotSupported($"{name} with {f.Parameters.Count} arguments");
            ReadFunction(f);
            var distinct = f.UniqueRowFilter == UniqueRowFilter.Distinct;
            var modelAlias = ModelAlias(alias ?? "");

            if (name == "COUNT" && f.Parameters[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard, MultiPartIdentifier: null } star)
            {
                if (distinct) throw TdsSqlErrors.NotSupported("COUNT(DISTINCT *)");
                ReadAll(star);
                var root = _tables[0];
                return (root, new ColumnNode(SystemAttributes.PrimaryIdName(root.Entity), modelAlias, XrmAggregate.Count));
            }

            if (UnparenthesizeScalar(f.Parameters[0]) is not ColumnReferenceExpression reference)
                throw TdsSqlErrors.NotSupported(DescribeExpression(f.Parameters[0], $"{name}"));
            var column = ResolveColumn(reference);
            if (column.Value != OutputValue.Raw) throw TdsSqlErrors.NotSupported("name columns in an aggregate query");
            var aggregate = name switch
            {
                "COUNT" => XrmAggregate.CountColumn,
                "SUM" => XrmAggregate.Sum,
                "AVG" => XrmAggregate.Avg,
                "MIN" => XrmAggregate.Min,
                _ => XrmAggregate.Max,
            };
            // MIN and MAX of the distinct values are those of all values.
            if (distinct && aggregate is XrmAggregate.Sum or XrmAggregate.Avg)
                throw TdsSqlErrors.NotSupported($"{name}(DISTINCT …)");
            var countDistinct = distinct && aggregate == XrmAggregate.CountColumn;
            return (column.Table, new ColumnNode(column.Attribute.LogicalName, modelAlias, aggregate, Distinct: countDistinct));
        }

        /// <summary>A model alias that no other column of the query has (the compiler requires distinct aliases).</summary>
        private string ModelAlias(string candidate)
        {
            if (candidate.Length == 0) candidate = "expr";
            var alias = candidate;
            for (var i = 1; !_modelAliases.Add(alias); i++) alias = candidate + i.ToString(CultureInfo.InvariantCulture);
            return alias;
        }

        private void AddOrders(OrderByClause orderBy, List<OutputColumn> outputs, List<(Column Column, ColumnNode Node)> groupKeys, bool aggregate)
        {
            Read(orderBy);
            var scopeOrder = ScopeOrder();
            var lastScope = 0;
            foreach (var element in orderBy.OrderByElements)
            {
                Read(element);
                var descending = element.SortOrder == SortOrder.Descending;
                if (UnparenthesizeScalar(element.Expression) is not ColumnReferenceExpression reference)
                    throw TdsSqlErrors.NotSupported(DescribeExpression(element.Expression, "ORDER BY"));

                // A one-part name is first a column alias of the select list, as in SQL Server.
                var output = reference is { ColumnType: ColumnType.Regular, MultiPartIdentifier.Identifiers: [var single], Collation: null }
                    ? outputs.FirstOrDefault(o => o.Name.Length > 0 && string.Equals(o.Name, single.Value, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (aggregate)
                {
                    string modelAlias;
                    if (output is not null)
                    {
                        ReadAll(reference);
                        modelAlias = output.Attribute;
                    }
                    else
                    {
                        var column = ResolveColumn(reference);
                        modelAlias = groupKeys.FirstOrDefault(g => SameColumn(g.Column, column)).Node?.Alias
                                     ?? throw TdsSqlErrors.NotSupported(
                                         $"ordering an aggregate query by a column that is not grouped ('{column.Written}')");
                    }
                    _tables[0].Node.Orders.Add(new OrderNode(modelAlias, descending, modelAlias));
                    continue;
                }

                Column target;
                if (output is not null)
                {
                    ReadAll(reference);
                    var table = _tables.First(t => t.Exposed == output.EntityAlias);
                    var attribute = table.Entity.Attributes.First(a => a.LogicalName == output.Attribute);
                    target = new Column(table, attribute, output.Value, output.Name);
                }
                else target = ResolveColumn(reference);
                if (target.Value != OutputValue.Raw) throw TdsSqlErrors.NotSupported("ordering by name columns");

                // The compiler orders by the root's orders, then each link's: an ORDER BY must list its tables in that order.
                var scope = scopeOrder[target.Table.Node];
                if (scope < lastScope)
                    throw TdsSqlErrors.NotSupported("an ORDER BY that lists a column of a table before a column of a table joined earlier");
                lastScope = scope;
                target.Table.Node.Orders.Add(new OrderNode(target.Attribute.LogicalName, descending));
            }
        }

        /// <summary>The position of each table's node in the compiler's scope order (the root, then the links depth first).</summary>
        private Dictionary<EntityNode, int> ScopeOrder()
        {
            var order = new Dictionary<EntityNode, int>(ReferenceEqualityComparer.Instance);
            void Walk(EntityNode node)
            {
                order[node] = order.Count;
                foreach (var link in node.Links) Walk(link);
            }
            Walk(_tables[0].Node);
            return order;
        }

        // ---- WHERE ----

        private void AddPredicate(FilterNode target, BooleanExpression expression, bool negate)
        {
            switch (expression)
            {
                case BooleanParenthesisExpression parenthesis:
                    Read(parenthesis);
                    AddPredicate(target, parenthesis.Expression, negate);
                    break;
                case BooleanNotExpression not:
                    Read(not);
                    AddPredicate(target, not.Expression, !negate);
                    break;
                case BooleanBinaryExpression binary:
                {
                    Read(binary);
                    var and = binary.BinaryExpressionType == BooleanBinaryExpressionType.And;
                    // De Morgan: NOT (a AND b) = NOT a OR NOT b. It holds in SQL's three-valued logic too.
                    var op = and != negate ? FilterLogic.And : FilterLogic.Or;
                    var into = target;
                    if (op != target.Operator)
                    {
                        into = new FilterNode { Operator = op };
                        target.Filters.Add(into);
                    }
                    AddPredicate(into, binary.FirstExpression, negate);
                    AddPredicate(into, binary.SecondExpression, negate);
                    break;
                }
                case BooleanComparisonExpression comparison:
                    target.Conditions.Add(Comparison(comparison, negate));
                    break;
                case LikePredicate like:
                {
                    Read(like);
                    if (like.EscapeExpression is not null || like.OdbcEscape) throw TdsSqlErrors.NotSupported("LIKE … ESCAPE");
                    var column = ConditionColumn(like.FirstExpression, "LIKE");
                    var op = like.NotDefined != negate ? FilterOperator.NotLike : FilterOperator.Like;
                    target.Conditions.Add(Condition(column, op, [Value(like.SecondExpression)]));
                    break;
                }
                case InPredicate @in:
                {
                    Read(@in);
                    if (@in.Subquery is not null) throw TdsSqlErrors.NotSupported(Describe(@in.Subquery));
                    var column = ConditionColumn(@in.Expression, "IN");
                    var op = @in.NotDefined != negate ? FilterOperator.NotIn : FilterOperator.In;
                    target.Conditions.Add(Condition(column, op, @in.Values.Select(Value).ToList()));
                    break;
                }
                case BooleanTernaryExpression ternary:
                {
                    Read(ternary);
                    var between = ternary.TernaryExpressionType == BooleanTernaryExpressionType.Between;
                    var column = ConditionColumn(ternary.FirstExpression, "BETWEEN");
                    var op = between != negate ? FilterOperator.Between : FilterOperator.NotBetween;
                    target.Conditions.Add(Condition(column, op, [Value(ternary.SecondExpression), Value(ternary.ThirdExpression)]));
                    break;
                }
                case BooleanIsNullExpression isNull:
                {
                    Read(isNull);
                    var column = ConditionColumn(isNull.Expression, "IS NULL");
                    var op = isNull.IsNot != negate ? FilterOperator.NotNull : FilterOperator.Null;
                    target.Conditions.Add(Condition(column, op, []));
                    break;
                }
                default:
                    throw TdsSqlErrors.NotSupported(expression is ExistsPredicate or SubqueryComparisonPredicate
                        ? Describe(expression) : $"{Words(expression.GetType().Name, null).ToLowerInvariant()} in WHERE");
            }
        }

        private ConditionNode Comparison(BooleanComparisonExpression comparison, bool negate)
        {
            Read(comparison);
            var op = comparison.ComparisonType switch
            {
                BooleanComparisonType.Equals => FilterOperator.Equal,
                BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => FilterOperator.NotEqual,
                BooleanComparisonType.LessThan => FilterOperator.LessThan,
                BooleanComparisonType.LessThanOrEqualTo => FilterOperator.LessEqual,
                BooleanComparisonType.GreaterThan => FilterOperator.GreaterThan,
                BooleanComparisonType.GreaterThanOrEqualTo => FilterOperator.GreaterEqual,
                _ => throw TdsSqlErrors.NotSupported($"the comparison {comparison.ComparisonType}"),
            };
            var first = UnparenthesizeScalar(comparison.FirstExpression);
            var second = UnparenthesizeScalar(comparison.SecondExpression);
            ScalarExpression columnSide, valueSide;
            if (first is ColumnReferenceExpression && second is ColumnReferenceExpression)
                throw TdsSqlErrors.NotSupported("comparisons between two columns");
            if (first is ColumnReferenceExpression) (columnSide, valueSide) = (first, second);
            else if (second is ColumnReferenceExpression)
            {
                (columnSide, valueSide) = (second, first);
                op = op switch
                {
                    FilterOperator.LessThan => FilterOperator.GreaterThan,
                    FilterOperator.LessEqual => FilterOperator.GreaterEqual,
                    FilterOperator.GreaterThan => FilterOperator.LessThan,
                    FilterOperator.GreaterEqual => FilterOperator.LessEqual,
                    _ => op,
                };
            }
            else
            {
                Value(first); // names the unsupported construct (a function, a subquery...) when there is one
                Value(second);
                throw TdsSqlErrors.NotSupported("comparisons without a column");
            }
            if (negate)
            {
                // Each negation keeps SQL's three-valued logic: a NULL column satisfies neither.
                op = op switch
                {
                    FilterOperator.Equal => FilterOperator.NotEqual,
                    FilterOperator.NotEqual => FilterOperator.Equal,
                    FilterOperator.LessThan => FilterOperator.GreaterEqual,
                    FilterOperator.LessEqual => FilterOperator.GreaterThan,
                    FilterOperator.GreaterThan => FilterOperator.LessEqual,
                    FilterOperator.GreaterEqual => FilterOperator.LessThan,
                    _ => throw new InvalidOperationException($"No negation for the comparison operator {op}."),
                };
            }
            var column = ConditionColumn(columnSide, "WHERE");
            return Condition(column, op, [Value(valueSide)]);
        }

        private Column ConditionColumn(ScalarExpression expression, string where)
        {
            if (UnparenthesizeScalar(expression) is not ColumnReferenceExpression reference)
                throw TdsSqlErrors.NotSupported(DescribeExpression(expression, where));
            var column = ResolveColumn(reference);
            if (column.Value != OutputValue.Raw) throw TdsSqlErrors.NotSupported("filtering on name columns");
            return column;
        }

        private static ConditionNode Condition(Column column, FilterOperator op, IReadOnlyList<object?> values) =>
            new(column.Table.IsRoot ? null : column.Table.Exposed, column.Attribute.LogicalName, op, values);

        /// <summary>A literal or a parameter: the only values the subset takes.</summary>
        private object Value(ScalarExpression expression)
        {
            switch (expression)
            {
                case ParenthesisExpression parenthesis:
                    Read(parenthesis);
                    return Value(parenthesis.Expression);
                case Literal { Collation: not null }:
                    throw TdsSqlErrors.NotSupported("COLLATE");
                case IntegerLiteral or NumericLiteral:
                {
                    // Each branch returns its own type, int, then long, then decimal: the coercer converts int and long for
                    // integer columns but never a decimal. ScriptDom makes an integer beyond the int range a NumericLiteral.
                    Read(expression);
                    var text = ((Literal)expression).Value;
                    if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) return i;
                    if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var l)) return l;
                    if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return d;
                    throw TdsSqlErrors.NotSupported($"the number {text}, which is out of range");
                }
                case RealLiteral real:
                    Read(real);
                    return double.TryParse(real.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) && double.IsFinite(dbl)
                        ? dbl
                        : throw TdsSqlErrors.NotSupported($"the number {real.Value}, which is out of range");
                case StringLiteral text:
                    Read(text);
                    return text.Value;
                case NullLiteral:
                    throw TdsSqlErrors.NotSupported("comparisons with NULL (use IS NULL or IS NOT NULL)");
                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative or UnaryExpressionType.Positive } unary
                    when UnparenthesizeScalar(unary.Expression) is IntegerLiteral or NumericLiteral or RealLiteral:
                {
                    Read(unary);
                    var value = Value(unary.Expression);
                    if (unary.UnaryExpressionType == UnaryExpressionType.Positive) return value;
                    // Each arm is cast to object: a switch over int, long, decimal and double would otherwise convert to one type.
                    return value switch
                    {
                        int n => (object)(-n), // a literal is never negative, and 2147483648 is a long
                        long n => (object)(-n),
                        decimal n => (object)(-n),
                        double n => (object)(-n),
                        _ => throw TdsSqlErrors.NotSupported("this value"),
                    };
                }
                case VariableReference variable:
                    return Parameter(variable) ?? throw TdsSqlErrors.NotSupported(
                        $"a NULL value for the parameter \"{variable.Name}\" in a comparison (use IS NULL or IS NOT NULL)");
                default:
                    throw TdsSqlErrors.NotSupported(DescribeExpression(expression, "WHERE"));
            }
        }

        /// <summary>
        /// The value of a parameter (null for SQL NULL). Only scalar CLR types are taken: a binary, an array, a table-valued
        /// parameter or any other object is refused, and so is a non-finite float.
        /// </summary>
        private object? Parameter(VariableReference variable)
        {
            Read(variable);
            if (!_parameters.TryGetValue(variable.Name.TrimStart('@'), out var value))
                throw new TdsSqlException(TdsSqlErrors.UndeclaredVariable,
                    $"The variable \"{variable.Name}\" has no value: pass it as a parameter of the request.");
            return value switch
            {
                null => null,
                string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or Guid or DateTime
                    or DateTimeOffset => value,
                double d when double.IsFinite(d) => value,
                float f when float.IsFinite(f) => value,
                _ => throw TdsSqlErrors.NotSupported(
                    $"the value of the parameter \"{variable.Name}\" (type {value.GetType().Name}); pass a number, text, date, GUID or bit"),
            };
        }

        // ---- Names ----

        /// <summary>
        /// A column of one of the tables. A one-part name must belong to exactly one table; a two-part name is
        /// <c>table.column</c> (the table by its exposed name), and a three-part one <c>dbo.table.column</c>.
        /// </summary>
        private Column ResolveColumn(ColumnReferenceExpression reference)
        {
            if (reference.Collation is not null) throw TdsSqlErrors.NotSupported("COLLATE");
            if (reference.ColumnType != ColumnType.Regular)
                throw TdsSqlErrors.NotSupported(reference.ColumnType == ColumnType.Wildcard ? "* here" : "pseudo-columns");
            var ids = reference.MultiPartIdentifier.Identifiers;
            var written = string.Join('.', ids.Select(i => i.Value));
            var name = ids[^1].Value;
            IEnumerable<Table> candidates;
            switch (ids.Count)
            {
                case 1:
                    candidates = _tables;
                    break;
                case 2:
                case 3 when string.Equals(ids[0].Value, DefaultSchema, StringComparison.OrdinalIgnoreCase):
                {
                    var qualifier = ids[^2].Value;
                    var table = _tables.FirstOrDefault(t => string.Equals(t.Exposed, qualifier, StringComparison.OrdinalIgnoreCase))
                                ?? throw new TdsSqlException(TdsSqlErrors.InvalidColumn,
                                    $"Invalid column name '{written}': no table of the query is named '{qualifier}'.");
                    candidates = [table];
                    break;
                }
                default:
                    throw TdsSqlErrors.NotSupported($"the column name '{written}' (use <table or alias>.<column>)");
            }

            Column? found = null;
            foreach (var table in candidates)
            {
                if (FindColumn(table, name, written) is not { } column) continue;
                if (found is not null)
                    throw new TdsSqlException(TdsSqlErrors.AmbiguousColumn,
                        $"The column name '{written}' is in more than one table: qualify it with the table or its alias.");
                found = column;
            }
            if (found is null) throw TdsSqlErrors.InvalidColumnName(written);
            ReadAll(reference);
            return found;
        }

        /// <summary>
        /// An exposed attribute of the table by name (ignoring case), or <c>&lt;lookup&gt;name</c> / <c>&lt;choice&gt;name</c>
        /// (ruling T7). An attribute of a type the endpoint does not expose is an invalid column.
        /// </summary>
        private static Column? FindColumn(Table table, string name, string written)
        {
            var attributes = table.Entity.Attributes ?? [];
            if (attributes.FirstOrDefault(a => string.Equals(a.LogicalName, name, StringComparison.OrdinalIgnoreCase)) is { } attribute)
                return IsExposed(attribute) ? new Column(table, attribute, OutputValue.Raw, written) : throw TdsSqlErrors.InvalidColumnName(written);
            if (name.Length <= NameSuffix.Length || !name.EndsWith(NameSuffix, StringComparison.OrdinalIgnoreCase)) return null;
            var baseName = name[..^NameSuffix.Length];
            if (attributes.FirstOrDefault(a => string.Equals(a.LogicalName, baseName, StringComparison.OrdinalIgnoreCase)) is not { } owner
                || !IsExposed(owner))
                return null;
            return owner switch
            {
                LookupAttributeMetadata => new Column(table, owner, OutputValue.Name, written),
                PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata => new Column(table, owner, OutputValue.Label, written),
                _ => null,
            };
        }

        private string? ColumnAlias(SelectScalarExpression scalar)
        {
            if (scalar.ColumnName is not { } name) return null;
            ReadAll(name);
            return name.Value;
        }

        // ---- Whitelist bookkeeping ----

        private void Read(TSqlFragment fragment) => _read.Add(fragment);

        /// <summary>Marks a node and everything under it as read: only for names, once every part was checked.</summary>
        private void ReadAll(TSqlFragment fragment)
        {
            var collector = new Collector();
            fragment.Accept(collector);
            foreach (var node in collector.Nodes) _read.Add(node);
        }

        /// <summary>Refuses the first node of the statement that the translation did not read (ruling T6: a whitelist).</summary>
        private void CheckEverythingRead(TSqlStatement statement)
        {
            var collector = new Collector();
            statement.Accept(collector);
            if (collector.Nodes.FirstOrDefault(n => !_read.Contains(n)) is { } unread)
                throw TdsSqlErrors.NotSupported(Describe(unread));
        }

        private BooleanExpression Unparenthesize(BooleanExpression expression)
        {
            while (expression is BooleanParenthesisExpression p)
            {
                Read(p);
                expression = p.Expression;
            }
            return expression;
        }

        private ScalarExpression UnparenthesizeScalar(ScalarExpression expression)
        {
            while (expression is ParenthesisExpression p)
            {
                Read(p);
                expression = p.Expression;
            }
            return expression;
        }

        private static string DescribeExpression(ScalarExpression expression, string where) => expression switch
        {
            ScalarSubquery or FunctionCall or CaseExpression or CastCall or ConvertCall or TryCastCall or TryConvertCall
                or GlobalVariableExpression => Describe(expression),
            ColumnReferenceExpression { Collation: not null } or Literal { Collation: not null } => "COLLATE",
            VariableReference => $"variables in {where}",
            _ => $"expressions in {where}",
        };
    }
}
