using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;

namespace Xrmd.Metadata;

/// <summary>The rollup column rules of the metadata store (plan 2.12b, Task 1): short calls from the write paths land here.</summary>
public sealed partial class MetadataStore
{
    private readonly object _rollupJobsGate = new();
    private Task _rollupJobsTail = Task.CompletedTask;

    /// <summary>
    /// Completes when every <see cref="IRollupJobs"/> notification queued so far has run. Notifications run in commit order after
    /// the commit, off the writer's call path (ruling R8): tests can wait for them here. Shutdown does not wait: a notification lost
    /// when the host stops is repaired by the reconcile at the next start (<c>RollupJobScheduler.ReconcileAsync</c>).
    /// </summary>
    public Task RollupJobNotifications
    {
        get
        {
            lock (_rollupJobsGate) return _rollupJobsTail;
        }
    }

    /// <summary>
    /// The one-to-many relationships of <paramref name="cache"/> by SchemaName (ignoring case), with <paramref name="extra"/> (a table
    /// being created) among the tables; built on first use.
    /// </summary>
    private static Func<string, OneToManyRelationshipMetadata?> RelationshipsOf(CacheState cache, EntityMetadata? extra = null)
    {
        var lazy = new Lazy<Dictionary<string, OneToManyRelationshipMetadata>>(() =>
        {
            var tables = cache.ByLogicalName.Values.Where(e => e.LogicalName != extra?.LogicalName).ToList();
            if (extra is not null) tables.Add(extra);
            var map = new Dictionary<string, OneToManyRelationshipMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in EntityMetadataExtensions.OneToManyRelationships(tables, cache.SavedOneToMany.Values))
                if (r.SchemaName is not null) map.TryAdd(r.SchemaName, r);
            return map;
        });
        return name => lazy.Value.GetValueOrDefault(name);
    }

    /// <summary>
    /// Checks the definition of <paramref name="a"/> of <paramref name="entity"/> (<see cref="ComputedColumnRules.ValidateDefinition"/>),
    /// a rollup with the counts of the other rollups of the table and of the organization.
    /// </summary>
    private void ValidateComputed(CacheState cache, EntityMetadata entity, AttributeMetadata a, AttributeMetadata? current,
        Func<string, EntityMetadata?> entities, bool creatingEntity = false)
    {
        int onTable = 0, inOrg = 0;
        if (RollupColumns.IsRollup(a))
        {
            onTable = (entity.Attributes ?? []).Count(x => RollupColumns.IsRollup(x) && x.LogicalName != a.LogicalName);
            inOrg = onTable + cache.ByLogicalName.Values.Where(e => e.LogicalName != entity.LogicalName)
                .Sum(e => (e.Attributes ?? []).Count(RollupColumns.IsRollup));
        }
        ComputedColumnRules.ValidateDefinition(entity, a, current, entities, _computed, _rollups,
            RelationshipsOf(cache, creatingEntity ? entity : null), onTable, inOrg);
    }

    /// <summary>
    /// The companions of the rollups among <paramref name="added"/> (each given a MetadataId), refused when one takes the name of
    /// another column of <paramref name="existing"/> or <paramref name="added"/> (ruling R2).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80060556</c> naming the companion and its rollup.</exception>
    private static List<AttributeMetadata> RollupCompanions(string entityLogicalName, IEnumerable<AttributeMetadata> existing,
        IReadOnlyCollection<AttributeMetadata> added)
    {
        var taken = existing.Concat(added).Select(x => x.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<AttributeMetadata> companions = [];
        foreach (var rollup in added.Where(RollupColumns.IsRollup).ToList())
        {
            rollup.MetadataId ??= Guid.NewGuid();
            foreach (var companion in RollupColumns.Companions(rollup))
            {
                if (taken.Contains(companion.LogicalName))
                    throw new XrmdFaultException(ErrorCodes.RollupDependentFieldNameAlreadyExists,
                        $"The rollup column '{entityLogicalName}.{rollup.LogicalName}' needs a column named '{companion.LogicalName}', which the " +
                        "table already has: give the rollup column another name.", 400);
                companions.Add(companion);
            }
        }
        return companions;
    }

    /// <summary>A companion of a rollup is the server's: it is neither updated nor deleted on its own (ruling R11).</summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80060555</c>.</exception>
    private static void DemandNotRollupCompanion(EntityMetadata entity, AttributeMetadata attribute)
    {
        if (!RollupColumns.IsCompanion(attribute)
            || !(entity.Attributes ?? []).Any(x => x.LogicalName == attribute.AttributeOf && RollupColumns.IsRollup(x))) return;
        throw new XrmdFaultException(ErrorCodes.CannotModifyRollupDependentField,
            $"The column '{entity.LogicalName}.{attribute.LogicalName}' belongs to the rollup column '{entity.LogicalName}.{attribute.AttributeOf}': " +
            "it cannot be changed or deleted on its own.", 400);
    }

    /// <exception cref="XrmdFaultException">400 <c>0x80060541</c> naming the rollup columns that read the component (ruling R13).</exception>
    private static void DemandNoRollupDependents(IReadOnlyList<string> dependents)
    {
        if (dependents.Count == 0) return;
        throw new XrmdFaultException(ErrorCodes.RollupFieldDependentFieldCannotDeleted,
            $"The component cannot be deleted because the rollup column '{string.Join("', '", dependents)}' reads it: delete the rollup column first.",
            400);
    }

    /// <summary>The rollups that read <paramref name="attribute"/> of <paramref name="entity"/> block its deletion.</summary>
    private void DemandNoRollupDependents(CacheState cache, string entity, string attribute)
    {
        if (_rollups is null) return;
        DemandNoRollupDependents(RollupColumnRules.Dependents(entity, attribute, cache.ByLogicalName.Values, _rollups, RelationshipsOf(cache)));
    }

    /// <summary>
    /// The columns deleted with <paramref name="attribute"/> (<paramref name="removed"/> but the column itself) block its deletion while a
    /// calculated column (400 <c>0x80040203</c>) or a rollup column (400 <c>0x80060541</c>) reads one of them. The column being deleted is
    /// not translated, so a column whose own definition cannot be read can still go.
    /// </summary>
    private void DemandNoDependentsOfCompanions(CacheState cache, string entity, string attribute, IReadOnlySet<string> removed)
    {
        var replaced = $"{entity}.{attribute}";
        foreach (var companion in removed.Where(n => n != attribute).Order(StringComparer.Ordinal))
        {
            if (_computed is not null)
                DemandNoCalculatedDependents(ComputedColumnRules.Dependents(entity, companion, cache.ByLogicalName.Values, _computed, replaced),
                    cache.ByLogicalName.Values);
            if (_rollups is not null)
                DemandNoRollupDependents(RollupColumnRules.Dependents(entity, companion, cache.ByLogicalName.Values, _rollups, RelationshipsOf(cache),
                    replaced));
        }
    }

    /// <summary>The rollups of other tables that read <paramref name="entity"/> block its deletion.</summary>
    private void DemandNoRollupDependentsOfTable(CacheState cache, string entity)
    {
        if (_rollups is null) return;
        DemandNoRollupDependents(RollupColumnRules.DependentsOfTable(entity, cache.ByLogicalName.Values, _rollups, RelationshipsOf(cache)));
    }

    /// <summary>The rollups that aggregate over the relationship <paramref name="schemaName"/> (or name it as hierarchy) block its deletion.</summary>
    private void DemandNoRollupDependentsOfRelationship(CacheState cache, string schemaName)
    {
        if (_rollups is null) return;
        DemandNoRollupDependents(RollupColumnRules.DependentsOfRelationship(schemaName, cache.ByLogicalName.Values, _rollups, RelationshipsOf(cache)));
    }

    /// <summary>
    /// An update of <paramref name="current"/> that changes its <c>DateTimeBehavior</c> is refused while a rollup reads it (ruling R11).
    /// Other updates of a column a rollup reads re-check those rollups as they would be after the update (a calculated column the
    /// rollup aggregates must stay basic, ruling R5).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80097214</c>, or the rule a dependent rollup would break, naming it.</exception>
    private void DemandRollupDependentsStayValid(CacheState cache, EntityMetadata entity, AttributeMetadata current, AttributeMetadata updated,
        AttributeMetadata changes)
    {
        if (_rollups is null) return;
        var behaviorChanges = changes is DateTimeAttributeMetadata { DateTimeBehavior: { } behavior } && current is DateTimeAttributeMetadata stored
                              && behavior.Value != RollupColumnRules.BehaviorOf(stored);
        if (!behaviorChanges && !MayAffectDependents(changes)) return;
        var relationships = RelationshipsOf(cache);
        var dependents = RollupColumnRules.Dependents(entity.LogicalName, current.LogicalName, cache.ByLogicalName.Values, _rollups, relationships);
        if (dependents.Count == 0) return;
        if (behaviorChanges)
            throw new XrmdFaultException(ErrorCodes.AttributeBehaviorCanNotBeUpdatedForRollupField,
                $"The behavior of '{entity.LogicalName}.{current.LogicalName}' cannot change: the rollup column '{string.Join("', '", dependents)}' reads it.",
                400);

        var after = MetadataSerializer.Clone(entity);
        after.SetAttributes((entity.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated : a));
        EntityMetadata? Find(string name) => name == after.LogicalName ? after : cache.ByLogicalName.GetValueOrDefault(name);
        foreach (var dependent in dependents)
        {
            var dot = dependent.IndexOf('.', StringComparison.Ordinal);
            if (Find(dependent[..dot]) is not { } owner || owner.Attributes?.FirstOrDefault(a => a.LogicalName == dependent[(dot + 1)..]) is not { } rollup)
                continue;
            try
            {
                RollupColumnRules.ValidateDefinition(owner, MetadataSerializer.Clone(rollup), rollup, Find, relationships, _rollups, 0, 0,
                    _computed);
            }
            catch (XrmdFaultException ex)
            {
                throw new XrmdFaultException(ex.ErrorCode,
                    $"The change of '{entity.LogicalName}.{current.LogicalName}' would make the rollup column '{dependent}' invalid: {ex.Message}", ex.HttpStatus);
            }
            catch (XrmdValidationException ex)
            {
                throw new XrmdValidationException(
                    $"The change of '{entity.LogicalName}.{current.LogicalName}' would make the rollup column '{dependent}' invalid: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Gives the rows of <paramref name="entity"/> the initial values of the new rollup <paramref name="rollup"/> (ruling R7): state 0
    /// (NotCalculated), <c>_date</c> now and <see cref="RollupColumns.InitialValue"/>, in the metadata transaction, after its columns were
    /// added (a separate statement: the batch that adds a column cannot name it).
    /// </summary>
    private async Task InitializeRollupRowsAsync(DataSession session, CacheState cache, EntityMetadata entity, AttributeMetadata rollup,
        CancellationToken ct)
    {
        if (_rollups is null) return;
        var definition = _rollups.Translate(entity, rollup, n => cache.ByLogicalName.GetValueOrDefault(n), RelationshipsOf(cache));
        var value = RollupColumns.InitialValue(rollup, definition);
        var name = rollup.LogicalName;
        var sql = $"UPDATE {session.DataTable(entity.LogicalName)} SET {session.Quote(name + RollupColumns.StateSuffix)} = {(int)RollupState.NotCalculated}, " +
                  $"{session.Quote(name + RollupColumns.DateSuffix)} = @now" + (value is null ? "" : $", {session.Quote(name)} = @value");
        (string, object)[] parameters = value is null ? [("@now", DateTime.UtcNow)] : [("@now", DateTime.UtcNow), ("@value", value)];
        await ExecuteAsync(session, sql, ct, parameters);
    }

    /// <summary>
    /// Tells the <see cref="IRollupJobs"/>, once <paramref name="session"/> commits, that a solution import (or a restore from a lower
    /// layer) wrote the rollup <paramref name="entity"/>.<paramref name="attribute"/> (<see cref="RollupChange.Imported"/>: its Mass job
    /// is due at once, plan 2.12b rulings R8, R14). Queued with the store's own notifications, after the <see cref="RollupChange.Added"/>
    /// or <see cref="RollupChange.Updated"/> the write queued, so that the job ends up due at once.
    /// </summary>
    public void NotifyRollupImported(DataSession session, string entity, string attribute)
    {
        ArgumentNullException.ThrowIfNull(session);
        NotifyRollupJobs(session, entity, attribute, RollupChange.Imported);
    }

    /// <summary>
    /// Tells the <see cref="IRollupJobs"/>, once <paramref name="session"/> commits (and the cache is published), that the rollup
    /// <paramref name="entity"/>.<paramref name="attribute"/> changed. Never inside the metadata transaction, never on the writer's
    /// call path; in commit order; a failure is logged (the jobs are reconciled at host start, ruling R9).
    /// </summary>
    private void NotifyRollupJobs(DataSession session, string entity, string attribute, RollupChange change)
    {
        if (_rollupJobs is not { } jobs) return;
        session.OnCommitted(() =>
        {
            lock (_rollupJobsGate)
                _rollupJobsTail = _rollupJobsTail.ContinueWith(_ => RunAsync(), CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
        });

        async Task RunAsync()
        {
            try
            {
                await jobs.DefinitionChangedAsync(entity, attribute, change, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "The rollup calculation jobs of {Entity}.{Attribute} could not be told of the change {Change}.",
                    entity, attribute, change);
            }
        }
    }
}
