using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Xrmd.Workflows.Xaml;

/// <summary>The XAML is not well-formed XML, or its root is not an <c>Activity</c>. The message names line and position.</summary>
public sealed class XamlWorkflowException(string message) : Exception(message);

/// <summary>
/// Reads the XAML of a classic process into a <see cref="WorkflowDefinition"/> with <c>System.Xml.Linq</c>; the XAML is
/// never executed. Each element and expression shape of <see cref="ActivityCatalog"/> maps to a step or value; anything
/// else becomes an <see cref="UnsupportedStep"/> (W16). Namespaces are matched by URI (normalized, without the assembly
/// version), never by prefix.
/// </summary>
public static partial class XamlWorkflowParser
{
    /// <summary>The deepest element nesting accepted; deeper XAML is refused before it is walked.</summary>
    public const int MaxElementDepth = 256;

    /// <summary>
    /// The reason of the unsupported step a custom workflow activity becomes when the Microsoft workflow SDK is not installed
    /// (plan 2.14, W17).
    /// </summary>
    public const string WorkflowSdkMissingReason = "custom workflow activities need the Microsoft workflow SDK: run xrmd sdk install";

    /// <summary>
    /// Parses <paramref name="xaml"/> with the workflow SDK the host found at startup (<see cref="Custom.WorkflowSdk.IsAvailable"/>):
    /// the definition is parsed again at each activation and host start, so installing the SDK later is seen then.
    /// </summary>
    public static WorkflowDefinition Parse(string xaml, int category) => Parse(xaml, category, Custom.WorkflowSdk.IsAvailable);

    /// <param name="xaml">The XAML of the process.</param>
    /// <param name="category">The process category.</param>
    /// <param name="workflowSdkAvailable">False makes each custom workflow activity step an <see cref="UnsupportedStep"/> with
    /// <see cref="WorkflowSdkMissingReason"/>.</param>
    public static WorkflowDefinition Parse(string xaml, int category, bool workflowSdkAvailable) =>
        new Session(Load(xaml), category, workflowSdkAvailable).Run();

    /// <summary>
    /// The <see cref="WorkflowDefinition.Category"/> of a calculated column definition (<see cref="ParseCalculated"/>): not a process
    /// category.
    /// </summary>
    public const int CalculatedCategory = -1;

    /// <summary>
    /// The category of a rollup column definition (<see cref="ParseRollup"/>, plan 2.12b): not a process category. Only
    /// <see cref="ParseRollup"/> and <see cref="CatalogEntriesUsed"/> accept it.
    /// </summary>
    public const int RollupCategory = -2;

    /// <summary>
    /// Parses the <c>FormulaDefinition</c> of the calculated column <paramref name="attribute"/> of <paramref name="entity"/> (plan
    /// 2.12a): the same reader, with the business rule's set-value shapes and the calculated column operators (catalog kinds
    /// <c>Function</c> and <c>CalculatedArithmetic</c>). The tree may hold only sequences, conditions and set-value steps on
    /// <paramref name="attribute"/>; anything else is refused.
    /// </summary>
    /// <param name="columnType">The CLR type of the column (<c>CalculatedColumns.TargetTypeOf</c>): the only <c>TargetType</c> a step may
    /// declare other than its value's own type (2.12a parity export). Null accepts none.</param>
    /// <exception cref="XamlWorkflowException">The XAML is not well-formed, or is not a calculated column definition of the column.</exception>
    public static WorkflowDefinition ParseCalculated(string xaml, string entity, string attribute, string? columnType = null)
    {
        var definition = new Session(Load(xaml), CalculatedCategory, workflowSdkAvailable: false, columnType).Run();
        if (definition.Unsupported is [var unsupported, ..])
            throw new XamlWorkflowException($"The calculated column definition has a step that cannot be read ({unsupported.Element} at {unsupported.Label}): {unsupported.Reason}.");
        if (definition.PrimaryEntity is { } primary && primary != entity)
            throw new XamlWorkflowException($"The calculated column definition is written for the table '{primary}', not '{entity}'.");
        var sets = 0;
        foreach (var step in definition.Root.DepthFirst())
            switch (step)
            {
                case SequenceStep or ConditionStep:
                    break;
                case SetValueStep { DefaultOnly: false } set:
                    if (set.Attribute != attribute)
                        throw new XamlWorkflowException($"The calculated column definition sets the column '{set.Attribute}', not '{attribute}'.");
                    sets++;
                    break;
                default:
                    throw new XamlWorkflowException($"The step {step.Label} ({step.GetType().Name.Replace("Step", "")}) cannot appear in a calculated column definition.");
            }
        if (sets == 0)
            throw new XamlWorkflowException($"The calculated column definition never sets the column '{attribute}'.");
        return definition;
    }

    /// <summary>
    /// The <see cref="ActivityCatalog"/> entries (<see cref="CatalogEntry.Element"/>) a parse of <paramref name="xaml"/> relies on:
    /// each element and referenced type it classifies, each expression and condition operator, and each shape it recognizes
    /// (<c>Shape:*</c>, <c>Vb:*</c>). Plan 2.14, Task 9: every entry the parity export uses must have the source <c>export</c>.
    /// </summary>
    /// <exception cref="XamlWorkflowException">As <see cref="Parse(string, int, bool)"/>.</exception>
    /// <param name="calculatedColumnType">With <see cref="CalculatedCategory"/>: the column's type, as for <see cref="ParseCalculated"/>.</param>
    public static IReadOnlySet<string> CatalogEntriesUsed(string xaml, int category, bool workflowSdkAvailable, string? calculatedColumnType = null)
    {
        var session = new Session(Load(xaml), category, workflowSdkAvailable, calculatedColumnType);
        if (category == RollupCategory)
            session.RunRollup();
        else
            session.Run();
        return session.Used;
    }

    /// <summary>
    /// The root <c>Activity</c> of <paramref name="xaml"/>, read with no DTD and no external resolution; refused (with line and
    /// position) when empty, not well-formed, nested deeper than <see cref="MaxElementDepth"/>, or rooted elsewhere.
    /// </summary>
    private static XElement Load(string xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml))
            throw new XamlWorkflowException("The workflow XAML is empty.");

        XDocument doc;
        try
        {
            // No DTD (entity expansion) and no external resolution.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            // A text kept verbatim from a file may start with its byte order mark (plan 2.12a, ruling K13): not part of the XML.
            using var reader = XmlReader.Create(new StringReader(xaml.StartsWith('\uFEFF') ? xaml[1..] : xaml), settings);
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            var where = ex.LineNumber > 0 ? $" (line {ex.LineNumber}, position {ex.LinePosition})" : "";
            throw new XamlWorkflowException($"The workflow XAML is not well-formed{where}: {ex.Message}");
        }

        var root = doc.Root!;
        CheckDepth(root);
        if (root.Name != XName.Get("Activity", ActivityCatalog.ActivitiesNs))
        {
            var info = (IXmlLineInfo)root;
            throw new XamlWorkflowException(
                $"The workflow XAML root must be an Activity element (line {info.LineNumber}, position {info.LinePosition}), not '{root.Name.LocalName}'.");
        }
        return root;
    }

    /// <summary>Refuses XAML nested deeper than <see cref="MaxElementDepth"/>, without recursion.</summary>
    private static void CheckDepth(XElement root)
    {
        var stack = new Stack<(XElement Element, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (depth > MaxElementDepth)
            {
                var info = (IXmlLineInfo)element;
                throw new XamlWorkflowException(
                    $"The workflow XAML nests elements deeper than {MaxElementDepth} levels (line {info.LineNumber}, position {info.LinePosition}).");
            }
            foreach (var child in element.Elements())
                stack.Push((child, depth + 1));
        }
    }

    /// <summary>Expression text shown in a reason, cut to <paramref name="max"/> characters.</summary>
    internal static string Clip(string text, int max = 200) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>A shape the parser does not know; caught where it becomes an <see cref="UnsupportedStep"/>.</summary>
    private sealed class ShapeException(string element, string reason) : Exception(Clip(reason, 400))
    {
        public string Element { get; } = element;
    }

    /// <summary>
    /// An entity being filled field by field before a create, update or send email step. The values keep the order the XAML sets
    /// them in (plan 2.21, Task 3: the exporter writes them back in that order).
    /// </summary>
    private sealed class TempEntity(string entity)
    {
        public string Entity { get; } = entity;
        public OrderedDictionary<string, ValueExpr> Values { get; } = new(StringComparer.Ordinal);
        public RecordRef? Record { get; set; }
    }

    private sealed partial class Session(XElement root, int category, bool workflowSdkAvailable, string? calculatedColumnType = null)
    {
        private static readonly XName XKey = XName.Get("Key", ActivityCatalog.XamlNs);
        private static readonly XName XCode = XName.Get("Code", ActivityCatalog.XamlNs);
        private static readonly XName XMembers = XName.Get("Members", ActivityCatalog.XamlNs);
        private static readonly XName XProperty = XName.Get("Property", ActivityCatalog.XamlNs);
        private static readonly XName XNull = XName.Get("Null", ActivityCatalog.XamlNs);
        private static readonly string[] EntityDictionaries = ["InputEntities", "CreatedEntities", "UpdatedEntities"];

        /// <summary>Designer variables bound to the value or condition they hold.</summary>
        private readonly Dictionary<string, object> _symbols = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TempEntity> _temps = new(StringComparer.Ordinal);
        /// <summary>Names read at run time: process arguments and outputs of action and custom activity steps.</summary>
        private readonly HashSet<string> _runtimeNames = new(StringComparer.Ordinal);

        /// <summary>The result variables of the "perform action" steps parsed so far, whose outputs later steps read by name.</summary>
        private readonly HashSet<string> _actionResults = new(StringComparer.Ordinal);
        private readonly List<WorkflowArgument> _arguments = [];
        private readonly List<UnsupportedStep> _unsupportedMembers = [];
        private string? _primaryEntity;

        /// <summary>The catalog entries this parse relied on (<see cref="CatalogEntriesUsed"/>).</summary>
        public HashSet<string> Used { get; } = new(StringComparer.Ordinal);

        /// <summary>Records a catalog entry the parse relied on; the key must name an entry.</summary>
        private void Use(string key)
        {
            if (ActivityCatalog.FindShape(key) is null)
                throw new InvalidOperationException($"'{key}' is not an entry of the activity catalog.");
            Used.Add(key);
        }

        /// <summary>Records the entry of an element (and the ActivityReference wrapper of a referenced type).</summary>
        private void Use(XElement e)
        {
            var aqn = (string?)e.Attribute("AssemblyQualifiedName");
            if (ActivityCatalog.Find(e.Name, aqn) is { } entry)
                Used.Add(entry.Element);
            if (aqn is not null && ActivityCatalog.Find(e.Name, null) is { } wrapper)
                Used.Add(wrapper.Element);
        }

        public WorkflowDefinition Run()
        {
            ReadMembers();
            // The designer's variables (Sequence.Variables and the Variables collections of step wrappers).
            if (root.Descendants(XName.Get("Variable", ActivityCatalog.ActivitiesNs)).Any())
                Use(XName.Get("Variable", ActivityCatalog.ActivitiesNs).ToString());
            var body = root.Elements()
                .Where(e => e.Name != XMembers && !e.Name.LocalName.Contains('.'))
                .ToList();

            WorkflowStep rootStep;
            if (body.Count == 1 && Kind(body[0]) is "Root" or "Sequence")
                rootStep = Parse(body[0], "0") ?? new SequenceStep("0", null, []);
            else
                rootStep = new SequenceStep("0", null, ParseActivities(body, "0", strict: false));

            // The bound record of an action must be of the process's primary entity (M6, review of Task 3b).
            foreach (var target in _arguments.Where(a => a.IsTarget && a.EntityTarget is not null && _primaryEntity is not null
                         && !string.Equals(a.EntityTarget, _primaryEntity, StringComparison.OrdinalIgnoreCase)).ToList())
                RejectMember(target, $"the target member '{target.Name}' names the entity '{target.EntityTarget}', not the primary entity '{_primaryEntity}'");

            return new WorkflowDefinition(category, _primaryEntity, _arguments, rootStep) { UnsupportedMembers = _unsupportedMembers };
        }

        // ---- arguments ---------------------------------------------------------------------------------------------

        private void ReadMembers()
        {
            foreach (var property in root.Elements(XMembers).Elements(XProperty))
            {
                var name = (string?)property.Attribute("Name");
                var type = (string?)property.Attribute("Type");
                if (name is null || type is null || name is "InputEntities" or "CreatedEntities")
                    continue;

                var open = type.IndexOf('(');
                var direction = open < 0 ? type : type[..open].Trim();
                var inner = open < 0 || !type.EndsWith(')') ? type : type[(open + 1)..^1].Trim();
                var isInput = direction switch
                {
                    "InArgument" => true,
                    "OutArgument" => false,
                    _ => (bool?)null,
                };
                if (isInput is null)
                {
                    _unsupportedMembers.Add(new UnsupportedStep($"x:Members/{name}", null, "x:Property",
                        $"the member '{name}' of type '{Clip(type)}' is not an input or output argument"));
                    continue;
                }

                var attributes = property.Elements(XName.Get("Property.Attributes", ActivityCatalog.XamlNs)).Elements().ToList();
                string? AttributeValue(string local) => attributes
                    .FirstOrDefault(a => a.Name.LocalName == local && ActivityCatalog.NormalizeNamespace(a.Name.NamespaceName) == ActivityCatalog.SdkWorkflowNs)
                    ?.Attribute("Value")?.Value;

                // The attributes of the parity export (mxsw:Argument{Required,Target,Description,Direction,Entity}Attribute); any
                // other one, or a direction that contradicts the type, makes the member unsupported.
                if (attributes.FirstOrDefault(a => !ArgumentAttributes.Contains(a.Name.LocalName)
                        || ActivityCatalog.NormalizeNamespace(a.Name.NamespaceName) != ActivityCatalog.SdkWorkflowNs) is { } unknown)
                {
                    _unsupportedMembers.Add(new UnsupportedStep($"x:Members/{name}", null, "x:Property",
                        $"the attribute '{unknown.Name.LocalName}' of the member '{name}' is not supported"));
                    continue;
                }
                // Each attribute once, and the two flags True or False (M6): a duplicate or another value is not guessed at.
                if (attributes.GroupBy(a => a.Name.LocalName).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                {
                    _unsupportedMembers.Add(new UnsupportedStep($"x:Members/{name}", null, "x:Property",
                        $"the attribute '{duplicate.Key}' of the member '{name}' is given twice"));
                    continue;
                }
                if (new[] { "ArgumentRequiredAttribute", "ArgumentTargetAttribute" }
                        .Select(flag => (Flag: flag, Value: AttributeValue(flag)))
                        .FirstOrDefault(f => f.Value is not null && !IsFlag(f.Value)) is { Flag: not null } odd)
                {
                    _unsupportedMembers.Add(new UnsupportedStep($"x:Members/{name}", null, "x:Property",
                        $"the {odd.Flag} of the member '{name}' has the value '{Clip(odd.Value!)}', not True or False"));
                    continue;
                }
                if (AttributeValue("ArgumentDirectionAttribute") is { } declared
                    && !string.Equals(declared, isInput.Value ? "Input" : "Output", StringComparison.OrdinalIgnoreCase))
                {
                    _unsupportedMembers.Add(new UnsupportedStep($"x:Members/{name}", null, "x:Property",
                        $"the member '{name}' of type '{Clip(type)}' declares the direction '{Clip(declared)}'"));
                    continue;
                }

                var required = string.Equals(AttributeValue("ArgumentRequiredAttribute"), "True", StringComparison.OrdinalIgnoreCase);
                var entity = AttributeValue("ArgumentEntityAttribute");
                var clrType = ResolveClrType(property, inner);
                var isTarget = string.Equals(AttributeValue("ArgumentTargetAttribute"), "True", StringComparison.OrdinalIgnoreCase);
                // The bound record of an action (parity export: Target) is one InArgument of EntityReference (M6).
                if (isTarget && (!isInput.Value || clrType != EntityReferenceType))
                {
                    _unsupportedMembers.Add(new UnsupportedStep($"x:Members/{name}", null, "x:Property",
                        $"the member '{name}' is marked as the target but is {(isInput.Value ? "an input" : "an output")} of {Clip(clrType)}: " +
                        $"only an input of {EntityReferenceType} can be the target"));
                    continue;
                }
                if (attributes.Count > 0)
                    Use("Shape:ArgumentAttributes");
                _arguments.Add(new WorkflowArgument(name, clrType, isInput.Value, required, string.IsNullOrEmpty(entity) ? null : entity)
                {
                    IsTarget = isTarget,
                    Description = AttributeValue("ArgumentDescriptionAttribute"),
                });
                _runtimeNames.Add(name);
            }

            var targets = _arguments.Where(a => a.IsTarget).ToList();
            if (targets.Count > 1)
                foreach (var target in targets)
                    RejectMember(target, $"several members are marked as the target ({string.Join(", ", targets.Select(t => t.Name))})");
        }

        private static bool IsFlag(string value) =>
            value.Equals("True", StringComparison.OrdinalIgnoreCase) || value.Equals("False", StringComparison.OrdinalIgnoreCase);

        /// <summary>Takes a declared argument back: it becomes an unsupported member, and steps that read it fail.</summary>
        private void RejectMember(WorkflowArgument argument, string reason)
        {
            _arguments.Remove(argument);
            _runtimeNames.Remove(argument.Name);
            _unsupportedMembers.Add(new UnsupportedStep($"x:Members/{argument.Name}", null, "x:Property", reason));
        }

        private static readonly HashSet<string> ArgumentAttributes = new(StringComparer.Ordinal)
        {
            "ArgumentRequiredAttribute", "ArgumentTargetAttribute", "ArgumentDescriptionAttribute", "ArgumentDirectionAttribute",
            "ArgumentEntityAttribute",
        };

        private static string ResolveClrType(XElement scope, string xamlType)
        {
            var colon = xamlType.IndexOf(':');
            if (colon < 0)
                return xamlType;
            var ns = scope.GetNamespaceOfPrefix(xamlType[..colon])?.NamespaceName;
            var local = xamlType[(colon + 1)..];
            if (ns == ActivityCatalog.XamlNs)
                return "System." + local;
            var clr = ns is null ? null : ActivityCatalog.ParseClrNamespace(ns);
            return clr is null ? xamlType : $"{clr.Value.ClrNamespace}.{local}";
        }

        // ---- activities ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Parses activities in order. Not strict: an activity that fails becomes an <see cref="UnsupportedStep"/> and the
        /// rest go on. Strict (inside one designer step): the failure propagates, so the whole step is one unsupported step.
        /// </summary>
        private List<WorkflowStep> ParseActivities(IEnumerable<XElement> activities, string parentPath, bool strict)
        {
            var steps = new List<WorkflowStep>();
            var index = 0;
            foreach (var activity in activities)
            {
                var path = $"{parentPath}/{index++}";
                if (strict)
                {
                    if (Parse(activity, path) is { } step)
                        steps.Add(step);
                    continue;
                }

                try
                {
                    if (Parse(activity, path) is { } step)
                        steps.Add(step);
                }
                catch (ShapeException ex)
                {
                    steps.Add(new UnsupportedStep(path, DisplayName(activity), ex.Element, ex.Message));
                }
            }
            return steps;
        }

        private static string? DisplayName(XElement activity) => (string?)activity.Attribute("DisplayName");

        private static string ElementLabel(XElement e) =>
            (string?)e.Attribute("AssemblyQualifiedName") is { } aqn ? ActivityCatalog.TypeNameOf(aqn) : e.Name.LocalName;

        private string Kind(XElement e)
        {
            if (e.Name == XCode)
                return "Code";
            Use(e);
            var (status, kind) = ActivityCatalog.Lookup(e.Name, (string?)e.Attribute("AssemblyQualifiedName"));
            return status == CatalogStatus.Unsupported ? "Unsupported" : kind;
        }

        private WorkflowStep? Parse(XElement e, string path)
        {
            var display = DisplayName(e);
            if (e.Name == XCode)
                throw new ShapeException("x:Code", "code blocks (x:Code) are not supported");

            var aqn = (string?)e.Attribute("AssemblyQualifiedName");
            var entry = ActivityCatalog.Find(e.Name, aqn);
            Use(e);
            var (status, kind) = ActivityCatalog.Lookup(e.Name, aqn);
            if (status == CatalogStatus.Unsupported)
                throw new ShapeException(ElementLabel(e), entry?.Reason ?? $"the activity '{ElementLabel(e)}' is not supported");
            if (status == CatalogStatus.ClientOnly)
                return category == 2
                    ? new ClientOnlyStep(path, display, kind) { Attribute = OptionalArgument(e, "ControlId"), Setting = OptionalArgument(e, ClientOnlySetting(kind)) }
                    : throw new ShapeException(ElementLabel(e), $"'{kind}' is a business rule action");

            switch (kind)
            {
                case "Sequence" when path != "0":
                    Use("Shape:DesignerSequence");
                    // A designer step (parity export: CreateStep1, UpdateStep1, StopWorkflowStep1, ...): one step, or one
                    // unsupported step when any of its activities is not supported, like StepComposite.
                    return Collapse(ParseActivities(ActivitiesOf(e), path, strict: true), path, display);
                case "Root":
                case "Sequence":
                case "Composite":
                    return new SequenceStep(path, display, ParseActivities(ActivitiesOf(e), path, strict: false));
                case "StepComposite":
                    return Collapse(ParseActivities(ActivitiesOf(e), path, strict: true), path, display);
                case "Reference":
                    throw new ShapeException("ActivityReference", "an ActivityReference without AssemblyQualifiedName is not supported");
                case "Persist":
                case "Variable":
                case "TypeLiteral":
                    return null;
                case "Retrieve":
                    ParseRetrieve(e);
                    return null;
                case "EntityBookkeeping":
                    return ParseAssign(e, path, display);
                case "GetValue":
                    ParseGetValue(e);
                    return null;
                case "SetValue":
                    return ParseSetValue(e, path, display);
                case "Create":
                    return ParseCreate(e, path, display);
                case "Update":
                    return ParseUpdate(e, path, display);
                case "Assign":
                    return new AssignStep(path, display, RecordOf(e), Value(e, "Owner", required: true));
                case "SetState":
                    return new SetStateStep(path, display, RecordOf(e), OptionValue(e, "State"), OptionValue(e, "Status"));
                case "SendEmail":
                    return ParseSendEmail(e, path, display);
                case "ChildWorkflow":
                    return ParseChildWorkflow(e, path, display);
                case "Wait":
                    return new WaitStep(path, display, null, Value(e, "PostponeUntil", required: true), null);
                case "Stop":
                    return ParseStop(e, path, display);
                case "EvaluateExpression":
                    ParseEvaluateExpression(e);
                    return null;
                case "ConvertTypes":
                    Bind(e, OutVariable(e, "Result"), ResolveText(e, Argument(e, "Input") ?? Fail<string>(e, "ConvertCrmXrmTypes needs an Input argument")));
                    return null;
                case "EvaluateCondition":
                    ParseEvaluateCondition(e);
                    return null;
                case "EvaluateLogicalCondition":
                    ParseEvaluateLogicalCondition(e);
                    return null;
                case "Condition":
                    return ParseConditionSequence(e, path, display);
                case "ConditionBranch":
                    throw new ShapeException(ElementLabel(e), "a condition branch outside a condition sequence is not supported");
                case "ActionCall":
                    return ParseActionCall(e, path, display);
                case "SetDefaultValue":
                    RequireBusinessRule(e);
                    return new SetValueStep(path, display, RequiredArgument(e, "Attribute"), ResolveText(e, Argument(e, "Value")), DefaultOnly: true);
                case "SetAttributeValue":
                    RequireBusinessRule(e);
                    return ParseSetAttributeValue(e, path, display);
                case "ClientSetMessage":
                    RequireBusinessRule(e);
                    return ParseClientSetMessage(e, path, display);
                case "ShowError":
                    RequireBusinessRule(e);
                    return new ShowErrorStep(path, display, ResolveText(e, Argument(e, "Message") ?? Fail<string>(e, "SetMessage needs a Message argument")));
                case "CustomActivity":
                    if (!workflowSdkAvailable)
                        throw new ShapeException(ElementLabel(e), WorkflowSdkMissingReason);
                    return ParseCustomActivity(e, path, display);
                default:
                    throw new ShapeException(ElementLabel(e), $"the activity '{ElementLabel(e)}' is not supported");
            }
        }

        /// <summary>The argument that holds the setting of a form-only business rule action (plan 2.21, Task 4).</summary>
        private static string ClientOnlySetting(string kind) => kind switch
        {
            "SetVisibility" => "IsVisible",
            "SetDisplayMode" => "IsReadOnly",
            _ => "RequiredLevel",
        };

        /// <summary>
        /// The text of an argument read only for the export (a form-only action's column and setting): null when absent or in a shape
        /// <see cref="Argument"/> refuses, which never fails the parse (the server does nothing with it).
        /// </summary>
        private static string? OptionalArgument(XElement e, string key)
        {
            try
            {
                return Argument(e, key);
            }
            catch (ShapeException)
            {
                return null;
            }
        }

        private void RequireBusinessRule(XElement e)
        {
            if (category is not (2 or CalculatedCategory))
                throw new ShapeException(ElementLabel(e), $"'{ElementLabel(e)}' is a business rule action");
        }

        private static WorkflowStep? Collapse(List<WorkflowStep> steps, string path, string? display) => steps.Count switch
        {
            0 => null,
            1 => steps[0] with { Path = path, DisplayName = display ?? steps[0].DisplayName },
            _ => new SequenceStep(path, display, steps),
        };

        /// <summary>
        /// The child activities of a container: the <c>Activities</c> collection of an <c>ActivityReference</c>, or the
        /// direct children of a <c>Sequence</c> or <c>Workflow</c> that are not property elements.
        /// </summary>
        private static IEnumerable<XElement> ActivitiesOf(XElement e)
        {
            var properties = Properties(e);
            if (properties is not null)
                return properties.Elements().FirstOrDefault(p => (string?)p.Attribute(XKey) == "Activities")?.Elements() ?? [];
            return e.Elements().Where(c => !c.Name.LocalName.Contains('.'));
        }

        private static XElement? Properties(XElement e) =>
            e.Elements().FirstOrDefault(c => c.Name.LocalName == "ActivityReference.Properties");

        private static XElement? PropertyByKey(XElement e, string key) =>
            Properties(e)?.Elements().FirstOrDefault(p => (string?)p.Attribute(XKey) == key);

        // ---- arguments of activities --------------------------------------------------------------------------------

        /// <summary>The text of an <c>ActivityReference</c> argument (<c>x:Key</c>), null when absent or <c>{x:Null}</c>.</summary>
        /// <exception cref="ShapeException">The argument is written as an element (a value or expression element inside it): not
        /// supported, never read as absent (final review M1).</exception>
        private static string? Argument(XElement e, string key)
        {
            var arg = e.Elements().FirstOrDefault(c => c.Name.LocalName == "ActivityReference.Arguments")
                ?.Elements().FirstOrDefault(a => (string?)a.Attribute(XKey) == key);
            // <x:Null x:Key="Parameters" /> (parity export: EvaluateCondition of a unary operator) is an absent argument.
            return arg is null || arg.Name == XNull ? null : ArgumentText(e, key, arg);
        }

        /// <inheritdoc cref="Argument" path="/exception"/>
        private static IEnumerable<(string Key, bool IsInput, string? Text)> AllArguments(XElement e) =>
            e.Elements().FirstOrDefault(c => c.Name.LocalName == "ActivityReference.Arguments")?.Elements()
                .Where(a => a.Attribute(XKey) is not null)
                .Select(a => ((string)a.Attribute(XKey)!, a.Name.LocalName != "OutArgument",
                    a.Name == XNull ? null : ArgumentText(e, (string)a.Attribute(XKey)!, a)))
                .ToList()
            ?? [];

        private static string? ArgumentText(XElement e, string key, XElement arg) => arg.HasElements
            ? Fail<string?>(e, $"the argument '{key}' written as an element is not supported")
            : NullIfXNull(arg.Value);

        private string RequiredArgument(XElement e, string key) =>
            Argument(e, key)?.Trim() is { Length: > 0 } text ? text : Fail<string>(e, $"the argument '{key}' is missing");

        /// <summary>An attribute of an activity, or the text of its property element (<c>Owner.Name</c>), null when <c>{x:Null}</c>.</summary>
        private static string? Member(XElement e, string name)
        {
            if (e.Attribute(name) is { } attribute)
                return NullIfXNull(attribute.Value);
            var property = e.Element(XName.Get($"{e.Name.LocalName}.{name}", e.Name.NamespaceName));
            if (property is null)
                return null;
            var argument = property.Elements().FirstOrDefault(c => c.Name.LocalName is "InArgument" or "OutArgument");
            var holder = argument ?? property;
            return holder.HasElements ? null : NullIfXNull(holder.Value);
        }

        private static string? NullIfXNull(string? text) => text?.Trim() == "{x:Null}" ? null : text;

        private static T Fail<T>(XElement e, string reason) => throw new ShapeException(ElementLabel(e), reason);

        // ---- expressions --------------------------------------------------------------------------------------------

        private VbExpr Vb(XElement e, string text)
        {
            var inner = ExpressionParser.Bracketed(text) ?? Fail<string>(e, $"'{Clip(text)}' is not an expression");
            try
            {
                return ExpressionParser.Parse(inner);
            }
            catch (FormatException)
            {
                throw new ShapeException(ElementLabel(e), $"the expression '{Clip(inner)}' is not supported");
            }
        }

        /// <summary>The variable name of an output argument written as <c>[name]</c>.</summary>
        private string OutVariable(XElement e, string key)
        {
            var text = Argument(e, key) ?? Member(e, key) ?? Fail<string>(e, $"the output '{key}' is missing");
            return Vb(e, text) is VbExpr.Name name && !name.Value.Contains('.')
                ? name.Value
                : Fail<string>(e, $"the output '{text}' is not a variable");
        }

        private void Bind(XElement e, string variable, object value)
        {
            if (!_symbols.TryAdd(variable, value))
                throw new ShapeException(ElementLabel(e), $"the variable '{variable}' is written twice");
        }

        private ValueExpr Value(XElement e, string member, bool required)
        {
            var text = Member(e, member);
            if (text is null)
                return required ? Fail<ValueExpr>(e, $"'{member}' is missing") : new ValueExpr.Literal("Null", null);
            return ResolveValue(e, Vb(e, text));
        }

        /// <summary>An argument's text: <c>[expr]</c> is resolved, plain text is a string literal, null is a null literal.</summary>
        private ValueExpr ResolveText(XElement e, string? text) => text is null
            ? new ValueExpr.Literal("Null", null)
            : ExpressionParser.Bracketed(text) is null ? new ValueExpr.Literal("String", text) : ResolveValue(e, Vb(e, text));

        private ValueExpr ResolveValue(XElement e, VbExpr expr)
        {
            switch (expr)
            {
                case VbExpr.Name { Value: var name } when !name.Contains('.'):
                    if (_symbols.TryGetValue(name, out var bound))
                        return bound as ValueExpr ?? Fail<ValueExpr>(e, $"'{name}' is a condition, not a value");
                    if (_runtimeNames.Contains(name))
                        return new ValueExpr.VariableRef(name);
                    throw new ShapeException(ElementLabel(e), $"the variable '{name}' is not defined");
                case VbExpr.Str s:
                    return new ValueExpr.Literal("String", s.Value);
                case VbExpr.Bool b:
                    return new ValueExpr.Literal("Boolean", b.Value);
                case VbExpr.Num n when ExpressionParser.TryDecimal(n.Text, out var d):
                    return new ValueExpr.Literal("Decimal", d);
                case VbExpr.Nothing:
                    return new ValueExpr.Literal("Null", null);
                case VbExpr.Call { Target: VbExpr.Name { Value: "String.Concat" }, Args: var parts }:
                    Use("Vb:String.Concat");
                    return new ValueExpr.Concat(parts.Select(p => ResolveValue(e, p)).ToList());
                case VbExpr.Call { Target: VbExpr.Name { Value: "DirectCast" }, Args: [var operand, VbExpr.Name { Value: var type }] }
                    when CastTypes.Contains(type):
                    Use(type is "System.String" or "System.Decimal" ? "Vb:DirectCast" : "Vb:DirectCast:OtherPropertyTypes");
                    return new ValueExpr.Cast(type, ResolveValue(e, operand));
                // An output of a "perform action" step (assumed shape, catalog Vb:ActionOutput).
                case VbExpr.Call { Target: VbExpr.Name { Value: var result }, Args: [VbExpr.Str output] } when _actionResults.Contains(result):
                    Use("Vb:ActionOutput");
                    return new ValueExpr.ActionOutput(result, output.Value);
                default:
                    throw new ShapeException(ElementLabel(e), $"the expression '{Describe(expr)}' is not supported");
            }
        }

        /// <summary>
        /// The types a <c>DirectCast</c> may name. <c>System.String</c> (a stop message) and <c>System.Decimal</c> (a decimal output
        /// argument) are in the parity export (catalog entry <c>Vb:DirectCast</c>, <c>export</c>); the CLR types of the other
        /// workflow property types are assumed (<c>Vb:DirectCast:OtherPropertyTypes</c>, checklist 1g) and parse the same way.
        /// </summary>
        private static readonly HashSet<string> CastTypes = new(StringComparer.Ordinal)
        {
            // export
            "System.String", "System.Decimal",
            // assumed (1g)
            "System.Int32", "System.Double", "System.Boolean", "System.DateTime", "System.Guid",
            EntityReferenceType, "Microsoft.Xrm.Sdk.Money", "Microsoft.Xrm.Sdk.OptionSetValue", EntityCollectionType,
        };

        private const string EntityReferenceType = "Microsoft.Xrm.Sdk.EntityReference", EntityCollectionType = "Microsoft.Xrm.Sdk.EntityCollection";

        // ---- TargetType (review of Task 3b, I1) ----------------------------------------------------------------------

        /// <summary>
        /// The <c>TargetType</c> of a <c>Get/SetEntityProperty</c> (its <c>.TargetType</c> property element) or of an
        /// <c>ActivityReference</c> (its <c>TargetType</c> argument), as a full CLR type name: an <c>InArgument</c> holding one
        /// <c>mxswa:ReferenceLiteral</c> (parity export). Null when absent or <c>x:Null</c>; any other shape is unsupported.
        /// </summary>
        private string? TargetTypeOf(XElement e)
        {
            if (e.Attribute("TargetType") is not null)
                Fail<string>(e, "a TargetType written as an attribute is not supported");
            var holder = e.Element(XName.Get($"{e.Name.LocalName}.TargetType", e.Name.NamespaceName)) is { } property
                ? property.Elements().ToList() is [var single] ? single : Fail<XElement>(e, "the TargetType property must hold one InArgument")
                : e.Elements().FirstOrDefault(c => c.Name.LocalName == "ActivityReference.Arguments")
                    ?.Elements().FirstOrDefault(a => (string?)a.Attribute(XKey) == "TargetType");
            if (holder is null || holder.Name == XNull)
                return null;
            if (holder.Name.LocalName != "InArgument" || holder.Elements().ToList() is not [var literal]
                || literal.Name.LocalName != "ReferenceLiteral"
                || ActivityCatalog.NormalizeNamespace(literal.Name.NamespaceName) != ActivityCatalog.SdkActivitiesNs
                || holder.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                return Fail<string>(e, $"the TargetType '{Clip(holder.ToString(SaveOptions.DisableFormatting))}' is not a type literal");
            Use(literal);
            if ((string?)literal.Attribute("Value") is { Length: > 0 } type)
            {
                Use("Shape:TargetType");
                return ResolveClrType(literal, type);
            }
            return literal.Elements().ToList() is [var nothing] && nothing.Name == XNull
                ? null
                : Fail<string>(e, $"the TargetType '{Clip(literal.ToString(SaveOptions.DisableFormatting))}' is not a type literal");
        }

        /// <summary>The CLR type of a <see cref="ValueExpr.Literal"/>'s workflow property type; null for <c>Null</c>.</summary>
        private static string? LiteralClrType(string type) => type switch
        {
            "String" => "System.String",
            "Integer" => "System.Int32",
            "Decimal" => "System.Decimal",
            "Double" => "System.Double",
            "Money" => "Microsoft.Xrm.Sdk.Money",
            "Boolean" => "System.Boolean",
            "DateTime" => "System.DateTime",
            "OptionSetValue" => "Microsoft.Xrm.Sdk.OptionSetValue",
            "Guid" => "System.Guid",
            "EntityReference" => EntityReferenceType,
            _ => null,
        };

        /// <summary>The CLR type a value has whatever the data, when the parser knows it; null when only the run can tell.</summary>
        private string? StaticType(ValueExpr value) => value switch
        {
            ValueExpr.Literal literal => LiteralClrType(literal.Type),
            ValueExpr.Conversion conversion => conversion.To,
            ValueExpr.AttributeOf attribute => attribute.TargetType,
            ValueExpr.Cast cast => cast.ClrType,
            ValueExpr.Concat => "System.String",
            ValueExpr.ExecutionTime => "System.DateTime",
            ValueExpr.VariableRef variable => _arguments.FirstOrDefault(a => a.Name == variable.Name)?.ClrType,
            ValueExpr.FirstNonNull first => first.Operands.Select(StaticType).Distinct().ToList() is [{ } only] ? only : null,
            _ => null,
        };

        /// <summary>
        /// Checks a value against a declared <c>TargetType</c> that converts nothing: when the parser knows the value's type, it
        /// must be that type (a pair that would need a conversion is unsupported, naming it). Otherwise the run checks it.
        /// </summary>
        private void CheckTargetType(XElement e, ValueExpr value, string? target)
        {
            if (target is null) return;
            var source = StaticType(value);
            // A calculated column definition writes the column's own type as the TargetType of the steps that compute its value
            // (2.12a parity export: the count of TRIMLEFT is an Integer with TargetType String, the current time an Int32 in
            // DIFFINDAYS): it converts nothing, and the translator types each value from the metadata and the result against the
            // column. Only the column's type passes: another TargetType that is not the value's own is refused.
            if (category == CalculatedCategory)
            {
                if (source == target) return;
                if (target == calculatedColumnType)
                {
                    Use("Shape:CalculatedTargetType");
                    return;
                }
                Fail<bool>(e, source is null
                    ? $"the TargetType {target} is not the type of the calculated column ({calculatedColumnType ?? "unknown"})"
                    : $"converting {source} to the TargetType {target} is not supported: only the type of the calculated column ({calculatedColumnType ?? "unknown"}) is");
            }
            if (source is not null && source != target)
                Fail<bool>(e, $"converting {source} to the TargetType {target} is not supported");
        }

        private static string Describe(VbExpr expr) => Clip(DescribeFull(expr));

        private static string DescribeFull(VbExpr expr) => expr switch
        {
            VbExpr.Name n => n.Value,
            VbExpr.Str s => $"\"{s.Value}\"",
            VbExpr.Num n => n.Text,
            VbExpr.Bool b => b.Value ? "True" : "False",
            VbExpr.Nothing => "Nothing",
            VbExpr.Call c => $"{Describe(c.Target)}({string.Join(", ", c.Args.Select(Describe))})",
            VbExpr.Member m => $"{Describe(m.Target)}.{m.MemberName}",
            VbExpr.New n => $"New {n.Type}({string.Join(", ", n.Args.Select(Describe))})",
            VbExpr.NewArray a => $"New {a.Type}() {{ {string.Join(", ", a.Elements.Select(Describe))} }}",
            _ => expr.ToString(),
        };

        /// <summary><c>InputEntities("primaryEntity")</c>, <c>CreatedEntities("k")</c> ... as (dictionary, key), or null.</summary>
        private static (string Dictionary, string Key)? EntityKey(VbExpr expr) =>
            expr is VbExpr.Call { Target: VbExpr.Name { Value: var dict }, Args: [VbExpr.Str key] } && EntityDictionaries.Contains(dict)
                ? (dict, key.Value)
                : null;

        /// <summary>The record an entity expression (or its <c>.Id</c>) designates.</summary>
        private RecordRef RecordRefOf(XElement e, VbExpr expr)
        {
            if (expr is VbExpr.Member { MemberName: "Id", Target: var target })
                expr = target;
            if (EntityKey(expr) is not { } key)
                throw new ShapeException(ElementLabel(e), $"the record '{Describe(expr)}' is not supported");
            Use("Shape:RecordKeys");

            return key switch
            {
                (_, "primaryEntity") => new RecordRef.Primary(),
                ("InputEntities", var k) when k.StartsWith("related_", StringComparison.Ordinal) && k.IndexOf('#') > "related_".Length
                    => new RecordRef.Related(k["related_".Length..k.IndexOf('#')]),
                ("CreatedEntities", var k) when !k.EndsWith("#Temp", StringComparison.Ordinal) => new RecordRef.Created(k),
                _ => throw new ShapeException(ElementLabel(e), $"the record '{Describe(expr)}' is not supported"),
            };
        }

        /// <summary>The record an activity acts on, from its <c>Entity</c> or <c>EntityId</c> member.</summary>
        private RecordRef RecordOf(XElement e)
        {
            var text = Member(e, "Entity") ?? Member(e, "EntityId") ?? Fail<string>(e, "the activity names no record");
            var record = RecordRefOf(e, Vb(e, text));
            NotePrimaryEntity(e, record);
            return record;
        }

        private void NotePrimaryEntity(XElement e, RecordRef record)
        {
            if (record is RecordRef.Primary && _primaryEntity is null && (string?)e.Attribute("EntityName") is { Length: > 0 } entity)
                _primaryEntity = entity;
        }

        private TempEntity TempOf(XElement e)
        {
            var text = Member(e, "Entity") ?? Fail<string>(e, "the activity names no entity");
            return EntityKey(Vb(e, text)) is { } key && _temps.TryGetValue($"{key.Dictionary}:{key.Key}", out var temp)
                ? temp
                : Fail<TempEntity>(e, $"the entity '{text}' was not prepared by the step");
        }

        // ---- data steps -----------------------------------------------------------------------------------------------

        private void ParseRetrieve(XElement e)
        {
            // Loading a related record into InputEntities: the related record ref is derived from the key itself.
            var record = RecordOf(e);
            if (record is not RecordRef.Related)
                throw new ShapeException(ElementLabel(e), "RetrieveEntity is supported only for related records");
        }

        /// <summary>
        /// A column read (<c>GetEntityProperty</c>) with its <c>TargetType</c> (review of Task 3b, I1). The parity export shows two
        /// conversions, kept as <see cref="ValueExpr.Conversion"/>: the primary key (<c>{entity}id</c>, or <c>activityid</c> of an activity table; a Guid) as an
        /// <c>EntityReference</c>, and a lookup as an <c>EntityCollection</c> (one activity party). Another target type on the primary
        /// key is unsupported; on any other column it converts nothing and is kept on the read
        /// (<see cref="ValueExpr.AttributeOf.TargetType"/>), for the run to check against the column's type.
        /// </summary>
        private void ParseGetValue(XElement e)
        {
            var attribute = (string?)e.Attribute("Attribute") ?? Fail<string>(e, "GetEntityProperty needs an Attribute");
            var record = RecordOf(e);
            var read = new ValueExpr.AttributeOf(record, attribute);
            var target = TargetTypeOf(e);
            var entity = (string?)e.Attribute("EntityName");
            ValueExpr value;
            if (target is null)
                value = read;
            // The primary key is {entity}id, or activityid on an activity table (plan 2.21, Task 2 review I4: Dataverse's own shape).
            else if (entity is { Length: > 0 } && (attribute == entity + "id" || attribute == "activityid"))
                value = target switch
                {
                    "System.Guid" => read,
                    EntityReferenceType => new ValueExpr.Conversion("System.Guid", EntityReferenceType, read, entity),
                    _ => Fail<ValueExpr>(e, $"converting System.Guid (the primary key '{attribute}') to the TargetType {target} is not supported"),
                };
            else if (target == EntityCollectionType)
                value = new ValueExpr.Conversion(EntityReferenceType, EntityCollectionType, read with { TargetType = EntityReferenceType });
            else
                value = read with { TargetType = target };
            Bind(e, OutVariable(e, "Value"), value);
        }

        private WorkflowStep? ParseSetValue(XElement e, string path, string? display)
        {
            var attribute = (string?)e.Attribute("Attribute") ?? Fail<string>(e, "SetEntityProperty needs an Attribute");
            var entityText = Member(e, "Entity") ?? Fail<string>(e, "SetEntityProperty names no entity");
            var key = EntityKey(Vb(e, entityText));
            var value = Value(e, "Value", required: false);
            CheckTargetType(e, value, TargetTypeOf(e));

            if (key is { } k && _temps.TryGetValue($"{k.Dictionary}:{k.Key}", out var temp))
            {
                temp.Values[attribute] = value;
                return null;
            }
            if (category is (2 or CalculatedCategory) && key is ("InputEntities", "primaryEntity"))
            {
                NotePrimaryEntity(e, new RecordRef.Primary());
                return new SetValueStep(path, display, attribute, value, DefaultOnly: false);
            }
            throw new ShapeException(ElementLabel(e), $"setting a value on '{entityText}' is not supported here");
        }

        private WorkflowStep ParseCreate(XElement e, string path, string? display)
        {
            var (key, temp) = TempWithKey(e);
            var variable = key.EndsWith("#Temp", StringComparison.Ordinal) ? key[..^"#Temp".Length] : key;
            return new CreateStep(path, display, temp.Entity, temp.Values, variable);
        }

        private WorkflowStep ParseUpdate(XElement e, string path, string? display)
        {
            var (key, temp) = TempWithKey(e);
            var record = temp.Record ?? RecordFromTempKey(e, key);
            NotePrimaryEntity(e, record);
            return new UpdateStep(path, display, record, temp.Values);
        }

        private RecordRef RecordFromTempKey(XElement e, string key)
        {
            var bare = key.EndsWith("#Temp", StringComparison.Ordinal) ? key[..^"#Temp".Length] : key;
            return RecordRefOf(e, new VbExpr.Call(new VbExpr.Name("InputEntities"), [new VbExpr.Str(bare)]));
        }

        private WorkflowStep ParseSendEmail(XElement e, string path, string? display)
        {
            var (key, temp) = TempWithKey(e);
            if (temp.Entity != "email")
                throw new ShapeException(ElementLabel(e), $"SendEmail needs an email entity, not '{temp.Entity}'");
            var variable = key.EndsWith("#Temp", StringComparison.Ordinal) ? key[..^"#Temp".Length] : key;
            return new SendEmailStep(path, display, variable, temp.Values);
        }

        private (string Key, TempEntity Temp) TempWithKey(XElement e)
        {
            var temp = TempOf(e);
            var key = EntityKey(Vb(e, Member(e, "Entity")!))!.Value.Key;
            return (key, temp);
        }

        /// <summary>
        /// The designer's bookkeeping assignments around a data step, and the assignment of an action's output argument.
        /// Any other assignment is not supported.
        /// </summary>
        private WorkflowStep? ParseAssign(XElement e, string path, string? display)
        {
            var toText = Member(e, "To") ?? Fail<string>(e, "Assign needs To");
            var valueText = Member(e, "Value") ?? Fail<string>(e, "Assign needs Value");
            var to = Vb(e, toText);
            var value = Vb(e, valueText);

            // New entity to fill: To = CreatedEntities("k#Temp"), Value = New Entity("task").
            if (EntityKey(to) is { } target && value is VbExpr.New { Type: "Entity" or "Microsoft.Xrm.Sdk.Entity", Args: [VbExpr.Str entity] })
            {
                _temps[$"{target.Dictionary}:{target.Key}"] = new TempEntity(entity.Value);
                Use("Shape:EntityBookkeeping");
                return null;
            }

            // The record an update writes: To = UpdatedEntities("k#Temp").Id, Value = InputEntities("k").Id.
            if (to is VbExpr.Member { MemberName: "Id", Target: var idOf } && EntityKey(idOf) is { } idKey
                && _temps.TryGetValue($"{idKey.Dictionary}:{idKey.Key}", out var temp))
            {
                temp.Record = RecordRefOf(e, value);
                Use("Shape:EntityBookkeeping");
                return null;
            }

            // Copying the filled entity back after a step: CreatedEntities("k") = CreatedEntities("k#Temp") (the created
            // record's alias) or InputEntities("k") = UpdatedEntities("k#Temp") / CreatedEntities("k#Temp") (write-back after an
            // update; the parity export uses CreatedEntities). No effect here.
            if (EntityKey(to) is { } copyTo && EntityKey(value) is { } copyFrom
                && copyFrom.Key == copyTo.Key + "#Temp"
                && (copyTo.Dictionary, copyFrom.Dictionary) is ("CreatedEntities", "CreatedEntities") or ("InputEntities", "UpdatedEntities")
                    or ("InputEntities", "CreatedEntities"))
            {
                Use("Shape:EntityBookkeeping");
                return null;
            }

            // An action's output argument (W14).
            if (to is VbExpr.Name { Value: var name } && _arguments.Any(a => !a.IsInput && a.Name == name))
            {
                Use("Shape:OutputAssign");
                return new AssignOutputStep(path, display, name, ResolveValue(e, value));
            }

            throw new ShapeException(ElementLabel(e), $"the assignment '{Describe(to)} = {Describe(value)}' is not supported");
        }

        private static int OptionValue(XElement e, string member)
        {
            if (e.Attribute(member) is { } attribute && int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
                return direct;
            var option = e.Element(XName.Get($"{e.Name.LocalName}.{member}", e.Name.NamespaceName))
                ?.Descendants().FirstOrDefault(d => d.Name.LocalName == "OptionSetValue" && d.Attribute("Value") is not null);
            return option is not null && int.TryParse((string)option.Attribute("Value")!, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : Fail<int>(e, $"SetState needs a literal {member}");
        }

        /// <summary>
        /// Business rule "set field value" (parity export): <c>mcwc:SetAttributeValue</c> applies the columns set on the temporary
        /// entity of the primary record (<c>CreatedEntities("primaryEntity#Temp")</c>), one <see cref="SetValueStep"/> each.
        /// </summary>
        private WorkflowStep ParseSetAttributeValue(XElement e, string path, string? display)
        {
            CheckAttributes(e, "DisplayName", "Entity", "EntityName");
            CheckNoChildren(e);
            var (key, temp) = TempWithKey(e);
            var record = temp.Record ?? RecordFromTempKey(e, key);
            if (record is not RecordRef.Primary)
                throw new ShapeException(ElementLabel(e), "SetAttributeValue is supported only on the primary record");
            if ((string?)e.Attribute("EntityName") is { } entityName && entityName != temp.Entity)
                throw new ShapeException(ElementLabel(e), $"SetAttributeValue names '{entityName}' for an entity of '{temp.Entity}'");
            NotePrimaryEntity(e, record);
            if (temp.Values.Count == 0)
                throw new ShapeException(ElementLabel(e), "SetAttributeValue sets no column");
            var steps = temp.Values.Select((v, i) => (WorkflowStep)new SetValueStep(temp.Values.Count == 1 ? path : $"{path}/{i}",
                temp.Values.Count == 1 ? display : null, v.Key, v.Value, DefaultOnly: false)).ToList();
            return steps.Count == 1 ? steps[0] : new SequenceStep(path, display, steps);
        }

        private static readonly XName StepLabelName =
            XName.Get("StepLabel", "clr-namespace:Microsoft.Crm.Workflow.ObjectModel;assembly=Microsoft.Crm");

        /// <summary>
        /// Business rule "show error message" (parity export): <c>mcwc:SetMessage</c> on a column of the primary record, its text in
        /// one <c>mcwo:StepLabel</c>, <c>Level</c> <c>[Nothing]</c>. Labels in several languages and any other level are not supported.
        /// </summary>
        private WorkflowStep ParseClientSetMessage(XElement e, string path, string? display)
        {
            CheckAttributes(e, "DisplayName", "ControlId", "Entity", "EntityName", "Level");
            var record = RecordOf(e);
            if (record is not RecordRef.Primary)
                throw new ShapeException(ElementLabel(e), "SetMessage is supported only on the primary record");
            if (Member(e, "Level") is { } level && Vb(e, level) is not VbExpr.Nothing)
                throw new ShapeException(ElementLabel(e), $"the message level '{Clip(level)}' is not supported");

            var labelsName = XName.Get($"{e.Name.LocalName}.StepLabels", e.Name.NamespaceName);
            if (e.Elements().FirstOrDefault(c => c.Name != labelsName) is { } other)
                throw new ShapeException(ElementLabel(e), $"the element '{other.Name.LocalName}' of SetMessage is not supported");
            var labels = e.Elements(labelsName).Elements().ToList();
            if (labels.Count != 1 || labels[0].Name.LocalName != StepLabelName.LocalName
                || ActivityCatalog.NormalizeNamespace(labels[0].Name.NamespaceName) != StepLabelName.NamespaceName)
                throw new ShapeException(ElementLabel(e), $"SetMessage needs exactly one StepLabel, not {labels.Count} label(s)");
            var text = (string?)labels[0].Attribute("Description") ?? Fail<string>(e, "the StepLabel has no Description");
            Use("Shape:StepLabel");
            return new ShowErrorStep(path, display, new ValueExpr.Literal("String", text)) { Attribute = (string?)e.Attribute("ControlId") };
        }

        /// <summary>Refuses an attribute (outside the XAML namespaces) that the shape does not know.</summary>
        private static void CheckAttributes(XElement e, params string[] known)
        {
            foreach (var attribute in e.Attributes())
                if (!attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.None && !known.Contains(attribute.Name.LocalName))
                    throw new ShapeException(ElementLabel(e), $"the attribute '{attribute.Name.LocalName}' of {e.Name.LocalName} is not supported");
        }

        private static void CheckNoChildren(XElement e)
        {
            if (e.HasElements)
                throw new ShapeException(ElementLabel(e), $"the element '{e.Elements().First().Name.LocalName}' of {e.Name.LocalName} is not supported");
        }

        private WorkflowStep ParseChildWorkflow(XElement e, string path, string? display)
        {
            var idText = Member(e, "WorkflowId") ?? Fail<string>(e, "StartChildWorkflow needs a WorkflowId");
            Guid workflowId;
            if (ExpressionParser.Bracketed(idText) is null)
            {
                if (!Guid.TryParse(idText.Trim(), out workflowId))
                    throw new ShapeException(ElementLabel(e), $"the workflow id '{idText}' is not a GUID");
            }
            else if (Vb(e, idText) is VbExpr.New { Type: "Guid" or "System.Guid", Args: [VbExpr.Str s] } && Guid.TryParse(s.Value, out var parsed))
            {
                workflowId = parsed;
            }
            else
            {
                throw new ShapeException(ElementLabel(e), $"the workflow id '{idText}' is not supported");
            }

            var record = RecordRefOf(e, Vb(e, Member(e, "EntityId") ?? Fail<string>(e, "StartChildWorkflow needs an EntityId")));
            NotePrimaryEntity(e, record);
            CheckNoChildInputs(e);
            return new ChildWorkflowStep(path, display, workflowId, record);
        }

        private const string EmptyDictionary = "[New Dictionary(Of System.String, System.Object)]";

        /// <summary>
        /// The <c>InputParameters</c> of a child workflow step: absent, or a variable of an enclosing step declared with an empty
        /// dictionary as default (parity export) that no activity writes; a child workflow gets no input from its parent.
        /// </summary>
        private void CheckNoChildInputs(XElement e)
        {
            if (Member(e, "InputParameters") is not { } text)
                return;
            if (Vb(e, text) is not VbExpr.Name { Value: var name } || name.Contains('.') || _symbols.ContainsKey(name) || _runtimeNames.Contains(name))
                throw new ShapeException(ElementLabel(e), $"the child workflow inputs '{Clip(text)}' are not supported");
            var declaration = e.Ancestors()
                .SelectMany(a => a.Elements().Where(c => c.Name.LocalName.EndsWith(".Variables", StringComparison.Ordinal)))
                .SelectMany(v => v.Elements(XName.Get("Variable", ActivityCatalog.ActivitiesNs)))
                .FirstOrDefault(v => (string?)v.Attribute("Name") == name);
            if (declaration is null || ((string?)declaration.Attribute("Default"))?.Trim() != EmptyDictionary || declaration.HasElements)
                throw new ShapeException(ElementLabel(e), $"the child workflow inputs '{Clip(text)}' are not an empty dictionary");
            Use("Shape:ChildInputs");
        }

        private WorkflowStep ParseStop(XElement e, string path, string? display)
        {
            var exceptionText = Member(e, "Exception") ?? Fail<string>(e, "a stop step needs an Exception");
            // The status and the message in the exception (assumed), or the status alone with the message in Reason (parity
            // export: Reason="[DirectCast(StopWorkflowStep1_1, System.String)]").
            if (Vb(e, exceptionText) is not VbExpr.New { Type: "Microsoft.Xrm.Sdk.InvalidPluginExecutionException", Args: [VbExpr.Name status, ..] args }
                || args.Count > 2)
                throw new ShapeException(ElementLabel(e), $"the stop expression '{exceptionText}' is not supported");

            var canceled = status.Value switch
            {
                "Microsoft.Xrm.Sdk.OperationStatus.Canceled" => true,
                "Microsoft.Xrm.Sdk.OperationStatus.Succeeded" => false,
                _ => Fail<bool>(e, $"the stop status '{status.Value}' is not supported"),
            };
            if (args.Count == 2 && Member(e, "Reason") is not null)
                Fail<bool>(e, "a stop step with a message in both its exception and its Reason is not supported");
            ValueExpr? messageValue = args.Count == 2
                ? ResolveValue(e, args[1])
                : Member(e, "Reason") is { } reason ? ResolveValue(e, Vb(e, reason)) : null;
            // A message cast to a string that already is one is that string.
            Use("Shape:StopException");
            if (messageValue is ValueExpr.Cast { ClrType: "System.String", Operand: ValueExpr.Literal { Type: "String" or "Null" } or ValueExpr.Concat } cast)
                messageValue = cast.Operand;
            return new StopStep(path, display, canceled, messageValue is null or ValueExpr.Literal { Value: null or "" } ? null : messageValue);
        }

        private WorkflowStep ParseActionCall(XElement e, string path, string? display)
        {
            var message = RequiredArgument(e, "ActionName");
            var targetText = Argument(e, "Target");
            var target = targetText is null ? null : RecordRefOf(e, Vb(e, targetText));
            var variable = OutVariable(e, "Result");

            var inputs = new Dictionary<string, ValueExpr>(StringComparer.Ordinal);
            foreach (var (key, isInput, text) in AllArguments(e))
                if (isInput && key is not ("ActionName" or "Target"))
                    inputs[key] = ResolveText(e, text);

            _runtimeNames.Add(variable);
            _actionResults.Add(variable);
            return new ActionStep(path, display, message, target, inputs, variable);
        }

        private WorkflowStep ParseCustomActivity(XElement e, string path, string? display)
        {
            var (clrNamespace, assembly) = ActivityCatalog.ParseClrNamespace(e.Name.NamespaceName)!.Value;
            var inputs = new Dictionary<string, ValueExpr>(StringComparer.Ordinal);
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var attribute in e.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None || attribute.Name.LocalName == "DisplayName")
                    continue;
                inputs[attribute.Name.LocalName] = ResolveValue(e, Vb(e, attribute.Value));
            }

            var prefix = e.Name.LocalName + ".";
            foreach (var property in e.Elements().Where(c => c.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var name = property.Name.LocalName[prefix.Length..];
                var children = property.Elements().ToList();
                var argument = children.Count == 1
                    ? children[0]
                    : Fail<XElement>(e, $"the property '{name}' must hold exactly one argument, not {children.Count}");
                var text = NullIfXNull(argument.HasElements ? null : argument.Value);
                switch (argument.Name.LocalName)
                {
                    case "InArgument":
                        inputs[name] = text is null ? new ValueExpr.Literal("Null", null) : ResolveValue(e, Vb(e, text));
                        break;
                    case "OutArgument" when text is not null && Vb(e, text) is VbExpr.Name { Value: var variable } && !variable.Contains('.'):
                        outputs[name] = variable;
                        _runtimeNames.Add(variable);
                        break;
                    default:
                        throw new ShapeException(ElementLabel(e), $"the property '{name}' of the custom activity is not supported");
                }
            }

            return new CustomActivityStep(path, display, $"{clrNamespace}.{e.Name.LocalName}", assembly, inputs, outputs);
        }

        // ---- expressions as activities --------------------------------------------------------------------------------

        private void ParseEvaluateExpression(XElement e)
        {
            var op = RequiredArgument(e, "ExpressionOperator");
            if (ActivityCatalog.FindShape("EvaluateExpression:" + op) is not { } entry
                || (ActivityCatalog.IsCalculatedOnly(entry) && category != CalculatedCategory)
                // An aggregate operator is read only as the aggregate of a rollup column definition (RunRollup), never as a value.
                || ActivityCatalog.IsRollupOnly(entry)
                // A calculated column reads the current time as Now (2.12a parity export), never as the workflow operator.
                || (op == "RetrieveCurrentTime" && category == CalculatedCategory))
                throw new ShapeException(ElementLabel(e), $"the expression operator '{op}' is not supported");
            Use("EvaluateExpression:" + op);

            var parameters = Parameters(e);
            ValueExpr value = op switch
            {
                "CreateCrmType" => CreateCrmType(e, parameters),
                "RetrieveCurrentTime" => new ValueExpr.ExecutionTime(),
                // The current time of a calculated column (2.12a parity export: Now, with an empty Parameters array).
                "Now" => parameters.Count == 0 ? new ValueExpr.ExecutionTime() : Fail<ValueExpr>(e, "Now takes no parameters"),
                // An action's input argument (parity export: { Increment }), read as the argument itself (W14).
                "CustomOperationArguments" => parameters is [VbExpr.Name { Value: var argument }]
                    && _arguments.Any(a => a.IsInput && a.Name == argument)
                        ? new ValueExpr.VariableRef(argument)
                        : Fail<ValueExpr>(e, "CustomOperationArguments must name one input argument of the process"),
                "SelectFirstNonNull" => parameters.Count switch
                {
                    0 => Fail<ValueExpr>(e, "SelectFirstNonNull needs parameters"),
                    1 => ResolveValue(e, parameters[0]),
                    _ => new ValueExpr.FirstNonNull(parameters.Select(p => ResolveValue(e, p)).ToList()),
                },
                // The designer writes a function as AddDays, DiffInDays, TrimLeft, Concat (2.12a parity export); the tree names it as
                // the maker does (ADDDAYS, DIFFINDAYS, TRIMLEFT, CONCAT).
                _ when entry.Kind == "Function" => new ValueExpr.FunctionCall(op.ToUpperInvariant(), parameters.Select(p => ResolveValue(e, p)).ToList()),
                _ => parameters.Count == 0
                    ? Fail<ValueExpr>(e, $"{op} needs parameters")
                    : new ValueExpr.Arithmetic(op, parameters.Select(p => ResolveValue(e, p)).ToList()),
            };
            var target = TargetTypeOf(e);
            // The id of a lookup literal (parity export: CreateCrmType of a Guid with the lookup's TargetType EntityReference) stays a
            // Guid: only the lookup's CreateCrmType reads it, and a Guid set anywhere else is checked against that target there.
            if (!(op == "CreateCrmType" && value is ValueExpr.Literal { Type: "Guid" } && target == EntityReferenceType))
                CheckTargetType(e, value, target);
            Bind(e, OutVariable(e, "Result"), value);
        }

        /// <summary>The elements of a <c>Parameters</c> argument written as <c>[New Object() { ... }]</c>.</summary>
        private IReadOnlyList<VbExpr> Parameters(XElement e)
        {
            var text = Argument(e, "Parameters");
            if (text is null)
                return [];
            return Vb(e, text) is VbExpr.NewArray { Type: "Object", Elements: var elements }
                ? elements
                : Fail<IReadOnlyList<VbExpr>>(e, $"the parameters '{text}' are not supported");
        }

        private const string PropertyTypePrefix = "Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.";

        /// <summary>
        /// <c>{ WorkflowPropertyType.T, value[, entity], "label" }</c> → a <see cref="ValueExpr.Literal"/>. A lookup literal is
        /// <c>{ WorkflowPropertyType.EntityReference, "entity", "display name", idVariable, "Lookup" }</c> (parity export), whose
        /// id variable holds a <c>Guid</c> literal; the shape <c>{ EntityReference, "id", "entity", ... }</c> is still read (assumed).
        /// </summary>
        private ValueExpr CreateCrmType(XElement e, IReadOnlyList<VbExpr> p)
        {
            if (p.Count < 2 || p[0] is not VbExpr.Name { Value: var typeName } || !typeName.StartsWith(PropertyTypePrefix, StringComparison.Ordinal))
                throw new ShapeException(ElementLabel(e), "CreateCrmType needs a WorkflowPropertyType and a value");

            var type = typeName[PropertyTypePrefix.Length..];
            if (type == "EntityReference" && p.Count >= 4 && p[1] is VbExpr.Str { Value: { Length: > 0 } lookupEntity } && p[3] is not VbExpr.Str)
            {
                var id = p[3] is VbExpr.Name { Value: var idName } && _symbols.TryGetValue(idName, out var bound)
                    && bound is ValueExpr.Literal { Type: "Guid", Value: Guid guid }
                        ? guid
                        : Fail<Guid>(e, $"CreateCrmType of EntityReference needs the id of a Guid literal, not '{Describe(p[3])}'");
                Use("Shape:LookupLiteral");
                return new ValueExpr.Literal(type, new EntityRefValue(lookupEntity, id));
            }
            var raw = p[1] switch
            {
                VbExpr.Str s => s.Value,
                VbExpr.Num n => n.Text,
                VbExpr.Bool b => b.Value ? "True" : "False",
                _ => Fail<string>(e, $"CreateCrmType of {type} needs a literal value"),
            };
            var inv = CultureInfo.InvariantCulture;
            object? value = type switch
            {
                "String" => raw,
                "Boolean" => raw.Equals("True", StringComparison.OrdinalIgnoreCase) || raw == "1",
                "Integer" when int.TryParse(raw, NumberStyles.Integer, inv, out var i) => i,
                "OptionSetValue" when int.TryParse(raw, NumberStyles.Integer, inv, out var o) => o,
                "Decimal" or "Money" when decimal.TryParse(raw, NumberStyles.Number, inv, out var d) => d,
                "Double" when double.TryParse(raw, NumberStyles.Float, inv, out var f) => f,
                "DateTime" when DateTime.TryParse(raw, inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) => t,
                "Guid" when Guid.TryParse(raw, out var g) => g,
                "EntityReference" when Guid.TryParse(raw, out var id) && p.Count >= 3 && p[2] is VbExpr.Str { Value: { Length: > 0 } entity }
                    => new EntityRefValue(entity, id),
                _ => Fail<object>(e, $"CreateCrmType of {type} with '{raw}' is not supported"),
            };
            return new ValueExpr.Literal(type, value);
        }

        private void ParseEvaluateCondition(XElement e)
        {
            var op = RequiredArgument(e, "ConditionOperator");
            if (ActivityCatalog.FindShape("EvaluateCondition:" + op) is null)
                throw new ShapeException(ElementLabel(e), $"the condition operator '{op}' is not supported");
            Use("EvaluateCondition:" + op);

            var left = ResolveValue(e, Vb(e, Argument(e, "Operand") ?? Fail<string>(e, "EvaluateCondition needs an Operand")));
            var right = Parameters(e).Select(p => ResolveValue(e, p)).ToList();

            ConditionExpr condition = op switch
            {
                "Null" or "NotNull" when right.Count == 0 => new ConditionExpr.Compare(op, left, null),
                "Between" when right.Count == 2 => new ConditionExpr.And(
                    new ConditionExpr.Compare("GreaterEqual", left, right[0]), new ConditionExpr.Compare("LessEqual", left, right[1])),
                "NotBetween" when right.Count == 2 => new ConditionExpr.Or(
                    new ConditionExpr.Compare("LessThan", left, right[0]), new ConditionExpr.Compare("GreaterThan", left, right[1])),
                not ("Null" or "NotNull" or "Between" or "NotBetween") when right.Count == 1 => new ConditionExpr.Compare(op, left, right[0]),
                _ => Fail<ConditionExpr>(e, $"the condition operator '{op}' with {right.Count} value(s) is not supported"),
            };
            Bind(e, OutVariable(e, "Result"), condition);
        }

        private void ParseEvaluateLogicalCondition(XElement e)
        {
            var op = RequiredArgument(e, "LogicalOperator");
            ConditionExpr Operand(string key) =>
                Vb(e, Argument(e, key) ?? Fail<string>(e, $"{op} needs {key}")) is VbExpr.Name { Value: var n }
                && _symbols.TryGetValue(n, out var bound) && bound is ConditionExpr c
                    ? c
                    : Fail<ConditionExpr>(e, $"the operand '{key}' is not a condition");

            if (op is "And" or "Or" or "Not")
                Use("EvaluateLogicalCondition:" + op);
            ConditionExpr condition = op switch
            {
                "And" => new ConditionExpr.And(Operand("LeftOperand"), Operand("RightOperand")),
                "Or" => new ConditionExpr.Or(Operand("LeftOperand"), Operand("RightOperand")),
                "Not" => new ConditionExpr.Not(Operand("LeftOperand")),
                _ => Fail<ConditionExpr>(e, $"the logical operator '{op}' is not supported"),
            };
            Bind(e, OutVariable(e, "Result"), condition);
        }

        // ---- conditions and waits --------------------------------------------------------------------------------------

        /// <summary>
        /// A <c>ConditionSequence</c>: evaluation activities, then <c>ConditionBranch</c>es in order. A branch's <c>Else</c>
        /// is the next level (a nested <c>ConditionSequence</c>, flattened into more branches) or the otherwise block.
        /// With the <c>Wait</c> argument true it is a wait: the first branch's condition is waited for and its
        /// <c>Then</c> runs after; a second branch whose <c>Then</c> starts with a <c>Postpone</c> is the timeout branch.
        /// </summary>
        private WorkflowStep ParseConditionSequence(XElement e, string path, string? display)
        {
            var isWait = string.Equals(Argument(e, "Wait")?.Trim(), "True", StringComparison.OrdinalIgnoreCase);
            var branches = new List<(XElement Branch, string Path)>();
            var index = 0;
            foreach (var activity in ActivitiesOf(e))
            {
                var childPath = $"{path}/{index++}";
                if (Kind(activity) == "ConditionBranch")
                {
                    branches.Add((activity, childPath));
                    continue;
                }
                if (Parse(activity, childPath) is { } step)
                    throw new ShapeException(ElementLabel(activity), $"the step '{step.Label}' cannot appear among a condition's evaluations");
            }
            if (branches.Count == 0)
                throw new ShapeException(ElementLabel(e), "a condition without branches is not supported");

            return isWait ? ParseWait(e, path, display, branches) : ParseCondition(path, display, branches);
        }

        private ConditionExpr ConditionOfBranch(XElement branch)
        {
            var text = Argument(branch, "Condition") ?? Fail<string>(branch, "a branch needs a Condition");
            return Vb(branch, text) is VbExpr.Name { Value: var n } && _symbols.TryGetValue(n, out var bound) && bound is ConditionExpr c
                ? c
                : Fail<ConditionExpr>(branch, $"the branch condition '{text}' is not supported");
        }

        /// <summary>A branch whose <c>Condition</c> is <c>[True]</c>: the VB literal, whatever variable the step declares.</summary>
        private static bool IsAlwaysTrue(XElement branch)
        {
            if (Argument(branch, "Condition") is not { } text || ExpressionParser.Bracketed(text) is not { } inner)
                return false;
            try
            {
                return ExpressionParser.Parse(inner) is VbExpr.Bool { Value: true };
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private WorkflowStep ParseCondition(string path, string? display, List<(XElement Branch, string Path)> branches)
        {
            var result = new List<ConditionBranch>();
            WorkflowStep? otherwise = null;
            for (var i = 0; i < branches.Count; i++)
            {
                var (branch, branchPath) = branches[i];
                if (IsAlwaysTrue(branch))
                {
                    // The designer's "otherwise" (parity export, business rule): a last branch whose condition is the literal True.
                    if (i != branches.Count - 1 || otherwise is not null || PropertyByKey(branch, "Else") is { } other && other.Name != XNull)
                        throw new ShapeException(ElementLabel(branch), "a branch whose condition is True must be the last one, without Else");
                    otherwise = BlockOf(branch, "Then", branchPath) ?? new SequenceStep(branchPath + "/then", null, []);
                    Use("Shape:ElseBranch");
                    continue;
                }
                result.Add(new ConditionBranch(ConditionOfBranch(branch), BlockOf(branch, "Then", branchPath) ?? new SequenceStep(branchPath + "/then", null, []))
                {
                    Description = DescriptionOf(branch),
                });

                var elseBlock = PropertyByKey(branch, "Else");
                if (elseBlock is null || elseBlock.Name == XName.Get("Null", ActivityCatalog.XamlNs))
                    continue;
                if (i != branches.Count - 1)
                    throw new ShapeException(ElementLabel(branch), "only the last branch of a condition may have an Else");

                if (Kind(elseBlock) == "Condition")
                {
                    if (ParseConditionSequence(elseBlock, branchPath + "/else", DisplayName(elseBlock)) is not ConditionStep nested)
                        throw new ShapeException(ElementLabel(elseBlock), "a wait in the Else of a condition branch is not supported");
                    result.AddRange(nested.Branches);
                    otherwise = nested.Otherwise;
                }
                else
                {
                    otherwise = BlockOf(branch, "Else", branchPath);
                }
            }
            return new ConditionStep(path, display, result, otherwise);
        }

        private WorkflowStep ParseWait(XElement e, string path, string? display, List<(XElement Branch, string Path)> branches)
        {
            if (branches.Count > 2)
                throw new ShapeException(ElementLabel(e), "a wait with more than one timeout branch is not supported");

            // A wait runs its branch's Then once the condition holds; an Else on a branch, or a ContainsElseBranch other than
            // False, has no meaning the export showed (review of Task 3b, I3).
            foreach (var (branch, _) in branches)
                if (PropertyByKey(branch, "Else") is { } elseBlock && elseBlock.Name != XNull)
                    throw new ShapeException(ElementLabel(branch), "a wait branch with an Else block is not supported");
            if (PropertyByKey(e, "ContainsElseBranch") is { } containsElse && containsElse.Name != XNull
                && !containsElse.Value.Trim().Equals("False", StringComparison.OrdinalIgnoreCase))
                throw new ShapeException(ElementLabel(e), $"a wait whose ContainsElseBranch is '{Clip(containsElse.Value.Trim())}' is not supported");

            var (conditionBranch, conditionPath) = branches[0];
            var until = ConditionOfBranch(conditionBranch);
            var then = BlockOf(conditionBranch, "Then", conditionPath);

            ValueExpr? timeout = null;
            WorkflowStep? onTimeout = null;
            if (branches.Count == 2)
            {
                var (timeoutBranch, timeoutPath) = branches[1];
                if (Argument(timeoutBranch, "Condition")?.Trim() is { Length: > 0 } condition && condition is not ("True" or "[True]"))
                    throw new ShapeException(ElementLabel(timeoutBranch), $"a timeout branch with the condition '{Clip(condition)}' is not supported");
                var block = PropertyByKey(timeoutBranch, "Then") ?? Fail<XElement>(timeoutBranch, "the timeout branch has no Then");
                var activities = ActivitiesOf(block).ToList();
                if (activities.Count == 0 || Kind(activities[0]) != "Wait")
                    throw new ShapeException(ElementLabel(timeoutBranch), "the timeout branch must start with a Postpone");
                timeout = ((WaitStep)Parse(activities[0], $"{timeoutPath}/then/0")!).Timeout;
                var rest = ParseActivities(activities.Skip(1), $"{timeoutPath}/then", strict: false);
                onTimeout = rest.Count == 0 ? null : new SequenceStep($"{timeoutPath}/then", DisplayName(block), rest);
            }

            Use(branches.Count == 2 ? "Shape:WaitTimeoutBranch" : "Shape:WaitBranch");
            var steps = new List<WorkflowStep> { new WaitStep(conditionPath, display ?? DisplayName(conditionBranch), until, timeout, onTimeout) };
            if (then is not null)
                steps.Add(then);
            return new SequenceStep(path, display, steps);
        }

        /// <summary>
        /// The <c>Description</c> of a branch when it is an <c>x:String</c> (the business rule of the parity export); null for
        /// <c>x:Null</c>, none, or any other shape (read only: the description never changes what the step does).
        /// </summary>
        private static string? DescriptionOf(XElement branch) =>
            PropertyByKey(branch, "Description") is { } description && description.Name == XName.Get("String", ActivityCatalog.XamlNs) && !description.HasElements
                ? description.Value
                : null;

        /// <summary>The block (<c>Then</c>, <c>Else</c>) of a branch as a sequence of its steps.</summary>
        private WorkflowStep? BlockOf(XElement branch, string key, string branchPath)
        {
            var block = PropertyByKey(branch, key);
            if (block is null || block.Name == XName.Get("Null", ActivityCatalog.XamlNs))
                return null;
            var blockPath = $"{branchPath}/{key.ToLowerInvariant()}";
            return Kind(block) is "Composite" or "Sequence"
                ? new SequenceStep(blockPath, DisplayName(block), ParseActivities(ActivitiesOf(block), blockPath, strict: false))
                : new SequenceStep(blockPath, null, ParseActivities([block], blockPath, strict: false));
        }
    }
}
