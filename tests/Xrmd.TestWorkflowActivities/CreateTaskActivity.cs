using System;
using System.Activities;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Workflow;

namespace Xrmd.TestWorkflowActivities
{
    /// <summary>
    /// Creates a task about an account through the activity's own organization service and returns its reference: the
    /// shape of a typical custom workflow activity (input arguments, a reference target, an output argument).
    /// </summary>
    public sealed class CreateTaskActivity : CodeActivity
    {
        [RequiredArgument]
        [Input("Subject")]
        public InArgument<string> Subject { get; set; }

        [Input("Account")]
        [ReferenceTarget("account")]
        public InArgument<EntityReference> Account { get; set; }

        [Output("Task")]
        [ReferenceTarget("task")]
        public OutArgument<EntityReference> TaskId { get; set; }

        protected override void Execute(CodeActivityContext executionContext)
        {
            var context = executionContext.GetExtension<IWorkflowContext>();
            var factory = executionContext.GetExtension<IOrganizationServiceFactory>();
            var tracing = executionContext.GetExtension<ITracingService>();
            var service = factory.CreateOrganizationService(context.UserId);

            var task = new Entity("task") { ["subject"] = Subject.Get(executionContext) };
            var account = Account.Get(executionContext);
            if (account != null) task["regardingobjectid"] = account;
            var id = service.Create(task);
            tracing?.Trace("Created task {0} at depth {1} in stage {2}.", id, context.Depth, context.StageName);
            TaskId.Set(executionContext, new EntityReference("task", id));
        }
    }

    /// <summary>
    /// Describes what the activity sees of its run (plan 2.14, Task 8b): the workflow context's depth, stage, primary record, message,
    /// mode and category, its user, and the user an organization service created with <c>null</c> acts as (<c>WhoAmI</c>).
    /// </summary>
    public sealed class DescribeContextActivity : CodeActivity
    {
        [Output("Description")]
        public OutArgument<string> Description { get; set; }

        protected override void Execute(CodeActivityContext executionContext)
        {
            var context = executionContext.GetExtension<IWorkflowContext>();
            var service = executionContext.GetExtension<IOrganizationServiceFactory>().CreateOrganizationService(null);
            var whoAmI = service.Execute(new OrganizationRequest("WhoAmI"));
            Description.Set(executionContext, string.Join(";",
                "depth=" + context.Depth,
                "stage=" + context.StageName,
                "entity=" + context.PrimaryEntityName,
                "id=" + context.PrimaryEntityId,
                "message=" + context.MessageName,
                "mode=" + context.Mode,
                "workflowmode=" + context.WorkflowMode,
                "category=" + context.WorkflowCategory,
                "user=" + context.UserId,
                "service=" + whoAmI.Results["UserId"],
                "parent=" + (context.ParentContext == null ? "null" : "set")));
        }
    }

    /// <summary>
    /// Fails as its input <c>Kind</c> says (plan 2.14, Task 8b fix round 1): <c>abort</c> throws an
    /// <see cref="InvalidPluginExecutionException"/> with <c>Message</c>; <c>crash</c> an <see cref="InvalidOperationException"/>;
    /// <c>swallow</c> creates a record of a table that does not exist, catches the fault and returns normally; <c>stop-succeeded</c>,
    /// <c>stop-canceled</c> and <c>stop-failed</c> throw a <see cref="StopWorkflowException"/> with that status and <c>Message</c>.
    /// </summary>
    public sealed class ThrowActivity : CodeActivity
    {
        [RequiredArgument]
        [Input("Kind")]
        public InArgument<string> Kind { get; set; }

        [Input("Message")]
        public InArgument<string> Message { get; set; }

        protected override void Execute(CodeActivityContext executionContext)
        {
            var message = Message.Get(executionContext);
            switch (Kind.Get(executionContext))
            {
                case "abort":
                    throw new InvalidPluginExecutionException(message);
                case "crash":
                    throw new InvalidOperationException(message);
                case "swallow":
                    var service = executionContext.GetExtension<IOrganizationServiceFactory>().CreateOrganizationService(null);
                    try
                    {
                        service.Create(new Entity("xrmd_nosuchtable"));
                    }
                    catch (System.ServiceModel.FaultException<OrganizationServiceFault>)
                    {
                        // Swallowed: the transaction is doomed all the same.
                    }
                    return;
                case "stop-succeeded":
                    throw new StopWorkflowException(XrmWorkflowCompletionStatus.Succeeded, message);
                case "stop-canceled":
                    throw new StopWorkflowException(XrmWorkflowCompletionStatus.Canceled, message);
                case "stop-failed":
                    throw new StopWorkflowException(XrmWorkflowCompletionStatus.Failed, message);
                default:
                    throw new ArgumentException("Unknown kind.");
            }
        }
    }

    /// <summary>Sleeps <c>Seconds</c> seconds, then creates a task "Slept" (which a timed-out run must never see).</summary>
    public sealed class SleepActivity : CodeActivity
    {
        [RequiredArgument]
        [Input("Seconds")]
        public InArgument<int> Seconds { get; set; }

        protected override void Execute(CodeActivityContext executionContext)
        {
            System.Threading.Thread.Sleep(Seconds.Get(executionContext) * 1000);
            var service = executionContext.GetExtension<IOrganizationServiceFactory>().CreateOrganizationService(null);
            service.Create(new Entity("task") { ["subject"] = "Slept" });
        }
    }

    /// <summary>
    /// A shared base class of activities: abstract, so not registrable itself, but followed as a base type
    /// (<c>DerivedActivity : ActivityBase : CodeActivity</c>).
    /// </summary>
    public abstract class ActivityBase : CodeActivity
    {
        [Output("Message")]
        public OutArgument<string> Message { get; set; }

        protected abstract string Compose(IWorkflowContext context);

        protected override void Execute(CodeActivityContext executionContext) =>
            Message.Set(executionContext, Compose(executionContext.GetExtension<IWorkflowContext>()));
    }

    /// <summary>An activity that derives from <see cref="CodeActivity"/> through <see cref="ActivityBase"/>.</summary>
    public sealed class DerivedActivity : ActivityBase
    {
        protected override string Compose(IWorkflowContext context) => "derived at depth " + context.Depth;
    }

    /// <summary>Not a workflow activity: a plain class of the same assembly (registration must refuse it).</summary>
    public sealed class NotAnActivity
    {
        public override string ToString() => "not an activity";
    }
}
