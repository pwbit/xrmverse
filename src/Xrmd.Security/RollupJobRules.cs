using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Model.Records;

namespace Xrmd.Security;

/// <summary>
/// The administration of the rollup calculation jobs (plan 2.12b, ruling R9), called by <see cref="SystemTableRules"/> for every caller:
/// on a job of operationtype 57 (Calculate Rollup Field) or 58 (Mass Calculate Rollup Field), an Update sets <c>postponeuntil</c>
/// alone (Postpone) or, on 57, <c>recurrencepattern</c> alone (Modify Recurrence, <see cref="RollupRecurrence"/>); a cancellation and a
/// Delete are refused (the jobs live as long as their rollups), and so are a pause and a resume (not replicated).
/// </summary>
public static class RollupJobRules
{
    /// <summary>The operation types of the rollup jobs (as <c>AsyncOperationQueue.CalculateRollupField</c> and <c>MassCalculateRollupField</c>).</summary>
    private const int CalculateRollupField = 57, MassCalculateRollupField = 58;

    private const string Table = "asyncoperation";
    private const string PostponeUntil = "postponeuntil", RecurrencePattern = "recurrencepattern";
    private const string StateCode = "statecode", StatusCode = "statuscode", OwnerId = "ownerid";
    private const int Completed = 3, Canceled = 32;

    /// <summary>The refusal of a cancellation or a Delete.</summary>
    public const string NotCancelledOrDeleted = "Rollup calculation jobs cannot be cancelled or deleted: postpone them instead.";

    /// <summary>The refusal of a pause or a resume (any other change of state).</summary>
    public const string NotPausedOrResumed =
        "Rollup calculation jobs cannot be paused or resumed by xrmverse: postpone them (postponeuntil) instead.";

    /// <summary>The refusal of any other Update.</summary>
    public const string OnlyPostponeOrRecurrence =
        "Only postponeuntil can be changed on a rollup calculation job, or recurrencepattern on a Calculate Rollup Field job, one at a time.";

    /// <summary>
    /// The rules of an Update of the job <paramref name="id"/> when it is a rollup job (read with UPDLOCK).
    /// </summary>
    /// <returns>True when the job is a rollup job (and the Update is allowed); false for any other job, left to the other rules.</returns>
    /// <exception cref="XrmdFaultException">400 for a refused Update; 400 <c>0x8004D2A2</c> for a recurrence below one hour.</exception>
    public static async Task<bool> DemandUpdateAsync(Record target, Guid id, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (await OperationTypeAsync(id, session, ct) is not ({ } type and (CalculateRollupField or MassCalculateRollupField))) return false;

        if (target.Attributes.ContainsKey(StateCode) || target.Attributes.ContainsKey(StatusCode))
        {
            var cancel = target.SdkValue<ChoiceValue>(StateCode)?.Value == Completed
                         && target.SdkValue<ChoiceValue>(StatusCode)?.Value is null or Canceled;
            throw XrmdFaultException.InvalidArgument(cancel ? NotCancelledOrDeleted : NotPausedOrResumed);
        }

        var columns = target.Attributes.Keys.Where(n => n == OwnerId || !SystemAttributes.IsReservedName(Table, n)).ToList();
        if (columns is [PostponeUntil] && target[PostponeUntil] is DateTime) return true;
        if (columns is [RecurrencePattern] && type == CalculateRollupField && target[RecurrencePattern] is string pattern)
        {
            RollupRecurrence.Parse(pattern);
            return true;
        }
        throw XrmdFaultException.InvalidArgument(OnlyPostponeOrRecurrence);
    }

    /// <summary>A Delete of the job <paramref name="id"/> is refused when it is a rollup job.</summary>
    /// <exception cref="XrmdFaultException">400 <see cref="NotCancelledOrDeleted"/>.</exception>
    public static async Task DemandNotDeletedAsync(Guid id, DataSession session, CancellationToken ct)
    {
        if (await OperationTypeAsync(id, session, ct) is CalculateRollupField or MassCalculateRollupField)
            throw XrmdFaultException.InvalidArgument(NotCancelledOrDeleted);
    }

    /// <summary>The <c>operationtype</c> of the job (read with UPDLOCK); null when it does not exist or has none.</summary>
    private static async Task<int?> OperationTypeAsync(Guid id, DataSession session, CancellationToken ct)
    {
        if (id == Guid.Empty) return null;
        await using var cmd = session.Command(
            $"SELECT operationtype FROM {session.DataTable(Table)}{session.Dialect.LockHint(RowLock.Update)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(Table))} = @_rollupjob{session.Dialect.LockClause(RowLock.Update)}");
        cmd.AddParameter("@_rollupjob", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as int?;
    }
}
