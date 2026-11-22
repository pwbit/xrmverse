using System.Globalization;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Schema;
using Xrmd.Workflows.Authoring;

namespace Xrmd.Workflows.Tests.Authoring;

/// <summary>
/// Seeded random workflow documents for the round-trip property (plan 2.21, Task 4, A12): every step kind of A5, every value kind of
/// A6 and every condition form of A7, over the metadata of <see cref="FakeAuthoringEnvironment"/> (step nesting at most 4). Each
/// document is valid (the compiler accepts it) and canonical (the form the exporter writes): an id on every document, a lookup with
/// its name, <c>all</c>/<c>any</c> of two or more conditions whose first is not of the same kind, a wait with only a timeout last in
/// its list, a custom activity with the id <c>activity{k}</c> (k its rank among the custom activities, in document order) only when
/// a later value reads its output, literals in their invariant text. A document the compiler refuses is a bug of this generator.
/// </summary>
internal sealed class DocumentGenerator
{
    public static readonly Guid ContactId = new("c0c0c0c0-0000-4000-8000-000000000001");
    public static readonly Guid TeamId = new("c0c0c0c0-0000-4000-8000-000000000002");
    public static readonly Guid TaskId = new("c0c0c0c0-0000-4000-8000-000000000003");
    public static readonly Guid OrderId = new("c0c0c0c0-0000-4000-8000-000000000004");

    private const string ActivityType = "Example.Activities.Normalize";
    private const string ActivityAssembly = "Example.Activities";

    /// <summary>Text special in XML, VB or YAML (RF3) and plain text, for names, descriptions, literals and messages.</summary>
    private static readonly string[] Texts =
    [
        "Call the customer", "Review", "He said \"hi\" & <left> ] [x]", "èé ñ 中文", "emoji \U0001F600", "line one\nline two",
        "a: b # c", "null", "~", "123", "true", "- dash", "'quoted'", " padded ", "[New Object() { 1 }]", "{ field: x }", "50%", "tab\tin",
    ];

    /// <summary>The environment the documents are generated for: the fake organization with the custom activity and the records of the lookups.</summary>
    public static FakeAuthoringEnvironment Environment()
    {
        var env = new FakeAuthoringEnvironment { WorkflowSdkAvailable = true };
        env.WorkflowActivities.Add((ActivityType, ActivityAssembly));
        env.Records[("contact", ContactId)] = "Grace Hopper";
        env.Records[("team", TeamId)] = "Sales & <Ops>";
        env.Records[("task", TaskId)] = "Follow up";
        env.Records[("xrmv_order", OrderId)] = "Order \"42\"";
        return env;
    }

    private readonly Random _r;
    private readonly FakeAuthoringEnvironment _env;
    private readonly WorkflowKind _kind;
    private readonly bool _background;
    private readonly string _entity;
    private readonly List<ArgumentNode> _arguments = [];

    /// <summary>The records created by an earlier step with an id (create, sendEmail), with their table.</summary>
    private readonly List<(string Id, string Entity)> _created = [];

    /// <summary>The outputs later values may read: (step id, output name, whether the step is a custom activity).</summary>
    private readonly List<(string StepId, string Name, bool Custom)> _outputs = [];

    /// <summary>The custom activity ids some value reads (the others lose their id: the XAML does not keep it).</summary>
    private readonly HashSet<string> _readCustom = new(StringComparer.Ordinal);

    /// <summary>
    /// The name each record is given in this document: the XAML keeps the name of a lookup literal but the parsed
    /// definition does not, so the exporter reads it back by record, and one record has one name in a canonical document.
    /// </summary>
    private readonly Dictionary<(string Entity, Guid Id), string> _lookupNames = [];

    private int _ids, _customActivities;

    private DocumentGenerator(int seed, FakeAuthoringEnvironment env)
    {
        _r = new Random(seed);
        _env = env;
        _kind = (seed % 7) switch
        {
            0 or 1 or 2 => WorkflowKind.Workflow,
            3 or 4 => WorkflowKind.Action,
            _ => WorkflowKind.BusinessRule,
        };
        _background = _kind == WorkflowKind.Workflow && seed % 7 != 0;
        _entity = _kind == WorkflowKind.Action
            ? Pick("xrmv_order", "account", "none")
            : Pick("xrmv_order", "account", "contact");
    }

    /// <summary>The document of <paramref name="seed"/> (the same seed gives the same document).</summary>
    public static WorkflowDocument Generate(int seed, FakeAuthoringEnvironment env) => new DocumentGenerator(seed, env).Document(seed);

    private WorkflowDocument Document(int seed)
    {
        var id = new Guid(Bytes(16));
        var name = Chance(0.2) ? $"Generated {seed} {Text()}".TrimEnd() : $"Generated {seed}";
        if (_kind == WorkflowKind.Action)
            Arguments();
        var steps = _kind == WorkflowKind.BusinessRule
            ? Enumerable.Range(0, _r.Next(1, 3)).Select(_ => (StepNode)If(1)).ToList()
            : StepList(1, 1, 5);
        steps = StripUnreadIds(steps);
        var document = new WorkflowDocument(_kind, id, name, Chance(0.3) ? Text() : null, _entity, steps);
        return _kind switch
        {
            WorkflowKind.Workflow => document with { Options = Options(), Trigger = Trigger() },
            WorkflowKind.Action => document with { Message = $"xrmv_Gen{seed}", Arguments = _arguments },
            _ => document,
        };
    }

    // ---- header -------------------------------------------------------------------------------------------------------------

    private WorkflowOptions Options() => new()
    {
        Mode = _background ? "background" : "realtime",
        Scope = Pick<string?>(null, "businessUnit", "parentChildBusinessUnits", "organization"),
        RunAs = !_background && Chance(0.3) ? "owner" : null,
        LogFailures = _background || !Chance(0.3),
        AutoDelete = _background && Chance(0.4),
        OnDemand = Chance(0.3),
        ChildProcess = Chance(0.3),
    };

    private TriggerNode? Trigger()
    {
        var create = Chance(0.5) ? new CreateTrigger(!_background && Chance(0.3) ? "pre" : null) : null;
        UpdateTrigger? update = null;
        if (Chance(0.4))
        {
            var columns = Shuffle(Table(_entity).Attributes.Select(a => a.LogicalName!).ToList()).Take(_r.Next(1, 4)).ToList();
            update = new UpdateTrigger(columns, !_background && Chance(0.3) ? "pre" : null);
        }
        var delete = Chance(0.2) ? new DeleteTrigger() : null;
        return create is null && update is null && delete is null ? null : new TriggerNode(create, update, delete);
    }

    private void Arguments()
    {
        var count = _r.Next(0, 5);
        for (var i = 1; i <= count; i++)
        {
            var type = Pick(WorkflowYaml.ArgumentTypes.ToArray());
            var input = Chance(0.6);
            var entity = type == "entityReference" && Chance(0.5) ? Pick("account", "contact", "xrmv_order", "systemuser") : null;
            _arguments.Add(new ArgumentNode($"P{i}{char.ToUpperInvariant(type[0])}{type[1..]}", input, type, entity, Chance(0.5),
                Chance(0.5) ? Text() : ""));
        }
    }

    // ---- steps --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The steps of a nested list: the records it creates and the outputs it produces are only in scope inside it (the
    /// compiler declares their variables in the list's sequence), so they are forgotten when it ends.
    /// </summary>
    private List<StepNode> Scoped(Func<List<StepNode>> body)
    {
        var (created, outputs) = (_created.Count, _outputs.Count);
        var steps = body();
        _created.RemoveRange(created, _created.Count - created);
        _outputs.RemoveRange(outputs, _outputs.Count - outputs);
        return steps;
    }

    private List<StepNode> StepList(int depth, int min, int max)
    {
        var steps = new List<StepNode>();
        var count = _r.Next(min, max + 1);
        for (var i = 0; i < count; i++)
        {
            var step = Step(depth);
            steps.Add(step);
            // A wait with only a timeout takes the steps after it as its own (A12 canonical form): it is last in its list.
            if (step is WaitNode { Until: null })
                break;
        }
        return steps;
    }

    private StepNode Step(int depth)
    {
        while (true)
        {
            StepNode? step = _r.Next(13) switch
            {
                0 => Create(),
                1 => Update(),
                2 => Assign(),
                3 => SetState(),
                4 => SendEmail(),
                5 => depth < 4 ? If(depth) : null,
                6 => depth < 4 && _background ? Wait(depth) : null,
                7 => Stop(),
                8 => StartChild(),
                9 => PerformAction(),
                10 => CustomActivity(),
                11 => _kind == WorkflowKind.Action ? SetOutput() : null,
                _ => Update(),
            };
            if (step is not null)
                return step;
        }
    }

    private string? Description() => Chance(0.3) ? Text() : null;

    private string NextId(string prefix) => $"{prefix}{++_ids}";

    private StepNode? Create()
    {
        var entity = Pick("task", "account", "contact", "xrmv_order");
        var set = Set(entity, create: true);
        var id = Chance(0.5) ? NextId("rec") : null;
        var step = new CreateNode(entity, set, id, Description());
        if (id is not null)
            _created.Add((id, entity));
        return step;
    }

    private StepNode? Update()
    {
        if (Record(_ => true) is not { } record)
            return null;
        return new UpdateNode(record.Node, Set(record.Entity, create: false), Description());
    }

    private StepNode? Assign()
    {
        if (Record(entity => Column(entity, "ownerid") is not null) is not { } record)
            return null;
        var owner = Column(record.Entity, "ownerid")!;
        return new AssignNode(record.Node, Value(new Dest(PropertyType.EntityReference, owner), 0), Description());
    }

    private StepNode? SetState()
    {
        if (Record(entity => Column(entity, "statecode") is not null) is not { } record)
            return null;
        var state = _r.Next(2);
        return new SetStateNode(record.Node, state, state + 1, Description());
    }

    private StepNode? SendEmail()
    {
        var id = Chance(0.3) ? NextId("mail") : null;
        var step = new SendEmailNode(Set("email", create: true), id, Description());
        if (id is not null)
            _created.Add((id, "email"));
        return step;
    }

    private IfNode If(int depth)
    {
        var branches = new List<BranchNode>();
        var count = Chance(0.7) ? 1 : _r.Next(2, 4);
        for (var i = 0; i < count; i++)
            branches.Add(new BranchNode(Condition(0), Scoped(() => Body(depth))));
        var otherwise = Chance(0.4) ? Scoped(() => Body(depth)) : null;
        return new IfNode(branches, otherwise, Description());
    }

    /// <summary>The steps of a branch: rule actions (and conditions) in a business rule, steps elsewhere.</summary>
    private List<StepNode> Body(int depth)
    {
        if (_kind != WorkflowKind.BusinessRule)
            return StepList(depth + 1, 0, 3);
        var steps = new List<StepNode>();
        var count = _r.Next(0, 4);
        for (var i = 0; i < count; i++)
            steps.Add(depth < 4 && Chance(0.15) ? If(depth + 1) : RuleAction());
        return steps;
    }

    private StepNode? Wait(int depth)
    {
        var until = Chance(0.75) ? Condition(0) : null;
        var timeout = until is null || Chance(0.4) ? Value(new Dest(PropertyType.DateTime, null), 0) : null;
        var then = Scoped(() => StepList(depth + 1, 0, 3));
        var onTimeout = until is not null && timeout is not null && Chance(0.5) ? Scoped(() => StepList(depth + 1, 1, 2)) : null;
        return new WaitNode(until, timeout, then, onTimeout, Description());
    }

    private StepNode Stop()
    {
        ValueNode? message = null;
        if (Chance(0.5))
            message = Chance(0.5) ? new LiteralNode(NonEmptyText()) : Value(new Dest(PropertyType.String, null), 0);
        // The empty text is what a stop without a message writes (A6): the canonical form leaves it out.
        if (message is LiteralNode { Text: "" })
            message = null;
        return new StopNode(Chance(0.5), message, Description());
    }

    private StepNode? StartChild()
    {
        if (Record(entity => entity == "xrmv_order") is not { } record)
            return null;
        var workflow = Chance(0.5) ? "Child" : FakeAuthoringEnvironment.TwinA.ToString("D");
        return new StartChildNode(workflow, record.Node, Description());
    }

    private StepNode PerformAction()
    {
        var message = Pick("xrmv_Score", "xrmv_Recalculate");
        var callee = _env.Messages[message];
        var target = Chance(0.5) ? Record(_ => true)?.Node : null;
        var inputs = Shuffle(callee.Where(c => c.IsInput).ToList()).Take(_r.Next(0, 4))
            .Select(c => (c.Name, Value(new Dest(PropertyTypes.OfClr(c.ClrType)!, null), 0))).ToList();
        var id = Chance(0.6) ? NextId("act") : null;
        var step = new PerformActionNode(message, target, inputs, id, Description());
        if (id is not null)
            foreach (var output in callee.Where(c => !c.IsInput))
                _outputs.Add((id, output.Name, false));
        return step;
    }

    private StepNode CustomActivity()
    {
        var callee = _env.ActivityArguments[ActivityType];
        var inputs = Shuffle(callee.Where(c => c.IsInput).ToList()).Take(_r.Next(0, 3))
            .Select(c => (c.Name, Value(new Dest(PropertyTypes.OfClr(c.ClrType)!, null), 0))).ToList();
        var id = $"activity{++_customActivities}";
        var outputs = new List<(string, string)>();
        if (Chance(0.7))
        {
            var name = $"out{++_ids}";
            outputs.Add(("Normalized", name));
            _outputs.Add((id, name, true));
        }
        return new CustomActivityNode(ActivityType, ActivityAssembly, inputs, outputs, id, Description());
    }

    private StepNode? SetOutput()
    {
        var outputs = _arguments.Where(a => !a.IsInput).ToList();
        if (outputs.Count == 0)
            return null;
        var argument = Pick(outputs.ToArray());
        return new SetOutputNode(argument.Name, Value(new Dest(PropertyTypes.OfArgument(argument.Type)!, null), 0), Description());
    }

    private StepNode RuleAction()
    {
        var table = Table(_entity);
        var updatable = table.Attributes.Where(a => a.IsValidForUpdate != false && PropertyTypes.Of(a) is not null).ToArray();
        var any = table.Attributes.ToArray();
        switch (_r.Next(7))
        {
            case 0:
            case 1:
            {
                var column = Pick(updatable);
                return new SetValueNode(column.LogicalName!, Value(new Dest(PropertyTypes.Of(column)!, column), 0), DefaultOnly: _r.Next(2) == 1,
                    Description());
            }
            case 2:
                return new ClearValueNode(Pick(updatable).LogicalName!, Description());
            case 3:
                return new ShowErrorNode(Chance(0.7) ? Pick(any).LogicalName : null, NonEmptyText(), Description());
            case 4:
                return new ClientOnlyNode("setVisibility", Pick(any).LogicalName!, Pick("true", "false"), Description());
            case 5:
                return new ClientOnlyNode("setLock", Pick(any).LogicalName!, Pick("true", "false"), Description());
            default:
                return new ClientOnlyNode("setRequirement", Pick(any).LogicalName!, Pick("none", "recommended", "required"), Description());
        }
    }

    /// <summary>The columns of a create, update or send email step: distinct, valid for that write, each with a value of its type.</summary>
    private List<(string Column, ValueNode Value)> Set(string entity, bool create)
    {
        var columns = Table(entity).Attributes
            .Where(a => a.AttributeType != AttributeTypeCode.Uniqueidentifier && PropertyTypes.Of(a) is not null
                        && (create ? a.IsValidForCreate : a.IsValidForUpdate) != false)
            .ToList();
        return Shuffle(columns).Take(_r.Next(1, 5))
            .Select(c => (c.LogicalName!, Value(new Dest(PropertyTypes.Of(c)!, c, AllowEmpty: true), 0)))
            .ToList();
    }

    // ---- records ------------------------------------------------------------------------------------------------------------

    private sealed record Rec(RecordNode Node, string Entity);

    /// <summary>The records a step or value may name: the primary one, those of a lookup of the primary table to one table, the created ones.</summary>
    private List<Rec> Records()
    {
        var records = new List<Rec>();
        if (_entity != "none")
        {
            records.Add(new Rec(RecordNode.PrimaryRecord, _entity));
            foreach (var lookup in Table(_entity).Attributes.OfType<LookupAttributeMetadata>()
                         .Where(l => l.AttributeType == AttributeTypeCode.Lookup && l.Targets is { Length: 1 }))
                records.Add(new Rec(new RecordNode.Related(lookup.LogicalName!), lookup.Targets[0]));
        }
        if (_kind != WorkflowKind.BusinessRule)
            records.AddRange(_created.Select(c => new Rec(new RecordNode.Created(c.Id), c.Entity)));
        return records;
    }

    private Rec? Record(Func<string, bool> accepts)
    {
        var records = Records().Where(r => accepts(r.Entity)).ToList();
        return records.Count == 0 ? null : records[_r.Next(records.Count)];
    }

    // ---- values (A6) --------------------------------------------------------------------------------------------------------

    /// <summary>Where a value goes: its type, its column (choice values, lookup targets) and whether the empty value is allowed.</summary>
    private sealed record Dest(PropertyType Type, AttributeMetadata? Column, bool AllowEmpty = false);

    private ValueNode Value(Dest to, int depth)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            ValueNode? value = _r.Next(11) switch
            {
                0 or 1 or 2 => Literal(to),
                3 or 4 => Field(to),
                5 => RecordValue(to),
                6 => Argument(to),
                7 => Output(to),
                // An operator into a value of unknown type (compared with an output) converts its operands to Object, which
                // the 2.14 runtime cannot do: the compiler refuses it, so the generator never writes it.
                8 => depth < 2 && to.Type != PropertyType.Object ? Operator(to, depth) : null,
                9 => to.Type == PropertyType.DateTime || to.Type == PropertyType.Object ? new NowNode() : null,
                _ => to.AllowEmpty ? new LiteralNode(null) : null,
            };
            if (value is not null)
                return value;
        }
        throw new InvalidOperationException($"The generator has no value of type {to.Type}.");
    }

    private ValueNode? Literal(Dest to)
    {
        var inv = CultureInfo.InvariantCulture;
        if (to.Type == PropertyType.String)
            return new LiteralNode(Chance(0.1) ? "" : Text());
        if (to.Type == PropertyType.OptionSet)
        {
            var options = to.Column is null ? [] : PropertyTypes.Options(to.Column);
            return new LiteralNode((options.Count > 0 ? options[_r.Next(options.Count)] : _r.Next(0, 1000)).ToString(inv));
        }
        if (to.Type == PropertyType.Boolean)
            return new LiteralNode(Pick("true", "false"));
        if (to.Type == PropertyType.Decimal || to.Type == PropertyType.Money)
            return new LiteralNode((_r.Next(-100_000, 100_000) / 100m).ToString(inv));
        if (to.Type == PropertyType.Integer)
            return new LiteralNode(_r.Next(-1000, 1000).ToString(inv));
        if (to.Type == PropertyType.Double)
            return new LiteralNode((_r.Next(-10_000, 10_000) / 8.0).ToString(inv));
        if (to.Type == PropertyType.DateTime)
            return new LiteralNode(new DateTime(2020 + _r.Next(10), _r.Next(1, 13), _r.Next(1, 29), _r.Next(24), _r.Next(60), _r.Next(60), DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv));
        if (to.Type == PropertyType.Guid)
            return new LiteralNode(new Guid(Bytes(16)).ToString("D"));
        if (to.Type == PropertyType.EntityReference)
            return Lookup(to.Column);
        return null;
    }

    private ValueNode? Lookup(AttributeMetadata? column)
    {
        var targets = column is LookupAttributeMetadata { Targets: { Length: > 0 } t } ? t : null;
        var records = _env.Records.Where(r => targets is null || targets.Contains(r.Key.Entity)).ToList();
        if (records.Count == 0)
            return null;
        var (key, name) = records[_r.Next(records.Count)];
        if (!_lookupNames.TryGetValue((key.Entity, key.Id), out var chosen))
            _lookupNames[(key.Entity, key.Id)] = chosen = Chance(0.3) ? NonEmptyText() : name;
        return new LookupNode(key.Entity, key.Id, chosen);
    }

    /// <summary>A column read of the destination's type (a lookup into a party list, A9); party lists are never read.</summary>
    private ValueNode? Field(Dest to)
    {
        var candidates = Records().SelectMany(r => Table(r.Entity).Attributes
                .Where(a => PropertyTypes.Of(a) is { } type && type != PropertyType.EntityCollection
                            && (type == to.Type || to.Type == PropertyType.Object
                                || (to.Type == PropertyType.EntityCollection && type == PropertyType.EntityReference)))
                .Select(a => new FieldNode(a.LogicalName!, r.Node)))
            .ToList();
        return candidates.Count == 0 ? null : candidates[_r.Next(candidates.Count)];
    }

    private ValueNode? RecordValue(Dest to)
    {
        if (to.Type != PropertyType.EntityReference)
            return null;
        var targets = to.Column is LookupAttributeMetadata { Targets: { Length: > 0 } t } ? t : null;
        return Record(entity => targets is null || targets.Contains(entity)) is { } record ? new RecordValueNode(record.Node) : null;
    }

    private ValueNode? Argument(Dest to)
    {
        var targets = to.Column is LookupAttributeMetadata { Targets: { Length: > 0 } t } ? t : null;
        var candidates = _arguments
            .Where(a => a.IsInput && (to.Type == PropertyType.Object || PropertyTypes.OfArgument(a.Type) == to.Type)
                        && (a.Entity is null || targets is null || targets.Contains(a.Entity)))
            .ToList();
        return candidates.Count == 0 ? null : new ArgNode(candidates[_r.Next(candidates.Count)].Name);
    }

    private ValueNode? Output(Dest to)
    {
        if (_outputs.Count == 0)
            return null;
        var (stepId, name, custom) = _outputs[_r.Next(_outputs.Count)];
        if (custom)
            _readCustom.Add(stepId);
        return new OutputNode(name, stepId);
    }

    private ValueNode? Operator(Dest to, int depth)
    {
        var operand = to with { AllowEmpty = false };
        if (to.Type.IsNumber && Chance(0.6))
        {
            var name = Pick("add", "subtract", "multiply", "increment", "decrement", "set");
            return new OperatorNode(name, Enumerable.Range(0, _r.Next(1, 4)).Select(_ => Value(operand, depth + 1)).ToList());
        }
        if (to.Type == PropertyType.String && Chance(0.6))
            return new OperatorNode("concat", Enumerable.Range(0, _r.Next(1, 4)).Select(_ => Part(depth + 1)).ToList());
        if (to.Type == PropertyType.EntityCollection)
            return null;
        return new OperatorNode("firstOf", Enumerable.Range(0, _r.Next(2, 4)).Select(_ => Value(operand, depth + 1)).ToList());
    }

    /// <summary>A part of <c>concat</c>: a value typed by itself (a literal is a text).</summary>
    private ValueNode Part(int depth)
    {
        while (true)
        {
            ValueNode? part = _r.Next(8) switch
            {
                0 or 1 => new LiteralNode(Text()),
                2 => Field(new Dest(PropertyType.Object, null)),
                3 => RecordValue(new Dest(PropertyType.EntityReference, null)),
                4 => new NowNode(),
                5 => Lookup(null),
                6 => Chance(0.5) ? Output(new Dest(PropertyType.Object, null)) : Argument(new Dest(PropertyType.Object, null)),
                _ => depth < 2 ? new OperatorNode("concat", [Part(depth + 1), Part(depth + 1)]) : null,
            };
            if (part is not null)
                return part;
        }
    }

    // ---- conditions (A7) ----------------------------------------------------------------------------------------------------

    private static readonly string[] Ops = ["eq", "ne", "null", "notNull", "contains", "notContains", "beginsWith", "endsWith", "gt", "ge", "lt", "le"];

    private CondNode Condition(int depth)
    {
        if (depth >= 2 || Chance(0.65))
            return Compare();
        switch (_r.Next(3))
        {
            case 0:
            case 1:
            {
                // Canonical: the first condition of all/any is not itself all/any of the same kind (the XAML nests them to the left).
                var all = _r.Next(2) == 0;
                var items = new List<CondNode>();
                for (var i = _r.Next(2, 4); i > 0; i--)
                {
                    var item = Condition(depth + 1);
                    if (items.Count == 0 && (all ? item is AllNode : item is AnyNode))
                        item = Compare();
                    items.Add(item);
                }
                return all ? new AllNode(items) : new AnyNode(items);
            }
            default:
                return new NotNode(Condition(depth + 1));
        }
    }

    private CondNode Compare()
    {
        while (true)
        {
            (ValueNode Left, Dest Type)? left = _r.Next(8) switch
            {
                0 or 1 or 2 => LeftField(),
                3 => Argument(new Dest(PropertyType.Object, null)) is ArgNode arg
                    ? (arg, new Dest(PropertyTypes.OfArgument(_arguments.Single(a => a.Name == arg.Name).Type)!, null))
                    : null,
                4 => (new NowNode(), new Dest(PropertyType.DateTime, null)),
                5 => RecordValue(new Dest(PropertyType.EntityReference, null)) is { } record ? (record, new Dest(PropertyType.EntityReference, null)) : null,
                6 => Lookup(null) is { } lookup ? (lookup, new Dest(PropertyType.EntityReference, null)) : null,
                _ => Output(new Dest(PropertyType.Object, null)) is { } output ? (output, new Dest(PropertyType.Object, null)) : null,
            };
            if (left is not { } l)
                continue;
            var op = Pick(Ops);
            return op is "null" or "notNull" ? new CompareNode(op, l.Left) : new CompareNode(op, l.Left, Value(l.Type, 1));
        }
    }

    private (ValueNode, Dest)? LeftField()
    {
        var candidates = Records().SelectMany(r => Table(r.Entity).Attributes
                .Where(a => PropertyTypes.Of(a) is { } type && type != PropertyType.EntityCollection)
                .Select(a => (Node: (ValueNode)new FieldNode(a.LogicalName!, r.Node), Column: a)))
            .ToList();
        if (candidates.Count == 0)
            return null;
        var (node, column) = candidates[_r.Next(candidates.Count)];
        return (node, new Dest(PropertyTypes.Of(column)!, column));
    }

    // ---- ids ----------------------------------------------------------------------------------------------------------------

    /// <summary>The steps without the ids of the custom activities no value reads (the XAML keeps no such id, A12).</summary>
    private List<StepNode> StripUnreadIds(IReadOnlyList<StepNode> steps) => steps.Select(step => step switch
    {
        CustomActivityNode custom when !_readCustom.Contains(custom.Id!) => custom with { Id = null },
        IfNode i => i with
        {
            Branches = i.Branches.Select(b => b with { Then = StripUnreadIds(b.Then) }).ToList(),
            Else = i.Else is null ? null : StripUnreadIds(i.Else),
        },
        WaitNode w => w with { Then = StripUnreadIds(w.Then), OnTimeout = w.OnTimeout is null ? null : StripUnreadIds(w.OnTimeout) },
        _ => step,
    }).ToList();

    // ---- randomness ---------------------------------------------------------------------------------------------------------

    private EntityMetadata Table(string name) => _env.GetEntity(name) ?? throw new InvalidOperationException($"No table {name}.");

    private AttributeMetadata? Column(string entity, string column) => Table(entity).Attributes.FirstOrDefault(a => a.LogicalName == column);

    private bool Chance(double p) => _r.NextDouble() < p;

    private T Pick<T>(params T[] items) => items[_r.Next(items.Length)];

    private string Text() => Pick(Texts);

    private string NonEmptyText() => Pick(Texts.Where(t => t.Trim().Length > 0).ToArray());

    private byte[] Bytes(int count)
    {
        var bytes = new byte[count];
        _r.NextBytes(bytes);
        return bytes;
    }

    private List<T> Shuffle<T>(List<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = _r.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }
}
