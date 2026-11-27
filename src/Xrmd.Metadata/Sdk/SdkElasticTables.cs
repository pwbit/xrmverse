using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="ElasticTables"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class ElasticTables
{
    /// <summary>The SDK twin of <see cref="IsElastic(TableDefinition)"/>.</summary>
    public static bool IsElastic(EntityMetadata e) =>
        e is not null && string.Equals(e.TableType, TableTypeElastic, StringComparison.OrdinalIgnoreCase);

    /// <summary>The SDK twin of <see cref="NoSqlKey(TableDefinition)"/>.</summary>
    public static EntityKeyMetadata NoSqlKey(EntityMetadata e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return SdkTwin.Sdk(NoSqlKey(SdkTwin.Model(e)));
    }

    /// <summary>The SDK twin of <see cref="DemandValidEntity(TableDefinition)"/>.</summary>
    public static void DemandValidEntity(EntityMetadata e)
    {
        ArgumentNullException.ThrowIfNull(e);
        DemandValidEntity(SdkTwin.Model(e));
    }

    /// <summary>The SDK twin of <see cref="DemandValidAttribute(TableDefinition, ColumnDefinition)"/>.</summary>
    public static void DemandValidAttribute(EntityMetadata e, AttributeMetadata a)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(a);
        DemandValidAttribute(SdkTwin.Model(e), SdkTwin.Model(a));
    }

    /// <summary>The SDK twin of <see cref="DemandValidRelationship(OneToManyRelationship, TableDefinition, TableDefinition)"/>.</summary>
    public static void DemandValidRelationship(OneToManyRelationshipMetadata r, EntityMetadata referenced, EntityMetadata referencing)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentNullException.ThrowIfNull(referencing);
        DemandValidRelationship(SdkTwin.Model(r), SdkTwin.Model(referenced), SdkTwin.Model(referencing));
    }

    /// <summary>The SDK twin of <see cref="UpsertClearedColumns(TableDefinition, Func{string, bool})"/>, for a request record.</summary>
    public static IReadOnlyList<string> UpsertClearedColumns(EntityMetadata e, Entity target)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(target);
        return UpsertClearedColumns(SdkTwin.Model(e), target.Contains);
    }

    /// <summary>The SDK twin of <see cref="DemandNotElastic(TableDefinition, string)"/>.</summary>
    public static void DemandNotElastic(EntityMetadata e, string feature)
    {
        ArgumentNullException.ThrowIfNull(e);
        DemandNotElastic(SdkTwin.Model(e), feature);
    }

    /// <summary>
    /// Final review I4: the ids a write on the elastic table <paramref name="e"/> gives for its row (<see cref="Entity.Id"/>, the
    /// primary key attribute, the primary id among the <c>KeyAttributes</c>) must agree, so that the security guard and the core
    /// address the same row whichever of them each reads first. Nothing is checked on a standard table.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when two of the ids given differ.</exception>
    public static void DemandOneRowId(EntityMetadata e, Entity target)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(target);
        if (!IsElastic(e)) return;
        var pk = SystemAttributes.PrimaryIdName(e);
        DemandOneRowId(e.LogicalName, pk, target.Id, target.Attributes.TryGetValue(pk, out var value) ? value : null,
            target.KeyAttributes is { } keys && keys.TryGetValue(pk, out var key) ? key : null);
    }
}
