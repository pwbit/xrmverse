using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Pipeline.Webhooks;

/// <summary>
/// The write-only <c>authvalue</c> of a <c>serviceendpoint</c> (plan 3.2): taken out of the Create or Update target before the core
/// operation and kept in <c>meta.serviceendpoint_secret</c>, which no read path reaches; <c>isauthvalueset</c> tells that it is
/// set. The value is read back only to deliver a webhook (and to check an Update as the row will be). It never appears in a log or
/// a fault.
/// </summary>
public sealed class WebhookSecrets
{
    private const string SecretTable = "meta.serviceendpoint_secret";

    /// <summary>
    /// Takes the <c>authvalue</c> out of <paramref name="target"/> (Create or Update of <c>serviceendpoint</c>) and stores it in the
    /// secret table, in <paramref name="s"/>'s transaction; sets <c>isauthvalueset</c> true in the target (the core operation does not
    /// write a read-only column: <see cref="MarkSetAsync"/> writes it after the core operation). A Create without an id is given one,
    /// the key of the secret row. Nothing happens when the target has no <c>authvalue</c> (an Update keeps the stored value).
    /// </summary>
    /// <returns>The endpoint id whose value was taken, or null when the target had none.</returns>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> "needs an authvalue" for an empty or blank value.</exception>
    public async Task<Guid?> TakeAsync(Entity target, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(s);
        if (!target.Attributes.TryGetValue(ServiceEndpointSchema.AuthValue, out var raw)) return null;
        var id = target.Id != Guid.Empty ? target.Id
            : target.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(ServiceEndpointSchema.Table), out var given) && given is Guid g ? g
            : Guid.NewGuid();
        if (raw is not string value || string.IsNullOrWhiteSpace(value))
            throw XrmdFaultException.InvalidArgument(ServiceEndpointRules.NeedsAuthValue(target.GetAttributeValue<string>("name") ?? id.ToString()));

        target.Attributes.Remove(ServiceEndpointSchema.AuthValue);
        target.Id = id;
        target[ServiceEndpointSchema.IsAuthValueSet] = true;
        // Plan 2.7b, ruling Q5: the update-else-insert batch became the key lock of the endpoint, its update, then its insert when
        // the update found no row (both in the caller's transaction). The key lock is PostgreSQL's; SQL Server runs the batch's
        // UPDATE and INSERT as they were, its UPDATE waiting for an uncommitted row of the endpoint (UpsertInterleavingTests).
        await s.KeyLockAsync(SecretTable, id, ct);
        await using (var update = s.Command($"UPDATE {SecretTable} SET authvalue = @value WHERE serviceendpointid = @id"))
        {
            update.AddParameter("@id", DbType.Guid, id);
            update.AddParameter("@value", DbType.String, value, ServiceEndpointRules.MaxAuthValueLength);
            if (await update.ExecuteNonQueryAsync(ct) > 0) return id;
        }
        await using var insert = s.Command($"INSERT INTO {SecretTable} (serviceendpointid, authvalue) VALUES (@id, @value)");
        insert.AddParameter("@id", DbType.Guid, id);
        insert.AddParameter("@value", DbType.String, value, ServiceEndpointRules.MaxAuthValueLength);
        await insert.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>Writes <c>isauthvalueset</c> = 1 on the stored endpoint, after the core operation of a <see cref="TakeAsync"/>.</summary>
    public async Task MarkSetAsync(Guid endpointId, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(
            $"UPDATE {s.DataTable(ServiceEndpointSchema.Table)} SET {ServiceEndpointSchema.IsAuthValueSet} = {s.Dialect.BooleanLiteral(true)} WHERE serviceendpointid = @id");
        cmd.AddParameter("@id", DbType.Guid, endpointId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The stored <c>authvalue</c> of the endpoint, or null when it has none.</summary>
    public async Task<string?> ReadAsync(Guid endpointId, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT authvalue FROM {SecretTable} WHERE serviceendpointid = @id");
        cmd.AddParameter("@id", DbType.Guid, endpointId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Removes the stored <c>authvalue</c> of a deleted endpoint (in the Delete's transaction).</summary>
    public async Task DeleteAsync(Guid endpointId, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"DELETE FROM {SecretTable} WHERE serviceendpointid = @id");
        cmd.AddParameter("@id", DbType.Guid, endpointId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
