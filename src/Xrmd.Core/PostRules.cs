using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Core;

/// <summary>
/// The server rules of the timeline posts (plan 3.5 UI-1g; spec §3.3-§3.4) on the row the Core writes, after the plug-ins:
/// <list type="bullet">
/// <item>Update of a <c>post</c> or <c>postcomment</c> is refused ("Posts and comments cannot be updated"); Create and Update of
/// <c>postregarding</c> are refused ("postregarding is maintained by the server"): its rows are written here, in SQL;</item>
/// <item>Create of a post with <c>source</c> 1 (Auto Post) or 3 (ActionHub Post) is refused;</item>
/// <item>Create of a post sets <c>regardingobjecttypecode</c> (the type column of its lookup), copies the regarding record's owner
/// columns, finds or creates the <c>postregarding</c> row of the record, and sets <c>postregardingid</c> and that row's
/// <c>latestmanualpostmodifiedon</c>;</item>
/// <item>Create of a post or comment fills <c>organizationid</c> and, under impersonation, <c>createdonbehalfby</c> (the
/// impersonating user, as for a delegate on Learn).</item>
/// </list>
/// The request-level refusals (before any privilege check) are <c>SystemTableRules</c>'; Delete of <c>postregarding</c> is refused
/// there only, since the Core deletes those rows with their record (<c>{Stem}_PostRegardings</c>, Delete Cascade). On the model since
/// S.2e3b.
/// </summary>
internal static class PostRules
{
    private const string IdParameter = "@_id";
    private const string Organization = "organization";
    private const string SystemUser = "systemuser";
    private const string Team = "team";
    private const string BusinessUnit = "businessunit";
    private const string OwnerColumn = "regardingobjectownerid", OwningUnitColumn = "regardingobjectowningbusinessunit";
    private const int ManualPost = 2, CheckIn = 1;

    /// <summary>The refusals of a Create or Update of <paramref name="m"/> with the values of <paramref name="row"/>.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    public static void DemandWritable(TableDefinition m, Record row, bool isCreate)
    {
        switch (m.LogicalName)
        {
            case PostSchema.Regarding:
                throw XrmdFaultException.InvalidArgument(PostSchema.ServerMaintained);
            case PostSchema.Post or PostSchema.Comment when !isCreate:
                throw XrmdFaultException.InvalidArgument(PostSchema.NotUpdatable);
            case PostSchema.Post when row.GetValue<ChoiceValue>(PostSchema.Source) is { } source && PostSchema.IsAutoSource(source.Value):
                throw XrmdFaultException.InvalidArgument(PostSchema.AutoPostsRefused);
        }
    }

    /// <summary>
    /// The columns the server sets on the Create of a post or comment (see the class), and the <c>postregarding</c> row of a post's
    /// record, in the Create's session. <paramref name="row"/> already holds <c>createdon</c>. No effect on another table.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>regardingobjectid</c> (post) or <c>postid</c> (comment); 404 when the
    /// regarding record does not exist.</exception>
    public static async Task ApplyCreateAsync(DataSession s, IMetadataStore store, Caller caller, TableDefinition m,
        IReadOnlyDictionary<string, ColumnDefinition> attrs, Record row, CancellationToken ct)
    {
        if (m.LogicalName is not (PostSchema.Post or PostSchema.Comment)) return;
        if (caller.ImpersonatorId is { } delegateId) SetIfDefined(row, attrs, "createdonbehalfby", Reference(SystemUser, delegateId));
        if (await OrganizationIdAsync(s, ct) is { } organization) SetIfDefined(row, attrs, "organizationid", Reference(Organization, organization));
        if (m.LogicalName == PostSchema.Comment)
        {
            if (row.GetValue<RecordReference>(PostSchema.PostId) is null)
                throw XrmdFaultException.InvalidArgument($"A {PostSchema.Comment} needs '{PostSchema.PostId}': the post it replies to.");
            return;
        }

        // xrmverse applies no column default on insert: the defaults of the choices (2 Manual Post, 1 Check-in) are written here.
        if (row.GetValue<ChoiceValue>(PostSchema.Source) is null) row[PostSchema.Source] = new ChoiceValue(ManualPost);
        if (row.GetValue<ChoiceValue>(PostSchema.Type) is null) row[PostSchema.Type] = new ChoiceValue(CheckIn);
        var regarding = row.GetValue<RecordReference>(ActivitySchema.RegardingObjectId)
                        ?? throw XrmdFaultException.InvalidArgument($"A {PostSchema.Post} needs '{ActivitySchema.RegardingObjectId}': the record it is about.");
        var target = store.GetTable(regarding.LogicalName ?? "")
                     ?? throw XrmdFaultException.InvalidArgument($"Attribute '{ActivitySchema.RegardingObjectId}' references entity '{regarding.LogicalName}', which does not exist.");
        var (owner, unit) = await RegardingOwnerAsync(s, target, regarding.Id, ct);
        if (owner is not null) row[OwnerColumn] = owner;
        if (unit is { } u) row[OwningUnitColumn] = Reference(BusinessUnit, u);
        var postedOn = row.GetValue<DateTime?>("createdon") ?? DateTime.UtcNow;
        var regardingRow = await KeepRegardingRowAsync(s, store, caller, target, regarding.Id, owner, unit, postedOn, ct);
        row[PostSchema.RegardingId] = Reference(PostSchema.Regarding, regardingRow);
    }

    /// <summary>The owner (user or team) and owning business unit of the regarding record; nulls for an organization-owned table.</summary>
    /// <exception cref="XrmdFaultException">404 when the record does not exist.</exception>
    private static async Task<(RecordReference? Owner, Guid? Unit)> RegardingOwnerAsync(DataSession s, TableDefinition target, Guid id,
        CancellationToken ct)
    {
        var known = (target.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        var columns = new[] { "owninguser", "owningteam", "owningbusinessunit" }.Where(known.Contains).ToList();
        await using var cmd = s.Command(
            $"SELECT 1{string.Concat(columns.Select(c => $", {s.Quote(c)}"))} FROM {s.DataTable(target.LogicalName!)}{s.Dialect.LockHint(RowLock.Shared)} " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(target))} = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(target.LogicalName!, id);
        Guid? Value(string column) => columns.IndexOf(column) is var i and >= 0 && !rd.IsDBNull(i + 1) ? rd.GetGuid(i + 1) : null;
        var owner = Value("owninguser") is { } user ? Reference(SystemUser, user)
            : Value("owningteam") is { } team ? Reference(Team, team)
            : null;
        return (owner, Value("owningbusinessunit"));
    }

    /// <summary>
    /// The <c>postregarding</c> row of the record (found under an update lock, else inserted), with the record's owner columns and
    /// <c>latestmanualpostmodifiedon</c> moved to <paramref name="postedOn"/> when later; returns its id.
    /// </summary>
    private static async Task<Guid> KeepRegardingRowAsync(DataSession s, IMetadataStore store, Caller caller, TableDefinition target,
        Guid recordId, RecordReference? owner, Guid? unit, DateTime postedOn, CancellationToken ct)
    {
        var table = store.GetTable(PostSchema.Regarding) ?? throw new InvalidOperationException($"The {PostSchema.Regarding} table has not been seeded.");
        var attrs = (table.Attributes ?? []).ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        var pk = SystemAttributes.PrimaryIdName(table);
        var typeColumn = ActivitySchema.RegardingObjectId + TypeMapping.TypeColumnSuffix;
        Guid? found;
        await s.KeyLockAsync(PostSchema.Regarding, recordId, ct);
        await using (var cmd = s.Command(s.Dialect.Limit(
                         $"SELECT {s.Quote(pk)} FROM {s.DataTable(PostSchema.Regarding)}{s.Dialect.LockHint(RowLock.UpdateRange)} " +
                         $"WHERE {s.Quote(ActivitySchema.RegardingObjectId)} = {IdParameter} AND {s.Quote(typeColumn)} = @_otc", "1") +
                         s.Dialect.LockClause(RowLock.UpdateRange)))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, recordId);
            cmd.AddParameter("@_otc", DbType.Int32, target.ObjectTypeCode ?? 0);
            found = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }

        var row = new Record(PostSchema.Regarding);
        row[OwnerColumn] = owner;
        row[OwningUnitColumn] = unit is { } u ? Reference(BusinessUnit, u) : null;
        if (found is { } existing)
        {
            var parameters = EntityRowMapper.ToParameters(row, attrs, store.GetTable);
            await using var cmd = s.Command(
                $"UPDATE {s.DataTable(PostSchema.Regarding)} SET " +
                string.Join(", ", parameters.Select(p => $"{s.Quote(p.ParameterName[1..])} = {p.ParameterName}")) +
                $", {s.Quote(PostSchema.LatestManualPostModifiedOn)} = CASE WHEN {s.Quote(PostSchema.LatestManualPostModifiedOn)} IS NULL " +
                $"OR {s.Quote(PostSchema.LatestManualPostModifiedOn)} < @_on THEN @_on ELSE {s.Quote(PostSchema.LatestManualPostModifiedOn)} END " +
                $"WHERE {s.Quote(pk)} = {IdParameter}");
            cmd.AddParameters(parameters);
            cmd.AddParameter("@_on", DbType.DateTime2, postedOn);
            cmd.AddParameter(IdParameter, DbType.Guid, existing);
            await cmd.ExecuteNonQueryAsync(ct);
            return existing;
        }

        var id = Guid.NewGuid();
        var by = Reference(SystemUser, caller.UserId);
        row[pk] = id;
        row[ActivitySchema.RegardingObjectId] = Reference(target.LogicalName!, recordId);
        row[PostSchema.LatestManualPostModifiedOn] = postedOn;
        SetIfDefined(row, attrs, "createdon", postedOn);
        SetIfDefined(row, attrs, "modifiedon", postedOn);
        SetIfDefined(row, attrs, "createdby", by);
        SetIfDefined(row, attrs, "modifiedby", by);
        SetIfDefined(row, attrs, "statecode", new ChoiceValue(0));
        SetIfDefined(row, attrs, "statuscode", new ChoiceValue(1));
        var inserted = EntityRowMapper.ToParameters(row, attrs, store.GetTable);
        await using (var cmd = s.Command(
                         $"INSERT INTO {s.DataTable(PostSchema.Regarding)} " +
                         $"({string.Join(", ", inserted.Select(p => s.Quote(p.ParameterName[1..])))}) " +
                         $"VALUES ({string.Join(", ", inserted.Select(p => p.ParameterName))})"))
        {
            cmd.AddParameters(inserted);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return id;
    }

    private static async Task<Guid?> OrganizationIdAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT organizationid FROM {s.DataTable(Organization)}", "1"));
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static void SetIfDefined(Record row, IReadOnlyDictionary<string, ColumnDefinition> attrs, string name, object value)
    {
        if (attrs.ContainsKey(name)) row[name] = value;
    }

    private static RecordReference Reference(string table, Guid id) => new() { LogicalName = table, Id = id };
}
