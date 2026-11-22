using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Core.Errors;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Engine;

/// <summary>The record steps: create, update, assign, set state, the action call (W13) and the business rule values (W15).</summary>
public sealed partial class WorkflowEngine
{
    /// <summary>The column a workflow sets on the activities it creates (checklist 1e).</summary>
    private const string IsWorkflowCreated = "isworkflowcreated";

    /// <summary>
    /// The columns of <paramref name="values"/>, evaluated; a null value is left out (<paramref name="keepNulls"/> false: a create) or
    /// clears the column (an update).
    /// </summary>
    private static async Task<Entity> EntityOfAsync(string entity, Guid id, IReadOnlyDictionary<string, ValueExpr> values, StepReader reader,
        bool keepNulls, CancellationToken ct)
    {
        var record = new Entity(entity, id);
        foreach (var (column, value) in values)
            if (await reader.EvaluateAsync(value, ct) is var evaluated && (evaluated is not null || keepNulls))
                record[column] = evaluated;
        return record;
    }

    /// <summary>A new record of an activity table is marked as created by a workflow (<c>isworkflowcreated</c>, checklist 1e).</summary>
    private void MarkWorkflowCreated(Entity record)
    {
        if (metadata.GetEntity(record.LogicalName)?.IsActivity == true && !record.Contains(IsWorkflowCreated))
            record[IsWorkflowCreated] = true;
    }

    private async Task<EntityReference> CreateRecordAsync(Entity record, WorkflowRun run, CancellationToken ct)
    {
        MarkWorkflowCreated(record);
        var response = await ExecuteRequestAsync(new CreateRequest { Target = record }, run, ct);
        return new EntityReference(record.LogicalName, (Guid)response.Results["id"]);
    }

    private async Task CreateAsync(CreateStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        var record = await EntityOfAsync(step.Entity, Guid.Empty, step.Values, reader, keepNulls: false, ct);
        run.Variables[step.Variable] = await CreateRecordAsync(record, run, ct);
    }

    private async Task UpdateAsync(UpdateStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        if (step.Record is RecordRef.Primary && run.Stage == 20)
        {
            // W8: a pre-operation workflow changes the message's Target, as a pre-operation plug-in would. On a Create the record
            // has no id yet (the core makes it at stage 30), so the record is not resolved here.
            var into = run.Target ?? throw XrmdFaultException.InvalidArgument(
                $"Workflow '{run.Workflow.Name}': the step '{step.Label}' updates the primary record at stage 20 without its Target.");
            var values = await EntityOfAsync(run.Primary.LogicalName, Guid.Empty, step.Values, reader, keepNulls: true, ct);
            foreach (var (column, value) in values.Attributes)
                into[column] = value;
            return;
        }
        var target = await RecordOfAsync(step, step.Record, reader, run, ct);
        var record = await EntityOfAsync(target.LogicalName, target.Id, step.Values, reader, keepNulls: true, ct);
        await ExecuteRequestAsync(new UpdateRequest { Target = record }, run, ct);
    }

    private async Task AssignAsync(AssignStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        var target = await RecordOfAsync(step, step.Record, reader, run, ct);
        var value = await reader.EvaluateAsync(step.Owner, ct);
        if (value is not EntityReference { LogicalName: "systemuser" or "team" } owner)
            throw XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}': the step '{step.Label}' assigns to " + value switch
            {
                null => "no one",
                EntityReference other => $"a {other.LogicalName} record",
                _ => $"a value of type {ExpressionEvaluator.TypeName(value)}",
            } + ", not to a user or a team.");
        await ExecuteRequestAsync(new AssignRequest { Target = target, Assignee = owner }, run, ct);
    }

    private async Task SetStateAsync(SetStateStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        var target = await RecordOfAsync(step, step.Record, reader, run, ct);
        await ExecuteRequestAsync(new SetStateRequest
        {
            EntityMoniker = target, State = new OptionSetValue(step.State), Status = new OptionSetValue(step.Status),
        }, run, ct);
    }

    /// <summary>
    /// "Perform action" (W13): the message with the step's inputs (and <c>Target</c>, the bound record), through the whole pipeline as
    /// the run's caller (its plug-in steps fire, its arguments are checked, a XAML action runs its process); its outputs (the response's
    /// results) become the step's variable, which later steps read by name (<see cref="ValueExpr.ActionOutput"/>). An input whose value
    /// is null is left out of the request, as a caller leaves out an argument it does not set (from memory, checklist 1d): a required
    /// one is then the action's 400.
    /// </summary>
    private async Task CallActionAsync(ActionStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        var request = new OrganizationRequest(step.Message);
        foreach (var (name, value) in step.Inputs)
            if (await reader.EvaluateAsync(value, ct) is { } evaluated)
                request[name] = evaluated;
        if (step.Target is { } bound)
            request["Target"] = await RecordOfAsync(step, bound, reader, run, ct);
        var response = await ExecuteRequestAsync(request, run, ct);
        run.Variables[step.Variable] = response.Results.ToDictionary(r => r.Key, r => (object?)r.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Business rule "set field value", "set default value" and "clear field value" (W15): into the message's Target at stage 20; a
    /// default only on <c>Create</c> and only when the column is empty.
    /// </summary>
    private static async Task SetValueAsync(SetValueStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        if (run.Stage != 20 || run.Target is not { } target)
            throw XrmdFaultException.InvalidArgument(
                $"Workflow '{run.Workflow.Name}': the business rule step '{step.Label}' sets values only at stage 20, in the message's Target.");
        if (step.DefaultOnly && (run.MessageName != "Create" || target.Attributes.TryGetValue(step.Attribute, out var current) && current is not null and not ""))
            return;
        target[step.Attribute] = await reader.EvaluateAsync(step.Value, ct);
    }

    /// <summary>The record a step acts on; a related record whose lookup is empty fails the step.</summary>
    private static async Task<EntityReference> RecordOfAsync(WorkflowStep step, RecordRef record, StepReader reader, WorkflowRun run, CancellationToken ct) =>
        await reader.ResolveAsync(record, ct) is { Id: var id } reference && id != Guid.Empty
            ? reference
            : throw XrmdFaultException.InvalidArgument(
                $"Workflow '{run.Workflow.Name}': the step '{step.Label}' acts on {Describe(record)}, which is empty.");

    private static string Describe(RecordRef record) => record switch
    {
        RecordRef.Related related => $"the record of the lookup '{related.LookupAttribute}'",
        RecordRef.Created created => $"the record created by '{created.Variable}'",
        _ => "the primary record",
    };
}
