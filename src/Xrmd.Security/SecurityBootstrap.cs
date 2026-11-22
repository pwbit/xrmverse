using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Security;

/// <summary>
/// Seeds the standard roles in the root business unit, the security indexes and the System Administrator assignment.
/// Every step is idempotent.
/// </summary>
public sealed class SecurityBootstrap(IDbConnectionFactory factory, IMetadataStore store, PrivilegeCatalog catalog, SecurityCache cache)
{
    private const int Global = 8;
    private const int Basic = 1;

    /// <summary>
    /// The standard roles in the root business unit (<c>roleid</c> = template id, recognized by <c>roletemplateid</c>) with
    /// their privileges, and the indexes
    /// <c>UX_principalobjectaccess_object_type_principal</c> (objectid, objecttypecode, principalid; unique; it replaces the
    /// earlier <c>UX_principalobjectaccess_object_principal</c>), <c>UX_team_record_template</c>
    /// (regardingobjectid, teamtemplateid; unique where teamtemplateid is not null: one access team per record and template),
    /// <c>IX_teamtemplate_objecttypecode</c>, <c>IX_duplicaterule_baseentityname</c> (baseentityname, statecode), <c>IX_teammembership_systemuserid</c>,
    /// <c>UX_systemuser_azureactivedirectoryobjectid</c> (unique on the non-null values: one user per Entra identity), and the queue
    /// indexes <c>IX_queueitem_objectid_state</c>, <c>IX_queueitem_queueid_state</c> and <c>IX_queuemembership_systemuserid</c>.
    /// </summary>
    /// <remarks>
    /// A standard role's privileges are seeded only by the run that creates the role row: privileges removed from (or
    /// changed in) a standard role afterwards are not restored. A privilege added to the standard roles by a later version
    /// of xrmverse reaches the roles of an existing organization through a top-up (<see cref="TopUps"/>): it adds the
    /// missing grants once, records its name in <c>meta.upgrade_step</c>, and never runs again, so a grant removed after it
    /// stays removed. A run that creates a role records every top-up too, since the role already has those grants.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A role took the <c>roleid</c> of a standard role without being it, or the root business unit is missing.</exception>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await catalog.EnsureAsync(ct);

        // A privilege granted twice to a role (e.g. AppendTo of plugintype, also in "every privilege") is one row.
        var grants = Grants().DistinctBy(g => (g.Role, g.Privilege.ToLowerInvariant())).Select(g => new
        {
            id = RolePrivilegeId(g.Role, g.Privilege), role = g.Role, privilege = PrivilegeNames.IdOf(g.Privilege), depth = g.Depth,
        }).ToList();
        var roles = StandardRoles.All.Select(r => new { id = r.Id, name = r.Name, description = r.Description }).ToList();
        var topUps = TopUps().Select(t => new
        {
            name = t.Name,
            grants = grants.Where(g => t.Privileges.Contains(g.privilege)).ToList(),
        }).ToList();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Plan 2.7a, Task 5 fix round 1: the T-SQL batch (DECLARE, THROW, OUTPUT … INTO, table variables, CROSS APPLY OPENJSON)
        // became commands of their own in the transaction; the decisions it took in T-SQL are taken here.
        var session = new DataSession(conn, tx);
        var d = session.Dialect;
        var (bu, now) = await session.RootBusinessUnitAsync(ct)
                        ?? throw new InvalidOperationException("The root business unit does not exist: run the system seed first.");
        var roleIds = d.JsonRecords("@roles", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id")]);
        var grantRows = d.JsonRecords("@grants", [
            new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("role", SqlColumnKind.Guid, Path: "$.role"),
            new JsonColumn("privilege", SqlColumnKind.Guid, Path: "$.privilege"), new JsonColumn("depth", SqlColumnKind.Int32, Path: "$.depth")]);

        // A standard role is recognized by its template: a row that took a template id as roleid without carrying
        // that template is not the standard role (and blocks its creation).
        await session.KeyLockAsync("role", "standard", ct);
        await using (var check = session.Command($"""
            SELECT COUNT(*) FROM data.role{d.LockHint(RowLock.UpdateRange)}
            WHERE roleid IN (SELECT j.id FROM {roleIds} AS j)
              AND (roletemplateid IS NULL OR roletemplateid <> roleid)
            """))
        {
            check.AddParameter("@roles", DbType.String, JsonSerializer.Serialize(roles));
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException(
                    "A role uses the reserved roleid of a standard role without being that standard role: remove or rename it.");
        }

        var created = new List<Guid>();
        await using (var insert = session.Command($"""
            INSERT INTO data.role (roleid, name, businessunitid, roletemplateid, isinherited, description,
                owningbusinessunit, createdon, modifiedon, statecode, statuscode){d.ReturningInline(["roleid"])}
            SELECT j.id, j.name, @bu, j.id, 1, j.description, @bu, @now, @now, 0, 1
            FROM {d.JsonRecords("@roles", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("name", SqlColumnKind.String, 100, "$.name"),
                new JsonColumn("description", SqlColumnKind.String, 2000, "$.description")])} AS j
            WHERE NOT EXISTS (SELECT 1 FROM data.role{d.LockHint(RowLock.UpdateRange)} WHERE roletemplateid = j.id){d.ReturningTrailing(["roleid"])}
            """))
        {
            insert.AddParameter("@roles", DbType.String, JsonSerializer.Serialize(roles));
            insert.AddParameter("@bu", DbType.Guid, bu);
            insert.AddParameter("@now", DbType.DateTime2, now);
            await using var rd = await insert.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) created.Add(rd.GetGuid(0));
        }

        // Only the roles created by this run get their privileges: later changes to a standard role's privileges are kept.
        // Top-ups not run yet: the grants a later version added to the standard roles that exist (recognized by their template)
        // and lack them. Each runs once; a role created above already holds its grants.
        var pending = new List<string>();
        await session.KeyLockAsync("meta.upgrade_step", "standard-role-top-ups", ct);
        await using (var steps = session.Command($"""
            SELECT name FROM meta.upgrade_step{d.LockHint(RowLock.UpdateRange)}
            WHERE name IN (SELECT j.name FROM {d.JsonRecords("@names", [new JsonColumn("name", SqlColumnKind.String, 128, "$")])} AS j){d.LockClause(RowLock.UpdateRange)}
            """))
        {
            steps.AddParameter("@names", DbType.String, JsonSerializer.Serialize(topUps.Select(t => t.name)));
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var rd = await steps.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) done.Add(rd.GetString(0));
            pending.AddRange(topUps.Select(t => t.name).Where(n => !done.Contains(n)));
        }
        var createdSet = created.ToHashSet();
        var topUpGrants = topUps.Where(t => pending.Contains(t.name)).SelectMany(t => t.grants)
            .Where(g => !createdSet.Contains(g.role)).DistinctBy(g => g.id).ToList();

        await session.KeyLockAsync("roleprivileges", "standard", ct);
        if (created.Count > 0)
            await GrantAsync(grants.Where(g => createdSet.Contains(g.role)), "");
        if (topUpGrants.Count > 0)
            await GrantAsync(topUpGrants, "EXISTS (SELECT 1 FROM data.role WHERE roleid = j.role AND roletemplateid = j.role)\n  AND ");
        if (pending.Count > 0)
        {
            await using var record = session.Command($"""
                INSERT INTO meta.upgrade_step (name, applied_on)
                SELECT j.name, @now FROM {d.JsonRecords("@names", [new JsonColumn("name", SqlColumnKind.String, 128, "$")])} AS j
                """);
            record.AddParameter("@names", DbType.String, JsonSerializer.Serialize(pending));
            record.AddParameter("@now", DbType.DateTime2, now);
            await record.ExecuteNonQueryAsync(ct);
        }

        async Task GrantAsync<T>(IEnumerable<T> rows, string condition)
        {
            await using var grant = session.Command($"""
                INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask,
                    owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                SELECT j.id, j.role, j.privilege, j.depth, @bu, @now, @now, 0, 1
                FROM {grantRows} AS j
                WHERE {condition}NOT EXISTS (SELECT 1 FROM data.roleprivileges{d.LockHint(RowLock.UpdateRange)} WHERE roleid = j.role AND privilegeid = j.privilege)
                """);
            grant.AddParameter("@grants", DbType.String, JsonSerializer.Serialize(rows));
            grant.AddParameter("@bu", DbType.Guid, bu);
            grant.AddParameter("@now", DbType.DateTime2, now);
            await grant.ExecuteNonQueryAsync(ct);
        }

        // One field share per (record, column, principal) (F6 rule 3). An organization from before the index may hold
        // duplicates: their access is merged (OR) into one row and the others are removed, so that IndexStatements can build the
        // index. Only a SQL Server organization made before the index can hold them: a PostgreSQL organization (new in 2.7a) gets
        // the index from IndexStatements below in its first startup, before any share is written, so the T-SQL merge (an
        // updatable CTE) never runs there.
        if (await FieldShareDuplicatesAsync(session, ct))
            await using (var merge = session.Command($"""
                WITH ranked AS (
                    SELECT readaccess, updateaccess,
                        MAX(CASE WHEN readaccess = 1 THEN 1 ELSE 0 END) OVER (PARTITION BY objectid, attributeid, principalid) AS anyread,
                        MAX(CASE WHEN updateaccess = 1 THEN 1 ELSE 0 END) OVER (PARTITION BY objectid, attributeid, principalid) AS anyupdate,
                        ROW_NUMBER() OVER (PARTITION BY objectid, attributeid, principalid ORDER BY principalobjectattributeaccessid) AS n
                    FROM data.{FieldSecurityTables.Share})
                UPDATE ranked SET readaccess = anyread, updateaccess = anyupdate WHERE n = 1;
                WITH ranked AS (
                    SELECT ROW_NUMBER() OVER (PARTITION BY objectid, attributeid, principalid ORDER BY principalobjectattributeaccessid) AS n
                    FROM data.{FieldSecurityTables.Share})
                DELETE FROM ranked WHERE n > 1;
                """))
                await merge.ExecuteNonQueryAsync(ct);
        await using (var indexes = conn.Command(IndexStatements(SqlDialects.For(conn)), tx))
            await indexes.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        cache.Invalidate(); // roles and role privileges were written
    }

    /// <summary>
    /// True when the field share table exists without its unique index and holds two rows of one (record, column, principal):
    /// the case of a SQL Server organization made before the index (plan 2.7a, Task 5 fix round 1).
    /// </summary>
    private static async Task<bool> FieldShareDuplicatesAsync(DataSession session, CancellationToken ct)
    {
        var d = session.Dialect;
        const string data = DdlGenerator.DataSchema;
        await using (var exists = session.Command(
            $"SELECT CASE WHEN {d.TableExists(data, FieldSecurityTables.Share)} AND NOT {d.IndexExists(data, FieldSecurityTables.Share, FieldSecurityTables.ShareUniqueIndex)} THEN 1 ELSE 0 END"))
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) == 0) return false;
        await using var duplicates = session.Command(d.Limit(
            $"SELECT 1 FROM data.{FieldSecurityTables.Share} GROUP BY objectid, attributeid, principalid HAVING COUNT(*) > 1", "1"));
        return await duplicates.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// The indexes of the security tables, each created when missing (plan 2.7a, Task 4: the dialect's guarded statements), in
    /// the transaction of the role bootstrap.
    /// </summary>
    internal static string IndexStatements(ISqlDialect d)
    {
        const string data = DdlGenerator.DataSchema;
        string[] statements =
        [
            // One share per (record, table, principal): records of two tables may share an id (plan 2.11, final review C2). The
            // index of an earlier version, on (objectid, principalid), is replaced; migration 0011 does the same on an existing
            // database, so a new and a migrated database have the same index.
            d.CreateIndexIfMissing(data, "principalobjectaccess", "UX_principalobjectaccess_object_type_principal",
                "objectid, objecttypecode, principalid", unique: true),
            d.DropIndexIfExists(data, "principalobjectaccess", "UX_principalobjectaccess_object_principal"),
            // At most one access team per (record, template) (plan 2.16). The columns come with the seed upgrade, which runs first;
            // the statement is compiled when it runs, so that it does not fail on a database without them.
            d.When($"{d.ColumnExists(data, "team", "regardingobjectid")} AND {d.ColumnExists(data, "team", "teamtemplateid")} " +
                   $"AND NOT {d.IndexExists(data, "team", "UX_team_record_template")}",
                DdlGenerator.CreateIndex("team", "UX_team_record_template", "regardingobjectid, teamtemplateid", d, unique: true,
                    filter: "teamtemplateid IS NOT NULL")),
            // The template rows of one table, which a template Create and the disabling of the table lock (UPDLOCK, HOLDLOCK):
            // with the index the key-range locks cover that table's rows only.
            d.When($"{d.ColumnExists(data, "teamtemplate", "objecttypecode")} AND NOT {d.IndexExists(data, "teamtemplate", "IX_teamtemplate_objecttypecode")}",
                DdlGenerator.CreateIndex("teamtemplate", "IX_teamtemplate_objecttypecode", "objecttypecode", d)),
            // One field share per (record, column, principal) (F6 rule 3); on SQL Server the role batch merges the duplicates of an
            // older organization first.
            d.When($"{d.TableExists(data, FieldSecurityTables.Share)} AND NOT {d.IndexExists(data, FieldSecurityTables.Share, FieldSecurityTables.ShareUniqueIndex)}",
                DdlGenerator.CreateIndex(FieldSecurityTables.Share, FieldSecurityTables.ShareUniqueIndex, "objectid, attributeid, principalid", d, unique: true)),
            // The rules of one base table (plan 2.15): the duplicate check and the publish limit look them up by table and state.
            d.When($"{d.ColumnExists(data, "duplicaterule", "baseentityname")} AND NOT {d.IndexExists(data, "duplicaterule", "IX_duplicaterule_baseentityname")}",
                DdlGenerator.CreateIndex("duplicaterule", "IX_duplicaterule_baseentityname", "baseentityname, statecode", d)),
            d.CreateIndexIfMissing(data, "teammembership", "IX_teammembership_systemuserid", "systemuserid"),
            // The queue lookups (plan 4.1a, final review I4): a record's active items (placement, the single-item rule and the
            // deactivation on every activity completion), a queue's active items (queue and team delete), and a user's queue
            // memberships (the read privacy of queues, RetrieveUserQueues). The tables come with the seed, which runs first.
            .. QueueIndexes(d),
            d.CreateIndexIfMissing(data, "systemuser", "UX_systemuser_azureactivedirectoryobjectid", "azureactivedirectoryobjectid",
                unique: true, filter: "azureactivedirectoryobjectid IS NOT NULL"),
        ];
        return string.Join("\n", statements);
    }

    /// <summary>The indexes of the queue tables (plan 4.1a, final review I4), each created when its table exists and it is missing.</summary>
    private static IEnumerable<string> QueueIndexes(ISqlDialect d)
    {
        const string data = DdlGenerator.DataSchema;
        foreach (var (table, index, columns) in new[]
                 {
                     (QueueSchema.QueueItem, "IX_queueitem_objectid_state", "objectid, statecode"),
                     (QueueSchema.QueueItem, "IX_queueitem_queueid_state", "queueid, statecode"),
                     (QueueSchema.QueueMembership, "IX_queuemembership_systemuserid", "systemuserid"),
                 })
            yield return d.When($"{d.TableExists(data, table)} AND NOT {d.IndexExists(data, table, index)}",
                DdlGenerator.CreateIndex(table, index, columns, d));
    }

    /// <summary>
    /// The systemuser with that Entra object id (created in the root business unit when missing) holding the System
    /// Administrator role. Idempotent; this is the API <c>xrmd org create</c> calls.
    /// </summary>
    public async Task<Guid> EnsureSystemAdministratorAsync(Guid objectId, string fullName, CancellationToken ct)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("objectId is required.", nameof(objectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Plan 2.7a, Task 5 fix round 1: the T-SQL batch (DECLARE, IF … BEGIN … END, THROW) became commands of their own.
        var session = new DataSession(conn, tx);
        var d = session.Dialect;
        var (bu, now) = await session.RootBusinessUnitAsync(ct)
                        ?? throw new InvalidOperationException("The root business unit does not exist: run the system seed first.");

        // The same key lock as the system seed's administrator lookup: two startups find or create the user once.
        await session.KeyLockAsync("systemuser", objectId, ct);
        Guid id;
        await using (var find = session.Command(d.Limit(
            $"SELECT systemuserid FROM data.systemuser{d.LockHint(RowLock.UpdateRange)} WHERE azureactivedirectoryobjectid = @oid", "1")
            + d.LockClause(RowLock.UpdateRange)))
        {
            find.AddParameter("@oid", DbType.Guid, objectId);
            id = await find.ExecuteScalarAsync(ct) is Guid found ? found : Guid.Empty;
        }
        if (id == Guid.Empty)
        {
            id = Guid.NewGuid();
            await using var insert = session.Command($"""
                INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid, isdisabled,
                    owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES (@user, @name, @name, @oid, @bu, {d.BooleanLiteral(false)}, @bu, @now, @user, @now, @user, 0, 1)
                """);
            insert.AddParameter("@user", DbType.Guid, id);
            insert.AddParameter("@name", DbType.String, fullName);
            insert.AddParameter("@oid", DbType.Guid, objectId);
            insert.AddParameter("@bu", DbType.Guid, bu);
            insert.AddParameter("@now", DbType.DateTime2, now);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await session.KeyLockAsync("systemuserroles", id, ct);
        await using (var assign = session.Command($"""
            INSERT INTO data.systemuserroles (systemuserrolesid, systemuserid, roleid, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT @id, @user, @role, @bu, @now, @now, 0, 1
            WHERE NOT EXISTS (SELECT 1 FROM data.systemuserroles{d.LockHint(RowLock.UpdateRange)} WHERE systemuserid = @user AND roleid = @role)
            """))
        {
            assign.AddParameter("@id", DbType.Guid, Guid.NewGuid());
            assign.AddParameter("@user", DbType.Guid, id);
            assign.AddParameter("@role", DbType.Guid, StandardRoles.SystemAdministrator);
            assign.AddParameter("@bu", DbType.Guid, bu);
            assign.AddParameter("@now", DbType.DateTime2, now);
            await assign.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        cache.Invalidate(); // a profile cached before the commit shows neither the user nor the role
        return id;
    }

    private static Guid RolePrivilegeId(Guid role, string privilege) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"xrmd:roleprivilege:{role}:{privilege.ToLowerInvariant()}")));

    /// <summary>
    /// The one-time additions to the standard roles of an existing organization, by name (the key in
    /// <c>meta.upgrade_step</c>, never renamed): each takes from <see cref="Grants"/> the rows of its privileges.
    /// </summary>
    private IEnumerable<(string Name, HashSet<Guid> Privileges)> TopUps()
    {
        // Plan 15: System Customizer's privileges and Basic User's Read on the custom API definitions.
        yield return ("customapi-role-privileges",
            CustomApiTables.All.SelectMany(t => PrivilegeCatalog.NamesOf(store.GetEntity(t)!)).Select(PrivilegeNames.IdOf).ToHashSet());
        // Plan 2.15 (ruling D17): System Customizer's privileges on the duplicate detection tables.
        yield return ("duplicate-detection-role-privileges",
            new[] { DuplicateDetectionSchema.Rule, DuplicateDetectionSchema.Condition, DuplicateDetectionSchema.Record }
                .SelectMany(t => PrivilegeCatalog.NamesOf(store.GetEntity(t)!)).Select(PrivilegeNames.IdOf).ToHashSet());
        // Plan 2.14 (ruling W24): System Customizer's privileges on the processes.
        yield return ("workflow-role-privileges",
            new[] { WorkflowSchema.Table, WorkflowSchema.TriggerTable }
                .SelectMany(t => PrivilegeCatalog.NamesOf(store.GetEntity(t)!)).Select(PrivilegeNames.IdOf).ToHashSet());
        // Plan 2.14 (W21): System Customizer's and Basic User's prvWorkflowExecution.
        yield return ("workflow-execution-role-privilege", [PrivilegeNames.IdOf(PrivilegeNames.WorkflowExecution)]);
        // Plan 2.21 (A17): System Customizer's activation privileges (real-time workflows and actions, business rules).
        yield return ("workflow-activation-role-privileges",
            [PrivilegeNames.IdOf(PrivilegeNames.ActivateSynchronousWorkflow), PrivilegeNames.IdOf(PrivilegeNames.ActivateBusinessRule)]);
        // Plan 2.10 (X15): Basic User's Read and AppendTo on the currencies, and Read, Write and Append on its own settings.
        yield return ("currency-role-privileges", CurrencyGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 3.2: System Customizer's privileges on the service endpoints (AppendTo included).
        yield return ("serviceendpoint-role-privileges",
            PrivilegeCatalog.NamesOf(store.GetEntity(ServiceEndpointSchema.Table)!).Select(PrivilegeNames.IdOf).ToHashSet());
        // Plan 2.11 (A12): Basic User's Activity and Note privileges.
        yield return ("activity-role-privileges", ActivityGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 3.5 UI-1a: Basic User's reads of the apps, their components and roles, site maps and views, and its personal views.
        yield return ("app-module-role-privileges", AppModuleGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 3.5 UI-1b (final review C1): Basic User's Read (Global) on the forms.
        yield return ("system-form-role-privileges", FormGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 3.5 UI-1c: Basic User's Read (Global) on the web resources.
        yield return ("web-resource-role-privileges", WebResourceGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // #119: Basic User's Create, Read and Write on its own userentityuisettings (the last form used).
        yield return ("user-entity-ui-settings-privileges", UiSettingsGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // #129: Basic User's Read (Global) on the app settings and their definitions, which the shell reads.
        yield return ("app-setting-role-privileges", AppSettingGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // #143: Basic User's Read (Global) on the entity and attribute maps.
        yield return ("attribute-map-role-privileges", AttributeMapGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // #131 (ruling Z3): Basic User's Read (Global) on the time zones.
        yield return ("time-zone-definition-privileges", TimeZoneGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // #167 (ruling E1): Basic User's Read (Global) and System Customizer's privileges on the environment variables.
        yield return ("environment-variable-role-privileges",
            EnvironmentVariableTables.SelectMany(t => PrivilegeCatalog.NamesOf(store.GetEntity(t)!)).Select(PrivilegeNames.IdOf).ToHashSet());
        // Plan 3.5 UI-1d (ruling R1): Basic User's Read (Global) on the custom controls.
        yield return ("customcontrol-role-privileges", CustomControlGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 3.5 UI-1e (ruling E1): Basic User's Read (Global) on the solutions, which the ribbon route reads.
        yield return ("ribbon-solution-read", RibbonSolutionGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 3.5 UI-1f (ruling F2): Basic User's Read (Global) on the system charts.
        yield return ("savedqueryvisualization-read", ChartGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 3.5 UI-1f (fix round 1): System Customizer's privileges on the system charts, as on the views.
        yield return ("savedqueryvisualization-customizer-privileges",
            PrivilegeCatalog.NamesOf(store.GetEntity(ChartSchema.Table)!).Select(PrivilegeNames.IdOf).ToHashSet());
        // Plan 3.5 UI-1g: Basic User's Create, Read and Delete (Global) on the posts.
        yield return ("post-privileges", PostGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 4.1a (from memory, checklist 3): Basic User's Read on the queues and Create, Read, Write and Delete on the queue items.
        yield return ("queue-role-privileges", QueueGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // Plan 4.1a (ruling R23): Basic User's Append and Append To on the queue items, added after the grants above.
        yield return ("queue-item-append-privileges", QueueItemAppendGrants().Select(g => PrivilegeNames.IdOf(g.Privilege)).ToHashSet());
        // UI-2a: Basic User's Read (Global) and System Customizer's privileges on the environment values of the settings
        // (organizationsetting). Its own top-up: app-setting-role-privileges already ran on an existing organization.
        yield return ("organization-setting-privileges",
            PrivilegeCatalog.NamesOf(store.GetEntity(AppModuleSchema.OrganizationSetting)!).Select(PrivilegeNames.IdOf).ToHashSet());
    }

    /// <summary>
    /// Basic User's grants on the queues (plan 4.1a, from memory: checklist 3): Read Queue at Global depth, and Create, Read, Write
    /// and Delete on the queue items (Global: the table has no owner, so Global is its only depth). Which queues and items a user
    /// reads and works is narrowed by the queue privacy and the read access to the item's record (ReadSecurity, ruling R23), not by
    /// the depth; no Write on the queue record is needed to work its items.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> QueueGrants()
    {
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(QueueSchema.Queue)!), Global);
        var item = store.GetEntity(QueueSchema.QueueItem)!;
        foreach (var right in new[] { AccessRights.ReadAccess, AccessRights.WriteAccess, AccessRights.CreateAccess, AccessRights.DeleteAccess })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(right, item), Global);
    }

    /// <summary>
    /// Basic User's Append and Append To on the queue items (plan 4.1a, ruling R23), at Global depth (the table has no owner). A top-up
    /// of its own, so an organization that already ran <c>queue-role-privileges</c> gets them too.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> QueueItemAppendGrants()
    {
        var item = store.GetEntity(QueueSchema.QueueItem)!;
        foreach (var right in new[] { AccessRights.AppendAccess, AccessRights.AppendToAccess })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(right, item), Global);
    }

    /// <summary>
    /// Basic User's grants on the posts (plan 3.5 UI-1g): Create, Read and Delete Post at Global depth (the tables are
    /// organization-owned), shared by <c>post</c>, <c>postcomment</c> and <c>postregarding</c>. Which posts a user reads is narrowed
    /// by the read access to their regarding record, not by the depth.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> PostGrants()
    {
        var post = store.GetEntity(PostSchema.Post)!;
        foreach (var right in new[] { AccessRights.CreateAccess, AccessRights.ReadAccess, AccessRights.DeleteAccess })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(right, post), Global);
    }

    /// <summary>
    /// Basic User's grant on <c>timezonedefinition</c> (#131, ruling Z3): Read at Global depth (the table has no owner), so that every
    /// user reads the time zone codes its settings name.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> TimeZoneGrants()
    {
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(TimeZoneSchema.Table)!), Global);
    }

    /// <summary>
    /// Basic User's grants on <c>userentityuisettings</c> (#119, ruling R2): Create, Read and Write at Basic (User) depth, so
    /// that the model-driven form remembers each user's last form of a table.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> UiSettingsGrants()
    {
        var settings = store.GetEntity(AppModuleSchema.UserEntityUiSettings)!;
        foreach (var right in new[] { AccessRights.CreateAccess, AccessRights.ReadAccess, AccessRights.WriteAccess })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(right, settings), Basic);
    }

    /// <summary>
    /// Basic User's grant for the web resources (plan 3.5 UI-1c): Read Web Resource at Global depth (the table is
    /// organization-owned), so that a user without System Customizer loads the scripts and pages of a form.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> WebResourceGrants()
    {
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity("webresource")!), Global);
    }

    /// <summary>
    /// Basic User's grant for the model-driven forms (plan 3.5 UI-1b, final review C1): Read System Form at Global depth (the
    /// table is organization-owned), as Dataverse's Basic User has it, so that a user without System Customizer opens a form.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> FormGrants()
    {
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity("systemform")!), Global);
    }

    /// <summary>
    /// Basic User's grants on activities and notes (plan 2.11, ruling A12): every Activity and Note privilege at Basic (User) depth,
    /// so that a user works with the activities and notes it owns (the verbs and depths are from memory: checklist 1d).
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> ActivityGrants()
    {
        foreach (var table in new[] { ActivitySchema.Pointer, ActivitySchema.Annotation })
            foreach (var name in PrivilegeCatalog.NamesOf(store.GetEntity(table)!))
                yield return (StandardRoles.BasicUser, name, Basic);
    }

    /// <summary>
    /// Basic User's grants for the model-driven shell (plan 3.5 UI-1a): Read (Global: the tables are organization-owned) on the apps,
    /// their components, the site maps and the system views (the <c>appmoduleroles</c> intersect has no privilege: every user reads it),
    /// and every <c>userquery</c> privilege at Basic (User) depth,
    /// so that a user creates and works with its own personal views.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> AppModuleGrants()
    {
        foreach (var table in new[] { "appmodule", AppModuleSchema.Component, "sitemap", "savedquery" })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(table)!), Global);
        foreach (var name in PrivilegeCatalog.NamesOf(store.GetEntity(AppModuleSchema.UserQuery)!))
            yield return (StandardRoles.BasicUser, name, Basic);
        foreach (var grant in AppSettingGrants()) yield return grant;
    }

    /// <summary>
    /// Basic User's grants on the app settings (#129): Read (Global: the tables are organization-owned) on <c>settingdefinition</c>,
    /// <c>appsetting</c> and <c>organizationsetting</c> (UI-2a), so that the shell of a user without System Customizer knows whether an app awaits its form handlers.
    /// Part of <see cref="AppModuleGrants"/>, with a top-up of its own for an organization that already ran that one.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> AppSettingGrants()
    {
        foreach (var table in new[] { AppModuleSchema.SettingDefinition, AppModuleSchema.AppSetting, AppModuleSchema.OrganizationSetting })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(table)!), Global);
    }

    /// <summary>
    /// Basic User's grants on the table and column maps (#143): Read at Global depth (the tables are organization-owned), so that a
    /// user lists the maps; writing them stays with the solution import.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> AttributeMapGrants()
    {
        foreach (var table in new[] { AttributeMapSchema.EntityMap, AttributeMapSchema.AttributeMap })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(table)!), Global);
    }

    /// <summary>The environment variable tables (#167).</summary>
    private static readonly string[] EnvironmentVariableTables = [EnvironmentVariableSchema.Definition, EnvironmentVariableSchema.Value];

    /// <summary>
    /// Basic User's grants on the environment variables (#167, ruling E1): Read at Global depth, so that plug-ins and form scripts
    /// running as any user read the definitions and their values. System Customizer's privileges are in <see cref="Grants"/>.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> EnvironmentVariableGrants()
    {
        foreach (var table in EnvironmentVariableTables)
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(table)!), Global);
    }

    /// <summary>
    /// Basic User's grant on <c>customcontrol</c> (plan 3.5 UI-1d, ruling R1): Read at Global depth (the table is organization-owned),
    /// so that the form of any user loads the code components it names; writing the table stays with the solution import.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> CustomControlGrants()
    {
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(CustomControlSchema.Table)!), Global);
    }

    /// <summary>
    /// Basic User's grant on <c>savedqueryvisualization</c> (plan 3.5 UI-1f, ruling F2): Read at Global depth (the table is
    /// organization-owned), as for <c>savedquery</c>, so that the dashboards of any user draw their system charts.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> ChartGrants()
    {
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(ChartSchema.Table)!), Global);
    }

    /// <summary>
    /// Basic User's grant on <c>solution</c> (plan 3.5 UI-1e, ruling E1): Read at Global depth (the table is organization-owned),
    /// so that the form of any user loads the ribbon diffs the installed solutions kept; writing stays with System Customizer.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> RibbonSolutionGrants()
    {
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity("solution")!), Global);
    }

    /// <summary>
    /// Basic User's grants on the currency tables (plan 2.10, ruling X15): every user reads the currencies (Global) and links records
    /// to them (AppendTo), and reads and writes its own <c>usersettings</c> row (Basic), Append included: setting the row's currency
    /// lookup demands it (X15 names Read and Write; without Append a user could not choose a currency). Writing currencies stays
    /// with System Administrator.
    /// </summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> CurrencyGrants()
    {
        var currency = store.GetEntity(CurrencySchema.Table)!;
        var settings = store.GetEntity(CurrencySchema.UserSettings)!;
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, currency), Global);
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.AppendToAccess, currency), Global);
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, settings), Basic);
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.WriteAccess, settings), Basic);
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.AppendAccess, settings), Basic);
    }

    /// <summary>The (role, privilege name, depth) rows of the standard roles; System Administrator has none (recognized by template).</summary>
    private IEnumerable<(Guid Role, string Privilege, int Depth)> Grants()
    {
        IEnumerable<string> All(string entity) => PrivilegeCatalog.NamesOf(store.GetEntity(entity)!);
        string Read(string entity) => PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity(entity)!);

        var customizer = StandardRoles.SystemCustomizer;
        foreach (var entity in new[]
                 {
                     "role", "pluginassembly", "plugintype", ServiceEndpointSchema.Table, "sdkmessageprocessingstep", "sdkmessageprocessingstepimage",
                     "solution", "publisher", "savedquery", "systemform", "webresource", "sitemap", "appmodule",
                     CustomApiTables.Api, CustomApiTables.RequestParameter, CustomApiTables.ResponseProperty,
                     DuplicateDetectionSchema.Rule, DuplicateDetectionSchema.Condition,
                     // Plan 2.14 (ruling W24): the processes.
                     WorkflowSchema.Table, WorkflowSchema.TriggerTable,
                     // #167 (ruling E1): the environment variables.
                     EnvironmentVariableSchema.Definition, EnvironmentVariableSchema.Value,
                     // Plan 3.5 UI-1f: the system charts, as the views.
                     ChartSchema.Table,
                     // UI-2a: the environment values of the settings.
                     AppModuleSchema.OrganizationSetting,
                 })
            foreach (var name in All(entity)) yield return (customizer, name, Global);
        // Duplicate detection (plan 2.15, ruling D17): the customizer manages the rules and reads the bulk detection results.
        yield return (customizer, Read(DuplicateDetectionSchema.Record), Global);
        foreach (var entity in new[]
                 {
                     "sdkmessage", "sdkmessagefilter", "privilege", "plugintracelog", "asyncoperation", "organization",
                     "businessunit", "systemuser", "team", "solutioncomponent", "importjob",
                 })
            yield return (customizer, Read(entity), Global);
        // AppendTo on what a step references, so that the customizer can register steps (Append/AppendTo of lookups).
        foreach (var entity in new[] { "sdkmessage", "sdkmessagefilter", "systemuser", "pluginassembly", "plugintype", ServiceEndpointSchema.Table })
            yield return (customizer, PrivilegeNames.For(AccessRights.AppendToAccess, store.GetEntity(entity)!), Global);
        foreach (var name in PrivilegeNames.Metadata) yield return (customizer, name, Global);
        foreach (var name in PrivilegeNames.Solution) yield return (customizer, name, Global);
        // Plan 2.14 (W21): starting a workflow on demand (System Administrator holds it by its template).
        yield return (customizer, PrivilegeNames.WorkflowExecution, Global);
        yield return (StandardRoles.BasicUser, PrivilegeNames.WorkflowExecution, Global);
        // Plan 2.21 (A17, from memory, checklist 1a): activating real-time workflows, actions and business rules; Basic User gets
        // neither (System Administrator holds them by its template).
        yield return (customizer, PrivilegeNames.ActivateSynchronousWorkflow, Global);
        yield return (customizer, PrivilegeNames.ActivateBusinessRule, Global);

        // The custom API definitions (Plan 15) are read by every user: they describe messages anyone may call.
        foreach (var entity in new[] { "organization", "businessunit", "systemuser", "team", "role" }.Concat(CustomApiTables.All))
            yield return (StandardRoles.BasicUser, Read(entity), Global);
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.ReadAccess, store.GetEntity("asyncoperation")!), Basic);
        yield return (StandardRoles.BasicUser, PrivilegeNames.For(AccessRights.DeleteAccess, store.GetEntity("asyncoperation")!), Basic);
        // Field shares (Plan 14, F9): Basic User creates, reads, updates and deletes them; the table is organization-owned, so
        // Global is its only depth. The profile and permission privileges stay with System Administrator.
        foreach (var right in new[] { AccessRights.CreateAccess, AccessRights.ReadAccess, AccessRights.WriteAccess, AccessRights.DeleteAccess })
            yield return (StandardRoles.BasicUser, PrivilegeNames.For(right, store.GetEntity(FieldSecurityTables.Share)!), Global);

        foreach (var grant in CurrencyGrants()) yield return grant;
        foreach (var grant in ActivityGrants()) yield return grant;
        foreach (var grant in AppModuleGrants()) yield return grant;
        foreach (var grant in FormGrants()) yield return grant;
        foreach (var grant in WebResourceGrants()) yield return grant;
        foreach (var grant in UiSettingsGrants()) yield return grant;
        foreach (var grant in AttributeMapGrants()) yield return grant;
        foreach (var grant in TimeZoneGrants()) yield return grant;
        foreach (var grant in EnvironmentVariableGrants()) yield return grant;
        foreach (var grant in CustomControlGrants()) yield return grant;
        foreach (var grant in RibbonSolutionGrants()) yield return grant;
        foreach (var grant in ChartGrants()) yield return grant;
        foreach (var grant in PostGrants()) yield return grant;
        foreach (var grant in QueueGrants()) yield return grant;
        foreach (var grant in QueueItemAppendGrants()) yield return grant;

        yield return (StandardRoles.Delegate, PrivilegeNames.ActOnBehalfOfAnotherUser, Global);
    }
}
