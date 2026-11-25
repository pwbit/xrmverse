using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline.Bulk;
using Xrmd.Pipeline.Registration;

namespace Xrmd.Pipeline.Merge;

/// <summary>
/// A step a stage runs. <paramref name="MultipleView"/>: the step is registered on <c>CreateMultiple</c>/<c>UpdateMultiple</c>
/// and runs on a single-record <c>Create</c>/<c>Update</c>, so it sees <see cref="MergedSteps.MultipleView"/> of the context.
/// </summary>
internal sealed record StageStep(PluginStep Step, bool MultipleView);

/// <summary>
/// Which steps a stage runs (#76, rulings B5 and B7). A <c>Create</c> or <c>Update</c> on a standard table that is not the
/// per-target run of a bulk message (<see cref="InnerKind.Bulk"/>, whose bulk steps already fired) also runs
/// the steps registered on its bulk message (<see cref="SdkMessageCatalog.MergePairs"/>), merged with its own by rank, then
/// step id. Those steps see a <see cref="MultipleView"/> of the context. The Create/Update of an Upsert (also within an
/// UpsertMultiple) does merge: Upsert and UpsertMultiple are no events on a standard table, so nothing fires twice. Every other
/// execution runs its own steps only.
/// </summary>
internal static class MergedSteps
{
    private const string Target = "Target";
    private const string Ids = "Ids";

    /// <summary>
    /// The steps of <paramref name="request"/>'s message on <paramref name="entity"/> for <paramref name="stage"/> and
    /// <paramref name="mode"/> whose filtering attributes match (<see cref="Applies"/>), merged with the steps of the bulk
    /// message when <paramref name="standardTable"/> and the request is a Create or Update that no bulk message runs per target.
    /// </summary>
    /// <param name="standardTable">True when <paramref name="entity"/> is a standard (not elastic) table: the merge is not built for
    /// elastic tables (ruling B8, E5 kept).</param>
    /// <param name="bulkInner">True when the request is the per-target run of a bulk message (marked <see cref="InnerKind.Bulk"/>).</param>
    public static async Task<IReadOnlyList<StageStep>> ForStageAsync(StepRegistry registry, Request request, string? entity,
        int stage, int mode, bool standardTable, bool bulkInner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(request);
        var own = (await registry.GetStepsAsync(request.Name!, entity, stage, mode, ct))
            .Where(s => Applies(s, request)).Select(s => new StageStep(s, false));
        if (!standardTable || entity is null || bulkInner
            || !SdkMessageCatalog.MergePairs.TryGetValue(request.Name ?? "", out var bulkMessage))
            return own.ToList();

        var bulk = (await registry.GetStepsAsync(bulkMessage, entity, stage, mode, ct))
            .Where(s => Applies(s, request)).Select(s => new StageStep(s, true));
        return own.Concat(bulk).OrderBy(s => s.Step.Rank).ThenBy(s => s.Step.Id).ToList();
    }

    /// <summary>
    /// True when <paramref name="step"/> fires for <paramref name="request"/> as far as its filtering attributes go (ruling B7):
    /// always without filtering attributes or for a message other than <c>Update</c>/<c>UpdateMultiple</c>; for an Update, when
    /// its <c>Target</c> carries one of them; for an UpdateMultiple, when any of its <c>Targets</c> does. A step merged into a
    /// single Update is matched against that Update's target.
    /// </summary>
    public static bool Applies(PluginStep step, Request request)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(request);
        if (step.FilteringAttributes is not { } filter) return true;
        IEnumerable<Record?> targets;
        if (string.Equals(request.Name, "Update", StringComparison.OrdinalIgnoreCase))
            targets = [request.Parameters.TryGetValue(Target, out var t) ? t as Record : null];
        else if (string.Equals(request.Name, BulkTargets.UpdateMultiple, StringComparison.OrdinalIgnoreCase))
            targets = request.Parameters.TryGetValue(BulkTargets.TargetsParameter, out var t) && t is RecordCollection c ? c.Records : [];
        else
            return true;
        return targets.Any(e => e is not null && filter.Any(a => e.Attributes.ContainsKey(a)));
    }

    /// <summary>
    /// The context a <c>CreateMultiple</c>/<c>UpdateMultiple</c> step sees on a single Create/Update (ruling B5): a copy of
    /// <paramref name="single"/> named after the bulk message, whose <c>InputParameters</c> are the request's with <c>Target</c>
    /// replaced by <c>Targets</c>, a <see cref="RecordCollection"/> of the table holding the <b>same</b> <see cref="Record"/> (a
    /// change to it is saved; a replaced item is not carried back), and whose <c>OutputParameters</c> hold <c>Ids</c> = [the new
    /// id] once a Create's stage 30 has run. <c>SharedVariables</c> is the same object as the single-record context's. The images
    /// are left to the caller (one-element collections).
    /// </summary>
    public static StepContext MultipleView(StepContext single, Request request)
    {
        ArgumentNullException.ThrowIfNull(single);
        ArgumentNullException.ThrowIfNull(request);
        var view = single.Clone(single.Stage);
        view.MessageName = SdkMessageCatalog.MergePairs.TryGetValue(single.MessageName ?? "", out var bulkMessage)
            ? bulkMessage
            : throw new InvalidOperationException($"'{single.MessageName}' has no bulk message to view it as: only Create and Update merge.");

        var input = new ParameterBag();
        foreach (var (key, value) in request.Parameters)
            if (key != Target) input[key] = value;
        var target = request.Parameters.TryGetValue(Target, out var t) ? t as Record : null;
        input[BulkTargets.TargetsParameter] = new RecordCollection { Records = target is null ? [] : [target], EntityName = single.PrimaryEntityName };
        view.InputParameters = input;

        var output = new ParameterBag();
        if (single.OutputParameters.TryGetValue("id", out var id) && id is Guid created) output[Ids] = new[] { created };
        view.OutputParameters = output;
        return view;
    }
}
