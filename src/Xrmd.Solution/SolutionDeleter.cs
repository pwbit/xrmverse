using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// What the Delete of a <c>solution</c> row does besides deleting the row, as Dataverse deletes an unmanaged solution
/// (Learn, "Create a solution", Delete an unmanaged solution: "deletes the solution container but doesn't delete any of the
/// unmanaged components within"; "Solution concepts": the customizations "remain in effect and belong to the default
/// solution"): its <c>solutioncomponent</c> rows go, every component stays and is tracked by <c>Default</c>. The publisher
/// stays. The kept opaque XML (<c>solution.xrmverse_unsupportedxml</c>) lives on the row and goes with it.
/// <para>
/// All the writes are <c>data.*</c> writes on the caller's session, in the transaction of the Delete (no metadata write).
/// The components are enumerated by <see cref="ComponentsAsync"/> on their own: the cascading delete of the components (an
/// explicit xrmverse-only command of the Admin API, <c>SolutionCascadeDeleter</c>) acts on that list instead of keeping them,
/// with the same refusals (<see cref="EnsureDeletableAsync(DataSession, Guid, CancellationToken)"/>).
/// </para>
/// <para>
/// The Delete of a managed solution is its uninstall (plan 2.6, ruling M9: <c>SolutionUninstaller</c> in the Core, which
/// refuses with the dependency code 0x8004F01D when another managed solution layers one of its components); this class
/// deletes its row only as the last step of that uninstall. An unmanaged container delete is never refused for dependencies.
/// </para>
/// </summary>
public sealed class SolutionDeleter(SolutionComponentTracker tracker)
{
    /// <summary>The table whose rows this class deletes the content of.</summary>
    public const string Table = "solution";

    /// <summary>The 403 for the system solutions <c>Default</c> and <c>Active</c>.</summary>
    public const string SystemSolutionFixed = "The system solutions Default and Active cannot be deleted and their unique name cannot be changed.";

    /// <summary>The 400 for a managed solution: it is not deleted as a container, it is uninstalled (plan 2.6, ruling M14).</summary>
    public const string ManagedNotSupported =
        "The solution is managed: a managed solution is not deleted with its components here, it is uninstalled (xrmd solution uninstall <name>, or the Admin API POST /admin/solutions/{name}/uninstall).";

    /// <summary>
    /// Removes the solution's container content before its row is deleted: every component it holds is added to
    /// <c>Default</c> (when Default does not track it yet), then the solution's <c>solutioncomponent</c> rows are deleted.
    /// A solution that does not exist is left to the Delete (404); nothing is written.
    /// </summary>
    /// <param name="s">The Delete's session.</param>
    /// <param name="solutionId">The solution.</param>
    /// <param name="uninstalling">The Delete is the last step of the uninstall of a managed solution (plan 2.6, ruling M9),
    /// which has removed its components and layers: the managed solution's row may go.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied for <c>Default</c> and <c>Active</c>; 400 InvalidArgument for a
    /// managed solution outside its uninstall (<see cref="ManagedNotSupported"/>).</exception>
    public async Task RemoveContainerAsync(DataSession s, Guid solutionId, bool uninstalling, CancellationToken ct)
    {
        if (!await EnsureDeletableAsync(s, solutionId, uninstalling, ct)) return;

        foreach (var (type, objectId) in await ComponentsAsync(s, solutionId, ct))
            await tracker.AddAsync(s, SystemSeed.DefaultSolutionId, type, objectId, null, ct);

        await using var cmd = s.Command($"DELETE FROM {s.DataTable("solutioncomponent")} WHERE solutionid = @solution");
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The refusals of a solution delete: 403 for <c>Default</c> and <c>Active</c>, 400 for a managed solution. Reads
    /// <c>ismanaged</c> with an update lock (kept to the end of the transaction, if any). False when the solution does not exist.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied; 400 InvalidArgument.</exception>
    public static Task<bool> EnsureDeletableAsync(DataSession s, Guid solutionId, CancellationToken ct) =>
        EnsureDeletableAsync(s, solutionId, uninstalling: false, ct);

    /// <summary>
    /// <see cref="EnsureDeletableAsync(DataSession, Guid, CancellationToken)"/>; with <paramref name="uninstalling"/> (the last
    /// step of an uninstall), a managed solution is deletable.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied; 400 InvalidArgument.</exception>
    public static async Task<bool> EnsureDeletableAsync(DataSession s, Guid solutionId, bool uninstalling, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (solutionId == SystemSeed.DefaultSolutionId || solutionId == SystemSeed.ActiveSolutionId)
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, SystemSolutionFixed, 403);
        if (await IsManagedAsync(s, solutionId, ct) is not { } managed) return false;
        if (managed && !uninstalling) throw XrmdFaultException.InvalidArgument(ManagedNotSupported);
        return true;
    }

    /// <summary>The components the solution holds (each once, by type and object id).</summary>
    public Task<IReadOnlyList<(int Type, Guid ObjectId)>> ComponentsAsync(DataSession s, Guid solutionId, CancellationToken ct) =>
        tracker.ListAsync(s, solutionId, ct);

    /// <summary><c>ismanaged</c> of the solution, read with an update lock (null when the solution does not exist).</summary>
    private static async Task<bool?> IsManagedAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        // Plan 2.7a, Task 6: the delete of an unmanaged solution row is a record delete, so it runs on PostgreSQL too.
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT {d.IsNull("ismanaged", d.BooleanLiteral(false))} FROM {s.DataTable(Table)}{d.LockHint(RowLock.UpdateRange)} " +
            $"WHERE solutionid = @solution{d.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        return await cmd.ExecuteScalarAsync(ct) is bool managed ? managed : null;
    }
}
