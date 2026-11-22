using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Security;

/// <summary>
/// The write rule of <c>organizationsetting</c> (UI-2a), run by the core on the final row of a Create or Update, in the request's
/// transaction: a definition has at most one environment value.
/// </summary>
public static class OrganizationSettingRules
{
    private const string Definition = "settingdefinitionid";

    /// <summary>
    /// A Create, or an Update that gives the definition, of an <c>organizationsetting</c> whose definition already has another
    /// row is refused. The rows read are locked (UPDLOCK, HOLDLOCK) until commit, so two concurrent writes cannot both pass.
    /// Other tables: nothing.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <see cref="ErrorCodes.DuplicateRecord"/>.</exception>
    public static async Task ApplyRowAsync(DataSession s, string entityLogicalName, Entity row, bool isCreate, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        if (entityLogicalName != AppModuleSchema.OrganizationSetting || (!isCreate && !row.Contains(Definition))) return;
        if (DuplicateRuleRules.IdOf(row.Contains(Definition) ? row[Definition] : null) is not { } definitionId || definitionId == Guid.Empty) return;

        await s.KeyLockAsync(AppModuleSchema.OrganizationSetting, definitionId, ct);
        await using var cmd = s.Command($"""
            SELECT COUNT(*) FROM {s.DataTable(AppModuleSchema.OrganizationSetting)}{s.Dialect.LockHint(RowLock.UpdateRange)}
            WHERE {s.Quote(Definition)} = @_definition AND {s.Quote(AppModuleSchema.OrganizationSetting + "id")} <> @_id
            """);
        cmd.AddParameter("@_definition", DbType.Guid, definitionId);
        cmd.AddParameter("@_id", DbType.Guid, id);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 0) return;

        var name = row.GetAttributeValue<string>("uniquename");
        if (string.IsNullOrEmpty(name))
        {
            await using var read = s.Command($"""
                SELECT uniquename FROM {s.DataTable(AppModuleSchema.SettingDefinition)} WHERE {s.Quote(AppModuleSchema.SettingDefinition + "id")} = @_definition
                """);
            read.AddParameter("@_definition", DbType.Guid, definitionId);
            name = await read.ExecuteScalarAsync(ct) as string ?? definitionId.ToString();
        }
        throw new XrmdFaultException(ErrorCodes.DuplicateRecord, $"The setting {name} already has an environment value.", 400);
    }
}
