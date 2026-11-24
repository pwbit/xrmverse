using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Core.Mail;

/// <summary>An address of an incoming email and the records that carry it.</summary>
/// <param name="Address">The address as the message gave it.</param>
/// <param name="Records">The active accounts and contacts, enabled users and active queues whose address equals it (ignoring case).</param>
public sealed record AddressMatch(string Address, IReadOnlyList<EntityReference> Records);

/// <summary>
/// The parties of an incoming email (plan 4.1c, Task 5): the addresses of the message resolve to records, and the party lists of the
/// email are built from them. An ambiguous sender stays unresolved (the party keeps only its address); every record that matches a
/// recipient is linked. A queue is a valid party. Smart matching is not done.
/// </summary>
public static class IncomingParties
{
    // One statement takes at most 2100 parameters on SQL Server; the address list of a statement stays far below.
    private const int BatchSize = 500;

    // The tables an address resolves against, with their key and the columns holding an address; the optional ones only when present.
    private static readonly (string Table, string Key, string[] Required, string[] Optional, string Active)[] Sources =
    [
        ("account", "accountid", ["emailaddress1"], ["emailaddress2", "emailaddress3"], "statecode"),
        ("contact", "contactid", ["emailaddress1"], ["emailaddress2", "emailaddress3"], "statecode"),
        ("systemuser", "systemuserid", [ActivitySchema.InternalEmailAddress], [], "isdisabled"),
        (QueueSchema.Queue, "queueid", ["emailaddress"], [], "statecode"),
    ];

    /// <summary>
    /// Resolves <paramref name="addresses"/> to records: one query per table (per batch of 500 addresses), equality ignoring case.
    /// Every address is a key of the result (case-insensitive), with no records when nothing matches. Addresses are compared trimmed
    /// and lower-cased on both sides. <paramref name="metadata"/> tells whether the optional <c>emailaddress2</c> and
    /// <c>emailaddress3</c> columns of account and contact exist.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, AddressMatch>> ResolveAsync(DataSession s, IMetadataStore metadata,
        IEnumerable<string> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(addresses);
        // The original spelling of each distinct address (by its lower-case form); the first one wins.
        var byLower = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = new Dictionary<string, List<EntityReference>>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address)) continue;
            var trimmed = address.Trim();
            if (byLower.TryAdd(trimmed.ToLowerInvariant(), trimmed)) found[trimmed.ToLowerInvariant()] = [];
        }

        var lowers = byLower.Keys.Where(a => a.Length <= ActivitySchema.AddressUsedMaxLength).ToArray();
        if (lowers.Length > 0)
            foreach (var (table, key, required, optional, active) in Sources)
            {
                var columns = new List<string>(required);
                foreach (var column in optional)
                    if (HasColumn(metadata, s, table, column)) columns.Add(column);
                var activeCondition = active == "isdisabled" ? $"({s.Quote(active)} IS NULL OR {s.Quote(active)} = @_active)" : $"({s.Quote(active)} IS NULL OR {s.Quote(active)} = 0)";
                foreach (var batch in lowers.Chunk(BatchSize))
                {
                    var list = string.Join(", ", batch.Select((_, i) => $"@_a{i}"));
                    var any = string.Join(" OR ", columns.Select(c => $"TRIM(LOWER({s.Quote(c)})) IN ({list})"));
                    var select = string.Join(", ", columns.Select(s.Quote));
                    await using var cmd = s.Command(
                        $"SELECT {s.Quote(key)}, {select} FROM {s.DataTable(table)} WHERE {activeCondition} AND ({any}) ORDER BY {s.Quote(key)}");
                    if (active == "isdisabled") cmd.AddParameter("@_active", DbType.Boolean, false);
                    for (var i = 0; i < batch.Length; i++)
                        cmd.AddParameter($"@_a{i}", DbType.String, batch[i], ActivitySchema.AddressUsedMaxLength);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        var id = rd.GetGuid(0);
                        var rowAddresses = new HashSet<string>(StringComparer.Ordinal);
                        for (var i = 0; i < columns.Count; i++)
                            if (!rd.IsDBNull(i + 1) && rd.GetString(i + 1).Trim().ToLowerInvariant() is var lower && byLower.ContainsKey(lower))
                                rowAddresses.Add(lower);
                        // One record, however many of its addresses (or columns) match.
                        foreach (var lower in rowAddresses) found[lower].Add(new EntityReference(table, id));
                    }
                }
            }

        return found.ToDictionary(kv => byLower[kv.Key], kv => new AddressMatch(byLower[kv.Key], kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasColumn(IMetadataStore metadata, DataSession s, string table, string column) =>
        metadata.GetEntity(s, table)?.Attributes?.Any(a => a.LogicalName == column) == true;

    /// <summary>
    /// The party lists of an incoming email. The sender is linked only with exactly one match, else its party carries the address alone
    /// (<c>SenderAmbiguous</c> tells more than one record matched); each recipient is one party per matching record, or one party with
    /// the address alone.
    /// </summary>
    public static (EntityCollection From, EntityCollection To, EntityCollection Cc, EntityCollection Bcc, EntityReference? Sender, bool SenderAmbiguous)
        Build(AddressMatch from, IReadOnlyList<AddressMatch> to, IReadOnlyList<AddressMatch> cc, IReadOnlyList<AddressMatch> bcc)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(cc);
        ArgumentNullException.ThrowIfNull(bcc);
        var sender = from.Records.Count == 1 ? from.Records[0] : null;
        var fromList = new EntityCollection([Party(sender, from.Address, ParticipationType.Sender)]) { EntityName = ActivitySchema.Party };
        return (fromList, Recipients(to, ParticipationType.ToRecipient), Recipients(cc, ParticipationType.CcRecipient),
            Recipients(bcc, ParticipationType.BccRecipient), sender, from.Records.Count > 1);
    }

    private static EntityCollection Recipients(IReadOnlyList<AddressMatch> matches, ParticipationType type)
    {
        var collection = new EntityCollection { EntityName = ActivitySchema.Party };
        // A record that matches two addresses of the same list is one party.
        var seen = new HashSet<(string, Guid)>();
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            if (match.Records.Count == 0 && seenAddresses.Add(match.Address.Trim())) collection.Entities.Add(Party(null, match.Address, type));
            foreach (var record in match.Records)
                if (seen.Add((record.LogicalName, record.Id))) collection.Entities.Add(Party(record, match.Address, type));
        }

        return collection;
    }

    private static Entity Party(EntityReference? record, string address, ParticipationType type) => new(ActivitySchema.Party)
    {
        [ActivitySchema.PartyId] = record,
        [ActivitySchema.AddressUsed] = address,
        [ActivitySchema.ParticipationTypeMask] = new OptionSetValue((int)type),
    };
}
