using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Schema;
using D = Xrmd.Workflows.Authoring.DesignerXaml;

namespace Xrmd.Workflows.Authoring;

public static partial class WorkflowCompiler
{
    /// <summary>
    /// Resolves and types a document against the organization (A6, A7, A10, A11): each problem becomes a <see cref="CompileError"/> at
    /// its YAML position and resolution goes on, so every error of the file is reported together.
    /// </summary>
    private sealed class Binder(WorkflowDocument d, IAuthoringEnvironment env, CancellationToken ct)
    {
        /// <summary>
        /// A step other steps refer to by its id: its kind, the table of the record it makes (null: none), its outputs (null: not known,
        /// a perform action of a message the organization does not have, already an error).
        /// </summary>
        private sealed record Declared(string Kind, string? Entity, IReadOnlyList<string>? Outputs);

        /// <summary>
        /// Where a value goes: its type, the column (choice values, lookup targets), what it is in messages, and whether the empty
        /// value is allowed (it clears a column).
        /// </summary>
        private sealed record Destination(PropertyType Type, AttributeMetadata? Column, string What, bool AllowEmpty = false);

        private readonly Dictionary<string, Declared> _declared = new(StringComparer.Ordinal);

        /// <summary>The primary table; null for a global action (<c>entity: none</c>), which has no primary record.</summary>
        private EntityMetadata? _primary;

        /// <summary>The arguments of an action, by name (A15), in the file's order.</summary>
        private readonly Dictionary<string, D.ProcessArgument> _arguments = new(StringComparer.Ordinal);

        /// <summary>How many conditions hold the step being bound (a business rule action must be inside one).</summary>
        private int _conditionDepth;

        public List<CompileError> Errors { get; } = [];

        private void Error(SourceSpan at, string message) => Errors.Add(new CompileError(at, message));

        private bool IsGlobalAction => d.Kind == WorkflowKind.Action && d.Entity == "none";

        public async Task<D.Process?> BindAsync(Guid id)
        {
            if (!IsGlobalAction)
            {
                if (env.GetEntity(d.Entity) is not { } primary)
                {
                    Error(d.Span, $"The table '{d.Entity}' does not exist.");
                    return null;
                }
                _primary = primary;
            }
            if (d.Trigger?.Update is { } update && _primary is { } table)
                for (var i = 0; i < update.Columns.Count; i++)
                    if (Column(table, update.Columns[i]) is null)
                        Error(update.ColumnSpans.Count > i ? update.ColumnSpans[i] : d.Trigger.Span,
                            $"The update trigger names the column '{update.Columns[i]}', which the table '{d.Entity}' does not have.");
            if (d.Description is { } description && !IsXmlText(description))
                Error(d.DescriptionSpan ?? d.Span, "The description holds a character XML cannot hold.");
            if (d.Kind == WorkflowKind.Action)
                BindArguments();

            var steps = await StepsAsync(d.Steps);
            return new D.Process(id, d.Entity, d.Kind == WorkflowKind.Workflow && d.Options.Mode != "realtime", steps)
            {
                Category = d.Kind switch { WorkflowKind.BusinessRule => 2, WorkflowKind.Action => 3, _ => 0 },
                Arguments = _arguments.Values.ToList(),
                Bound = d.Kind == WorkflowKind.Action && !IsGlobalAction,
            };
        }

        // ---- action arguments (A15) ------------------------------------------------------------------------------------------

        private void BindArguments()
        {
            foreach (var argument in d.Arguments)
            {
                var at = argument.Span;
                CheckName(argument.Name, at, "The argument name");
                if (_arguments.Keys.FirstOrDefault(k => string.Equals(k, argument.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
                {
                    // VB names ignore case: the XAML cannot hold both.
                    Error(at, $"The argument '{argument.Name}' has the name of the argument '{same}' (names ignore case).");
                    continue;
                }
                if (!IsXmlText(argument.Description))
                    Error(at, $"The description of the argument '{argument.Name}' holds a character XML cannot hold.");
                if (PropertyTypes.OfArgument(argument.Type) is not { } type)
                {
                    Error(at, $"The argument '{argument.Name}' has the type '{argument.Type}' (allowed: {string.Join(", ", WorkflowYaml.ArgumentTypes)}).");
                    continue;
                }
                if (argument.Entity is { } entity)
                {
                    if (type != PropertyType.EntityReference)
                        Error(at, $"The argument '{argument.Name}' is of type {argument.Type}: only an entityReference argument names a table.");
                    else
                        Table(entity, at);
                }
                _arguments[argument.Name] = new D.ProcessArgument(argument.Name, argument.IsInput, type, argument.Entity, argument.Required, argument.Description)
                {
                    YamlType = argument.Type,
                };
            }
        }

        /// <summary>The input argument an <c>{ arg }</c> value reads; null (and an error) when there is none.</summary>
        private D.ProcessArgument? InputArgument(ArgNode arg)
        {
            if (d.Kind != WorkflowKind.Action)
            {
                Error(arg.Span, $"'{arg.Name}' is not an input argument: only an action has arguments.");
                return null;
            }
            if (!_arguments.TryGetValue(arg.Name, out var argument))
            {
                Error(arg.Span, $"The action has no argument '{arg.Name}' (inputs: {Names(_arguments.Values.Where(a => a.IsInput).Select(a => a.Name))}).");
                return null;
            }
            if (!argument.IsInput)
            {
                Error(arg.Span, $"'{arg.Name}' is an output argument: only an input argument can be read.");
                return null;
            }
            return argument;
        }

        private static string Names(IEnumerable<string> names) => names.Any() ? string.Join(", ", names) : "none";

        // ---- metadata --------------------------------------------------------------------------------------------------------

        private static AttributeMetadata? Column(EntityMetadata table, string name) =>
            table.Attributes?.FirstOrDefault(a => string.Equals(a.LogicalName, name, StringComparison.Ordinal));

        private EntityMetadata? Table(string name, SourceSpan at)
        {
            if (env.GetEntity(name) is { } table)
                return table;
            Error(at, $"The table '{name}' does not exist.");
            return null;
        }

        /// <summary>The column of a table a value is read from or set into, with its type; null (and an error) when there is none.</summary>
        private (AttributeMetadata Column, PropertyType Type)? TypedColumn(EntityMetadata table, string name, SourceSpan at)
        {
            if (Column(table, name) is not { } column)
            {
                Error(at, $"The table '{table.LogicalName}' has no column '{name}'.");
                return null;
            }
            if (PropertyTypes.Of(column) is not { } type)
            {
                Error(at, $"The column '{name}' of '{table.LogicalName}' is of type {column.AttributeType}, which a workflow cannot read or set.");
                return null;
            }
            return (column, type);
        }

        private static string Targets(AttributeMetadata column) =>
            column is LookupAttributeMetadata { Targets: { Length: > 0 } targets } ? string.Join(", ", targets) : "";

        // ---- records ---------------------------------------------------------------------------------------------------------

        /// <summary>The record a step or value names: the primary record, the record of a lookup of the primary table, or a created one.</summary>
        private D.RecordKey? Record(RecordNode record, SourceSpan at)
        {
            if (_primary is null && record is RecordNode.Primary or RecordNode.Related)
            {
                Error(at, "A global action (entity: none) has no primary record: it can read and write only the records it creates.");
                return null;
            }
            switch (record)
            {
                case RecordNode.Primary:
                    return D.RecordKey.Primary(_primary!.LogicalName);
                case RecordNode.Related related:
                {
                    if (TypedColumn(_primary!, related.Lookup, at) is not { } lookup)
                        return null;
                    if (lookup.Column is not LookupAttributeMetadata { Targets: [var target] } || lookup.Type != PropertyType.EntityReference)
                    {
                        Error(at, $"The column '{related.Lookup}' of '{_primary!.LogicalName}' is not a lookup to one table, so it names no related record.");
                        return null;
                    }
                    return D.RecordKey.Related(related.Lookup, target);
                }
                case RecordNode.Created created:
                {
                    if (!_declared.TryGetValue(created.StepId, out var declared))
                    {
                        Error(at, $"No earlier step has the id '{created.StepId}'.");
                        return null;
                    }
                    if (declared.Entity is null)
                    {
                        var outputs = declared.Kind is "performAction" or "customActivity"
                            ? $": read its outputs with {{ output: <name>, of: {created.StepId} }}"
                            : "";
                        Error(at, $"The step '{created.StepId}' is a {declared.Kind} step, which creates no record a workflow can read or write{outputs}.");
                        return null;
                    }
                    return D.RecordKey.Created(created.StepId, declared.Entity);
                }
                default:
                    throw new InvalidOperationException($"Unknown record {record.GetType().Name}.");
            }
        }

        // ---- values (A6, A11) ------------------------------------------------------------------------------------------------

        /// <summary>A value typed by its destination; null (and an error) when it cannot go there.</summary>
        private async Task<D.Value?> Value(ValueNode node, Destination to)
        {
            switch (node)
            {
                case LiteralNode { Text: null }:
                    if (to.AllowEmpty)
                        return new D.Empty();
                    Error(node.Span, $"The empty value (~) cannot be used as {to.What}.");
                    return null;
                case LiteralNode literal:
                    return Literal(literal, to);
                case LookupNode lookup:
                    return await LookupAsync(lookup, to);
                case FieldNode field:
                {
                    if (Read(field) is not { } read)
                        return null;
                    if (!Compatible(read.Type, to.Type))
                    {
                        Error(node.Span, $"{Capital(to.What)} takes a {to.Type} value; the column '{field.Field}' of '{read.Read.Record.Entity}' is {read.Type}.");
                        return null;
                    }
                    // Into a value of unknown type (compared with an output), the read keeps the column's own type.
                    var target = to.Type == PropertyType.Object ? read.Type : to.Type;
                    return Wrap(read.Read with { Target = target }, target);
                }
                case RecordValueNode recordValue:
                {
                    if (RecordReference(recordValue, node.Span) is not { } read)
                        return null;
                    if (to.Type != PropertyType.EntityReference)
                    {
                        Error(node.Span, $"{Capital(to.What)} is {Article(to.Type)}: a reference to a record cannot be set into it.");
                        return null;
                    }
                    if (to.Column is LookupAttributeMetadata { Targets: { Length: > 0 } targets } && !targets.Contains(read.Record.Entity, StringComparer.Ordinal))
                    {
                        Error(node.Span, $"{Capital(to.What)} refers to {Targets(to.Column)}, not to '{read.Record.Entity}'.");
                        return null;
                    }
                    return Wrap(read, PropertyType.EntityReference);
                }
                case ArgNode arg:
                {
                    if (InputArgument(arg) is not { } argument)
                        return null;
                    if (argument.Type != to.Type && to.Type != PropertyType.Object)
                    {
                        Error(node.Span, $"{Capital(to.What)} takes a {to.Type} value; the argument '{arg.Name}' is {argument.Type}.");
                        return null;
                    }
                    if (argument.Entity is { } entity && to.Column is LookupAttributeMetadata { Targets: { Length: > 0 } targets } && !targets.Contains(entity, StringComparer.Ordinal))
                    {
                        Error(node.Span, $"{Capital(to.What)} refers to {Targets(to.Column)}, not to '{entity}' (the table of the argument '{arg.Name}').");
                        return null;
                    }
                    // Export Recalculate: CustomOperationArguments with the destination's type, in the one-operand wrapper.
                    return Wrap(new D.Argument(arg.Name, argument.Type), argument.Type);
                }
                case OutputNode output:
                    return Output(output);
                case NowNode:
                    if (to.Type != PropertyType.DateTime && to.Type != PropertyType.Object)
                    {
                        Error(node.Span, $"{Capital(to.What)} is {Article(to.Type)}; the execution time is a DateTime.");
                        return null;
                    }
                    return Wrap(new D.Now(), PropertyType.DateTime);
                case OperatorNode op:
                    return await OperatorAsync(op, to);
                default:
                    throw new InvalidOperationException($"Unknown value {node.GetType().Name}.");
            }
        }

        /// <summary>The designer's one-operand <c>SelectFirstNonNull</c> around a dynamic value (export: column reads, record keys, arguments).</summary>
        private static D.Operator Wrap(D.Value value, PropertyType type) => new("SelectFirstNonNull", type, [value]);

        /// <summary>
        /// A read of a column's value may go to its own type, to a value of unknown type (an output), and a lookup into a party list
        /// (one activity party, export OnUpdate).
        /// </summary>
        private static bool Compatible(PropertyType from, PropertyType to) =>
            from == to || to == PropertyType.Object || (from == PropertyType.EntityReference && to == PropertyType.EntityCollection);

        private D.Value? Literal(LiteralNode literal, Destination to)
        {
            var text = literal.Text!;
            if (to.Type == PropertyType.EntityReference)
            {
                Error(literal.Span, $"{Capital(to.What)} is a lookup: give {{ lookup: <table>, id: <guid> }} or a dynamic value, not the text '{text}'.");
                return null;
            }
            if (to.Type == PropertyType.EntityCollection)
            {
                Error(literal.Span, $"{Capital(to.What)} is a party list: give a dynamic lookup value, not the text '{text}'.");
                return null;
            }
            if (to.Type == PropertyType.Object)
            {
                Error(literal.Span, $"The type of {to.What} is not known, so the literal '{text}' cannot be typed: give a dynamic value.");
                return null;
            }
            if (!IsXmlText(text))
            {
                Error(literal.Span, $"The text of {to.What} holds a character XML cannot hold.");
                return null;
            }
            if (PropertyTypes.LiteralText(to.Type, text) is not { } typed)
            {
                Error(literal.Span, $"'{text}' is not {Article(to.Type)} value, which {to.What} takes.");
                return null;
            }
            if (to.Type == PropertyType.OptionSet && to.Column is { } column)
            {
                var options = PropertyTypes.Options(column);
                if (!options.Contains(int.Parse(typed, System.Globalization.CultureInfo.InvariantCulture)))
                {
                    Error(literal.Span, $"The option value {typed} is not in the choice '{column.LogicalName}' (values: {string.Join(", ", options)}).");
                    return null;
                }
            }
            return new D.Literal(to.Type, typed);
        }

        private async Task<D.Value?> LookupAsync(LookupNode lookup, Destination to)
        {
            if (to.Type != PropertyType.EntityReference)
            {
                Error(lookup.Span, $"{Capital(to.What)} is {Article(to.Type)}: a lookup value cannot be set into it.");
                return null;
            }
            if (Table(lookup.Entity, lookup.Span) is null)
                return null;
            if (to.Column is LookupAttributeMetadata { Targets: { Length: > 0 } targets } && !targets.Contains(lookup.Entity, StringComparer.Ordinal))
            {
                Error(lookup.Span, $"{Capital(to.What)} refers to {Targets(to.Column)}, not to '{lookup.Entity}'.");
                return null;
            }
            var name = await env.PrimaryNameAsync(lookup.Entity, lookup.Id, ct);
            if (name is null)
            {
                Error(lookup.Span, $"No {lookup.Entity} record has the id {lookup.Id:D}.");
                return null;
            }
            name = lookup.Name ?? name;
            if (!IsXmlText(name))
            {
                Error(lookup.Span, $"The name of the {lookup.Entity} record {lookup.Id:D} holds a character XML cannot hold.");
                return null;
            }
            return new D.Lookup(lookup.Entity, lookup.Id, name);
        }

        /// <summary>A column read of a record, with the column's own type (the destination's type is set by the caller).</summary>
        private (D.Read Read, PropertyType Type, AttributeMetadata Column)? Read(FieldNode field)
        {
            if (Record(field.Record, field.Span) is { } record
                && env.GetEntity(record.Entity) is { } table
                && TypedColumn(table, field.Field, field.Span) is { } column)
                return (new D.Read(record, field.Field, column.Type), column.Type, column.Column);
            return null;
        }

        /// <summary>A reference to a record: its key read as an <c>EntityReference</c> (export OnCreate, OnUpdate).</summary>
        private D.Read? RecordReference(RecordValueNode node, SourceSpan at)
        {
            if (Record(node.Record, at) is not { } record || env.GetEntity(record.Entity) is not { } table)
                return null;
            // The 2.14 parser reads a record reference from the key named "{table}id", or "activityid" of an activity table.
            if (table.PrimaryIdAttribute != record.Entity + "id" && table.PrimaryIdAttribute != "activityid")
            {
                Error(at, $"A reference to a '{record.Entity}' record cannot be written: its key '{table.PrimaryIdAttribute}' is neither '{record.Entity}id' nor 'activityid'.");
                return null;
            }
            return new D.Read(record, table.PrimaryIdAttribute, PropertyType.EntityReference);
        }

        private D.Value? Output(OutputNode output)
        {
            if (!_declared.TryGetValue(output.StepId, out var declared))
            {
                Error(output.Span, $"No earlier step has the id '{output.StepId}'.");
                return null;
            }
            if (declared.Outputs is { } outputs && !outputs.Contains(output.Name, StringComparer.Ordinal))
            {
                Error(output.Span, $"The step '{output.StepId}' has no output named '{output.Name}' (outputs: {Names(outputs)}).");
                return null;
            }
            // A perform action's output is read from its result by name (the message's response property, review fix 4).
            if (declared.Kind == "performAction")
                return new D.Output($"{output.StepId}({Vb.String(output.Name)})");
            // The variable is named after the step the writer names (review I1): resolved when written.
            return new D.StepOutput(output.StepId, output.Name);
        }

        private static readonly Dictionary<string, string> Arithmetic = new(StringComparer.Ordinal)
        {
            ["add"] = "Add", ["subtract"] = "Subtract", ["multiply"] = "Multiply",
            ["increment"] = "Increment", ["decrement"] = "Decrement", ["set"] = "Set",
        };

        private async Task<D.Value?> OperatorAsync(OperatorNode op, Destination to)
        {
            if (Arithmetic.TryGetValue(op.Operator, out var name))
            {
                if (!to.Type.IsNumber)
                {
                    Error(op.Span, $"'{op.Operator}' computes a number; {to.What} is {Article(to.Type)}.");
                    return null;
                }
                var operands = await Operands(op.Operands, to with { What = $"an operand of '{op.Operator}'", AllowEmpty = false });
                return operands is null ? null : new D.Operator(name, to.Type, operands);
            }
            if (op.Operator == "firstOf")
            {
                var operands = await Operands(op.Operands, to with { What = "an operand of 'firstOf'", AllowEmpty = false });
                // The operands of the designer's default value are not wrapped themselves: the operator is the wrapper (assumed).
                return operands is null ? null : new D.Operator("SelectFirstNonNull", to.Type,
                    operands.Select(o => o is D.Operator { Name: "SelectFirstNonNull", Operands: [var inner] } ? inner : o).ToList());
            }
            if (op.Operator == "concat")
            {
                if (to.Type != PropertyType.String)
                {
                    Error(op.Span, $"'concat' computes a text; {to.What} is {Article(to.Type)}.");
                    return null;
                }
                var parts = new List<D.Value>();
                var ok = true;
                foreach (var part in op.Operands)
                {
                    if (await Natural(part, "a part of 'concat'", literalsAreText: true) is { } typed)
                        parts.Add(typed.Value);
                    else
                        ok = false;
                }
                return ok ? new D.Concat(parts) : null;
            }
            throw new InvalidOperationException($"Unknown operator {op.Operator}.");
        }

        private async Task<List<D.Value>?> Operands(IReadOnlyList<ValueNode> nodes, Destination to)
        {
            var operands = new List<D.Value>();
            var ok = true;
            foreach (var node in nodes)
            {
                if (await Value(node, to) is { } value)
                    operands.Add(value);
                else
                    ok = false;
            }
            return ok ? operands : null;
        }

        /// <summary>
        /// A value typed by itself, where no destination types it (a part of <c>concat</c>, an input of a perform action or custom
        /// activity step): a column read is of the column's type; a literal is a text in <c>concat</c> and refused elsewhere.
        /// </summary>
        private async Task<(D.Value Value, PropertyType Type)?> Natural(ValueNode node, string what, bool literalsAreText)
        {
            switch (node)
            {
                case LiteralNode { Text: { } text } when literalsAreText:
                    return Literal(new LiteralNode(text, node.Span), new Destination(PropertyType.String, null, what)) is { } literal
                        ? (literal, PropertyType.String)
                        : null;
                case LiteralNode:
                    Error(node.Span, $"The type of {what} is not known, so a literal cannot be typed: give a dynamic value.");
                    return null;
                case FieldNode field:
                    return Read(field) is { } read ? (Wrap(read.Read, read.Type), read.Type) : null;
                case RecordValueNode recordValue:
                    return RecordReference(recordValue, node.Span) is { } reference ? (Wrap(reference, PropertyType.EntityReference), PropertyType.EntityReference) : null;
                case NowNode:
                    return (Wrap(new D.Now(), PropertyType.DateTime), PropertyType.DateTime);
                case LookupNode lookup:
                    return await LookupAsync(lookup, new Destination(PropertyType.EntityReference, null, what)) is { } value ? (value, PropertyType.EntityReference) : null;
                case OutputNode output:
                    return Output(output) is { } o ? (o, PropertyType.Object) : null;
                case OperatorNode { Operator: "concat" } concat:
                    return await OperatorAsync(concat, new Destination(PropertyType.String, null, what)) is { } c ? (c, PropertyType.String) : null;
                case ArgNode arg:
                    return InputArgument(arg) is { } argument ? (Wrap(new D.Argument(arg.Name, argument.Type), argument.Type), argument.Type) : null;
                default:
                    Error(node.Span, $"The type of {what} is not known, so '{((OperatorNode)node).Operator}' cannot be typed.");
                    return null;
            }
        }

        // ---- conditions (A7) -------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
        {
            ["eq"] = "Equal", ["ne"] = "NotEqual", ["null"] = "Null", ["notNull"] = "NotNull", ["contains"] = "Contains",
            ["notContains"] = "DoesNotContain", ["beginsWith"] = "BeginsWith", ["endsWith"] = "EndsWith", ["gt"] = "GreaterThan",
            ["ge"] = "GreaterEqual", ["lt"] = "LessThan", ["le"] = "LessEqual",
        };

        private async Task<D.Condition?> Condition(CondNode node)
        {
            switch (node)
            {
                case CompareNode compare:
                    return await Compare(compare);
                case AllNode all:
                    return await Fold("And", all.Conditions);
                case AnyNode any:
                    return await Fold("Or", any.Conditions);
                case NotNode not:
                    return await Condition(not.Condition) is { } operand ? new D.Logical("Not", operand, null) : null;
                default:
                    throw new InvalidOperationException($"Unknown condition {node.GetType().Name}.");
            }
        }

        /// <summary><c>all</c>/<c>any</c> as binary logical conditions nested to the left: <c>((a And b) And c)</c> (assumed).</summary>
        private async Task<D.Condition?> Fold(string op, IReadOnlyList<CondNode> nodes)
        {
            var conditions = new List<D.Condition?>();
            foreach (var node in nodes)
                conditions.Add(await Condition(node));
            if (conditions.Any(c => c is null))
                return null;
            return conditions.Skip(1).Aggregate(conditions[0]!, (left, right) => new D.Logical(op, left, right!));
        }

        /// <summary>
        /// A comparison: the left operand as the export writes it in a condition (a column read or argument with the <c>x:Null</c>
        /// target type and no wrapper), the right one typed by the left operand's type.
        /// </summary>
        private async Task<D.Condition?> Compare(CompareNode compare)
        {
            var op = Operators[compare.Op];
            D.Value? left;
            PropertyType? type;
            AttributeMetadata? column = null;
            string what;
            switch (compare.Left)
            {
                case FieldNode field:
                    var read = Read(field);
                    left = read is { } r ? r.Read with { Target = null } : null;
                    type = read?.Type;
                    column = read?.Column;
                    what = $"the value compared with the column '{field.Field}'";
                    break;
                case ArgNode arg:
                    // Export Recalculate: the argument read alone, with the x:Null target type.
                    var argument = InputArgument(arg);
                    left = argument is null ? null : new D.Argument(arg.Name, null);
                    type = argument?.Type;
                    what = $"the value compared with the argument '{arg.Name}'";
                    break;
                case OperatorNode { Operator: "concat" }:
                    // A12: a concatenation as the Operand of a condition has no YAML form the exporter can give back (plan 2.21, Task 4).
                    Error(compare.Left.Span, "A concat cannot be the left operand of a condition: compare a column, an argument or another value, and put the text in 'value'.");
                    return null;
                default:
                    var natural = await Natural(compare.Left, "the left operand of a condition", literalsAreText: false);
                    left = natural?.Value;
                    type = natural?.Type;
                    what = "the value compared with it";
                    break;
            }
            if (compare.Right is null)
                return left is null ? null : new D.Compare(op, left, null);
            if (type is null)
                return null;
            var right = await Value(compare.Right, new Destination(type, column, what));
            return left is null || right is null ? null : new D.Compare(op, left, right);
        }

        // ---- steps (A5) ------------------------------------------------------------------------------------------------------

        private async Task<List<D.Step>> StepsAsync(IReadOnlyList<StepNode> nodes)
        {
            var steps = new List<D.Step>();
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                if (node.Description is { } description && !IsXmlText(description))
                    Error(node.DescriptionSpan ?? node.Span, "The description holds a character XML cannot hold.");
                if (node.Id is { } id)
                {
                    CheckName(id, node.IdSpan ?? node.Span, "The step id");
                    if (_arguments.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)) is { } argument)
                        Error(node.IdSpan ?? node.Span, $"The step id '{id}' is the name of the argument '{argument}' (names ignore case).");
                }
                if (await StepAsync(node) is { } step)
                    steps.Add(step);
            }
            return steps;
        }

        private async Task<D.Step?> StepAsync(StepNode node)
        {
            // The step library of each category (A5, A10): the reader checks it too; a document built in code is checked here.
            var ruleStep = node is SetValueNode or ClearValueNode or ShowErrorNode or ClientOnlyNode;
            if (d.Kind == WorkflowKind.BusinessRule && !ruleStep && node is not IfNode)
            {
                Error(node.Span, $"The step '{node.GetType().Name}' cannot be used in a business rule (a business rule takes if, setValue, setDefault, clearValue, showError, setVisibility, setLock, setRequirement).");
                return null;
            }
            if (d.Kind != WorkflowKind.BusinessRule && ruleStep)
            {
                Error(node.Span, $"The step '{node.GetType().Name}' is only valid in a business rule.");
                return null;
            }
            if (ruleStep && _conditionDepth == 0)
            {
                Error(node.Span, "A business rule action must be inside an if: a rule without a condition is not supported yet, because no designer export confirms its shape.");
                return null;
            }
            switch (node)
            {
                case CreateNode create:
                {
                    var table = Table(create.Entity, create.Span);
                    var set = table is null ? null : await Columns(table, create.Set, create: true);
                    if (create.Id is { } id)
                        _declared[id] = new Declared("create", create.Entity, []);
                    return set is null ? null : new D.Create(create.Entity, create.Id, set, create.Description);
                }
                case UpdateNode update:
                {
                    var record = Record(update.Record, update.Span);
                    var set = record is null || env.GetEntity(record.Entity) is not { } table ? null : await Columns(table, update.Set, create: false);
                    return set is null ? null : new D.Update(record!, set, update.Description);
                }
                case AssignNode assign:
                {
                    var record = Record(assign.Record, assign.Span);
                    if (record is null || env.GetEntity(record.Entity) is not { } table)
                        return null;
                    if (Column(table, "ownerid") is not { AttributeType: AttributeTypeCode.Owner } owner)
                    {
                        Error(assign.Span, $"The table '{record.Entity}' has no owner, so its records cannot be assigned.");
                        return null;
                    }
                    var value = await Value(assign.Owner, new Destination(PropertyType.EntityReference, owner, "the owner"));
                    return value is null ? null : new D.AssignOwner(record, value, assign.Description);
                }
                case SetStateNode setState:
                    return SetState(setState);
                case SendEmailNode email:
                {
                    var table = Table("email", email.Span);
                    var set = table is null ? null : await Columns(table, email.Set, create: true);
                    if (email.Id is { } id)
                        _declared[id] = new Declared("sendEmail", "email", []);
                    return set is null ? null : new D.SendEmail(email.Id, set, email.Description);
                }
                case IfNode condition:
                {
                    var branches = new List<D.Branch>();
                    var ok = true;
                    _conditionDepth++;
                    foreach (var branch in condition.Branches)
                    {
                        var when = await Condition(branch.When);
                        var then = await StepsAsync(branch.Then);
                        if (when is null)
                            ok = false;
                        else
                            branches.Add(new D.Branch(when, then));
                    }
                    var otherwise = condition.Else is null ? null : await StepsAsync(condition.Else);
                    _conditionDepth--;
                    return ok ? new D.If(branches, otherwise, condition.Description) : null;
                }
                case WaitNode wait:
                    return await WaitAsync(wait);
                case StopNode stop:
                {
                    if (stop.Message is null or LiteralNode { Text: null })
                        return new D.Stop(stop.Canceled, null, stop.Description);
                    var message = await Value(stop.Message, new Destination(PropertyType.String, null, "the stop message"));
                    return message is null ? null : new D.Stop(stop.Canceled, message, stop.Description);
                }
                case StartChildNode child:
                    return await StartChildAsync(child);
                case PerformActionNode action:
                {
                    IReadOnlyList<CalleeArgument>? callee = null;
                    if (CheckText(action.Message, action.Span, "The message of the perform action step"))
                    {
                        callee = await env.MessageArgumentsAsync(action.Message, ct);
                        if (callee is null)
                            Error(action.MessageSpan ?? action.Span, $"No action, custom API or message is named '{action.Message}'.");
                    }
                    var target = action.Target is null ? null : Record(action.Target, action.Span);
                    var inputs = await Inputs(action.Inputs, "perform action", $"the message '{action.Message}'", callee);
                    if (action.Id is { } id)
                        _declared[id] = new Declared("performAction", null, callee?.Where(c => !c.IsInput).Select(c => c.Name).ToList());
                    return inputs is null || (action.Target is not null && target is null) ? null
                        : new D.PerformAction(action.Message, target, inputs, action.Id, action.Description);
                }
                case CustomActivityNode custom:
                    return await CustomActivityAsync(custom);
                case SetOutputNode output:
                    return await SetOutputAsync(output);
                case SetValueNode value:
                    return await RuleSetValueAsync(value);
                case ClearValueNode clear:
                    return RuleColumn(clear.Field, clear.Span, "clear") is { } cleared
                        ? new D.RuleSetValue(clear.Field, cleared.Type, new D.Empty(), DefaultOnly: false, clear.Description)
                        : null;
                case ShowErrorNode error:
                {
                    var ok = CheckText(error.Message, error.Span, "The error message");
                    if (error.Field is { } field && Column(_primary!, field) is null)
                    {
                        Error(error.Span, $"The table '{_primary!.LogicalName}' has no column '{field}'.");
                        ok = false;
                    }
                    return ok ? new D.RuleShowError(error.Field, error.Message, error.Description) : null;
                }
                case ClientOnlyNode client:
                {
                    if (Column(_primary!, client.Field) is null)
                    {
                        Error(client.Span, $"The table '{_primary!.LogicalName}' has no column '{client.Field}'.");
                        return null;
                    }
                    return new D.RuleClientOnly(client.Kind, client.Field, client.Setting, client.Description);
                }
                default:
                    Error(node.Span, $"The step '{node.GetType().Name}' cannot be used in a workflow.");
                    return null;
            }
        }

        /// <summary>The columns of a create, update or send email step, each valid for that write and typed (A10, A11).</summary>
        private async Task<List<D.Assignment>?> Columns(EntityMetadata table, IReadOnlyList<(string Column, ValueNode Value)> set, bool create)
        {
            var result = new List<D.Assignment>();
            var ok = true;
            foreach (var (name, node) in set)
            {
                if (TypedColumn(table, name, node.Span) is not { } column)
                {
                    ok = false;
                    continue;
                }
                if ((create ? column.Column.IsValidForCreate : column.Column.IsValidForUpdate) == false)
                {
                    Error(node.Span, $"The column '{name}' of '{table.LogicalName}' cannot be set on {(create ? "create" : "update")}.");
                    ok = false;
                    continue;
                }
                var value = await Value(node, new Destination(column.Type, column.Column, $"the column '{name}' of '{table.LogicalName}'", AllowEmpty: true));
                if (value is null)
                    ok = false;
                else
                    result.Add(new D.Assignment(name, column.Type, value));
            }
            return ok ? result : null;
        }

        private D.Step? SetState(SetStateNode node)
        {
            var record = Record(node.Record, node.Span);
            if (record is null || env.GetEntity(record.Entity) is not { } table)
                return null;
            if (Column(table, "statecode") is not StateAttributeMetadata state || Column(table, "statuscode") is not StatusAttributeMetadata status)
            {
                Error(node.Span, $"The table '{record.Entity}' has no state and status.");
                return null;
            }
            var ok = true;
            if (!PropertyTypes.Options(state).Contains(node.State))
            {
                Error(node.Span, $"The state {node.State} is not a state of '{record.Entity}' (values: {string.Join(", ", PropertyTypes.Options(state))}).");
                ok = false;
            }
            var statuses = status.OptionSet?.Options.OfType<StatusOptionMetadata>().Where(o => o.State == node.State).Select(o => o.Value!.Value).ToList() ?? [];
            if (ok && !statuses.Contains(node.Status))
            {
                Error(node.Span, $"The status {node.Status} is not a status of the state {node.State} of '{record.Entity}' (values: {string.Join(", ", statuses)}).");
                ok = false;
            }
            return ok ? new D.SetState(record, node.State, node.Status, node.Description) : null;
        }

        private async Task<D.Step?> WaitAsync(WaitNode wait)
        {
            var until = wait.Until is null ? null : await Condition(wait.Until);
            var timeout = wait.Timeout is null ? null : await Value(wait.Timeout, new Destination(PropertyType.DateTime, null, "the timeout"));
            if (wait.Until is null && wait.OnTimeout is not null)
                Error(wait.Span, "A wait without 'until' always ends by its timeout: put the steps of 'onTimeout' in 'then'.");
            var then = await StepsAsync(wait.Then);
            var onTimeout = wait.OnTimeout is null ? null : await StepsAsync(wait.OnTimeout);
            if ((wait.Until is not null && until is null) || (wait.Timeout is not null && timeout is null))
                return null;
            return new D.Wait(until, timeout, then, onTimeout, wait.Description);
        }

        private async Task<D.Step?> StartChildAsync(StartChildNode child)
        {
            var record = Record(child.Record, child.Span);
            var found = await env.FindDefinitionsAsync(child.Workflow, ct);
            switch (found.Count)
            {
                case 0:
                    Error(child.Span, $"No process is named '{child.Workflow}' or has that id.");
                    return null;
                case > 1:
                    Error(child.Span, $"Several processes are named '{child.Workflow}' ({string.Join(", ", found.Select(f => f.Id.ToString("D")))}): give the id of the one to start.");
                    return null;
            }
            var definition = found[0];
            if (!definition.Subprocess)
            {
                Error(child.Span, $"The process '{definition.Name}' ({definition.Id:D}) is not a child process (childProcess: true), so it cannot be started from another workflow.");
                return null;
            }
            if (record is not null && !string.Equals(definition.Entity, record.Entity, StringComparison.Ordinal))
            {
                Error(child.Span, $"The child process '{definition.Name}' runs on '{definition.Entity}', not on '{record.Entity}'.");
                return null;
            }
            return record is null ? null : new D.StartChild(definition.Id, record, child.Description);
        }

        private async Task<D.Step?> CustomActivityAsync(CustomActivityNode custom)
        {
            var ok = CheckText(custom.Type, custom.Span, "The type of the custom activity step")
                     & CheckText(custom.Assembly, custom.Span, "The assembly of the custom activity step");
            var dot = custom.Type.LastIndexOf('.');
            if (ok && (dot <= 0 || !custom.Type.Split('.').All(IsIdentifier)))
            {
                // The XAML names the activity with an element of its namespace: Namespace.Type of identifiers.
                Error(custom.Span, $"The custom activity type '{custom.Type}' must be a full type name (Namespace.Type, each part a word).");
                ok = false;
            }
            else if (ok && env.WorkflowSdkAvailable && !env.IsWorkflowActivity(custom.Type, custom.Assembly))
            {
                Error(custom.Span, $"No registered plug-in assembly '{custom.Assembly}' holds the custom workflow activity '{custom.Type}'.");
                ok = false;
            }
            // The activity's own arguments (ruling R6), when the host can read them (the SDK is there and the activity registered).
            var callee = ok && env.WorkflowSdkAvailable ? env.WorkflowActivityArguments(custom.Type, custom.Assembly) : null;
            var inputs = await Inputs(custom.Inputs, "custom activity", $"the custom activity '{custom.Type}'", callee);
            var outputs = custom.Outputs.Select(o => (o.Property, o.Name)).ToList();
            for (var i = 0; i < outputs.Count; i++)
            {
                var (property, name) = outputs[i];
                var at = custom.OutputSpans.Count > i ? custom.OutputSpans[i] : custom.Span;
                if (!IsIdentifier(property) && CheckText(property, at, "The output property"))
                {
                    Error(at, $"The output property '{property}' must be a word: a letter or _, then letters, digits and _.");
                    ok = false;
                }
                else if (!IsIdentifier(property))
                    ok = false;
                else if (name.Length == 0)
                {
                    Error(at, $"The output '{property}' has an empty name: name it with a word (a letter or _, then letters, digits and _).");
                    ok = false;
                }
                else if (!IsIdentifier(name))
                {
                    Error(at, $"The output name '{name}' must be a word: a letter or _, then letters, digits and _.");
                    ok = false;
                }
                else if (callee is not null && !callee.Any(c => !c.IsInput && c.Name == property))
                {
                    Error(at, $"The custom activity '{custom.Type}' has no output '{property}' (outputs: {Names(callee.Where(c => !c.IsInput).Select(c => c.Name))}).");
                    ok = false;
                }
            }
            if (custom.Id is { } id)
                _declared[id] = new Declared("customActivity", null, outputs.Select(o => o.Name).ToList());
            return ok && inputs is not null ? new D.CustomActivity(custom.Type, custom.Assembly, inputs, outputs, custom.Id, custom.Description) : null;
        }

        /// <summary>
        /// The inputs of a perform action or custom activity step, each typed by the callee's input of that name (ruling R6, A6); an
        /// input the callee does not have is an error. When the callee's arguments are not known (<paramref name="callee"/> null), or
        /// its type is none the designer has, an input is typed by its value, and a literal cannot be typed.
        /// </summary>
        private async Task<List<D.Input>?> Inputs(IReadOnlyList<(string Name, ValueNode Value)> inputs, string step, string calleeName,
            IReadOnlyList<CalleeArgument>? callee)
        {
            var result = new List<D.Input>();
            var ok = true;
            foreach (var (name, node) in inputs)
            {
                // A custom activity's input is an element named after it; an action's, the x:Key of its argument.
                if (!CheckText(name, node.Span, $"The name of an input of the {step} step"))
                {
                    ok = false;
                    continue;
                }
                if (step == "custom activity" && !IsIdentifier(name))
                {
                    Error(node.Span, $"The input name '{name}' must be a word: a letter or _, then letters, digits and _.");
                    ok = false;
                    continue;
                }
                var what = $"the input '{name}' of the {step} step";
                PropertyType? type = null;
                if (callee is not null)
                {
                    if (callee.FirstOrDefault(c => c.IsInput && c.Name == name) is not { } argument)
                    {
                        Error(node.Span, $"{Capital(calleeName)} has no input '{name}' (inputs: {Names(callee.Where(c => c.IsInput).Select(c => c.Name))}).");
                        ok = false;
                        continue;
                    }
                    type = PropertyTypes.OfClr(argument.ClrType);
                }
                if (type is not null)
                {
                    if (await Value(node, new Destination(type, null, what)) is { } value)
                        result.Add(new D.Input(name, type, value));
                    else
                        ok = false;
                }
                else if (await Natural(node, what, literalsAreText: false) is { } typed)
                    result.Add(new D.Input(name, typed.Type, typed.Value));
                else
                    ok = false;
            }
            return ok ? result : null;
        }

        // ---- action output (A15) ---------------------------------------------------------------------------------------------

        private async Task<D.Step?> SetOutputAsync(SetOutputNode node)
        {
            if (d.Kind != WorkflowKind.Action)
            {
                Error(node.Span, "The step 'setOutput' is only valid in an action.");
                return null;
            }
            var outputs = Names(_arguments.Values.Where(a => !a.IsInput).Select(a => a.Name));
            if (!_arguments.TryGetValue(node.Argument, out var argument))
            {
                Error(node.Span, $"The action has no output argument '{node.Argument}' (outputs: {outputs}).");
                return null;
            }
            if (argument.IsInput)
            {
                Error(node.Span, $"'{node.Argument}' is an input argument: setOutput sets an output argument (outputs: {outputs}).");
                return null;
            }
            var value = await Value(node.Value, new Destination(argument.Type, null, $"the output argument '{node.Argument}'"));
            return value is null ? null : new D.SetOutput(node.Argument, argument.Type, value, node.Description);
        }

        // ---- business rule actions (A5) --------------------------------------------------------------------------------------

        /// <summary>A column of the primary table a business rule action sets or clears: one the server can update (A10).</summary>
        private (AttributeMetadata Column, PropertyType Type)? RuleColumn(string field, SourceSpan at, string action)
        {
            if (TypedColumn(_primary!, field, at) is not { } column)
                return null;
            if (column.Column.IsValidForUpdate == false)
            {
                Error(at, $"The column '{field}' of '{_primary!.LogicalName}' cannot be updated, so a business rule cannot {action} it.");
                return null;
            }
            return column;
        }

        private async Task<D.Step?> RuleSetValueAsync(SetValueNode node)
        {
            var kind = node.DefaultOnly ? "setDefault" : "setValue";
            if (RuleColumn(node.Field, node.Span, "set") is not { } column)
                return null;
            if (node.Value is LiteralNode { Text: null })
            {
                // One XAML for one meaning: the empty value is clearValue's (A12).
                Error(node.Value.Span, $"{kind} with the empty value (~): use clearValue: {{ field: {node.Field} }}.");
                return null;
            }
            var value = await Value(node.Value, new Destination(column.Type, column.Column, $"the column '{node.Field}' of '{_primary!.LogicalName}'"));
            return value is null ? null : new D.RuleSetValue(node.Field, column.Type, value, node.DefaultOnly, node.Description);
        }

        // ---- names ------------------------------------------------------------------------------------------------------------

        /// <summary>Text the XAML can hold; otherwise an error naming <paramref name="what"/>.</summary>
        private bool CheckText(string text, SourceSpan at, string what)
        {
            if (IsXmlText(text))
                return true;
            Error(at, $"{what} holds a character XML cannot hold.");
            return false;
        }

        /// <summary>
        /// A step id (review M3): the XAML uses it as a VB variable (a perform action's result) or a created record's key next to the
        /// designer's own names, so it must be a VB identifier that is no VB keyword and no name the designer generates. VB names
        /// ignore case.
        /// </summary>
        private void CheckName(string id, SourceSpan at, string what)
        {
            if (!CheckText(id, at, what))
                return;
            if (!IsIdentifier(id))
                Error(at, $"{what} '{id}' must be a word: a letter or _, then letters, digits and _.");
            else if (VbKeywords.Contains(id))
                Error(at, $"{what} '{id}' is a VB keyword the XAML expressions cannot use as a name.");
            else if (GeneratedNames.Contains(id) || GeneratedStepName().IsMatch(id))
                Error(at, $"{what} '{id}' is a name the designer generates: choose another.");
        }

        private static bool IsIdentifier(string name) =>
            name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && name != "_";

        /// <summary>The names the designer writes besides the step names (A8, A9).</summary>
        private static readonly HashSet<string> GeneratedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "InputEntities", "CreatedEntities", "UpdatedEntities", "primaryEntity", "stepLabelLabelId", "stepLabelDescription",
            "stepLabelLanguageCode", "DebuggerManager", "Workflow", "CustomOperationArguments",
            // An action's generated bound argument and the variable of an output assignment (export Recalculate, Task 3).
            "Target", "TypeName",
        };

        /// <summary>The VB keywords (reserved words) an expression cannot use as a plain name.</summary>
        private static readonly HashSet<string> VbKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "AddHandler", "AddressOf", "Alias", "And", "AndAlso", "As", "Boolean", "ByRef", "Byte", "ByVal", "Call", "Case", "Catch", "CBool",
            "CByte", "CChar", "CDate", "CDbl", "CDec", "Char", "CInt", "Class", "CLng", "CObj", "Const", "Continue", "CSByte", "CShort", "CSng",
            "CStr", "CType", "CUInt", "CULng", "CUShort", "Date", "Decimal", "Declare", "Default", "Delegate", "Dim", "DirectCast", "Do",
            "Double", "Each", "Else", "ElseIf", "End", "EndIf", "Enum", "Erase", "Error", "Event", "Exit", "False", "Finally", "For", "Friend",
            "Function", "Get", "GetType", "GetXMLNamespace", "Global", "GoSub", "GoTo", "Handles", "If", "Implements", "Imports", "In",
            "Inherits", "Integer", "Interface", "Is", "IsNot", "Let", "Lib", "Like", "Long", "Loop", "Me", "Mod", "Module", "MustInherit",
            "MustOverride", "MyBase", "MyClass", "Namespace", "Narrowing", "New", "Next", "Not", "Nothing", "NotInheritable",
            "NotOverridable", "Object", "Of", "On", "Operator", "Option", "Optional", "Or", "OrElse", "Out", "Overloads", "Overridable",
            "Overrides", "ParamArray", "Partial", "Private", "Property", "Protected", "Public", "RaiseEvent", "ReadOnly", "ReDim", "REM",
            "RemoveHandler", "Resume", "Return", "SByte", "Select", "Set", "Shadows", "Shared", "Short", "Single", "Static", "Step", "Stop",
            "String", "Structure", "Sub", "SyncLock", "Then", "Throw", "To", "True", "Try", "TryCast", "TypeOf", "UInteger", "ULong",
            "UShort", "Using", "Variant", "Wend", "When", "While", "Widening", "With", "WithEvents", "WriteOnly", "Xor",
        };

        // ---- messages --------------------------------------------------------------------------------------------------------

        private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

        private static string Article(PropertyType type) => type.Name[0] is 'A' or 'E' or 'I' or 'O' or 'U' ? $"an {type.Name}" : $"a {type.Name}";
    }
}
