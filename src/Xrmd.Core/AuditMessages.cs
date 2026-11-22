using System.Data.Common;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// The audit messages (plan 13, rulings A4, A7, A8), answered outside the plug-in pipeline by the <see cref="AuditReader"/>:
/// <list type="bullet">
/// <item>RetrieveAuditDetails (<c>AuditId</c>) → <c>AuditDetail</c>; requires <c>prvReadAuditSummary</c>; 404 for an unknown id.</item>
/// <item>RetrieveRecordChangeHistory (<c>Target</c>, <c>PagingInfo</c>) and RetrieveAttributeChangeHistory (<c>Target</c>,
/// <c>AttributeLogicalName</c>, <c>PagingInfo</c>) → <c>AuditDetailCollection</c>; require <c>prvReadRecordAuditHistory</c> and
/// <c>prvReadAuditSummary</c>.</item>
/// <item>DeleteRecordChangeHistory (<c>Target</c>) → <c>DeletedEntriesCount</c>; requires <c>prvDeleteRecordChangeHistory</c>.</item>
/// <item>DeleteAuditData (<c>EndDate</c>, UTC when unspecified) → <c>PartitionsDeleted</c>, the closed calendar quarters deleted;
/// requires <c>prvDeleteAuditPartitions</c>. It deletes in batches but in a single transaction, which holds the locks of the
/// deleted rows until it commits.</item>
/// </list>
/// Field security (plan 14, ruling F11): the secured columns the caller cannot read (through its profiles or a field share on the
/// audited record) are left out of <c>OldValue</c>, <c>NewValue</c>, <c>DeletedAttributes</c> and the <c>changedata</c> of the
/// <c>AuditRecord</c>; RetrieveAttributeChangeHistory of such a column is refused (403 FieldReadDenied). The rows stay stored in full.
/// Access to the target record is not checked: the record may have been deleted, its history stays. A missing or mistyped
/// parameter is 400. Without a session (the organization service's callers) a read runs on a connection of its own and a delete
/// in a transaction of its own; a plug-in's call runs in the plug-in's session.
/// </summary>
public sealed class AuditMessages(AuditReader reader, SecurityGuard guard, IDbConnectionFactory factory, TimeProvider time, FieldSecurity fields)
{
    private const string RetrieveAuditDetails = "RetrieveAuditDetails";
    private const string RetrieveRecordChangeHistory = "RetrieveRecordChangeHistory";
    private const string RetrieveAttributeChangeHistory = "RetrieveAttributeChangeHistory";
    private const string DeleteRecordChangeHistory = "DeleteRecordChangeHistory";
    private const string DeleteAuditData = "DeleteAuditData";

    /// <summary>The five messages.</summary>
    public static IReadOnlyList<string> Messages { get; } =
        [RetrieveAuditDetails, RetrieveRecordChangeHistory, RetrieveAttributeChangeHistory, DeleteRecordChangeHistory, DeleteAuditData];

    /// <summary>True for one of <see cref="Messages"/> (ignoring case).</summary>
    public static bool IsAuditMessage(string? name) => name is not null && Messages.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs <paramref name="request"/> for <paramref name="caller"/>, in <paramref name="session"/> when given.</summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied; 400 for a missing or invalid parameter; 404 for an unknown audit id.</exception>
    /// <exception cref="NotSupportedException">Not an audit message.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, DataSession? session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        var name = Messages.FirstOrDefault(m => string.Equals(m, request.RequestName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new NotSupportedException($"Request '{request.RequestName}' is not an audit message.");
        switch (name)
        {
            case RetrieveAuditDetails:
            {
                var auditId = Required<Guid>(request, "AuditId");
                await DemandAsync(caller, ct, PrivilegeNames.ReadAuditSummary);
                var detail = await ReadAsync(session, async (s, token) =>
                {
                    var read = await reader.DetailAsync(s, auditId, token);
                    await HideUnreadableAsync(caller, [read], s, token);
                    return read;
                }, ct);
                return new RetrieveAuditDetailsResponse { ResponseName = name, Results = { ["AuditDetail"] = detail } };
            }
            case RetrieveRecordChangeHistory or RetrieveAttributeChangeHistory:
            {
                var target = Target(request);
                var attribute = name == RetrieveAttributeChangeHistory ? AttributeName(request) : null;
                var paging = Optional<PagingInfo>(request, "PagingInfo");
                await DemandAsync(caller, ct, PrivilegeNames.ReadRecordAuditHistory, PrivilegeNames.ReadAuditSummary);
                var collection = await ReadAsync(session, async (s, token) =>
                {
                    if (attribute is not null) await DemandFieldReadAsync(caller, target, attribute, s, token);
                    var read = await reader.HistoryAsync(s, target, attribute, paging, token);
                    await HideUnreadableAsync(caller, read.AuditDetails, s, token);
                    return read;
                }, ct);
                OrganizationResponse response = name == RetrieveRecordChangeHistory
                    ? new RetrieveRecordChangeHistoryResponse()
                    : new RetrieveAttributeChangeHistoryResponse();
                response.ResponseName = name;
                response.Results["AuditDetailCollection"] = collection;
                return response;
            }
            case DeleteRecordChangeHistory:
            {
                var target = Target(request);
                await DemandAsync(caller, ct, PrivilegeNames.DeleteRecordChangeHistory);
                var count = await WriteAsync(session, (s, token) => reader.DeleteRecordAsync(s, target, token), ct);
                return new DeleteRecordChangeHistoryResponse { ResponseName = name, Results = { ["DeletedEntriesCount"] = count } };
            }
            default: // DeleteAuditData
            {
                var endDate = Required<DateTime>(request, "EndDate");
                var endUtc = endDate.Kind == DateTimeKind.Local ? endDate.ToUniversalTime() : DateTime.SpecifyKind(endDate, DateTimeKind.Utc);
                await DemandAsync(caller, ct, PrivilegeNames.DeleteAuditPartitions);
                var now = time.GetUtcNow().UtcDateTime;
                var partitions = await WriteAsync(session, (s, token) => reader.DeleteUntilAsync(s, endUtc, now, token), ct);
                return new DeleteAuditDataResponse { ResponseName = name, Results = { ["PartitionsDeleted"] = partitions } };
            }
        }
    }

    // ---- field security (ruling F11) ---------------------------------------------------------------------------------

    /// <summary>The attribute history of a secured column needs read on it: through the caller's profiles or a field share on the record.</summary>
    /// <exception cref="XrmdFaultException">403 FieldReadDenied.</exception>
    private async Task DemandFieldReadAsync(Caller caller, EntityReference target, string attribute, DataSession s, CancellationToken ct)
    {
        var set = await fields.ForAsync(caller, target.LogicalName, ct);
        if (set.CanRead(attribute)) return;
        var shares = await fields.SharesAsync(caller, target.LogicalName, [target.Id], [attribute], s, ct);
        if (shares.TryGetValue(target.Id, out var grant) && grant.Read.Contains(attribute)) return;
        throw XrmdFaultException.FieldReadDenied(target.LogicalName, attribute, caller.UserId);
    }

    /// <summary>
    /// Leaves out of each attribute detail the secured columns of its table the caller cannot read on its record
    /// (<see cref="AuditFieldSecurity"/>).
    /// </summary>
    private async Task HideUnreadableAsync(Caller caller, IEnumerable<AuditDetail> details, DataSession s, CancellationToken ct)
    {
        var attributes = details.OfType<AttributeAuditDetail>().ToList();
        if (attributes.Count == 0) return;
        var hidden = await AuditFieldSecurity.HiddenAsync(fields, caller, attributes.Select(d => (TableOf(d), RecordOf(d))), s, ct);
        foreach (var detail in attributes) AuditFieldSecurity.Hide(detail, hidden(TableOf(detail), RecordOf(detail)));

        static string TableOf(AttributeAuditDetail d) => d.AuditRecord.GetAttributeValue<string>(AuditTable.ObjectTypeCodeColumn) ?? "";
        static Guid RecordOf(AttributeAuditDetail d) => d.AuditRecord.GetAttributeValue<EntityReference>(AuditTable.ObjectId)?.Id ?? Guid.Empty;
    }

    private async Task DemandAsync(Caller caller, CancellationToken ct, params string[] privileges)
    {
        foreach (var privilege in privileges) await guard.DemandPrivilegeAsync(caller, privilege, AuditTable.Name, ct);
    }

    private async Task<T> ReadAsync<T>(DataSession? session, Func<DataSession, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        if (session is not null) return await read(session, ct);
        await using var conn = await factory.OpenAsync(ct);
        return await read(new DataSession(conn, null), ct);
    }

    private async Task<T> WriteAsync<T>(DataSession? session, Func<DataSession, CancellationToken, Task<T>> write, CancellationToken ct)
    {
        if (session is not null) return await write(session, ct);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var own = new DataSession(conn, tx);
        try
        {
            var result = await write(own, ct);
            // CancellationToken.None: once everything is deleted, a cancelled caller must not leave the commit half done.
            await tx.CommitAsync(CancellationToken.None);
            own.Committed();
            return result;
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // Already rolled back by the server: nothing is committed.
            }
            own.RolledBack();
            throw;
        }
    }

    // ---- parameters -------------------------------------------------------------------------------------------------

    private static EntityReference Target(OrganizationRequest request)
    {
        var target = Required<EntityReference>(request, "Target");
        if (string.IsNullOrEmpty(target.LogicalName) || target.Id == Guid.Empty)
            throw XrmdFaultException.InvalidArgument(
                $"The parameter 'Target' of request '{request.RequestName}' must name a table and a record id.");
        return target;
    }

    private static string AttributeName(OrganizationRequest request)
    {
        var name = Required<string>(request, "AttributeLogicalName");
        return name.Length > 0 ? name : throw Missing(request, "AttributeLogicalName");
    }

    private static T Required<T>(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is T typed ? typed : throw Missing(request, name);

    private static T? Optional<T>(OrganizationRequest request, string name) where T : class =>
        !request.Parameters.TryGetValue(name, out var value) || value is null
            ? null
            : value as T ?? throw XrmdFaultException.InvalidArgument(
                $"The parameter '{name}' of request '{request.RequestName}' must be a {typeof(T).Name}.");

    private static XrmdFaultException Missing(OrganizationRequest request, string name) =>
        XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or of the wrong type.");
}
