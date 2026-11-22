using System.Text.RegularExpressions;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// The comparison of the export's recompile check (plan 2.21, A12): two definitions are equivalent when they run the same steps on the
/// same values. It ignores what the designer generates: the <see cref="WorkflowStep.Path"/>s, the variable names (the keys of created
/// records, the results of perform action steps, the outputs of custom activities: matched by where they are declared), and the
/// generated part of each <see cref="WorkflowStep.DisplayName"/> (<c>CreateStep1</c> of <c>CreateStep1: a task</c>, A8). It compares
/// everything else, the description part of the display names and the branch descriptions included.
/// </summary>
public static partial class DefinitionEquivalence
{
    /// <summary>
    /// What differs between <paramref name="expected"/> (the original definition) and <paramref name="actual"/> (its recompile), each
    /// difference naming the step of <paramref name="expected"/> and what differs; empty when they are equivalent.
    /// </summary>
    public static IReadOnlyList<string> Differences(WorkflowDefinition expected, WorkflowDefinition actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var comparison = new Comparison();
        comparison.Definitions(expected, actual);
        return comparison.Found;
    }

    /// <summary>
    /// The description part of a display name (A8): the text after the generated name and <c>": "</c> (null when there is none); a display
    /// name the designer did not generate is all description.
    /// </summary>
    public static string? DescriptionOf(string? displayName)
    {
        if (displayName is null)
            return null;
        var match = GeneratedDisplayName().Match(displayName);
        return !match.Success ? displayName : match.Groups["d"].Success ? match.Groups["d"].Value : null;
    }

    /// <summary>The generated part of a display name: <c>CreateStep1</c>, <c>SetMessageStep_ConditionBranchStep2_2</c>, then <c>": "</c> and the description.</summary>
    [GeneratedRegex(@"^[A-Za-z]+Step(?:[0-9]+|_[A-Za-z]+Step[0-9]+_[0-9]+)(?:: (?<d>.*))?$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedDisplayName();

    private sealed class Comparison
    {
        /// <summary>The variables of the expected definition and the actual one they correspond to (declared at the same place).</summary>
        private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

        public List<string> Found { get; } = [];

        private void Differ(WorkflowStep at, string what) => Found.Add($"The step {at.Label}: {what}.");

        public void Definitions(WorkflowDefinition e, WorkflowDefinition a)
        {
            if (e.Category != a.Category)
                Found.Add($"The category is {e.Category}, not {a.Category}.");
            if (!string.Equals(e.PrimaryEntity, a.PrimaryEntity, StringComparison.Ordinal))
                Found.Add($"The primary table is '{e.PrimaryEntity}', not '{a.PrimaryEntity}'.");
            Arguments(e.Arguments, a.Arguments);
            if (e.UnsupportedMembers.Count != a.UnsupportedMembers.Count)
                Found.Add($"{e.UnsupportedMembers.Count} member(s) of x:Members are not supported, not {a.UnsupportedMembers.Count}.");
            Step(e.Root, a.Root);
        }

        private void Arguments(IReadOnlyList<WorkflowArgument> e, IReadOnlyList<WorkflowArgument> a)
        {
            var names = string.Join(", ", e.Select(x => x.Name));
            if (e.Count != a.Count || !e.Select(x => x.Name).SequenceEqual(a.Select(x => x.Name), StringComparer.Ordinal))
            {
                Found.Add($"The arguments are ({names}), not ({string.Join(", ", a.Select(x => x.Name))}).");
                return;
            }
            for (var i = 0; i < e.Count; i++)
            {
                var (x, y) = (e[i], a[i]);
                if (x.ClrType != y.ClrType || x.IsInput != y.IsInput || x.Required != y.Required || x.EntityTarget != y.EntityTarget
                    || x.IsTarget != y.IsTarget || x.Description != y.Description)
                    Found.Add($"The argument '{x.Name}' is {Describe(x)}, not {Describe(y)}.");
            }
        }

        private static string Describe(WorkflowArgument a) =>
            $"{(a.IsInput ? "an input" : "an output")} of {a.ClrType}{(a.Required ? ", required" : "")}{(a.IsTarget ? ", the target" : "")}" +
            $"{(a.EntityTarget is null ? "" : $" of '{a.EntityTarget}'")} described '{a.Description}'";

        // ---- steps ------------------------------------------------------------------------------------------------------------

        private void Step(WorkflowStep e, WorkflowStep a)
        {
            if (e.GetType() != a.GetType())
            {
                Differ(e, $"it is {Kind(e)}, not {Kind(a)} ({a.Label})");
                return;
            }
            var (de, da) = (DescriptionOf(e.DisplayName), DescriptionOf(a.DisplayName));
            if (de != da)
                Differ(e, $"the description is {Quote(de)}, not {Quote(da)}");

            switch (e, a)
            {
                case (SequenceStep x, SequenceStep y):
                    Steps(e, x.Steps, y.Steps);
                    break;
                case (CreateStep x, CreateStep y):
                    Text(e, "table", x.Entity, y.Entity);
                    Columns(e, x.Values, y.Values);
                    Declare(x.Variable, y.Variable);
                    break;
                case (UpdateStep x, UpdateStep y):
                    Record(e, "record", x.Record, y.Record);
                    Columns(e, x.Values, y.Values);
                    break;
                case (AssignStep x, AssignStep y):
                    Record(e, "record", x.Record, y.Record);
                    Value(e, "the owner", x.Owner, y.Owner);
                    break;
                case (SetStateStep x, SetStateStep y):
                    Record(e, "record", x.Record, y.Record);
                    if (x.State != y.State || x.Status != y.Status)
                        Differ(e, $"the state and status are {x.State}/{x.Status}, not {y.State}/{y.Status}");
                    break;
                case (SendEmailStep x, SendEmailStep y):
                    Columns(e, x.Values, y.Values);
                    if (x.CreatedEmailVariable is { } ve && y.CreatedEmailVariable is { } va)
                        Declare(ve, va);
                    break;
                case (ConditionStep x, ConditionStep y):
                    Condition(e, x, y);
                    break;
                case (WaitStep x, WaitStep y):
                    if (x.Until is null != y.Until is null)
                        Differ(e, x.Until is null ? "it waits for no condition, but the other does" : "it waits for a condition, the other does not");
                    else if (x.Until is not null)
                        Condition(e, "the condition waited for", x.Until, y.Until!);
                    if (x.Timeout is null != y.Timeout is null)
                        Differ(e, x.Timeout is null ? "it has no timeout, the other has one" : "it has a timeout, the other has none");
                    else if (x.Timeout is not null)
                        Value(e, "the timeout", x.Timeout, y.Timeout!);
                    Optional(e, "the steps on timeout", x.OnTimeout, y.OnTimeout);
                    break;
                case (StopStep x, StopStep y):
                    if (x.Canceled != y.Canceled)
                        Differ(e, $"it stops {(x.Canceled ? "canceled" : "succeeded")}, not {(y.Canceled ? "canceled" : "succeeded")}");
                    OptionalValue(e, "the message", x.Message, y.Message);
                    break;
                case (ChildWorkflowStep x, ChildWorkflowStep y):
                    if (x.WorkflowId != y.WorkflowId)
                        Differ(e, $"it starts the process {x.WorkflowId:D}, not {y.WorkflowId:D}");
                    Record(e, "record", x.Record, y.Record);
                    break;
                case (ActionStep x, ActionStep y):
                    Text(e, "message", x.Message, y.Message);
                    if (x.Target is null != y.Target is null)
                        Differ(e, x.Target is null ? "it has no target, the other has one" : "it has a target, the other has none");
                    else if (x.Target is not null)
                        Record(e, "target", x.Target, y.Target!);
                    Map(e, "input", x.Inputs, y.Inputs);
                    Declare(x.Variable, y.Variable);
                    break;
                case (CustomActivityStep x, CustomActivityStep y):
                    Text(e, "activity type", x.TypeName, y.TypeName);
                    Text(e, "assembly", x.AssemblyName, y.AssemblyName);
                    Map(e, "input", x.Inputs, y.Inputs);
                    if (!x.Outputs.Keys.SequenceEqual(y.Outputs.Keys, StringComparer.Ordinal))
                        Differ(e, $"the outputs are ({string.Join(", ", x.Outputs.Keys)}), not ({string.Join(", ", y.Outputs.Keys)})");
                    else
                        foreach (var (property, variable) in x.Outputs)
                            Declare(variable, y.Outputs[property]);
                    break;
                case (AssignOutputStep x, AssignOutputStep y):
                    Text(e, "output argument", x.Argument, y.Argument);
                    Value(e, $"the value of '{x.Argument}'", x.Value, y.Value);
                    break;
                case (SetValueStep x, SetValueStep y):
                    Text(e, "column", x.Attribute, y.Attribute);
                    if (x.DefaultOnly != y.DefaultOnly)
                        Differ(e, x.DefaultOnly ? "it sets a default value, the other a value" : "it sets a value, the other a default value");
                    Value(e, $"the value of '{x.Attribute}'", x.Value, y.Value);
                    break;
                case (ShowErrorStep x, ShowErrorStep y):
                    Text(e, "column", x.Attribute, y.Attribute);
                    Value(e, "the message", x.Message, y.Message);
                    break;
                case (ClientOnlyStep x, ClientOnlyStep y):
                    Text(e, "action", x.Kind, y.Kind);
                    Text(e, "column", x.Attribute, y.Attribute);
                    Text(e, "setting", x.Setting, y.Setting);
                    break;
                case (UnsupportedStep x, UnsupportedStep y):
                    Text(e, "element", x.Element, y.Element);
                    Text(e, "reason", x.Reason, y.Reason);
                    break;
            }
        }

        private void Steps(WorkflowStep at, IReadOnlyList<WorkflowStep> e, IReadOnlyList<WorkflowStep> a)
        {
            for (var i = 0; i < Math.Min(e.Count, a.Count); i++)
                Step(e[i], a[i]);
            if (e.Count != a.Count)
                Differ(at, $"it holds {e.Count} step(s), not {a.Count}");
        }

        private void Optional(WorkflowStep at, string what, WorkflowStep? e, WorkflowStep? a)
        {
            if (e is null != a is null)
                Differ(at, e is null ? $"it has no {what}, the other has" : $"it has {what}, the other has none");
            else if (e is not null)
                Step(e, a!);
        }

        private void Condition(WorkflowStep at, ConditionStep e, ConditionStep a)
        {
            if (e.Branches.Count != a.Branches.Count)
                Differ(at, $"it has {e.Branches.Count} branch(es), not {a.Branches.Count}");
            for (var i = 0; i < Math.Min(e.Branches.Count, a.Branches.Count); i++)
            {
                var (x, y) = (e.Branches[i], a.Branches[i]);
                Condition(at, $"the condition of branch {i + 1}", x.When, y.When);
                if (x.Description != y.Description)
                    Differ(at, $"the description of branch {i + 1} is {Quote(x.Description)}, not {Quote(y.Description)}");
                Step(x.Then, y.Then);
            }
            Optional(at, "an otherwise block", e.Otherwise, a.Otherwise);
        }

        private void Text(WorkflowStep at, string what, string? e, string? a)
        {
            if (!string.Equals(e, a, StringComparison.Ordinal))
                Differ(at, $"the {what} is {Quote(e)}, not {Quote(a)}");
        }

        private static string Quote(string? text) => text is null ? "none" : $"'{text}'";

        private static string Kind(WorkflowStep step) => step.GetType().Name;

        // ---- variables ----------------------------------------------------------------------------------------------------------

        private void Declare(string e, string a) => _variables[e] = a;

        /// <summary>The name in the actual definition that a variable of the expected one corresponds to (an argument keeps its name).</summary>
        private string Variable(string e) => _variables.GetValueOrDefault(e, e);

        // ---- records, values, conditions ----------------------------------------------------------------------------------------

        private void Record(WorkflowStep at, string what, RecordRef e, RecordRef a)
        {
            if (!SameRecord(e, a))
                Differ(at, $"the {what} is {Show(e)}, not {Show(a)}");
        }

        private bool SameRecord(RecordRef e, RecordRef a) => (e, a) switch
        {
            (RecordRef.Primary, RecordRef.Primary) => true,
            (RecordRef.Related x, RecordRef.Related y) => x.LookupAttribute == y.LookupAttribute,
            (RecordRef.Created x, RecordRef.Created y) => Variable(x.Variable) == y.Variable,
            _ => false,
        };

        private static string Show(RecordRef record) => record switch
        {
            RecordRef.Primary => "the primary record",
            RecordRef.Related r => $"the record of '{r.LookupAttribute}'",
            RecordRef.Created c => $"the record created as '{c.Variable}'",
            _ => record.ToString(),
        };

        private void Columns(WorkflowStep at, IReadOnlyDictionary<string, ValueExpr> e, IReadOnlyDictionary<string, ValueExpr> a) => Map(at, "column", e, a);

        /// <summary>Named values (columns, inputs), in order.</summary>
        private void Map(WorkflowStep at, string what, IReadOnlyDictionary<string, ValueExpr> e, IReadOnlyDictionary<string, ValueExpr> a)
        {
            if (!e.Keys.SequenceEqual(a.Keys, StringComparer.Ordinal))
            {
                Differ(at, $"the {what}s are ({string.Join(", ", e.Keys)}), not ({string.Join(", ", a.Keys)})");
                return;
            }
            foreach (var (name, value) in e)
                Value(at, $"the {what} '{name}'", value, a[name]);
        }

        private void OptionalValue(WorkflowStep at, string what, ValueExpr? e, ValueExpr? a)
        {
            if (e is null != a is null)
                Differ(at, e is null ? $"it has no {what}, the other has {Show(a!)}" : $"{what} is {Show(e)}, the other has none");
            else if (e is not null)
                Value(at, what, e, a!);
        }

        private void Value(WorkflowStep at, string what, ValueExpr e, ValueExpr a)
        {
            if (!SameValue(e, a))
                Differ(at, $"{what} is {Show(e)}, not {Show(a)}");
        }

        private bool SameValue(ValueExpr e, ValueExpr a) => (e, a) switch
        {
            (ValueExpr.Literal x, ValueExpr.Literal y) => x.Type == y.Type && Equals(x.Value, y.Value),
            // A target type the original lacks is the column's own type the recompile reads it as (A11): no difference.
            (ValueExpr.AttributeOf x, ValueExpr.AttributeOf y) => x.Attribute == y.Attribute && SameRecord(x.Record, y.Record)
                && (x.TargetType is null || x.TargetType == y.TargetType),
            (ValueExpr.Conversion x, ValueExpr.Conversion y) => x.From == y.From && x.To == y.To && x.Entity == y.Entity && SameValue(x.Operand, y.Operand),
            (ValueExpr.Arithmetic x, ValueExpr.Arithmetic y) => x.Operator == y.Operator && SameValues(x.Operands, y.Operands),
            (ValueExpr.Concat x, ValueExpr.Concat y) => SameValues(x.Parts, y.Parts),
            (ValueExpr.FirstNonNull x, ValueExpr.FirstNonNull y) => SameValues(x.Operands, y.Operands),
            (ValueExpr.ExecutionTime, ValueExpr.ExecutionTime) => true,
            (ValueExpr.VariableRef x, ValueExpr.VariableRef y) => Variable(x.Name) == y.Name,
            (ValueExpr.ActionOutput x, ValueExpr.ActionOutput y) => Variable(x.Variable) == y.Variable && x.Output == y.Output,
            (ValueExpr.Cast x, ValueExpr.Cast y) => x.ClrType == y.ClrType && SameValue(x.Operand, y.Operand),
            _ => false,
        };

        /// <summary>A value as messages show it.</summary>
        internal static string Show(ValueExpr value) => value switch
        {
            ValueExpr.Literal { Value: null } => "the empty value",
            ValueExpr.Literal l => $"the {l.Type} '{(l.Value is EntityRefValue r ? $"{r.LogicalName} {r.Id:D}" : Convert.ToString(l.Value, System.Globalization.CultureInfo.InvariantCulture))}'",
            ValueExpr.AttributeOf a => $"the column '{a.Attribute}' of {Show(a.Record)}{(a.TargetType is null ? "" : $" as {a.TargetType}")}",
            ValueExpr.Conversion c => $"{Show(c.Operand)} converted to {c.To}",
            ValueExpr.Arithmetic a => $"{a.Operator}({string.Join(", ", a.Operands.Select(Show))})",
            ValueExpr.Concat c => $"concat({string.Join(", ", c.Parts.Select(Show))})",
            ValueExpr.FirstNonNull f => $"firstOf({string.Join(", ", f.Operands.Select(Show))})",
            ValueExpr.ExecutionTime => "the execution time",
            ValueExpr.VariableRef v => $"the variable '{v.Name}'",
            ValueExpr.ActionOutput o => $"the output '{o.Output}' of '{o.Variable}'",
            ValueExpr.Cast c => $"DirectCast({Show(c.Operand)}, {c.ClrType})",
            _ => value.ToString(),
        };

        private static string Show(ConditionExpr condition) => condition switch
        {
            ConditionExpr.Compare c => c.Right is null ? $"{Show(c.Left)} {c.Operator}" : $"{Show(c.Left)} {c.Operator} {Show(c.Right)}",
            ConditionExpr.And x => $"({Show(x.Left)} and {Show(x.Right)})",
            ConditionExpr.Or x => $"({Show(x.Left)} or {Show(x.Right)})",
            ConditionExpr.Not x => $"not {Show(x.Operand)}",
            _ => condition.ToString(),
        };

        private bool SameValues(IReadOnlyList<ValueExpr> e, IReadOnlyList<ValueExpr> a) =>
            e.Count == a.Count && e.Zip(a).All(p => SameValue(p.First, p.Second));

        private void Condition(WorkflowStep at, string what, ConditionExpr e, ConditionExpr a)
        {
            if (!SameCondition(e, a))
                Differ(at, $"{what} is {Show(e)}, not {Show(a)}");
        }

        private bool SameCondition(ConditionExpr e, ConditionExpr a) => (e, a) switch
        {
            (ConditionExpr.Compare x, ConditionExpr.Compare y) => x.Operator == y.Operator && SameValue(x.Left, y.Left)
                && (x.Right is null ? y.Right is null : y.Right is not null && SameValue(x.Right, y.Right)),
            (ConditionExpr.And x, ConditionExpr.And y) => SameCondition(x.Left, y.Left) && SameCondition(x.Right, y.Right),
            (ConditionExpr.Or x, ConditionExpr.Or y) => SameCondition(x.Left, y.Left) && SameCondition(x.Right, y.Right),
            (ConditionExpr.Not x, ConditionExpr.Not y) => SameCondition(x.Operand, y.Operand),
            _ => false,
        };
    }
}
