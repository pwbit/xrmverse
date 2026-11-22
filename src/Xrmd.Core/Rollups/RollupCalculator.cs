using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Query.Sql;

namespace Xrmd.Core.Rollups;

/// <summary>
/// The limits of a rollup calculation (plan 2.12b, ruling R6). An online calculation (<c>CalculateRollupField</c>) aggregates at
/// most <paramref name="MaxRelatedRows"/> rows and walks at most <paramref name="MaxOnlineDepth"/> hierarchy levels below the row,
/// with a command timeout of <paramref name="OnlineCommandTimeoutSeconds"/> (the caller waits for it). A job's statement has
/// <paramref name="JobCommandTimeoutSeconds"/> (0: no limit, a mass run over a large table takes as long as it needs).
/// </summary>
public sealed record RollupLimits(int MaxRelatedRows = 50_000, int MaxOnlineDepth = 10, int JobCommandTimeoutSeconds = 0,
    int OnlineCommandTimeoutSeconds = 120);

/// <summary>
/// Calculates rollup columns (plan 2.12b, Task 3) as the system, outside the pipeline: no plug-in, no audit, no security check (the
/// callers check what they need, R10, R12). Each call runs the one statement of <see cref="RollupSql"/> on a connection and a
/// <c>READ COMMITTED</c> transaction of its own, and writes only the rollup's columns.
/// </summary>
public sealed class RollupCalculator(
    IDbConnectionFactory factory, ISqlDialect dialect, IMetadataStore store, IRollupColumns rollups, IComputedColumns? computed, TimeProvider time, RollupLimits? limits = null)
{
    private readonly RollupLimits _limits = limits ?? new RollupLimits();

    /// <summary>Recalculates <paramref name="attribute"/> of <paramref name="entity"/> on <paramref name="scope"/>; returns the rows written.</summary>
    public async Task<int> RecalculateAsync(string entity, string attribute, RollupScope scope, CancellationToken ct)
    {
        var (table, rollup, definition) = Resolve(entity, attribute);
        var command = Sql().Recalculate(table, rollup, definition, scope, time.GetUtcNow().UtcDateTime);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var written = 0;
        await using (var cmd = Command(command, conn, tx, CommandTimeoutSeconds(online: false)))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) written++;
        await tx.CommitAsync(ct);
        return written;
    }

    /// <summary>
    /// Calculates <paramref name="attribute"/> on the row <paramref name="target"/> only (no other rollup, no other row) and returns
    /// the row with the rollup's value, <c>_date</c>, <c>_state</c> (and <c>_base</c> for Currency). Refuses a row with more than
    /// <see cref="RollupLimits.MaxRelatedRows"/> rows to aggregate or more than <see cref="RollupLimits.MaxOnlineDepth"/> levels below it.
    /// </summary>
    public async Task<Entity> CalculateOneAsync(EntityReference target, string attribute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var entity = await CalculateOneAsync(conn, tx, target, attribute, ct);
        await tx.CommitAsync(ct);
        return entity;
    }

    /// <summary>
    /// <see cref="CalculateOneAsync(EntityReference, string, CancellationToken)"/> in <paramref name="session"/> (its connection and
    /// transaction, which commit or roll back the write with the rest of the request): <c>CalculateRollupField</c>'s stage 30 (ruling R10).
    /// </summary>
    public Task<Entity> CalculateOneAsync(DataSession session, EntityReference target, string attribute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        return CalculateOneAsync(session.Connection, session.Transaction, target, attribute, ct);
    }

    private async Task<Entity> CalculateOneAsync(DbConnection conn, DbTransaction? tx, EntityReference target, string attribute, CancellationToken ct)
    {
        var (table, rollup, definition) = Resolve(target.LogicalName, attribute);
        var sql = Sql();
        var measure = sql.Measure(table, rollup, definition, target.Id, _limits.MaxOnlineDepth);
        var recalculate = sql.Recalculate(table, rollup, definition, new RollupScope.One(target.Id), time.GetUtcNow().UtcDateTime);
        var where = $"'{table.LogicalName}.{rollup.LogicalName}'";

        await using (var cmd = Command(measure, conn, tx, CommandTimeoutSeconds(online: true)))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            await rd.ReadAsync(ct);
            // Counts read as numbers of any width: COUNT(*) is an int on SQL Server and a bigint on PostgreSQL (plan 2.7a, Task 7).
            if (Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture) == 0) throw XrmdFaultException.ObjectDoesNotExist(table.LogicalName, target.Id);
            if (Convert.ToInt64(rd.GetValue(1), CultureInfo.InvariantCulture) > _limits.MaxOnlineDepth)
                throw new XrmdFaultException(ErrorCodes.HierarchyCalculateLimitReached,
                    $"The rollup column {where} cannot be calculated for this row: its hierarchy has more than {_limits.MaxOnlineDepth} levels below it.", 400);
            if (Convert.ToInt64(rd.GetValue(2), CultureInfo.InvariantCulture) > _limits.MaxRelatedRows)
                throw new XrmdFaultException(ErrorCodes.RollupAggregateQueryRecordLimitExceeded,
                    $"The rollup column {where} cannot be calculated for this row: it would aggregate more than {_limits.MaxRelatedRows} rows.", 400);
        }

        var entity = new Entity(table.LogicalName, target.Id);
        await using (var cmd = Command(recalculate, conn, tx, CommandTimeoutSeconds(online: true)))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(table.LogicalName, target.Id);
            entity[rollup.LogicalName] = ValueOf(rollup, rd, 1);
            if (rollup is MoneyAttributeMetadata)
                entity[rollup.LogicalName + TypeMapping.BaseColumnSuffix] = rd.IsDBNull(2) ? null : new Money(rd.GetDecimal(2));
            entity[rollup.LogicalName + RollupColumns.DateSuffix] = rd.IsDBNull(3) ? null : DateTime.SpecifyKind(rd.GetDateTime(3), DateTimeKind.Utc);
            entity[rollup.LogicalName + RollupColumns.StateSuffix] = rd.IsDBNull(4) ? null : rd.GetInt32(4);
        }
        return entity;
    }

    /// <summary>
    /// Sets on <paramref name="row"/>, a row about to be inserted into <paramref name="table"/>, the initial values of each of the table's
    /// rollups (ruling R7): over related rows the value of an empty set with <c>_state</c> 1, else <c>_state</c> 0 (NotCalculated) and
    /// <see cref="RollupColumns.InitialValue"/>; <c>_date</c> <paramref name="nowUtc"/>
    /// (a value given for the rollup or its <c>_base</c> is dropped). A definition that cannot be read gives a null value: the row is
    /// still created, and the job reports the definition.
    /// </summary>
    internal void ApplyInitialValues(EntityMetadata table, Entity row, DateTime nowUtc)
    {
        foreach (var rollup in (table.Attributes ?? []).Where(RollupColumns.IsRollup))
        {
            object? value;
            var state = RollupState.NotCalculated;
            try
            {
                var definition = rollups.Translate(table, rollup, store.GetEntity, Relationship);
                // Dataverse calculates at once, on Create, a rollup over related rows: a new row has none, so it gets the value of
                // an empty set with state 1 (parity run on a licensed environment, plan 2.12b Task 7). A rollup over the hierarchy
                // only waits for a job (state 0).
                if (definition.Related is not null)
                {
                    value = RollupColumns.EmptySetValue(rollup, definition);
                    state = RollupState.Calculated;
                }
                else
                {
                    value = RollupColumns.InitialValue(rollup, definition);
                }
            }
            catch (Exception ex) when (ex is XrmdFaultException or XrmdValidationException)
            {
                value = null;
            }
            var name = rollup.LogicalName;
            row.Attributes.Remove(name + TypeMapping.BaseColumnSuffix);
            if (value is null) row.Attributes.Remove(name);
            else row[name] = value;
            row[name + RollupColumns.StateSuffix] = (int)state;
            row[name + RollupColumns.DateSuffix] = nowUtc;
        }
    }

    private RollupSql Sql() => new(store, dialect, computed);

    /// <summary>The table, the rollup column and its definition; a column that is not a rollup is 400 <c>0x80060554</c>.</summary>
    private (EntityMetadata Table, AttributeMetadata Rollup, RollupDefinition Definition) Resolve(string entity, string attribute)
    {
        var table = store.GetEntity(entity) ?? throw new XrmdValidationException($"Entity '{entity}' does not exist.");
        var rollup = table.Attributes?.FirstOrDefault(a => a.LogicalName == attribute);
        if (rollup is null || !RollupColumns.IsRollup(rollup))
            throw new XrmdFaultException(ErrorCodes.RecalculateNotSupportedOnNonRollupField,
                $"The column '{entity}.{attribute}' is not a rollup column, so it cannot be recalculated.", 400);
        var definition = rollups.Translate(table, rollup, store.GetEntity, Relationship);
        return (table, rollup, definition);
    }

    private OneToManyRelationshipMetadata? Relationship(string name) =>
        store.GetRelationship(name) as OneToManyRelationshipMetadata
        ?? store.GetOneToManyRelationships().FirstOrDefault(r => string.Equals(r.SchemaName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The command timeout of a job's statement (<paramref name="online"/> false) or of an online calculation's.</summary>
    internal int CommandTimeoutSeconds(bool online) => online ? _limits.OnlineCommandTimeoutSeconds : _limits.JobCommandTimeoutSeconds;

    private static DbCommand Command(RollupCommand command, DbConnection conn, DbTransaction? tx, int timeoutSeconds)
    {
        var cmd = conn.Command(command.Sql, tx);
        cmd.CommandTimeout = timeoutSeconds;
        // The parameters belong to the command object: each execution gets copies.
        cmd.AddParameters(command.Parameters);
        return cmd;
    }

    private static object? ValueOf(AttributeMetadata rollup, DbDataReader rd, int ordinal)
    {
        if (rd.IsDBNull(ordinal)) return null;
        return rollup switch
        {
            IntegerAttributeMetadata => rd.GetInt32(ordinal),
            DecimalAttributeMetadata => rd.GetDecimal(ordinal),
            MoneyAttributeMetadata => new Money(rd.GetDecimal(ordinal)),
            DateTimeAttributeMetadata => DateTime.SpecifyKind(rd.GetDateTime(ordinal), DateTimeKind.Utc),
            _ => rd.GetValue(ordinal),
        };
    }
}
