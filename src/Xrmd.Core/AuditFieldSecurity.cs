using Microsoft.Crm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// Field security on audit data (plan 14, ruling F11): which secured columns of an audited record a caller cannot read, and the
/// change data and audit details without them. Shared by the audit messages (<see cref="AuditMessages"/>) and by queries over the
/// <c>audit</c> table (<see cref="Query.QueryExecutor"/>), so that both doors to the same rows hide the same values.
/// </summary>
internal static class AuditFieldSecurity
{
    private static readonly IReadOnlySet<string> NoColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The secured columns <paramref name="caller"/> cannot read on each audited record of <paramref name="records"/> (table
    /// logical name and record id): those its profiles deny on the table, less those a field share grants on the record. Read once
    /// per table; a table without such a column (or a caller field security does not restrict) reads no share.
    /// </summary>
    public static async Task<Func<string, Guid, IReadOnlySet<string>>> HiddenAsync(FieldSecurity fields, Caller caller,
        IEnumerable<(string Table, Guid Record)> records, DataSession session, CancellationToken ct)
    {
        var tables = new Dictionary<string, (IReadOnlySet<string> Unreadable, IReadOnlyDictionary<Guid, FieldShareGrant> Shares)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var table in records.GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase))
        {
            var unreadable = (await fields.ForAsync(caller, table.Key, ct)).UnreadableSecured;
            if (unreadable.Count == 0) continue;
            var ids = table.Select(r => r.Record).Distinct().ToList();
            tables[table.Key] = (unreadable, await fields.SharesAsync(caller, table.Key, ids, unreadable, session, ct));
        }
        return (table, record) =>
        {
            if (!tables.TryGetValue(table, out var t)) return NoColumns;
            return t.Shares.TryGetValue(record, out var grant)
                ? t.Unreadable.Where(c => !grant.Read.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : t.Unreadable;
        };
    }

    /// <summary>
    /// <paramref name="changeData"/> without the values of <paramref name="hidden"/> (the same text when it holds none of them, or
    /// is not an attribute change); null when it cannot be read, which hides it all (fails closed).
    /// </summary>
    public static string? WithoutColumns(string changeData, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0) return changeData;
        AuditChange change;
        try
        {
            change = ChangeData.Deserialize(changeData);
        }
        catch (FormatException)
        {
            return null;
        }
        if (change is not AttributeChange a || !(a.Old.Keys.Any(hidden.Contains) || a.New.Keys.Any(hidden.Contains))) return changeData;
        return ChangeData.Serialize(new AttributeChange(Without(a.Old), Without(a.New)));

        IReadOnlyDictionary<string, object?> Without(IReadOnlyDictionary<string, object?> values) =>
            values.Where(v => !hidden.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Removes <paramref name="hidden"/> from an attribute detail: the old and new values with their formatted values, the deleted
    /// attributes and the <c>changedata</c> of its audit record.
    /// </summary>
    public static void Hide(AttributeAuditDetail detail, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0) return;
        foreach (var image in new[] { detail.OldValue, detail.NewValue })
        {
            if (image is null) continue;
            foreach (var column in image.Attributes.Keys.Where(hidden.Contains).ToList())
            {
                image.Attributes.Remove(column);
                image.FormattedValues.Remove(column);
            }
        }
        foreach (var position in detail.DeletedAttributes.Where(d => hidden.Contains(d.Value)).Select(d => d.Key).ToList())
            detail.DeletedAttributes.Remove(position);
        if (detail.AuditRecord.GetAttributeValue<string>(AuditTable.ChangeData) is { } json)
            detail.AuditRecord[AuditTable.ChangeData] = WithoutColumns(json, hidden);
    }
}
