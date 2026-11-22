using System.Collections.Concurrent;
using System.Xml;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Rollup;

/// <summary>
/// The rollup columns of plan 2.12b: reads the XAML of a column's <c>FormulaDefinition</c> with the 2.14 front end
/// (<see cref="XamlWorkflowParser.ParseRollup"/>) and translates it with <see cref="RollupTranslator"/>. Parses are cached by (table,
/// column, definition text); the translation, which depends on the metadata passed in, runs on each call. A definition that cannot be
/// read is an <see cref="XrmdValidationException"/> naming the column; a refused relationship or filter is an
/// <see cref="XrmdFaultException"/> with its rollup error code.
/// </summary>
public sealed class RollupColumnsService : IRollupColumns
{
    /// <summary>Above this many cached parses the cache starts over (definitions replaced by updates are never read again).</summary>
    private const int MaxCachedParses = 4096;

    private readonly ConcurrentDictionary<(string Entity, string Attribute, string Xaml), Parsed> _parsed = new();

    private int _parses;

    /// <summary>How many definition texts this instance has parsed (not read from its cache); for tests.</summary>
    internal int Parses => Volatile.Read(ref _parses);

    /// <summary>A parse result: the definition, or why it was refused (a reason, or the filter condition that was refused).</summary>
    private sealed record Parsed(RollupXaml? Definition, string? Error, XamlRollupConditionException? Condition);

    /// <inheritdoc/>
    public RollupDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities,
        Func<string, OneToManyRelationshipMetadata?> relationships)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        return RollupTranslator.Translate(entity, attribute, Parse(entity, attribute), entities, relationships);
    }

    private static string Key(EntityMetadata entity, AttributeMetadata attribute) => $"'{entity.LogicalName}.{attribute.LogicalName}'";

    /// <summary>The parsed definition of a column, from the cache when its text was read before.</summary>
    private RollupXaml Parse(EntityMetadata entity, AttributeMetadata attribute)
    {
        var xaml = ComputedColumnRules.FormulaOf(attribute);
        if (string.IsNullOrWhiteSpace(xaml))
            throw new XrmdValidationException($"The rollup column {Key(entity, attribute)} has no FormulaDefinition.");

        var key = (entity.LogicalName, attribute.LogicalName, xaml);
        if (!_parsed.TryGetValue(key, out var parsed))
        {
            Interlocked.Increment(ref _parses);
            try
            {
                parsed = new Parsed(XamlWorkflowParser.ParseRollup(xaml), null, null);
            }
            catch (XamlRollupConditionException ex)
            {
                parsed = new Parsed(null, null, ex);
            }
            catch (Exception ex) when (IsDefinitionError(ex))
            {
                parsed = new Parsed(null, ex.Message, null);
            }
            if (_parsed.Count >= MaxCachedParses)
                _parsed.Clear();
            _parsed[key] = parsed;
        }

        if (parsed.Definition is { } definition)
            return definition;
        if (parsed.Condition is { } condition)
            throw new XrmdFaultException(
                condition.OnRelatedRows ? ErrorCodes.RollupFieldsTargetFilterConditionInvalid : ErrorCodes.RollupFieldsSourceFilterConditionInvalid,
                $"The filter of the rollup column {Key(entity, attribute)} on {(condition.OnRelatedRows ? "the related rows" : "its own rows")} " +
                $"uses the condition operator '{XamlWorkflowParser.Clip(condition.ConditionOperator)}', which is not supported.", 400);
        throw new XrmdValidationException(
            $"The rollup column {Key(entity, attribute)} has a definition that is not valid: {XamlWorkflowParser.Clip(parsed.Error!.TrimEnd('.'), 600)}.");
    }

    /// <summary>The exceptions a definition that cannot be read raises (the parser's own, and what the XML and expression readers throw).</summary>
    private static bool IsDefinitionError(Exception ex) =>
        ex is XamlWorkflowException or XmlException or FormatException or OverflowException or ArgumentException or InvalidOperationException;
}
