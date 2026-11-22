namespace Xrmd.Workflows.Xaml;

/// <summary>An input or output argument of a process, read from the XAML's <c>x:Members</c> (actions, W14).</summary>
/// <param name="ClrType">The full CLR type name, e.g. <c>System.String</c> or <c>Microsoft.Xrm.Sdk.EntityReference</c>.</param>
/// <param name="EntityTarget">The entity logical name an <c>EntityReference</c> or <c>Entity</c> argument targets, if declared
/// (<c>ArgumentEntityAttribute</c>; an empty value is null).</param>
public sealed record WorkflowArgument(string Name, string ClrType, bool IsInput, bool Required, string? EntityTarget)
{
    /// <summary>
    /// <c>ArgumentTargetAttribute</c> True: the bound record of an action (its <c>Target</c> input, an <c>EntityReference</c> of
    /// <see cref="EntityTarget"/>; seen in the parity export).
    /// </summary>
    public bool IsTarget { get; init; }

    /// <summary>The <c>ArgumentDescriptionAttribute</c>, null when absent.</summary>
    public string? Description { get; init; }
}

/// <summary>A parsed classic process: its step tree and the arguments it declares.</summary>
public sealed record WorkflowDefinition(int Category, string? PrimaryEntity, IReadOnlyList<WorkflowArgument> Arguments, WorkflowStep Root)
{
    /// <summary>
    /// Members of <c>x:Members</c> that are not input or output arguments (path <c>x:Members/{name}</c>).
    /// </summary>
    public IReadOnlyList<UnsupportedStep> UnsupportedMembers { get; init; } = [];

    /// <summary>
    /// Every <see cref="UnsupportedStep"/>: the <see cref="UnsupportedMembers"/>, then those of the tree, depth-first.
    /// A definition with any is not runnable (W4).
    /// </summary>
    public IReadOnlyList<UnsupportedStep> Unsupported => UnsupportedMembers.Concat(Root.DepthFirst().OfType<UnsupportedStep>()).ToList();
}

// ---- steps ------------------------------------------------------------------------------------------------------------

/// <summary>
/// One node of the step tree. <see cref="Path"/> is the index path of the XAML activity the step comes from
/// (<c>0</c> is the workflow body, <c>0/2</c> its third activity, and so on); <see cref="DisplayName"/> is the
/// activity's <c>DisplayName</c>. Together (<see cref="Label"/>) they name the step in errors and the import log.
/// </summary>
public abstract record WorkflowStep(string Path, string? DisplayName)
{
    public string Label => DisplayName is null ? Path : $"{Path} ({DisplayName})";

    /// <summary>The steps this step contains directly, in order.</summary>
    public virtual IEnumerable<WorkflowStep> Children => [];

    /// <summary>This step and every step below it, depth-first, in document order (iterative: any depth).</summary>
    public IEnumerable<WorkflowStep> DepthFirst()
    {
        var stack = new Stack<WorkflowStep>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var step = stack.Pop();
            yield return step;
            foreach (var child in step.Children.Reverse())
                stack.Push(child);
        }
    }
}

public sealed record SequenceStep(string Path, string? DisplayName, IReadOnlyList<WorkflowStep> Steps) : WorkflowStep(Path, DisplayName)
{
    public override IEnumerable<WorkflowStep> Children => Steps;
}

/// <summary>Creates a record. <see cref="Variable"/> names it for later steps (<see cref="RecordRef.Created"/>).</summary>
public sealed record CreateStep(string Path, string? DisplayName, string Entity, IReadOnlyDictionary<string, ValueExpr> Values, string Variable)
    : WorkflowStep(Path, DisplayName);

public sealed record UpdateStep(string Path, string? DisplayName, RecordRef Record, IReadOnlyDictionary<string, ValueExpr> Values)
    : WorkflowStep(Path, DisplayName);

/// <summary>Assigns a record; <see cref="Owner"/> evaluates to an <see cref="EntityRefValue"/> (systemuser or team).</summary>
public sealed record AssignStep(string Path, string? DisplayName, RecordRef Record, ValueExpr Owner) : WorkflowStep(Path, DisplayName);

public sealed record SetStateStep(string Path, string? DisplayName, RecordRef Record, int State, int Status) : WorkflowStep(Path, DisplayName);

/// <summary>Creates an email from <see cref="Values"/> and sends it. <see cref="CreatedEmailVariable"/> names the email when later steps use it.</summary>
public sealed record SendEmailStep(string Path, string? DisplayName, string? CreatedEmailVariable, IReadOnlyDictionary<string, ValueExpr> Values)
    : WorkflowStep(Path, DisplayName);

/// <summary>"Check condition": the first branch whose condition holds runs, else <see cref="Otherwise"/> (if any).</summary>
public sealed record ConditionStep(string Path, string? DisplayName, IReadOnlyList<ConditionBranch> Branches, WorkflowStep? Otherwise)
    : WorkflowStep(Path, DisplayName)
{
    public override IEnumerable<WorkflowStep> Children =>
        Otherwise is null ? Branches.Select(b => b.Then) : Branches.Select(b => b.Then).Append(Otherwise);
}

public sealed record ConditionBranch(ConditionExpr When, WorkflowStep Then)
{
    /// <summary>
    /// The designer's description of the condition (the branch's <c>x:String</c> <c>Description</c> property; the business rule of the
    /// parity export writes it on each branch), null when the XAML has none or <c>x:Null</c> (plan 2.21, Task 3).
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// A wait (background only, W10). The run suspends until <see cref="Until"/> holds or, when <see cref="Timeout"/> is set,
/// until that date. With both: whichever comes first. When the condition is met the run continues with the steps
/// after this one in its <see cref="SequenceStep"/>; when the timeout comes first <see cref="OnTimeout"/> runs (if any)
/// and the rest of that enclosing sequence is skipped. The parser always wraps a wait with a timeout branch in its own
/// sequence, so the steps after the whole wait block still run in both cases.
/// </summary>
public sealed record WaitStep(string Path, string? DisplayName, ConditionExpr? Until, ValueExpr? Timeout, WorkflowStep? OnTimeout)
    : WorkflowStep(Path, DisplayName)
{
    public override IEnumerable<WorkflowStep> Children => OnTimeout is null ? [] : [OnTimeout];
}

/// <summary>"Stop workflow": succeeded (<see cref="Canceled"/> false) or canceled with <see cref="Message"/> (W11).</summary>
public sealed record StopStep(string Path, string? DisplayName, bool Canceled, ValueExpr? Message) : WorkflowStep(Path, DisplayName);

public sealed record ChildWorkflowStep(string Path, string? DisplayName, Guid WorkflowId, RecordRef Record) : WorkflowStep(Path, DisplayName);

/// <summary>"Perform action" (W13): runs <see cref="Message"/>; its outputs are available as <see cref="Variable"/>.</summary>
public sealed record ActionStep(string Path, string? DisplayName, string Message, RecordRef? Target, IReadOnlyDictionary<string, ValueExpr> Inputs, string Variable)
    : WorkflowStep(Path, DisplayName);

/// <summary>
/// A custom workflow activity (W17). <see cref="Inputs"/> maps the activity's input property names to values;
/// <see cref="Outputs"/> maps its output property names to the variable names later steps read (<see cref="VariableRef"/>).
/// </summary>
public sealed record CustomActivityStep(
    string Path, string? DisplayName, string TypeName, string AssemblyName,
    IReadOnlyDictionary<string, ValueExpr> Inputs, IReadOnlyDictionary<string, string> Outputs)
    : WorkflowStep(Path, DisplayName);

/// <summary>Sets an output argument of an action (category 3, W14) to a value.</summary>
public sealed record AssignOutputStep(string Path, string? DisplayName, string Argument, ValueExpr Value) : WorkflowStep(Path, DisplayName);

/// <summary>Business rule "set field value" / "set default value" (<see cref="DefaultOnly"/>) / "clear field value" (W15).</summary>
public sealed record SetValueStep(string Path, string? DisplayName, string Attribute, ValueExpr Value, bool DefaultOnly) : WorkflowStep(Path, DisplayName);

/// <summary>Business rule "show error message" (W15).</summary>
public sealed record ShowErrorStep(string Path, string? DisplayName, ValueExpr Message) : WorkflowStep(Path, DisplayName)
{
    /// <summary>The column the message is shown on (<c>SetMessage</c>'s <c>ControlId</c>), null when the XAML names none.</summary>
    public string? Attribute { get; init; }
}

/// <summary>A business rule action that only affects forms (show/hide, enable/disable, requirement level): a no-op on the server (W15).</summary>
public sealed record ClientOnlyStep(string Path, string? DisplayName, string Kind) : WorkflowStep(Path, DisplayName)
{
    /// <summary>The column the action is on (its <c>ControlId</c> argument), null when the XAML has none (plan 2.21, Task 4: the export).</summary>
    public string? Attribute { get; init; }

    /// <summary>
    /// The setting as the XAML writes it: <c>IsVisible</c> of <c>SetVisibility</c>, <c>IsReadOnly</c> of <c>SetDisplayMode</c>,
    /// <c>RequiredLevel</c> of <c>SetFieldRequiredLevel</c> (e.g. <c>False</c>, <c>2</c>); null when the XAML has none. Read only for
    /// the export (plan 2.21, Task 4): the server never applies it.
    /// </summary>
    public string? Setting { get; init; }
}

/// <summary>A XAML activity or expression xrmverse cannot run faithfully (W16, W20). <see cref="Element"/> names the XAML element.</summary>
public sealed record UnsupportedStep(string Path, string? DisplayName, string Element, string Reason) : WorkflowStep(Path, DisplayName);

// ---- records the steps act on ------------------------------------------------------------------------------------------

public abstract record RecordRef
{
    /// <summary>The record the process runs on.</summary>
    public sealed record Primary : RecordRef;

    /// <summary>The record a lookup column of the primary record points to (N:1).</summary>
    public sealed record Related(string LookupAttribute) : RecordRef;

    /// <summary>A record an earlier create step of this run created.</summary>
    public sealed record Created(string Variable) : RecordRef;
}

// ---- values ---------------------------------------------------------------------------------------------------------------

/// <summary>An entity reference written as a literal in the XAML (owner, lookup, email party).</summary>
public sealed record EntityRefValue(string LogicalName, Guid Id);

public abstract record ValueExpr
{
    /// <summary>
    /// A constant. <see cref="Type"/> is the workflow property type it was declared with (<c>String</c>, <c>Integer</c>,
    /// <c>Decimal</c>, <c>Double</c>, <c>Money</c>, <c>Boolean</c>, <c>DateTime</c>, <c>OptionSetValue</c>, <c>Guid</c>,
    /// <c>EntityReference</c>) or <c>Null</c>. <see cref="Value"/> is a <c>string</c>, <c>int</c>, <c>decimal</c> (Decimal,
    /// Money), <c>double</c>, <c>bool</c>, <c>DateTime</c> (UTC), <c>int</c> (OptionSetValue), <c>Guid</c>,
    /// <see cref="EntityRefValue"/> or null.
    /// </summary>
    public sealed record Literal(string Type, object? Value) : ValueExpr;

    /// <summary>A column of a record (the primary record, a related record or a created record).</summary>
    public sealed record AttributeOf(RecordRef Record, string Attribute) : ValueExpr
    {
        /// <summary>
        /// The <c>TargetType</c> the designer read the column as (a full CLR type name), when it declared one that needs no
        /// conversion: the column's value must already be of that type (a null value is null). A value of another type fails the
        /// run naming the column, its type and this one; the parser cannot check it, as it has no metadata. Null when the XAML
        /// declares no target type (<c>&lt;x:Null /&gt;</c> or none).
        /// </summary>
        public string? TargetType { get; init; }
    }

    /// <summary>
    /// A conversion a <c>GetEntityProperty</c> <c>TargetType</c> asks for (parity export, plan 2.14 Task 3b). Only two pairs
    /// exist; any other pair is an <see cref="UnsupportedStep"/>:
    /// <list type="bullet">
    /// <item><c>System.Guid</c> → <c>Microsoft.Xrm.Sdk.EntityReference</c>: the primary key of <see cref="Entity"/> as a reference
    /// to that record (OnCreate and OnUpdate: <c>xrmv_orderid</c> set into <c>regardingobjectid</c>).</item>
    /// <item><c>Microsoft.Xrm.Sdk.EntityReference</c> → <c>Microsoft.Xrm.Sdk.EntityCollection</c>: an activity party list with one
    /// <c>activityparty</c> whose <c>partyid</c> is the reference (OnUpdate: a contact lookup set into an email's <c>to</c>). A
    /// null reference is null.</item>
    /// </list>
    /// The operand's value must be of <see cref="From"/>; another type fails the run naming the pair.
    /// </summary>
    public sealed record Conversion(string From, string To, ValueExpr Operand, string? Entity = null) : ValueExpr;

    /// <summary>
    /// A designer arithmetic operator (<c>Add</c>, <c>Subtract</c>, <c>Multiply</c>, <c>Increment</c>, <c>Decrement</c>,
    /// <c>Set</c>) over its operands, in order.
    /// </summary>
    public sealed record Arithmetic(string Operator, IReadOnlyList<ValueExpr> Operands) : ValueExpr;

    /// <summary>String concatenation of the parts' text values (null parts add nothing).</summary>
    public sealed record Concat(IReadOnlyList<ValueExpr> Parts) : ValueExpr;

    /// <summary>The first operand whose value is not null (a dynamic value with a default).</summary>
    public sealed record FirstNonNull(IReadOnlyList<ValueExpr> Operands) : ValueExpr;

    /// <summary>The process execution time (UTC now of the run).</summary>
    public sealed record ExecutionTime : ValueExpr;

    /// <summary>A process argument or a variable an action or custom activity step wrote.</summary>
    public sealed record VariableRef(string Name) : ValueExpr;

    /// <summary>
    /// An output of a "perform action" step (W13), written <c>[{result}("{output}")]</c> (assumed shape, catalog entry
    /// <c>Vb:ActionOutput</c>, checklist 1g): the value the action's response holds under <see cref="Output"/>, null when the response
    /// has none. <see cref="Variable"/> is the step's <see cref="ActionStep.Variable"/>.
    /// </summary>
    public sealed record ActionOutput(string Variable, string Output) : ValueExpr;

    /// <summary>
    /// <c>DirectCast(operand, ClrType)</c> as the designer writes it for an output argument (parity export): the operand's value
    /// unchanged, which must be of <see cref="ClrType"/> (a full CLR type name, e.g. <c>System.Decimal</c>); a null operand is the
    /// default of a value type (0, false, ...) and null for a reference type, as in Visual Basic. A value of another type fails the
    /// run (VB throws <c>InvalidCastException</c>).
    /// </summary>
    public sealed record Cast(string ClrType, ValueExpr Operand) : ValueExpr;

    /// <summary>
    /// A calculated column function (<c>ADDDAYS</c>, <c>DIFFINDAYS</c>, <c>CONCAT</c>, <c>TRIMLEFT</c>, ...) over its arguments in
    /// order (plan 2.12a, assumed shape <c>EvaluateExpression:&lt;function&gt;</c>). Only <see cref="XamlWorkflowParser.ParseCalculated"/>
    /// produces it: in a process the operator is not supported.
    /// </summary>
    public sealed record FunctionCall(string Name, IReadOnlyList<ValueExpr> Args) : ValueExpr;
}

// ---- conditions -----------------------------------------------------------------------------------------------------------

public abstract record ConditionExpr
{
    /// <summary>
    /// <see cref="Operator"/> is a <c>ConditionOperator</c> name (<c>Equal</c>, <c>NotEqual</c>, <c>Null</c>, <c>NotNull</c>,
    /// <c>Contains</c>, <c>DoesNotContain</c>, <c>BeginsWith</c>, <c>EndsWith</c>, <c>GreaterThan</c>, <c>GreaterEqual</c>,
    /// <c>LessThan</c>, <c>LessEqual</c>). <see cref="Right"/> is null for the unary operators. "Between" is parsed as
    /// <c>GreaterEqual</c> and <c>LessEqual</c> joined by <see cref="And"/>.
    /// </summary>
    public sealed record Compare(string Operator, ValueExpr Left, ValueExpr? Right) : ConditionExpr;

    public sealed record And(ConditionExpr Left, ConditionExpr Right) : ConditionExpr;

    public sealed record Or(ConditionExpr Left, ConditionExpr Right) : ConditionExpr;

    public sealed record Not(ConditionExpr Operand) : ConditionExpr;
}
