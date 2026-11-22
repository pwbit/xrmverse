using System.Globalization;
using System.Xml.Linq;

namespace Xrmd.Workflows.Authoring;

internal sealed partial class DesignerXaml
{
    private const string PropertyTypePrefix = "Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.";

    /// <summary>
    /// The variables of one designer step (A8): <c>{name}_{n}</c> numbered in pre-order (a result before its operands), and the
    /// condition results <c>{name}_condition</c>, <c>{name}_condition{k}</c>, declared in the order they are allocated.
    /// </summary>
    private sealed class Scope(string name)
    {
        private int _values, _conditions;

        public string Name { get; } = name;

        public List<XElement> Variables { get; } = [];

        public string Value()
        {
            var variable = $"{Name}_{++_values}";
            Variables.Add(Variable("x:Object", variable));
            return variable;
        }

        /// <summary>The result of the branch's condition (<c>{name}_condition</c>) or of a part of it (<c>{name}_condition{k}</c>).</summary>
        public string Condition(bool part)
        {
            var variable = part ? $"{Name}_condition{++_conditions}" : $"{Name}_condition";
            Variables.Add(Variable("x:Boolean", variable, "False"));
            return variable;
        }
    }

    // ---- values ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the activities that compute <paramref name="value"/> into <paramref name="activities"/> (operands first) and returns the
    /// VB expression that holds it, or null for the empty value. Variables are allocated before the operands' (A8).
    /// </summary>
    private string? Emit(Value value, Scope scope, List<XElement> activities)
    {
        switch (value)
        {
            case Empty:
                return null;
            case Literal literal:
            {
                if (UnconfirmedLiterals.Contains(literal.Type))
                    Assume($"literal:{literal.Type.Name}");
                var result = scope.Value();
                activities.Add(EvaluateExpression("CreateCrmType", Vb.Objects(LiteralParameters(literal)), literal.Type, result));
                return result;
            }
            case Lookup lookup:
            {
                // Export OnUpdate: the reference is allocated before its id, the id is computed first.
                var result = scope.Value();
                var id = scope.Value();
                activities.Add(EvaluateExpression("CreateCrmType",
                    Vb.Objects([PropertyTypePrefix + "Guid", Vb.String(lookup.Id.ToString("D")), Vb.String("Key")]), PropertyType.EntityReference, id));
                activities.Add(EvaluateExpression("CreateCrmType",
                    Vb.Objects([PropertyTypePrefix + "EntityReference", Vb.String(lookup.Entity), Vb.String(lookup.Name), id, Vb.String("Lookup")]),
                    PropertyType.EntityReference, result));
                return result;
            }
            case Read read:
            {
                var result = scope.Value();
                var name = Mxswa + "GetEntityProperty";
                activities.Add(new XElement(name,
                    new XAttribute("Attribute", read.Attribute),
                    new XAttribute("Entity", Expr(read.Record.Expression)),
                    new XAttribute("EntityName", read.Record.Entity),
                    new XAttribute("Value", Expr(result)),
                    TargetTypeProperty(name, read.Target)));
                return result;
            }
            case Argument argument:
            {
                var result = scope.Value();
                activities.Add(EvaluateExpression("CustomOperationArguments", Vb.Objects([argument.Name]), argument.Target, result));
                return result;
            }
            case Now:
            {
                var result = scope.Value();
                activities.Add(EvaluateExpression("RetrieveCurrentTime", null, PropertyType.DateTime, result));
                return result;
            }
            case Operator op:
            {
                var result = scope.Value();
                var operands = op.Operands.Select(o => Emit(o, scope, activities) ?? "Nothing").ToList();
                activities.Add(EvaluateExpression(op.Name, Vb.Objects(operands), op.Type, result));
                return result;
            }
            case Concat concat:
            {
                var parts = concat.Parts.Select(p => Emit(p, scope, activities) ?? "Nothing").ToList();
                return $"String.Concat({string.Join(", ", parts)})";
            }
            case Output output:
                return output.Expression;
            case StepOutput output:
                return $"{_stepNames[output.StepId]}_{output.Name}";
            default:
                throw new InvalidOperationException($"Unknown value {value.GetType().Name}.");
        }
    }

    /// <summary>The literal types no export showed in a <c>CreateCrmType</c> (A11: their crm type label is omitted, assumed).</summary>
    private static readonly HashSet<PropertyType> UnconfirmedLiterals = [PropertyType.Integer, PropertyType.Double, PropertyType.Money, PropertyType.DateTime];

    /// <summary>The <c>CreateCrmType</c> parameters of a literal: the property type, the text, and the crm type name when the type has one (A11).</summary>
    private static IEnumerable<string> LiteralParameters(Literal literal)
    {
        yield return PropertyTypePrefix + literal.Type.Member;
        yield return Vb.String(literal.Text);
        if (literal.Type.Label is { } label)
            yield return Vb.String(label);
    }

    /// <summary>An <c>EvaluateExpression</c>; <paramref name="parameters"/> null writes <c>x:Null</c>.</summary>
    private static XElement EvaluateExpression(string op, string? parameters, PropertyType? target, string result) =>
        Reference("EvaluateExpression", "EvaluateExpression", Arguments(
            In("x:String", "ExpressionOperator", op),
            parameters is null ? XNull("Parameters") : In("s:Object[]", "Parameters", parameters),
            In("s:Type", "TargetType", TypeLiteral(target)),
            Out("x:Object", "Result", result)));

    // ---- conditions -----------------------------------------------------------------------------------------------------------

    /// <summary>Writes the evaluation of <paramref name="condition"/> into <paramref name="activities"/>, its result in <paramref name="result"/>.</summary>
    private void EmitCondition(Condition condition, Scope scope, string result, List<XElement> activities)
    {
        _usesQuery = true;
        switch (condition)
        {
            case Compare compare:
            {
                var left = Emit(compare.Left, scope, activities) ?? "Nothing";
                var right = compare.Right is null ? null : Emit(compare.Right, scope, activities) ?? "Nothing";
                activities.Add(Reference("EvaluateCondition", "EvaluateCondition", Arguments(
                    In("mxsq:ConditionOperator", "ConditionOperator", compare.Operator),
                    right is null ? XNull("Parameters") : In("s:Object[]", "Parameters", Vb.Objects([right])),
                    In("x:Object", "Operand", Expr(left)),
                    Out("x:Boolean", "Result", result))));
                break;
            }
            case Logical logical:
            {
                // Pre-order names (the result, then each operand's), post-order activities (assumed, fixture background-wait).
                var left = scope.Condition(part: true);
                EmitCondition(logical.Left, scope, left, activities);
                string? right = null;
                if (logical.Right is not null)
                {
                    right = scope.Condition(part: true);
                    EmitCondition(logical.Right, scope, right, activities);
                }
                activities.Add(Reference("EvaluateLogicalCondition", "EvaluateLogicalCondition", Arguments(
                    In("mxsq:LogicalOperator", "LogicalOperator", logical.Operator),
                    In("x:Boolean", "LeftOperand", Expr(left)),
                    right is null ? null : In("x:Boolean", "RightOperand", Expr(right)),
                    Out("x:Boolean", "Result", result))));
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown condition {condition.GetType().Name}.");
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
