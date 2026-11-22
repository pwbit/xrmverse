using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Core;

/// <summary>
/// The deletion log of change tracking (plan 2.13): one <c>meta.deleted_record</c> row per record deleted from a tracked table,
/// written in the transaction of the Delete, so a rolled back Delete leaves no row. The row's <c>version</c> is its own
/// rowversion, which orders the deletions for <c>RetrieveEntityChanges</c>.
/// </summary>
public static class ChangeTrackingLog
{
    /// <summary>Logs the deletion of record <paramref name="id"/> of <paramref name="entity"/> (deleted on = now, UTC).</summary>
    public static async Task RecordDeleteAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // PostgreSQL draws the version through the organization's function (plan 2.7a, Task 7: ISqlDialect.NextRowVersion), which
        // publishes the transaction to the bound of RetrieveEntityChanges; SQL Server's rowversion sets itself.
        var version = s.Dialect.NextRowVersion;
        await using var cmd = s.Command(
            $"INSERT INTO meta.deleted_record ({(version is null ? "" : "version, ")}entity_logical_name, object_id, deleted_on) " +
            $"VALUES ({(version is null ? "" : version + ", ")}@entity, @id, {s.Dialect.UtcNow})");
        cmd.AddParameter("@entity", DbType.String, entity, 128);
        cmd.AddParameter("@id", DbType.Guid, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
