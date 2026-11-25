using Xrmd.Abstractions;

namespace Xrmd.Pipeline.Connector;

/// <summary>
/// X.12b: told by the <see cref="PipelineExecutor"/>, in the write's transaction, of each row a Create, Update (SetState included),
/// Assign or Delete writes: a Create or Update at post-operation, a Delete after pre-operation and before the core delete (the row can
/// still be read, so the subscriber's access to it is checked; <see cref="RowChanges.BeforeCoreOperation"/>). The flow trigger's
/// subscriptions queue their notifications there. Implemented outside
/// the pipeline (it must not depend on the executor).
/// </summary>
public interface IRowChangeHook
{
    /// <summary>Answers from memory whether <paramref name="table"/> may have subscribers; false means nothing is done for the write.</summary>
    bool MayHave(string table);

    /// <summary>Queues what the change of the row <paramref name="id"/> notifies.</summary>
    /// <param name="message">The change: <see cref="RowChanges.Create"/>, <see cref="RowChanges.Update"/> or <see cref="RowChanges.Delete"/>.</param>
    /// <param name="sentColumns">The columns the request sent (an Assign sends <c>ownerid</c>, a Delete none).</param>
    /// <returns>True when a job was queued (the executor then signals the asynchronous worker after the commit).</returns>
    Task<bool> OnChangedAsync(string message, string table, Guid id, IReadOnlyCollection<string> sentColumns, DataSession s, CancellationToken ct);
}

/// <summary>X.12b: the change names and the dispatch of a write to an <see cref="IRowChangeHook"/>.</summary>
public static class RowChanges
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";

    private static readonly string[] AssignColumns = ["ownerid"];

    /// <summary>
    /// True when the change of <paramref name="requestName"/> is told after pre-operation, before the core operation (a Delete: the row
    /// is gone after it), false when it is told at post-operation. The executor tells each write once, at one of the two.
    /// </summary>
    public static bool BeforeCoreOperation(string requestName) => ChangeOf(requestName) == Delete;

    /// <summary>The change of the request <paramref name="requestName"/> (a SetState runs as an Update); null for any other message.</summary>
    public static string? ChangeOf(string requestName) => requestName switch
    {
        _ when string.Equals(requestName, "Create", StringComparison.OrdinalIgnoreCase) => Create,
        _ when string.Equals(requestName, "Update", StringComparison.OrdinalIgnoreCase) => Update,
        _ when string.Equals(requestName, "Assign", StringComparison.OrdinalIgnoreCase) => Update,
        _ when string.Equals(requestName, "Delete", StringComparison.OrdinalIgnoreCase) => Delete,
        _ => null,
    };

    /// <summary>
    /// Passes the write to <paramref name="hook"/> when it is a row change of <paramref name="table"/> that may have subscribers; the
    /// <paramref name="sentColumns"/> are read only then. A bulk message (CreateMultiple, UpdateMultiple, Upsert) is not passed: its
    /// inner Create and Update are.
    /// </summary>
    /// <returns>True when the hook queued a job.</returns>
    public static async Task<bool> NotifyAsync(IRowChangeHook? hook, string requestName, string? table, Guid id,
        Func<IReadOnlyCollection<string>> sentColumns, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requestName);
        ArgumentNullException.ThrowIfNull(sentColumns);
        if (hook is null || table is null || id == Guid.Empty || ChangeOf(requestName) is not { } change || !hook.MayHave(table)) return false;
        IReadOnlyCollection<string> columns = change == Delete ? []
            : string.Equals(requestName, "Assign", StringComparison.OrdinalIgnoreCase) ? AssignColumns
            : sentColumns();
        return await hook.OnChangedAsync(change, table, id, columns, s, ct);
    }
}
