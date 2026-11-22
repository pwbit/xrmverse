namespace Xrmd.Abstractions;

/// <summary>
/// Told by the core of every change of an existing record row, inside the write's session and transaction, right after its SQL
/// (the row is locked by the write): an Update (Assign, SetState, the state change of SendEmail, a file column and an elastic Upsert
/// come through it), a Delete (before the links to the record are cleared), and the lookups a Delete clears (RemoveLink). It writes
/// through the session, so what it does commits or rolls back with the write; an exception fails the write.
/// </summary>
/// <remarks>
/// Plan 2.14 (W10): the background workflow jobs waiting on a record are woken by any core write of it. A Create is not told: no
/// one can be waiting on a record that did not exist.
/// </remarks>
public interface IRecordWriteObserver
{
    /// <summary>
    /// The row <paramref name="id"/> of <paramref name="entity"/> was updated; <paramref name="id"/> null: some rows of the table were,
    /// by one set-based write whose ids are not known.
    /// </summary>
    Task OnWrittenAsync(DataSession s, string entity, Guid? id, CancellationToken ct);

    /// <summary>The row <paramref name="id"/> of <paramref name="entity"/> was deleted.</summary>
    Task OnDeletedAsync(DataSession s, string entity, Guid id, CancellationToken ct);

    /// <summary>
    /// The row <paramref name="id"/> of <paramref name="entity"/> is about to be deleted, in the Delete's transaction: its delete rules
    /// have passed and nothing of it is deleted yet (its cascades neither). A fault refuses the Delete.
    /// </summary>
    Task OnDeletingAsync(DataSession s, string entity, Guid id, CancellationToken ct) => Task.CompletedTask;
}
