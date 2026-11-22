using System.Xml.Linq;

namespace Xrmd.Workflows.Xaml;

/// <summary>
/// A rollup column definition as its XAML writes it (plan 2.12b, ruling R3), before it is checked against the metadata: the
/// hierarchical relationship of the <c>Source</c> section (null for none), the related rows of the <c>Target</c> section (null for
/// none), the filter of the <c>Source</c> section on the rollup's own rows, and the aggregate operator (<c>Sum</c>, <c>Count</c>,
/// <c>Maximum</c>, <c>Minimum</c>, <c>Average</c>) of <paramref name="AggregateEntity"/>.<paramref name="AggregateAttribute"/>.
/// Filters read <see cref="RecordRef.Primary"/>: the row they filter.
/// </summary>
public sealed record RollupXaml(
    string? HierarchicalRelationship,
    RollupXamlRelated? Related,
    ConditionExpr? SourceFilter,
    string Operator,
    string AggregateEntity,
    string AggregateAttribute)
{
    /// <summary>The table the steps of the source filter read (their <c>EntityName</c>); null without a source filter.</summary>
    public string? SourceFilterEntity { get; init; }
}

/// <summary>
/// The related rows of a rollup definition: the rows of <paramref name="Entity"/> whose lookup <paramref name="Lookup"/> points to
/// the rollup's row through the relationship <paramref name="Relationship"/> (its SchemaName), kept when <paramref name="Filter"/> holds.
/// </summary>
public sealed record RollupXamlRelated(string Entity, string Lookup, string Relationship, ConditionExpr? Filter);

/// <summary>
/// A filter condition of a rollup definition uses a condition operator the catalog does not know (plan 2.12b, ruling R5): the caller
/// answers with the code of the section, <see cref="OnRelatedRows"/> telling which.
/// </summary>
public sealed class XamlRollupConditionException(string message, bool onRelatedRows, string conditionOperator) : Exception(message)
{
    /// <summary>True for the filter of the <c>Target</c> section (the related rows), false for the <c>Source</c> section.</summary>
    public bool OnRelatedRows { get; } = onRelatedRows;

    /// <summary>The operator, as the XAML names it.</summary>
    public string ConditionOperator { get; } = conditionOperator;
}

public static partial class XamlWorkflowParser
{
    /// <summary>
    /// Parses the <c>FormulaDefinition</c> of a rollup column (plan 2.12b, ruling R3) with the 2.14 front end: a root
    /// <c>Sequence</c> <c>RollupRuleStep1</c> holding the designer sequences <c>Source</c>, <c>Target</c> and <c>Aggregate</c>, in
    /// that order. <c>Source</c> declares the variable <c>HierarchicalRelationshipName</c> and may hold filter steps on the rollup's
    /// own row; <c>Target</c> may name the related rows (<c>mcwc:SetAttributeValue</c>) and hold filter steps on them;
    /// <c>Aggregate</c> holds one <c>GetEntityProperty</c> and one <c>EvaluateExpression</c> with the aggregate operator. A filter step
    /// is a <c>GetEntityProperty</c> of the row, a <c>CreateCrmType</c> literal or an <c>EvaluateCondition</c> bound to a Boolean
    /// variable; the conditions of one section are combined with AND (assumed). Anything else is refused, naming the element.
    /// </summary>
    /// <exception cref="XamlWorkflowException">The XAML is not well-formed, or is not a rollup column definition xrmverse can read.</exception>
    /// <exception cref="XamlRollupConditionException">A filter uses a condition operator the catalog does not know.</exception>
    public static RollupXaml ParseRollup(string xaml) => new Session(Load(xaml), RollupCategory, workflowSdkAvailable: false).RunRollup();

    private sealed partial class Session
    {
        private const string RollupRoot = "RollupRuleStep1";
        private const string HierarchyVariable = "HierarchicalRelationshipName";
        private const string RelatedKeyPrefix = "relatedlinked_";
        private const string TempSuffix = "#Temp";

        /// <summary>Reads the definition of a rollup column (<see cref="ParseRollup"/>).</summary>
        public RollupXaml RunRollup()
        {
            try
            {
                return ReadRollup();
            }
            catch (ShapeException ex)
            {
                throw new XamlWorkflowException($"The rollup column definition has a step that cannot be read ({ex.Element}): {ex.Message.TrimEnd('.')}.");
            }
        }

        private RollupXaml ReadRollup()
        {
            ReadMembers();
            if (_arguments.Count > 0 || _unsupportedMembers.Count > 0)
            {
                var members = _arguments.Select(a => a.Name)
                    .Concat(_unsupportedMembers.Select(u => u.Path.StartsWith("x:Members/", StringComparison.Ordinal) ? u.Path["x:Members/".Length..] : u.Path))
                    .Distinct(StringComparer.Ordinal).Select(m => $"'{m}'");
                throw new XamlWorkflowException(
                    $"A rollup column definition declares no member other than InputEntities and CreatedEntities, not {string.Join(", ", members)}.");
            }
            if (root.Descendants(XName.Get("Variable", ActivityCatalog.ActivitiesNs)).Any())
                Use(XName.Get("Variable", ActivityCatalog.ActivitiesNs).ToString());

            var body = root.Elements().Where(e => e.Name != XMembers && !e.Name.LocalName.Contains('.')).ToList();
            if (body is not [var workflow] || Kind(workflow) != "Root")
                throw new XamlWorkflowException("A rollup column definition holds one Workflow element.");
            if (ActivitiesOf(workflow).ToList() is not [var rule] || Kind(rule) != "Sequence" || DisplayName(rule) != RollupRoot)
                throw new XamlWorkflowException($"A rollup column definition holds one Sequence named {RollupRoot}.");
            NoPropertyElements(rule, allowVariables: false);
            if (ActivitiesOf(rule).ToList() is not [var source, var target, var aggregate]
                || new[] { source, target, aggregate }.Any(s => Kind(s) != "Sequence")
                || (DisplayName(source), DisplayName(target), DisplayName(aggregate)) != ("Source", "Target", "Aggregate"))
                throw new XamlWorkflowException($"The Sequence {RollupRoot} of a rollup column definition holds the sequences Source, Target and Aggregate, in that order.");
            Use("Shape:RollupRule");

            // Source: the hierarchy and the filter on the rollup's own rows.
            var sourceVariables = SectionVariables(source, hierarchy: true);
            var (hierarchyType, hierarchyDefault) = sourceVariables.GetValueOrDefault(HierarchyVariable);
            if (hierarchyType != "x:String" || hierarchyDefault is null)
                Fail<bool>(source, $"the Source sequence must declare the String variable {HierarchyVariable} with a Default");
            Use("Shape:RollupHierarchy");
            var hierarchyName = ExpressionParser.Bracketed(hierarchyDefault!) is null
                ? hierarchyDefault!.Trim() is { Length: > 0 } plain && !plain.Contains(' ') ? plain : Fail<string>(source, $"the {HierarchyVariable} '{Clip(hierarchyDefault!)}' is not a relationship name")
                : Vb(source, hierarchyDefault!) is VbExpr.Nothing ? null : Fail<string>(source, $"the {HierarchyVariable} '{Clip(hierarchyDefault!)}' is not supported");
            var (sourceFilter, sourceEntity) = Filter(ActivitiesOf(source).ToList(), "Source", sourceVariables, onRelatedRows: false, entity: null);

            // Target: the related rows and their filter.
            var targetVariables = SectionVariables(target, hierarchy: false);
            var targetSteps = ActivitiesOf(target).ToList();
            RollupXamlRelated? related = null;
            if (targetSteps.Count > 0)
            {
                if (Kind(targetSteps[0]) != "SetAttributeValue")
                    Fail<bool>(targetSteps[0], "the Target sequence must start with the SetAttributeValue that names the related rows");
                var (entity, lookup, relationship) = RelatedLink(targetSteps[0]);
                var (filter, _) = Filter(targetSteps.Skip(1).ToList(), "Target", targetVariables, onRelatedRows: true, entity);
                related = new RollupXamlRelated(entity, lookup, relationship, filter);
            }

            // Aggregate: one column read and one aggregate operator.
            var aggregateVariables = SectionVariables(aggregate, hierarchy: false);
            if (ActivitiesOf(aggregate).ToList() is not [var read, var function] || Kind(read) != "GetValue" || Kind(function) != "EvaluateExpression")
                return Fail<RollupXaml>(aggregate, "the Aggregate sequence must hold one GetEntityProperty and one EvaluateExpression");
            DemandDeclared(read, "Aggregate", aggregateVariables);
            DemandDeclared(function, "Aggregate", aggregateVariables);
            var (aggregateEntity, aggregateAttribute, variable) = ReadOwnColumn(read, null);
            var op = RequiredArgument(function, "ExpressionOperator");
            if (ActivityCatalog.FindShape("EvaluateExpression:" + op) is not { } entry || !ActivityCatalog.IsRollupOnly(entry))
                return Fail<RollupXaml>(function, $"the aggregate operator '{Clip(op)}' is not supported");
            Use("EvaluateExpression:" + op);
            if (Parameters(function) is not [VbExpr.Name { Value: var parameter }] || parameter != variable)
                return Fail<RollupXaml>(function, $"the aggregate {op} must take the one column the Aggregate sequence reads ({variable})");
            if (TargetTypeOf(function) is { } type)
                return Fail<RollupXaml>(function, $"the aggregate {op} with the TargetType {type} is not supported");
            Bind(function, OutVariable(function, "Result"), new ValueExpr.Literal("Null", null));

            return new RollupXaml(hierarchyName, related, sourceFilter, op, aggregateEntity, aggregateAttribute) { SourceFilterEntity = sourceEntity };
        }

        /// <summary>
        /// The variables of a section (<c>Sequence.Variables</c>), by name, with their type and <c>Default</c>: Boolean variables
        /// default to False, Object variables have no default, and only the Source section declares the String
        /// <see cref="HierarchyVariable"/>. Any other property element or variable shape is refused.
        /// </summary>
        private Dictionary<string, (string? Type, string? Default)> SectionVariables(XElement section, bool hierarchy)
        {
            NoPropertyElements(section, allowVariables: true);
            var variables = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
            foreach (var holder in section.Elements(XName.Get("Sequence.Variables", ActivityCatalog.ActivitiesNs)))
                foreach (var variable in holder.Elements())
                {
                    if (variable.Name != XName.Get("Variable", ActivityCatalog.ActivitiesNs) || variable.HasElements)
                        Fail<bool>(section, $"the variable element '{variable.Name.LocalName}' of the {DisplayName(section)} sequence is not supported");
                    var name = (string?)variable.Attribute("Name") ?? Fail<string>(section, "a variable has no Name");
                    var type = (string?)variable.Attribute(XName.Get("TypeArguments", ActivityCatalog.XamlNs));
                    var value = (string?)variable.Attribute("Default");
                    var allowed = (type, value) switch
                    {
                        ("x:Boolean", "False") => true,
                        ("x:Object", null) => true,
                        ("x:String", not null) => hierarchy && name == HierarchyVariable,
                        _ => false,
                    };
                    if (!allowed || !variables.TryAdd(name, (type, value)))
                        Fail<bool>(section, $"the variable '{name}' ({type ?? "no type"}{(value is null ? "" : $", Default {Clip(value)}")}) of the {DisplayName(section)} sequence is not supported");
                }
            return variables;
        }

        /// <summary>Refuses property elements of a sequence (other than <c>Sequence.Variables</c> when allowed).</summary>
        private static void NoPropertyElements(XElement sequence, bool allowVariables)
        {
            if (sequence.Elements().FirstOrDefault(c => c.Name.LocalName.Contains('.')
                    && !(allowVariables && c.Name == XName.Get("Sequence.Variables", ActivityCatalog.ActivitiesNs))) is { } other)
                Fail<bool>(sequence, $"the element '{other.Name.LocalName}' of the {DisplayName(sequence) ?? "unnamed"} sequence is not supported");
        }

        /// <summary>
        /// The related rows (<c>mcwc:SetAttributeValue</c>): its <c>DisplayName</c> <c>&lt;related&gt;.&lt;lookup&gt;.&lt;relationship&gt;</c>,
        /// its <c>Entity</c> <c>CreatedEntities("relatedlinked_&lt;relationship&gt;#&lt;lookup&gt;#&lt;related&gt;#Temp")</c> and its
        /// <c>EntityName</c> must name the same table, lookup and relationship.
        /// </summary>
        private (string Entity, string Lookup, string Relationship) RelatedLink(XElement e)
        {
            CheckAttributes(e, "DisplayName", "Entity", "EntityName");
            CheckNoChildren(e);
            var display = DisplayName(e) ?? Fail<string>(e, "the SetAttributeValue that names the related rows has no DisplayName");
            if (display.Split('.') is not [{ Length: > 0 } entity, { Length: > 0 } lookup, { Length: > 0 } relationship])
                return Fail<(string, string, string)>(e, $"the DisplayName '{Clip(display)}' is not <related table>.<lookup>.<relationship>");

            var entityText = Member(e, "Entity") ?? Fail<string>(e, "the SetAttributeValue that names the related rows has no Entity");
            if (EntityKey(Vb(e, entityText)) is not ("CreatedEntities", var key)
                || !key.StartsWith(RelatedKeyPrefix, StringComparison.Ordinal) || !key.EndsWith(TempSuffix, StringComparison.Ordinal)
                || key[RelatedKeyPrefix.Length..^TempSuffix.Length].Split('#') is not [var keyRelationship, var keyLookup, var keyEntity])
                return Fail<(string, string, string)>(e,
                    $"the Entity '{Clip(entityText)}' is not CreatedEntities(\"{RelatedKeyPrefix}<relationship>#<lookup>#<related table>{TempSuffix}\")");
            var entityName = (string?)e.Attribute("EntityName");
            if ((keyEntity, keyLookup, keyRelationship) != (entity, lookup, relationship) || entityName != entity)
                return Fail<(string, string, string)>(e,
                    $"the DisplayName '{Clip(display)}', the Entity key '{Clip(key)}' and the EntityName '{entityName ?? ""}' do not name the same related table, lookup and relationship");
            Use("Shape:RollupRelatedLink");
            return (entity, lookup, relationship);
        }

        /// <summary>
        /// A <c>GetEntityProperty</c> of the row being read (<c>InputEntities("primaryEntity")</c>, no <c>TargetType</c>): its table
        /// (<c>EntityName</c>, which must be <paramref name="entity"/> when given), column and variable.
        /// </summary>
        private (string Entity, string Attribute, string Variable) ReadOwnColumn(XElement e, string? entity)
        {
            var name = (string?)e.Attribute("EntityName") is { Length: > 0 } n ? n : Fail<string>(e, "GetEntityProperty needs an EntityName");
            if (entity is not null && name != entity)
                Fail<bool>(e, $"GetEntityProperty reads the table '{name}', not '{entity}'");
            var variable = OutVariable(e, "Value");
            ParseGetValue(e);
            if (_symbols[variable] is not ValueExpr.AttributeOf { Record: RecordRef.Primary, TargetType: null, Attribute: var attribute })
                return Fail<(string, string, string)>(e, "a rollup column definition reads only the columns of the row itself, with no TargetType");
            return (name, attribute, variable);
        }

        /// <summary>
        /// Every variable <paramref name="step"/> reads or writes (its <c>Value</c>, <c>Operand</c>, <c>Parameters</c> and
        /// <c>Result</c>) is declared by its own section: a step never reads a value another section bound.
        /// </summary>
        private void DemandDeclared(XElement step, string section, Dictionary<string, (string? Type, string? Default)> variables)
        {
            var texts = new List<string?> { Member(step, "Value") };
            if (step.Elements().Any(c => c.Name.LocalName == "ActivityReference.Arguments"))
                texts.AddRange(new[] { "Operand", "Parameters", "Result" }.Select(key => Argument(step, key)));
            foreach (var text in texts.OfType<string>())
                if (ExpressionParser.Bracketed(text) is not null)
                    foreach (var name in VariableNames(Vb(step, text)))
                        if (!variables.ContainsKey(name))
                            Fail<bool>(step, $"the variable '{name}' is not declared by the {section} sequence: a step reads and writes only the variables of its own sequence");
        }

        /// <summary>The variable names an expression uses (names without a dot: <c>WorkflowPropertyType.X</c> and the like are not variables).</summary>
        private static IEnumerable<string> VariableNames(VbExpr expr) => expr switch
        {
            VbExpr.Name { Value: var name } when !name.Contains('.') => [name],
            VbExpr.NewArray array => array.Elements.SelectMany(VariableNames),
            VbExpr.Call call => call.Args.SelectMany(VariableNames),
            _ => [],
        };

        /// <summary>
        /// The filter steps of a section: column reads of the row (of <paramref name="entity"/>, or of one table for the source
        /// filter), <c>CreateCrmType</c> literals and conditions, each bound to a Boolean variable of the section; the conditions are
        /// combined with AND, in order (assumed). Returns the filter (null when there is none) and the table its reads name.
        /// </summary>
        private (ConditionExpr? Filter, string? Entity) Filter(List<XElement> steps, string section,
            Dictionary<string, (string? Type, string? Default)> variables, bool onRelatedRows, string? entity)
        {
            // The conditions not yet combined, by their Boolean variable: the designer joins two conditions with an
            // EvaluateLogicalCondition And (designer output on a licensed environment, plan 2.12b Task 7).
            var pending = new List<(string Variable, ConditionExpr Condition)>();
            foreach (var step in steps)
                switch (Kind(step))
                {
                    case "GetValue":
                        DemandDeclared(step, section, variables);
                        entity ??= (string?)step.Attribute("EntityName");
                        ReadOwnColumn(step, entity);
                        break;
                    case "EvaluateExpression":
                        if (RequiredArgument(step, "ExpressionOperator") is var literal && literal != "CreateCrmType")
                            Fail<bool>(step, $"the expression operator '{Clip(literal)}' cannot appear in the filter of a rollup column");
                        DemandDeclared(step, section, variables);
                        ParseEvaluateExpression(step);
                        break;
                    case "EvaluateCondition":
                        var op = RequiredArgument(step, "ConditionOperator");
                        if (ActivityCatalog.FindShape("EvaluateCondition:" + op) is null)
                            throw new XamlRollupConditionException(
                                $"The filter of the rollup column definition on {(onRelatedRows ? "the related rows" : "its own rows")} uses the condition operator '{Clip(op)}', which is not supported.",
                                onRelatedRows, op);
                        DemandDeclared(step, section, variables);
                        var result = OutVariable(step, "Result");
                        if (variables.GetValueOrDefault(result).Type != "x:Boolean")
                            Fail<bool>(step, $"the condition result '{result}' is not a Boolean variable of the sequence");
                        ParseEvaluateCondition(step);
                        pending.Add((result, (ConditionExpr)_symbols[result]));
                        break;
                    case "EvaluateLogicalCondition":
                        if (RequiredArgument(step, "LogicalOperator") is var logical && logical != "And")
                            Fail<bool>(step, $"the logical operator '{Clip(logical)}' cannot appear in the filter of a rollup column: only And is supported");
                        DemandDeclared(step, section, variables);
                        var joined = OutVariable(step, "Result");
                        if (variables.GetValueOrDefault(joined).Type != "x:Boolean")
                            Fail<bool>(step, $"the condition result '{joined}' is not a Boolean variable of the sequence");
                        ParseEvaluateLogicalCondition(step);
                        foreach (var key in new[] { "LeftOperand", "RightOperand" })
                            if (Vb(step, Argument(step, key) ?? "") is VbExpr.Name { Value: var operand })
                                pending.RemoveAll(p => p.Variable == operand);
                        pending.Add((joined, (ConditionExpr)_symbols[joined]));
                        break;
                    default:
                        Fail<bool>(step, $"the step '{ElementLabel(step)}' cannot appear in the filter of a rollup column");
                        break;
                }
            var filter = pending.Count == 0 ? null : pending.Select(p => p.Condition).Aggregate((a, b) => new ConditionExpr.And(a, b));
            if (filter is null && steps.Count > 0)
                Fail<bool>(steps[0], "the filter steps of a rollup column hold no condition");
            return (filter, filter is null ? null : entity);
        }
    }
}
