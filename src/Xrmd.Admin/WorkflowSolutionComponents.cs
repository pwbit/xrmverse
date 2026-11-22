using Microsoft.Crm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Workflows.Authoring;

namespace Xrmd.Admin;

/// <summary>
/// The <c>AddSolutionComponent</c> of the authoring service (plan 2.21, A15, A19): the metadata message, run as the caller in the
/// service's transaction (<see cref="MetadataOperations"/> demands <c>prvWriteSolution</c> and refuses a managed or unknown solution).
/// </summary>
public sealed class WorkflowSolutionComponents(MetadataOperations operations) : IAuthoringSolutionComponents
{
    private const int Workflow = 29;

    public Task AddWorkflowAsync(Caller caller, string solution, Guid workflowId, DataSession s, CancellationToken ct) =>
        operations.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = Workflow, ComponentId = workflowId, SolutionUniqueName = solution, AddRequiredComponents = false,
        }, caller, s, ct);
}
