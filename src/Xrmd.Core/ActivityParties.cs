using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// The <c>activityparty</c> rows of an activity (plan 2.11, rulings A8, A9, A10): the party list columns (<c>to</c>, <c>from</c>, …)
/// have no SQL column, each party is a row of <c>data.activityparty</c> with the column's participation type. The Core writes them
/// in the session of the activity's Create and Update, keeps one Owner row (9) and, with a regarding record, one Regarding row (8),
/// deletes them with the activity and marks them <c>ispartydeleted</c> when the party record is deleted. Rows are not audited
/// (A21).
/// </summary>
public sealed class ActivityParties(IMetadataStore store)
{
    private const string IdParameter = "@_id";
    private static string Table(ISqlDialect d) => d.DataTable(ActivitySchema.Party);

    /// <summary>
    /// Removes the party list values from <paramref name="target"/> and returns them by column (a null value becomes an empty
    /// collection: both clear the column's rows on Update). Nothing for a table without party lists.
    /// </summary>
    public static IReadOnlyDictionary<string, EntityCollection> ExtractPartyLists(EntityMetadata activity, Entity target)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(target);
        var lists = new Dictionary<string, EntityCollection>(StringComparer.Ordinal);
        foreach (var (attribute, _, _) in ActivitySchema.PartyLists(activity))
        {
            if (!target.Attributes.TryGetValue(attribute, out var value)) continue;
            lists[attribute] = value as EntityCollection ?? new EntityCollection();
            target.Attributes.Remove(attribute);
        }
        return lists;
    }

    /// <summary>
    /// Writes the parties of <paramref name="lists"/> (validated by the <c>EntityValidator</c>) for the activity
    /// <paramref name="id"/>: on Update a column sent replaces the rows of its participation type only, the other types stay (RF4);
    /// an empty collection clears the type. Each <c>partyid</c> must exist (404 otherwise, read with HOLDLOCK); an empty
    /// <c>addressused</c> is filled from the party's address (<c>emailaddress1</c>, or <c>internalemailaddress</c> of a user).
    /// The rows carry the activity's owner columns. No privilege on the party record is needed (A9, checklist 3d).
    /// </summary>
    /// <remarks>
    /// Lock order (final review I2): every party record of every list is read and locked (HOLDLOCK) first, and only then are the
    /// <c>activityparty</c> rows deleted and inserted. That is the order of the Delete of a party record, which locks the record
    /// and then marks its party rows (<see cref="MarkPartyDeletedAsync"/>), so the two never wait on each other in a cycle. The
    /// activity row itself is locked before either (the Update locks it first, the Create inserts it first).
    /// </remarks>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) for a missing party record.</exception>
    public async Task WriteAsync(DataSession s, EntityMetadata activity, Guid id, IReadOnlyDictionary<string, EntityCollection> lists,
        bool isCreate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(lists);
        if (lists.Count == 0) return;
        var columns = ActivitySchema.PartyLists(activity).ToDictionary(p => p.Attribute, p => p.Type, StringComparer.Ordinal);

        // 1. The party records, read and locked before any party row is touched (see the remarks).
        var rows = new List<(int Type, List<(Guid? Party, int? PartyType, string? Address)> Items)>();
        foreach (var (attribute, collection) in lists)
        {
            var items = new List<(Guid?, int?, string?)>();
            foreach (var item in collection.Entities)
            {
                var party = item.GetAttributeValue<EntityReference>(ActivitySchema.PartyId);
                var address = item.GetAttributeValue<string>(ActivitySchema.AddressUsed);
                int? partyType = null;
                if (party is not null)
                {
                    var table = store.GetEntity(party.LogicalName)
                                ?? throw XrmdFaultException.InvalidArgument($"A party names table '{party.LogicalName}', which does not exist.");
                    partyType = table.ObjectTypeCode;
                    var known = await PartyAddressAsync(s, table, party.Id, ct);
                    if (string.IsNullOrEmpty(address)) address = known;
                }
                items.Add((party?.Id, partyType, string.IsNullOrEmpty(address) ? null : address));
            }
            rows.Add(((int)columns[attribute], items));
        }

        // 2. The party rows of each list sent.
        foreach (var (type, items) in rows)
        {
            if (!isCreate)
            {
                await using var delete = s.Command($"DELETE FROM {s.DataTable(ActivitySchema.Party)} WHERE {s.Quote(ActivitySchema.ActivityId)} = {IdParameter} AND {s.Quote(ActivitySchema.ParticipationTypeMask)} = @_type");
                delete.AddParameter(IdParameter, DbType.Guid, id);
                delete.AddParameter("@_type", DbType.Int32, type);
                await delete.ExecuteNonQueryAsync(ct);
            }
            foreach (var (party, partyType, address) in items)
                await InsertAsync(s, activity, id, type, party, partyType, address, ct);
        }
    }

    /// <summary>
    /// The Owner row (9: <c>partyid</c> = the owner, user or team) and the Regarding row (8: the regarding record, when there is
    /// one) of the activity <paramref name="id"/>, read from its <c>activitypointer</c> row (written first); a row that still names
    /// the right record is kept. Every party row of the activity gets the owner columns of the activity (the read-only
    /// <c>ownerid</c> copy, A10). These rows may name tables outside <c>partyid</c>'s targets: the type column holds them.
    /// </summary>
    public static async Task SyncSystemPartiesAsync(DataSession s, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var d = s.Dialect;
        // Plan 2.7a, Task 6: the T-SQL batch became a read of the pointer row and statements of their own, with its values as
        // parameters (same statements, same order, on the caller's session).
        Guid? owner, reg, ou, ot, obu;
        int? ownerType, regType, activityType;
        await using (var read = s.Command($"""
            SELECT ownerid, owneridtype, regardingobjectid, regardingobjectidtype, owninguser, owningteam, owningbusinessunit, activitytypecode
            FROM {s.DataTable(ActivitySchema.Pointer)} WHERE activityid = {IdParameter}
            """))
        {
            read.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await read.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return;
            Guid? G(int i) => rd.IsDBNull(i) ? null : rd.GetGuid(i);
            int? I(int i) => rd.IsDBNull(i) ? null : rd.GetInt32(i);
            (owner, ownerType, reg, regType, ou, ot, obu, activityType) = (G(0), I(1), G(2), I(3), G(4), G(5), G(6), I(7));
        }

        async Task RunAsync(string sql)
        {
            await using var cmd = s.Command(sql);
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            cmd.AddParameter("@owner", DbType.Guid, (object?)owner ?? DBNull.Value);
            cmd.AddParameter("@ownertype", DbType.Int32, (object?)ownerType ?? DBNull.Value);
            cmd.AddParameter("@reg", DbType.Guid, (object?)reg ?? DBNull.Value);
            cmd.AddParameter("@regtype", DbType.Int32, (object?)regType ?? DBNull.Value);
            cmd.AddParameter("@ou", DbType.Guid, (object?)ou ?? DBNull.Value);
            cmd.AddParameter("@ot", DbType.Guid, (object?)ot ?? DBNull.Value);
            cmd.AddParameter("@obu", DbType.Guid, (object?)obu ?? DBNull.Value);
            cmd.AddParameter("@atype", DbType.Int32, (object?)activityType ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var table = Table(d);
        foreach (var (mask, party, partyType) in new[] { ("9", owner, "@ownertype"), ("8", reg, "@regtype") })
        {
            var partyParam = mask == "9" ? "@owner" : "@reg";
            if (party is null)
                await RunAsync($"DELETE FROM {d.DataTable(ActivitySchema.Party)} WHERE activityid = {IdParameter} AND participationtypemask = {mask}");
            else
            {
                await RunAsync($"""
                    DELETE FROM {d.DataTable(ActivitySchema.Party)} WHERE activityid = {IdParameter} AND participationtypemask = {mask}
                        AND (partyid IS NULL OR partyid <> {partyParam} OR {d.IsNull("partyidtype", "-1")} <> {d.IsNull(partyType, "-1")})
                    """);
                await RunAsync($"""
                    INSERT INTO {table} ({InsertColumns}) SELECT {InsertValues(d, mask, partyParam, partyType, "NULL")}
                    WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE activityid = {IdParameter} AND participationtypemask = {mask})
                    """);
            }
        }
        const string none = "'00000000-0000-0000-0000-000000000000'";
        await RunAsync($"""
            UPDATE {table} SET ownerid = @owner, owneridtype = @ownertype, owninguser = @ou, owningteam = @ot, owningbusinessunit = @obu
            WHERE activityid = {IdParameter} AND (ownerid IS NULL OR ownerid <> @owner OR {d.IsNull("owneridtype", "-1")} <> {d.IsNull("@ownertype", "-1")}
                OR {d.IsNull("owninguser", none)} <> {d.IsNull("@ou", none)}
                OR {d.IsNull("owningteam", none)} <> {d.IsNull("@ot", none)}
                OR {d.IsNull("owningbusinessunit", none)} <> {d.IsNull("@obu", none)})
            """);
    }

    /// <summary>Deletes every party row of the activity <paramref name="id"/> (A10: they go with the activity).</summary>
    public static async Task DeleteAsync(DataSession s, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"DELETE FROM {s.DataTable(ActivitySchema.Party)} WHERE {s.Quote(ActivitySchema.ActivityId)} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The party record <paramref name="id"/> of <paramref name="table"/> is deleted: its party rows stay, with <c>partyid</c>, and
    /// get <c>ispartydeleted</c> true (Learn; pre-flight C10: called by the Core's Delete of account, contact and team).
    /// </summary>
    public async Task MarkPartyDeletedAsync(DataSession s, string table, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (store.GetEntity(table)?.ObjectTypeCode is not { } code) return;
        await using var cmd = s.Command(
            $"UPDATE {Table(s.Dialect)} SET {s.Quote(ActivitySchema.IsPartyDeleted)} = {s.Dialect.BooleanLiteral(true)} WHERE {s.Quote(ActivitySchema.PartyId)} = {IdParameter} AND {s.Quote(ActivitySchema.PartyId + "type")} = @_otc");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        cmd.AddParameter("@_otc", DbType.Int32, code);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The party lists <paramref name="attributes"/> of the activities <paramref name="ids"/> of <paramref name="activity"/>: for each
    /// activity, each requested column of the table with its <c>activityparty</c> entities (an empty collection when it has none),
    /// each carrying <c>activitypartyid</c>, <c>activityid</c>, <c>partyid</c> (with the record's name), <c>participationtypemask</c>,
    /// <c>addressused</c> and <c>ispartydeleted</c>, in the order they were written. Names that are not party lists of the table are
    /// ignored.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, EntityCollection>>> ReadAsync(DataSession s, EntityMetadata activity,
        IReadOnlyCollection<Guid> ids, IReadOnlyCollection<string> attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(attributes);
        var columns = ActivitySchema.PartyLists(activity).Where(p => attributes.Contains(p.Attribute)).ToList();
        var result = new Dictionary<Guid, Dictionary<string, EntityCollection>>();
        foreach (var id in ids.Distinct())
            result[id] = columns.ToDictionary(c => c.Attribute, _ => new EntityCollection { EntityName = ActivitySchema.Party }, StringComparer.Ordinal);
        if (columns.Count == 0 || result.Count == 0) return Freeze(result);
        var byType = columns.ToDictionary(c => (int)c.Type, c => c.Attribute);

        var rows = new List<Entity>();
        // SQL Server takes at most 2100 parameters in one command.
        foreach (var chunk in result.Keys.Chunk(1000))
        {
            await using var cmd = s.Command($"""
                SELECT activitypartyid, activityid, partyid, partyidtype, participationtypemask, addressused, ispartydeleted
                FROM {Table(s.Dialect)}
                WHERE activityid IN ({string.Join(", ", chunk.Select((_, i) => $"@_a{i}"))})
                    AND participationtypemask IN ({string.Join(", ", byType.Keys)})
                ORDER BY activityid, participationtypemask, createdon, versionnumber
                """);
            for (var i = 0; i < chunk.Length; i++) cmd.AddParameter($"@_a{i}", DbType.Guid, chunk[i]);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var activityId = rd.GetGuid(1);
                var party = new Entity(ActivitySchema.Party, rd.GetGuid(0))
                {
                    [ActivitySchema.ActivityPartyId] = rd.GetGuid(0),
                    [ActivitySchema.ActivityId] = new EntityReference(activity.LogicalName, activityId),
                    [ActivitySchema.ParticipationTypeMask] = new OptionSetValue(rd.GetInt32(4)),
                    [ActivitySchema.IsPartyDeleted] = !rd.IsDBNull(6) && rd.GetBoolean(6),
                };
                if (!rd.IsDBNull(2) && !rd.IsDBNull(3) && store.GetEntityByObjectTypeCode(rd.GetInt32(3)) is { } partyTable)
                    party[ActivitySchema.PartyId] = new EntityReference(partyTable.LogicalName, rd.GetGuid(2));
                if (!rd.IsDBNull(5)) party[ActivitySchema.AddressUsed] = rd.GetString(5);
                result[activityId][byType[rd.GetInt32(4)]].Entities.Add(party);
                rows.Add(party);
            }
        }
        await NameAsync(s, rows, ct);
        return Freeze(result);
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, EntityCollection>> Freeze(
        Dictionary<Guid, Dictionary<string, EntityCollection>> result) =>
        result.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, EntityCollection>)kv.Value);

    /// <summary>Gives each <c>partyid</c> the primary name of its record (a deleted record keeps no name).</summary>
    private async Task NameAsync(DataSession s, List<Entity> rows, CancellationToken ct)
    {
        foreach (var group in rows.Select(r => r.GetAttributeValue<EntityReference>(ActivitySchema.PartyId)).OfType<EntityReference>()
                     .GroupBy(r => r.LogicalName))
        {
            if (store.GetEntity(group.Key) is not { PrimaryNameAttribute: { Length: > 0 } nameColumn } table) continue;
            var ids = group.Select(r => r.Id).Distinct().ToArray();
            var names = new Dictionary<Guid, string>();
            foreach (var chunk in ids.Chunk(1000))
            {
                await using var cmd = s.Command(
                    $"SELECT {s.Quote(SystemAttributes.PrimaryIdName(table))}, {s.Quote(nameColumn)} FROM {s.DataTable(table.LogicalName)} " +
                    $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(table))} IN ({string.Join(", ", chunk.Select((_, i) => $"@_p{i}"))})");
                for (var i = 0; i < chunk.Length; i++) cmd.AddParameter($"@_p{i}", DbType.Guid, chunk[i]);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    if (!rd.IsDBNull(1)) names[rd.GetGuid(0)] = rd.GetString(1);
            }
            foreach (var reference in group)
                if (names.TryGetValue(reference.Id, out var name)) reference.Name = name;
        }
    }

    /// <summary>
    /// The address of the party record, read with HOLDLOCK in the write's transaction: <c>emailaddress1</c>, or
    /// <c>internalemailaddress</c> of a <c>systemuser</c> (null when the table has no such column or it is empty).
    /// </summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the record does not exist.</exception>
    private static async Task<string?> PartyAddressAsync(DataSession s, EntityMetadata table, Guid id, CancellationToken ct)
    {
        var column = table.LogicalName == "systemuser" ? ActivitySchema.InternalEmailAddress : "emailaddress1";
        var hasColumn = table.Attributes?.Any(a => a.LogicalName == column) == true;
        await using var cmd = s.Command(
            $"SELECT {(hasColumn ? $"{s.Quote(column)}" : "NULL")} FROM {s.DataTable(table.LogicalName)}{s.Dialect.LockHint(RowLock.Shared)} " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(table))} = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(table.LogicalName, id);
        return rd.IsDBNull(0) ? null : rd.GetString(0);
    }

    private const string InsertColumns =
        "activitypartyid, activityid, activityidtype, partyid, partyidtype, participationtypemask, addressused, ispartydeleted, " +
        "ownerid, owneridtype, owninguser, owningteam, owningbusinessunit, createdon, modifiedon, statecode, statuscode";

    /// <summary>The values of <see cref="InsertColumns"/> in <see cref="SyncSystemPartiesAsync"/>, whose parameters hold the owner columns.</summary>
    private static string InsertValues(ISqlDialect d, string type, string party, string partyType, string address) =>
        $"{d.NewGuid}, {IdParameter}, @atype, {party}, {partyType}, {type}, {address}, {d.BooleanLiteral(false)}, @owner, @ownertype, @ou, @ot, @obu, " +
        $"{d.UtcNow}, {d.UtcNow}, 0, 1";

    /// <summary>Inserts one party row, with the owner columns of the stored activity row.</summary>
    private static async Task InsertAsync(DataSession s, EntityMetadata activity, Guid id, int type, Guid? party, int? partyType, string? address,
        CancellationToken ct)
    {
        await using var cmd = s.Command($"""
            INSERT INTO {Table(s.Dialect)} ({InsertColumns})
            SELECT {s.Dialect.NewGuid}, a.activityid, @_atype, @_party, @_partytype, @_type, @_address, {s.Dialect.BooleanLiteral(false)}, a.ownerid, a.owneridtype, a.owninguser,
                a.owningteam, a.owningbusinessunit, {s.Dialect.UtcNow}, {s.Dialect.UtcNow}, 0, 1
            FROM {s.DataTable(activity.LogicalName)} a WHERE a.activityid = {IdParameter}
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        cmd.AddParameter("@_atype", DbType.Int32, activity.ObjectTypeCode ?? 0);
        cmd.AddParameter("@_party", DbType.Guid, (object?)party ?? DBNull.Value);
        cmd.AddParameter("@_partytype", DbType.Int32, (object?)partyType ?? DBNull.Value);
        cmd.AddParameter("@_type", DbType.Int32, type);
        cmd.AddParameter("@_address", DbType.String, (object?)address ?? DBNull.Value, ActivitySchema.AddressUsedMaxLength);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
