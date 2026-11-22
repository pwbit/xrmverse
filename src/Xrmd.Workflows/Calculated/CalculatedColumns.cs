using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Xml;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Computed;
using Xrmd.Query.Sql;
using Xrmd.Workflows.Formula;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Calculated;

/// <summary>
/// The calculated columns of plan 2.12a and the formula columns of plan 2.12c. A calculated column (<c>SourceType</c> 1): reads the
/// XAML of its <c>FormulaDefinition</c> with the 2.14 front end (<see cref="XamlWorkflowParser.ParseCalculated"/>) and translates it with
/// <see cref="CalculatedTranslator"/>; parses are cached by (table, column, definition text). A formula column (<c>SourceType</c> 3):
/// translates its Power Fx formula with <see cref="FormulaTranslator"/>; parses and checks are cached (<see cref="FormulaCache"/>). The
/// translation, which depends on the metadata passed in, runs on each call (a tree walk). Every refusal is an <see cref="XrmdValidationException"/> naming the column.
/// </summary>
/// <param name="dialect">The SQL a formula column is checked in (<see cref="FormulaSqlCheck"/>; plan 2.7a).</param>
public sealed class CalculatedColumns(ISqlDialect dialect) : IComputedColumns
{
    /// <summary>Above this many cached parses the cache starts over (definitions replaced by updates are never read again).</summary>
    private const int MaxCachedParses = 4096;

    private readonly ConcurrentDictionary<(string Entity, string Attribute, string? Type, string Xaml), Parsed> _parsed = new();

    /// <summary>The parses and checks of formula columns (<see cref="FormulaCache"/>).</summary>
    internal FormulaCache Formulas { get; } = new();

    /// <summary>A parse result: the definition, or the reason it was refused.</summary>
    private sealed record Parsed(WorkflowDefinition? Definition, string? Error);

    /// <inheritdoc/>
    public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(entities);
        return Resolve(entity, attribute, entities, [], null, false, []);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A formula column is also compiled to the SQL a query reading it would hold (<see cref="FormulaSqlCheck"/>), refused when it
    /// is too large or nests too deeply (plan 2.12c, final review C1, I1).
    /// </remarks>
    public ComputedDefinition Check(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities)
    {
        var definition = Translate(entity, attribute, entities);
        if (attribute.SourceType == ComputedColumnRules.Formula)
            FormulaSqlCheck.Check(entity, attribute, entities, this, dialect);
        return definition;
    }

    private static string Key(EntityMetadata entity, AttributeMetadata attribute) => $"{entity.LogicalName}.{attribute.LogicalName}";

    /// <summary>
    /// Translates one column, calculated (<c>SourceType</c> 1, XAML) or formula (<c>SourceType</c> 3, Power Fx). <paramref name="chain"/>
    /// holds the computed columns that led to it, the first one outermost (<paramref name="root"/> is "calculated" or "formula", what
    /// that first one is), and <paramref name="formulas"/> tells whether it holds a formula column: a chain holding a formula column may
    /// hold <see cref="FormulaTranslator.MaxDepth"/> columns, a chain of calculated columns only <see cref="CalculatedTranslator.MaxDepth"/>
    /// (plan 2.12c, ruling F10). <paramref name="resolved"/> keeps the columns of the chain already translated in this call, by column,
    /// chain length and kind of chain, so that a column read several times (by one formula, or by several columns of the chain) is
    /// translated once (final review C1: a chain whose links read the previous one 4 times was translated 4^n times).
    /// </summary>
    private ComputedDefinition Resolve(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities,
        ImmutableArray<string> chain, string? root, bool formulas,
        Dictionary<(string Column, int Length, bool Formulas), ComputedDefinition> resolved)
    {
        var path = chain.Add(Key(entity, attribute));
        var isFormula = attribute.SourceType == ComputedColumnRules.Formula;
        root ??= isFormula ? "formula" : "calculated";
        formulas |= isFormula;

        ComputedDefinition Nested(EntityMetadata e, AttributeMetadata a)
        {
            var next = Key(e, a);
            var mixed = formulas || a.SourceType == ComputedColumnRules.Formula;
            var columns = mixed ? "formula or calculated columns" : "calculated columns";
            var limit = mixed ? FormulaTranslator.MaxDepth : CalculatedTranslator.MaxDepth;
            if (path.Contains(next))
                throw new XrmdValidationException(
                    $"The {root} column '{path[0]}' is part of a cycle of {columns}: {string.Join(" -> ", path.Add(next))}.");
            if (path.Length + 1 > limit)
                throw new XrmdValidationException(
                    $"The {root} column '{path[0]}' chains more than {limit} {columns}: {string.Join(" -> ", path.Add(next))}.");
            // The checks above depend on the path; what a column translates to does not (a cycle through it would have failed
            // its first translation), so a column met again at the same length of chain is not translated again.
            if (resolved.TryGetValue((next, path.Length, mixed), out var done)) return done;
            return resolved[(next, path.Length, mixed)] = Resolve(e, a, entities, path, root, mixed, resolved);
        }

        if (isFormula)
        {
            var formula = ComputedColumnRules.FormulaOf(attribute);
            if (string.IsNullOrWhiteSpace(formula))
                throw new XrmdValidationException($"The formula column '{Key(entity, attribute)}' has no FormulaDefinition.");
            try
            {
                return FormulaTranslator.Translate(entity, attribute, formula, entities, Nested, Formulas);
            }
            catch (Exception ex) when (ex is not XrmdValidationException && IsDefinitionError(ex))
            {
                throw new XrmdValidationException($"The formula column '{Key(entity, attribute)}' has a formula that is not valid: " +
                    $"{XamlWorkflowParser.Clip(ex.Message.TrimEnd('.'), 600)}.", ex);
            }
        }

        var parsed = Parse(entity, attribute);
        try
        {
            return CalculatedTranslator.Translate(entity, attribute, parsed, entities, Nested);
        }
        catch (Exception ex) when (ex is not XrmdValidationException && IsDefinitionError(ex))
        {
            throw Invalid(entity, attribute, ex.Message, ex);
        }
    }

    /// <summary>
    /// The CLR type a definition writes as the <c>TargetType</c> of the steps that compute a column of the type of
    /// <paramref name="attribute"/> (2.12a parity export); null for a type that cannot be calculated.
    /// </summary>
    public static string? TargetTypeOf(AttributeMetadata attribute) => attribute switch
    {
        StringAttributeMetadata => "System.String",
        IntegerAttributeMetadata => "System.Int32",
        DecimalAttributeMetadata => "System.Decimal",
        MoneyAttributeMetadata => "Microsoft.Xrm.Sdk.Money",
        BooleanAttributeMetadata => "System.Boolean",
        DateTimeAttributeMetadata => "System.DateTime",
        PicklistAttributeMetadata => "Microsoft.Xrm.Sdk.OptionSetValue",
        _ => null,
    };

    /// <summary>The parsed definition of a column, from the cache when its text was read before.</summary>
    private WorkflowDefinition Parse(EntityMetadata entity, AttributeMetadata attribute)
    {
        var xaml = ComputedColumnRules.FormulaOf(attribute);
        if (string.IsNullOrWhiteSpace(xaml))
            throw new XrmdValidationException($"The calculated column '{Key(entity, attribute)}' has no FormulaDefinition.");

        var type = TargetTypeOf(attribute);
        var key = (entity.LogicalName, attribute.LogicalName, type, xaml);
        if (!_parsed.TryGetValue(key, out var parsed))
        {
            try
            {
                parsed = new Parsed(XamlWorkflowParser.ParseCalculated(xaml, entity.LogicalName, attribute.LogicalName, type), null);
            }
            catch (Exception ex) when (IsDefinitionError(ex))
            {
                parsed = new Parsed(null, ex.Message);
            }
            if (_parsed.Count >= MaxCachedParses)
                _parsed.Clear();
            _parsed[key] = parsed;
        }
        return parsed.Definition ?? throw Invalid(entity, attribute, parsed.Error!, null);
    }

    /// <summary>
    /// The exceptions a definition that cannot be read raises (the parser's own, and what the XML and expression readers throw):
    /// they become an <see cref="XrmdValidationException"/>, so that callers see one type (<see cref="ComputedColumnRules.ApplyMask"/>).
    /// </summary>
    private static bool IsDefinitionError(Exception ex) =>
        ex is XamlWorkflowException or XmlException or FormatException or OverflowException or ArgumentException or InvalidOperationException;

    private static XrmdValidationException Invalid(EntityMetadata entity, AttributeMetadata attribute, string reason, Exception? inner)
    {
        var message = $"The calculated column '{Key(entity, attribute)}' has a definition that is not valid: {XamlWorkflowParser.Clip(reason.TrimEnd('.'), 600)}.";
        return inner is null ? new XrmdValidationException(message) : new XrmdValidationException(message, inner);
    }
}
