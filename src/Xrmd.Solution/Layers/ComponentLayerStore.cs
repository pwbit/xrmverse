using System.Data;
using System.Data.Common;
using System.Text.Json;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Solution.Layers;

/// <summary>
/// The layers of the solution components (plan 2.6, rulings M1 and M2): <c>meta.component_layer</c>, one row per component and
/// owner, and <c>meta.solution_rank</c>, where each managed solution's layers sit. The top layer of a component wins. Every
/// read and write runs on the caller's session, so a solution operation changes layers in its own transaction. The store
/// holds no state.
/// </summary>
public sealed class ComponentLayerStore
{
    // Plan 2.7a, Task 6: quoted by the session's dialect (every metadata write marks and removes layers).
    private static string Layers(DataSession s) => $"{s.Dialect.Quote("meta")}.{s.Dialect.Quote("component_layer")}";
    private static string Ranks(DataSession s) => $"{s.Dialect.Quote("meta")}.{s.Dialect.Quote("solution_rank")}";

    /// <summary>
    /// The owner of the System layers (the base state of a seeded component). From memory (plan 2.6 checklist 1a); no
    /// <c>solution</c> row is seeded for it.
    /// </summary>
    public static readonly Guid SystemLayerSolutionId = new("FD140AAD-4DF4-11DD-BD17-0019B9312238");

    /// <summary>The orders a segment spans: <c>layer_order = segment * 1024 + sub</c> (ruling M2).</summary>
    public const int SubsPerSegment = 1024;

    /// <summary>The sub of a holding solution <c>{name}_Upgrade</c>: directly above its base, below any later segment.</summary>
    public const int HoldingSub = SubsPerSegment - 1;

    /// <summary>The order of a managed layer whose solution has <paramref name="segment"/> and <paramref name="sub"/>.</summary>
    public static long Order(int segment, int sub) => (long)segment * SubsPerSegment + sub;

    /// <summary>The kind of the layers <paramref name="solutionId"/> owns.</summary>
    public static LayerKind KindOf(Guid solutionId) =>
        solutionId == SystemLayerSolutionId ? LayerKind.System
        : solutionId == SystemSeed.ActiveSolutionId ? LayerKind.Active
        : LayerKind.Managed;

    /// <summary>The layers of a component, top first (Active, then managed solutions by rank, then System).</summary>
    public async Task<IReadOnlyList<ComponentLayer>> StackAsync(DataSession s, int type, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"""
            SELECT component_type, object_id, solution_id, layer_order, payload FROM {Layers(s)}
            WHERE component_type = @type AND object_id = @object
            ORDER BY layer_order DESC, solution_id
            """);
        AddComponent(cmd, type, id);
        return await ReadAsync(cmd, ct);
    }

    /// <summary>
    /// Writes the layer <paramref name="layer"/> holds, replacing the one its solution already has on the component. The order
    /// stored follows the owner: 0 for System, <see cref="long.MaxValue"/> for Active (payload dropped: a marker has none),
    /// and for a managed solution its rank when <see cref="SetRankAsync"/> gave it one, else <see cref="ComponentLayer.Order"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The kind does not match the owner (<see cref="KindOf"/>).</exception>
    public async Task UpsertAsync(DataSession s, ComponentLayer layer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(layer);
        var kind = KindOf(layer.SolutionId);
        if (layer.Kind != kind)
            throw new ArgumentException($"A layer owned by {layer.SolutionId} is a {kind} layer, not a {layer.Kind} layer.", nameof(layer));
        // Plan 2.7a, Task 6: the update-else-insert batch became the key lock of the layer, its update, then its insert when the
        // update found no row (both in the caller's transaction).
        var order = kind switch
        {
            LayerKind.System => 0L,
            LayerKind.Active => long.MaxValue,
            _ => await RankOrderAsync(s, layer.SolutionId, ct) ?? layer.Order,
        };
        await s.KeyLockAsync("component_layer", $"{ComponentType.Normalize(layer.Type)}:{layer.ObjectId:N}:{layer.SolutionId:N}", ct);
        var payload = kind == LayerKind.Active || layer.Payload is null ? (object)DBNull.Value : layer.Payload;
        await using (var update = s.Command($"""
            UPDATE {Layers(s)}{s.Dialect.LockHint(RowLock.UpdateSerializable)} SET layer_order = @order, payload = @payload
            WHERE component_type = @type AND object_id = @object AND solution_id = @solution
            """))
        {
            AddComponent(update, layer.Type, layer.ObjectId);
            update.AddParameter("@solution", DbType.Guid, layer.SolutionId);
            update.AddParameter("@order", DbType.Int64, order);
            update.AddParameter("@payload", DbType.String, payload, -1);
            if (await update.ExecuteNonQueryAsync(ct) > 0) return;
        }
        await using var insert = s.Command($"""
            INSERT INTO {Layers(s)} (component_type, object_id, solution_id, layer_order, payload)
            VALUES (@type, @object, @solution, @order, @payload)
            """);
        AddComponent(insert, layer.Type, layer.ObjectId);
        insert.AddParameter("@solution", DbType.Guid, layer.SolutionId);
        insert.AddParameter("@order", DbType.Int64, order);
        insert.AddParameter("@payload", DbType.String, payload, -1);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The eager Active marker (ruling M4): gives the component an Active layer when it has a System or Managed layer. A
    /// component without one is left alone (an unmanaged component has no layers).
    /// </summary>
    /// <returns>True when the component has an Active layer afterwards (created now or before); false when it has no
    /// System or Managed layer (nothing written).</returns>
    public async Task<bool> MarkActiveAsync(DataSession s, int type, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // A plain read first: the common case (an unlayered component) takes no lock on the layer table.
        await using (var probe = s.Command($"""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM {Layers(s)} WHERE component_type = @type AND object_id = @object AND solution_id <> @active)
                THEN 1 ELSE 0 END
            """))
        {
            AddComponent(probe, type, id);
            probe.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
            if (Convert.ToInt32(await probe.ExecuteScalarAsync(ct)) == 0) return false;
        }
        // Plan 2.7a, Task 6: the key lock of the marker, then an insert that checks for it under the range lock.
        await s.KeyLockAsync("component_layer", $"{ComponentType.Normalize(type)}:{id:N}:{SystemSeed.ActiveSolutionId:N}", ct);
        await using var cmd = s.Command($"""
            INSERT INTO {Layers(s)} (component_type, object_id, solution_id, layer_order, payload)
            SELECT @type, @object, @active, 9223372036854775807, NULL
            WHERE NOT EXISTS (SELECT 1 FROM {Layers(s)}{s.Dialect.LockHint(RowLock.UpdateRange)}
                              WHERE component_type = @type AND object_id = @object AND solution_id = @active)
            """);
        AddComponent(cmd, type, id);
        cmd.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>Removes the component's Active marker, if any (<c>OverwriteUnmanagedCustomizations</c>, ruling M6).</summary>
    public async Task RemoveActiveAsync(DataSession s, int type, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"DELETE FROM {Layers(s)} WHERE component_type = @type AND object_id = @object AND solution_id = @active");
        AddComponent(cmd, type, id);
        cmd.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The layers <paramref name="solutionId"/> owns, by component type then object id.</summary>
    public async Task<IReadOnlyList<ComponentLayer>> OfSolutionAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"""
            SELECT component_type, object_id, solution_id, layer_order, payload FROM {Layers(s)}
            WHERE solution_id = @solution ORDER BY component_type, object_id
            """);
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        return await ReadAsync(cmd, ct);
    }

    /// <summary>Removes every layer <paramref name="solutionId"/> owns, and its rank.</summary>
    public async Task RemoveSolutionAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        foreach (var table in new[] { Layers(s), Ranks(s) })
        {
            await using var cmd = s.Command($"DELETE FROM {table} WHERE solution_id = @solution");
            cmd.AddParameter("@solution", DbType.Guid, solutionId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Removes every layer (any owner) of the deleted components, by object id.</summary>
    public async Task RemoveComponentAsync(DataSession s, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;
        await using var cmd = s.Command($"""
            DELETE FROM {Layers(s)} WHERE object_id IN (SELECT j.id FROM {s.Dialect.JsonRecords("@ids", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")])} AS j)
            """);
        cmd.AddParameter("@ids", DbType.String, JsonSerializer.Serialize(ids), -1);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The segment of a new base managed solution: one above the highest ranked (1 when none; 0 is the System layer's). The
    /// ranks are locked until the end of the transaction, so two installs cannot take the same segment.
    /// </summary>
    public async Task<long> NextSegmentAsync(DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // Plan 2.7a, Task 6: an aggregate takes no row lock on PostgreSQL; the key lock of the ranks serializes the installs.
        await s.KeyLockAsync("solution_rank", "segment", ct);
        await using var cmd = s.Command($"SELECT CAST({s.Dialect.IsNull("MAX(segment)", "0")} + 1 AS bigint) FROM {Ranks(s)}{s.Dialect.LockHint(RowLock.UpdateRange)}");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The order of the layers of <paramref name="solutionId"/> (<see cref="Order"/> of its rank); null when it has no rank.</summary>
    public async Task<long?> RankOrderAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT CAST(segment AS bigint) * {SubsPerSegment} + sub FROM {Ranks(s)} WHERE solution_id = @solution");
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    /// <summary>
    /// Sets the rank of a managed solution (ruling M2) and moves its existing layers to the new order: a base solution has
    /// sub 0, a holding solution its base's segment with <see cref="HoldingSub"/>; 1 to 1022 are reserved for patches.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A segment below 1 or a sub outside 0 to 1023.</exception>
    /// <exception cref="ArgumentException">The System layer id or the <c>Active</c> solution, which have no rank.</exception>
    public async Task SetRankAsync(DataSession s, Guid solutionId, int segment, int sub, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentOutOfRangeException.ThrowIfLessThan(segment, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sub);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sub, HoldingSub);
        if (KindOf(solutionId) != LayerKind.Managed)
            throw new ArgumentException("Only a managed solution has a rank.", nameof(solutionId));
        // Plan 2.7a, Task 6: the update-else-insert batch as statements of their own after the key lock of the rank.
        await s.KeyLockAsync("solution_rank", solutionId, ct);
        int updated;
        await using (var update = s.Command(
                         $"UPDATE {Ranks(s)}{s.Dialect.LockHint(RowLock.UpdateSerializable)} SET segment = @segment, sub = @sub WHERE solution_id = @solution"))
        {
            update.AddParameter("@solution", DbType.Guid, solutionId);
            update.AddParameter("@segment", DbType.Int32, segment);
            update.AddParameter("@sub", DbType.Int32, sub);
            updated = await update.ExecuteNonQueryAsync(ct);
        }
        if (updated == 0)
        {
            await using var insert = s.Command($"INSERT INTO {Ranks(s)} (solution_id, segment, sub) VALUES (@solution, @segment, @sub)");
            insert.AddParameter("@solution", DbType.Guid, solutionId);
            insert.AddParameter("@segment", DbType.Int32, segment);
            insert.AddParameter("@sub", DbType.Int32, sub);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = s.Command($"UPDATE {Layers(s)} SET layer_order = @order WHERE solution_id = @solution");
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        cmd.AddParameter("@order", DbType.Int64, Order(segment, sub));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddComponent(DbCommand cmd, int type, Guid id)
    {
        cmd.AddParameter("@type", DbType.Int32, ComponentType.Normalize(type));
        cmd.AddParameter("@object", DbType.Guid, id);
    }

    private static async Task<IReadOnlyList<ComponentLayer>> ReadAsync(DbCommand cmd, CancellationToken ct)
    {
        var layers = new List<ComponentLayer>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var solution = reader.GetGuid(2);
            layers.Add(new ComponentLayer(reader.GetInt32(0), reader.GetGuid(1), solution, reader.GetInt64(3), KindOf(solution),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return layers;
    }
}
