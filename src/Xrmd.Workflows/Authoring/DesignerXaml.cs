using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// Writes the XAML the Dataverse classic designer writes (plan 2.21, A8 and A9) for a resolved <see cref="Process"/>: the document
/// skeleton and namespaces of the real exports (<c>tests/fixtures/workflows/real</c>), each step and value in its export shape (or,
/// for a shape no export showed, in the export's designer style; such shapes are reported as assumed by the compiler), the step names
/// and variables allocated as the designer does. The text is UTF-8 without BOM, CRLF line ends and two-space indentation, without a
/// final line end, exactly as the exports; escaping is <see cref="XmlWriter"/>'s.
/// </summary>
/// <remarks>
/// The class is split by concern: this file holds the skeleton, the namespaces, the text and the element helpers;
/// <c>DesignerXaml.Steps.cs</c> the steps and their names; <c>DesignerXaml.Values.cs</c> the values, the conditions and the variables;
/// <c>DesignerXaml.Model.cs</c> the resolved model the compiler hands over.
/// </remarks>
internal sealed partial class DesignerXaml
{
    // ---- namespaces (copied from the real exports) ---------------------------------------------------------------------------

    private const string SdkVersion = "Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    private const string ClrVersion = "Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    internal static readonly XNamespace Wf = ActivityCatalog.ActivitiesNs;
    internal static readonly XNamespace X = ActivityCatalog.XamlNs;
    internal static readonly XNamespace Mcwc = $"clr-namespace:Microsoft.Crm.Workflow.ClientActivities;assembly=Microsoft.Crm.Workflow, {SdkVersion}";
    internal static readonly XNamespace Mcwo = $"clr-namespace:Microsoft.Crm.Workflow.ObjectModel;assembly=Microsoft.Crm, {SdkVersion}";
    internal static readonly XNamespace Mva = "clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    internal static readonly XNamespace Mxs = $"clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, {SdkVersion}";
    internal static readonly XNamespace Mxsq = $"clr-namespace:Microsoft.Xrm.Sdk.Query;assembly=Microsoft.Xrm.Sdk, {SdkVersion}";
    internal static readonly XNamespace Mxsw = $"clr-namespace:Microsoft.Xrm.Sdk.Workflow;assembly=Microsoft.Xrm.Sdk.Workflow, {SdkVersion}";
    internal static readonly XNamespace Mxswa = $"clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, {SdkVersion}";
    internal static readonly XNamespace S = $"clr-namespace:System;assembly=mscorlib, {ClrVersion}";
    internal static readonly XNamespace Scg = $"clr-namespace:System.Collections.Generic;assembly=mscorlib, {ClrVersion}";
    internal static readonly XNamespace Sco = $"clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, {ClrVersion}";
    internal static readonly XNamespace Srs = $"clr-namespace:System.Runtime.Serialization;assembly=System.Runtime.Serialization, {ClrVersion}";
    internal static readonly XNamespace This = "clr-namespace:";

    /// <summary>The <c>mva:VisualBasic.Settings</c> text of every export.</summary>
    private const string VisualBasicSettings = "Assembly references and imported namespaces for internal implementation";

    /// <summary>The assembly of the designer's referenced step and expression activities.</summary>
    private const string CrmWorkflow = $", Microsoft.Crm.Workflow, {SdkVersion}";

    private readonly Process _process;

    /// <summary>The designer step counter (A8): pre-order over the designer steps of the process.</summary>
    private int _counter;

    /// <summary>The optional namespaces the body uses: a namespace is declared only when the XAML uses it, as the export does.</summary>
    private bool _usesQuery, _usesCollections, _usesArguments, _usesClientActivities, _usesObjectModel;

    /// <summary>
    /// The business rule action names (A8): the name of the first branch of the condition that holds the action, and the count of
    /// the rule's actions so far (the export's <c>SetMessageStep_ConditionBranchStep2_2</c>).
    /// </summary>
    private string? _ruleBranch;

    private int _ruleActions;

    /// <summary>The namespaces of custom workflow activities, with their prefixes.</summary>
    private readonly SortedDictionary<string, XNamespace> _custom = new(StringComparer.Ordinal);

    /// <summary>The designer name of each step with a YAML id, as written (the variables of its outputs are named after it).</summary>
    private readonly Dictionary<string, string> _stepNames = new(StringComparer.Ordinal);

    /// <summary>The shapes written that no real export showed and the catalog does not name (review M1, M7).</summary>
    private readonly SortedSet<string> _assumed = new(StringComparer.Ordinal);

    private DesignerXaml(Process process) => _process = process;

    /// <summary>The XAML of <paramref name="process"/>.</summary>
    public static string Write(Process process) => WriteWithShapes(process).Xaml;

    /// <summary>
    /// The XAML of <paramref name="process"/> and the shapes it holds that no real export showed, beyond the catalog's: stable short
    /// names (<c>customActivity:&lt;type&gt;</c>, <c>createdKey:id</c>, <c>sendEmail:assignToId</c>, <c>sendEmail:noPersist</c>,
    /// <c>stop:succeeded</c>, <c>stop:message</c>, <c>literal:&lt;type&gt;</c>).
    /// </summary>
    public static (string Xaml, IReadOnlySet<string> Assumed) WriteWithShapes(Process process)
    {
        var writer = new DesignerXaml(process);
        var body = writer.StepList(process.Steps);
        return (Text(writer.Document(body)), writer._assumed);
    }

    private void Assume(string shape) => _assumed.Add(shape);

    // ---- skeleton -------------------------------------------------------------------------------------------------------------

    private XElement Document(IEnumerable<XElement> body)
    {
        // The export's business rule has the x:Class of the empty id (Newbusinessrule); a workflow and an action their own id.
        var name = $"XrmWorkflow{(_process.Category == 2 ? Guid.Empty : _process.Id):N}";
        var arguments = Arguments().ToList();
        _usesArguments = arguments.Count > 0;
        var target = arguments.FirstOrDefault(a => a.IsTarget);
        var root = new XElement(Wf + "Activity", new XAttribute(X + "Class", name));
        foreach (var (prefix, ns) in Namespaces())
            root.Add(prefix.Length == 0 ? new XAttribute("xmlns", ns.NamespaceName) : new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));

        const string entities = "scg:IDictionary(x:String, mxs:Entity)";
        root.Add(
            new XElement(X + "Members",
                new XElement(X + "Property", new XAttribute("Name", "InputEntities"), new XAttribute("Type", $"InArgument({entities})")),
                new XElement(X + "Property", new XAttribute("Name", "CreatedEntities"), new XAttribute("Type", $"InArgument({entities})")),
                arguments.Select(Member)),
            new XElement(This + $"{name}.InputEntities", new XElement(Wf + "InArgument", TypeArguments(entities))),
            new XElement(This + $"{name}.CreatedEntities", new XElement(Wf + "InArgument", TypeArguments(entities))),
            // Export Recalculate: the bound record has its property element; the other arguments have none.
            target is null ? null : new XElement(This + $"{name}.{target.Name}", new XElement(Wf + "InArgument", TypeArguments(target.Type))),
            new XElement(Mva + "VisualBasic.Settings", VisualBasicSettings),
            new XElement(Mxswa + "Workflow", body));
        return root;
    }

    /// <summary>An argument of an action as <c>x:Members</c> declares it: name, XAML type, the export's attribute values.</summary>
    private sealed record MemberArgument(string Name, bool IsInput, string Type, bool Required, bool IsTarget, string Description, string Entity);

    /// <summary>
    /// The arguments of an action in the export's order (Recalculate): those of the file, then the generated <c>Target</c> of a bound
    /// action (an <c>EntityReference</c> of the primary table, required, described "Target Argument", A15).
    /// </summary>
    private IEnumerable<MemberArgument> Arguments()
    {
        if (_process.Category != 3)
            yield break;
        // A15: export Recalculate confirms Decimal arguments and the EntityReference Target; the other types are assumed, and so is
        // a global action (no Target).
        if (!_process.Bound)
            Assume("action:global");
        foreach (var a in _process.Arguments)
        {
            if (a.Type != PropertyType.Decimal && a.Type != PropertyType.EntityReference)
                Assume($"argument:{a.YamlType}");
            yield return new MemberArgument(a.Name, a.IsInput, a.Type.Xaml, a.Required, false, a.Description, a.Entity ?? "");
        }
        if (_process.Bound)
            yield return new MemberArgument("Target", true, PropertyType.EntityReference.Xaml, true, true, "Target Argument", _process.Entity);
    }

    /// <summary>An argument's <c>x:Property</c> with the five <c>mxsw:Argument*Attribute</c>s in the export's order.</summary>
    private static XElement Member(MemberArgument a)
    {
        static XElement Attribute(string name, string value) => new(Mxsw + name, new XAttribute("Value", value));
        static string Flag(bool value) => value ? "True" : "False";
        return new XElement(X + "Property",
            new XAttribute("Name", a.Name),
            new XAttribute("Type", $"{(a.IsInput ? "InArgument" : "OutArgument")}({a.Type})"),
            new XElement(X + "Property.Attributes",
                Attribute("ArgumentRequiredAttribute", Flag(a.Required)),
                Attribute("ArgumentTargetAttribute", Flag(a.IsTarget)),
                Attribute("ArgumentDescriptionAttribute", a.Description),
                Attribute("ArgumentDirectionAttribute", a.IsInput ? "Input" : "Output"),
                Attribute("ArgumentEntityAttribute", a.Entity)));
    }

    /// <summary>
    /// The declarations of the root, in the export's order (the default namespace, then by prefix): those of every export, and
    /// <c>mxsq</c> (a condition), <c>sco</c> (a condition or a wait), <c>mxsw</c> (arguments), <c>mcwc</c> and <c>mcwo</c> (business
    /// rule actions) and the custom activity namespaces only when used.
    /// </summary>
    private IEnumerable<(string Prefix, XNamespace Ns)> Namespaces()
    {
        var declared = new List<(string Prefix, XNamespace Ns)>
        {
            ("mva", Mva), ("mxs", Mxs), ("mxswa", Mxswa), ("s", S), ("scg", Scg), ("srs", Srs), ("this", This), ("x", X),
        };
        if (_usesQuery)
            declared.Add(("mxsq", Mxsq));
        if (_usesCollections)
            declared.Add(("sco", Sco));
        if (_usesArguments)
            declared.Add(("mxsw", Mxsw));
        if (_usesClientActivities)
            declared.Add(("mcwc", Mcwc));
        if (_usesObjectModel)
            declared.Add(("mcwo", Mcwo));
        foreach (var (prefix, ns) in _custom)
            declared.Add((prefix, ns));
        return declared.OrderBy(d => d.Prefix, StringComparer.Ordinal).Prepend(("", Wf));
    }

    /// <summary>The prefix of a custom activity namespace: the initials of its CLR namespace, as the XAML writer derives them (assumed).</summary>
    private XNamespace CustomNamespace(string clrNamespace, string assembly)
    {
        XNamespace ns = $"clr-namespace:{clrNamespace};assembly={assembly}";
        if (_custom.FirstOrDefault(c => c.Value == ns) is { Key: { } existing })
            return _custom[existing];
        var initials = string.Concat(clrNamespace.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToLowerInvariant(part[0])));
        if (initials.Length == 0 || !XmlConvert.IsStartNCNameChar(initials[0]))
            initials = "c" + initials;
        string[] taken = ["mcwc", "mcwo", "mva", "mxs", "mxsq", "mxsw", "mxswa", "s", "scg", "sco", "srs", "this", "x", "xmlns", "xml"];
        var prefix = initials;
        for (var n = 1; taken.Contains(prefix, StringComparer.Ordinal) || _custom.ContainsKey(prefix); n++)
            prefix = initials + n.ToString(CultureInfo.InvariantCulture);
        _custom[prefix] = ns;
        return ns;
    }

    // ---- text -----------------------------------------------------------------------------------------------------------------

    /// <summary>The export's text: two-space indentation, CRLF, no declaration, no final line end, line breaks inside values entitized.</summary>
    private static string Text(XElement root)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Entitize,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(new StringWriter(sb, CultureInfo.InvariantCulture), settings))
            root.WriteTo(writer);
        return sb.ToString();
    }

    // ---- element helpers ------------------------------------------------------------------------------------------------------

    private static XAttribute TypeArguments(string type) => new(X + "TypeArguments", type);

    private static XAttribute Key(string key) => new(X + "Key", key);

    private static XAttribute DisplayName(string name) => new("DisplayName", name);

    /// <summary>A VB expression attribute or text: <c>[expression]</c>.</summary>
    private static string Expr(string expression) => $"[{expression}]";

    /// <summary>An <c>ActivityReference</c> to a designer activity of <c>Microsoft.Crm.Workflow.Activities</c>.</summary>
    private static XElement Reference(string type, string displayName, params object?[] content) =>
        new(Mxswa + "ActivityReference",
            new XAttribute("AssemblyQualifiedName", ActivityCatalog.CrmActivities + type + CrmWorkflow),
            DisplayName(displayName), content);

    private static XElement Arguments(params object?[] arguments) => new(Mxswa + "ActivityReference.Arguments", arguments);

    private static XElement Properties(params object?[] properties) => new(Mxswa + "ActivityReference.Properties", properties);

    private static XElement In(string type, string key, object? value) => new(Wf + "InArgument", TypeArguments(type), Key(key), value);

    private static XElement Out(string type, string key, string variable) => new(Wf + "OutArgument", TypeArguments(type), Key(key), Expr(variable));

    private static XElement XNull(string key) => new(X + "Null", Key(key));

    /// <summary>The <c>sco:Collection</c> of variables or activities of a step wrapper (empty: a self-closing element).</summary>
    private XElement Collection(string type, string key, IEnumerable<XElement> items)
    {
        _usesCollections = true;
        return new XElement(Sco + "Collection", TypeArguments(type), Key(key), items);
    }

    /// <summary>A type literal (<c>mxswa:ReferenceLiteral</c>) of <paramref name="type"/>, or of <c>x:Null</c>.</summary>
    private static XElement TypeLiteral(PropertyType? type) => type is null
        ? new XElement(Mxswa + "ReferenceLiteral", TypeArguments("s:Type"), new XElement(X + "Null"))
        : new XElement(Mxswa + "ReferenceLiteral", TypeArguments("s:Type"), new XAttribute("Value", type.Xaml));

    /// <summary>The <c>.TargetType</c> property element of a <c>Get/SetEntityProperty</c>.</summary>
    private static XElement TargetTypeProperty(XName owner, PropertyType? type) =>
        new(owner.Namespace + (owner.LocalName + ".TargetType"), new XElement(Wf + "InArgument", TypeArguments("s:Type"), TypeLiteral(type)));

    /// <summary><c>Assign</c> of the designer's bookkeeping (<c>x:TypeArguments</c>, <c>To</c>, <c>Value</c>).</summary>
    private static XElement Assign(string type, string to, string value) =>
        new(Wf + "Assign", TypeArguments(type), new XAttribute("To", Expr(to)), new XAttribute("Value", Expr(value)));

    private static XElement Variable(string type, string name, string? @default = null) =>
        new(Wf + "Variable", TypeArguments(type), @default is null ? null : new XAttribute("Default", @default), new XAttribute("Name", name));

    /// <summary>A designer step: <c>Sequence</c> with <c>DisplayName</c> and its <c>Sequence.Variables</c> (A9).</summary>
    private static XElement DesignerSequence(string displayName, IReadOnlyList<XElement> variables, IEnumerable<XElement> activities) =>
        new(Wf + "Sequence", DisplayName(displayName),
            variables.Count == 0 ? null : new XElement(Wf + "Sequence.Variables", variables),
            activities);
}

/// <summary>Visual Basic literals as the designer writes them inside <c>[...]</c>.</summary>
internal static class Vb
{
    /// <summary>A VB string literal: quoted, every <c>"</c> doubled. XML escaping is the writer's.</summary>
    public static string String(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary><c>[New Object() { a, b }]</c>, the <c>Parameters</c> of the designer's expression activities.</summary>
    public static string Objects(IEnumerable<string> items) => $"[New Object() {{ {string.Join(", ", items)} }}]";
}
