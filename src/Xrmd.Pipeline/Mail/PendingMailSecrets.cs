using System.Runtime.CompilerServices;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline.Mail;

/// <summary>
/// The write-only mail secrets (plan 4.1b) of a Create or Update, between the end of stage 20 and the core operation. Like the webhook
/// <c>authvalue</c> (plan 3.2), a secret is taken out of the request's Target itself, so that no later stage sees it: not a synchronous
/// stage-40 step, not an asynchronous step (whose context is persisted) and not a webhook. <see cref="Strip"/> keeps the values aside, keyed
/// by the model Target (S.2e3c2b, Decision 6); the core operation <see cref="Claim"/>s them and stores them. Plug-ins of stage 10 and 20 see
/// the Target as sent (as in Dataverse).
/// </summary>
public static class PendingMailSecrets
{
    /// <summary>The MaxLength of a value in <c>meta.mail_secret</c>.</summary>
    public const int StoreLength = 2048;

    private static readonly ConditionalWeakTable<Record, Dictionary<string, string?>> Pending = [];

    private static readonly IReadOnlyDictionary<string, string?> None = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Removes the secret columns of <paramref name="table"/> from <paramref name="target"/> (in place) and returns them by column name,
    /// a null value clearing the secret.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> for a value that is not a string, or longer than the column's
    /// MaxLength (and never more than <see cref="StoreLength"/>).</exception>
    public static Dictionary<string, string?> Extract(Record target, TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(table);
        var taken = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in SecretNames(target.LogicalName))
        {
            if (!target.Attributes.TryGetValue(name, out var raw)) continue;
            target.Attributes.Remove(name);
            if (raw is not (null or string))
                throw XrmdFaultException.InvalidArgument($"Attribute '{name}' of entity '{target.LogicalName}' must be a string.");
            var max = Math.Min(StoreLength, table.Attributes?.OfType<TextColumn>().FirstOrDefault(a => a.LogicalName == name)?.MaxLength ?? StoreLength);
            if (raw is string { } text && text.Length > max)
                throw XrmdFaultException.InvalidArgument($"Attribute '{name}' of entity '{target.LogicalName}' is longer than its maximum length of {max} characters.");
            taken[name] = (string?)raw;
        }
        return taken;
    }

    /// <summary>
    /// <see cref="Extract"/> on the pipeline's model Target, the values kept for the core operation (<see cref="Claim"/>). Nothing happens
    /// for a table without secrets, or a Target that carries none.
    /// </summary>
    /// <returns>True when a secret column was taken out of <paramref name="target"/> (the Target changed).</returns>
    public static bool Strip(Record target, TableDefinition table)
    {
        var taken = Extract(target, table);
        if (taken.Count == 0) return false;
        Pending.AddOrUpdate(target, taken);
        return true;
    }

    private static readonly HashSet<string> WriteMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Update", "Upsert", "CreateMultiple", "UpdateMultiple", "UpsertMultiple",
    };

    /// <summary>
    /// <see cref="Strip"/> on every Target of a write message of <paramref name="table"/>: the <c>Target</c> of Create, Update and Upsert and
    /// the <c>Targets</c> of the bulk messages (all rows at once, so that no row's secret waits in the context of the row before it).
    /// </summary>
    /// <returns>True when a Target changed. A fault of a later row leaves the rows before it stripped.</returns>
    public static bool StripRequest(Request request, TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Name is not { } message || !WriteMessages.Contains(message)) return false;
        var stripped = false;
        if (request.Parameters.TryGetValue("Target", out var target) && target is Record single) stripped |= Strip(single, table);
        if (request.Parameters.TryGetValue("Targets", out var targets) && targets is RecordCollection rows)
            foreach (var row in rows.Records)
                if (row is not null) stripped |= Strip(row, table); // a null row is BulkTargets' 400, not an exception here
        return stripped;
    }

    /// <summary>The values <see cref="Strip"/> took out of <paramref name="target"/>, once; empty when there are none.</summary>
    public static IReadOnlyDictionary<string, string?> Claim(Record target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Pending.TryGetValue(target, out var taken)) return None;
        Pending.Remove(target);
        return taken;
    }

    /// <summary>
    /// Hands the values <see cref="Strip"/> took out of <paramref name="source"/> (an Upsert's model Target) to <paramref name="copy"/> (the
    /// model Target of its inner Create or Update). The source keeps none.
    /// </summary>
    public static void Carry(Record source, Record copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var taken = Claim(source);
        if (taken.Count == 0) return;
        Pending.AddOrUpdate(copy, new Dictionary<string, string?>(taken, StringComparer.Ordinal));
    }

    /// <summary>True when <paramref name="target"/> carries one of its table's secret columns.</summary>
    public static bool Carries(Record target) => SecretNames(target.LogicalName).Any(target.Attributes.ContainsKey);

    private static IEnumerable<string> SecretNames(string table) =>
        MailSchema.SecretColumns.Keys.Where(k => k.StartsWith(table + ".", StringComparison.Ordinal)).Select(k => k[(table.Length + 1)..]);
}
