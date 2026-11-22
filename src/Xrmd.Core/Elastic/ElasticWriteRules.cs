using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Elastic;

/// <summary>
/// The rules of a single-record write on an elastic table (plan 3.4): the partition id is valid and fixed at creation, the time
/// to live is in range, JSON columns hold well-formed JSON (ruling E2), and there is no deep insert and no sharing (ruling E9).
/// Every refusal is 400 <see cref="ErrorCodes.InvalidArgument"/>; a standard table passes every check.
/// </summary>
public static class ElasticWriteRules
{
    /// <summary>The checks of a Create (<paramref name="isCreate"/>) or of an Update that need no stored row.</summary>
    /// <exception cref="XrmdFaultException">400 as the checks below.</exception>
    public static void DemandWrite(EntityMetadata m, Entity e, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(e);
        if (!ElasticTables.IsElastic(m)) return;
        if (isCreate)
        {
            DemandCreate(m, e);
            return;
        }
        DemandNoRelatedEntities(e);
        DemandTtl(m, e);
        DemandValidJsonColumns(m, e);
    }

    /// <summary>
    /// A Create: a valid <c>partitionid</c> (when given), a time to live of at least 1 second (when given), no related entities and
    /// well-formed JSON columns.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming what is refused.</exception>
    public static void DemandCreate(EntityMetadata m, Entity e)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(e);
        if (!ElasticTables.IsElastic(m)) return;
        DemandNoRelatedEntities(e);
        if (e.Attributes.TryGetValue(ElasticTables.PartitionId, out var partition))
            ElasticTables.DemandValidPartitionId(partition as string
                                                 ?? (partition is null ? null : throw XrmdFaultException.InvalidArgument("Invalid partitionid value: it must be a string.")));
        DemandTtl(m, e);
        DemandValidJsonColumns(m, e);
    }

    /// <summary>
    /// An Update of a row stored in <paramref name="storedPartition"/>: a <c>partitionid</c> value, if given, must be the stored
    /// one (it is never changed, Review Focus 5); the other checks of <see cref="DemandWrite"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming what is refused.</exception>
    public static void DemandUpdate(EntityMetadata m, Entity e, string? storedPartition)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(e);
        if (!ElasticTables.IsElastic(m)) return;
        if (e.Attributes.TryGetValue(ElasticTables.PartitionId, out var given) && !Equals(given, storedPartition))
            throw XrmdFaultException.InvalidArgument(
                $"The partitionid of a record of the elastic table '{m.LogicalName}' cannot be changed after it is created.");
        DemandWrite(m, e, isCreate: false);
    }

    /// <summary>A write with related entities (a deep insert) is refused.</summary>
    /// <exception cref="XrmdFaultException">400.</exception>
    public static void DemandNoRelatedEntities(Entity e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.RelatedEntities is { Count: > 0 })
            throw XrmdFaultException.InvalidArgument(
                $"Creating or updating related records together with a record of the elastic table '{e.LogicalName}' is not supported (no deep insert on an elastic table).");
    }

    /// <summary>Sharing (GrantAccess, ModifyAccess) a record of an elastic table is refused (ruling E9).</summary>
    /// <exception cref="XrmdFaultException">400.</exception>
    public static void DemandShareable(EntityMetadata m)
    {
        ArgumentNullException.ThrowIfNull(m);
        ElasticTables.DemandNotElastic(m, "Record shares (GrantAccess, ModifyAccess)");
    }

    /// <summary>
    /// The value of each String column with <c>FormatName = Json</c> written by <paramref name="e"/> must be well-formed JSON
    /// (ruling E2); null and absent values pass.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the column.</exception>
    public static void DemandValidJsonColumns(EntityMetadata m, Entity e)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(e);
        if (!ElasticTables.IsElastic(m)) return;
        foreach (var a in m.Attributes ?? [])
        {
            if (a is not StringAttributeMetadata { FormatName.Value: { } format } || format != StringFormatName.Json.Value
                || !e.Attributes.TryGetValue(a.LogicalName, out var value) || value is not string json)
                continue;
            try
            {
                using var _ = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw XrmdFaultException.InvalidArgument(
                    $"Column '{a.LogicalName}' of entity '{m.LogicalName}' holds JSON and the value is not well-formed JSON: {ex.Message}");
            }
        }
    }

    /// <summary>A time to live, when given, is a whole number of seconds from 1 to 2147483647.</summary>
    private static void DemandTtl(EntityMetadata m, Entity e)
    {
        if (e.Attributes.TryGetValue(ElasticTables.TtlInSeconds, out var ttl) && ttl is int seconds && seconds < 1)
            throw XrmdFaultException.InvalidArgument(
                $"Column '{ElasticTables.TtlInSeconds}' of entity '{m.LogicalName}' must be from 1 to {int.MaxValue} seconds, got {seconds}.");
    }
}
