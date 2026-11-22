using Xrmd.Abstractions;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.Background;

/// <summary>
/// The core's record writes, for the background workflow jobs (plan 2.14, W4 and W10): see <see cref="WorkflowJobs.OnRecordWrittenAsync"/>.
/// While no job waits (<see cref="WorkflowWaits.HasAny"/> false), the write of a record other than a job or a process costs nothing.
/// The delete of an action's definition also deletes its message (W14, <see cref="Actions.ActionMessages.OnWorkflowDeletingAsync"/>).
/// </summary>
/// <remarks>
/// <see cref="WorkflowJobs"/> and <see cref="Actions.ActionMessages"/> are resolved on first need: they reach the core, and the core
/// holds this observer.
/// </remarks>
public sealed class WorkflowWriteObserver(WorkflowWaits waits, Func<WorkflowJobs> jobs, Func<Actions.ActionMessages>? actions = null)
    : IRecordWriteObserver
{
    private WorkflowJobs? _jobs;

    private WorkflowJobs Jobs => _jobs ??= jobs();

    public Task OnWrittenAsync(DataSession s, string entity, Guid? id, CancellationToken ct) =>
        Observed(entity) ? Jobs.OnRecordWrittenAsync(s, entity, id, deleted: false, ct) : Task.CompletedTask;

    public Task OnDeletedAsync(DataSession s, string entity, Guid id, CancellationToken ct) =>
        Observed(entity) ? Jobs.OnRecordWrittenAsync(s, entity, id, deleted: true, ct) : Task.CompletedTask;

    public Task OnDeletingAsync(DataSession s, string entity, Guid id, CancellationToken ct) =>
        entity == S.Table && actions is not null ? actions().OnWorkflowDeletingAsync(s, id, ct) : Task.CompletedTask;

    private bool Observed(string entity) => waits.HasAny || entity is WorkflowJobs.AsyncOperation or S.Table;
}
