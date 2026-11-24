using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Security;

/// <summary>
/// The rules of access team templates and of the teams the system manages for them (plan 2.16, rulings A2 and A4). The
/// limits are those of <see cref="AccessTeamSchema"/>, which the metadata store also uses.
/// </summary>
/// <remarks>
/// A template's table is checked twice: by <see cref="SystemTableRules"/> on the parameters the caller sent (before stage 10,
/// outside the transaction), and by the core operation at stage 30, inside the request's transaction, on the values it
/// writes (what pre-operation plug-ins left). Only the second takes the lock that settles the race with disabling the table
/// (<see cref="DemandTemplateAsync"/>).
/// </remarks>
public static class AccessTeamRules
{
    /// <summary>The most templates a table can have (<see cref="AccessTeamSchema.MaxTemplatesPerTable"/>).</summary>
    public const int MaxTemplatesPerTable = AccessTeamSchema.MaxTemplatesPerTable;

    /// <summary>The most tables with <c>AutoCreateAccessTeams</c> (<see cref="AccessTeamSchema.MaxTables"/>).</summary>
    public const int MaxTables = AccessTeamSchema.MaxTables;

    /// <summary>The rights a template can grant: Read, Write, Append, AppendTo, Delete, Share and Assign (not Create).</summary>
    public const int TemplateRights = (int)(AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.AppendAccess
        | AccessRights.AppendToAccess | AccessRights.DeleteAccess | AccessRights.ShareAccess | AccessRights.AssignAccess);

    /// <summary>The team columns only the record team messages write (with <c>teamtype</c>, which no Update changes).</summary>
    public static readonly string[] SystemManagedColumns =
        [AccessTeamSchema.TeamTemplateId, AccessTeamSchema.RegardingObjectId, AccessTeamSchema.SystemManaged];

    /// <summary>True for a non-empty subset of <see cref="TemplateRights"/>.</summary>
    public static bool MaskIsValid(int mask) => mask != 0 && (mask & ~TemplateRights) == 0;

    /// <summary>The refusal of a membership change of a system-managed team (ruling A4).</summary>
    public static XrmdFaultException UseRecordTeamMessages(Guid teamId) =>
        XrmdFaultException.InvalidArgument(
            $"The team {teamId} is an access team managed by the system for a record: use AddUserToRecordTeam/RemoveUserFromRecordTeam to change its members.");

    /// <summary>
    /// The A2 rules of a template written by Create (<paramref name="isCreate"/>) or Update (<paramref name="id"/>):
    /// <list type="bullet">
    /// <item><c>defaultaccessrightsmask</c>: needed on Create; when given, a non-empty subset of <see cref="TemplateRights"/>.</item>
    /// <item>Create: <c>objecttypecode</c> names a table whose stored definition has <c>AutoCreateAccessTeams</c>, read from
    /// <c>meta.entity</c> after the template rows of that table are locked (UPDLOCK, HOLDLOCK: the lock the metadata store
    /// takes to disable the flag), and the table has fewer than <see cref="MaxTemplatesPerTable"/> templates under that lock.
    /// Inside a transaction a template therefore never lands on a table being disabled: whichever takes the lock first wins,
    /// and the other sees its result.</item>
    /// <item>Update: <c>objecttypecode</c>, when given, is the stored one.</item>
    /// </list>
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the column.</exception>
    public static async Task DemandTemplateAsync(DataSession session, IMetadataStore store, Entity target, bool isCreate, Guid id,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        var maskGiven = target.Attributes.TryGetValue(AccessTeamSchema.DefaultAccessRightsMask, out var mask);
        if ((isCreate || maskGiven) && !(mask is int value && MaskIsValid(value)))
            throw XrmdFaultException.InvalidArgument(
                $"The {AccessTeamSchema.DefaultAccessRightsMask} of an access team template must combine some of Read (1), Write (2), Append (4), " +
                $"AppendTo (16), Delete (65536), Share (262144) and Assign (524288), and nothing else; got {mask ?? "no value"}.");

        var codeGiven = target.Attributes.TryGetValue(AccessTeamSchema.ObjectTypeCode, out var codeValue);
        if (isCreate)
        {
            if (codeValue is not int code)
                throw XrmdFaultException.InvalidArgument(
                    $"An access team template needs {AccessTeamSchema.ObjectTypeCode}, the object type code of its table.");
            await DemandTemplateTableAsync(session, store, code, ct);
        }
        else if (codeGiven && id != Guid.Empty && await StoredTemplateTableAsync(session, id, ct) is { } stored
                 && !(codeValue is int changed && changed == stored))
        {
            throw XrmdFaultException.InvalidArgument(
                $"The {AccessTeamSchema.ObjectTypeCode} of an access team template cannot change (it is {stored}); create another template instead.");
        }
    }

    /// <summary>The Create part of <see cref="DemandTemplateAsync"/>: the lock, the flag of the stored definition, the count.</summary>
    private static async Task DemandTemplateTableAsync(DataSession session, IMetadataStore store, int code, CancellationToken ct)
    {
        var table = store.GetEntityByObjectTypeCode(code)?.LogicalName;
        await session.KeyLockAsync(AccessTeamSchema.TeamTemplate, code, ct);
        await using var cmd = session.Command($"""
            SELECT COUNT(*) FROM {session.DataTable(AccessTeamSchema.TeamTemplate)}{session.Dialect.LockHint(RowLock.UpdateRange)}
                WHERE {session.Quote(AccessTeamSchema.ObjectTypeCode)} = @_code;
            SELECT {session.Dialect.JsonValue("definition", "$.AutoCreateAccessTeams")} FROM meta.entity WHERE logical_name = @_table;
            """);
        cmd.AddParameter("@_code", DbType.Int32, code);
        cmd.AddParameter("@_table", DbType.String, (object?)table ?? DBNull.Value, 128);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        var count = rd.GetInt32(0);
        await rd.NextResultAsync(ct);
        var enabled = await rd.ReadAsync(ct) && !rd.IsDBNull(0) && rd.GetString(0) == "true";
        if (!enabled)
            throw XrmdFaultException.InvalidArgument(table is null
                ? $"The {AccessTeamSchema.ObjectTypeCode} {code} of the access team template names no table."
                : $"An access team template needs a table with AutoCreateAccessTeams: '{table}' does not have it.");
        if (count >= MaxTemplatesPerTable)
            throw XrmdFaultException.InvalidArgument(
                $"The table '{table}' already has {count} access team templates, which is the most a table can have ({MaxTemplatesPerTable}).");
    }

    /// <summary>The stored <c>objecttypecode</c> of a template (read with UPDLOCK); null when it does not exist.</summary>
    private static async Task<int?> StoredTemplateTableAsync(DataSession session, Guid id, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT {session.Quote(AccessTeamSchema.ObjectTypeCode)} FROM {session.DataTable(AccessTeamSchema.TeamTemplate)}{session.Dialect.LockHint(RowLock.Update)} " +
            $"WHERE {session.Quote(AccessTeamSchema.TeamTemplateId)} = @_id{session.Dialect.LockClause(RowLock.Update)}");
        cmd.AddParameter("@_id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as int?;
    }

    // ---- AddUserToRecordTeam / RemoveUserFromRecordTeam (rulings A6, A7) ---------------------------------------------

    /// <summary>The message that adds a user to a record's access team.</summary>
    public const string AddUserToRecordTeam = "AddUserToRecordTeam";

    /// <summary>The message that removes a user from a record's access team.</summary>
    public const string RemoveUserFromRecordTeam = "RemoveUserFromRecordTeam";

    /// <summary>True for AddUserToRecordTeam and RemoveUserFromRecordTeam (case-insensitive).</summary>
    public static bool IsRecordTeamMessage(string? name) =>
        string.Equals(name, AddUserToRecordTeam, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, RemoveUserFromRecordTeam, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The parameters of a record team message: <c>Record</c> (EntityReference), <c>SystemUserId</c> and <c>TeamTemplateId</c> (Guid).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when one is missing or of another type.</exception>
    public static (EntityReference Record, Guid UserId, Guid TemplateId) RecordTeamParameters(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (Parameter<EntityReference>("Record"), Parameter<Guid>("SystemUserId"), Parameter<Guid>("TeamTemplateId"));

        T Parameter<T>(string name) =>
            request.Parameters.TryGetValue(name, out var value) && value is T typed
                ? typed
                : throw XrmdFaultException.InvalidArgument(
                    $"Required parameter '{name}' of request '{request.RequestName}' is missing or not a {typeof(T).Name}.");
    }

    /// <summary>
    /// A record team message's parameters once checked (<see cref="DemandRecordTeamAsync"/>): the record's table, the template's
    /// mask now and the record's owning business unit (the business unit of a team created for it). In what the
    /// <c>SecurityGuard</c> records (<see cref="MarkChecked"/>), <see cref="Mask"/> is every right it checked: the template's mask,
    /// with, for AddUserToRecordTeam, the rights of the existing team's share (<see cref="TeamShareMaskAsync"/>).
    /// </summary>
    public sealed record RecordTeamTarget(EntityMetadata Table, Guid RecordId, Guid UserId, Guid TemplateId, AccessRights Mask,
        Guid? OwningBusinessUnit);

    /// <summary>
    /// Steps 1–5 of ruling A6, in this order, for both record team messages and every caller: the template exists (404); the
    /// record is of the template's table (400); that table has <c>AutoCreateAccessTeams</c> (400); the record exists (404); the
    /// user exists (404) and is enabled (400). The caller's rights (step 6) and the added user's read (step 7) are the
    /// <c>SecurityGuard</c>'s.
    /// </summary>
    /// <param name="lockRows">In the request's transaction (the core): the template row is read WITH (XLOCK, ROWLOCK, HOLDLOCK)
    /// and the record WITH (UPDLOCK, ROWLOCK, HOLDLOCK), both until commit, so neither can be deleted, nor the mask changed,
    /// while the team is found or created. The template is locked first and exclusively: a template Delete's first lock on the
    /// template is a shared one (its existence check), taken before it scans the teams, so the two queue up on the template row
    /// instead of deadlocking on the team rows. A record Delete takes the record's update lock before its access teams
    /// (<c>CoreOperations.DeleteAccessTeamsAsync</c>), the order used here.</param>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist or 400 InvalidArgument for the first check that fails.</exception>
    public static async Task<RecordTeamTarget> DemandRecordTeamAsync(DataSession session, IMetadataStore store, EntityReference record,
        Guid userId, Guid templateId, bool lockRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);
        var templateHint = lockRows ? $"{session.Dialect.LockHint(RowLock.Exclusive)}" : "";
        var recordHint = lockRows ? $"{session.Dialect.LockHint(RowLock.UpdateRowRange)}" : "";
        var templateClause = lockRows ? session.Dialect.LockClause(RowLock.Exclusive) : "";
        var recordClause = lockRows ? session.Dialect.LockClause(RowLock.UpdateRowRange) : "";

        int code, mask;
        await using (var cmd = session.Command(
                         $"SELECT {session.Quote(AccessTeamSchema.ObjectTypeCode)}, {session.Quote(AccessTeamSchema.DefaultAccessRightsMask)} " +
                         $"FROM {session.DataTable(AccessTeamSchema.TeamTemplate)}{templateHint} WHERE {session.Quote(AccessTeamSchema.TeamTemplateId)} = @_id{templateClause}"))
        {
            cmd.AddParameter("@_id", DbType.Guid, templateId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(AccessTeamSchema.TeamTemplate, templateId);
            code = rd.IsDBNull(0) ? 0 : rd.GetInt32(0);
            mask = rd.IsDBNull(1) ? 0 : rd.GetInt32(1);
        }

        var table = store.GetEntity(record.LogicalName ?? "");
        if (table?.ObjectTypeCode != code)
            throw XrmdFaultException.InvalidArgument(
                $"The record is a '{record.LogicalName}', but the access team template {templateId} is for the table " +
                $"'{store.GetEntityByObjectTypeCode(code)?.LogicalName ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture)}'.");
        if (!store.AccessTeamTables.Contains(table.LogicalName))
            throw XrmdFaultException.InvalidArgument($"The table '{table.LogicalName}' does not have AutoCreateAccessTeams.");

        Guid? businessUnit;
        await using (var cmd = session.Command(
                         $"SELECT {session.Quote("owningbusinessunit")} FROM {session.DataTable(table.LogicalName)}{recordHint} " +
                         $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(table))} = @_id{recordClause}"))
        {
            cmd.AddParameter("@_id", DbType.Guid, record.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(table.LogicalName, record.Id);
            businessUnit = rd.IsDBNull(0) ? null : rd.GetGuid(0);
        }

        await using (var cmd = session.Command(
                         $"SELECT {session.Dialect.IsNull(session.Quote("isdisabled"), session.Dialect.BooleanLiteral(false))} FROM {session.DataTable("systemuser")} WHERE {session.Quote("systemuserid")} = @_id"))
        {
            cmd.AddParameter("@_id", DbType.Guid, userId);
            switch (await cmd.ExecuteScalarAsync(ct))
            {
                case null or DBNull:
                    throw XrmdFaultException.ObjectDoesNotExist("systemuser", userId);
                case true:
                    throw XrmdFaultException.InvalidArgument($"The user {userId} is disabled and cannot be a member of an access team.");
            }
        }

        return new RecordTeamTarget(table, record.Id, userId, templateId, (AccessRights)mask, businessUnit);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OrganizationRequest, RecordTeamTarget> Checked = new();

    /// <summary>Records what the <c>SecurityGuard</c> checked (steps 1–7) for <paramref name="request"/>, for <see cref="DemandSameAsChecked"/>.</summary>
    public static void MarkChecked(OrganizationRequest request, RecordTeamTarget target)
    {
        ArgumentNullException.ThrowIfNull(request);
        Checked.AddOrUpdate(request, target);
    }

    /// <summary>
    /// The core's side of the caller and added-user checks (steps 6 and 7), which the <c>SecurityGuard</c> made before stage 10 on
    /// the parameters the caller sent and the template's mask it read then: what the core is about to write (the final
    /// parameters, after pre-operation plug-ins, and the mask read under the template's lock) must be what was checked, with no
    /// right the check did not cover. Otherwise the request fails explicitly instead of writing unchecked rights. No check for
    /// <see cref="Caller.Internal"/>, which the guard does not check either.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when the record, user or template differs from the checked
    /// one, or the template's mask gained a right since the check; 500 when no check was recorded.</exception>
    public static void DemandSameAsChecked(OrganizationRequest request, Caller caller, RecordTeamTarget target)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        if (caller == Caller.Internal) return;
        if (!Checked.TryGetValue(request, out var check))
            throw new XrmdFaultException(ErrorCodes.Unexpected,
                $"{request.RequestName} reached its core operation without the security check of its caller.", 500);
        if (check.Table.LogicalName != target.Table.LogicalName || check.RecordId != target.RecordId || check.UserId != target.UserId
            || check.TemplateId != target.TemplateId)
            throw XrmdFaultException.InvalidArgument(
                $"The parameters of {request.RequestName} were changed after its security check (by a plug-in); they cannot be checked again.");
        if ((target.Mask & ~check.Mask) != 0)
            throw XrmdFaultException.InvalidArgument(
                $"The access team template {target.TemplateId} was changed while {request.RequestName} ran: it now grants {target.Mask}, " +
                $"but the caller's rights were checked for {check.Mask}. Retry the request.");
    }

    /// <summary>
    /// The rights of the share of the record held by its team for the template (direct and inherited masks), the rights a member
    /// added now receives; null when the record has no team for the template yet, <see cref="AccessRights.None"/> when the team
    /// has no share of the record. With <paramref name="lockRows"/> (the core, after it locked the team), the share row is read
    /// WITH (UPDLOCK, HOLDLOCK) until commit, so it cannot change before the member is added.
    /// </summary>
    public static async Task<AccessRights?> TeamShareMaskAsync(DataSession session, RecordTeamTarget target, bool lockRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        var hint = lockRows ? $"{session.Dialect.LockHint(RowLock.UpdateRange)}" : "";
        // The share rows sit on the nullable side of the outer join, which a trailing lock clause cannot reach: the key lock does.
        if (lockRows) await session.KeyLockAsync("principalobjectaccess", target.RecordId, ct);
        await using var cmd = session.Command($"""
            SELECT t.{session.Quote(SystemAttributes.PrimaryIdName(AccessTeamSchema.Team))},
                   {session.Dialect.IsNull($"poa.{session.Quote("accessrightsmask")}", "0")} | {session.Dialect.IsNull($"poa.{session.Quote("inheritedaccessrightsmask")}", "0")}
            FROM {session.DataTable(AccessTeamSchema.Team)} AS t
            LEFT JOIN {session.DataTable("principalobjectaccess")} AS poa{hint}
                ON poa.{session.Quote("principalid")} = t.{session.Quote(SystemAttributes.PrimaryIdName(AccessTeamSchema.Team))} AND poa.{session.Quote("objectid")} = @_record
                   AND poa.{session.Quote("objecttypecode")} = @_otc
            WHERE t.{session.Quote(AccessTeamSchema.RegardingObjectId)} = @_record AND t.{session.Quote(AccessTeamSchema.TeamTemplateId)} = @_template
            """);
        cmd.AddParameter("@_record", DbType.Guid, target.RecordId);
        cmd.AddParameter("@_template", DbType.Guid, target.TemplateId);
        cmd.AddParameter("@_otc", DbType.Int32, target.Table.ObjectTypeCode ?? 0);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var mask = 0;
        do mask |= rd.GetInt32(1);
        while (await rd.ReadAsync(ct));
        return (AccessRights)mask;
    }

    /// <summary>
    /// The core's side of step 6 for an existing team (AddUserToRecordTeam): the rights of the team's share, read under the
    /// core's locks (<paramref name="teamShare"/>), must all have been checked by the <c>SecurityGuard</c>. Otherwise the share
    /// was widened, or the team created, between the check and the write, and the request fails explicitly instead of handing
    /// out unchecked rights. No check for <see cref="Caller.Internal"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument (retry); 500 when no check was recorded.</exception>
    public static void DemandTeamShareChecked(OrganizationRequest request, Caller caller, RecordTeamTarget target, AccessRights teamShare)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        if (caller == Caller.Internal) return;
        if (!Checked.TryGetValue(request, out var check))
            throw new XrmdFaultException(ErrorCodes.Unexpected,
                $"{request.RequestName} reached its core operation without the security check of its caller.", 500);
        if ((teamShare & ~check.Mask) != 0)
            throw XrmdFaultException.InvalidArgument(
                $"The access team of the record for the template {target.TemplateId} changed while {request.RequestName} ran: its share " +
                $"now grants {teamShare}, but the caller's rights were checked for {check.Mask}. Retry the request.");
    }

    /// <summary>True when the team exists and is system-managed (<c>systemmanaged</c> = 1).</summary>
    public static async Task<bool> IsSystemManagedTeamAsync(DataSession session, Guid teamId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (teamId == Guid.Empty) return false;
        await using var cmd = session.Command(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable(AccessTeamSchema.Team)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(AccessTeamSchema.Team))} = @_id AND {session.Quote(AccessTeamSchema.SystemManaged)} = {session.Dialect.BooleanLiteral(true)}) THEN 1 ELSE 0 END");
        cmd.AddParameter("@_id", DbType.Guid, teamId);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    /// <summary>
    /// The stored values of the columns of <see cref="SystemManagedColumns"/> (read with UPDLOCK, HOLDLOCK): the two lookups as
    /// ids, <c>systemmanaged</c> with NULL as false; null when the team does not exist.
    /// </summary>
    public static async Task<(Guid? Template, Guid? Regarding, bool SystemManaged)?> StoredSystemManagedColumnsAsync(DataSession session,
        Guid teamId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var cmd = session.Command(
            $"SELECT {session.Quote(AccessTeamSchema.TeamTemplateId)}, {session.Quote(AccessTeamSchema.RegardingObjectId)}, {session.Dialect.IsNull(session.Quote(AccessTeamSchema.SystemManaged), session.Dialect.BooleanLiteral(false))} " +
            $"FROM {session.DataTable(AccessTeamSchema.Team)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(AccessTeamSchema.Team))} = @_id{session.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@_id", DbType.Guid, teamId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.IsDBNull(0) ? null : rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.GetBoolean(2));
    }
}
