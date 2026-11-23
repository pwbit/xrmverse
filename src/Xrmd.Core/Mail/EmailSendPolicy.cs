using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Mail;

/// <summary>
/// The sender policy of the environment (plan 4.1b, an xrmverse extension: Dataverse has no such switch), the setting
/// <see cref="AppModuleSchema.EmailSendFrom"/>: <c>userOrQueue</c> (the default, as Dataverse) lets a user send from their own mailbox
/// or from a queue they can read; <c>queueOnly</c> lets only a queue send. Read as the shell resolves an environment setting: the
/// <c>organizationsetting</c> value of the definition, else the definition's <c>defaultvalue</c>, else <c>userOrQueue</c>.
/// </summary>
public static class EmailSendPolicy
{
    /// <summary>True when the environment value is <c>queueOnly</c>.</summary>
    /// <exception cref="XrmdFaultException">400 for a value that is neither <c>userOrQueue</c> nor <c>queueOnly</c>: an unknown policy
    /// is refused rather than read as either.</exception>
    public static async Task<bool> QueueOnlyAsync(DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"""
            SELECT o.value, d.defaultvalue FROM {s.DataTable(AppModuleSchema.SettingDefinition)} d
            LEFT JOIN {s.DataTable(AppModuleSchema.OrganizationSetting)} o ON o.settingdefinitionid = d.settingdefinitionid
            WHERE d.settingdefinitionid = @id
            """);
        cmd.AddParameter("@id", DbType.Guid, AppModuleSchema.EmailSendFromId);
        string? value = null;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct))
                value = !rd.IsDBNull(0) && rd.GetString(0).Trim() is { Length: > 0 } set ? set : rd.IsDBNull(1) ? null : rd.GetString(1).Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, AppModuleSchema.EmailSendFromUserOrQueue, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(value, AppModuleSchema.EmailSendFromQueueOnly, StringComparison.OrdinalIgnoreCase)) return true;
        throw XrmdFaultException.InvalidArgument(
            $"The environment setting {AppModuleSchema.EmailSendFrom} has the value '{value}', which xrmverse does not know: set it to " +
            $"'{AppModuleSchema.EmailSendFromUserOrQueue}' or '{AppModuleSchema.EmailSendFromQueueOnly}'.");
    }

    /// <summary>The refusal of a sender that is not a queue under <c>queueOnly</c>.</summary>
    /// <param name="sender">The <c>from</c> party; null when the email has none.</param>
    public static string QueueOnlyRefusal(Guid emailId, EntityReference? sender) =>
        $"The email {emailId} cannot be sent: the environment setting {AppModuleSchema.EmailSendFrom} is '{AppModuleSchema.EmailSendFromQueueOnly}', " +
        $"so only a queue sends email, and its sender (from) is {(sender is null ? "empty" : $"{sender.LogicalName} {sender.Id}")}. Set from to a queue.";
}
