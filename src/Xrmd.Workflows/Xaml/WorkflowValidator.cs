namespace Xrmd.Workflows.Xaml;

/// <summary>
/// The activation checks of a parsed definition against its row (W4, with the rules of W2, W6, W10, W14 and W15).
/// An empty list means the definition can be activated. <see cref="UnsupportedStep"/>s are not errors here: they are
/// recorded at activation and make the process fail when it is triggered (W4, W20).
/// </summary>
public static class WorkflowValidator
{
    private const int RealTime = 1;

    public static IReadOnlyList<string> Validate(WorkflowDefinition d, WorkflowRow row)
    {
        var errors = new List<string>();

        if (row.Category is not (0 or 2 or 3))
        {
            errors.Add($"Processes of category {row.Category} are not run by xrmverse.");
            return errors;
        }
        if (row.Mode is not (0 or 1))
            errors.Add($"The mode {row.Mode} is not valid: use 0 (background) or 1 (real-time).");

        // W10: waits suspend a background job; real-time workflows, actions and business rules run in a transaction.
        var waitsInvalid = row.Category is 2 or 3 || row.Mode == RealTime;
        if (waitsInvalid)
        {
            var what = row.Category switch { 2 => "a business rule", 3 => "an action", _ => "a real-time workflow" };
            foreach (var wait in d.Root.DepthFirst().OfType<WaitStep>())
                errors.Add($"The step {wait.Label} is a wait, which {what} cannot contain.");
        }

        switch (row.Category)
        {
            case 0:
                ValidateTriggers(row, errors);
                break;
            case 2 when string.IsNullOrWhiteSpace(row.PrimaryEntity) || row.PrimaryEntity == "none":
                errors.Add("A business rule needs a primary entity.");
                break;
            case 3 when string.IsNullOrWhiteSpace(row.UniqueName):
                errors.Add("An action needs a unique name, which is its message name.");
                break;
        }

        return errors;
    }

    /// <summary>
    /// The columns the waits of <paramref name="d"/> read (their condition and their timeout), with the record each is read from
    /// (plan 2.12b, ruling R11: a wait condition cannot use a rollup column, Learn).
    /// </summary>
    public static IEnumerable<(string Step, RecordRef Record, string Attribute)> WaitColumns(WorkflowDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        foreach (var wait in d.Root.DepthFirst().OfType<WaitStep>())
        {
            var found = new List<ValueExpr.AttributeOf>();
            if (wait.Until is { } until) Collect(until, found);
            if (wait.Timeout is { } timeout) Collect(timeout, found);
            foreach (var column in found) yield return (wait.Label, column.Record, column.Attribute);
        }
    }

    private static void Collect(ConditionExpr condition, List<ValueExpr.AttributeOf> found)
    {
        switch (condition)
        {
            case ConditionExpr.Compare c:
                Collect(c.Left, found);
                if (c.Right is { } right) Collect(right, found);
                break;
            case ConditionExpr.And a:
                Collect(a.Left, found);
                Collect(a.Right, found);
                break;
            case ConditionExpr.Or o:
                Collect(o.Left, found);
                Collect(o.Right, found);
                break;
            case ConditionExpr.Not n:
                Collect(n.Operand, found);
                break;
        }
    }

    private static void Collect(ValueExpr value, List<ValueExpr.AttributeOf> found)
    {
        switch (value)
        {
            case ValueExpr.AttributeOf a:
                found.Add(a);
                break;
            case ValueExpr.Conversion c:
                Collect(c.Operand, found);
                break;
            case ValueExpr.Cast c:
                Collect(c.Operand, found);
                break;
            case ValueExpr.Arithmetic a:
                foreach (var operand in a.Operands) Collect(operand, found);
                break;
            case ValueExpr.Concat c:
                foreach (var part in c.Parts) Collect(part, found);
                break;
            case ValueExpr.FirstNonNull f:
                foreach (var operand in f.Operands) Collect(operand, found);
                break;
            case ValueExpr.FunctionCall f:
                foreach (var arg in f.Args) Collect(arg, found);
                break;
        }
    }

    private static void ValidateTriggers(WorkflowRow row, List<string> errors)
    {
        if (row.TriggerOnUpdateAttributes is { } list && !list.Split(',').Any(c => c.Trim().Length > 0))
            errors.Add("The update trigger lists no columns.");

        // W6: stages are real-time only; background runs ignore them.
        if (row.Mode != RealTime)
            return;
        if (row.TriggerOnCreate)
            CheckStage("create", row.CreateStage, allowPost: true, errors);
        if (row.TriggerOnUpdateAttributes is not null)
            CheckStage("update", row.UpdateStage, allowPost: true, errors);
        if (row.TriggerOnDelete)
            CheckStage("delete", row.DeleteStage, allowPost: false, errors);
    }

    private static void CheckStage(string trigger, int? stage, bool allowPost, List<string> errors)
    {
        if (stage is null or 20 || (allowPost && stage == 40))
            return;
        errors.Add(allowPost
            ? $"The {trigger} stage {stage} is not valid: a real-time workflow runs at 20 (pre-operation) or 40 (post-operation)."
            : $"The {trigger} stage {stage} is not valid: a real-time workflow runs on delete only at 20 (pre-operation).");
    }
}
