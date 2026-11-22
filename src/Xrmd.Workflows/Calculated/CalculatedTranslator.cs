using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Calculated;

/// <summary>
/// Turns a calculated column definition read by <see cref="XamlWorkflowParser.ParseCalculated"/> into a <see cref="ComputedDefinition"/>
/// and checks it against the metadata (plan 2.12a): the columns it reads, the argument types of each function, the result type
/// against the column type, the option values, the parent rules (a single-target lookup, a parent's calculated column reading only
/// its own record) and the date behaviors of the DIFF functions (K9). Chains and cycles are checked by <c>nested</c>.
/// </summary>
internal static class CalculatedTranslator
{
    /// <summary>The most calculated columns a chain may hold, the column being defined included.</summary>
    public const int MaxDepth = 5;

    /// <summary>The type of a value, as far as the checks need it.</summary>
    private enum Kind { Null, Text, Whole, Decimal, Money, Date, Boolean, Option }

    /// <summary>
    /// A translated value. <paramref name="Behavior"/> is the <c>DateTimeBehavior</c> of a date read from a column (null when
    /// unknown); <paramref name="Options"/> the column whose choices an option value must be one of.
    /// </summary>
    private sealed record Typed(ComputedExpr Expr, Kind Kind, string? Behavior = null, EnumAttributeMetadata? Options = null, string? Name = null);

    private static readonly string[] DateShifts = ["HOURS", "DAYS", "WEEKS", "MONTHS", "YEARS"];

    /// <summary>The comparison operators a condition may use (<c>ConditionOperator</c> names, as the workflow conditions).</summary>
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal)
    {
        "Equal", "NotEqual", "GreaterThan", "GreaterEqual", "LessThan", "LessEqual", "Null", "NotNull",
        "Contains", "DoesNotContain", "BeginsWith", "EndsWith",
    };

    /// <param name="entity">The table of the calculated column.</param>
    /// <param name="attribute">The calculated column.</param>
    /// <param name="parsed">Its definition, read by <see cref="XamlWorkflowParser.ParseCalculated"/>.</param>
    /// <param name="entities">Finds a table by logical name.</param>
    /// <param name="nested">The definition of another calculated column this one reads (checks the chain depth and cycles).</param>
    /// <exception cref="XrmdValidationException">The definition is not valid.</exception>
    public static ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, WorkflowDefinition parsed,
        Func<string, EntityMetadata?> entities, Func<EntityMetadata, AttributeMetadata, ComputedDefinition> nested)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(parsed);
        var context = new Context(entity, attribute, entities, nested);
        var body = context.Block(parsed.Root);
        if (IsEmpty(body))
            throw context.EmptyEverywhere();
        var definition = new ComputedDefinition(body, context.Sources, 0);
        return definition with { SourceTypeMask = ComputedColumnRules.SourceTypeMask(entity, definition, entities) };
    }

    /// <summary>An empty (null) constant: the value of a branch that sets nothing.</summary>
    private static bool IsEmpty(ComputedExpr value) => value is ComputedExpr.Literal { Value: null };

    private sealed class Context(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities,
        Func<EntityMetadata, AttributeMetadata, ComputedDefinition> nested)
    {
        private readonly List<ComputedExpr.Column> _sources = [];

        public IReadOnlyList<ComputedExpr.Column> Sources => _sources;

        private string Where => $"'{entity.LogicalName}.{attribute.LogicalName}'";

        private XrmdValidationException Refuse(string reason) => new($"The calculated column {Where} {reason}.");

        /// <summary>
        /// The refusal of a definition that sets an empty value in every branch (or sets only an empty value): SQL Server cannot
        /// type a <c>CASE</c> whose results are all <c>NULL</c> (final review I2).
        /// </summary>
        public XrmdValidationException EmptyEverywhere() =>
            Refuse("sets an empty value in every branch: at least one branch needs a value");

        // ---- steps -----------------------------------------------------------------------------------------------------

        /// <summary>The value a block gives: one set-value step, or one condition whose branches are blocks.</summary>
        public ComputedExpr Block(WorkflowStep step)
        {
            var steps = Flatten(step).ToList();
            return steps switch
            {
                [SetValueStep set] => Result(Value(set.Value)),
                [ConditionStep condition] => Conditional(condition),
                [] => throw Refuse("has a branch that sets no value"),
                _ => throw Refuse("sets its value in more than one step of a branch"),
            };
        }

        /// <summary>
        /// A condition step; one of its branches at least sets a value that is not an empty constant. A condition with no branch but
        /// its otherwise is that block: the designer wraps every definition in one (2.12a parity export: a single <c>[True]</c> branch).
        /// </summary>
        private ComputedExpr Conditional(ConditionStep condition)
        {
            if (condition.Branches.Count == 0 && condition.Otherwise is { } always)
                return Block(always);
            var conditional = new ComputedExpr.Conditional(
                condition.Branches.Select(b => new ComputedBranch(Condition(b.When), Block(b.Then))).ToList(),
                condition.Otherwise is null ? null : Block(condition.Otherwise));
            if (conditional.Branches.All(b => IsEmpty(b.Then)) && (conditional.Otherwise is null || IsEmpty(conditional.Otherwise)))
                throw EmptyEverywhere();
            return conditional;
        }

        private static IEnumerable<WorkflowStep> Flatten(WorkflowStep step) =>
            step is SequenceStep sequence ? sequence.Steps.SelectMany(Flatten) : [step];

        /// <summary>Checks a value the column takes against the column's type.</summary>
        private ComputedExpr Result(Typed value)
        {
            var fits = attribute switch
            {
                StringAttributeMetadata => value.Kind is Kind.Text,
                IntegerAttributeMetadata or DecimalAttributeMetadata or MoneyAttributeMetadata => IsNumber(value.Kind),
                DateTimeAttributeMetadata => value.Kind is Kind.Date,
                BooleanAttributeMetadata => value.Kind is Kind.Boolean,
                PicklistAttributeMetadata picklist => value.Kind is Kind.Option && value.Expr is ComputedExpr.Literal
                    && CheckOption(picklist, value, $"the column {Where}"),
                _ => throw Refuse($"has the type {attribute.AttributeType}, which cannot be calculated"),
            };
            if (value.Kind is not Kind.Null && !fits)
                throw Refuse(attribute is PicklistAttributeMetadata
                    ? $"can only take a constant option value, not {Describe(value)}"
                    : $"of type {attribute.AttributeType} cannot take {Describe(value)}");
            return value.Expr;
        }

        // ---- values ----------------------------------------------------------------------------------------------------

        private Typed Value(ValueExpr value) => value switch
        {
            ValueExpr.Literal literal => Literal(literal),
            ValueExpr.AttributeOf read => Column(read.Record, read.Attribute),
            ValueExpr.ExecutionTime => new Typed(new ComputedExpr.Now(), Kind.Date),
            ValueExpr.Arithmetic arithmetic => Arithmetic(arithmetic.Operator, arithmetic.Operands),
            ValueExpr.FunctionCall call => Function(call.Name, call.Args.Select(Value).ToList()),
            ValueExpr.Concat concat => Function("CONCAT", concat.Parts.Select(Value).ToList()),
            _ => throw Refuse($"uses a value that a calculated column cannot read ({value.GetType().Name})"),
        };

        private Typed Literal(ValueExpr.Literal literal)
        {
            var kind = literal.Type switch
            {
                "String" => Kind.Text,
                "Integer" => Kind.Whole,
                "Decimal" or "Double" => Kind.Decimal,
                "Money" => Kind.Money,
                "Boolean" => Kind.Boolean,
                "DateTime" => Kind.Date,
                "OptionSetValue" => Kind.Option,
                "Null" => Kind.Null,
                _ => throw Refuse($"uses a constant of type {literal.Type}, which a calculated column cannot hold"),
            };
            return new Typed(new ComputedExpr.Literal(literal.Value), kind);
        }

        private Typed Column(RecordRef record, string name)
        {
            EntityMetadata owner;
            string? lookup = null;
            switch (record)
            {
                case RecordRef.Primary:
                    owner = entity;
                    if (name == attribute.LogicalName)
                        throw Refuse("reads itself");
                    break;
                case RecordRef.Related related:
                    lookup = related.LookupAttribute;
                    owner = Parent(lookup);
                    break;
                default:
                    throw Refuse("reads a record other than its own or its parent");
            }

            var column = owner.Attributes?.FirstOrDefault(a => a.LogicalName == name)
                ?? throw Refuse($"reads the column '{owner.LogicalName}.{name}', which does not exist");
            // A calculated or formula column (plan 2.12c: Learn advises against a calculated column reading a formula column, but
            // allows it) enters as its own definition, checked for chains and cycles.
            if (ComputedColumnRules.IsComputed(column))
            {
                var definition = nested(owner, column);
                if (lookup is not null)
                    OwnRecordOnly(owner, column, definition);
            }
            else if (column is not MoneyAttributeMetadata { IsBaseCurrency: true } && TypeMapping.ToColumns(column).Count == 0)
            {
                // A companion stored by another column (the name of a lookup, an EntityName), a party list, a note body: the query
                // has no value to read. A money's _base is stored with its money column and is read from it.
                throw Refuse($"reads the column '{owner.LogicalName}.{name}', which has no value of its own");
            }

            var source = new ComputedExpr.Column(lookup, name);
            if (!_sources.Contains(source))
                _sources.Add(source);
            var label = $"'{owner.LogicalName}.{name}'";
            return column switch
            {
                StringAttributeMetadata or MemoAttributeMetadata => new Typed(source, Kind.Text, Name: label),
                IntegerAttributeMetadata or BigIntAttributeMetadata => new Typed(source, Kind.Whole, Name: label),
                DecimalAttributeMetadata or DoubleAttributeMetadata => new Typed(source, Kind.Decimal, Name: label),
                MoneyAttributeMetadata => new Typed(source, Kind.Money, Name: label),
                DateTimeAttributeMetadata date => new Typed(source, Kind.Date, date.DateTimeBehavior?.Value ?? DateTimeBehavior.UserLocal.Value, Name: label),
                BooleanAttributeMetadata => new Typed(source, Kind.Boolean, Name: label),
                PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata =>
                    new Typed(source, Kind.Option, Options: (EnumAttributeMetadata)column, Name: label),
                _ => throw Refuse($"reads the column {label} of type {column.AttributeType}, which a calculated column cannot use"),
            };
        }

        /// <summary>The parent table a lookup of the column's own table points to: a lookup with a single target, not a Customer one.</summary>
        private EntityMetadata Parent(string lookup)
        {
            var column = entity.Attributes?.FirstOrDefault(a => a.LogicalName == lookup)
                ?? throw Refuse($"reads a parent record through '{lookup}', which is not a column of '{entity.LogicalName}'");
            if (column is not LookupAttributeMetadata l || l.AttributeType is not (null or AttributeTypeCode.Lookup))
                throw Refuse($"reads a parent record through '{lookup}', which is not a lookup to one table ({column.AttributeType})");
            if (l.Targets is not [var target])
                throw Refuse($"reads a parent record through '{lookup}', which can point to several tables");
            return entities(target) ?? throw Refuse($"reads a parent record through '{lookup}', whose table '{target}' does not exist");
        }

        /// <summary>
        /// A calculated column of the parent record reads only its own record, directly or through its own calculated columns (two
        /// tables at most).
        /// </summary>
        private void OwnRecordOnly(EntityMetadata parent, AttributeMetadata column, ComputedDefinition definition)
        {
            foreach (var source in definition.Sources)
            {
                if (source.Lookup is not null)
                    throw Refuse($"reads '{parent.LogicalName}.{column.LogicalName}', a {KindOf(column)} column of its parent record that " +
                        "reads another record: a calculated column reads two tables at most");
                if (parent.Attributes?.FirstOrDefault(a => a.LogicalName == source.Attribute) is { } chained && ComputedColumnRules.IsComputed(chained))
                    OwnRecordOnly(parent, column, nested(parent, chained));
            }
        }

        private static string KindOf(AttributeMetadata column) => column.SourceType == ComputedColumnRules.Formula ? "formula" : "calculated";

        private Typed Arithmetic(string op, IReadOnlyList<ValueExpr> operands)
        {
            if (op is not ("Add" or "Subtract" or "Multiply" or "Divide"))
                throw Refuse($"uses the operator '{op}', which a calculated column does not support");
            if (operands.Count < 2)
                throw Refuse($"uses {op} with {operands.Count} operand(s)");
            var values = operands.Select(Value).ToList();
            // An empty constant has no SQL type: NULLIF(NULL, 0) and the like fail every read (final review I2).
            if (values.Any(v => v.Kind is Kind.Null))
                throw Refuse($"uses {op} with an empty value: arithmetic needs numbers");
            if (values.FirstOrDefault(v => !IsNumber(v.Kind)) is { } bad)
                throw Refuse($"uses {op} on {Describe(bad)}: arithmetic needs numbers");
            var result = values[0];
            foreach (var right in values.Skip(1))
                result = new Typed(new ComputedExpr.Binary(op, result.Expr, right.Expr), Wider(result.Kind, right.Kind));
            return result;
        }

        private Typed Function(string name, IReadOnlyList<Typed> args)
        {
            // An empty constant has no SQL type: a function of it fails every read (final review I2).
            for (var i = 0; i < args.Count; i++)
                if (args[i].Kind is Kind.Null)
                    throw Refuse($"uses {name} with an empty value as argument {i + 1}: it needs a value");
            var shift = name.StartsWith("ADD", StringComparison.Ordinal) ? name[3..]
                : name.StartsWith("SUBTRACT", StringComparison.Ordinal) ? name[8..] : null;
            if (shift is not null && DateShifts.Contains(shift))
            {
                Arity(name, args, 2);
                Expect(name, args, 0, "a whole number", Kind.Whole);
                Expect(name, args, 1, "a date", Kind.Date);
                // A Date only value is a date without a time: SQL Server cannot add hours to it (final review I2).
                if (shift == "HOURS" && args[1].Behavior == DateTimeBehavior.DateOnly.Value)
                    throw Refuse($"uses {name} on a date only value{(args[1].Name is { } label ? $" ({label})" : "")}: hours can only be added to or subtracted from a date and time");
                return new Typed(new ComputedExpr.Function(name, args.Select(a => a.Expr).ToList()), Kind.Date, args[1].Behavior);
            }
            if (name.StartsWith("DIFFIN", StringComparison.Ordinal) && name[6..] is "DAYS" or "HOURS" or "MINUTES" or "MONTHS" or "WEEKS" or "YEARS")
            {
                Arity(name, args, 2);
                Expect(name, args, 0, "a date", Kind.Date);
                Expect(name, args, 1, "a date", Kind.Date);
                if (args[0].Behavior is { } first && args[1].Behavior is { } second && first != second)
                    throw Refuse($"uses {name} on dates of different behaviors ({first} and {second}): both dates need the same behavior");
                return new Typed(new ComputedExpr.Function(name, args.Select(a => a.Expr).ToList()), Kind.Whole);
            }
            switch (name)
            {
                case "CONCAT":
                    if (args.Count < 2)
                        throw Refuse($"uses CONCAT with {args.Count} argument(s): it needs at least 2");
                    for (var i = 0; i < args.Count; i++)
                        Expect(name, args, i, "text", Kind.Text);
                    return new Typed(new ComputedExpr.Function(name, args.Select(a => a.Expr).ToList()), Kind.Text);
                case "TRIMLEFT" or "TRIMRIGHT":
                    Arity(name, args, 2);
                    Expect(name, args, 0, "text", Kind.Text);
                    Expect(name, args, 1, "a whole number", Kind.Whole);
                    return new Typed(new ComputedExpr.Function(name, args.Select(a => a.Expr).ToList()), Kind.Text);
                default:
                    throw Refuse($"uses the function '{name}', which a calculated column does not support");
            }
        }

        private void Arity(string name, IReadOnlyList<Typed> args, int count)
        {
            if (args.Count != count)
                throw Refuse($"uses {name} with {args.Count} argument(s): it needs {count}");
        }

        /// <summary>Argument <paramref name="index"/> of a function is of <paramref name="kind"/>.</summary>
        private void Expect(string name, IReadOnlyList<Typed> args, int index, string expected, Kind kind)
        {
            if (args[index].Kind != kind)
                throw Refuse($"uses {name} with {Describe(args[index])} as argument {index + 1}: it needs {expected}");
        }

        // ---- conditions ------------------------------------------------------------------------------------------------

        private ComputedCondition Condition(ConditionExpr condition) => condition switch
        {
            ConditionExpr.And and => new ComputedCondition.And(Condition(and.Left), Condition(and.Right)),
            ConditionExpr.Or or => new ComputedCondition.Or(Condition(or.Left), Condition(or.Right)),
            ConditionExpr.Not not => new ComputedCondition.Not(Condition(not.Operand)),
            ConditionExpr.Compare compare => Compare(compare),
            _ => throw Refuse($"uses a condition it cannot read ({condition.GetType().Name})"),
        };

        private ComputedCondition.Compare Compare(ConditionExpr.Compare compare)
        {
            if (!Comparisons.Contains(compare.Operator))
                throw Refuse($"uses the condition operator '{compare.Operator}', which a calculated column does not support");
            var left = Value(compare.Left);
            if (compare.Operator is "Null" or "NotNull")
                return compare.Right is null
                    ? new ComputedCondition.Compare(compare.Operator, left.Expr, null)
                    : throw Refuse($"uses {compare.Operator} with a value");
            var right = compare.Right is null ? throw Refuse($"uses {compare.Operator} without a value") : Value(compare.Right);

            // An empty (null) constant: "equals empty" is "has no value" and "does not equal empty" is "has a value" (a comparison
            // with NULL would never be true in SQL); no other operator can compare with it.
            if (IsEmptyConstant(left) || IsEmptyConstant(right))
            {
                var other = IsEmptyConstant(right) ? left : right;
                return compare.Operator switch
                {
                    "Equal" => new ComputedCondition.Compare("Null", other.Expr, null),
                    "NotEqual" => new ComputedCondition.Compare("NotNull", other.Expr, null),
                    var op => throw Refuse($"uses {op} with an empty value: only Equal and NotEqual compare with it"),
                };
            }

            var comparable = (left.Kind, right.Kind) switch
            {
                (Kind.Null, _) or (_, Kind.Null) => true,
                var (l, r) when IsNumber(l) && IsNumber(r) => true,
                var (l, r) => l == r,
            };
            if (!comparable)
                throw Refuse($"compares {Describe(left)} with {Describe(right)}");
            if (compare.Operator is "Contains" or "DoesNotContain" or "BeginsWith" or "EndsWith" && (left.Kind != Kind.Text || right.Kind != Kind.Text))
                throw Refuse($"uses {compare.Operator} on {Describe(left.Kind is Kind.Text ? right : left)}: it needs text");
            if (compare.Operator is not ("Equal" or "NotEqual") && (left.Kind is Kind.Boolean or Kind.Option))
                throw Refuse($"uses {compare.Operator} on {Describe(left)}: only Equal and NotEqual compare it");
            if (left.Options is { } options && right.Expr is ComputedExpr.Literal)
                CheckOption(options, right, $"the column {left.Name}");
            if (right.Options is { } rightOptions && left.Expr is ComputedExpr.Literal)
                CheckOption(rightOptions, left, $"the column {right.Name}");
            return new ComputedCondition.Compare(compare.Operator, left.Expr, right.Expr);
        }

        private static bool IsEmptyConstant(Typed value) => value.Expr is ComputedExpr.Literal { Value: null };

        /// <summary>An option value literal is one of the choices of <paramref name="column"/>.</summary>
        private bool CheckOption(EnumAttributeMetadata column, Typed value, string what)
        {
            if (value.Expr is not ComputedExpr.Literal { Value: int option })
                throw Refuse($"compares {what} with {Describe(value)}, not an option value");
            if (column.OptionSet?.Options?.Any(o => o.Value == option) != true)
                throw Refuse($"uses the option value {option}, which is not a choice of {what}");
            return true;
        }

        // ---- types -----------------------------------------------------------------------------------------------------

        private static bool IsNumber(Kind kind) => kind is Kind.Whole or Kind.Decimal or Kind.Money;

        private static Kind Wider(Kind left, Kind right) =>
            left is Kind.Money || right is Kind.Money ? Kind.Money
            : left is Kind.Decimal || right is Kind.Decimal ? Kind.Decimal
            : left is Kind.Null ? right
            : left;

        private static string Describe(Typed value)
        {
            var kind = value.Kind switch
            {
                Kind.Text => "text",
                Kind.Whole => "a whole number",
                Kind.Decimal => "a decimal number",
                Kind.Money => "a currency value",
                Kind.Date => "a date",
                Kind.Boolean => "a yes/no value",
                Kind.Option => "an option value",
                _ => "an empty value",
            };
            return value.Name is null ? kind : $"{kind} ({value.Name})";
        }
    }
}
