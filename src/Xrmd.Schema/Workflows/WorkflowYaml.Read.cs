using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using ScalarEvent = YamlDotNet.Core.Events.Scalar;

namespace Xrmd.Schema;

public static partial class WorkflowYaml
{
    /// <summary>Reads a workflow YAML document.</summary>
    /// <exception cref="SchemaFormatException">The text is not valid YAML or not a valid workflow (line and column of the problem).</exception>
    public static WorkflowDocument Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (Encoding.UTF8.GetByteCount(yaml) > MaxBytes)
            throw new SchemaFormatException($"The workflow is larger than {MaxBytes / 1_048_576} MiB.", 1, 1);
        var stream = new YamlStream();
        try
        {
            Scan(yaml);
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new SchemaFormatException(Inner(ex).Message, ex.Start.Line, ex.Start.Column, ex);
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            throw new SchemaFormatException("The workflow is empty.", 1, 1);
        if (stream.Documents.Count > 1)
            throw Error(stream.Documents[1].RootNode, "The workflow must be a single YAML document.");
        return new Reader().Read(stream.Documents[0].RootNode);
    }

    // ---------------------------------------------------------------- reading

    private static Exception Inner(YamlException ex) => ex.InnerException is YamlException inner ? Inner(inner) : ex.InnerException ?? ex;

    private static SchemaFormatException Error(YamlNode node, string message) => new(message, node.Start.Line, node.Start.Column);

    private static SchemaFormatException Error(Mark at, string message) => new(message, at.Line, at.Column);

    /// <summary>
    /// A pass over the parser events before the tree is built: the nesting limit (the tree loader recurses, so a hostile
    /// body must be refused before it), duplicate keys, anchors and aliases (a shared node would be read once per use).
    /// </summary>
    private static void Scan(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var stack = new Stack<(bool IsMapping, HashSet<string>? Keys, bool[] ExpectKey)>();
        while (parser.MoveNext())
        {
            var current = parser.Current!;
            if (current is AnchorAlias) throw Error(current.Start, "Anchors and aliases are not supported in a workflow.");
            if (current is MappingEnd or SequenceEnd)
            {
                stack.Pop();
                continue;
            }
            if (current is not (ScalarEvent or MappingStart or SequenceStart)) continue;
            if (current is NodeEvent { Anchor.IsEmpty: false }) throw Error(current.Start, "Anchors and aliases are not supported in a workflow.");

            if (stack.Count > 0 && stack.Peek() is { IsMapping: true } top)
            {
                if (top.ExpectKey[0])
                {
                    if (current is not ScalarEvent key) throw Error(current.Start, "A key must be a single word.");
                    if (!top.Keys!.Add(key.Value)) throw Error(current.Start, $"The key '{key.Value}' appears more than once.");
                    top.ExpectKey[0] = false;
                    continue;
                }
                top.ExpectKey[0] = true;
            }

            if (current is MappingStart or SequenceStart)
            {
                if (stack.Count + 1 > MaxDepth) throw Error(current.Start, $"The workflow is nested deeper than {MaxDepth} levels.");
                stack.Push(current is MappingStart ? (true, new HashSet<string>(StringComparer.Ordinal), [true]) : (false, null, [false]));
            }
        }
    }

    private static SourceSpan Span(YamlNode node) => new((int)node.Start.Line, (int)node.Start.Column);

    private static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } scalar && (scalar.Value is null || YamlText.IsNullText(scalar.Value));

    /// <summary>One mapping with its keys checked against <paramref name="allowed"/>.</summary>
    private sealed class Fields
    {
        private readonly Dictionary<string, (YamlNode Key, YamlNode Value)> _values = new(StringComparer.Ordinal);

        public Fields(YamlNode node, string what, params string[] allowed)
        {
            Node = node as YamlMappingNode ?? throw Error(node, $"{what} must be a mapping (key: value).");
            foreach (var (key, value) in Node.Children)
            {
                if (key is not YamlScalarNode { Value: { } text })
                    throw Error(key, $"A key of {what} must be a single word.");
                if (!allowed.Contains(text, StringComparer.Ordinal))
                    throw Error(key, $"Unknown key '{text}' in {what} (allowed: {string.Join(", ", allowed)}).");
                _values[text] = (key, value);
            }
            What = what;
        }

        public YamlMappingNode Node { get; }

        public string What { get; }

        public bool Has(string key) => _values.ContainsKey(key);

        public YamlNode? Get(string key) => _values.TryGetValue(key, out var entry) ? entry.Value : null;

        public YamlNode KeyNode(string key) => _values[key].Key;

        public YamlNode Required(string key) =>
            Get(key) is { } node && !IsNull(node) ? node : throw Error(Node, $"{What} needs the key '{key}'.");

        /// <summary>The text of a key that is a word (never the empty value).</summary>
        public string Text(string key) => ReadText(Required(key), key);

        /// <summary>A key that must be there but may hold the empty value (<c>value: ~</c>).</summary>
        public YamlNode Present(string key) => Get(key) ?? throw Error(Node, $"{What} needs the key '{key}'.");

        public string? OptionalText(string key) => Get(key) is { } node ? ReadText(node, key) : null;

        public bool? OptionalBool(string key) => Get(key) is { } node ? ReadBool(node, key) : null;
    }

    private static string ReadText(YamlNode node, string key, bool nullIsText = false)
    {
        if (node is not YamlScalarNode scalar) throw Error(node, $"'{key}' must be a single value.");
        if (!nullIsText && IsNull(scalar)) throw Error(node, $"'{key}' needs a value.");
        return scalar.Value ?? "";
    }

    private static bool ReadBool(YamlNode node, string key)
    {
        var text = ReadText(node, key);
        return text switch
        {
            "true" => true,
            "false" => false,
            _ => throw Error(node, $"'{key}' must be true or false, not '{text}'."),
        };
    }

    private static int ReadInt(YamlNode node, string key)
    {
        var text = ReadText(node, key);
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Error(node, $"'{key}' must be an integer, not '{text}'.");
    }

    private static string OneOf(YamlNode node, string key, params string[] allowed)
    {
        var text = ReadText(node, key);
        return allowed.Contains(text, StringComparer.Ordinal)
            ? text
            : throw Error(node, $"'{key}' is '{text}', which is not supported ({string.Join(" | ", allowed)}).");
    }

    private enum Context
    {
        RealtimeWorkflow,
        BackgroundWorkflow,
        BusinessRule,
        Action,
    }

    private sealed class Reader
    {
        private readonly Dictionary<string, string> _stepIds = new(StringComparer.Ordinal);

        private static readonly string[] CommonKeys = ["kind", "id", "name", "description", "entity", "steps"];
        private static readonly string[] WorkflowOnlyKeys = ["mode", "scope", "runAs", "logFailures", "autoDelete", "trigger", "onDemand", "childProcess"];
        private static readonly string[] ActionOnlyKeys = ["message", "arguments"];

        public WorkflowDocument Read(YamlNode root)
        {
            if (root is not YamlMappingNode map) throw Error(root, "The workflow must be a mapping (key: value).");
            var kindNode = map.Children.TryGetValue(new YamlScalarNode("kind"), out var found) && !IsNull(found)
                ? found
                : throw Error(map, "The workflow needs the key 'kind' (workflow, businessRule or action).");
            var kind = OneOf(kindNode, "kind", "workflow", "businessRule", "action") switch
            {
                "workflow" => WorkflowKind.Workflow,
                "businessRule" => WorkflowKind.BusinessRule,
                _ => WorkflowKind.Action,
            };

            // A key of another kind gets its own message, then the unknown keys are refused with the allowed list.
            foreach (var (key, _) in map.Children)
            {
                if (key is YamlScalarNode { Value: { } text })
                {
                    if (kind != WorkflowKind.Workflow && WorkflowOnlyKeys.Contains(text) && !(kind == WorkflowKind.BusinessRule && text == "scope"))
                        throw Error(key, $"The key '{text}' is only valid in a workflow, not in {Describe(kind)}.");
                    if (kind != WorkflowKind.Action && ActionOnlyKeys.Contains(text))
                        throw Error(key, $"The key '{text}' is only valid in an action, not in {Describe(kind)}.");
                }
            }
            string[] allowed = kind switch
            {
                WorkflowKind.Workflow => [.. CommonKeys, .. WorkflowOnlyKeys],
                WorkflowKind.BusinessRule => [.. CommonKeys, "scope"],
                _ => [.. CommonKeys, .. ActionOnlyKeys],
            };
            var fields = new Fields(map, "the workflow", allowed);

            var nameNode = fields.Required("name");
            var name = ReadText(nameNode, "name");
            if (name.Length > 100) throw Error(nameNode, $"The name is {name.Length} characters; at most 100 are allowed.");
            Guid? id = null;
            if (fields.Get("id") is { } idNode)
            {
                var idText = ReadText(idNode, "id");
                id = Guid.TryParse(idText, out var guid) ? guid : throw Error(idNode, $"The id '{idText}' is not a GUID.");
            }
            var entityNode = fields.Required("entity");
            var entity = ReadText(entityNode, "entity");
            if (kind != WorkflowKind.Action && entity == "none")
                throw Error(entityNode, $"Only an action can have the entity 'none' (a global action), not {Describe(kind)}.");

            var options = new WorkflowOptions();
            TriggerNode? trigger = null;
            var context = kind switch
            {
                WorkflowKind.BusinessRule => Context.BusinessRule,
                WorkflowKind.Action => Context.Action,
                _ => Context.RealtimeWorkflow,
            };
            if (kind == WorkflowKind.Workflow)
            {
                var modeNode = fields.Required("mode");
                var mode = OneOf(modeNode, "mode", "realtime", "background");
                var realtime = mode == "realtime";
                context = realtime ? Context.RealtimeWorkflow : Context.BackgroundWorkflow;
                if (!realtime) { RefuseKey(fields, "runAs", "a real-time workflow"); RefuseKey(fields, "logFailures", "a real-time workflow"); }
                else RefuseKey(fields, "autoDelete", "a background workflow");
                options = new WorkflowOptions
                {
                    Mode = mode,
                    Scope = fields.Get("scope") is { } scopeNode && OneOf(scopeNode, "scope", WorkflowScopes) is var scope and not "user" ? scope : null,
                    RunAs = fields.Get("runAs") is { } runAsNode && OneOf(runAsNode, "runAs", "caller", "owner") is var runAs and not "caller" ? runAs : null,
                    LogFailures = fields.OptionalBool("logFailures") ?? true,
                    AutoDelete = fields.OptionalBool("autoDelete") ?? false,
                    OnDemand = fields.OptionalBool("onDemand") ?? false,
                    ChildProcess = fields.OptionalBool("childProcess") ?? false,
                };
                if (fields.Get("trigger") is { } triggerNode) trigger = Trigger(triggerNode, realtime);
            }
            else if (kind == WorkflowKind.BusinessRule && fields.Get("scope") is { } ruleScope)
            {
                var text = ReadText(ruleScope, "scope");
                if (text != "entity")
                    throw Error(ruleScope, $"The business rule scope '{text}' is not supported: form-scope rules run only in forms, which xrmverse does not have yet (use scope: entity).");
                options = new WorkflowOptions { RuleScope = "entity" };
            }

            string? message = null;
            var arguments = new List<ArgumentNode>();
            if (kind == WorkflowKind.Action)
            {
                message = fields.Text("message");
                if (fields.Get("arguments") is { } argumentsNode) arguments.AddRange(Arguments(argumentsNode));
            }

            var stepsNode = fields.Get("steps") ?? throw Error(map, "The workflow needs the key 'steps'.");
            var steps = Steps(stepsNode, context);
            return new WorkflowDocument(kind, id, name, fields.OptionalText("description"), entity, steps)
            {
                Options = options, Trigger = trigger, Message = message, Arguments = arguments, Span = Span(root),
                DescriptionSpan = fields.Get("description") is { } descriptionNode ? Span(descriptionNode) : null,
                MessageSpan = kind == WorkflowKind.Action && fields.Get("message") is { } messageNode ? Span(messageNode) : null,
            };
        }

        private static string Describe(WorkflowKind kind) => kind switch
        {
            WorkflowKind.Workflow => "a workflow",
            WorkflowKind.BusinessRule => "a business rule",
            _ => "an action",
        };

        private static void RefuseKey(Fields fields, string key, string only)
        {
            if (fields.Has(key)) throw Error(fields.KeyNode(key), $"The key '{key}' is only valid in {only}.");
        }

        private static TriggerNode Trigger(YamlNode node, bool realtime)
        {
            var fields = new Fields(node, "the trigger", "create", "update", "delete");
            if (fields.Node.Children.Count == 0) throw Error(node, "The trigger must name create, update or delete.");
            CreateTrigger? create = null;
            UpdateTrigger? update = null;
            DeleteTrigger? delete = null;
            if (fields.Get("create") is { } createNode)
                create = new CreateTrigger(TriggerBody(createNode, "create", realtime, "post", "pre", "post"));
            if (fields.Get("update") is { } updateNode)
            {
                if (updateNode is not YamlMappingNode)
                    throw Error(updateNode, "'update' must be { columns: [<column>, ...], stage?: pre | post }: an update trigger needs the columns it watches.");
                var body = new Fields(updateNode, "the update trigger", "columns", "stage");
                var columnsNode = body.Get("columns");
                if (columnsNode is not YamlSequenceNode { Children.Count: > 0 } list)
                    throw Error(columnsNode ?? updateNode, "The update trigger needs 'columns', a non-empty list of the columns it watches.");
                var columns = list.Select(column => ReadText(column, "columns")).ToList();
                update = new UpdateTrigger(columns, StageOf(body, realtime, "post", "pre", "post")) { ColumnSpans = list.Select(Span).ToList() };
            }
            if (fields.Get("delete") is { } deleteNode)
                delete = new DeleteTrigger(TriggerBody(deleteNode, "delete", realtime, "pre", "pre"));
            return new TriggerNode(create, update, delete, Span(node));
        }

        /// <summary>The stage of a create or delete trigger (<c>true</c> or <c>{ stage }</c>); the default stage is read as null.</summary>
        private static string? TriggerBody(YamlNode node, string key, bool realtime, string defaultStage, params string[] stages)
        {
            if (node is YamlMappingNode)
                return StageOf(new Fields(node, $"the {key} trigger", "stage"), realtime, defaultStage, stages);
            if (!IsTrue(node)) throw Error(node, $"'{key}' must be true or {{ stage: {string.Join(" | ", stages)} }}.");
            return null;
        }

        private static string? StageOf(Fields fields, bool realtime, string defaultStage, params string[] stages)
        {
            if (fields.Get("stage") is not { } stageNode) return null;
            if (!realtime) throw Error(fields.KeyNode("stage"), "A stage is only valid in a real-time workflow.");
            var stage = OneOf(stageNode, "stage", stages);
            return stage == defaultStage ? null : stage;
        }

        private static bool IsTrue(YamlNode node) => node is YamlScalarNode { Value: "true", Style: ScalarStyle.Plain };

        private static IEnumerable<ArgumentNode> Arguments(YamlNode node)
        {
            if (node is not YamlSequenceNode list) throw Error(node, "'arguments' must be a list.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list)
            {
                var fields = new Fields(item, "an argument", "name", "direction", "type", "entity", "required", "description");
                var nameNode = fields.Required("name");
                var name = ReadText(nameNode, "name");
                if (name == "Target")
                    throw Error(nameNode, "An argument cannot be named 'Target': it is generated for a bound action.");
                if (!names.Add(name)) throw Error(nameNode, $"The argument '{name}' is defined more than once.");
                yield return new ArgumentNode(
                    name,
                    OneOf(fields.Required("direction"), "direction", "input", "output") == "input",
                    OneOf(fields.Required("type"), "type", [.. ArgumentTypes]),
                    fields.OptionalText("entity"),
                    fields.OptionalBool("required") ?? false,
                    fields.OptionalText("description") ?? "",
                    Span(item));
            }
        }

        // ------------------------------------------------------------ steps

        private List<StepNode> Steps(YamlNode node, Context context)
        {
            if (node is not YamlSequenceNode list) throw Error(node, "Steps must be a list (- kind: ...).");
            var steps = new List<StepNode>();
            foreach (var item in list) steps.Add(Step(item, context));
            return steps;
        }

        private StepNode Step(YamlNode item, Context context)
        {
            var step = StepBody(item, context);
            // The positions of the id and description values, for the compiler's messages about them.
            if (item is YamlMappingNode { Children.Count: 1 } map && map.Children.First().Value is YamlMappingNode body)
            {
                SourceSpan? At(string key) => body.Children.TryGetValue(new YamlScalarNode(key), out var value) ? Span(value) : null;
                step = step with { IdSpan = At("id"), DescriptionSpan = At("description") };
            }
            return step;
        }

        private StepNode StepBody(YamlNode item, Context context)
        {
            if (item is not YamlMappingNode map || map.Children.Count == 0)
                throw Error(item, "A step must be a mapping with one key, the kind of step (for example create: { ... }).");
            if (map.Children.Count > 1)
                throw Error(map.Children.Keys.ElementAt(1), $"A step takes exactly one kind; '{((YamlScalarNode)map.Children.Keys.First()).Value}' and '{(map.Children.Keys.ElementAt(1) as YamlScalarNode)?.Value}' are both given.");
            var (keyNode, body) = map.Children.First();
            if (keyNode is not YamlScalarNode { Value: { } kind }) throw Error(keyNode, "The kind of a step must be a single word.");
            var span = Span(item);
            CheckKind(keyNode, kind, context);

            switch (kind)
            {
                case "create":
                {
                    var f = new Fields(body, "a create step", "id", "description", "entity", "set");
                    var step = new CreateNode(f.Text("entity"), Set(f.Required("set"), "set"), f.OptionalText("id"), f.OptionalText("description"), span);
                    return Declare(f, step, kind);
                }
                case "update":
                {
                    var f = new Fields(body, "an update step", "description", "record", "set");
                    return new UpdateNode(Record(f.Required("record")), Set(f.Required("set"), "set"), f.OptionalText("description"), span);
                }
                case "assign":
                {
                    var f = new Fields(body, "an assign step", "description", "record", "owner");
                    return new AssignNode(Record(f.Required("record")), Value(f.Required("owner")), f.OptionalText("description"), span);
                }
                case "setState":
                {
                    var f = new Fields(body, "a setState step", "description", "record", "state", "status");
                    return new SetStateNode(Record(f.Required("record")), ReadInt(f.Required("state"), "state"), ReadInt(f.Required("status"), "status"), f.OptionalText("description"), span);
                }
                case "sendEmail":
                {
                    var f = new Fields(body, "a sendEmail step", "id", "description", "set");
                    var step = new SendEmailNode(Set(f.Required("set"), "set"), f.OptionalText("id"), f.OptionalText("description"), span);
                    return Declare(f, step, kind);
                }
                case "if":
                    return If(body, context, span);
                case "wait":
                {
                    var f = new Fields(body, "a wait step", "description", "until", "timeout", "then", "onTimeout");
                    if (!f.Has("until") && !f.Has("timeout"))
                        throw Error(f.Node, "A wait step needs 'until' or 'timeout' (or both).");
                    if (f.Has("onTimeout") && !f.Has("timeout"))
                        throw Error(f.KeyNode("onTimeout"), "'onTimeout' needs a 'timeout'.");
                    return new WaitNode(
                        f.Get("until") is { } until ? Cond(until) : null,
                        f.Get("timeout") is { } timeout ? Value(timeout) : null,
                        Steps(f.Required("then"), context),
                        f.Get("onTimeout") is { } onTimeout ? Steps(onTimeout, context) : null,
                        f.OptionalText("description"), span);
                }
                case "stop":
                {
                    var f = new Fields(body, "a stop step", "status", "message", "description");
                    var canceled = OneOf(f.Required("status"), "status", "succeeded", "canceled") == "canceled";
                    return new StopNode(canceled, f.Get("message") is { } message ? Value(message) : null, f.OptionalText("description"), span);
                }
                case "startChild":
                {
                    var f = new Fields(body, "a startChild step", "workflow", "record", "description");
                    return new StartChildNode(f.Text("workflow"), f.Get("record") is { } record ? Record(record) : RecordNode.PrimaryRecord, f.OptionalText("description"), span);
                }
                case "performAction":
                {
                    var f = new Fields(body, "a performAction step", "id", "description", "message", "target", "inputs");
                    var step = new PerformActionNode(f.Text("message"), f.Get("target") is { } target ? Record(target) : null,
                        f.Get("inputs") is { } inputs ? Set(inputs, "inputs") : [], f.OptionalText("id"), f.OptionalText("description"), span)
                    {
                        MessageSpan = Span(f.Get("message")!),
                    };
                    return Declare(f, step, kind);
                }
                case "customActivity":
                {
                    var f = new Fields(body, "a customActivity step", "id", "description", "type", "assembly", "inputs", "outputs");
                    var outputs = new List<(string, string)>();
                    var outputSpans = new List<SourceSpan>();
                    if (f.Get("outputs") is { } outputsNode)
                    {
                        var map2 = outputsNode as YamlMappingNode ?? throw Error(outputsNode, "'outputs' must be a mapping (property: name).");
                        foreach (var (property, name) in map2.Children)
                        {
                            outputs.Add((ReadText(property, "outputs"), ReadText(name, "outputs")));
                            outputSpans.Add(Span(name));
                        }
                    }
                    var step = new CustomActivityNode(f.Text("type"), f.Text("assembly"), f.Get("inputs") is { } inputs ? Set(inputs, "inputs") : [], outputs, f.OptionalText("id"), f.OptionalText("description"), span)
                    {
                        OutputSpans = outputSpans,
                    };
                    return Declare(f, step, kind);
                }
                case "setOutput":
                {
                    var f = new Fields(body, "a setOutput step", "argument", "value", "description");
                    return new SetOutputNode(f.Text("argument"), Value(f.Present("value")), f.OptionalText("description"), span);
                }
                case "setValue" or "setDefault":
                {
                    var f = new Fields(body, $"a {kind} step", "field", "value", "description");
                    return new SetValueNode(f.Text("field"), Value(f.Present("value")), kind == "setDefault", f.OptionalText("description"), span);
                }
                case "clearValue":
                {
                    var f = new Fields(body, "a clearValue step", "field", "description");
                    return new ClearValueNode(f.Text("field"), f.OptionalText("description"), span);
                }
                case "showError":
                {
                    var f = new Fields(body, "a showError step", "field", "message", "description");
                    return new ShowErrorNode(f.OptionalText("field"), f.Text("message"), f.OptionalText("description"), span);
                }
                case "setVisibility":
                {
                    var f = new Fields(body, "a setVisibility step", "field", "visible", "description");
                    return new ClientOnlyNode(kind, f.Text("field"), ReadBool(f.Required("visible"), "visible") ? "true" : "false", f.OptionalText("description"), span);
                }
                case "setLock":
                {
                    var f = new Fields(body, "a setLock step", "field", "locked", "description");
                    return new ClientOnlyNode(kind, f.Text("field"), ReadBool(f.Required("locked"), "locked") ? "true" : "false", f.OptionalText("description"), span);
                }
                default:
                {
                    var f = new Fields(body, "a setRequirement step", "field", "level", "description");
                    return new ClientOnlyNode("setRequirement", f.Text("field"), OneOf(f.Required("level"), "level", Requirements), f.OptionalText("description"), span);
                }
            }
        }

        private static void CheckKind(YamlNode keyNode, string kind, Context context)
        {
            string[] all = ["create", "update", "assign", "setState", "sendEmail", "if", "wait", "stop", "startChild", "performAction", "customActivity", "setOutput", .. BusinessRuleKinds];
            if (!all.Contains(kind, StringComparer.Ordinal))
                throw Error(keyNode, $"Unknown step '{kind}' (allowed: {string.Join(", ", all)}). A step the format lacks (a dialog, a recommendation, a template) has no equivalent: use a webhook or a custom workflow activity.");
            var isBusinessRuleKind = BusinessRuleKinds.Contains(kind, StringComparer.Ordinal);
            if (context == Context.BusinessRule)
            {
                if (kind != "if" && !isBusinessRuleKind)
                    throw Error(keyNode, $"The step '{kind}' cannot be used in a business rule (a business rule takes if, {string.Join(", ", BusinessRuleKinds)}).");
                return;
            }
            if (isBusinessRuleKind) throw Error(keyNode, $"The step '{kind}' is only valid in a business rule.");
            if (kind == "setOutput" && context != Context.Action) throw Error(keyNode, "The step 'setOutput' is only valid in an action.");
            if (kind == "wait" && context != Context.BackgroundWorkflow)
                throw Error(keyNode, "The step 'wait' is only valid in a background workflow (not in a real-time workflow, a business rule or an action).");
        }

        /// <summary>Registers the id of a step other steps can refer to (unique in the whole document).</summary>
        private StepNode Declare(Fields fields, StepNode step, string kind)
        {
            if (step.Id is { } id)
            {
                if (id.Length == 0) throw Error(fields.Get("id")!, "The step id must not be empty.");
                if (!_stepIds.TryAdd(id, kind)) throw Error(fields.Get("id")!, $"The step id '{id}' is used more than once.");
            }
            return step;
        }

        private StepNode If(YamlNode body, Context context, SourceSpan span)
        {
            var f = new Fields(body, "an if step", "description", "when", "then", "elseIf", "else");
            var branches = new List<BranchNode>
            {
                new(Cond(f.Required("when")), Steps(f.Required("then"), context), Span(f.Required("when"))),
            };
            if (f.Get("elseIf") is { } elseIfNode)
            {
                if (elseIfNode is not YamlSequenceNode list) throw Error(elseIfNode, "'elseIf' must be a list of { when, then }.");
                foreach (var item in list)
                {
                    var b = new Fields(item, "an elseIf branch", "when", "then");
                    branches.Add(new BranchNode(Cond(b.Required("when")), Steps(b.Required("then"), context), Span(item)));
                }
            }
            return new IfNode(branches, f.Get("else") is { } otherwise ? Steps(otherwise, context) : null, f.OptionalText("description"), span);
        }

        // ------------------------------------------------------------ records, values, conditions

        private RecordNode Record(YamlNode node)
        {
            if (node is YamlScalarNode scalar)
                return scalar.Value == "primary" && !IsNull(scalar)
                    ? RecordNode.PrimaryRecord
                    : throw Error(node, $"A record is 'primary', {{ related: <lookup column> }} or {{ created: <step id> }}, not '{scalar.Value}'.");
            var f = new Fields(node, "a record", "related", "created");
            if (f.Has("related") == f.Has("created"))
                throw Error(node, "A record takes exactly one of 'related' or 'created'.");
            if (f.Has("related")) return new RecordNode.Related(f.Text("related"));
            return CreatedRecord(f);
        }

        /// <summary>The record a <c>created: &lt;step id&gt;</c> names: an earlier create, sendEmail or performAction step.</summary>
        private RecordNode.Created CreatedRecord(Fields f)
        {
            var stepId = f.Text("created");
            CheckStepRef(f.Get("created")!, stepId, "created", "create, sendEmail or performAction", "create", "sendEmail", "performAction");
            return new RecordNode.Created(stepId);
        }

        /// <summary>A reference to a step id: the step must come earlier in the document and be one of <paramref name="kinds"/>.</summary>
        private void CheckStepRef(YamlNode at, string stepId, string key, string what, params string[] kinds)
        {
            if (!_stepIds.TryGetValue(stepId, out var kind))
                throw Error(at, $"'{key}: {stepId}' names no earlier step with that id (a step must come before the step that refers to it).");
            if (!kinds.Contains(kind, StringComparer.Ordinal))
                throw Error(at, $"'{key}: {stepId}' names a {kind} step; '{key}' needs a {what} step.");
        }

        private List<(string, ValueNode)> Set(YamlNode node, string key)
        {
            var map = node as YamlMappingNode ?? throw Error(node, $"'{key}' must be a mapping (name: value).");
            var entries = new List<(string, ValueNode)>();
            foreach (var (name, value) in map.Children)
                entries.Add((ReadText(name, key), Value(value)));
            return entries;
        }
        private ValueNode Value(YamlNode node) => node switch
        {
            YamlScalarNode scalar => new LiteralNode(IsNull(scalar) ? null : scalar.Value ?? "", Span(node)),
            YamlMappingNode map => ValueMapping(map),
            _ => throw Error(node, "A value is a scalar or a mapping ({ field: ... }), not a list."),
        };

        /// <summary>A dynamic value: one kind key plus its own keys, and <paramref name="extra"/> (the keys of a comparison).</summary>
        private ValueNode ValueMapping(YamlMappingNode map, params string[] extra)
        {
            var kinds = map.Children.Keys.OfType<YamlScalarNode>().Where(k => k.Value is { } t && ValueKinds.Contains(t, StringComparer.Ordinal)).ToList();
            if (kinds.Count == 0)
                throw Error(map, $"A value takes one of {string.Join(", ", ValueKinds)} (a scalar is a literal).");
            if (kinds.Count > 1)
                throw Error(kinds[1], $"A value takes exactly one kind; '{kinds[0].Value}' and '{kinds[1].Value}' are both given.");
            var kind = kinds[0].Value!;
            string[] own = kind switch
            {
                "field" => ["field", "related", "created"],
                "output" => ["output", "of"],
                "lookup" => ["lookup", "id", "name"],
                _ => [kind],
            };
            var f = new Fields(map, "a value", [.. own, .. extra]);
            var span = Span(map);
            switch (kind)
            {
                case "field":
                {
                    if (f.Has("related") && f.Has("created"))
                        throw Error(f.KeyNode("created"), "A field takes 'related' or 'created', not both.");
                    RecordNode record = RecordNode.PrimaryRecord;
                    if (f.Has("related")) record = new RecordNode.Related(f.Text("related"));
                    else if (f.Has("created")) record = CreatedRecord(f);
                    return new FieldNode(f.Text("field"), record, span);
                }
                case "record":
                    return new RecordValueNode(Record(f.Required("record")), span);
                case "arg":
                    return new ArgNode(f.Text("arg"), span);
                case "output":
                {
                    var stepId = f.Text("of");
                    CheckStepRef(f.Get("of")!, stepId, "of", "performAction or customActivity", "performAction", "customActivity");
                    return new OutputNode(f.Text("output"), stepId, span);
                }
                case "now":
                    return f.Get("now") is { } now && IsTrue(now) ? new NowNode(span) : throw Error(f.Get("now")!, "'now' must be true ({ now: true }).");
                case "lookup":
                {
                    var idNode = f.Required("id");
                    var idText = ReadText(idNode, "id");
                    return Guid.TryParse(idText, out var id)
                        ? new LookupNode(f.Text("lookup"), id, f.OptionalText("name"), span)
                        : throw Error(idNode, $"The id '{idText}' is not a GUID.");
                }
                default:
                {
                    var operandsNode = f.Get(kind)!;
                    if (operandsNode is not YamlSequenceNode list) throw Error(operandsNode, $"'{kind}' takes a list of values ([a, b]).");
                    var min = kind == "firstOf" ? 2 : 1;
                    if (list.Children.Count < min) throw Error(operandsNode, $"'{kind}' needs at least {min} value{(min == 1 ? "" : "s")}.");
                    return new OperatorNode(kind, list.Select(Value).ToList(), span);
                }
            }
        }

        private CondNode Cond(YamlNode node)
        {
            var map = node as YamlMappingNode ?? throw Error(node, "A condition must be a mapping (for example { field: a, op: eq, value: 1 }).");
            var span = Span(node);
            foreach (var logic in new[] { "all", "any", "not" })
            {
                if (!map.Children.TryGetValue(new YamlScalarNode(logic), out var operand)) continue;
                if (map.Children.Count > 1) throw Error(map, $"'{logic}' takes no other key in a condition.");
                if (logic == "not") return new NotNode(Cond(operand), span);
                if (operand is not YamlSequenceNode list || list.Children.Count == 0)
                    throw Error(operand, $"'{logic}' takes a list of conditions.");
                var items = list.Select(Cond).ToList();
                return logic == "all" ? new AllNode(items, span) : new AnyNode(items, span);
            }

            var f = new Fields(map, "a condition", [.. ValueKinds, "related", "created", "of", "id", "name", "op", "value", "all", "any", "not"]);
            var opNode = f.Present("op");
            var op = ReadText(opNode, "op", nullIsText: true);
            if (op == "between")
                throw Error(opNode, "'between' is not an operator: write both limits, for example all: [{ field: a, op: ge, value: 1 }, { field: a, op: le, value: 9 }].");
            if (!Operators.Contains(op, StringComparer.Ordinal))
                throw Error(opNode, $"The operator '{op}' is not supported (allowed: {string.Join(", ", Operators)}).");
            var left = ValueMapping(map, "op", "value");
            if (op is "null" or "notNull")
            {
                if (f.Has("value")) throw Error(f.KeyNode("value"), $"The operator '{op}' takes no value.");
                return new CompareNode(op, left, null, span);
            }
            return new CompareNode(op, left, Value(f.Present("value")), span);
        }
    }
}
