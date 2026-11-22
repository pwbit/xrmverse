using System.Xml.Linq;

namespace Xrmd.Workflows.Xaml;

public enum CatalogStatus
{
    /// <summary>xrmverse runs it.</summary>
    Supported,

    /// <summary>A business rule action that only affects forms: accepted and a no-op on the server (W15).</summary>
    ClientOnly,

    /// <summary>Known, but not run: a process using it becomes an <see cref="UnsupportedStep"/>.</summary>
    Unsupported,
}

/// <summary>
/// One XAML shape the parser knows. <see cref="Element"/> is an element name in <c>{namespace}Local</c> form (the
/// namespace normalized by <see cref="ActivityCatalog.NormalizeNamespace"/>), the type name of an
/// <c>ActivityReference</c>'s <c>AssemblyQualifiedName</c>, or an expression shape (<c>EvaluateExpression:Add</c>).
/// <see cref="Source"/> is <c>learn</c> (a public type name of the Learn reference that the parity exports do not use), <c>export</c> (seen in a parity
/// export: <c>tests/fixtures/solutions/RealExports/xrmdWfTest_1_0_0_2.zip</c>, whose XAML files are in <c>tests/fixtures/workflows/real</c>,
/// or, for calculated column definitions, <c>xrmCalcTest_1_0_0_4.zip</c>, whose XAML files are in <c>tests/fixtures/calculated/real</c>)
/// or <c>assumed</c> (the shape we expect the classic designer to emit; each one is a checklist item 1g/1h until an export
/// confirms it, W25).
/// </summary>
public sealed record CatalogEntry(string Element, string Kind, CatalogStatus Status, string Source, string? Reason = null);

/// <summary>The inventory of XAML activities and expression shapes of classic processes (W16, W25).</summary>
public static class ActivityCatalog
{
    public const string ActivitiesNs = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
    public const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string SdkActivitiesNs = "clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow";
    public const string SdkWorkflowNs = "clr-namespace:Microsoft.Xrm.Sdk.Workflow;assembly=Microsoft.Xrm.Sdk.Workflow";
    public const string ObjectModelNs = "clr-namespace:System.Collections.ObjectModel;assembly=mscorlib";

    /// <summary>The namespace of the business rule activities (<c>mcwc</c> in the parity export).</summary>
    public const string ClientActivitiesNs = "clr-namespace:Microsoft.Crm.Workflow.ClientActivities;assembly=Microsoft.Crm.Workflow";

    /// <summary>The prefix of the type names of the step wrappers the designer references through <c>ActivityReference</c>.</summary>
    public const string CrmActivities = "Microsoft.Crm.Workflow.Activities.";

    private const string Learn = "learn";
    private const string Assumed = "assumed";
    private const string Export = "export";

    /// <summary>The reason of the calculated column operators (<c>Function</c>, <c>CalculatedArithmetic</c> and <c>CalculatedTime</c> kinds).</summary>
    private const string CalculatedOnly = "calculated column definitions only";

    /// <summary>The note of an entry the 2.12a parity export confirms only in calculated column definitions, not in a process.</summary>
    private const string ExportCalculated = "export evidence from calculated column definitions only (2.12a)";

    /// <summary>An operator read only in a calculated column definition: a process using one has an unsupported step.</summary>
    public static bool IsCalculatedOnly(CatalogEntry entry) => entry.Kind is "Function" or "CalculatedArithmetic" or "CalculatedTime";

    /// <summary>The reason of the rollup aggregate operators (<c>RollupAggregate</c> kind).</summary>
    private const string RollupOnly = "rollup column definitions only";

    /// <summary>
    /// An aggregate operator of a rollup column definition (plan 2.12b): read only as the aggregate of one; a process or a calculated
    /// column using one has an unsupported step.
    /// </summary>
    public static bool IsRollupOnly(CatalogEntry entry) => entry.Kind == "RollupAggregate";

    public static IReadOnlyList<CatalogEntry> Entries { get; } = BuildEntries();

    private static readonly Dictionary<string, CatalogEntry> ByElement =
        Entries.ToDictionary(e => e.Element, StringComparer.Ordinal);

    /// <summary>
    /// The status and kind of an element. For <c>ActivityReference</c>, pass its <c>AssemblyQualifiedName</c>: the
    /// referenced type decides. An element of another <c>clr-namespace</c> is a custom workflow activity (W17). Anything
    /// else is <see cref="CatalogStatus.Unsupported"/> with kind <c>Unknown</c>.
    /// </summary>
    public static (CatalogStatus Status, string Kind) Lookup(XName element, string? assemblyQualifiedName)
    {
        var entry = Find(element, assemblyQualifiedName);
        if (entry is not null)
            return (entry.Status, entry.Kind);
        if (assemblyQualifiedName is null && IsCustomActivityNamespace(element.NamespaceName))
            return (CatalogStatus.Supported, "CustomActivity");
        return (CatalogStatus.Unsupported, "Unknown");
    }

    /// <summary>The catalog entry of an element or referenced type, or null.</summary>
    public static CatalogEntry? Find(XName element, string? assemblyQualifiedName)
    {
        if (assemblyQualifiedName is not null && NormalizeNamespace(element.NamespaceName) == SdkActivitiesNs && element.LocalName == "ActivityReference")
            return ByElement.GetValueOrDefault(TypeNameOf(assemblyQualifiedName));
        return ByElement.GetValueOrDefault(XName.Get(element.LocalName, NormalizeNamespace(element.NamespaceName)).ToString());
    }

    /// <summary>The catalog entry of an expression shape (<c>EvaluateExpression:Add</c>, <c>EvaluateCondition:Equal</c>, ...), or null.</summary>
    public static CatalogEntry? FindShape(string shape) => ByElement.GetValueOrDefault(shape);

    /// <summary>The type name of an assembly-qualified name (the part before the first comma), trimmed.</summary>
    public static string TypeNameOf(string assemblyQualifiedName)
    {
        var comma = assemblyQualifiedName.IndexOf(',');
        return (comma < 0 ? assemblyQualifiedName : assemblyQualifiedName[..comma]).Trim();
    }

    /// <summary>
    /// A <c>clr-namespace:N;assembly=A, Version=..., Culture=..., PublicKeyToken=...</c> URI reduced to
    /// <c>clr-namespace:N;assembly=A</c> (the simple assembly name), so namespaces match by URI whatever the version and
    /// whatever prefix the file declares. Other URIs are returned unchanged.
    /// </summary>
    public static string NormalizeNamespace(string uri)
    {
        if (!uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
            return uri;
        var semicolon = uri.IndexOf(';');
        if (semicolon < 0)
            return uri.Trim();
        var ns = uri[..semicolon].Trim();
        var rest = uri[(semicolon + 1)..].Trim();
        if (!rest.StartsWith("assembly=", StringComparison.Ordinal))
            return ns;
        var assembly = rest["assembly=".Length..];
        var comma = assembly.IndexOf(',');
        return $"{ns};assembly={(comma < 0 ? assembly : assembly[..comma]).Trim()}";
    }

    /// <summary>
    /// The CLR namespace and full assembly name of a <c>clr-namespace:</c> URI, or null for other URIs.
    /// </summary>
    public static (string ClrNamespace, string AssemblyName)? ParseClrNamespace(string uri)
    {
        if (!uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
            return null;
        var semicolon = uri.IndexOf(';');
        var ns = (semicolon < 0 ? uri["clr-namespace:".Length..] : uri["clr-namespace:".Length..semicolon]).Trim();
        var assembly = "";
        if (semicolon >= 0)
        {
            var rest = uri[(semicolon + 1)..].Trim();
            if (rest.StartsWith("assembly=", StringComparison.Ordinal))
                assembly = rest["assembly=".Length..].Trim();
        }
        return (ns, assembly);
    }

    /// <summary>
    /// A <c>clr-namespace</c> that is not one of the platform's (the workflow SDK, the core SDK, the BCL): the namespace
    /// of a custom workflow activity.
    /// </summary>
    public static bool IsCustomActivityNamespace(string uri)
    {
        var parsed = ParseClrNamespace(uri);
        if (parsed is null)
            return false;
        var simple = NormalizeNamespace(uri);
        var assembly = simple[(simple.IndexOf("assembly=", StringComparison.Ordinal) + "assembly=".Length)..];
        string[] platform = ["Microsoft.Xrm.Sdk", "Microsoft.Xrm.Sdk.Workflow", "Microsoft.Crm.Sdk.Proxy", "mscorlib", "System",
            "System.Core", "System.Activities", "System.Runtime.Serialization", "Microsoft.Crm.Workflow", "Microsoft.Crm"];
        return assembly.Length > 0 && !platform.Contains(assembly, StringComparer.Ordinal);
    }

    private static List<CatalogEntry> BuildEntries()
    {
        static string Sdk(string local) => XName.Get(local, SdkActivitiesNs).ToString();
        static string Wf(string local) => XName.Get(local, ActivitiesNs).ToString();
        static string Crm(string type) => CrmActivities + type;
        static string Client(string local) => XName.Get(local, ClientActivitiesNs).ToString();

        return
        [
            // Microsoft.Xrm.Sdk.Workflow.Activities (type names from the Learn namespace page). Those the parity export uses are
            // `export` (plan 2.14, Task 9: RealExportXamlTests checks that the entries a parse of the export relies on are exactly these).
            new(Sdk("Workflow"), "Root", CatalogStatus.Supported, Export),
            new(Sdk("CreateEntity"), "Create", CatalogStatus.Supported, Export),                 // export: OnCreate
            new(Sdk("UpdateEntity"), "Update", CatalogStatus.Supported, Export),                 // export: Recalculate
            new(Sdk("AssignEntity"), "Assign", CatalogStatus.Supported, Learn),
            new(Sdk("SetState"), "SetState", CatalogStatus.Supported, Learn),
            new(Sdk("SendEmail"), "SendEmail", CatalogStatus.Supported, Export),                 // export: OnUpdate
            new(Sdk("StartChildWorkflow"), "ChildWorkflow", CatalogStatus.Supported, Export),    // export: OnCreate
            new(Sdk("Postpone"), "Wait", CatalogStatus.Supported, Learn),
            new(Sdk("GetEntityProperty"), "GetValue", CatalogStatus.Supported, Export),          // export: all
            new(Sdk("SetEntityProperty"), "SetValue", CatalogStatus.Supported, Export),          // export: all
            new(Sdk("RetrieveEntity"), "Retrieve", CatalogStatus.Supported, Learn),
            new(Sdk("ActivityReference"), "Reference", CatalogStatus.Supported, Export),
            new(Sdk("ReferenceLiteral"), "TypeLiteral", CatalogStatus.Supported, Export),
            new(Sdk("SendEmailFromTemplate"), "SendEmailFromTemplate", CatalogStatus.Unsupported, Learn,
                "sending an email from a template is not supported: xrmverse has no email templates"),
            new(Sdk("WebActivityReference"), "WebActivityReference", CatalogStatus.Unsupported, Learn,
                "web activity references are not supported"),
            new(Sdk("GetPrimaryEntity"), "GetPrimaryEntity", CatalogStatus.Unsupported, Learn,
                "GetPrimaryEntity is not part of the shapes xrmverse runs"),

            // System.Activities built-ins the designer uses (type names from Learn; all five are in the parity export, so `export`).
            new(Wf("Sequence"), "Sequence", CatalogStatus.Supported, Export),
            new(Wf("Assign"), "EntityBookkeeping", CatalogStatus.Supported, Export),
            new(Wf("Persist"), "Persist", CatalogStatus.Supported, Export),
            new(Wf("Variable"), "Variable", CatalogStatus.Supported, Export),
            new(Wf("TerminateWorkflow"), "Stop", CatalogStatus.Supported, Export),

            // Step wrappers and expression activities referenced through ActivityReference (classic designer output).
            new(Crm("Composite"), "Composite", CatalogStatus.Supported, Export),
            new(Crm("StepComposite"), "StepComposite", CatalogStatus.Supported, Assumed),
            new(Crm("ConditionSequence"), "Condition", CatalogStatus.Supported, Export),
            new(Crm("ConditionBranch"), "ConditionBranch", CatalogStatus.Supported, Export),
            new(Crm("EvaluateCondition"), "EvaluateCondition", CatalogStatus.Supported, Export),
            new(Crm("EvaluateLogicalCondition"), "EvaluateLogicalCondition", CatalogStatus.Supported, Export, ExportCalculated),
            new(Crm("EvaluateExpression"), "EvaluateExpression", CatalogStatus.Supported, Export),
            new(Crm("ConvertCrmXrmTypes"), "ConvertTypes", CatalogStatus.Supported, Assumed),
            new(Crm("ExecuteAction"), "ActionCall", CatalogStatus.Supported, Assumed),

            // Business rule activities (W15). The parity export writes elements of Microsoft.Crm.Workflow.ClientActivities; the
            // ActivityReference shapes below it are still assumed (no export has shown them).
            new(Client("SetAttributeValue"), "SetAttributeValue", CatalogStatus.Supported, Export),
            new(Client("SetMessage"), "ClientSetMessage", CatalogStatus.Supported, Export),
            new(Crm("SetDefaultValue"), "SetDefaultValue", CatalogStatus.Supported, Assumed),
            new(Crm("SetMessage"), "ShowError", CatalogStatus.Supported, Assumed),
            new(Crm("SetVisibility"), "SetVisibility", CatalogStatus.ClientOnly, Assumed),
            new(Crm("SetDisplayMode"), "SetDisplayMode", CatalogStatus.ClientOnly, Assumed),
            new(Crm("SetFieldRequiredLevel"), "SetFieldRequiredLevel", CatalogStatus.ClientOnly, Assumed),

            // Shapes of several elements together.
            new("Shape:EntityBookkeeping", "Assign New Entity(\"x\") to a #Temp key, its .Id, copies between entity dictionaries",
                CatalogStatus.Supported, Export),
            new("Shape:RecordKeys", "InputEntities(\"primaryEntity\"), InputEntities(\"related_<lookup>#<entity>\"), CreatedEntities(\"<step>\")",
                CatalogStatus.Supported, Export),
            new("Shape:StopException", "TerminateWorkflow Exception = New InvalidPluginExecutionException(OperationStatus.Canceled|Succeeded[, message]), Reason = the message",
                CatalogStatus.Supported, Export),
            new("Shape:DesignerSequence", "a Sequence with DisplayName and Sequence.Variables for each designer step",
                CatalogStatus.Supported, Export),
            new("Shape:WaitBranch", "ConditionSequence Wait=True with one ConditionBranch: wait for its condition, then its Then",
                CatalogStatus.Supported, Export),
            new("Shape:WaitTimeoutBranch", "ConditionSequence Wait=True with a second branch whose Then starts with Postpone",
                CatalogStatus.Supported, Assumed),
            new("Shape:ElseBranch", "a last ConditionBranch whose Condition is [True]: the otherwise block",
                CatalogStatus.Supported, Export),
            new("Shape:ChildInputs", "StartChildWorkflow InputParameters = a variable whose Default is an empty Dictionary(Of String, Object)",
                CatalogStatus.Supported, Export),
            new("Shape:OutputAssign", "Assign To = [<output argument>]: sets an action's output argument",
                CatalogStatus.Supported, Export),
            new("Shape:ArgumentAttributes", "x:Property.Attributes Argument{Required,Target,Description,Direction,Entity}Attribute",
                CatalogStatus.Supported, Export),
            new("Shape:LookupLiteral", "CreateCrmType { EntityReference, \"entity\", \"name\", <Guid literal variable>, \"Lookup\" }",
                CatalogStatus.Supported, Export),
            new("Shape:TargetType", "Get/SetEntityProperty and EvaluateExpression TargetType: the value's own type, a Guid primary key " +
                "as EntityReference, an EntityReference as EntityCollection (one activity party)", CatalogStatus.Supported, Export),
            new("Shape:StepLabel", "SetMessage.StepLabels with one mcwo:StepLabel (Description, LabelId, LanguageCode)",
                CatalogStatus.Supported, Export),
            new("Shape:CalculatedTargetType", "in a calculated column definition, a TargetType other than the value's own is the column's type, never another: it converts nothing",
                CatalogStatus.Supported, Export),
            // Plan 2.12b, ruling R3: the shapes of a rollup column definition, seen in the parity export xrmCalcTest_1_0_0_4.zip
            // (tests/fixtures/rollup/real).
            new("Shape:RollupRule", "a rollup column definition: Sequence RollupRuleStep1 holding the designer sequences Source, Target and Aggregate",
                CatalogStatus.Supported, Export),
            new("Shape:RollupHierarchy", "the Source variable HierarchicalRelationshipName: the hierarchical relationship a rollup column aggregates over, [Nothing] for none",
                CatalogStatus.Supported, Export),
            new("Shape:RollupRelatedLink", "mcwc:SetAttributeValue DisplayName=\"<related>.<lookup>.<relationship>\" Entity=CreatedEntities(\"relatedlinked_<relationship>#<lookup>#<related>#Temp\"): the related rows of a rollup column",
                CatalogStatus.Supported, Export),

            // EvaluateExpression operators (W16, and W14 for CustomOperationArguments).
            new("EvaluateExpression:CreateCrmType", "Literal", CatalogStatus.Supported, Export),
            new("EvaluateExpression:RetrieveCurrentTime", "ExecutionTime", CatalogStatus.Supported, Assumed),
            new("EvaluateExpression:SelectFirstNonNull", "FirstNonNull", CatalogStatus.Supported, Export),
            new("EvaluateExpression:CustomOperationArguments", "ArgumentRead", CatalogStatus.Supported, Export),
            new("EvaluateExpression:Add", "Arithmetic", CatalogStatus.Supported, Export),
            new("EvaluateExpression:Subtract", "Arithmetic", CatalogStatus.Supported, Export, ExportCalculated),
            new("EvaluateExpression:Multiply", "Arithmetic", CatalogStatus.Supported, Export, ExportCalculated),
            new("EvaluateExpression:Increment", "Arithmetic", CatalogStatus.Supported, Assumed),
            new("EvaluateExpression:Decrement", "Arithmetic", CatalogStatus.Supported, Assumed),
            new("EvaluateExpression:Set", "Arithmetic", CatalogStatus.Supported, Assumed),

            // Calculated column operators (plan 2.12a, ruling K2): read only in a calculated column definition; a process using one
            // has an unsupported step. All seen in the 2.12a parity export (xrmCalcTest_1_0_0_4.zip), which writes the functions in
            // this case (AddDays, DiffInDays, ...); the parser names them as the maker does (ADDDAYS, DIFFINDAYS, ...).
            new("EvaluateExpression:Divide", "CalculatedArithmetic", CatalogStatus.Supported, Export, CalculatedOnly),
            new("EvaluateExpression:Now", "CalculatedTime", CatalogStatus.Supported, Export, CalculatedOnly),
            .. new[]
            {
                "AddHours", "AddDays", "AddWeeks", "AddMonths", "AddYears", "SubtractHours", "SubtractDays", "SubtractWeeks",
                "SubtractMonths", "SubtractYears", "DiffInDays", "DiffInHours", "DiffInMinutes", "DiffInMonths", "DiffInWeeks",
                "DiffInYears", "Concat", "TrimLeft", "TrimRight",
            }.Select(f => new CatalogEntry("EvaluateExpression:" + f, "Function", CatalogStatus.Supported, Export, CalculatedOnly)),
            // Rollup column aggregate operators (plan 2.12b, ruling R3): Sum, Count and Maximum are in the parity export 1.0.0.4,
            // Minimum and Average in the export 1.0.0.5 (Task 7).
            .. new[] { "Sum", "Count", "Maximum", "Minimum", "Average" }
                .Select(op => new CatalogEntry("EvaluateExpression:" + op, "RollupAggregate", CatalogStatus.Supported, Export, RollupOnly)),
            new("Vb:String.Concat", "Concat", CatalogStatus.Supported, Assumed),
            new("Vb:DirectCast", "Cast to System.String or System.Decimal", CatalogStatus.Supported, Export),
            new("Vb:DirectCast:OtherPropertyTypes", "Cast to the CLR type of another workflow property type", CatalogStatus.Supported, Assumed),
            new("Vb:ActionOutput", "<perform action result>(\"<output>\"): an output of a perform action step", CatalogStatus.Supported, Assumed),

            // EvaluateCondition operators (ConditionOperator names, W16).
            .. new[] { "Null", "GreaterThan", "LessThan" }
                .Select(op => new CatalogEntry("EvaluateCondition:" + op, "ConditionOperator", CatalogStatus.Supported, Export)),
            // Equal and GreaterEqual: seen in the 2.12a parity export (calculated column conditions).
            .. new[] { "Equal", "GreaterEqual" }
                .Select(op => new CatalogEntry("EvaluateCondition:" + op, "ConditionOperator", CatalogStatus.Supported, Export, ExportCalculated)),
            .. new[]
            {
                "NotEqual", "NotNull", "Contains", "DoesNotContain", "BeginsWith", "EndsWith",
                "LessEqual", "Between", "NotBetween",
            }.Select(op => new CatalogEntry("EvaluateCondition:" + op, "ConditionOperator", CatalogStatus.Supported, Assumed)),

            // EvaluateLogicalCondition operators.
            new("EvaluateLogicalCondition:And", "LogicalOperator", CatalogStatus.Supported, Export, ExportCalculated),
            new("EvaluateLogicalCondition:Or", "LogicalOperator", CatalogStatus.Supported, Export, ExportCalculated),
            new("EvaluateLogicalCondition:Not", "LogicalOperator", CatalogStatus.Supported, Assumed),
        ];
    }
}
