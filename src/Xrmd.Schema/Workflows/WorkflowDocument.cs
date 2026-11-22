namespace Xrmd.Schema;

/// <summary>A position in the workflow YAML (1-based line and column), carried by every node for error messages.</summary>
public readonly record struct SourceSpan(int Line, int Column)
{
    public override string ToString() => $"line {Line}, column {Column}";
}

/// <summary>The category a workflow document compiles to (plan 2.21): a workflow (0), a business rule (2) or an action (3).</summary>
public enum WorkflowKind
{
    Workflow,
    BusinessRule,
    Action,
}

/// <summary>
/// The syntax tree of a workflow YAML file (plan 2.21, A1). It is only a tree: what it means at run time is always the
/// definition the 2.14 parser reads from the compiled XAML. Values are text until a compiler types them by metadata.
/// </summary>
/// <param name="Kind">The category.</param>
/// <param name="Id">The workflow id (optional in the file).</param>
/// <param name="Name">The workflow name (at most 100 characters).</param>
/// <param name="Description">The description.</param>
/// <param name="Entity">The primary table; for an action a table or <c>none</c> (a global action).</param>
/// <param name="Steps">The steps, in document order.</param>
public sealed record WorkflowDocument(WorkflowKind Kind, Guid? Id, string Name, string? Description, string Entity, IReadOnlyList<StepNode> Steps)
{
    /// <summary>The designer settings of a workflow (mode, scope, run as, ...).</summary>
    public WorkflowOptions Options { get; init; } = new();

    /// <summary>When the workflow starts (a workflow only).</summary>
    public TriggerNode? Trigger { get; init; }

    /// <summary>The name of the action message (an action only).</summary>
    public string? Message { get; init; }

    /// <summary>The arguments of an action.</summary>
    public IReadOnlyList<ArgumentNode> Arguments { get; init; } = [];

    /// <summary>Where the document starts.</summary>
    public SourceSpan Span { get; init; }

    /// <summary>Where the description's value is (null: no description), for messages about it.</summary>
    public SourceSpan? DescriptionSpan { get; init; }

    /// <summary>Where the action's message is (null: none, or not read from YAML), for messages about it.</summary>
    public SourceSpan? MessageSpan { get; init; }
}

/// <summary>The designer settings of a workflow document. A null or default value is the designer's default and is not written.</summary>
public sealed record WorkflowOptions
{
    /// <summary><c>realtime</c> or <c>background</c> (a workflow); null for a business rule and an action.</summary>
    public string? Mode { get; init; }

    /// <summary><c>user</c> (null), <c>businessUnit</c>, <c>parentChildBusinessUnits</c> or <c>organization</c> (a workflow only).</summary>
    public string? Scope { get; init; }

    /// <summary><c>caller</c> (null) or <c>owner</c> (real-time only).</summary>
    public string? RunAs { get; init; }

    /// <summary>Whether a failure is logged (real-time only; default true).</summary>
    public bool LogFailures { get; init; } = true;

    /// <summary>Whether a finished run is deleted (background only; default false).</summary>
    public bool AutoDelete { get; init; }

    /// <summary>Whether a user can start the workflow by hand.</summary>
    public bool OnDemand { get; init; }

    /// <summary>Whether the workflow is a child process that other workflows start.</summary>
    public bool ChildProcess { get; init; }

    /// <summary>The scope of a business rule: <c>entity</c>, the only one accepted (null when the file does not say).</summary>
    public string? RuleScope { get; init; }
}

/// <summary>The triggers of a workflow (A4).</summary>
public sealed record TriggerNode(CreateTrigger? Create, UpdateTrigger? Update, DeleteTrigger? Delete, SourceSpan Span = default);

/// <summary>Start when a record is created. <paramref name="Stage"/> is <c>pre</c> or <c>post</c> (real-time only; null is <c>post</c>).</summary>
public sealed record CreateTrigger(string? Stage = null);

/// <summary>Start when one of <paramref name="Columns"/> (at least one) changes. <paramref name="Stage"/> as for <see cref="CreateTrigger"/>.</summary>
public sealed record UpdateTrigger(IReadOnlyList<string> Columns, string? Stage = null)
{
    /// <summary>Where each of <see cref="Columns"/> is, in the same order (empty when not read from YAML), for messages about it.</summary>
    public IReadOnlyList<SourceSpan> ColumnSpans { get; init; } = [];
}

/// <summary>Start when a record is deleted. The only stage is <c>pre</c> (null).</summary>
public sealed record DeleteTrigger(string? Stage = null);

/// <summary>An argument of an action.</summary>
public sealed record ArgumentNode(string Name, bool IsInput, string Type, string? Entity, bool Required, string Description, SourceSpan Span = default);

/// <summary>A step. <paramref name="Id"/> is set only on the steps that other steps can refer to.</summary>
public abstract record StepNode(string? Id, string? Description, SourceSpan Span)
{
    /// <summary>Where the id's value is (null: no id, or not read from YAML), for messages about it.</summary>
    public SourceSpan? IdSpan { get; init; }

    /// <summary>Where the description's value is (null: no description, or not read from YAML), for messages about it.</summary>
    public SourceSpan? DescriptionSpan { get; init; }
}

/// <summary>Create a record of <paramref name="Entity"/>; the columns are written in the order of <paramref name="Set"/>.</summary>
public sealed record CreateNode(string Entity, IReadOnlyList<(string Column, ValueNode Value)> Set, string? Id = null, string? Description = null, SourceSpan Span = default)
    : StepNode(Id, Description, Span);

public sealed record UpdateNode(RecordNode Record, IReadOnlyList<(string Column, ValueNode Value)> Set, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record AssignNode(RecordNode Record, ValueNode Owner, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record SetStateNode(RecordNode Record, int State, int Status, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record SendEmailNode(IReadOnlyList<(string Column, ValueNode Value)> Set, string? Id = null, string? Description = null, SourceSpan Span = default)
    : StepNode(Id, Description, Span);

/// <summary>A condition with its branches (the first is <c>when</c>, then each <c>elseIf</c>) and an optional <c>else</c>.</summary>
public sealed record IfNode(IReadOnlyList<BranchNode> Branches, IReadOnlyList<StepNode>? Else = null, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record BranchNode(CondNode When, IReadOnlyList<StepNode> Then, SourceSpan Span = default);

/// <summary>Wait until <paramref name="Until"/> or <paramref name="Timeout"/>; <paramref name="Then"/> follows the wait.</summary>
public sealed record WaitNode(CondNode? Until, ValueNode? Timeout, IReadOnlyList<StepNode> Then, IReadOnlyList<StepNode>? OnTimeout = null, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record StopNode(bool Canceled, ValueNode? Message = null, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record StartChildNode(string Workflow, RecordNode Record, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record PerformActionNode(string Message, RecordNode? Target, IReadOnlyList<(string Name, ValueNode Value)> Inputs, string? Id = null, string? Description = null, SourceSpan Span = default)
    : StepNode(Id, Description, Span)
{
    /// <summary>Where the message's value is (null: not read from YAML), for messages about it.</summary>
    public SourceSpan? MessageSpan { get; init; }
}

public sealed record CustomActivityNode(string Type, string Assembly, IReadOnlyList<(string Name, ValueNode Value)> Inputs, IReadOnlyList<(string Property, string Name)> Outputs, string? Id = null, string? Description = null, SourceSpan Span = default)
    : StepNode(Id, Description, Span)
{
    /// <summary>Where the name of each of <see cref="Outputs"/> is, in the same order (empty when not read from YAML).</summary>
    public IReadOnlyList<SourceSpan> OutputSpans { get; init; } = [];
}

public sealed record SetOutputNode(string Argument, ValueNode Value, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

/// <summary>Business rule: set a column, or only when it is empty (<paramref name="DefaultOnly"/>, <c>setDefault</c>).</summary>
public sealed record SetValueNode(string Field, ValueNode Value, bool DefaultOnly = false, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record ClearValueNode(string Field, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

public sealed record ShowErrorNode(string? Field, string Message, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

/// <summary>Business rule: <c>setVisibility</c>, <c>setLock</c> or <c>setRequirement</c> (<paramref name="Kind"/>) with <paramref name="Setting"/> <c>true</c>/<c>false</c> or <c>none</c>/<c>recommended</c>/<c>required</c>.</summary>
public sealed record ClientOnlyNode(string Kind, string Field, string Setting, string? Description = null, SourceSpan Span = default)
    : StepNode(null, Description, Span);

/// <summary>A record: the primary one, one reached through a lookup column of the primary table, or one a step created.</summary>
public abstract record RecordNode
{
    public sealed record Primary : RecordNode;

    public sealed record Related(string Lookup) : RecordNode;

    public sealed record Created(string StepId) : RecordNode;

    /// <summary>The primary record.</summary>
    public static RecordNode PrimaryRecord { get; } = new Primary();
}

/// <summary>A value: a literal (text until typed by its destination) or a dynamic form (A6).</summary>
public abstract record ValueNode(SourceSpan Span);

/// <summary>A literal; <c>null</c> text is the empty value (<c>~</c>).</summary>
public sealed record LiteralNode(string? Text, SourceSpan Span = default) : ValueNode(Span);

public sealed record FieldNode(string Field, RecordNode Record, SourceSpan Span = default) : ValueNode(Span);

public sealed record RecordValueNode(RecordNode Record, SourceSpan Span = default) : ValueNode(Span);

public sealed record ArgNode(string Name, SourceSpan Span = default) : ValueNode(Span);

public sealed record OutputNode(string Name, string StepId, SourceSpan Span = default) : ValueNode(Span);

public sealed record NowNode(SourceSpan Span = default) : ValueNode(Span);

public sealed record LookupNode(string Entity, Guid Id, string? Name = null, SourceSpan Span = default) : ValueNode(Span);

/// <summary><c>add</c>, <c>subtract</c>, <c>multiply</c>, <c>increment</c>, <c>decrement</c>, <c>set</c>, <c>concat</c> or <c>firstOf</c> over <paramref name="Operands"/>.</summary>
public sealed record OperatorNode(string Operator, IReadOnlyList<ValueNode> Operands, SourceSpan Span = default) : ValueNode(Span);

/// <summary>A condition (A7).</summary>
public abstract record CondNode(SourceSpan Span);

/// <summary>A comparison; <paramref name="Right"/> is null for <c>null</c> and <c>notNull</c>.</summary>
public sealed record CompareNode(string Op, ValueNode Left, ValueNode? Right = null, SourceSpan Span = default) : CondNode(Span);

public sealed record AllNode(IReadOnlyList<CondNode> Conditions, SourceSpan Span = default) : CondNode(Span);

public sealed record AnyNode(IReadOnlyList<CondNode> Conditions, SourceSpan Span = default) : CondNode(Span);

public sealed record NotNode(CondNode Condition, SourceSpan Span = default) : CondNode(Span);
