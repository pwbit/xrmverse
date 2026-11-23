using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Mail;

/// <summary>
/// The rules of the attachments of an email (plan 4.1b, ruling P4), on the caller's <see cref="DataSession"/>: an attachment belongs to
/// an <c>email</c> (not the pointer, a task, a template or anything else), that is still open (state Open: from memory, checklist 5);
/// its file name must not end in an extension the organization blocks (<c>organization.blockedattachments</c>, seeded with Dataverse's
/// default list from Learn: <see cref="MailSchema.DefaultBlockedAttachments"/>); the email's <c>attachmentcount</c> follows the rows and the next
/// <c>attachmentnumber</c> is the highest of the email plus one. They check nothing about the caller's rights (the guard did).
/// </summary>
internal static class EmailAttachments
{
    /// <summary>The email an attachment is created on: 400 for any other table, and the email's row is locked.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for a table other than <c>email</c> or an email that is not open.</exception>
    public static async Task<Guid> DemandOpenEmailAsync(DataSession s, EntityMetadata email, EntityReference? objectId, CancellationToken ct)
    {
        if (objectId is null)
            throw XrmdFaultException.InvalidArgument($"An attachment needs the email it belongs to: set '{ActivitySchema.ObjectId}'.");
        if (objectId.LogicalName != ActivitySchema.Email)
            throw XrmdFaultException.InvalidArgument(
                $"An attachment belongs to an email, not to a record of '{objectId.LogicalName}': set '{ActivitySchema.ObjectId}' to an email.");
        await DemandOpenAsync(s, email, objectId.Id, ct);
        return objectId.Id;
    }

    /// <summary>Locks the email's row and demands it is open.</summary>
    public static async Task DemandOpenAsync(DataSession s, EntityMetadata email, Guid emailId, CancellationToken ct)
    {
        var pk = SystemAttributes.PrimaryIdName(email);
        await s.KeyLockAsync(email.LogicalName, emailId, ct);
        await using var cmd = s.Command(
            $"SELECT {s.Quote("statecode")} FROM {s.DataTable(email.LogicalName)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
            $"WHERE {s.Quote(pk)} = @id{s.Dialect.LockClause(RowLock.UpdateRow)}");
        cmd.AddParameter("@id", DbType.Guid, emailId);
        var state = await cmd.ExecuteScalarAsync(ct);
        if (state is null or DBNull) throw XrmdFaultException.ObjectDoesNotExist(email.LogicalName, emailId);
        if (Convert.ToInt32(state, System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw XrmdFaultException.InvalidArgument(
                $"The email {emailId} is not open (it is sent or closed): its attachments cannot be added or changed.");
    }

    /// <summary>The email of a stored attachment (null when the row does not exist).</summary>
    public static async Task<Guid?> StoredEmailAsync(DataSession s, EntityMetadata attachment, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(ActivitySchema.ObjectId)} FROM {s.DataTable(attachment.LogicalName)} " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(attachment))} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) is Guid email ? email : null;
    }

    /// <summary>A string column of the stored attachment (null when empty or when the row does not exist).</summary>
    public static async Task<string?> StoredStringAsync(DataSession s, EntityMetadata attachment, Guid id, string column, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(column)} FROM {s.DataTable(attachment.LogicalName)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(attachment))} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>The next <c>attachmentnumber</c> of the email: the highest one of its attachments plus one (1 for the first).</summary>
    public static async Task<int> NextNumberAsync(DataSession s, EntityMetadata attachment, Guid emailId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT MAX({s.Quote(MailSchema.AttachmentNumber)}) FROM {s.DataTable(attachment.LogicalName)} " +
            $"WHERE {s.Quote(ActivitySchema.ObjectId)} = @id");
        cmd.AddParameter("@id", DbType.Guid, emailId);
        return await cmd.ExecuteScalarAsync(ct) is { } max and not DBNull ? Convert.ToInt32(max, System.Globalization.CultureInfo.InvariantCulture) + 1 : 1;
    }

    /// <summary>
    /// Sets <c>email.attachmentcount</c> to the number of the email's attachment rows plus <paramref name="adjust"/> (1 before an
    /// INSERT that is about to add one, 0 after a DELETE). The email's row is locked by the caller.
    /// </summary>
    public static async Task RecountAsync(DataSession s, EntityMetadata email, EntityMetadata attachment, Guid emailId, int adjust, CancellationToken ct)
    {
        if (email.Attributes?.Any(a => a.LogicalName == MailSchema.AttachmentCount) != true) return;
        await using var cmd = s.Command(
            $"UPDATE {s.DataTable(email.LogicalName)} SET {s.Quote(MailSchema.AttachmentCount)} = " +
            $"(SELECT COUNT(*) FROM {s.DataTable(attachment.LogicalName)} WHERE {s.Quote(ActivitySchema.ObjectId)} = @id) + @adjust " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(email))} = @id");
        cmd.AddParameter("@id", DbType.Guid, emailId);
        cmd.AddParameter("@adjust", DbType.Int32, adjust);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Refuses a file name whose extension is in <c>organization.blockedattachments</c> (extensions separated by <c>;</c>, a leading
    /// dot or <c>*.</c> allowed, compared without regard to case).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the extension.</exception>
    public static async Task DemandNotBlockedAsync(DataSession s, string? fileName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        await using var cmd = s.Command(
            s.Dialect.Limit($"SELECT {s.Quote(MailSchema.BlockedAttachments)} FROM {s.DataTable("organization")}", "1"));
        if (await cmd.ExecuteScalarAsync(ct) is not string { Length: > 0 } list) return;
        // Windows drops trailing dots and spaces when it saves the file: "run.exe." arrives as "run.exe".
        var extension = Path.GetExtension(fileName.TrimEnd().TrimEnd('.', ' ', '\t').TrimEnd()).TrimStart('.').Trim();
        if (extension.Length == 0) return;
        var blocked = list.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.TrimStart('*').TrimStart('.'));
        if (blocked.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            throw XrmdFaultException.InvalidArgument(
                $"Files of the type '{extension}' are blocked by the organization (blockedattachments): '{fileName}' cannot be attached.");
    }
}
