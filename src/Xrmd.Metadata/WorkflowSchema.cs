namespace Xrmd.Metadata;

/// <summary>
/// The seeded process tables of plan 2.14 (<c>fixtures/seed/workflow.json</c> and <c>processtrigger.json</c>, ruling W1): their
/// names, object type codes, columns, relationships and option values, and the link of a workflow job
/// (<c>asyncoperation.workflowactivationid</c>).
/// </summary>
/// <remarks>
/// Column names and most option values: Learn (workflow, processtrigger and asyncoperation table references). The object type
/// codes 4703 and 4712, the <c>scope</c>, <c>runas</c> and state/status values and the relationship names other than
/// <see cref="AsyncOperationsRelationship"/> are from memory: the plan 2.14 checklist (1a) verifies them on a real environment.
/// </remarks>
public static class WorkflowSchema
{
    /// <summary>The logical names of the two tables.</summary>
    public const string Table = "workflow", TriggerTable = "processtrigger";

    /// <summary>The object type codes of the two tables (from memory).</summary>
    public const int ObjectTypeCode = 4703, TriggerObjectTypeCode = 4712;

    /// <summary>The columns of <see cref="Table"/>.</summary>
    public const string WorkflowId = "workflowid", Name = "name", UniqueName = "uniquename", Description = "description",
        CategoryColumn = "category", ModeColumn = "mode", TypeColumn = "type", ScopeColumn = "scope", PrimaryEntity = "primaryentity",
        Xaml = "xaml", OnDemand = "ondemand", Subprocess = "subprocess", TriggerOnCreate = "triggeroncreate",
        TriggerOnDelete = "triggerondelete", TriggerOnUpdateAttributeList = "triggeronupdateattributelist",
        AsyncAutoDelete = "asyncautodelete", SyncWorkflowLogOnFailure = "syncworkflowlogonfailure", IsTransacted = "istransacted",
        CreateStage = "createstage", UpdateStage = "updatestage", DeleteStage = "deletestage", RunAsColumn = "runas", Rank = "rank",
        ParentWorkflowId = "parentworkflowid", ActiveWorkflowId = "activeworkflowid", SdkMessageId = "sdkmessageid",
        InputParameters = "inputparameters", IntroducedVersion = "introducedversion", IsCustomizable = "iscustomizable";

    /// <summary>
    /// The server-written list of the steps xrmverse cannot run (ruling W4), one per line; null when every step runs. Written at
    /// activation; a process that has any fails explicitly when triggered (W20).
    /// </summary>
    public const string UnsupportedStepsColumn = "xrmverse_unsupportedsteps";

    /// <summary>
    /// The attribute the server sets on the root of a process's kept XML (<c>xrmverse_componentxml</c>) when an accepted Update changes
    /// its name, description or XAML (plan 2.21, ruling R10): the labels the import kept (<c>LocalizedNames</c>, <c>Descriptions</c>,
    /// <c>labels</c>) are stale, and the solution export writes them from the row and the XAML instead. A re-import replaces the XML.
    /// </summary>
    public const string StaleLabelsAttribute = "xrmverseStaleLabels";

    /// <summary>The columns of <see cref="TriggerTable"/>.</summary>
    public const string ProcessTriggerId = "processtriggerid", ProcessId = "processid", PrimaryEntityTypeCode = "primaryentitytypecode",
        Event = "event", ControlName = "controlname", ControlType = "controltype", FormId = "formid", TriggerScope = "scope",
        PipelineStage = "pipelinestage", MethodId = "methodid";

    /// <summary>
    /// The <c>processtrigger.scope</c>, and the <c>workflow.processtriggerscope</c>, of a business rule run on the server too (W15 as
    /// amended by Task 3b); 1 is Form, 3 All Forms (<c>processtriggerscope</c> only).
    /// </summary>
    public const int EntityTriggerScope = 2;

    /// <summary>
    /// The scope of a business rule as its <c>Workflow</c> element writes it (<c>&lt;ProcessTriggerScope&gt;</c> of the parity export,
    /// plan 2.14 Task 3b): Picklist 1 Form, 2 Entity (Learn and the export), 3 All Forms (the brief; not on Learn, checklist 1f).
    /// </summary>
    public const string ProcessTriggerScope = "processtriggerscope";

    /// <summary>The lookup of a workflow job to the activation it runs (Learn).</summary>
    public const string WorkflowActivationId = "workflowactivationid";

    /// <summary>The relationships of the process tables (Learn for <see cref="AsyncOperationsRelationship"/>, the others from memory).</summary>
    public const string ParentRelationship = "workflow_parent_workflow", ActiveRelationship = "workflow_active_workflow",
        TriggersRelationship = "process_processtrigger", AsyncOperationsRelationship = "Workflow_AsyncOperations";

    /// <summary>The <c>asyncoperation.operationtype</c> of a workflow job (Learn).</summary>
    public const int WorkflowOperationType = 10;

    /// <summary>The <c>primaryentity</c> of a global action (category 3).</summary>
    public const string NoPrimaryEntity = "none";

    /// <summary>The <c>category</c> values xrmverse runs (W2); the others are stored and never run.</summary>
    public enum Category { Workflow = 0, BusinessRule = 2, Action = 3 }

    /// <summary>The <c>mode</c> values.</summary>
    public enum Mode { Background = 0, Realtime = 1 }

    /// <summary>The <c>type</c> values xrmverse writes (3 Template is stored as imported).</summary>
    public enum Type { Definition = 1, Activation = 2 }

    /// <summary>The <c>createstage</c>, <c>updatestage</c> and <c>deletestage</c> values.</summary>
    public enum Stage { Pre = 20, Post = 40 }

    /// <summary>The <c>runas</c> values.</summary>
    public enum RunAs { Owner = 0, CallingUser = 1 }

    /// <summary>The <c>statecode</c> values and their only status (<c>statuscode</c> 1 Draft, 2 Activated).</summary>
    public const int DraftState = 0, ActivatedState = 1, DraftStatus = 1, ActivatedStatus = 2;
}
