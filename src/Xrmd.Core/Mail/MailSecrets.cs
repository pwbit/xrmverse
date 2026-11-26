using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Mail;

namespace Xrmd.Core.Mail;

/// <summary>
/// The write-only passwords of the mail tables (plan 4.1b): <c>emailserverprofile.incomingpassword</c>, <c>outgoingpassword</c> and
/// <c>oauthclientsecret</c>, and <c>mailbox.password</c>. As the webhook <c>authvalue</c> (plan 3.2), a secret has no data column:
/// the core write takes it out of the Create or Update target (the pipeline already did, <see cref="PendingMailSecrets"/>), <see cref="StoreAsync"/> keeps it in
/// <c>meta.mail_secret</c> (one row per record and column, which no read path reaches) and sets the record's read-only <c>is*set</c>
/// flag. A value is read back only by the server (<see cref="ReadAsync"/>) to send or sync mail, and never appears in a log or a fault.
/// </summary>
public static class MailSecrets
{
    private const string SecretTable = "meta.mail_secret";

    /// <summary>The MaxLength of the secret column in <c>meta.mail_secret</c>.</summary>
    private const int MaxValueLength = PendingMailSecrets.StoreLength;

    /// <summary>
    /// Stores the taken values for the record in <paramref name="s"/>'s transaction (update, else insert, under the record's key lock;
    /// a null deletes the row) and writes the record's <c>is*set</c> flags: true for a value, false after a null. Call it after the core
    /// write of the record (the flags are read-only columns, which the core operation does not write).
    /// </summary>
    public static async Task StoreAsync(DataSession s, Guid recordId, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return;
        await s.KeyLockAsync(SecretTable, recordId, ct);
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                await using var delete = s.Command($"DELETE FROM {SecretTable} WHERE recordid = @id AND name = @name");
                delete.AddParameter("@id", DbType.Guid, recordId);
                delete.AddParameter("@name", DbType.String, name, 64);
                await delete.ExecuteNonQueryAsync(ct);
                continue;
            }
            await using (var update = s.Command($"UPDATE {SecretTable} SET value = @value WHERE recordid = @id AND name = @name"))
            {
                update.AddParameter("@id", DbType.Guid, recordId);
                update.AddParameter("@name", DbType.String, name, 64);
                update.AddParameter("@value", DbType.String, value, MaxValueLength);
                if (await update.ExecuteNonQueryAsync(ct) > 0) continue;
            }
            await using var insert = s.Command($"INSERT INTO {SecretTable} (recordid, name, value) VALUES (@id, @name, @value)");
            insert.AddParameter("@id", DbType.Guid, recordId);
            insert.AddParameter("@name", DbType.String, name, 64);
            insert.AddParameter("@value", DbType.String, value, MaxValueLength);
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach (var byTable in values.GroupBy(v => FlagOf(v.Key).Table))
        {
            var sets = string.Join(", ", byTable.Select(v => $"{FlagOf(v.Key).FlagColumn} = {s.Dialect.BooleanLiteral(v.Value is not null)}"));
            await using var flag = s.Command($"UPDATE {s.DataTable(byTable.Key)} SET {sets} WHERE {byTable.Key}id = @id");
            flag.AddParameter("@id", DbType.Guid, recordId);
            await flag.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The stored secret <paramref name="name"/> of the record, or null when it has none. For server use only.</summary>
    public static async Task<string?> ReadAsync(DataSession s, Guid recordId, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT value FROM {SecretTable} WHERE recordid = @id AND name = @name");
        cmd.AddParameter("@id", DbType.Guid, recordId);
        cmd.AddParameter("@name", DbType.String, name, 64);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Removes the secrets of a deleted record (in the Delete's transaction).</summary>
    public static async Task DeleteAsync(DataSession s, Guid recordId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"DELETE FROM {SecretTable} WHERE recordid = @id");
        cmd.AddParameter("@id", DbType.Guid, recordId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>True for a table that has secret columns.</summary>
    public static bool HasSecrets(string table) => MailSchema.SecretColumns.Keys.Any(k => k.StartsWith(table + ".", StringComparison.Ordinal));

    private static (string Table, string FlagColumn) FlagOf(string column) =>
        MailSchema.SecretColumns.Single(kv => kv.Key.EndsWith("." + column, StringComparison.Ordinal)).Value;
}
