using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Schema;
using Xrmd.Solution;
using Xrmd.Solution.Layers;

namespace Xrmd.Admin;

/// <summary>One row of a layer listing: a layer, the solution that owns it and its place in the stack.</summary>
/// <param name="ComponentType">The component type (type 3 is listed as 10).</param>
/// <param name="ObjectId">The component's <c>MetadataId</c> or row id.</param>
/// <param name="Solution">The unique name of the owner: a managed solution, <c>System</c> or <c>Active</c>.</param>
/// <param name="Kind"><c>system</c>, <c>managed</c> or <c>active</c>.</param>
/// <param name="Order">Its place in the stack, higher on top (<see cref="ComponentLayer.Order"/>).</param>
/// <param name="Version">The owner's version; null for the System and Active layers.</param>
public sealed record LayerRow(int ComponentType, Guid ObjectId, string Solution, string Kind, long Order, string? Version);

/// <summary>
/// The read side of the component layers (plan 2.6, ruling M16): <c>GET /admin/solutions/{uniquename}/layers</c>,
/// <c>GET /admin/layers/{componenttype}/{objectid}</c> and the MCP tool <c>list_component_layers</c>. Reads only.
/// </summary>
public sealed class SolutionLayerLister(IDbConnectionFactory factory, ComponentLayerStore layers)
{
    /// <summary>The layers the solution owns, by component type then object id.</summary>
    /// <exception cref="XrmdFaultException">404 for an unknown solution.</exception>
    public async Task<IReadOnlyList<LayerRow>> OfSolutionAsync(string uniqueName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uniqueName);
        await using var conn = await factory.OpenAsync(ct);
        var s = new DataSession(conn, null);
        Guid? id = null;
        await using (var cmd = s.Command($"SELECT solutionid FROM {s.DataTable("solution")} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}"))
        {
            cmd.AddParameter("@name", DbType.String, uniqueName, 256);
            id = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        if (id is not { } solutionId)
            throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"The solution with unique name '{uniqueName}' does not exist.", 404);
        return await RowsAsync(s, await layers.OfSolutionAsync(s, solutionId, ct), ct);
    }

    /// <summary>The layers of a component, top first.</summary>
    /// <exception cref="XrmdFaultException">404 when the component has no layers (unknown, or unmanaged and never layered).</exception>
    public async Task<IReadOnlyList<LayerRow>> OfComponentAsync(int type, Guid objectId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var s = new DataSession(conn, null);
        var stack = await layers.StackAsync(s, type, objectId, ct);
        if (stack.Count == 0)
            throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist,
                $"The component {objectId} of type {type} has no layers: it is unknown or unmanaged.", 404);
        return await RowsAsync(s, stack, ct);
    }

    private static async Task<IReadOnlyList<LayerRow>> RowsAsync(DataSession s, IReadOnlyList<ComponentLayer> stack, CancellationToken ct)
    {
        var owners = new Dictionary<Guid, (string Name, string? Version)>();
        foreach (var owner in stack.Select(l => l.SolutionId).Distinct())
        {
            if (owner == ComponentLayerStore.SystemLayerSolutionId) { owners[owner] = ("System", null); continue; }
            if (owner == SystemSeed.ActiveSolutionId) { owners[owner] = ("Active", null); continue; }
            await using var cmd = s.Command($"SELECT uniquename, version FROM {s.DataTable("solution")} WHERE solutionid = @id");
            cmd.AddParameter("@id", DbType.Guid, owner);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            owners[owner] = await reader.ReadAsync(ct)
                ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
                : (owner.ToString(), null);
        }
        return stack.Select(l => new LayerRow(l.Type, l.ObjectId, owners[l.SolutionId].Name, l.Kind.ToString().ToLowerInvariant(), l.Order,
            owners[l.SolutionId].Version)).ToList();
    }
}
