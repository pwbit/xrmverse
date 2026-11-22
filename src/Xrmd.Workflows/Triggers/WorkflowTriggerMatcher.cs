using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.Triggers;

/// <summary>
/// Which activated processes a message triggers (plan 2.14, rulings W5, W6 and W15). Pure: the caller supplies the activated rows,
/// the columns the request sent and how to find business units.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Create → <c>triggeroncreate</c>; Update → <c>triggeronupdateattributelist</c> ∩ the columns sent (a column sent with an
/// unchanged value counts); Assign → the list holds <c>ownerid</c>; SetState → the list holds a column sent (<c>statecode</c>,
/// <c>statuscode</c>); Delete → <c>triggerondelete</c>.</item>
/// <item>Real-time (<c>stage</c> 20 or 40): the process's stage for the message, 40 for Create and Update and 20 for Delete when the
/// row has none (the designer's defaults, checklist 1d). Background (<c>stage</c> null): no stage.</item>
/// <item>Scope (W5): 1 the record's owner is the workflow's owner, 2 same business unit, 3 the workflow owner's unit or one below it,
/// 4 any. No record owner (<see cref="Guid.Empty"/>: an organization-owned table) matches every scope.</item>
/// <item>Business rules (category 2, W15 as amended by Task 3b): only an Entity scope rule
/// (<see cref="WorkflowRow.EntityScopeRule"/>), at stage 20 of Create and Update whatever columns change; scope does not filter them.</item>
/// <item>Draft rows, actions (category 3) and every other category never match.</item>
/// </list>
/// </remarks>
public static class WorkflowTriggerMatcher
{
    private const string OwnerId = "ownerid";

    /// <summary>The processes of <paramref name="activated"/> that the message triggers, ordered by rank then id (W6).</summary>
    /// <param name="activated">The activated processes (their activation rows).</param>
    /// <param name="message">Create, Update, Assign, SetState or Delete (any other message matches nothing).</param>
    /// <param name="entity">The logical name of the record's table.</param>
    /// <param name="sentColumns">The columns of the request's <c>Target</c> (Update and SetState).</param>
    /// <param name="recordOwnerId">The record's owner (user or team), <see cref="Guid.Empty"/> when its table has none.</param>
    /// <param name="businessUnitOf">
    /// The business unit of a principal (user or team: the record's owner and the workflows' owners), and the parent of a business
    /// unit (<see cref="Guid.Empty"/> for the root or an unknown id). Called only for scopes 2 and 3.
    /// </param>
    /// <param name="stage">20 or 40 for real-time processes, null for background ones.</param>
    /// <param name="mode">0 background, 1 real-time.</param>
    public static IReadOnlyList<WorkflowRow> Match(IReadOnlyList<WorkflowRow> activated, string message, string entity,
        IReadOnlyCollection<string> sentColumns, Guid recordOwnerId, Func<Guid, Guid> businessUnitOf, int? stage, int mode)
    {
        ArgumentNullException.ThrowIfNull(activated);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(sentColumns);
        ArgumentNullException.ThrowIfNull(businessUnitOf);

        return activated
            .Where(row => row.StateCode == S.ActivatedState && string.Equals(row.PrimaryEntity, entity, StringComparison.OrdinalIgnoreCase))
            .Where(row => row.Category switch
            {
                (int)S.Category.Workflow => row.Mode == mode && Triggers(row, message, sentColumns, stage)
                                            && InScope(row, recordOwnerId, businessUnitOf),
                (int)S.Category.BusinessRule => row.EntityScopeRule && mode == (int)S.Mode.Realtime && stage == (int)S.Stage.Pre
                                                && message is "Create" or "Update",
                _ => false,
            })
            .OrderBy(row => row.Rank).ThenBy(row => row.Id)
            .ToList();
    }

    private static bool Triggers(WorkflowRow row, string message, IReadOnlyCollection<string> sentColumns, int? stage)
    {
        switch (message)
        {
            case "Create":
                return row.TriggerOnCreate && AtStage(row.CreateStage, (int)S.Stage.Post, stage);
            case "Update":
            case "SetState":
                return AtStage(row.UpdateStage, (int)S.Stage.Post, stage) && Lists(row, sentColumns);
            case "Assign":
                return AtStage(row.UpdateStage, (int)S.Stage.Post, stage) && Lists(row, [OwnerId]);
            case "Delete":
                return row.TriggerOnDelete && AtStage(row.DeleteStage, (int)S.Stage.Pre, stage);
            default:
                return false;
        }
    }

    private static bool AtStage(int? rowStage, int defaultStage, int? stage) => stage is null || (rowStage ?? defaultStage) == stage;

    private static bool Lists(WorkflowRow row, IReadOnlyCollection<string> columns) =>
        row.TriggerOnUpdateAttributes is { } list
        && list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(listed => columns.Contains(listed, StringComparer.OrdinalIgnoreCase));

    private static bool InScope(WorkflowRow row, Guid recordOwnerId, Func<Guid, Guid> businessUnitOf)
    {
        if (recordOwnerId == Guid.Empty || row.Scope == 4) return true;
        var workflowOwner = row.OwnerId?.Id ?? Guid.Empty;
        switch (row.Scope)
        {
            case 1:
                return recordOwnerId == workflowOwner;
            case 2:
                return businessUnitOf(recordOwnerId) is var unit && unit != Guid.Empty && unit == businessUnitOf(workflowOwner);
            case 3:
                var top = businessUnitOf(workflowOwner);
                if (top == Guid.Empty) return false;
                // Up from the record owner's unit; the bound keeps a cycle in the data from looping.
                var current = businessUnitOf(recordOwnerId);
                for (var i = 0; current != Guid.Empty && i < 1000; i++, current = businessUnitOf(current))
                    if (current == top) return true;
                return false;
            default:
                return false; // not a valid scope: never triggered rather than widened
        }
    }
}
