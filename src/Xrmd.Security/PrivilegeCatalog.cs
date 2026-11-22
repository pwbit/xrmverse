using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Security;

/// <summary>
/// The <c>privilege</c> / <c>privilegeobjecttypecodes</c> catalog: the privileges of every non-intersect entity plus the
/// ones not tied to an entity, with stable ids so that every restart and every instance derives the same rows. Idempotent. A
/// deleted entity takes its privileges with it, their grants to roles and the shares of its records.
/// </summary>
public sealed class PrivilegeCatalog(IDbConnectionFactory factory, IMetadataStore store) : IEntityCreatedHandler, IEntityDeletedHandler
{
    private static readonly AccessRights[] OrganizationRights =
    [
        AccessRights.CreateAccess, AccessRights.ReadAccess, AccessRights.WriteAccess, AccessRights.DeleteAccess,
        AccessRights.AppendAccess, AccessRights.AppendToAccess,
    ];

    /// <summary>The rights of the Post privileges (plan 3.5 UI-1g).</summary>
    private static readonly AccessRights[] PostRights = [AccessRights.CreateAccess, AccessRights.ReadAccess, AccessRights.DeleteAccess];

    /// <summary>Creates the missing privileges of every known entity and the ones not tied to an entity.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await UpsertAsync(new DataSession(conn, tx), store.GetAllEntities(), includeNonEntity: true, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Creates, in the creating session, the privileges of the new entity.</summary>
    public Task OnEntityCreatedAsync(EntityMetadata entity, DataSession session, CancellationToken ct) =>
        UpsertAsync(session, [entity], includeNonEntity: false, ct);

    /// <summary>
    /// Deletes, in the deleting session, the entity's privileges (those it would get now and those linked to it in
    /// <c>privilegeobjecttypecodes</c>), their <c>roleprivileges</c> and links, and the <c>principalobjectaccess</c> rows
    /// of its records.
    /// </summary>
    public async Task OnEntityDeletingAsync(EntityMetadata entity, DataSession session, CancellationToken ct)
    {
        if (store.GetEntity("privilege") is null || store.GetEntity("privilegeobjecttypecodes") is null) return;

        // Plan 2.7a, Task 5 fix round 1: the T-SQL batch (a table variable, IF … OBJECT_ID) became commands of their own.
        var d = session.Dialect;
        var ids = RowsOf(entity).Select(r => r.Id).ToHashSet();
        await using (var linked = session.Command("SELECT privilegeid FROM data.privilegeobjecttypecodes WHERE objecttypecode = @entity"))
        {
            linked.AddParameter("@entity", DbType.String, entity.LogicalName);
            await using var rd = await linked.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                if (!rd.IsDBNull(0)) ids.Add(rd.GetGuid(0));
        }

        var json = d.JsonRecords("@ids", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")]);
        await using (var cmd = session.Command($"""
            DELETE FROM data.roleprivileges WHERE privilegeid IN (SELECT j.id FROM {json} AS j);
            DELETE FROM data.privilegeobjecttypecodes WHERE privilegeid IN (SELECT j.id FROM {json} AS j) OR objecttypecode = @entity;
            DELETE FROM data.privilege WHERE privilegeid IN (SELECT j.id FROM {json} AS j);
            """))
        {
            cmd.AddParameter("@ids", DbType.String, JsonSerializer.Serialize(ids));
            cmd.AddParameter("@entity", DbType.String, entity.LogicalName);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (entity.ObjectTypeCode is { } otc && store.GetEntity("principalobjectaccess") is not null)
        {
            await using var shares = session.Command("DELETE FROM data.principalobjectaccess WHERE objecttypecode = @otc");
            shares.AddParameter("@otc", DbType.Int32, otc);
            await shares.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Stable id of the <c>privilegeobjecttypecodes</c> row of a privilege.</summary>
    private static Guid LinkId(string privilegeName) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("xrmd:privilegeobjecttypecode:" + privilegeName.ToLowerInvariant())));

    /// <summary>The names of the privileges of an entity (none for an intersect entity).</summary>
    internal static IEnumerable<string> NamesOf(EntityMetadata e) => RowsOf(e).Select(r => r.Name);

    private sealed record Row(Guid Id, string Name, int AccessRight, bool Basic, bool Local, bool Deep, bool Global, string? Entity, Guid? LinkId);

    private static IEnumerable<Row> RowsOf(EntityMetadata e)
    {
        // The audit table has no entity privileges: reading it is prvReadAuditSummary, and nothing writes it through a request.
        if (e.IsIntersect == true || e.LogicalName == AuditTable.Name) yield break;
        // Plan 2.11 A12 (pre-flight C5): the activity tables share the Activity privileges, owned (and linked) by the pointer only,
        // so that deleting a custom activity keeps them; activityparty has none (its rows are read through their activity).
        if (e.LogicalName == ActivitySchema.Party || ActivitySchema.IsActivityTable(e)) yield break;
        // Plan 3.5 UI-1g: postcomment and postregarding share the Post privileges, owned (and linked) by post, which has Create,
        // Read and Delete only (posts and comments are never updated; the names are from memory: acceptance checklist).
        if (e.LogicalName is PostSchema.Comment or PostSchema.Regarding) yield break;
        var post = e.LogicalName == PostSchema.Post;
        var businessOwned = e.OwnershipType == OwnershipTypes.BusinessOwned;
        var organizationOwned = e.OwnershipType is not (OwnershipTypes.BusinessOwned or OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned);
        foreach (var (right, verb) in PrivilegeNames.Verbs)
        {
            if ((businessOwned || organizationOwned) && !OrganizationRights.Contains(right)) continue;
            if (post && !PostRights.Contains(right)) continue;
            var name = "prv" + verb + PrivilegeNames.Stem(e);
            yield return new Row(PrivilegeNames.IdOf(name), name, (int)right,
                Basic: !businessOwned && !organizationOwned, Local: !organizationOwned, Deep: !organizationOwned, Global: true,
                e.LogicalName, LinkId(name));
        }
    }

    private async Task UpsertAsync(DataSession session, IEnumerable<EntityMetadata> entities, bool includeNonEntity, CancellationToken ct)
    {
        // During the first seed the catalog tables may not exist yet: EnsureAsync runs after the seed.
        if (store.GetEntity("privilege") is null || store.GetEntity("privilegeobjecttypecodes") is null) return;

        var rows = entities.SelectMany(RowsOf).ToList();
        if (includeNonEntity)
            rows.AddRange(PrivilegeNames.NonEntity.Select(n =>
                new Row(PrivilegeNames.IdOf(n), n, 0, false, false, false, true, null, null)));
        if (rows.Count == 0) return;

        var json = JsonSerializer.Serialize(rows.Select(r => new
        {
            id = r.Id, name = r.Name, right = r.AccessRight, basic = r.Basic, local = r.Local, deep = r.Deep, global = r.Global,
            entity = r.Entity, link = r.LinkId,
        }));

        // Plan 2.7a, Task 5 fix round 1: the T-SQL batch (DECLARE, a table variable, IF … RETURN) became commands of their own.
        // Mid-seed (no root business unit yet): the post-seed EnsureAsync inserts everything with the root business unit.
        if (await session.RootBusinessUnitAsync(ct) is not { } root) return;
        var (bu, now) = root;
        var d = session.Dialect;
        var rowsJson = d.JsonRecords("@json", [
            new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("name", SqlColumnKind.String, 100, "$.name"),
            new JsonColumn("accessright", SqlColumnKind.Int32, Path: "$.right"), new JsonColumn("canbebasic", SqlColumnKind.Boolean, Path: "$.basic"),
            new JsonColumn("canbelocal", SqlColumnKind.Boolean, Path: "$.local"), new JsonColumn("canbedeep", SqlColumnKind.Boolean, Path: "$.deep"),
            new JsonColumn("canbeglobal", SqlColumnKind.Boolean, Path: "$.global"), new JsonColumn("entity", SqlColumnKind.String, 64, "$.entity"),
            new JsonColumn("link", SqlColumnKind.Guid, Path: "$.link")]);

        // Checks then inserts of many rows: the key locks serialize the seeds where the hint does not lock the missing rows.
        await session.KeyLockAsync("privilege", "seed", ct);
        await ExecuteAsync($"""
            INSERT INTO data.privilege (privilegeid, name, accessright, canbebasic, canbelocal, canbedeep, canbeglobal,
                owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT r.id, r.name, r.accessright, r.canbebasic, r.canbelocal, r.canbedeep, r.canbeglobal, @bu, @now, @now, 0, 1
            FROM {rowsJson} AS r
            WHERE NOT EXISTS (SELECT 1 FROM data.privilege{d.LockHint(RowLock.UpdateRange)} WHERE privilegeid = r.id);
            """);
        await session.KeyLockAsync("privilegeobjecttypecodes", "seed", ct);
        await ExecuteAsync($"""
            INSERT INTO data.privilegeobjecttypecodes (privilegeobjecttypecodesid, privilegeid, objecttypecode,
                owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT r.link, r.id, r.entity, @bu, @now, @now, 0, 1
            FROM {rowsJson} AS r
            WHERE r.link IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM data.privilegeobjecttypecodes{d.LockHint(RowLock.UpdateRange)} WHERE privilegeobjecttypecodesid = r.link);
            """);

        async Task ExecuteAsync(string sql)
        {
            await using var cmd = session.Command(sql);
            cmd.AddParameter("@json", DbType.String, json);
            cmd.AddParameter("@bu", DbType.Guid, bu);
            cmd.AddParameter("@now", DbType.DateTime2, now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
