using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>The UI language of a caller (#109), shared by the query executor and the connector catalog.</summary>
public static class UserLanguage
{
    private const string UiLanguageId = "uilanguageid";

    /// <summary>
    /// The caller's UI language (<c>usersettings.uilanguageid</c>), in which choice labels are formatted and choice orders
    /// sort; <see cref="FormattedValues.English"/> for an internal caller, a caller without settings or an unset language.
    /// </summary>
    /// <param name="store">When given, a model without <c>usersettings.uilanguageid</c> yields English without querying.</param>
    public static async Task<int> ForAsync(Caller caller, DataSession session, CancellationToken ct, IMetadataStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);
        if (caller.UserId == Guid.Empty ||
            (store is not null && store.GetTable(CurrencySchema.UserSettings)?.Attributes?
                .Any(a => a.LogicalName == UiLanguageId) != true))
            return FormattedValues.English;
        await using var cmd = session.Command(
            $"SELECT {UiLanguageId} FROM {session.DataTable(CurrencySchema.UserSettings)} WHERE systemuserid = @xrmd_user");
        cmd.AddParameter("@xrmd_user", DbType.Guid, caller.UserId);
        return await cmd.ExecuteScalarAsync(ct) is int lcid && lcid > 0 ? lcid : FormattedValues.English;
    }
}
