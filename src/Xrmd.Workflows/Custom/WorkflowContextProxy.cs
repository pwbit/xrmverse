using System.ComponentModel;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline;
using Xrmd.Workflows.Engine;

namespace Xrmd.Workflows.Custom;

/// <summary>
/// The <c>IWorkflowContext</c> a custom workflow activity gets from <c>CodeActivityContext.GetExtension</c> (plan 2.14, W17). The
/// interface is the customer's (the workflow SDK installed with <c>xrmd sdk install</c>, <see cref="WorkflowSdk"/>): nothing here
/// names it at compile time. The proxy implements it at run time over the <c>IExecutionContext</c> members of the run.
/// </summary>
/// <remarks>
/// What the members return (rulings of Task 8b; checklist: not confirmed against Dataverse):
/// <list type="bullet">
/// <item><c>UserId</c> and <c>BusinessUnitId</c>: the run's user (<see cref="WorkflowRun.Caller"/>: runas for a real-time run, the job
/// owner for a background run, the caller for an on-demand run or an action); <c>InitiatingUserId</c> the run's initiating user.</item>
/// <item><c>Depth</c>: the depth of the message that triggered the run (<see cref="WorkflowRun.Invocation"/>), the depth its plug-ins see;
/// the activity's own requests run one level deeper, as the run's built-in steps do.</item>
/// <item><c>PrimaryEntityName</c>, <c>PrimaryEntityId</c>: the record the process runs on. <c>MessageName</c>: the triggering message.</item>
/// <item><c>InputParameters</c>, <c>OutputParameters</c>, <c>SharedVariables</c>: the triggering message's for a real-time run or an action
/// (the same collections its plug-ins see), empty for a background run. No entity images.</item>
/// <item><c>Mode</c> (plug-in convention: 0 synchronous, 1 asynchronous) and <c>WorkflowMode</c> (Learn: 0 asynchronous, 1 synchronous)
/// both say whether the run is a background one. <c>WorkflowCategory</c>: the process's category.</item>
/// <item><c>StageName</c>: <c>PreOperation</c> (stage 20), <c>MainOperation</c> (an action, 30) or <c>PostOperation</c> (stage 40 and
/// background runs, which start after the post-operation of their message).</item>
/// <item><c>ParentContext</c>: null. <c>OwningExtension</c>: the process (<c>workflow</c>) that runs.</item>
/// </list>
/// A member the installed interface has and this list does not (a later SDK) fails with a <see cref="NotSupportedException"/>
/// naming it, never a silent default.
/// </remarks>
public static class WorkflowContextProxy
{
    /// <summary>The full name of the interface in the workflow SDK.</summary>
    public const string InterfaceName = "Microsoft.Xrm.Sdk.Workflow.IWorkflowContext";

    /// <summary>An object implementing <paramref name="workflowContextInterface"/> for <paramref name="run"/>.</summary>
    /// <param name="workflowContextInterface">The installed <c>IWorkflowContext</c> (it must extend <see cref="IExecutionContext"/>).</param>
    /// <param name="run">The run the activity is a step of.</param>
    /// <param name="organization">The organization's id and name (empty when unknown).</param>
    public static object Create(Type workflowContextInterface, WorkflowRun run, (Guid Id, string Name) organization = default)
    {
        ArgumentNullException.ThrowIfNull(workflowContextInterface);
        ArgumentNullException.ThrowIfNull(run);
        if (!workflowContextInterface.IsInterface || !typeof(IExecutionContext).IsAssignableFrom(workflowContextInterface))
            throw new ArgumentException($"{workflowContextInterface.FullName} is not an interface extending IExecutionContext.", nameof(workflowContextInterface));

        var background = run.Stage == 0;
        var backing = Values(run, organization);
        var proxy = (WorkflowContextDispatchProxy)DispatchProxy.Create(workflowContextInterface, typeof(WorkflowContextDispatchProxy));
        proxy.Initialize(workflowContextInterface, backing, StageNameOf(run.Stage), run.Workflow.Category, background ? 0 : 1);
        return proxy;
    }

    /// <summary>The <see cref="IExecutionContext"/> values of <paramref name="run"/>, as listed on the class (also those of its traces).</summary>
    internal static XrmdPluginExecutionContext Values(WorkflowRun run, (Guid Id, string Name) organization = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var background = run.Stage == 0;
        var parent = run.Invocation.Parent;
        return new XrmdPluginExecutionContext
        {
            Mode = background ? 1 : 0,
            Depth = run.Invocation.Depth,
            MessageName = run.MessageName ?? "",
            PrimaryEntityName = run.Primary.LogicalName,
            PrimaryEntityId = run.Primary.Id,
            UserId = run.Caller.UserId,
            InitiatingUserId = run.Invocation.InitiatingUserId,
            BusinessUnitId = run.Caller.BusinessUnitId,
            OrganizationId = organization.Id,
            OrganizationName = organization.Name ?? "",
            CorrelationId = run.Invocation.CorrelationId,
            OperationCreatedOn = run.Now,
            IsInTransaction = run.Invocation.Session?.Transaction is not null,
            OwningExtension = new EntityReference("workflow", run.Workflow.Id),
            InputParameters = parent?.InputParameters ?? new(),
            OutputParameters = parent?.OutputParameters ?? new(),
            SharedVariables = parent?.SharedVariables ?? new(),
        };
    }

    /// <summary>The <c>StageName</c> of a run at <paramref name="stage"/> (0 for a background run).</summary>
    public static string StageNameOf(int stage) => stage switch
    {
        20 => "PreOperation",
        30 => "MainOperation",
        _ => "PostOperation",
    };
}

/// <summary>The dispatch of <see cref="WorkflowContextProxy"/> (public only because <see cref="DispatchProxy"/> derives from it).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public class WorkflowContextDispatchProxy : DispatchProxy
{
    private Type _interface = null!;
    private XrmdPluginExecutionContext _backing = null!;
    private string? _stageName;
    private int _category;
    private int _workflowMode;

    internal void Initialize(Type workflowContextInterface, XrmdPluginExecutionContext backing, string? stageName, int category, int workflowMode)
    {
        _interface = workflowContextInterface;
        _backing = backing;
        _stageName = stageName;
        _category = category;
        _workflowMode = workflowMode;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var declaring = targetMethod.DeclaringType;
        if (declaring == _interface)
            return targetMethod.Name switch
            {
                "get_StageName" => _stageName,
                "get_WorkflowCategory" => _category,
                "get_WorkflowMode" => _workflowMode,
                "get_ParentContext" => null,
                _ => throw Unsupported(targetMethod),
            };
        if (declaring is not null && declaring.IsInterface && declaring.IsAssignableFrom(typeof(XrmdPluginExecutionContext)))
        {
            try
            {
                return targetMethod.Invoke(_backing, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }

        throw Unsupported(targetMethod);
    }

    private static NotSupportedException Unsupported(MethodInfo method) =>
        new($"xrmverse does not implement {method.DeclaringType?.FullName}.{method.Name} of the workflow context.");
}
