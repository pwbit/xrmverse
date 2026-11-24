using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Sql;
using Xrmd.Security;

namespace Xrmd.Core.Query;

/// <summary>
/// Plan "personal date format", ruling F4: the formatted values of the date and time columns of a query result are written in
/// the caller's date and time formats and, for a User Local column, in the caller's time zone (<c>usersettings</c>). The
/// settings are read only when the result holds a date formatted value; internal callers and users without a settings row keep
/// <see cref="UserDateFormat.Default"/> (en-US, UTC). Aggregate queries are left as they are: their date groups are computed
/// in UTC (a known limit), so their values stay in UTC and en-US too.
/// </summary>
internal static class UserDateFormatRead
{
    private static readonly string[] Columns =
        [TimeZoneSchema.Code, "dateformatstring", "timeformatstring", "dateseparator", "timeseparator", "amdesignator", "pmdesignator"];

    public static async Task CompleteAsync(EntityCollection result, Caller caller, IMetadataStore store, DataSession session, CancellationToken ct)
    {
        if (caller.UserId == Guid.Empty || !HasDateFormattedValue(result, store)) return;
        var format = await ReadAsync(caller.UserId, store, session, ct);
        if (format != UserDateFormat.Default) Apply(result, store, format);
    }

    /// <summary>Rewrites the date formatted values of <paramref name="result"/> (root columns and linked ones) in <paramref name="format"/>.</summary>
    public static void Apply(EntityCollection result, IMetadataStore store, UserDateFormat format)
    {
        foreach (var entity in result.Entities)
            foreach (var (key, column, value) in Dates(entity, store))
                entity.FormattedValues[key] = format.Format(column, value);
    }

    private static bool HasDateFormattedValue(EntityCollection result, IMetadataStore store) =>
        result.Entities.Any(e => Dates(e, store).Any());

    /// <summary>The attributes of <paramref name="entity"/> holding a date that has a formatted value, with their column.</summary>
    private static IEnumerable<(string Key, DateTimeAttributeMetadata Column, DateTime Value)> Dates(Entity entity, IMetadataStore store)
    {
        foreach (var (key, raw) in entity.Attributes)
        {
            if (!entity.FormattedValues.ContainsKey(key)) continue;
            var (table, attribute, value) = raw is AliasedValue aliased
                ? (aliased.EntityLogicalName, aliased.AttributeLogicalName, aliased.Value)
                : (entity.LogicalName, key, raw);
            if (value is not DateTime date || table is null || attribute is null) continue;
            if (store.GetEntity(table)?.Attributes?.FirstOrDefault(a => a.LogicalName == attribute) is DateTimeAttributeMetadata column)
                yield return (key, column, date);
        }
    }

    private static async Task<UserDateFormat> ReadAsync(Guid userId, IMetadataStore store, DataSession session, CancellationToken ct)
    {
        var settings = store.GetEntity(CurrencySchema.UserSettings);
        if (Columns.Any(c => settings?.Attributes?.Any(a => a.LogicalName == c) != true)) return UserDateFormat.Default;
        // Plan 2.7a: in the dialect's SQL (a query of every user, P11), as LanguageCodeAsync.
        await using var cmd = session.Command(
            $"SELECT {string.Join(", ", Columns)} FROM {session.DataTable(CurrencySchema.UserSettings)} WHERE systemuserid = @xrmd_user");
        cmd.AddParameter("@xrmd_user", DbType.Guid, userId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return UserDateFormat.Default;
        string? Text(int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
        return UserDateFormat.From(rd.IsDBNull(0) ? null : Convert.ToInt32(rd.GetValue(0)), Text(1), Text(2), Text(3), Text(4), Text(5), Text(6));
    }
}
