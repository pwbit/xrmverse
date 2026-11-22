using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Pipeline;

namespace Xrmd.Workflows.Engine;

/// <summary>
/// The mutable state of one run of a process (plan 2.14, Task 4): the record it runs on, who runs it and where, and the variables
/// its steps wrote. A background run that suspends (a wait, W10) is resumed later with a new <see cref="WorkflowRun"/> holding the
/// same <see cref="Variables"/> and the suspended step in <see cref="ResumePath"/>.
/// </summary>
public sealed class WorkflowRun
{
    /// <summary>The activation (or definition) row that runs.</summary>
    public required WorkflowRow Workflow { get; init; }

    /// <summary>The record the process runs on (its primary entity).</summary>
    public required EntityReference Primary { get; init; }

    /// <summary>
    /// Stage 20 only (real-time pre-operation and business rules, W8 and W15): the message's <c>Target</c>, which reads of the primary
    /// record see over the stored row and into which an update of the primary record writes. Null otherwise.
    /// </summary>
    public Entity? Target { get; init; }

    /// <summary>The user every step's request runs as (W7).</summary>
    public required Caller Caller { get; init; }

    /// <summary>
    /// Where the run is: its depth, its session (the triggering transaction for a real-time run or an action, W19; null for a
    /// background run, whose steps run each in its own transaction, W9) and its parent context. Each step's request runs one level
    /// deeper, as <see cref="Caller"/>.
    /// </summary>
    public required InvocationContext Invocation { get; init; }

    /// <summary>
    /// The process arguments (an action's inputs and outputs, W14), the records created by its steps (by the step's variable name,
    /// as an <see cref="EntityReference"/>) and the outputs of its action call steps (W13).
    /// </summary>
    public IDictionary<string, object?> Variables { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// The <see cref="Xaml.WorkflowStep.Path"/> of the step a resumed run starts at: every step before it in document order is
    /// skipped and the conditions on its way are not evaluated again. The engine clears it when it reaches that step.
    /// </summary>
    public string? ResumePath { get; set; }

    /// <summary>0 for a background run, 20 or 40 for a real-time run (its stage), 30 for an action.</summary>
    public int Stage { get; init; }

    /// <summary>The message that triggered the run (<c>Create</c>, <c>Update</c>, ...), when there is one: "set default value" (W15)
    /// applies on <c>Create</c> only, and a stage 20 <c>Create</c> has no stored row to read.</summary>
    public string? MessageName { get; init; }

    /// <summary>The time of this run (UTC): the process execution time (<c>RetrieveCurrentTime</c>) and the clock of the waits.</summary>
    public DateTime Now { get; init; } = DateTime.UtcNow;
}

/// <summary>How a run of <see cref="WorkflowEngine.RunAsync"/> ended. A step fault is not an outcome: it is thrown.</summary>
public abstract record WorkflowOutcome
{
    /// <summary>Every step ran, or a stop step ended the run as succeeded (W11).</summary>
    public sealed record Completed : WorkflowOutcome;

    /// <summary>
    /// A stop step ended the run as canceled, or a business rule showed its error (W11, W15): <see cref="Message"/> is the step's
    /// text (empty when it has none). A real-time caller turns it into the 400 plug-in abort; a background job ends 3/32.
    /// </summary>
    public sealed record Canceled(string Message) : WorkflowOutcome;

    /// <summary>
    /// A background run waits (W10): until <see cref="Until"/> (a timeout or a postpone date, null for none) or until
    /// <see cref="WaitRecord"/> (the record its condition reads, null for a plain postpone) changes. It resumes at
    /// <see cref="ResumePath"/>, the wait step, which evaluates its condition again.
    /// </summary>
    public sealed record Suspended(DateTimeOffset? Until, EntityReference? WaitRecord, string ResumePath) : WorkflowOutcome;
}
