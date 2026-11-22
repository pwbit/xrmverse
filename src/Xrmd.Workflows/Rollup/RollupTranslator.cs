using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Rollup;

/// <summary>
/// Turns a rollup column definition read by <see cref="XamlWorkflowParser.ParseRollup"/> into a <see cref="RollupDefinition"/> and
/// checks its relationships and filters against the metadata (plan 2.12b, rulings R3, R4, R5): the related rows through a one-to-many
/// relationship from the rollup's table (not to itself, not to <c>activitypointer</c> or <c>activityparty</c>), the hierarchy through
/// the table's hierarchical relationship, and filter conditions that compare a column of the filtered row with a constant. The
/// aggregate and type matrix, and what kind of column may be read, are checked by <see cref="RollupColumnRules"/>.
/// </summary>
internal static class RollupTranslator
{
    /// <summary>The tables whose one-to-many relationships a rollup cannot aggregate over (ruling R4).</summary>
    private static readonly HashSet<string> NotAggregatable = new(StringComparer.Ordinal) { "activitypointer", "activityparty" };

    /// <summary>The comparison operators a filter condition may use: those of 2.12a's calculated column conditions (ruling R5).</summary>
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal)
    {
        "Equal", "NotEqual", "GreaterThan", "GreaterEqual", "LessThan", "LessEqual", "Null", "NotNull",
        "Contains", "DoesNotContain", "BeginsWith", "EndsWith",
    };

    private static readonly HashSet<string> TextOperators = new(StringComparer.Ordinal) { "Contains", "DoesNotContain", "BeginsWith", "EndsWith" };

    /// <param name="entity">The table of the rollup column.</param>
    /// <param name="attribute">The rollup column.</param>
    /// <param name="parsed">Its definition, read by <see cref="XamlWorkflowParser.ParseRollup"/>.</param>
    /// <param name="entities">Finds a table by logical name.</param>
    /// <param name="relationships">Finds a one-to-many relationship by SchemaName.</param>
    /// <exception cref="XrmdFaultException">400 with the rollup error code of the refused definition.</exception>
    /// <exception cref="XrmdValidationException">The definition does not fit together.</exception>
    public static RollupDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, RollupXaml parsed,
        Func<string, EntityMetadata?> entities, Func<string, OneToManyRelationshipMetadata?> relationships)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        var table = entity.LogicalName;
        var where = $"'{table}.{attribute.LogicalName}'";

        var aggregate = parsed.Operator switch
        {
            "Sum" => RollupAggregate.Sum,
            "Count" => RollupAggregate.Count,
            "Maximum" => RollupAggregate.Max,
            "Minimum" => RollupAggregate.Min,
            "Average" => RollupAggregate.Avg,
            var op => throw new XrmdValidationException($"The rollup column {where} uses the aggregate operator '{op}', which is not supported."),
        };
        if (parsed.Related is null && parsed.HierarchicalRelationship is null)
            throw Fault(ErrorCodes.RollupFieldsTargetRelationshipNull,
                $"The rollup column {where} names neither related rows nor the hierarchy of '{table}': it has nothing to aggregate.");

        List<RollupSource> sources = [];
        void Read(string owner, string column)
        {
            var source = new RollupSource(owner, column);
            if (!sources.Contains(source)) sources.Add(source);
        }

        // The aggregated column belongs to the related rows, or to the rollup's own table without them.
        var aggregatedEntity = parsed.Related?.Entity ?? table;
        if (parsed.AggregateEntity != aggregatedEntity)
            throw new XrmdValidationException(
                $"The rollup column {where} aggregates a column of '{parsed.AggregateEntity}', but the rows it aggregates are of '{aggregatedEntity}'.");
        Read(aggregatedEntity, parsed.AggregateAttribute);

        RollupRelated? related = null;
        if (parsed.Related is { } r)
        {
            var relatedTable = Related(entity, where, r, entities, relationships);
            Read(r.Entity, r.Lookup);
            var filter = r.Filter is null
                ? null
                : new Filter(relatedTable, where, "the related rows", ErrorCodes.RollupFieldsTargetFilterConditionInvalid).Condition(r.Filter);
            foreach (var column in RollupColumnRules.ColumnsOf(filter)) Read(r.Entity, column);
            related = new RollupRelated(r.Relationship, r.Entity, r.Lookup, filter);
        }

        ComputedCondition? sourceFilter = null;
        if (parsed.SourceFilter is { } own)
        {
            if (parsed.SourceFilterEntity is { } filtered && filtered != table)
                throw new XrmdValidationException(
                    $"The rollup column {where} filters its own rows with columns of '{filtered}', not of '{table}'.");
            sourceFilter = new Filter(entity, where, "its own rows", ErrorCodes.RollupFieldsSourceFilterConditionInvalid).Condition(own);
            foreach (var column in RollupColumnRules.ColumnsOf(sourceFilter)) Read(table, column);
        }

        if (parsed.HierarchicalRelationship is { } hierarchy)
            Read(table, Hierarchy(entity, where, hierarchy, relationships));

        return new RollupDefinition(aggregate, parsed.AggregateAttribute, related, parsed.HierarchicalRelationship, sourceFilter, sources);
    }

    /// <summary>The table of the related rows, once the relationship is checked (ruling R4).</summary>
    private static EntityMetadata Related(EntityMetadata entity, string where, RollupXamlRelated related, Func<string, EntityMetadata?> entities,
        Func<string, OneToManyRelationshipMetadata?> relationships)
    {
        var table = entity.LogicalName;
        if (related.Entity == table)
            throw Fault(ErrorCodes.RollupFieldsTargetSameAsSourceEntity,
                $"The rollup column {where} aggregates rows of its own table '{table}' through '{related.Relationship}': use the hierarchy instead.");
        if (NotAggregatable.Contains(related.Entity))
            throw Fault(ErrorCodes.RollupFieldsTargetEntityNotValid,
                $"The rollup column {where} cannot aggregate rows of '{related.Entity}' (relationship '{related.Relationship}').");

        var relationship = relationships(related.Relationship);
        if (relationship is null)
            throw NotOneToMany($"names the relationship '{related.Relationship}', which is not a one-to-many relationship of the organization");
        if (relationship.ReferencedEntity != table || relationship.ReferencingEntity != related.Entity
            || relationship.ReferencingAttribute != related.Lookup)
            throw NotOneToMany($"names the relationship '{related.Relationship}' from '{table}' to '{related.Entity}' through " +
                $"'{related.Lookup}', but that relationship goes from '{relationship.ReferencedEntity}' to '{relationship.ReferencingEntity}' " +
                $"through '{relationship.ReferencingAttribute}'");
        var relatedTable = entities(related.Entity)
            ?? throw NotOneToMany($"aggregates rows of '{related.Entity}', which does not exist");
        if (relatedTable.Attributes?.FirstOrDefault(a => a.LogicalName == related.Lookup) is not LookupAttributeMetadata)
            throw NotOneToMany($"follows '{related.Entity}.{related.Lookup}', which is not a lookup column");
        return relatedTable;

        XrmdFaultException NotOneToMany(string reason) =>
            Fault(ErrorCodes.RollupFieldsTargetRelationshipNotPartOfOneToNRelationship, $"The rollup column {where} {reason}.");
    }

    /// <summary>The lookup of the table's hierarchical relationship <paramref name="name"/>, once it is checked (ruling R4).</summary>
    private static string Hierarchy(EntityMetadata entity, string where, string name, Func<string, OneToManyRelationshipMetadata?> relationships)
    {
        var table = entity.LogicalName;
        var relationship = relationships(name);
        if (relationship is not { IsHierarchical: true, ReferencingAttribute: { } lookup }
            || relationship.ReferencedEntity != table || relationship.ReferencingEntity != table)
            throw Fault(ErrorCodes.RollupFieldsSourceEntityNotHierarchical,
                $"The rollup column {where} aggregates over the hierarchy '{name}', which is not the hierarchical relationship of '{table}'.");
        return lookup;
    }

    private static XrmdFaultException Fault(int code, string message) => new(code, message, 400);

    /// <summary>The conditions of one filter: a column of the filtered row (of <paramref name="table"/>) compared with a constant.</summary>
    private sealed class Filter(EntityMetadata table, string where, string rows, int code)
    {
        private XrmdFaultException Refuse(string reason) => Fault(code, $"The filter of the rollup column {where} on {rows} {reason}.");

        public ComputedCondition Condition(ConditionExpr condition) => condition switch
        {
            ConditionExpr.And and => new ComputedCondition.And(Condition(and.Left), Condition(and.Right)),
            ConditionExpr.Or or => new ComputedCondition.Or(Condition(or.Left), Condition(or.Right)),
            ConditionExpr.Not not => new ComputedCondition.Not(Condition(not.Operand)),
            ConditionExpr.Compare compare => Compare(compare),
            _ => throw Refuse($"uses a condition it cannot read ({condition.GetType().Name})"),
        };

        private ComputedCondition Compare(ConditionExpr.Compare compare)
        {
            var op = compare.Operator;
            if (!Comparisons.Contains(op))
                throw Refuse($"uses the condition operator '{op}', which is not supported");
            if (compare.Left is not ValueExpr.AttributeOf { Record: RecordRef.Primary, TargetType: null, Attribute: var name })
                throw Refuse($"compares a value that is not a column of '{table.LogicalName}'");
            var column = table.Attributes?.FirstOrDefault(a => a.LogicalName == name)
                ?? throw Refuse($"names '{table.LogicalName}.{name}', which does not exist");
            var left = new ComputedExpr.Column(null, name);
            var label = $"'{table.LogicalName}.{name}'";

            if (op is "Null" or "NotNull")
                return compare.Right is null ? new ComputedCondition.Compare(op, left, null) : throw Refuse($"uses {op} with a value");
            if (compare.Right is not ValueExpr.Literal literal)
                throw Refuse($"compares {label} with a value that is not a constant");
            if (literal.Value is null)
                return op switch
                {
                    "Equal" => new ComputedCondition.Compare("Null", left, null),
                    "NotEqual" => new ComputedCondition.Compare("NotNull", left, null),
                    _ => throw Refuse($"uses {op} on {label} with an empty value: only Equal and NotEqual compare with it"),
                };

            var fits = column switch
            {
                StringAttributeMetadata or MemoAttributeMetadata => literal.Type == "String",
                IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata or DoubleAttributeMetadata or MoneyAttributeMetadata
                    => literal.Type is "Integer" or "Decimal" or "Double" or "Money",
                DateTimeAttributeMetadata => literal.Type == "DateTime",
                BooleanAttributeMetadata => literal.Type == "Boolean" && op is "Equal" or "NotEqual",
                PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata =>
                    literal.Type == "OptionSetValue" && op is "Equal" or "NotEqual",
                _ => false,
            };
            if (!fits || (TextOperators.Contains(op) && literal.Type != "String"))
                throw Refuse($"uses {op} on {label} ({column.AttributeType?.ToString() ?? column.GetType().Name}) with a constant of type {literal.Type}");
            if (column is EnumAttributeMetadata options && (literal.Value is not int option || options.OptionSet?.Options?.Any(o => o.Value == option) != true))
                throw Refuse($"compares {label} with {literal.Value}, which is not one of its choices");
            return new ComputedCondition.Compare(op, left, new ComputedExpr.Literal(literal.Value));
        }
    }
}
