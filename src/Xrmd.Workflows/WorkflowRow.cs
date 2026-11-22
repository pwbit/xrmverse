using Microsoft.Xrm.Sdk;

namespace Xrmd.Workflows;

/// <summary>
/// A <c>workflow</c> row (plan 2.14, ruling W1): what <see cref="WorkflowStore"/> reads and writes, and what decides whether its
/// definition can be activated (W4). Option values are the stored numbers (see <c>WorkflowSchema</c>).
/// </summary>
public sealed record WorkflowRow
{
    /// <summary>The <c>workflowid</c>.</summary>
    public Guid Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>0 Background, 1 Real-time.</summary>
    public int Mode { get; init; }

    /// <summary>0 Workflow, 2 Business Rule, 3 Action run; other categories are kept and never run (W2).</summary>
    public int Category { get; init; }

    /// <summary>1 User, 2 Business Unit, 3 Parent: Child Business Units, 4 Organization (W5).</summary>
    public int Scope { get; init; } = 1;

    /// <summary>1 Definition, 2 Activation (a copy made by the activation, W4), 3 Template.</summary>
    public int Type { get; init; } = 1;

    /// <summary>The <c>uniquename</c>: the message name of an action (category 3, W14).</summary>
    public string? UniqueName { get; init; }

    /// <summary>The <c>primaryentity</c>; <c>none</c> for a global action.</summary>
    public string? PrimaryEntity { get; init; }

    /// <summary>The definition's XAML, stored byte for byte as imported.</summary>
    public string? Xaml { get; init; }

    public bool TriggerOnCreate { get; init; }

    public bool TriggerOnDelete { get; init; }

    /// <summary>
    /// The <c>triggeronupdateattributelist</c> (comma-separated logical names). Null: no update trigger. Not null but
    /// without any column name: an update trigger with an empty list, which is invalid (W4). <see cref="WorkflowStore"/> reads an
    /// empty stored list as null.
    /// </summary>
    public string? TriggerOnUpdateAttributes { get; init; }

    /// <summary>20 Pre-operation, 40 Post-operation; real-time only (background runs ignore stages).</summary>
    public int? CreateStage { get; init; }

    public int? UpdateStage { get; init; }

    public int? DeleteStage { get; init; }

    /// <summary>0 Owner, 1 Calling User (real-time workflows, W7).</summary>
    public int RunAs { get; init; } = 1;

    /// <summary>The order among the real-time workflows and plug-in steps of a stage (W6).</summary>
    public int Rank { get; init; }

    public bool OnDemand { get; init; }

    public bool Subprocess { get; init; }

    /// <summary>A succeeded background job is deleted instead of kept (W9).</summary>
    public bool AsyncAutoDelete { get; init; }

    public bool SyncWorkflowLogOnFailure { get; init; }

    /// <summary>Stored as imported; an action always runs in the caller's transaction (W14).</summary>
    public bool IsTransacted { get; init; } = true;

    public string? InputParameters { get; init; }

    public string? IntroducedVersion { get; init; }

    public bool IsCustomizable { get; init; } = true;

    /// <summary>0 Draft, 1 Activated.</summary>
    public int StateCode { get; init; }

    /// <summary>The owner (a user or a team); <see cref="WorkflowStore"/> gives a new row without one to the SYSTEM user.</summary>
    public EntityReference? OwnerId { get; init; }

    /// <summary>The <c>owningbusinessunit</c> (read only: the core sets it from the owner); the scope of a trigger compares it (W5).</summary>
    public Guid? OwningBusinessUnitId { get; init; }

    /// <summary>On an activation (type 2): its definition.</summary>
    public Guid? ParentWorkflowId { get; init; }

    /// <summary>On an activated definition: its activation.</summary>
    public Guid? ActiveWorkflowId { get; init; }

    /// <summary>On an action (category 3): its message (W14).</summary>
    public Guid? SdkMessageId { get; init; }

    /// <summary>The steps xrmverse cannot run, one per line (<c>xrmverse_unsupportedsteps</c>, written at activation, W4).</summary>
    public string? UnsupportedSteps { get; init; }

    /// <summary>
    /// The <c>processtriggerscope</c> of a business rule: 1 Form, 2 Entity, 3 All Forms; null when the file has none (plan 2.14,
    /// Task 3b).
    /// </summary>
    public int? ProcessTriggerScope { get; init; }

    /// <summary>
    /// True when <see cref="ProcessTriggerScope"/> is 2 (Entity) or a <c>processtrigger</c> of the definition has <c>scope</c> 2: a
    /// business rule run on the server too (W15 as amended by plan 2.14 Task 3b). Computed by <see cref="WorkflowStore"/>; not a column.
    /// </summary>
    public bool EntityScopeRule { get; init; }
}
