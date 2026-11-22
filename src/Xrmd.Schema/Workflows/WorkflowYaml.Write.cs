using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Schema;

public static partial class WorkflowYaml
{
    /// <summary>Writes <paramref name="document"/> in the canonical form: the key order of the format, defaults omitted, flow style for one-line mappings.</summary>
    public static string Write(WorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new YamlMappingNode { { "kind", Scalar(KindName(document.Kind)) } };
        if (document.Id is { } id) root.Add("id", Scalar(id.ToString("D")));
        root.Add("name", Scalar(document.Name));
        AddText(root, "description", document.Description);
        root.Add("entity", Scalar(document.Entity));
        var o = document.Options;
        if (document.Kind == WorkflowKind.Workflow)
        {
            if (o.Mode is not ("realtime" or "background"))
                throw new ArgumentException($"The workflow '{document.Name}' has the mode '{o.Mode ?? "null"}'; a workflow needs the mode realtime or background.", nameof(document));
            root.Add("mode", Scalar(o.Mode));
            if (o.Scope is { } scope and not "user") root.Add("scope", Scalar(scope));
            if (o.RunAs is { } runAs and not "caller") root.Add("runAs", Scalar(runAs));
            if (!o.LogFailures) root.Add("logFailures", Bool(false));
            if (o.AutoDelete) root.Add("autoDelete", Bool(true));
            if (o.OnDemand) root.Add("onDemand", Bool(true));
            if (o.ChildProcess) root.Add("childProcess", Bool(true));
            if (document.Trigger is { } trigger) root.Add("trigger", Trigger(trigger));
        }
        if (document.Kind == WorkflowKind.Action)
        {
            AddText(root, "message", document.Message);
            if (document.Arguments.Count > 0)
                root.Add("arguments", new YamlSequenceNode(document.Arguments.Select(a => (YamlNode)Argument(a))));
        }
        root.Add("steps", Steps(document.Steps));

        var text = new StringBuilder();
        YamlText.BlockMapping(text, root, 0, firstInline: false);
        return text.ToString();
    }

    // ---------------------------------------------------------------- writing

    private static ArgumentException Invalid(string message) => new(message);

    private static string KindName(WorkflowKind kind) => kind switch
    {
        WorkflowKind.Workflow => "workflow",
        WorkflowKind.BusinessRule => "businessRule",
        _ => "action",
    };

    private static YamlMappingNode Trigger(TriggerNode t)
    {
        var node = new YamlMappingNode();
        if (t.Create is { } create)
        {
            if (create.Stage is not (null or "pre" or "post")) throw Invalid($"The create trigger has the stage '{create.Stage}' (pre or post).");
            node.Add("create", create.Stage is "pre" ? Flow(Map(("stage", Scalar("pre")))) : Plain("true"));
        }
        if (t.Update is { } update)
        {
            if (update.Columns.Count == 0) throw Invalid("The update trigger has no columns; it needs the columns it watches.");
            if (update.Stage is not (null or "pre" or "post")) throw Invalid($"The update trigger has the stage '{update.Stage}' (pre or post).");
            var map = new YamlMappingNode { { "columns", FlowSeq(update.Columns.Select(c => (YamlNode)Scalar(c))) } };
            if (update.Stage is "pre") map.Add("stage", Scalar("pre"));
            node.Add("update", Flow(map));
        }
        if (t.Delete is { } delete)
        {
            if (delete.Stage is not (null or "pre")) throw Invalid($"The delete trigger has the stage '{delete.Stage}' (pre, the only one).");
            node.Add("delete", Plain("true"));
        }
        return node;
    }

    private static YamlMappingNode Argument(ArgumentNode a)
    {
        if (!ArgumentTypes.Contains(a.Type, StringComparer.Ordinal))
            throw Invalid($"The argument '{a.Name}' has the type '{a.Type}' (allowed: {string.Join(", ", ArgumentTypes)}).");
        var node = new YamlMappingNode
        {
            { "name", Scalar(a.Name) },
            { "direction", Scalar(a.IsInput ? "input" : "output") },
            { "type", Scalar(a.Type) },
        };
        AddText(node, "entity", a.Entity);
        if (a.Required) node.Add("required", Bool(true));
        if (a.Description.Length > 0) node.Add("description", Scalar(a.Description));
        return Flow(node);
    }

    private static YamlSequenceNode Steps(IReadOnlyList<StepNode> steps) =>
        new(steps.Select(s => (YamlNode)new YamlMappingNode { { StepKey(s), StepBody(s) } }));

    private static string StepKey(StepNode step) => step switch
    {
        CreateNode => "create",
        UpdateNode => "update",
        AssignNode => "assign",
        SetStateNode => "setState",
        SendEmailNode => "sendEmail",
        IfNode => "if",
        WaitNode => "wait",
        StopNode => "stop",
        StartChildNode => "startChild",
        PerformActionNode => "performAction",
        CustomActivityNode => "customActivity",
        SetOutputNode => "setOutput",
        SetValueNode { DefaultOnly: true } => "setDefault",
        SetValueNode => "setValue",
        ClearValueNode => "clearValue",
        ShowErrorNode => "showError",
        ClientOnlyNode c => c.Kind,
        _ => throw new ArgumentException($"Unknown step type {step.GetType().Name}."),
    };

    /// <summary>The body of a step: a block mapping with the description first, or (for the one-line kinds) a flow mapping with the description last.</summary>
    private static YamlMappingNode StepBody(StepNode step)
    {
        var map = new YamlMappingNode();
        switch (step)
        {
            case CreateNode c:
                AddText(map, "id", c.Id);
                AddText(map, "description", c.Description);
                map.Add("entity", Scalar(c.Entity));
                map.Add("set", Set(c.Set));
                return map;
            case UpdateNode u:
                AddText(map, "description", u.Description);
                map.Add("record", Record(u.Record));
                map.Add("set", Set(u.Set));
                return map;
            case SendEmailNode e:
                AddText(map, "id", e.Id);
                AddText(map, "description", e.Description);
                map.Add("set", Set(e.Set));
                return map;
            case IfNode i:
                if (i.Branches.Count == 0) throw Invalid("An if step has no branch: it needs at least the when branch.");
                AddText(map, "description", i.Description);
                map.Add("when", Cond(i.Branches[0].When));
                map.Add("then", Steps(i.Branches[0].Then));
                if (i.Branches.Count > 1)
                    map.Add("elseIf", new YamlSequenceNode(i.Branches.Skip(1).Select(b =>
                        (YamlNode)new YamlMappingNode { { "when", Cond(b.When) }, { "then", Steps(b.Then) } })));
                if (i.Else is { } otherwise) map.Add("else", Steps(otherwise));
                return map;
            case WaitNode w:
                AddText(map, "description", w.Description);
                if (w.Until is { } until) map.Add("until", Cond(until));
                if (w.Timeout is { } timeout) map.Add("timeout", Value(timeout));
                map.Add("then", Steps(w.Then));
                if (w.OnTimeout is { } onTimeout) map.Add("onTimeout", Steps(onTimeout));
                return map;
            case PerformActionNode a:
                AddText(map, "id", a.Id);
                AddText(map, "description", a.Description);
                map.Add("message", Scalar(a.Message));
                if (a.Target is { } target) map.Add("target", Record(target));
                if (a.Inputs.Count > 0) map.Add("inputs", Set(a.Inputs));
                return map;
            case CustomActivityNode x:
                AddText(map, "id", x.Id);
                AddText(map, "description", x.Description);
                map.Add("type", Scalar(x.Type));
                map.Add("assembly", Scalar(x.Assembly));
                if (x.Inputs.Count > 0) map.Add("inputs", Set(x.Inputs));
                if (x.Outputs.Count > 0)
                {
                    var outputs = new YamlMappingNode();
                    foreach (var (property, name) in x.Outputs) outputs.Add(Scalar(property), Scalar(name));
                    map.Add("outputs", outputs);
                }
                return map;
        }

        switch (step)
        {
            case AssignNode a:
                map.Add("record", Record(a.Record));
                map.Add("owner", Value(a.Owner));
                break;
            case SetStateNode s:
                map.Add("record", Record(s.Record));
                map.Add("state", Plain(s.State.ToString(CultureInfo.InvariantCulture)));
                map.Add("status", Plain(s.Status.ToString(CultureInfo.InvariantCulture)));
                break;
            case StopNode stop:
                map.Add("status", Scalar(stop.Canceled ? "canceled" : "succeeded"));
                if (stop.Message is { } message) map.Add("message", Value(message));
                break;
            case StartChildNode c:
                map.Add("workflow", Scalar(c.Workflow));
                if (c.Record is not RecordNode.Primary) map.Add("record", Record(c.Record));
                break;
            case SetOutputNode o:
                map.Add("argument", Scalar(o.Argument));
                map.Add("value", Value(o.Value));
                break;
            case SetValueNode v:
                map.Add("field", Scalar(v.Field));
                map.Add("value", Value(v.Value));
                break;
            case ClearValueNode c:
                map.Add("field", Scalar(c.Field));
                break;
            case ShowErrorNode e:
                AddText(map, "field", e.Field);
                map.Add("message", Scalar(e.Message));
                break;
            case ClientOnlyNode c:
                var settings = c.Kind switch
                {
                    "setVisibility" or "setLock" => new[] { "true", "false" },
                    "setRequirement" => Requirements,
                    _ => throw Invalid($"The business rule step '{c.Kind}' is not setVisibility, setLock or setRequirement."),
                };
                if (!settings.Contains(c.Setting, StringComparer.Ordinal))
                    throw Invalid($"The {c.Kind} step has the setting '{c.Setting}' (allowed: {string.Join(", ", settings)}).");
                map.Add("field", Scalar(c.Field));
                map.Add(c.Kind switch { "setVisibility" => "visible", "setLock" => "locked", _ => "level" }, Plain(c.Setting));
                break;
        }
        AddText(map, "description", step.Description);
        return Flow(map);
    }

    private static YamlMappingNode Set(IEnumerable<(string Name, ValueNode Value)> entries)
    {
        var map = new YamlMappingNode();
        foreach (var (name, value) in entries) map.Add(Scalar(name), Value(value));
        if (map.Children.Count == 0) map.Style = MappingStyle.Flow;
        return map;
    }

    private static YamlNode Record(RecordNode record) => record switch
    {
        RecordNode.Primary => Plain("primary"),
        RecordNode.Related r => Flow(Map(("related", Scalar(r.Lookup)))),
        RecordNode.Created c => Flow(Map(("created", Scalar(c.StepId)))),
        _ => throw new ArgumentException($"Unknown record type {record.GetType().Name}."),
    };

    private static YamlNode Value(ValueNode value)
    {
        switch (value)
        {
            case LiteralNode l:
                return l.Text is null ? Plain("~") : Scalar(l.Text);
            case FieldNode f:
            {
                var map = Map(("field", Scalar(f.Field)));
                switch (f.Record)
                {
                    case RecordNode.Related r: map.Add("related", Scalar(r.Lookup)); break;
                    case RecordNode.Created c: map.Add("created", Scalar(c.StepId)); break;
                }
                return Flow(map);
            }
            case RecordValueNode r:
                return Flow(Map(("record", Record(r.Record))));
            case ArgNode a:
                return Flow(Map(("arg", Scalar(a.Name))));
            case OutputNode o:
                return Flow(Map(("output", Scalar(o.Name)), ("of", Scalar(o.StepId))));
            case NowNode:
                return Flow(Map(("now", Plain("true"))));
            case LookupNode l:
            {
                var map = Map(("lookup", Scalar(l.Entity)), ("id", Scalar(l.Id.ToString("D"))));
                AddText(map, "name", l.Name);
                return Flow(map);
            }
            case OperatorNode op:
                if (!ValueOperators.Contains(op.Operator, StringComparer.Ordinal))
                    throw Invalid($"'{op.Operator}' is not a value operator (allowed: {string.Join(", ", ValueOperators)}).");
                return Flow(Map((op.Operator, FlowSeq(op.Operands.Select(Value)))));
            default:
                throw new ArgumentException($"Unknown value type {value.GetType().Name}.");
        }
    }

    private static YamlNode Cond(CondNode cond)
    {
        switch (cond)
        {
            case CompareNode c:
            {
                if (!Operators.Contains(c.Op, StringComparer.Ordinal))
                    throw Invalid($"'{c.Op}' is not a comparison operator (allowed: {string.Join(", ", Operators)}).");
                if (c.Left is LiteralNode)
                    throw Invalid($"A comparison with the operator '{c.Op}' has a literal as its left operand; the left operand must be a field, argument, output or other dynamic value.");
                if ((c.Op is "null" or "notNull") != (c.Right is null))
                    throw Invalid(c.Op is "null" or "notNull" ? $"The operator '{c.Op}' takes no value." : $"The operator '{c.Op}' needs a value.");
                var map = (YamlMappingNode)Value(c.Left);
                map.Add("op", Plain(c.Op));
                if (c.Right is { } right) map.Add("value", Value(right));
                return map;
            }
            case AllNode { Conditions.Count: 0 }:
                throw Invalid("An all condition has no conditions.");
            case AnyNode { Conditions.Count: 0 }:
                throw Invalid("An any condition has no conditions.");
            case AllNode all:
                return Flow(Map(("all", FlowSeq(all.Conditions.Select(Cond)))));
            case AnyNode any:
                return Flow(Map(("any", FlowSeq(any.Conditions.Select(Cond)))));
            case NotNode not:
                return Flow(Map(("not", Cond(not.Condition))));
            default:
                throw new ArgumentException($"Unknown condition type {cond.GetType().Name}.");
        }
    }

    private static YamlMappingNode Map(params (string Key, YamlNode Value)[] entries)
    {
        var map = new YamlMappingNode();
        foreach (var (key, value) in entries) map.Add(key, value);
        return map;
    }

    private static YamlMappingNode Flow(YamlMappingNode node)
    {
        node.Style = MappingStyle.Flow;
        return node;
    }

    private static YamlSequenceNode FlowSeq(IEnumerable<YamlNode> items) => new(items) { Style = SequenceStyle.Flow };

    private static void AddText(YamlMappingNode node, string key, string? value)
    {
        if (value is not null) node.Add(key, Scalar(value));
    }

    private static YamlScalarNode Bool(bool value) => Plain(value ? "true" : "false");

    private static YamlScalarNode Plain(string value) => new(value) { Style = ScalarStyle.Plain };

    /// <summary>A text scalar; one a plain scalar would read as no value (empty, <c>~</c>, <c>null</c>) is quoted, and so is one with a backslash.</summary>
    private static YamlScalarNode Scalar(string value) => new(value)
    {
        Style = YamlText.IsNullText(value) || value.Contains('\\') ? ScalarStyle.SingleQuoted : ScalarStyle.Any,
    };
}
