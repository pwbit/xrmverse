using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Pipeline.Mail;

/// <summary>
/// The write-only mail secrets (plan 4.1b) of a Create or Update, between the end of stage 20 and the core operation. Like the webhook
/// <c>authvalue</c> (plan 3.2), a secret is taken out of the request's Target itself, so that no later stage sees it: not a synchronous
/// stage-40 step, not an asynchronous step (whose context is persisted) and not a webhook. <see cref="Strip"/> keeps the values aside, keyed
/// by the Target; the core operation <see cref="Claim"/>s them and stores them. Plug-ins of stage 10 and 20 see the Target as sent (as in Dataverse).
/// </summary>
public static class PendingMailSecrets
{
    /// <summary>The MaxLength of a value in <c>meta.mail_secret</c>.</summary>
    public const int StoreLength = 2048;

    private static readonly ConditionalWeakTable<Entity, Dictionary<string, string?>> Pending = [];

    /// <summary>
    /// Removes the secret columns of <paramref name="table"/> from <paramref name="target"/> (in place) and returns them by column name,
    /// a null value clearing the secret.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> for a value that is not a string, or longer than the column's
    /// MaxLength (and never more than <see cref="StoreLength"/>).</exception>
    public static Dictionary<string, string?> Extract(Entity target, EntityMetadata table)
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
            var max = Math.Min(StoreLength, table.Attributes?.OfType<StringAttributeMetadata>().FirstOrDefault(a => a.LogicalName == name)?.MaxLength ?? StoreLength);
            if (raw is string { } text && text.Length > max)
                throw XrmdFaultException.InvalidArgument($"Attribute '{name}' of entity '{target.LogicalName}' is longer than its maximum length of {max} characters.");
            taken[name] = (string?)raw;
        }
        return taken;
    }

    /// <summary>
    /// <see cref="Extract"/> on the pipeline's Target, the values kept for the core operation (<see cref="Claim"/>). Nothing happens for a
    /// table without secrets.
    /// </summary>
    public static void Strip(Entity target, EntityMetadata table)
    {
        var taken = Extract(target, table);
        if (taken.Count == 0) return;
        Pending.Remove(target);
        Pending.Add(target, taken);
    }

    private static readonly HashSet<string> WriteMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Update", "Upsert", "CreateMultiple", "UpdateMultiple", "UpsertMultiple",
    };

    /// <summary>
    /// <see cref="Strip"/> on every Target of a write message of <paramref name="table"/>: the <c>Target</c> of Create, Update and Upsert and
    /// the <c>Targets</c> of the bulk messages (all rows at once, so that no row's secret waits in the context of the row before it).
    /// </summary>
    public static void StripRequest(OrganizationRequest request, EntityMetadata table)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestName is not { } message || !WriteMessages.Contains(message)) return;
        if (request.Parameters.TryGetValue("Target", out var target) && target is Entity single) Strip(single, table);
        if (request.Parameters.TryGetValue("Targets", out var targets) && targets is EntityCollection rows)
            foreach (var row in rows.Entities)
                if (row is not null) Strip(row, table); // a null row is BulkTargets' 400, not an exception here
    }

    /// <summary>The values <see cref="Strip"/> took out of <paramref name="target"/>, once; empty when there are none.</summary>
    public static IReadOnlyDictionary<string, string?> Claim(Entity target)
    {
        if (!Pending.TryGetValue(target, out var taken)) return new Dictionary<string, string?>();
        Pending.Remove(target);
        return taken;
    }

    /// <summary>
    /// Hands the values <see cref="Strip"/> took out of <paramref name="source"/> (an Upsert's Target) to <paramref name="copy"/> (the
    /// Target of its inner Create or Update). The source keeps none.
    /// </summary>
    public static void Carry(Entity source, Entity copy)
    {
        var taken = Claim(source);
        if (taken.Count == 0) return;
        Pending.Remove(copy);
        Pending.Add(copy, new Dictionary<string, string?>(taken, StringComparer.Ordinal));
    }

    /// <summary>True when <paramref name="target"/> carries one of its table's secret columns.</summary>
    public static bool Carries(Entity target) => SecretNames(target.LogicalName).Any(target.Attributes.ContainsKey);

    private static IEnumerable<string> SecretNames(string table) =>
        MailSchema.SecretColumns.Keys.Where(k => k.StartsWith(table + ".", StringComparison.Ordinal)).Select(k => k[(table.Length + 1)..]);
}
