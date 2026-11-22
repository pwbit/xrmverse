using System.Data;
using System.Text.Json;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Solution.Layers;

namespace Xrmd.Solution;

/// <summary>
/// The <c>solutioncomponent</c> rows: which components each solution holds. Every component is in the system solution
/// <c>Default</c> (<see cref="SystemSeed.DefaultSolutionId"/>); a component appears once per solution. The writes are
/// <c>data.*</c> writes on the caller's session: inside a metadata message they come after its metadata writes.
/// Tracking a component also gives it its eager Active marker when it is layered, and removing a deleted component removes
/// its layers (plan 2.6, rulings M1 and M4; <see cref="ComponentLayerStore"/>).
/// </summary>
/// <param name="layers">The layer store; a new one when not given (it holds no state).</param>
public sealed class SolutionComponentTracker(ComponentLayerStore? layers = null)
{
    private const string SolutionTable = "solution";

    /// <summary>The layers of the components this tracker records.</summary>
    public ComponentLayerStore Layers { get; } = layers ?? new ComponentLayerStore();

    /// <summary>The length of <c>solution.uniquename</c> (Dataverse: 65).</summary>
    public const int UniqueNameMaxLength = 65;

    /// <summary>
    /// The id of the solution with that unique name (compared as the database collation does: ignoring case).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for a name longer than <see cref="UniqueNameMaxLength"/> (no
    /// solution can have it); 404 ObjectDoesNotExist when no solution has that unique name.</exception>
    public async Task<Guid> SolutionIdAsync(DataSession s, string uniqueName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // A longer name would be cut by the parameter and could match another solution.
        if (uniqueName is { Length: > UniqueNameMaxLength })
            throw XrmdFaultException.InvalidArgument(
                $"The solution unique name '{uniqueName}' is longer than {UniqueNameMaxLength} characters.");
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT solutionid FROM {s.DataTable(SolutionTable)} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}", "1"));
        cmd.AddParameter("@name", DbType.String, uniqueName ?? "", 65);
        return await cmd.ExecuteScalarAsync(ct) as Guid?
               ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"The solution with unique name '{uniqueName}' does not exist.", 404);
    }

    /// <summary>
    /// Adds the component to the solution and returns its <c>solutioncomponentid</c>; a component already in the solution
    /// keeps its row (and its id is returned), with its <c>rootcomponentbehavior</c> set to <paramref name="rootComponentBehavior"/>
    /// when one is given. <paramref name="rootSolutionComponentId"/> links a subcomponent (an attribute added with its
    /// entity) to its root component's row. Type 3 is recorded as 10 (<see cref="ComponentType.Normalize"/>).
    /// </summary>
    /// <param name="rootComponentBehavior">0 include subcomponents, 1 do not include subcomponents, 2 include as shell only;
    /// null: 0 for a new row, unchanged for an existing one.</param>
    public async Task<Guid> AddAsync(DataSession s, Guid solutionId, int componentType, Guid objectId, int? rootComponentBehavior,
        CancellationToken ct, Guid? rootSolutionComponentId = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        componentType = ComponentType.Normalize(componentType);
        // Plan 2.7a, Task 6: the T-SQL batch became statements of their own in the caller's transaction: the key lock and the
        // locking read (UPDLOCK, HOLDLOCK) of the row, then its insert or update.
        var existing = await FindLockedAsync(s, solutionId, componentType, objectId, ct);
        var table = s.DataTable("solutioncomponent");
        if (existing is null)
        {
            existing = Guid.NewGuid();
            await using var insert = s.Command($"""
                INSERT INTO {table} (solutioncomponentid, solutionid, componenttype, objectid,
                    rootcomponentbehavior, rootsolutioncomponentid, ismetadata, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                VALUES (@existing, @solution, @type, @object, @behavior, @root, @ismetadata,
                    ({s.Dialect.Limit($"SELECT businessunitid FROM {s.DataTable("businessunit")} WHERE parentbusinessunitid IS NULL ORDER BY createdon", "1")}),
                    {s.Dialect.UtcNow}, {s.Dialect.UtcNow}, 0, 1)
                """);
            insert.AddParameter("@existing", DbType.Guid, existing.Value);
            insert.AddParameter("@solution", DbType.Guid, solutionId);
            insert.AddParameter("@type", DbType.Int32, componentType);
            insert.AddParameter("@object", DbType.Guid, objectId);
            insert.AddParameter("@behavior", DbType.Int32, rootComponentBehavior ?? 0);
            insert.AddParameter("@root", DbType.Guid, (object?)rootSolutionComponentId ?? DBNull.Value);
            insert.AddParameter("@ismetadata", DbType.Boolean, ComponentType.IsMetadata(componentType));
            await insert.ExecuteNonQueryAsync(ct);
        }
        else if (rootComponentBehavior is { } requested)
        {
            await using var update = s.Command($"""
                UPDATE {table} SET rootcomponentbehavior = @requested, modifiedon = {s.Dialect.UtcNow}
                WHERE solutioncomponentid = @existing AND (rootcomponentbehavior IS NULL OR rootcomponentbehavior <> @requested)
                """);
            update.AddParameter("@requested", DbType.Int32, requested);
            update.AddParameter("@existing", DbType.Guid, existing.Value);
            await update.ExecuteNonQueryAsync(ct);
        }
        return existing.Value;
    }

    /// <summary>
    /// The row of the component in the solution, after the key lock of the triple and an update lock on the row (and, on SQL
    /// Server, the key range): the caller inserts, changes or deletes it in the same transaction.
    /// </summary>
    private static async Task<Guid?> FindLockedAsync(DataSession s, Guid solutionId, int componentType, Guid objectId, CancellationToken ct)
    {
        var d = s.Dialect;
        await s.KeyLockAsync("solutioncomponent", $"{solutionId:N}:{componentType}:{objectId:N}", ct);
        await using var cmd = s.Command(d.Limit($"""
            SELECT solutioncomponentid FROM {s.DataTable("solutioncomponent")}{d.LockHint(RowLock.UpdateRange)}
            WHERE solutionid = @solution AND componenttype = @type AND objectid = @object
            """, "1") + d.LockClause(RowLock.UpdateRange));
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        cmd.AddParameter("@type", DbType.Int32, componentType);
        cmd.AddParameter("@object", DbType.Guid, objectId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>
    /// Removes the component from the solution, with the subcomponents added with it (rows whose
    /// <c>rootsolutioncomponentid</c> is its row), and returns its <c>solutioncomponentid</c>; null when the solution does
    /// not hold it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for the <c>Default</c> solution, which holds every component.</exception>
    public async Task<Guid?> RemoveAsync(DataSession s, Guid solutionId, int componentType, Guid objectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (solutionId == SystemSeed.DefaultSolutionId)
            throw XrmdFaultException.InvalidArgument("A component cannot be removed from the Default solution.");
        componentType = ComponentType.Normalize(componentType);
        var removed = await FindLockedAsync(s, solutionId, componentType, objectId, ct);
        if (removed is null) return null;
        await using var cmd = s.Command($"""
            DELETE FROM {s.DataTable("solutioncomponent")}
            WHERE solutioncomponentid = @removed OR (solutionid = @solution AND rootsolutioncomponentid = @removed)
            """);
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        cmd.AddParameter("@removed", DbType.Guid, removed.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return removed;
    }

    /// <summary>The components of the solution, by component type then object id.</summary>
    public async Task<IReadOnlyList<(int Type, Guid ObjectId)>> ListAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"""
            SELECT componenttype, objectid FROM {s.DataTable("solutioncomponent")}
            WHERE solutionid = @solution AND componenttype IS NOT NULL AND objectid IS NOT NULL
            ORDER BY componenttype, objectid
            """);
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        var components = new List<(int, Guid)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) components.Add((reader.GetInt32(0), reader.GetGuid(1)));
        return components;
    }

    /// <summary>
    /// Records a component just created or changed: in <c>Default</c> and, when <paramref name="solutionUniqueName"/> is
    /// given, in that solution too (resolved first, so that an unknown name writes nothing). An unmanaged write: a component
    /// with a System or Managed layer gets its Active marker (<see cref="ComponentLayerStore.MarkActiveAsync"/>, ruling M4)
    /// unless <paramref name="markActive"/> is false (a layer operation running as <see cref="Caller.Internal"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when no solution has that unique name.</exception>
    public async Task TrackAsync(DataSession s, int componentType, Guid objectId, string? solutionUniqueName, CancellationToken ct,
        bool markActive = true)
    {
        Guid? named = string.IsNullOrEmpty(solutionUniqueName) ? null : await SolutionIdAsync(s, solutionUniqueName, ct);
        await AddAsync(s, SystemSeed.DefaultSolutionId, componentType, objectId, null, ct);
        if (named is { } id && id != SystemSeed.DefaultSolutionId) await AddAsync(s, id, componentType, objectId, null, ct);
        if (markActive) await Layers.MarkActiveAsync(s, componentType, objectId, ct);
    }

    /// <summary>
    /// An unmanaged change of a component that is not tracked again (no solution named): its Active marker when it is
    /// layered (ruling M4); nothing otherwise.
    /// </summary>
    public Task MarkChangedAsync(DataSession s, int componentType, Guid objectId, CancellationToken ct) =>
        Layers.MarkActiveAsync(s, componentType, objectId, ct);

    /// <summary>
    /// Removes deleted components (by object id) from every solution, <c>Default</c> included, with their layers
    /// (<see cref="ComponentLayerStore.RemoveComponentAsync"/>).
    /// </summary>
    public async Task RemoveDeletedAsync(DataSession s, IReadOnlyCollection<Guid> objectIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(objectIds);
        if (objectIds.Count == 0) return;
        await using var cmd = s.Command($"""
            DELETE FROM {s.DataTable("solutioncomponent")}
            WHERE objectid IN (SELECT j.id FROM {s.Dialect.JsonRecords("@ids", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")])} AS j)
            """);
        cmd.AddParameter("@ids", DbType.String, JsonSerializer.Serialize(objectIds), -1);
        await cmd.ExecuteNonQueryAsync(ct);
        await Layers.RemoveComponentAsync(s, objectIds, ct);
    }
}
