using Xrmd.Model.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// The table flag <c>AutoCreateAccessTeams</c> of <see cref="MetadataStore"/> (plan 2.16, rulings A1 and A5). It is kept in the
/// stored definition and set by CreateEntity and UpdateEntity (and so by a solution import). Enabling a table needs a table
/// owned by a user or a team and a free place among the <see cref="AccessTeamSchema.MaxTables"/>; it adds the table to the
/// targets of <c>team.regardingobjectid</c>, in the same session. Disabling it needs the table to have no template; its
/// target stays (no team can refer to the table then, since teams go with their templates). Deleting a table removes its
/// target once neither a template nor a team refers to it. Only these paths change the targets of
/// <c>team.regardingobjectid</c>; the public messages refuse it.
/// </summary>
public sealed partial class MetadataStore
{
    /// <inheritdoc />
    public IReadOnlySet<string> AccessTeamTables => _cache.AccessTeamTables;

    /// <summary>
    /// <paramref name="entity"/> can be enabled: owned by a user or a team, not an intersect table, and fewer than
    /// <see cref="AccessTeamSchema.MaxTables"/> other tables of <paramref name="cache"/> are enabled.
    /// </summary>
    /// <exception cref="XrmdValidationException">400 naming <c>AutoCreateAccessTeams</c>.</exception>
    private static void DemandAccessTeamsAllowed(CacheState cache, TableDefinition entity)
    {
        if (!AccessTeamSchema.AllowsAccessTeams(entity.OwnershipType) || entity.IsIntersect == true)
            throw new XrmdValidationException(
                $"AutoCreateAccessTeams cannot be enabled on entity '{entity.LogicalName}': only a table owned by a user or a team has access teams (its ownership is {entity.OwnershipType?.ToString() ?? "not set"}).");
        var enabled = cache.ByLogicalName.Values.Count(e => e.AutoCreateAccessTeams == true && e.LogicalName != entity.LogicalName);
        if (enabled >= AccessTeamSchema.MaxTables)
            throw new XrmdValidationException(
                $"AutoCreateAccessTeams cannot be enabled on entity '{entity.LogicalName}': {AccessTeamSchema.MaxTables} tables already have it, which is the most an organization can have.");
        if (RegardingLookup(cache) is null)
            throw new XrmdValidationException(
                $"AutoCreateAccessTeams needs the column '{AccessTeamSchema.Team}.{AccessTeamSchema.RegardingObjectId}', which this organization does not have yet.");
    }

    /// <summary>
    /// No access team template names the table of <paramref name="entity"/>. The rows read stay locked until the session ends, so
    /// that a template written meanwhile waits for the change.
    /// </summary>
    /// <param name="what">What is refused, for the error ("AutoCreateAccessTeams cannot be disabled on entity 'x'").</param>
    /// <exception cref="XrmdValidationException">400 naming the number of templates.</exception>
    private static async Task DemandNoTemplatesAsync(DataSession session, CacheState cache, TableDefinition entity, string what, CancellationToken ct)
    {
        if (!cache.ByLogicalName.ContainsKey(AccessTeamSchema.TeamTemplate) || entity.ObjectTypeCode is not { } code) return;
        await session.KeyLockAsync(AccessTeamSchema.TeamTemplate, code, ct);
        var count = await ScalarAsync<int>(session,
            $"SELECT COUNT(*) FROM data.{AccessTeamSchema.TeamTemplate}{session.Dialect.LockHint(RowLock.UpdateRange)} WHERE {AccessTeamSchema.ObjectTypeCode} = @code", ct,
            ("@code", code));
        if (count > 0)
            throw new XrmdValidationException(
                $"{what}: {count} access team template(s) are defined for it. Delete the templates first.");
    }

    /// <summary>No team refers to a record of <paramref name="entity"/> (<c>team.regardingobjectid</c>).</summary>
    /// <exception cref="XrmdValidationException">400 naming the number of teams.</exception>
    private static async Task DemandNoRegardingTeamsAsync(DataSession session, TableDefinition entity, CancellationToken ct)
    {
        if (entity.ObjectTypeCode is not { } code) return;
        await session.KeyLockAsync(AccessTeamSchema.Team, code, ct);
        var count = await ScalarAsync<int>(session,
            $"SELECT COUNT(*) FROM data.{AccessTeamSchema.Team}{session.Dialect.LockHint(RowLock.UpdateRange)} WHERE {AccessTeamSchema.RegardingObjectId}{TypeMapping.TypeColumnSuffix} = @code",
            ct, ("@code", code));
        if (count > 0)
            throw new XrmdValidationException(
                $"Entity '{entity.LogicalName}' cannot be deleted: {count} access team(s) belong to its records.");
    }

    /// <summary>
    /// Checks what deleting <paramref name="entity"/> needs of its access teams: when it is a target of
    /// <c>team.regardingobjectid</c>, no template and no team may refer to it. Returns whether the target is to be removed.
    /// </summary>
    private static async Task<bool> PrepareRegardingUnlinkAsync(DataSession session, CacheState cache, TableDefinition entity, CancellationToken ct)
    {
        if (RegardingLookup(cache) is not { } lookup || !(lookup.Targets ?? []).Contains(entity.LogicalName, StringComparer.Ordinal)) return false;
        await DemandNoTemplatesAsync(session, cache, entity, $"Entity '{entity.LogicalName}' cannot be deleted", ct);
        await DemandNoRegardingTeamsAsync(session, entity, ct);
        return true;
    }

    /// <summary>Adds <paramref name="entityLogicalName"/> to the targets of <c>team.regardingobjectid</c> (once).</summary>
    private static Task AddRegardingTargetAsync(DataSession session, CacheState cache, string entityLogicalName, long stamp, CancellationToken ct) =>
        SetRegardingTargetsAsync(session, cache, targets => targets.Contains(entityLogicalName) ? targets : [.. targets, entityLogicalName], stamp, ct);

    /// <summary>Removes <paramref name="entityLogicalName"/> from the targets of <c>team.regardingobjectid</c>.</summary>
    private static Task RemoveRegardingTargetAsync(DataSession session, CacheState cache, string entityLogicalName, long stamp, CancellationToken ct) =>
        SetRegardingTargetsAsync(session, cache, targets => targets.Where(t => t != entityLogicalName).ToArray(), stamp, ct);

    private static LookupColumn? RegardingLookup(CacheState cache) =>
        cache.ByLogicalName.GetValueOrDefault(AccessTeamSchema.Team)?.Attributes?
            .OfType<LookupColumn>().FirstOrDefault(a => a.LogicalName == AccessTeamSchema.RegardingObjectId);

    private static async Task SetRegardingTargetsAsync(DataSession session, CacheState cache, Func<string[], string[]> change, long stamp, CancellationToken ct)
    {
        if (RegardingLookup(cache) is not { } current) return;
        var targets = change([.. current.Targets ?? []]);
        if (targets.SequenceEqual(current.Targets ?? [])) return;

        var team = cache.ByLogicalName[AccessTeamSchema.Team];
        var updated = MetadataJson.Clone(current);
        updated.Targets = [.. targets];
        await StoreAttributeAsync(session, team.LogicalName!, updated, stamp, ct);
        var copy = MetadataJson.Clone(team);
        copy.SetAttributes((team.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated : a));
        cache.Put(copy, previous: team);
    }
}
