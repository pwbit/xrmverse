using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Solution;

/// <summary>
/// Startup upgrade of an organization made before plan 4.1c: every Sent email with a <c>messageid</c> gets its outgoing message key
/// (<c>meta.mail_message</c>), as the send job registers it from now on, so that a copy of it that comes back is not imported. Runs
/// after <see cref="MailboxSettingsUpgrade"/>.
/// </summary>
/// <remarks>
/// Once: the step <see cref="Step"/> is recorded in <c>meta.upgrade_step</c> in the same transaction, under its key lock, so a second
/// start writes nothing (a new organization records it at its first start, with no email to key). The key is computed by the caller's
/// <c>registerOutgoing</c> (the core's <c>MailMessageKeys.RegisterOutgoingAsync</c>, which this assembly cannot reference), which skips
/// an empty message id and a key already registered.
/// </remarks>
public static class IncomingMailSettingsUpgrade
{
    /// <summary>The <c>meta.upgrade_step</c> name of the backfill (never renamed).</summary>
    public const string Step = "email-outgoing-messagekeys";

    /// <summary>Runs the upgrade.</summary>
    /// <param name="registerOutgoing">Registers the outgoing key of an email's message id; true when it inserted a row.</param>
    public static async Task EnsureAsync(IMetadataStore store, IDbConnectionFactory factory,
        Func<DataSession, Guid, string?, CancellationToken, Task<bool>> registerOutgoing, CancellationToken ct = default, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registerOutgoing);
        if (store.GetEntity(ActivitySchema.Email)?.Attributes?.Any(a => a.LogicalName == "messageid") != true) return;
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var s = new DataSession(conn, tx);
        await s.KeyLockAsync("meta.upgrade_step", Step, ct);
        await using (var done = s.Command($"SELECT COUNT(*) FROM meta.upgrade_step{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE name = @step"))
        {
            done.AddParameter("@step", DbType.String, Step);
            if (Convert.ToInt64(await done.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0) return;
        }

        var sent = new List<(Guid Id, string MessageId)>();
        await using (var cmd = s.Command(
                         $"SELECT activityid, messageid FROM {s.DataTable(ActivitySchema.Email)} " +
                         $"WHERE statecode = 1 AND statuscode = {MailSchema.EmailSent} AND messageid IS NOT NULL ORDER BY activityid"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) sent.Add((rd.GetGuid(0), rd.GetString(1)));
        var registered = 0;
        foreach (var (id, messageId) in sent)
            if (await registerOutgoing(s, id, messageId, ct)) registered++;

        await using (var record = s.Command($"INSERT INTO meta.upgrade_step (name, applied_on) VALUES (@step, {s.Dialect.UtcNow})"))
        {
            record.AddParameter("@step", DbType.String, Step);
            await record.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (registered > 0) logger?.LogInformation("Registered the message keys of {Count} sent emails.", registered);
    }
}
