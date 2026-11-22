using System.Globalization;
using System.Text;
using Microsoft.PowerFx;
using Microsoft.PowerFx.Syntax;
using Microsoft.PowerFx.Types;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Xaml;
using static Xrmd.Workflows.Formula.FormulaFunctions;

namespace Xrmd.Workflows.Formula;

/// <summary>
/// Turns the Power Fx formula of a formula column (<c>SourceType</c> 3) into a <see cref="ComputedDefinition"/> (plan 2.12c, ruling F1).
/// The formula is parsed and bound by <c>Microsoft.PowerFx.Core</c> (<c>Engine.Check</c>
/// against a record type built from the table's columns); the translation walks the public syntax tree and types each node itself
/// from the column metadata, which is where the rules of formula columns are checked: the supported functions (Global Constraints),
/// blank semantics (F5: a blank number operand is <c>fx.Zero</c>, a blank text operand <c>fx.Empty</c>; an error, the null of a
/// failed operation, is never wrapped and stays null: Q1 2026-11-19), the result type against the
/// column type (F6) and the refusals of F7. Chains and cycles are checked by <c>chained</c>.
/// </summary>
/// <remarks>
/// The <c>ComputedExpr.Function</c> names this translator emits all start with <c>fx.</c>: the Power Fx function name
/// (<c>fx.Upper</c>, <c>fx.DateAdd</c>, ...) plus <c>fx.Zero</c>, <c>fx.Empty</c>, <c>fx.Concat</c> (the <c>&amp;</c> operator),
/// <c>fx.Power</c> (<c>^</c>), <c>fx.In</c> and <c>fx.ExactIn</c>. <c>Now()</c> and <c>UTCNow()</c> are <see cref="ComputedExpr.Now"/>.
/// </remarks>
internal static class FormulaTranslator
{
    /// <summary>The longest formula, in characters (Learn).</summary>
    public const int MaxLength = 1000;

    /// <summary>The most formula and calculated columns a chain holding a formula column may hold, the column defined included (F10).</summary>
    public const int MaxDepth = 10;

    /// <summary>
    /// The functions whose result is an error (null in SQL) although their arguments have values: a division by zero (<c>Mod</c>), a
    /// value outside the range of formula decimals (<c>Sum</c>, <c>Average</c>, the rounding functions with a digit count outside
    /// int), a count or code outside its range (<c>Char</c>, <c>Left</c>, <c>Right</c>, <c>Mid</c>, <c>Replace</c>), a value outside
    /// the domain of a float function (<c>Exp</c>, <c>Ln</c>, <c>Sqrt</c>, <c>Power</c>). An error is not a blank: it stays an error
    /// (null) through every further operation (Power Fx; Dataverse, Q1 2026-11-19: <c>amount / 0 + 1</c> is null).
    /// </summary>
    private static readonly IReadOnlySet<string> MayFail = new HashSet<string>(StringComparer.Ordinal)
    {
        "Mod", "Sum", "Average", "Round", "RoundDown", "RoundUp", "Trunc", "Char", "Left", "Right", "Mid", "Replace",
        "Exp", "Ln", "Sqrt", "Power",
    };

    /// <summary>
    /// The functions whose SQL would not give null for an error argument (<c>GREATEST</c> skips a null, <c>CONCAT</c> reads it as
    /// ""): their call is guarded, null when an argument is an error.
    /// </summary>
    private static readonly IReadOnlySet<string> Guarded = new HashSet<string>(StringComparer.Ordinal)
    {
        "Max", "Min", "Len", "Replace", "Substitute", "Concatenate", "Concat", "Text",
    };

    /// <summary>The <c>SourceTypeMask</c> bit of a formula reading the columns of its own record.</summary>
    private const int OwnColumnsMask = 1;

    /// <summary>The range of a decimal value in a formula column (Learn): a literal outside it is refused.</summary>
    private const decimal Range = 100_000_000_000m;

    /// <summary>
    /// The engine of every parse and check, shared by all threads: only <c>Parse</c> and <c>Check</c> are called on it, which read its
    /// configuration and build a new result each time; no symbol, function or option set is ever added to it after construction.
    /// </summary>
    private static readonly Microsoft.PowerFx.Engine PowerFx = new(new PowerFxConfig());

    private static ParserOptions Options() => new() { NumberIsFloat = false, Culture = CultureInfo.InvariantCulture };

    /// <summary>The formula as stored: without surrounding whitespace and without one leading <c>=</c> (ruling F2).</summary>
    public static string Normalize(string formula) => FormulaColumnRules.Normalize(formula);

    /// <param name="entity">The table of the formula column.</param>
    /// <param name="attribute">The formula column.</param>
    /// <param name="formula">Its formula, with or without a leading <c>=</c>.</param>
    /// <param name="entities">Finds a table by logical name.</param>
    /// <param name="chained">The definition of a formula or calculated column this one reads (checks the chain depth and cycles).</param>
    /// <param name="cache">
    /// Keeps the parse of each formula text and the outcome of <c>Engine.Check</c> by formula text and the columns it reads (with their
    /// types), so that a query compilation does not run Power Fx again; null runs both each time. The translation of the parsed tree,
    /// which depends on the metadata passed in, runs on each call.
    /// </param>
    /// <exception cref="XrmdValidationException">The formula is not valid for the column.</exception>
    public static ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, string formula,
        Func<string, EntityMetadata?> entities, Func<EntityMetadata, AttributeMetadata, ComputedDefinition> chained,
        FormulaCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(chained);
        var where = $"'{entity.LogicalName}.{attribute.LogicalName}'";
        CheckColumnType(attribute, where);

        var text = Normalize(formula ?? "");
        if (text.Length == 0)
            throw new XrmdValidationException($"The formula column {where} has no formula.");
        if (text.Length > MaxLength)
            throw new XrmdValidationException(
                $"The formula column {where} has a formula of {text.Length} characters: a formula column takes 1,000 at most.");

        // Dataverse's Web API reads a formula in the caller's language (';' between arguments for an Italian user) and stores the
        // invariant form; xrmverse reads only that form (Q1 finding 7). Outside text, ';' is only Power Fx's chaining operator,
        // which formula columns refuse anyway: say why.
        if (HasSemicolon(text))
            throw new XrmdValidationException($"The formula column {where} uses ';', which formula columns do not read: use ',' between " +
                "arguments and '.' as decimal mark (the invariant form Dataverse stores).");

        var parse = cache is null ? Parse(text) : cache.Parse(text, Parse);
        if (FirstError(parse.Errors) is { } syntax)
            throw NotValid(where, syntax);

        var context = new Context(entity, attribute, entities, chained);
        var value = context.Value(parse.Root);

        // The check binds only the names the formula reads: the same text reading the same columns with the same types binds the same.
        string? Check() => FirstError(PowerFx.Check(CheckedText(text, parse.Root), Options(),
            ReadOnlySymbolTable.NewFromRecord(RecordOf(entity, entities, lookups: true), null, true, false)).Errors);
        if ((cache is null ? Check() : cache.Check(text, context.Reads, Check)) is { } binding)
            throw NotValid(where, binding);

        var definition = new ComputedDefinition(context.Result(value), context.Sources, 0)
        {
            DateBehavior = value.Kind is Kind.Date ? value.Behavior ?? Behaviors.UserLocal : null,
        };
        // A formula reading no column (constants, UTCNow()) has the mask of one reading its own record's columns: Dataverse gives
        // Mod(-7, 3) the mask 1 (Q1 2026-11-19, finding 9).
        var mask = ComputedColumnRules.SourceTypeMask(entity, definition, entities);
        return definition with { SourceTypeMask = mask == 0 ? OwnColumnsMask : mask };
    }

    private static ParseResult Parse(string text) => PowerFx.Parse(text, Options());

    /// <summary>A ';' outside text literals (<c>"…"</c>, with <c>""</c> for a quote) and quoted names (<c>'…'</c>).</summary>
    private static bool HasSemicolon(string text)
    {
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is { } q) { if (c == q) quote = null; }
            else if (c is '"' or '\'') quote = c;
            else if (c == ';') return true;
        }
        return false;
    }

    /// <summary>The types a formula column can have (Global Constraints); Money and Choice have their own texts.</summary>
    private static void CheckColumnType(AttributeMetadata attribute, string where)
    {
        switch (attribute)
        {
            case MoneyAttributeMetadata:
                throw new XrmdValidationException($"A formula column cannot be of type Money: {where}.");
            case PicklistAttributeMetadata:
                throw new XrmdValidationException($"Choice formula columns are not supported yet: {where}.");
            case StringAttributeMetadata or IntegerAttributeMetadata or DecimalAttributeMetadata or DoubleAttributeMetadata
                or BooleanAttributeMetadata or DateTimeAttributeMetadata:
                return;
            default:
                throw new XrmdValidationException($"A formula column cannot be of type {attribute.AttributeType}: {where}.");
        }
    }

    private static string? FirstError(IEnumerable<ExpressionError> errors) => errors.FirstOrDefault(e => !e.IsWarning)?.Message;

    private static XrmdValidationException NotValid(string where, string reason) =>
        new($"The formula column {where} has a formula that is not valid: {XamlWorkflowParser.Clip(reason.TrimEnd('.'), 600)}.");

    // ---- the text and record type Engine.Check reads -----------------------------------------------------------------------

    /// <summary>
    /// <paramref name="text"/> with the calls of the Dataverse functions the core library does not know renamed to their core
    /// counterpart (<see cref="FormulaFunctions.CheckedAs"/>). The tree holds only the nodes the translation accepted.
    /// </summary>
    private static string CheckedText(string text, TexlNode root)
    {
        var renames = new List<(Span Span, string Name)>();
        Collect(root);
        if (renames.Count == 0) return text;
        var result = new StringBuilder(text);
        foreach (var (span, name) in renames.OrderByDescending(r => r.Span.Min))
            result.Remove(span.Min, span.Lim - span.Min).Insert(span.Min, name);
        return result.ToString();

        void Collect(TexlNode node)
        {
            switch (node)
            {
                case CallNode call:
                    if (CheckedAs.TryGetValue(call.Head.Name.Value, out var core))
                        renames.Add((call.Head.Span, core));
                    foreach (var arg in call.Args.ChildNodes) Collect(arg);
                    break;
                case BinaryOpNode binary:
                    Collect(binary.Left);
                    Collect(binary.Right);
                    break;
                case UnaryOpNode unary:
                    Collect(unary.Child);
                    break;
            }
        }
    }

    /// <summary>
    /// The record type of <paramref name="entity"/>: its columns that hold a value, by logical name, with their display name where it
    /// is unique (F4); Currency columns as Decimal; with <paramref name="lookups"/>, each single-target lookup as the record of its
    /// target's columns (one level).
    /// </summary>
    private static RecordType RecordOf(EntityMetadata entity, Func<string, EntityMetadata?> entities, bool lookups)
    {
        var record = RecordType.Empty();
        var displays = DisplayNames(entity);
        foreach (var a in entity.Attributes ?? [])
        {
            if (string.IsNullOrEmpty(a.LogicalName)) continue;
            var type = TypeOf(a);
            if (type is null && lookups && SingleTarget(a) is { } target && entities(target) is { } parent)
            {
                // A related record is named by the lookup's navigation property (Dataverse, Q1 2026-11-19, finding 5).
                record = record.Add(NavigationOf(entity, (LookupAttributeMetadata)a), RecordOf(parent, entities, lookups: false));
                continue;
            }
            if (type is null) continue;
            var display = DisplayOf(a);
            record = display is not null && displays.TryGetValue(display, out var logical) && logical == a.LogicalName
                ? record.Add(a.LogicalName, type, display)
                : record.Add(a.LogicalName, type);
        }
        return record;
    }

    private static FormulaType? TypeOf(AttributeMetadata a) => a switch
    {
        StringAttributeMetadata or MemoAttributeMetadata => FormulaType.String,
        IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata or MoneyAttributeMetadata => FormulaType.Decimal,
        DoubleAttributeMetadata => FormulaType.Number,
        BooleanAttributeMetadata => FormulaType.Boolean,
        DateTimeAttributeMetadata d => BehaviorOf(d) switch
        {
            Behaviors.DateOnly => FormulaType.Date,
            Behaviors.TimeZoneIndependent => FormulaType.DateTimeNoTimeZone,
            _ => FormulaType.DateTime,
        },
        _ => null,
    };

    /// <summary>
    /// The navigation property of the lookup <paramref name="lookup"/> (<c>ReferencingEntityNavigationPropertyName</c>): the name a
    /// formula reads a related record by, case as in the metadata (<c>xrmv_AccountId.name</c>). The relationships of
    /// <paramref name="entity"/> when it carries them, otherwise the default: the SchemaName of a custom lookup, the logical name of a
    /// system one.
    /// </summary>
    private static string NavigationOf(EntityMetadata entity, LookupAttributeMetadata lookup) =>
        EntityMetadataExtensions.ReferencingNavigationName(entity, lookup, entity.ManyToOneRelationships);

    /// <summary>The target of a lookup to one table (not a Customer or Owner lookup); null otherwise.</summary>
    private static string? SingleTarget(AttributeMetadata a) =>
        a is LookupAttributeMetadata { Targets: [var target] } l && l.AttributeType is null or AttributeTypeCode.Lookup ? target : null;

    private static string? DisplayOf(AttributeMetadata a) =>
        a.DisplayName?.UserLocalizedLabel?.Label is { Length: > 0 } user ? user
        : a.DisplayName?.LocalizedLabels?.FirstOrDefault(l => !string.IsNullOrEmpty(l.Label))?.Label;

    /// <summary>The display names of the columns of <paramref name="entity"/> that name one column and are no column's logical name.</summary>
    private static Dictionary<string, string> DisplayNames(EntityMetadata entity)
    {
        var attributes = entity.Attributes ?? [];
        var logicals = attributes.Select(a => a.LogicalName).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        return attributes
            .Where(a => a.LogicalName is not null && DisplayOf(a) is { } d && !logicals.Contains(d))
            .GroupBy(a => DisplayOf(a)!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().LogicalName, StringComparer.Ordinal);
    }

    private static class Behaviors
    {
        public const string UserLocal = "UserLocal", DateOnly = "DateOnly", TimeZoneIndependent = "TimeZoneIndependent";

        public static string Describe(string? behavior) => behavior switch
        {
            DateOnly => "a Date only date",
            TimeZoneIndependent => "a Time zone independent date",
            _ => "a User local date",
        };
    }

    private static string BehaviorOf(DateTimeAttributeMetadata d) => d.DateTimeBehavior?.Value ?? Behaviors.UserLocal;

    // ---- the translation -------------------------------------------------------------------------------------------------

    /// <summary>The type of a value, as far as the rules need it.</summary>
    private enum Kind { Blank, Text, Decimal, Float, Boolean, Date, Currency, Unit, StartOfWeek }

    /// <summary>
    /// A translated value. <paramref name="Nullable"/>: it can be blank (a column, <c>Blank()</c>, an <c>If</c> without a branch that
    /// matches, a function of a blank date, ...); <paramref name="Behavior"/>: the <c>DateTimeBehavior</c> of a date;
    /// <paramref name="Name"/>: the column it reads, for messages; <paramref name="Condition"/>: a yes/no value as a condition, true
    /// only when the value is true (never when it is blank or an error).
    /// </summary>
    /// <remarks>
    /// Power Fx tells a blank from an error (Q1 2026-11-19, finding 1): a blank number operand is 0 and a blank text operand "", but
    /// an error (a division by zero, a value out of range, ...) stays an error through every further operation, and a formula column
    /// shows it as null. SQL has one null for both, so a value that can be an error says so (<see cref="Fails"/>): it is never
    /// wrapped in <c>fx.Zero</c> or <c>fx.Empty</c>, and a value that can be both (<see cref="BlankWhen"/>) is 0 or "" only when it is
    /// blank.
    /// </remarks>
    private sealed record Typed(ComputedExpr Expr, Kind Kind, bool Nullable, string? Behavior = null, string? Name = null,
        ComputedCondition? Condition = null)
    {
        /// <summary>The value can be an error (null in SQL).</summary>
        public bool Fails { get; init; }

        /// <summary>When the value can be blank and an error: true exactly when it is blank (its null is not an error).</summary>
        public ComputedCondition? BlankWhen { get; init; }

        /// <summary>When the value can be an error: true exactly when it is one; null derives it from the value.</summary>
        public ComputedCondition? ErrorWhen { get; init; }
    }

    /// <summary>A yes/no value as a condition: <paramref name="When"/> true only when it is true, <paramref name="Error"/> when it is an error.</summary>
    private readonly record struct Test(ComputedCondition When, ComputedCondition? Error);

    private sealed class Context(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities,
        Func<EntityMetadata, AttributeMetadata, ComputedDefinition> chained) : TexlFunctionalVisitor<Typed, object?>
    {
        private readonly List<ComputedExpr.Column> _sources = [];

        private readonly StringBuilder _reads = new();

        /// <summary>Every name the formula reads, as written, with the column it resolved to and its type (the key of the check).</summary>
        public string Reads => _reads.ToString();

        public IReadOnlyList<ComputedExpr.Column> Sources => _sources;

        private string Where => $"'{entity.LogicalName}.{attribute.LogicalName}'";

        private XrmdValidationException Refuse(string reason) => new($"The formula column {Where} {reason}.");

        private static readonly ComputedExpr True = new ComputedExpr.Literal(true);

        // ---- entry points --------------------------------------------------------------------------------------------

        /// <summary>The value of <paramref name="node"/>; a Currency column read directly is refused (F7).</summary>
        public Typed Value(TexlNode node) => Plain(Raw(node));

        private Typed Raw(TexlNode node) => node.Accept(this, null);

        /// <summary>Checks the value of the formula against the column type (F6) and gives its expression.</summary>
        public ComputedExpr Result(Typed value)
        {
            var fits = attribute switch
            {
                StringAttributeMetadata => value.Kind is Kind.Text,
                IntegerAttributeMetadata or DecimalAttributeMetadata => value.Kind is Kind.Decimal,
                DoubleAttributeMetadata => value.Kind is Kind.Float,
                BooleanAttributeMetadata => value.Kind is Kind.Boolean,
                DateTimeAttributeMetadata => value.Kind is Kind.Date,
                _ => false,
            };
            if (!fits)
                throw new XrmdValidationException(
                    $"The formula of {Where} gives {TypeName(value.Kind)}, but the column is {attribute.AttributeType}.");
            return value.Expr;
        }

        private static string TypeName(Kind kind) => kind switch
        {
            Kind.Text => "Text",
            Kind.Decimal => "Decimal",
            Kind.Float => "Float",
            Kind.Boolean => "Boolean",
            Kind.Date => "DateTime",
            Kind.Blank => "Blank",
            _ => kind.ToString(),
        };

        // ---- literals ------------------------------------------------------------------------------------------------

        public override Typed Visit(StrLitNode node, object? context) => new(new ComputedExpr.Literal(node.Value), Kind.Text, false);

        public override Typed Visit(DecLitNode node, object? context) => Number(node.ActualDecValue);

        public override Typed Visit(NumLitNode node, object? context) => Number((decimal)node.ActualNumValue);

        private Typed Number(decimal value)
        {
            if (Math.Abs(value) > Range)
                throw Refuse($"uses the number {value.ToString(CultureInfo.InvariantCulture)}, which is outside the range of formula columns " +
                    "(-100,000,000,000 to 100,000,000,000)");
            return new Typed(new ComputedExpr.Literal(value), Kind.Decimal, false);
        }

        public override Typed Visit(BoolLitNode node, object? context)
        {
            var literal = new ComputedExpr.Literal(node.Value);
            return new Typed(literal, Kind.Boolean, false, Condition: new ComputedCondition.Compare("Equal", literal, True));
        }

        // ---- names ---------------------------------------------------------------------------------------------------

        public override Typed Visit(FirstNameNode node, object? context) => Name([node.Ident.Name.Value]);

        public override Typed Visit(DottedNameNode node, object? context)
        {
            var path = new List<string>();
            TexlNode current = node;
            while (current is DottedNameNode dotted)
            {
                path.Insert(0, dotted.Right.Name.Value);
                current = dotted.Left;
            }
            if (current is not FirstNameNode first)
                throw Refuse($"uses '{node}', which formula columns do not support");
            path.Insert(0, first.Ident.Name.Value);
            return Name(path);
        }

        /// <summary>A name: a column, <c>ThisRecord.</c> a column, a lookup's column (one level), or a <c>TimeUnit</c> value.</summary>
        private Typed Name(List<string> path)
        {
            var typed = Resolve(path);
            _reads.Append(string.Join(".", path)).Append('=').Append(typed.Expr).Append(':').Append(typed.Kind).Append(':')
                .Append(typed.Behavior).Append('|');
            return typed;
        }

        private Typed Resolve(List<string> path)
        {
            var written = string.Join(".", path);
            if (path is ["ThisRecord", _, ..] && Find(entity, "ThisRecord") is null)
                path = path[1..];
            switch (path)
            {
                case ["TimeUnit", var unit] when Find(entity, "TimeUnit") is null:
                    return TimeUnits.Contains(unit)
                        ? new Typed(new ComputedExpr.Literal(unit), Kind.Unit, false)
                        : throw Refuse($"uses 'TimeUnit.{unit}', which formula columns do not support");
                case ["StartOfWeek", _] when Find(entity, "StartOfWeek") is null:
                    return new Typed(new ComputedExpr.Literal(path[1]), Kind.StartOfWeek, false);
                case [var name]:
                    return Column(entity, Find(entity, name) ?? throw Refuse($"reads '{name}', which is not a column of '{entity.LogicalName}'"), null);
                case [var lookupName, var name]:
                {
                    // A related record is read through the lookup's navigation property (Dataverse refuses the logical name, Q1).
                    var lookup = (AttributeMetadata?)(entity.Attributes ?? []).OfType<LookupAttributeMetadata>()
                                     .FirstOrDefault(l => SingleTarget(l) is not null && NavigationOf(entity, l) == lookupName)
                                 ?? Find(entity, lookupName)
                                 ?? (entity.Attributes ?? []).OfType<LookupAttributeMetadata>().FirstOrDefault(l =>
                                     string.Equals(l.LogicalName, lookupName, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(l.SchemaName, lookupName, StringComparison.OrdinalIgnoreCase))
                                 ?? throw Refuse($"reads '{written}', but '{lookupName}' is not a column of '{entity.LogicalName}'");
                    if (lookup is not LookupAttributeMetadata single)
                        throw Refuse($"reads '{written}', but '{entity.LogicalName}.{lookup.LogicalName}' is not a lookup");
                    var target = SingleTarget(lookup)
                        ?? throw Refuse($"reads '{written}' through '{lookup.LogicalName}', a lookup that can point to several tables: " +
                            "a formula column reads a related record through a lookup to one table");
                    if (NavigationOf(entity, single) is var navigation && navigation != lookupName)
                        throw Refuse($"reads '{written}' through the lookup's logical name or display name: a formula reads a related " +
                            $"record through the lookup's navigation property name; use '{navigation}.{name}'");
                    var parent = entities(target)
                        ?? throw Refuse($"reads '{written}' through '{lookup.LogicalName}', whose table '{target}' does not exist");
                    var column = Find(parent, name) ?? throw Refuse($"reads '{written}', but '{name}' is not a column of '{target}'");
                    return Column(parent, column, lookup.LogicalName);
                }
                default:
                    throw Refuse($"reads '{written}', which is more than one level away: a formula column reads the columns of its own " +
                        "record and of a related record one level away");
            }
        }

        /// <summary>A column of <paramref name="table"/> by logical name, or by a display name that names one column (F4).</summary>
        private static AttributeMetadata? Find(EntityMetadata table, string name)
        {
            var attributes = table.Attributes ?? [];
            return attributes.FirstOrDefault(a => a.LogicalName == name)
                ?? (DisplayNames(table).TryGetValue(name, out var logical) ? attributes.First(a => a.LogicalName == logical) : null);
        }

        private Typed Column(EntityMetadata owner, AttributeMetadata column, string? lookup)
        {
            var label = $"'{owner.LogicalName}.{column.LogicalName}'";
            if (lookup is null && column.LogicalName == attribute.LogicalName)
                throw Refuse("reads itself");
            if (column is MoneyAttributeMetadata { IsBaseCurrency: true })
                throw Refuse($"reads the base currency column {label}, which formula columns cannot read");
            if (RollupColumns.IsCompanion(column))
                throw Refuse($"reads {label}, a companion column of a rollup column, which formula columns cannot read");
            if (lookup is not null && column is MoneyAttributeMetadata)
                throw Refuse($"reads the currency column {label} of a related record, which formula columns cannot read");
            if (column.SourceType == ComputedColumnRules.Prompt)
                throw Refuse($"reads the prompt column {label}, which formula columns cannot read");
            if (column.SourceType is ComputedColumnRules.Calculated or ComputedColumnRules.Formula)
                chained(owner, column);
            else if (column is not (LookupAttributeMetadata or MoneyAttributeMetadata) && TypeMapping.ToColumns(column).Count == 0)
                throw Refuse($"reads {label}, which has no value of its own");

            var source = new ComputedExpr.Column(lookup, column.LogicalName);
            Typed typed = column switch
            {
                StringAttributeMetadata or MemoAttributeMetadata => new(source, Kind.Text, true, Name: label),
                IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata => new(source, Kind.Decimal, true, Name: label),
                DoubleAttributeMetadata => new(source, Kind.Float, true, Name: label),
                MoneyAttributeMetadata => new(source, Kind.Currency, true, Name: label),
                DateTimeAttributeMetadata date => new(source, Kind.Date, true, BehaviorOf(date), label),
                BooleanAttributeMetadata => new(source, Kind.Boolean, true, Name: label, Condition: Truthy(source, true)),
                LookupAttributeMetadata l => throw Refuse($"reads the lookup {label} as a value: read one of its record's columns " +
                    $"({NavigationOf(owner, l)}.<column>)"),
                _ => throw Refuse($"reads the column {label} of type {column.AttributeType}, which formula columns cannot read"),
            };
            if (!_sources.Contains(source))
                _sources.Add(source);
            return typed;
        }

        /// <summary>Refuses a Currency column read directly: only <c>Decimal(column)</c> reads it (F7, Learn).</summary>
        private Typed Plain(Typed value) => value.Kind is Kind.Currency
            ? throw Refuse($"reads the currency column {value.Name} directly: a formula column reads a currency column through " +
                $"Decimal({(value.Expr as ComputedExpr.Column)?.Attribute})")
            : value;

        // ---- operators -----------------------------------------------------------------------------------------------

        public override Typed Visit(UnaryOpNode node, object? context)
        {
            switch (node.Op)
            {
                case UnaryOp.Not:
                {
                    var test = Condition(Value(node.Child), "!");
                    return Bool(new ComputedCondition.Not(test.When), test.Error);
                }
                case UnaryOp.Minus:
                {
                    var operand = Value(node.Child);
                    if (operand.Expr is ComputedExpr.Literal { Value: decimal d })
                        return operand with { Expr = new ComputedExpr.Literal(-d) };
                    return new Typed(new ComputedExpr.Binary("Subtract", new ComputedExpr.Literal(0m), NumberArg(operand, "'-'")),
                        NumberKind(operand), false) { Fails = operand.Fails };
                }
                case UnaryOp.Percent:
                {
                    var operand = Value(node.Child);
                    return new Typed(new ComputedExpr.Binary("Divide", NumberArg(operand, "'%'"), new ComputedExpr.Literal(100m)),
                        NumberKind(operand), false) { Fails = operand.Fails };
                }
                default:
                    throw Refuse($"uses the operator '{node.Op}', which formula columns do not support");
            }
        }

        public override Typed Visit(BinaryOpNode node, object? context)
        {
            switch (node.Op)
            {
                case BinaryOp.And or BinaryOp.Or:
                {
                    var symbol = node.Op is BinaryOp.And ? "&&" : "||";
                    return Logical(node.Op is BinaryOp.And, [Condition(Value(node.Left), $"'{symbol}'"), Condition(Value(node.Right), $"'{symbol}'")]);
                }
                case BinaryOp.Concat:
                {
                    var left = Value(node.Left);
                    var right = Value(node.Right);
                    return Strict(Fx("Concat", TextOperand(left, "&"), TextOperand(right, "&")), Kind.Text, "Concat", [left, right], false, null);
                }
                case BinaryOp.Add when node.Right is UnaryOpNode { Op: UnaryOp.Minus } minus:
                    return Arithmetic("Subtract", "'-'", Value(node.Left), Value(minus.Child));
                case BinaryOp.Add:
                    return Arithmetic("Add", "'+'", Value(node.Left), Value(node.Right));
                case BinaryOp.Mul:
                    return Arithmetic("Multiply", "'*'", Value(node.Left), Value(node.Right));
                case BinaryOp.Div:
                    return Arithmetic("Divide", "'/'", Value(node.Left), Value(node.Right));
                case BinaryOp.Power:
                {
                    var left = Value(node.Left);
                    var right = Value(node.Right);
                    return new Typed(Fx("Power", NumberArg(left, "'^'"), NumberArg(right, "'^'")), Kind.Float, false) { Fails = true };
                }
                case BinaryOp.Equal:
                    return Comparison("Equal", "'='", Value(node.Left), Value(node.Right));
                case BinaryOp.NotEqual:
                    return Comparison("NotEqual", "'<>'", Value(node.Left), Value(node.Right));
                case BinaryOp.Less:
                    return Comparison("LessThan", "'<'", Value(node.Left), Value(node.Right));
                case BinaryOp.LessEqual:
                    return Comparison("LessEqual", "'<='", Value(node.Left), Value(node.Right));
                case BinaryOp.Greater:
                    return Comparison("GreaterThan", "'>'", Value(node.Left), Value(node.Right));
                case BinaryOp.GreaterEqual:
                    return Comparison("GreaterEqual", "'>='", Value(node.Left), Value(node.Right));
                case BinaryOp.In or BinaryOp.Exactin:
                {
                    var symbol = node.Op is BinaryOp.In ? "in" : "exactin";
                    var left = Value(node.Left);
                    var right = Value(node.Right);
                    return BoolFunction(Fx(node.Op is BinaryOp.In ? "In" : "ExactIn", TextOperand(left, symbol), TextOperand(right, symbol)),
                        [left, right]);
                }
                default:
                    throw Refuse($"uses the operator '{node.Op}', which formula columns do not support");
            }
        }

        /// <summary>
        /// <c>+ - * /</c> on numbers; a blank operand is 0 (F5). The result can be an error, never a blank: a division by zero, or a
        /// value outside ±100,000,000,000 (ComputedSql limits every operation of a formula row); an error stays an error (null) in a
        /// further operation (Q1 2026-11-19: <c>amount / 0 + 1</c> is null on Dataverse).
        /// </summary>
        private Typed Arithmetic(string op, string symbol, Typed left, Typed right) =>
            new(new ComputedExpr.Binary(op, NumberArg(left, symbol), NumberArg(right, symbol)),
                left.Kind is Kind.Float || right.Kind is Kind.Float ? Kind.Float : Kind.Decimal, false) { Fails = true };

        /// <summary>A comparison as a yes/no value: an error on either side makes it an error (Power Fx: error in, error out).</summary>
        private Typed Comparison(string op, string symbol, Typed left, Typed right) =>
            Bool(Compare(op, symbol, left, right), AnyOf(ErrorOf(left), ErrorOf(right)));

        /// <summary>
        /// A comparison. Blank on either side tests for null (F5); a relational operator sees a blank number as 0. Every condition
        /// this gives is two-valued in SQL (never unknown) when neither side is an error, so that <c>Not</c> of it is right (an error
        /// operand is tested first by <see cref="Comparison"/>): with a blank operand, <c>=</c> is true
        /// only when both sides are blank and <c>&lt;&gt;</c> is its negation (Power Fx: <c>Blank() &lt;&gt; 5</c> is true), and a relational
        /// operator on a blank date is false. <paramref name="nullSafe"/> false (the matches of <c>Switch</c>, never negated) keeps the
        /// plain comparison.
        /// </summary>
        private ComputedCondition Compare(string op, string symbol, Typed left, Typed right, bool nullSafe = true)
        {
            if (left.Kind is Kind.Blank || right.Kind is Kind.Blank)
            {
                var other = left.Kind is Kind.Blank ? right : left;
                return op switch
                {
                    "Equal" => new ComputedCondition.Compare("Null", other.Expr, null),
                    "NotEqual" => new ComputedCondition.Compare("NotNull", other.Expr, null),
                    _ => throw Refuse($"uses {symbol} with Blank(): only '=' and '<>' compare with it"),
                };
            }
            var equality = op is "Equal" or "NotEqual";
            if (IsNumber(left.Kind) && IsNumber(right.Kind))
            {
                if (!equality)
                    return new ComputedCondition.Compare(op, NumberArg(left, symbol), NumberArg(right, symbol));
            }
            else
            {
                if (left.Kind != right.Kind || left.Kind is Kind.Unit or Kind.StartOfWeek)
                    throw Refuse($"compares {Describe(left)} with {Describe(right)}");
                switch (left.Kind)
                {
                    case Kind.Date:
                        SameBehaviorFamily(symbol, left, right);
                        break;
                    case Kind.Text or Kind.Boolean when !equality:
                        throw Refuse($"uses {symbol} on {Describe(left)}: only '=' and '<>' compare it");
                }
                if (!equality)
                    return NotNull(left, NotNull(right, new ComputedCondition.Compare(op, left.Expr, right.Expr)));
            }

            if (!nullSafe)
                return new ComputedCondition.Compare(op, left.Expr, right.Expr);
            ComputedCondition same = NotNull(left, NotNull(right, new ComputedCondition.Compare("Equal", left.Expr, right.Expr)));
            if (left.Nullable && right.Nullable)
                same = new ComputedCondition.Or(new ComputedCondition.And(
                    new ComputedCondition.Compare("Null", left.Expr, null), new ComputedCondition.Compare("Null", right.Expr, null)), same);
            return op == "Equal" ? same : new ComputedCondition.Not(same);
        }

        /// <summary><paramref name="condition"/>, and <paramref name="value"/> not blank when it can be.</summary>
        private static ComputedCondition NotNull(Typed value, ComputedCondition condition) => value.Nullable
            ? new ComputedCondition.And(new ComputedCondition.Compare("NotNull", value.Expr, null), condition)
            : condition;

        // ---- calls ---------------------------------------------------------------------------------------------------

        public override Typed Visit(CallNode node, object? context)
        {
            var name = node.Head.Namespace.Length == 0 ? node.Head.Name.Value : $"{node.Head.Namespace.ToDottedSyntax()}.{node.Head.Name.Value}";
            if (!Supported.TryGetValue(name, out var spec))
                throw Refuse($"uses the function '{name}', which formula columns do not support");
            var nodes = node.Args.ChildNodes;
            if (nodes.Count < spec.Min || nodes.Count > spec.Max)
                throw Refuse($"calls {name} with {nodes.Count} argument(s): it takes " +
                    (spec.Min == spec.Max ? $"{spec.Min}" : spec.Max == int.MaxValue ? $"at least {spec.Min}" : $"{spec.Min} to {spec.Max}"));

            switch (name)
            {
                case "If": return If(nodes);
                case "Switch": return Switch(nodes);
                case "And" or "Or":
                    return Logical(name == "And", nodes.Select(n => Condition(Value(n), name)).ToList());
                case "Not":
                {
                    var test = Condition(Value(nodes[0]), name);
                    return Bool(new ComputedCondition.Not(test.When), test.Error);
                }
                case "IsBlank":
                {
                    // IsBlank of an error is the error (Power Fx: only IsError, IsBlankOrError and IfError take an error in).
                    var value = Value(nodes[0]);
                    return Bool(IsBlank(value), ErrorOf(value));
                }
                case "Blank": return new Typed(new ComputedExpr.Literal(null), Kind.Blank, true);
                case "Now": return new Typed(new ComputedExpr.Now(), Kind.Date, false, Behaviors.UserLocal);
                case "UTCNow": return new Typed(new ComputedExpr.Now(), Kind.Date, false, Behaviors.TimeZoneIndependent);
                // A date without a time of day, like a Date only column (it fits a Date only or Time zone independent column).
                case "UTCToday": return new Typed(Fx("UTCToday"), Kind.Date, false, Behaviors.DateOnly);
                case "Decimal": return Convert(name, Raw(nodes[0]), Kind.Decimal);
                case "Float": return Convert(name, Value(nodes[0]), Kind.Float);
                case "Value": return Convert(name, Value(nodes[0]), Kind.Decimal);
                case "Text": return Text(nodes);
                case "DateAdd": return DateAdd(nodes);
                case "DateDiff": return DateDiff(nodes);
                // ComputedSql finds the instance-th match by one CHARINDEX per match before it: a constant from 1 to 100.
                case "Substitute" when nodes.Count == 4 && !(nodes[3] is DecLitNode or NumLitNode
                                                             && Value(nodes[3]).Expr is ComputedExpr.Literal { Value: decimal instance }
                                                             && instance == decimal.Truncate(instance)
                                                             && instance is >= 1 and <= FormulaColumnRules.MaxSubstituteInstance):
                    throw Refuse("uses Substitute with an instance that is not a whole number from 1 to 100 written as a constant, " +
                        "which formula columns do not support");
                case "Weekday" or "WeekNum" when nodes.Count == 2:
                    throw Refuse($"uses the StartOfWeek argument of {name}, which formula columns do not support");
            }

            var args = nodes.Select(Value).ToList();
            var exprs = new List<ComputedExpr>(args.Count);
            for (var i = 0; i < args.Count; i++)
                exprs.Add(spec.ArgAt(i) switch
                {
                    Arg.Text => TextArg(args[i], name),
                    Arg.Number when name is "Max" or "Min" => Skipped(args[i], name),
                    Arg.Number => NumberArg(args[i], name),
                    Arg.Date => DateArg(args[i], name, i),
                    Arg.Unit => UnitArg(args[i], name),
                    _ => args[i].Expr,
                });
            if (DateParts.Contains(name) && args[0].Behavior is Behaviors.UserLocal)
                throw Refuse($"uses {name} on {Describe(args[0])}: a part of a User local date depends on the user's time zone; " +
                    "use a Time zone independent or Date only date");
            var call = Fx(name, [.. exprs]);
            // A blank date stays blank through a date function; a blank number or text argument is 0 or "".
            var blank = name is "Max" or "Min"
                // Max and Min leave a blank argument out (Q1 follow-up, finding 11): blank only when every argument is.
                ? args.All(a => a.Nullable) ? args.Select(BlankOf).Aggregate((all, b) => new ComputedCondition.And(all!, b!)) : null
                : AnyOf([.. args.Where(a => a.Kind is Kind.Date).Select(BlankOf)]);
            var fails = MayFail.Contains(name);
            return spec.Returns switch
            {
                Returns.Text => Strict(call, Kind.Text, name, args, fails, blank),
                Returns.Decimal => Strict(call, Kind.Decimal, name, args, fails, blank),
                Returns.Float => Strict(call, Kind.Float, name, args, fails, blank),
                Returns.SameNumber => Strict(call, args.Any(a => a.Kind is Kind.Float) ? Kind.Float : Kind.Decimal, name, args, fails, blank),
                Returns.Boolean => BoolFunction(call, args),
                _ => throw new InvalidOperationException($"The function '{name}' has no translation."),
            };
        }

        /// <summary>
        /// The value of the call <paramref name="call"/> of <paramref name="function"/> (Power Fx: error in, error out): an error when
        /// one of <paramref name="args"/> is one or, with <paramref name="fails"/>, when the function itself fails; otherwise blank when
        /// <paramref name="blank"/> holds. A <see cref="Guarded"/> function whose SQL would not give null for an error argument is
        /// null when one is an error.
        /// </summary>
        private static Typed Strict(ComputedExpr call, Kind kind, string function, IReadOnlyList<Typed> args, bool fails,
            ComputedCondition? blank, string? behavior = null)
        {
            var error = AnyOf([.. args.Select(ErrorOf)]);
            if (error is not null && Guarded.Contains(function))
                call = new ComputedExpr.Conditional([new ComputedBranch(new ComputedCondition.Not(error), call)], null);
            fails |= error is not null;
            return new Typed(call, kind, blank is not null, behavior)
            {
                Fails = fails,
                BlankWhen = blank is not null && fails ? Unless(error, blank) : null,
            };
        }

        /// <summary><c>If(c1, v1, [c2, v2, ...], [else])</c>: a condition that is an error makes the result that error (Power Fx).</summary>
        private Typed If(IReadOnlyList<TexlNode> nodes)
        {
            var branches = new List<(Test When, Typed Then)>();
            for (var i = 0; i + 1 < nodes.Count; i += 2)
                branches.Add((Condition(Value(nodes[i]), "If"), Value(nodes[i + 1])));
            var otherwise = nodes.Count % 2 == 1 ? Value(nodes[^1]) : null;
            return Branches("If", null, branches, otherwise);
        }

        /// <summary>
        /// <c>Switch(value, m1, r1, [m2, r2, ...], [default])</c>: a branch per match, compared with '='; a value or a match that is an
        /// error makes the result that error.
        /// </summary>
        private Typed Switch(IReadOnlyList<TexlNode> nodes)
        {
            var value = Value(nodes[0]);
            var branches = new List<(Test When, Typed Then)>();
            for (var i = 1; i + 1 < nodes.Count; i += 2)
            {
                var match = Value(nodes[i]);
                branches.Add((new Test(Compare("Equal", "Switch", value, match, nullSafe: false), ErrorOf(match)), Value(nodes[i + 1])));
            }
            var otherwise = nodes.Count % 2 == 0 ? Value(nodes[^1]) : null;
            return Branches("Switch", ErrorOf(value), branches, otherwise);
        }

        /// <summary>
        /// The <see cref="ComputedExpr.Conditional"/> of <c>If</c> and <c>Switch</c>; every result has the same type. A condition that
        /// is an error (and <paramref name="subjectError"/>, the value of <c>Switch</c>) gives null before the branches after it; the
        /// result is blank when no branch matches and there is no default.
        /// </summary>
        private Typed Branches(string name, ComputedCondition? subjectError, List<(Test When, Typed Then)> branches, Typed? otherwise)
        {
            var results = branches.Select(b => b.Then).Concat(otherwise is null ? [] : [otherwise]).ToList();
            var kinds = results.Where(r => r.Kind is not Kind.Blank).Select(r => IsNumber(r.Kind) ? Kind.Decimal : r.Kind).Distinct().ToList();
            if (kinds.Count > 1)
                throw Refuse($"gives values of different types in the branches of {name} " +
                    $"({string.Join(", ", results.Where(r => r.Kind is not Kind.Blank).Select(Describe).Distinct())})");
            var kind = kinds.Count == 0 ? Kind.Blank
                : results.Any(r => r.Kind is Kind.Float) ? Kind.Float
                : results.First(r => r.Kind is not Kind.Blank).Kind;
            if (kind is Kind.Unit or Kind.StartOfWeek)
                throw Refuse($"gives an enumeration value in {name}");
            string? behavior = null;
            if (kind is Kind.Date)
            {
                var dates = results.Where(r => r.Kind is Kind.Date).ToList();
                foreach (var date in dates.Skip(1))
                    SameBehaviorFamily(name, dates[0], date);
                behavior = dates.Select(d => d.Behavior).Distinct().Count() == 1 ? dates[0].Behavior : Behaviors.TimeZoneIndependent;
            }

            // The branches of the value, and of a yes/no telling whether it is blank (BlankWhen): an error gives null, and is no blank.
            var values = new List<ComputedBranch>();
            var blanks = new List<ComputedBranch>();
            var no = new ComputedExpr.Literal(false);
            if (subjectError is not null)
            {
                values.Add(new ComputedBranch(subjectError, Null));
                blanks.Add(new ComputedBranch(subjectError, no));
            }
            foreach (var (when, then) in branches)
            {
                if (when.Error is not null)
                {
                    values.Add(new ComputedBranch(when.Error, Null));
                    blanks.Add(new ComputedBranch(when.Error, no));
                }
                values.Add(new ComputedBranch(when.When, then.Expr));
                blanks.Add(new ComputedBranch(when.When, IsBlankFlag(then)));
            }
            var expr = new ComputedExpr.Conditional(values, otherwise?.Expr);
            var nullable = otherwise is null || results.Any(r => r.Nullable);
            var fails = subjectError is not null || branches.Any(b => b.When.Error is not null) || results.Any(r => r.Fails);
            var blankWhen = nullable && fails
                ? new ComputedCondition.Compare("Equal",
                    new ComputedExpr.Conditional(blanks, otherwise is null ? True : IsBlankFlag(otherwise)), True)
                : null;
            return new Typed(expr, kind, nullable, behavior,
                Condition: kind is Kind.Boolean ? Truthy(expr, nullable || fails) : null) { Fails = fails, BlankWhen = blankWhen };
        }

        /// <summary>A yes/no value: true when <paramref name="value"/> is blank (an error is not).</summary>
        private static ComputedExpr IsBlankFlag(Typed value) => value.Kind is Kind.Blank ? True
            : BlankOf(value) is { } blank ? new ComputedExpr.Conditional([new ComputedBranch(blank, True)], new ComputedExpr.Literal(false))
            : new ComputedExpr.Literal(false);

        /// <summary><c>Decimal</c>, <c>Float</c> and <c>Value</c>: a number from a number or text (<c>Decimal</c> also from a Currency column).</summary>
        private Typed Convert(string name, Typed value, Kind result)
        {
            if (value.Kind is not (Kind.Decimal or Kind.Float or Kind.Currency or Kind.Text or Kind.Blank))
                throw Refuse($"uses {name} on {Describe(value)}: it takes a number or text");
            if (name == "Value" && IsNumber(value.Kind))
                return value;
            // Text that is no number is an error; a blank or empty text is blank (Power Fx). A number converts as it is, but a float
            // outside the range of decimals is an error.
            var text = value.Kind is Kind.Text;
            var blank = text
                ? AnyOf(BlankOf(value), new ComputedCondition.Compare("Equal", value.Expr, new ComputedExpr.Literal("")))
                : BlankOf(value);
            return Strict(Fx(name, value.Expr), result, name, [value], text || (result is Kind.Decimal && value.Kind is Kind.Float), blank);
        }

        /// <summary><c>Text(number, format)</c>, the only number to text conversion (F6, F7).</summary>
        private Typed Text(IReadOnlyList<TexlNode> nodes)
        {
            var value = Value(nodes[0]);
            if (value.Kind is Kind.Text)
                throw Refuse("uses Text on text: formula columns take Text(number, format) only");
            if (nodes.Count == 1)
                throw Refuse("uses Text with a single argument: formula columns take Text(number, format)");
            if (nodes[1] is not StrLitNode format)
                throw Refuse("uses Text with a format that is not a constant text");
            if (!IsFormat(format.Value))
                throw Refuse($"uses the format \"{format.Value}\" in Text, which formula columns do not support: a format is made of 0, # " +
                    "and other characters than '.', ',', '%', ';', 'e', '[' and quotes (no locale tokens)");
            if (value.Kind is Kind.Date)
                throw Refuse($"passes {Describe(value)} to Text, a string function, which formula columns do not allow");
            return Strict(Fx("Text", NumberArg(value, "Text"), new ComputedExpr.Literal(format.Value)), Kind.Text, "Text", [value], false, null);
        }

        private static bool IsFormat(string format) =>
            format.Any(c => c is '0' or '#') && !format.Any(c => c is '.' or ',' or '%' or ';' or 'e' or 'E' or '[' or ']' or '"' or '\'' or '\\');

        /// <summary><c>DateAdd(date, count, [unit])</c>; the unit defaults to <c>Days</c>.</summary>
        private Typed DateAdd(IReadOnlyList<TexlNode> nodes)
        {
            var date = Value(nodes[0]);
            var dateExpr = DateArg(date, "DateAdd", 0);
            var countValue = Value(nodes[1]);
            var count = NumberArg(countValue, "DateAdd");
            var unit = nodes.Count == 3 ? UnitArg(Value(nodes[2]), "DateAdd") : new ComputedExpr.Literal("Days");
            if (date.Behavior is Behaviors.DateOnly && unit is ComputedExpr.Literal { Value: string u } && TimeOfDayUnits.Contains(u))
                throw Refuse($"uses DateAdd with {u} on {Describe(date)}: a time can only be added to a date and time");
            // A result outside the years 1 to 9999 is an error (null in ComputedSql), even for a date that has a value.
            return Strict(Fx("DateAdd", dateExpr, count, unit), Kind.Date, "DateAdd", [date, countValue], true, BlankOf(date), date.Behavior);
        }

        /// <summary><c>DateDiff(start, end, [unit])</c>; the unit defaults to <c>Days</c>; the behaviors must go together (Learn).</summary>
        private Typed DateDiff(IReadOnlyList<TexlNode> nodes)
        {
            var start = Value(nodes[0]);
            var end = Value(nodes[1]);
            var startExpr = DateArg(start, "DateDiff", 0);
            var endExpr = DateArg(end, "DateDiff", 1);
            SameBehaviorFamily("DateDiff", start, end);
            var unit = nodes.Count == 3 ? UnitArg(Value(nodes[2]), "DateDiff") : new ComputedExpr.Literal("Days");
            return Strict(Fx("DateDiff", startExpr, endExpr, unit), Kind.Decimal, "DateDiff", [start, end], false,
                AnyOf(BlankOf(start), BlankOf(end)));
        }

        /// <summary>A User local date goes only with another User local date; Date only and Time zone independent go together (Learn).</summary>
        private void SameBehaviorFamily(string what, Typed left, Typed right)
        {
            if ((left.Behavior is Behaviors.UserLocal) != (right.Behavior is Behaviors.UserLocal))
                throw Refuse($"uses {what} on {Describe(left)} and {Describe(right)}: a User local date only goes with another User local date");
        }

        /// <summary><c>IsBlank</c>: the real null; for text, also the empty text (Power Fx).</summary>
        private ComputedCondition IsBlank(Typed value)
        {
            if (value.Kind is Kind.Unit or Kind.StartOfWeek)
                throw Refuse("uses IsBlank on an enumeration value");
            var isNull = new ComputedCondition.Compare("Null", value.Expr, null);
            return value.Kind is Kind.Text
                ? new ComputedCondition.Or(isNull, new ComputedCondition.Compare("Equal", value.Expr, new ComputedExpr.Literal("")))
                : isNull;
        }

        // ---- arguments -----------------------------------------------------------------------------------------------

        /// <summary>A number operand of <paramref name="what"/>: a blank number is 0 (F5), an error stays an error (Q1 finding 1).</summary>
        private ComputedExpr NumberArg(Typed value, string what) => value.Kind switch
        {
            Kind.Blank => new ComputedExpr.Literal(0m),
            Kind.Decimal or Kind.Float => Defaulted(value, "Zero", new ComputedExpr.Literal(0m)),
            Kind.Text => throw Refuse($"uses text with {what}: formula columns do not convert text to a number; use Value(text)"),
            Kind.Date => throw Refuse($"uses {Describe(value)} with {what}: dates take DateAdd and DateDiff"),
            _ => throw Refuse($"uses {Describe(value)} with {what}: it needs a number"),
        };

        /// <summary>
        /// <paramref name="value"/> with a blank read as <paramref name="empty"/>: <c>fx.<paramref name="function"/></c> (a
        /// <c>COALESCE</c>) when it can only be blank, a <c>CASE</c> on <see cref="Typed.BlankWhen"/> when it can also be an error, whose
        /// null must stay null.
        /// </summary>
        private static ComputedExpr Defaulted(Typed value, string function, ComputedExpr empty) =>
            !value.Nullable ? value.Expr
            : !value.Fails ? Fx(function, value.Expr)
            : new ComputedExpr.Conditional([new ComputedBranch(BlankOf(value)!, empty)], value.Expr);

        /// <summary>
        /// A number argument of <c>Max</c> or <c>Min</c>: as it is, a blank one null, which <c>GREATEST</c> and <c>LEAST</c> leave out
        /// (Dataverse: <c>Max(Blank(), -5)</c> is -5, Q1 follow-up). An error argument is caught by the guard of the call.
        /// </summary>
        private ComputedExpr Skipped(Typed value, string what) => value.Kind switch
        {
            Kind.Blank => Null,
            Kind.Decimal or Kind.Float => value.Expr,
            _ => NumberArg(value, what), // refuses any other kind with its text
        };

        private static Kind NumberKind(Typed value) => value.Kind is Kind.Float ? Kind.Float : Kind.Decimal;

        /// <summary>A text argument of the string function <paramref name="function"/>: a blank text is "" (F5); no number or date (F7).</summary>
        private ComputedExpr TextArg(Typed value, string function) => value.Kind switch
        {
            Kind.Blank => new ComputedExpr.Literal(""),
            Kind.Text => Defaulted(value, "Empty", new ComputedExpr.Literal("")),
            Kind.Decimal or Kind.Float => throw Refuse($"passes a number to {function}, a string function: formula columns do not convert " +
                "numbers to text; use Text(number, format)"),
            Kind.Date => throw Refuse($"passes {Describe(value)} to {function}, a string function, which formula columns do not allow"),
            _ => throw Refuse($"passes {Describe(value)} to {function}, a string function: it needs text"),
        };

        /// <summary>A text operand of the operator <paramref name="symbol"/> (<c>&amp;</c>, <c>in</c>, <c>exactin</c>).</summary>
        private ComputedExpr TextOperand(Typed value, string symbol) => value.Kind switch
        {
            Kind.Blank or Kind.Text => TextArg(value, symbol),
            Kind.Decimal or Kind.Float => throw Refuse($"uses a number with '{symbol}': formula columns do not convert numbers to text; " +
                "use Text(number, format)"),
            _ => throw Refuse($"uses {Describe(value)} with '{symbol}': it needs text"),
        };

        private ComputedExpr DateArg(Typed value, string function, int index) => value.Kind is Kind.Date
            ? value.Expr
            : throw Refuse($"uses {function} with {Describe(value)} as argument {index + 1}: it needs a date");

        private ComputedExpr UnitArg(Typed value, string function) => value.Kind is Kind.Unit
            ? value.Expr
            : throw Refuse($"uses {function} with {Describe(value)} as its unit: it needs a TimeUnit value");

        // ---- yes/no values -------------------------------------------------------------------------------------------

        /// <summary>
        /// A condition as a value: <c>Conditional([When c, true], false)</c>; with <paramref name="error"/>, null first when it holds (the
        /// yes/no value is an error). <see cref="Typed.Condition"/> is true only when the value is.
        /// </summary>
        private static Typed Bool(ComputedCondition condition, ComputedCondition? error = null) => error is null
            ? new(new ComputedExpr.Conditional([new ComputedBranch(condition, True)], new ComputedExpr.Literal(false)), Kind.Boolean, false,
                Condition: condition)
            : new(new ComputedExpr.Conditional([new ComputedBranch(error, Null), new ComputedBranch(condition, True)],
                new ComputedExpr.Literal(false)), Kind.Boolean, false, Condition: Unless(error, condition)) { Fails = true, ErrorWhen = error };

        /// <summary>A yes/no <c>fx.</c> function (never blank) as a value: an error when one of <paramref name="args"/> is.</summary>
        private static Typed BoolFunction(ComputedExpr call, IReadOnlyList<Typed> args) =>
            Bool(new ComputedCondition.Compare("Equal", call, True), AnyOf([.. args.Select(ErrorOf)]));

        /// <summary>
        /// <c>And</c> / <c>Or</c> (and <c>&amp;&amp;</c> / <c>||</c>), evaluated from left to right and stopping as Power Fx does: an
        /// error is the result only when it is reached (<c>And(false, 1/0 &gt; 0)</c> is false, <c>Or(true, 1/0 &gt; 0)</c> true).
        /// </summary>
        private static Typed Logical(bool and, IReadOnlyList<Test> tests)
        {
            var result = tests[0];
            foreach (var next in tests.Skip(1))
                result = and
                    ? new Test(new ComputedCondition.And(result.When, next.When),
                        AnyOf(result.Error, next.Error is null ? null : new ComputedCondition.And(result.When, next.Error)))
                    : new Test(new ComputedCondition.Or(result.When, next.When),
                        AnyOf(result.Error, next.Error is null ? null : new ComputedCondition.And(new ComputedCondition.Not(result.When), next.Error)));
            return Bool(result.When, result.Error);
        }

        /// <summary>A yes/no value as a condition: true only when it is not blank and true (a blank one is false, also under Not).</summary>
        private static ComputedCondition Truthy(ComputedExpr value, bool nullable)
        {
            var isTrue = new ComputedCondition.Compare("Equal", value, True);
            return nullable ? new ComputedCondition.And(new ComputedCondition.Compare("NotNull", value, null), isTrue) : isTrue;
        }

        private Test Condition(Typed value, string what) => value.Kind is Kind.Boolean
            ? new Test(value.Condition ?? Truthy(value.Expr, value.Nullable || value.Fails), ErrorOf(value))
            : throw Refuse($"uses {Describe(value)} as a condition of {what}: it needs a yes/no value");

        // ---- blanks and errors ---------------------------------------------------------------------------------------

        private static readonly ComputedExpr Null = new ComputedExpr.Literal(null);

        private static ComputedCondition IsNull(ComputedExpr value) => new ComputedCondition.Compare("Null", value, null);

        /// <summary>True exactly when <paramref name="value"/> is blank; null when it never is.</summary>
        private static ComputedCondition? BlankOf(Typed value) =>
            !value.Nullable ? null
            : value.Fails ? value.BlankWhen ?? throw new InvalidOperationException("A value that can be blank and an error needs BlankWhen.")
            : IsNull(value.Expr);

        /// <summary>True exactly when <paramref name="value"/> is an error; null when it never is.</summary>
        private static ComputedCondition? ErrorOf(Typed value) =>
            !value.Fails ? null
            : value.ErrorWhen ?? (value.Nullable
                ? new ComputedCondition.And(IsNull(value.Expr), new ComputedCondition.Not(BlankOf(value)!))
                : IsNull(value.Expr));

        /// <summary>Any of the conditions (null ones left out); null when there is none.</summary>
        private static ComputedCondition? AnyOf(params ComputedCondition?[] conditions) =>
            conditions.OfType<ComputedCondition>().Aggregate((ComputedCondition?)null, (all, c) => all is null ? c : new ComputedCondition.Or(all, c));

        /// <summary><paramref name="condition"/>, and not <paramref name="error"/> when there is one.</summary>
        private static ComputedCondition Unless(ComputedCondition? error, ComputedCondition condition) =>
            error is null ? condition : new ComputedCondition.And(new ComputedCondition.Not(error), condition);

        // ---- helpers -------------------------------------------------------------------------------------------------

        private static ComputedExpr.Function Fx(string name, params ComputedExpr[] args) => new("fx." + name, args);

        private static bool IsNumber(Kind kind) => kind is Kind.Decimal or Kind.Float;

        private static string Describe(Typed value)
        {
            var kind = value.Kind switch
            {
                Kind.Text => "text",
                Kind.Decimal => "a number",
                Kind.Float => "a float number",
                Kind.Currency => "a currency value",
                Kind.Date => Behaviors.Describe(value.Behavior),
                Kind.Boolean => "a yes/no value",
                Kind.Unit => "a TimeUnit value",
                Kind.StartOfWeek => "a StartOfWeek value",
                _ => "a blank value",
            };
            return value.Name is null ? kind : $"{kind} ({value.Name})";
        }

        // ---- constructs formula columns do not support -----------------------------------------------------------------

        private Typed Unsupported(TexlNode node, string what) => throw Refuse($"uses {what} ('{XamlWorkflowParser.Clip(node.ToString(), 60)}'), which formula columns do not support");

        public override Typed Visit(TypeLiteralNode node, object? context) => Unsupported(node, "a type literal");
        public override Typed Visit(ErrorNode node, object? context) => Unsupported(node, "an expression that cannot be read");
        public override Typed Visit(BlankNode node, object? context) => Unsupported(node, "an empty expression");
        public override Typed Visit(ParentNode node, object? context) => Unsupported(node, "Parent");
        public override Typed Visit(SelfNode node, object? context) => Unsupported(node, "Self");
        public override Typed Visit(StrInterpNode node, object? context) => Unsupported(node, "string interpolation");
        public override Typed Visit(VariadicOpNode node, object? context) => Unsupported(node, "a chain of expressions");
        public override Typed Visit(ListNode node, object? context) => Unsupported(node, "a list");
        public override Typed Visit(RecordNode node, object? context) => Unsupported(node, "a record");
        public override Typed Visit(TableNode node, object? context) => Unsupported(node, "a table");
        public override Typed Visit(AsNode node, object? context) => Unsupported(node, "As");
    }
}
