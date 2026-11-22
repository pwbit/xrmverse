using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xrmd.Schema;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Authoring;

/// <summary>What <see cref="WorkflowDecompiler"/> made of a definition: its YAML document, or every reason it has none (A12).</summary>
public abstract record DecompileResult
{
    /// <summary>The definition as a canonical YAML document, checked by a recompile.</summary>
    public sealed record Representable(WorkflowDocument Document) : DecompileResult;

    /// <summary>The definition has no YAML form: every reason, each naming the step or column it comes from.</summary>
    public sealed record NotRepresentable(IReadOnlyList<string> Reasons) : DecompileResult;
}

/// <summary>
/// Exports a process definition as a workflow document (plan 2.21, A12), the inverse of <see cref="WorkflowCompiler"/>: the row's columns
/// become the header (A3, A4), the definition the 2.14 parser reads from the row's XAML becomes the steps (A5–A8). The document is
/// canonical: keys in the format's order, defaults left out, <c>set</c> in XAML order, descriptions from the display names (A8), a child
/// workflow by name when the name is unique among definitions, else by id. Anything with no YAML form is refused with every reason:
/// another category, an unsupported step, a value or condition A6/A7 cannot write (a <c>DirectCast</c> outside an output, a concatenation
/// as a condition's operand, ...). A document is then <b>verified</b>: compiled with the same environment, its definition must be
/// equivalent to the original (<see cref="DefinitionEquivalence"/>) and its row must have the same columns the YAML holds; a mismatch is
/// reported as not representable with the differences, never fixed in the document.
/// </summary>
public static partial class WorkflowDecompiler
{
    /// <summary>Exports <paramref name="row"/> (a definition, <c>type</c> 1) as YAML, parsing its XAML and verifying the result by a recompile.</summary>
    public static async Task<DecompileResult> DecompileAsync(WorkflowRow row, IAuthoringEnvironment env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(env);
        var label = $"The process '{row.Name}' ({row.Id:D})";
        if (row.Category is not (0 or 2 or 3))
            return Refuse($"{label} is of category {row.Category} ({CategoryName(row.Category)}): only a workflow (0), a business rule (2) and an action (3) have a YAML form.");
        if (row.Type != 1)
            return Refuse($"{label} is of type {row.Type}: only a definition (type 1) is exported, not an activation or a template.");
        if (string.IsNullOrEmpty(row.Xaml))
            return Refuse($"{label} has no XAML.");

        WorkflowDefinition definition;
        Dictionary<(string, Guid), string?> lookupNames;
        try
        {
            definition = XamlWorkflowParser.Parse(row.Xaml, row.Category, env.WorkflowSdkAvailable);
            lookupNames = LookupNames(row.Xaml);
        }
        catch (XamlWorkflowException ex)
        {
            return Refuse(ex.Message);
        }
        if (definition.Unsupported.Count > 0)
            return new DecompileResult.NotRepresentable(definition.Unsupported
                .Select(u => $"The step {u.Label} is not supported ({u.Element}): {u.Reason}.").ToList());

        var decompiler = new Decompiler(row, definition, env, lookupNames);
        var document = await decompiler.DocumentAsync(ct);
        if (decompiler.Reasons.Count > 0 || document is null)
            return new DecompileResult.NotRepresentable(decompiler.Reasons);

        // A12: the document must compile back to an equivalent definition and the same row.
        CompileResult recompiled;
        try
        {
            recompiled = await WorkflowCompiler.CompileAsync(document, env, null, ct);
        }
        catch (WorkflowCompileException ex)
        {
            return new DecompileResult.NotRepresentable(ex.Errors.Select(e => $"The YAML of the process does not compile back: {e.Message}").ToList());
        }
        var differences = DefinitionEquivalence.Differences(definition, recompiled.Definition)
            .Select(d => $"The YAML of the process compiles to another definition: {d}")
            .Concat(RowDifferences(row, recompiled.Row))
            .ToList();
        return differences.Count > 0 ? new DecompileResult.NotRepresentable(differences) : new DecompileResult.Representable(document);
    }

    private static DecompileResult Refuse(string reason) => new DecompileResult.NotRepresentable([reason]);

    private static string CategoryName(int category) => category switch
    {
        1 => "a dialog",
        4 => "a business process flow",
        5 => "a modern flow",
        6 => "a desktop flow",
        7 => "an AI flow",
        _ => "not a classic process",
    };

    /// <summary>The columns the YAML holds (A3, A4) that differ between the row and the row its YAML compiles to, and a <c>rank</c> other than 0.</summary>
    private static IEnumerable<string> RowDifferences(WorkflowRow e, WorkflowRow a)
    {
        static string? List(string? columns) => string.IsNullOrWhiteSpace(columns) ? null : columns;
        var columns = new (string Column, object? Expected, object? Actual)[]
        {
            ("category", e.Category, a.Category), ("mode", e.Mode, a.Mode), ("scope", e.Scope, a.Scope), ("runas", e.RunAs, a.RunAs),
            ("primaryentity", e.PrimaryEntity, a.PrimaryEntity), ("name", e.Name, a.Name), ("description", e.Description, a.Description),
            ("uniquename", e.UniqueName, a.UniqueName), ("triggeroncreate", e.TriggerOnCreate, a.TriggerOnCreate),
            ("triggerondelete", e.TriggerOnDelete, a.TriggerOnDelete),
            ("triggeronupdateattributelist", List(e.TriggerOnUpdateAttributes), List(a.TriggerOnUpdateAttributes)),
            ("createstage", e.CreateStage, a.CreateStage), ("updatestage", e.UpdateStage, a.UpdateStage), ("deletestage", e.DeleteStage, a.DeleteStage),
            ("ondemand", e.OnDemand, a.OnDemand), ("subprocess", e.Subprocess, a.Subprocess), ("asyncautodelete", e.AsyncAutoDelete, a.AsyncAutoDelete),
            ("syncworkflowlogonfailure", e.SyncWorkflowLogOnFailure, a.SyncWorkflowLogOnFailure), ("istransacted", e.IsTransacted, a.IsTransacted),
            ("processtriggerscope", e.ProcessTriggerScope, a.ProcessTriggerScope),
        };
        foreach (var (column, expected, actual) in columns)
            if (!Equals(expected, actual))
                yield return $"The column {column} is {Show(expected)}, but its YAML gives {Show(actual)}.";
        // Final review I2: no YAML key holds the run order of a process; an apply writes rank 0.
        if (e.Rank != 0) yield return $"The rank {e.Rank.ToString(CultureInfo.InvariantCulture)} (run order) has no YAML form.";
    }

    private static string Show(object? value) => value switch
    {
        null => "empty",
        bool b => b ? "1" : "0",
        string s => $"'{s}'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    // ---- lookup names -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The name each lookup literal of the XAML gives its record (A6 <c>name</c>): the 2.14 definition keeps only the table and the id, the
    /// name is read from the designer's <c>CreateCrmType</c> of <c>EntityReference</c> (export OnUpdate). A record named differently by two
    /// literals has no single name (null: not representable when used).
    /// </summary>
    private static Dictionary<(string, Guid), string?> LookupNames(string xaml)
    {
        var root = XDocument.Parse(xaml).Root!;
        var guids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var references = new List<(string Entity, string Name, string IdVariable)>();
        foreach (var arguments in root.Descendants().Where(e => e.Name.LocalName == "ActivityReference.Arguments"))
        {
            var items = arguments.Elements().ToList();
            string? Text(string key) => items.FirstOrDefault(a => (string?)a.Attribute(XName.Get("Key", ActivityCatalog.XamlNs)) == key) is { HasElements: false } a
                ? a.Value
                : null;
            if (Text("ExpressionOperator") != "CreateCrmType" || Text("Parameters") is not { } parameters || Text("Result") is not { } result)
                continue;
            VbExpr? parsed, variable;
            try
            {
                parsed = ExpressionParser.Bracketed(parameters) is { } p ? ExpressionParser.Parse(p) : null;
                variable = ExpressionParser.Bracketed(result) is { } r ? ExpressionParser.Parse(r) : null;
            }
            catch (FormatException)
            {
                continue;
            }
            if (parsed is not VbExpr.NewArray { Elements: [VbExpr.Name { Value: var type }, ..] } array)
                continue;
            var rest = array.Elements.Skip(1).ToList();
            if (type.EndsWith(".Guid", StringComparison.Ordinal) && rest is [VbExpr.Str id, ..] && variable is VbExpr.Name { Value: var name }
                && Guid.TryParse(id.Value, out var guid))
                guids[name] = guid;
            else if (type.EndsWith(".EntityReference", StringComparison.Ordinal) && rest is [VbExpr.Str entity, VbExpr.Str display, VbExpr.Name idVariable, ..])
                references.Add((entity.Value, display.Value, idVariable.Value));
        }
        var names = new Dictionary<(string, Guid), string?>();
        foreach (var (entity, name, idVariable) in references)
        {
            if (!guids.TryGetValue(idVariable, out var id))
                continue;
            names[(entity, id)] = names.TryGetValue((entity, id), out var known) && known != name ? null : name;
        }
        return names;
    }

    // ---- the document -----------------------------------------------------------------------------------------------------------

    /// <summary>The key a create or send email step gets without an id, and the result variable of a perform action without one (A8).</summary>
    [GeneratedRegex(@"^(?<kind>Create|SendEmail|Action)Step(?<n>[0-9]+)_(localParameter|result)$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedVariable();

    private sealed class Decompiler(WorkflowRow row, WorkflowDefinition definition, IAuthoringEnvironment env, Dictionary<(string, Guid), string?> lookupNames)
    {
        public List<string> Reasons { get; } = [];

        /// <summary>The ids of the document, by the variable the XAML declares for them (created records, perform action results).</summary>
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

        /// <summary>The outputs of custom activities: their variable to (step id, output name).</summary>
        private readonly Dictionary<string, (string StepId, string Name)> _customOutputs = new(StringComparer.Ordinal);

        /// <summary>The variables a value or record of the definition reads (created records, results, outputs).</summary>
        private readonly HashSet<string> _read = new(StringComparer.Ordinal);

        private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);

        private int _customActivities;

        private CancellationToken _ct;

        private bool IsRule => row.Category == 2;

        private void Refuse(WorkflowStep at, string what) => Reasons.Add($"The step {at.Label}: {what}.");

        public async Task<WorkflowDocument?> DocumentAsync(CancellationToken ct)
        {
            _ct = ct;
            if (row.Name is not { Length: > 0 } name)
            {
                Reasons.Add("The process has no name, which the YAML needs.");
                return null;
            }
            if (name.Length > 100)
                Reasons.Add($"The name of the process is {name.Length} characters; the YAML takes at most 100.");
            if (row.PrimaryEntity is not { Length: > 0 } entity)
            {
                Reasons.Add("The process has no primary table, which the YAML needs.");
                return null;
            }
            var kind = row.Category switch { 2 => WorkflowKind.BusinessRule, 3 => WorkflowKind.Action, _ => WorkflowKind.Workflow };
            if (kind != WorkflowKind.Action && entity == "none")
                Reasons.Add("Only an action can have the primary table 'none'.");

            foreach (var step in definition.Root.DepthFirst())
                foreach (var variable in VariablesRead(step))
                    _read.Add(variable);
            var steps = await StepListAsync(Steps(definition.Root));
            var document = new WorkflowDocument(kind, row.Id, name, row.Description, entity, steps);
            return kind switch
            {
                WorkflowKind.Workflow => document with { Options = Options(), Trigger = Trigger() },
                WorkflowKind.Action => document with { Message = await MessageAsync(), Arguments = Arguments() },
                _ => document,
            };
        }

        // ---- header (A3, A4, A15) ----------------------------------------------------------------------------------------------

        private WorkflowOptions Options()
        {
            var realtime = row.Mode == 1;
            return new WorkflowOptions
            {
                Mode = realtime ? "realtime" : "background",
                Scope = row.Scope switch { 2 => "businessUnit", 3 => "parentChildBusinessUnits", 4 => "organization", _ => null },
                RunAs = realtime && row.RunAs == 0 ? "owner" : null,
                LogFailures = !realtime || row.SyncWorkflowLogOnFailure,
                AutoDelete = !realtime && row.AsyncAutoDelete,
                OnDemand = row.OnDemand,
                ChildProcess = row.Subprocess,
            };
        }

        private TriggerNode? Trigger()
        {
            var realtime = row.Mode == 1;
            var create = row.TriggerOnCreate ? new CreateTrigger(realtime && row.CreateStage == 20 ? "pre" : null) : null;
            var columns = (row.TriggerOnUpdateAttributes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var update = columns.Length > 0 ? new UpdateTrigger(columns, realtime && row.UpdateStage == 20 ? "pre" : null) : null;
            var delete = row.TriggerOnDelete ? new DeleteTrigger() : null;
            return create is null && update is null && delete is null ? null : new TriggerNode(create, update, delete);
        }

        private async Task<string?> MessageAsync()
        {
            if (row.UniqueName is not { Length: > 0 } unique)
            {
                Reasons.Add("The action has no unique name, so it has no message.");
                return null;
            }
            return $"{await env.PublisherPrefixAsync(row.Id, null, _ct)}_{unique}";
        }

        private List<ArgumentNode> Arguments()
        {
            var arguments = new List<ArgumentNode>();
            foreach (var argument in definition.Arguments.Where(a => !a.IsTarget))
            {
                if (ArgumentType(argument.ClrType) is not { } type)
                {
                    Reasons.Add($"The argument '{argument.Name}' is of type {argument.ClrType}, which an action argument of the YAML cannot be.");
                    continue;
                }
                arguments.Add(new ArgumentNode(argument.Name, argument.IsInput, type, argument.EntityTarget, argument.Required, argument.Description ?? ""));
            }
            return arguments;
        }

        /// <summary>The YAML <c>type</c> of an argument's CLR type (A15).</summary>
        private static string? ArgumentType(string clrType) =>
            WorkflowYaml.ArgumentTypes.FirstOrDefault(t => PropertyTypes.OfArgument(t)?.Clr == clrType);

        // ---- steps (A5) ------------------------------------------------------------------------------------------------------------

        private static IReadOnlyList<WorkflowStep> Steps(WorkflowStep block) => block is SequenceStep sequence ? sequence.Steps : [block];

        private static string? Description(WorkflowStep step) => DefinitionEquivalence.DescriptionOf(step.DisplayName);

        private async Task<List<StepNode>> StepListAsync(IReadOnlyList<WorkflowStep> steps)
        {
            var nodes = new List<StepNode>();
            for (var i = 0; i < steps.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                var step = steps[i];
                switch (step)
                {
                    // A wait until a condition (export OnUpdate): the parser's sequence of the wait and the steps of its branch.
                    case SequenceStep { Steps: [WaitStep { Until: { } until } wait, ..] } sequence when sequence.Steps.Count <= 2:
                    {
                        var rest = sequence.Steps.Skip(1).ToList();
                        var then = rest.Count == 0 ? [] : await StepListAsync(Steps(rest[0]));
                        var onTimeout = wait.OnTimeout is null ? null : await StepListAsync(Steps(wait.OnTimeout));
                        nodes.Add(new WaitNode(Condition(wait, until), wait.Timeout is null ? null : Value(wait, wait.Timeout, "the timeout"), then,
                            onTimeout, Description(sequence)));
                        break;
                    }
                    // A wait with only a timeout: the steps after it are those that follow the wait (A12 canonical form).
                    case WaitStep { Until: null, Timeout: { } timeout } wait:
                    {
                        if (wait.OnTimeout is not null)
                            Refuse(wait, "a wait with only a timeout has steps on timeout, which the YAML cannot hold");
                        var then = await StepListAsync(steps.Skip(i + 1).ToList());
                        nodes.Add(new WaitNode(null, Value(wait, timeout, "the timeout"), then, null, Description(wait)));
                        return nodes;
                    }
                    case SequenceStep:
                        Refuse(step, "it groups several steps in one designer step, which the YAML cannot hold");
                        break;
                    default:
                        if (await StepAsync(step) is { } node)
                            nodes.Add(node);
                        break;
                }
            }
            return nodes;
        }

        private async Task<StepNode?> StepAsync(WorkflowStep step)
        {
            var description = Description(step);
            switch (step)
            {
                case CreateStep create:
                    return new CreateNode(create.Entity, Set(create, create.Values), Declare(create, create.Variable, "created"), description);
                case UpdateStep update:
                    return new UpdateNode(Record(update, update.Record), Set(update, update.Values), description);
                case AssignStep assign:
                    return new AssignNode(Record(assign, assign.Record), Value(assign, assign.Owner, "the owner"), description);
                case SetStateStep setState:
                    return new SetStateNode(Record(setState, setState.Record), setState.State, setState.Status, description);
                case SendEmailStep email:
                    return new SendEmailNode(Set(email, email.Values),
                        email.CreatedEmailVariable is { } variable ? Declare(email, variable, "email") : null, description);
                case ConditionStep condition:
                    return await IfAsync(condition, description);
                case StopStep stop:
                    // A stop message is cast to a text (export Child, Recalculate): the cast is the step's own.
                    var message = stop.Message is ValueExpr.Cast { ClrType: "System.String", Operand: var text } ? text : stop.Message;
                    return new StopNode(stop.Canceled, message is null ? null : Value(stop, message, "the message"), description);
                case ChildWorkflowStep child:
                    return new StartChildNode(await ChildAsync(child), Record(child, child.Record), description);
                case ActionStep action:
                {
                    var target = action.Target is null ? null : Record(action, action.Target);
                    var inputs = action.Inputs.Select(i => (i.Key, Value(action, i.Value, $"the input '{i.Key}'"))).ToList();
                    return new PerformActionNode(action.Message, target, inputs, Declare(action, action.Variable, "action"), description);
                }
                case CustomActivityStep custom:
                    return CustomActivity(custom, description);
                case AssignOutputStep output:
                {
                    // Export Recalculate: the value cast to the argument's type is the step's own cast.
                    var value = output.Value is ValueExpr.Cast cast ? cast.Operand : output.Value;
                    return new SetOutputNode(output.Argument, Value(output, value, $"the value of '{output.Argument}'"), description);
                }
                case SetValueStep { DefaultOnly: true } value:
                    return new SetValueNode(value.Attribute, Value(value, value.Value, $"the value of '{value.Attribute}'"), DefaultOnly: true, description);
                case SetValueStep { Value: ValueExpr.Literal { Type: "Null" } } clear:
                    return new ClearValueNode(clear.Attribute, description);
                case SetValueStep value:
                    return new SetValueNode(value.Attribute, Value(value, value.Value, $"the value of '{value.Attribute}'"), DefaultOnly: false, description);
                case ShowErrorStep { Message: ValueExpr.Literal { Type: "String", Value: string errorText } } error:
                    return new ShowErrorNode(error.Attribute, errorText, description);
                case ShowErrorStep:
                    Refuse(step, "the error message is not a text, which the YAML's showError holds");
                    return null;
                case ClientOnlyStep client:
                    return ClientOnly(client, description);
                default:
                    Refuse(step, $"a {step.GetType().Name} has no YAML form");
                    return null;
            }
        }

        private async Task<StepNode?> IfAsync(ConditionStep condition, string? description)
        {
            // A business rule's condition holds its description on its branches (export Newbusinessrule, Task 3).
            if (IsRule)
                description = condition.Branches.Count > 0 ? condition.Branches[0].Description : null;
            var branches = new List<BranchNode>();
            foreach (var branch in condition.Branches)
                branches.Add(new BranchNode(Condition(condition, branch.When), await StepListAsync(Steps(branch.Then))));
            var otherwise = condition.Otherwise is null ? null : await StepListAsync(Steps(condition.Otherwise));
            if (branches.Count == 0)
            {
                Refuse(condition, "a condition without a branch has no YAML form");
                return null;
            }
            return new IfNode(branches, otherwise, description);
        }

        /// <summary>A child workflow by its name when no other definition has it, else by its id (A12).</summary>
        private async Task<string> ChildAsync(ChildWorkflowStep child)
        {
            var id = child.WorkflowId.ToString("D");
            var found = await env.FindDefinitionsAsync(id, _ct);
            if (found.FirstOrDefault(d => d.Id == child.WorkflowId) is not { Name: { Length: > 0 } name })
                return id;
            var named = await env.FindDefinitionsAsync(name, _ct);
            return named.Count == 1 && named[0].Id == child.WorkflowId && !Guid.TryParse(name, out _) ? name : id;
        }

        private StepNode CustomActivity(CustomActivityStep custom, string? description)
        {
            _customActivities++;
            var inputs = custom.Inputs.Select(i => (i.Key, Value(custom, i.Value, $"the input '{i.Key}'"))).ToList();
            // The variable of an output is "{step name}_{name}" (A8, review I1 of Task 2); the YAML id is not in the XAML: a step whose
            // outputs are read gets "activity{k}", k its rank among the custom activities.
            var prefix = DisplayNamePrefix(custom.DisplayName);
            var read = custom.Outputs.Values.Any(_read.Contains);
            var id = read ? NewId(custom, $"activity{_customActivities}") : null;
            var outputs = new List<(string, string)>();
            foreach (var (property, variable) in custom.Outputs)
            {
                var name = prefix is not null && variable.StartsWith(prefix + "_", StringComparison.Ordinal) ? variable[(prefix.Length + 1)..] : variable;
                outputs.Add((property, name));
                if (id is not null)
                    _customOutputs[variable] = (id, name);
            }
            return new CustomActivityNode(custom.TypeName, custom.AssemblyName, inputs, outputs, id, description);
        }

        private static string? DisplayNamePrefix(string? displayName) =>
            displayName is null ? null : Regex.Match(displayName, "^[A-Za-z]+Step[0-9]+") is { Success: true } m ? m.Value : null;

        private StepNode? ClientOnly(ClientOnlyStep client, string? description)
        {
            var (kind, settings) = client.Kind switch
            {
                "SetVisibility" => ("setVisibility", new Dictionary<string, string> { ["True"] = "true", ["False"] = "false" }),
                "SetDisplayMode" => ("setLock", new Dictionary<string, string> { ["True"] = "true", ["False"] = "false" }),
                "SetFieldRequiredLevel" => ("setRequirement", new Dictionary<string, string> { ["0"] = "none", ["2"] = "required", ["3"] = "recommended" }),
                _ => ("", []),
            };
            if (kind.Length == 0)
            {
                Refuse(client, $"the form action '{client.Kind}' has no YAML form");
                return null;
            }
            if (client.Attribute is not { Length: > 0 } field)
            {
                Refuse(client, "the form action names no column (ControlId)");
                return null;
            }
            var setting = client.Setting?.Trim();
            if (setting is null || settings.FirstOrDefault(s => string.Equals(s.Key, setting, StringComparison.OrdinalIgnoreCase)).Value is not { } yaml)
            {
                Refuse(client, $"the form action's setting '{client.Setting}' is none of {string.Join(", ", settings.Keys)}");
                return null;
            }
            return new ClientOnlyNode(kind, field, yaml, description);
        }

        private List<(string, ValueNode)> Set(WorkflowStep step, IReadOnlyDictionary<string, ValueExpr> values) =>
            values.Select(v => (v.Key, Value(step, v.Value, $"the column '{v.Key}'"))).ToList();

        // ---- ids and records -----------------------------------------------------------------------------------------------------

        /// <summary>
        /// The YAML id of the record or result a step declares under <paramref name="variable"/>: the variable itself when the file gave it
        /// (the compiler uses the id, Task 2), none for the designer's own name (<c>CreateStep1_localParameter</c>) unless a later step reads
        /// it, then <c>{prefix}{n}</c>.
        /// </summary>
        private string? Declare(WorkflowStep step, string variable, string prefix)
        {
            string? id;
            if (GeneratedVariable().Match(variable) is { Success: true } generated)
                id = _read.Contains(variable) ? NewId(step, prefix + generated.Groups["n"].Value) : null;
            else
                id = NewId(step, variable);
            if (id is not null)
                _ids[variable] = id;
            return id;
        }

        private string NewId(WorkflowStep step, string id)
        {
            if (!_usedIds.Add(id))
                Refuse(step, $"its id '{id}' is the id of an earlier step");
            return id;
        }

        private RecordNode Record(WorkflowStep step, RecordRef record)
        {
            switch (record)
            {
                case RecordRef.Primary:
                    return RecordNode.PrimaryRecord;
                case RecordRef.Related related:
                    return new RecordNode.Related(related.LookupAttribute);
                case RecordRef.Created created when _ids.TryGetValue(created.Variable, out var id):
                    return new RecordNode.Created(id);
                case RecordRef.Created created:
                    Refuse(step, $"the record '{created.Variable}' is created by no earlier create or send email step");
                    return RecordNode.PrimaryRecord;
                default:
                    Refuse(step, $"the record {record} has no YAML form");
                    return RecordNode.PrimaryRecord;
            }
        }

        /// <summary>The variables a step reads: created records, perform action results, custom activity outputs.</summary>
        private static IEnumerable<string> VariablesRead(WorkflowStep step)
        {
            IEnumerable<RecordRef> records = step switch
            {
                UpdateStep s => [s.Record],
                AssignStep s => [s.Record],
                SetStateStep s => [s.Record],
                ChildWorkflowStep s => [s.Record],
                ActionStep { Target: { } target } => [target],
                _ => [],
            };
            IEnumerable<ValueExpr> values = step switch
            {
                CreateStep s => s.Values.Values,
                UpdateStep s => s.Values.Values,
                SendEmailStep s => s.Values.Values,
                AssignStep s => [s.Owner],
                WaitStep { Timeout: { } timeout } => [timeout],
                StopStep { Message: { } message } => [message],
                ActionStep s => s.Inputs.Values,
                CustomActivityStep s => s.Inputs.Values,
                AssignOutputStep s => [s.Value],
                SetValueStep s => [s.Value],
                ShowErrorStep s => [s.Message],
                _ => [],
            };
            IEnumerable<ConditionExpr> conditions = step switch
            {
                ConditionStep s => s.Branches.Select(b => b.When),
                WaitStep { Until: { } until } => [until],
                _ => [],
            };
            foreach (var record in records)
                if (record is RecordRef.Created created)
                    yield return created.Variable;
            foreach (var value in values.Concat(conditions.SelectMany(ValuesOf)))
                foreach (var variable in VariablesOf(value))
                    yield return variable;
        }

        private static IEnumerable<ValueExpr> ValuesOf(ConditionExpr condition) => condition switch
        {
            ConditionExpr.Compare c => c.Right is null ? [c.Left] : [c.Left, c.Right],
            ConditionExpr.And a => ValuesOf(a.Left).Concat(ValuesOf(a.Right)),
            ConditionExpr.Or o => ValuesOf(o.Left).Concat(ValuesOf(o.Right)),
            ConditionExpr.Not n => ValuesOf(n.Operand),
            _ => [],
        };

        private static IEnumerable<string> VariablesOf(ValueExpr value) => value switch
        {
            ValueExpr.AttributeOf { Record: RecordRef.Created created } => [created.Variable],
            ValueExpr.Conversion c => VariablesOf(c.Operand),
            ValueExpr.Arithmetic a => a.Operands.SelectMany(VariablesOf),
            ValueExpr.Concat c => c.Parts.SelectMany(VariablesOf),
            ValueExpr.FirstNonNull f => f.Operands.SelectMany(VariablesOf),
            ValueExpr.VariableRef v => [v.Name],
            ValueExpr.ActionOutput o => [o.Variable],
            ValueExpr.Cast c => VariablesOf(c.Operand),
            _ => [],
        };

        // ---- values (A6) -----------------------------------------------------------------------------------------------------------

        private ValueNode Value(WorkflowStep step, ValueExpr value, string what, bool conditionOperand = false)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (value)
            {
                case ValueExpr.Literal { Value: null }:
                    return new LiteralNode(null);
                case ValueExpr.Literal { Type: "EntityReference", Value: EntityRefValue reference }:
                {
                    if (!lookupNames.TryGetValue((reference.LogicalName, reference.Id), out var name))
                        name = null;
                    else if (name is null)
                        Refuse(step, $"{what}: the lookup literals of the {reference.LogicalName} record {reference.Id:D} give it different names");
                    return new LookupNode(reference.LogicalName, reference.Id, name);
                }
                case ValueExpr.Literal literal:
                {
                    string? text = literal.Value switch
                    {
                        string s => s,
                        bool b => b ? "true" : "false",
                        int i => i.ToString(inv),
                        decimal d => d.ToString(inv),
                        double d => d.ToString("R", inv),
                        DateTime t => t.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", inv),
                        Guid g => g.ToString("D"),
                        _ => null,
                    };
                    if (text is null)
                    {
                        Refuse(step, $"{what} is a literal of type {literal.Type} the YAML cannot write");
                        return new LiteralNode(null);
                    }
                    return new LiteralNode(text);
                }
                case ValueExpr.AttributeOf attribute:
                    return new FieldNode(attribute.Attribute, Record(step, attribute.Record));
                // A reference to a record: its key read as a reference (export OnCreate, OnUpdate).
                case ValueExpr.Conversion { From: "System.Guid", To: "Microsoft.Xrm.Sdk.EntityReference", Operand: ValueExpr.AttributeOf key, Entity: { } entity }
                    when env.GetEntity(entity)?.PrimaryIdAttribute == key.Attribute:
                    return new RecordValueNode(Record(step, key.Record));
                // A lookup set into a party list (export OnUpdate).
                case ValueExpr.Conversion { From: "Microsoft.Xrm.Sdk.EntityReference", To: "Microsoft.Xrm.Sdk.EntityCollection", Operand: ValueExpr.AttributeOf read }:
                    return new FieldNode(read.Attribute, Record(step, read.Record));
                case ValueExpr.Conversion conversion:
                    Refuse(step, $"{what} converts {conversion.From} to {conversion.To}, which no YAML value does");
                    return new LiteralNode(null);
                case ValueExpr.Arithmetic arithmetic:
                    return new OperatorNode(char.ToLowerInvariant(arithmetic.Operator[0]) + arithmetic.Operator[1..],
                        arithmetic.Operands.Select(o => Value(step, o, what)).ToList());
                case ValueExpr.Concat when conditionOperand:
                    Refuse(step, $"{what} is a text concatenation, which the YAML has no condition for (A12)");
                    return new LiteralNode(null);
                case ValueExpr.Concat concat:
                    return new OperatorNode("concat", concat.Parts.Select(p => Value(step, p, what)).ToList());
                case ValueExpr.FirstNonNull first:
                    return new OperatorNode("firstOf", first.Operands.Select(o => Value(step, o, what)).ToList());
                case ValueExpr.ExecutionTime:
                    return new NowNode();
                case ValueExpr.VariableRef variable when definition.Arguments.Any(a => a.IsInput && !a.IsTarget && a.Name == variable.Name):
                    return new ArgNode(variable.Name);
                case ValueExpr.VariableRef variable when _customOutputs.TryGetValue(variable.Name, out var output):
                    return new OutputNode(output.Name, output.StepId);
                case ValueExpr.ActionOutput output when _ids.TryGetValue(output.Variable, out var stepId):
                    return new OutputNode(output.Output, stepId);
                case ValueExpr.Cast cast:
                    Refuse(step, $"{what} is a DirectCast to {cast.ClrType}, which the YAML writes only around the value of an output argument (A12)");
                    return new LiteralNode(null);
                default:
                    Refuse(step, $"{what} is {DefinitionEquivalenceShow(value)}, which has no YAML form");
                    return new LiteralNode(null);
            }
        }

        private static string DefinitionEquivalenceShow(ValueExpr value) => value switch
        {
            ValueExpr.VariableRef v => $"the variable '{v.Name}' that no earlier step declares",
            ValueExpr.ActionOutput o => $"the output '{o.Output}' of '{o.Variable}' that no earlier step declares",
            _ => value.GetType().Name,
        };

        // ---- conditions (A7) -----------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
        {
            ["Equal"] = "eq", ["NotEqual"] = "ne", ["Null"] = "null", ["NotNull"] = "notNull", ["Contains"] = "contains",
            ["DoesNotContain"] = "notContains", ["BeginsWith"] = "beginsWith", ["EndsWith"] = "endsWith", ["GreaterThan"] = "gt",
            ["GreaterEqual"] = "ge", ["LessThan"] = "lt", ["LessEqual"] = "le",
        };

        private CondNode Condition(WorkflowStep step, ConditionExpr condition)
        {
            switch (condition)
            {
                case ConditionExpr.Compare compare:
                {
                    if (!Operators.TryGetValue(compare.Operator, out var op))
                    {
                        Refuse(step, $"the condition operator '{compare.Operator}' has no YAML form");
                        op = "eq";
                    }
                    var left = Value(step, compare.Left, "the left operand of a condition", conditionOperand: true);
                    // A scalar cannot be a left operand (a comparison is a value mapping, A7); a lookup is a mapping and can.
                    if (compare.Left is ValueExpr.Literal && left is not LookupNode)
                        Refuse(step, "a condition compares a literal, which the YAML cannot write as a left operand");
                    return new CompareNode(op, left, compare.Right is null ? null : Value(step, compare.Right, "the value of a condition"));
                }
                // all/any are nested to the left by the compiler: ((a And b) And c) is all: [a, b, c] (canonical form).
                case ConditionExpr.And and:
                    return new AllNode([.. Flatten<ConditionExpr.And>(and, a => (a.Left, a.Right)).Select(c => Condition(step, c))]);
                case ConditionExpr.Or or:
                    return new AnyNode([.. Flatten<ConditionExpr.Or>(or, o => (o.Left, o.Right)).Select(c => Condition(step, c))]);
                case ConditionExpr.Not not:
                    return new NotNode(Condition(step, not.Operand));
                default:
                    Refuse(step, $"the condition {condition} has no YAML form");
                    return new CompareNode("null", new NowNode());
            }
        }

        private static List<ConditionExpr> Flatten<T>(T node, Func<T, (ConditionExpr Left, ConditionExpr Right)> parts) where T : ConditionExpr
        {
            var (left, right) = parts(node);
            var items = left is T inner ? Flatten(inner, parts) : [left];
            items.Add(right);
            return items;
        }
    }
}
