using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Pipeline;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Engine;

/// <summary>
/// The interpreter of the classic process step model (plan 2.14, Task 4). It walks the step tree in document order; every read and
/// write of a step is an SDK request run through the <see cref="PipelineExecutor"/> as <see cref="WorkflowRun.Caller"/>, one level
/// deeper than the run (W7: privileges and record access are checked per step, plug-ins fire on the step's writes). The requests
/// join the run's session when it has one (real-time workflows and actions: the triggering transaction, W19) and otherwise each runs
/// in its own transaction (background, W9).
/// </summary>
/// <remarks>
/// Runs here: create, update (at stage 20 of the primary record: into <see cref="WorkflowRun.Target"/>, W8), assign, set state, send
/// email, check condition, stop (W11), wait (background only, W10: the run suspends), the action call (W13: the outputs become the
/// step's variable), the output assignment of an action (W14), the business rule steps (W15) and "start child workflow" (W12: a
/// background job of the child, queued through <see cref="Background.WorkflowJobs"/>) and a custom workflow activity (W17, through the
/// <see cref="Custom.CustomActivityRunner"/>: its outputs become the variables the step names). A definition with an
/// <see cref="UnsupportedStep"/> does not run (W20).
/// </remarks>
/// <param name="executor">The pipeline every step's request runs through.</param>
/// <param name="metadata">The metadata the steps read.</param>
/// <param name="jobs">Queues the child workflows (W12); without it a child workflow step fails explicitly.</param>
/// <param name="activities">Runs the custom workflow activities (W17); without it such a step fails explicitly.</param>
public sealed partial class WorkflowEngine(PipelineExecutor executor, IMetadataStore metadata, Background.WorkflowJobs? jobs = null,
    Custom.CustomActivityRunner? activities = null)
{
    /// <summary>
    /// Runs <paramref name="d"/> on <paramref name="run"/>: from the start, or from <see cref="WorkflowRun.ResumePath"/> when it is set.
    /// </summary>
    /// <returns><see cref="WorkflowOutcome.Completed"/>, <see cref="WorkflowOutcome.Canceled"/> (a stop step, a business rule error) or
    /// <see cref="WorkflowOutcome.Suspended"/> (a wait).</returns>
    /// <exception cref="XrmdFaultException">A step's fault: the pipeline's (privileges, validation, plug-ins, depth) or the run's own
    /// 400 for a value or step it cannot run faithfully; 400 naming the unsupported steps of a definition that has any (W20).</exception>
    public async Task<WorkflowOutcome> RunAsync(WorkflowDefinition d, WorkflowRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(run);
        if (d.Unsupported.Count > 0)
            throw XrmdFaultException.InvalidArgument(
                $"Workflow '{run.Workflow.Name}' cannot run on xrmverse: {string.Join("; ", d.Unsupported.Select(u => $"{u.Label}: {u.Reason}"))}");
        if (run.ResumePath is { } resume && !d.Root.DepthFirst().Any(s => s.Path == resume))
            throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}' has no step '{resume}' to resume at.");

        var flow = await ExecuteAsync(d.Root, run, ct);
        return flow.End ?? new WorkflowOutcome.Completed();
    }

    /// <summary>What a step tells the steps around it: go on, end the run (<see cref="End"/>) or skip the rest of the enclosing sequence.</summary>
    private readonly record struct Flow(WorkflowOutcome? End, bool SkipRest)
    {
        public static Flow Continue => default;

        public static Flow Ended(WorkflowOutcome outcome) => new(outcome, false);

        public static Flow SkipRestOfSequence => new(null, true);
    }

    private static bool Contains(WorkflowStep step, string path) => step.DepthFirst().Any(s => s.Path == path);

    private async Task<Flow> ExecuteAsync(WorkflowStep step, WorkflowRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (step)
        {
            case SequenceStep sequence:
                return await ExecuteSequenceAsync(sequence, run, ct);
            case ConditionStep condition:
                return await ExecuteConditionAsync(condition, run, ct);
            case WaitStep wait:
                return await ExecuteWaitAsync(wait, run, ct);
        }

        // A leaf: when resuming, it is the step to resume at (the containers above skipped every other).
        if (run.ResumePath is not null)
        {
            if (run.ResumePath != step.Path)
                throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}' cannot resume at '{run.ResumePath}' from '{step.Label}'.");
            run.ResumePath = null;
        }

        var reader = new StepReader(this, run);
        switch (step)
        {
            case CreateStep create:
                await CreateAsync(create, reader, run, ct);
                return Flow.Continue;
            case UpdateStep update:
                await UpdateAsync(update, reader, run, ct);
                return Flow.Continue;
            case AssignStep assign:
                await AssignAsync(assign, reader, run, ct);
                return Flow.Continue;
            case SetStateStep setState:
                await SetStateAsync(setState, reader, run, ct);
                return Flow.Continue;
            case SendEmailStep sendEmail:
                await SendEmailAsync(sendEmail, reader, run, ct);
                return Flow.Continue;
            case ActionStep action:
                await CallActionAsync(action, reader, run, ct);
                return Flow.Continue;
            case AssignOutputStep output:
                run.Variables[output.Argument] = await reader.EvaluateAsync(output.Value, ct);
                return Flow.Continue;
            case SetValueStep setValue:
                await SetValueAsync(setValue, reader, run, ct);
                return Flow.Continue;
            case ShowErrorStep error:
                return Flow.Ended(new WorkflowOutcome.Canceled(await MessageAsync(error.Message, reader, ct)));
            case ClientOnlyStep:
                return Flow.Continue; // show/hide, enable/disable, requirement level: forms only (W15)
            case StopStep stop:
                return Flow.Ended(stop.Canceled
                    ? new WorkflowOutcome.Canceled(stop.Message is null ? "" : await MessageAsync(stop.Message, reader, ct))
                    : new WorkflowOutcome.Completed());
            case ChildWorkflowStep child:
                await StartChildAsync(child, reader, run, ct);
                return Flow.Continue;
            case CustomActivityStep custom:
                return await RunCustomActivityAsync(custom, reader, run, ct);
            case UnsupportedStep unsupported:
                throw XrmdFaultException.InvalidArgument(
                    $"Workflow '{run.Workflow.Name}' cannot run on xrmverse: {unsupported.Label}: {unsupported.Reason}");
            default:
                throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}': the step '{step.Label}' cannot run on xrmverse.");
        }
    }

    /// <summary>W12: a background job of the child workflow on the step's record (children always run in the background).</summary>
    private async Task StartChildAsync(ChildWorkflowStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        if (jobs is null) throw NotYet(run, step, "starting a child workflow");
        var record = await RecordOfAsync(step, step.Record, reader, run, ct);
        await jobs.QueueChildAsync(step.WorkflowId, record, run, ct);
    }

    /// <summary>
    /// W17: the activity runs with the step's inputs evaluated; its outputs are written to the variables the step names. A
    /// <c>StopWorkflowException</c> ends the run as a stop step does (W11).
    /// </summary>
    private async Task<Flow> RunCustomActivityAsync(CustomActivityStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        if (activities is null) throw NotYet(run, step, "a custom workflow activity");
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in step.Inputs) inputs[name] = await reader.EvaluateAsync(value, ct);
        var result = await activities.RunAsync(step, inputs, run, executor, ct);
        if (result.Stop is { } stop) return Flow.Ended(stop);
        foreach (var (output, variable) in step.Outputs) run.Variables[variable] = result.Outputs.GetValueOrDefault(output);
        return Flow.Continue;
    }

    private static XrmdFaultException NotYet(WorkflowRun run, WorkflowStep step, string what) =>
        XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}': the step '{step.Label}' ({what}) cannot run on xrmverse yet.");

    private async Task<Flow> ExecuteSequenceAsync(SequenceStep sequence, WorkflowRun run, CancellationToken ct)
    {
        var start = 0;
        if (run.ResumePath is { } resume)
        {
            start = sequence.Steps.ToList().FindIndex(s => Contains(s, resume));
            if (start < 0)
                throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}' cannot resume at '{resume}' in '{sequence.Label}'.");
        }
        for (var i = start; i < sequence.Steps.Count; i++)
        {
            var flow = await ExecuteAsync(sequence.Steps[i], run, ct);
            if (flow.End is not null) return flow;
            if (flow.SkipRest) return Flow.Continue;
        }
        return Flow.Continue;
    }

    private async Task<Flow> ExecuteConditionAsync(ConditionStep condition, WorkflowRun run, CancellationToken ct)
    {
        if (run.ResumePath is { } resume)
        {
            // The branch the suspended run had taken: its condition is not evaluated again.
            var taken = condition.Branches.Select(b => b.Then).Append(condition.Otherwise).FirstOrDefault(b => b is not null && Contains(b, resume))
                        ?? throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}' cannot resume at '{resume}' in '{condition.Label}'.");
            return await ExecuteAsync(taken, run, ct);
        }

        var reader = new StepReader(this, run);
        foreach (var branch in condition.Branches)
            if (await ConditionEvaluator.EvaluateAsync(branch.When, reader.ReadAsync, run, ct))
                return await ExecuteAsync(branch.Then, run, ct);
        return condition.Otherwise is null ? Flow.Continue : await ExecuteAsync(condition.Otherwise, run, ct);
    }

    /// <summary>
    /// A wait (W10, background only). The condition holds (or a plain postpone's date has come): the run goes on. The timeout has
    /// come first: its block runs and the rest of the enclosing sequence is skipped. Otherwise the run suspends at this step.
    /// </summary>
    private async Task<Flow> ExecuteWaitAsync(WaitStep wait, WorkflowRun run, CancellationToken ct)
    {
        if (run.ResumePath is { } resume)
        {
            if (resume != wait.Path)
            {
                // Suspended inside the timeout block: it goes on there, then the rest of the enclosing sequence is skipped.
                var flow = await ExecuteAsync(wait.OnTimeout!, run, ct);
                return flow.End is not null ? flow : Flow.SkipRestOfSequence;
            }
            run.ResumePath = null;
        }
        if (run.Stage != 0)
            throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}': the wait '{wait.Label}' can only run in a background workflow.");

        var reader = new StepReader(this, run);
        DateTime? timeout = null;
        if (wait.Timeout is not null)
            timeout = await reader.EvaluateAsync(wait.Timeout, ct) switch
            {
                null => null,
                DateTime date => date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc),
                var other => throw ExpressionEvaluator.Fault(
                    $"The timeout of the wait '{wait.Label}' is a value of type {ExpressionEvaluator.TypeName(other)}, not a date."),
            };
        var due = timeout is { } t && run.Now >= t;

        if (wait.Until is null)
            return timeout is null
                ? throw XrmdFaultException.InvalidArgument(
                    $"Workflow '{run.Workflow.Name}': the date of the wait '{wait.Label}' is empty, so the run cannot know until when to wait.")
                : due ? Flow.Continue : Suspend(wait, timeout, null);
        if (await ConditionEvaluator.EvaluateAsync(wait.Until, reader.ReadAsync, run, ct))
            return Flow.Continue;
        if (due)
        {
            if (wait.OnTimeout is null) return Flow.SkipRestOfSequence;
            var flow = await ExecuteAsync(wait.OnTimeout, run, ct);
            return flow.End is not null ? flow : Flow.SkipRestOfSequence;
        }
        var record = await WaitRecordAsync(wait, reader, run, ct);
        if (record is null && timeout is null)
            throw XrmdFaultException.InvalidArgument(
                $"Workflow '{run.Workflow.Name}': the condition of the wait '{wait.Label}' reads no record and the wait has no timeout, " +
                "so nothing could ever resume it.");
        return Suspend(wait, timeout, record);
    }

    private static Flow Suspend(WaitStep wait, DateTime? until, EntityReference? record) =>
        Flow.Ended(new WorkflowOutcome.Suspended(until is { } u ? new DateTimeOffset(u, TimeSpan.Zero) : null, record, wait.Path));

    /// <summary>The one record a wait's condition reads (a change of it wakes the run, W10).</summary>
    private static async Task<EntityReference?> WaitRecordAsync(WaitStep wait, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        var records = RecordsOf(wait.Until!).Distinct().ToList();
        return records switch
        {
            [] => null,
            // A related record whose lookup is empty: the primary record, whose change may set it.
            [var record] => await reader.ResolveAsync(record, ct) ?? run.Primary,
            _ => throw XrmdFaultException.InvalidArgument(
                $"Workflow '{run.Workflow.Name}': the wait '{wait.Label}' reads several records ({string.Join(", ", records)}), which xrmverse does not support."),
        };
    }

    private static IEnumerable<RecordRef> RecordsOf(ConditionExpr c) => c switch
    {
        ConditionExpr.And and => RecordsOf(and.Left).Concat(RecordsOf(and.Right)),
        ConditionExpr.Or or => RecordsOf(or.Left).Concat(RecordsOf(or.Right)),
        ConditionExpr.Not not => RecordsOf(not.Operand),
        ConditionExpr.Compare compare => RecordsOf(compare.Left).Concat(compare.Right is null ? [] : RecordsOf(compare.Right)),
        _ => [],
    };

    private static IEnumerable<RecordRef> RecordsOf(ValueExpr v) => v switch
    {
        ValueExpr.AttributeOf attribute => [attribute.Record],
        ValueExpr.Conversion conversion => RecordsOf(conversion.Operand),
        ValueExpr.Arithmetic arithmetic => arithmetic.Operands.SelectMany(RecordsOf),
        ValueExpr.Concat concat => concat.Parts.SelectMany(RecordsOf),
        ValueExpr.FirstNonNull first => first.Operands.SelectMany(RecordsOf),
        ValueExpr.Cast cast => RecordsOf(cast.Operand),
        _ => [], // ActionOutput, VariableRef, Literal, ExecutionTime: no record
    };

    private static async Task<string> MessageAsync(ValueExpr message, StepReader reader, CancellationToken ct) =>
        await reader.EvaluateAsync(message, ct) switch
        {
            null => "",
            string text => text,
            var other => throw ExpressionEvaluator.Fault($"The message of a stop or error step is a value of type {ExpressionEvaluator.TypeName(other)}, not a text."),
        };

    // ---- requests -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one request of a step (the W13 helper): through the pipeline, as the run's caller, one level deeper than the run, in the
    /// run's session (if any).
    /// </summary>
    internal Task<OrganizationResponse> ExecuteRequestAsync(OrganizationRequest request, WorkflowRun run, CancellationToken ct) =>
        executor.ExecuteAsync(request, run.Invocation with { Caller = run.Caller, Depth = run.Invocation.Depth + 1 }, ct);

    /// <summary>
    /// The reads and evaluations of one step: each column of a record is read once per step, through a <c>Retrieve</c> of the pipeline
    /// as the run's caller. A primary key (<c>{entity}id</c>) is the record's id; at stage 20 the primary record is the stored row
    /// overlaid with the <see cref="WorkflowRun.Target"/> (W8), and a <c>Create</c> has no stored row.
    /// </summary>
    private sealed class StepReader(WorkflowEngine engine, WorkflowRun run)
    {
        private readonly Dictionary<(RecordRef, string), object?> _values = [];

        public Task<object?> EvaluateAsync(ValueExpr e, CancellationToken ct) => ExpressionEvaluator.Evaluate(e, ReadAsync, run, ct);

        public async Task<object?> ReadAsync(RecordRef record, string attribute, CancellationToken ct)
        {
            if (_values.TryGetValue((record, attribute), out var cached)) return cached;
            var value = await ReadUncachedAsync(record, attribute, ct);
            _values[(record, attribute)] = value;
            return value;
        }

        private async Task<object?> ReadUncachedAsync(RecordRef record, string attribute, CancellationToken ct)
        {
            if (await ResolveAsync(record, ct) is not { } reference) return null;
            if (attribute == reference.LogicalName + "id") return reference.Id == Guid.Empty ? null : reference.Id;
            if (record is RecordRef.Primary && run.Stage == 20 && run.Target is { } target)
            {
                if (target.Attributes.TryGetValue(attribute, out var overlaid)) return overlaid;
                if (run.MessageName == "Create") return null;
            }
            if (reference.Id == Guid.Empty) return null;

            var response = await engine.ExecuteRequestAsync(new RetrieveRequest { Target = reference, ColumnSet = new ColumnSet(attribute) }, run, ct);
            var entity = response.Results.TryGetValue("Entity", out var e) ? e as Entity : null;
            return entity is not null && entity.Attributes.TryGetValue(attribute, out var value) ? value : null;
        }

        /// <summary>The record a <see cref="RecordRef"/> designates; null for a related record whose lookup is empty.</summary>
        public async Task<EntityReference?> ResolveAsync(RecordRef record, CancellationToken ct)
        {
            switch (record)
            {
                case RecordRef.Primary:
                    // At stage 20 of a Create the id is the Target's, or the primary key the client set in its columns, if any.
                    if (run.Primary.Id != Guid.Empty || run.Target is not { } target) return run.Primary;
                    if (target.Id != Guid.Empty) return new EntityReference(run.Primary.LogicalName, target.Id);
                    return target.Attributes.TryGetValue(run.Primary.LogicalName + "id", out var key) && key is Guid clientId && clientId != Guid.Empty
                        ? new EntityReference(run.Primary.LogicalName, clientId)
                        : run.Primary;
                case RecordRef.Related related:
                    return await ReadAsync(new RecordRef.Primary(), related.LookupAttribute, ct) switch
                    {
                        null => null,
                        EntityReference reference => reference,
                        var other => throw ExpressionEvaluator.Fault(
                            $"The column '{related.LookupAttribute}' of the primary record is not a lookup (its value is of type {ExpressionEvaluator.TypeName(other)})."),
                    };
                case RecordRef.Created created:
                    return run.Variables.TryGetValue(created.Variable, out var value) && value is EntityReference createdRecord
                        ? createdRecord
                        : throw XrmdFaultException.InvalidArgument(
                            $"Workflow '{run.Workflow.Name}': the record of the step '{created.Variable}' was not created in this run.");
                default:
                    throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}': the record '{record}' is not supported.");
            }
        }
    }
}
